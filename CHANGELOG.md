# Changelog

## 0.1.0 Experimental

First packaged build of GRID Autosport VR for the Steam game (AVX renderer, build 4011980).

- Native stereo racing with head tracking through the game's own HMD renderer and a 32-bit OpenXR bridge (Virtual Desktop / VDXR tested on a Quest 3).
- Startup videos, loading screens and the menus appear on a screen in front of your seat; races are in stereo with a seated HUD (or one that follows your view).
- VR render scale relative to the headset's recommended resolution (Graphics → VR rendering).
- Recenter key (default F10, Controls → VR) and Keep horizon level; the game's F11 also recenters.
- The game keeps running when its window is not focused, so the headset view never stalls.
- Launcher in GRID2VR's layout: collapsible sections, Modern/Classic themes, Save settings, Restore original files and Open logs.
- Launch VR adds the VR files for the session only and restores the original game files when the game exits.
- Launch mode Practice or Race (Launcher → Session): pick an installed car and track, 1–20 laps and, for Race, 1–11 AI opponents (same car, same discipline or mixed). Launch VR goes straight to the grid and you drive; Normal keeps the game's own menus.

- End session (beside Launch VR while a session runs) closes the game and restores the original files.
- Practice and Race in VR start in the cockpit view (Launcher → Session → Camera, on by default).
- The pre-race intro shots appear on the seated screen instead of as a broken panel in the headset.
- The in-race pause menu appears on the seated screen, whole, instead of following your view and running past the bottom of it.
- The seated screen is 16:9, so menus, loading cards and intro shots are no longer cut off at the sides.
- Graphics → VR screen → Intro shots: show the pre-race intro in 3D, without the game's 2D info panel and wipes (off by default).
- Graphics → VR HUD: Show HUD and HUD parts (dial, minimap, counters, position info) override the game's own HUD options in VR sessions, and Distance places the HUD 1–20 m ahead of your seat.
- Graphics → VR screen → Distance: the screen for videos, loading, menus and intro shots now sits 3 m ahead of your seat (was 2 m), adjustable from 1.5 to 6 m.

Known limits: menus are shown flat. Practice and Race start from Launch VR only, in day and dry conditions, and you start on pole; they have no pause menu, so stop them with End session. Controller/wheel recenter buttons are not available yet.
