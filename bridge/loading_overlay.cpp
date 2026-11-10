#include "loading_overlay.h"
#include "bridge_api.h"
#include <atomic>

namespace {
using MakeVisibility = void* (__thiscall*)(void*, unsigned int);
using DrawFrontend = void (__cdecl*)(void*, void*, void*, unsigned int, unsigned int);
MakeVisibility makeVisibility;
DrawFrontend drawFrontend;

// These two per-slate calls bracket actual native visibility requests. The FE may issue
// its draw on another thread. Begin/Finish and the capture state belong to the native
// frame owner.
std::atomic<unsigned> visibilityRevision{0};
std::atomic<bool> slateVisible{false};
std::atomic<bool> slateSuppressed{false};
std::atomic<ULONGLONG> slateShowTick{0};
enum class Sample { None, Original, WithoutFrontend, Production };
std::atomic<Sample> sample{Sample::None};
std::atomic<unsigned> draws{0};
uint64_t sampleFrame;
unsigned sampleRevision;
bool originalCaptured;
unsigned completedPairs;
constexpr ULONGLONG sampleOffsetsMs[] = {900, 1800};
}

void AutosportBindLoadingOverlay(const void* module) {
    const auto* base = static_cast<const unsigned char*>(module);
    makeVisibility = reinterpret_cast<MakeVisibility>(base + 0x9ffbc0);
    drawFrontend = reinterpret_cast<DrawFrontend>(base + 0x720b80);
}

void* __fastcall AutosportSlateVisibility(void* message, void*, unsigned int visible) {
    // Only the show/hide constructors that carry the interned "pre_race_slate" target.
    const bool shown = (visible & 0xff) != 0;
    // ScreenSessionIntroSlateShow retains its stock timing and native hide/flow callbacks.
    // This message addresses only the pre_race_slate child of fe/overlay.
    const bool hideInScene = shown && !AutosportCompareSlate() && !AutosportFlatScreen();
    void* result = makeVisibility(message, hideInScene ? 0u : visible);
    slateSuppressed.store(hideInScene);
    if (slateVisible.exchange(shown) != shown) {
        if (shown) slateShowTick.store(GetTickCount64());
        visibilityRevision.fetch_add(1);
        AutosportLog("Native pre_race_slate showRequested=%d visible=%d", shown, shown && !hideInScene);
    }
    return result;
}

void __cdecl AutosportDrawFrontendOverlay(void* renderer, void* root, void* camera,
                                          unsigned int flags, unsigned int mask) {
    const Sample current = sample.load();
    if (current != Sample::None && slateVisible.load()) {
        draws.fetch_add(1);
        if (current == Sample::WithoutFrontend) return;
    }
    drawFrontend(renderer, root, camera, flags, mask);
}

void AutosportBeginLoadingOverlayFrame(uint64_t frame, bool scene) {
    sample.store(Sample::None);
    if (!AutosportUiComparison() || completedPairs >= 2 || !scene || !slateVisible.load()) return;
    // Capture during the visible portion. Camera cuts can coincide with the native SHOW.
    // These offsets select diagnostic images only. Presentation uses the named native event.
    if (GetTickCount64() - slateShowTick.load() < sampleOffsetsMs[completedPairs]) return;
    const unsigned revision = visibilityRevision.load();
    if (originalCaptured && (revision != sampleRevision || frame != sampleFrame + 1)) {
        // An interrupted capture cannot be compared to a different slate or camera sequence.
        originalCaptured = false;
    }
    sampleFrame = frame;
    sampleRevision = revision;
    draws.store(0);
    if (AutosportCompareSlate())
        sample.store(originalCaptured ? Sample::WithoutFrontend : Sample::Original);
    else
        sample.store(Sample::Production);
}

const char* AutosportLoadingOverlaySample(uint64_t frame) {
    const Sample current = sample.load();
    if (current == Sample::None || frame != sampleFrame || !draws.load() ||
        !slateVisible.load() || visibilityRevision.load() != sampleRevision) return nullptr;
    if (current == Sample::Production)
        return slateSuppressed.load() ? "pre-race-slate-suppressed" : "pre-race-slate-original-from-loading";
    return current == Sample::Original ? "pre-race-slate-original" : "pre-race-slate-without-fe-overlay";
}

void AutosportFinishLoadingOverlayFrame(uint64_t frame, bool captured) {
    const char* label = AutosportLoadingOverlaySample(frame);
    const Sample current = sample.exchange(Sample::None);
    if (current == Sample::None || frame != sampleFrame) return;
    if (current == Sample::WithoutFrontend || (current == Sample::Production && label && captured)) {
        ++completedPairs;
        originalCaptured = false;
    }
    if (label && captured && current == Sample::Original) originalCaptured = true;
    if ((label && captured) || current == Sample::WithoutFrontend) {
        AutosportLog("Overlay comparison nativeFrame=%llu variant=%s feDraws=%u captured=%d",
                     frame, label ? label : "interrupted", draws.load(), label && captured);
    }
}
