"""Observe Autosport's legacy eye state on Windows. This tool only reads the supported builds."""
import argparse
import ctypes as c
from ctypes import wintypes as w
from datetime import datetime, timezone
import json
from pathlib import Path
import struct
import sys
import time
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from baseline.run import GAME, HASHES, digest, game_window


# RVA of the renderer pointer and the code which reads it before checking the HMD.
LAYOUT = {
    "gridautosport.exe": (0xF1A28C, 0x7F14BE),
    "gridautosport_avx.exe": (0xF2D28C, 0x7FC3CF),
}
kernel = c.WinDLL("kernel32", use_last_error=True)


def api(name, result, *args):
    fn = getattr(kernel, name)
    fn.argtypes = args
    fn.restype = result
    return fn


open_process = api("OpenProcess", w.HANDLE, w.DWORD, w.BOOL, w.DWORD)
close_handle = api("CloseHandle", w.BOOL, w.HANDLE)
get_path = api("QueryFullProcessImageNameW", w.BOOL, w.HANDLE, w.DWORD, w.LPWSTR, c.POINTER(w.DWORD))
list_modules = api("K32EnumProcessModulesEx", w.BOOL, w.HANDLE, c.POINTER(w.HMODULE), w.DWORD,
                   c.POINTER(w.DWORD), w.DWORD)
module_path = api("K32GetModuleFileNameExW", w.DWORD, w.HANDLE, w.HMODULE, w.LPWSTR, w.DWORD)
read_memory = api("ReadProcessMemory", w.BOOL, w.HANDLE, c.c_void_p, c.c_void_p, c.c_size_t,
                  c.POINTER(c.c_size_t))


def require(ok):
    if not ok:
        raise c.WinError(c.get_last_error())


def read(handle, address, length):
    buffer = c.create_string_buffer(length)
    count = c.c_size_t()
    require(read_memory(handle, address, buffer, length, c.byref(count)))
    if count.value != length:
        raise OSError("Incomplete read from Autosport")
    return buffer.raw


def identify(handle):
    buf = c.create_unicode_buffer(32768)
    length = w.DWORD(len(buf))
    require(get_path(handle, 0, buf, c.byref(length)))
    path = Path(buf.value)
    name = path.name.lower()
    if name not in LAYOUT or path.parent != GAME:
        raise ValueError("PID is not one of the installed GRID Autosport executables")
    fingerprint = digest(path)
    if fingerprint != HASHES[name]:
        raise ValueError("Unsupported Autosport executable fingerprint")
    # Resolve the 32-bit module base in the held process, including ASLR.
    modules = (w.HMODULE * 1024)()
    size = w.DWORD()
    require(list_modules(handle, modules, c.sizeof(modules), c.byref(size), 3))
    if size.value > c.sizeof(modules):
        raise ValueError("Module enumeration was truncated")
    for base in modules[:size.value // c.sizeof(w.HMODULE)]:
        if module_path(handle, base, buf, len(buf)) and Path(buf.value) == path:
            verify_reference(read(handle, base + LAYOUT[name][1], 12), base, name)
            return path, fingerprint, base
    raise ValueError("Autosport's primary module was not found")


def verify_reference(instructions, base, name):
    expected = b"\xa1" + struct.pack("<I", base + LAYOUT[name][0]) + bytes.fromhex("83 b8 e8 52 00 00 00")
    if instructions != expected:
        raise ValueError("Renderer reference in the executable does not match the supported build")


def snapshot_state(handle, base, name):
    ptr_address = base + LAYOUT[name][0]
    ptr = struct.unpack("<I", read(handle, ptr_address, 4))[0]
    result = {"timeUtc": datetime.now(timezone.utc).isoformat(), "renderer": ptr or None,
              "legacyHmd": None, "boundEyeTarget": None, "compositorFlag": None}
    if ptr:
        state = read(handle, ptr + 0x52E8, 10)
        result["legacyHmd"], result["boundEyeTarget"] = struct.unpack_from("<II", state)
        result["compositorFlag"] = state[9]
        if struct.unpack("<I", read(handle, ptr_address, 4))[0] != ptr:
            raise ValueError("Renderer changed during observation")
    return result


def sample(pid, run_dir, screenshot=False):
    run_dir.mkdir(parents=True, exist_ok=True)
    stem = "stock-eye-" + uuid.uuid4().hex[:12]
    report = {"pid": pid, "screenshot": None}
    handle = open_process(0x0400 | 0x0010, False, pid)  # QUERY_INFORMATION | VM_READ
    try:
        require(handle)
        path, fingerprint, base = identify(handle)
        name = path.name.lower()
        report.update(executable=str(path), sha256=fingerprint, moduleBase=base)
        start = time.perf_counter()
        report["before"] = snapshot_state(handle, base, name)
        image = None
        if screenshot:
            try:
                from PIL import ImageGrab
                hwnd = game_window(pid)
                if not hwnd:
                    raise ValueError("Autosport has no visible window")
                image = ImageGrab.grab(window=hwnd)
            except (ValueError, OSError) as exc:
                report["screenshotError"] = str(exc)
        report["after"] = snapshot_state(handle, base, name)
        report["bracketMs"] = round((time.perf_counter() - start) * 1000, 2)
        keys = ("renderer", "legacyHmd", "boundEyeTarget", "compositorFlag")
        report["sameState"] = all(report["before"][k] == report["after"][k] for k in keys)
        if image is not None:
            filename = stem + ".png"
            image.save(run_dir / filename)
            report["screenshot"] = filename
            report["screenshotNonblack"] = image.convert("L").getextrema()[1] > 8
    except (ValueError, OSError) as exc:
        report["error"] = str(exc)
    finally:
        if handle:
            close_handle(handle)
    output = run_dir / (stem + ".json")
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return output, report


def verify_files():
    for name, (_, check_rva) in LAYOUT.items():
        path = GAME / name
        if digest(path) != HASHES[name]:
            raise ValueError(f"Fingerprint mismatch: {path}")
        data = path.read_bytes()
        pe = struct.unpack_from("<I", data, 60)[0]
        optional = pe + 24
        base = struct.unpack_from("<I", data, optional + 28)[0]
        section_table = optional + struct.unpack_from("<H", data, pe + 20)[0]
        for i in range(struct.unpack_from("<H", data, pe + 6)[0]):
            rva, length, offset = struct.unpack_from("<III", data, section_table + 40 * i + 12)
            if rva <= check_rva and check_rva + 12 <= rva + length:
                offset += check_rva - rva
                verify_reference(data[offset:offset + 12], base, name)
                break
        else:
            raise ValueError("Code reference outside executable sections")
        print(f"{name}: fingerprint and renderer reference verified")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--pid", type=int, help="PID of the stock Autosport game process")
    group.add_argument("--verify-files", action="store_true", help="offline fingerprint and renderer-reference check")
    parser.add_argument("--run-dir", type=Path, help="directory for observation JSON and optional window image")
    parser.add_argument("--screenshot", action="store_true", help="bracket a game-window image with two state samples")
    args = parser.parse_args()
    if args.verify_files:
        verify_files()
    else:
        if not args.run_dir or not 0 < args.pid <= 0xFFFFFFFF:
            parser.error("--pid requires a positive Windows PID and --run-dir")
        out, result = sample(args.pid, args.run_dir, args.screenshot)
        print(out)
        print(json.dumps(result))
        sys.exit(1 if "error" in result else 0)
