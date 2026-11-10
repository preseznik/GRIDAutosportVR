"""Run the stock Autosport benchmark through Steam and retain its report and window captures."""
import argparse
import ctypes
from ctypes import wintypes
from datetime import datetime
import hashlib
import json
import math
from pathlib import Path
import shutil
import subprocess
import sys
import time
import uuid
import xml.etree.ElementTree as ET

import psutil


ROOT = Path(__file__).resolve().parents[1]
GAME = Path(r"E:\SteamLibrary\steamapps\common\GRID Autosport")
STEAM = Path(r"C:\Program Files (x86)\Steam\steam.exe")
HASHES = {
    "gridautosport.exe": "030f76024bc1b19978d02677e5bece8d058f76457351b4849ee10340cb6ef00b",
    "gridautosport_avx.exe": "c88929ba8ed5327799fb6f57dc7889b2f5e8baec47b92832038af913ff54fc48",
}
user = ctypes.WinDLL("user32", use_last_error=True)
user.GetForegroundWindow.restype = wintypes.HWND
user.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
user.IsWindowVisible.argtypes = [wintypes.HWND]
user.GetClientRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
user.ClientToScreen.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.POINT)]
user.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user.SetProcessDPIAware()


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def documents():
    path = ctypes.create_unicode_buffer(32768)
    if ctypes.windll.shell32.SHGetFolderPathW(None, 5, None, 0, path) != 0:
        raise OSError("Cannot resolve the interactive user's Documents folder")
    return Path(path.value)


def processes(game):
    for proc in psutil.process_iter(["name", "exe"]):
        if (proc.info["name"] or "").lower() not in HASHES or not proc.info["exe"]:
            continue
        if Path(proc.info["exe"]).parent == game:
            yield proc


def game_window(pid, allow_minimized=False):
    matches = []
    callback = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    @callback
    def visit(hwnd, _):
        owner = wintypes.DWORD()
        user.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        rect = wintypes.RECT()
        if owner.value == pid and user.IsWindowVisible(hwnd) and user.GetClientRect(hwnd, ctypes.byref(rect)):
            if rect.right >= 320 and rect.bottom >= 200:
                matches.append((rect.right * rect.bottom, hwnd))
            elif allow_minimized and user.IsIconic(hwnd):
                title = ctypes.create_unicode_buffer(128)
                user.GetWindowTextW(hwnd, title, len(title))
                if title.value == "GRID Autosport":
                    matches.append((0, hwnd))
        return True

    user.EnumWindows(visit, 0)
    return max(matches)[1] if matches else None


def snapshot(hwnd, output):
    from PIL import ImageGrab
    # Restrict the screen fallback to the foreground game's client rectangle.
    img = ImageGrab.grab(window=hwnd)
    if img.convert("L").getextrema()[1] <= 8 and hwnd == user.GetForegroundWindow():
        rect, origin = wintypes.RECT(), wintypes.POINT()
        if user.GetClientRect(hwnd, ctypes.byref(rect)) and user.ClientToScreen(hwnd, ctypes.byref(origin)):
            img = ImageGrab.grab(bbox=(origin.x, origin.y, origin.x + rect.right, origin.y + rect.bottom),
                                 all_screens=True)
    img.save(output)


def read_result(path, fixture):
    if not path.is_file():
        return None
    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, OSError):
        return None
    settings = root.find("track/settings")
    results = root.find("track/results")
    track = fixture.find("track")
    if root.tag != "benchmark" or settings is None or results is None or track is None:
        return None
    if any(settings.get(k) != track.get(k) for k in ("name", "route")):
        return None
    def grid(node):
        return [(car.get("name"), car.get("livery")) for car in node.findall("car")]
    if grid(settings) != grid(track):
        return None
    try:
        fps = float(results.get("av_fps", "0"))
        if int(results.get("samples", "0")) <= 0 or not math.isfinite(fps) or fps <= 0:
            return None
    except ValueError:
        return None
    return {"settings": settings.attrib, "results": results.attrib, "benchmark": root.attrib}


def run(args):
    game = args.game.resolve(strict=True)
    for name, expected in HASHES.items():
        if digest(game / name) != expected:
            raise ValueError(f"Unrecognized game build: {name}")
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:6]
    dest = ROOT / "artifacts" / "baseline" / stamp
    dest.mkdir(parents=True)
    prefix = "GASVR_" + stamp
    tree = ET.parse(game / "example_benchmark.xml")
    config = tree.getroot()
    config.set("outputfilename", prefix)
    config.set("cycle_camera", "true" if args.cycle_camera else "false")
    config.set("skipreplays", "true")
    config.set("infinite_loop", "false")
    fixture = dest / (prefix + ".xml")
    tree.write(fixture, encoding="utf-8", xml_declaration=True)
    print(f"Evidence directory {dest}", flush=True)
    if not args.run:
        return
    if list(processes(game)):
        raise RuntimeError("An Autosport session already exists")
    reports = documents() / "My Games" / "GRID Autosport" / "benchmarks"
    staged = game / fixture.name
    state = {"fixture": fixture.name, "route": "san_francisco/route_0", "reports": str(reports),
             "cameraCycling": args.cycle_camera, "captures": [], "executable": None, "result": None,
             "startup": "Steam -applaunch 255220", "exitObserved": False}
    if args.direct:
        state["startup"] = "Direct documented benchmark launch"
    statefile = dest / "run.json"
    if args.sample_eyes:
        sys.path.insert(0, str(ROOT / "capture"))
        from stock_eye_state import sample
    def save():
        temp = statefile.with_suffix(".tmp")
        temp.write_text(json.dumps(state, indent=2), encoding="utf-8")
        temp.replace(statefile)

    expected = fixture.read_bytes()
    with staged.open("xb") as stream:
        stream.write(expected)
    try:
        started = time.time()
        deadline = started + args.timeout
        if args.direct:
            cmd = [str(game / args.direct), "-benchmark", fixture.name]
        else:
            cmd = [str(args.steam), "-applaunch", "255220", "-benchmark", fixture.name]
        subprocess.Popen(cmd, cwd=game)
        proc = None
        gone_since = None
        last = 0
        save()
        while time.time() < deadline:
            if not proc or not proc.is_running() or proc.name().lower() != "gridautosport_avx.exe":
                matches = []
                for candidate in processes(game):
                    try:
                        owned = candidate.create_time() >= started - 2 and fixture.name.lower() in [
                            arg.lower() for arg in candidate.cmdline()]
                    except psutil.NoSuchProcess:
                        continue
                    if owned:
                        matches.append(candidate)
                matches.sort(key=lambda p: p.name().lower() == "gridautosport_avx.exe", reverse=True)
                if matches and (not proc or proc.pid != matches[0].pid):
                    proc = matches[0]
                    gone_since = None
                    state.update(pid=proc.pid, executable=proc.exe(), sha256=digest(Path(proc.exe())))
                    state.setdefault("processes", []).append({"pid": proc.pid, "name": proc.name()})
                    save()
                    print(f"Owned benchmark PID {proc.pid} ({proc.name()})", flush=True)
            if proc and proc.is_running():
                hwnd = game_window(proc.pid)
                now = time.time()
                if hwnd and now - last >= 4:
                    last = now
                    shot = dest / f"window-{int(now - started):03d}.png"
                    try:
                        if args.sample_eyes and now - started >= 30:
                            record, reading = sample(proc.pid, dest, screenshot=True)
                            state.setdefault("observations", []).append(record.name)
                            if reading.get("screenshot"):
                                state["captures"].append(reading["screenshot"])
                        else:
                            snapshot(hwnd, shot)
                            state["captures"].append(shot.name)
                    except OSError as exc:
                        state["captureError"] = str(exc)
                    save()
            for report in reports.glob(prefix + "*.xml"):
                parsed = read_result(report, config)
                if parsed and not state["result"]:
                    shutil.copyfile(report, dest / "result.xml")
                    state["result"] = parsed
                    save()
            if proc and not proc.is_running():
                gone_since = gone_since or time.time()
                if time.time() - gone_since > 5:
                    state["exitObserved"] = True
                    break
            if not proc and time.time() - started >= 90:
                state["error"] = "Steam did not start a matching benchmark process within 90 seconds"
                break
            time.sleep(1)
        if proc and proc.is_running():
            hwnd = game_window(proc.pid, allow_minimized=True)
            state["timedOut"] = True
            if hwnd:
                user.PostMessageW(hwnd, 0x10, 0, 0)
                try:
                    proc.wait(timeout=12)
                    state["exitObserved"] = True
                except psutil.TimeoutExpired:
                    state["error"] = "Owned game did not exit after WM_CLOSE"
        save()
        print(json.dumps({k: state.get(k) for k in ("pid", "executable", "result", "exitObserved", "error")}), flush=True)
        if not state["result"] or not state["exitObserved"] or state.get("timedOut"):
            raise RuntimeError(f"Incomplete benchmark; see {statefile}")
    finally:
        if staged.is_file() and staged.read_bytes() == expected:
            staged.unlink()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", action="store_true", help="launch the game; default only prepares a fixture")
    parser.add_argument("--game", type=Path, default=GAME)
    parser.add_argument("--steam", type=Path, default=STEAM)
    parser.add_argument("--direct", choices=tuple(HASHES), help="use the documented executable entry point")
    parser.add_argument("--timeout", type=int, default=220)
    parser.add_argument("--cycle-camera", action="store_true", help="use the game's native camera cycle")
    parser.add_argument("--sample-eyes", action="store_true", help="record the known Oculus fields around captures")
    run(parser.parse_args())
