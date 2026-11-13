// Per-car seat adjustment in the cockpit view (GRID2VR's Controls -> Seat and in-game Tab panel).
//
// Offsets are millimetres right / up / forward of the game's own head-cam position, one line per
// car in <data>\seat-adjustments.txt ("car right up forward"; <data> is the launcher's data folder,
// two levels above the session folder). The launcher edits the same file under Controls -> Seat and
// writes the car names the panel shows to <data>\car-names.txt ("car<TAB>name").
// The car is the one whose head-cam the race start selected (native_ovr raceStartView); the offset
// is added to the head pose only while that head-cam is the live view (AutosportCockpitView).
//
// In game, the panel key (default Tab) in the cockpit opens the seat panel (an OpenXR quad drawn by
// xr_bridge, GRID2VR's layout): the up / down keys move the seat up and down, back / forward move it
// back and forward, and with Shift left and right, 1 cm a press; Backspace resets this car; Enter or
// the panel key saves; Esc cancels. -gasvrseatkeys=PANEL,UP,DOWN,BACK,FORWARD (Windows virtual-key
// codes) rebinds the five; the default is Tab and the arrow keys. While the panel is open those
// keys, Shift, Enter, Esc and Backspace are withheld from the game (window messages, Raw Input
// and GetAsyncKeyState).
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
constexpr int limit = 500, step = 10;
enum KeyIndex { PanelKey, UpKey, DownKey, BackKey, ForwardKey, KeyCount };
UINT keys[KeyCount] = {VK_TAB, VK_UP, VK_DOWN, VK_LEFT, VK_RIGHT};
std::mutex lock;
std::map<std::string, Offset> saved;
std::map<std::string, std::wstring> names;
wchar_t file[MAX_PATH];
char car[8];
Offset live{}, before{};
bool open, sideways;
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

void loadNames(const wchar_t* data) {
    wchar_t path[MAX_PATH];
    swprintf_s(path, L"%s\\car-names.txt", data);
    FILE* f = _wfsopen(path, L"rb", _SH_DENYWR);
    if (!f) return;
    char line[256];
    while (fgets(line, sizeof(line), f) && names.size() < 512) {
        char* tab = std::strchr(line, '\t');
        if (!tab) continue;
        *tab = 0;
        if (!validCar(line)) continue;
        char* name = tab + 1;
        name[std::strcspn(name, "\r\n")] = 0;
        wchar_t wide[96]{};
        if (*name && MultiByteToWideChar(CP_UTF8, 0, name, -1, wide, 95) > 0) names[line] = wide;
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
    for (UINT key : keys) if (vk == key) return true;
    switch (vk) {
    case VK_RETURN: case VK_ESCAPE: case VK_BACK: case VK_SHIFT: case VK_LSHIFT: case VK_RSHIFT: return true;
    default: return false;
    }
}

bool shiftHeld() { return (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0; }

void parseKeys() {
    const wchar_t* arg = wcsstr(GetCommandLineW(), L" -gasvrseatkeys=");
    if (!arg) return;
    unsigned v[KeyCount]{};
    if (swscanf_s(arg, L" -gasvrseatkeys=%u,%u,%u,%u,%u", &v[0], &v[1], &v[2], &v[3], &v[4]) != KeyCount) return;
    for (unsigned i = 0; i < KeyCount; ++i) {
        if (v[i] < 1 || v[i] > 254 || v[i] == VK_RETURN || v[i] == VK_ESCAPE || v[i] == VK_BACK || v[i] == VK_SHIFT ||
            v[i] == VK_CONTROL || v[i] == VK_MENU) return;
        for (unsigned j = 0; j < i; ++j) if (v[i] == v[j]) return;
    }
    for (unsigned i = 0; i < KeyCount; ++i) keys[i] = v[i];
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
    parseKeys();
    swprintf_s(file, L"%s\\seat-adjustments.txt", data);
    load();
    loadNames(data);
    const bool raw = AutosportHookGameImport("user32.dll", "GetRawInputData", reinterpret_cast<void*>(&filteredRawData),
                                              reinterpret_cast<void**>(&stockRawData));
    const bool async = AutosportHookGameImport("user32.dll", "GetAsyncKeyState", reinterpret_cast<void*>(&filteredAsyncKey),
                                                reinterpret_cast<void**>(&stockAsyncKey));
    AutosportLog("Seat adjustments: %u car(s) saved, %u car names; keys panel=%u up=%u down=%u back=%u forward=%u; key filter raw=%d async=%d",
                 unsigned(saved.size()), unsigned(names.size()), keys[PanelKey], keys[UpKey], keys[DownKey], keys[BackKey],
                 keys[ForwardKey], raw, async);
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
        if (vk != keys[PanelKey] || !down || repeat || !car[0] || !AutosportCockpitView()) return false;
        open = true; before = live; ++version;
        AutosportLog("Seat panel opened for %s", car);
        return true;
    }
    if (!blocked(vk)) return false;
    if (vk == VK_SHIFT || vk == VK_LSHIFT || vk == VK_RSHIFT) { ++version; return true; } // Redraw BACK / FORWARD as LEFT / RIGHT.
    if (!down) return true;
    const bool side = shiftHeld();
    if (vk == keys[UpKey]) live.up = clamp(live.up + step);
    else if (vk == keys[DownKey]) live.up = clamp(live.up - step);
    else if (vk == keys[BackKey]) { if (side) live.right = clamp(live.right - step); else live.forward = clamp(live.forward - step); }
    else if (vk == keys[ForwardKey]) { if (side) live.right = clamp(live.right + step); else live.forward = clamp(live.forward + step); }
    else if (vk == VK_BACK) live = {};
    else if (vk == VK_ESCAPE) {
        if (repeat) return true;
        live = before; open = false;
        AutosportLog("Seat panel cancelled for %s", car);
    } else if (vk == keys[PanelKey] || vk == VK_RETURN) {
        if (repeat) return true;
        saved[car] = live; open = false;
        const bool ok = *file && store();
        AutosportLog("Seat %s saved%s: right %d mm, up %d mm, forward %d mm", car, ok ? "" : " (file not written)",
                     live.right, live.up, live.forward);
    } else return true;
    ++version;
    return true;
}

// The seat panel's contents while it is open; version changes whenever they do.
bool AutosportSeatPanel(SeatPanelState& state) {
    std::lock_guard<std::mutex> guard(lock);
    if (!open) { state.version = version; return false; }
    const bool side = shiftHeld();
    if (side != sideways) { sideways = side; ++version; }
    state.version = version;
    const auto found = names.find(car);
    if (found != names.end()) wcsncpy_s(state.name, found->second.c_str(), _TRUNCATE);
    else swprintf_s(state.name, L"%hs", car);
    state.right = live.right; state.up = live.up; state.forward = live.forward;
    state.sideways = sideways;
    for (unsigned i = 0; i < KeyCount; ++i) state.keys[i] = keys[i];
    return true;
}
