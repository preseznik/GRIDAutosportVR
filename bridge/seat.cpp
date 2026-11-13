// Per-car seat adjustment in the cockpit view (GRID2VR's Controls -> Seat and in-game Tab panel).
//
// Offsets are millimetres right / up / forward of the game's own head-cam position, one line per
// car in <data>\seat-adjustments.txt ("car right up forward"; <data> is the launcher's data folder,
// two levels above the session folder). The launcher edits the same file under Controls -> Seat.
// The car is the one whose head-cam the race start selected (native_ovr raceStartView); the offset
// is added to the head pose only while that head-cam is the live view (AutosportCockpitView).
//
// In game, Tab in the cockpit opens the seat panel (an OpenXR quad drawn by xr_bridge):
// Left / Right, Up / Down (forward / back) and Page Up / Page Down (height) move the seat 1 cm,
// with Shift 5 mm; Backspace resets this car; Enter or Tab saves; Esc cancels. While it is open
// those keys are withheld from the game (window messages, Raw Input and GetAsyncKeyState).
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <map>
#include <mutex>
#include <string>
#include "bridge_api.h"

namespace {
struct Offset { int right, up, forward; }; // millimetres
constexpr int limit = 500;
std::mutex lock;
std::map<std::string, Offset> saved;
wchar_t file[MAX_PATH];
char car[8];
Offset live{}, before{};
bool open;
unsigned version = 1;

bool validCar(const char* id) {
    return std::strlen(id) == 3 && std::strspn(id, "abcdefghijklmnopqrstuvwxyz0123456789") == 3;
}
int clamp(int v) { return v < -limit ? -limit : v > limit ? limit : v; }

void load() {
    saved.clear();
    FILE* f = _wfsopen(file, L"rb", _SH_DENYWR);
    if (!f) return;
    char line[128];
    while (fgets(line, sizeof(line), f) && saved.size() < 256) {
        char id[16]{};
        int r = 0, u = 0, fw = 0;
        if (line[0] == '#') continue;
        if (sscanf_s(line, "%15s %d %d %d", id, unsigned(sizeof(id)), &r, &u, &fw) == 4 && validCar(id))
            saved[id] = {clamp(r), clamp(u), clamp(fw)};
    }
    fclose(f);
}

bool store() {
    wchar_t temp[MAX_PATH];
    swprintf_s(temp, L"%s.tmp", file);
    FILE* f = _wfsopen(temp, L"wb", _SH_DENYWR);
    if (!f) return false;
    fputs("# GRID Autosport VR seat adjustments: car right up forward (millimetres)\n", f);
    for (const auto& [id, o] : saved)
        if (o.right || o.up || o.forward) fprintf(f, "%s %d %d %d\n", id.c_str(), o.right, o.up, o.forward);
    const bool ok = fclose(f) == 0;
    return ok && MoveFileExW(temp, file, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
}

bool blocked(UINT vk) {
    switch (vk) {
    case VK_TAB: case VK_LEFT: case VK_RIGHT: case VK_UP: case VK_DOWN: case VK_PRIOR: case VK_NEXT:
    case VK_RETURN: case VK_ESCAPE: case VK_BACK: case VK_SHIFT: case VK_LSHIFT: case VK_RSHIFT: return true;
    default: return false;
    }
}

using RawDataFn = UINT (WINAPI*)(HRAWINPUT, UINT, LPVOID, PUINT, UINT);
using AsyncKeyFn = SHORT (WINAPI*)(int);
RawDataFn stockRawData;
AsyncKeyFn stockAsyncKey;
UINT WINAPI filteredRawData(HRAWINPUT input, UINT command, LPVOID data, PUINT size, UINT headerSize) {
    const UINT result = stockRawData(input, command, data, size, headerSize);
    if (result == UINT(-1) || command != RID_INPUT || !data || headerSize != sizeof(RAWINPUTHEADER) ||
        result < sizeof(RAWINPUTHEADER) + sizeof(RAWKEYBOARD)) return result;
    auto* raw = static_cast<RAWINPUT*>(data);
    if (raw->header.dwType == RIM_TYPEKEYBOARD && AutosportSeatBlocksKey(raw->data.keyboard.VKey)) {
        raw->data.keyboard.VKey = 0xFF; // KEYBOARD_OVERRUN_MAKE_CODE: no key.
        raw->data.keyboard.MakeCode = 0;
    }
    return result;
}
SHORT WINAPI filteredAsyncKey(int key) {
    const SHORT state = stockAsyncKey(key);
    return key > 0 && key < 256 && AutosportSeatBlocksKey(UINT(key)) ? 0 : state;
}
} // namespace

void AutosportSeatConfigure(const wchar_t* sessionFolder) {
    if (!sessionFolder || !*sessionFolder) return;
    wchar_t data[MAX_PATH];
    wcscpy_s(data, sessionFolder);
    for (int up = 0; up < 2; ++up) {
        wchar_t* slash = wcsrchr(data, L'\\');
        if (!slash) return;
        *slash = 0;
    }
    std::lock_guard<std::mutex> guard(lock);
    swprintf_s(file, L"%s\\seat-adjustments.txt", data);
    load();
    const bool raw = AutosportHookGameImport("user32.dll", "GetRawInputData", reinterpret_cast<void*>(&filteredRawData),
                                              reinterpret_cast<void**>(&stockRawData));
    const bool async = AutosportHookGameImport("user32.dll", "GetAsyncKeyState", reinterpret_cast<void*>(&filteredAsyncKey),
                                                reinterpret_cast<void**>(&stockAsyncKey));
    AutosportLog("Seat adjustments: %u car(s) saved; key filter raw=%d async=%d", unsigned(saved.size()), raw, async);
}

// The race start's head-cam car (an empty ID when the car has none).
void AutosportSeatCar(const char* id) {
    std::lock_guard<std::mutex> guard(lock);
    if (!validCar(id)) { car[0] = 0; live = {}; open = false; ++version; return; }
    if (std::strcmp(car, id)) AutosportLog("Seat car %s", id);
    strcpy_s(car, id);
    const auto found = saved.find(car);
    live = found == saved.end() ? Offset{} : found->second;
    open = false;
    ++version;
    if (live.right || live.up || live.forward)
        AutosportLog("Seat %s: right %d mm, up %d mm, forward %d mm", car, live.right, live.up, live.forward);
}

// Metres right / up / forward to add to the head pose, while the cockpit view is live.
bool AutosportSeatOffset(float (&metres)[3]) {
    if (!AutosportCockpitView()) return false;
    std::lock_guard<std::mutex> guard(lock);
    if (!car[0] || !(live.right || live.up || live.forward)) return false;
    metres[0] = live.right * .001f; metres[1] = live.up * .001f; metres[2] = live.forward * .001f;
    return true;
}

bool AutosportSeatBlocksKey(UINT vk) {
    std::lock_guard<std::mutex> guard(lock);
    return open && blocked(vk);
}

// A key from the game window (down or up). True when the seat panel used it: the game must not see it.
bool AutosportSeatKey(UINT vk, bool down, bool repeat) {
    std::lock_guard<std::mutex> guard(lock);
    if (!open) {
        if (vk != VK_TAB || !down || repeat || !car[0] || !AutosportCockpitView()) return false;
        open = true; before = live; ++version;
        AutosportLog("Seat panel opened for %s", car);
        return true;
    }
    if (!blocked(vk)) return false;
    if (!down) return true;
    const int step = (GetAsyncKeyState(VK_SHIFT) & 0x8000) ? 5 : 10;
    switch (vk) {
    case VK_LEFT: live.right = clamp(live.right - step); break;
    case VK_RIGHT: live.right = clamp(live.right + step); break;
    case VK_UP: live.forward = clamp(live.forward + step); break;
    case VK_DOWN: live.forward = clamp(live.forward - step); break;
    case VK_PRIOR: live.up = clamp(live.up + step); break;
    case VK_NEXT: live.up = clamp(live.up - step); break;
    case VK_BACK: live = {}; break;
    case VK_ESCAPE:
        if (repeat) return true;
        live = before; open = false;
        AutosportLog("Seat panel cancelled for %s", car);
        break;
    case VK_TAB: case VK_RETURN: {
        if (repeat) return true;
        saved[car] = live; open = false;
        const bool ok = *file && store();
        AutosportLog("Seat %s saved%s: right %d mm, up %d mm, forward %d mm", car, ok ? "" : " (file not written)",
                     live.right, live.up, live.forward);
        break;
    }
    default: return true;
    }
    ++version;
    return true;
}

// The seat panel's lines while it is open; version changes whenever they do.
bool AutosportSeatPanel(wchar_t (&text)[512], unsigned& changed) {
    std::lock_guard<std::mutex> guard(lock);
    changed = version;
    if (!open) return false;
    // Tab-separated columns (xr_bridge draws them with tab stops).
    swprintf_s(text, L"Seat position  \x2014  %hs\n\nRight\t%+.1f cm\tLeft / Right\nForward\t%+.1f cm\tUp / Down\nHeight\t%+.1f cm\tPage Up / Down\n\n"
                     L"Shift: 5 mm steps.  Backspace: reset.\nEnter or Tab: save.  Esc: cancel.",
               car, live.right / 10.0, live.forward / 10.0, live.up / 10.0);
    return true;
}
