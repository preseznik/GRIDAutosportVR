#pragma once
#include <d3d11.h>

bool AutosportUseHeadset();
bool AutosportRegularPlay();
bool AutosportHudFollowsView();
bool AutosportUiComparison();
bool AutosportCompareSlate();
const char* AutosportTransitionSample();
bool AutosportFlatScreen();
void __fastcall AutosportLoadingEntered(void* dispatcher, void*, const void* event);
void __fastcall AutosportLoadingExited(void* dispatcher, void*, const void* event);
bool AutosportCaptureRequested();
void AutosportCaptureCompleted();
const wchar_t* AutosportRunDirectory();
void AutosportLog(const char* format, ...);
