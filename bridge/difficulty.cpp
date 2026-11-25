// Launcher Practice / Race difficulty.
//
// Those sessions run on -benchmarkAttract, where no car belongs to a human player. The race start's
// human path (RVA 3A97A0, run when the race loads and when settings are reapplied, as after the
// pause menu's Difficulty screen) gives the human's car the player's difficulty: AI level (39E370),
// damage (39D530), then the car setup with human 1 (39FBF0), which copies the assists into the
// car's assist record. It finds that car through its owner, and in a benchmark race there is none,
// so the player drove with the record's defaults (ABS only) whatever the Difficulty screen said.
// In Practice and Race the bridge runs that same path for the grid's first car (vehicle list index
// 0, RVA 5F9180), the player's: the launcher puts the player's car on pole.
//
// -gasvrdifficulty=ABS,TRACTION,STABILITY,CORNERING,STEERING,DAMAGE,AI,TRANSMISSION,FLASHBACKS
// (whole numbers; Launcher -> Difficulty) sets where those start. The game reads them through small
// getters, __thiscall(accessor) returning the profile setting (the settings lookup RVA 9F0F90 the
// HUD options use): assist_abs, assist_traction_control, assist_stability_control,
// assist_cornering, assist_auto_steer (steering assist), realistic_damage (1 full, 0 visual only)
// and ai_difficulty (0-4). Each starts with 9 bytes of position-independent code (mov eax,[ecx+8];
// mov reg,[eax+disp32]), so the bridge jumps from there to its own getter; a trampoline keeps the
// original. The gearbox setup (RVA 2EC460) reads transmission_type straight from the profile store
// (its find, __thiscall(store, name) returning a record with the value at +8; transmission_type_drag
// for drag events): 0 automatic, 1 manual, 2 manual H-pattern, 3 H-pattern with clutch, 4 manual
// with clutch (gearbox 4/2/2/1/1, shifter sequential/sequential/H/H/sequential). The flashback count
// (RVA 36DDF0, __cdecl(context), 0-5, 6 unlimited) is fixed at 5 in benchmark races.
//
// A setting changed in the pause menu's Difficulty screen wins for the rest of the session: once
// the game's own value differs from the one it had at the first read, the getter answers it. The
// screen itself shows the game's values; the save profile is never read or written by the bridge.
// With launcher values the race start's block that turns the assists on for a keyboard or tilt
// device (RVA 39FDB1, a "je") is skipped, so the chosen assists hold whatever drives the car.
#include <windows.h>
#include <cstring>
#include <cwchar>
#include <cwctype>
#include "game_build.h"
#include "bridge_api.h"

namespace {
constexpr unsigned GetterCount = 7, ValueCount = 9, Transmission = 7, Flashbacks = 8;
const char* const names[ValueCount] = {"ABS", "traction control", "stability control", "cornering assist", "steering assist",
                                       "damage", "AI difficulty", "transmission", "flashbacks"};
constexpr int limits[ValueCount] = {1, 1, 1, 1, 1, 1, 4, 4, 6};
int launcherValue[ValueCount];
bool launcherDifficulty;

// The game's first answer for each setting, and whether it has changed since (the pause menu).
struct Seen { bool known, changed; int first; };
Seen seen[ValueCount];
int choose(unsigned index, int game) {
    if (!launcherDifficulty) return game;
    Seen& s = seen[index];
    if (!s.known) {
        s = {true, false, game};
        AutosportLog("Difficulty: %s from the launcher, %d (the game's is %d)", names[index], launcherValue[index], game);
    } else if (!s.changed && game != s.first) {
        s.changed = true;
        AutosportLog("Difficulty: %s changed in the game to %d; the game's value applies from now on", names[index], game);
    }
    return s.changed ? game : launcherValue[index];
}

using GetterFn = int (__thiscall*)(void*);
using FlashbackFn = int (__cdecl*)(void*);
using FindFn = const BYTE* (__thiscall*)(void*, const char*);
using HumanStartFn = void (__thiscall*)(void*, int, int);
using GameFn = void (__thiscall*)(void*);
using CarSetupFn = void (__thiscall*)(void*, int, void*);
using VehicleAtFn = void* (__thiscall*)(void*, int);
GetterFn stockGetter[GetterCount];
FlashbackFn stockFlashbacks;
FindFn stockFind;
HumanStartFn stockHumanStart;
GameFn aiLevelSetup, damageSetup;
CarSetupFn carSetup;
VehicleAtFn vehicleAt;
constexpr DWORD vehicleListOffset = 0x13F4;

// Getters 0-4 (the assists) and 5 (damage) return a bool in AL only.
template <unsigned I>
int __fastcall getter(void* accessor, void*) {
    int game = stockGetter[I](accessor);
    if constexpr (I < 6) game &= 0xFF;
    return choose(I, game);
}
GetterFn const replacements[GetterCount] = {
    reinterpret_cast<GetterFn>(&getter<0>), reinterpret_cast<GetterFn>(&getter<1>), reinterpret_cast<GetterFn>(&getter<2>),
    reinterpret_cast<GetterFn>(&getter<3>), reinterpret_cast<GetterFn>(&getter<4>), reinterpret_cast<GetterFn>(&getter<5>),
    reinterpret_cast<GetterFn>(&getter<6>)};

int __cdecl flashbacks(void* context) { return choose(Flashbacks, stockFlashbacks(context)); }

// The profile store's record shape the gearbox setup reads: the value at +8.
struct Record { int type, reserved, value; } transmissionRecord;
const BYTE* __fastcall transmission(void* store, void*, const char* name) {
    const BYTE* record = stockFind(store, name);
    int game = 0;
    if (record) std::memcpy(&game, record + 8, sizeof(game));
    transmissionRecord = {1, 0, choose(Transmission, game)};
    return reinterpret_cast<const BYTE*>(&transmissionRecord);
}

// The race start's human path, then the same for the grid's first car.
void __fastcall humanStart(void* race, void*, int a, int b) {
    stockHumanStart(race, a, b);
    void* list = *reinterpret_cast<void**>(static_cast<BYTE*>(race) + vehicleListOffset);
    void* vehicle = list ? vehicleAt(list, 0) : nullptr;
    static LONG logged;
    if (InterlockedIncrement(&logged) <= 8)
        AutosportLog("Difficulty: the player's car (grid 0, %p) gets the player's difficulty", vehicle);
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
UINT callTarget(const BYTE* base, UINT call) { return UINT(call + 5 + callDelta(base + call)); }
// A getter's first 9 bytes: mov eax,[ecx+8]; mov ecx|edx,[eax+disp32].
bool getterPrologue(const BYTE* code) {
    return code[0] == 0x8B && code[1] == 0x41 && code[2] == 0x08 && code[3] == 0x8B && (code[4] == 0x88 || code[4] == 0x90);
}
// The flashback count's: push ebp; mov ebp,esp; mov ecx,[abs32].
bool flashbackPrologue(const BYTE* code) {
    return code[0] == 0x55 && code[1] == 0x8B && code[2] == 0xEC && code[3] == 0x8B && code[4] == 0x0D;
}
bool framePrologue(const BYTE* code) { return code[0] == 0x55 && code[1] == 0x8B && code[2] == 0xEC; }

// Copies the 9 relocatable bytes into executable memory with a jump back, then jumps from the
// function's start to the replacement. Returns the trampoline, or nullptr.
BYTE* trampolines;
size_t trampolineUsed;
void* detour(BYTE* target, const void* replacement) {
    if (!trampolines) {
        trampolines = static_cast<BYTE*>(VirtualAlloc(nullptr, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
        if (!trampolines) return nullptr;
    }
    if (trampolineUsed + 16 > 4096) return nullptr;
    BYTE* stub = trampolines + trampolineUsed;
    trampolineUsed += 16;
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
    const GameBuild* build = AutosportGameBuild();
    if (!build) return;
    auto* base = const_cast<BYTE*>(image);
    if (wcsstr(commandLine, L" -gasvrdifficulty=")) {
        launcherDifficulty = parse(commandLine);
        if (!launcherDifficulty) AutosportLog("Difficulty: -gasvrdifficulty is not nine values within range; the game's own difficulty applies");
    }
    // Verify everything before changing anything.
    bool ok = framePrologue(base + build->humanStart) && framePrologue(base + build->vehicleAt) &&
              framePrologue(base + build->carSetup) && framePrologue(base + build->aiLevelSetup) && framePrologue(base + build->damageSetup);
    for (UINT call : build->humanStartCalls) ok = ok && base[call] == 0xE8 && callTarget(base, call) == build->humanStart;
    if (launcherDifficulty) {
        for (UINT at : build->difficultyGetters) ok = ok && getterPrologue(base + at);
        ok = ok && flashbackPrologue(base + build->flashbackCount) && base[build->keyboardAssists] == 0x74;
        for (UINT call : build->transmissionReads)
            ok = ok && base[call] == 0xE8 && callTarget(base, call) == callTarget(base, build->transmissionReads[0]);
    }
    if (!ok) { AutosportLog("Difficulty: this executable's code does not match; the game's own race setup is kept"); return; }

    stockHumanStart = reinterpret_cast<HumanStartFn>(base + build->humanStart);
    vehicleAt = reinterpret_cast<VehicleAtFn>(base + build->vehicleAt);
    carSetup = reinterpret_cast<CarSetupFn>(base + build->carSetup);
    aiLevelSetup = reinterpret_cast<GameFn>(base + build->aiLevelSetup);
    damageSetup = reinterpret_cast<GameFn>(base + build->damageSetup);
    for (UINT call : build->humanStartCalls) ok = ok && redirectCall(base + call, reinterpret_cast<const void*>(&humanStart));
    if (!launcherDifficulty) {
        AutosportLog("Difficulty: the player's car gets the game's difficulty settings%s", ok ? "" : " (partly hooked)");
        return;
    }
    for (unsigned i = 0; i < GetterCount; ++i) {
        stockGetter[i] = reinterpret_cast<GetterFn>(detour(base + build->difficultyGetters[i], reinterpret_cast<const void*>(replacements[i])));
        ok = ok && stockGetter[i];
    }
    stockFlashbacks = reinterpret_cast<FlashbackFn>(detour(base + build->flashbackCount, reinterpret_cast<const void*>(&flashbacks)));
    ok = ok && stockFlashbacks;
    stockFind = reinterpret_cast<FindFn>(base + callTarget(base, build->transmissionReads[0]));
    for (UINT call : build->transmissionReads) ok = ok && redirectCall(base + call, reinterpret_cast<const void*>(&transmission));
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
