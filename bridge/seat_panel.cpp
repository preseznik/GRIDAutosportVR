// The in-game seat panel's picture, GRID2VR's layout (src/seat_panel.cpp there): a 768x1024
// premultiplied BGRA image with the title, the car, a side view of the seat with red arrows
// (UP / DOWN / BACK / FORWARD, or LEFT / RIGHT while Shift is held), the offsets and the keys.
// xr_bridge uploads it to the panel's OpenXR quad whenever SeatPanelState.version changes.
#include <windows.h>
#include <algorithm>
#include <cwchar>
#include <memory>
#include <objidl.h>
namespace Gdiplus { using std::min; using std::max; }
#include <gdiplus.h>
#include "bridge_api.h"
#pragma comment(lib, "gdiplus.lib")

namespace {
ULONG_PTR gdiplusToken;
bool gdiplusReady;

// A short name for a Windows virtual-key code.
void keyName(UINT vk, wchar_t (&out)[32]) {
    switch (vk) {
    case VK_TAB: wcscpy_s(out, L"Tab"); return;
    case VK_UP: wcscpy_s(out, L"Up"); return;
    case VK_DOWN: wcscpy_s(out, L"Down"); return;
    case VK_LEFT: wcscpy_s(out, L"Left"); return;
    case VK_RIGHT: wcscpy_s(out, L"Right"); return;
    case VK_PRIOR: wcscpy_s(out, L"Page Up"); return;
    case VK_NEXT: wcscpy_s(out, L"Page Down"); return;
    case VK_HOME: wcscpy_s(out, L"Home"); return;
    case VK_END: wcscpy_s(out, L"End"); return;
    case VK_INSERT: wcscpy_s(out, L"Insert"); return;
    case VK_DELETE: wcscpy_s(out, L"Delete"); return;
    case VK_SPACE: wcscpy_s(out, L"Space"); return;
    default: break;
    }
    if (vk >= VK_NUMPAD0 && vk <= VK_NUMPAD9) { swprintf_s(out, L"Num %u", vk - VK_NUMPAD0); return; }
    if (vk >= VK_F1 && vk <= VK_F24) { swprintf_s(out, L"F%u", vk - VK_F1 + 1); return; }
    const UINT scan = MapVirtualKeyW(vk, MAPVK_VK_TO_VSC);
    if (scan && GetKeyNameTextW(LONG(scan << 16), out, 32) > 0) return;
    swprintf_s(out, L"Key %u", vk);
}

// Whole centimetres as GRID2VR shows them; half centimetres (from the launcher) with one decimal.
void centimetres(int millimetres, wchar_t (&out)[16]) {
    if (millimetres % 10 == 0) swprintf_s(out, L"%+d cm", millimetres / 10);
    else swprintf_s(out, L"%+.1f cm", millimetres / 10.0);
}
} // namespace

bool AutosportPaintSeatPanel(const SeatPanelState& state, BYTE* pixels, UINT width, UINT height) {
    using namespace Gdiplus;
    if (!gdiplusReady) {
        GdiplusStartupInput startup;
        if (GdiplusStartup(&gdiplusToken, &startup, nullptr) != Ok) return false;
        gdiplusReady = true;
    }
    Bitmap bitmap(INT(width), INT(height), INT(width * 4), PixelFormat32bppPARGB, pixels);
    if (bitmap.GetLastStatus() != Ok) return false;
    Graphics g(&bitmap);
    // Drawn in GRID2VR's 768x1024 coordinates, scaled to the panel's size.
    g.ScaleTransform(width / 768.f, height / 1024.f);
    g.SetSmoothingMode(SmoothingModeAntiAlias);
    g.SetTextRenderingHint(TextRenderingHintAntiAliasGridFit);
    g.Clear(Color(238, 25, 27, 29));
    const Color redAccent(255, 255, 119, 115); // GRID2VR's dark-theme accent (#ff7773).
    SolidBrush white(Color(255, 245, 245, 245)), accent(redAccent);
    Font title(L"Segoe UI", 43, FontStyleBold, UnitPixel), normal(L"Segoe UI", 29, FontStyleRegular, UnitPixel),
         bold(L"Segoe UI", 32, FontStyleBold, UnitPixel);
    auto label = [&](const wchar_t* text, float x, float y, float w, float h, Font& font, Brush& brush) {
        g.DrawString(text, -1, &font, RectF(x, y, w, h), nullptr, &brush);
    };
    wchar_t panelKey[32], up[32], down[32], back[32], forward[32];
    keyName(state.keys[0], panelKey); keyName(state.keys[1], up); keyName(state.keys[2], down);
    keyName(state.keys[3], back); keyName(state.keys[4], forward);
    wchar_t line[160];
    label(L"SEAT POSITION", 40, 30, 690, 65, title, white);
    label(state.name, 40, 98, 690, 80, normal, white);
    swprintf_s(line, L"%s: save & close", panelKey);
    label(line, 40, 174, 690, 45, normal, accent);
    // The seat from the side, facing right (the car's front).
    Pen seatPen(Color(255, 245, 245, 245), 24);
    seatPen.SetLineJoin(LineJoinRound); seatPen.SetStartCap(LineCapRound); seatPen.SetEndCap(LineCapRound);
    const PointF seatPoints[] = {{330, 315}, {347, 426}, {364, 480}, {445, 480}, {480, 520}};
    g.DrawLines(&seatPen, seatPoints, 5);
    auto arrow = [&](float x, float y, float dx, float dy) {
        Pen pen(redAccent, 9);
        g.DrawLine(&pen, x - dx * 23, y - dy * 23, x + dx * 23, y + dy * 23);
        const PointF triangle[] = {{x + dx * 40, y + dy * 40}, {x + dx * 12 - dy * 20, y + dy * 12 + dx * 20},
                                   {x + dx * 12 + dy * 20, y + dy * 12 - dx * 20}};
        g.FillPolygon(&accent, triangle, 3);
    };
    arrow(384, 254, 0, -1); arrow(384, 555, 0, 1); arrow(126, 406, -1, 0); arrow(642, 406, 1, 0);
    label(L"UP", 356, 286, 90, 45, bold, white);
    label(L"DOWN", 335, 597, 130, 45, bold, white);
    label(state.sideways ? L"LEFT" : L"BACK", 55, 451, 180, 45, bold, white);
    label(state.sideways ? L"RIGHT" : L"FORWARD", 550, 451, 205, 45, bold, white);
    wchar_t h[16], d[16], s[16];
    centimetres(state.up, h); centimetres(state.forward, d); centimetres(state.right, s);
    swprintf_s(line, L"Height      %s\nDepth       %s\nSideways   %s", h, d, s);
    label(line, 175, 670, 550, 145, bold, white);
    swprintf_s(line, L"%s / %s: height   %s / %s: depth", up, down, back, forward);
    label(line, 40, 838, 690, 45, normal, white);
    label(L"Shift: sideways   Enter: save   Esc: cancel\nBackspace: reset", 40, 886, 690, 120, normal, white);
    return g.GetLastStatus() == Ok;
}
