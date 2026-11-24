#pragma once
#include <d3d11.h>
#include <cstdint>

// The game's D3D11CreateDevice (gasxr.dll's export; d3d11_proxy.cpp forwards the game's calls to it in
// the 2014 build's d3d11.dll), and the system's.
extern "C" HRESULT WINAPI AutosportCreateDevice(IDXGIAdapter* adapter, D3D_DRIVER_TYPE type, HMODULE software,
        UINT flags, const D3D_FEATURE_LEVEL* levels, UINT count, UINT sdk, ID3D11Device** device,
        D3D_FEATURE_LEVEL* achieved, ID3D11DeviceContext** context);
HRESULT AutosportSystemCreateDevice(IDXGIAdapter* adapter, D3D_DRIVER_TYPE type, HMODULE software, UINT flags, const D3D_FEATURE_LEVEL* levels,
        UINT count, UINT sdk, ID3D11Device** device, D3D_FEATURE_LEVEL* achieved, ID3D11DeviceContext** context);

bool AutosportUseHeadset();
bool AutosportRegularPlay();
bool AutosportStartInCockpit();
bool AutosportPracticeSession();
void AutosportSetVehicleViews(const void* manager);
void AutosportSampleVehicleCamera();
void AutosportSetPauseState(const void* state);
bool AutosportPreRaceIntro3D();
bool AutosportPauseMenuShown();
bool AutosportRaceScene();
// Headset play with the car camera live in a race: not the menus, an intro shot or the pause menu.
bool AutosportCarCameraLive();
// The game executable's loaded base.
const BYTE* AutosportGameImage();
// Replaces one import of the game executable; the original stays callable through *original.
bool AutosportHookGameImport(const char* dll, const char* function, void* replacement, void** original);
// Seat adjustment (seat.cpp): the head-cam view of this race, the cockpit check, offsets and panel.
void AutosportSetHeadCam(int index);
void AutosportSetDrivingViews(unsigned mask);
void AutosportRaceHudSetUp();
bool AutosportCockpitView();
void AutosportSeatConfigure(const wchar_t* sessionFolder);
void AutosportSeatCar(const char* id);
bool AutosportSeatOffset(float (&metres)[3]);
bool AutosportSeatBlocksKey(UINT vk);
bool AutosportSeatKey(UINT vk, bool down, bool repeat);
// The seat panel while it is open: car name, millimetres, Shift held (sideways), the five keys
// (panel, up, down, back, forward) and a version that changes whenever any of it does.
struct SeatPanelState { wchar_t name[96]; int right, up, forward; bool sideways; UINT keys[5]; unsigned version; };
bool AutosportSeatPanel(SeatPanelState& state);
// Paints the seat panel (GRID2VR's layout) into premultiplied BGRA pixels (seat_panel.cpp).
bool AutosportPaintSeatPanel(const SeatPanelState& state, BYTE* pixels, UINT width, UINT height);
// Replaces the LOD selector's apply call (RVA 5FE647): nearby opponents keep their best model in VR.
void __fastcall AutosportOpponentLodApply(void* manager, void*);
bool AutosportHudFollowsView();
bool AutosportHudParts(unsigned& mask);
float AutosportHudDistance();
bool AutosportDiagnosticLogging();
bool AutosportPerformanceDisplay();
bool AutosportClassicColor();
// Texture filtering (texture_filtering.cpp): sampler logging and the 16x anisotropic override.
void AutosportConfigureTextureFiltering(ID3D11Device* device);
// Shadow acne (shadow_bias.cpp): rasterizer state logging and the -gasvrshadowbias depth bias scale.
void AutosportConfigureShadowBias(ID3D11Device* device);
// Test only: -gasvrshadowcb=N logs the shadow mask draws' constants (shadow_constants.cpp).
void AutosportConfigureShadowConstants(ID3D11Device* device);
// VR scene depth in 32-bit float instead of the game's 16 bits (depth_precision.cpp).
void AutosportConfigureDepthPrecision(ID3D11Device* device);
// Shadow cascades (shadow_probe.cpp): the fit call (RVA 28B7E4 -> 284BF0) and its research probe.
void __fastcall AutosportShadowFit(void* self, void* edx, void* a0, void* a1, void* a2, void* a3, void* camera);
void AutosportConfigureShadowProbe(const BYTE* image);
// The native frame serial and the head orientation (x, y, z, w) given to the game (native_ovr.cpp).
uint64_t AutosportNativeFrame();
void AutosportHeadOrientation(float (&q)[4]);
float AutosportScreenDistance();
bool AutosportKeepActive();
bool AutosportRecenterShortcut(unsigned& key, unsigned& modifiers);
bool AutosportKeepHorizonLevel();
bool AutosportEyeSizeRequest(unsigned& width, unsigned& height);
bool AutosportUiComparison();
bool AutosportCompareSlate();
const char* AutosportTransitionSample();
bool AutosportFlatScreen();
// A career discipline / DLC championship intro video state is active (seated screen from the back buffer).
bool AutosportIntroVideo();
// A video opened over a loaded race scene (the career start's rush01.bk2) is playing.
bool AutosportSceneVideo();
bool AutosportStartupFromBackBuffer();
void __fastcall AutosportLoadingEntered(void* dispatcher, void*, const void* event);
void __fastcall AutosportLoadingExited(void* dispatcher, void*, const void* event);
// Practice / Race device claim (device_claim.cpp): -gasvrdevice=FAMILY[,FAMILY...].
bool AutosportTakeDeviceRecenter();
// Cockpit texture observe / replace (cockpit_textures.cpp): -gasvrcockpittextures=observe|replace.
struct ID3D11Device;
void AutosportConfigureCockpitTextures(ID3D11Device* device, const wchar_t* sessionFolder, const wchar_t* gameFolder);
// Launcher force feedback (force_feedback.cpp): -gasvrffb=STRENGTH,WEIGHT,VIBRATION, from DllMain.
void AutosportConfigureForceFeedback(const BYTE* image, const wchar_t* commandLine);
void AutosportConfigureDeviceClaim(const BYTE* image, const wchar_t* commandLine, bool practice);
extern "C" void AutosportLoadingExitedThunk();
bool AutosportCaptureRequested();
void AutosportCaptureCompleted();
const wchar_t* AutosportRunDirectory();
void AutosportLog(const char* format, ...);
