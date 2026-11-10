#pragma once
#include <windows.h>
#include <d3d11.h>
#include <cstdint>

// OpenXR coordinates: metres, +X right, +Y up, -Z forward; FOV angles in radians.
struct BridgeView {
    float position[3];
    float orientation[4]; // x, y, z, w
    float fov[4];         // left, right, up, down
};

// Call from the same game render thread on the device's immediate context.
// The frame number identifies one native scene tick and must increase within a session.
bool AutosportBeginViews(ID3D11Device* device, ID3D11DeviceContext* context, bool useHeadset,
                         uint64_t frame, BridgeView (&views)[2]) noexcept;

struct BridgeEndResult { bool submitted, captured; };
enum class BridgePresentation { Stereo, FlatScreen };
// rects are left then right, relative to the selected mip. For disk evidence, pass
// an existing or creatable directory. Pass nullptr on ordinary presentation frames.
BridgeEndResult AutosportEndViews(ID3D11Texture2D* packed, unsigned subresource,
                                  const RECT (&rects)[2], uint64_t frame,
                                  const wchar_t* captureDirectory,
                                  BridgePresentation presentation = BridgePresentation::Stereo) noexcept;
void AutosportCancelViews(uint64_t frame) noexcept;
void AutosportCloseBridge() noexcept;
void AutosportPrepareForGameExit() noexcept;
// Static status string for the caller's log. No game process file or config writes.
const char* AutosportBridgeStatus() noexcept;
double AutosportDisplayPeriodSeconds() noexcept;
// True once, on the render thread, after the first FOCUSED of a session whose views were
// already in use: the caller recenters its seated origin on the focused pose.
bool AutosportTakeRecenterRequest() noexcept;
// Place the seated screen again in front of the current head pose (recenter key, game recenter).
void AutosportRecenterSeatedScreen() noexcept;
