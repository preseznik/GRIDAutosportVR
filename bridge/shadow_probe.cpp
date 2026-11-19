// Test only: the game's shadow cascade fit. The game fits its two shadow cascades once a frame in
// RVA 284BF0 (Steam build), called once from 28B7E4 (thiscall on the shadow system, five
// arguments, ret 14h). The fourth argument is the car camera the cascades cover: up (+0x10), minus
// right (+0x20), forward (+0x30), position (+0x40), vertical field of view in radians (+0x50), near
// and far; the third (+0xF0: the aspect) is the same camera. Neither follows the head: the fit
// covers the car's view, and both eyes then use the same world-to-shadow transform (checked with
// shadow_constants.cpp), so it holds wherever the head turns. The shadow stripes that followed the
// head came from the 16-bit scene depth instead (depth_precision.cpp).
// -gasvrshadowprobe=1 logs the fit's arguments and result every 300th frame (=N every Nth);
// -gasvrshadowfov=MILLIRADIANS and -gasvrshadowaspect=PERCENT force the covered field of view.
#include "game_build.h"
#include "bridge_api.h"
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <cwchar>

namespace {
using FitFn = void(__fastcall*)(void* self, void*, void* a0, void* a1, void* a2, void* a3, void* camera);
FitFn stockFit;
bool probing;
float fitFov, fitAspect;
uint64_t lastFrame, callsThisFrame, callsLastFrame;
unsigned probeEvery = 300;

void dumpFloats(char* out, size_t size, const void* p, unsigned count) {
    size_t used = 0;
    __try {
        const float* f = static_cast<const float*>(p);
        for (unsigned i = 0; i < count && used + 16 < size; ++i)
            used += snprintf(out + used, size - used, "%s%.4g", i ? " " : "", f[i]);
    } __except (EXCEPTION_EXECUTE_HANDLER) { snprintf(out + used, size - used, " <unreadable>"); }
}

// Test only: the shadow system after the fit (its cascade matrices), 64 floats a line.
void probeResult(void* self) {
    if (AutosportNativeFrame() % probeEvery != 0 || callsThisFrame > 1) return;
    static char text[2048];
    for (unsigned offset = 0; offset < 0xC00; offset += 0x100) {
        dumpFloats(text, sizeof(text), static_cast<BYTE*>(self) + offset, 64);
        AutosportLog("Shadow fit result +%03X: %s", offset, text);
    }
}

void probe(void* self, void* a0, void* a1, void* a2, void* a3, void* camera) {
    const uint64_t frame = AutosportNativeFrame();
    if (frame != lastFrame) { callsLastFrame = callsThisFrame; callsThisFrame = 0; lastFrame = frame; }
    ++callsThisFrame;
    if (frame % probeEvery != 0 || callsThisFrame > 2) return;
    float head[4]{};
    AutosportHeadOrientation(head);
    AutosportLog("Shadow fit frame=%llu call=%llu previous_frame_calls=%llu self=%p a0=%p a1=%p a2=%p a3=%p camera=%p head_q=(%.4f %.4f %.4f %.4f)",
                 frame, callsThisFrame, callsLastFrame, self, a0, a1, a2, a3, camera, head[0], head[1], head[2], head[3]);
    static char text[2048];
    const void* args[] = {a0, a1, a2, a3, camera};
    const char* names[] = {"a0", "a1", "a2", "a3", "camera"};
    for (int i = 0; i < 5; ++i) {
        dumpFloats(text, sizeof(text), args[i], 96);
        AutosportLog("Shadow fit %s: %s", names[i], text);
    }
}

}

void __fastcall AutosportShadowFit(void* self, void* edx, void* a0, void* a1, void* a2, void* a3, void* camera) {
    if (probing) probe(self, a0, a1, a2, a3, camera);
    BYTE* car = static_cast<BYTE*>(a3);
    BYTE* tracked = static_cast<BYTE*>(a2);
    float savedFov{}, savedAspect{};
    if (car) savedFov = *reinterpret_cast<float*>(car + 0x50);
    if (tracked) savedAspect = *reinterpret_cast<float*>(tracked + 0xF0);
    if (car && fitFov > 0) *reinterpret_cast<float*>(car + 0x50) = fitFov;
    if (tracked && fitAspect > 0) *reinterpret_cast<float*>(tracked + 0xF0) = fitAspect;
    stockFit(self, edx, a0, a1, a2, a3, camera);
    if (probing) probeResult(self);
    if (car) *reinterpret_cast<float*>(car + 0x50) = savedFov;
    if (tracked) *reinterpret_cast<float*>(tracked + 0xF0) = savedAspect;
}

void AutosportConfigureShadowProbe(const BYTE* image) {
    const wchar_t* cmd = GetCommandLineW();
    if (const wchar_t* arg = wcsstr(cmd, L" -gasvrshadowprobe=")) {
        const unsigned every = wcstoul(arg + wcslen(L" -gasvrshadowprobe="), nullptr, 10);
        probing = every != 0;
        if (every > 1) probeEvery = every;
    }
    stockFit = reinterpret_cast<FitFn>(const_cast<BYTE*>(image) + AutosportGameBuild()->shadowFit);
    if (const wchar_t* arg = wcsstr(cmd, L" -gasvrshadowfov=")) fitFov = wcstoul(arg + wcslen(L" -gasvrshadowfov="), nullptr, 10) / 1000.f;
    if (const wchar_t* arg = wcsstr(cmd, L" -gasvrshadowaspect=")) fitAspect = wcstoul(arg + wcslen(L" -gasvrshadowaspect="), nullptr, 10) / 100.f;
}
