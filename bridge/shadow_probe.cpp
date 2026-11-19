// Shadow cascades in VR. The game fits its two shadow cascades once a frame in 0x684BF0, called
// once from 0x68B7E4 (thiscall on the shadow system, five arguments, ret 14h). The fourth
// argument describes the car camera the cascades cover: up (+0x10), minus right (+0x20), forward
// (+0x30), position (+0x40), vertical field of view in radians (+0x50), near and far. The third
// is the head-tracked camera: world rows right (+0x30), up (+0x40), back (+0x50), position
// (+0x60), and the aspect the fit uses (+0xF0). The game covers its desktop camera, 48 degrees
// straight ahead; the headset sees about 100 degrees per eye wherever the head points, and the
// screen-space shadow mask picks a cascade per pixel from the eye's own depth. Off to the side,
// walls fall outside the detailed cascade, and shadows change shape as the head moves.
//
// -gasvrshadowview=follow (launcher Graphics -> VR rendering -> VR shadows, the default) fits the
// cascades to the head-tracked view with the headset's field of view (both eyes); =wide keeps the
// car's direction and widens the field of view to the headset's. Without it the game's fit runs
// unchanged. Both apply to stereo frames only, and the camera data is restored after the fit.
// Test only: -gasvrshadowprobe=1 logs the fit's arguments every 300th frame;
// -gasvrshadowfov=MILLIRADIANS and -gasvrshadowaspect=PERCENT force the covered field of view.
#include "bridge_api.h"
#include <windows.h>
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <cwchar>

namespace {
using FitFn = void(__fastcall*)(void* self, void*, void* a0, void* a1, void* a2, void* a3, void* camera);
FitFn stockFit;
enum class View { Game, Follow, Wide } view = View::Game;
bool probing;
float fitFov, fitAspect;
uint64_t lastFrame, callsThisFrame, callsLastFrame;
bool loggedMode;

void dumpFloats(char* out, size_t size, const void* p, unsigned count) {
    size_t used = 0;
    __try {
        const float* f = static_cast<const float*>(p);
        for (unsigned i = 0; i < count && used + 16 < size; ++i)
            used += snprintf(out + used, size - used, "%s%.4g", i ? " " : "", f[i]);
    } __except (EXCEPTION_EXECUTE_HANDLER) { snprintf(out + used, size - used, " <unreadable>"); }
}

void probe(void* self, void* a0, void* a1, void* a2, void* a3, void* camera) {
    const uint64_t frame = AutosportNativeFrame();
    if (frame != lastFrame) { callsLastFrame = callsThisFrame; callsThisFrame = 0; lastFrame = frame; }
    ++callsThisFrame;
    if (frame % 300 != 0 || callsThisFrame > 2) return;
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

// The covered vertical field of view (radians) and aspect for the headset: both eyes, symmetric.
bool headsetCoverage(float& fov, float& aspect) {
    float tangents[4]{};
    if (!AutosportHeadsetFov(tangents)) return false;
    const float vertical = (std::max)(tangents[0], tangents[1]), horizontal = (std::max)(tangents[2], tangents[3]);
    if (!(vertical > .1f && vertical < 10 && horizontal > .1f && horizontal < 10)) return false;
    fov = 2 * std::atan(vertical);
    aspect = horizontal / vertical;
    return true;
}
}

void __fastcall AutosportShadowFit(void* self, void* edx, void* a0, void* a1, void* a2, void* a3, void* camera) {
    if (probing) probe(self, a0, a1, a2, a3, camera);
    BYTE* car = static_cast<BYTE*>(a3);
    BYTE* tracked = static_cast<BYTE*>(a2);
    float coverFov = fitFov, coverAspect = fitAspect;
    bool follow = false;
    if (view != View::Game && car && tracked && !AutosportFlatScreen()) {
        float fov, aspect;
        if (headsetCoverage(fov, aspect)) {
            // Never cover less than the game's own camera.
            if (coverFov <= 0) coverFov = (std::max)(fov, *reinterpret_cast<float*>(car + 0x50));
            if (coverAspect <= 0) coverAspect = aspect;
            follow = view == View::Follow;
            if (!loggedMode) {
                loggedMode = true;
                AutosportLog("VR shadows: %s, covering %.1f degrees at aspect %.2f (game camera %.1f degrees, aspect %.2f)",
                             follow ? "follow the view" : "wide around the car", coverFov * 57.29578f, coverAspect,
                             *reinterpret_cast<float*>(car + 0x50) * 57.29578f, *reinterpret_cast<float*>(tracked + 0xF0));
            }
        }
    }
    float savedBasis[12]{}, savedFov{}, savedAspect{};
    if (car) { std::memcpy(savedBasis, car + 0x10, sizeof(savedBasis)); savedFov = *reinterpret_cast<float*>(car + 0x50); }
    if (tracked) savedAspect = *reinterpret_cast<float*>(tracked + 0xF0);
    if (follow) {
        // Up, minus right and forward from the head-tracked rows (w lanes unchanged).
        const float* right = reinterpret_cast<const float*>(tracked + 0x30);
        const float* up = reinterpret_cast<const float*>(tracked + 0x40);
        const float* back = reinterpret_cast<const float*>(tracked + 0x50);
        float* basis = reinterpret_cast<float*>(car + 0x10);
        for (int i = 0; i < 3; ++i) { basis[i] = up[i]; basis[4 + i] = -right[i]; basis[8 + i] = -back[i]; }
    }
    if (car && coverFov > 0) *reinterpret_cast<float*>(car + 0x50) = coverFov;
    if (tracked && coverAspect > 0) *reinterpret_cast<float*>(tracked + 0xF0) = coverAspect;
    stockFit(self, edx, a0, a1, a2, a3, camera);
    if (car) { std::memcpy(car + 0x10, savedBasis, sizeof(savedBasis)); *reinterpret_cast<float*>(car + 0x50) = savedFov; }
    if (tracked) *reinterpret_cast<float*>(tracked + 0xF0) = savedAspect;
}

void AutosportConfigureShadowProbe(const BYTE* image) {
    const wchar_t* cmd = GetCommandLineW();
    probing = wcsstr(cmd, L" -gasvrshadowprobe=1") != nullptr;
    view = wcsstr(cmd, L" -gasvrshadowview=follow") ? View::Follow : wcsstr(cmd, L" -gasvrshadowview=wide") ? View::Wide : View::Game;
    stockFit = reinterpret_cast<FitFn>(const_cast<BYTE*>(image) + 0x284BF0);
    if (const wchar_t* arg = wcsstr(cmd, L" -gasvrshadowfov=")) fitFov = wcstoul(arg + wcslen(L" -gasvrshadowfov="), nullptr, 10) / 1000.f;
    if (const wchar_t* arg = wcsstr(cmd, L" -gasvrshadowaspect=")) fitAspect = wcstoul(arg + wcslen(L" -gasvrshadowaspect="), nullptr, 10) / 100.f;
}
