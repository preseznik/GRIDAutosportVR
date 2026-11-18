# Changelog

## 0.2.1

- **Colors in VR no longer look washed out.** The headset now shows the game's colors as a monitor does, with full contrast and dark shadows. **Graphics → VR rendering → Classic colors** keeps the lighter look of 0.2.0 and earlier if you prefer it.
- **Shadow acne fix.** Thin shadow stripes across sunlit walls that crawled as you moved your head (San Francisco was the worst) are shadow acne: the game's shadow bias suits its desktop camera, not a headset's much wider view. VR launches now double the shadow bias. **Graphics → VR rendering → Shadow acne fix** offers 4x for the fewest stripes, or Off.
- **Sharper textures at an angle.** VR launches use 16x anisotropic filtering instead of the game's mostly 4x. **Graphics → VR rendering → Texture filtering** also offers **16x and steadier distance** or the game's own filtering.
- **The view no longer skews when you brake.** With launcher driving controls, the game's own default look mappings stayed on wheel axes, so on many wheels (Logitech G29 and G920, Fanatec Clubsport and others) the brake or clutch pedal also moved the view. Look actions you have not bound now lose any input you bound for driving and, on wheels and pedals, every axis; look buttons stay. In presets for a wheel base with separate pedals, a binding now applies only to the device you bound it on.
- **Performance display.** Settings → Diagnostics → Performance display shows a small panel below your view in the headset: frames per second against the headset's refresh rate (amber while below it) and how busy your graphics card is. Off by default.
- **Diagnostic logging.** Settings → Diagnostics → Diagnostic logging adds troubleshooting detail to the session logs (the frame rate and GPU usage each second, and a summary of your PC and VR runtime) for bug reports. Off by default; **Open logs** shows the files.

## 0.2.0

- **Graphics presets for VR.** Graphics → Graphics preset picks the game's own presets (Ultra Low to Ultra), **VR Balanced** (High with Medium crowd and VR anti-aliasing Off) or **Potato / Low-End** (Ultra Low, anti-aliasing Off, 70% render scale), with every quality setting of the game's Graphics menu under Quality settings and **VR anti-aliasing** beside them. **Game settings** (the default) keeps the quality you chose in the game. A preset applies to VR launches only: the launcher sets it in the game's graphics settings for the session and puts yours back afterwards.
- **Recommend for this PC** suggests a preset and VR render scale from your graphics card and headset, or from your last VR session, with **Undo preset**. **Last VR session** shows the frame rate measured while driving against the headset's rate.
- **Launch progress.** Launch VR shows a small window with each step (checking the headset, preparing the game files, Steam starting the game, loading, waiting for the headset) until VR is running, and the reason when VR cannot start. It does not take focus from the game, and **Hide** lets the launch continue. The launcher's status line follows the session too.
- **Simple and Advanced views.** **Simple** (beside **?**) shows the essentials on one Quick start page: Play (runtime, launch mode and the session), Performance (graphics preset, Recommend, VR render scale) and Controls & comfort (recenter key, driving controls, HUD, seat adjustment). **Advanced** shows every tab, after a confirmation. Both edit the same settings, and switching never changes or saves one. New installations start in Simple; existing settings open in Advanced.

## 0.1.1

- **VR runtime.** Settings → Paths and runtime → VR runtime chooses OpenXR (system Default, this PC's 32-bit OpenXR runtime), VDXR or SteamVR for the headset check and the VR session, without changing the system default.
- **Pause menu in Practice and Race.** Esc, or Start on a controller, opens the game's pause menu, with Restart and Quit; Quit now closes the VR session cleanly before the game exits.
- **Skip intro / logos.** Settings → Paths and runtime → Skip intro / logos skips the two startup logo videos when the game starts in VR.
- **Force feedback.** Controls → Driving → Force feedback ("Use launcher force feedback", off by default) sets Wheel weight, Strength and Vibration for the force the game sends to your wheel in VR; 100% is the game's own force.
- **OpenXR add-ons that stop VR from starting** ([#1](https://github.com/preseznik/GRIDAutosportVR/issues/1)). An add-on layer such as ReShade or OpenXR OBS Mirror without a working 32-bit library stopped the headset check ("The 32-bit OpenXR probe failed with exit code 1"). Launch VR now names the layer and offers **Disable and retry**, for GRID Autosport VR only; the choice can be remembered and undone under Settings → VR compatibility.

## 0.1.0

First release of GRID Autosport VR.

- Native stereo VR with head tracking through a 32-bit OpenXR runtime (Virtual Desktop VDXR tested on a Quest 3).
- Normal, Practice and Race launches, with car, track, laps and AI opponents.
- Cockpit start, per-car seat adjustment with an in-game seat panel, and full detail on nearby opponents.
- VR render scale, VR HUD options and a seated screen for videos, loading, menus and intro shots.
- Custom driving controls with force feedback, and keyboard, controller or wheel recenter bindings.
- Optional redrawn cockpit textures for 79 cars, downloaded from the launcher.
- A red modern interface, Help / About and verified GitHub updates; original game files are restored after each session.

Requires the Steam GRID Autosport (AVX executable, build 4011980). No game files or saves are included. Practice and Race have no pause menu (use End session), and the pre-race intro's info panel is cut at the left (Intro shots in 3D hides it). Report issues with the session logs.
