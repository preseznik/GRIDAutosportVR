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
Rotation originYaw{0, 0, 0, 1};
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
void __cdecl recenterTracking(Hmd*) { recenter = true; }

// This SDK returns an 8-byte size in EDX:EAX. The game scales it relative to its display mode.
uint64_t __cdecl textureSize(Hmd* target, int eye, Fov, float density) {
    if (target != &hmd || unsigned(eye) > 1 || !(density > 0 && density <= 4)) return 0;
    const UINT width = UINT((hmd.resolution.x / 2) * density);
    const UINT height = UINT(hmd.resolution.y * density);
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
    if (config.chain && SUCCEEDED(config.chain->GetDesc(&swap)))
        gameWindow.store(swap.OutputWindow);
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

void applyViews(const BridgeView (&views)[2]) {
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
        originPosition = center;
        recenter = false;
    }
    for (int eye = 0; eye != 2; ++eye) {
        auto& v = views[eye];
        Rotation q{v.orientation[0], v.orientation[1], v.orientation[2], v.orientation[3]};
        Vec pos{v.position[0], v.position[1], v.position[2]};
        poses[eye] = {multiply(originYaw, q), rotate(originYaw, sub(center, originPosition))};
        setOptics(eye, {std::tan(v.fov[2]), -std::tan(v.fov[3]), -std::tan(v.fov[0]), std::tan(v.fov[1])},
                  rotate(conjugate(q), sub(center, pos)));
    }
}

// Nontrivial legacy structure returns carry an explicit output pointer before the HMD.
Timing* __cdecl beginFrame(Timing* timing, Hmd* target, unsigned) {
    *timing = {};
    if (target != &hmd || !descriptors) return timing;
    closeFrame();
    ++frame; // GRID passes frameIndex=0 on every call. This serial identifies one native begin/end pair.
    AutosportBeginLoadingOverlayFrame(frame, !AutosportFlatScreen());
    BridgeView views[2]{};
    frameOpen = AutosportBeginViews(config.device, config.context, AutosportUseHeadset(), frame, views);
    if (frameOpen) {
        if (AutosportTakeRecenterRequest()) recenter = true;
        applyViews(views);
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
    config.chain->Present(0, 0);
}

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
        static int lastMode = -1;
        if (lastMode != static_cast<int>(mode)) {
            lastMode = static_cast<int>(mode);
            AutosportLog("Native frame %llu presentation=%s HUD follows view=%d", frame,
                         mode == BridgePresentation::FlatScreen ? "seated-screen" : "race-stereo", AutosportHudFollowsView());
        }
        auto result = AutosportEndViews(eyes[0].texture, 0, rects, frame,
                                        (capture || transition || overlay) ? AutosportRunDirectory() : nullptr, mode);
        AutosportFinishLoadingOverlayFrame(frame, result.captured);
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
    presentMirror(eyes && eyes[0].api == 5 ? eyes[0].texture : nullptr);
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
        site(0x720C4A, 0x11D4D0, uiCameraCall)
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
