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
bool AutosportCaptureRequested();
void AutosportCaptureCompleted();
const wchar_t* AutosportRunDirectory();
void AutosportLog(const char* format, ...);
