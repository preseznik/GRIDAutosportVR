// Cockpit light at night (-gasvrnightcockpit=1, launcher Graphics -> VR rendering -> Cockpit light
// at night). The game's car interior shaders end their lighting with a helmet-camera contrast step,
// max(0, light * scale + shift) with helmetCamInteriorTweaks.zw (each track's lighting file:
// helmetCamScaleInteriorBrightness 1.2 or 1.5, helmetCamShiftInteriorBrightness -0.85). By day
// the sun lifts most of the cabin over that cut; at night almost nothing is, so the dashboard,
// steering wheel, driver and seats are drawn pure black. At night, while the player drives in a
// race, the interior draws that carry this cut get a copy of their constants with scale 1 and a
// small fill instead: the cabin keeps the night scene's own light and shading, a little lifted.
// Night is the track lighting's EnvmapExposure (PerFrameConstantBuffer
// backlightColourAndEnvmapExposure.w): 0.6 or 0.65 on every night track, 1.4 or more by day.
//
// Shaders are recognised by their reflection when the game creates them. Constant buffers are
// read as the game writes them (Map/Unmap, UpdateSubresource; the game records its draws on
// deferred contexts, whose buffers cannot be read back), only for buffers seen bound to an
// interior draw, per context.
#include "bridge_api.h"
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <algorithm>
#include <atomic>
#include <cstring>
#include <cwchar>
#include <map>
#include <mutex>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

namespace {
using CreatePsFn = HRESULT(STDMETHODCALLTYPE*)(ID3D11Device*, const void*, SIZE_T, ID3D11ClassLinkage*, ID3D11PixelShader**);
using CreateDeferredFn = HRESULT(STDMETHODCALLTYPE*)(ID3D11Device*, UINT, ID3D11DeviceContext**);
using SetPsFn = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11PixelShader*, ID3D11ClassInstance* const*, UINT);
using DrawIndexedFn = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, UINT, UINT, INT);
using DrawIndexedInstancedFn = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, UINT, UINT, UINT, INT, UINT);
using MapFn = HRESULT(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Resource*, UINT, D3D11_MAP, UINT, D3D11_MAPPED_SUBRESOURCE*);
using UnmapFn = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Resource*, UINT);
using UpdateFn = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Resource*, UINT, const D3D11_BOX*, const void*, UINT, UINT);

constexpr float nightScale = 1.0f, nightFill = 0.12f, nightExposure = 1.0f;

struct Table { SetPsFn setPs; DrawIndexedFn drawIndexed; DrawIndexedInstancedFn drawIndexedInstanced; MapFn map; UnmapFn unmap; UpdateFn update; };
// An interior shader: where its tweaks and the envmap exposure are (slot, byte offset).
struct Interior { UINT tweakSlot, tweakOffset, exposureSlot, exposureOffset; bool exposure; };
using Key = std::pair<ID3D11DeviceContext*, ID3D11Buffer*>;

std::mutex lock;
std::unordered_map<void**, Table> tables;
CreatePsFn stockCreatePs;
CreateDeferredFn stockCreateDeferred;
std::unordered_map<ID3D11PixelShader*, Interior> interiors;
std::unordered_map<ID3D11DeviceContext*, const Interior*> current;
std::unordered_set<ID3D11Buffer*> watched;
std::map<Key, std::vector<BYTE>> contents;
std::map<Key, void*> mapped;
std::map<std::pair<ID3D11Device*, UINT>, ID3D11Buffer*> copies;
std::atomic<int> night{-1};
std::atomic<unsigned> lifted;

Table& table(ID3D11DeviceContext* context) { return tables[*reinterpret_cast<void***>(context)]; }

void* swap(void** vtable, int slot, void* replacement) {
    DWORD old{};
    if (!VirtualProtect(vtable + slot, sizeof(void*), PAGE_EXECUTE_READWRITE, &old)) return nullptr;
    void* stock = vtable[slot];
    vtable[slot] = replacement;
    VirtualProtect(vtable + slot, sizeof(void*), old, &old);
    return stock;
}

ID3D11Buffer* watchedBuffer(ID3D11Resource* resource) {
    // Only buffers already seen bound to an interior draw (the caller holds the lock).
    auto* buffer = static_cast<ID3D11Buffer*>(resource);
    return resource && watched.count(buffer) ? buffer : nullptr;
}

void hookContext(ID3D11DeviceContext* context);

bool copyMapped(std::vector<BYTE>& copy, const void* source) {
    __try { std::memcpy(copy.data(), source, copy.size()); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

void reflect(ID3D11PixelShader* shader, const void* code, SIZE_T size) {
    ID3D11ShaderReflection* reflection = nullptr;
    if (FAILED(D3DReflect(code, size, IID_ID3D11ShaderReflection, reinterpret_cast<void**>(&reflection))) || !reflection) return;
    Interior interior{};
    bool tweaks = false;
    D3D11_SHADER_DESC desc{};
    reflection->GetDesc(&desc);
    for (UINT r = 0; r < desc.BoundResources; ++r) {
        D3D11_SHADER_INPUT_BIND_DESC bind{};
        if (FAILED(reflection->GetResourceBindingDesc(r, &bind)) || bind.Type != D3D_SIT_CBUFFER) continue;
        ID3D11ShaderReflectionConstantBuffer* buffer = reflection->GetConstantBufferByName(bind.Name);
        D3D11_SHADER_BUFFER_DESC bufferDesc{};
        if (!buffer || FAILED(buffer->GetDesc(&bufferDesc))) continue;
        for (UINT v = 0; v < bufferDesc.Variables; ++v) {
            D3D11_SHADER_VARIABLE_DESC variable{};
            if (FAILED(buffer->GetVariableByIndex(v)->GetDesc(&variable)) || variable.Size != 16) continue;
            if (!strcmp(variable.Name, "helmetCamInteriorTweaks") && (variable.uFlags & D3D_SVF_USED)) {
                tweaks = true; interior.tweakSlot = bind.BindPoint; interior.tweakOffset = variable.StartOffset;
            } else if (!strcmp(variable.Name, "backlightColourAndEnvmapExposure")) {
                interior.exposure = true; interior.exposureSlot = bind.BindPoint; interior.exposureOffset = variable.StartOffset + 12;
            }
        }
    }
    reflection->Release();
    if (!tweaks || interior.tweakSlot >= D3D11_COMMONSHADER_CONSTANT_BUFFER_API_SLOT_COUNT) return;
    std::lock_guard<std::mutex> guard(lock);
    interiors[shader] = interior;
    if (interiors.size() <= 16)
        AutosportLog("Cockpit light: interior shader %p (tweaks b%u+%u, exposure %s b%u+%u)", shader, interior.tweakSlot, interior.tweakOffset,
                     interior.exposure ? "at" : "not", interior.exposureSlot, interior.exposureOffset);
}

HRESULT STDMETHODCALLTYPE createPs(ID3D11Device* device, const void* code, SIZE_T size, ID3D11ClassLinkage* linkage, ID3D11PixelShader** shader) {
    const HRESULT hr = stockCreatePs(device, code, size, linkage, shader);
    if (SUCCEEDED(hr) && shader && *shader && code) {
        reflect(*shader, code, size);
        // The device drawing the game may not be the first one created; its context class can differ.
        ID3D11DeviceContext* context = nullptr;
        device->GetImmediateContext(&context);
        if (context) { hookContext(context); context->Release(); }
    }
    return hr;
}

void STDMETHODCALLTYPE drawIndexed(ID3D11DeviceContext* context, UINT count, UINT start, INT base);
void STDMETHODCALLTYPE drawIndexedInstanced(ID3D11DeviceContext* context, UINT count, UINT instances, UINT start, INT base, UINT first);

void STDMETHODCALLTYPE setPs(ID3D11DeviceContext* context, ID3D11PixelShader* shader, ID3D11ClassInstance* const* instances, UINT count) {
    SetPsFn stock;
    {
        std::lock_guard<std::mutex> guard(lock);
        auto found = shader ? interiors.find(shader) : interiors.end();
        current[context] = found == interiors.end() ? nullptr : &found->second;
        stock = table(context).setPs;
    }
    stock(context, shader, instances, count);
    // The D3D11 runtime rewrites its immediate context's draw entries as state changes; take them back.
    std::lock_guard<std::mutex> guard(lock);
    void** vtable = *reinterpret_cast<void***>(context);
    Table& t = table(context);
    if (vtable[12] != reinterpret_cast<void*>(&drawIndexed)) t.drawIndexed = reinterpret_cast<DrawIndexedFn>(swap(vtable, 12, reinterpret_cast<void*>(&drawIndexed)));
    if (vtable[20] != reinterpret_cast<void*>(&drawIndexedInstanced))
        t.drawIndexedInstanced = reinterpret_cast<DrawIndexedInstancedFn>(swap(vtable, 20, reinterpret_cast<void*>(&drawIndexedInstanced)));
}

HRESULT STDMETHODCALLTYPE map(ID3D11DeviceContext* context, ID3D11Resource* resource, UINT sub, D3D11_MAP type, UINT flags, D3D11_MAPPED_SUBRESOURCE* out) {
    MapFn stock;
    { std::lock_guard<std::mutex> guard(lock); stock = table(context).map; }
    const HRESULT hr = stock(context, resource, sub, type, flags, out);
    if (SUCCEEDED(hr) && out && type != D3D11_MAP_READ) {
        std::lock_guard<std::mutex> guard(lock);
        if (ID3D11Buffer* buffer = watchedBuffer(resource)) mapped[{context, buffer}] = out->pData;
    }
    return hr;
}

void STDMETHODCALLTYPE unmap(ID3D11DeviceContext* context, ID3D11Resource* resource, UINT sub) {
    UnmapFn stock;
    {
        std::lock_guard<std::mutex> guard(lock);
        stock = table(context).unmap;
        if (ID3D11Buffer* buffer = watchedBuffer(resource)) {
            auto found = mapped.find({context, buffer});
            if (found != mapped.end()) {
                D3D11_BUFFER_DESC desc{};
                buffer->GetDesc(&desc);
                auto& copy = contents[{context, buffer}];
                copy.resize(desc.ByteWidth);
                if (!copyMapped(copy, found->second)) copy.clear();
                mapped.erase(found);
            }
        }
    }
    stock(context, resource, sub);
}

void STDMETHODCALLTYPE update(ID3D11DeviceContext* context, ID3D11Resource* resource, UINT sub, const D3D11_BOX* box, const void* data, UINT pitch, UINT depth) {
    UpdateFn stock;
    {
        std::lock_guard<std::mutex> guard(lock);
        stock = table(context).update;
        if (ID3D11Buffer* buffer = data ? watchedBuffer(resource) : nullptr) {
            D3D11_BUFFER_DESC desc{};
            buffer->GetDesc(&desc);
            auto& copy = contents[{context, buffer}];
            copy.resize(desc.ByteWidth);
            const UINT from = box ? (std::min)(box->left, desc.ByteWidth) : 0, to = box ? (std::min)(box->right, desc.ByteWidth) : desc.ByteWidth;
            std::memcpy(copy.data() + from, data, to - from);
        }
    }
    stock(context, resource, sub, box, data, pitch, depth);
}

// An interior draw in a race: learns its buffers, reads night from the exposure and, at night,
// binds a copy of its constants with the cut replaced. Returns the game's buffer to bind back.
ID3D11Buffer* adjust(ID3D11DeviceContext* context, UINT& slot) {
    Interior interior;
    {
        std::lock_guard<std::mutex> guard(lock);
        auto found = current.find(context);
        if (found == current.end() || !found->second) return nullptr;
        interior = *found->second;
    }
    if (!AutosportCarCameraLive()) return nullptr;
    ID3D11Buffer* game = nullptr;
    context->PSGetConstantBuffers(interior.tweakSlot, 1, &game);
    if (!game) return nullptr;
    std::vector<BYTE> data;
    {
        std::lock_guard<std::mutex> guard(lock);
        watched.insert(game);
        if (interior.exposure) {
            ID3D11Buffer* frame = nullptr;
            context->PSGetConstantBuffers(interior.exposureSlot, 1, &frame);
            if (frame) {
                watched.insert(frame);
                auto kept = contents.find({context, frame});
                if (kept != contents.end() && interior.exposureOffset + 4 <= kept->second.size()) {
                    float exposure;
                    std::memcpy(&exposure, kept->second.data() + interior.exposureOffset, 4);
                    const int now = exposure < nightExposure ? 1 : 0;
                    if (night.exchange(now) != now) AutosportLog("Cockpit light: %s (envmap exposure %.2f)", now ? "night, the cockpit is lit" : "day, the game's own cockpit light", exposure);
                }
                frame->Release();
            }
        }
        auto kept = contents.find({context, game});
        if (night.load() == 1 && kept != contents.end() && interior.tweakOffset + 16 <= kept->second.size()) {
            const float* tweaks = reinterpret_cast<const float*>(kept->second.data() + interior.tweakOffset);
            // Only the helmet camera's cut (a scale above 1 and a negative shift).
            if (tweaks[2] > 1.0f && tweaks[3] < 0.0f) data = kept->second;
        }
    }
    if (data.empty()) { game->Release(); return nullptr; }
    float* tweaks = reinterpret_cast<float*>(data.data() + interior.tweakOffset);
    const float gameScale = tweaks[2], gameShift = tweaks[3];
    tweaks[2] = nightScale;
    tweaks[3] = nightFill;
    ID3D11Device* device = nullptr;
    context->GetDevice(&device);
    ID3D11Buffer* copy = nullptr;
    {
        std::lock_guard<std::mutex> guard(lock);
        ID3D11Buffer*& kept = copies[{device, UINT(data.size())}];
        if (!kept) {
            D3D11_BUFFER_DESC desc{UINT(data.size()), D3D11_USAGE_DEFAULT, D3D11_BIND_CONSTANT_BUFFER, 0, 0, 0};
            device->CreateBuffer(&desc, nullptr, &kept);
        }
        copy = kept;
    }
    device->Release();
    if (!copy) { game->Release(); return nullptr; }
    context->UpdateSubresource(copy, 0, nullptr, data.data(), 0, 0);
    context->PSSetConstantBuffers(interior.tweakSlot, 1, &copy);
    if (++lifted == 1) AutosportLog("Cockpit light: first lit interior draw (game scale %.2f shift %.2f)", gameScale, gameShift);
    // A sample of how many draws are lit per frame (the player's car: cockpit, driver, wheel).
    static std::atomic<uint64_t> countedFrame{0};
    static std::atomic<unsigned> perFrame{0};
    const uint64_t frame = AutosportNativeFrame();
    if (countedFrame.exchange(frame) != frame) {
        static unsigned logged;
        const unsigned previous = perFrame.exchange(0);
        if (frame % 900 == 0 && ++logged <= 20) AutosportLog("Cockpit light: %u interior draws lit in frame %llu", previous, frame - 1);
    }
    ++perFrame;
    slot = interior.tweakSlot;
    return game;
}

void restore(ID3D11DeviceContext* context, ID3D11Buffer* game, UINT slot) {
    if (!game) return;
    context->PSSetConstantBuffers(slot, 1, &game);
    game->Release();
}

void STDMETHODCALLTYPE drawIndexed(ID3D11DeviceContext* context, UINT count, UINT start, INT base) {
    UINT slot = 0;
    ID3D11Buffer* game = adjust(context, slot);
    DrawIndexedFn stock;
    { std::lock_guard<std::mutex> guard(lock); stock = table(context).drawIndexed; }
    stock(context, count, start, base);
    restore(context, game, slot);
}

void STDMETHODCALLTYPE drawIndexedInstanced(ID3D11DeviceContext* context, UINT count, UINT instances, UINT start, INT base, UINT first) {
    UINT slot = 0;
    ID3D11Buffer* game = adjust(context, slot);
    DrawIndexedInstancedFn stock;
    { std::lock_guard<std::mutex> guard(lock); stock = table(context).drawIndexedInstanced; }
    stock(context, count, instances, start, base, first);
    restore(context, game, slot);
}

void hookContext(ID3D11DeviceContext* context) {
    void** vtable = *reinterpret_cast<void***>(context);
    std::lock_guard<std::mutex> guard(lock);
    if (tables.count(vtable)) return;
    Table& t = tables[vtable];
    t.setPs = reinterpret_cast<SetPsFn>(swap(vtable, 9, reinterpret_cast<void*>(&setPs)));
    t.drawIndexed = reinterpret_cast<DrawIndexedFn>(swap(vtable, 12, reinterpret_cast<void*>(&drawIndexed)));
    t.map = reinterpret_cast<MapFn>(swap(vtable, 14, reinterpret_cast<void*>(&map)));
    t.unmap = reinterpret_cast<UnmapFn>(swap(vtable, 15, reinterpret_cast<void*>(&unmap)));
    t.drawIndexedInstanced = reinterpret_cast<DrawIndexedInstancedFn>(swap(vtable, 20, reinterpret_cast<void*>(&drawIndexedInstanced)));
    t.update = reinterpret_cast<UpdateFn>(swap(vtable, 48, reinterpret_cast<void*>(&update)));
}

HRESULT STDMETHODCALLTYPE createDeferred(ID3D11Device* device, UINT flags, ID3D11DeviceContext** context) {
    const HRESULT hr = stockCreateDeferred(device, flags, context);
    if (SUCCEEDED(hr) && context && *context) hookContext(*context);
    return hr;
}
} // namespace

void AutosportConfigureCockpitLight(ID3D11Device* device) {
    if (!device || stockCreatePs || !wcsstr(GetCommandLineW(), L" -gasvrnightcockpit=1")) return;
    ID3D11DeviceContext* context = nullptr;
    device->GetImmediateContext(&context);
    if (!context) return;
    void** deviceTable = *reinterpret_cast<void***>(device);
    stockCreatePs = reinterpret_cast<CreatePsFn>(swap(deviceTable, 15, reinterpret_cast<void*>(&createPs)));
    stockCreateDeferred = reinterpret_cast<CreateDeferredFn>(swap(deviceTable, 27, reinterpret_cast<void*>(&createDeferred)));
    hookContext(context);
    context->Release();
    AutosportLog("Cockpit light: at night the cockpit interior keeps its night light, lifted (scale %.2f, fill %.2f)", nightScale, nightFill);
}
