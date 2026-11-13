#pragma once
#include <d3d11.h>

bool AutosportUseHeadset();
bool AutosportRegularPlay();
bool AutosportStartInCockpit();
bool AutosportPracticeSession();
void AutosportSetVehicleViews(const void* manager);
void AutosportSampleVehicleCamera();
void AutosportSetPauseState(const void* state);
bool AutosportPreRaceIntro3D();
bool AutosportPauseMenuShown();
bool AutosportRaceScene();
// The game executable's loaded base.
const BYTE* AutosportGameImage();
// Replaces the LOD selector's apply call (RVA 5FE647): nearby opponents keep their best model in VR.
void __fastcall AutosportOpponentLodApply(void* manager, void*);
bool AutosportHudFollowsView();
bool AutosportHudParts(unsigned& mask);
float AutosportHudDistance();
float AutosportScreenDistance();
bool AutosportKeepActive();
bool AutosportRecenterShortcut(unsigned& key, unsigned& modifiers);
bool AutosportKeepHorizonLevel();
bool AutosportEyeSizeRequest(unsigned& width, unsigned& height);
bool AutosportUiComparison();
bool AutosportCompareSlate();
const char* AutosportTransitionSample();
bool AutosportFlatScreen();
// A career discipline / DLC championship intro video state is active (seated screen from the back buffer).
bool AutosportIntroVideo();
bool AutosportStartupFromBackBuffer();
void __fastcall AutosportLoadingEntered(void* dispatcher, void*, const void* event);
void __fastcall AutosportLoadingExited(void* dispatcher, void*, const void* event);
// Practice / Race device claim (device_claim.cpp): -gasvrdevice=FAMILY[,FAMILY...].
bool AutosportTakeDeviceRecenter();
// Cockpit texture observe / replace (cockpit_textures.cpp): -gasvrcockpittextures=observe|replace.
struct ID3D11Device;
void AutosportConfigureCockpitTextures(ID3D11Device* device, const wchar_t* sessionFolder, const wchar_t* gameFolder);
void AutosportConfigureDeviceClaim(const BYTE* image, const wchar_t* commandLine, bool practice);
extern "C" void AutosportLoadingExitedThunk();
bool AutosportCaptureRequested();
void AutosportCaptureCompleted();
const wchar_t* AutosportRunDirectory();
void AutosportLog(const char* format, ...);
