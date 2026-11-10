// Offline x86 test. From an x86 MSVC developer prompt at the project root:
// cl /nologo /EHsc /Od /std:c++17 capture\verify_native_hud.cpp user32.lib /Fe:%TEMP%\verify_native_hud.exe /Fo:%TEMP%\verify_native_hud.obj
// %TEMP%\verify_native_hud.exe
#include "../bridge/native_ovr.cpp"
#include <array>
#include <cassert>
#include <cstdio>

namespace {
bool follow, flatScreen;
unsigned cameraUpdates;
EyeDesc testEyes[2];
Matrix stock{};
constexpr float lensX = .13f, lensY = -.05f;
using Camera = std::array<BYTE, 0x150>;

// Same sparse coefficients and C ABI as the stock SDK helper at RVA 0xA9E6D0.
Matrix* __cdecl originalOrtho(Matrix* m, Matrix p, float sx, float sy, float distance, float offset) {
    *m = {};
    m->m[0][0] = sx*p.m[0][0];
    m->m[0][3] = offset*p.m[0][0]/distance - p.m[0][2];
    m->m[1][1] = -sy*p.m[1][1];
    m->m[1][3] = -p.m[1][2];
    m->m[3][3] = 1;
    stock = *m;
    return m;
}
EyeDesc* __fastcall originalEye(void*, void*, int eye) { return testEyes+eye; }
void __fastcall originalCamera(void*, void*) { ++cameraUpdates; }
Matrix transposed(const Matrix& m) {
    Matrix out{};
    for (int r=0; r<4; ++r) for (int c=0; c<4; ++c) out.m[r][c]=m.m[c][r];
    return out;
}
bool equal(const Matrix& a, const Matrix& b) { return !std::memcmp(&a,&b,sizeof(a)); }
bool closeTo(float a, float b) { return std::abs(a-b) < .00005f; }

void prepareEye(int eye, Rotation q, Vec center) {
    testEyes[eye].viewAdjust = {eye ? -.032f : .032f,0,0};
    poses[eye] = {q, center};
    assert(selectProjectionEye(nullptr,nullptr,eye)==testEyes+eye);
    Matrix projection{}, out{};
    projection.m[0][0] = projection.m[1][1] = 1;
    projection.m[0][2] = lensX;
    projection.m[1][2] = lensY;
    projection.m[2][2] = -1.00001f;
    projection.m[2][3] = -.100001f;
    projection.m[3][2] = -1;
    auto pre = projection;
    hudOrtho(&out, projection,.01f,-.01f,200.f,testEyes[eye].viewAdjust.x);
    assert(equal(projection,pre));
    assert(equal(out,stock)); // Every generic caller sees original SDK bits, even when HUD is fixed.
    stock=projection; // The OSD root uses the eye's actual SDK perspective matrix.
}
Matrix render(const void* caller, bool perspective=true, bool matrixMismatch=false, bool stereo=true) {
    Camera camera{};
    auto m=transposed(stock);
    if (matrixMismatch) m.m[0][0] += .001f;
    std::memcpy(camera.data()+0x110,&m,sizeof(m));
    camera[0xe0]=perspective ? 1 : 0;
    // Enter the actual selector shim exactly as native RVA 0x720C4A does.
    const void* nativeFrame[] = {nullptr,caller};
    auto* data=camera.data();
    static std::array<BYTE,0x52f4> renderer;
    renderer[0x52e8]=1;
    renderer[0x52f1]=stereo;
    auto* renderDevice=renderer.data();
    unsigned before=cameraUpdates;
    __asm {
        push ebx
        push edi
        lea ebx,nativeFrame
        mov edi,renderDevice
        mov ecx,data
        call uiCameraCall
        pop edi
        pop ebx
    }
    assert(cameraUpdates==before+1);
    std::memcpy(&m,camera.data()+0x110,sizeof(m));
    return transposed(m);
}
float clip(const Matrix& m, int row, float u=0.f) {
    // Test points lie in the authored camera's forward plane, 100 game units away.
    return m.m[row][0]*u-m.m[row][2]*100+m.m[row][3];
}
float x(const Matrix& m, float u=0.f) { return clip(m,0,u)/clip(m,3,u); }
float y(const Matrix& m) { return clip(m,1)/clip(m,3); }
}

void AutosportLog(const char*, ...) {}
bool AutosportHudFollowsView(){return follow;}
bool AutosportFlatScreen(){return flatScreen;}
bool AutosportUiComparison(){return false;}
bool AutosportRegularPlay(){return false;}
const char* AutosportTransitionSample(){return nullptr;}
void AutosportBindLoadingOverlay(const void*) {}
void* __fastcall AutosportSlateVisibility(void*,void*,unsigned int){return nullptr;}
void __cdecl AutosportDrawFrontendOverlay(void*,void*,void*,unsigned int,unsigned int){}
void AutosportBeginLoadingOverlayFrame(uint64_t,bool){}
const char* AutosportLoadingOverlaySample(uint64_t){return nullptr;}
void AutosportFinishLoadingOverlayFrame(uint64_t,bool){}
void __fastcall AutosportLoadingEntered(void*,void*,const void*) {}
void __fastcall AutosportLoadingExited(void*,void*,const void*) {}
const wchar_t* AutosportRunDirectory(){return nullptr;}
bool AutosportUseHeadset(){return true;}
bool AutosportCaptureRequested(){return false;}
void AutosportCaptureCompleted(){}
bool AutosportBeginViews(ID3D11Device*,ID3D11DeviceContext*,bool,uint64_t,BridgeView (&)[2]) noexcept {return false;}
BridgeEndResult AutosportEndViews(ID3D11Texture2D*,unsigned,const RECT (&)[2],uint64_t,const wchar_t*,BridgePresentation) noexcept {return {};}
void AutosportCancelViews(uint64_t) noexcept {}
void AutosportCloseBridge() noexcept {}
void AutosportPrepareForGameExit() noexcept {}
const char* AutosportBridgeStatus() noexcept {return "";}
double AutosportDisplayPeriodSeconds() noexcept {return 0;}

int main() {
    int hudOwner, cinematicOwner;
    stockEyeDesc=reinterpret_cast<NativeEyeDesc>(originalEye);
    stockCameraUpdate=reinterpret_cast<NativeCameraUpdate>(originalCamera);
    stockOrtho=originalOrtho;
    osdLayoutReturn=&hudOwner;
    descriptors=testEyes;
    for (auto& eye:testEyes) eye.pixelScale[0]=eye.pixelScale[1]=120;
    frameOpen=true;
    ++frame;
    for(int eye=0;eye<2;++eye) {
        const float shift=eye ? -.032f : .032f;
        const Rotation straight{0,0,0,1};
        auto hud=[&](Rotation q,Vec pos) { prepareEye(eye,q,pos); return render(&hudOwner); };
        auto center=hud(straight,{});
        assert(closeTo(x(center)+lensX,shift) && closeTo(y(center)+lensY,0));
        auto lean=hud(straight,{.10f,.06f,0});
        assert(closeTo(x(lean)+lensX,shift-.10f) && closeTo(y(lean)+lensY,-.06f));
        const auto nearer=hud(straight,{0,0,-.2f});
        assert(closeTo(clip(nearer,3)/100,.8f));
        assert(closeTo(clip(nearer,2)/clip(nearer,3),1.00001f-.100001f/80.f));
        float s=std::sin(.25f), c=std::cos(.25f);
        assert(x(hud({0,-s,0,c},{}))+lensX < -.49f);
        assert(x(hud({0,s,0,c},{}))+lensX > .49f);
        assert(y(hud({s,0,0,c},{}))+lensY < -.53f);
        assert(y(hud({-s,0,0,c},{}))+lensY > .53f);
        assert(std::abs(hud({0,0,s,c},{}).m[1][0]) > .003f);

        // A matte vertex outside the native viewport can enter the seated frustum when
        // looking right. The generic FE/cinematic draw must still clip this vertex.
        s=std::sin(.5f);c=std::cos(.5f);
        auto tilted=hud({0,-s,0,c},{});
        assert(std::abs(x(stock,300))>1 && std::abs(x(tilted,300))<1);
        assert(equal(render(&cinematicOwner),stock));
        assert(equal(render(&hudOwner,false),stock));
        assert(equal(render(&hudOwner,true,false,false),stock));
        auto changed=stock;changed.m[0][0]+=.001f;
        assert(equal(render(&hudOwner,true,true),changed));
        ++frame;
        assert(equal(render(&hudOwner),stock));
        follow=true;
        auto follows=hud({s,0,0,c},{.1f,.1f,0});
        assert(equal(follows,stock));
        follow=false;
        flatScreen=true;
        auto loading=hud({s,0,0,c},{.1f,.1f,0});
        assert(equal(loading,stock));
        flatScreen=false;
    }
    std::puts("PASS: both eyes, 1 m seat, yaw/pitch/roll/lean, preserved stock ortho and perspective, named HUD caller, cinematic matte bounds, stale/changed camera, Follow View and loading");
}
