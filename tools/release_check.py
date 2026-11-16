"""Release checks for Launch VR on xr-sim, through Steam and the launcher's own session supervisor.

    python tools/release_check.py LAUNCHER_FOLDER [--only preset,interrupted,normal]

LAUNCHER_FOLDER holds GRIDAutosportVR.exe with probe\\AutosportXRProbe.exe and renderer\\gasxr.dll
(a package's GRIDAutosportVR folder, or a build with both copied in). Steam must already be running.
Reserve the test PC first: the game window opens through Steam.

The supervisor (GRIDAutosportVR.exe --play-vr, what Launch VR runs) gets GASVR_TEST_VDXR_MANIFEST:
a copy of xr-sim's manifest named as Virtual Desktop's, so with the VR runtime set to VDXR the game
started by Steam runs on xr-sim (OpenXrRuntimes.TestVdxrManifest). xr-sim uses its built-in Quest 3.

  preset       Practice with VR Balanced and VR anti-aliasing Off: the launch steps in vr-launch.json,
               the preset in the game's settings file during the session and the player's file
               afterwards, and the measured frame rate kept as Last VR session.
  interrupted  Practice with Potato; the supervisor and the game are killed mid-race, then
               --restore-play must restore the game files and the player's graphics settings.
  normal       Normal play with Game settings: the settings file is not touched.

The launcher's own files (settings.json, last-vr-performance.json, vr-launch.json, headset.json) and
the player's hardware_settings_config.xml are backed up first and restored byte for byte at the end.
"""
import argparse, hashlib, json, os, subprocess, sys, time
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
GAME = Path(r"E:\SteamLibrary\steamapps\common\GRID Autosport")
DATA = Path(os.environ["LOCALAPPDATA"]) / "GRIDAutosportVR"
CONFIG = Path(os.path.expandvars(r"%USERPROFILE%\Documents\My Games\GRID Autosport\hardwaresettings\hardware_settings_config.xml"))
XRSIM_DLL = REPO / ".deps" / "xr-sim" / "build" / "x86-vs" / "bin" / "RelWithDebInfo" / "xrsim.dll"
CATALOG = json.loads((REPO / "launcher" / "data" / "graphics-options.json").read_text(encoding="utf-8"))
KEPT = ["settings.json", "last-vr-performance.json", "vr-launch.json", "headset.json"]


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest() if path.exists() else None


def quality(preset):
    values = dict(next(p for p in CATALOG["presets"] if p["id"] == {"vrbalanced": "high", "potato": "ultralow"}.get(preset, preset))["values"])
    if preset == "vrbalanced": values["Crowd"] = "medium"
    return values


def running(name):
    out = subprocess.run(["tasklist", "/FI", f"IMAGENAME eq {name}", "/NH"], capture_output=True, text=True).stdout
    return name.lower() in out.lower()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("launcher")
    parser.add_argument("--only", default="preset,interrupted,normal")
    parser.add_argument("--drive", type=int, default=45, help="Seconds in VR before ending the session")
    args = parser.parse_args()
    exe = Path(args.launcher) / "GRIDAutosportVR.exe"
    assert exe.exists() and XRSIM_DLL.exists(), "launcher or xr-sim build missing"
    assert running("steam.exe"), "Start Steam first"
    assert not running("GRIDAutosport_avx.exe") and not running("GRIDAutosport.exe"), "Close GRID Autosport first"
    assert not (GAME / "gasvr-play-session.json").exists(), "A VR play lease exists: restore it first"

    work = REPO / "artifacts" / "release-check" / time.strftime("%Y%m%d-%H%M%S")
    work.mkdir(parents=True)
    manifest = work / "virtualdesktop-openxr-32.json"
    manifest.write_text(json.dumps({"file_format_version": "1.0.0", "runtime": {"name": "xr-sim (release check)", "library_path": str(XRSIM_DLL)}}), encoding="utf-8")
    backups = {name: (DATA / name).read_bytes() if (DATA / name).exists() else None for name in KEPT}
    player_config = CONFIG.read_bytes()
    player_sha = hashlib.sha256(player_config).hexdigest()
    base = json.loads(backups["settings.json"] or b"{}")
    env = dict(os.environ, GASVR_TEST_VDXR_MANIFEST=str(manifest))
    results = {}

    def settings(**values):
        document = dict(base, openXrRuntime="VDXR", **values)
        for key in [k for k, v in values.items() if v is None]: document.pop(key)
        (DATA / "settings.json").write_text(json.dumps(document, indent=2), encoding="utf-8")

    def session(name, kill=False, drive=args.drive):
        (DATA / "vr-launch.json").unlink(missing_ok=True)
        started = time.time()
        supervisor = subprocess.Popen([str(exe), "--play-vr", "--game-dir", str(GAME)], env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        steps, during, session_id, focused_at = [], None, None, None
        while supervisor.poll() is None and time.time() - started < 240:
            try: status = json.loads((DATA / "vr-launch.json").read_text(encoding="utf-8"))
            except (OSError, ValueError): status = None
            if status and status.get("SupervisorId") == supervisor.pid:
                session_id = status.get("SessionId") or session_id
                if not steps or steps[-1][1] != status["Message"]:
                    steps.append((round(time.time() - started, 1), status["Message"]))
                if status["Stage"] == "running" and focused_at is None: focused_at = time.time()
                if status["Stage"] == "failed": break
            if focused_at and during is None and time.time() - focused_at > 5: during = sha(CONFIG)
            if focused_at and time.time() - focused_at > drive: break
            time.sleep(.25)
        row = dict(steps=steps, session=session_id, configDuring=during)
        if kill:
            subprocess.run(["taskkill", "/F", "/PID", str(supervisor.pid)], capture_output=True)
            supervisor.wait(30)
            time.sleep(1)
            for image in ("GRIDAutosport_avx.exe", "GRIDAutosport.exe"):
                subprocess.run(["taskkill", "/F", "/IM", image], capture_output=True)
            time.sleep(3)
            row["leaseAfterKill"] = (GAME / "gasvr-play-session.json").exists()
            row["configAfterKill"] = sha(CONFIG)
            restore = subprocess.run([str(exe), "--restore-play", "--game-dir", str(GAME)], capture_output=True, text=True, timeout=120)
            row["restoreExit"] = restore.returncode
            row["restoreError"] = restore.stderr.strip()[:300]
        else:
            end = subprocess.run([str(exe), "--end-session", "--game-dir", str(GAME)], capture_output=True, text=True, timeout=120)
            row["endSession"] = end.returncode
            try: supervisor.wait(120)
            except subprocess.TimeoutExpired: supervisor.kill()
            row["supervisorExit"] = supervisor.returncode
            row["supervisorError"] = supervisor.stderr.read().strip()[:300]
        row["leaseAfter"] = (GAME / "gasvr-play-session.json").exists()
        row["configRestored"] = sha(CONFIG) == player_sha
        folder = DATA / "sessions" / (session_id or "-")
        log = (folder / "supervisor.log").read_text(errors="replace") if (folder / "supervisor.log").exists() else ""
        row["supervisorLog"] = [line.split(" ", 1)[1] for line in log.splitlines() if any(k in line for k in ("Graphics", "graphics", "Frame rate", "Restored and verified", "exited", "VR runtime"))]
        bridge = (folder / "bridge.log").read_text(errors="replace") if (folder / "bridge.log").exists() else ""
        row["bridge"] = [line.split("] ", 1)[1][:160] for line in bridge.splitlines() if any(k in line for k in ("OpenXR runtime", "VR failed", "display_hz", "Frame rate windows", "state=FOCUSED"))][:8]
        print(name, json.dumps(row, indent=1), flush=True)
        results[name] = row
        return row

    try:
        only = args.only.split(",")
        if "preset" in only:
            balanced = quality("vrbalanced")
            settings(launchMode="Practice", laps=3, graphicsQuality=balanced, vrMsaa="off", vrRenderScale=100)
            row = session("preset")
            perf = json.loads((DATA / "last-vr-performance.json").read_text()) if (DATA / "last-vr-performance.json").exists() else None
            row["lastVrPerformance"] = perf
            row["pass"] = bool(row["configDuring"] and row["configDuring"] != player_sha and row["configRestored"] and not row["leaseAfter"] and
                               row["steps"] and row["steps"][-1][1] == "VR is running." and perf and perf.get("Windows", 0) >= 10 and
                               perf.get("Msaa") == "off" and "Crowd=medium" in perf.get("Quality", ""))
        if "interrupted" in only:
            settings(launchMode="Practice", laps=3, graphicsQuality=quality("potato"), vrMsaa="off", vrRenderScale=70)
            row = session("interrupted", kill=True, drive=15)
            row["pass"] = bool(row["leaseAfterKill"] and row["configAfterKill"] != player_sha and row["restoreExit"] == 0 and
                               row["configRestored"] and not row["leaseAfter"])
        if "normal" in only:
            settings(launchMode="Normal", graphicsQuality=None, vrMsaa="game", vrRenderScale=100)
            row = session("normal", drive=15)
            # The game rewrites its own file as it starts; the launcher must simply not stage one.
            row["pass"] = bool(row["configDuring"] is not None and not any("Graphics for this session" in l for l in row["supervisorLog"]) and
                               not row["leaseAfter"] and row["steps"] and row["steps"][-1][1] == "VR is running.")
    finally:
        # The game rewrites its settings file when it exits; the player's own comes back either way.
        if not running("GRIDAutosport_avx.exe"):
            CONFIG.write_bytes(player_config)
        for name, data in backups.items():
            if data is None: (DATA / name).unlink(missing_ok=True)
            else: (DATA / name).write_bytes(data)
        restored = sha(CONFIG) == player_sha and all(((DATA / n).read_bytes() if (DATA / n).exists() else None) == d for n, d in backups.items())
        results["restoredPlayerFiles"] = restored
        (work / "results.json").write_text(json.dumps(results, indent=1), encoding="utf-8")
        print("restored player files:", restored, "| results:", work / "results.json", flush=True)
    sys.exit(0 if all(r.get("pass") for k, r in results.items() if isinstance(r, dict)) and results["restoredPlayerFiles"] else 1)


if __name__ == "__main__":
    main()
