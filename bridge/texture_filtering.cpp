// Texture filtering in VR (launcher Graphics -> VR rendering -> Texture filtering):
// ID3D11Device::CreateSamplerState (vtable slot 23) is watched in VR sessions. The game creates
// most world-surface samplers anisotropic at only 4x (hardware_settings_config.xml
// <anisotropic min="4" max="16" />), and one with a -1 mip LOD bias, so building walls and the
// road, seen at steep angles, crawl and shimmer as the view moves. -gasvrtexturefilter=1 raises
// every mip-filtered sampler to 16x anisotropic and clears a negative bias; -gasvrtexturefilter=2
// also adds +0.5 mip LOD bias (slightly softer, steadier distant textures). Comparison (shadow),
// minimum / maximum and point samplers are left alone. With diagnostic logging
// (-gasvrdiagnostics=1) each distinct sampler the game creates is logged once.
#include "bridge_api.h"
#include <windows.h>
#include <d3d11.h>
#include <algorithm>
#include <cstring>
#include <mutex>
#include <vector>

namespace {
using SamplerFn = HRESULT(STDMETHODCALLTYPE*)(ID3D11Device*, const D3D11_SAMPLER_DESC*, ID3D11SamplerState**);
SamplerFn stockCreateSampler;
bool logSamplers, sharpen;
float extraBias; // -gasvrtexturefilter=2: also +0.5 mip LOD bias (softer, steadier distant textures)
std::mutex seenLock;
std::vector<D3D11_SAMPLER_DESC> seen;

// Mip-filtered texture sampling the game uses for surfaces: linear mips, not comparison or
// minimum / maximum reduction (D3D11 filter bits 0x80 and above).
bool surfaceSampler(const D3D11_SAMPLER_DESC& desc) {
    const unsigned filter = unsigned(desc.Filter);
    if (filter >= 0x80) return false;
    return filter == D3D11_FILTER_ANISOTROPIC || (filter & 0x1) != 0; // MIP_LINEAR bit
}

HRESULT STDMETHODCALLTYPE createSampler(ID3D11Device* device, const D3D11_SAMPLER_DESC* desc, ID3D11SamplerState** state) {
    if (!desc) return stockCreateSampler(device, desc, state);
    D3D11_SAMPLER_DESC changed = *desc;
    if (sharpen && surfaceSampler(*desc)) {
        changed.Filter = D3D11_FILTER_ANISOTROPIC;
        changed.MaxAnisotropy = 16;
        if (changed.MipLODBias < 0) changed.MipLODBias = 0;
        changed.MipLODBias += extraBias;
    }
    if (logSamplers) {
        std::lock_guard<std::mutex> lock(seenLock);
        const bool known = std::any_of(seen.begin(), seen.end(), [&](const D3D11_SAMPLER_DESC& s) { return !memcmp(&s, desc, sizeof(s)); });
        if (!known && seen.size() < 128) {
            seen.push_back(*desc);
            AutosportLog("Sampler %zu filter=0x%x aniso=%u bias=%.3f lod=%.1f..%.1f address=%u,%u,%u%s", seen.size(), unsigned(desc->Filter),
                         desc->MaxAnisotropy, desc->MipLODBias, desc->MinLOD, desc->MaxLOD, unsigned(desc->AddressU), unsigned(desc->AddressV),
                         unsigned(desc->AddressW), memcmp(&changed, desc, sizeof(changed)) ? " -> anisotropic 16x, bias >= 0" : "");
        }
    }
    return stockCreateSampler(device, &changed, state);
}
}

void AutosportConfigureTextureFiltering(ID3D11Device* device) {
    if (!device || stockCreateSampler) return;
    logSamplers = AutosportDiagnosticLogging();
    sharpen = wcsstr(GetCommandLineW(), L" -gasvrtexturefilter=1") || wcsstr(GetCommandLineW(), L" -gasvrtexturefilter=2");
    extraBias = wcsstr(GetCommandLineW(), L" -gasvrtexturefilter=2") ? .5f : 0.f;
    if (!logSamplers && !sharpen) return;
    void** table = *reinterpret_cast<void***>(device);
    DWORD old{};
    if (!VirtualProtect(table + 23, sizeof(void*), PAGE_EXECUTE_READWRITE, &old)) return;
    stockCreateSampler = reinterpret_cast<SamplerFn>(table[23]);
    table[23] = reinterpret_cast<void*>(&createSampler);
    VirtualProtect(table + 23, sizeof(void*), old, &old);
    AutosportLog("Texture filtering: samplers %s%s", logSamplers ? "logged" : "", sharpen ? (logSamplers ? ", anisotropic 16x" : "anisotropic 16x") : "");
}
