// Cockpit textures (GRID2VR's focus_cockpit, for GRID Autosport): every car's cockpit atlas is
// <car>_cabin.tga in its livery pack, 512x512 DXT5 (BC3) with 9 mips (tools/Export-CockpitTextures.ps1).
// The game creates it with its pixels as initial data; the bridge recognises it by the SHA-256
// of those pixels and, with a pack installed, gives the game's shader resource view of it a
// view of a higher-resolution replacement instead. No game file is touched.
//
// -gasvrcockpittextures=observe  logs every texture shape and each 512x512 BC3 atlas's hash
//                                (cockpit-textures.log in the session folder);
// -gasvrcockpittextures=replace  also swaps in <game>\GRIDAutosportVR\addons\cockpit-textures\
//                                <car>.dds for each atlas listed in catalog.txt there
//                                ("car sourceSha256 ddsSha256" per line; the launcher verifies
//                                the pack before launch, the bridge checks each file again).
// Hooks: ID3D11Device CreateTexture2D (vtable slot 5) and CreateShaderResourceView (slot 7).
#include <windows.h>
#include <d3d11.h>
#include <bcrypt.h>
#include <algorithm>
#include <cstdarg>
#include <share.h>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <mutex>
#include <set>
#include <string>
#include <vector>
#include "bridge_api.h"
#pragma comment(lib, "bcrypt.lib")

namespace {
using TextureFn = HRESULT (STDMETHODCALLTYPE*)(ID3D11Device*, const D3D11_TEXTURE2D_DESC*, const D3D11_SUBRESOURCE_DATA*, ID3D11Texture2D**);
using ViewFn = HRESULT (STDMETHODCALLTYPE*)(ID3D11Device*, ID3D11Resource*, const D3D11_SHADER_RESOURCE_VIEW_DESC*, ID3D11ShaderResourceView**);
constexpr GUID replacementId{0x3c1d7a52, 0x8e0b, 0x4a7f, {0x9b, 0x31, 0x55, 0x0e, 0x6d, 0x2a, 0x91, 0xc4}};
constexpr GUID observedId{0x5a9e04d1, 0x2f6c, 0x4b18, {0x86, 0x7d, 0x0b, 0xe2, 0x44, 0x13, 0xa9, 0x5f}};
constexpr UINT stockSize = 512, stockMips = 9;

enum class Mode { Off, Observe, Replace };
Mode mode = Mode::Off;
TextureFn stockCreateTexture;
ViewFn stockCreateView;
wchar_t logPath[MAX_PATH], packFolder[MAX_PATH];
struct Atlas { std::string car, source, dds; };
std::vector<Atlas> atlases;
std::mutex lock;
std::set<std::string> shapes, refused;
unsigned events, matches, replacements, fallbacks;

void event(const char* format, ...) {
    if (!*logPath || events >= 512) return;
    ++events;
    char line[640];
    va_list args;
    va_start(args, format);
    vsnprintf(line, sizeof(line), format, args);
    va_end(args);
    if (FILE* file = _wfsopen(logPath, L"ab", _SH_DENYNO)) { fprintf(file, "%s\r\n", line); fclose(file); }
}

std::string sha256(const unsigned char* data, size_t bytes) {
    BCRYPT_ALG_HANDLE algorithm{};
    BCRYPT_HASH_HANDLE hash{};
    unsigned char result[32]{};
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) return {};
    bool ok = BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) >= 0 &&
              BCryptHashData(hash, const_cast<PUCHAR>(data), static_cast<ULONG>(bytes), 0) >= 0 &&
              BCryptFinishHash(hash, result, sizeof(result), 0) >= 0;
    if (hash) BCryptDestroyHash(hash);
    BCryptCloseAlgorithmProvider(algorithm, 0);
    std::string text;
    if (ok) for (unsigned char b : result) { text += "0123456789abcdef"[b >> 4]; text += "0123456789abcdef"[b & 15]; }
    return text;
}

UINT blocks(UINT size) { return (std::max)(1u, (size + 3) / 4); }
bool bc3(DXGI_FORMAT f) { return f == DXGI_FORMAT_BC3_TYPELESS || f == DXGI_FORMAT_BC3_UNORM || f == DXGI_FORMAT_BC3_UNORM_SRGB; }
bool candidate(const D3D11_TEXTURE2D_DESC& d) {
    return d.Width == stockSize && d.Height == stockSize && d.MipLevels >= 1 && d.MipLevels <= 10 && d.ArraySize == 1 && bc3(d.Format) &&
           d.SampleDesc.Count == 1 && d.BindFlags == D3D11_BIND_SHADER_RESOURCE && !d.CPUAccessFlags && !d.MiscFlags &&
           (d.Usage == D3D11_USAGE_DEFAULT || d.Usage == D3D11_USAGE_IMMUTABLE);
}

// The tight BC3 rows of every uploaded mip (driver pitch padding excluded), read under SEH.
// 512x512 BC3 down to 1x1 is 349,568 bytes; the caller holds the lock.
unsigned char tight[512 * 1024];
size_t copyTight(const D3D11_SUBRESOURCE_DATA* data, UINT mipCount) {
    size_t at = 0;
    __try {
        for (UINT mip = 0; mip < mipCount; ++mip) {
            const UINT size = (std::max)(1u, stockSize >> mip), pitch = blocks(size) * 16, rows = blocks(size);
            const D3D11_SUBRESOURCE_DATA& src = data[mip];
            if (!src.pSysMem || src.SysMemPitch < pitch) return 0;
            for (UINT row = 0; row < rows; ++row) {
                if (at + pitch > sizeof(tight)) return 0;
                std::memcpy(tight + at, static_cast<const unsigned char*>(src.pSysMem) + size_t(row) * src.SysMemPitch, pitch);
                at += pitch;
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
    return at;
}
std::string sourceHash(const D3D11_SUBRESOURCE_DATA* data, UINT mipCount) {
    if (!data) return {};
    const size_t bytes = copyTight(data, mipCount);
    return bytes ? sha256(tight, bytes) : std::string();
}

// A replacement DDS: legacy 128-byte header, square BC3 (DXT5), a full mip chain.
bool loadReplacement(const Atlas& atlas, std::vector<unsigned char>& pixels, UINT& size, UINT& mips) {
    wchar_t path[MAX_PATH];
    swprintf_s(path, L"%s\\%hs.dds", packFolder, atlas.car.c_str());
    const DWORD attributes = GetFileAttributesW(path);
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_REPARSE_POINT)) return false;
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER length{};
    std::vector<unsigned char> bytes;
    if (GetFileSizeEx(file, &length) && length.QuadPart > 128 && length.QuadPart < (64 << 20)) {
        bytes.resize(size_t(length.QuadPart));
        DWORD read{};
        if (!ReadFile(file, bytes.data(), DWORD(bytes.size()), &read, nullptr) || read != bytes.size()) bytes.clear();
    }
    CloseHandle(file);
    if (bytes.empty() || sha256(bytes.data(), bytes.size()) != atlas.dds) return false;
    UINT h[32];
    std::memcpy(h, bytes.data(), sizeof(h));
    size = h[4]; mips = h[7];
    if (h[0] != 0x20534444 || h[1] != 124 || h[3] != size || size < stockSize || size > 4096 || (size & (size - 1)) ||
        h[19] != 32 || !(h[20] & 4) || h[21] != 0x35545844 /* DXT5 */ || mips < 1 || mips > 13) return false;
    size_t expected = 0;
    for (UINT mip = 0; mip < mips; ++mip) expected += size_t(blocks((std::max)(1u, size >> mip))) * blocks((std::max)(1u, size >> mip)) * 16;
    if (bytes.size() != 128 + expected) return false;
    pixels.assign(bytes.begin() + 128, bytes.end());
    return true;
}

HRESULT STDMETHODCALLTYPE createTexture(ID3D11Device* device, const D3D11_TEXTURE2D_DESC* desc, const D3D11_SUBRESOURCE_DATA* data, ID3D11Texture2D** texture) {
    const HRESULT hr = stockCreateTexture(device, desc, data, texture);
    if (FAILED(hr) || !desc || !texture || !*texture) return hr;
    const D3D11_TEXTURE2D_DESC d = *desc;
    std::lock_guard<std::mutex> guard(lock);
    if (mode == Mode::Observe) {
        char shape[160];
        sprintf_s(shape, "%ux%u mips=%u array=%u format=%u usage=%u bind=%u cpu=%u misc=%u data=%d",
                  d.Width, d.Height, d.MipLevels, d.ArraySize, unsigned(d.Format), unsigned(d.Usage), d.BindFlags, d.CPUAccessFlags, d.MiscFlags, data != nullptr);
        if (shapes.size() < 96 && shapes.insert(shape).second) event("shape %s", shape);
    }
    if (!candidate(d) || !data) return hr;
    const std::string hash = sourceHash(data, d.MipLevels);
    const auto found = std::find_if(atlases.begin(), atlases.end(), [&](const Atlas& a) { return a.source == hash; });
    if (mode == Mode::Observe || found == atlases.end()) {
        event("atlas mips=%u sha256=%s car=%s", d.MipLevels, hash.c_str(), found == atlases.end() ? "-" : found->car.c_str());
        if (found == atlases.end()) return hr;
    }
    ++matches;
    const UINT tag = d.MipLevels;
    if (FAILED((*texture)->SetPrivateData(observedId, sizeof(tag), &tag)) || mode != Mode::Replace || refused.count(found->car)) return hr;
    // Read, upload and free one replacement at a time: this is a 32-bit game.
    std::vector<unsigned char> pixels;
    UINT size = 0, mips = 0;
    if (!loadReplacement(*found, pixels, size, mips)) {
        refused.insert(found->car); ++fallbacks;
        event("stock kept for %s: its replacement failed verification", found->car.c_str());
        return hr;
    }
    std::vector<D3D11_SUBRESOURCE_DATA> levels(mips);
    size_t offset = 0;
    for (UINT mip = 0; mip < mips; ++mip) {
        const UINT side = (std::max)(1u, size >> mip), pitch = blocks(side) * 16;
        levels[mip] = {pixels.data() + offset, pitch, pitch * blocks(side)};
        offset += size_t(pitch) * blocks(side);
    }
    D3D11_TEXTURE2D_DESC larger = d;
    larger.Width = larger.Height = size; larger.MipLevels = mips; larger.Usage = D3D11_USAGE_IMMUTABLE;
    ID3D11Texture2D* replacement = nullptr;
    if (FAILED(stockCreateTexture(device, &larger, levels.data(), &replacement)) || !replacement ||
        FAILED((*texture)->SetPrivateDataInterface(replacementId, replacement))) {
        ++fallbacks;
        event("stock kept for %s: the replacement texture could not be created", found->car.c_str());
    }
    if (replacement) replacement->Release(); // The game's texture holds it as private data.
    return hr;
}

HRESULT STDMETHODCALLTYPE createView(ID3D11Device* device, ID3D11Resource* resource, const D3D11_SHADER_RESOURCE_VIEW_DESC* desc, ID3D11ShaderResourceView** view) {
    const HRESULT hr = stockCreateView(device, resource, desc, view);
    if (FAILED(hr) || !resource || !view || !*view || mode != Mode::Replace) return hr;
    UINT tag = 0, bytes = sizeof(tag);
    if (FAILED(resource->GetPrivateData(observedId, &bytes, &tag))) return hr;
    D3D11_SHADER_RESOURCE_VIEW_DESC actual{};
    (*view)->GetDesc(&actual);
    if (actual.ViewDimension != D3D11_SRV_DIMENSION_TEXTURE2D || actual.Texture2D.MostDetailedMip != 0 || actual.Texture2D.MipLevels != tag || !bc3(actual.Format)) return hr;
    ID3D11Texture2D* replacement = nullptr;
    bytes = sizeof(replacement);
    if (FAILED(resource->GetPrivateData(replacementId, &bytes, &replacement)) || !replacement) return hr;
    D3D11_TEXTURE2D_DESC larger{};
    replacement->GetDesc(&larger);
    actual.Texture2D.MipLevels = larger.MipLevels;
    ID3D11ShaderResourceView* swapped = nullptr;
    if (SUCCEEDED(stockCreateView(device, replacement, &actual, &swapped)) && swapped) {
        (*view)->Release();
        *view = swapped;
        std::lock_guard<std::mutex> guard(lock);
        if (++replacements <= 64) event("replacement view %u (%ux%u)", replacements, larger.Width, larger.Height);
    }
    replacement->Release();
    return hr;
}

bool loadCatalog() {
    wchar_t path[MAX_PATH];
    swprintf_s(path, L"%s\\catalog.txt", packFolder);
    FILE* file = _wfsopen(path, L"rb", _SH_DENYWR);
    if (!file) return false;
    char line[256];
    while (fgets(line, sizeof(line), file) && atlases.size() < 256) {
        char car[16]{}, source[80]{}, dds[80]{};
        if (sscanf_s(line, "%15s %79s %79s", car, unsigned(sizeof(car)), source, unsigned(sizeof(source)), dds, unsigned(sizeof(dds))) == 3 &&
            std::strlen(source) == 64 && std::strlen(dds) == 64 && std::strspn(car, "abcdefghijklmnopqrstuvwxyz0123456789") == std::strlen(car))
            atlases.push_back({car, source, dds});
    }
    fclose(file);
    return !atlases.empty();
}
} // namespace

void AutosportConfigureCockpitTextures(ID3D11Device* device, const wchar_t* sessionFolder, const wchar_t* gameFolder) {
    static bool done;
    if (done || !device) return;
    done = true;
    const wchar_t* cmd = GetCommandLineW();
    if (wcsstr(cmd, L" -gasvrcockpittextures=observe")) mode = Mode::Observe;
    else if (wcsstr(cmd, L" -gasvrcockpittextures=replace")) mode = Mode::Replace;
    else return;
    if (sessionFolder && *sessionFolder) swprintf_s(logPath, L"%s\\cockpit-textures.log", sessionFolder);
    swprintf_s(packFolder, L"%s\\GRIDAutosportVR\\addons\\cockpit-textures", gameFolder);
    loadCatalog();
    if (mode == Mode::Replace && atlases.empty()) {
        mode = Mode::Off;
        AutosportLog("Cockpit textures: no pack catalog in %ls; stock textures", packFolder);
        return;
    }
    void** table = *reinterpret_cast<void***>(device);
    DWORD old{};
    if (!VirtualProtect(table + 5, 3 * sizeof(void*), PAGE_EXECUTE_READWRITE, &old)) { mode = Mode::Off; return; }
    stockCreateTexture = reinterpret_cast<TextureFn>(table[5]);
    stockCreateView = reinterpret_cast<ViewFn>(table[7]);
    table[5] = reinterpret_cast<void*>(&createTexture);
    table[7] = reinterpret_cast<void*>(&createView);
    VirtualProtect(table + 5, 3 * sizeof(void*), old, &old);
    AutosportLog("Cockpit textures: %s, %u atlases in the catalog", mode == Mode::Replace ? "replace" : "observe", unsigned(atlases.size()));
}
