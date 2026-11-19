// The game builds the bridge supports, and where each keeps the code and data the bridge uses.
// Every value is an RVA in that build's GRIDAutosport executable. The Steam build (2019 update,
// GRIDAutosport_avx.exe) is the reference; the 2014 build (GRIDAutosport.exe 1.0.103.1840, linked
// 2014-11-03) is the same game version, linked in a different order. Its addresses were mapped
// from the Steam build's by matching functions (shared strings, then aligned call graphs) and
// instructions, and every structure offset the bridge reads is the same in both.
#pragma once
#include <windows.h>

enum NativeSite : unsigned {
    SitePrepareExit, SiteBenchmarkEnd, SiteBenchmarkResults, SiteFrontend1, SiteFrontend2, SiteFrontend3,
    SiteSlate1, SiteSlate2, SiteLoadingEntered, SiteLoadingExited, SiteEndFrame, SiteBeginFrame, SiteDestroyHmd,
    SiteShutdownSdk, SiteInitialize, SiteCreateHmd, SiteAttachWindow, SiteHealthWarning, SiteTextureSize,
    SiteEyePose, SiteRecenter, SiteConfigureRenderer, SiteSetCaps, SiteConfigureTracking, SiteEyeDesc, SiteHudOrtho,
    SiteUiCamera, SiteRaceStartView, SiteHudEnabled, SiteHudPart1, SiteHudPart2, SiteHudPart3, SiteHudPart4,
    SitePauseEntered, SiteOpponentLod, SiteShadowFit, SiteCount
};

struct GameBuild {
    const char* name;
    const wchar_t* executable;      // The file the VR session stages and the game runs as.
    DWORD timestamp, imageSize;     // PE header, to recognise the build.
    DWORD importName;               // The d3d11.dll import name the launcher renames to gasxr.dll.
    struct { UINT call, target; } sites[SiteCount]; // Five-byte E8 calls the bridge redirects.
    UINT benchmarkEndResume, benchmarkResultsResume, exitProcessSlot;
    // Functions the bridge calls.
    UINT internName, enumerateDevices, releaseVector, bindDevice, loadingDispatch, makeVisibility, drawFrontend,
         terminalCleanup, terminalExit, vehicleView, focusedCar, opponentLodApply, shadowFit, eyeDesc, hudOrtho,
         cameraUpdate, viewLookup, hudEnabled, settingLookup, pauseEntry;
    // Data: the service indices (input, players, pause), the benchmark state, the HUD setting names.
    UINT inputService, playersService, pauseService, benchmarkState, hudNames[4];
    // Code: the OSD layout call and its return address.
    UINT osdCaller, osdLayoutReturn;
    // Frontend state vtables (Discipline and Championship) and the slots the bridge wraps.
    UINT disciplineTable, disciplineEnter, championshipTable, championshipEnter, videoLeave;
};

// The running game's build, or nullptr when it is not a supported one.
const GameBuild* AutosportGameBuild();
