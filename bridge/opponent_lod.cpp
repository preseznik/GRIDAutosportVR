// Nearby opponents keep their full detail in VR (GRID2VR's nearby-opponent LOD, as DiRT2VR's).
// The game picks each car's level of detail for the main view only: an opponent beside or behind
// the player is outside that view and drops to its lowest model, which the headset then shows.
//
// The LOD selector (VA 9FDBD0) writes every car record's selected LOD (record +0xFC; records at
// manager +0x10FC, stride 0x18A0, count at +0x19BB4) and then calls the apply (VA 9FDB40), which
// hands each car its selected value (car SetLod VA B33A80; it falls back to a loaded model when
// that level's model is not loaded). The bridge takes that one call (RVA 5FE647) and, before the
// apply, raises every opponent within 30 native units of the focus car (the player; manager
// helper VA 670B00) to LOD 1, the game's own top level for opponents (or the car's best, car
// +0x124, when that is lower in detail), keeping it until it is 40 units away. The selector's
// budget table (manager +0x19CB4, five counts per profile) allows LOD 0 for one car only: the
// focus car's own model, whose player-only parts show on opponents. Only in VR race scenes; the
// focus car itself, distant cars and everything else stay native.
#include <windows.h>
#include <cmath>
#include <cwchar>
#include "bridge_api.h"

namespace {
constexpr DWORD recordsOffset = 0x10FC, countOffset = 0x19BB4, recordStride = 0x18A0;
constexpr DWORD recordCar = 0xDC, recordSelected = 0xFC, carPosition = 0x60, carBest = 0x124, carWorst = 0x120;
constexpr unsigned maxRecords = 16;
constexpr float enterRadius = 30.f, leaveRadius = 40.f;
constexpr int opponentTop = 1; // The best LOD the game itself gives an opponent (LOD 0 is the focus car's).
using ApplyFn = void (__thiscall*)(void*);
using FocusFn = BYTE* (__thiscall*)(void*);

struct Slot { const BYTE* car; bool nearby; };
Slot slots[maxRecords];
unsigned long long passes, raised;
bool failed;

float distance2(const BYTE* a, const BYTE* b) {
    const float* p = reinterpret_cast<const float*>(a + carPosition);
    const float* q = reinterpret_cast<const float*>(b + carPosition);
    const float x = p[0] - q[0], y = p[1] - q[1], z = p[2] - q[2];
    return x * x + y * y + z * z;
}

unsigned floorNearby(BYTE* manager) {
    const BYTE* image = AutosportGameImage();
    const BYTE* focus = reinterpret_cast<FocusFn>(const_cast<BYTE*>(image) + 0x270B00)(manager);
    const int count = *reinterpret_cast<const int*>(manager + countOffset);
    BYTE* records = *reinterpret_cast<BYTE**>(manager + recordsOffset);
    if (!focus || !records || count <= 0) return 0;
    const float* f = reinterpret_cast<const float*>(focus + carPosition);
    if (!std::isfinite(f[0]) || !std::isfinite(f[1]) || !std::isfinite(f[2])) return 0;
    unsigned changes = 0;
    for (unsigned i = 0; i < maxRecords; ++i) {
        Slot& slot = slots[i];
        BYTE* record = records + size_t(i) * recordStride;
        const BYTE* car = int(i) < count ? *reinterpret_cast<BYTE* const*>(record + recordCar) : nullptr;
        if (car != slot.car) slot = {car, false};
        if (!car || car == focus) { slot.nearby = false; continue; }
        const float d2 = distance2(car, focus);
        if (!std::isfinite(d2)) { slot.nearby = false; continue; }
        slot.nearby = d2 < (slot.nearby ? leaveRadius * leaveRadius : enterRadius * enterRadius);
        if (!slot.nearby) continue;
        const int best = *reinterpret_cast<const int*>(car + carBest);
        const int worst = *reinterpret_cast<const int*>(car + carWorst);
        int& selected = *reinterpret_cast<int*>(record + recordSelected);
        // LOD 0 is the focus car's own model: the selector's budget allows it for one car only, and
        // on opponents its player-only parts show (seen under a Ford Focus ST). Opponents the game
        // shows close up in front get LOD 1; nearby ones get the same.
        const int target = best > opponentTop ? best : opponentTop;
        if (best < 0 || best > worst || target > worst || selected <= target) continue;
        static unsigned noted;
        if (noted < 12) {
            ++noted;
            AutosportLog("Nearby opponent LOD: record %u at %.1f units, native LOD %d raised to %d (best %d, worst %d)",
                         i, std::sqrt(d2), selected, target, best, worst);
        }
        selected = target;
        ++changes;
    }
    return changes;
}

// -gasvropponentlod=0 keeps the native selection (comparison runs only; the launcher never passes it).
bool disabled() {
    static const bool off = wcsstr(GetCommandLineW(), L" -gasvropponentlod=0") != nullptr;
    return off;
}
} // namespace

// Replaces the selector's `call 9FDB40` (ECX = the LOD manager).
void __fastcall AutosportOpponentLodApply(void* manager, void*) {
    if (!disabled() && !failed && AutosportUseHeadset() && AutosportRegularPlay() && AutosportRaceScene()) {
        unsigned changes = 0;
        __try { changes = floorNearby(static_cast<BYTE*>(manager)); }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            failed = true;
            AutosportLog("Nearby opponent LOD stopped: a car record could not be read");
        }
        ++passes;
        raised += changes;
        if (changes && raised == changes)
            AutosportLog("Nearby opponent LOD: first raise, %u car(s) to their best model", changes);
        if (passes % 3600 == 0)
            AutosportLog("Nearby opponent LOD: %llu passes, %llu raises", passes, raised);
    }
    reinterpret_cast<ApplyFn>(const_cast<BYTE*>(AutosportGameImage()) + 0x5FDB40)(manager);
}
