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
- A video the game plays over a loaded race (the career start's intro) is shown on the seated screen instead of leaving the headset on a frozen frame of the race, also with Intro shots in 3D.
- Nearby opponents keep the detail the game gives a close car in front (its opponent model) beside and behind you in VR, instead of dropping to low-detail models, as in GRID2VR. Higher-detail nearby cars can cost some performance.
- The in-race pause menu appears on the seated screen, whole, instead of following your view and running past the bottom of it.
- The seated screen is 16:9, and the game's loading cards and menus are laid out across the whole of it in their own proportions, so their text is no longer cut off or stretched at the sides. The pre-race intro's info panel still loses part of its left edge (Intro shots in 3D hides it).
- Graphics → VR screen → Intro shots: show the pre-race intro in 3D, without the game's 2D info panel and wipes (off by default).
- Controls → Driving: driving bindings as in GRID2VR (binding wizard, Key… / Device…, calibration, Xbox preset, profiles) and a live input view with a rumble / force feedback test. VR launches apply them to the game's own input presets for that session only, and the originals are restored afterwards. Practice and Race make your bound wheel (or else an Xbox controller) the player's controller, as the game's Press Start screen does in Normal play, so the bindings and force feedback work there too.
- Controls → Seat: move your head in the cockpit view per car (right, forward, height, up to 50 cm each way), as in GRID2VR. In the game, Tab in the cockpit opens a seat panel in the headset: arrow keys and Page Up / Page Down move the seat, Enter or Tab saves, Esc cancels; those keys do not reach the game while it is open.
- Controls → VR → Controller / wheel recenter: bind a button or hat direction on an Xbox controller or a wheel that recenters alongside the key.
- Graphics → VR HUD: Show HUD and HUD parts (dial, minimap, counters, position info) override the game's own HUD options in VR sessions, and Distance places the HUD 1–20 m ahead of your seat.
- Graphics → Cockpit textures: optional AI-redrawn cockpit artwork for 79 cars, as in GRID2VR, at 2048 x 2048 instead of 512 x 512. The pack is downloaded from the GitHub releases the first time you launch VR with it on (or installed from a copy of its ZIP), verified, and swapped in by the VR renderer in memory; game files are not changed. Markings and materials can differ slightly from the originals. Four cars keep their original cockpit (the image tool refused to redraw them): BMW 320 TC, Ariel Atom V8, Caterham-Lola SP/300.R and Mini Challenge.
- Graphics → VR screen → Distance: the screen for videos, loading, menus and intro shots now sits 3 m ahead of your seat (was 2 m), adjustable from 1.5 to 6 m.

Known limits: menus are shown flat. Practice and Race start from Launch VR only, in day and dry conditions, and you start on pole; they have no pause menu, so stop them with End session.
