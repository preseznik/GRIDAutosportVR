# GRID Autosport VR

Play GRID Autosport in native stereo VR with head tracking.

## Get started

You need **Windows 10/11 (64-bit)**, the **Steam** version of GRID Autosport, and a headset connected through a **32-bit OpenXR runtime** such as Virtual Desktop (VDXR).

1. Close GRID Autosport, then run `GRIDAutosportVR-<version>-Setup.exe` and keep (or choose) your GRID Autosport Steam folder.
2. Open **GRID Autosport VR** from the Start menu.
3. Choose a **Launch mode** under **Launcher → Session**: **Normal** opens the game's menus; **Practice** puts your chosen car on your chosen track; **Race** adds 1–11 AI opponents.
4. Connect your headset, then click **Launch VR**. The launcher checks the headset, adds its VR files and starts the game through Steam.
5. Finish with **Quit** in the game. The launcher restores the original game files. After an interrupted session, click **Restore original files**.

The portable ZIP works the same way: extract it into your GRID Autosport folder and run `GRIDAutosportVR\GRIDAutosportVR.exe`.

## While playing

- Press **F10** (or the key set under **Controls → VR**) to recenter. The game's own F11 also works.
- **Graphics → VR rendering** sets the VR render scale; 100% is the headset's recommended resolution.
- **Graphics → VR HUD** turns the race HUD and its parts (dial, minimap, counters, position info) on or off for VR, whatever the game's own HUD options say, sets how far ahead of your seat it appears (1–20 m) and whether it follows your view. **Graphics → VR screen → Distance** moves the screen for videos, loading and menus further away (1.5–6 m, default 3 m); **Intro shots** shows the pre-race intro in 3D instead.
- **Controls → Driving → Driving controls…** binds your wheel, pedals, shifter, controller or keyboard: a binding wizard, one action at a time, calibration (deadzone, saturation, direction), an Xbox preset and saved profiles. **Live input…** shows every connected device as you move it, with a rumble / force feedback test. Bindings apply to VR launches, for that session only; choose the transmission in the game's own settings.
- Startup videos, loading screens, menus, the pause menu and the pre-race intro shots appear on a screen in front of you; races are in stereo.
- Practice and Race start in the driver's seat (**Launcher → Session → Camera**); the game's change-camera button still switches views. Normal races keep the camera you last used.
- In Practice and Race the start lights go green straight away: you start on pole, and the race is yours to drive. They run on the game's benchmark mode, which has no pause menu: to stop, choose **End session** in the launcher (beside Launch VR). It closes the game and restores the original files.

## Help

Use **Open logs** in the launcher and attach the newest session folder when reporting a problem. Settings live in `%LOCALAPPDATA%\GRIDAutosportVR` and are kept when you update or uninstall.

GRID Autosport VR requires your own copy of GRID Autosport. No game files or saves are included.

## Building

See `bridge/README.md` and `launcher/README.md`. `tools/Package.ps1 -Version 0.1.0` builds the bridge, probe and launcher from the committed source and writes the installer, portable ZIP and `SHA256SUMS.txt` to `artifacts/packages`.
