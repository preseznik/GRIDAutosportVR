#include "native_ovr.h"
#include "loading_overlay.h"
#include "bridge_api.h"
#include "xr_bridge.h"
#include <atomic>
#include <cmath>
#include <cstddef>
#include <cstring>

namespace {
static_assert(sizeof(void*) == 4, "GRID Autosport uses the 32-bit Oculus ABI");
struct Pair { int x, y; };
struct Viewport { Pair position, size; };
struct Vec { float x, y, z; };
struct Rotation { float x, y, z, w; };
struct Pose { Rotation q; Vec position; };
struct Fov { float up, down, left, right; };
struct Matrix { float m[4][4]; }; // LibOVR matrices use rows and multiply column vectors.
struct EyeDesc {
    int eye;
    Fov fov;
    Viewport viewport;
    float pixelScale[2];
    Vec viewAdjust;
};
struct Hmd {
    void* sdkPrivate;
    int type;
    const char* product;
    const char* maker;
    unsigned char deviceInfo[0x3c];
    Fov defaultFov[2], maxFov[2];
    int order[2];
    Pair resolution, desktop;
    const char* displayName;
    int displayId;
};
struct Config {
    int api;
    Pair window;
    int samples;
    ID3D11Device* device;
    ID3D11DeviceContext* context;
    ID3D11RenderTargetView* rtv;
    IDXGISwapChain* chain;
};
struct EyeTexture {
    int api;
    Pair size;
    Viewport viewport;
    ID3D11Texture2D* texture;
    ID3D11ShaderResourceView* srv;
    UINT spare[6];
};
struct Timing {
    float delta;
    UINT pad;
    double start, timewarp, next, middle, scan[2];
};
static_assert(sizeof(Pose) == 28 && sizeof(EyeDesc) == 56 && sizeof(Timing) == 56);
static_assert(offsetof(Hmd, defaultFov) == 0x4c && offsetof(Hmd, order) == 0x8c &&
              offsetof(Hmd, resolution) == 0x94 && offsetof(Config, device) == 0x10);
static_assert(sizeof(EyeTexture) == 60 && offsetof(EyeTexture, texture) == 0x1c);

Hmd hmd{};
Config config{};
std::atomic<HWND> gameWindow{};
std::atomic<bool> benchmarkClosePosted{};
EyeDesc* descriptors = nullptr; // Owned by the game's current renderer.
Pose poses[2]{};
uint64_t frame = 0;
unsigned requestedEyes = 0;
bool frameOpen = false, recenter = true;
Rotation originYaw{0, 0, 0, 1}; // Seat reference: heading only, or the whole head pose without Keep horizon level.
std::atomic<bool> recenterKeyPending{};
Vec originPosition{};
double previousBegin = 0;
using NativeEyeDesc = EyeDesc* (__thiscall*)(void*, int);
using NativeOrtho = Matrix* (__cdecl*)(Matrix*, Matrix, float, float, float, float);
using NativeCameraUpdate = void (__thiscall*)(void*);
NativeEyeDesc stockEyeDesc = nullptr;
NativeOrtho stockOrtho = nullptr;
NativeCameraUpdate stockCameraUpdate = nullptr;
const void* osdLayoutReturn = nullptr;
const BYTE* gameImage = nullptr;
thread_local int projectionEye = -1;
struct HudMatrices {
    Matrix original, seated;
    uint64_t frame;
    bool ready;
};
thread_local HudMatrices hudMatrices{};

Vec sub(Vec a, Vec b) { return {a.x - b.x, a.y - b.y, a.z - b.z}; }
Rotation conjugate(Rotation q) { return {-q.x, -q.y, -q.z, q.w}; }
Rotation multiply(Rotation a, Rotation b) {
    return {a.w*b.x + a.x*b.w + a.y*b.z - a.z*b.y, a.w*b.y - a.x*b.z + a.y*b.w + a.z*b.x,
            a.w*b.z + a.x*b.y - a.y*b.x + a.z*b.w, a.w*b.w - a.x*b.x - a.y*b.y - a.z*b.z};
}
Vec rotate(Rotation q, Vec p) {
    Rotation result = multiply(multiply(q, {p.x, p.y, p.z, 0}), conjugate(q));
    return {result.x, result.y, result.z};
}

// The caller builds both perspective and HUD matrices from this descriptor. Preserve the
// stock lookup verbatim and remember the eye for the subsequent, HUD-only SDK call.
EyeDesc* __fastcall selectProjectionEye(void* renderer, void*, int eye) {
    EyeDesc* desc = stockEyeDesc(renderer, eye);
    projectionEye = unsigned(eye) < 2 && descriptors && desc == descriptors + eye ? eye : -1;
    return desc;
}

bool seatedHud(Matrix& hud, const Matrix& projection, int eye) {
    const float sx = projection.m[0][0], sy = projection.m[1][1];
    const float cx = projection.m[0][2], cy = projection.m[1][2];
    const float densityX = (descriptors[0].pixelScale[0] + descriptors[1].pixelScale[0]) * .5f;
    const float densityY = (descriptors[0].pixelScale[1] + descriptors[1].pixelScale[1]) * .5f;
    if (!(sx > 0 && sy > 0 && densityX > 0 && densityY > 0) || projection.m[3][2] != -1.f ||
        projection.m[3][0] != 0 || projection.m[3][1] != 0 || projection.m[3][3] != 0) return false;
    const Pose& pose = poses[eye];
    const Rotation inverseEye = conjugate(pose.q);
    const Vec adjust = descriptors[eye].viewAdjust; // Eye-to-head offset in eye axes.
    constexpr float distance = 1.f; // GRID2VR's default seated HUD distance in metres.
    for (int c = 0; c < 4; ++c) {
        const float w = hud.m[3][c];
        // Convert the authored perspective HUD's clip coordinates to a common seat plane.
        // The SDK perspective contains the lens-center term. Unlike the SDK ortho
        // helper, it has no explicit legacy 200-unit HUD disparity term.
        float x = (hud.m[0][c]+cx*w)/sx;
        float y = (hud.m[1][c]+cy*w)/sy;
        Vec point{distance*x*descriptors[eye].pixelScale[0]/densityX,
                  distance*y*descriptors[eye].pixelScale[1]/densityY, -distance*w};
        Vec local = rotate(inverseEye, {point.x-pose.position.x*w, point.y-pose.position.y*w,
                                        point.z-pose.position.z*w});
        local = {local.x+adjust.x*w, local.y+adjust.y*w, local.z+adjust.z*w};
        hud.m[0][c] = (sx*local.x+cx*local.z)/distance;
        hud.m[1][c] = (sy*local.y+cy*local.z)/distance;
        hud.m[3][c] = -local.z/distance;
        // In the game's perspective Zclip = A*W + B. Apply its same near/far
        // mapping to the relocated W so an ordinary head turn does not clip the artwork.
        hud.m[2][c] += (projection.m[2][2]/projection.m[3][2]) * (hud.m[3][c] - w);
    }
    return true;
}

Matrix* __cdecl hudOrtho(Matrix* output, Matrix projection, float scaleX, float scaleY,
                         float distance, float viewAdjustX) {
    Matrix* hud = stockOrtho(output, projection, scaleX, scaleY, distance, viewAdjustX);
    int eye = projectionEye;
    projectionEye = -1;
    hudMatrices.ready = false;
    if (frameOpen && descriptors && unsigned(eye) < 2 && (AutosportUseHeadset() || AutosportUiComparison()) &&
        !AutosportHudFollowsView() && !AutosportFlatScreen()) {
        Matrix seated = projection;
        if (seatedHud(seated, projection, eye)) {
            // Native code transposes both SDK matrices at 0x680400. Neither global is modified.
            for (int r=0; r<4; ++r) for (int c=0; c<4; ++c) {
                hudMatrices.original.m[r][c] = projection.m[c][r];
                hudMatrices.seated.m[r][c] = seated.m[c][r];
            }
            hudMatrices.frame = frame;
            hudMatrices.ready = true;
        }
    }
    return hud;
}

void __fastcall updateUiCamera(void* camera, const void* drawCaller, const BYTE* renderer) {
    // 0x1C6478 binds osd/osd_layout to FE manager+0x164. At 0x1B7ACC its root
    // and camera are passed to the unique 0x1B7AEB draw. Other FE draws share
    // this callback and must keep their own projection, even in the same race frame.
    if (AutosportUiComparison() && !AutosportFlatScreen()) {
        static const void* observedCallers[12]{};
        static unsigned count;
        bool seen=false;
        for (unsigned i=0; i<count; ++i) if (observedCallers[i]==drawCaller) seen=true;
        if (!seen && count < 12) {
            observedCallers[count++] = drawCaller;
            AutosportLog("Native FE scene draw callerRVA=%X frame=%llu", unsigned(static_cast<const BYTE*>(drawCaller)-gameImage-5), frame);
        }
    }
    if (drawCaller == osdLayoutReturn && !AutosportFlatScreen() && !AutosportHudFollowsView()) {
        auto* bytes = static_cast<BYTE*>(camera);
        // The native draw saves/restores the projection only in this stereo pass.
        const bool stereo = *reinterpret_cast<const DWORD*>(renderer + 0x52e8) && renderer[0x52f1];
        const bool current = hudMatrices.ready && frameOpen && frame == hudMatrices.frame;
        const bool matches = current && !std::memcmp(bytes + 0x110, &hudMatrices.original, sizeof(Matrix));
        if (AutosportUiComparison()) {
            static unsigned observed;
            const unsigned state = (stereo ? 1u : 0u) | (bytes[0xe0] ? 2u : 0u) |
                                   (current ? 4u : 0u) | (matches ? 8u : 0u);
            if (!(observed & (1u << state))) {
                observed |= 1u << state;
                const Matrix& m = *reinterpret_cast<const Matrix*>(bytes + 0x110);
                AutosportLog("osd_layout camera frame=%llu stereo=%u perspective=%u current=%u matches=%u xy=(%.6g,%.6g) zw=(%.6g,%.6g)",
                    frame, unsigned(stereo), unsigned(bytes[0xe0]), unsigned(current), unsigned(matches),
                    m.m[0][0], m.m[1][1], m.m[2][3], m.m[3][3]);
            }
        }
        // d_osd.pssg names this root camera osd_perspective_camCamera, isPerspective=1.
        if (stereo && bytes[0xe0] == 1 && matches) {
            // The original draw saved this camera field at 0x720BB9 and restores it at
            // 0x720D34 after submitting this batch. Never alter the renderer-global matrix.
            std::memcpy(bytes + 0x110, &hudMatrices.seated, sizeof(Matrix));
            static LONG notified;
            if (InterlockedCompareExchange(&notified, 1, 0) == 0)
                AutosportLog("Seated HUD applied to osd/osd_layout draw RVA 1B7AEB");
        }
    }
    stockCameraUpdate(camera);
}

// The AVX UI draw saves EBX and uses it as the pre-alignment stack base at
// 0x720B80. ECX is the camera for the original __thiscall update. EDX is dead
// here and supplies the draw caller. EDI is the renderer used by the save/restore gate.
__declspec(naked) void uiCameraCall() {
    __asm {
        push edi
        mov edx, dword ptr [ebx+4]
        call updateUiCamera
        ret
    }
}
double clockSeconds() {
    LARGE_INTEGER ticks, frequency;
    QueryPerformanceFrequency(&frequency);
    QueryPerformanceCounter(&ticks);
    return static_cast<double>(ticks.QuadPart) / frequency.QuadPart;
}

// The main window procedure (RVA 4E0E50) sets the game window's inactive flag (+0x1D9) on
// WM_ACTIVATEAPP and clears it on WM_ACTIVATE; the startup videos stop advancing while it is
// set, and the rest of the game drops to ~18 fps. A headset player rarely has the game window
// focused, and test runs share the desktop, so bridge sessions withhold the deactivations
// (-gasvrkeepactive=0 opts out). Every activation change is logged.
WNDPROC stockWndProc = nullptr;
LRESULT CALLBACK gameWndProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    const bool keep = AutosportKeepActive();
    switch (message) {
    case WM_ACTIVATEAPP:
        AutosportLog("Window WM_ACTIVATEAPP active=%u frame=%llu%s", unsigned(wParam != 0), frame,
                     !wParam && keep ? " (withheld)" : "");
        if (!wParam && keep) return 0;
        break;
    case WM_ACTIVATE:
        AutosportLog("Window WM_ACTIVATE state=%u minimized=%u frame=%llu%s", unsigned(LOWORD(wParam)),
                     unsigned(HIWORD(wParam) != 0), frame, LOWORD(wParam) == WA_INACTIVE && keep ? " (withheld)" : "");
        if (LOWORD(wParam) == WA_INACTIVE && keep) return 0;
        break;
    case WM_KILLFOCUS:
        if (keep) return 0; // Keyboard focus moves regardless; the game is not told.
        break;
    case WM_KEYDOWN: case WM_SYSKEYDOWN: case WM_KEYUP: case WM_SYSKEYUP: {
        // The launcher's recenter shortcut, matched with exactly its modifiers. It is consumed so
        // the game and Windows do not also act on it (F10 would open the system menu).
        unsigned key{}, modifiers{};
        if (!AutosportUseHeadset() || !AutosportRecenterShortcut(key, modifiers) || wParam != key) break;
        // Physical modifier state: the window thread's GetKeyState snapshot goes stale when the
        // game loses focus mid-keystroke (e.g. Alt+Tab leaves Alt "held"). Alt also sets bit 29.
        auto down = [](int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; };
        const unsigned held = (down(VK_CONTROL) ? 1u : 0u) | (down(VK_MENU) || (lParam & (1L << 29)) ? 2u : 0u) | (down(VK_SHIFT) ? 4u : 0u);
        if (held != modifiers) {
            if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)
                AutosportLog("Recenter key ignored: modifiers held=%u expected=%u frame=%llu", held, modifiers, frame);
            break;
        }
        const bool pressed = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
        if (pressed && !(lParam & (1L << 30))) {
            recenterKeyPending = true;
            AutosportLog("Recenter shortcut pressed key=%u modifiers=%u frame=%llu", key, modifiers, frame);
        }
        return 0;
    }
    case WM_SIZE:
        if (wParam == SIZE_MINIMIZED || wParam == SIZE_RESTORED)
            AutosportLog("Window WM_SIZE %s frame=%llu", wParam == SIZE_MINIMIZED ? "minimized" : "restored", frame);
        break;
    }
    return CallWindowProcW(stockWndProc, window, message, wParam, lParam);
}
void watchGameWindow(HWND window) {
    if (stockWndProc || !window || !IsWindowUnicode(window)) return;
    DWORD pid{};
    if (!GetWindowThreadProcessId(window, &pid) || pid != GetCurrentProcessId()) return;
    stockWndProc = reinterpret_cast<WNDPROC>(SetWindowLongW(window, GWL_WNDPROC, reinterpret_cast<LONG>(&gameWndProc)));
    const HWND foreground = GetForegroundWindow();
    AutosportLog("Window procedure watched=%d keepActive=%d foreground=%d active=%d",
                 stockWndProc != nullptr, AutosportKeepActive(), foreground == window, GetActiveWindow() == window);
}

// Shortly after the title scene appears (native frame ~2300 on xr-sim), the game stops calling
// the HMD frame path and draws the menus with its own swap-chain Present, uncapped (~1200/s).
// While that lasts, each game Present is shown on the seated screen (showMenuPresent), which
// also paces the menu to the headset. presentMirror's own Present is excluded.
using PresentFn = HRESULT (STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
PresentFn stockPresent = nullptr;
thread_local bool mirrorPresenting = false;
uint64_t hmdBegins = 0; // Native BeginFrame calls; unchanged between two Presents = HMD path idle.
bool showMenuPresent(IDXGISwapChain* chain);
HRESULT STDMETHODCALLTYPE watchedPresent(IDXGISwapChain* chain, UINT sync, UINT flags) {
    if (!mirrorPresenting) {
        static ULONGLONG since;
        static unsigned count, transitions;
        static uint64_t startBegins = UINT64_MAX;
        const ULONGLONG now = GetTickCount64();
        if (startBegins != hmdBegins) {
            // Capped, in case a mode interleaves HMD frames and presents every frame.
            if (++transitions <= 40) {
                if (startBegins != UINT64_MAX && count)
                    AutosportLog("Game Presents ended: %u in %llums", count, now - since);
                AutosportLog("Game Present without the HMD path from native frame=%llu sync=%u", frame, sync);
            }
            startBegins = hmdBegins; since = now; count = 0;
        }
        if (++count % 10000 == 0)
            AutosportLog("Game Presents continuing: %u in %llums nativeFrame=%llu", count, now - since, frame);
        showMenuPresent(chain);
    }
    return stockPresent(chain, sync, flags);
}
void watchPresent(IDXGISwapChain* chain) {
    if (stockPresent || !chain) return;
    void** table = *reinterpret_cast<void***>(chain);
    DWORD protection{};
    if (!VirtualProtect(&table[8], sizeof(void*), PAGE_EXECUTE_READWRITE, &protection)) return;
    stockPresent = reinterpret_cast<PresentFn>(table[8]);
    table[8] = reinterpret_cast<void*>(&watchedPresent);
    DWORD previous{};
    VirtualProtect(&table[8], sizeof(void*), protection, &previous);
    AutosportLog("Swap chain Present watched");
}

char __cdecl initialize() { return 1; }
Hmd* __cdecl createHmd(int index) {
    if (index != 0) return nullptr;
    hmd = {};
    hmd.type = 6; // DK2 descriptor layout consumed by this game.
    hmd.product = "GRID Autosport VR";
    hmd.maker = "GRID Autosport VR";
    hmd.displayName = "";
    hmd.resolution = {1920, 1080};
    for (int eye = 0; eye != 2; ++eye) {
        hmd.order[eye] = eye;
        hmd.maxFov[eye] = hmd.defaultFov[eye] = {1, 1, 1, 1};
        poses[eye] = {{0, 0, 0, 1}, {}};
    }
    recenter = true;
    AutosportLog("native Oculus device created (AVX)");
    return &hmd;
}

void closeFrame() {
    if (frameOpen) AutosportCancelViews(frame);
    frameOpen = false;
    requestedEyes = 0;
}
void __cdecl destroyHmd(Hmd* target) {
    if (target != &hmd) return;
    closeFrame();
    AutosportCloseBridge();
    config = {};
    descriptors = nullptr;
    previousBegin = 0;
}
void __cdecl shutdownLegacySdk() {}
char __cdecl attachWindow(Hmd* target, HWND, const void*, const void*) {
    return target == &hmd;
}
char __cdecl dismissHealthWarning(Hmd*) { return 0; }
void __cdecl setCaps(Hmd*, unsigned) {}
char __cdecl configureTracking(Hmd* target, unsigned, unsigned) { return target == &hmd; }
void __cdecl recenterTracking(Hmd*) {
    recenter = true;
    AutosportRecenterSeatedScreen();
    AutosportLog("Native recenter requested frame=%llu", frame);
}

// This SDK returns an 8-byte size in EDX:EAX. The game scales it relative to its display mode.
uint64_t __cdecl textureSize(Hmd* target, int eye, Fov, float density) {
    if (target != &hmd || unsigned(eye) > 1 || !(density > 0 && density <= 4)) return 0;
    UINT width = UINT((hmd.resolution.x / 2) * density);
    UINT height = UINT(hmd.resolution.y * density);
    unsigned requestedWidth{}, requestedHeight{};
    if (AutosportEyeSizeRequest(requestedWidth, requestedHeight)) { width = UINT(requestedWidth * density); height = UINT(requestedHeight * density); }
    AutosportLog("Native texture size request eye=%d density=%.3f -> %ux%u", eye, density, width, height);
    return uint64_t(width) | (uint64_t(height) << 32);
}
void setOptics(int eye, Fov fov, Vec adjustment) {
    hmd.defaultFov[eye] = fov;
    if (!descriptors) return;
    auto& desc = descriptors[eye];
    desc.fov = fov;
    desc.viewAdjust = adjustment;
    desc.pixelScale[0] = desc.viewport.size.x / (fov.left + fov.right);
    desc.pixelScale[1] = desc.viewport.size.y / (fov.up + fov.down);
}
char __cdecl configureRenderer(Hmd* target, const Config* next, unsigned, const Fov* fov, EyeDesc* output) {
    if (target != &hmd || !next || !output || !fov || next->api != 5 || !next->device ||
        !next->context || next->window.x < 2 || next->window.y < 1) return 0;
    closeFrame();
    config = *next;
    DXGI_SWAP_CHAIN_DESC swap{};
    if (config.chain && SUCCEEDED(config.chain->GetDesc(&swap))) {
        gameWindow.store(swap.OutputWindow);
        watchGameWindow(swap.OutputWindow);
        watchPresent(config.chain);
    }
    descriptors = output;
    int middle = config.window.x / 2;
    for (int eye = 0; eye != 2; ++eye) {
        output[eye] = {};
        output[eye].eye = eye;
        output[eye].viewport = {{eye ? middle : 0, 0}, {eye ? config.window.x-middle : middle, config.window.y}};
        // This build's SDK calls the field ViewAdjust. It translates the view matrix,
        // so a left eye located at negative head-space X has positive adjustment.
        setOptics(eye, fov[eye], {eye == 0 ? .032f : -.032f, 0, 0});
    }
    AutosportLog("native renderer configured: %dx%d device=%p context=%p", config.window.x,
                 config.window.y, (void*)config.device, (void*)config.context);
    return 1;
}

// flat: the frame is shown on the seated screen (startup, loading), not as a stereo projection.
// The headset's lenses are off-centre (e.g. 54 degrees inward, 40 outward), which pushes the game's
// centred loading cards past the eye image's edge. A flat screen needs no lens match, so give the
// game a centred field of view with the same total width and height.
void applyViews(const BridgeView (&views)[2], bool flat) {
    Vec center{};
    for (auto& v : views) {
        center.x += .5f*v.position[0];
        center.y += .5f*v.position[1];
        center.z += .5f*v.position[2];
    }
    if (recenter) {
        // Average the two eye forward vectors so canted displays center symmetrically.
        float x = 0, z = 0;
        for (auto& v : views) {
            auto q = v.orientation;
            auto forward = rotate({q[0], q[1], q[2], q[3]}, {0, 0, -1});
            x -= forward.x;
            z -= forward.z;
        }
        float yaw = -.5f * std::atan2(x, z);
        originYaw = {0, std::sin(yaw), 0, std::cos(yaw)};
        if (!AutosportKeepHorizonLevel()) {
            // Align pitch and roll too: the reference becomes the inverse of the head pose.
            const auto& a = views[0].orientation; const auto& b = views[1].orientation;
            const float sign = a[0]*b[0] + a[1]*b[1] + a[2]*b[2] + a[3]*b[3] < 0 ? -1.f : 1.f;
            Rotation head{a[0] + sign*b[0], a[1] + sign*b[1], a[2] + sign*b[2], a[3] + sign*b[3]};
            const float length = std::sqrt(head.x*head.x + head.y*head.y + head.z*head.z + head.w*head.w);
            if (length > 1e-6f) originYaw = conjugate({head.x/length, head.y/length, head.z/length, head.w/length});
        }
        originPosition = center;
        recenter = false;
    }
    for (int eye = 0; eye != 2; ++eye) {
        auto& v = views[eye];
        Rotation q{v.orientation[0], v.orientation[1], v.orientation[2], v.orientation[3]};
        Vec pos{v.position[0], v.position[1], v.position[2]};
        poses[eye] = {multiply(originYaw, q), rotate(originYaw, sub(center, originPosition))};
        Fov fov{std::tan(v.fov[2]), -std::tan(v.fov[3]), -std::tan(v.fov[0]), std::tan(v.fov[1])};
        if (flat) {
            const float horizontal = .5f * (fov.left + fov.right), vertical = .5f * (fov.up + fov.down);
            fov = {vertical, vertical, horizontal, horizontal};
        }
        setOptics(eye, fov, rotate(conjugate(q), sub(center, pos)));
    }
}

// Nontrivial legacy structure returns carry an explicit output pointer before the HMD.
Timing* __cdecl beginFrame(Timing* timing, Hmd* target, unsigned) {
    *timing = {};
    if (target != &hmd || !descriptors) return timing;
    closeFrame();
    ++hmdBegins;
    ++frame; // GRID passes frameIndex=0 on every call. This serial identifies one native begin/end pair.
    AutosportBeginLoadingOverlayFrame(frame, !AutosportFlatScreen());
    BridgeView views[2]{};
    frameOpen = AutosportBeginViews(config.device, config.context, AutosportUseHeadset(), frame, views);
    if (frameOpen) {
        if (AutosportTakeRecenterRequest()) recenter = true;
        if (recenterKeyPending.exchange(false)) {
            recenter = true;
            AutosportRecenterSeatedScreen();
            AutosportLog("Recentered by the shortcut at frame=%llu keepHorizonLevel=%d", frame, AutosportKeepHorizonLevel());
        }
        applyViews(views, AutosportFlatScreen());
        if (frame == 1) AutosportLog("Native BeginFrame: %s", AutosportBridgeStatus());
    }
    else if (frame == 1 || frame % 300 == 0) AutosportLog("begin frame %llu: %s", frame, AutosportBridgeStatus());
    double now = clockSeconds();
    double dt = now - previousBegin;
    if (!(dt >= .001 && dt <= .1)) dt = 1.0 / 75.0;
    const double predictedPeriod = AutosportDisplayPeriodSeconds();
    if (frameOpen && predictedPeriod > 0) dt = predictedPeriod;
    previousBegin = now;
    timing->delta = static_cast<float>(dt);
    timing->start = now;
    timing->next = now + dt;
    timing->timewarp = timing->middle = timing->scan[0] = timing->scan[1] = now + dt * .5;
    return timing;
}
Pose* __cdecl eyePose(Pose* output, Hmd* target, int eye) {
    *output = {{0, 0, 0, 1}, {}};
    if (target == &hmd && unsigned(eye) < 2) {
        *output = poses[eye];
        if (frameOpen) requestedEyes |= (1u << eye);
    }
    return output;
}

DXGI_FORMAT colorFamily(DXGI_FORMAT format) {
    switch (format) {
    case DXGI_FORMAT_R8G8B8A8_TYPELESS:
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: return DXGI_FORMAT_R8G8B8A8_UNORM;
    case DXGI_FORMAT_B8G8R8A8_TYPELESS:
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: return DXGI_FORMAT_B8G8R8A8_UNORM;
    default: return format;
    }
}

void presentMirror(ID3D11Texture2D* packed) {
    if (!config.chain) return;
    ID3D11Texture2D* back = nullptr;
    if (packed && SUCCEEDED(config.chain->GetBuffer(0, IID_PPV_ARGS(&back)))) {
        if (back != packed) {
            D3D11_TEXTURE2D_DESC src{}, dst{};
            packed->GetDesc(&src);
            back->GetDesc(&dst);
            if (src.Width == dst.Width && src.Height == dst.Height &&
                colorFamily(src.Format) == colorFamily(dst.Format)) {
                if (src.SampleDesc.Count == dst.SampleDesc.Count && src.SampleDesc.Quality == dst.SampleDesc.Quality)
                    config.context->CopySubresourceRegion(back, 0, 0, 0, 0, packed, 0, nullptr);
                else if (src.SampleDesc.Count > 1 && dst.SampleDesc.Count == 1)
                    config.context->ResolveSubresource(back, 0, packed, 0, colorFamily(dst.Format));
            }
        }
        back->Release();
    }
    // The native Oculus submit bypasses the ordinary game swap-chain Present.
    mirrorPresenting = true;
    config.chain->Present(0, 0);
    mirrorPresenting = false;
}

// A game Present while the HMD path is idle carries a finished flat frame (the menus). End
// the HMD frame the game abandoned, then show the back buffer on the seated screen. The
// bridge's xrWaitFrame paces these presents to the headset.
bool showMenuPresent(IDXGISwapChain* chain) {
    static uint64_t seenBegins = UINT64_MAX;
    static bool showing;
    if (!AutosportUseHeadset() || !descriptors || chain != config.chain) return false;
    const bool hmdIdle = seenBegins == hmdBegins;
    seenBegins = hmdBegins;
    if (!hmdIdle) {
        if (showing) AutosportLog("Menu presents on the seated screen ended at native frame=%llu", frame);
        showing = false;
        return false;
    }
    if (frameOpen) closeFrame();
    ID3D11Texture2D* back = nullptr;
    if (FAILED(chain->GetBuffer(0, IID_PPV_ARGS(&back))) || !back) return false;
    D3D11_TEXTURE2D_DESC desc{};
    back->GetDesc(&desc);
    BridgeView views[2]{};
    BridgeEndResult result{};
    ++frame;
    if (AutosportBeginViews(config.device, config.context, true, frame, views)) {
        const RECT full{0, 0, LONG(desc.Width), LONG(desc.Height)};
        const RECT rects[2]{full, full};
        result = AutosportEndViews(back, 0, rects, frame, nullptr, BridgePresentation::FlatScreen);
    }
    back->Release();
    static unsigned reported;
    if (!showing || (!result.submitted && reported < 20)) {
        if (!result.submitted) ++reported;
        AutosportLog("Menu present native frame=%llu back=%ux%u format=%u submitted=%u status=%s", frame,
                     desc.Width, desc.Height, unsigned(desc.Format), unsigned(result.submitted), AutosportBridgeStatus());
    }
    showing = true;
    return result.submitted;
}
bool startupFromBack = false;
void __cdecl endFrame(Hmd* target, const Pose*, const EyeTexture* eyes) {
    if (target != &hmd) return;
    if (frameOpen && eyes && eyes[0].api == 5 && eyes[1].api == 5 && eyes[0].texture &&
        eyes[0].texture == eyes[1].texture) {
        RECT rects[2]{};
        for (int i = 0; i < 2; ++i) {
            const auto& v = eyes[i].viewport;
            rects[i] = {v.position.x, v.position.y, v.position.x + v.size.x, v.position.y + v.size.y};
        }
        const char* overlay = AutosportLoadingOverlaySample(frame);
        bool capture = !overlay && requestedEyes == 3 && AutosportCaptureRequested();
        const char* transition = (overlay || capture) ? nullptr : AutosportTransitionSample();
        const auto mode = AutosportFlatScreen() ? BridgePresentation::FlatScreen : BridgePresentation::Stereo;
        startupFromBack = AutosportStartupFromBackBuffer();
        static int lastMode = -1;
        if (lastMode != static_cast<int>(mode)) {
            lastMode = static_cast<int>(mode);
            AutosportLog("Native frame %llu presentation=%s HUD follows view=%d", frame,
                         mode == BridgePresentation::FlatScreen ? "seated-screen" : "race-stereo", AutosportHudFollowsView());
        }
        // The startup videos never reach the eye texture (a flat dark fill). Show the game's
        // back buffer on the seated screen until the first loading state instead.
        ID3D11Texture2D* back = nullptr;
        if (startupFromBack && mode == BridgePresentation::FlatScreen && config.chain &&
            FAILED(config.chain->GetBuffer(0, IID_PPV_ARGS(&back)))) back = nullptr;
        BridgeEndResult result{};
        if (back && back != eyes[0].texture) {
            D3D11_TEXTURE2D_DESC desc{};
            back->GetDesc(&desc);
            const RECT full{0, 0, LONG(desc.Width), LONG(desc.Height)};
            const RECT whole[2]{full, full};
            result = AutosportEndViews(back, 0, whole, frame, nullptr, mode);
            static bool noted;
            if (!noted) {
                noted = true;
                AutosportLog("Startup seated screen from the back buffer %ux%u format=%u frame=%llu submitted=%u",
                             desc.Width, desc.Height, unsigned(desc.Format), frame, unsigned(result.submitted));
            }
        } else {
            result = AutosportEndViews(eyes[0].texture, 0, rects, frame,
                                       (capture || transition || overlay) ? AutosportRunDirectory() : nullptr, mode);
        }
        if (back) back->Release();
        AutosportFinishLoadingOverlayFrame(frame, result.captured);
        {
            // One line per eye-target size: the size the game actually renders each eye at.
            static UINT loggedWidth, loggedHeight;
            D3D11_TEXTURE2D_DESC packedDesc{};
            eyes[0].texture->GetDesc(&packedDesc);
            if (packedDesc.Width != loggedWidth || packedDesc.Height != loggedHeight) {
                loggedWidth = packedDesc.Width; loggedHeight = packedDesc.Height;
                AutosportLog("Native eye target %ux%u samples=%u left=%ld,%ld,%ld,%ld right=%ld,%ld,%ld,%ld frame=%llu", packedDesc.Width, packedDesc.Height,
                             packedDesc.SampleDesc.Count, rects[0].left, rects[0].top, rects[0].right, rects[0].bottom,
                             rects[1].left, rects[1].top, rects[1].right, rects[1].bottom, frame);
            }
        }
        if (frame == 1 || frame % 1800 == 0 || result.captured)
            AutosportLog("Frame %llu pose-mask=%u packed=%p submitted=%u status=%s", frame, requestedEyes,
                         static_cast<void*>(eyes[0].texture), result.submitted, AutosportBridgeStatus());
        if (result.captured) {
            AutosportLog("native EndFrame %llu: packed=%p pose-request-mask=%u left=%ld,%ld,%ld,%ld right=%ld,%ld,%ld,%ld",
                         frame, (void*)eyes[0].texture, requestedEyes, rects[0].left, rects[0].top,
                         rects[0].right, rects[0].bottom, rects[1].left, rects[1].top, rects[1].right, rects[1].bottom);
            if (capture) AutosportCaptureCompleted();
            else if (transition) AutosportLog("UI transition sample phase=%s nativeFrame=%llu", transition, frame);
        } else if (capture && (frame < 3 || frame % 120 == 0)) {
            AutosportLog("native capture pending: %s", AutosportBridgeStatus());
        }
        frameOpen = false;
    } else {
        if (frameOpen && (frame < 3 || frame % 120 == 0)) AutosportLog("native submit lacks a common D3D11 eye texture");
        closeFrame();
    }
    // During startup the back buffer already holds the frame shown on the desktop; keep it.
    presentMirror(eyes && eyes[0].api == 5 && !startupFromBack ? eyes[0].texture : nullptr);
    startupFromBack = false;
}

struct Site { UINT call, target; void (__cdecl *replacement)(); UINT resume; };
void __cdecl prepareGameExit() {
    AutosportLog("Native terminal cleanup RVA=49E8E0 callerThread=%lu", GetCurrentThreadId());
    AutosportPrepareForGameExit();
    reinterpret_cast<void (__cdecl*)()>(gameImage + 0x49E8E0)();
}
void __cdecl finishBenchmark() {
    // The benchmark worker has already committed to ExitProcess(0). Let it
    // return to the engine so the ordinary window close can run on the owner.
    if (benchmarkClosePosted.exchange(true)) return;
    const HWND window = gameWindow.load();
    DWORD pid{};
    if (window && GetWindowThreadProcessId(window, &pid) && pid == GetCurrentProcessId() &&
        PostMessageW(window, WM_CLOSE, 0, 0)) {
        AutosportLog("Native benchmark finished; queued game window close from thread=%lu", GetCurrentThreadId());
        return;
    }
    AutosportLog("Native benchmark window unavailable; forwarding original terminal exit");
    AutosportPrepareForGameExit();
    reinterpret_cast<void (__cdecl*)()>(gameImage + 0x4EE040)();
    ExitProcess(0);
}
// Race start camera. When the race scene begins, RVA 2D4AE5 looks up the player's view by
// name on the vehicle view manager (RVA 830300: __thiscall, djb2 of the name against each
// view's hash at +0xD0, index or -1) and selects it: the saved current_camera, or chase_close
// in benchmark and attract sessions (Practice / Race). With -gasvrcockpit=1 a headset Practice
// / Race session gets the car's head-cam (the driver cockpit, defined for every shipped car)
// instead, or the game's choice for a car without one. It runs on the game's own thread; the
// camera button still cycles views afterwards.
using ViewLookupFn = int (__thiscall*)(void*, const char*);
ViewLookupFn stockViewLookup = nullptr;
int __fastcall raceStartView(void* manager, void*, const char* requested) {
    if (AutosportStartInCockpit() && AutosportUseHeadset() && AutosportPracticeSession()) {
        const int cockpit = stockViewLookup(manager, "head-cam");
        if (cockpit >= 0) {
            char label[48]{};
            __try { strncpy_s(label, requested ? requested : "(none)", _TRUNCATE); }
            __except (EXCEPTION_EXECUTE_HANDLER) { strcpy_s(label, "(unreadable)"); }
            AutosportLog("Race start camera head-cam (view %d) instead of %s at frame=%llu", cockpit, label, frame);
            return cockpit;
        }
        AutosportLog("Race start camera: this car has no head-cam view; keeping the game's choice");
    }
    return stockViewLookup(manager, requested);
}

template<class F> Site site(UINT call, UINT target, F callback, UINT resume = 0) {
    return {call, target, reinterpret_cast<void (__cdecl*)()>(callback), resume};
}
} // namespace

bool AutosportInstallNativeOvr() noexcept {
    auto* base = reinterpret_cast<BYTE*>(GetModuleHandleW(nullptr));
    if (!base) return false;
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return false;
    const auto* pe = reinterpret_cast<const IMAGE_NT_HEADERS32*>(base + dos->e_lfanew);
    if (pe->Signature != IMAGE_NT_SIGNATURE || pe->FileHeader.Machine != IMAGE_FILE_MACHINE_I386 ||
        pe->FileHeader.TimeDateStamp != 0x5c8007d1 || pe->OptionalHeader.SizeOfImage != 0x1083000) return false;
    // All HMD-dependent game→SDK calls in GRIDAutosport_avx.exe, plus SDK initialization and shutdown.
    // The game retains its shared perspective and ortho projections. Only the named OSD
    // camera temporarily receives the seated projection during its stereo draw.
    const BYTE drawFrame[] = {0x53,0x8b,0xdc,0x83,0xec,0x08,0x83,0xe4,0xf0,0x83,0xc4,0x04,0x55,0x8b,0x6b,0x04};
    const BYTE osdCaller[] = {0x8b,0x86,0x64,0x01,0x00,0x00,0x8b,0x88,0x98,0x00,0x00,0x00,
        0x8b,0x90,0x94,0x00,0x00,0x00,0x8b,0x86,0xbc,0x00,0x00,0x00,0x6a,0x10,
        0x6a,0x00,0x51,0x52,0x50,0xe8,0x90,0x90,0x56,0x00};
    if (std::memcmp(base + 0x720B80, drawFrame, sizeof(drawFrame)) ||
        std::memcmp(base + 0x1B7ACC, osdCaller, sizeof(osdCaller))) return false;
    const Site sites[] = {
        site(0x2B29A3, 0x49E8E0, prepareGameExit),
        site(0x685011, 0x4EE040, finishBenchmark, 0x68506B),
        site(0x6851F4, 0x4EE040, finishBenchmark, 0x68505C),
        site(0x1B7B63, 0x720B80, AutosportDrawFrontendOverlay),
        site(0x1D5221, 0x9FFBC0, AutosportSlateVisibility), site(0x1D5482, 0x9FFBC0, AutosportSlateVisibility),
        site(0x243F26, 0x9F6C80, AutosportLoadingEntered), site(0x243FC9, 0x9F6C80, AutosportLoadingExited),
        site(0x4DBFCB, 0xA9E420, endFrame), site(0x4DC25D, 0xA9E3B0, beginFrame),
        site(0x4DEF77, 0xA9E240, destroyHmd), site(0x4DEF7F, 0xA9DE10, shutdownLegacySdk),
        site(0x4DFF38, 0xA9EB40, initialize), site(0x4DFF3F, 0xA9E750, createHmd),
        site(0x4E06BB, 0xA9E1F0, attachWindow), site(0x4E119A, 0xA9E690, dismissHealthWarning),
        site(0x59E7F6, 0xA9E350, textureSize), site(0x59E894, 0xA9E850, eyePose),
        site(0x59E92B, 0xA9DEA0, recenterTracking), site(0x5C52EE, 0xA9DED0, configureRenderer),
        site(0x5C5306, 0xA9DE40, setCaps), site(0x5C5316, 0xA9DE60, configureTracking),
        site(0x680311, 0x678000, selectProjectionEye), site(0x6803E9, 0xA9E6D0, hudOrtho),
        site(0x720C4A, 0x11D4D0, uiCameraCall), site(0x2D4AE5, 0x830300, raceStartView)
    };
    // Original worker return: clear both active flags, pop EBX (only on the
    // results path), then the common pop ESI / pop EBP / ret 4.
    const BYTE workerReturn[] = {0xc6,0x86,0x8a,0,0,0,0,0xc6,0x86,0x94,0,0,0,0,0x5b,0x5e,0x5d,0xc2,4,0};
    if (std::memcmp(base + 0x68505C, workerReturn, sizeof(workerReturn))) return false;
    for (auto& s : sites) {
        if (s.resume && AutosportRegularPlay()) continue;
        UINT delta;
        std::memcpy(&delta, base + s.call + 1, sizeof(delta));
        if (base[s.call] != 0xE8 || s.call + 5 + delta != s.target) return false;
        if (s.resume) {
            UINT slot;
            std::memcpy(&slot, base + s.call + 9, sizeof(slot));
            if (std::memcmp(base + s.call + 5, "\x6a\x00\xff\x15", 4) ||
                slot != reinterpret_cast<UINT>(base + 0xBC20F4)) return false;
        }
    }
    struct Page { BYTE* address; DWORD original; };
    Page pages[sizeof(sites)/sizeof(sites[0])]{};
    size_t count = 0;
    for (auto& s : sites) {
        // Each verified five-byte instruction lies wholly inside a single 4 KiB page.
        auto* page = base + (s.call & ~UINT(0xfff));
        if (count && pages[count-1].address == page) continue;
        DWORD original;
        if (!VirtualProtect(page, 4096, PAGE_EXECUTE_READWRITE, &original)) {
            for (size_t i = 0; i < count; ++i) {
                DWORD old;
                VirtualProtect(pages[i].address, 4096, pages[i].original, &old);
            }
            return false;
        }
        pages[count++] = {page, original};
    }
    stockEyeDesc = reinterpret_cast<NativeEyeDesc>(base + 0x678000);
    stockOrtho = reinterpret_cast<NativeOrtho>(base + 0xA9E6D0);
    stockCameraUpdate = reinterpret_cast<NativeCameraUpdate>(base + 0x11D4D0);
    AutosportBindLoadingOverlay(base);
    gameImage = base;
    osdLayoutReturn = base + 0x1B7AF0;
    stockViewLookup = reinterpret_cast<ViewLookupFn>(base + 0x830300);
    for (auto& s : sites) {
        if (s.resume && AutosportRegularPlay()) continue;
        UINT delta = reinterpret_cast<UINT>(s.replacement) - reinterpret_cast<UINT>(base + s.call + 5);
        std::memcpy(base + s.call + 1, &delta, sizeof(delta));
        if (s.resume) {
            BYTE* tail = base + s.call + 5;
            tail[0] = 0xe9;
            UINT jump = s.resume - (s.call + 10);
            std::memcpy(tail + 1, &jump, sizeof(jump));
            std::memset(tail + 5, 0x90, 3);
        }
    }
    bool ok = true;
    for (size_t i = 0; i < count; ++i) {
        DWORD old;
        if (!VirtualProtect(pages[i].address, 4096, pages[i].original, &old)) ok = false;
        if (!FlushInstructionCache(GetCurrentProcess(), pages[i].address, 4096)) ok = false;
    }
    return ok;
}
