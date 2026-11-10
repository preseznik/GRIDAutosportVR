"""Headset-free normal-play check against the xr-sim OpenXR runtime, then restore stock.

Stages the bridge like bridge/run.py, but starts GRIDAutosport_avx.exe directly (Steam does not pass
XR_RUNTIME_JSON to the game) with a normal-play token, so the real startup videos and main menu run.
Once xr-sim reports FOCUSED, it models Virtual Desktop's measured unfocused behaviour: shouldRender
held false, ~87 ms frame blocking, and focus re-granted only after layer-carrying frames
(`focus policy vdxr-layers`). Then it watches whether the session gets FOCUSED back.

Build xr-sim first: cmake --preset x86 && cmake --build --preset x86-release in .deps/xr-sim.
Reserve the PC in ProJict first. The game appears to stop advancing (videos, menu frames) while
its window is inactive, so a run while someone uses the desktop can stall; this script never takes
the foreground or captures the screen.
"""
import argparse
from datetime import datetime
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from baseline.run import game_window
from bridge.run import (DLL, EXE, GAME, IMPORT_NAME, LEASE, PRIVATE, STOCK, BUILT_DLL, cleanup, exclusive,
                        file_sha, game_processes, paths, session_lock, sha)

XRSIM = ROOT / ".deps" / "xr-sim"
XRSIM_DLL = XRSIM / "build" / "x86-vs" / "bin" / "RelWithDebInfo" / "xrsim.dll"
HEADSET = XRSIM / "headsets" / "prey-quest3-vdxr.json"
APPID = GAME / "steam_appid.txt"
SESSIONS = Path(os.environ["LOCALAPPDATA"]) / "GRIDAutosportVR" / "sessions"
UNFOCUSED = ["focus policy vdxr-layers", "focus frames 3", "focus norender on", "focus throttle 87"]


def read_state(folder):
    try:
        return json.loads((folder / "state.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def send(folder, lines, timeout=5):
    before = (read_state(folder) or {}).get("cmdSeq", 0)
    tmp = folder / "command.txt.tmp"
    tmp.write_text("\n".join(lines) + "\n", encoding="ascii")
    os.replace(tmp, folder / "command.txt")
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        state = read_state(folder) or {}
        if state.get("cmdSeq", 0) >= before + len(lines):
            if state.get("lastCmdError"):
                raise RuntimeError(f"xr-sim rejected {lines}: {state['lastCmdError']}")
            return state
        time.sleep(.05)
    raise RuntimeError(f"xr-sim did not apply {lines}")


def runtime_manifest(folder):
    # A private copy per run, so a rebuild during a test never swaps the loaded DLL.
    dll = folder / "runtime" / "xrsim.dll"
    dll.parent.mkdir(parents=True)
    shutil.copy2(XRSIM_DLL, dll)
    manifest = folder / "runtime" / "xrsim-x86.json"
    manifest.write_text(json.dumps({"file_format_version": "1.0.0",
                                    "runtime": {"name": "xr-sim", "library_path": str(dll)}}), encoding="utf-8")
    return manifest


def run(args):
    if list(game_processes()) or LEASE.exists() or DLL.exists():
        raise RuntimeError("A game, bridge or interrupted session is already present. Use bridge/run.py --restore.")
    if not XRSIM_DLL.is_file():
        raise RuntimeError(f"Build xr-sim first: {XRSIM_DLL}")
    original = EXE.read_bytes()
    if sha(original) != STOCK or original[IMPORT_NAME:IMPORT_NAME+10].lower() != b"d3d11.dll\0":
        raise ValueError("Unsupported GRID Autosport AVX executable")
    private = original[:IMPORT_NAME] + b"gasxr.dll" + original[IMPORT_NAME+9:]
    if sha(private) != PRIVATE:
        raise ValueError("Unexpected test executable hash")
    bridge = Path(args.bridge).resolve()
    dll = bridge.read_bytes()
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:6]
    folder, _, temp = paths(stamp)
    folder.mkdir(parents=True)
    shutil.copy2(EXE, folder / "original-avx.exe")
    if file_sha(folder / "original-avx.exe") != STOCK:
        raise RuntimeError("Backup hash mismatch")
    sim = folder / "xrsim"
    sim.mkdir()
    manifest = runtime_manifest(folder)
    token = uuid.uuid4().hex
    session = SESSIONS / token
    session.mkdir(parents=True)
    (session / "session.id").write_bytes(token.encode("ascii"))
    report = dict(kind="xrsim-normal-play", bridge=str(bridge), bridgeSha256=sha(dll), stockSha256=STOCK,
                  focusLoss=not args.no_focus_loss, session=str(session), directory=str(folder), samples=[])
    def save():
        (folder / "run.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    save()
    print(folder, flush=True)
    exclusive(LEASE, json.dumps({"stamp": stamp, "dll": sha(dll), "fixture": ""}).encode())
    wrote_appid = False
    game = None
    try:
        exclusive(DLL, dll)
        exclusive(temp, private)
        if file_sha(EXE) != STOCK or file_sha(temp) != PRIVATE:
            raise RuntimeError("Executable changed during staging")
        os.replace(temp, EXE)
        if not APPID.exists():
            # Without it the Steam API restarts the game through Steam, which drops XR_RUNTIME_JSON.
            exclusive(APPID, b"255220")
            wrote_appid = True
        env = dict(os.environ, XR_RUNTIME_JSON=str(manifest), XRSIM_DIR=str(sim), XRSIM_HEADSET_CONFIG=str(HEADSET))
        game = subprocess.Popen([str(EXE), "-gasvrplay=" + token, "-gasvrmode=openxr", "-gasvrhudfollow=0"],
                                cwd=GAME, env=env)
        report["pid"] = game.pid
        print(f"Started {EXE.name} PID {game.pid} on xr-sim", flush=True)
        start = time.monotonic()
        injected = None
        next_shot = 10.0
        while time.monotonic() - start < args.seconds:
            if game.poll() is not None:
                raise RuntimeError(f"Game exited early with {game.returncode:#x}")
            state = read_state(sim)
            if state:
                sample = {k: state.get(k) for k in ("uptimeMs", "sessionState", "endFrames", "layeredFrames",
                                                    "focusPolicy", "focusNoRender")}
                sample["t"] = round(time.monotonic() - start, 2)
                report["samples"].append(sample)
                if injected is None and not args.no_focus_loss and state.get("sessionState") == "FOCUSED":
                    send(sim, UNFOCUSED)
                    send(sim, ["focus lose 500"])
                    injected = time.monotonic()
                    report["focusLostAt"] = round(injected - start, 2)
                    report["layeredAtFocusLoss"] = state.get("layeredFrames")
                    print(f"Focus dropped VDXR-style at {report['focusLostAt']} s", flush=True)
                if (injected is not None and "focusRegainedAfter" not in report and
                        time.monotonic() - injected > .6 and state.get("sessionState") == "FOCUSED"):
                    report["focusRegainedAfter"] = round(time.monotonic() - injected, 2)
                    print(f"FOCUSED again {report['focusRegainedAfter']} s after the drop", flush=True)
                if time.monotonic() - start >= next_shot and state.get("sessionRunning"):
                    # A timeline of what the headset is shown: xr-sim composites the submitted layers.
                    try:
                        send(sim, [f"capture tag t{int(next_shot):03d}s", "capture next 1"])
                    except RuntimeError as exc:
                        report.setdefault("captureErrors", []).append(str(exc))
                    next_shot += 10
            save()
            time.sleep(1)
        state = read_state(sim) or {}
        report["final"] = {k: state.get(k) for k in ("sessionState", "endFrames", "layeredFrames")}
        try:
            send(sim, ["capture next 1"])
            time.sleep(2)
        except RuntimeError as exc:
            report["captureError"] = str(exc)
        window = game_window(game.pid)
        from ctypes import windll
        if not window or not windll.user32.PostMessageW(window, 0x10, 0, 0):
            raise RuntimeError("Could not post WM_CLOSE to the game window")
        report["exitCode"] = game.wait(120)
    except Exception as exc:
        report["error"] = str(exc)
        if game and game.poll() is None:
            game.terminate()
            game.wait(30)
        raise
    finally:
        try:
            if wrote_appid and APPID.read_bytes() == b"255220":
                APPID.unlink()
            cleanup()
            report["stockRestored"] = True
        finally:
            for name in ("bridge.log",):
                if (session / name).is_file():
                    shutil.copy2(session / name, folder / name)
            save()
    regained = "focusRegainedAfter" in report
    print(f"exit={report.get('exitCode')} final={report.get('final')} regained={regained}", flush=True)
    return 0 if report.get("exitCode") == 0 and (args.no_focus_loss or regained) else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--bridge", default=str(BUILT_DLL), help="gasxr.dll to stage (default: the latest build)")
    parser.add_argument("--seconds", type=int, default=60, help="How long to watch normal play before closing")
    parser.add_argument("--no-focus-loss", action="store_true", help="Leave xr-sim's focus model alone")
    options = parser.parse_args()
    with session_lock():
        sys.exit(run(options))
