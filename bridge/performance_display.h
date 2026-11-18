#pragma once
// Launcher Settings → Diagnostics, as GRID2VR (src/performance_display.*): the frame rate sent to
// the headset and the graphics card's usage, sampled each second. The performance display shows
// them in the headset as a small compositor panel below the view (-gasvrperformance=1); diagnostic
// logging writes them to bridge.log (-gasvrdiagnostics=1). No game shader, render target or HUD
// geometry is changed, and all XR and D3D work stays on the bridge's render thread.
#include <windows.h>
#include <d3d11.h>
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <wrl/client.h>
#include <algorithm>
#include <cstdint>
#include <memory>
#include <vector>

struct GpuCounterState;
// Windows adapter counters (GPU Engine utilization) sampled once a second on a worker thread that
// owns no D3D or XR objects. Stop signals the worker and never waits for it.
class GpuUsageSampler {
public:
    GpuUsageSampler() = default;
    GpuUsageSampler(const GpuUsageSampler&) = delete;
    GpuUsageSampler& operator=(const GpuUsageSampler&) = delete;
    ~GpuUsageSampler() { Stop(); }
    bool Start(ID3D11Device* device);
    // The busiest engine of the game's adapter, all processes included; -1 when unavailable.
    double Percent() const;
    void Stop();
private:
    std::shared_ptr<GpuCounterState> state_;
};

// Frames sent to the headset in each second (the seated screen's included).
class SubmittedFrameRate {
public:
    // True when this frame completes a one-second window.
    bool Frame(uint64_t milliseconds) {
        if (!started_ || milliseconds < start_ || milliseconds - start_ > 3000) { started_ = true; start_ = milliseconds; count_ = 0; return false; }
        ++count_;
        const auto elapsed = milliseconds - start_;
        if (elapsed < 1000) return false;
        fps_ = double(count_) * 1000 / double(elapsed); count_ = 0; start_ = milliseconds;
        return true;
    }
    double Fps() const { return fps_; }
private:
    uint64_t start_{}, count_{};
    double fps_{-1};
    bool started_{};
};

class PerformanceDisplay {
public:
    static constexpr unsigned Width = 640, Height = 64;
    // A swapchain of the bridge's session; false (and nothing shown) when it cannot be made.
    bool Initialize(XrSession session, XrSpace head, ID3D11Device* device, ID3D11DeviceContext* context,
                    const std::vector<int64_t>& formats, float hudDistance);
    // The panel for this frame, redrawn once a second; null until its first picture is ready.
    const XrCompositionLayerQuad* Layer(uint64_t milliseconds, double fps, double displayHz, double gpu, bool& gpuUsed);
    bool Shutdown();
    bool Enabled() const { return enabled_; }
    unsigned Updates() const { return updates_; }
    // The panel stays nearer than the stereo HUD's plane (it is composited over the eye images,
    // so behind the HUD it would cover HUD drawn in front of it): at most 95% of the HUD distance,
    // scaled so it keeps its angular size. As GRID2VR.
    static float Scale(float hudDistance, float panelDistance) {
        return hudDistance > 0 && hudDistance < 1e4f && panelDistance > 0 ? (std::min)(1.f, .95f * hudDistance / panelDistance) : 1.f;
    }
    // The picture as RGBA bytes (Width x Height): "FPS 89.9/90  GPU 72%", amber while below 95% of the rate.
    static std::vector<uint32_t> Pixels(double fps, double displayHz, double gpu);
private:
    XrSwapchain chain_{};
    XrCompositionLayerQuad layer_{XR_TYPE_COMPOSITION_LAYER_QUAD};
    std::vector<XrSwapchainImageD3D11KHR> images_;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> context_;
    uint64_t updated_{};
    uint32_t index_{};
    unsigned updates_{};
    bool bgra_{}, enabled_{}, ready_{}, acquired_{}, waited_{};
    bool upload(uint64_t milliseconds, double fps, double displayHz, double gpu, bool& gpuUsed);
};
