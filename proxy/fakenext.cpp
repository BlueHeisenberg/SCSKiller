// Test stand-in for a mod's d3d12.dll that the proxy chains to (scskiller.ini next=): forwards D3D12CreateDevice to the
// system dll and counts the calls. Only D3D12CreateDevice, like many wrappers: the proxy takes the rest from the system dll.
// After FakeNext_SwapPs it acts like a shader-replacing wrapper (ReShade with a RenoDX addon): the game gets a wrapper
// device whose CreateGraphicsPipelineState passes the desc on with that pixel shader instead of the game's.
// After FakeNext_Layer it is a layer like ReShade as dxgi.dll instead: see there.
// Never shipped (build/publish.ps1 copies named files only); `selftest chain` loads it.
#include <windows.h>
#define D3D12CreateDevice D3D12CreateDevice_h  // d3d12.h declares it without dllexport
#include <d3d12.h>
#include <dxgi1_6.h>
#undef D3D12CreateDevice
#include <algorithm>
#include <atomic>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

static LONG g_calls;
static std::string g_ps;
static bool g_unwrap, g_rewrite_rs;
static const GUID kUnwrapped = {0x7f2c9a11, 0x3b4e, 0x4d6a, {0x81, 0x2f, 0x5e, 0x9c, 0xd3, 0x7a, 0x1b, 0x42}};  // ReShade 6.8+

extern "C" __declspec(dllexport) LONG WINAPI FakeNext_Calls() { return g_calls; }
extern "C" __declspec(dllexport) void WINAPI FakeNext_SwapPs(const void* p, SIZE_T n) { g_ps.assign((const char*)p, n); }
// Copied as a game's D3D12\D3D12Core.dll: an Agility runtime newer than any system's that makes no device (WarmerTests)
extern "C" __declspec(dllexport) const UINT D3D12SDKVersion = 100000;

// The wrapper: every slot forwards to the same slot of the wrapped device (a thunk swaps `this`), but QueryInterface and
// CreateGraphicsPipelineState. 128 slots cover ID3D12Device14.
struct Wrap {
    void** vt;
    ID3D12Device* real;
};

static HRESULT STDMETHODCALLTYPE wrap_qi(Wrap* w, REFIID riid, void** pp) {
    if (g_unwrap && riid == kUnwrapped) return w->real->AddRef(), *pp = w->real, S_OK;
    HRESULT hr = w->real->QueryInterface(riid, pp);
    if (SUCCEEDED(hr) && *pp == w->real) *pp = w;  // every device interface stays the wrapper, as ReShade's do
    return hr;
}

static HRESULT STDMETHODCALLTYPE wrap_gfx(Wrap* w, const D3D12_GRAPHICS_PIPELINE_STATE_DESC* d, REFIID riid, void** pp) {
    D3D12_GRAPHICS_PIPELINE_STATE_DESC c = *d;
    if (c.PS.pShaderBytecode) c.PS = {g_ps.data(), g_ps.size()};
    return w->real->CreateGraphicsPipelineState(&c, riid, pp);
}

static HMODULE system_d3d12() {
    wchar_t sys[MAX_PATH];
    GetSystemDirectoryW(sys, MAX_PATH);
    return LoadLibraryW((std::wstring(sys) + L"\\d3d12.dll").c_str());
}

// RenoDX's injection: a root constant (b13, space 50) appended to every root signature, serialized anew.
static HRESULT STDMETHODCALLTYPE wrap_rs(Wrap* w, UINT mask, const void* blob, SIZE_T n, REFIID riid, void** pp) {
    if (!g_rewrite_rs) return w->real->CreateRootSignature(mask, blob, n, riid, pp);
    static HMODULE sys = system_d3d12();
    auto deser = (PFN_D3D12_CREATE_ROOT_SIGNATURE_DESERIALIZER)GetProcAddress(sys, "D3D12CreateRootSignatureDeserializer");
    auto ser = (PFN_D3D12_SERIALIZE_ROOT_SIGNATURE)GetProcAddress(sys, "D3D12SerializeRootSignature");
    ID3D12RootSignatureDeserializer* ds = nullptr;
    if (!deser || !ser || FAILED(deser(blob, n, IID_PPV_ARGS(&ds)))) return E_FAIL;
    auto d = *ds->GetRootSignatureDesc();
    std::vector<D3D12_ROOT_PARAMETER> p(d.pParameters, d.pParameters + d.NumParameters);
    D3D12_ROOT_PARAMETER c = {D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS};
    c.Constants = {13, 50, 4};
    p.push_back(c);
    d.NumParameters = (UINT)p.size(), d.pParameters = p.data();
    ID3DBlob *b = nullptr, *err = nullptr;
    HRESULT hr = ser(&d, D3D_ROOT_SIGNATURE_VERSION_1, &b, &err);
    if (SUCCEEDED(hr)) hr = w->real->CreateRootSignature(mask, b->GetBufferPointer(), b->GetBufferSize(), riid, pp);
    if (b) b->Release();
    if (err) err->Release();
    ds->Release();
    return hr;
}

// A vtable whose every slot forwards to the same slot of the object at offset 8.
static void** forward_vtable() {
    const int n = 128;
    auto code = (uint8_t*)VirtualAlloc(nullptr, n * 16, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    auto t = new void*[n];
    for (int i = 0; i < n; ++i) {
        uint8_t* c = code + i * 16;
        const uint8_t op[] = {0x48, 0x8B, 0x49, 0x08, 0x48, 0x8B, 0x01, 0xFF, 0xA0};  // mov rcx,[rcx+8]; mov rax,[rcx]; jmp [rax+disp32]
        int32_t disp = i * 8;
        memcpy(c, op, sizeof op), memcpy(c + sizeof op, &disp, 4);
        t[i] = c;
    }
    FlushInstructionCache(GetCurrentProcess(), code, n * 16);
    return t;
}

static void** wrapper_vtable() {
    static void** vt = [] {
        void** t = forward_vtable();
        t[0] = (void*)wrap_qi, t[10] = (void*)wrap_gfx, t[16] = (void*)wrap_rs;
        return t;
    }();
    return vt;
}

// Copied as dxgi.dll beside selftest.exe (`selftest framesmod`): a mod's dxgi.dll like ReShade's, which forwards to
// System32's. CreateDXGIFactory1's factory is its own object around System32's (every call forwarded, interfaces from
// QueryInterface included, so it stays the wrapper). The wrapper itself is leaked: a test process.
static HMODULE system_dxgi() {
    wchar_t sys[MAX_PATH];
    GetSystemDirectoryW(sys, MAX_PATH);
    return LoadLibraryW((std::wstring(sys) + L"\\dxgi.dll").c_str());
}
struct FactoryWrap {
    void** vt;
    IUnknown* real;
};
static HRESULT STDMETHODCALLTYPE factory_qi(FactoryWrap* w, REFIID riid, void** pp) {
    HRESULT hr = w->real->QueryInterface(riid, pp);
    if (SUCCEEDED(hr) && *pp == w->real) *pp = w;
    return hr;
}
static HMODULE g_self;
static decltype(&LoadLibraryW) g_sys_loadlib;
static bool g_opti_wrap, g_opti_fg;
static HRESULT opti_create1(REFIID riid, void** pp);
extern "C" HRESULT WINAPI fake_dxgi_create1(REFIID riid, void** pp) {
    if (g_sys_loadlib) return opti_create1(riid, pp);
    static auto real = (HRESULT(WINAPI*)(REFIID, void**))GetProcAddress(system_dxgi(), "CreateDXGIFactory1");
    static void** vt = [] {
        void** t = forward_vtable();
        t[0] = (void*)factory_qi;
        return t;
    }();
    IUnknown* f = nullptr;
    HRESULT hr = real ? real(riid, (void**)&f) : E_FAIL;
    if (SUCCEEDED(hr)) *pp = new FactoryWrap{vt, f};
    return hr;
}
extern "C" HRESULT WINAPI fake_dxgi_create2(UINT flags, REFIID riid, void** pp) {
    if (g_sys_loadlib && !flags) return opti_create1(riid, pp);
    static auto real = (HRESULT(WINAPI*)(UINT, REFIID, void**))GetProcAddress(system_dxgi(), "CreateDXGIFactory2");
    return real ? real(flags, riid, pp) : E_FAIL;
}
#pragma comment(linker, "/EXPORT:CreateDXGIFactory1=fake_dxgi_create1")
#pragma comment(linker, "/EXPORT:CreateDXGIFactory2=fake_dxgi_create2")

extern "C" __declspec(dllexport) HRESULT WINAPI D3D12CreateDevice(IUnknown* adapter, D3D_FEATURE_LEVEL fl, REFIID riid, void** pp) {
    static auto real = (decltype(&D3D12CreateDevice_h))GetProcAddress(system_d3d12(), "D3D12CreateDevice");
    InterlockedIncrement(&g_calls);
    if (!real) return E_FAIL;
    if (g_ps.empty() || !pp) return real(adapter, fl, riid, pp);
    ID3D12Device* dev = nullptr;
    HRESULT hr = real(adapter, fl, IID_PPV_ARGS(&dev));
    if (FAILED(hr)) return hr;
    auto w = new Wrap{wrapper_vtable(), dev};  // leaked: a test process
    hr = wrap_qi(w, riid, pp);
    dev->Release();
    return hr;
}

// FakeNext_Layer: a layer like ReShade as dxgi.dll with a RenoDX addon. Loaded before the proxy, it points the system
// d3d12.dll's D3D12CreateDevice export here (its export table entry: ReShade patches the code instead, and the proxy reaches
// either the same way), and the device comes wrapped with root signatures rewritten (wrap_rs). unwrap: the wrapper answers
// IID_UnwrappedObject with the real device, as ReShade 6.8 does; without it the proxy finds the real device another way.
static decltype(&D3D12CreateDevice_h) g_sys_create;

static HRESULT WINAPI layer_create(IUnknown* adapter, D3D_FEATURE_LEVEL fl, REFIID riid, void** pp) {
    if (!pp) return g_sys_create(adapter, fl, riid, pp);
    ID3D12Device* dev = nullptr;
    HRESULT hr = g_sys_create(adapter, fl, IID_PPV_ARGS(&dev));
    if (FAILED(hr)) return hr;
    auto w = new Wrap{wrapper_vtable(), dev};  // leaked: a test process
    hr = wrap_qi(w, riid, pp);
    dev->Release();
    return hr;
}

// Points module base's export name at to (its export table entry, through a jump stub within the 32-bit reach of the
// image base): later imports and GetProcAddress get to. The export's function before, or null.
static void* eat_point(uint8_t* base, const char* name, void* to) {
    if (!base) return nullptr;
    auto nt = (IMAGE_NT_HEADERS*)(base + ((IMAGE_DOS_HEADER*)base)->e_lfanew);
    auto ex = (IMAGE_EXPORT_DIRECTORY*)(base + nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT].VirtualAddress);
    auto names = (DWORD*)(base + ex->AddressOfNames);
    auto ords = (WORD*)(base + ex->AddressOfNameOrdinals);
    auto fns = (DWORD*)(base + ex->AddressOfFunctions);
    for (DWORD i = 0; i < ex->NumberOfNames; ++i) {
        if (strcmp((const char*)base + names[i], name)) continue;
        DWORD* slot = &fns[ords[i]];
        void* was = base + *slot;
        uint8_t* stub = nullptr;
        for (uintptr_t a = ((uintptr_t)base + nt->OptionalHeader.SizeOfImage + 0xFFFF) & ~(uintptr_t)0xFFFF; !stub && a < (uintptr_t)base + 0x7FFF0000; a += 0x10000)
            stub = (uint8_t*)VirtualAlloc((void*)a, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        if (!stub) return nullptr;
        const uint8_t jmp[] = {0xFF, 0x25, 0, 0, 0, 0};  // jmp [rip]
        memcpy(stub, jmp, sizeof jmp), memcpy(stub + sizeof jmp, &to, 8);
        DWORD old;
        VirtualProtect(slot, 4, PAGE_READWRITE, &old);
        *slot = (DWORD)(stub - base);
        VirtualProtect(slot, 4, old, &old);
        return was;
    }
    return nullptr;
}

extern "C" __declspec(dllexport) BOOL WINAPI FakeNext_Layer(BOOL unwrap) {
    g_unwrap = unwrap, g_rewrite_rs = true;
    g_sys_create = (decltype(g_sys_create))eat_point((uint8_t*)system_d3d12(), "D3D12CreateDevice", (void*)layer_create);
    return g_sys_create != nullptr;
}

// SCSKILLER_SELFTEST_OPTI, copied as dxgi.dll (`selftest framesopti`): OptiScaler 0.9.4 as dxgi.dll. Its LoadLibraryW
// hands back this dll for any name ending in dxgi.dll, System32's full path included, and CreateDXGIFactory1 gives
// System32's own factory. "wrap": System32's CreateSwapChainForHwnd is this dll's beneath any later hook (Opti detours its
// code), and the swap chain it returns is a wrapper that leads nowhere else: QueryInterface answers only swap chain
// interfaces, with itself. Its Present calls the real one's.
static HMODULE WINAPI opti_loadlib(LPCWSTR name) {
    size_t n = name ? wcslen(name) : 0;
    if (n >= 8 && !_wcsicmp(name + n - 8, L"dxgi.dll")) return g_self;
    return g_sys_loadlib(name);
}
struct ScHide {
    void** vt;
    IDXGISwapChain1* real;
};
static HRESULT STDMETHODCALLTYPE hide_qi(ScHide* w, REFIID riid, void** pp) {
    const IID* mine[] = {&__uuidof(IUnknown), &__uuidof(IDXGIObject), &__uuidof(IDXGIDeviceSubObject), &__uuidof(IDXGISwapChain), &__uuidof(IDXGISwapChain1)};
    for (const IID* i : mine)
        if (riid == *i) return w->real->AddRef(), *pp = w, S_OK;
    return *pp = nullptr, E_NOINTERFACE;
}
using PFN_ScHwndF = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory2*, IUnknown*, HWND, const DXGI_SWAP_CHAIN_DESC1*, const DXGI_SWAP_CHAIN_FULLSCREEN_DESC*,
                                                IDXGIOutput*, IDXGISwapChain1**);
static PFN_ScHwndF g_opti_create;
// "fg": Opti's own frame generation (FSRFG_Dx12::CreateSwapchain1 through FG_Hooks): the game's create makes frame
// generation's swap chain instead, in this dll's copy as amd_fidelityfx_framegeneration_dx12.dll beside it (FakeNext_OptiFg,
// its real swap chain made nested from there, through the factory's vtable), and the game gets that chain unwrapped. Its
// Present and Present1 then run this dll's code first, as Opti's detour of their code does: the vtable keeps the module's
// functions (FakeNext_OptiFgPresents: how many ran).
static thread_local bool t_opti_fg;
static LONG g_opti_fg_presents;
using PFN_FgPresent = HRESULT(STDMETHODCALLTYPE*)(IUnknown*, UINT, UINT);
using PFN_FgPresent1 = HRESULT(STDMETHODCALLTYPE*)(IUnknown*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);
static PFN_FgPresent g_ofg_body;
static PFN_FgPresent1 g_ofg_body1;
static HRESULT STDMETHODCALLTYPE opti_fg_present(IUnknown* c, UINT sync, UINT flags) { return InterlockedIncrement(&g_opti_fg_presents), g_ofg_body(c, sync, flags); }
static HRESULT STDMETHODCALLTYPE opti_fg_present1(IUnknown* c, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* p) {
    return InterlockedIncrement(&g_opti_fg_presents), g_ofg_body1(c, sync, flags, p);
}
extern "C" __declspec(dllexport) LONG WINAPI FakeNext_OptiFgPresents() { return g_opti_fg_presents; }
static HRESULT STDMETHODCALLTYPE opti_create(IDXGIFactory2* f, IUnknown* q, HWND w, const DXGI_SWAP_CHAIN_DESC1* d, const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fs,
                                             IDXGIOutput* o, IDXGISwapChain1** pp) {
    if (g_opti_fg && !t_opti_fg) {
        wchar_t dir[MAX_PATH];
        DWORD n = GetModuleFileNameW(g_self, dir, MAX_PATH);
        while (n && dir[n - 1] != L'\\') --n;
        dir[n] = 0;
        HMODULE fgm = LoadLibraryW((std::wstring(dir) + L"amd_fidelityfx_framegeneration_dx12.dll").c_str());
        using PFN_OptiFg = HRESULT(WINAPI*)(IDXGIFactory2*, IUnknown*, HWND, const DXGI_SWAP_CHAIN_DESC1*, IDXGISwapChain1**);
        using PFN_Detour = void(WINAPI*)(void*, void*, void**, void**);
        auto make = fgm ? (PFN_OptiFg)GetProcAddress(fgm, "FakeNext_OptiFg") : nullptr;
        auto detour = fgm ? (PFN_Detour)GetProcAddress(fgm, "FakeNext_OptiFgDetour") : nullptr;
        if (!make || !detour) return E_FAIL;
        t_opti_fg = true;
        HRESULT hr = make(f, q, w, d, pp);
        t_opti_fg = false;
        if (SUCCEEDED(hr)) detour((void*)opti_fg_present, (void*)opti_fg_present1, (void**)&g_ofg_body, (void**)&g_ofg_body1);
        return hr;
    }
    static void** vt = [] {
        void** t = forward_vtable();
        t[0] = (void*)hide_qi;
        return t;
    }();
    IDXGISwapChain1* real = nullptr;
    HRESULT hr = g_opti_create(f, q, w, d, fs, o, &real);
    if (SUCCEEDED(hr)) *pp = (IDXGISwapChain1*)new ScHide{vt, real};  // leaked: a test process
    return hr;
}
// "wrap" also takes slot 24 (CreateSwapChainForComposition) under any later hook: each create's Present slot as it returns,
// as a hooker that keeps the vtable's Present for its own detour would (FakeNext_Comp: the last one and the creates since
// the last reset).
using PFN_ScCompF = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory2*, IUnknown*, const DXGI_SWAP_CHAIN_DESC1*, IDXGIOutput*, IDXGISwapChain1**);
static PFN_ScCompF g_opti_comp;
static void* g_opti_captured;
static LONG g_opti_comps;
static HRESULT STDMETHODCALLTYPE opti_comp(IDXGIFactory2* f, IUnknown* q, const DXGI_SWAP_CHAIN_DESC1* d, IDXGIOutput* o, IDXGISwapChain1** pp) {
    HRESULT hr = g_opti_comp(f, q, d, o, pp);
    if (SUCCEEDED(hr)) g_opti_captured = (*(void***)*pp)[8], ++g_opti_comps;
    return hr;
}
extern "C" __declspec(dllexport) LONG WINAPI FakeNext_Comp(void** captured, BOOL reset) {
    if (captured) *captured = g_opti_captured;
    LONG n = g_opti_comps;
    if (reset) g_opti_captured = nullptr, g_opti_comps = 0;
    return n;
}
static HRESULT opti_create1(REFIID riid, void** pp) {
    static auto real = (HRESULT(WINAPI*)(REFIID, void**))GetProcAddress(system_dxgi(), "CreateDXGIFactory1");
    HRESULT hr = real ? real(riid, pp) : E_FAIL;
    IDXGIFactory2* f2 = nullptr;
    if (SUCCEEDED(hr) && (g_opti_wrap || g_opti_fg) && !g_opti_create && SUCCEEDED(((IUnknown*)*pp)->QueryInterface(IID_PPV_ARGS(&f2)))) {
        void** vt = *(void***)f2;
        DWORD old;
        VirtualProtect(&vt[15], sizeof(void*), PAGE_READWRITE, &old);
        g_opti_create = (PFN_ScHwndF)vt[15], vt[15] = (void*)opti_create;
        VirtualProtect(&vt[15], sizeof(void*), old, &old);
        VirtualProtect(&vt[24], sizeof(void*), PAGE_READWRITE, &old);
        g_opti_comp = (PFN_ScCompF)vt[24], vt[24] = (void*)opti_comp;
        VirtualProtect(&vt[24], sizeof(void*), old, &old);
        f2->Release();
    }
    return hr;
}

// FidelityFX's ffxCreateContext for `selftest framesfg ffx` (the test copies this dll as amd_fidelityfx_loader_dx12.dll):
// a FOR_HWND frame generation swap chain desc (type 0x30006) gets a stand-in swap chain, its vtable read-only data here,
// whose Present presents the real one on the calling thread (FSR 3 generating nothing, as in menus), then twice from
// another thread (a rendered and a generated frame).
// As FidelityFX's: the game renders into replacement buffers on its queue; each present waits for that queue, then copies
// the buffer into the real swap chain's on frame generation's present queue.
struct FgChain {
    const void* const* vt;
    IDXGISwapChain1* real;
    ID3D12CommandQueue *game, *present;
    ID3D12Fence* fence;
    UINT64 value;
    ID3D12Resource* rep[2];
    UINT idx;
    ID3D12CommandAllocator* alloc;
    ID3D12GraphicsCommandList* list;
    HANDLE done;
    bool broken;  // the GPU wasn't seen past the last copy: its allocator is never reset again
};
static HRESULT STDMETHODCALLTYPE fg_qi(FgChain* c, REFIID riid, void** pp) {  // as FidelityFX's: an IDXGISwapChain4 itself
    const IID* mine[] = {&__uuidof(IUnknown), &__uuidof(IDXGIObject), &__uuidof(IDXGIDeviceSubObject), &__uuidof(IDXGISwapChain),
                         &__uuidof(IDXGISwapChain1), &__uuidof(IDXGISwapChain2), &__uuidof(IDXGISwapChain3), &__uuidof(IDXGISwapChain4)};
    for (const IID* i : mine)
        if (riid == *i) return *pp = c, S_OK;
    return *pp = nullptr, E_NOINTERFACE;
}
static ULONG STDMETHODCALLTYPE fg_ref(FgChain*) { return 1; }
static HRESULT STDMETHODCALLTYPE fg_none(FgChain*) { return E_NOTIMPL; }
static HRESULT STDMETHODCALLTYPE fg_buffer(FgChain* c, UINT i, REFIID riid, void** pp) { return i < 2 ? c->rep[i]->QueryInterface(riid, pp) : E_INVALIDARG; }
static UINT STDMETHODCALLTYPE fg_index(FgChain* c) { return c->idx; }
static HRESULT STDMETHODCALLTYPE fg_present(FgChain* c, UINT sync, UINT flags) {
    ID3D12Resource* dst = nullptr;
    IDXGISwapChain3* r3 = nullptr;
    HRESULT hr = c->broken ? E_FAIL : c->real->QueryInterface(IID_PPV_ARGS(&r3));
    if (SUCCEEDED(hr)) hr = r3->GetBuffer(r3->GetCurrentBackBufferIndex(), IID_PPV_ARGS(&dst)), r3->Release();
    if (SUCCEEDED(hr)) hr = c->alloc->Reset();
    if (SUCCEEDED(hr)) hr = c->list->Reset(c->alloc, nullptr);
    if (FAILED(hr)) return hr;
    D3D12_RESOURCE_BARRIER b[2] = {};
    b[0].Transition = {c->rep[c->idx], D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_SOURCE};
    b[1].Transition = {dst, D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_DEST};
    c->list->ResourceBarrier(2, b);
    c->list->CopyResource(dst, c->rep[c->idx]);
    for (auto& x : b) std::swap(x.Transition.StateBefore, x.Transition.StateAfter);
    c->list->ResourceBarrier(2, b);
    dst->Release();
    if (FAILED(hr = c->list->Close()) || FAILED(hr = c->game->Signal(c->fence, ++c->value)) || FAILED(hr = c->present->Wait(c->fence, c->value)))
        return c->broken = true, hr;
    ID3D12CommandList* l = c->list;
    c->present->ExecuteCommandLists(1, &l);
    hr = c->real->Present(sync, flags);
    if (FAILED(c->present->Signal(c->fence, ++c->value)) || FAILED(c->fence->SetEventOnCompletion(c->value, c->done)) ||
        WaitForSingleObject(c->done, 5000) != WAIT_OBJECT_0)
        return c->broken = true, E_FAIL;
    c->idx ^= 1;
    std::thread([&] { for (int i = 0; i < 2 && SUCCEEDED(hr); ++i) hr = c->real->Present(sync, flags); }).join();
    return hr;
}
// this module's data, as FidelityFX's vtable is; every other method forwarded to the real swap chain (private data too, as
// FidelityFX's does)
static const void* g_fg_vt[40];
static const void* const* fg_vt() {
    static bool made = [] {
        void** t = forward_vtable();
        for (int i = 0; i < 40; ++i) g_fg_vt[i] = t[i];
        g_fg_vt[0] = (const void*)fg_qi, g_fg_vt[1] = g_fg_vt[2] = (const void*)fg_ref, g_fg_vt[8] = (const void*)fg_present;
        g_fg_vt[9] = (const void*)fg_buffer, g_fg_vt[36] = (const void*)fg_index;
        return true;
    }();
    return made ? g_fg_vt : nullptr;
}
struct FgForHwnd;
static HRESULT fg_create(IDXGIFactory2* f2, ID3D12CommandQueue* q, FgForHwnd* d, IDXGISwapChain1** pp);
struct FgForHwnd {
    uint64_t type;
    void* next;
    IUnknown** swapchain;
    HWND hwnd;
    DXGI_SWAP_CHAIN_DESC1* desc;
    DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fullscreen;
    IDXGIFactory* factory;
    ID3D12CommandQueue* queue;
};
// Another module's create for fake_ffx_create (SCSKILLER_SELFTEST_FG_SHIM: a copy of this dll as fgshim.dll beside it),
// as a layer's factory forwards it: the factory's caller isn't the module that asked for the swap chain.
extern "C" __declspec(dllexport) HRESULT WINAPI FakeNext_CreateSc(IDXGIFactory2* f, IUnknown* q, HWND w, const DXGI_SWAP_CHAIN_DESC1* d,
                                                                   const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fs, IDXGISwapChain1** pp) {
    HRESULT hr = f->CreateSwapChainForHwnd(q, w, d, fs, nullptr, pp);
    ++g_calls;  // not a tail call: this frame stays the create's caller
    return hr;
}
extern "C" uint32_t fake_ffx_create(void** ctx, FgForHwnd* d, const void*) {  // ffxCreateContext (fakenext.asm) jumps here
    if (!d || (uint32_t)d->type != 0x30006) return 1;
    ID3D12Device* dev = nullptr;
    IDXGIFactory2* f2 = nullptr;
    ID3D12CommandQueue* q = nullptr;
    D3D12_COMMAND_QUEUE_DESC qd = {};
    auto c = new FgChain{fg_vt(), nullptr, d->queue};
    bool ok = SUCCEEDED(d->queue->GetDevice(IID_PPV_ARGS(&dev))) && SUCCEEDED(dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&q))) &&
              SUCCEEDED(dev->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&c->fence))) && ((c->present = q) != nullptr) &&
              (GetEnvironmentVariableW(L"SCSKILLER_SELFTEST_FG_UNNAMED", nullptr, 0) || SUCCEEDED(q->SetName(L"AMD FSR PresentQueue"))) &&
              SUCCEEDED(d->factory->QueryInterface(IID_PPV_ARGS(&f2))) && SUCCEEDED(fg_create(f2, q, d, &c->real));
    ID3D12Resource* b = nullptr;
    ok = ok && SUCCEEDED(c->real->GetBuffer(0, IID_PPV_ARGS(&b)));
    D3D12_HEAP_PROPERTIES hp = {D3D12_HEAP_TYPE_DEFAULT};
    D3D12_RESOURCE_DESC rd = ok ? b->GetDesc() : D3D12_RESOURCE_DESC{};
    if (b) b->Release();
    for (auto& r : c->rep) ok = ok && SUCCEEDED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_PRESENT, nullptr, IID_PPV_ARGS(&r)));
    ok = ok && SUCCEEDED(dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&c->alloc))) &&
         SUCCEEDED(dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, c->alloc, nullptr, IID_PPV_ARGS(&c->list))) && SUCCEEDED(c->list->Close()) &&
         (c->done = CreateEventW(nullptr, FALSE, FALSE, nullptr)) != nullptr;
    if (!ok) return delete c, 1;
    *d->swapchain = (IUnknown*)c;
    *ctx = c;
    return 0;
}

static HRESULT fg_create(IDXGIFactory2* f2, ID3D12CommandQueue* q, FgForHwnd* d, IDXGISwapChain1** pp) {
    if (!GetEnvironmentVariableW(L"SCSKILLER_SELFTEST_FG_SHIM", nullptr, 0)) return f2->CreateSwapChainForHwnd(q, d->hwnd, d->desc, d->fullscreen, nullptr, pp);
    wchar_t dir[MAX_PATH];
    HMODULE self = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)&fg_create, &self);
    DWORD n = GetModuleFileNameW(self, dir, MAX_PATH);
    while (n && dir[n - 1] != L'\\') --n;
    dir[n] = 0;
    wchar_t name[64] = L"1";  // SCSKILLER_SELFTEST_FG_SHIM: 1 for fgshim.dll, else the copy's file name (sl.dlss_g.dll)
    GetEnvironmentVariableW(L"SCSKILLER_SELFTEST_FG_SHIM", name, 64);
    HMODULE shim = LoadLibraryW((std::wstring(dir) + (wcscmp(name, L"1") ? name : L"fgshim.dll")).c_str());
    auto create = shim ? (decltype(&FakeNext_CreateSc))GetProcAddress(shim, "FakeNext_CreateSc") : nullptr;
    return create ? create(f2, q, d->hwnd, d->desc, d->fullscreen, pp) : E_FAIL;
}

// FakeNext_OptiFg (this dll as amd_fidelityfx_framegeneration_dx12.dll, called by the "fg" dxgi.dll copy): frame
// generation's swap chain for the game's create, as FidelityFX makes it for Opti. Its real swap chain is made from here on
// a queue named "AMD FSR PresentQueue", through the factory's vtable (the caller and queue the proxy's rules know), and the
// stand-in's Present presents it on the calling thread, then once from another (a generated frame). Present and Present1
// go through a pointer FakeNext_OptiFgDetour redirects, the vtable's functions staying this module's.
struct OptiFgChain {
    const void* const* vt;
    IDXGISwapChain1* real;
};
static HRESULT STDMETHODCALLTYPE ofg_body(IUnknown* u, UINT sync, UINT flags) {
    auto c = (OptiFgChain*)u;
    HRESULT hr = c->real->Present(sync, flags);
    std::thread([&] { if (SUCCEEDED(hr)) hr = c->real->Present(sync, flags); }).join();
    return hr;
}
static HRESULT STDMETHODCALLTYPE ofg_body1(IUnknown* u, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* p) {
    auto c = (OptiFgChain*)u;
    HRESULT hr = c->real->Present1(sync, flags, p);
    std::thread([&] { if (SUCCEEDED(hr)) hr = c->real->Present1(sync, flags, p); }).join();
    return hr;
}
static std::atomic<HRESULT(STDMETHODCALLTYPE*)(IUnknown*, UINT, UINT)> g_ofg_entry{ofg_body};
static std::atomic<HRESULT(STDMETHODCALLTYPE*)(IUnknown*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*)> g_ofg_entry1{ofg_body1};
static HRESULT STDMETHODCALLTYPE ofg_present(IUnknown* c, UINT sync, UINT flags) { return g_ofg_entry.load()(c, sync, flags); }
static HRESULT STDMETHODCALLTYPE ofg_present1(IUnknown* c, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* p) { return g_ofg_entry1.load()(c, sync, flags, p); }
static HRESULT STDMETHODCALLTYPE ofg_qi(OptiFgChain* c, REFIID riid, void** pp) {  // FidelityFX's answers as an IDXGISwapChain4
    const IID* mine[] = {&__uuidof(IUnknown), &__uuidof(IDXGIObject), &__uuidof(IDXGIDeviceSubObject), &__uuidof(IDXGISwapChain), &__uuidof(IDXGISwapChain1)};
    for (const IID* i : mine)
        if (riid == *i) return *pp = c, S_OK;
    return *pp = nullptr, E_NOINTERFACE;
}
static const void* const kOptiFgVt[23] = {(const void*)ofg_qi, (const void*)fg_ref, (const void*)fg_ref, (const void*)fg_none, (const void*)fg_none,
    (const void*)fg_none, (const void*)fg_none, (const void*)fg_none, (const void*)ofg_present, (const void*)fg_none, (const void*)fg_none,
    (const void*)fg_none, (const void*)fg_none, (const void*)fg_none, (const void*)fg_none, (const void*)fg_none, (const void*)fg_none,
    (const void*)fg_none, (const void*)fg_none, (const void*)fg_none, (const void*)fg_none, (const void*)fg_none, (const void*)ofg_present1};
extern "C" __declspec(dllexport) HRESULT WINAPI FakeNext_OptiFg(IDXGIFactory2* f, IUnknown* gq, HWND w, const DXGI_SWAP_CHAIN_DESC1* d, IDXGISwapChain1** pp) {
    ID3D12CommandQueue* q = nullptr;
    ID3D12Device* dev = nullptr;
    D3D12_COMMAND_QUEUE_DESC qd = {};
    auto c = new OptiFgChain{kOptiFgVt, nullptr};  // leaked: a test process
    HRESULT hr = SUCCEEDED(gq->QueryInterface(IID_PPV_ARGS(&q))) && SUCCEEDED(q->GetDevice(IID_PPV_ARGS(&dev))) ? S_OK : E_INVALIDARG;
    if (q) q->Release(), q = nullptr;
    if (SUCCEEDED(hr)) hr = dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&q));
    if (SUCCEEDED(hr)) hr = q->SetName(L"AMD FSR PresentQueue");
    if (SUCCEEDED(hr)) hr = f->CreateSwapChainForHwnd(q, w, d, nullptr, nullptr, &c->real);
    ++g_calls;  // not a tail call: this frame stays the create's caller
    if (FAILED(hr)) return delete c, hr;
    wchar_t m[8] = L"";  // fgsys: System32's swap chain handed back, fgfail: the game's create fails after this one made it
    GetEnvironmentVariableW(L"SCSKILLER_SELFTEST_OPTI", m, 8);
    if (!wcscmp(m, L"fgfail")) return E_FAIL;
    *pp = !wcscmp(m, L"fgsys") ? ((ScHide*)c->real)->real : (IDXGISwapChain1*)c;  // the dxgi.dll copy hid it
    return S_OK;
}
extern "C" __declspec(dllexport) void WINAPI FakeNext_OptiFgDetour(void* hook, void* hook1, void** body, void** body1) {
    *body = (void*)ofg_body, *body1 = (void*)ofg_body1;
    g_ofg_entry = (HRESULT(STDMETHODCALLTYPE*)(IUnknown*, UINT, UINT))hook;
    g_ofg_entry1 = (HRESULT(STDMETHODCALLTYPE*)(IUnknown*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*))hook1;
}

// NVAPI's nvapi_QueryInterface for `selftest nvqi` (the test loads a copy as nvapi64.dll): 0x5EE000nn resolves to its own
// function, 0x5EE100nn to the same one by resolving 0x5EE000nn through the exported entry from inside this resolution (a
// layer that resolves through whatever hooked the entry), every other id to none.
static char g_nv_fns[16];
extern "C" void* nvapi_QueryInterface(uint32_t id);
#pragma comment(linker, "/EXPORT:nvapi_QueryInterface")
#pragma comment(linker, "/EXPORT:ffxCreateContext")
#pragma comment(linker, "/EXPORT:slUpgradeInterface")

// Streamline's proxy swap chain for `selftest framesfg slg` (this dll as sl.interposer.dll): slUpgradeInterface swaps a
// swap chain for a proxy that takes over the caller's reference, answers Streamline's base GUID with what it wraps, and
// presents through it. FakeNext_SlSet points it at another object (frame generation's swap chain), or none.
struct SlProxy {
    const void* const* vt;
    IUnknown* real;
    LONG ref;
};
static const GUID kSlBaseFake = {0xadec44e2, 0x61f0, 0x45c3, {0xad, 0x9f, 0x1b, 0x37, 0x37, 0x92, 0x84, 0xff}};
static HRESULT STDMETHODCALLTYPE sl_qi(SlProxy* p, REFIID iid, void** pp) {
    if (iid == kSlBaseFake && p->real) return p->real->AddRef(), *pp = p->real, S_OK;
    if (iid == __uuidof(IUnknown) || iid == __uuidof(IDXGISwapChain) || iid == __uuidof(IDXGISwapChain1)) return InterlockedIncrement(&p->ref), *pp = p, S_OK;
    return *pp = nullptr, E_NOINTERFACE;
}
static ULONG STDMETHODCALLTYPE sl_ref(SlProxy* p) { return InterlockedIncrement(&p->ref); }
static ULONG STDMETHODCALLTYPE sl_rel(SlProxy* p) {
    const LONG n = InterlockedDecrement(&p->ref);
    if (!n) {
        if (p->real) p->real->Release();
        delete p;
    }
    return n;
}
static HRESULT STDMETHODCALLTYPE sl_none(SlProxy*) { return E_NOTIMPL; }
static HRESULT STDMETHODCALLTYPE sl_present(SlProxy* p, UINT sync, UINT flags) {
    return p->real ? ((IDXGISwapChain*)p->real)->Present(sync, flags) : DXGI_ERROR_INVALID_CALL;
}
static HRESULT STDMETHODCALLTYPE sl_present1(SlProxy* p, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS*) { return sl_present(p, sync, flags); }
static const void* const kSlVt[23] = {(const void*)sl_qi, (const void*)sl_ref, (const void*)sl_rel, (const void*)sl_none, (const void*)sl_none,
    (const void*)sl_none, (const void*)sl_none, (const void*)sl_none, (const void*)sl_present, (const void*)sl_none, (const void*)sl_none,
    (const void*)sl_none, (const void*)sl_none, (const void*)sl_none, (const void*)sl_none, (const void*)sl_none, (const void*)sl_none,
    (const void*)sl_none, (const void*)sl_none, (const void*)sl_none, (const void*)sl_none, (const void*)sl_none, (const void*)sl_present1};
#pragma comment(linker, "/EXPORT:slGetFeatureFunction")
#pragma comment(linker, "/EXPORT:slSetConstants")
extern "C" int fake_sl_consts(const void*, const void*, const void*) { return 0; }
extern "C" void slPCLSetMarker();
extern "C" void slReflexSetMarker();
static LONG g_sl_markers;
extern "C" int fake_sl_getfn(uint32_t feature, const char* name, void** fn) {  // PCL's marker function, and Reflex's
    if (feature == 4 && !strcmp(name, "slPCLSetMarker")) return *fn = (void*)slPCLSetMarker, 0;
    return feature == 3 && !strcmp(name, "slReflexSetMarker") ? (*fn = (void*)slReflexSetMarker, 0) : 1;
}
extern "C" int fake_sl_rmarker(uint32_t, const void*) { return 0; }
// SCSKILLER_SELFTEST_SL_FWD: PCL's marker forwards each marker to Reflex's, as Streamline's PCL does with Reflex loaded
extern "C" int fake_sl_marker(uint32_t m, const void* f) {
    InterlockedIncrement(&g_sl_markers);
    static const bool fwd = GetEnvironmentVariableW(L"SCSKILLER_SELFTEST_SL_FWD", nullptr, 0) > 0;
    return fwd ? ((int (*)(uint32_t, const void*))slReflexSetMarker)(m, f) : 0;
}
// The marker function as a game that fetched it before the recorder came holds it, and how many calls reached it
extern "C" __declspec(dllexport) void* WINAPI FakeNext_SlMarkerFn() { return (void*)slPCLSetMarker; }
extern "C" __declspec(dllexport) LONG WINAPI FakeNext_SlMarkers() { return g_sl_markers; }
extern "C" int fake_sl_upgrade(void** p) {  // slUpgradeInterface (fakenext.asm) jumps here
    IDXGISwapChain1* sc = nullptr;
    if (!p || !*p || FAILED(((IUnknown*)*p)->QueryInterface(IID_PPV_ARGS(&sc)))) return 1;
    sc->Release();
    *p = new SlProxy{kSlVt, (IUnknown*)*p, 1};
    return 0;
}
extern "C" __declspec(dllexport) void FakeNext_SlSet(SlProxy* p, IUnknown* real) {
    if (real) real->AddRef();
    if (p->real) p->real->Release();
    p->real = real;
}
// `selftest nvopts`: NvAPI_Initialize and SetCreatePipelineStateOptions, which keeps the flags of each call it accepts for FakeNv_Opts.
static std::mutex g_nv_mx;
static std::vector<uint32_t> g_nv_opts;
extern "C" void FakeNv_SetOpts();
extern "C" void FakeNv_OneByte();
extern "C" void FakeNv_ShortPad();
extern "C" void FakeNv_LivePad();
extern "C" void FakeNv_Raced();
extern "C" int FakeNv_OnOpts(void*, const uint32_t* p) {
    if (!p || (p[0] & 0xFFFF) < 8) return -9;  // NVAPI_INCOMPATIBLE_STRUCT_VERSION
    std::lock_guard l(g_nv_mx);
    g_nv_opts.push_back(p[1]);
    return 0;
}
static int nv_init() { return 0; }
extern "C" __declspec(dllexport) int WINAPI FakeNv_Opts(uint32_t* out, int max) {
    std::lock_guard l(g_nv_mx);
    int n = (int)std::min<size_t>(max, g_nv_opts.size());
    std::copy_n(g_nv_opts.begin(), n, out);
    return n;
}

// `selftest nvqi`: a thread inside nvapi_QueryInterface, past its first instruction, while the proxy hooks it.
static std::atomic<int> g_nv_inside;
extern "C" void fake_nv_qi_inside() {
    g_nv_inside = 1;
    g_nv_inside.notify_all();
    g_nv_inside.wait(1);
}
#pragma comment(linker, "/EXPORT:FakeNext_NvQiInside")
extern "C" __declspec(dllexport) void WINAPI FakeNext_NvQiHold(bool release) {  // false: until a thread is inside; true: it goes on
    if (!release) g_nv_inside.wait(0);
    else g_nv_inside = 2, g_nv_inside.notify_all();
}

extern "C" void* fake_nv_qi(uint32_t id) {
    if (id == 0x0150E828) return (void*)nv_init;
    if (id == 0x5C607A27) return (void*)FakeNv_SetOpts;
    if (id == 0xAC2DFEB5) return (void*)FakeNv_OneByte;   // SetNvShaderExtnSlotSpace
    if (id == 0x43D867C0) return (void*)FakeNv_ShortPad;  // SetNvShaderExtnSlotSpaceLocalThread
    if (id == 0x2FC28856) return (void*)FakeNv_LivePad;   // CreateGraphicsPipelineState
    if (id == 0x2762DEAC) return (void*)FakeNv_Raced;     // CreateComputePipelineState
    if ((id & ~0xFu) == 0x5EE00000) return &g_nv_fns[id & 0xF];
    if ((id & ~0xFu) == 0x5EE10000) return nvapi_QueryInterface(0x5EE00000 | (id & 0xF));
    return nullptr;
}

// After FakeNext_NvAtExit, this dll's process detach resolves 16 ids it never resolved before and the 16 known ones
// through the exported entry (a dll that detaches after the proxy, loaded before it, calling NVAPI at process exit), then
// makes nv_at_exit.done beside itself.
static bool g_nv_at_exit;
extern "C" __declspec(dllexport) void WINAPI FakeNext_NvAtExit() { g_nv_at_exit = true; }
BOOL WINAPI DllMain(HINSTANCE self, DWORD reason, LPVOID reserved) {
    wchar_t mode[8], me[MAX_PATH];
    if (reason == DLL_PROCESS_ATTACH && GetEnvironmentVariableW(L"SCSKILLER_SELFTEST_OPTI", mode, 8) && GetModuleFileNameW(self, me, MAX_PATH) &&
        wcsrchr(me, L'\\') && !_wcsicmp(wcsrchr(me, L'\\') + 1, L"dxgi.dll")) {
        g_self = self, g_opti_wrap = !wcscmp(mode, L"wrap"), g_opti_fg = !wcsncmp(mode, L"fg", 2);
        g_sys_loadlib = (decltype(g_sys_loadlib))eat_point((uint8_t*)GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW", (void*)opti_loadlib);
    }
    if (reason != DLL_PROCESS_DETACH || !reserved || !g_nv_at_exit) return TRUE;
    for (uint32_t i = 0; i < 16; ++i) nvapi_QueryInterface(0x5EE20000 | i), nvapi_QueryInterface(0x5EE00000 | i);
    wchar_t p[MAX_PATH];
    if (DWORD n = GetModuleFileNameW(self, p, MAX_PATH); n && n < MAX_PATH - 16 && wcsrchr(p, L'\\')) {
        wcscpy(wcsrchr(p, L'\\') + 1, L"nv_at_exit.done");
        CloseHandle(CreateFileW(p, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr));
    }
    return TRUE;
}
