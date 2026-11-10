// Standalone, Win32-only OpenXR diagnostic for GRID Autosport.
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <algorithm>
#include <array>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cwchar>
#include <io.h>
#include <stdexcept>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;
using Clock = std::chrono::steady_clock;
static_assert(sizeof(void*) == 4, "This probe targets the Win32 loader");

namespace {
void check(XrResult result, const char* operation) {
    if (result != XR_SUCCESS) {
        std::fprintf(stderr, "%s: XrResult=%d\n", operation, result);
        throw std::runtime_error(operation);
    }
}
void check(HRESULT result, const char* operation) {
    if (FAILED(result)) {
        std::fprintf(stderr, "%s: HRESULT=0x%08lx\n", operation, result);
        throw std::runtime_error(operation);
    }
}

struct Eye {
    XrSwapchain swapchain{};
    int width{}, height{};
    std::vector<XrSwapchainImageD3D11KHR> images;
    std::vector<ComPtr<ID3D11RenderTargetView>> renderTargets;
    std::vector<unsigned> marker;
    UINT markerSize{};
};

struct Probe {
    XrInstance instance{};
    XrSystemId system{};
    XrSession session{};
    XrSpace space{};
    std::array<XrViewConfigurationView, 2> sizes{};
    std::array<Eye, 2> eyes{};
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    bool clean = true;

    void destroyed(XrResult result, const char* name) {
        std::printf("destroy_%s=%d\n", name, result);
        clean = clean && result == XR_SUCCESS;
    }
    void close() {
        for (auto& eye : eyes) {
            eye.renderTargets.clear();
            if (eye.swapchain) destroyed(xrDestroySwapchain(eye.swapchain), "swapchain");
            eye.swapchain = XR_NULL_HANDLE;
        }
        if (space) destroyed(xrDestroySpace(space), "space");
        space = XR_NULL_HANDLE;
        if (session) destroyed(xrDestroySession(session), "session");
        session = XR_NULL_HANDLE;
        if (instance) destroyed(xrDestroyInstance(instance), "instance");
        instance = XR_NULL_HANDLE;
    }
    ~Probe() { close(); }

    bool discover() {
        uint32_t count{};
        check(xrEnumerateInstanceExtensionProperties(nullptr, 0, &count, nullptr), "count_extensions");
        std::vector<XrExtensionProperties> extensions(count, {XR_TYPE_EXTENSION_PROPERTIES});
        check(xrEnumerateInstanceExtensionProperties(nullptr, count, &count, extensions.data()), "read_extensions");
        const char* extension = XR_KHR_D3D11_ENABLE_EXTENSION_NAME;
        const bool hasD3d = std::any_of(extensions.begin(), extensions.end(), [extension](const auto& item) {
            return std::strcmp(item.extensionName, extension) == 0;
        });
        std::printf("%s=%u\n", extension, unsigned(hasD3d));
        if (!hasD3d) throw std::runtime_error("missing D3D11 extension");

        XrInstanceCreateInfo create{XR_TYPE_INSTANCE_CREATE_INFO};
        strcpy_s(create.applicationInfo.applicationName, "GRID Autosport VR probe");
        create.applicationInfo.apiVersion = XR_MAKE_VERSION(1, 0, 0);
        create.enabledExtensionCount = 1;
        create.enabledExtensionNames = &extension;
        check(xrCreateInstance(&create, &instance), "xrCreateInstance");
        XrInstanceProperties runtime{XR_TYPE_INSTANCE_PROPERTIES};
        check(xrGetInstanceProperties(instance, &runtime), "xrGetInstanceProperties");
        std::printf("runtime=%s version=%u.%u.%u\n", runtime.runtimeName, unsigned(XR_VERSION_MAJOR(runtime.runtimeVersion)),
                    unsigned(XR_VERSION_MINOR(runtime.runtimeVersion)), unsigned(XR_VERSION_PATCH(runtime.runtimeVersion)));
        XrSystemGetInfo get{XR_TYPE_SYSTEM_GET_INFO};
        get.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
        const XrResult systemResult = xrGetSystem(instance, &get, &system);
        if (systemResult == XR_ERROR_FORM_FACTOR_UNAVAILABLE) {
            std::puts("headset=unavailable result=XR_ERROR_FORM_FACTOR_UNAVAILABLE");
            return false;
        }
        check(systemResult, "xrGetSystem");
        XrSystemProperties properties{XR_TYPE_SYSTEM_PROPERTIES};
        check(xrGetSystemProperties(instance, system, &properties), "xrGetSystemProperties");
        std::printf("headset=%s position_tracking=%u orientation_tracking=%u\n", properties.systemName,
                    properties.trackingProperties.positionTracking, properties.trackingProperties.orientationTracking);
        check(xrEnumerateViewConfigurationViews(instance, system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO,
                                                 0, &count, nullptr), "count_stereo_views");
        if (count != sizes.size()) throw std::runtime_error("expected two primary stereo views");
        sizes.fill({XR_TYPE_VIEW_CONFIGURATION_VIEW});
        check(xrEnumerateViewConfigurationViews(instance, system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO,
                                                 UINT(sizes.size()), &count, sizes.data()), "read_stereo_views");
        for (unsigned i = 0; i < sizes.size(); ++i) {
            std::printf("eye%u=%ux%u recommended_samples=%u\n", i, sizes[i].recommendedImageRectWidth,
                        sizes[i].recommendedImageRectHeight, sizes[i].recommendedSwapchainSampleCount);
        }
        PFN_xrGetD3D11GraphicsRequirementsKHR getRequirements{};
        check(xrGetInstanceProcAddr(instance, "xrGetD3D11GraphicsRequirementsKHR",
                                    reinterpret_cast<PFN_xrVoidFunction*>(&getRequirements)), "lookup_d3d_requirements");
        XrGraphicsRequirementsD3D11KHR requirements{XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR};
        check(getRequirements(instance, system, &requirements), "get_d3d_requirements");
        const auto required = requirements.adapterLuid;
        std::printf("required_adapter_luid=%08lx:%08lx minimum_feature_level=0x%x\n",
                    DWORD(required.HighPart), required.LowPart, unsigned(requirements.minFeatureLevel));
        ComPtr<IDXGIFactory1> factory;
        check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "CreateDXGIFactory1");
        ComPtr<IDXGIAdapter1> adapter;
        DXGI_ADAPTER_DESC1 desc{};
        for (UINT index = 0;; ++index) {
            const auto status = factory->EnumAdapters1(index, adapter.ReleaseAndGetAddressOf());
            if (status == DXGI_ERROR_NOT_FOUND) throw std::runtime_error("required DXGI adapter unavailable");
            check(status, "EnumAdapters1");
            check(adapter->GetDesc1(&desc), "GetDesc1");
            if (desc.AdapterLuid.LowPart == required.LowPart && desc.AdapterLuid.HighPart == required.HighPart) break;
            if (!required.LowPart && !required.HighPart && !(desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE)) {
                std::puts("runtime_adapter_unspecified=1");
                break;
            }
        }
        D3D_FEATURE_LEVEL actual{};
        constexpr D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_12_1, D3D_FEATURE_LEVEL_12_0,
                                                D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0,
                                                D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0};
        check(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, levels, ARRAYSIZE(levels),
                                D3D11_SDK_VERSION, &device, &actual, &context), "D3D11CreateDevice");
        std::printf("device=%ls adapter_luid=%08lx:%08lx feature_level=0x%x\n", desc.Description,
                    DWORD(desc.AdapterLuid.HighPart), desc.AdapterLuid.LowPart, unsigned(actual));
        if (actual < requirements.minFeatureLevel) throw std::runtime_error("device feature level below runtime minimum");
        return true;
    }
    void openSession();
    void paint(unsigned index);
    bool runSession(int seconds);
};

void Probe::openSession() {
    XrGraphicsBindingD3D11KHR binding{XR_TYPE_GRAPHICS_BINDING_D3D11_KHR};
    binding.device = device.Get();
    XrSessionCreateInfo create{XR_TYPE_SESSION_CREATE_INFO, &binding};
    create.systemId = system;
    check(xrCreateSession(instance, &create, &session), "xrCreateSession");
    XrReferenceSpaceCreateInfo ref{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
    ref.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
    ref.poseInReferenceSpace.orientation.w = 1.f;
    check(xrCreateReferenceSpace(session, &ref, &space), "xrCreateReferenceSpace");
    uint32_t count{};
    check(xrEnumerateSwapchainFormats(session, 0, &count, nullptr), "count_formats");
    std::vector<int64_t> formats(count);
    check(xrEnumerateSwapchainFormats(session, count, &count, formats.data()), "read_formats");
    const auto chosen = std::find_if(formats.begin(), formats.end(), [](int64_t f) {
        return f == DXGI_FORMAT_R8G8B8A8_UNORM || f == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB ||
               f == DXGI_FORMAT_B8G8R8A8_UNORM || f == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
    });
    if (chosen == formats.end()) throw std::runtime_error("no supported RGBA8/BGRA8 eye format");
    std::printf("eye_format=%lld sample_count=1\n", *chosen);
    constexpr const char* glyphs[][7] = {
        {"10000", "10000", "10000", "10000", "10000", "10000", "11111"},
        {"11110", "10001", "10001", "11110", "10100", "10010", "10001"}
    };
    for (unsigned i = 0; i < eyes.size(); ++i) {
        Eye& eye = eyes[i];
        eye.width = int(sizes[i].recommendedImageRectWidth);
        eye.height = int(sizes[i].recommendedImageRectHeight);
        XrSwapchainCreateInfo sc{XR_TYPE_SWAPCHAIN_CREATE_INFO};
        sc.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT;
        sc.format = *chosen;
        sc.sampleCount = sc.faceCount = sc.arraySize = sc.mipCount = 1;
        sc.width = eye.width;
        sc.height = eye.height;
        check(xrCreateSwapchain(session, &sc, &eye.swapchain), "xrCreateSwapchain");
        check(xrEnumerateSwapchainImages(eye.swapchain, 0, &count, nullptr), "count_eye_images");
        eye.images.resize(count, {XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR});
        check(xrEnumerateSwapchainImages(eye.swapchain, count, &count,
              reinterpret_cast<XrSwapchainImageBaseHeader*>(eye.images.data())), "read_eye_images");
        D3D11_RENDER_TARGET_VIEW_DESC rtv{};
        rtv.Format = DXGI_FORMAT(*chosen);
        rtv.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
        eye.renderTargets.resize(count);
        for (unsigned n = 0; n < count; ++n)
            check(device->CreateRenderTargetView(eye.images[n].texture, &rtv, &eye.renderTargets[n]), "create_eye_rtv");
        eye.markerSize = UINT(std::max(1, std::min(eye.width, eye.height) / 5));
        eye.marker.resize(size_t(eye.markerSize) * eye.markerSize);
        for (UINT y = 0; y < eye.markerSize; ++y) {
            for (UINT x = 0; x < eye.markerSize; ++x) {
                const UINT gx = x * 7 / eye.markerSize, gy = y * 9 / eye.markerSize;
                const bool stroke = gx >= 1 && gx <= 5 && gy >= 1 && gy <= 7 && glyphs[i][gy-1][gx-1] == '1';
                eye.marker[size_t(y) * eye.markerSize + x] = stroke ? 0xffeeeeeeu : 0xff333333u;
            }
        }
    }
}

void Probe::paint(unsigned index) {
    Eye& eye = eyes[index];
    uint32_t image{};
    XrSwapchainImageAcquireInfo acquire{XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO};
    check(xrAcquireSwapchainImage(eye.swapchain, &acquire, &image), "xrAcquireSwapchainImage");
    XrSwapchainImageWaitInfo wait{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO};
    wait.timeout = 2'000'000'000;
    check(xrWaitSwapchainImage(eye.swapchain, &wait), "xrWaitSwapchainImage");
    const float background[] = {0.06f, 0.07f, 0.08f, 1.f};
    context->ClearRenderTargetView(eye.renderTargets.at(image).Get(), background);
    UINT x = (eye.width - eye.markerSize) / 2, y = (eye.height - eye.markerSize) / 2;
    D3D11_BOX box{x, y, 0, x + eye.markerSize, y + eye.markerSize, 1};
    context->UpdateSubresource(eye.images.at(image).texture, 0, &box, eye.marker.data(), eye.markerSize * 4, 0);
    XrSwapchainImageReleaseInfo release{XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO};
    check(xrReleaseSwapchainImage(eye.swapchain, &release), "xrReleaseSwapchainImage");
}

bool Probe::runSession(int seconds) {
    openSession();
    uint32_t count{};
    check(xrEnumerateEnvironmentBlendModes(instance, system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO,
                                            0, &count, nullptr), "count_blend_modes");
    std::vector<XrEnvironmentBlendMode> modes(count);
    check(xrEnumerateEnvironmentBlendModes(instance, system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO,
                                            count, &count, modes.data()), "read_blend_modes");
    if (std::find(modes.begin(), modes.end(), XR_ENVIRONMENT_BLEND_MODE_OPAQUE) == modes.end())
        throw std::runtime_error("opaque stereo unavailable");
    unsigned frames{}, submitted{}, visible{}, focused{};
    bool running = false, requestedExit = false, stopped = false, exited = false, lost = false;
    XrSessionState state = XR_SESSION_STATE_UNKNOWN;
    const auto deadline = Clock::now() + std::chrono::seconds(seconds);
    while (!exited) {
        XrEventDataBuffer event{XR_TYPE_EVENT_DATA_BUFFER};
        for (;;) {
            auto result = xrPollEvent(instance, &event);
            if (result == XR_EVENT_UNAVAILABLE) break;
            check(result, "xrPollEvent");
            if (event.type == XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING)
                throw std::runtime_error("OpenXR instance loss pending");
            if (event.type == XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) {
                const auto& changed = reinterpret_cast<const XrEventDataSessionStateChanged&>(event);
                if (changed.session != session) throw std::runtime_error("unexpected session");
                state = changed.state;
                constexpr const char* labels[] = {"UNKNOWN", "IDLE", "READY", "SYNCHRONIZED", "VISIBLE",
                                                   "FOCUSED", "STOPPING", "LOSS_PENDING", "EXITING"};
                const char* stateName = unsigned(state) < ARRAYSIZE(labels) ? labels[unsigned(state)] : "OTHER";
                std::printf("session_state=%s value=%d\n", stateName, state);
                if (state == XR_SESSION_STATE_READY) {
                    XrSessionBeginInfo begin{XR_TYPE_SESSION_BEGIN_INFO};
                    begin.primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
                    check(xrBeginSession(session, &begin), "xrBeginSession");
                    running = true;
                } else if (state == XR_SESSION_STATE_STOPPING && running) {
                    check(xrEndSession(session), "xrEndSession");
                    running = false;
                    stopped = true;
                } else if (state == XR_SESSION_STATE_EXITING || state == XR_SESSION_STATE_LOSS_PENDING) {
                    lost = state == XR_SESSION_STATE_LOSS_PENDING;
                    exited = true;
                }
            }
            event = {XR_TYPE_EVENT_DATA_BUFFER};
        }
        if (exited) break;
        const auto now = Clock::now();
        if (now >= deadline) {
            if (!running || now >= deadline + std::chrono::seconds(4)) break;
            if (!requestedExit) {
                check(xrRequestExitSession(session), "xrRequestExitSession");
                requestedExit = true;
            }
        }
        if (!running) {
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
            continue;
        }
        XrFrameWaitInfo wait{XR_TYPE_FRAME_WAIT_INFO};
        XrFrameState frame{XR_TYPE_FRAME_STATE};
        check(xrWaitFrame(session, &wait, &frame), "xrWaitFrame");
        XrFrameBeginInfo begin{XR_TYPE_FRAME_BEGIN_INFO};
        const auto beginResult = xrBeginFrame(session, &begin);
        if (beginResult != XR_FRAME_DISCARDED) check(beginResult, "xrBeginFrame");
        XrFrameEndInfo end{XR_TYPE_FRAME_END_INFO};
        end.displayTime = frame.predictedDisplayTime;
        end.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
        XrCompositionLayerProjection layer{XR_TYPE_COMPOSITION_LAYER_PROJECTION};
        std::array<XrCompositionLayerProjectionView, 2> projection{};
        const auto header = reinterpret_cast<const XrCompositionLayerBaseHeader*>(&layer);
        if (frame.shouldRender && beginResult != XR_FRAME_DISCARDED && !requestedExit) {
            XrViewLocateInfo locate{XR_TYPE_VIEW_LOCATE_INFO};
            locate.viewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
            locate.displayTime = frame.predictedDisplayTime;
            locate.space = space;
            std::array<XrView, 2> views{};
            views.fill({XR_TYPE_VIEW});
            XrViewState viewState{XR_TYPE_VIEW_STATE};
            check(xrLocateViews(session, &locate, &viewState, UINT(views.size()), &count, views.data()), "xrLocateViews");
            constexpr auto valid = XR_VIEW_STATE_ORIENTATION_VALID_BIT | XR_VIEW_STATE_POSITION_VALID_BIT;
            if (count == views.size() && (viewState.viewStateFlags & valid) == valid) {
                for (unsigned i = 0; i < eyes.size(); ++i) {
                    paint(i);
                    auto& view = projection[i];
                    view.type = XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW;
                    view.pose = views[i].pose;
                    view.fov = views[i].fov;
                    view.subImage.swapchain = eyes[i].swapchain;
                    view.subImage.imageRect.extent = {eyes[i].width, eyes[i].height};
                }
                layer.space = space;
                layer.viewCount = unsigned(projection.size());
                layer.views = projection.data();
                end.layerCount = 1;
                end.layers = &header;
                if (!submitted) {
                    std::printf("first_pair_frame=%u predicted_time=%lld left_x=%.4f right_x=%.4f pattern=L/R\n",
                                frames, frame.predictedDisplayTime, views[0].pose.position.x, views[1].pose.position.x);
                }
            }
        }
        check(xrEndFrame(session, &end), "xrEndFrame");
        ++frames;
        if (end.layerCount) {
            ++submitted;
            if (state == XR_SESSION_STATE_VISIBLE || state == XR_SESSION_STATE_FOCUSED) ++visible;
            if (state == XR_SESSION_STATE_FOCUSED) ++focused;
        }
    }
    std::printf("frames=%u submitted_pairs=%u visible_pairs=%u focused_pairs=%u session_ended=%u session_lost=%u\n",
                frames, submitted, visible, focused, unsigned(stopped), unsigned(lost));
    return visible > 0 && stopped && !lost;
}

void usage() {
    std::puts("AutosportXRProbe (Win32 D3D11 OpenXR)\n"
              "  --query | --session [--seconds 1..120] [--log PATH]\n"
              "Query reports the selected OpenXR runtime, HMD, stereo sizes and required GPU.\n"
              "Session displays a left-eye L and right-eye R. Close each eye to check the marker.\n"
              "Exit 0 = query succeeded or visible pairs submitted and session ended, 2 = no HMD,\n"
              "1 = failure, 64 = invalid arguments, 124 = runtime call timed out.");
}
} // namespace

int wmain(int argc, wchar_t** argv) {
    if (argc == 2 && !wcscmp(argv[1], L"--help")) { usage(); return 0; }
    const bool session = argc >= 2 && !wcscmp(argv[1], L"--session");
    if (!session && (argc < 2 || wcscmp(argv[1], L"--query"))) { usage(); return 64; }
    const wchar_t* log = nullptr;
    int seconds = 20;
    for (int i = 2; i < argc; ++i) {
        if (session && !wcscmp(argv[i], L"--seconds") && i+1 < argc) {
            wchar_t* end = nullptr;
            const long parsed = wcstol(argv[++i], &end, 10);
            if (end == argv[i] || *end || parsed < 1 || parsed > 120) { usage(); return 64; }
            seconds = int(parsed);
        } else if (!wcscmp(argv[i], L"--log") && i+1 < argc && !log) {
            log = argv[++i];
        } else { usage(); return 64; }
    }
    if (log) {
        FILE* redirected{};
        if (_wfreopen_s(&redirected, log, L"w", stdout)) { std::perror("open log"); return 1; }
        if (_dup2(_fileno(stdout), _fileno(stderr)) != 0) { std::perror("redirect stderr"); return 1; }
    }
    setvbuf(stdout, nullptr, _IONBF, 0);
    setvbuf(stderr, nullptr, _IONBF, 0);
    std::printf("architecture=Win32 mode=%s\n", session ? "session" : "query");
    char* overrideManifest{};
    size_t manifestLength{};
    _dupenv_s(&overrideManifest, &manifestLength, "XR_RUNTIME_JSON");
    std::printf("XR_RUNTIME_JSON=%s\n", overrideManifest ? overrideManifest : "(unset; system loader discovery)");
    std::free(overrideManifest);
    const unsigned limit = (session ? seconds : 0) + 35;
    HANDLE finished = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!finished) return 1;
    std::thread watchdog([finished, limit] {
        if (WaitForSingleObject(finished, limit*1000) == WAIT_TIMEOUT) {
            std::fprintf(stderr, "hard_timeout_seconds=%u clean_shutdown=unconfirmed\n", limit);
            std::_Exit(124);
        }
    });
    int code = 1;
    try {
        Probe probe;
        if (!probe.discover()) code = 2;
        else if (!session || probe.runSession(seconds)) code = 0;
        probe.close();
        if (!probe.clean) code = 1;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "error=%s\n", error.what());
    }
    std::printf("exit_code=%d\n", code);
    SetEvent(finished);
    watchdog.join();
    CloseHandle(finished);
    return code;
}
