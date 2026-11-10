"""Run the title-specific renderer on the registered Steam game's benchmark, then restore stock."""
import argparse
from contextlib import ExitStack, contextmanager
import ctypes
from ctypes import wintypes
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import uuid
import xml.etree.ElementTree as ET

import psutil

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from baseline.run import documents, game_window, read_result
RUNS = ROOT / "artifacts" / "bridge-runs"
GAME = Path(r"E:\SteamLibrary\steamapps\common\GRID Autosport")
STEAM = Path(r"C:\Program Files (x86)\Steam\steam.exe")
BUILT_DLL = ROOT / "artifacts" / "bridge-build" / "Release" / "gasxr.dll"
EXE = GAME / "GRIDAutosport_avx.exe"
DLL = GAME / "gasxr.dll"
LEASE = GAME / "gasvr-benchmark-session.json"
PLAY_LEASE = GAME / "gasvr-play-session.json"
STOCK = "c88929ba8ed5327799fb6f57dc7889b2f5e8baec47b92832038af913ff54fc48"
PRIVATE = "9d3e8f9a36571e20707187f1da86967201ab74e72f9f72e8ec446f01dbe59856"
IMPORT_NAME = 0xe0c23c  # Verified PE descriptor's name for the sole D3D11CreateDevice thunk.


@contextmanager
def session_lock():
    # Both Steam launch paths temporarily replace the same game files.
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateMutexW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.LPCWSTR]
    kernel.CreateMutexW.restype = wintypes.HANDLE
    kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.WaitForSingleObject.restype = wintypes.DWORD
    kernel.ReleaseMutex.argtypes = kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.CreateMutexW(None, False, r"Local\GRIDAutosportVR-255220")
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        state = kernel.WaitForSingleObject(handle, 0)
        if state == 258:  # WAIT_TIMEOUT
            raise RuntimeError("Another Autosport VR session is running.")
        if state not in (0, 0x80):  # WAIT_OBJECT_0, WAIT_ABANDONED
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            if PLAY_LEASE.exists():
                raise RuntimeError("Recover the interrupted play session in the Autosport VR launcher.")
            yield
        finally:
            kernel.ReleaseMutex(handle)
    finally:
        kernel.CloseHandle(handle)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def file_sha(path):
    return sha(path.read_bytes())


def hud_follows_view():
    path = Path(os.environ["LOCALAPPDATA"]) / "GRIDAutosportVR" / "settings.json"
    if not path.exists():
        return False
    settings = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(settings, dict) or type(settings.get("hudFollowView", False)) is not bool:
        raise ValueError(f"Invalid hudFollowView setting in {path}")
    return settings.get("hudFollowView", False)


def game_processes():
    for proc in psutil.process_iter(["name"]):
        if (proc.info["name"] or "").lower() in ("gridautosport.exe", "gridautosport_avx.exe"):
            yield proc


def paths(stamp):
    if not re.fullmatch(r"[0-9]{8}-[0-9]{6}-[a-f0-9]{6}", stamp):
        raise ValueError("Invalid session identifier")
    run_dir = RUNS / stamp
    return run_dir, GAME / ("GASVR_" + stamp + ".xml"), GAME / ("gasvr-" + stamp + ".tmp")


def exclusive(path, data):
    with path.open("xb") as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())


def cleanup():
    if not LEASE.exists():
        return
    if list(game_processes()):
        raise RuntimeError("A GRID Autosport process is still running; close it before restoring")
    lease = json.loads(LEASE.read_text(encoding="utf8"))
    folder, fixture, temp = paths(lease["stamp"])
    backup = folder / "original-avx.exe"
    if file_sha(backup) != STOCK:
        raise RuntimeError("Backup hash mismatch")
    current = file_sha(EXE)
    if current not in (PRIVATE, STOCK):
        raise RuntimeError("Installed executable changed independently; preserving it")
    if current == PRIVATE:
        if temp.exists() and file_sha(temp) == STOCK:
            pass
        else:
            exclusive(temp, backup.read_bytes())
        os.replace(temp, EXE)
    if file_sha(EXE) != STOCK:
        raise RuntimeError("Stock restoration failed")
    for asset, hashes in ((temp, (STOCK, PRIVATE)), (DLL, (lease["dll"],)), (fixture, (lease["fixture"],))):
        if asset.exists():
            if file_sha(asset) not in hashes:
                raise RuntimeError(f"Preserving independently changed {asset}")
            asset.unlink()
    LEASE.unlink()
    print(f"Verified restored stock AVX {STOCK}", flush=True)


def owned(proc, fixture):
    try:
        return Path(proc.exe()).parent == GAME and fixture.name.lower() in [arg.lower() for arg in proc.cmdline()]
    except (psutil.NoSuchProcess, psutil.AccessDenied):
        return False


def watch(fixture, report, timeout, close_log=None):
    import _winapi

    deadline = time.monotonic() + timeout
    seen = {}
    with ExitStack() as handles:
        while time.monotonic() < deadline:
            for proc in game_processes():
                if proc.pid in seen or not owned(proc, fixture):
                    continue
                try:
                    name = proc.name()
                    handle = _winapi.OpenProcess(0x100000 | 0x1000, False, proc.pid)
                except (ProcessLookupError, psutil.NoSuchProcess):
                    continue
                handles.callback(_winapi.CloseHandle, handle)
                info = {"pid":proc.pid, "exe":name}
                seen[proc.pid] = proc, handle, info
                report["processes"].append(info)
                print(f"Steam started {name} PID {proc.pid}", flush=True)
            if close_log is not None and close_log.is_file() and "diagnosticClose" not in report:
                samples = re.findall(r"Captured active benchmark raceFrame=\d+ raceSeconds=[0-9.]+ hudFollow=([01])",
                                     close_log.read_text(encoding="ascii", errors="replace"))
                ready_to_close = samples == ["0", "1"] if report["uiComparison"] else bool(samples)
                if ready_to_close:
                    from ctypes import windll
                    for proc, _, info in seen.values():
                        if info["exe"].lower() == EXE.name.lower() and owned(proc, fixture):
                            window = game_window(proc.pid)
                            if window and windll.user32.PostMessageW(window, 0x10, 0, 0):
                                report["diagnosticClose"] = ("Compared both HUD modes at the requested race time"
                                                            if report["uiComparison"] else "Sent WM_CLOSE after the race capture")
            if seen and all(_winapi.WaitForSingleObject(h, 0) == _winapi.WAIT_OBJECT_0 for _, h, _ in seen.values()):
                for _, handle, info in seen.values():
                    info["exitCode"] = _winapi.GetExitCodeProcess(handle)
                if any(entry["exitCode"] for entry in report["processes"]):
                    raise RuntimeError(f"Abnormal game exit: {report['processes']}")
                return
            time.sleep(.25)
        for proc, _, _ in seen.values():
            if proc.is_running() and owned(proc, fixture):
                proc.terminate()
                proc.wait(15)
        raise RuntimeError("Steam benchmark timed out")


def record_finish(folder, fixture, config, report):
    log = folder / "bridge.log"
    expected = "orderly_exit" if report["mode"] == "openxr" else "session_inactive"
    report["shutdownVerified"] = log.is_file() and (
        f"XR shutdown complete handshake={expected}" in log.read_text(encoding="ascii", errors="replace"))
    report["benchmarkCompleted"] = False
    reports = documents() / "My Games" / "GRID Autosport" / "benchmarks"
    for result_file in reports.glob(fixture.stem + "_*.xml"):
        result = read_result(result_file, config)
        if result:
            shutil.copyfile(result_file, folder / "result.xml")
            report["benchmarkCompleted"] = True
            report["raceResults"] = result["results"]
            break


def run(args):
    if list(game_processes()) or LEASE.exists() or DLL.exists():
        raise RuntimeError("A game, bridge or interrupted session is already present. Use --restore for an interrupted test.")
    follow_view = hud_follows_view()
    original = EXE.read_bytes()
    if sha(original) != STOCK or original[IMPORT_NAME:IMPORT_NAME+10].lower() != b"d3d11.dll\0":
        raise ValueError("Unsupported GRID Autosport AVX executable")
    private = original[:IMPORT_NAME] + b"gasxr.dll" + original[IMPORT_NAME+9:]
    if sha(private) != PRIVATE:
        raise ValueError("Unexpected test executable hash")
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:6]
    folder, fixture, temp = paths(stamp)
    folder.mkdir(parents=True)
    shutil.copy2(EXE, folder / "original-avx.exe")
    if file_sha(folder / "original-avx.exe") != STOCK:
        raise RuntimeError("Backup hash mismatch")
    tree = ET.parse(GAME / "example_benchmark.xml")
    tree.getroot().attrib.update(outputfilename=fixture.stem, cycle_camera="true", skipreplays="true", infinite_loop="false")
    tree.write(folder / fixture.name, encoding="utf-8", xml_declaration=True)
    data = (folder / fixture.name).read_bytes()
    dll = BUILT_DLL.read_bytes()
    lease = {"stamp":stamp, "dll":sha(dll), "fixture":sha(data)}
    report = dict(mode=args.mode, hudFollowView=follow_view, uiComparison=args.compare_hud, slateComparison=args.compare_slate, quitAfterCapture=args.quit_after_capture, requestedRaceSecond=args.race_seconds, stockSha256=STOCK, testSha256=PRIVATE,
                  bridgeSha256=sha(dll), directory=str(folder), route="san_francisco/route_0", processes=[])
    def save():
        (folder / "run.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    save()
    print(folder, flush=True)
    exclusive(LEASE, json.dumps(lease).encode())
    try:
        exclusive(DLL, dll)
        exclusive(fixture, data)
        exclusive(temp, private)
        if file_sha(EXE) != STOCK or file_sha(temp) != PRIVATE:
            raise RuntimeError("Executable changed during staging")
        os.replace(temp, EXE)
        subprocess.run([str(STEAM), "-applaunch", "255220", "-benchmark", fixture.name,
                        "-gasvrdir=" + str(folder), "-gasvrmode=" + args.mode,
                        "-gasvrhudfollow=" + str(int(follow_view)),
                        "-gasvruicheck=" + str(int(args.compare_hud)),
                        "-gasvrcompareslate=" + str(int(args.compare_slate)),
                        "-gasvrseconds=" + str(args.race_seconds)], cwd=GAME, timeout=20, check=True)
        close_log = folder / "bridge.log" if args.compare_hud or args.quit_after_capture else None
        watch(fixture, report, args.timeout, close_log)
    except Exception as exc:
        report["error"] = str(exc)
        raise
    finally:
        report["captures"] = [p.parent.name for p in folder.glob("frame-*/frame.json")]
        try:
            cleanup()
            report["stockRestored"] = True
        finally:
            try:
                record_finish(folder, fixture, tree.getroot(), report)
            finally:
                save()
    if args.quit_after_capture:
        return 0 if report.get("diagnosticClose") and report["captures"] and report["shutdownVerified"] else 1
    if args.compare_hud:
        return 0 if report.get("diagnosticClose") and report["captures"] else 1
    return 0 if report["captures"] and report["benchmarkCompleted"] and report["shutdownVerified"] else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    action = parser.add_mutually_exclusive_group(required=True)
    action.add_argument("--mode", choices=["capture", "openxr"])
    action.add_argument("--restore", action="store_true")
    checks = parser.add_mutually_exclusive_group()
    checks.add_argument("--compare-hud", action="store_true", help="Compare fixed and following HUD using the same tilted reference pose, four race frames apart")
    checks.add_argument("--quit-after-capture", action="store_true", help="Send the game window a close after the requested race sample and verify bridge shutdown")
    parser.add_argument("--compare-slate", action="store_true", help="Capture the original pre-race slate and the underlying scene during the HUD comparison")
    parser.add_argument("--race-seconds", type=int, default=90, choices=range(241), metavar="0..240")
    parser.add_argument("--timeout", type=int, default=350, choices=range(20,481), metavar="20..480")
    options = parser.parse_args()
    if options.quit_after_capture and not options.mode:
        parser.error("--quit-after-capture requires --mode")
    if options.compare_hud and options.mode != "capture":
        parser.error("--compare-hud requires --mode capture")
    if options.compare_slate and not options.compare_hud:
        parser.error("--compare-slate requires --compare-hud")
    with session_lock():
        if options.restore:
            cleanup()
        else:
            sys.exit(run(options))
