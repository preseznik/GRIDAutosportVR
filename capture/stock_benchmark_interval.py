"""Measure a short interval with the stock AVX benchmark's own frame and seconds counters."""
import argparse
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import json
import math
from pathlib import Path
import struct
import sys
import time
import uuid

from stock_eye_state import GAME, HASHES, close_handle, digest, game_window, identify, open_process, read, require


EXE = "gridautosport_avx.exe"
CONTROLLER = 0xF399B8


def check_layout(fetch, base):
    # The frame-loop call, the accumulator writes and the race-result division.
    signatures = {
        0x2CE042: b"\xb9" + struct.pack("<I", base + CONTROLLER) + bytes.fromhex("c5 fb 5a c0 c5 fa 11 04 24 e8 2b b2 3a 00"),
        0x6792C2: bytes.fromhex("c5 fa 58 89 ec 45 00 00 c5 fa 58 81 f4 45 00 00 01 81 e8 45 00 00"),
        0x6792EC: bytes.fromhex("c5 fa 11 89 ec 45 00 00"),
        0x68507C: bytes.fromhex("db 86 e8 45 00 00 8b 8e e4 45 00 00"),
        0x6850AC: bytes.fromhex("d8 b6 ec 45 00 00 d9 98 dc 06 00 00"),
    }
    for rva, expected in signatures.items():
        if fetch(rva, len(expected)) != expected:
            raise ValueError(f"Unexpected AVX benchmark instructions at RVA {rva:#x}")


def verify_file():
    path = GAME / EXE
    if digest(path) != HASHES[EXE]:
        raise ValueError("Unsupported AVX executable fingerprint")
    data = path.read_bytes()
    pe = struct.unpack_from("<I", data, 60)[0]
    base = struct.unpack_from("<I", data, pe + 52)[0]
    table = pe + 24 + struct.unpack_from("<H", data, pe + 20)[0]
    sections = [struct.unpack_from("<III", data, table + 40 * i + 12)
                for i in range(struct.unpack_from("<H", data, pe + 6)[0])]

    def fetch(rva, length):
        for start, size, offset in sections:
            if start <= rva and rva + length <= start + size:
                begin = offset + rva - start
                return data[begin:begin + length]
        raise ValueError("Code outside PE sections")

    check_layout(fetch, base)


def counters(handle, controller):
    def get():
        mode, flags = struct.unpack("<I13s", read(handle, controller + 0x84, 17))
        index, frames, seconds = struct.unpack("<IIf", read(handle, controller + 0x45E4, 12))
        active = mode == 1 and flags[0x94 - 0x88] == 1 and flags[0x8A - 0x88] == 0
        return index, frames, seconds, active

    for _ in range(8):
        sample = get()
        if sample == get() and math.isfinite(sample[2]):
            index, frames, seconds, active = sample
            return {"raceIndex": index, "frames": frames, "seconds": seconds, "active": active}
    raise ValueError("Could not obtain consistent benchmark counters")


def measure(samples):
    first, last = samples[0], samples[-1]
    if any(not s["active"] or s["raceIndex"] != first["raceIndex"] for s in samples):
        raise ValueError("Benchmark race was inactive or changed during measurement")
    for old, new in zip(samples, samples[1:]):
        if new["frames"] <= old["frames"] or new["seconds"] <= old["seconds"]:
            raise ValueError("Benchmark counters stopped or reset during measurement")
    frames, seconds = last["frames"] - first["frames"], last["seconds"] - first["seconds"]
    return {"frames": frames, "engineSeconds": seconds, "averageFps": frames / seconds,
            "meanFrameMs": 1000 * seconds / frames}


def run(pid, directory, duration, target):
    from PIL import ImageGrab

    directory.mkdir(parents=True, exist_ok=True)
    stem = "benchmark-interval-" + uuid.uuid4().hex[:12]
    log = {"pid": pid, "requestedRaceSeconds": target, "samples": [], "cameraVerified": False}
    handle = open_process(0x0400 | 0x0010, False, pid)
    try:
        require(handle)
        path, fingerprint, base = identify(handle)
        if path.name.lower() != EXE:
            raise ValueError("This benchmark sampler supports the stock AVX executable")
        check_layout(lambda rva, length: read(handle, base + rva, length), base)
        log.update(executable=str(path), sha256=fingerprint)
        controller = base + CONTROLLER
        deadline = time.monotonic() + target + 60
        while True:
            current = counters(handle, controller)
            if current["active"] and current["seconds"] >= target and current["frames"] > 0:
                break
            if time.monotonic() >= deadline:
                raise ValueError("Timed out waiting for active race and requested race seconds")
            time.sleep(0.2)

        desktop = ctypes.WinDLL("user32")
        desktop.GetForegroundWindow.restype = wintypes.HWND
        desktop.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
        def picture(label):
            hwnd = game_window(pid)
            if not hwnd:
                raise ValueError("Autosport has no visible window")
            before = counters(handle, controller)
            image = ImageGrab.grab(window=hwnd)
            after = counters(handle, controller)
            if image.convert("L").getextrema()[1] <= 8:
                raise ValueError("Autosport window is black")
            image.thumbnail((800, 600))
            filename = f"{stem}-{label}.jpg"
            image.convert("RGB").save(directory / filename, quality=90)
            log[label] = {"image": filename, "before": before, "after": after}

        picture("startView")
        start = time.monotonic()
        log["startedUtc"] = datetime.now(timezone.utc).isoformat()
        for i in range(duration * 2 + 1):
            time.sleep(max(0, start + i * 0.5 - time.monotonic()))
            reading = counters(handle, controller)
            owner = wintypes.DWORD()
            desktop.GetWindowThreadProcessId(desktop.GetForegroundWindow(), ctypes.byref(owner))
            reading["gameForeground"] = owner.value == pid
            log["samples"].append(reading)
        log["wallSeconds"] = time.monotonic() - start
        picture("endView")
        samples = log["samples"]
        result = measure(samples)
        # The images must bracket the measured samples within the same stock switch period.
        before, after = log["startView"]["before"], log["endView"]["after"]
        if any(not s["active"] or s["raceIndex"] != samples[0]["raceIndex"]
               for s in [before, after]) or not 0 < after["seconds"] - before["seconds"] < 20:
            raise ValueError("Images did not bracket an active interval shorter than one camera period")
        log["interval"] = result
        log["foregroundThroughout"] = all(s["gameForeground"] for s in samples)
    except (ValueError, OSError) as exc:
        log["error"] = str(exc)
    finally:
        if handle:
            close_handle(handle)
    report = directory / (stem + ".json")
    report.write_text(json.dumps(log, indent=2) + "\n", encoding="utf-8")
    print(report)
    print(log.get("error") or log["interval"])
    return 1 if "error" in log else 0


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--verify-file", action="store_true")
    mode.add_argument("--pid", type=int)
    parser.add_argument("--run-dir", type=Path)
    parser.add_argument("--at-race-seconds", type=float, default=0,
                        help="Begin once active race time reaches this value (0 starts immediately)")
    parser.add_argument("--seconds", type=int, default=4, choices=range(2, 9))
    args = parser.parse_args()
    if args.verify_file:
        verify_file()
        print("AVX fingerprint, race accumulator and result computation verified")
    else:
        if not args.run_dir or not 0 < args.pid <= 0xffffffff or not 0 <= args.at_race_seconds <= 240:
            parser.error("Provide a positive game PID, --run-dir and --at-race-seconds within 0..240")
        sys.exit(run(args.pid, args.run_dir, args.seconds, args.at_race_seconds))
