// Test only (-gasvrcockpitprobe=N, every Nth native frame in the cockpit view): how the game lights
// the car interior. Every pixel shader's reflection (constant buffer variables and bound resources)
// goes to cockpit-shaders.log in the session folder when it is created; shaders whose variables or
// resources name the interior, the helmet camera, glow, ambient or emissive light are "lighting"
// shaders. In the cockpit view, each sampled frame logs the draws made with lighting shaders and the
// values of their named variables (from the bound constant buffers' contents, kept as written,
// since the game records draws on deferred contexts), so the interior's lighting inputs can be
// compared between day and night.
#include "bridge_api.h"
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <map>
#include <mutex>
#include <share.h>
#include <string>
#include <unordered_map>
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

struct Table { SetPsFn setPs; DrawIndexedFn drawIndexed; DrawIndexedInstancedFn drawIndexedInstanced; MapFn map; UnmapFn unmap; UpdateFn update; };
struct Variable { std::string name; UINT slot, offset, size; };
struct Shader { unsigned id; bool lighting; int tweaks = -1; std::string resources; std::vector<Variable> variables; };

std::mutex lock;
std::unordered_map<void**, Table> tables;
CreatePsFn stockCreatePs;
CreateDeferredFn stockCreateDeferred;
unsigned every;
wchar_t folder[MAX_PATH], shadersPath[MAX_PATH], drawsPath[MAX_PATH];
std::unordered_map<ID3D11PixelShader*, Shader> shaders;
std::unordered_map<ID3D11DeviceContext*, ID3D11PixelShader*> current;
std::unordered_map<ID3D11Buffer*, std::vector<BYTE>> contents;
std::unordered_map<ID3D11Resource*, void*> mapped;
unsigned nextId;
uint64_t sampledFrame = ~0ull;
unsigned sampledDraws, sampledFrames;
std::map<unsigned, unsigned> drawsPerShader;
std::atomic<unsigned> seenDraws, seenCockpit, seenBinds, adjusted;
// Experiment (-gasvrcockpitprobe=N,SCALE,SHIFT): the interior shaders' helmetCamInteriorTweaks.zw
// (light * scale + shift, then clamped at 0) replaced in the cockpit view.
bool overrideOn;
float overrideScale, overrideShift;
std::map<std::pair<ID3D11Device*, UINT>, ID3D11Buffer*> copies;

void write(const wchar_t* path, const char* format, ...) {
    char line[4096];
    va_list args;
    va_start(args, format);
    vsnprintf(line, sizeof(line), format, args);
    va_end(args);
    if (FILE* file = _wfsopen(path, L"ab", _SH_DENYNO)) { fprintf(file, "%s\r\n", line); fclose(file); }
}

bool lightingName(const char* name) {
    static const char* const words[] = {"nterior", "elmet", "low", "mbient", "missive", "ockpit", "abin", "xposure", "SH", "Light", "light"};
    for (const char* word : words) if (strstr(name, word)) return true;
    return false;
}

Table& table(ID3D11DeviceContext* context) { return tables[*reinterpret_cast<void***>(context)]; }

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

void hookContext(ID3D11DeviceContext* context, const char* kind);

void reflect(ID3D11PixelShader* shader, const void* code, SIZE_T size) {
    ID3D11ShaderReflection* reflection = nullptr;
    if (FAILED(D3DReflect(code, size, IID_ID3D11ShaderReflection, reinterpret_cast<void**>(&reflection))) || !reflection) return;
    Shader info{};
    D3D11_SHADER_DESC desc{};
    reflection->GetDesc(&desc);
    std::string cbuffers;
    bool interior = false;
    for (UINT r = 0; r < desc.BoundResources; ++r) {
        D3D11_SHADER_INPUT_BIND_DESC bind{};
        if (FAILED(reflection->GetResourceBindingDesc(r, &bind))) continue;
        char item[160];
        snprintf(item, sizeof(item), "%s%s:%c%u", info.resources.empty() ? "" : " ", bind.Name,
                 bind.Type == D3D_SIT_CBUFFER ? 'b' : bind.Type == D3D_SIT_SAMPLER ? 's' : 't', bind.BindPoint);
        info.resources += item;
        if (bind.Type != D3D_SIT_SAMPLER && lightingName(bind.Name)) info.lighting = true;
        if (bind.Type != D3D_SIT_CBUFFER) continue;
        ID3D11ShaderReflectionConstantBuffer* buffer = reflection->GetConstantBufferByName(bind.Name);
        D3D11_SHADER_BUFFER_DESC bufferDesc{};
        if (!buffer || FAILED(buffer->GetDesc(&bufferDesc))) continue;
        for (UINT v = 0; v < bufferDesc.Variables; ++v) {
            D3D11_SHADER_VARIABLE_DESC variable{};
            if (FAILED(buffer->GetVariableByIndex(v)->GetDesc(&variable))) continue;
            info.variables.push_back({variable.Name, bind.BindPoint, variable.StartOffset, variable.Size});
            if (lightingName(variable.Name)) info.lighting = true;
            if ((variable.uFlags & D3D_SVF_USED) && (!strcmp(variable.Name, "helmetCamInteriorTweaks") || !strcmp(variable.Name, "interiorLightTweaks"))) interior = true;
            if ((variable.uFlags & D3D_SVF_USED) && bind.BindPoint == 0 && !strcmp(variable.Name, "helmetCamInteriorTweaks")) info.tweaks = int(variable.StartOffset);
            snprintf(item, sizeof(item), " %s@b%u+%u/%u%s", variable.Name, bind.BindPoint, variable.StartOffset, variable.Size,
                     (variable.uFlags & D3D_SVF_USED) ? "" : "(unused)");
            cbuffers += item;
        }
    }
    reflection->Release();
    std::lock_guard<std::mutex> guard(lock);
    info.id = ++nextId;
    write(shadersPath, "ps %u size=%u%s resources: %s", info.id, unsigned(size), info.lighting ? " LIGHTING" : "", info.resources.c_str());
    write(shadersPath, "ps %u variables:%s", info.id, cbuffers.c_str());
    if (interior) {
        // The interior shaders' disassembly, to see how the lighting values are used.
        ID3DBlob* text = nullptr;
        if (SUCCEEDED(D3DDisassemble(code, size, 0, nullptr, &text)) && text) {
            wchar_t path[MAX_PATH];
            swprintf_s(path, L"%s\\cockpit-ps%u.asm", folder, info.id);
            if (FILE* file = _wfsopen(path, L"wb", _SH_DENYNO)) { fwrite(text->GetBufferPointer(), 1, text->GetBufferSize() - 1, file); fclose(file); }
            text->Release();
        }
    }
    shaders[shader] = std::move(info);
}

HRESULT STDMETHODCALLTYPE createPs(ID3D11Device* device, const void* code, SIZE_T size, ID3D11ClassLinkage* linkage, ID3D11PixelShader** shader) {
    const HRESULT hr = stockCreatePs(device, code, size, linkage, shader);
    if (SUCCEEDED(hr) && shader && *shader && code) {
        reflect(*shader, code, size);
        // The device drawing the game may not be the first one created; its context class can differ.
        ID3D11DeviceContext* context = nullptr;
        device->GetImmediateContext(&context);
        if (context) { hookContext(context, "game immediate"); context->Release(); }
    }
    return hr;
}

void* swap(void** vtable, int slot, void* replacement);
void STDMETHODCALLTYPE drawIndexed(ID3D11DeviceContext* context, UINT count, UINT start, INT base);
void STDMETHODCALLTYPE drawIndexedInstanced(ID3D11DeviceContext* context, UINT count, UINT instances, UINT start, INT base, UINT first);

// The D3D11 runtime rewrites its immediate context's draw entries as state changes; take them back.
void rehookDraws(ID3D11DeviceContext* context, Table& t) {
    void** vtable = *reinterpret_cast<void***>(context);
    if (vtable[12] != reinterpret_cast<void*>(&drawIndexed)) t.drawIndexed = reinterpret_cast<DrawIndexedFn>(swap(vtable, 12, reinterpret_cast<void*>(&drawIndexed)));
    if (vtable[20] != reinterpret_cast<void*>(&drawIndexedInstanced))
        t.drawIndexedInstanced = reinterpret_cast<DrawIndexedInstancedFn>(swap(vtable, 20, reinterpret_cast<void*>(&drawIndexedInstanced)));
}

void STDMETHODCALLTYPE setPs(ID3D11DeviceContext* context, ID3D11PixelShader* shader, ID3D11ClassInstance* const* instances, UINT count) {
    SetPsFn stock;
    { std::lock_guard<std::mutex> guard(lock); current[context] = shader; Table& t = table(context); stock = t.setPs; }
    stock(context, shader, instances, count);
    ++seenBinds;
    std::lock_guard<std::mutex> guard(lock);
    rehookDraws(context, table(context));
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

// One line per lighting draw in a sampled frame: the shader, its textures' shapes and its named values.
void sample(ID3D11DeviceContext* context, UINT indices) {
    const unsigned seen = ++seenDraws;
    if ((seen & 0x3FFFF) == 1) AutosportLog("Cockpit light probe: draws=%u binds=%u cockpit draws=%u frame=%llu context=%p", seen, seenBinds.load(), seenCockpit.load(), AutosportNativeFrame(), context);
    if (!AutosportCockpitView()) return;
    ++seenCockpit;
    const uint64_t frame = AutosportNativeFrame();
    if (frame % every != 0) return;
    ID3D11PixelShader* shader;
    Shader info;
    {
        std::lock_guard<std::mutex> guard(lock);
        if (frame != sampledFrame) {
            if (sampledFrame != ~0ull && !drawsPerShader.empty()) {
                std::string counts;
                for (auto& [id, n] : drawsPerShader) counts += " ps" + std::to_string(id) + "x" + std::to_string(n);
                write(drawsPath, "frame %llu draws per shader:%s", sampledFrame, counts.c_str());
            }
            drawsPerShader.clear();
            sampledFrame = frame; sampledDraws = 0;
            if (++sampledFrames > 8) { every = 0x7FFFFFFF; return; }
            float head[4]{};
            AutosportHeadOrientation(head);
            write(drawsPath, "frame %llu head_q=(%.3f %.3f %.3f %.3f)", frame, head[0], head[1], head[2], head[3]);
        }
        auto found = current.find(context);
        shader = found == current.end() ? nullptr : found->second;
        auto known = shader ? shaders.find(shader) : shaders.end();
        if (known == shaders.end()) return;
        ++drawsPerShader[known->second.id];
        if (!known->second.lighting || ++sampledDraws > 300) return;
        info = known->second;
    }
    ID3D11Buffer* buffers[14]{};
    context->PSGetConstantBuffers(0, 14, buffers);
    std::string values;
    char item[256];
    {
        std::lock_guard<std::mutex> guard(lock);
        for (const Variable& v : info.variables) {
            if (!lightingName(v.name.c_str()) || v.slot >= 14 || !buffers[v.slot]) continue;
            auto found = contents.find(buffers[v.slot]);
            if (found == contents.end() || v.offset + v.size > found->second.size()) { values += " " + v.name + "=?"; continue; }
            const float* f = reinterpret_cast<const float*>(found->second.data() + v.offset);
            int used = snprintf(item, sizeof(item), " %s=", v.name.c_str());
            for (UINT i = 0; i < (std::min)(v.size / 4, 12u) && used < 220; ++i) used += snprintf(item + used, sizeof(item) - used, "%s%.4g", i ? "," : "", f[i]);
            values += item;
        }
    }
    for (ID3D11Buffer* b : buffers) if (b) b->Release();
    std::string textures;
    ID3D11ShaderResourceView* views[8]{};
    context->PSGetShaderResources(0, 8, views);
    for (UINT slot = 0; slot < 8; ++slot) {
        if (!views[slot]) continue;
        ID3D11Resource* resource = nullptr;
        views[slot]->GetResource(&resource);
        D3D11_SHADER_RESOURCE_VIEW_DESC desc{};
        views[slot]->GetDesc(&desc);
        D3D11_TEXTURE2D_DESC texture{};
        D3D11_RESOURCE_DIMENSION type{};
        if (resource) { resource->GetType(&type); if (type == D3D11_RESOURCE_DIMENSION_TEXTURE2D) static_cast<ID3D11Texture2D*>(resource)->GetDesc(&texture); resource->Release(); }
        snprintf(item, sizeof(item), " t%u=%ux%u/f%u/d%u", slot, texture.Width, texture.Height, unsigned(desc.Format), unsigned(desc.ViewDimension));
        textures += item;
        views[slot]->Release();
    }
    write(drawsPath, "draw ps%u indices=%u%s |%s", info.id, indices, textures.c_str(), values.c_str());
}

// The experiment: an interior draw gets a copy of its $Globals with the tweaks replaced; returns the
// game's buffer to bind back after the draw.
ID3D11Buffer* adjust(ID3D11DeviceContext* context) {
    if (!overrideOn || !AutosportCockpitView()) return nullptr;
    ID3D11Buffer* game = nullptr;
    std::vector<BYTE> data;
    {
        std::lock_guard<std::mutex> guard(lock);
        auto found = current.find(context);
        auto known = found == current.end() || !found->second ? shaders.end() : shaders.find(found->second);
        if (known == shaders.end() || known->second.tweaks < 0) return nullptr;
        context->PSGetConstantBuffers(0, 1, &game);
        auto kept = game ? contents.find(game) : contents.end();
        if (kept == contents.end() || UINT(known->second.tweaks) + 16 > kept->second.size()) { if (game) game->Release(); return nullptr; }
        data = kept->second;
        float* tweaks = reinterpret_cast<float*>(data.data() + known->second.tweaks);
        tweaks[2] = overrideScale; tweaks[3] = overrideShift;
    }
    ID3D11Device* device = nullptr;
    context->GetDevice(&device);
    ID3D11Buffer*& copy = copies[{device, UINT(data.size())}];
    if (!copy) {
        D3D11_BUFFER_DESC desc{UINT(data.size()), D3D11_USAGE_DEFAULT, D3D11_BIND_CONSTANT_BUFFER, 0, 0, 0};
        device->CreateBuffer(&desc, nullptr, &copy);
    }
    device->Release();
    if (!copy) { game->Release(); return nullptr; }
    context->UpdateSubresource(copy, 0, nullptr, data.data(), 0, 0);
    context->PSSetConstantBuffers(0, 1, &copy);
    if (++adjusted == 1) AutosportLog("Cockpit light probe: interior tweaks replaced (scale %.2f, shift %.2f)", overrideScale, overrideShift);
    return game;
}

void restore(ID3D11DeviceContext* context, ID3D11Buffer* game) {
    if (!game) return;
    context->PSSetConstantBuffers(0, 1, &game);
    game->Release();
}

void STDMETHODCALLTYPE drawIndexed(ID3D11DeviceContext* context, UINT count, UINT start, INT base) {
    sample(context, count);
    ID3D11Buffer* game = adjust(context);
    DrawIndexedFn stock;
    { std::lock_guard<std::mutex> guard(lock); stock = table(context).drawIndexed; }
    stock(context, count, start, base);
    restore(context, game);
}

void STDMETHODCALLTYPE drawIndexedInstanced(ID3D11DeviceContext* context, UINT count, UINT instances, UINT start, INT base, UINT first) {
    sample(context, count);
    ID3D11Buffer* game = adjust(context);
    DrawIndexedInstancedFn stock;
    { std::lock_guard<std::mutex> guard(lock); stock = table(context).drawIndexedInstanced; }
    stock(context, count, instances, start, base, first);
    restore(context, game);
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
    t.map = reinterpret_cast<MapFn>(swap(vtable, 14, reinterpret_cast<void*>(&map)));
    t.unmap = reinterpret_cast<UnmapFn>(swap(vtable, 15, reinterpret_cast<void*>(&unmap)));
    t.drawIndexedInstanced = reinterpret_cast<DrawIndexedInstancedFn>(swap(vtable, 20, reinterpret_cast<void*>(&drawIndexedInstanced)));
    t.update = reinterpret_cast<UpdateFn>(swap(vtable, 48, reinterpret_cast<void*>(&update)));
    AutosportLog("Cockpit light probe: hooked %s context vtable %p", kind, vtable);
}

HRESULT STDMETHODCALLTYPE createDeferred(ID3D11Device* device, UINT flags, ID3D11DeviceContext** context) {
    const HRESULT hr = stockCreateDeferred(device, flags, context);
    if (SUCCEEDED(hr) && context && *context) hookContext(*context, "deferred");
    return hr;
}
} // namespace

void AutosportConfigureCockpitLightProbe(ID3D11Device* device, const wchar_t* sessionFolder) {
    const wchar_t* arg = wcsstr(GetCommandLineW(), L" -gasvrcockpitprobe=");
    if (!device || stockCreatePs || !arg || !sessionFolder || !*sessionFolder) return;
    wchar_t* rest = nullptr;
    every = wcstoul(arg + wcslen(L" -gasvrcockpitprobe="), &rest, 10);
    if (!every) return;
    if (rest && *rest == L',') {
        overrideScale = wcstof(rest + 1, &rest);
        if (rest && *rest == L',') { overrideShift = wcstof(rest + 1, nullptr); overrideOn = true; }
    }
    wcscpy_s(folder, sessionFolder);
    swprintf_s(shadersPath, L"%s\\cockpit-shaders.log", sessionFolder);
    swprintf_s(drawsPath, L"%s\\cockpit-light.log", sessionFolder);
    ID3D11DeviceContext* context = nullptr;
    device->GetImmediateContext(&context);
    if (!context) return;
    void** deviceTable = *reinterpret_cast<void***>(device);
    stockCreatePs = reinterpret_cast<CreatePsFn>(swap(deviceTable, 15, reinterpret_cast<void*>(&createPs)));
    stockCreateDeferred = reinterpret_cast<CreateDeferredFn>(swap(deviceTable, 27, reinterpret_cast<void*>(&createDeferred)));
    hookContext(context, "immediate");
    context->Release();
    AutosportLog("Cockpit light probe: every %u frames in the cockpit view", every);
}
