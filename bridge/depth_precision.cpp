// Scene depth precision in VR. The game renders the scene into a 16-bit depth buffer
// (R16_TYPELESS: D16_UNORM, read back as R16_UNORM) and its screen-space shadow mask rebuilds each
// pixel's position from it (shadow_constants.cpp has the pass). The cockpit camera's near plane is
// 0.075 m, so 16 bits resolve depth only to about z*z/(0.075*65536): 18 cm at 30 m. With the sun
// behind the player, where the view runs along the light, those steps put the rebuilt wall in
// front of and behind the shadow map's surface in bands along lines of equal depth: shadow stripes
// that tighten where a wall is seen at a glancing angle, widen and flip as the head turns to face
// it, and crawl as the head moves. VR sessions give the eye's scene depth 32-bit float depth
// (R32_TYPELESS: D32_FLOAT, R32_FLOAT views); shadow maps (square, power of two) keep 16 bits, as
// their depth bias is tuned for it. -gasvrdepth16=1 keeps the game's 16-bit scene depth.
#include "bridge_api.h"
#include <windows.h>
#include <d3d11.h>
#include <cwchar>

namespace {
using TextureFn = HRESULT(STDMETHODCALLTYPE*)(ID3D11Device*, const D3D11_TEXTURE2D_DESC*, const D3D11_SUBRESOURCE_DATA*, ID3D11Texture2D**);
using ViewFn = HRESULT(STDMETHODCALLTYPE*)(ID3D11Device*, ID3D11Resource*, const D3D11_SHADER_RESOURCE_VIEW_DESC*, ID3D11ShaderResourceView**);
using DepthViewFn = HRESULT(STDMETHODCALLTYPE*)(ID3D11Device*, ID3D11Resource*, const D3D11_DEPTH_STENCIL_VIEW_DESC*, ID3D11DepthStencilView**);
TextureFn stockTexture;
ViewFn stockView;
DepthViewFn stockDepthView;
unsigned logged;

bool sceneDepth(const D3D11_TEXTURE2D_DESC& d) {
    const bool shadowMap = d.Width == d.Height && (d.Width & (d.Width - 1)) == 0;
    return d.Format == DXGI_FORMAT_R16_TYPELESS && (d.BindFlags & D3D11_BIND_DEPTH_STENCIL) && !shadowMap && d.Width >= 256 && d.Height >= 256;
}

// A texture made 32-bit here: the game asks for 16-bit views of it (no texture of its own is
// R32_TYPELESS with 16-bit views, which Direct3D would refuse).
bool isDeepened(ID3D11Resource* resource) {
    if (!resource) return false;
    D3D11_RESOURCE_DIMENSION type{};
    resource->GetType(&type);
    if (type != D3D11_RESOURCE_DIMENSION_TEXTURE2D) return false;
    D3D11_TEXTURE2D_DESC desc{};
    static_cast<ID3D11Texture2D*>(resource)->GetDesc(&desc);
    return desc.Format == DXGI_FORMAT_R32_TYPELESS;
}

HRESULT STDMETHODCALLTYPE createTexture(ID3D11Device* device, const D3D11_TEXTURE2D_DESC* desc, const D3D11_SUBRESOURCE_DATA* data, ID3D11Texture2D** texture) {
    if (!desc || !texture || !sceneDepth(*desc)) return stockTexture(device, desc, data, texture);
    D3D11_TEXTURE2D_DESC deeper = *desc;
    deeper.Format = DXGI_FORMAT_R32_TYPELESS;
    const HRESULT hr = stockTexture(device, &deeper, data, texture);
    if (FAILED(hr)) return stockTexture(device, desc, data, texture);
    if (logged++ < 8) AutosportLog("Depth precision: scene depth %ux%u (%u samples) made 32-bit float", desc->Width, desc->Height, desc->SampleDesc.Count);
    return hr;
}

HRESULT STDMETHODCALLTYPE createView(ID3D11Device* device, ID3D11Resource* resource, const D3D11_SHADER_RESOURCE_VIEW_DESC* desc, ID3D11ShaderResourceView** view) {
    if (desc && desc->Format == DXGI_FORMAT_R16_UNORM && isDeepened(resource)) {
        D3D11_SHADER_RESOURCE_VIEW_DESC deeper = *desc;
        deeper.Format = DXGI_FORMAT_R32_FLOAT;
        return stockView(device, resource, &deeper, view);
    }
    return stockView(device, resource, desc, view);
}

HRESULT STDMETHODCALLTYPE createDepthView(ID3D11Device* device, ID3D11Resource* resource, const D3D11_DEPTH_STENCIL_VIEW_DESC* desc, ID3D11DepthStencilView** view) {
    if (desc && desc->Format == DXGI_FORMAT_D16_UNORM && isDeepened(resource)) {
        D3D11_DEPTH_STENCIL_VIEW_DESC deeper = *desc;
        deeper.Format = DXGI_FORMAT_D32_FLOAT;
        return stockDepthView(device, resource, &deeper, view);
    }
    return stockDepthView(device, resource, desc, view);
}

void* swap(void** table, int slot, void* replacement) {
    DWORD old{};
    if (!VirtualProtect(table + slot, sizeof(void*), PAGE_EXECUTE_READWRITE, &old)) return nullptr;
    void* stock = table[slot];
    table[slot] = replacement;
    VirtualProtect(table + slot, sizeof(void*), old, &old);
    return stock;
}
}

void AutosportConfigureDepthPrecision(ID3D11Device* device) {
    if (!device || stockTexture || wcsstr(GetCommandLineW(), L" -gasvrdepth16=1")) return;
    void** table = *reinterpret_cast<void***>(device);
    stockTexture = reinterpret_cast<TextureFn>(swap(table, 5, reinterpret_cast<void*>(&createTexture)));
    stockView = reinterpret_cast<ViewFn>(swap(table, 7, reinterpret_cast<void*>(&createView)));
    stockDepthView = reinterpret_cast<DepthViewFn>(swap(table, 10, reinterpret_cast<void*>(&createDepthView)));
    AutosportLog("Depth precision: VR scene depth is 32-bit float");
}
