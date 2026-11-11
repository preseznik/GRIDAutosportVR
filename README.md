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
- **Graphics → VR HUD → Follow view** chooses between a HUD fixed in front of your seat and one that moves with your head.
- Startup videos, loading screens, menus and the pre-race intro shots appear on a screen in front of you; races are in stereo.
- Practice and Race start in the driver's seat (**Launcher → Session → Camera**); the game's change-camera button still switches views. Normal races keep the camera you last used.
- In Practice and Race the start lights go green straight away: you start on pole, and the race is yours to drive. They run on the game's benchmark mode, which has no pause menu: to stop, choose **End session** in the launcher (beside Launch VR). It closes the game and restores the original files.

## Help

Use **Open logs** in the launcher and attach the newest session folder when reporting a problem. Settings live in `%LOCALAPPDATA%\GRIDAutosportVR` and are kept when you update or uninstall.

GRID Autosport VR requires your own copy of GRID Autosport. No game files or saves are included.

## Building

See `bridge/README.md` and `launcher/README.md`. `tools/Package.ps1 -Version 0.1.0` builds the bridge, probe and launcher from the committed source and writes the installer, portable ZIP and `SHA256SUMS.txt` to `artifacts/packages`.
