// Launcher Practice / Race difficulty.
//
// Those sessions run on -benchmarkAttract, where no car belongs to a human player. The race start's
// human path (RVA 3A97A0, run when the race loads and when settings are reapplied, as after the
// pause menu's Difficulty screen) gives the human's car the player's difficulty: AI level (39E370),
// damage (39D530), then the car setup with human 1 (39FBF0), which copies the assists into the
// car's assist record. It finds that car through its owner, and in a benchmark race there is none,
// so the player drove with the record's defaults (ABS only) whatever the Difficulty screen said.
// In Practice and Race the bridge runs that same path for the player's car: the race's vehicle
// list (race +0x13F4, RVA 5F9180 by index) entry the game drives as a human's (+0xB8 == 0, as the
// game's own per-car assist updates test), or the first one.
//
// -gasvrdifficulty=ABS,TRACTION,STABILITY,CORNERING,STEERING,DAMAGE,AI,TRANSMISSION,FLASHBACKS
// (whole numbers; Launcher -> Difficulty) puts the launcher's choices where the game keeps them for
// the session, so the race reads them as its own and the pause menu's Difficulty screen shows and
// changes them. The settings (assist_abs, assist_traction_control, assist_stability_control,
// assist_cornering, assist_auto_steer, realistic_damage 1 full / 0 visual only, ai_difficulty 0-4,
// career_flashbacks 0-5 / 6 unlimited) are objects from the settings lookup (RVA 9F0F90,
// __thiscall(container, category, name); the container is the services entry at the index in RVA
// F623D8, the services reached from the profile store's +4), set through their vtable +0xC with a
// pointer to the value as Press Start sets press_start_user_index. transmission_type (0 automatic,
// 1 manual, 2 manual H-pattern, 3 H-pattern with clutch, 4 manual with clutch) is a record in the
// profile store ([race +0x142C] +0x10; its find, __thiscall(store, name), returns the record with
// the value at +8), which the gearbox setup (RVA 2EC460) reads while the race loads: the bridge
// takes that read (its two calls, transmission_type and _drag), writes the launcher's choices
// there first, and lets the game read on. Practice and Race never save the profile (no save is
// written in those sessions), and Normal launches are untouched. The benchmark's flashback count
// (RVA 36DDF0, __cdecl(context)) is a fixed 5; with launcher values it answers career_flashbacks.
// The race start's block that turns the assists on for a keyboard or tilt device (RVA 39FDB1, a
// "je") is skipped too, so the chosen assists hold whatever drives the car.
#include <windows.h>
#include <cstring>
#include <cwchar>
#include <cwctype>
#include "game_build.h"
#include "bridge_api.h"

namespace {
constexpr unsigned ValueCount = 9, TreeCount = 8, Transmission = 7, Flashbacks = 8;
// GameBuild settingNames order; the first five and damage are in the assist category.
const char* const names[ValueCount] = {"assist_abs", "assist_traction_control", "assist_stability_control", "assist_cornering",
                                       "assist_auto_steer", "realistic_damage", "ai_difficulty", "transmission_type", "career_flashbacks"};
constexpr int limits[ValueCount] = {1, 1, 1, 1, 1, 1, 4, 4, 6};
int launcherValue[ValueCount];
bool launcherDifficulty;

using FlashbackFn = int (__cdecl*)(void*);
using FindFn = BYTE* (__thiscall*)(void*, const char*);
using LookupFn = void* (__thiscall*)(void*, const void*, const char*);
using SetFn = void (__thiscall*)(void*, const int*);
using GetFn = int (__thiscall*)(void*);
using HumanStartFn = void (__thiscall*)(void*, int, int);
using GameFn = void (__thiscall*)(void*);
using CarSetupFn = void (__thiscall*)(void*, int, void*);
using VehicleAtFn = BYTE* (__thiscall*)(void*, int);
const GameBuild* build;
BYTE* base;
FlashbackFn stockFlashbacks;
FindFn stockFind;
LookupFn lookup;
HumanStartFn stockHumanStart;
GameFn aiLevelSetup, damageSetup;
CarSetupFn carSetup;
VehicleAtFn vehicleAt;
constexpr DWORD vehicleListOffset = 0x13F4, vehicleDriven = 0xB8, raceProfile = 0x142C, profileStore = 0x10, storeServices = 4;

const char* settingName(unsigned i) { return reinterpret_cast<const char*>(base + build->settingNames[i]); }
const void* category(unsigned i) { return base + build->settingCategories[i < 6 ? 0 : 1]; }

// The settings container, as the setting getters reach it from the race: [[[race +0x142C] +0x10]
// +4] is the services table, and the container its entry at the settings service index.
void* volatile settingsContainer;
void* container(BYTE* race) {
    BYTE* profile = *reinterpret_cast<BYTE**>(race + raceProfile);
    BYTE* holder = profile ? *reinterpret_cast<BYTE**>(profile + profileStore) : nullptr;
    void* const* services = holder ? *reinterpret_cast<void* const* const*>(holder + storeServices) : nullptr;
    return services ? services[*reinterpret_cast<const unsigned*>(base + build->settingsService)] : nullptr;
}

// The pause menu's Difficulty screen shows and changes the career twins (cs_assist_abs ...
// cs_ai_difficulty, found by name), while a benchmark race reads the plain settings.
const char* const twins[] = {"cs_assist_abs", "cs_assist_traction_control", "cs_assist_stability_control", "cs_assist_cornering",
                             "cs_assist_auto_steer", "cs_realistic_damage", "cs_ai_difficulty"};
constexpr unsigned TwinCount = 7;
void* setting(void* settings, unsigned i, const char* name) { return settings ? lookup(settings, category(i), name) : nullptr; }
int get(void* item) { return reinterpret_cast<GetFn>((*static_cast<void* const* const*>(item))[2])(item); }
void put(void* item, int value) { reinterpret_cast<SetFn>((*static_cast<void* const* const*>(item))[3])(item, &value); }
int normal(unsigned i, int value) { return i < 6 ? value & 0xFF : value; }

// Before each race start: the first time, the launcher's values go into both; every time, what
// the Difficulty screen holds goes into the settings the race reads.
volatile LONG treeApplied, recordApplied;
void syncSettings(BYTE* race) {
    __try {
        void* settings = container(race);
        settingsContainer = settings;
        if (!settings) return;
        if (launcherDifficulty && !InterlockedExchange(&treeApplied, 1)) {
            int set = 0;
            for (unsigned i = 0; i < ValueCount; ++i) {
                if (i == Transmission) continue;
                void* plain = setting(settings, i, settingName(i));
                void* twin = i < TwinCount ? setting(settings, i, twins[i]) : nullptr;
                if (plain) put(plain, launcherValue[i]);
                if (twin) put(twin, launcherValue[i]);
                if (plain && normal(i, get(plain)) == launcherValue[i] && (i >= TwinCount || (twin && normal(i, get(twin)) == launcherValue[i]))) ++set;
                else AutosportLog("Difficulty: %s did not take the launcher's %d", settingName(i), launcherValue[i]);
            }
            AutosportLog("Difficulty: %d of %u settings take the launcher's values for this session", set, ValueCount - 1);
            return;
        }
        for (unsigned i = 0; i < TwinCount; ++i) {
            void* plain = setting(settings, i, settingName(i));
            void* twin = setting(settings, i, twins[i]);
            if (!plain || !twin) continue;
            const int want = normal(i, get(twin)), had = normal(i, get(plain));
            if (want == had) continue;
            put(plain, want);
            AutosportLog("Difficulty: %s %d from the Difficulty screen (was %d)", settingName(i), want, had);
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { AutosportLog("Difficulty: reading the session's difficulty failed with an exception"); }
}

int settingValue(unsigned i, int fallback) {
    void* settings = settingsContainer;
    void* setting = settings ? lookup(settings, category(i), settingName(i)) : nullptr;
    if (!setting) return fallback;
    return reinterpret_cast<GetFn>((*static_cast<void* const* const*>(setting))[2])(setting);
}

// The gearbox setup's reads of transmission_type (and _drag): the record takes the launcher's
// transmission once, then the game reads it, and changes to it, as its own.
BYTE* __fastcall transmission(BYTE* store, void*, const char* name) {
    BYTE* record = stockFind(store, name);
    if (record && name == settingName(Transmission) && !InterlockedExchange(&recordApplied, 1)) {
        int game = 0;
        std::memcpy(&game, record + 8, sizeof(game));
        std::memcpy(record + 8, &launcherValue[Transmission], sizeof(int));
        AutosportLog("Difficulty: transmission_type %d from the launcher (the game's was %d)", launcherValue[Transmission], game);
    }
    return record;
}

// In the benchmark the flashback count is fixed; with launcher values it is the session's setting.
int __cdecl flashbacks(void* context) {
    const int stock = stockFlashbacks(context);
    int count = stock;
    __try { count = settingValue(Flashbacks, stock); }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
    static LONG logged;
    if (InterlockedIncrement(&logged) <= 3) AutosportLog("Difficulty: flashbacks %d (the benchmark's %d)", count, stock);
    return count;
}

// The car the game drives as a human's (+0xB8 == 0), or the first.
BYTE* playerVehicle(void* list, int& index) {
    BYTE* first = vehicleAt(list, 0);
    index = 0;
    static LONG listed;
    if (InterlockedIncrement(&listed) <= 2)
        for (int i = 0; i < 32; ++i) {
            const BYTE* vehicle = vehicleAt(list, i);
            if (!vehicle) break;
            AutosportLog("Difficulty: vehicle %d %p driven %d", i, vehicle, *reinterpret_cast<const int*>(vehicle + vehicleDriven));
        }
    for (int i = 0; i < 32; ++i) {
        BYTE* vehicle = vehicleAt(list, i);
        if (!vehicle) break;
        if (*reinterpret_cast<const int*>(vehicle + vehicleDriven) == 0) { index = i; return vehicle; }
    }
    return first;
}

// The race start's human path, then the same for the player's car.
void __fastcall humanStart(BYTE* race, void*, int a, int b) {
    syncSettings(race);
    stockHumanStart(race, a, b);
    void* list = *reinterpret_cast<void**>(race + vehicleListOffset);
    int index = 0;
    BYTE* vehicle = list ? playerVehicle(list, index) : nullptr;
    static LONG logged;
    if (InterlockedIncrement(&logged) <= 8)
        AutosportLog("Difficulty: the player's car (vehicle %d, %p) gets the session's difficulty", index, vehicle);
    if (!vehicle) return;
    aiLevelSetup(race);
    damageSetup(race);
    carSetup(race, 1, vehicle);
}

// "-gasvrdifficulty=a,b,...": nine whole numbers within their limits.
bool parse(const wchar_t* cmd) {
    const wchar_t* at = wcsstr(cmd, L" -gasvrdifficulty=");
    if (!at) return false;
    at += wcslen(L" -gasvrdifficulty=");
    for (unsigned i = 0; i < ValueCount; ++i) {
        if (!iswdigit(*at)) return false;
        const int value = *at++ - L'0';
        if (value > limits[i]) return false;
        launcherValue[i] = value;
        if (i + 1 < ValueCount && *at++ != L',') return false;
    }
    return !*at || *at == L' ';
}

INT32 callDelta(const BYTE* call) { INT32 delta; std::memcpy(&delta, call + 1, 4); return delta; }
UINT callTarget(UINT call) { return UINT(call + 5 + callDelta(base + call)); }
// The flashback count's first 9 bytes: push ebp; mov ebp,esp; mov ecx,[abs32].
bool flashbackPrologue(const BYTE* code) {
    return code[0] == 0x55 && code[1] == 0x8B && code[2] == 0xEC && code[3] == 0x8B && code[4] == 0x0D;
}
bool framePrologue(const BYTE* code) { return code[0] == 0x55 && code[1] == 0x8B && code[2] == 0xEC; }

// Copies the 9 relocatable bytes into executable memory with a jump back, then jumps from the
// function's start to the replacement. Returns the trampoline, or nullptr.
void* detour(BYTE* target, const void* replacement) {
    BYTE* stub = static_cast<BYTE*>(VirtualAlloc(nullptr, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
    if (!stub) return nullptr;
    std::memcpy(stub, target, 9);
    stub[9] = 0xE9;
    const INT32 back = INT32(reinterpret_cast<uintptr_t>(target + 9) - reinterpret_cast<uintptr_t>(stub + 14));
    std::memcpy(stub + 10, &back, 4);
    DWORD old;
    if (!VirtualProtect(target, 9, PAGE_EXECUTE_READWRITE, &old)) return nullptr;
    target[0] = 0xE9;
    const INT32 jump = INT32(reinterpret_cast<uintptr_t>(replacement) - reinterpret_cast<uintptr_t>(target + 5));
    std::memcpy(target + 1, &jump, 4);
    std::memset(target + 5, 0x90, 4);
    VirtualProtect(target, 9, old, &old);
    FlushInstructionCache(GetCurrentProcess(), target, 9);
    return stub;
}

bool redirectCall(BYTE* call, const void* replacement) {
    DWORD old;
    if (!VirtualProtect(call, 5, PAGE_EXECUTE_READWRITE, &old)) return false;
    const INT32 delta = INT32(reinterpret_cast<uintptr_t>(replacement) - reinterpret_cast<uintptr_t>(call + 5));
    std::memcpy(call + 1, &delta, 4);
    VirtualProtect(call, 5, old, &old);
    FlushInstructionCache(GetCurrentProcess(), call, 5);
    return true;
}
}

void AutosportConfigureDifficulty(const BYTE* image, const wchar_t* commandLine) {
    static bool attempted;
    if (attempted || !image || !commandLine || !AutosportPracticeSession()) return;
    attempted = true;
    build = AutosportGameBuild();
    if (!build) return;
    base = const_cast<BYTE*>(image);
    if (wcsstr(commandLine, L" -gasvrdifficulty=")) {
        launcherDifficulty = parse(commandLine);
        if (!launcherDifficulty) AutosportLog("Difficulty: -gasvrdifficulty is not nine values within range; the game's own difficulty applies");
    }
    // Verify everything before changing anything.
    bool ok = framePrologue(base + build->humanStart) && framePrologue(base + build->vehicleAt) &&
              framePrologue(base + build->carSetup) && framePrologue(base + build->aiLevelSetup) && framePrologue(base + build->damageSetup);
    for (UINT call : build->humanStartCalls) ok = ok && base[call] == 0xE8 && callTarget(call) == build->humanStart;
    if (launcherDifficulty) {
        for (unsigned i = 0; i < ValueCount; ++i) ok = ok && !std::strcmp(settingName(i), names[i]);
        ok = ok && flashbackPrologue(base + build->flashbackCount) && base[build->keyboardAssists] == 0x74 && framePrologue(base + build->settingLookup);
        for (UINT call : build->transmissionReads) ok = ok && base[call] == 0xE8 && callTarget(call) == callTarget(build->transmissionReads[0]);
    }
    if (!ok) { AutosportLog("Difficulty: this executable's code does not match; the game's own race setup is kept"); return; }

    stockHumanStart = reinterpret_cast<HumanStartFn>(base + build->humanStart);
    vehicleAt = reinterpret_cast<VehicleAtFn>(base + build->vehicleAt);
    carSetup = reinterpret_cast<CarSetupFn>(base + build->carSetup);
    aiLevelSetup = reinterpret_cast<GameFn>(base + build->aiLevelSetup);
    damageSetup = reinterpret_cast<GameFn>(base + build->damageSetup);
    lookup = reinterpret_cast<LookupFn>(base + build->settingLookup);
    stockFind = reinterpret_cast<FindFn>(base + callTarget(build->transmissionReads[0]));
    for (UINT call : build->humanStartCalls) ok = ok && redirectCall(base + call, reinterpret_cast<const void*>(&humanStart));
    if (!launcherDifficulty) {
        AutosportLog("Difficulty: the player's car gets the game's difficulty settings%s", ok ? "" : " (partly hooked)");
        return;
    }
    for (UINT call : build->transmissionReads) ok = ok && redirectCall(base + call, reinterpret_cast<const void*>(&transmission));
    stockFlashbacks = reinterpret_cast<FlashbackFn>(detour(base + build->flashbackCount, reinterpret_cast<const void*>(&flashbacks)));
    ok = ok && stockFlashbacks;
    DWORD old;
    if (VirtualProtect(base + build->keyboardAssists, 1, PAGE_EXECUTE_READWRITE, &old)) {
        base[build->keyboardAssists] = 0xEB;
        VirtualProtect(base + build->keyboardAssists, 1, old, &old);
        FlushInstructionCache(GetCurrentProcess(), base + build->keyboardAssists, 1);
    } else ok = false;
    AutosportLog("Difficulty from the launcher%s: ABS %d, traction %d, stability %d, cornering %d, steering %d, damage %d, AI %d, "
                 "transmission %d, flashbacks %d", ok ? "" : " (partly applied)", launcherValue[0], launcherValue[1], launcherValue[2],
                 launcherValue[3], launcherValue[4], launcherValue[5], launcherValue[6], launcherValue[7], launcherValue[8]);
}
