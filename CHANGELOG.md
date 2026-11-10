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

Known limits: menus are shown flat; choose cars and events in the game's menus. Controller/wheel recenter buttons and launcher-side Practice/Race selection are not available yet.
