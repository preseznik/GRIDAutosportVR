# Changelog

## 0.2.6

- **Every pause screen fits the VR screen.** In a race, the pause menu's own list fitted the screen, but the screens it opens (Options and its Controls screens, Rules, Difficulty and the confirmation dialogs) were drawn too large and cut off at both sides and the bottom. Every pause screen now shows whole, at any game resolution.

## 0.2.5

- **Difficulty in Practice and Race.** Practice and Race load no save profile, and the game set up the player's car there as a computer-controlled one: your car drove with ABS only, and the game's Difficulty screen in the pause menu changed nothing. Your car now gets the game's difficulty settings (by default Medium AI, automatic transmission, ABS and traction control, full damage and 5 flashbacks). **Launcher → Difficulty** starts Practice and Race with your own transmission (automatic, manual, H-pattern shifter, with clutch), AI difficulty, damage, flashbacks and assists (ABS, traction control, stability control, cornering and steering assist), and the pause menu's Difficulty screen shows them; a change made there applies when the game restarts the event for it. Normal launches keep the difficulty from your save profile.
- **Night Practice and Race.** **Launcher → Session → Time of day** (also on Quick start) runs Practice and Race at night on the routes the game has a night version of: most routes at Algarve, Austin, Brands Hatch, Hockenheim, Mont-Tremblant, Okutama, Sepang, Spa-Francorchamps and Yas Marina. It is available when such a route is chosen; other routes run by day, and the session summary says so.
- **A visible cockpit at night.** In night races the inside of the car was pitch black wherever no outside light reached it: the dashboard, the steering wheel, your arms and the seats. The game's interior lighting cuts everything below a fixed brightness to black for its helmet camera, and at night almost the whole cabin is below it. **Graphics → VR rendering → Cockpit light at night** (on by default) removes that cut at night and adds a little light, so the cabin stays faintly visible in the light of the night around you. Daytime races are unchanged.
- **Driving controls work in Normal launches.** In a Normal VR launch the game applied the controls saved in your profile after Press Start, so the controller and wheel bindings from Driving controls did nothing there (they worked only in Practice and Race). They now apply in Normal launches too, from the first race on, for that session only: the controls saved in your profile are kept as they are, and the game's own saves keep them even while the launcher's bindings are in use (changes made in the game's Controls screen during such a session are not saved). Keyboard bindings still apply in Practice and Race only.
- **Your controller on the Practice and Race loading screen.** The loading screen before Practice and Race showed Enter as its continue button and took only Enter: the launcher made your controller or wheel the player's only once loading was over. It now does so as loading starts, so the screen's button is your controller's.
- **Menus stay after a race.** In a Normal VR launch the race results, the paddock area and the menus after them were blank again once you left a race (Return to paddock area, the finish, or back to the main menu). 0.2.4 removed the grey bar whenever the game showed a car through the race camera, and after a race the game does that under the results and behind the menus too. The bar is now removed only while you drive in a race, on your own cameras (chase, bumper, bonnet, dashboard and the driver's view, and looking back or to the side from them).
- **No more team-mate radio on every shift.** A button bound to Gear Up or Gear Down in Driving controls could also stay on a team radio action: the Xbox preset puts Team orders on RB and LB, and a wheel the game has no preset for (Logitech G29 and G920, Fanatec DD and others) kept the game's gamepad defaults, team orders and Clutch, on its shift paddles. Every shift then had the race engineer say "I've asked your teammate...". A button you bind to a driving action now leaves any other driving or team radio action on that controller, as the game's own control settings do, and bindings saved earlier keep the driving action. Menu and replay controls still share buttons with driving.

## 0.2.4

- **Controllers checked before launch.** Launch VR, and Launch with Practice or Race, now check that the wheels and other controllers in Driving controls are connected. If Windows has given a wheel a new identity (after a driver or G HUB update, another USB port or a reinstall), its bindings and controller recenter button follow it, and the launcher says so. If a bound controller is missing, or now reports as another model (as after switching a wheel's compatibility mode), the launcher names it and asks before starting a session in which its bindings would do nothing.
- **Sharp image for the whole session.** With the game set to fullscreen, anything that took focus from the game during a session (another window, the VR streamer, Alt+Tab) made Windows drop it out of fullscreen. The game then switched to a small 800x600 window, and the headset image dropped to a fraction of its resolution for the rest of the session, often a few seconds into a race. VR launches now run the game in a window of the same size, which keeps full resolution whatever has focus. Your own display settings are restored afterwards.
- **The VR screen is closer by default.** The screen for videos, loading and menus now starts at 1.5 m (Graphics → VR screen → Distance), the nearest setting. A distance you saved earlier is kept.
- **Menus are back in Normal launches.** 0.2.3's fix for the grey bar while racing also hid the game's menus in a Normal VR launch, so the main menu and every screen before the race were blank in the headset. The bar is now removed only while you drive, and the menus show again.

## 0.2.3

- **Practice and Race on the desktop.** **Launch** now starts the chosen Practice or Race on the desktop too, with your car, track, laps and opponents, and your controller or wheel already in charge. The launcher adds its session files for the run and restores the original files when the game exits, as for VR. With **Normal**, Launch still starts the unmodified game.
- **The 2014 GRIDAutosport.exe is no longer changed.** For the 2014 version the launcher now leaves GRIDAutosport.exe exactly as it is and puts the VR renderer beside it as d3d11.dll for the session, removing it afterwards. Some copies of the 2014 version refused to start once the executable was changed (Windows reported error 0xc0000142). If the game folder already has a d3d11.dll from another mod, such as ReShade, the launcher asks you to move it first. The Steam version is unchanged.
- **No more launcher error as VR starts.** The launcher could show an error ("Cannot access a disposed object. Object name: 'Icon'") just as VR started, when the game changed the display mode while the launch progress window was closing. VR itself kept running.
- **No grey bar at the bottom of the view.** With the game set to a widescreen resolution (most monitors), a dark band with a grey bar, left over from the loading screen, covered the bottom of the view while driving in VR. It is gone.
- **The pause menu is readable at any game resolution.** With a widescreen game resolution, the in-race pause menu was shrunk into a thin strip above a large dark panel. It now fills the screen as intended.
- **Continue from the pause menu.** In Practice and Race the pause menu had no Continue, so you could only restart or quit. It now has **Continue** (and **Options**), as in the rest of the game. Choose Continue to return to the race; Esc or Back still does not close the menu.

## 0.2.2

- **No more shadow stripes on buildings.** With the sun behind you, sunlit walls showed dark stripes that changed as you turned your head. The game keeps the scene's depth in 16 bits, too coarse for the cockpit camera's close near plane, so its shadows misjudged where walls are. VR now keeps the scene's depth in 32 bits.
- **The 2014 GRIDAutosport.exe works too.** Besides the Steam version, the launcher now accepts GRIDAutosport.exe 1.0.103.1840 from 2014 in any folder, starts it directly instead of through Steam, and the VR renderer supports it with all the same features. When a chosen folder is not usable, the launcher says why.

## 0.2.1

- **Colors in VR no longer look washed out.** The headset now shows the game's colors as a monitor does, with full contrast and dark shadows. **Graphics → VR rendering → Classic colors** keeps the lighter look of 0.2.0 and earlier if you prefer it.
- **Sharper textures at an angle.** VR launches use 16x anisotropic filtering instead of the game's mostly 4x. **Graphics → VR rendering → Texture filtering** also offers **16x and steadier distance** or the game's own filtering.
- **The view no longer skews when you brake.** With launcher driving controls, the game's own default look mappings stayed on wheel axes, so on many wheels (Logitech G29 and G920, Fanatec Clubsport and others) the brake or clutch pedal also moved the view. Look actions you have not bound now lose any input you bound for driving and, on wheels and pedals, every axis; look buttons stay. In presets for a wheel base with separate pedals, a binding now applies only to the device you bound it on.
- **Performance display.** Settings → Diagnostics → Performance display shows a small panel below your view in the headset: frames per second against the headset's refresh rate (amber while below it) and how busy your graphics card is. Off by default.
- **Diagnostic logging.** Settings → Diagnostics → Diagnostic logging adds troubleshooting detail to the session logs (the frame rate and GPU usage each second, and a summary of your PC and VR runtime) for bug reports. Off by default; **Open logs** shows the files.

Known issue: with the sun behind you, shadows on buildings change shape as you move your head. The game refits its shadows to the view every frame; a fix is being worked on.

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
