#include "loading_overlay.h"
#include "game_build.h"
#include "bridge_api.h"
#include <atomic>
#include <intrin.h>

namespace {
using MakeVisibility = void* (__thiscall*)(void*, unsigned int);
using DrawFrontend = void (__cdecl*)(void*, void*, void*, unsigned int, unsigned int);
MakeVisibility makeVisibility;
DrawFrontend drawFrontend;
const void* frontendPass1; // return address of the FE manager's first non-HUD pass (SiteFrontend1)

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
    makeVisibility = reinterpret_cast<MakeVisibility>(base + AutosportGameBuild()->makeVisibility);
    drawFrontend = reinterpret_cast<DrawFrontend>(base + AutosportGameBuild()->drawFrontend);
    frontendPass1 = base + AutosportGameBuild()->sites[SiteFrontend1].call + 5;
}

void* __fastcall AutosportSlateVisibility(void* message, void*, unsigned int visible) {
    // Only the show/hide constructors that carry the interned "pre_race_slate" target.
    const bool shown = (visible & 0xff) != 0;
    // ScreenSessionIntroSlateShow retains its stock timing and native hide/flow callbacks.
    // This message addresses only the pre_race_slate child of fe/overlay.
    // Decided by the race scene, not by this frame's presentation: an intro shot (seated screen)
    // can be on screen when the slate is requested, and the slate then stays up in stereo.
    const bool hideInScene = shown && !AutosportCompareSlate() && AutosportRaceScene();
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
    // 3D pre-race intro: the shots stay in stereo, so the 2D broadcast layer drawn over them
    // (info panel, wipes) would be stuck to the view. The FE manager's three non-HUD passes
    // (RVA 1B7B63, 1B7B98, 1B7C09) all come here; skip them for those shots only.
    if (AutosportPreRaceIntro3D()) return;
    // Stereo race (car camera live, not the seated screen): the first of those passes still draws
    // the loading screen's lower panel, a dark band with its grey progress bar, laid out for half
    // the game's window per eye. With an 800x600 window it falls below the eye image; with a 16:9
    // one it covers the bottom fifth of the view (3840x2160: eye rows 80-100% on xr-sim, 22 Nov),
    // and skipping this pass removed only that. So it is skipped there; the HUD (0x1B7AEB) and the
    // other passes still draw, and the seated screen (loading, intro shots, pause) keeps it.
    if (_ReturnAddress() == frontendPass1 && AutosportRaceScene() && !AutosportFlatScreen()) {
        static LONG logged;
        if (InterlockedCompareExchange(&logged, 1, 0) == 0) AutosportLog("Stereo race: loading panel FE pass skipped");
        return;
    }
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
