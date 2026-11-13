// Practice / Race device claim (GRID2VR's device claim, for GRID Autosport).
//
// Normal play reaches the race through ScreenPressStart, whose update (RVA 196530) binds the
// device that pressed Start to player 0's input map: Input2 = services[*RVA E57334], player
// manager = services[*RVA E574D4], player 0 = [manager+0x44], map = [player+0xC], then
// Input2 RVA 78E4B0 (__thiscall, map, const Device*) at RVA 19669E. The bind looks the device's
// preset up (the launcher's session preset for a wheel the game ships none for), merges the
// keyboard defaults and makes it the player's controller, force feedback included.
// The launcher's Practice / Race boot (-benchmarkAttract) never shows that screen, so the map
// stays keyboard-only and wheel and pad bindings do nothing. -gasvrdevice=FAMILY[,FAMILY...]
// (xinput_pad or a DirectInput product GUID, in order of preference) asks the bridge to make
// that same native bind once, when the race's StateLoading exits, for the first family the
// game's own device enumerator lists. The enumerator (RVA 97DAB0 on Input2+0x84, a vector of
// 12-byte {backend, family, index} records released by RVA 8E1C0) and the string interning
// (RVA 98AE00) are byte for byte GRID 2's (GRID2VR 1a0be0 / 39e3a0 / 643fa0).
#include <windows.h>
#include <cstring>
#include <cwchar>
#include <cwctype>
#include "bridge_api.h"

namespace {
struct Device { unsigned backend, family, index; };
struct DeviceVector { void* allocator; Device* begin; Device* end; unsigned capacity; int limit; };
static_assert(sizeof(Device) == 12 && sizeof(DeviceVector) == 20);

using InternFn = void* (__thiscall*)(unsigned*, const char*);
using EnumerateFn = unsigned (__thiscall*)(void*, DeviceVector*);
using ReleaseFn = void (__thiscall*)(DeviceVector*, unsigned, int);
using BindFn = unsigned (__thiscall*)(void*, unsigned, const Device*);
using LoadingDispatch = void (__thiscall*)(void*, const void*);

const BYTE* image;
char families[8][40];
unsigned familyCount;
bool requested, done;
void* exitingState; // StateLoading's `this` (ESI at the exit call site), captured by the thunk.

unsigned intern(const char* text) {
    unsigned id = 0;
    reinterpret_cast<InternFn>(image + 0x98AE00)(&id, text);
    return id;
}

// Every connected device, as the game lists them; false if the call or its result is unusable.
bool enumerate(BYTE* input, Device (&out)[32], unsigned& count) {
    BYTE* library = input + 0x80;
    DeviceVector vector{*reinterpret_cast<void**>(library), nullptr, nullptr, 0, -1};
    const unsigned result = reinterpret_cast<EnumerateFn>(image + 0x97DAB0)(library + 4, &vector);
    const auto bytes = reinterpret_cast<UINT_PTR>(vector.end) - reinterpret_cast<UINT_PTR>(vector.begin);
    const bool shape = result == 0 && vector.end >= vector.begin && bytes % sizeof(Device) == 0 && bytes / sizeof(Device) <= 32;
    count = shape ? static_cast<unsigned>(bytes / sizeof(Device)) : 0;
    if (count) std::memcpy(out, vector.begin, count * sizeof(Device));
    reinterpret_cast<ReleaseFn>(image + 0x8E1C0)(&vector, 0, -1);
    return shape;
}

void claim(const BYTE* state) {
    done = true;
    const BYTE* context = *reinterpret_cast<BYTE* const*>(state + 0xC);
    BYTE* const* services = *reinterpret_cast<BYTE* const* const*>(context + 4);
    BYTE* input = services[*reinterpret_cast<const unsigned*>(image + 0xE57334)];
    const BYTE* players = services[*reinterpret_cast<const unsigned*>(image + 0xE574D4)];
    const BYTE* player = players ? *reinterpret_cast<BYTE* const*>(players + 0x44) : nullptr;
    if (!input || !player) { AutosportLog("Device claim: no input service or player 0"); return; }
    const unsigned map = *reinterpret_cast<const unsigned*>(player + 0xC);
    Device devices[32];
    unsigned count = 0;
    if (map == 0xFFFFFFFFu || !enumerate(input, devices, count)) {
        AutosportLog("Device claim: player map %d or the device list is not usable", int(map));
        return;
    }
    const unsigned keyboard = intern("keyboard");
    for (unsigned i = 0; i < count; ++i)
        AutosportLog("Device claim: game device %u backend=%08X family=%08X index=%d", i, devices[i].backend, devices[i].family, int(devices[i].index));
    for (unsigned f = 0; f < familyCount; ++f) {
        char upper[40];
        strcpy_s(upper, families[f]);
        _strupr_s(upper);
        const unsigned ids[] = {intern(families[f]), intern(upper)};
        for (unsigned i = 0; i < count; ++i) {
            const Device& device = devices[i];
            if (!device.backend || !device.family || device.index == 0xFFFFFFFFu || device.family == keyboard ||
                (device.family != ids[0] && device.family != ids[1])) continue;
            const unsigned result = reinterpret_cast<BindFn>(image + 0x78E4B0)(input, map, &device);
            AutosportLog("Device claim: bound %s (device %u) to player map %u, native result %u%s", families[f], i, map, result,
                         result == 0x58 ? " (the game found no preset for it)" : "");
            return;
        }
    }
    AutosportLog("Device claim: none of the %u requested devices is connected (%u game devices)", familyCount, count);
}

void __fastcall loadingExited(void* dispatcher, void*, const void* event) {
    AutosportLoadingExited(dispatcher, nullptr, event);
    if (!requested || done || !exitingState) return;
    __try { claim(static_cast<const BYTE*>(exitingState)); }
    __except (EXCEPTION_EXECUTE_HANDLER) { done = true; AutosportLog("Device claim failed with an exception"); }
}
} // namespace

// The call at RVA 243FC9 sits inside StateLoading's exit with the state in ESI (callee-saved, so
// still intact here); the thunk records it and continues as a plain __thiscall replacement.
extern "C" __declspec(naked) void AutosportLoadingExitedThunk() {
    __asm {
        mov exitingState, esi
        jmp loadingExited
    }
}

void AutosportConfigureDeviceClaim(const BYTE* base, const wchar_t* cmd, bool practice) {
    image = base;
    const wchar_t* flag = wcsstr(cmd, L" -gasvrdevice=");
    if (!flag || !practice) return;
    const wchar_t* at = flag + wcslen(L" -gasvrdevice=");
    const size_t length = wcscspn(at, L" \t");
    familyCount = 0;
    for (size_t i = 0; i < length && familyCount < ARRAYSIZE(families);) {
        const size_t part = wcscspn(at + i, L", \t");
        if (part == 0 || part >= ARRAYSIZE(families[0])) return;
        for (size_t c = 0; c < part; ++c) {
            const wchar_t ch = at[i + c];
            if (!(iswalnum(ch) || ch == L'_' || ch == L'-' || ch == L'{' || ch == L'}')) { familyCount = 0; return; }
            families[familyCount][c] = static_cast<char>(ch);
        }
        families[familyCount++][part] = 0;
        i += part + (at[i + part] == L',' ? 1 : 0);
    }
    requested = familyCount > 0;
    if (requested) AutosportLog("Device claim requested for %u device famil%s, first %s", familyCount, familyCount == 1 ? "y" : "ies", families[0]);
}
