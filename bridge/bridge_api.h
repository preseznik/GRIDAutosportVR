#pragma once
#include <d3d11.h>

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
// The game executable's loaded base.
const BYTE* AutosportGameImage();
// Replaces one import of the game executable; the original stays callable through *original.
bool AutosportHookGameImport(const char* dll, const char* function, void* replacement, void** original);
// Seat adjustment (seat.cpp): the head-cam view of this race, the cockpit check, offsets and panel.
void AutosportSetHeadCam(int index);
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
void AutosportConfigureDeviceClaim(const BYTE* image, const wchar_t* commandLine, bool practice);
extern "C" void AutosportLoadingExitedThunk();
bool AutosportCaptureRequested();
void AutosportCaptureCompleted();
const wchar_t* AutosportRunDirectory();
void AutosportLog(const char* format, ...);
