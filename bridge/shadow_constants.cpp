// Test only (-gasvrshadowcb=N, every Nth frame): logs the constants of the game's shadow mask
// draws. The mask pixel shaders rebuild each pixel's view-space position from scene depth with
// CameraParams.inverseProj (cb3) and map it into the first cascade with $Globals.clip2depth0
// (cb0, view space to shadow map). For both eyes the world-to-shadow transform
// clip2depth0 * view must be the same; logging cb0 and cb3 per eye shows whether it is.
// Pixel shaders are recognised by their reflection names (clip2depth0 and inverseProj). The game
// may record its draws on deferred contexts, where buffers cannot be read back, so the contents
// of every constant buffer are kept as they are written (Map/Unmap, UpdateSubresource).
#include "bridge_api.h"
#include <windows.h>
#include <d3d11.h>
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <mutex>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace {
using CreatePsFn = HRESULT(STDMETHODCALLTYPE*)(ID3D11Device*, const void*, SIZE_T, ID3D11ClassLinkage*, ID3D11PixelShader**);
using CreateDeferredFn = HRESULT(STDMETHODCALLTYPE*)(ID3D11Device*, UINT, ID3D11DeviceContext**);
using SetPsFn = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11PixelShader*, ID3D11ClassInstance* const*, UINT);
using DrawIndexedFn = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, UINT, UINT, INT);
using DrawFn = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, UINT, UINT);
using DrawIndexedInstancedFn = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, UINT, UINT, UINT, INT, UINT);
using DrawInstancedFn = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, UINT, UINT, UINT, UINT);
using MapFn = HRESULT(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Resource*, UINT, D3D11_MAP, UINT, D3D11_MAPPED_SUBRESOURCE*);
using UnmapFn = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Resource*, UINT);
using SetCbFn = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, UINT, UINT, ID3D11Buffer* const*);
using UpdateFn = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Resource*, UINT, const D3D11_BOX*, const void*, UINT, UINT);

// Stock functions per context vtable (immediate and deferred contexts have different ones).
struct Table { SetPsFn setPs; DrawIndexedFn drawIndexed; DrawFn draw; MapFn map; UnmapFn unmap; UpdateFn update; DrawIndexedInstancedFn drawIndexedInstanced; DrawInstancedFn drawInstanced; SetCbFn setCb; };
std::mutex lock;
std::unordered_map<void**, Table> tables;
CreatePsFn stockCreatePs;
CreateDeferredFn stockCreateDeferred;
unsigned every;
std::unordered_set<ID3D11PixelShader*> maskShaders;
std::unordered_map<ID3D11DeviceContext*, ID3D11PixelShader*> current;
std::unordered_map<ID3D11Buffer*, std::vector<BYTE>> contents;
std::unordered_map<ID3D11Resource*, void*> mapped;
uint64_t loggedFrame = ~0ull;
unsigned loggedDraws, setCalls, maskSets, maskDraws, allDraws;

Table& table(ID3D11DeviceContext* context) { return tables[*reinterpret_cast<void***>(context)]; }
void hookContext(ID3D11DeviceContext* context, const char* kind);
void maybeDump(ID3D11DeviceContext* context, const char* when);

bool contains(const void* data, SIZE_T size, const char* text) {
    const size_t n = strlen(text);
    const char* p = static_cast<const char*>(data);
    for (SIZE_T i = 0; i + n <= size; ++i) if (!memcmp(p + i, text, n)) return true;
    return false;
}

ID3D11Buffer* constantBuffer(ID3D11Resource* resource, UINT* width) {
    D3D11_RESOURCE_DIMENSION type{};
    resource->GetType(&type);
    if (type != D3D11_RESOURCE_DIMENSION_BUFFER) return nullptr;
    auto* buffer = static_cast<ID3D11Buffer*>(resource);
    D3D11_BUFFER_DESC desc{};
    buffer->GetDesc(&desc);
    if (!(desc.BindFlags & D3D11_BIND_CONSTANT_BUFFER)) return nullptr;
    *width = desc.ByteWidth;
    return buffer;
}

HRESULT STDMETHODCALLTYPE createPs(ID3D11Device* device, const void* code, SIZE_T size, ID3D11ClassLinkage* linkage, ID3D11PixelShader** shader) {
    const HRESULT hr = stockCreatePs(device, code, size, linkage, shader);
    if (SUCCEEDED(hr) && shader && *shader && code && contains(code, size, "clip2depth0") && contains(code, size, "inverseProj")) {
        {
            std::lock_guard<std::mutex> guard(lock);
            maskShaders.insert(*shader);
            AutosportLog("Shadow constants: pixel shader %p size=%u uses clip2depth0 (device %p)", *shader, unsigned(size), device);
        }
        // The device drawing the game may not be the first one created; its context class can differ.
        ID3D11DeviceContext* context = nullptr;
        device->GetImmediateContext(&context);
        if (context) { hookContext(context, "game immediate"); context->Release(); }
    }
    return hr;
}

void STDMETHODCALLTYPE setPs(ID3D11DeviceContext* context, ID3D11PixelShader* shader, ID3D11ClassInstance* const* instances, UINT count) {
    SetPsFn stock;
    {
        std::lock_guard<std::mutex> guard(lock);
        const bool mask = shader && maskShaders.count(shader);
        current[context] = mask ? shader : nullptr;
        ++setCalls; if (mask) ++maskSets;
        if (mask && maskSets <= 3) AutosportLog("Shadow constants: mask bind on context %p vtable %p", context, *reinterpret_cast<void**>(context));
        stock = table(context).setPs;
    }
    stock(context, shader, instances, count);
    if (shader && maskShaders.count(shader)) maybeDump(context, "bind");
}

// The D3D11 runtime rewrites its immediate context's draw entries as state changes, so the mask
// constants are logged when the mask shader is bound and when constant buffers are set with it.
void STDMETHODCALLTYPE setCb(ID3D11DeviceContext* context, UINT start, UINT count, ID3D11Buffer* const* buffers) {
    SetCbFn stock;
    bool mask;
    { std::lock_guard<std::mutex> guard(lock); stock = table(context).setCb; auto found = current.find(context); mask = found != current.end() && found->second; }
    stock(context, start, count, buffers);
    if (mask) maybeDump(context, "constants");
}

HRESULT STDMETHODCALLTYPE map(ID3D11DeviceContext* context, ID3D11Resource* resource, UINT sub, D3D11_MAP type, UINT flags, D3D11_MAPPED_SUBRESOURCE* out) {
    MapFn stock;
    { std::lock_guard<std::mutex> guard(lock); stock = table(context).map; }
    const HRESULT hr = stock(context, resource, sub, type, flags, out);
    UINT width = 0;
    if (SUCCEEDED(hr) && out && resource && type != D3D11_MAP_READ && constantBuffer(resource, &width)) {
        std::lock_guard<std::mutex> guard(lock);
        mapped[resource] = out->pData;
    }
    return hr;
}

void STDMETHODCALLTYPE unmap(ID3D11DeviceContext* context, ID3D11Resource* resource, UINT sub) {
    UnmapFn stock;
    {
        std::lock_guard<std::mutex> guard(lock);
        stock = table(context).unmap;
        auto found = mapped.find(resource);
        UINT width = 0;
        if (found != mapped.end()) {
            if (auto* buffer = constantBuffer(resource, &width)) {
                auto& copy = contents[buffer];
                copy.resize(width);
                std::memcpy(copy.data(), found->second, width);
            }
            mapped.erase(found);
        }
    }
    stock(context, resource, sub);
}

void STDMETHODCALLTYPE update(ID3D11DeviceContext* context, ID3D11Resource* resource, UINT sub, const D3D11_BOX* box, const void* data, UINT pitch, UINT depth) {
    UpdateFn stock;
    {
        std::lock_guard<std::mutex> guard(lock);
        stock = table(context).update;
        UINT width = 0;
        if (resource && data) if (auto* buffer = constantBuffer(resource, &width)) {
            auto& copy = contents[buffer];
            copy.resize(width);
            const UINT from = box ? (std::min)(box->left, width) : 0, to = box ? (std::min)(box->right, width) : width;
            std::memcpy(copy.data() + from, data, to - from);
        }
    }
    stock(context, resource, sub, box, data, pitch, depth);
}

void logBuffer(ID3D11DeviceContext* context, UINT slot, const char* name) {
    ID3D11Buffer* buffer = nullptr;
    context->PSGetConstantBuffers(slot, 1, &buffer);
    if (!buffer) { AutosportLog("Shadow constants: %s none", name); return; }
    std::vector<BYTE> copy;
    { std::lock_guard<std::mutex> guard(lock); auto found = contents.find(buffer); if (found != contents.end()) copy = found->second; }
    buffer->Release();
    if (copy.empty()) { AutosportLog("Shadow constants: %s contents unknown", name); return; }
    static char text[8192];
    size_t used = 0;
    const float* f = reinterpret_cast<const float*>(copy.data());
    const unsigned count = (std::min)(unsigned(copy.size() / 4), 112u);
    for (unsigned i = 0; i < count && used + 20 < sizeof(text); ++i)
        used += snprintf(text + used, sizeof(text) - used, "%s%.7g", i ? " " : "", f[i]);
    AutosportLog("Shadow constants: %s bytes=%u: %s", name, unsigned(copy.size()), text);
}

void maybeDump(ID3D11DeviceContext* context, const char* when) {
    ID3D11PixelShader* shader;
    {
        std::lock_guard<std::mutex> guard(lock);
        auto found = current.find(context);
        shader = found == current.end() ? nullptr : found->second;
        ++allDraws;
        if (allDraws % 200000 == 1) AutosportLog("Shadow constants: draw on context %p vtable %p", context, *reinterpret_cast<void**>(context));
        if (shader) ++maskDraws;
    }
    const uint64_t frame = AutosportNativeFrame();
    static uint64_t reported = ~0ull;
    if (frame % every == 0 && reported != frame) {
        reported = frame;
        AutosportLog("Shadow constants: frame=%llu PSSetShader calls=%u mask binds=%u draws=%u mask draws=%u contexts=%u", frame, setCalls, maskSets, allDraws, maskDraws, unsigned(current.size()));
    }
    if (!shader || frame % every != 0) return;
    if (frame != loggedFrame) { loggedFrame = frame; loggedDraws = 0; }
    if (++loggedDraws > 12) return;
    float head[4]{};
    AutosportHeadOrientation(head);
    AutosportLog("Shadow constants: frame=%llu draw=%u at %s context=%p shader=%p head_q=(%.4f %.4f %.4f %.4f)", frame, loggedDraws, when, context, shader,
                 head[0], head[1], head[2], head[3]);
    logBuffer(context, 0, "cb0");
    logBuffer(context, 3, "cb3");
    if (loggedDraws <= 2) for (UINT slot = 3; slot <= 5; ++slot) {
        ID3D11ShaderResourceView* view = nullptr;
        context->PSGetShaderResources(slot, 1, &view);
        if (!view) { AutosportLog("Shadow constants: t%u none", slot); continue; }
        D3D11_SHADER_RESOURCE_VIEW_DESC desc{};
        view->GetDesc(&desc);
        ID3D11Resource* resource = nullptr;
        view->GetResource(&resource);
        D3D11_TEXTURE2D_DESC texture{};
        D3D11_RESOURCE_DIMENSION type{};
        if (resource) { resource->GetType(&type); if (type == D3D11_RESOURCE_DIMENSION_TEXTURE2D) static_cast<ID3D11Texture2D*>(resource)->GetDesc(&texture); resource->Release(); }
        AutosportLog("Shadow constants: t%u view format=%u dimension=%u texture %ux%u format=%u samples=%u bind=0x%X", slot, unsigned(desc.Format),
                     unsigned(desc.ViewDimension), texture.Width, texture.Height, unsigned(texture.Format), texture.SampleDesc.Count, texture.BindFlags);
        view->Release();
    }
}

void STDMETHODCALLTYPE drawIndexed(ID3D11DeviceContext* context, UINT count, UINT start, INT base) {
    maybeDump(context, "draw");
    DrawIndexedFn stock;
    { std::lock_guard<std::mutex> guard(lock); stock = table(context).drawIndexed; }
    stock(context, count, start, base);
}

void STDMETHODCALLTYPE draw(ID3D11DeviceContext* context, UINT count, UINT start) {
    maybeDump(context, "draw");
    DrawFn stock;
    { std::lock_guard<std::mutex> guard(lock); stock = table(context).draw; }
    stock(context, count, start);
}

void STDMETHODCALLTYPE drawIndexedInstanced(ID3D11DeviceContext* context, UINT count, UINT instances, UINT start, INT base, UINT first) {
    maybeDump(context, "draw");
    DrawIndexedInstancedFn stock;
    { std::lock_guard<std::mutex> guard(lock); stock = table(context).drawIndexedInstanced; }
    stock(context, count, instances, start, base, first);
}

void STDMETHODCALLTYPE drawInstanced(ID3D11DeviceContext* context, UINT count, UINT instances, UINT start, UINT first) {
    maybeDump(context, "draw");
    DrawInstancedFn stock;
    { std::lock_guard<std::mutex> guard(lock); stock = table(context).drawInstanced; }
    stock(context, count, instances, start, first);
}

void* swap(void** vtable, int slot, void* replacement) {
    DWORD old{};
    if (!VirtualProtect(vtable + slot, sizeof(void*), PAGE_EXECUTE_READWRITE, &old)) return nullptr;
    void* stock = vtable[slot];
    vtable[slot] = replacement;
    VirtualProtect(vtable + slot, sizeof(void*), old, &old);
    return stock;
}

void hookContext(ID3D11DeviceContext* context, const char* kind) {
    void** vtable = *reinterpret_cast<void***>(context);
    std::lock_guard<std::mutex> guard(lock);
    if (tables.count(vtable)) return;
    Table& t = tables[vtable];
    t.setPs = reinterpret_cast<SetPsFn>(swap(vtable, 9, reinterpret_cast<void*>(&setPs)));
    t.drawIndexed = reinterpret_cast<DrawIndexedFn>(swap(vtable, 12, reinterpret_cast<void*>(&drawIndexed)));
    t.draw = reinterpret_cast<DrawFn>(swap(vtable, 13, reinterpret_cast<void*>(&draw)));
    t.map = reinterpret_cast<MapFn>(swap(vtable, 14, reinterpret_cast<void*>(&map)));
    t.unmap = reinterpret_cast<UnmapFn>(swap(vtable, 15, reinterpret_cast<void*>(&unmap)));
    t.update = reinterpret_cast<UpdateFn>(swap(vtable, 48, reinterpret_cast<void*>(&update)));
    t.drawIndexedInstanced = reinterpret_cast<DrawIndexedInstancedFn>(swap(vtable, 20, reinterpret_cast<void*>(&drawIndexedInstanced)));
    t.drawInstanced = reinterpret_cast<DrawInstancedFn>(swap(vtable, 21, reinterpret_cast<void*>(&drawInstanced)));
    t.setCb = reinterpret_cast<SetCbFn>(swap(vtable, 16, reinterpret_cast<void*>(&setCb)));
    AutosportLog("Shadow constants: hooked %s context vtable %p", kind, vtable);
}

HRESULT STDMETHODCALLTYPE createDeferred(ID3D11Device* device, UINT flags, ID3D11DeviceContext** context) {
    const HRESULT hr = stockCreateDeferred(device, flags, context);
    if (SUCCEEDED(hr) && context && *context) hookContext(*context, "deferred");
    return hr;
}
}

void AutosportConfigureShadowConstants(ID3D11Device* device) {
    const wchar_t* arg = wcsstr(GetCommandLineW(), L" -gasvrshadowcb=");
    if (!device || stockCreatePs || !arg) return;
    every = wcstoul(arg + wcslen(L" -gasvrshadowcb="), nullptr, 10);
    if (!every) return;
    ID3D11DeviceContext* context = nullptr;
    device->GetImmediateContext(&context);
    if (!context) return;
    void** deviceTable = *reinterpret_cast<void***>(device);
    stockCreatePs = reinterpret_cast<CreatePsFn>(swap(deviceTable, 15, reinterpret_cast<void*>(&createPs)));
    stockCreateDeferred = reinterpret_cast<CreateDeferredFn>(swap(deviceTable, 27, reinterpret_cast<void*>(&createDeferred)));
    hookContext(context, "immediate");
    context->Release();
    AutosportLog("Shadow constants: logging mask draws every %u frames", every);
}
