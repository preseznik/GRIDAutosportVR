// Shadow acne in VR: stripes of self-shadowing across sunlit walls that crawl as the head moves.
// The game draws its shadow casters with rasterizer states that carry a depth bias, tuned for the
// desktop camera; each VR eye's much wider view spreads the shadow cascades over more of the world
// per texel, and that bias no longer covers it. ID3D11Device::CreateRasterizerState (vtable slot
// 22) is watched in VR sessions: with diagnostic logging each distinct state is logged once, and
// -gasvrshadowbias=PERCENT (test only; the launcher never passes it) scales the depth bias and
// slope-scaled depth bias of the states that have one. In the headset even 4x made no difference:
// the moving shadows come from the cascades being refitted to each view.
#include "bridge_api.h"
#include <windows.h>
#include <d3d11.h>
#include <algorithm>
#include <cstring>
#include <cwchar>
#include <mutex>
#include <vector>

namespace {
using RasterizerFn = HRESULT(STDMETHODCALLTYPE*)(ID3D11Device*, const D3D11_RASTERIZER_DESC*, ID3D11RasterizerState**);
RasterizerFn stockCreateRasterizer;
bool logStates;
float scale = 1.f;
std::mutex seenLock;
std::vector<D3D11_RASTERIZER_DESC> seen;

HRESULT STDMETHODCALLTYPE createRasterizer(ID3D11Device* device, const D3D11_RASTERIZER_DESC* desc, ID3D11RasterizerState** state) {
    if (!desc) return stockCreateRasterizer(device, desc, state);
    D3D11_RASTERIZER_DESC changed = *desc;
    const bool biased = desc->DepthBias != 0 || desc->SlopeScaledDepthBias != 0;
    if (biased && scale != 1.f) {
        changed.DepthBias = INT(double(desc->DepthBias) * scale);
        changed.SlopeScaledDepthBias = desc->SlopeScaledDepthBias * scale;
    }
    if (logStates) {
        std::lock_guard<std::mutex> lock(seenLock);
        const bool known = std::any_of(seen.begin(), seen.end(), [&](const D3D11_RASTERIZER_DESC& s) { return !memcmp(&s, desc, sizeof(s)); });
        if (!known && seen.size() < 128) {
            seen.push_back(*desc);
            AutosportLog("Rasterizer %zu fill=%u cull=%u ccw=%d bias=%d clamp=%.6f slope=%.3f depthclip=%d scissor=%d msaa=%d%s", seen.size(),
                         unsigned(desc->FillMode), unsigned(desc->CullMode), desc->FrontCounterClockwise, desc->DepthBias, desc->DepthBiasClamp,
                         desc->SlopeScaledDepthBias, desc->DepthClipEnable, desc->ScissorEnable, desc->MultisampleEnable,
                         biased && scale != 1.f ? " -> bias scaled" : "");
        }
    }
    return stockCreateRasterizer(device, &changed, state);
}
}

void AutosportConfigureShadowBias(ID3D11Device* device) {
    if (!device || stockCreateRasterizer) return;
    logStates = AutosportDiagnosticLogging();
    if (const wchar_t* arg = wcsstr(GetCommandLineW(), L" -gasvrshadowbias=")) {
        const unsigned percent = wcstoul(arg + wcslen(L" -gasvrshadowbias="), nullptr, 10);
        if (percent >= 100 && percent <= 2000) scale = percent / 100.f;
    }
    if (!logStates && scale == 1.f) return;
    void** table = *reinterpret_cast<void***>(device);
    DWORD old{};
    if (!VirtualProtect(table + 22, sizeof(void*), PAGE_EXECUTE_READWRITE, &old)) return;
    stockCreateRasterizer = reinterpret_cast<RasterizerFn>(table[22]);
    table[22] = reinterpret_cast<void*>(&createRasterizer);
    VirtualProtect(table + 22, sizeof(void*), old, &old);
    AutosportLog("Shadow bias: rasterizer states %s, depth bias x%.2f", logStates ? "logged" : "not logged", scale);
}
