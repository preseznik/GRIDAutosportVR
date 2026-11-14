// Launcher Force Feedback (GRID2VR's force feedback shaping, for GRID Autosport).
//
// -gasvrffb=STRENGTH,WEIGHT,VIBRATION (whole percent: 0-100, 1-100, 0-100) shapes the force the
// game sends to a wheel. GRID Autosport, like GRID 2, opens the wheel through DINPUT8's
// DirectInput8Create and drives it with a DirectInput constant-force effect whose magnitude it
// updates about once per frame, with the aligning torque, kerbs, road surface and impacts already
// mixed. The bridge splits that magnitude with a 3 Hz one-pole low-pass: the slow part is the
// wheel's weight, the fast remainder is vibration, and the wheel gets
//     strength x (weight x slow + vibration x (magnitude - slow)), clamped to +/-10000.
// 100/100/100 passes the game's force through unchanged, and then nothing is hooked at all.
//
// No game code or data is written. The game's DirectInput8Create import is routed from DllMain,
// before the game creates its DirectInput object. From there the bridge follows the game's own
// objects: the CreateDevice slot of its IDirectInput8 method table, the CreateEffect slot of each
// device method table it creates, and the SetParameters slot of each constant-force effect's
// method table. Those tables belong to dinput8.dll; each one is patched once, and every detour
// looks its original up by the caller's table. Only calls that return into the game image are
// shaped, and only on a private copy of the game's DIEFFECT; anything unexpected passes through.
#define DIRECTINPUT_VERSION 0x0800
#include <windows.h>
#include <dinput.h>
#include <intrin.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <cwchar>
#include <cwctype>
#include "bridge_api.h"

namespace {
constexpr size_t CreateDeviceSlot = 3, CreateEffectSlot = 18, SetParametersSlot = 6;
using DirectInputCreateFn = HRESULT (WINAPI*)(HINSTANCE, DWORD, REFIID, LPVOID*, LPUNKNOWN);
using CreateDeviceFn = HRESULT (STDMETHODCALLTYPE*)(void*, REFGUID, void**, LPUNKNOWN);
using CreateEffectFn = HRESULT (STDMETHODCALLTYPE*)(void*, REFGUID, LPCDIEFFECT, LPDIRECTINPUTEFFECT*, LPUNKNOWN);
using SetParametersFn = HRESULT (STDMETHODCALLTYPE*)(void*, LPCDIEFFECT, DWORD);

constexpr long NominalMax = 10000;     // DI_FFNOMINALMAX
constexpr double SplitHz = 3.0;        // weight below, vibration above
constexpr double MinStep = 0.001, MaxStep = 0.1, RestartGap = 0.25; // seconds

int strength = 100, weight = 100, vibration = 100;
uintptr_t base, imageEnd;
DirectInputCreateFn stockCreate;

// A patched dinput8 method table: the table and the method it held in the patched slot.
struct Patched { std::atomic<void**> table{}; std::atomic<void*> original{}; };
Patched createDevice[4], createEffect[8], setParameters[8];
SRWLOCK patchLock = SRWLOCK_INIT;
std::atomic<unsigned> devices, constantEffects, otherEffects, shaped, clipped, skipped;
std::atomic<long> peakIn, peakOut;

bool fromGame(void* address) {
    const auto at = reinterpret_cast<uintptr_t>(address);
    return at >= base && at < imageEnd;
}
bool copy(const void* from, void* to, size_t size) {
    __try { std::memcpy(to, from, size); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
void** tableOf(void* object) {
    void** table = nullptr;
    return object && copy(object, &table, sizeof(table)) ? table : nullptr;
}
template <size_t N> void* originalFor(Patched (&tables)[N], void* self) {
    void** table = tableOf(self);
    for (auto& entry : tables) if (table && entry.table.load() == table) return entry.original.load();
    return nullptr;
}
// Patches tables[i] slot to detour once per distinct table; false if it is not patched.
template <size_t N> bool patch(Patched (&tables)[N], void** table, size_t slot, void* detour) {
    if (!table) return false;
    AcquireSRWLockExclusive(&patchLock);
    bool ok = false;
    for (auto& entry : tables) {
        if (entry.table.load() == table) { ok = true; break; }
        if (entry.table.load()) continue;
        void* current = nullptr;
        DWORD old{};
        if (!copy(table + slot, &current, sizeof(current)) || !current || current == detour ||
            !VirtualProtect(table + slot, sizeof(void*), PAGE_READWRITE, &old)) break;
        entry.original.store(current);
        entry.table.store(table);
        table[slot] = detour;
        VirtualProtect(table + slot, sizeof(void*), old, &old);
        FlushInstructionCache(GetCurrentProcess(), table + slot, sizeof(void*));
        ok = true;
        break;
    }
    ReleaseSRWLockExclusive(&patchLock);
    return ok;
}
void peak(std::atomic<long>& value, long candidate) {
    long current = value.load();
    while (candidate > current && !value.compare_exchange_weak(current, candidate)) {}
}

// The low-pass split of the one constant-force effect the game updates (GRID2VR's Shaper).
struct Shaper {
    void* effect = nullptr;
    LARGE_INTEGER last{}, frequency{};
    double slow = 0;
    bool primed = false;
    long apply(void* self, long magnitude) {
        LARGE_INTEGER now{};
        QueryPerformanceCounter(&now);
        if (!frequency.QuadPart) QueryPerformanceFrequency(&frequency);
        const double seconds = self == effect && primed ? double(now.QuadPart - last.QuadPart) / double(frequency.QuadPart) : 0;
        effect = self;
        last = now;
        const double x = double(magnitude);
        if (!primed || !(seconds > 0) || seconds > RestartGap) { slow = x; primed = true; }
        else slow += (1.0 - std::exp(-2.0 * 3.14159265358979323846 * SplitHz * std::clamp(seconds, MinStep, MaxStep))) * (x - slow);
        const double out = strength / 100.0 * (weight / 100.0 * slow + vibration / 100.0 * (x - slow));
        return std::lround(std::clamp(out, -double(NominalMax), double(NominalMax)));
    }
} shaper;
SRWLOCK shapeLock = SRWLOCK_INIT;

HRESULT STDMETHODCALLTYPE shapedSetParameters(void* self, LPCDIEFFECT effect, DWORD flags) {
    const auto stock = reinterpret_cast<SetParametersFn>(originalFor(setParameters, self));
    if (!stock) return DIERR_GENERIC;
    DIEFFECT local{};
    DICONSTANTFORCE force{};
    DWORD size = 0;
    LPCDIEFFECT sent = effect;
    if (fromGame(_ReturnAddress()) && (flags & DIEP_TYPESPECIFICPARAMS)) {
        if (effect && copy(effect, &size, sizeof(size)) && size >= sizeof(DIEFFECT_DX5) && size <= sizeof(DIEFFECT) &&
            copy(effect, &local, size) && local.cbTypeSpecificParams == sizeof(DICONSTANTFORCE) && local.lpvTypeSpecificParams &&
            copy(local.lpvTypeSpecificParams, &force, sizeof(force))) {
            const long in = force.lMagnitude;
            AcquireSRWLockExclusive(&shapeLock);
            force.lMagnitude = shaper.apply(self, in);
            ReleaseSRWLockExclusive(&shapeLock);
            local.lpvTypeSpecificParams = &force;
            sent = &local;
            peak(peakIn, std::labs(in));
            peak(peakOut, std::labs(force.lMagnitude));
            if (std::labs(force.lMagnitude) == NominalMax) ++clipped;
            const unsigned count = ++shaped;
            if (count == 1 || count % 18000 == 0)
                AutosportLog("Force feedback: %u updates shaped, peak in=%ld out=%ld, %u clipped, %u skipped", count,
                             peakIn.load(), peakOut.load(), clipped.load(), skipped.load());
        } else {
            ++skipped;
        }
    }
    return stock(self, sent, flags);
}

HRESULT STDMETHODCALLTYPE observedCreateEffect(void* self, REFGUID kind, LPCDIEFFECT effect, LPDIRECTINPUTEFFECT* output, LPUNKNOWN outer) {
    const auto stock = reinterpret_cast<CreateEffectFn>(originalFor(createEffect, self));
    if (!stock) return DIERR_GENERIC;
    const HRESULT hr = stock(self, kind, effect, output, outer);
    if (SUCCEEDED(hr) && output && fromGame(_ReturnAddress())) {
        GUID type{};
        LPDIRECTINPUTEFFECT created = nullptr;
        if (copy(&kind, &type, sizeof(type)) && IsEqualGUID(type, GUID_ConstantForce) && copy(output, &created, sizeof(created)) && created) {
            const bool hooked = patch(setParameters, tableOf(created), SetParametersSlot, reinterpret_cast<void*>(&shapedSetParameters));
            const unsigned count = ++constantEffects;
            if (count <= 8) AutosportLog("Force feedback: the game created constant-force effect %p (%u), shaping=%d", created, count, hooked);
        } else {
            const unsigned count = ++otherEffects;
            if (count <= 8) AutosportLog("Force feedback: the game created another effect kind %08lX (%u), left unchanged", type.Data1, count);
        }
    }
    return hr;
}

HRESULT STDMETHODCALLTYPE observedCreateDevice(void* self, REFGUID guid, void** output, LPUNKNOWN outer) {
    const auto stock = reinterpret_cast<CreateDeviceFn>(originalFor(createDevice, self));
    if (!stock) return DIERR_GENERIC;
    const HRESULT hr = stock(self, guid, output, outer);
    if (SUCCEEDED(hr) && output && fromGame(_ReturnAddress())) {
        void* device = nullptr;
        GUID id{};
        if (copy(output, &device, sizeof(device)) && device) {
            const bool hooked = patch(createEffect, tableOf(device), CreateEffectSlot, reinterpret_cast<void*>(&observedCreateEffect));
            const unsigned count = ++devices;
            if (count <= 8) AutosportLog("Force feedback: the game opened device %08lX, effects watched=%d",
                                         copy(&guid, &id, sizeof(id)) ? id.Data1 : 0ul, hooked);
        }
    }
    return hr;
}

HRESULT WINAPI observedDirectInputCreate(HINSTANCE instance, DWORD version, REFIID iid, LPVOID* output, LPUNKNOWN outer) {
    const HRESULT hr = stockCreate(instance, version, iid, output, outer);
    if (SUCCEEDED(hr) && output && fromGame(_ReturnAddress())) {
        void* object = nullptr;
        if (copy(output, &object, sizeof(object)) && object) {
            const bool hooked = patch(createDevice, tableOf(object), CreateDeviceSlot, reinterpret_cast<void*>(&observedCreateDevice));
            AutosportLog("Force feedback: the game created DirectInput, devices watched=%d", hooked);
        }
    }
    return hr;
}

// "-gasvrffb=S,W,V": three whole percentages, strength 0-100, weight 1-100, vibration 0-100.
bool parse(const wchar_t* cmd) {
    const wchar_t* at = wcsstr(cmd, L" -gasvrffb=");
    if (!at) return false;
    at += wcslen(L" -gasvrffb=");
    int values[3]{};
    for (int i = 0; i < 3; ++i) {
        if (!iswdigit(*at)) return false;
        wchar_t* end = nullptr;
        const long value = wcstol(at, &end, 10);
        if (end - at > 3 || value > 100) return false;
        values[i] = int(value);
        at = end;
        if (i < 2 && *at++ != L',') return false;
    }
    if (*at && *at != L' ') return false;
    if (values[1] < 1) return false;
    strength = values[0]; weight = values[1]; vibration = values[2];
    return true;
}
}

void AutosportConfigureForceFeedback(const BYTE* image, const wchar_t* commandLine) {
    static bool attempted;
    if (attempted || !image || !commandLine) return;
    attempted = true;
    if (!wcsstr(commandLine, L" -gasvrffb=")) return;
    if (!parse(commandLine)) { AutosportLog("Force feedback: -gasvrffb is not STRENGTH,WEIGHT,VIBRATION; the game's force is unchanged"); return; }
    if (strength == 100 && weight == 100 && vibration == 100) { AutosportLog("Force feedback: 100/100/100 is the game's own force; nothing hooked"); return; }
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(image);
    const auto* pe = reinterpret_cast<const IMAGE_NT_HEADERS32*>(image + dos->e_lfanew);
    base = reinterpret_cast<uintptr_t>(image);
    imageEnd = base + pe->OptionalHeader.SizeOfImage;
    const bool hooked = AutosportHookGameImport("DINPUT8.dll", "DirectInput8Create", reinterpret_cast<void*>(&observedDirectInputCreate),
                                                reinterpret_cast<void**>(&stockCreate));
    AutosportLog("Force feedback: strength=%d%% weight=%d%% vibration=%d%%, DirectInput8Create routed=%d", strength, weight, vibration, hooked);
}
