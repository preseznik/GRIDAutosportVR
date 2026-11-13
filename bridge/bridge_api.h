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
