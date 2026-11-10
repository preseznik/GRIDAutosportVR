# GRID Autosport VR launcher

C# WinForms launcher (.NET 10, x64) ported from GRID2VR's launcher shell: header with logo, channel and **?** (F1), selectable status and notice lines, owner-drawn tabs of collapsible sections (remembered in `launcher-window.json`), a responsive two/one-column layout, Modern/Classic themes that follow Windows light/dark and high contrast, and the footer **Launch | Launch VR | Save settings | Restore original files | Open logs**.

Choices are kept by **Save settings** or a launch in `%LOCALAPPDATA%\GRIDAutosportVR\settings.json` (`hudFollowView`, `modernInterface`, `vrRenderScale`; other keys are preserved). **Graphics → VR rendering** sets VR render scale relative to the headset's OpenXR recommended per-eye size; **Refresh headset** and every VR launch read that size from the probe and keep it in `headset.json`, and the launch passes the scaled size to the bridge as `-gasvreyesize`. **Launch VR** re-runs the launcher as `--play-vr --game-dir FOLDER`: it checks the 32-bit OpenXR runtime, stages the verified VR executable and bridge under a recovery lease, starts the game through Steam and restores the stock files when it exits. `--restore-play --game-dir FOLDER` restores an interrupted session and `--inspect` prints the detected installation as JSON.

Build and check without a headset or game launch:

```powershell
dotnet build launcher\GRIDAutosportVR.csproj -c Release
launcher\bin\Release\net10.0-windows\win-x64\GRIDAutosportVR.Launcher.exe --self-test artifacts\launcher-selftest
pwsh -File capture\verify_play_session.ps1 -LauncherAssembly launcher\bin\Release\net10.0-windows\win-x64\GRIDAutosportVR.Launcher.dll -StockAvx "<Steam>\steamapps\common\GRID Autosport\GRIDAutosport_avx.exe" -Bridge artifacts\bridge-build\Release\gasxr.dll
dotnet publish launcher\GRIDAutosportVR.csproj -c Release -o artifacts\launcher
```

The self-test uses a temporary data folder and renders every tab, Modern and Classic, at 900 and 600 px to `launcher-*.png` from an off-screen window that never takes focus. `verify_play_session.ps1` exercises staging and recovery on a copy of the stock executable in a temporary Steam library. The single-file publish places `probe\AutosportXRProbe.exe` and `renderer\gasxr.dll` beside the launcher.
