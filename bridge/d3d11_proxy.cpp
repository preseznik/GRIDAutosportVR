// The 2014 build's renderer as d3d11.dll (CMake target gasxr_classic, GASVR_PROXY). The 2014
// GRIDAutosport.exe stays unmodified: it imports d3d11.dll, which Windows loads from the game's folder
// first (d3d11.dll is not a KnownDLL), so the launcher stages this file there for the session.
// D3D11CreateDevice called from the game image is the bridge's (AutosportCreateDevice); from anywhere
// else (an OpenXR runtime, an overlay) it is the system's. Every other export jumps to the system
// d3d11.dll, loaded by full path on first use. Generated from SysWOW64\d3d11.dll's 51 exports (same
// names and ordinals); see bridge/README.md.
#include "bridge_api.h"
#include <windows.h>
#include <d3d11.h>
#include <intrin.h>

namespace {
HMODULE systemD3D11() {
    static HMODULE module;
    if (!module) {
        wchar_t path[MAX_PATH]{};
        const UINT length = GetSystemDirectoryW(path, MAX_PATH);
        if (length && length < MAX_PATH - 12) {
            wcscat_s(path, L"\\d3d11.dll");
            module = LoadLibraryW(path);
        }
    }
    return module;
}
using CreateDeviceFn = HRESULT(WINAPI*)(IDXGIAdapter*, D3D_DRIVER_TYPE, HMODULE, UINT, const D3D_FEATURE_LEVEL*, UINT, UINT,
                                        ID3D11Device**, D3D_FEATURE_LEVEL*, ID3D11DeviceContext**);
CreateDeviceFn systemCreateDevice() {
    static CreateDeviceFn create;
    if (!create)
        if (HMODULE module = systemD3D11()) create = reinterpret_cast<CreateDeviceFn>(GetProcAddress(module, "D3D11CreateDevice"));
    return create;
}
bool fromGame(const void* caller) {
    const auto* base = reinterpret_cast<const BYTE*>(GetModuleHandleW(nullptr));
    const auto* pe = reinterpret_cast<const IMAGE_NT_HEADERS32*>(base + reinterpret_cast<const IMAGE_DOS_HEADER*>(base)->e_lfanew);
    const auto* at = static_cast<const BYTE*>(caller);
    return at >= base && at < base + pe->OptionalHeader.SizeOfImage;
}
}

HRESULT AutosportSystemCreateDevice(IDXGIAdapter* adapter, D3D_DRIVER_TYPE type, HMODULE software, UINT flags, const D3D_FEATURE_LEVEL* levels,
        UINT count, UINT sdk, ID3D11Device** device, D3D_FEATURE_LEVEL* achieved, ID3D11DeviceContext** context) {
    const CreateDeviceFn create = systemCreateDevice();
    if (!create) return E_FAIL;
    return create(adapter, type, software, flags, levels, count, sdk, device, achieved, context);
}

extern "C" HRESULT WINAPI AutosportProxyCreateDevice(IDXGIAdapter* adapter, D3D_DRIVER_TYPE type, HMODULE software, UINT flags,
        const D3D_FEATURE_LEVEL* levels, UINT count, UINT sdk, ID3D11Device** device, D3D_FEATURE_LEVEL* achieved, ID3D11DeviceContext** context) {
    if (fromGame(_ReturnAddress())) return AutosportCreateDevice(adapter, type, software, flags, levels, count, sdk, device, achieved, context);
    return AutosportSystemCreateDevice(adapter, type, software, flags, levels, count, sdk, device, achieved, context);
}

// The other exports: a jump through this table, filled on each one's first call.
extern "C" void* autosportProxyTable[50] = {};
namespace {
const char* const proxyNames[50] = {
    "D3D11CreateDeviceForD3D12",
    "D3DKMTCloseAdapter",
    "D3DKMTDestroyAllocation",
    "D3DKMTDestroyContext",
    "D3DKMTDestroyDevice",
    "D3DKMTDestroySynchronizationObject",
    "D3DKMTPresent",
    "D3DKMTQueryAdapterInfo",
    "D3DKMTSetDisplayPrivateDriverFormat",
    "D3DKMTSignalSynchronizationObject",
    "D3DKMTUnlock",
    "D3DKMTWaitForSynchronizationObject",
    "EnableFeatureLevelUpgrade",
    "OpenAdapter10",
    "OpenAdapter10_2",
    "CreateDirect3D11DeviceFromDXGIDevice",
    "CreateDirect3D11SurfaceFromDXGISurface",
    "D3D11CoreCreateDevice",
    "D3D11CoreCreateLayeredDevice",
    "D3D11CoreGetLayeredDeviceSize",
    "D3D11CoreRegisterLayers",
    "D3D11CreateDeviceAndSwapChain",
    "D3D11On12CreateDevice",
    "D3DKMTCreateAllocation",
    "D3DKMTCreateContext",
    "D3DKMTCreateDevice",
    "D3DKMTCreateSynchronizationObject",
    "D3DKMTEscape",
    "D3DKMTGetContextSchedulingPriority",
    "D3DKMTGetDeviceState",
    "D3DKMTGetDisplayModeList",
    "D3DKMTGetMultisampleMethodList",
    "D3DKMTGetRuntimeData",
    "D3DKMTGetSharedPrimaryHandle",
    "D3DKMTLock",
    "D3DKMTOpenAdapterFromHdc",
    "D3DKMTOpenResource",
    "D3DKMTQueryAllocationResidency",
    "D3DKMTQueryResourceInfo",
    "D3DKMTRender",
    "D3DKMTSetAllocationPriority",
    "D3DKMTSetContextSchedulingPriority",
    "D3DKMTSetDisplayMode",
    "D3DKMTSetGammaRamp",
    "D3DKMTSetVidPnSourceOwner",
    "D3DKMTWaitForVerticalBlankEvent",
    "D3DPerformance_BeginEvent",
    "D3DPerformance_EndEvent",
    "D3DPerformance_GetStatus",
    "D3DPerformance_SetMarker"};
}
extern "C" void __cdecl autosportProxyResolve(unsigned index) {
    if (HMODULE module = systemD3D11()) autosportProxyTable[index] = reinterpret_cast<void*>(GetProcAddress(module, proxyNames[index]));
}

extern "C" __declspec(naked) void autosportProxy_D3D11CreateDeviceForD3D12() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 0]
        test eax, eax
        jnz forward
        pushad
        push 0
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 0]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTCloseAdapter() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 4]
        test eax, eax
        jnz forward
        pushad
        push 1
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 4]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTDestroyAllocation() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 8]
        test eax, eax
        jnz forward
        pushad
        push 2
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 8]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTDestroyContext() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 12]
        test eax, eax
        jnz forward
        pushad
        push 3
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 12]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTDestroyDevice() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 16]
        test eax, eax
        jnz forward
        pushad
        push 4
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 16]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTDestroySynchronizationObject() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 20]
        test eax, eax
        jnz forward
        pushad
        push 5
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 20]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTPresent() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 24]
        test eax, eax
        jnz forward
        pushad
        push 6
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 24]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTQueryAdapterInfo() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 28]
        test eax, eax
        jnz forward
        pushad
        push 7
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 28]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTSetDisplayPrivateDriverFormat() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 32]
        test eax, eax
        jnz forward
        pushad
        push 8
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 32]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTSignalSynchronizationObject() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 36]
        test eax, eax
        jnz forward
        pushad
        push 9
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 36]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTUnlock() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 40]
        test eax, eax
        jnz forward
        pushad
        push 10
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 40]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTWaitForSynchronizationObject() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 44]
        test eax, eax
        jnz forward
        pushad
        push 11
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 44]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_EnableFeatureLevelUpgrade() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 48]
        test eax, eax
        jnz forward
        pushad
        push 12
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 48]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_OpenAdapter10() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 52]
        test eax, eax
        jnz forward
        pushad
        push 13
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 52]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_OpenAdapter10_2() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 56]
        test eax, eax
        jnz forward
        pushad
        push 14
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 56]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_CreateDirect3D11DeviceFromDXGIDevice() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 60]
        test eax, eax
        jnz forward
        pushad
        push 15
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 60]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_CreateDirect3D11SurfaceFromDXGISurface() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 64]
        test eax, eax
        jnz forward
        pushad
        push 16
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 64]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3D11CoreCreateDevice() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 68]
        test eax, eax
        jnz forward
        pushad
        push 17
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 68]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3D11CoreCreateLayeredDevice() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 72]
        test eax, eax
        jnz forward
        pushad
        push 18
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 72]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3D11CoreGetLayeredDeviceSize() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 76]
        test eax, eax
        jnz forward
        pushad
        push 19
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 76]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3D11CoreRegisterLayers() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 80]
        test eax, eax
        jnz forward
        pushad
        push 20
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 80]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3D11CreateDeviceAndSwapChain() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 84]
        test eax, eax
        jnz forward
        pushad
        push 21
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 84]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3D11On12CreateDevice() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 88]
        test eax, eax
        jnz forward
        pushad
        push 22
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 88]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTCreateAllocation() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 92]
        test eax, eax
        jnz forward
        pushad
        push 23
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 92]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTCreateContext() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 96]
        test eax, eax
        jnz forward
        pushad
        push 24
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 96]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTCreateDevice() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 100]
        test eax, eax
        jnz forward
        pushad
        push 25
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 100]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTCreateSynchronizationObject() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 104]
        test eax, eax
        jnz forward
        pushad
        push 26
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 104]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTEscape() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 108]
        test eax, eax
        jnz forward
        pushad
        push 27
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 108]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTGetContextSchedulingPriority() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 112]
        test eax, eax
        jnz forward
        pushad
        push 28
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 112]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTGetDeviceState() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 116]
        test eax, eax
        jnz forward
        pushad
        push 29
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 116]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTGetDisplayModeList() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 120]
        test eax, eax
        jnz forward
        pushad
        push 30
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 120]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTGetMultisampleMethodList() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 124]
        test eax, eax
        jnz forward
        pushad
        push 31
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 124]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTGetRuntimeData() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 128]
        test eax, eax
        jnz forward
        pushad
        push 32
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 128]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTGetSharedPrimaryHandle() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 132]
        test eax, eax
        jnz forward
        pushad
        push 33
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 132]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTLock() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 136]
        test eax, eax
        jnz forward
        pushad
        push 34
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 136]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTOpenAdapterFromHdc() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 140]
        test eax, eax
        jnz forward
        pushad
        push 35
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 140]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTOpenResource() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 144]
        test eax, eax
        jnz forward
        pushad
        push 36
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 144]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTQueryAllocationResidency() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 148]
        test eax, eax
        jnz forward
        pushad
        push 37
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 148]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTQueryResourceInfo() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 152]
        test eax, eax
        jnz forward
        pushad
        push 38
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 152]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTRender() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 156]
        test eax, eax
        jnz forward
        pushad
        push 39
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 156]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTSetAllocationPriority() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 160]
        test eax, eax
        jnz forward
        pushad
        push 40
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 160]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTSetContextSchedulingPriority() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 164]
        test eax, eax
        jnz forward
        pushad
        push 41
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 164]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTSetDisplayMode() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 168]
        test eax, eax
        jnz forward
        pushad
        push 42
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 168]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTSetGammaRamp() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 172]
        test eax, eax
        jnz forward
        pushad
        push 43
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 172]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTSetVidPnSourceOwner() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 176]
        test eax, eax
        jnz forward
        pushad
        push 44
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 176]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DKMTWaitForVerticalBlankEvent() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 180]
        test eax, eax
        jnz forward
        pushad
        push 45
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 180]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DPerformance_BeginEvent() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 184]
        test eax, eax
        jnz forward
        pushad
        push 46
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 184]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DPerformance_EndEvent() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 188]
        test eax, eax
        jnz forward
        pushad
        push 47
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 188]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DPerformance_GetStatus() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 192]
        test eax, eax
        jnz forward
        pushad
        push 48
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 192]
    forward:
        jmp eax
    }
}
extern "C" __declspec(naked) void autosportProxy_D3DPerformance_SetMarker() {
    __asm {
        mov eax, dword ptr [autosportProxyTable + 196]
        test eax, eax
        jnz forward
        pushad
        push 49
        call autosportProxyResolve
        add esp, 4
        popad
        mov eax, dword ptr [autosportProxyTable + 196]
    forward:
        jmp eax
    }
}
