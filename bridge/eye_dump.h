#pragma once
#include "xr_bridge.h"

void WriteEyePair(const wchar_t* directory, ID3D11Device* device, ID3D11DeviceContext* context,
                  ID3D11Texture2D* snapshot, DXGI_FORMAT format, const RECT (&rects)[2],
                  const BridgeView (&views)[2], bool headset, uint64_t frame, int64_t predictedTime,
                  unsigned sourceSubresource, const D3D11_TEXTURE2D_DESC& source);
