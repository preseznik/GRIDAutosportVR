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
import xml.etree.ElementTree as ET

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
    fixture_hash = ""
    if args.benchmark:
        # The game's own benchmark race, as in bridge/run.py; logs and eye captures go to the run folder.
        fixture = paths(stamp)[1]
        tree = ET.parse(GAME / "example_benchmark.xml")
        tree.getroot().attrib.update(outputfilename=fixture.stem, cycle_camera=str(args.cycle_camera).lower(),
                                     skipreplays="true", infinite_loop="false")
        tree.write(folder / fixture.name, encoding="utf-8", xml_declaration=True)
        fixture_data = (folder / fixture.name).read_bytes()
        fixture_hash = sha(fixture_data)
        session = folder
        game_args = ["-benchmark", fixture.name, "-gasvrdir=" + str(folder), "-gasvrmode=openxr",
                     "-gasvrhudfollow=0", "-gasvrseconds=" + str(args.race_seconds)]
    else:
        session = SESSIONS / token
        session.mkdir(parents=True)
        (session / "session.id").write_bytes(token.encode("ascii"))
        game_args = ["-gasvrplay=" + token, "-gasvrmode=openxr", "-gasvrhudfollow=0"]
    script = json.loads(Path(args.script).read_text(encoding="utf-8")) if args.script else []
    report = dict(kind="xrsim-benchmark" if args.benchmark else "xrsim-normal-play", bridge=str(bridge), bridgeSha256=sha(dll), stockSha256=STOCK,
                  focusLoss=not args.no_focus_loss, keepActive=not args.no_keep_active, deactivateAt=args.deactivate_at, session=str(session), directory=str(folder), samples=[])
    def save():
        (folder / "run.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    save()
    print(folder, flush=True)
    exclusive(LEASE, json.dumps({"stamp": stamp, "dll": sha(dll), "fixture": fixture_hash}).encode())
    wrote_appid = False
    game = None
    try:
        exclusive(DLL, dll)
        if args.benchmark:
            exclusive(fixture, fixture_data)
        exclusive(temp, private)
        if file_sha(EXE) != STOCK or file_sha(temp) != PRIVATE:
            raise RuntimeError("Executable changed during staging")
        os.replace(temp, EXE)
        if not APPID.exists():
            # Without it the Steam API restarts the game through Steam, which drops XR_RUNTIME_JSON.
            exclusive(APPID, b"255220")
            wrote_appid = True
        env = dict(os.environ, XR_RUNTIME_JSON=str(manifest), XRSIM_DIR=str(sim), XRSIM_HEADSET_CONFIG=str(HEADSET))
        game = subprocess.Popen([str(EXE)] + game_args +
                                (["-gasvrkeepactive=0"] if args.no_keep_active else []) + list(args.game_arg),
                                cwd=GAME, env=env)
        race_start = None
        log = session / "bridge.log"
        report["pid"] = game.pid
        print(f"Started {EXE.name} PID {game.pid} on xr-sim", flush=True)
        start = time.monotonic()
        injected = None
        shots = sorted(float(s) for s in args.shots.split(",") if s.strip())
        deactivated = args.deactivate_at is None
        while time.monotonic() - start < args.seconds:
            if game.poll() is not None:
                if args.benchmark and game.returncode == 0:
                    break  # The benchmark closes the game itself after the race.
                raise RuntimeError(f"Game exited early with {game.returncode:#x}")
            if script and race_start is None and log.is_file() and \
                    "StateLoading exited" in log.read_text(encoding="ascii", errors="replace"):
                race_start = time.monotonic()
                report["raceStartAt"] = round(race_start - start, 2)
                print(f"Race scene at {report['raceStartAt']} s; running {len(script)} pose steps", flush=True)
            while script and race_start is not None and time.monotonic() - race_start >= script[0]["at"]:
                # Scripted head poses after the race scene appears; each optional shot is what
                # the simulated headset shows once the game has rendered the new pose.
                step = script.pop(0)
                if step.get("cmds"):
                    send(sim, step["cmds"])
                if step.get("key"):
                    # A key posted to the game window only (e.g. F11 recenter); desktop focus is untouched.
                    from ctypes import windll
                    vk = {"F10": 0x79, "F11": 0x7A, "ESC": 0x1B, "ENTER": 0x0D}[step["key"]]
                    window = game_window(game.pid)
                    if window:
                        windll.user32.PostMessageW(window, 0x0100, vk, 1)        # WM_KEYDOWN
                        time.sleep(.1)
                        windll.user32.PostMessageW(window, 0x0101, vk, 0xC0000001)  # WM_KEYUP
                if step.get("shot"):
                    time.sleep(.4)
                    send(sim, [f"capture tag {step['shot']}", "capture next 1"])
                report.setdefault("steps", []).append({"t": round(time.monotonic() - race_start, 2), **step})
            if not deactivated and time.monotonic() - start >= args.deactivate_at:
                # Tell the game it lost activation, as Windows does when another app is clicked;
                # posted to the game window only, the desktop's real focus is untouched.
                window = game_window(game.pid)
                if window:
                    from ctypes import windll
                    windll.user32.PostMessageW(window, 0x0006, 0, 0)  # WM_ACTIVATE, WA_INACTIVE
                    windll.user32.PostMessageW(window, 0x001C, 0, 0)  # WM_ACTIVATEAPP, deactivated
                    deactivated = True
                    report["deactivatedAt"] = round(time.monotonic() - start, 2)
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
                if shots and time.monotonic() - start >= shots[0] and state.get("sessionRunning"):
                    # A timeline of what the headset is shown: xr-sim composites the submitted layers.
                    try:
                        send(sim, [f"capture tag t{int(shots[0]):03d}s", "capture next 1"])
                    except RuntimeError as exc:
                        report.setdefault("captureErrors", []).append(str(exc))
                    shots.pop(0)
            save()
            time.sleep(.5 if shots and shots[0] < 15 else 1)
        state = read_state(sim) or {}
        report["final"] = {k: state.get(k) for k in ("sessionState", "endFrames", "layeredFrames")}
        if game.poll() is None:
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
        if script:
            report["stepsNotRun"] = script
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
            if session != folder and (session / "bridge.log").is_file():
                shutil.copy2(session / "bridge.log", folder / "bridge.log")
            save()
    regained = "focusRegainedAfter" in report
    print(f"exit={report.get('exitCode')} final={report.get('final')} regained={regained}", flush=True)
    return 0 if report.get("exitCode") == 0 and (args.no_focus_loss or regained) else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--bridge", default=str(BUILT_DLL), help="gasxr.dll to stage (default: the latest build)")
    parser.add_argument("--seconds", type=int, default=60, help="How long to watch normal play before closing")
    parser.add_argument("--no-focus-loss", action="store_true", help="Leave xr-sim's focus model alone")
    parser.add_argument("--benchmark", action="store_true", help="Run the game's benchmark race instead of normal play")
    parser.add_argument("--cycle-camera", action="store_true", help="Benchmark: let the game cycle its cameras")
    parser.add_argument("--race-seconds", type=int, default=30, help="Benchmark: race second of the bridge's own eye capture")
    parser.add_argument("--script", help='JSON list of {"at": s after the race scene, "cmds": [...], "shot": "tag"}')
    parser.add_argument("--game-arg", action="append", default=[], help="Extra -gasvr... argument for the game (repeatable)")
    parser.add_argument("--shots", default="10,20,30,40,50,60", help="Seconds at which xr-sim captures what the headset is shown")
    parser.add_argument("--no-keep-active", action="store_true", help="Start with -gasvrkeepactive=0 (game sees deactivations)")
    parser.add_argument("--deactivate-at", type=float, help="Post WM_ACTIVATE/WM_ACTIVATEAPP deactivation to the game window after this many seconds")
    options = parser.parse_args()
    with session_lock():
        sys.exit(run(options))
