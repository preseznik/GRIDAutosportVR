#pragma once
#include <cstdint>

void AutosportBindLoadingOverlay(const void* module);
void* __fastcall AutosportSlateVisibility(void* message, void*, unsigned int visible);
void __cdecl AutosportDrawFrontendOverlay(void* renderer, void* root, void* camera,
                                          unsigned int flags, unsigned int mask);
void AutosportBeginLoadingOverlayFrame(uint64_t frame, bool scene);
const char* AutosportLoadingOverlaySample(uint64_t frame);
void AutosportFinishLoadingOverlayFrame(uint64_t frame, bool captured);
