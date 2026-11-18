#include "xr_bridge.h"
#include "bridge_api.h"
#include "eye_dump.h"
#include "performance_display.h"
#include <wrl/client.h>
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <dxgi1_2.h>
#include <d3dcompiler.h>
#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstdlib>
#include <cstdio>
#include <share.h>
#include <cstring>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <vector>

using Microsoft::WRL::ComPtr;
namespace {
thread_local char status[256] = "idle";
void report(const char* message) { snprintf(status, sizeof(status), "%s", message); }
const char* stateName(XrSessionState state) {
    constexpr const char* names[] = {"UNKNOWN", "IDLE", "READY", "SYNCHRONIZED", "VISIBLE",
                                     "FOCUSED", "STOPPING", "LOSS_PENDING", "EXITING"};
    return unsigned(state) < ARRAYSIZE(names) ? names[unsigned(state)] : "OTHER";
}
// Whether a window of this process is the Windows foreground window. Logged with
// runtime visibility, which some runtimes tie to the focused desktop application.
bool gameForeground() {
    DWORD pid{};
    const HWND window = GetForegroundWindow();
    return window && GetWindowThreadProcessId(window, &pid) && pid == GetCurrentProcessId();
}
void require(bool condition, const char* what) { if (!condition) throw std::runtime_error(what); }
void checked(XrResult result, const char* what) {
    if (result == XR_SUCCESS) return;
    char error[128];
    snprintf(error, sizeof(error), "%s(XrResult=%d)", what, result);
    throw std::runtime_error(error);
}
void checked(HRESULT result, const char* what) {
    if (SUCCEEDED(result)) return;
    char error[128];
    snprintf(error, sizeof(error), "%s(HRESULT=0x%08lx)", what, result);
    throw std::runtime_error(error);
}
DXGI_FORMAT typedFormat(DXGI_FORMAT f) {
    switch (f) {
    case DXGI_FORMAT_R8G8B8A8_TYPELESS: return DXGI_FORMAT_R8G8B8A8_UNORM;
    case DXGI_FORMAT_B8G8R8A8_TYPELESS: return DXGI_FORMAT_B8G8R8A8_UNORM;
    case DXGI_FORMAT_B8G8R8X8_TYPELESS: return DXGI_FORMAT_B8G8R8X8_UNORM;
    case DXGI_FORMAT_R10G10B10A2_TYPELESS: return DXGI_FORMAT_R10G10B10A2_UNORM;
    case DXGI_FORMAT_R16G16B16A16_TYPELESS: return DXGI_FORMAT_R16G16B16A16_FLOAT;
    case DXGI_FORMAT_R8G8B8A8_UNORM: case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
    case DXGI_FORMAT_B8G8R8A8_UNORM: case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
    case DXGI_FORMAT_B8G8R8X8_UNORM: case DXGI_FORMAT_B8G8R8X8_UNORM_SRGB:
    case DXGI_FORMAT_R10G10B10A2_UNORM: case DXGI_FORMAT_R16G16B16A16_FLOAT:
    case DXGI_FORMAT_R11G11B10_FLOAT: return f;
    default: throw std::runtime_error("unsupported_packed_color_format");
    }
}

// The sRGB format with the same bytes, or UNKNOWN when there is none.
DXGI_FORMAT srgbVariant(DXGI_FORMAT f) {
    switch (f) {
    case DXGI_FORMAT_R8G8B8A8_UNORM: return DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
    case DXGI_FORMAT_B8G8R8A8_UNORM: return DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
    case DXGI_FORMAT_B8G8R8X8_UNORM: return DXGI_FORMAT_B8G8R8X8_UNORM_SRGB;
    default: return DXGI_FORMAT_UNKNOWN;
    }
}
bool isSrgb(DXGI_FORMAT f) {
    return f == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB || f == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB || f == DXGI_FORMAT_B8G8R8X8_UNORM_SRGB;
}

struct Chain {
    XrSwapchain handle{};
    unsigned width{}, height{};
    DXGI_FORMAT format{};
    std::vector<XrSwapchainImageD3D11KHR> images;
};
// How the game's picture reaches an eye swapchain (Bridge::submit).
struct EyeFormat { DXGI_FORMAT format; bool copy, decode; };
struct PredicateScope {
    ID3D11DeviceContext* ctx;
    ComPtr<ID3D11Predicate> old;
    BOOL value{};
    explicit PredicateScope(ID3D11DeviceContext* c) : ctx(c) {
        ctx->GetPredication(&old, &value);
        if (old) ctx->SetPredication(nullptr, FALSE);
    }
    ~PredicateScope() { if (old) ctx->SetPredication(old.Get(), value); }
};
// The seated screen: AutosportScreenDistance() forward in LOCAL (GRID2VR's is two metres; the
// launcher's default is three), level with the head at placement, fixed in place until the next
// recenter. Projecting the forward axis removes pitch and roll.
bool levelAnchor(const XrPosef& head, XrPosef& anchor, double distance = AutosportScreenDistance(), float drop = 0) {
    const auto& p = head.position;
    const auto& q = head.orientation;
    const double norm = q.x*q.x + q.y*q.y + q.z*q.z + q.w*q.w;
    if (!std::isfinite(norm) || norm < .99 || norm > 1.01 ||
        !std::isfinite(p.x) || !std::isfinite(p.y) || !std::isfinite(p.z)) return false;
    double x = -2.*(q.x*q.z + q.w*q.y)/norm, z = -1.+2.*(q.x*q.x + q.y*q.y)/norm;
    double length = std::hypot(x,z);
    if (length < 1e-6) { // vertical gaze: the horizontal right axis still has a heading
        x = 2.*(q.x*q.z - q.w*q.y)/norm;
        z = -1.+2.*(q.y*q.y + q.z*q.z)/norm;
        length = std::hypot(x,z);
    }
    if (length < 1e-6) return false;
    x /= length; z /= length;
    const double yaw = std::atan2(-x,-z);
    anchor = {{0,float(std::sin(.5*yaw)),0,float(std::cos(.5*yaw))},
              {p.x+float(distance*x),p.y-drop,p.z+float(distance*z)}};
    return true;
}
// performance-display.bmp (-gasvrperfdump=1): 32-bit, bottom-up, from RGBA pixels.
void writePanelPicture(const std::vector<uint32_t>& rgba) {
    const wchar_t* folder = AutosportRunDirectory();
    if (!folder || !*folder) return;
    wchar_t path[MAX_PATH];
    if (swprintf_s(path, L"%s\\performance-display.bmp", folder) < 0) return;
    constexpr unsigned w = PerformanceDisplay::Width, h = PerformanceDisplay::Height;
    BITMAPFILEHEADER file{0x4d42, DWORD(sizeof(BITMAPFILEHEADER) + sizeof(BITMAPINFOHEADER) + w * h * 4), 0, 0,
                          DWORD(sizeof(BITMAPFILEHEADER) + sizeof(BITMAPINFOHEADER))};
    BITMAPINFOHEADER info{sizeof(BITMAPINFOHEADER), LONG(w), LONG(h), 1, 32, BI_RGB};
    FILE* out{};
    if (_wfopen_s(&out, path, L"wb") != 0 || !out) return;
    fwrite(&file, sizeof(file), 1, out);
    fwrite(&info, sizeof(info), 1, out);
    for (unsigned y = h; y-- > 0;)
        for (unsigned x = 0; x < w; ++x) {
            const uint32_t p = rgba[size_t(y) * w + x];
            const uint32_t bgra = (p & 0xff00ff00) | ((p & 0xff) << 16) | ((p >> 16) & 0xff);
            fwrite(&bgra, 4, 1, out);
        }
    fclose(out);
}
// Graphics → Last VR session in the launcher: the frame rate while the race is shown in stereo,
// in one-second windows. A window holds only stereo frames without a gap over 250 ms, so menus,
// the seated screen, pauses and loading end it. Written to performance.json in the session
// folder when the bridge closes.
struct FrameRate {
    using clock = std::chrono::steady_clock;
    clock::time_point start{}, last{};
    unsigned frames{};
    std::vector<float> windows;
    void stereo() {
        const auto now = clock::now();
        if (frames == 0 || now - last > std::chrono::milliseconds(250)) { start = now; frames = 0; }
        last = now; ++frames;
        const double seconds = std::chrono::duration<double>(now - start).count();
        if (seconds >= 1.0) {
            if (windows.size() < 36000) windows.push_back(float((frames - 1) / seconds));
            start = now; frames = 1;
        }
    }
    void interrupt() { frames = 0; }
    void write(double displayHz) const {
        const wchar_t* folder = AutosportRunDirectory();
        if (!folder || !*folder) return;
        wchar_t path[MAX_PATH];
        if (swprintf_s(path, L"%s\\performance.json", folder) < 0) return;
        std::vector<float> sorted(windows);
        std::sort(sorted.begin(), sorted.end());
        auto at = [&](double share) { return sorted.empty() ? 0.0 : double(sorted[size_t(share * (sorted.size() - 1))]); };
        const auto dips = displayHz > 0 ? std::count_if(sorted.begin(), sorted.end(), [&](float fps) { return fps < .95 * displayHz; }) : 0;
        FILE* file{};
        if (_wfopen_s(&file, path, L"wb") != 0 || !file) return;
        fprintf(file, "{\"schema\":1,\"displayHz\":%.2f,\"windows\":%zu,\"fps\":{\"p50\":%.1f,\"p5\":%.1f,\"min\":%.1f},\"dips\":{\"share\":%.3f}}\n",
                displayHz, sorted.size(), at(.5), at(.05), at(0), sorted.empty() ? 0.0 : double(dips) / sorted.size());
        fclose(file);
        AutosportLog("Frame rate windows=%zu p50=%.1f p5=%.1f display_hz=%.2f", sorted.size(), at(.5), at(.05), displayHz);
    }
};
struct Bridge {
    DWORD owner{GetCurrentThreadId()};
    FrameRate frameRate;
    // Settings -> Diagnostics (performance_display.h): frames sent each second, GPU usage, and the
    // in-VR panel. Started with the first submitted frame when either option is on.
    SubmittedFrameRate sent;
    GpuUsageSampler gpu;
    PerformanceDisplay meter;
    bool metersStarted{};
    double displayHz() const { return period > 0 ? 1e9 / double(period) : 0.0; }
    void startMeters();
    const XrCompositionLayerBaseHeader* meterLayer();
    void frameSent();
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context, deferred;
    ComPtr<ID3D11Texture2D> snapshot;
    ComPtr<ID3D11ShaderResourceView> snapshotView;
    ComPtr<ID3D11VertexShader> blitVS;
    ComPtr<ID3D11PixelShader> blitPS;
    ComPtr<ID3D11Buffer> cropBuffer;
    ComPtr<ID3D11Query> completion;
    XrInstance instance{};
    XrSession session{};
    XrSpace local{}, headSpace{};
    XrPosef screenAnchor{};
    bool screenAnchored{};
    bool headset{}, running{}, frameOpen{}, active{}, failed{}, failureLogged{}, seen{}, gpuUsed{}, closePending{};
    uint64_t serial{};
    XrSessionState state{XR_SESSION_STATE_UNKNOWN};
    // A run of native frames the runtime asked us not to render (shouldRender false or discarded).
    bool hidden{};
    // Views located before the first FOCUSED come from a session the runtime is not yet
    // presenting. Re-place the seated screen and recenter once when it is focused.
    bool locatedBeforeFocus{}, everFocused{}, recenterPending{};
    uint64_t hiddenFrom{}, hiddenFrames{};
    ULONGLONG hiddenTick{};
    XrTime time{};
    XrDuration period{}, loggedPeriod{};
    XrView located[2]{};
    BridgeView views[2]{};
    Chain chains[2]{};
    std::vector<int64_t> formats;
    void initXR();
    bool locate();
    // The frame's layers in order (null entries skipped): the eye images or seated screen first,
    // then the seat panel and the performance display over them.
    void end(const XrCompositionLayerBaseHeader* layer = nullptr, const XrCompositionLayerBaseHeader* overlay = nullptr,
             const XrCompositionLayerBaseHeader* top = nullptr) {
        active = false;
        if (!frameOpen) return;
        frameOpen = false;
        const XrCompositionLayerBaseHeader* layers[3]{};
        uint32_t count = 0;
        if (layer) {
            layers[count++] = layer;
            if (overlay) layers[count++] = overlay;
            if (top) layers[count++] = top;
        }
        XrFrameEndInfo info{XR_TYPE_FRAME_END_INFO};
        info.displayTime = time;
        info.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
        info.layerCount = count;
        info.layers = count ? layers : nullptr;
        checked(xrEndFrame(session, &info), "xrEndFrame");
    }
    // The in-game seat panel (seat.cpp, picture from seat_panel.cpp): a quad in front of the seat,
    // redrawn only when its contents change.
    static constexpr UINT panelWidth = 768, panelHeight = 1024;
    XrSwapchain panelChain{};
    std::vector<XrSwapchainImageD3D11KHR> panelImages;
    DXGI_FORMAT panelFormat{};
    unsigned panelVersion{};
    bool panelReady{}, panelAnchored{};
    XrPosef panelAnchor{};
    void dropPanel() {
        if (panelChain) checked(xrDestroySwapchain(panelChain), "xrDestroySwapchain");
        panelChain = {}; panelImages.clear(); panelReady = false; panelVersion = 0;
    }
    bool updatePanel(const SeatPanelState& seat);
    // The desktop mirror (drawMirror): the last submitted eye rectangle of the snapshot.
    RECT mirrorSource{};
    bool mirrorValid{};
    ComPtr<ID3D11PixelShader> mirrorPS;
    ComPtr<ID3D11SamplerState> mirrorSampler;
    ComPtr<ID3D11Buffer> mirrorBuffer;
    void drawMirror(ID3D11Texture2D* back);
    bool drain() noexcept {
        if (!gpuUsed) return true;
        context->End(completion.Get());
        context->Flush();
        const ULONGLONG deadline = GetTickCount64()+2000;
        for (;;) {
            HRESULT hr = context->GetData(completion.Get(), nullptr, 0, 0);
            if (hr != S_FALSE) {
                if (FAILED(hr) && SUCCEEDED(device->GetDeviceRemovedReason())) return false;
                gpuUsed = false;
                return true;
            }
            if (GetTickCount64() >= deadline) return false;
            Sleep(1);
        }
    }
    void dropChains() {
        require(drain(), "gpu_work_pending");
        for (Chain& chain : chains) {
            if (chain.handle) checked(xrDestroySwapchain(chain.handle), "xrDestroySwapchain");
            chain = {};
        }
    }
    const char* stopSession() noexcept {
        if (!session || !running) return "session_inactive";
        const auto request = xrRequestExitSession(session);
        AutosportLog("XR shutdown request_exit=%d", request);
        if (request != XR_SUCCESS) return "exit_request_failed";
        const ULONGLONG deadline = GetTickCount64()+1500;
        do {
            XrEventDataBuffer event{XR_TYPE_EVENT_DATA_BUFFER};
            const auto result = xrPollEvent(instance, &event);
            if (result == XR_EVENT_UNAVAILABLE) { Sleep(2); continue; }
            if (result != XR_SUCCESS || event.type == XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING) return "exit_poll_or_instance_loss";
            if (event.type != XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) continue;
            const auto& changed = reinterpret_cast<const XrEventDataSessionStateChanged&>(event);
            if (changed.session != session) continue;
            AutosportLog("XR shutdown state=%d", changed.state);
            if (changed.state == XR_SESSION_STATE_STOPPING && running) {
                const auto ended = xrEndSession(session);
                AutosportLog("XR shutdown end_session=%d", ended);
                if (ended != XR_SUCCESS) return "end_session_failed";
                running = false;
            }
            if (changed.state == XR_SESSION_STATE_EXITING) return running ? "exiting_without_stopping" : "orderly_exit";
            if (changed.state == XR_SESSION_STATE_LOSS_PENDING) return "session_loss";
        } while (GetTickCount64()<deadline);
        // xrEndSession is valid only after STOPPING. xrDestroySession is legal
        // in any state once synchronized, including a runtime that stops responding.
        return running ? "stopping_timeout" : "exiting_timeout_after_end";
    }
    const char* close() {
        AutosportLog("XR shutdown begin owner=%lu caller=%lu frame=%llu open=%u running=%u",
                     owner, GetCurrentThreadId(), serial, unsigned(frameOpen), unsigned(running));
        try { end(); } catch (const std::exception& error) { AutosportLog("XR shutdown frame_end=%s", error.what()); }
        frameRate.write(loggedPeriod > 0 ? 1e9 / double(loggedPeriod) : 0.0);
        const char* outcome = stopSession();
        AutosportLog("XR shutdown handshake=%s releasing_gpu_and_swapchains", outcome);
        dropChains();
        dropPanel();
        if (metersStarted && (AutosportPerformanceDisplay() || AutosportDiagnosticLogging())) {
            AutosportLog("Performance display=%u updates=%u last_fps=%.1f last_gpu=%.0f display_hz=%.2f",
                         unsigned(meter.Enabled()), meter.Updates(), sent.Fps(), gpu.Percent(), displayHz());
            gpu.Stop();
            if (!meter.Shutdown()) AutosportLog("XR shutdown performance_display_swapchain=failed");
        }
        AutosportLog("XR shutdown swapchains_released destroying_spaces");
        if (headSpace) { checked(xrDestroySpace(headSpace), "xrDestroyViewSpace"); headSpace = {}; }
        if (local) { checked(xrDestroySpace(local), "xrDestroySpace"); local = {}; }
        AutosportLog("XR shutdown spaces_released destroying_session");
        if (session) { checked(xrDestroySession(session), "xrDestroySession"); session = {}; }
        AutosportLog("XR shutdown session_released destroying_instance");
        if (instance) { checked(xrDestroyInstance(instance), "xrDestroyInstance"); instance = {}; }
        return outcome;
    }
    void copySource(ID3D11Texture2D* source, unsigned subresource, const RECT (&r)[2], unsigned used, D3D11_TEXTURE2D_DESC& desc);
    void createChain(Chain& chain, UINT width, UINT height, DXGI_FORMAT format);
    void convert(ID3D11Texture2D* target, DXGI_FORMAT targetFormat, const RECT& rect, bool decode);
    EyeFormat eyeFormat(DXGI_FORMAT source) const;
    DXGI_FORMAT loggedSource{}, loggedTarget{};
    bool submit(const RECT (&rects)[2], DXGI_FORMAT sourceFormat, BridgePresentation presentation);
};
// Explicit teardown on the render thread; never dispatch GPU/runtime work from DLL detach.
Bridge* bridge{};
std::mutex guard;
bool gameExiting{};
// Called with guard held. A foreign-thread callback can request shutdown, never
// touch the game's immediate context. Service it at the next render callback.
bool finishPendingClose() noexcept {
    if (!bridge || !bridge->closePending || bridge->owner != GetCurrentThreadId()) return false;
    try {
        const char* handshake = bridge->close();
        delete bridge; bridge = nullptr;
        snprintf(status, sizeof(status), "bridge_closed_%s", handshake);
        AutosportLog("XR shutdown complete handshake=%s", handshake);
    } catch (const std::exception& error) {
        bridge->failed = true;
        report(error.what());
        AutosportLog("XR shutdown incomplete reason=%s", status);
    }
    return true;
}

void Bridge::initXR() {
    const char* enabled = XR_KHR_D3D11_ENABLE_EXTENSION_NAME;
    XrInstanceCreateInfo info{XR_TYPE_INSTANCE_CREATE_INFO};
    strcpy_s(info.applicationInfo.applicationName, "GRID Autosport VR");
    info.applicationInfo.apiVersion = XR_MAKE_VERSION(1,0,0);
    info.enabledExtensionCount = 1;
    info.enabledExtensionNames = &enabled;
    // Runtime choice (launcher Settings → VR runtime, as GRID2VR): openxr-runtime.txt in the session
    // folder holds "VDXR" or "SteamVR" and the runtime's manifest path on the next line; without it
    // the system's 32-bit runtime is used. Steam does not pass the launcher's environment to the
    // game, so XR_RUNTIME_JSON is set in this process only around instance creation, then restored.
    wchar_t manifest[MAX_PATH]{};
    char kind[16]{};
    if (const wchar_t* folder = AutosportRunDirectory(); folder && *folder) {
        wchar_t path[MAX_PATH];
        swprintf_s(path, L"%s\\openxr-runtime.txt", folder);
        if (FILE* file = _wfsopen(path, L"rb", _SH_DENYWR)) {
            char line[16]{}, target[MAX_PATH * 3]{};
            if (fgets(line, sizeof(line), file) && fgets(target, sizeof(target), file)) {
                line[strcspn(line, "\r\n")] = 0; target[strcspn(target, "\r\n")] = 0;
                const bool vdxr = !strcmp(line, "VDXR"), steamVr = !strcmp(line, "SteamVR");
                if ((vdxr || steamVr) && MultiByteToWideChar(CP_UTF8, 0, target, -1, manifest, MAX_PATH) > 0) {
                    const wchar_t* name = wcsrchr(manifest, L'\\');
                    name = name ? name + 1 : manifest;
                    const DWORD attributes = GetFileAttributesW(manifest);
                    if (_wcsicmp(name, vdxr ? L"virtualdesktop-openxr-32.json" : L"steamxr_win32.json") ||
                        attributes == INVALID_FILE_ATTRIBUTES || (attributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)))
                        manifest[0] = 0;
                    else strcpy_s(kind, line);
                }
            }
            fclose(file);
            require(kind[0] != 0, "runtime_choice_unusable");
        }
    }
    // OpenXR add-on layers the player chose to disable for GRID Autosport VR (launcher
    // VrApiLayers.cs, GitHub issue #1): openxr-layer-bypass.txt in the session folder lists each
    // layer's own disable switch, one per line. Set to 1 only around instance creation, then restored.
    std::vector<std::wstring> layerSwitches;
    if (const wchar_t* folder = AutosportRunDirectory(); folder && *folder) {
        wchar_t path[MAX_PATH];
        swprintf_s(path, L"%s\\openxr-layer-bypass.txt", folder);
        if (FILE* file = _wfsopen(path, L"rb", _SH_DENYWR)) {
            char line[256]{};
            bool valid = true;
            while (valid && fgets(line, sizeof(line), file)) {
                const size_t length = strcspn(line, "\r\n");
                valid = line[length] != 0 || feof(file); // every line fits
                line[length] = 0;
                const size_t prefix = !strncmp(line, "DISABLE_XR_", 11) ? 11 : !strncmp(line, "DISABLE_OPENXR_", 15) ? 15 : 0;
                valid = valid && prefix && length > prefix && length - prefix <= 180 && layerSwitches.size() < 16 &&
                        std::all_of(line + prefix, line + length, [](char c) { return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'; });
                if (valid) layerSwitches.emplace_back(line, line + length);
            }
            fclose(file);
            require(valid && !layerSwitches.empty(), "layer_bypass_request");
        }
    }
    struct Saved { std::wstring name; std::vector<wchar_t> value; };
    std::vector<Saved> saved;
    for (const wchar_t* name : {L"XR_RUNTIME_JSON"}) saved.push_back({name, {}});
    for (const auto& name : layerSwitches) saved.push_back({name, {}});
    bool environmentSaved = true;
    for (auto& variable : saved) {
        const DWORD length = GetEnvironmentVariableW(variable.name.c_str(), nullptr, 0);
        variable.value.assign(length, L'\0');
        environmentSaved = environmentSaved && (!length || GetEnvironmentVariableW(variable.name.c_str(), variable.value.data(), length));
    }
    require(environmentSaved, "environment_save");
    bool environmentSet = true;
    if (kind[0]) {
        environmentSet = SetEnvironmentVariableW(L"XR_RUNTIME_JSON", manifest) != FALSE;
        AutosportLog("OpenXR runtime choice %s: %ls", kind, manifest);
    }
    for (const auto& name : layerSwitches) {
        environmentSet = environmentSet && SetEnvironmentVariableW(name.c_str(), L"1");
        AutosportLog("OpenXR layer disabled for this session: %ls", name.c_str());
    }
    const XrResult created = environmentSet ? xrCreateInstance(&info, &instance) : XR_ERROR_INITIALIZATION_FAILED;
    bool environmentRestored = true;
    for (const auto& variable : saved)
        environmentRestored = SetEnvironmentVariableW(variable.name.c_str(), variable.value.empty() ? nullptr : variable.value.data()) && environmentRestored;
    if (!layerSwitches.empty()) AutosportLog("OpenXR layer switches restored=%d", environmentRestored);
    checked(created, "xrCreateInstance");
    require(environmentRestored, "environment_restore");
    XrInstanceProperties properties{XR_TYPE_INSTANCE_PROPERTIES};
    checked(xrGetInstanceProperties(instance, &properties), "xrGetInstanceProperties");
    AutosportLog("OpenXR runtime %s version %llu", properties.runtimeName, static_cast<unsigned long long>(properties.runtimeVersion));
    // xr-sim, the headset-free test runtime, stands in for either in the release checks
    // (tools/release_check.py), whose manifest copy carries the chosen runtime's file name.
    require(!kind[0] || !strcmp(properties.runtimeName, "xr-sim") || (!strcmp(kind, "VDXR") ? !strcmp(properties.runtimeName, "VirtualDesktopXR")
                                               : !strncmp(properties.runtimeName, "SteamVR/", 8)), "runtime_identity_mismatch");
    XrSystemGetInfo systemInfo{XR_TYPE_SYSTEM_GET_INFO};
    systemInfo.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    XrSystemId system{};
    checked(xrGetSystem(instance, &systemInfo, &system), "xrGetSystem");
    uint32_t n{};
    checked(xrEnumerateViewConfigurationViews(instance, system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO,
                                                0, &n, nullptr), "enumerate_stereo_views");
    require(n == 2, "expected_two_runtime_eyes");
    checked(xrEnumerateEnvironmentBlendModes(instance, system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO,
                                               0, &n, nullptr), "count_blend_modes");
    std::vector<XrEnvironmentBlendMode> modes(n);
    checked(xrEnumerateEnvironmentBlendModes(instance, system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO,
                                               n, &n, modes.data()), "read_blend_modes");
    require(std::find(modes.begin(), modes.end(), XR_ENVIRONMENT_BLEND_MODE_OPAQUE) != modes.end(), "opaque_mode_missing");
    PFN_xrGetD3D11GraphicsRequirementsKHR get{};
    checked(xrGetInstanceProcAddr(instance, "xrGetD3D11GraphicsRequirementsKHR",
                                  reinterpret_cast<PFN_xrVoidFunction*>(&get)), "get_requirements_proc");
    XrGraphicsRequirementsD3D11KHR requirements{XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR};
    checked(get(instance, system, &requirements), "get_d3d_requirements");
    ComPtr<IDXGIDevice> dxgi;
    ComPtr<IDXGIAdapter> adapter;
    DXGI_ADAPTER_DESC desc{};
    checked(device.As(&dxgi), "get_dxgi_device");
    checked(dxgi->GetAdapter(&adapter), "get_game_adapter");
    checked(adapter->GetDesc(&desc), "describe_game_adapter");
    const auto luid = requirements.adapterLuid;
    require((!luid.HighPart && !luid.LowPart) ||
            (desc.AdapterLuid.HighPart == luid.HighPart && desc.AdapterLuid.LowPart == luid.LowPart), "adapter_luid_mismatch");
    require(device->GetFeatureLevel() >= requirements.minFeatureLevel, "feature_level_below_runtime_minimum");
    XrGraphicsBindingD3D11KHR binding{XR_TYPE_GRAPHICS_BINDING_D3D11_KHR};
    binding.device = device.Get();
    XrSessionCreateInfo sessionInfo{XR_TYPE_SESSION_CREATE_INFO, &binding};
    sessionInfo.systemId = system;
    checked(xrCreateSession(instance, &sessionInfo, &session), "xrCreateSession");
    XrReferenceSpaceCreateInfo spaceInfo{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
    spaceInfo.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
    spaceInfo.poseInReferenceSpace.orientation.w = 1;
    checked(xrCreateReferenceSpace(session, &spaceInfo, &local), "create_local_space");
    spaceInfo.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_VIEW;
    checked(xrCreateReferenceSpace(session, &spaceInfo, &headSpace), "create_view_space");
    checked(xrEnumerateSwapchainFormats(session, 0, &n, nullptr), "count_eye_formats");
    require(n > 0 && n < 1024, "invalid_eye_format_count");
    formats.resize(n);
    checked(xrEnumerateSwapchainFormats(session, n, &n, formats.data()), "read_eye_formats");
    const D3D11_QUERY_DESC query{D3D11_QUERY_EVENT, 0};
    checked(device->CreateQuery(&query, &completion), "create_completion_query");
}

bool Bridge::locate() {
    XrEventDataBuffer event{XR_TYPE_EVENT_DATA_BUFFER};
    for (;;) {
        const auto result = xrPollEvent(instance, &event);
        if (result == XR_EVENT_UNAVAILABLE) break;
        checked(result, "xrPollEvent");
        if (event.type == XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING) throw std::runtime_error("instance_loss_pending");
        if (event.type == XR_TYPE_EVENT_DATA_REFERENCE_SPACE_CHANGE_PENDING) {
            // A runtime recenter (e.g. holding the Quest's Oculus button) moves LOCAL onto the
            // head. The seat origin and the seated screen were placed in the old LOCAL; place
            // both again from the head in the new one, as the game's own recenter does.
            const auto& change = reinterpret_cast<const XrEventDataReferenceSpaceChangePending&>(event);
            // xr-sim leaves the session handle null; this bridge only ever has one session.
            if ((change.session == session || change.session == XR_NULL_HANDLE) &&
                change.referenceSpaceType == XR_REFERENCE_SPACE_TYPE_LOCAL) {
                screenAnchored = false;
                recenterPending = true;
                AutosportLog("XR LOCAL space recentered by the runtime at frame=%llu: re-placing the seated screen and recentering", serial);
            }
        }
        if (event.type == XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) {
            const auto& changed = reinterpret_cast<const XrEventDataSessionStateChanged&>(event);
            require(changed.session == session, "unexpected_session_event");
            state = changed.state;
            AutosportLog("XR session state=%s frame=%llu gameForeground=%d", stateName(state), serial, gameForeground());
            if (changed.state == XR_SESSION_STATE_FOCUSED && !everFocused) {
                everFocused = true;
                if (locatedBeforeFocus) {
                    screenAnchored = false;
                    recenterPending = true;
                    AutosportLog("XR first focus at frame=%llu: re-placing the seated screen and recentering", serial);
                }
            }
            if (changed.state == XR_SESSION_STATE_READY && !running) {
                XrSessionBeginInfo begin{XR_TYPE_SESSION_BEGIN_INFO};
                begin.primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
                checked(xrBeginSession(session, &begin), "xrBeginSession");
                running = true;
            } else if (changed.state == XR_SESSION_STATE_STOPPING && running) {
                checked(xrEndSession(session), "xrEndSession");
                running = false;
            } else if (changed.state == XR_SESSION_STATE_EXITING || changed.state == XR_SESSION_STATE_LOSS_PENDING)
                throw std::runtime_error("headset_session_ended");
        }
        event = {XR_TYPE_EVENT_DATA_BUFFER};
    }
    if (!running) { report("waiting_for_session_ready"); return false; }
    XrFrameWaitInfo wait{XR_TYPE_FRAME_WAIT_INFO};
    XrFrameState frameState{XR_TYPE_FRAME_STATE};
    checked(xrWaitFrame(session, &wait, &frameState), "xrWaitFrame");
    time = frameState.predictedDisplayTime;
    // The launcher compares the last session's frame rate with this (Graphics → Last VR session).
    // Logged again only after a change of more than 1%, so a jittery period does not flood the log.
    const XrDuration displayPeriod = frameState.predictedDisplayPeriod;
    if (displayPeriod > 0 && (loggedPeriod == 0 || std::llabs(displayPeriod - loggedPeriod) * 100 > loggedPeriod)) {
        loggedPeriod = displayPeriod;
        AutosportLog("XR display period=%lld ns display_hz=%.2f", (long long)displayPeriod, 1e9/(double)displayPeriod);
    }
    period = frameState.predictedDisplayPeriod;
    XrFrameBeginInfo begin{XR_TYPE_FRAME_BEGIN_INFO};
    const auto result = xrBeginFrame(session, &begin);
    if (result != XR_FRAME_DISCARDED) checked(result, "xrBeginFrame");
    frameOpen = true;
    if (result == XR_FRAME_DISCARDED) { end(); report("runtime_discarded_frame"); return false; }
    // Virtual Desktop holds shouldRender false while the session is not FOCUSED, and only
    // grants FOCUSED again after frames that carry a layer. Ending those frames empty
    // parks the session at VISIBLE indefinitely: the headset stays black through the
    // startup videos and menu. The game draws these frames regardless, so keep
    // submitting them; OpenXR allows layers on a frame the runtime says not to render.
    if (!frameState.shouldRender) {
        if (!hidden) {
            hidden = true;
            hiddenFrom = serial;
            hiddenFrames = 0;
            hiddenTick = GetTickCount64();
            AutosportLog("XR runtime not rendering from frame=%llu state=%s gameForeground=%d; still submitting",
                         serial, stateName(state), gameForeground());
        }
        ++hiddenFrames;
    } else if (hidden) {
        hidden = false;
        AutosportLog("XR runtime rendering from frame=%llu after %llu frames (%.1fs from frame %llu) state=%s gameForeground=%d",
                     serial, hiddenFrames, (GetTickCount64()-hiddenTick)*1e-3, hiddenFrom, stateName(state), gameForeground());
    }
    XrViewLocateInfo locateInfo{XR_TYPE_VIEW_LOCATE_INFO};
    locateInfo.displayTime = time;
    locateInfo.viewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
    locateInfo.space = local;
    XrViewState viewState{XR_TYPE_VIEW_STATE};
    uint32_t n{};
    for (auto& view : located) view = {XR_TYPE_VIEW};
    checked(xrLocateViews(session, &locateInfo, &viewState, 2, &n, located), "xrLocateViews");
    constexpr auto valid = XR_VIEW_STATE_ORIENTATION_VALID_BIT | XR_VIEW_STATE_POSITION_VALID_BIT;
    if (n != 2 || (viewState.viewStateFlags & valid) != valid) { end(); report("invalid_runtime_pose"); return false; }
    if (!everFocused) locatedBeforeFocus = true;
    if (!screenAnchored) {
        XrSpaceLocation head{XR_TYPE_SPACE_LOCATION};
        constexpr auto flags = XR_SPACE_LOCATION_ORIENTATION_VALID_BIT | XR_SPACE_LOCATION_POSITION_VALID_BIT;
        const auto headResult = xrLocateSpace(headSpace, local, time, &head);
        if (headResult == XR_SUCCESS && (head.locationFlags & flags) == flags)
            screenAnchored = levelAnchor(head.pose, screenAnchor);
    }
    for (int i=0; i<2; ++i) {
        const auto& pose = located[i].pose;
        const auto& f = located[i].fov;
        views[i] = {{pose.position.x,pose.position.y,pose.position.z},
                    {pose.orientation.x,pose.orientation.y,pose.orientation.z,pose.orientation.w},
                    {f.angleLeft,f.angleRight,f.angleUp,f.angleDown}};
        for (float v : views[i].orientation) require(std::isfinite(v), "nonfinite_quaternion");
        for (float v : views[i].position) require(std::isfinite(v), "nonfinite_position");
        for (float v : views[i].fov) require(std::isfinite(v), "nonfinite_fov");
    }
    return true;
}

// used=1: only r[0] is presented (a flat screen, which may cover the whole texture).
void Bridge::copySource(ID3D11Texture2D* source, unsigned subresource, const RECT (&r)[2], unsigned used,
                        D3D11_TEXTURE2D_DESC& desc) {
    require(source, "no_packed_source");
    ComPtr<ID3D11Device> sourceDevice;
    source->GetDevice(&sourceDevice);
    require(sourceDevice.Get() == device.Get(), "source_device_mismatch");
    source->GetDesc(&desc);
    require(desc.MipLevels && desc.ArraySize && subresource/desc.MipLevels < desc.ArraySize, "invalid_source_subresource");
    const UINT mip = subresource % desc.MipLevels;
    const UINT width = std::max(1u, desc.Width>>mip), height = std::max(1u, desc.Height>>mip);
    for (unsigned i = 0; i < used; ++i) {
        const RECT& rect = r[i];
        require(rect.left >= 0 && rect.top >= 0 && rect.left < rect.right && rect.top < rect.bottom &&
                UINT(rect.right) <= width && UINT(rect.bottom) <= height, "invalid_eye_viewport");
    }
    require(used < 2 || r[0].right <= r[1].left || r[1].right <= r[0].left || r[0].bottom <= r[1].top ||
            r[1].bottom <= r[0].top, "overlapping_eye_viewports");
    const DXGI_FORMAT format = typedFormat(desc.Format);
    D3D11_TEXTURE2D_DESC previous{};
    if (snapshot) snapshot->GetDesc(&previous);
    if (!snapshot || previous.Width != width || previous.Height != height || previous.Format != format) {
        snapshotView.Reset();
        snapshot.Reset();
        D3D11_TEXTURE2D_DESC frozen{};
        frozen.Width = width; frozen.Height = height;
        frozen.ArraySize = frozen.MipLevels = frozen.SampleDesc.Count = 1;
        frozen.Format = format; frozen.Usage = D3D11_USAGE_DEFAULT;
        frozen.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        checked(device->CreateTexture2D(&frozen, nullptr, &snapshot), "allocate_packed_snapshot");
    }
    if (desc.SampleDesc.Count > 1) {
        UINT support{};
        checked(device->CheckFormatSupport(format, &support), "check_resolve_format");
        require((support & D3D11_FORMAT_SUPPORT_MULTISAMPLE_RESOLVE) != 0, "unsupported_packed_msaa_resolve");
        context->ResolveSubresource(snapshot.Get(), 0, source, subresource, format);
    } else {
        context->CopySubresourceRegion(snapshot.Get(), 0, 0,0,0, source, subresource, nullptr);
    }
}

void Bridge::createChain(Chain& chain, UINT width, UINT height, DXGI_FORMAT format) {
    XrSwapchainCreateInfo info{XR_TYPE_SWAPCHAIN_CREATE_INFO};
    info.usageFlags = XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT | XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT;
    info.format = format; info.width = width; info.height = height;
    info.sampleCount = info.faceCount = info.arraySize = info.mipCount = 1;
    checked(xrCreateSwapchain(session, &info, &chain.handle), "create_eye_swapchain");
    uint32_t n{};
    checked(xrEnumerateSwapchainImages(chain.handle, 0, &n, nullptr), "count_eye_images");
    require(n && n <= 32, "invalid_eye_image_count");
    chain.images.resize(n, {XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR});
    checked(xrEnumerateSwapchainImages(chain.handle, n, &n,
            reinterpret_cast<XrSwapchainImageBaseHeader*>(chain.images.data())), "read_eye_images");
    chain.width = width; chain.height = height; chain.format = format;
}

// Convert formats through a private deferred context. Executing with restore=TRUE
// preserves the game's immediate-context shaders, targets, viewports and resources.
// The game's final picture holds display-encoded (sRGB) colour in a plain UNORM or float texture.
// OpenXR runtimes read a non-sRGB swapchain as linear light and encode it again for the display,
// which lifts the shadows and flattens contrast (the "washed out" look). As GRID2VR, the eye
// images go to an sRGB swapchain: the same bytes copied for 8-bit sources, else decoded to linear
// by the conversion shader so the sRGB target's hardware write encodes them once. Classic colors
// (-gasvrclassiccolor=1) keeps the earlier choice: the source's own format, else the first offered.
EyeFormat Bridge::eyeFormat(DXGI_FORMAT source) const {
    auto available = [&](DXGI_FORMAT f) { return std::find(formats.begin(), formats.end(), int64_t(f)) != formats.end(); };
    if (!AutosportClassicColor()) {
        if (isSrgb(source) && available(source)) return {source, true, false};
        if (const auto same = srgbVariant(source); same != DXGI_FORMAT_UNKNOWN && available(same)) return {same, true, false};
        for (const auto srgb : {DXGI_FORMAT_R8G8B8A8_UNORM_SRGB, DXGI_FORMAT_B8G8R8A8_UNORM_SRGB})
            if (available(srgb)) return {srgb, false, !isSrgb(source)};
    }
    if (available(source)) return {source, true, false};
    constexpr DXGI_FORMAT alternatives[] = {DXGI_FORMAT_R8G8B8A8_UNORM_SRGB, DXGI_FORMAT_B8G8R8A8_UNORM_SRGB,
                                            DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_B8G8R8A8_UNORM};
    const auto choice = std::find_if(std::begin(alternatives), std::end(alternatives), available);
    require(choice != std::end(alternatives), "no_supported_xr_color_format");
    return {*choice, false, false};
}

void Bridge::convert(ID3D11Texture2D* target, DXGI_FORMAT targetFormat, const RECT& rect, bool decode) {
    if (!deferred) {
        checked(device->CreateDeferredContext(0, &deferred), "create_conversion_context");
        constexpr char shader[] = R"(
            Texture2D<float4> source : register(t0);
            cbuffer Crop : register(b0) { int2 origin; int decode; int unused; };
            float4 vs(uint id : SV_VertexID) : SV_Position {
                return float4(id==1 ? 3 : -1, id==2 ? -3 : 1, 0, 1);
            }
            float4 ps(float4 position : SV_Position) : SV_Target {
                float3 c = source.Load(int3(origin + int2(position.xy), 0)).rgb;
                if (decode) { c = saturate(c); c = c <= .04045 ? c / 12.92 : pow((c + .055) / 1.055, 2.4); }
                return float4(c, 1);
            })";
        ComPtr<ID3DBlob> vs, ps;
        checked(D3DCompile(shader, sizeof(shader)-1, nullptr,nullptr,nullptr,"vs","vs_4_0",0,0,&vs,nullptr), "compile_conversion_vs");
        checked(D3DCompile(shader, sizeof(shader)-1, nullptr,nullptr,nullptr,"ps","ps_4_0",0,0,&ps,nullptr), "compile_conversion_ps");
        checked(device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&blitVS), "create_conversion_vs");
        checked(device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&blitPS), "create_conversion_ps");
        D3D11_BUFFER_DESC buffer{};
        buffer.ByteWidth = 16; buffer.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        checked(device->CreateBuffer(&buffer,nullptr,&cropBuffer), "create_crop_buffer");
    }
    if (!snapshotView) checked(device->CreateShaderResourceView(snapshot.Get(), nullptr, &snapshotView), "create_source_srv");
    D3D11_RENDER_TARGET_VIEW_DESC desc{};
    desc.Format = targetFormat; desc.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
    ComPtr<ID3D11RenderTargetView> rtv;
    checked(device->CreateRenderTargetView(target,&desc,&rtv), "create_conversion_target");
    const LONG origin[4] = {rect.left,rect.top,decode ? 1 : 0,0};
    deferred->UpdateSubresource(cropBuffer.Get(),0,nullptr,origin,0,0);
    D3D11_VIEWPORT viewport{0,0,float(rect.right-rect.left),float(rect.bottom-rect.top),0,1};
    deferred->RSSetViewports(1,&viewport);
    deferred->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    deferred->VSSetShader(blitVS.Get(),nullptr,0);
    deferred->PSSetShader(blitPS.Get(),nullptr,0);
    deferred->PSSetConstantBuffers(0,1,cropBuffer.GetAddressOf());
    deferred->PSSetShaderResources(0,1,snapshotView.GetAddressOf());
    deferred->OMSetRenderTargets(1,rtv.GetAddressOf(),nullptr);
    deferred->Draw(3,0);
    ComPtr<ID3D11CommandList> commands;
    checked(deferred->FinishCommandList(FALSE,&commands), "finish_conversion");
    context->ExecuteCommandList(commands.Get(), TRUE);
}

// The game's window shows the left eye (or the seated-screen frame), scaled to fill the window and
// cropped at the centre to its aspect: the eye image is far larger than the window, so the plain
// copy presentMirror uses for equal sizes never applies. Drawn on the private deferred context.
void Bridge::drawMirror(ID3D11Texture2D* back) {
    if (!mirrorValid || !snapshot) return;
    if (!deferred) checked(device->CreateDeferredContext(0, &deferred), "create_conversion_context");
    if (!mirrorPS) {
        constexpr char shader[] = R"(
            Texture2D<float4> source : register(t0);
            SamplerState linearClamp : register(s0);
            cbuffer Map : register(b0) { float2 origin; float2 scale; float2 target; float2 unused; };
            float4 vs(uint id : SV_VertexID) : SV_Position {
                return float4(id==1 ? 3 : -1, id==2 ? -3 : 1, 0, 1);
            }
            float4 ps(float4 position : SV_Position) : SV_Target {
                return float4(source.SampleLevel(linearClamp, origin + scale * (position.xy / target), 0).rgb, 1);
            })";
        ComPtr<ID3DBlob> vs, ps;
        checked(D3DCompile(shader, sizeof(shader)-1, nullptr,nullptr,nullptr,"ps","ps_4_0",0,0,&ps,nullptr), "compile_mirror_ps");
        checked(device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&mirrorPS), "create_mirror_ps");
        if (!blitVS) {
            checked(D3DCompile(shader, sizeof(shader)-1, nullptr,nullptr,nullptr,"vs","vs_4_0",0,0,&vs,nullptr), "compile_mirror_vs");
            checked(device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&blitVS), "create_mirror_vs");
        }
        D3D11_SAMPLER_DESC sampler{};
        sampler.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampler.MaxLOD = D3D11_FLOAT32_MAX;
        checked(device->CreateSamplerState(&sampler, &mirrorSampler), "create_mirror_sampler");
        D3D11_BUFFER_DESC buffer{};
        buffer.ByteWidth = 32; buffer.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        checked(device->CreateBuffer(&buffer, nullptr, &mirrorBuffer), "create_mirror_buffer");
    }
    if (!snapshotView) checked(device->CreateShaderResourceView(snapshot.Get(), nullptr, &snapshotView), "create_source_srv");
    D3D11_TEXTURE2D_DESC target{}, source{};
    back->GetDesc(&target);
    snapshot->GetDesc(&source);
    if (!target.Width || !target.Height || !(target.BindFlags & D3D11_BIND_RENDER_TARGET)) return;
    // The source rectangle cropped at its centre to the window's aspect.
    float x = float(mirrorSource.left), y = float(mirrorSource.top);
    float w = float(mirrorSource.right - mirrorSource.left), h = float(mirrorSource.bottom - mirrorSource.top);
    const float windowAspect = float(target.Width) / float(target.Height);
    if (w / h > windowAspect) { const float crop = h * windowAspect; x += (w - crop) * .5f; w = crop; }
    else { const float crop = w / windowAspect; y += (h - crop) * .5f; h = crop; }
    const float map[8] = {x / source.Width, y / source.Height, w / source.Width, h / source.Height,
                          float(target.Width), float(target.Height), 0, 0};
    D3D11_RENDER_TARGET_VIEW_DESC desc{};
    desc.Format = typedFormat(target.Format);
    desc.ViewDimension = target.SampleDesc.Count > 1 ? D3D11_RTV_DIMENSION_TEXTURE2DMS : D3D11_RTV_DIMENSION_TEXTURE2D;
    ComPtr<ID3D11RenderTargetView> rtv;
    checked(device->CreateRenderTargetView(back, &desc, &rtv), "create_mirror_target");
    deferred->UpdateSubresource(mirrorBuffer.Get(), 0, nullptr, map, 0, 0);
    D3D11_VIEWPORT viewport{0, 0, float(target.Width), float(target.Height), 0, 1};
    deferred->RSSetViewports(1, &viewport);
    deferred->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    deferred->VSSetShader(blitVS.Get(), nullptr, 0);
    deferred->PSSetShader(mirrorPS.Get(), nullptr, 0);
    deferred->PSSetConstantBuffers(0, 1, mirrorBuffer.GetAddressOf());
    deferred->PSSetSamplers(0, 1, mirrorSampler.GetAddressOf());
    deferred->PSSetShaderResources(0, 1, snapshotView.GetAddressOf());
    deferred->OMSetRenderTargets(1, rtv.GetAddressOf(), nullptr);
    deferred->Draw(3, 0);
    ComPtr<ID3D11CommandList> commands;
    checked(deferred->FinishCommandList(FALSE, &commands), "finish_mirror");
    context->ExecuteCommandList(commands.Get(), TRUE);
    gpuUsed = true;
    // Test only (never passed by the launcher): -gasvrmirrordump=1 writes the mirrored back buffer
    // once, 300 mirrored frames in, as raw rows to mirror.raw in the session folder (16-byte
    // header: width, height, DXGI format, row pitch), so the mirror can be checked headset-free.
    static unsigned mirrored;
    if (++mirrored == 300 && target.SampleDesc.Count == 1 && wcsstr(GetCommandLineW(), L" -gasvrmirrordump=1") && AutosportRunDirectory()) {
        D3D11_TEXTURE2D_DESC staging = target;
        staging.Usage = D3D11_USAGE_STAGING; staging.BindFlags = 0; staging.CPUAccessFlags = D3D11_CPU_ACCESS_READ; staging.MiscFlags = 0;
        ComPtr<ID3D11Texture2D> copy;
        D3D11_MAPPED_SUBRESOURCE mapped{};
        if (SUCCEEDED(device->CreateTexture2D(&staging, nullptr, &copy))) {
            context->CopyResource(copy.Get(), back);
            if (SUCCEEDED(context->Map(copy.Get(), 0, D3D11_MAP_READ, 0, &mapped))) {
                wchar_t path[MAX_PATH];
                swprintf_s(path, L"%s\\mirror.raw", AutosportRunDirectory());
                if (FILE* f = _wfsopen(path, L"wb", _SH_DENYWR)) {
                    const UINT header[4] = {target.Width, target.Height, UINT(target.Format), mapped.RowPitch};
                    fwrite(header, sizeof(header), 1, f);
                    fwrite(mapped.pData, mapped.RowPitch, target.Height, f);
                    fclose(f);
                }
                context->Unmap(copy.Get(), 0);
            }
        }
        AutosportLog("Desktop mirror dump %ux%u format=%u", target.Width, target.Height, unsigned(target.Format));
    }
}

bool Bridge::submit(const RECT (&rects)[2], DXGI_FORMAT sourceFormat, BridgePresentation presentation) {
    const bool flat = presentation == BridgePresentation::FlatScreen;
    mirrorSource = rects[0];
    mirrorValid = rects[0].right > rects[0].left && rects[0].bottom > rects[0].top;
    if (flat && !screenAnchored) {
        end();
        report("waiting_for_level_screen_anchor");
        return false;
    }
    const unsigned eyeCount = flat ? 1u : 2u;
    const EyeFormat chosen = eyeFormat(sourceFormat);
    const DXGI_FORMAT format = chosen.format;
    if (sourceFormat != loggedSource || format != loggedTarget) {
        loggedSource = sourceFormat; loggedTarget = format;
        AutosportLog("Eye colour: source format=%u swapchain format=%u %s%s", unsigned(sourceFormat), unsigned(format),
                     chosen.copy ? "copy" : chosen.decode ? "decode_to_srgb" : "convert", AutosportClassicColor() ? " (classic colors)" : "");
    }
    for (unsigned i=0; i<eyeCount; ++i) {
        const auto& r = rects[i];
        if (chains[i].handle && (chains[i].width != UINT(r.right-r.left) || chains[i].height != UINT(r.bottom-r.top) ||
                                 chains[i].format != format)) { dropChains(); break; }
    }
    XrCompositionLayerProjectionView projections[2]{};
    for (unsigned i=0; i<eyeCount; ++i) {
        Chain& chain = chains[i];
        const auto& r = rects[i];
        if (!chain.handle) createChain(chain, r.right-r.left,r.bottom-r.top,format);
        XrSwapchainImageAcquireInfo acquire{XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO};
        uint32_t index{};
        checked(xrAcquireSwapchainImage(chain.handle,&acquire,&index), "acquire_eye_image");
        const XrSwapchainImageWaitInfo wait{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO, nullptr, 100'000'000};
        checked(xrWaitSwapchainImage(chain.handle,&wait), "wait_eye_image");
        XrSwapchainImageReleaseInfo release{XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO};
        try {
            require(index < chain.images.size() && chain.images[index].texture, "invalid_swapchain_image");
            auto* target = chain.images[index].texture;
            gpuUsed = true;
            if (chosen.copy) {
                const D3D11_BOX box{UINT(r.left),UINT(r.top),0,UINT(r.right),UINT(r.bottom),1};
                context->CopySubresourceRegion(target,0,0,0,0,snapshot.Get(),0,&box);
            } else {
                convert(target,format,r,chosen.decode);
            }
        } catch (...) {
            xrReleaseSwapchainImage(chain.handle,&release);
            throw;
        }
        checked(xrReleaseSwapchainImage(chain.handle,&release), "release_eye_image");
        auto& view = projections[i];
        view.type = XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW;
        view.pose = located[i].pose; view.fov = located[i].fov;
        view.subImage.swapchain = chain.handle;
        view.subImage.imageRect.extent = {int32_t(chain.width),int32_t(chain.height)};
    }
    if (flat) {
        XrCompositionLayerQuad screen{XR_TYPE_COMPOSITION_LAYER_QUAD};
        screen.space = local;
        screen.eyeVisibility = XR_EYE_VISIBILITY_BOTH;
        screen.subImage = projections[0].subImage;
        screen.pose = screenAnchor;
        // A whole-texture source (the game's back buffer) keeps its pixel aspect. An eye image
        // covers the field of view the game rendered with, so show it at that tangent aspect;
        // the game's pixels need not be square (the legacy 400x600 eye was not).
        D3D11_TEXTURE2D_DESC canvas{};
        snapshot->GetDesc(&canvas);
        const auto& f = located[0].fov;
        // The field of view the game rendered the flat frame with (native_ovr applyViews):
        // centred and at least 16:9.
        const float tangentAspect = (std::max)((std::tan(-f.angleLeft) + std::tan(f.angleRight)) /
                                               (std::tan(f.angleUp) + std::tan(-f.angleDown)), 16.f / 9.f);
        const bool wholeTexture = UINT(rects[0].right - rects[0].left) == canvas.Width && UINT(rects[0].bottom - rects[0].top) == canvas.Height;
        const float aspect = wholeTexture || !(tangentAspect > .2f && tangentAspect < 5.f) ? float(canvas.Width) / float(canvas.Height) : tangentAspect;
        // At most 2.4 m wide and 1.8 m tall at any distance (further away it covers less of the
        // view): 4:3 keeps the original 2.4 x 1.8 m.
        screen.size = aspect >= 2.4f / 1.8f ? XrExtent2Df{2.4f, 2.4f / aspect} : XrExtent2Df{1.8f * aspect, 1.8f};
        frameRate.interrupt();
        end(reinterpret_cast<const XrCompositionLayerBaseHeader*>(&screen), meterLayer());
        frameSent();
        return true;
    }
    XrCompositionLayerProjection projection{XR_TYPE_COMPOSITION_LAYER_PROJECTION};
    projection.space = local; projection.viewCount = 2; projection.views = projections;
    SeatPanelState seat{};
    XrCompositionLayerQuad panel{XR_TYPE_COMPOSITION_LAYER_QUAD};
    bool showPanel = AutosportSeatPanel(seat) && updatePanel(seat);
    if (!showPanel) panelAnchored = false;
    else if (!panelAnchored) {
        // Placed once when it opens, as GRID2VR: 1 m ahead along the gaze heading, at eye level.
        XrSpaceLocation head{XR_TYPE_SPACE_LOCATION};
        constexpr auto flags = XR_SPACE_LOCATION_ORIENTATION_VALID_BIT | XR_SPACE_LOCATION_POSITION_VALID_BIT;
        if (xrLocateSpace(headSpace, local, time, &head) == XR_SUCCESS && (head.locationFlags & flags) == flags)
            panelAnchored = levelAnchor(head.pose, panelAnchor, 1.0, 0.f);
        showPanel = panelAnchored;
    }
    if (showPanel) {
        panel.layerFlags = XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT | XR_COMPOSITION_LAYER_UNPREMULTIPLIED_ALPHA_BIT; // As GRID2VR.
        panel.space = local;
        panel.eyeVisibility = XR_EYE_VISIBILITY_BOTH;
        panel.subImage.swapchain = panelChain;
        panel.subImage.imageRect.extent = {int32_t(panelWidth), int32_t(panelHeight)};
        panel.pose = panelAnchor;
        panel.size = {.6f, .8f};
    }
    end(reinterpret_cast<const XrCompositionLayerBaseHeader*>(&projection),
        showPanel ? reinterpret_cast<const XrCompositionLayerBaseHeader*>(&panel) : nullptr, meterLayer());
    frameRate.stereo();
    frameSent();
    return true;
}

void Bridge::startMeters() {
    metersStarted = true;
    const bool display = AutosportPerformanceDisplay(), diagnostics = AutosportDiagnosticLogging();
    if (!display && !diagnostics) return;
    const bool sampling = gpu.Start(device.Get());
    const bool shown = display && meter.Initialize(session, headSpace, device.Get(), context.Get(), formats, AutosportHudDistance());
    AutosportLog("Performance meters: display=%u (%s) diagnostics=%u gpu_counter=%u", unsigned(display),
                 display ? (shown ? "shown" : "unavailable") : "off", unsigned(diagnostics), unsigned(sampling));
}
// The performance display's quad for this frame, or null.
const XrCompositionLayerBaseHeader* Bridge::meterLayer() {
    if (!metersStarted) startMeters();
    if (!meter.Enabled()) return nullptr;
    const auto* quad = meter.Layer(GetTickCount64(), sent.Fps(), displayHz(), gpu.Percent(), gpuUsed);
    if (!quad && !meter.Enabled()) AutosportLog("Performance display stopped: its swapchain image could not be updated");
    // Test only (never passed by the launcher): -gasvrperfdump=1 saves the panel's first picture
    // as performance-display.bmp in the session folder, so it can be checked without a headset.
    static bool dumped;
    if (quad && meter.Updates() == 1 && !dumped && wcsstr(GetCommandLineW(), L" -gasvrperfdump=1")) {
        dumped = true;
        writePanelPicture(PerformanceDisplay::Pixels(sent.Fps(), displayHz(), gpu.Percent()));
    }
    return reinterpret_cast<const XrCompositionLayerBaseHeader*>(quad);
}
// A frame reached the headset: the frames-sent rate, and with diagnostic logging a line a second.
void Bridge::frameSent() {
    if (!sent.Frame(GetTickCount64()) || !AutosportDiagnosticLogging()) return;
    const double usage = gpu.Percent();
    char gpuText[16];
    if (usage < 0) strcpy_s(gpuText, "n/a"); else sprintf_s(gpuText, "%.0f%%", usage);
    AutosportLog("Frame rate second fps=%.1f display_hz=%.2f gpu=%s", sent.Fps(), displayHz(), gpuText);
}

bool Bridge::updatePanel(const SeatPanelState& seat) {
    if (!panelChain) {
        constexpr DXGI_FORMAT wanted[] = {DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_FORMAT_R8G8B8A8_UNORM,
                                          DXGI_FORMAT_B8G8R8A8_UNORM_SRGB, DXGI_FORMAT_R8G8B8A8_UNORM_SRGB};
        const auto choice = std::find_if(std::begin(wanted), std::end(wanted), [&](DXGI_FORMAT f) {
            return std::find(formats.begin(), formats.end(), int64_t(f)) != formats.end(); });
        if (choice == std::end(wanted)) return false;
        XrSwapchainCreateInfo info{XR_TYPE_SWAPCHAIN_CREATE_INFO};
        info.usageFlags = XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT | XR_SWAPCHAIN_USAGE_SAMPLED_BIT;
        info.format = *choice; info.width = panelWidth; info.height = panelHeight;
        info.sampleCount = info.faceCount = info.arraySize = info.mipCount = 1;
        checked(xrCreateSwapchain(session, &info, &panelChain), "create_panel_swapchain");
        uint32_t n{};
        checked(xrEnumerateSwapchainImages(panelChain, 0, &n, nullptr), "count_panel_images");
        require(n && n <= 32, "invalid_panel_image_count");
        panelImages.assign(n, {XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR});
        checked(xrEnumerateSwapchainImages(panelChain, n, &n, reinterpret_cast<XrSwapchainImageBaseHeader*>(panelImages.data())),
                "read_panel_images");
        panelFormat = *choice;
        panelReady = false;
        AutosportLog("Seat panel swapchain %ux%u format=%u", panelWidth, panelHeight, unsigned(panelFormat));
    }
    if (panelReady && seat.version == panelVersion) return true;
    // GRID2VR's picture (seat_panel.cpp), premultiplied BGRA; swapped to RGBA for an RGBA swapchain.
    std::vector<BYTE> pixels(size_t(panelWidth) * panelHeight * 4);
    if (!AutosportPaintSeatPanel(seat, pixels.data(), panelWidth, panelHeight)) return panelReady;
    if (panelFormat == DXGI_FORMAT_R8G8B8A8_UNORM || panelFormat == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB)
        for (size_t i = 0; i < pixels.size(); i += 4) std::swap(pixels[i], pixels[i + 2]);
    XrSwapchainImageAcquireInfo acquire{XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO};
    uint32_t index{};
    bool written = false;
    if (XR_SUCCEEDED(xrAcquireSwapchainImage(panelChain, &acquire, &index))) {
        const XrSwapchainImageWaitInfo wait{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO, nullptr, 100'000'000};
        if (XR_SUCCEEDED(xrWaitSwapchainImage(panelChain, &wait)) && index < panelImages.size() && panelImages[index].texture) {
            context->UpdateSubresource(panelImages[index].texture, 0, nullptr, pixels.data(), panelWidth * 4, 0);
            gpuUsed = true;
            written = true;
        }
        XrSwapchainImageReleaseInfo release{XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO};
        xrReleaseSwapchainImage(panelChain, &release);
    }
    if (written) { panelReady = true; panelVersion = seat.version; }
    return panelReady;
}

} // namespace

const char* AutosportBridgeStatus() noexcept { return status; }
bool AutosportDrawMirror(ID3D11Texture2D* back) noexcept {
    std::lock_guard<std::mutex> lock(guard);
    if (!bridge || bridge->owner != GetCurrentThreadId() || !back) return false;
    try { bridge->drawMirror(back); return true; }
    catch (...) { return false; }
}
void AutosportRecenterSeatedScreen() noexcept {
    std::lock_guard<std::mutex> lock(guard);
    if (bridge) bridge->screenAnchored = false;
}
bool AutosportTakeRecenterRequest() noexcept {
    std::lock_guard<std::mutex> lock(guard);
    if (!bridge || bridge->owner != GetCurrentThreadId() || !bridge->recenterPending) return false;
    bridge->recenterPending = false;
    return true;
}
double AutosportDisplayPeriodSeconds() noexcept {
    std::lock_guard<std::mutex> lock(guard);
    return bridge && bridge->owner == GetCurrentThreadId() ? bridge->period*1e-9 : 0.0;
}
bool AutosportBeginViews(ID3D11Device* device, ID3D11DeviceContext* context, bool useHeadset,
                         uint64_t frame, BridgeView (&views)[2]) noexcept {
    std::lock_guard<std::mutex> lock(guard);
    if (finishPendingClose() || gameExiting) return false;
    try {
        require(device && context && context->GetType() == D3D11_DEVICE_CONTEXT_IMMEDIATE, "immediate_context_required");
        if (!bridge) {
            ComPtr<ID3D11Device> actual;
            context->GetDevice(&actual);
            require(actual.Get() == device, "device_context_mismatch");
            bridge = new Bridge;
            bridge->device = device; bridge->context = context; bridge->headset = useHeadset;
            if (useHeadset) {
                try { bridge->initXR(); }
                catch (...) { bridge->failed = true; throw; }
            }
        }
        auto& b = *bridge;
        require(b.owner == GetCurrentThreadId(), "wrong_render_thread");
        require(b.device.Get() == device && b.context.Get() == context && b.headset == useHeadset, "binding_changed");
        require(!b.failed, "reinitialize_bridge_after_failure");
        require(!b.seen || frame > b.serial, "nonincreasing_frame");
        if (b.active || b.frameOpen) b.end();
        b.seen = true; b.serial = frame;
        if (useHeadset) {
            try { if (!b.locate()) return false; }
            catch (...) { b.failed = true; throw; }
        } else {
            constexpr float angle = .7853981634f;
            b.views[0] = {{-.032f,0,0},{0,0,0,1},{-angle,angle,angle,-angle}};
            b.views[1] = {{ .032f,0,0},{0,0,0,1},{-angle,angle,angle,-angle}};
            if (AutosportUiComparison() && !AutosportFlatScreen()) {
                // Fixed, tilted reference after the neutral startup pose, for reproducible HUD comparisons.
                const float pitch = -.18f, roll = .19f;
                const float x = std::sin(pitch)*std::cos(roll), y = -std::sin(pitch)*std::sin(roll);
                const float z = std::cos(pitch)*std::sin(roll), w = std::cos(pitch)*std::cos(roll);
                for (auto& view : b.views) {
                    const float localX = view.position[0];
                    view.position[0] = localX*(1-2*(y*y+z*z));
                    view.position[1] = localX*2*(x*y+z*w);
                    view.position[2] = localX*2*(x*z-y*w);
                    view.orientation[0]=x; view.orientation[1]=y; view.orientation[2]=z; view.orientation[3]=w;
                }
            }
        }
        std::copy_n(b.views,2,views);
        b.active = true;
        report(useHeadset ? "located_runtime_views" : "fixed_reference_views");
        return true;
    } catch (const std::exception& error) {
        report(error.what());
        // Once, with the reason: the launcher's progress window shows it (VrLaunchStatus).
        if (bridge && bridge->failed && !bridge->failureLogged) { bridge->failureLogged = true; AutosportLog("VR failed: %s", error.what()); }
        return false;
    }
}

BridgeEndResult AutosportEndViews(ID3D11Texture2D* packed, unsigned subresource, const RECT (&rects)[2],
                                  uint64_t frame, const wchar_t* captureDirectory,
                                  BridgePresentation presentation) noexcept {
    std::lock_guard<std::mutex> lock(guard);
    BridgeEndResult result{};
    if (finishPendingClose()) return result;
    if (!bridge || bridge->owner != GetCurrentThreadId() || !bridge->active || bridge->serial != frame) {
        report("end_frame_identity_mismatch"); return result;
    }
    auto& b = *bridge;
    try {
        const bool flat = presentation == BridgePresentation::FlatScreen;
        const bool saveSource = captureDirectory && (!flat || AutosportUiComparison());
        if (!b.headset && !saveSource) {
            b.end(); report(flat ? "reference_loading_screen" : "reference_frame_ended"); return result;
        }
        PredicateScope unpredicated(b.context.Get());
        D3D11_TEXTURE2D_DESC desc{};
        b.copySource(packed,subresource,rects,(flat && !saveSource) ? 1u : 2u,desc);
        const auto format = typedFormat(desc.Format);
        char dumpFailure[200]{};
        if (saveSource) {
            try {
                WriteEyePair(captureDirectory,b.device.Get(),b.context.Get(),b.snapshot.Get(),format,rects,b.views,
                             b.headset,frame,b.time,subresource,desc);
                result.captured = true;
            } catch (const std::exception& error) { snprintf(dumpFailure,sizeof(dumpFailure),"%s",error.what()); }
        }
        if (b.headset) {
            try { result.submitted = b.submit(rects,format,presentation); }
            catch (...) { b.failed = true; throw; }
        } else b.end();
        if (*dumpFailure) report(dumpFailure);
        else if (result.submitted) report(flat ? "submitted_local_screen" : "submitted_stereo_pair");
        else if (result.captured) report("captured_reference_pair");
    } catch (const std::exception& error) {
        try { b.end(); } catch (...) { b.failed = true; }
        report(error.what());
    }
    return result;
}
void AutosportCancelViews(uint64_t frame) noexcept {
    std::lock_guard<std::mutex> lock(guard);
    if (finishPendingClose()) return;
    if (!bridge || bridge->owner != GetCurrentThreadId() || bridge->serial != frame) return;
    try { bridge->end(); } catch (const std::exception& error) { bridge->failed = true; report(error.what()); }
}
static void requestCloseLocked() noexcept {
    if (!bridge) return;
    bridge->closePending = true;
    if (!finishPendingClose()) {
        report("close_deferred_to_render_thread");
        AutosportLog("XR shutdown deferred render_owner=%lu caller=%lu", bridge->owner, GetCurrentThreadId());
    }
}
void AutosportCloseBridge() noexcept {
    std::lock_guard<std::mutex> lock(guard);
    requestCloseLocked();
}
void AutosportPrepareForGameExit() noexcept {
    std::lock_guard<std::mutex> lock(guard);
    gameExiting = true;
    requestCloseLocked();
}
