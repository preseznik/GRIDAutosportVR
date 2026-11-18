# GRID Autosport VR

Play GRID Autosport in native stereo VR with head tracking.

## Get started

You need **Windows 10/11 (64-bit)**, the **Steam** version of GRID Autosport, and a headset connected through a **32-bit OpenXR runtime** such as Virtual Desktop (VDXR).

1. Download `GRIDAutosportVR-<version>-Setup.exe` from [Releases](https://github.com/preseznik/GRIDAutosportVR/releases). Close GRID Autosport, run the installer and keep (or choose) your GRID Autosport Steam folder.
2. Open **GRID Autosport VR** from the Start menu.
3. A new installation opens in the **Simple** view: one Quick start page with the essentials. **Advanced** (beside **?**) shows every option; switching views never changes a setting. Choose a **Launch mode** (under **Launcher → Session** in Advanced): **Normal** opens the game's menus; **Practice** puts your chosen car on your chosen track; **Race** adds 1–11 AI opponents.
4. Connect your headset, then click **Launch VR**. The launcher checks the headset, adds its VR files and starts the game through Steam. A small progress window shows each step until VR is running, or why VR could not start.
5. Finish with **Quit** in the game. The launcher restores the original game files. After an interrupted session, click **Restore original files**.

The portable ZIP works the same way: extract it into your GRID Autosport folder and run `GRIDAutosportVR\GRIDAutosportVR.exe`.

## While playing

- **Settings → Paths and runtime → VR runtime** picks **OpenXR (system Default)** (your PC's 32-bit OpenXR runtime), **VDXR** (Virtual Desktop; keep SteamVR closed) or **SteamVR**, for the next VR launch only.
- **Settings → Paths and runtime → Skip intro / logos** skips the two startup logo videos when the game starts in VR.
- **Settings → Diagnostics → Performance display** shows frames per second (against the headset's rate) and GPU usage in a small panel below your view in VR. **Diagnostic logging** adds the frame rate and GPU usage each second and a summary of your PC and VR runtime to the session logs, for bug reports. Both are off by default.
- **Controls → Driving → Force feedback**, with "Use launcher force feedback" on, shapes the force the game sends to your wheel in VR: Wheel weight scales the steady steering force, Vibration the kerbs, road surface and impacts, and Strength both. 100% is the game's own force, and the game's own force feedback settings still apply.
- Press **F10** (or the key set under **Controls → VR**) to recenter. The game's own F11 also works. **Controls → VR → Controller / wheel recenter** adds a button on an Xbox controller or a wheel.
- **Graphics → VR rendering** sets the VR render scale; 100% is the headset's recommended resolution. **Texture filtering** sets 16x anisotropic filtering (default, sharper textures at an angle), **16x and steadier distance**, or the game's own. **Classic colors** keeps the lighter, lower-contrast look of 0.2.0 and earlier; by default the headset matches the game on a monitor.
- **Graphics → Graphics preset** sets the game's quality for VR: its own presets, **VR Balanced** or **Potato / Low-End**, or your own mix under **Quality settings**, plus **VR anti-aliasing**. **Game settings** keeps the quality you chose in the game. The launcher applies a preset for each VR session and puts your own settings back afterwards, so changes made inside the game during a VR session last for that session only. **Recommend for this PC** suggests a preset and render scale; after a VR session, **Last VR session** shows the frame rate you drove at, and Recommend fine-tunes from it.
- **Graphics → VR HUD** turns the race HUD and its parts (dial, minimap, counters, position info) on or off for VR, whatever the game's own HUD options say, sets how far ahead of your seat it appears (1–20 m) and whether it follows your view. **Graphics → VR screen → Distance** moves the screen for videos, loading and menus further away (1.5–6 m, default 3 m); **Intro shots** shows the pre-race intro in 3D instead.
- **Controls → Driving → Driving controls…** binds your wheel, pedals, shifter, controller or keyboard: a binding wizard, one action at a time, calibration (deadzone, saturation, direction), an Xbox preset and saved profiles. **Live input…** shows every connected device as you move it, with a rumble / force feedback test. Bindings apply to VR launches, for that session only; choose the transmission in the game's own settings. In Practice and Race the wheel your steering is bound to (or else an Xbox controller) becomes your controller, as pressing Start does in Normal play.
- **Controls → Seat** moves your head in the cockpit for each car (right, forward, height). In the game, press **Tab** in the cockpit to adjust it live: **Up / Down** move the seat up and down, **Left / Right** back and forward, with **Shift** left and right; **Enter** saves, **Esc** cancels. **Seat keys** on the same page changes those keys.
- **Graphics → Cockpit textures** swaps in sharper, AI-redrawn cockpit artwork (2048 x 2048, 79 of the 83 cars) while you play in VR. It is an optional download from the GitHub releases, made the first time you launch VR with it on; **Install from file…** takes a copy of the pack ZIP instead. Game files are not changed; untick it for the originals.
- Startup videos, loading screens, menus, the pause menu and the pre-race intro shots appear on a screen in front of you; races are in stereo.
- Practice and Race start in the driver's seat (**Launcher → Session → Camera**); the game's change-camera button still switches views. Normal races keep the camera you last used.
- In Practice and Race the start lights go green straight away: you start on pole, and the race is yours to drive. Pause with the game's pause button (**Esc**, or **Start** on a controller) to restart or quit. **Quit** closes the game, and the launcher restores the original files; **End session** in the launcher (beside Launch VR) does the same at any time.

## Updates and help

Use **Help / About** (**?** or **F1**) to check for updates and read the instructions. **Include experimental releases** also offers Experimental updates; **Return to stable…** goes back to the latest Stable release. Updates are downloaded from this project's GitHub releases, checked against their published size and SHA-256, and installed into the same game folder; your settings are kept.

**An OpenXR add-on stops VR from starting:** GRID Autosport is a 32-bit game, and an add-on layer such as ReShade or OpenXR OBS Mirror that has no working 32-bit library can stop every 32-bit OpenXR app from starting. **Launch VR** then names the layer and offers **Disable and retry** when its installed registration provides its own disable switch. Review which features will be unavailable before accepting. The layer is disabled only for GRID Autosport VR; other applications keep their settings. **Remember for this GRID Autosport VR installation** is optional and is saved only after the headset check works. To undo it, use **Settings → VR compatibility → Manage layer bypasses… → Re-enable selected**. A changed layer registration needs approval again.

Use **Open logs** in the launcher and attach the newest session folder when reporting a problem. Settings live in `%LOCALAPPDATA%\GRIDAutosportVR` and are kept when you update or uninstall.

GRID Autosport VR requires your own copy of GRID Autosport. No game files or saves are included.

## Building

See `bridge/README.md` and `launcher/README.md`. `tools/Package.ps1 -Version 0.1.0` builds the bridge, probe and launcher from the committed source and writes the installer, portable ZIP and `SHA256SUMS.txt` to `artifacts/packages`.
