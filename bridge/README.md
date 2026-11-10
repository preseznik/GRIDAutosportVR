# GRID Autosport OpenXR bridge

The x86 bridge uses the Steam AVX renderer from build 4011980. The game draws the scene twice into its existing undistorted eye target. At the native frame boundary, the bridge copies that target once and uses the two game-provided rectangles for capture and OpenXR projection.

For normal play, connect the headset and choose **Launch VR** in the Autosport launcher. The launcher checks the 32-bit runtime, starts the registered Steam game and restores the stock executable when play ends. The portable launcher expects `renderer/gasxr.dll` and `probe/AutosportXRProbe.exe` beside it. Normal-session bridge and supervisor logs are under `%LOCALAPPDATA%\GRIDAutosportVR\sessions`. If a session is interrupted, close the game and reopen the launcher to recover the installation.

With MSVC 2022 and a local Khronos OpenXR-SDK checkout, build from the project root:

```powershell
cmake -S bridge -B artifacts/bridge-build -G "Visual Studio 17 2022" -A Win32 -DOPENXR_SDK=F:/Coding/Codex/Mods/GRID2VR/.deps/OpenXR-SDK
cmake --build artifacts/bridge-build --config Release --target gasxr -- /m:1
```

Close Autosport and leave Steam signed in. The benchmark runner uses the local Steam installation on E: and requires Python with psutil. For a reference pair with 64 mm eye separation:

```powershell
python bridge/run.py --mode capture --race-seconds 90
```

For headset projection, connect the headset to its 32-bit OpenXR runtime before launch:

```powershell
python bridge/run.py --mode openxr --race-seconds 90
```

The runner creates a hash-verified AVX backup, temporarily changes one D3D11 import name, and starts the registered game through Steam. Each run writes its images, native frame metadata and log under `artifacts/bridge-runs`. The log ties the capture to the game's benchmark frame and time. The San Francisco fixture cycles cameras. Race second 90 produced the driver cockpit in the reference run.

In headset mode, startup and the game's loading state use a level screen anchored in the seated space. When loading ends, the bridge shows the stereo track preview and hides the game's “SUNDAY / RACE 1/1” intro card. The native fades and the rest of the frontend continue drawing. **HUD follows view** on the Launcher tab controls the race HUD for the next run. The default holds it one metre ahead of the recentered seat. Enabling it uses the game's original head-relative HUD.

To check both HUD modes and the track reveal without a headset, run `python bridge/run.py --mode capture --compare-hud --race-seconds 8`. It captures both eyes at a fixed head tilt and closes the benchmark after sampling the race HUD. Add `--compare-slate` for a control run that shows the original intro card and captures the underlying scene on the next frame.

To reproduce an early window close with a connected headset, run `python bridge/run.py --mode openxr --quit-after-capture --race-seconds 1`. Success requires a stereo capture, clean process exits, stock restoration, and a completed OpenXR shutdown handshake. Omit `--quit-after-capture` for a full lap. The full run also requires the game's result XML to match the fixture. It saves that result alongside the eye capture.

Normal play can be checked without a headset on the [xr-sim](https://github.com/phunkaeg/xr-sim) OpenXR runtime: build it for x86 in `.deps/xr-sim` (`cmake --preset x86`, `cmake --build --preset x86-release`), then run `python capture/xrsim_play.py --seconds 50`. It starts the AVX game directly with a normal-play token, so the startup videos and title scene run, and models Virtual Desktop dropping focus (`focus policy vdxr-layers`, `shouldRender` held false, ~87 ms frames). It passes when the session is FOCUSED again; `--no-focus-loss` leaves the focus model alone. Results and xr-sim captures go under `artifacts/bridge-runs`.

Virtual Desktop holds `shouldRender` false while a session is not focused and restores focus only after frames that carry a layer. The bridge therefore keeps submitting while `shouldRender` is false, and on the first FOCUSED it re-places the seated screen and recenters once.

The runner restores the stock executable and removes its session files after the game exits. If the runner was interrupted, close Autosport and run `python bridge/run.py --restore`. Keep the run directory until restoration succeeds. Steam game executables and backups are local test data and should be excluded from release packages.
