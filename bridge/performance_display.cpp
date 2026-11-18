#include "performance_display.h"
#include <dxgi.h>
#include <pdh.h>
#include <pdhmsg.h>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <map>
#include <string>

using Microsoft::WRL::ComPtr;

struct GpuCounterState {
    HANDLE stop{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    std::atomic<double> percent{-1};
    std::wstring adapter;
    ~GpuCounterState() { if (stop) CloseHandle(stop); }
};

namespace {
struct GpuWorker { std::shared_ptr<GpuCounterState> state; HMODULE module; };

// Adapter-wide usage, as GRID2VR: processes sharing one engine are summed, then the busiest engine
// is taken (summing different engines would pass 100%). Instance names look like
// "pid_1234_luid_0x00000000_0x0000C3F1_phys_0_eng_0_engtype_3D".
struct GpuEngineUsage {
    std::map<std::wstring, double> totals;
    void add(const wchar_t* name, const std::wstring& adapter, double percent, bool valid) {
        if (!valid || !std::isfinite(percent) || percent < 0) return;
        // Windows may spell the LUID's hex digits in either case ("0x00019F0A"); adapter is lower case.
        std::wstring text(name);
        for (auto& c : text) c = towlower(c);
        const auto start = text.find(adapter);
        if (start == std::wstring::npos) return;
        const auto key = text.substr(start + adapter.size());
        if (key.compare(0, 6, L"_phys_") != 0 || key.find(L"_eng_") == std::wstring::npos) return;
        totals[key] += percent;
    }
    double percent() const {
        if (totals.empty()) return -1;
        double busiest{};
        for (const auto& entry : totals) busiest = (std::max)(busiest, entry.second);
        return (std::min)(busiest, 100.);
    }
};

void collectGpu(GpuCounterState& self) {
    PDH_HQUERY query{};
    PDH_HCOUNTER counter{};
    if (PdhOpenQueryW(nullptr, 0, &query) != ERROR_SUCCESS) return;
    if (PdhAddEnglishCounterW(query, L"\\GPU Engine(*)\\Utilization Percentage", 0, &counter) != ERROR_SUCCESS) { PdhCloseQuery(query); return; }
    PdhCollectQueryData(query);
    try {
        while (WaitForSingleObject(self.stop, 1000) == WAIT_TIMEOUT) {
            double value = -1;
            DWORD bytes{}, count{};
            if (PdhCollectQueryData(query) == ERROR_SUCCESS &&
                PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, &bytes, &count, nullptr) == PDH_MORE_DATA && bytes <= 4 * 1024 * 1024) {
                std::vector<unsigned char> storage(bytes);
                auto* items = reinterpret_cast<PDH_FMT_COUNTERVALUE_ITEM_W*>(storage.data());
                if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, &bytes, &count, items) == ERROR_SUCCESS &&
                    count <= storage.size() / sizeof(*items)) {
                    GpuEngineUsage usage;
                    for (DWORD i = 0; i < count; ++i)
                        if (items[i].szName)
                            usage.add(items[i].szName, self.adapter, items[i].FmtValue.doubleValue,
                                      items[i].FmtValue.CStatus == PDH_CSTATUS_VALID_DATA || items[i].FmtValue.CStatus == PDH_CSTATUS_NEW_DATA);
                    value = usage.percent();
                }
            }
            self.percent.store(value);
        }
    } catch (...) { self.percent.store(-1); }
    PdhCloseQuery(query);
}

DWORD WINAPI sampleGpu(void* raw) {
    auto* worker = static_cast<GpuWorker*>(raw);
    const auto module = worker->module;
    collectGpu(*worker->state);
    delete worker;
    // Keeps this DLL loaded until the worker is done, without making the game's exit wait for a
    // slow Windows performance provider.
    FreeLibraryAndExitThread(module, 0);
}

// A 5x7 bitmap font, as GRID2VR: independent of desktop DPI and font services.
std::array<unsigned char, 7> glyph(char c) {
    switch (c) {
    case '0': return {14,17,19,21,25,17,14}; case '1': return {4,12,4,4,4,4,14};
    case '2': return {14,17,1,2,4,8,31};     case '3': return {30,1,1,14,1,1,30};
    case '4': return {2,6,10,18,31,2,2};     case '5': return {31,16,16,30,1,1,30};
    case '6': return {14,16,16,30,17,17,14}; case '7': return {31,1,2,4,8,8,8};
    case '8': return {14,17,17,14,17,17,14}; case '9': return {14,17,17,15,1,1,14};
    case 'F': return {31,16,16,30,16,16,16}; case 'P': return {30,17,17,30,16,16,16};
    case 'S': return {15,16,16,14,1,1,30};   case 'G': return {14,17,16,23,17,17,14};
    case 'U': return {17,17,17,17,17,17,14}; case 'N': return {17,25,25,21,19,19,17};
    case 'A': return {14,17,17,31,17,17,17}; case '/': return {1,2,2,4,8,8,16};
    case '.': return {0,0,0,0,0,12,12};      case '%': return {25,25,2,4,8,19,19};
    default: return {};
    }
}
// RGBA bytes in memory (0xAABBGGRR).
constexpr uint32_t background = 0xff181818, accent = 0xff3030df, text = 0xffeeeeee, dipping = 0xff20b0ff;
}

bool GpuUsageSampler::Start(ID3D11Device* device) {
    if (state_ || !device) return false;
    ComPtr<IDXGIDevice> dxgi;
    ComPtr<IDXGIAdapter> adapter;
    DXGI_ADAPTER_DESC desc{};
    if (FAILED(device->QueryInterface(IID_PPV_ARGS(&dxgi))) || FAILED(dxgi->GetAdapter(&adapter)) || FAILED(adapter->GetDesc(&desc))) return false;
    wchar_t name[64]{};
    swprintf_s(name, L"luid_0x%08x_0x%08x", unsigned(desc.AdapterLuid.HighPart), unsigned(desc.AdapterLuid.LowPart));
    auto state = std::make_shared<GpuCounterState>();
    state->adapter = name;
    HMODULE module{};
    if (!state->stop || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(&sampleGpu), &module)) return false;
    auto* worker = new GpuWorker{state, module};
    const auto handle = CreateThread(nullptr, 0, sampleGpu, worker, 0, nullptr);
    if (!handle) { delete worker; FreeLibrary(module); return false; }
    CloseHandle(handle);
    state_ = std::move(state);
    return true;
}
double GpuUsageSampler::Percent() const { return state_ ? state_->percent.load() : -1.; }
void GpuUsageSampler::Stop() { if (state_) { SetEvent(state_->stop); state_.reset(); } }

std::vector<uint32_t> PerformanceDisplay::Pixels(double fps, double displayHz, double gpu) {
    std::vector<uint32_t> pixels(size_t(Width) * Height, background);
    for (unsigned y = 0; y < Height; ++y) for (unsigned x = 0; x < 4; ++x) pixels[y * Width + x] = accent;
    char fpsText[32], rateText[16], gpuText[16], line[64];
    if (fps < 0) strcpy_s(fpsText, "N/A"); else sprintf_s(fpsText, "%.1f", fps);
    if (displayHz > 0) sprintf_s(rateText, "/%.0f", displayHz); else rateText[0] = 0;
    if (gpu < 0) strcpy_s(gpuText, "N/A"); else sprintf_s(gpuText, "%.0f%%", gpu);
    sprintf_s(line, "FPS %s%s  GPU %s", fpsText, rateText, gpuText);
    const size_t fpsEnd = 4 + strlen(fpsText) + strlen(rateText);
    const bool dip = fps >= 0 && displayHz > 0 && fps < .95 * displayHz;
    unsigned left = 16;
    for (size_t i = 0; line[i] && left + 20 < Width; ++i, left += 24) {
        const auto rows = glyph(line[i]);
        const uint32_t color = dip && i < fpsEnd ? dipping : text;
        for (unsigned y = 0; y < 7; ++y) for (unsigned x = 0; x < 5; ++x)
            if (rows[y] & (1u << (4 - x)))
                for (unsigned dy = 0; dy < 4; ++dy) for (unsigned dx = 0; dx < 4; ++dx)
                    pixels[(18 + y * 4 + dy) * Width + left + x * 4 + dx] = color;
    }
    return pixels;
}

bool PerformanceDisplay::Initialize(XrSession session, XrSpace head, ID3D11Device* device, ID3D11DeviceContext* context,
                                    const std::vector<int64_t>& formats, float hudDistance) {
    if (chain_ || !session || !head || !device || !context) return false;
    constexpr DXGI_FORMAT wanted[] = {DXGI_FORMAT_R8G8B8A8_UNORM_SRGB, DXGI_FORMAT_B8G8R8A8_UNORM_SRGB,
                                      DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_B8G8R8A8_UNORM};
    const auto choice = std::find_if(std::begin(wanted), std::end(wanted), [&](DXGI_FORMAT f) {
        return std::find(formats.begin(), formats.end(), int64_t(f)) != formats.end(); });
    if (choice == std::end(wanted)) return false;
    bgra_ = *choice == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB || *choice == DXGI_FORMAT_B8G8R8A8_UNORM;
    XrSwapchainCreateInfo create{XR_TYPE_SWAPCHAIN_CREATE_INFO};
    create.format = *choice;
    create.usageFlags = XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT | XR_SWAPCHAIN_USAGE_SAMPLED_BIT;
    create.sampleCount = create.faceCount = create.arraySize = create.mipCount = 1;
    create.width = Width; create.height = Height;
    if (xrCreateSwapchain(session, &create, &chain_) != XR_SUCCESS) { chain_ = XR_NULL_HANDLE; return false; }
    uint32_t count{};
    if (xrEnumerateSwapchainImages(chain_, 0, &count, nullptr) != XR_SUCCESS || !count || count > 16) { Shutdown(); return false; }
    images_.assign(count, {XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR});
    if (xrEnumerateSwapchainImages(chain_, count, &count, reinterpret_cast<XrSwapchainImageBaseHeader*>(images_.data())) != XR_SUCCESS ||
        count != images_.size()) { Shutdown(); return false; }
    for (const auto& image : images_) if (!image.texture) { Shutdown(); return false; }
    context_ = context;
    // Follows the view: 1.5 m ahead and below the centre, or nearer than the HUD plane.
    const float scale = Scale(hudDistance, 1.5f);
    layer_.space = head;
    layer_.eyeVisibility = XR_EYE_VISIBILITY_BOTH;
    layer_.subImage.swapchain = chain_;
    layer_.subImage.imageRect.extent = {int32_t(Width), int32_t(Height)};
    layer_.pose.orientation.w = 1;
    layer_.pose.position = {0, -.45f * scale, -1.5f * scale};
    layer_.size = {.75f * scale, .075f * scale};
    enabled_ = true;
    return true;
}

bool PerformanceDisplay::upload(uint64_t now, double fps, double displayHz, double gpu, bool& gpuUsed) {
    if (ready_ && now - updated_ < 1000) return true;
    if (!acquired_) {
        const XrSwapchainImageAcquireInfo acquire{XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO};
        if (xrAcquireSwapchainImage(chain_, &acquire, &index_) != XR_SUCCESS) return false;
        acquired_ = true;
    }
    // An optional meter must not stall rendering: an image still in use is retried next frame,
    // and is never released before a successful wait.
    if (!waited_) {
        const XrSwapchainImageWaitInfo wait{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO, nullptr, 0};
        const auto result = xrWaitSwapchainImage(chain_, &wait);
        if (result == XR_TIMEOUT_EXPIRED) return true;
        if (result != XR_SUCCESS) return false;
        waited_ = true;
    }
    if (index_ >= images_.size()) return false;
    auto pixels = Pixels(fps, displayHz, gpu);
    if (bgra_) for (auto& p : pixels) p = (p & 0xff00ff00) | ((p & 0xff) << 16) | ((p >> 16) & 0xff);
    ComPtr<ID3D11Predicate> predicate;
    BOOL predicateValue{};
    context_->GetPredication(&predicate, &predicateValue);
    context_->SetPredication(nullptr, FALSE);
    context_->UpdateSubresource(images_[index_].texture, 0, nullptr, pixels.data(), Width * 4, 0);
    context_->SetPredication(predicate.Get(), predicateValue);
    gpuUsed = true;
    const XrSwapchainImageReleaseInfo release{XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO};
    if (xrReleaseSwapchainImage(chain_, &release) != XR_SUCCESS) return false;
    acquired_ = waited_ = false;
    updated_ = now; ready_ = true; ++updates_;
    return true;
}

const XrCompositionLayerQuad* PerformanceDisplay::Layer(uint64_t now, double fps, double displayHz, double gpu, bool& gpuUsed) {
    if (!enabled_) return nullptr;
    if (!upload(now, fps, displayHz, gpu, gpuUsed)) { enabled_ = false; return nullptr; }
    return ready_ ? &layer_ : nullptr;
}

bool PerformanceDisplay::Shutdown() {
    enabled_ = false;
    if (!chain_) return true;
    if (acquired_) {
        const XrSwapchainImageWaitInfo wait{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO, nullptr, 100'000'000};
        if (waited_ || xrWaitSwapchainImage(chain_, &wait) == XR_SUCCESS) {
            const XrSwapchainImageReleaseInfo release{XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO};
            xrReleaseSwapchainImage(chain_, &release);
        }
        acquired_ = waited_ = false;
    }
    const bool destroyed = xrDestroySwapchain(chain_) == XR_SUCCESS;
    chain_ = XR_NULL_HANDLE; images_.clear(); context_.Reset(); ready_ = false;
    return destroyed;
}
