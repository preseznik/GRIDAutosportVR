#include "bridge_api.h"
#include "native_ovr.h"
#include "play_session.h"
#include <atomic>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <cstdlib>
#include <xinput.h>

namespace {
const BYTE* image;
wchar_t directory[MAX_PATH]{};
bool headset;
bool regularPlay;
bool practiceSession; // A play session started with its own -benchmarkAttract file (launcher Practice / Race).
bool hudFollowsView;
unsigned eyeWidth, eyeHeight;
bool keepActive = true;
bool testPad; // -gasvrtestpad=1: harness-driven virtual gamepad (hookTestPad).
bool uiComparison;
bool compareSlate;
unsigned captures;
unsigned lastCapturedRaceFrame;
bool adapterReady;
bool captured;
unsigned captureTime = 90;
unsigned captureFrame;
float captureSeconds;
SRWLOCK logLock = SRWLOCK_INIT;
constexpr DWORD benchmark = 0xf399b8;
enum class Phase { Startup, Loading, Scene };
std::atomic<Phase> phase{Phase::Startup};

bool supportedHost() {
    wchar_t filename[MAX_PATH]{};
    const DWORD count = GetModuleFileNameW(nullptr, filename, MAX_PATH);
    if (!count || count >= MAX_PATH) return false;
    const wchar_t* name = wcsrchr(filename, L'\\');
    if (!name || _wcsicmp(name + 1, L"GRIDAutosport_avx.exe")) return false;
    const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(image);
    const auto pe = reinterpret_cast<const IMAGE_NT_HEADERS32*>(image + dos->e_lfanew);
    if (pe->FileHeader.Machine != IMAGE_FILE_MACHINE_I386 || pe->FileHeader.TimeDateStamp != 0x5c8007d1 ||
        pe->OptionalHeader.SizeOfImage != 0x1083000) return false;
    return !memcmp(image + 0xe0d83c, "gasxr.dll", 10) &&
           !memcmp(image + 0x6792ec, "\xc5\xfa\x11\x89\xec\x45\x00\x00", 8);
}

bool commandSession(wchar_t (&mode)[16], wchar_t (&seconds)[8]) {
    // Steam starts the renderer itself and does not inherit the test runner's environment.
    const wchar_t* cmd = GetCommandLineW();
    keepActive = !wcsstr(cmd, L" -gasvrkeepactive=0");
    testPad = wcsstr(cmd, L" -gasvrtestpad=1") != nullptr;
    constexpr wchar_t playArg[] = L" -gasvrplay=";
    if (const wchar_t* flag = wcsstr(cmd, playArg)) {
        const wchar_t* value = flag + ARRAYSIZE(playArg) - 1;
        const size_t length = wcscspn(value, L" \t");
        const wchar_t* xr = wcsstr(cmd, L" -gasvrmode=openxr");
        if (length != 32 || !xr || wcscspn(xr + 1, L" \t") != wcslen(L"-gasvrmode=openxr") ||
            wcsstr(value, playArg) || wcsstr(cmd, L" -gasvrdir=")) return false;
        wchar_t token[33]{}, localData[MAX_PATH]{};
        wmemcpy(token, value, length);
        // Launcher Practice / Race: only this session's own file through the attract flow.
        if (const wchar_t* bench = wcsstr(cmd, L" -benchmark")) {
            wchar_t expected[80]{};
            swprintf_s(expected, L" -benchmarkAttract GASVR_%s.xml", token);
            const size_t size = wcslen(expected);
            if (wcsncmp(bench, expected, size) || (bench[size] && bench[size] != L' ' && bench[size] != L'\t') ||
                wcsstr(bench + size, L" -benchmark")) return false;
            practiceSession = true;
        }
        DWORD count = GetEnvironmentVariableW(L"LOCALAPPDATA", localData, ARRAYSIZE(localData));
        if (!count || count >= ARRAYSIZE(localData) || !AutosportPlayDirectory(token, localData, directory)) return false;
        const wchar_t* follow = wcsstr(cmd, L" -gasvrhudfollow=1");
        hudFollowsView = follow && wcscspn(follow + 1, L" \t") == wcslen(L"-gasvrhudfollow=1");
        wcscpy_s(mode, L"openxr");
        regularPlay = true;
        return true;
    }
    // -benchmarkAttract runs the same session file through the game's attract flow (Practice/Race research).
    if (!wcsstr(cmd, L"-benchmark GASVR_") && !wcsstr(cmd, L"-benchmarkAttract GASVR_")) return false;
    hudFollowsView = wcsstr(cmd, L" -gasvrhudfollow=1") != nullptr;
    uiComparison = wcsstr(cmd, L" -gasvruicheck=1") && !wcsstr(cmd, L"-gasvrmode=openxr");
    compareSlate = uiComparison && wcsstr(cmd, L" -gasvrcompareslate=1");
    const wchar_t* arg = wcsstr(cmd, L"-gasvrdir=");
    if (!arg) return false;
    arg += wcslen(L"-gasvrdir=");
    const bool quoted = *arg == L'"';
    if (quoted) ++arg;
    const size_t len = wcscspn(arg, quoted ? L"\"" : L" \t\"");
    if (len < 3 || len >= MAX_PATH - 16 || arg[1] != L':' || arg[2] != L'\\') return false;
    wmemcpy(directory, arg, len);
    directory[len] = 0;
    wcscpy_s(mode, wcsstr(cmd, L" -gasvrmode=openxr") ? L"openxr" : L"capture");
    if (const wchar_t* time = wcsstr(cmd, L" -gasvrseconds=")) {
        time += wcslen(L" -gasvrseconds=");
        const size_t digits = wcsspn(time, L"0123456789");
        if (digits > 0 && digits <= 3) wcsncpy_s(seconds, time, digits);
    }
    return true;
}
}

BOOL WINAPI DllMain(HINSTANCE self, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(self);
        image = reinterpret_cast<const BYTE*>(GetModuleHandleW(nullptr));
        wchar_t mode[16]{}, seconds[8]{};
        if (!supportedHost() || !commandSession(mode, seconds)) return TRUE;
        headset = !wcscmp(mode, L"openxr");
        if (!headset && wcscmp(mode, L"capture")) return TRUE;
        if (*seconds) {
            wchar_t* end;
            const unsigned long n = wcstoul(seconds, &end, 10);
            if (*seconds && !*end && n <= 240) captureTime = n;
        }
        adapterReady = AutosportInstallNativeOvr();
        if (!adapterReady) return FALSE;
    }
    return TRUE;
}

void AutosportLog(const char* format, ...) {
    if (!directory[0]) return;
    wchar_t path[MAX_PATH]{};
    if (swprintf_s(path, L"%s\\bridge.log", directory) < 0) return;
    char text[1200]{};
    va_list args;
    va_start(args, format);
    vsnprintf_s(text, sizeof(text), _TRUNCATE, format, args);
    va_end(args);
    char line[1320]{};
    const int size = snprintf(line, sizeof(line), "[%llu] %s\r\n", GetTickCount64(), text);
    AcquireSRWLockExclusive(&logLock);
    HANDLE file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file != INVALID_HANDLE_VALUE) {
        DWORD written;
        if (size > 0 && size < sizeof(line)) WriteFile(file, line, size, &written, nullptr);
        CloseHandle(file);
    }
    ReleaseSRWLockExclusive(&logLock);
}

namespace {
// Startup and attract videos play through bink2w32.dll. Log each open and close so a
// session log shows which native frames carried a video. Behaviour is unchanged.
using BinkOpenFn = void* (__stdcall*)(const char*, unsigned);
using BinkCloseFn = void (__stdcall*)(void*);
BinkOpenFn stockBinkOpen;
BinkCloseFn stockBinkClose;
constexpr unsigned binkFileHandle = 0x00800000; // BINKFILEHANDLE: the name is a HANDLE.

void copyVideoName(const char* name, unsigned flags, char (&out)[160]) {
    strcpy_s(out, "(handle)");
    if (!name || (flags & binkFileHandle)) return;
    __try { strncpy_s(out, name, _TRUNCATE); }
    __except (EXCEPTION_EXECUTE_HANDLER) { strcpy_s(out, "(unreadable)"); }
}
void* __stdcall loggedBinkOpen(const char* name, unsigned flags) {
    char label[160];
    copyVideoName(name, flags, label);
    void* video = stockBinkOpen(name, flags);
    AutosportLog("Native video open name=%s flags=%08x handle=%p", label, flags, video);
    return video;
}
void __stdcall loggedBinkClose(void* video) {
    AutosportLog("Native video close handle=%p", video);
    stockBinkClose(video);
}

// Replace one import of the game image, by name (or by ordinal when function is null). The
// original stays callable through *original.
bool hookImport(const char* dll, const char* function, void* replacement, void** original, WORD ordinal = 0) {
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(image);
    const auto* pe = reinterpret_cast<const IMAGE_NT_HEADERS32*>(image + dos->e_lfanew);
    const auto& imports = pe->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!imports.VirtualAddress) return false;
    for (auto* desc = reinterpret_cast<const IMAGE_IMPORT_DESCRIPTOR*>(image + imports.VirtualAddress);
         desc->Name; ++desc) {
        if (_stricmp(reinterpret_cast<const char*>(image + desc->Name), dll) || !desc->OriginalFirstThunk) continue;
        const auto* names = reinterpret_cast<const IMAGE_THUNK_DATA32*>(image + desc->OriginalFirstThunk);
        auto* slots = reinterpret_cast<IMAGE_THUNK_DATA32*>(const_cast<BYTE*>(image) + desc->FirstThunk);
        for (; names->u1.AddressOfData; ++names, ++slots) {
            if (IMAGE_SNAP_BY_ORDINAL32(names->u1.Ordinal)) {
                if (function || IMAGE_ORDINAL32(names->u1.Ordinal) != ordinal) continue;
            } else {
                const auto* byName = reinterpret_cast<const IMAGE_IMPORT_BY_NAME*>(image + names->u1.AddressOfData);
                if (!function || strcmp(reinterpret_cast<const char*>(byName->Name), function)) continue;
            }
            DWORD protection{};
            if (!VirtualProtect(&slots->u1.Function, sizeof(DWORD), PAGE_READWRITE, &protection)) return false;
            *original = reinterpret_cast<void*>(slots->u1.Function);
            slots->u1.Function = reinterpret_cast<DWORD>(replacement);
            DWORD previous{};
            VirtualProtect(&slots->u1.Function, sizeof(DWORD), protection, &previous);
            return *original != nullptr;
        }
    }
    return false;
}
void hookVideoLogging() {
    static bool attempted;
    if (attempted || !adapterReady || !directory[0]) return;
    attempted = true; // The renderer's D3D11 device exists, so the game's imports are bound.
    const bool open = hookImport("bink2w32.dll", "_BinkOpen@8", reinterpret_cast<void*>(&loggedBinkOpen),
                                 reinterpret_cast<void**>(&stockBinkOpen));
    const bool close = hookImport("bink2w32.dll", "_BinkClose@4", reinterpret_cast<void*>(&loggedBinkClose),
                                  reinterpret_cast<void**>(&stockBinkClose));
    AutosportLog("Native video logging open=%d close=%d", open, close);
}

// Test only, never passed by the launcher: with -gasvrtestpad=1 controller 0 is a virtual
// gamepad whose buttons the xr-sim harness sets by writing pad.txt (hex XINPUT_GAMEPAD
// wButtons) into the session folder. The game reads the keyboard through Raw Input and
// DirectInput, which a test cannot reach without taking the desktop's focus.
using XInputGetStateFn = DWORD (WINAPI*)(DWORD, XINPUT_STATE*);
using XInputGetCapabilitiesFn = DWORD (WINAPI*)(DWORD, DWORD, XINPUT_CAPABILITIES*);
XInputGetStateFn stockXInputGetState;
XInputGetCapabilitiesFn stockXInputGetCapabilities;
WORD testPadButtons() {
    static ULONGLONG checked;
    static WORD buttons;
    const ULONGLONG now = GetTickCount64();
    if (now - checked < 30) return buttons;
    checked = now;
    wchar_t path[MAX_PATH]{};
    char text[16]{};
    DWORD read{};
    swprintf_s(path, L"%s\\pad.txt", directory);
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
    if (file == INVALID_HANDLE_VALUE) return buttons = 0;
    ReadFile(file, text, sizeof(text) - 1, &read, nullptr);
    CloseHandle(file);
    const WORD next = WORD(strtoul(text, nullptr, 16));
    if (next != buttons) AutosportLog("Test pad buttons=%04x", next);
    return buttons = next;
}
DWORD WINAPI testPadGetState(DWORD user, XINPUT_STATE* state) {
    if (user != 0 || !state) return stockXInputGetState(user, state);
    static DWORD packet;
    static WORD last = 0xffff;
    const WORD buttons = testPadButtons();
    if (buttons != last) { last = buttons; ++packet; }
    *state = {};
    state->dwPacketNumber = packet;
    state->Gamepad.wButtons = buttons;
    return ERROR_SUCCESS;
}
DWORD WINAPI testPadCapabilities(DWORD user, DWORD flags, XINPUT_CAPABILITIES* caps) {
    if (user != 0 || !caps) return stockXInputGetCapabilities(user, flags, caps);
    *caps = {};
    caps->Type = XINPUT_DEVTYPE_GAMEPAD;
    caps->SubType = XINPUT_DEVSUBTYPE_GAMEPAD;
    caps->Gamepad.wButtons = 0xf3ff;
    return ERROR_SUCCESS;
}
// The game ignores input while its window is not the foreground one, and a test never takes the
// desktop's focus. With the test pad only, these report the game's own window as foreground.
using WindowQueryFn = HWND (WINAPI*)();
WindowQueryFn stockForeground, stockActive, stockFocus;
HWND ownWindow() {
    static HWND found;
    if (found && IsWindow(found)) return found;
    EnumWindows([](HWND window, LPARAM) -> BOOL {
        DWORD pid{};
        GetWindowThreadProcessId(window, &pid);
        if (pid != GetCurrentProcessId() || !IsWindowVisible(window) || GetWindow(window, GW_OWNER)) return TRUE;
        found = window;
        return FALSE;
    }, 0);
    return found;
}
HWND WINAPI testForeground() { HWND own = ownWindow(); return own ? own : stockForeground(); }
HWND WINAPI testActive() { HWND own = ownWindow(); return own ? own : stockActive(); }
HWND WINAPI testFocus() { HWND own = ownWindow(); return own ? own : stockFocus(); }

void hookTestPad() {
    static bool attempted;
    if (attempted || !testPad || !adapterReady || !directory[0]) return;
    attempted = true;
    const bool windows = hookImport("user32.dll", "GetForegroundWindow", reinterpret_cast<void*>(&testForeground), reinterpret_cast<void**>(&stockForeground)) &&
                         hookImport("user32.dll", "GetActiveWindow", reinterpret_cast<void*>(&testActive), reinterpret_cast<void**>(&stockActive)) &&
                         hookImport("user32.dll", "GetFocus", reinterpret_cast<void*>(&testFocus), reinterpret_cast<void**>(&stockFocus));
    AutosportLog("Test pad foreground report=%d", windows);
    const bool state = hookImport("xinput1_3.dll", nullptr, reinterpret_cast<void*>(&testPadGetState),
                                  reinterpret_cast<void**>(&stockXInputGetState), 2);
    const bool caps = hookImport("xinput1_3.dll", nullptr, reinterpret_cast<void*>(&testPadCapabilities),
                                 reinterpret_cast<void**>(&stockXInputGetCapabilities), 4);
    AutosportLog("Test pad on controller 0 state=%d capabilities=%d", state, caps);
}
}

extern "C" HRESULT WINAPI AutosportCreateDevice(IDXGIAdapter* adapter, D3D_DRIVER_TYPE type, HMODULE software,
        UINT flags, const D3D_FEATURE_LEVEL* levels, UINT count, UINT sdk, ID3D11Device** device,
        D3D_FEATURE_LEVEL* achieved, ID3D11DeviceContext** context) {
    hookVideoLogging();
    hookTestPad();
    const HRESULT hr = D3D11CreateDevice(adapter, type, software, flags, levels, count, sdk, device, achieved, context);
    AutosportLog("D3D11 creation hr=%08lx nativeAdapter=%d device=%p captureRaceSecond=%u practiceSession=%d",
                 hr, adapterReady, SUCCEEDED(hr) && device ? (void*)*device : nullptr, captureTime, practiceSession);
    return hr;
}

bool AutosportUseHeadset() { return headset; }
bool AutosportRegularPlay() { return regularPlay; }
// -gasvrrecenterkey=VK -gasvrrecentermods=MASK (1 Ctrl, 2 Alt, 4 Shift): the launcher's recenter
// shortcut. -gasvrkeephorizon=0 also aligns pitch and roll to the head when recentering.
bool AutosportRecenterShortcut(unsigned& key, unsigned& modifiers) {
    static unsigned parsedKey, parsedModifiers;
    static const bool valid = [] {
        const wchar_t* cmd = GetCommandLineW();
        const wchar_t* arg = wcsstr(cmd, L" -gasvrrecenterkey=");
        unsigned value{}, mods{};
        if (!arg || swscanf_s(arg, L" -gasvrrecenterkey=%u", &value) != 1 || value < 8 || value > 254) return false;
        if (const wchar_t* modArg = wcsstr(cmd, L" -gasvrrecentermods=")) {
            if (swscanf_s(modArg, L" -gasvrrecentermods=%u", &mods) != 1 || mods > 7) return false;
        }
        parsedKey = value; parsedModifiers = mods;
        return true;
    }();
    key = parsedKey; modifiers = parsedModifiers;
    return valid;
}
bool AutosportKeepHorizonLevel() {
    static const bool keep = wcsstr(GetCommandLineW(), L" -gasvrkeephorizon=0") == nullptr;
    return keep;
}
// -gasvreyesize=WIDTHxHEIGHT: the per-eye size reported to the game (density 1). Absent: the
// legacy 960x1080 per eye from the fixed 1920x1080 HMD descriptor.
bool AutosportEyeSizeRequest(unsigned& width, unsigned& height) {
    static const bool parsed = [] {
        const wchar_t* arg = wcsstr(GetCommandLineW(), L" -gasvreyesize=");
        unsigned w{}, h{};
        if (arg && swscanf_s(arg, L" -gasvreyesize=%ux%u", &w, &h) == 2 && w >= 64 && h >= 64 && w <= 8192 && h <= 8192) {
            eyeWidth = w; eyeHeight = h;
            return true;
        }
        return false;
    }();
    if (!parsed) return false;
    width = eyeWidth; height = eyeHeight;
    return true;
}
bool AutosportHudFollowsView() { return uiComparison ? captures == 1 : hudFollowsView; }
// Every bridge session (headset play and the capture benchmark) keeps the game running when its
// window is inactive; the game otherwise drops to ~18 fps and pauses its startup videos.
bool AutosportKeepActive() { return keepActive; }
bool AutosportUiComparison() { return uiComparison; }
bool AutosportCompareSlate() { return compareSlate; }
const char* AutosportTransitionSample() {
    if (!uiComparison) return nullptr;
    static int lastPhase = -1;
    static ULONGLONG since;
    static unsigned slot;
    const int current = static_cast<int>(phase.load());
    const auto now = GetTickCount64();
    if (current != lastPhase) {
        since = now;
        slot = 0;
        lastPhase = current;
    }
    constexpr unsigned offsets[] = {120, 1400, 4000};
    if (slot >= ARRAYSIZE(offsets) || now - since < offsets[slot]) return nullptr;
    ++slot;
    constexpr const char* names[] = {"startup", "loading", "scene"};
    return names[current];
}
// Normal play before the first StateLoading: the startup videos. -gasvrstartupsource=eye keeps
// the HMD eye texture as the seated screen's source there (the pre-2026-11-10 behaviour).
bool AutosportStartupFromBackBuffer() {
    static const bool eye = wcsstr(GetCommandLineW(), L" -gasvrstartupsource=eye") != nullptr;
    return regularPlay && headset && !eye && phase.load() == Phase::Startup;
}
bool AutosportFlatScreen() {
    const Phase current = phase.load();
    if (current != Phase::Startup) return current == Phase::Loading;
    if (regularPlay) return true;
    // A benchmark can resume directly into a live race without another loading event.
    const BYTE* state = image + benchmark;
    return !(*reinterpret_cast<const unsigned*>(state + 0x84) == 1 && state[0x94] == 1 &&
             state[0x8a] == 0 && *reinterpret_cast<const unsigned*>(state + 0x45e8) > 0);
}

// These two call sites dispatch the paired enter/leave messages from StateLoading.
// Preserve the original event handling. Both calls use __thiscall and pop one argument.
using LoadingDispatch = void (__thiscall*)(void*, const void*);
void __fastcall AutosportLoadingEntered(void* dispatcher, void*, const void* event) {
    phase.store(Phase::Loading);
    AutosportLog("StateLoading entered");
    reinterpret_cast<LoadingDispatch>(image + 0x9f6c80)(dispatcher, event);
}
void __fastcall AutosportLoadingExited(void* dispatcher, void*, const void* event) {
    reinterpret_cast<LoadingDispatch>(image + 0x9f6c80)(dispatcher, event);
    phase.store(Phase::Scene);
    AutosportLog("StateLoading exited");
}
const wchar_t* AutosportRunDirectory() { return directory; }
bool AutosportCaptureRequested() {
    if (regularPlay || captured || !adapterReady) return false;
    const BYTE* state = image + benchmark;
    if (*reinterpret_cast<const unsigned*>(state + 0x84) != 1 || state[0x94] != 1 || state[0x8a] != 0) return false;
    captureFrame = *reinterpret_cast<const unsigned*>(state + 0x45e8);
    captureSeconds = *reinterpret_cast<const float*>(state + 0x45ec);
    return captureFrame > lastCapturedRaceFrame + (captures ? 3u : 0u) && std::isfinite(captureSeconds) &&
           captureSeconds >= captureTime && captureSeconds < captureTime + 8;
}
void AutosportCaptureCompleted() {
    AutosportLog("Captured active benchmark raceFrame=%u raceSeconds=%.6f hudFollow=%d", captureFrame, captureSeconds, AutosportHudFollowsView());
    lastCapturedRaceFrame = captureFrame;
    ++captures;
    captured = !uiComparison || captures == 2;
}
