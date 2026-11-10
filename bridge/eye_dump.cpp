#include "eye_dump.h"
#include <wrl/client.h>
#include <DirectXPackedVector.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <stdexcept>
#include <string>
#include <vector>

namespace fs = std::filesystem;
using Microsoft::WRL::ComPtr;
namespace {
using Pixel = std::array<unsigned char, 3>; // BMP byte order
unsigned char encodeLinear(float value) {
    const float f = std::clamp(value, 0.f, 1.f);
    if (!std::isfinite(f)) return 0;
    const float srgb = f <= .0031308f ? 12.92f*f : 1.055f*std::pow(f, 1.f/2.4f) - .055f;
    return static_cast<unsigned char>(std::lround(255.f*srgb));
}
Pixel pixel(const unsigned char* p, DXGI_FORMAT format) {
    switch (format) {
    case DXGI_FORMAT_R8G8B8A8_UNORM:
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: return {p[2], p[1], p[0]};
    case DXGI_FORMAT_B8G8R8A8_UNORM:
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
    case DXGI_FORMAT_B8G8R8X8_UNORM:
    case DXGI_FORMAT_B8G8R8X8_UNORM_SRGB: return {p[0], p[1], p[2]};
    case DXGI_FORMAT_R10G10B10A2_UNORM: {
        uint32_t bits;
        std::memcpy(&bits, p, sizeof(bits));
        return {static_cast<unsigned char>(((bits>>20)&1023)*255/1023),
                static_cast<unsigned char>(((bits>>10)&1023)*255/1023),
                static_cast<unsigned char>((bits&1023)*255/1023)};
    }
    case DXGI_FORMAT_R16G16B16A16_FLOAT: {
        uint16_t channels[4];
        std::memcpy(channels, p, sizeof(channels));
        using DirectX::PackedVector::XMConvertHalfToFloat;
        return {encodeLinear(XMConvertHalfToFloat(channels[2])), encodeLinear(XMConvertHalfToFloat(channels[1])),
                encodeLinear(XMConvertHalfToFloat(channels[0]))};
    }
    case DXGI_FORMAT_R11G11B10_FLOAT: {
        DirectX::PackedVector::XMFLOAT3PK packed;
        std::memcpy(&packed, p, sizeof(packed));
        DirectX::XMFLOAT3 channels;
        DirectX::XMStoreFloat3(&channels, DirectX::PackedVector::XMLoadFloat3PK(&packed));
        return {encodeLinear(channels.z), encodeLinear(channels.y), encodeLinear(channels.x)};
    }
    default: throw std::runtime_error("unsupported_capture_format");
    }
}
void writeBmp(const fs::path& path, const D3D11_MAPPED_SUBRESOURCE& source, DXGI_FORMAT format, const RECT& rect) {
    const DWORD w = rect.right-rect.left, h = rect.bottom-rect.top;
    const DWORD pitch = (3*w+3)&~3u;
    BITMAPFILEHEADER file{};
    BITMAPINFOHEADER info{};
    file.bfType = 0x4d42;
    file.bfOffBits = sizeof(file)+sizeof(info);
    file.bfSize = file.bfOffBits + pitch*h;
    info.biSize = sizeof(info); info.biWidth = w; info.biHeight = -LONG(h); // top-down
    info.biPlanes = 1; info.biBitCount = 24; info.biSizeImage = pitch*h;
    std::ofstream out(path, std::ios::binary);
    out.exceptions(std::ios::badbit | std::ios::failbit);
    out.write(reinterpret_cast<const char*>(&file), sizeof(file));
    out.write(reinterpret_cast<const char*>(&info), sizeof(info));
    const size_t bpp = format == DXGI_FORMAT_R16G16B16A16_FLOAT ? 8 : 4;
    std::vector<unsigned char> row(pitch);
    for (DWORD y = 0; y < h; ++y) {
        auto src = static_cast<const unsigned char*>(source.pData)+(rect.top+y)*size_t(source.RowPitch)+rect.left*bpp;
        for (DWORD x = 0; x < w; ++x) {
            const auto bgr = pixel(src+x*bpp, format);
            std::memcpy(row.data()+x*3, bgr.data(), 3);
        }
        out.write(reinterpret_cast<const char*>(row.data()), row.size());
    }
    out.close();
}
template <size_t N> void array(std::ostream& out, const float (&v)[N]) {
    out << '[';
    for (size_t i = 0; i < N; ++i) out << (i ? "," : "") << v[i];
    out << ']';
}
}

void WriteEyePair(const wchar_t* directory, ID3D11Device* device, ID3D11DeviceContext* context,
                  ID3D11Texture2D* snapshot, DXGI_FORMAT format, const RECT (&rects)[2],
                  const BridgeView (&views)[2], bool headset, uint64_t frame, int64_t predictedTime,
                  unsigned sourceSubresource, const D3D11_TEXTURE2D_DESC& source) {
    D3D11_TEXTURE2D_DESC desc{};
    snapshot->GetDesc(&desc);
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = desc.MiscFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> staging;
    if (FAILED(device->CreateTexture2D(&desc, nullptr, &staging))) throw std::runtime_error("create_eye_readback");
    context->CopyResource(staging.Get(), snapshot);
    D3D11_MAPPED_SUBRESOURCE data{};
    if (FAILED(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &data))) throw std::runtime_error("map_eye_pair");
    struct Unmap {
        ID3D11DeviceContext* ctx; ID3D11Texture2D* texture;
        ~Unmap() { ctx->Unmap(texture, 0); }
    } unmap{context, staging.Get()};
    const fs::path root(directory);
    fs::create_directories(root);
    const auto name = L"frame-" + std::to_wstring(frame) + L"-" + std::to_wstring(GetCurrentProcessId());
    const auto pending = root / (name + L".pending"), complete = root / name;
    if (fs::exists(complete) || !fs::create_directory(pending)) throw std::runtime_error("eye_pair_already_exists");
    try {
        writeBmp(pending / "left.bmp", data, format, rects[0]);
        writeBmp(pending / "right.bmp", data, format, rects[1]);
        std::ofstream out(pending / "frame.json");
        out.exceptions(std::ios::badbit | std::ios::failbit);
        out << std::setprecision(9) << "{\n  \"frame\":" << frame << ", \"pid\":" << GetCurrentProcessId()
            << ", \"pose_source\":\"" << (headset ? "OpenXR" : "fixed_reference") << "\", \"predicted_display_time\":" << predictedTime
            << ",\n  \"source\":{\"width\":" << source.Width << ",\"height\":" << source.Height
            << ",\"format\":" << source.Format << ",\"subresource\":" << sourceSubresource
            << ",\"sample_count\":" << source.SampleDesc.Count << ",\"sample_quality\":" << source.SampleDesc.Quality
            << ",\"mip_levels\":" << source.MipLevels << ",\"array_size\":" << source.ArraySize
            << ",\"snapshot_format\":" << format << ",\"native_reads\":1},\n  \"eyes\":[";
        for (unsigned i = 0; i < 2; ++i) {
            const auto& r = rects[i];
            out << (i ? "," : "") << "\n    {\"eye\":\"" << (i ? "right" : "left") << "\",\"rect_ltrb\":["
                << r.left << ',' << r.top << ',' << r.right << ',' << r.bottom << "],\"position_m\":";
            array(out, views[i].position); out << ",\"orientation_xyzw\":";
            array(out, views[i].orientation); out << ",\"fov_lrud_rad\":"; array(out, views[i].fov); out << '}';
        }
        out << "\n  ]\n}\n";
        out.close();
        fs::rename(pending, complete);
    } catch (...) {
        std::error_code ignored;
        for (const auto* file : {"left.bmp","right.bmp","frame.json"}) fs::remove(pending / file, ignored);
        fs::remove(pending, ignored);
        throw;
    }
}
