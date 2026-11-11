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

In headset mode, startup and the game's loading state use a level screen anchored in the seated space. When loading ends, the bridge hides the game's “SUNDAY / RACE 1/1” intro card. The pre-race intro shots (and any other shot that is not the player's car camera, such as finish cameras) are a 2D broadcast layout with an info panel and wipes, so headset play shows them on the seated screen and switches to stereo when the car camera is live: the vehicle view manager's byte +0x834 is set while it is, sampled once per native frame from the manager the race-start view request uses. The native fades and the rest of the frontend continue drawing. **HUD follows view** on the Launcher tab controls the race HUD for the next run. The default holds it one metre ahead of the recentered seat. Enabling it uses the game's original head-relative HUD.

To check both HUD modes and the track reveal without a headset, run `python bridge/run.py --mode capture --compare-hud --race-seconds 8`. It captures both eyes at a fixed head tilt and closes the benchmark after sampling the race HUD. Add `--compare-slate` for a control run that shows the original intro card and captures the underlying scene on the next frame.

To reproduce an early window close with a connected headset, run `python bridge/run.py --mode openxr --quit-after-capture --race-seconds 1`. Success requires a stereo capture, clean process exits, stock restoration, and a completed OpenXR shutdown handshake. Omit `--quit-after-capture` for a full lap. The full run also requires the game's result XML to match the fixture. It saves that result alongside the eye capture.

Normal play can be checked without a headset on the [xr-sim](https://github.com/phunkaeg/xr-sim) OpenXR runtime: build it for x86 in `.deps/xr-sim` (`cmake --preset x86`, `cmake --build --preset x86-release`), then run `python capture/xrsim_play.py --seconds 50`. It starts the AVX game directly with a normal-play token, so the startup videos and title scene run, and models Virtual Desktop dropping focus (`focus policy vdxr-layers`, `shouldRender` held false, ~87 ms frames). It passes when the session is FOCUSED again; `--no-focus-loss` leaves the focus model alone. Results and xr-sim captures go under `artifacts/bridge-runs`.

Virtual Desktop holds `shouldRender` false while a session is not focused and restores focus only after frames that carry a layer. The bridge therefore keeps submitting while `shouldRender` is false, and on the first FOCUSED it re-places the seated screen and recenters once.

While its window is inactive the game pauses its startup videos and drops to about 18 fps, and a headset player rarely has that window focused. Bridge sessions (headset play and the capture benchmark) therefore withhold `WM_ACTIVATEAPP`/`WM_ACTIVATE` deactivations and `WM_KILLFOCUS` from the game; `-gasvrkeepactive=0` opts out. `--deactivate-at 2` (with `--no-keep-active` for the control) posts a deactivation to the game window to check this without touching the desktop focus.

The startup videos (`intel_sting.bk2`, `sting.bk2`) are drawn into the game's back buffer, not the HMD eye texture, which stays a flat dark fill. Until the first loading state of normal play, the seated screen therefore shows the back buffer, and the desktop mirror leaves it as the game drew it; `-gasvrstartupsource=eye` restores the eye-texture source. `capture/xrsim_play.py --shots 3,5,8,11` captures that interval.

Head tracking can be checked in the cockpit with scripted xr-sim poses during the game's own benchmark race: `python capture/xrsim_play.py --benchmark --cycle-camera --race-seconds 90 --no-focus-loss --seconds 220 --shots "" --script capture/poses_cockpit.json` (the cycling camera shows the driver cockpit around race second 90; without `--cycle-camera` it is the chase camera). Each step's `shot` is what the simulated headset shows; `capture/poses_recenter.json` checks a runtime recenter. When the runtime recenters LOCAL (`REFERENCE_SPACE_CHANGE_PENDING`, e.g. holding the Quest's Oculus button), the bridge re-places the seated screen and recenters the seat, as the game's own recenter does. xr-sim's compositor does not apply the LOCAL origin to projection layers, so after a simulated recenter its captures show the correct render rotated by the recenter angle.

The game sizes each eye from the per-eye size the bridge reports at renderer setup; it no longer follows the desktop window. The launcher reads the headset's OpenXR recommended size from its runtime check before every VR launch, applies **VR render scale** (Graphics → VR rendering, 50–300%, limited to 8192 px per eye because both eyes share one texture) and passes `-gasvreyesize=WIDTHxHEIGHT`. Without it the game keeps its legacy eye size (half the 800×600 window per eye here). `capture/xrsim_play.py --game-arg=-gasvreyesize=3072x3264` reproduces a Quest 3 at 100%; the bridge log records the request and the resulting eye target. While the seated screen is shown (startup and loading), the bridge gives the game a centred field of view with the headset's total width and height, so the off-centre lenses do not push the loading cards past the eye image, and shows that image at its field-of-view aspect. (The back buffer during loading holds only the generic logo splash.)

The launcher's recenter shortcut (Controls → VR, default F10 as in GRID2VR) reaches the bridge as `-gasvrrecenterkey=VK -gasvrrecentermods=MASK` (1 Ctrl, 2 Alt, 4 Shift). The bridge matches it in the game window's messages with exactly those modifiers (physical key state), consumes it, recenters the seat and places the seated screen in front again; the game's own F11 recenter keeps working. `-gasvrkeephorizon=0` (Keep horizon level off) also aligns pitch and roll to the head. `capture/poses_recenter_key.json` checks both with a posted F10.

Shortly after the title scene appears, the game stops calling the HMD frame path and draws the menus with its own uncapped swap-chain `Present`. While that lasts, the bridge ends the abandoned HMD frame and shows each presented back buffer on the seated screen, paced by `xrWaitFrame`. The HMD path takes over again at the next native BeginFrame.

The runner restores the stock executable and removes its session files after the game exits. If the runner was interrupted, close Autosport and run `python bridge/run.py --restore`. Keep the run directory until restoration succeeds. Steam game executables and backups are local test data and should be excluded from release packages.

A play session may carry its own Practice / Race file: with `-gasvrplay=<token>` the bridge accepts exactly `-benchmarkAttract GASVR_<token>.xml` (and no other `-benchmark` argument), logged as `practiceSession=1`. The launcher stages that file and the guarded `system/flow.bin` with the session. Without a headset: `GRIDAutosportVR.exe --write-session --game-dir <game> --settings practice.json practice.xml`, then `python capture/xrsim_play.py --session practice.xml --flow <guarded flow.bin> --no-focus-loss --seconds 150 --script capture/poses_race_progress.json`; the harness stages and removes the session file and restores the stock flow.

The game reads the keyboard through Raw Input and DirectInput and ignores input while its window is not in the foreground, so keys the harness posts to the window (`"key"` steps) reach only the bridge's own handler (the recenter key). For game input without taking the desktop's focus, `-gasvrtestpad=1` (test only; the launcher never passes it) makes controller 0 a virtual gamepad and reports the game's window as the foreground one. `capture/xrsim_play.py` adds the flag when a script has `"pad"` steps, e.g. `{"at": 4, "pad": "START", "hold": 0.4}` or `"A"`, `"DOWN"`, `"LB+RB"`: the harness writes the buttons to `pad.txt` in the session folder and the bridge reports them. On the title screen Start and A work through the menus; in a Practice / Race session Start does not pause, because benchmark mode has no pause menu (`capture/poses_pause_menu.json`).

With `-gasvrcockpit=1` (the launcher's **Camera → Start in the cockpit view**, on by default) a headset Practice / Race session starts in the car's `head-cam` view, the driver cockpit that every shipped car defines. When the race scene begins the game asks its vehicle view manager for the player's view by name (RVA 2D4AE5: the saved `current_camera`, which benchmark and attract sessions replace with `chase_close`); the bridge answers that one request with `head-cam`, on the game's own thread, and keeps the game's choice for a car without it. The change-camera button still cycles views. Normal play keeps the camera the player last used.
