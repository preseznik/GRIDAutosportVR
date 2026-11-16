# Changelog

## 0.1.2

- **Graphics presets for VR.** Graphics → Graphics preset picks the game's own presets (Ultra Low to Ultra), **VR Balanced** (High with Medium crowd and VR anti-aliasing Off) or **Potato / Low-End** (Ultra Low, anti-aliasing Off, 70% render scale), with every quality setting of the game's Graphics menu under Quality settings and **VR anti-aliasing** beside them. **Game settings** (the default) keeps the quality you chose in the game. A preset applies to VR launches only: the launcher sets it in the game's graphics settings for the session and puts yours back afterwards.
- **Recommend for this PC** suggests a preset and VR render scale from your graphics card and headset, or from your last VR session, with **Undo preset**. **Last VR session** shows the frame rate measured while driving against the headset's rate.

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
