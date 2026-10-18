// End-to-end check of the proxy on the real GPU, each phase in its own process (like separate game launches).
// `selftest`: record run: 4 creates (graphics, compute, stream, stream again with garbage padding) -> exactly 3 db PSOs.
//        warm run:   all 3 replay, and the same 4 creates are all reported as known.
// Every run uses fresh shader bytecode (seeded constant) so nothing is already in the driver cache.
// `selftest <record|warm|probe|warmonly|debugwarm|dump> <seed>` runs one phase (see child).
// `selftest fields [runs] [dxil]` runs only probe 6: which PSO fields are in the driver's cache key (one field changed at a
//        time around cached shaders; see fields_child). Uses the system d3d12.dll, not the proxy.
// `selftest dxrchild 6 <seed> <blobs> <out>` creates the recorder rows through the proxy d3d12.dll next to the exe, and
//        `selftest dxrblobs <seed> <out>` writes the DXIL libraries they need.
// `selftest layoutrules` checks which input layouts the runtime accepts, on WARP (layout_rules).
// `selftest so <seed>` records stream output pipelines through the proxy, on WARP (so_rows).
// `selftest dxr [runs]` runs only probe 7: does the driver's disk cache keep ray tracing state objects
//        (CreateStateObject / collections / AddToStateObject), and at what granularity (see dxr_plan). Needs
//        dxcompiler.dll + dxil.dll (next to the exe, SELFTEST_DXC=<dir>, or the newest Windows SDK's bin\<ver>\x64).
// `selftest vulkan [runs]` runs only probe 8: does the Vulkan driver keep its own disk cache of pipelines (no
//        VkPipelineCache), keyed how (exe name, folder, NVIDIA's cache redirect variables), and at what granularity
//        (see vk_child). Needs vulkan-1.dll (installed with every Vulkan driver); SPIR-V from vk_spirv.h.
// `selftest cacheprobe`: what NVIDIA's DXCache keeps of a PSO (see cacheprobe_parent).
// `selftest nvext [runs]`: is NVAPI's shader-extension slot in the driver's key, and does the warm carry it (see nvext_child).
// `selftest nvopts`: on WARP, NVAPI's pipeline creation flags as recorded and as the warm sets them (see nvopts_child).
// `selftest amwarm [n]`: does a warm that starts NVIDIA Aftermath as the game does reach the game's cache (see amwarm_parent).
// `selftest bindless [runs] [exe name]`: are heap-indexed (SM 6.6 bindless) and RayQuery compute PSOs cached, per runtime (see bindless_parent).
#define NOMINMAX
#include <windows.h>
#include <initguid.h>
#include <bcrypt.h>
#include <shlobj.h>
#include <knownfolders.h>
#include <d3d12.h>
#include <d3d11.h>
#include <d3d12shader.h>
#include <dxgi1_4.h>
#include <tlhelp32.h>
#include <psapi.h>
#include <winternl.h>
#include <bcrypt.h>
#include <dxcapi.h>
#define VK_NO_PROTOTYPES
#include <vulkan/vulkan_core.h>  // third_party/vulkan: Khronos Vulkan-Headers vulkan-sdk-1.4.341.0 (Apache-2.0)
#include "vk_spirv.h"
#include "probe_util.h"
#include "recorder_key.h"
#include "overlay.h"
#include "admission.h"
#include "warm_memory.h"
#include "sys_dxgi.h"
#include <algorithm>
#include <array>
#include <atomic>
#include <cstring>
#include <deque>
#include <filesystem>
#include <functional>
#include <map>
#include <mutex>
#include <random>
#include <set>
#include <string>
#include <thread>
#include <tuple>
#include <vector>

#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")

extern "C" __declspec(dllexport) const int SCSKiller_WarmHost = 1;  // the proxy warms only in a process exporting it

template <D3D12_PIPELINE_STATE_SUBOBJECT_TYPE T, class V> struct alignas(void*) Sub { D3D12_PIPELINE_STATE_SUBOBJECT_TYPE t; V v; };
struct Stream {
    Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_ROOT_SIGNATURE, ID3D12RootSignature*> rs;
    Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_VS, D3D12_SHADER_BYTECODE> vs;
    Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_PS, D3D12_SHADER_BYTECODE> ps;
    Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_INPUT_LAYOUT, D3D12_INPUT_LAYOUT_DESC> il;
    Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_PRIMITIVE_TOPOLOGY, D3D12_PRIMITIVE_TOPOLOGY_TYPE> topo;
    Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RENDER_TARGET_FORMATS, D3D12_RT_FORMAT_ARRAY> rtf;
    Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL, D3D12_DEPTH_STENCIL_DESC> ds;
    Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_CACHED_PSO, D3D12_CACHED_PIPELINE_STATE> cached;  // must be dropped
};

// The system's copy, never a proxy next to the exe.
static HMODULE load_system(const wchar_t* dll) {
    wchar_t sys[MAX_PATH];
    GetSystemDirectoryW(sys, MAX_PATH);
    return LoadLibraryW((std::wstring(sys) + L"\\" + dll).c_str());
}

static IDXGIAdapter1* adapter_of(ID3D12Device* dev, DXGI_ADAPTER_DESC1& d) {
    IDXGIFactory4* f;
    IDXGIAdapter1* a;
    return SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumAdapterByLuid(dev->GetAdapterLuid(), IID_PPV_ARGS(&a))) &&
           SUCCEEDED(a->GetDesc1(&d)) ? a : nullptr;
}

// A probe's table: per column, each row's value in every run. A child writes "name\tvalue[\tvalue...]" lines; a failed
// create is a negative value, and -2 stands for a row the column doesn't have.
using Col = std::map<std::string, std::vector<double>>;

static double col_median(const Col& col, const std::string& n) {
    auto it = col.find(n);
    if (it == col.end() || it->second.empty()) return -2;
    auto v = it->second;
    std::sort(v.begin(), v.end());
    return v[v.size() / 2];
}

static std::string cell(double x) {
    char b[32];
    if (x >= 0) snprintf(b, sizeof b, " %8.2f", x);
    else snprintf(b, sizeof b, " %8s", x == -2 ? "-" : "fail");
    return std::string(b);
}

// row(name, rest) per line of a child's file, which is then deleted; new names are appended to order. False: no file.
template <class F> static bool read_rows(const std::wstring& path, std::vector<std::string>* order, F&& row) {
    FILE* f = _wfopen(path.c_str(), L"r");
    if (!f) return false;
    char line[512];
    while (fgets(line, sizeof line, f)) {
        char* tab = strchr(line, '\t');
        if (!tab) continue;
        std::string name(line, tab);
        if (order && std::find(order->begin(), order->end(), name) == order->end()) order->push_back(name);
        row(name, tab + 1);
    }
    fclose(f);
    DeleteFileW(path.c_str());
    return true;
}

// mode: record | warm (asserting test phases), probe (time creates), warmonly (replay the db, compile nothing of this
// seed's), debugwarm (warmonly under the debug layer), dump (write the seed's shaders)
static int child(const std::wstring& dir, const std::wstring& mode, unsigned seed) {
    bool warm = mode == L"warm" || mode == L"warmonly" || mode == L"debugwarm";
    SetEnvironmentVariableW(L"SCSKILLER_MODE", warm ? L"warm" : L"record");
    // agsunsigned: vendor=amd added by hand to an ini signed without it, before the proxy loads (its load-time gate, which
    // can't check sig=, then hooks AGS), and taken out again before the first device: admission takes the hook out
    if (mode == L"agsunsigned") CHECK(WritePrivateProfileStringW(L"scskiller", L"vendor", L"amd", (dir + L"scskiller.ini").c_str()));
    HMODULE m = LoadLibraryW((dir + L"d3d12.dll").c_str());
    CHECK(m);
    auto create_device = (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice");
    auto serialize_rs = (decltype(&D3D12SerializeRootSignature))GetProcAddress(m, "D3D12SerializeRootSignature");  // via asm stub
    auto stats = (void(WINAPI*)(uint64_t*))GetProcAddress(m, "SCSKiller_Stats");
    CHECK(create_device && serialize_rs && stats);

    if (mode == L"debugwarm") {  // D3D12 debug layer: explains why the runtime rejects a replayed PSO
        auto get_debug = (decltype(&D3D12GetDebugInterface))GetProcAddress(m, "D3D12GetDebugInterface");
        ID3D12Debug* dbg = nullptr;
        CHECK(get_debug && SUCCEEDED(get_debug(IID_PPV_ARGS(&dbg))));
        dbg->EnableDebugLayer();
    }
    if (mode == L"ags" || mode == L"agsrevoked" || mode == L"agsnoldr" || mode == L"agsdisarmed" || mode == L"agsunsigned") {  // a game's device through its AGS DLL (SELFTEST_AGS, AGS 6), loaded after the proxy as by LoadLibrary
        wchar_t path[MAX_PATH], self[MAX_PATH];
        GetModuleFileNameW(nullptr, self, MAX_PATH);
        std::wstring dir2 = std::wstring(self, wcsrchr(self, L'\\') + 1), revoked;
        bool pristineCmp = mode == L"agsrevoked" || mode == L"agsnoldr" || mode == L"agsdisarmed" || mode == L"agsunsigned";   // these compare the entry against a pristine copy
        bool expectNoHook = mode == L"agsnoldr" || mode == L"agsdisarmed";
        std::string before;
        bool listed = false;
        if (mode == L"agsrevoked") {  // the app revokes the folder after the proxy loaded: admission refuses at the first device
            revoked = recorder_dir(dir2) + L"\\recorder.revoked";
            CHECK(revoked.size() > 17);
            if (FILE* f = _wfopen(revoked.c_str(), L"rb")) {
                char b[4096];
                for (size_t n; (n = fread(b, 1, sizeof b, f));) before.append(b, n);
                fclose(f), listed = true;
            }
            std::string line;
            for (wchar_t c : file_key(dir2.substr(0, dir2.size() - 1))) line += (char)c;
            FILE* f = _wfopen(revoked.c_str(), L"ab");
            CHECK(f);
            fputs(("\r\n" + line + "\r\n").c_str(), f), fclose(f);
        }
        struct Unrevoke {
            std::wstring& f;
            std::string& was;
            bool& listed;
            ~Unrevoke() {
                if (f.empty()) return;
                if (!listed) DeleteFileW(f.c_str());
                else if (FILE* o = _wfopen(f.c_str(), L"wb")) fwrite(was.data(), 1, was.size(), o), fclose(o);
            }
        } unrevoke{revoked, before, listed};
        // the stamp's sig= gone after the proxy loaded (so its notification is registered) but before AGS loads: the
        // load-time gate must then install nothing. Its cheap check (no key, no shell) is loader-lock safe.
        if (mode == L"agsdisarmed") CHECK(WritePrivateProfileStringW(L"scskiller", L"sig", nullptr, (dir2 + L"scskiller.ini").c_str()));
        HMODULE ags = GetEnvironmentVariableW(L"SELFTEST_AGS", path, MAX_PATH) ? LoadLibraryW(path) : nullptr;
        CHECK(ags);
        // the hook patches the thunk's target, not the exported thunk: follow the same E9 chain the recorder does, bounded
        // to the module (the recorder's relay jump leaves the image, so a hooked entry stops at the module's own function)
        auto imgsize = [](HMODULE m) { auto b = (const uint8_t*)m; return ((const IMAGE_NT_HEADERS64*)(b + ((const IMAGE_DOS_HEADER*)b)->e_lfanew))->OptionalHeader.SizeOfImage; };
        auto follow = [](const uint8_t* p, const uint8_t* base, uint32_t size) {
            for (int i = 0; i < 4 && p && p[0] == 0xE9; ++i) { int32_t r; memcpy(&r, p + 1, 4); auto to = p + 5 + r; if (to < base || to >= base + size) break; p = to; }
            return p;
        };
        auto entry = follow((const uint8_t*)GetProcAddress(ags, "agsDriverExtensionsDX12_CreateDevice"), (const uint8_t*)ags, imgsize(ags));
        CHECK(entry);
        uint8_t pristine[8] = {};
        if (pristineCmp) {
            // a second amd_ags_x64.dll from another folder stays the original (the recorder declines a second once one is
            // bound, and installs nothing at all with no notification), so its entry is the pristine reference.
            std::wstring second = dir2 + L"second\\";
            CreateDirectoryW(second.c_str(), nullptr);
            CHECK(CopyFileW(path, (second + L"amd_ags_x64.dll").c_str(), FALSE));
            HMODULE p2 = LoadLibraryW((second + L"amd_ags_x64.dll").c_str());
            auto e2 = p2 ? follow((const uint8_t*)GetProcAddress(p2, "agsDriverExtensionsDX12_CreateDevice"), (const uint8_t*)p2, imgsize(p2)) : nullptr;
            CHECK(p2 && p2 != ags && e2);
            memcpy(pristine, e2, 8);
        }
        if (expectNoHook) {  // no notification (agsnoldr) or disarmed before AGS loaded (agsdisarmed): nothing installed
            if (memcmp(entry, pristine, 8)) return printf("FAIL: %ls: AGS was hooked when the load-time gate should have refused\n", mode.c_str()), 1;
            printf("ags: %ls: not installed\n", mode.c_str());
            return 0;
        }
        if ((mode == L"agsrevoked" || mode == L"agsunsigned") && !memcmp(pristine, entry, 8)) return printf("FAIL: the recorder didn't hook the first AGS, or hooked the second copy\n"), 1;
        if (mode == L"agsunsigned") CHECK(WritePrivateProfileStringW(L"scskiller", L"vendor", nullptr, (dir2 + L"scskiller.ini").c_str()));
        auto version = (int (*)())GetProcAddress(ags, "agsGetVersionNumber");
        auto init = (int (*)(int, const void*, void**, void*))GetProcAddress(ags, "agsInitialize");
        auto create = (int (*)(void*, const void*, const void*, void*))GetProcAddress(ags, "agsDriverExtensionsDX12_CreateDevice");
        CHECK(version && init && create);
        void* ctx = nullptr;
        if (int r = init(version(), nullptr, &ctx, nullptr)) {
            if (HANDLE h = CreateFileW((dir2 + L"no-ags-device").c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr); h != INVALID_HANDLE_VALUE) CloseHandle(h);
            return printf("ags: agsInitialize = %d, no AMD driver\n", r), 0;
        }
        IDXGIFactory1* f = nullptr;
        IDXGIAdapter1* adapter = nullptr;
        CHECK(SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumAdapters1(0, &adapter)));
        struct { IDXGIAdapter* adapter; IID iid; D3D_FEATURE_LEVEL level; } dp = {adapter, __uuidof(ID3D12Device), D3D_FEATURE_LEVEL_11_0};
        struct { const wchar_t *app, *engine; unsigned av, ev, uav; } ep = {L"SelftestAgsApp", L"Selftest,Engine%", 0x10203, 0x40506, 0};
        struct { ID3D12Device* device; unsigned ext; unsigned char later[60]; } rp = {};
        int r = create(ctx, &dp, &ep, &rp);
        printf("ags: DX12_CreateDevice = %d, extensions 0x%x\n", r, rp.ext);
        CHECK(r == 0 && rp.device);
        // admission refused at the device create: our own patch is cleaned back out, so the entry equals the pristine copy
        if ((mode == L"agsrevoked" || mode == L"agsunsigned") && memcmp(entry, pristine, 8)) return printf("FAIL: %ls: our AGS patch wasn't taken back out at admission\n", mode.c_str()), 1;
        if (mode == L"agsrevoked" || mode == L"agsunsigned") return printf("ags: %ls: patch removed at admission\n", mode.c_str()), 0;
        // a game that registers app X, then Y, then X again: only a consecutive repeat is dropped, so the csv gets all three
        for (auto app : {L"SelftestAgsAppB", L"SelftestAgsApp"}) {
            ep.app = app;
            rp = {};
            create(ctx, &dp, &ep, &rp);
        }
        return 0;
    }
    wchar_t am[MAX_PATH];  // loaded before the device, as a game does: the recorder hooks it at device creation
    HMODULE aftermath = mode == L"aftermath" && GetEnvironmentVariableW(L"SELFTEST_AFTERMATH", am, MAX_PATH) ? LoadLibraryW(am) : nullptr;
    if (mode == L"aftermath") CHECK(aftermath);
    if (aftermath) {  // the first devices from several threads at once: the hooks (NVAPI, Aftermath) install once, intact
        std::vector<std::thread> ts;
        std::atomic<int> made = 0;
        for (int i = 0; i < 6; ++i)
            ts.emplace_back([&] {
                ID3D12Device* d = nullptr;
                if (SUCCEEDED(create_device(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&d)))) ++made, d->Release();
            });
        for (auto& th : ts) th.join();
        CHECK(made == 6);
    }
    ID3D12Device* dev = nullptr;
    CHECK(SUCCEEDED(create_device(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))));
    ID3D12InfoQueue* iq = nullptr;
    if (mode == L"debugwarm" && SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&iq)))) iq->SetMuteDebugOutput(TRUE), iq->SetMessageCountLimit(UINT64_MAX);
    ID3D12Device2* dev2 = nullptr;
    CHECK(SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&dev2))));
    if (DXGI_ADAPTER_DESC1 d; mode == L"record" && adapter_of(dev, d)) printf("adapter: %ls\n", d.Description);
    if (aftermath) {  // the recorder logs the feature flags it is initialized with
        auto init = (int (*)(int, uint32_t, ID3D12Device*))GetProcAddress(aftermath, "GFSDK_Aftermath_DX12_Initialize");
        CHECK(init);
        printf("aftermath: DX12_Initialize = 0x%x\n", init(0x217, 8, dev));
        auto init11 = (int (*)(int, uint32_t, void*))GetProcAddress(aftermath, "GFSDK_Aftermath_DX11_Initialize");
        if (init11) printf("aftermath: DX11_Initialize = 0x%x\n", init11(0x217, 4, nullptr));   // shares the hooked init, never logged
        return 0;
    }

    // Shaders bind a cbuffer, texture, sampler and UAV so the root signature layout could matter to the compiler.
    std::string k = std::to_string(seed) + ".0";
    ID3DBlob* vs = compile("cbuffer C : register(b0) { float4 kc; };"
                           "float4 main(float3 p : POSITION, float2 uv : TEXCOORD0, out float2 ouv : TEXCOORD0) : SV_Position"
                           "{ ouv = uv; return float4(p * " + k + ", 1) + kc; }", "vs_5_0");
    ID3DBlob* ps = compile("Texture2D t : register(t0); SamplerState ss : register(s0); cbuffer C : register(b0) { float4 kc; };"
                           "float4 main(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return t.Sample(ss, uv) * " + k + " + kc; }", "ps_5_0");
    ID3DBlob* cs = compile("RWBuffer<uint> b : register(u0); [numthreads(1,1,1)] void main() { b[0] = " + std::to_string(seed) + "; }", "cs_5_0");
    CHECK(vs && ps && cs);
    if (mode == L"dump") {  // write this seed's shaders, then exit
        wchar_t out[MAX_PATH];
        CHECK(GetEnvironmentVariableW(L"SELFTEST_DUMP", out, MAX_PATH));
        for (auto [n, b] : {std::pair{L"vs", vs}, {L"ps", ps}, {L"cs", cs}})
            if (FILE* f = _wfopen((std::wstring(out) + L"\\" + n + L".bin").c_str(), L"wb")) fwrite(b->GetBufferPointer(), 1, b->GetBufferSize(), f), fclose(f);
        return 0;
    }

    D3D12_DESCRIPTOR_RANGE rcbv = {D3D12_DESCRIPTOR_RANGE_TYPE_CBV, 1, 0, 0, 0}, rsrv = {D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 0, 0, 0},
                           ruav = {D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 1, 0, 0, 0};
    D3D12_ROOT_PARAMETER prm[3] = {};
    for (auto& q : prm) q.ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
    prm[0].DescriptorTable = {1, &rcbv}, prm[1].DescriptorTable = {1, &rsrv}, prm[2].DescriptorTable = {1, &ruav};
    D3D12_STATIC_SAMPLER_DESC smp = {D3D12_FILTER_MIN_MAG_MIP_LINEAR, D3D12_TEXTURE_ADDRESS_MODE_WRAP, D3D12_TEXTURE_ADDRESS_MODE_WRAP,
                                     D3D12_TEXTURE_ADDRESS_MODE_WRAP, 0, 1, D3D12_COMPARISON_FUNC_NEVER, D3D12_STATIC_BORDER_COLOR_OPAQUE_BLACK,
                                     0, D3D12_FLOAT32_MAX, 0, 0, D3D12_SHADER_VISIBILITY_ALL};
    D3D12_ROOT_SIGNATURE_DESC rsd = {3, prm, 1, &smp, D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT};
    ID3DBlob *rsb = nullptr, *err = nullptr;
    CHECK(SUCCEEDED(serialize_rs(&rsd, D3D_ROOT_SIGNATURE_VERSION_1, &rsb, &err)));
    ID3D12RootSignature* rs = nullptr;
    CHECK(SUCCEEDED(dev->CreateRootSignature(0, rsb->GetBufferPointer(), rsb->GetBufferSize(), IID_PPV_ARGS(&rs))));
    ID3D12PipelineState* pso = nullptr;
    HRESULT hr = 0;

    D3D12_COMPUTE_PIPELINE_STATE_DESC c = {rs, {cs->GetBufferPointer(), cs->GetBufferSize()}};
    if (mode == L"warmonly" || mode == L"debugwarm") {  // any PSO create triggers the warm; this one is the only compile we cause
        CHECK(SUCCEEDED(dev->CreateComputePipelineState(&c, IID_PPV_ARGS(&pso))));
        uint64_t st[7];
        stats(st);
        printf("warmonly: replayed ok=%llu fail=%llu\n", st[2], st[3]);
        if (iq) {  // unique errors, with counts
            std::map<int, std::pair<int, std::string>> errs;
            for (UINT64 i = 0, n = iq->GetNumStoredMessages(); i < n; ++i) {
                SIZE_T len = 0;
                iq->GetMessage(i, nullptr, &len);
                std::string buf(len, '\0');
                auto* msg = (D3D12_MESSAGE*)buf.data();
                iq->GetMessage(i, msg, &len);
                if (msg->Severity > D3D12_MESSAGE_SEVERITY_ERROR) continue;
                auto& e = errs[msg->ID];
                if (!e.first++) e.second.assign(msg->pDescription, msg->DescriptionByteLength);
            }
            for (auto& [id, e] : errs) printf("[%d x%d] %.700s\n", id, e.first, e.second.c_str());
        }
        return 0;
    }

    D3D12_INPUT_ELEMENT_DESC ie[2] = {{"POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0},
                                      {"TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 12, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0}};
    D3D12_GRAPHICS_PIPELINE_STATE_DESC g = {};
    g.pRootSignature = rs;
    g.VS = {vs->GetBufferPointer(), vs->GetBufferSize()};
    g.PS = {ps->GetBufferPointer(), ps->GetBufferSize()};
    g.BlendState.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;
    g.SampleMask = UINT_MAX;
    g.RasterizerState = {D3D12_FILL_MODE_SOLID, D3D12_CULL_MODE_BACK};
    g.RasterizerState.DepthClipEnable = TRUE;
    g.InputLayout = {ie, 2};
    g.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
    g.NumRenderTargets = 1;
    g.RTVFormats[0] = DXGI_FORMAT_R8G8B8A8_UNORM;
    g.SampleDesc = {1, 0};

    double t_gfx = ms([&] { hr = dev->CreateGraphicsPipelineState(&g, IID_PPV_ARGS(&pso)); });
    CHECK(SUCCEEDED(hr));
    pso->Release();
    double t_cs = ms([&] { hr = dev->CreateComputePipelineState(&c, IID_PPV_ARGS(&pso)); });
    CHECK(SUCCEEDED(hr));
    pso->Release();

    double t_stream = 0;
    Stream s;
    D3D12_PIPELINE_STATE_STREAM_DESC sd = {sizeof s, &s};
    for (int garbage = 0; garbage < 2; ++garbage) {  // same PSO twice; padding bytes differ, key must not
        memset(&s, garbage ? 0xCD : 0, sizeof s);
        s.rs = {D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_ROOT_SIGNATURE, rs};
        s.vs = {D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_VS, g.VS};
        s.ps = {D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_PS, g.PS};
        s.il.t = D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_INPUT_LAYOUT, s.il.v = g.InputLayout;
        s.topo = {D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_PRIMITIVE_TOPOLOGY, D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE};
        s.rtf.t = D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RENDER_TARGET_FORMATS;
        s.rtf.v = {{DXGI_FORMAT_R16G16B16A16_FLOAT}, 1};
        s.ds.t = D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL;
        s.ds.v.DepthEnable = FALSE, s.ds.v.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ZERO, s.ds.v.DepthFunc = D3D12_COMPARISON_FUNC_ALWAYS;
        s.ds.v.StencilEnable = FALSE, s.ds.v.StencilReadMask = s.ds.v.StencilWriteMask = 0xFF;
        s.ds.v.FrontFace = s.ds.v.BackFace = {D3D12_STENCIL_OP_KEEP, D3D12_STENCIL_OP_KEEP, D3D12_STENCIL_OP_KEEP, D3D12_COMPARISON_FUNC_ALWAYS};
        s.cached = {D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_CACHED_PSO, {}};
        double t = ms([&] { hr = dev2->CreatePipelineState(&sd, IID_PPV_ARGS(&pso)); });
        if (!garbage) t_stream = t;
        CHECK(SUCCEEDED(hr));
        pso->Release();
    }

    if (mode == L"record") {  // UE4's PSO disk cache path: PSOs served from an ID3D12PipelineLibrary must be seen too
        ID3D12Device1* d1 = nullptr;
        ID3D12PipelineLibrary1* lib = nullptr;
        CHECK(SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&d1))) && SUCCEEDED(d1->CreatePipelineLibrary(nullptr, 0, IID_PPV_ARGS(&lib))));
        ID3D12PipelineState *pg, *pc, *pst, *out;
        CHECK(SUCCEEDED(dev->CreateGraphicsPipelineState(&g, IID_PPV_ARGS(&pg))) && SUCCEEDED(dev->CreateComputePipelineState(&c, IID_PPV_ARGS(&pc))));
        CHECK(SUCCEEDED(dev2->CreatePipelineState(&sd, IID_PPV_ARGS(&pst))));
        CHECK(SUCCEEDED(lib->StorePipeline(L"g", pg)) && SUCCEEDED(lib->StorePipeline(L"c", pc)) && SUCCEEDED(lib->StorePipeline(L"s", pst)));
        CHECK(SUCCEEDED(lib->LoadGraphicsPipeline(L"g", &g, IID_PPV_ARGS(&out))));
        out->Release();
        CHECK(SUCCEEDED(lib->LoadComputePipeline(L"c", &c, IID_PPV_ARGS(&out))));
        out->Release();
        CHECK(SUCCEEDED(lib->LoadPipeline(L"s", &sd, IID_PPV_ARGS(&out))));
        out->Release();
        CHECK(FAILED(lib->LoadGraphicsPipeline(L"missing", &g, IID_PPV_ARGS(&out))));  // a miss is not recorded
    }

    uint64_t st[7];
    stats(st);
    if (mode == L"record") CHECK(st[6] == 3);  // three library loads seen, no new db entries (same descs)
    if (mode == L"probe") {
        printf("  gfx=%6.2fms  cs=%6.2fms  stream=%6.2fms  tuple_known=%llu\n", t_gfx, t_cs, t_stream, st[5]);
        return 0;
    }
    printf("%ls: db_at_start=%llu db_now=%llu warm_ok=%llu warm_fail=%llu known=%llu\n", mode.c_str(), st[0], st[1], st[2], st[3], st[4]);
    if (mode == L"warmed") CHECK(st[0] == 3 && st[1] == 3 && st[4] == 2 && st[5] == 2);  // scskiller.warmed names the stream PSO only, created twice
    else if (!warm) CHECK(st[0] == 0 && st[1] == 3 && st[4] == 0);
    else CHECK(st[0] == 3 && st[1] == 3 && st[2] == 3 && st[3] == 0 && st[4] == 4);
    return 0;
}

// Probe 6 (`selftest fields`): which PSO fields are in the cache key.
// One child process creates a baseline PSO (cold: fresh seed), then variants that change ONE field each, every one twice
// (first-seen, then again in the same process). A second process with the same exe name re-creates everything: fast =
// the variant was written to the disk cache. A third, same exe file name in another folder, does the same before it.
// Shaders: the VS reads POSITION + TEXCOORD0; the layout also has COLOR0, which the VS doesn't read, so semantic
// name/index/slot changes can be tried without touching the VS (a read element must match the VS signature).
// Root signature: register space can only change on a range no shader uses (u0 table for graphics); SM5 has no spaces.
// Probe 6b rows (planner reductions): pair/xpair (is the cache per stage or per VS+PS pair, in and across processes),
// il1 (declared-but-unread input), rs2 (RS changes the VS can't see, attributed with a fresh partner stage), vsonly
// (depth pre-pass PSO vs VS+PS), zps (PS writing SV_Depth), rt2 (export class of more RT formats). Vendor-neutral.
// `selftest fields [runs] dxil`: the same rows with SM 6.0 DXIL shaders (dxcompiler, see dxc_load) instead of SM 5.0 DXBC.
// vi rows: view instancing (a pipeline stream); so: stream output; rs "same bytes, another object": root-signature identity.
struct FCfg {
    D3D12_GRAPHICS_PIPELINE_STATE_DESC g = {};
    std::vector<D3D12_INPUT_ELEMENT_DESC> il;
    int rs = 0, vs = 0, ps = 0;
    UINT views = 0;  // > 0: created as a pipeline stream with a VIEW_INSTANCING subobject of this many views
};

// From RS_NGEN on: changes the VS doesn't see (rs2 rows): RS_T0N2 from base, RS_P_* from RS_VIS (t0 table PIXEL-only).
enum { RS_BASE, RS_VIS, RS_ORDER, RS_SMP_POINT, RS_SMP_CLAMP, RS_EXTRA_CONST, RS_SPACE, RS_ROOT_CBV, RS_DENY, RS_V11, RS_SAME,
       RS_SO, RS_NGEN, RS_T0N2 = RS_NGEN, RS_P_N2, RS_P_OFF, RS_P_ADD, RS_P_SMP, RS_P_U4, RS_COUNT };
static const char* const kRsNames[RS_COUNT] = {
    "base", "t0 table visibility ALL->PIXEL", "params 0/1 swapped (b0 table <-> t0 table)", "static sampler LINEAR->POINT",
    "static sampler WRAP->CLAMP", "+ unused root constant (b1)", "unused u0 range space0->space1", "b0 table -> root CBV",
    "flags + DENY_HS/DS/GS", "same layout serialized v1.1 (static descriptors)", "same bytes, another object",
    "flags + ALLOW_STREAM_OUTPUT",
    "t0 table (ALL, only PS reads) 1->2 descr", "PIXEL t0 table 1->2 descr", "PIXEL t0 table: t1 range first",
    "+ PIXEL-only table (t1, unread)", "static sampler vis ALL->PIXEL", "u0 table (unread) 1->4 descr"};

static ID3D12RootSignature* fields_rs(ID3D12Device* dev, PFN_D3D12_SERIALIZE_VERSIONED_ROOT_SIGNATURE ser, int v) {
    D3D12_DESCRIPTOR_RANGE r[3] = {{D3D12_DESCRIPTOR_RANGE_TYPE_CBV, 1, 0, 0, 0}, {D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 0, 0, 0},
                                   {D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 1, 0, v == RS_SPACE ? 1u : 0u, 0}};
    std::vector<D3D12_ROOT_PARAMETER> p(3);
    for (int i = 0; i < 3; ++i) p[i].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE, p[i].DescriptorTable = {1, &r[i]};
    if (v == RS_VIS || v > RS_T0N2) p[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
    D3D12_DESCRIPTOR_RANGE t1 = {D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 1, 0, 0},
                           t1t0[2] = {t1, {D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 0, 0, D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND}};
    if (v == RS_T0N2 || v == RS_P_N2) r[1].NumDescriptors = 2;
    if (v == RS_P_U4) r[2].NumDescriptors = 4;
    if (v == RS_P_OFF) p[1].DescriptorTable = {2, t1t0};
    if (v == RS_P_ADD) {
        p.push_back({}), p[3].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE, p[3].DescriptorTable = {1, &t1};
        p[3].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
    }
    if (v == RS_ORDER) std::swap(p[0], p[1]);
    if (v == RS_EXTRA_CONST) p.push_back({}), p[3].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS, p[3].Constants = {1, 0, 1};
    if (v == RS_ROOT_CBV) p[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV, p[0].Descriptor = {0, 0};
    D3D12_STATIC_SAMPLER_DESC smp = {v == RS_SMP_POINT ? D3D12_FILTER_MIN_MAG_MIP_POINT : D3D12_FILTER_MIN_MAG_MIP_LINEAR,
                                     D3D12_TEXTURE_ADDRESS_MODE_WRAP, D3D12_TEXTURE_ADDRESS_MODE_WRAP, D3D12_TEXTURE_ADDRESS_MODE_WRAP, 0, 1,
                                     D3D12_COMPARISON_FUNC_NEVER, D3D12_STATIC_BORDER_COLOR_OPAQUE_BLACK, 0, D3D12_FLOAT32_MAX, 0, 0,
                                     D3D12_SHADER_VISIBILITY_ALL};
    if (v == RS_SMP_CLAMP) smp.AddressU = smp.AddressV = smp.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
    if (v == RS_P_SMP) smp.ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
    auto flags = D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT;
    if (v == RS_DENY)
        flags |= D3D12_ROOT_SIGNATURE_FLAG_DENY_HULL_SHADER_ROOT_ACCESS | D3D12_ROOT_SIGNATURE_FLAG_DENY_DOMAIN_SHADER_ROOT_ACCESS |
                 D3D12_ROOT_SIGNATURE_FLAG_DENY_GEOMETRY_SHADER_ROOT_ACCESS;
    if (v == RS_SO) flags |= D3D12_ROOT_SIGNATURE_FLAG_ALLOW_STREAM_OUTPUT;
    D3D12_VERSIONED_ROOT_SIGNATURE_DESC vd = {D3D_ROOT_SIGNATURE_VERSION_1_0};
    vd.Desc_1_0 = {(UINT)p.size(), p.data(), 1, &smp, flags};
    D3D12_DESCRIPTOR_RANGE1 r1[3];
    D3D12_ROOT_PARAMETER1 p1[3] = {};
    if (v == RS_V11) {  // same ranges, 1.1 default flags (descriptors static, data static-while-set) instead of 1.0's volatile
        for (int i = 0; i < 3; ++i) {
            r1[i] = {r[i].RangeType, 1, 0, 0, D3D12_DESCRIPTOR_RANGE_FLAG_NONE, 0};
            p1[i].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE, p1[i].DescriptorTable = {1, &r1[i]};
        }
        vd.Version = D3D_ROOT_SIGNATURE_VERSION_1_1, vd.Desc_1_1 = {3, p1, 1, &smp, flags};
    }
    ID3DBlob *b = nullptr, *err = nullptr;
    ID3D12RootSignature* rs = nullptr;
    if (FAILED(ser(&vd, &b, &err)) || FAILED(dev->CreateRootSignature(0, b->GetBufferPointer(), b->GetBufferSize(), IID_PPV_ARGS(&rs))))
        printf("  root signature %d failed\n", v);
    return rs;
}

static IDxcCompiler3* dxc_compiler();
static bool dxc_compile(IDxcCompiler3* c, const std::string& src, std::string& out, LPCWSTR target);

// proc 1 writes "name\tfirst\tagain" per row, later procs "name\tms"; a failed create is written as -1.
static int fields_child(int proc, unsigned seed, const std::wstring& out) {
    HMODULE m = load_system(L"d3d12.dll");
    CHECK(m);
    auto create_device = (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice");
    auto ser = (PFN_D3D12_SERIALIZE_VERSIONED_ROOT_SIGNATURE)GetProcAddress(m, "D3D12SerializeVersionedRootSignature");
    CHECK(create_device && ser);
    ID3D12Device* dev = nullptr;
    CHECK(SUCCEEDED(create_device(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))));
    if (DXGI_ADAPTER_DESC1 d; proc == 1 && adapter_of(dev, d)) printf("  adapter: %ls, seed %u\n", d.Description, seed);

    // Shader sets: 0 = baseline, 1 = second VS / PS (cached with the baseline state first), 2 = fresh-seed control,
    // 3 = a seed only this (later) process sees: cold there.
    // Further sets (probe 6b) are added by addvs/addps: fresh per run (seed + 1000.. / 2000..), the same in every process.
    std::vector<D3D12_SHADER_BYTECODE> vsb, psb;
    bool shaders_ok = true;
    const bool want_dxil = GetEnvironmentVariableW(L"SELFTEST_FIELDS_DXIL", nullptr, 0) > 0;
    IDxcCompiler3* dxc = want_dxil ? dxc_compiler() : nullptr;
    CHECK(dxc || !want_dxil);
    std::deque<std::string> dxil;
    auto blob = [&](const std::string& src, const char* target) -> D3D12_SHADER_BYTECODE {
        if (dxc) {
            std::wstring t(target, target + 3);
            if (!dxc_compile(dxc, src, dxil.emplace_back(), (t + L"6_0").c_str())) return shaders_ok = false, D3D12_SHADER_BYTECODE{};
            return {dxil.back().data(), dxil.back().size()};
        }
        ID3DBlob* b = compile(src, target);  // kept alive for the process
        if (!b) return shaders_ok = false, D3D12_SHADER_BYTECODE{};
        return {b->GetBufferPointer(), b->GetBufferSize()};
    };
    auto vs_src = [](unsigned k, const char* extra_in) {
        return "cbuffer C : register(b0) { float4 kc; };"
               "float4 main(float3 p : POSITION, float2 uv : TEXCOORD0, " + std::string(extra_in) + "out float2 ouv : TEXCOORD0) : SV_Position"
               "{ ouv = uv; return float4(p * " + std::to_string(k) + ".0, 1) + kc; }";
    };
    auto ps_src = [](unsigned k) {
        return "Texture2D t : register(t0); SamplerState ss : register(s0); cbuffer C : register(b0) { float4 kc; };"
               "float4 main(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return t.Sample(ss, uv) * " + std::to_string(k) + ".0 + kc; }";
    };
    for (unsigned i = 0; i < 4; ++i) {
        unsigned o = i == 3 ? 10 + proc : i;  // set 3 differs per process: cold in each
        vsb.push_back(blob(vs_src(seed + (i == 1 ? 700 : o), ""), "vs_5_0")), psb.push_back(blob(ps_src(seed + (i == 1 ? 500 : o)), "ps_5_0"));
    }
    unsigned nvs = 0, nps = 0;
    auto addvs = [&](const char* extra_in = "") { vsb.push_back(blob(vs_src(seed + 1000 + nvs++, extra_in), "vs_5_0")); return (int)vsb.size() - 1; };
    auto addps = [&](const char* src = nullptr) {
        unsigned k = seed + 2000 + nps++;
        psb.push_back(blob(src ? std::string(src) + std::to_string(k) + ".0 + kc.x; }" : ps_src(k), "ps_5_0"));
        return (int)psb.size() - 1;
    };
    // VS u: declares TEXCOORD1 but never reads it (input signature mask xyzw, used mask 0, like FF7's 687 such elements)
    int vs_u = addvs("float4 u1 : TEXCOORD1, ");
    // PS z: writes only SV_Depth (no render target)
    int ps_z = addps("cbuffer C : register(b0) { float4 kc; };"
                     "float main(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Depth { return uv.x * ");
    CHECK(shaders_ok);
    if (proc == 1) {  // the used mask the driver sees for TEXCOORD1 (0 = declared, never read)
        ID3D12ShaderReflection* refl = nullptr;
        D3D12_SHADER_DESC sd = {};
        if (SUCCEEDED(D3DReflect(vsb[vs_u].pShaderBytecode, vsb[vs_u].BytecodeLength, IID_PPV_ARGS(&refl))) && SUCCEEDED(refl->GetDesc(&sd)))
            for (UINT i = 0; i < sd.InputParameters; ++i) {
                D3D12_SIGNATURE_PARAMETER_DESC pd;
                refl->GetInputParameterDesc(i, &pd);
                printf("  VS u input %s%u: register %u, mask 0x%x, used mask 0x%x\n", pd.SemanticName, pd.SemanticIndex, pd.Register, pd.Mask, pd.ReadWriteMask);
            }
    }
    D3D12_SHADER_BYTECODE csb = blob("RWBuffer<uint> b : register(u0); Texture2D t : register(t0); cbuffer C : register(b0) { uint4 kc; };"
                                     "[numthreads(8,1,1)] void main(uint i : SV_DispatchThreadID) { b[i] = kc.x + (uint)t.Load(int3(i, 0, 0)).x * " +
                                     std::to_string(seed) + "; }", "cs_5_0");
    CHECK(shaders_ok);
    ID3D12RootSignature* rss[RS_COUNT];
    for (int i = 0; i < RS_COUNT; ++i) rss[i] = fields_rs(dev, ser, i);

    FCfg base;
    base.il = {{"POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0},
               {"TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 12, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0},
               {"COLOR", 0, DXGI_FORMAT_R8G8B8A8_UNORM, 0, 20, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0}};  // not read by the VS
    auto& g = base.g;
    g.BlendState.RenderTarget[0] = {TRUE, FALSE, D3D12_BLEND_SRC_ALPHA, D3D12_BLEND_INV_SRC_ALPHA, D3D12_BLEND_OP_ADD, D3D12_BLEND_ONE,
                                    D3D12_BLEND_ZERO, D3D12_BLEND_OP_ADD, D3D12_LOGIC_OP_NOOP, D3D12_COLOR_WRITE_ENABLE_ALL};
    g.SampleMask = UINT_MAX;
    g.RasterizerState = {D3D12_FILL_MODE_SOLID, D3D12_CULL_MODE_BACK, FALSE, 0, 0.f, 0.f, TRUE, FALSE, FALSE, 0,
                         D3D12_CONSERVATIVE_RASTERIZATION_MODE_OFF};
    D3D12_DEPTH_STENCILOP_DESC so = {D3D12_STENCIL_OP_KEEP, D3D12_STENCIL_OP_KEEP, D3D12_STENCIL_OP_KEEP, D3D12_COMPARISON_FUNC_ALWAYS};
    g.DepthStencilState = {TRUE, D3D12_DEPTH_WRITE_MASK_ALL, D3D12_COMPARISON_FUNC_LESS_EQUAL, FALSE, 0xFF, 0xFF, so, so};
    g.DSVFormat = DXGI_FORMAT_D24_UNORM_S8_UINT;
    g.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
    g.NumRenderTargets = 1;
    g.RTVFormats[0] = DXGI_FORMAT_R8G8B8A8_UNORM;
    g.SampleDesc = {1, 0};

    auto gfx = [&](FCfg c) {
        c.g.pRootSignature = rss[c.rs], c.g.VS = vsb[c.vs], c.g.PS = c.ps < 0 ? D3D12_SHADER_BYTECODE{} : psb[c.ps], c.g.InputLayout = {c.il.data(), (UINT)c.il.size()};
        ID3D12PipelineState* pso = nullptr;
        HRESULT hr = 0;
        double t;
        if (c.views) {  // the same desc as a stream (the vi rows); views 1 = no VIEW_INSTANCING effect, the stream path's control
            std::vector<D3D12_VIEW_INSTANCE_LOCATION> loc(c.views, {0, 0});
            using T = D3D12_PIPELINE_STATE_SUBOBJECT_TYPE;
            D3D12_RT_FORMAT_ARRAY rtf = {{}, c.g.NumRenderTargets};
            memcpy(rtf.RTFormats, c.g.RTVFormats, sizeof rtf.RTFormats);
            struct {
                Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_ROOT_SIGNATURE, ID3D12RootSignature*> rs;
                Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_VS, D3D12_SHADER_BYTECODE> vs;
                Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_PS, D3D12_SHADER_BYTECODE> ps;
                Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_BLEND, D3D12_BLEND_DESC> blend;
                Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_SAMPLE_MASK, UINT> mask;
                Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RASTERIZER, D3D12_RASTERIZER_DESC> rast;
                Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL, D3D12_DEPTH_STENCIL_DESC> ds;
                Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_INPUT_LAYOUT, D3D12_INPUT_LAYOUT_DESC> il;
                Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_PRIMITIVE_TOPOLOGY, D3D12_PRIMITIVE_TOPOLOGY_TYPE> topo;
                Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RENDER_TARGET_FORMATS, D3D12_RT_FORMAT_ARRAY> rtf;
                Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL_FORMAT, DXGI_FORMAT> dsv;
                Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_SAMPLE_DESC, DXGI_SAMPLE_DESC> sd;
                Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_VIEW_INSTANCING, D3D12_VIEW_INSTANCING_DESC> vi;
            } s = {{T(0), c.g.pRootSignature}, {T(1), c.g.VS}, {T(2), c.g.PS}, {T(8), c.g.BlendState}, {T(9), c.g.SampleMask},
                   {T(10), c.g.RasterizerState}, {T(11), c.g.DepthStencilState}, {T(12), c.g.InputLayout}, {T(14), c.g.PrimitiveTopologyType},
                   {T(15), rtf}, {T(16), c.g.DSVFormat}, {T(17), c.g.SampleDesc}, {T(22), {c.views > 1 ? c.views : 0, loc.data(), D3D12_VIEW_INSTANCING_FLAG_NONE}}};
            D3D12_PIPELINE_STATE_STREAM_DESC d = {sizeof s, &s};
            ID3D12Device2* d2 = nullptr;
            if (FAILED(dev->QueryInterface(IID_PPV_ARGS(&d2)))) return -1.0;
            t = ms([&] { hr = d2->CreatePipelineState(&d, IID_PPV_ARGS(&pso)); });
            d2->Release();
        } else t = ms([&] { hr = dev->CreateGraphicsPipelineState(&c.g, IID_PPV_ARGS(&pso)); });
        if (FAILED(hr)) return -1.0;
        pso->Release();
        return t;
    };
    auto cs = [&](int rs) {
        D3D12_COMPUTE_PIPELINE_STATE_DESC c = {rss[rs], csb};
        ID3D12PipelineState* pso = nullptr;
        HRESULT hr = 0;
        double t = ms([&] { hr = dev->CreateComputePipelineState(&c, IID_PPV_ARGS(&pso)); });
        if (FAILED(hr)) return -1.0;
        pso->Release();
        return t;
    };

    struct Row { std::string name; std::function<double()> f; };
    std::vector<Row> rows;
    auto V = [&](const char* name, std::function<void(FCfg&)> mod) {
        rows.push_back({name, [&, mod] { FCfg c = base; mod(c); return gfx(c); }});
    };
    // il indices: 0 POSITION (read), 1 TEXCOORD (read), 2 COLOR (unread)
    // The first compile in a process also pays compiler start-up: an unrelated cold PSO (set 3) takes it first.
    rows.push_back({"ctl: compiler warm-up (unrelated cold PSO)", [&] { FCfg c = base; c.vs = c.ps = 3; return gfx(c); }});
    V("ctl: baseline (proc 1: cold)", [](FCfg&) {});
    V("ctl: baseline re-create", [](FCfg&) {});
    V("ctl: fresh seed (VS+PS)", [](FCfg& c) { c.vs = c.ps = 2; });
    V("setup: baseline with VS b (cold VS)", [](FCfg& c) { c.vs = 1; });
    V("setup: baseline with PS b (cold PS)", [](FCfg& c) { c.ps = 1; });
    // Per stage or per pair? VS b and PS b were each compiled only with another partner (the two setup rows above).
    // FREE/CHEAP = stage binaries are cached and linked; FULL = the cache is per VS+PS pair.
    V("pair: VS b + PS b (each only seen with another partner)", [](FCfg& c) { c.vs = c.ps = 1; });
    {  // Same across processes: proc 1 compiles VS x + PS a and VS a + PS y; the pair x+y is first created in od (proc 3),
       // another pair only in p2 (proc 2). Its p1 column is that setup (4 creates, 2 cold stages each), not a variant.
        int xv[2] = {addvs(), addvs()}, xp[2] = {addps(), addps()};
        rows.push_back({"xpair: VS x + PS y, partners from p1; pair new in od/p2", [&, xv, xp] {
            FCfg c = base;
            if (proc == 3 || proc == 2) return c.vs = xv[proc == 2], c.ps = xp[proc == 2], gfx(c);
            double t = 0;
            for (int i = 0; i < 2; ++i) c.vs = xv[i], c.ps = 0, t += gfx(c), c.vs = 0, c.ps = xp[i], t += gfx(c);
            return t;
        }});
    }

    V("il: POSITION fmt R32G32B32_FLOAT->R16G16B16A16_FLOAT", [](FCfg& c) { c.il[0].Format = DXGI_FORMAT_R16G16B16A16_FLOAT; });
    V("il:   same layout + PS b (VS unchanged)", [](FCfg& c) { c.il[0].Format = DXGI_FORMAT_R16G16B16A16_FLOAT, c.ps = 1; });
    V("il:   same layout + VS b", [](FCfg& c) { c.il[0].Format = DXGI_FORMAT_R16G16B16A16_FLOAT, c.vs = 1; });
    V("il: POSITION fmt R32G32B32_FLOAT->R32G32B32A32_FLOAT", [](FCfg& c) { c.il[0].Format = DXGI_FORMAT_R32G32B32A32_FLOAT; });
    V("il: TEXCOORD fmt R32G32_FLOAT->R16G16_FLOAT", [](FCfg& c) { c.il[1].Format = DXGI_FORMAT_R16G16_FLOAT; });
    V("il: TEXCOORD fmt R32G32_FLOAT->R16G16_UNORM", [](FCfg& c) { c.il[1].Format = DXGI_FORMAT_R16G16_UNORM; });
    V("il: COLOR (unread) fmt RGBA8_UNORM->R32_FLOAT", [](FCfg& c) { c.il[2].Format = DXGI_FORMAT_R32_FLOAT; });
    V("il: TEXCOORD offset 12->16", [](FCfg& c) { c.il[1].AlignedByteOffset = 16; });
    V("il: TEXCOORD offset 12->APPEND_ALIGNED (=12)", [](FCfg& c) { c.il[1].AlignedByteOffset = D3D12_APPEND_ALIGNED_ELEMENT; });
    V("il: COLOR (unread) offset 20->24", [](FCfg& c) { c.il[2].AlignedByteOffset = 24; });
    V("il: TEXCOORD slot 0->1", [](FCfg& c) { c.il[1].InputSlot = 1; });
    V("il: COLOR (unread) slot 0->1", [](FCfg& c) { c.il[2].InputSlot = 1; });
    V("il: COLOR (unread) semantic name ->NORMAL", [](FCfg& c) { c.il[2].SemanticName = "NORMAL"; });
    V("il: COLOR (unread) semantic index 0->1", [](FCfg& c) { c.il[2].SemanticIndex = 1; });
    V("il: POSITION semantic case ->position", [](FCfg& c) { c.il[0].SemanticName = "position"; });
    // One slot can't mix per-vertex and per-instance elements (create fails): compare these with the "slot 0->1" rows.
    V("il: TEXCOORD slot 1 per-instance, step 1", [](FCfg& c) { c.il[1].InputSlot = 1, c.il[1].InputSlotClass = D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, c.il[1].InstanceDataStepRate = 1; });
    V("il: TEXCOORD slot 1 per-instance, step 2", [](FCfg& c) { c.il[1].InputSlot = 1, c.il[1].InputSlotClass = D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, c.il[1].InstanceDataStepRate = 2; });
    V("il: COLOR (unread) slot 1 per-instance, step 1", [](FCfg& c) { c.il[2].InputSlot = 1, c.il[2].InputSlotClass = D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, c.il[2].InstanceDataStepRate = 1; });
    V("il: + unused TANGENT element", [](FCfg& c) { c.il.push_back({"TANGENT", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 0, 24, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0}); });
    V("il: - unused COLOR element", [](FCfg& c) { c.il.pop_back(); });
    V("il: element order TEXCOORD,POSITION,COLOR", [](FCfg& c) { std::swap(c.il[0], c.il[1]); });
    V("il: element order COLOR,POSITION,TEXCOORD", [](FCfg& c) { std::rotate(c.il.begin(), c.il.begin() + 2, c.il.end()); });

    V("blend: enable off", [](FCfg& c) { c.g.BlendState.RenderTarget[0].BlendEnable = FALSE; });
    V("blend: color factors ->ONE/ONE", [](FCfg& c) { c.g.BlendState.RenderTarget[0].SrcBlend = c.g.BlendState.RenderTarget[0].DestBlend = D3D12_BLEND_ONE; });
    V("blend: color op ADD->REV_SUBTRACT", [](FCfg& c) { c.g.BlendState.RenderTarget[0].BlendOp = D3D12_BLEND_OP_REV_SUBTRACT; });
    V("blend: alpha factors ONE/ZERO->ZERO/ONE", [](FCfg& c) { c.g.BlendState.RenderTarget[0].SrcBlendAlpha = D3D12_BLEND_ZERO, c.g.BlendState.RenderTarget[0].DestBlendAlpha = D3D12_BLEND_ONE; });
    V("blend: write mask ALL->RGB", [](FCfg& c) { c.g.BlendState.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_RED | D3D12_COLOR_WRITE_ENABLE_GREEN | D3D12_COLOR_WRITE_ENABLE_BLUE; });
    V("blend: write mask ALL->0", [](FCfg& c) { c.g.BlendState.RenderTarget[0].RenderTargetWriteMask = 0; });
    V("blend: alpha-to-coverage on", [](FCfg& c) { c.g.BlendState.AlphaToCoverageEnable = TRUE; });
    V("blend: independent blend on (RT0 same)", [](FCfg& c) { c.g.BlendState.IndependentBlendEnable = TRUE; });
    V("blend: logic op XOR (blend off), RT RGBA8_UINT", [](FCfg& c) {
        auto& r = c.g.BlendState.RenderTarget[0];
        r.BlendEnable = FALSE, r.LogicOpEnable = TRUE, r.LogicOp = D3D12_LOGIC_OP_XOR, c.g.RTVFormats[0] = DXGI_FORMAT_R8G8B8A8_UINT;
    });
    V("blend:   ref for logic op: blend off, RT RGBA8_UINT", [](FCfg& c) { c.g.BlendState.RenderTarget[0].BlendEnable = FALSE, c.g.RTVFormats[0] = DXGI_FORMAT_R8G8B8A8_UINT; });

    V("rast: cull BACK->NONE", [](FCfg& c) { c.g.RasterizerState.CullMode = D3D12_CULL_MODE_NONE; });
    V("rast:   same + PS b", [](FCfg& c) { c.g.RasterizerState.CullMode = D3D12_CULL_MODE_NONE, c.ps = 1; });
    V("rast: cull BACK->FRONT", [](FCfg& c) { c.g.RasterizerState.CullMode = D3D12_CULL_MODE_FRONT; });
    V("rast: fill WIREFRAME", [](FCfg& c) { c.g.RasterizerState.FillMode = D3D12_FILL_MODE_WIREFRAME; });
    V("rast: FrontCounterClockwise", [](FCfg& c) { c.g.RasterizerState.FrontCounterClockwise = TRUE; });
    V("rast: depth bias 100 / slope 1.0", [](FCfg& c) { c.g.RasterizerState.DepthBias = 100, c.g.RasterizerState.SlopeScaledDepthBias = 1.f; });
    V("rast: conservative raster on", [](FCfg& c) { c.g.RasterizerState.ConservativeRaster = D3D12_CONSERVATIVE_RASTERIZATION_MODE_ON; });
    V("rast: depth clip off", [](FCfg& c) { c.g.RasterizerState.DepthClipEnable = FALSE; });
    V("rast: MultisampleEnable (1 sample)", [](FCfg& c) { c.g.RasterizerState.MultisampleEnable = TRUE; });

    V("ds: depth off", [](FCfg& c) { c.g.DepthStencilState.DepthEnable = FALSE; });
    V("ds: depth write ALL->ZERO", [](FCfg& c) { c.g.DepthStencilState.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ZERO; });
    V("ds: depth func LESS_EQUAL->GREATER", [](FCfg& c) { c.g.DepthStencilState.DepthFunc = D3D12_COMPARISON_FUNC_GREATER; });
    V("ds: stencil on", [](FCfg& c) { c.g.DepthStencilState.StencilEnable = TRUE; });
    V("ds: DSV D24S8->D32_FLOAT", [](FCfg& c) { c.g.DSVFormat = DXGI_FORMAT_D32_FLOAT; });
    V("ds: DSV D24S8->D32_FLOAT_S8X24", [](FCfg& c) { c.g.DSVFormat = DXGI_FORMAT_D32_FLOAT_S8X24_UINT; });
    V("ds: DSV D24S8->D16", [](FCfg& c) { c.g.DSVFormat = DXGI_FORMAT_D16_UNORM; });
    V("ds: DSV none (+ depth off)", [](FCfg& c) { c.g.DSVFormat = DXGI_FORMAT_UNKNOWN, c.g.DepthStencilState.DepthEnable = FALSE; });

    V("rt: RGBA8->RGBA16_FLOAT", [](FCfg& c) { c.g.RTVFormats[0] = DXGI_FORMAT_R16G16B16A16_FLOAT; });
    V("rt: RGBA8->R10G10B10A2_UNORM", [](FCfg& c) { c.g.RTVFormats[0] = DXGI_FORMAT_R10G10B10A2_UNORM; });
    V("rt: RGBA8->RGBA8_UNORM_SRGB", [](FCfg& c) { c.g.RTVFormats[0] = DXGI_FORMAT_R8G8B8A8_UNORM_SRGB; });
    V("rt: RGBA8->BGRA8_UNORM", [](FCfg& c) { c.g.RTVFormats[0] = DXGI_FORMAT_B8G8R8A8_UNORM; });
    V("rt: RGBA8->R11G11B10_FLOAT", [](FCfg& c) { c.g.RTVFormats[0] = DXGI_FORMAT_R11G11B10_FLOAT; });
    V("rt: RGBA8->R32_FLOAT", [](FCfg& c) { c.g.RTVFormats[0] = DXGI_FORMAT_R32_FLOAT; });
    V("rt: count 1->2 (+RGBA8, PS writes only 0)", [](FCfg& c) { c.g.NumRenderTargets = 2, c.g.RTVFormats[1] = DXGI_FORMAT_R8G8B8A8_UNORM; });
    V("rt: MSAA 1->4", [](FCfg& c) { c.g.SampleDesc = {4, 0}; });
    V("rt: SampleMask ~0->1 (same with 1 sample)", [](FCfg& c) { c.g.SampleMask = 1; });
    V("rt: SampleMask ~0->0", [](FCfg& c) { c.g.SampleMask = 0; });

    V("topo: TRIANGLE->LINE", [](FCfg& c) { c.g.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_LINE; });
    V("topo: TRIANGLE->POINT", [](FCfg& c) { c.g.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_POINT; });
    V("topo: IBStripCutValue 0xFFFF", [](FCfg& c) { c.g.IBStripCutValue = D3D12_INDEX_BUFFER_STRIP_CUT_VALUE_0xFFFF; });
    V("topo: IBStripCutValue 0xFFFFFFFF", [](FCfg& c) { c.g.IBStripCutValue = D3D12_INDEX_BUFFER_STRIP_CUT_VALUE_0xFFFFFFFF; });
    V("vi: same desc as a pipeline stream (1 view)", [](FCfg& c) { c.views = 1; });
    V("vi: view instancing, 2 views (SV_ViewID unread)", [](FCfg& c) { c.views = 2; });
    V("vi: view instancing, 4 views", [](FCfg& c) { c.views = 4; });

    for (int i = 1; i < RS_NGEN; ++i) V(("rs: " + std::string(kRsNames[i])).c_str(), [i](FCfg& c) { c.rs = i; });
    V("rs:   params swapped + PS b", [](FCfg& c) { c.rs = RS_ORDER, c.ps = 1; });
    static D3D12_SO_DECLARATION_ENTRY so_uv = {0, "TEXCOORD", 0, 0, 2, 0};
    static UINT so_stride = 8;
    V("so: TEXCOORD0 streamed out (RS with the SO flag)", [](FCfg& c) { c.rs = RS_SO, c.g.StreamOutput = {&so_uv, 1, &so_stride, 1, 0}; });

    // Probe 6b: reductions the planner relies on.
    // 1. An input element the VS declares but never reads (used mask 0): FREE like an absent one, or FULL like a read one?
    //    VS u = VS a + "float4 u1 : TEXCOORD1" (unread); its layout adds TEXCOORD1 at offset 24 (after COLOR).
    auto U = [&, vs_u](const char* name, std::function<void(FCfg&)> mod) {
        V(name, [vs_u, mod](FCfg& c) {
            c.vs = vs_u, c.il.push_back({"TEXCOORD", 1, DXGI_FORMAT_R32G32B32A32_FLOAT, 0, 24, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0});
            mod(c);
        });
    };
    U("setup: VS u (TEXCOORD1 declared, unread) + PS a (cold VS)", [](FCfg&) {});
    U("il1: TEXCOORD1 (mask 0) fmt RGBA32F->RGBA8_UNORM", [](FCfg& c) { c.il[3].Format = DXGI_FORMAT_R8G8B8A8_UNORM; });
    U("il1: TEXCOORD1 (mask 0) fmt RGBA32F->R16G16_FLOAT", [](FCfg& c) { c.il[3].Format = DXGI_FORMAT_R16G16_FLOAT; });
    U("il1: TEXCOORD1 (mask 0) offset 24->40", [](FCfg& c) { c.il[3].AlignedByteOffset = 40; });
    U("il1: TEXCOORD1 (mask 0) slot 0->1", [](FCfg& c) { c.il[3].InputSlot = 1; });
    U("il1: TEXCOORD1 (mask 0) slot 1 per-instance", [](FCfg& c) { c.il[3].InputSlot = 1, c.il[3].InputSlotClass = D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, c.il[3].InstanceDataStepRate = 1; });
    U("il1: TEXCOORD1 (mask 0) fmt+offset+slot together", [](FCfg& c) { c.il[3].Format = DXGI_FORMAT_R16G16B16A16_SNORM, c.il[3].AlignedByteOffset = 8, c.il[3].InputSlot = 2; });
    U("il1: TEXCOORD1 (mask 0) removed from layout", [](FCfg& c) { c.il.pop_back(); });
    U("il1:   ctl: COLOR (undeclared) fmt ->R32_FLOAT", [](FCfg& c) { c.il[2].Format = DXGI_FORMAT_R32_FLOAT; });
    U("il1:   ctl: TEXCOORD0 (read) fmt ->R16G16_UNORM", [](FCfg& c) { c.il[1].Format = DXGI_FORMAT_R16G16_UNORM; });

    // 2. Root-signature changes the VS can't see. Class row: VS b + PS b (both cached under the variant's base RS).
    //    Attribution, before VS a / PS a ever met that RS: "VS a + fresh PS" costs a cold PS (~setup row) if the VS is
    //    reused, + a VS recompile if not; "fresh VS + PS a" likewise for the PS. RS_VIS first (known FULL) as the reference;
    //    its attribution uses VS b / PS b, since VS a + PS a already met RS_VIS in the rs rows.
    V("rs2: ref t0 ALL->PIXEL: VS b + fresh PS", [p = addps()](FCfg& c) { c.rs = RS_VIS, c.vs = 1, c.ps = p; });
    V("rs2: ref t0 ALL->PIXEL: fresh VS + PS b", [v = addvs()](FCfg& c) { c.rs = RS_VIS, c.vs = v, c.ps = 1; });
    V("setup: RS t0 PIXEL with VS b + PS b", [](FCfg& c) { c.rs = RS_VIS, c.vs = c.ps = 1; });
    for (int i = RS_NGEN; i < RS_COUNT; ++i) {
        std::string n = std::string("rs2: ") + (i == RS_T0N2 ? "" : "[t0 PIXEL] ") + kRsNames[i];
        V((n + " (VS b+PS b)").c_str(), [i](FCfg& c) { c.rs = i, c.vs = c.ps = 1; });
        V("rs2:   same RS: VS a + fresh PS", [i, p = addps()](FCfg& c) { c.rs = i, c.ps = p; });
        rows.back().name += " #" + std::to_string(i);
        V("rs2:   same RS: fresh VS + PS a", [i, v = addvs()](FCfg& c) { c.rs = i, c.vs = v; });
        rows.back().name += " #" + std::to_string(i);
    }

    // 3. VS-only PSO (no PS, 0 RT, DSV D24S8: a depth pre-pass) after VS a + PS a with the same VS/layout/RS, and the
    //    reverse: a fresh VS first VS-only, then with PS a. 5. depth/DSV fields around it, and around a PS writing SV_Depth.
    auto depth_only = [](FCfg& c) { c.ps = -1, c.g.NumRenderTargets = 0, c.g.RTVFormats[0] = DXGI_FORMAT_UNKNOWN; };
    V("vsonly: VS a, no PS, 0 RT (after VS a+PS a)", depth_only);
    V("vsonly:   same, depth write ALL->ZERO", [=](FCfg& c) { depth_only(c), c.g.DepthStencilState.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ZERO; });
    V("vsonly:   same, depth func ->GREATER", [=](FCfg& c) { depth_only(c), c.g.DepthStencilState.DepthFunc = D3D12_COMPARISON_FUNC_GREATER; });
    V("vsonly:   same, DSV D24S8->D32_FLOAT", [=](FCfg& c) { depth_only(c), c.g.DSVFormat = DXGI_FORMAT_D32_FLOAT; });
    V("vsonly:   same, DSV D24S8->D16", [=](FCfg& c) { depth_only(c), c.g.DSVFormat = DXGI_FORMAT_D16_UNORM; });
    V("vsonly:   same, cull BACK->NONE", [=](FCfg& c) { depth_only(c), c.g.RasterizerState.CullMode = D3D12_CULL_MODE_NONE; });
    {
        int v = addvs();
        V("ctl: vsonly rev: fresh VS c alone, no PS (cold VS)", [=](FCfg& c) { depth_only(c), c.vs = v; });
        V("vsonly rev: VS c + PS a (VS c only seen VS-only)", [=](FCfg& c) { c.vs = v; });
    }
    auto zps = [ps_z](FCfg& c) { c.ps = ps_z, c.g.NumRenderTargets = 0, c.g.RTVFormats[0] = DXGI_FORMAT_UNKNOWN; };
    V("setup: VS a + PS z (writes SV_Depth, 0 RT; cold PS)", zps);
    V("zps: depth write ALL->ZERO", [=](FCfg& c) { zps(c), c.g.DepthStencilState.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ZERO; });
    V("zps: depth func ->GREATER", [=](FCfg& c) { zps(c), c.g.DepthStencilState.DepthFunc = D3D12_COMPARISON_FUNC_GREATER; });
    V("zps: depth off", [=](FCfg& c) { zps(c), c.g.DepthStencilState.DepthEnable = FALSE; });
    V("zps: DSV D24S8->D32_FLOAT", [=](FCfg& c) { zps(c), c.g.DSVFormat = DXGI_FORMAT_D32_FLOAT; });
    V("zps: DSV D24S8->D16", [=](FCfg& c) { zps(c), c.g.DSVFormat = DXGI_FORMAT_D16_UNORM; });
    V("zps: DSV D24S8->D32_FLOAT_S8X24", [=](FCfg& c) { zps(c), c.g.DSVFormat = DXGI_FORMAT_D32_FLOAT_S8X24_UINT; });

    // 4. Export class: each format with its own PS (float4 out), cached with RGBA8 first, so earlier rt rows can't have
    //    compiled that export already. RGBA16F (CHEAP) and R32_FLOAT (FULL) repeat the rt rows as references.
    for (DXGI_FORMAT f : {DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT_R32_FLOAT, DXGI_FORMAT_R16G16_FLOAT, DXGI_FORMAT_R16G16B16A16_UNORM,
                          DXGI_FORMAT_R8_UNORM, DXGI_FORMAT_R16_FLOAT, DXGI_FORMAT_R8G8_UNORM, DXGI_FORMAT_R32G32_FLOAT}) {
        static const std::map<DXGI_FORMAT, const char*> fn = {
            {DXGI_FORMAT_R16G16B16A16_FLOAT, "RGBA16_FLOAT (ref)"}, {DXGI_FORMAT_R32_FLOAT, "R32_FLOAT (ref)"},
            {DXGI_FORMAT_R16G16_FLOAT, "R16G16_FLOAT"}, {DXGI_FORMAT_R16G16B16A16_UNORM, "R16G16B16A16_UNORM"},
            {DXGI_FORMAT_R8_UNORM, "R8_UNORM"}, {DXGI_FORMAT_R16_FLOAT, "R16_FLOAT"}, {DXGI_FORMAT_R8G8_UNORM, "R8G8_UNORM"},
            {DXGI_FORMAT_R32G32_FLOAT, "R32G32_FLOAT"}};
        int p = addps();
        V((std::string("setup: rt2 own PS with RGBA8 (cold PS) for ") + fn.at(f)).c_str(), [p](FCfg& c) { c.ps = p; });
        V((std::string("rt2: RGBA8->") + fn.at(f) + " (own PS)").c_str(), [p, f](FCfg& c) { c.ps = p, c.g.RTVFormats[0] = f; });
    }

    // 6. What the PS makes of the VS's outputs (FF7 session 2: a VS compiled only behind a PS whose color output had no
    //    render target recompiled in full behind the same PS with one). Each pair: a fresh VS first behind PS a in the
    //    setup state, then behind PS a with the baseline's RT (the "pair" row is the live-to-live reference).
    {
        int d = addvs(), e = addvs(), h = addvs();
        V("setup: deadps: fresh VS d + PS a, 0 RT (color output unbound)", [=](FCfg& c) { c.vs = d, c.g.NumRenderTargets = 0, c.g.RTVFormats[0] = DXGI_FORMAT_UNKNOWN; });
        V("deadps: VS d + PS a, 1 RT (VS d only seen with no RT)", [=](FCfg& c) { c.vs = d; });
        V("setup: deadps: fresh VS e + PS a, RT0 write mask 0", [=](FCfg& c) { c.vs = e, c.g.BlendState.RenderTarget[0].RenderTargetWriteMask = 0; });
        V("deadps: VS e + PS a, write mask ALL (VS e only seen masked)", [=](FCfg& c) { c.vs = e; });
        // a PS that reads only TEXCOORD0.x (used mask x): does the VS compile for its partner's used components?
        int px = addps("cbuffer C : register(b0) { float4 kc; };"
                       "float4 main(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return uv.x * ");
        V("setup: deadps: fresh VS h + PS x (reads TEXCOORD0.x only)", [=](FCfg& c) { c.vs = h, c.ps = px; });
        V("deadps: VS h + PS a (reads .xy; VS h only seen with PS x)", [=](FCfg& c) { c.vs = h; });
    }

    rows.push_back({"cs ctl: first create (cold CS)", [&] { return cs(RS_BASE); }});
    rows.push_back({"cs ctl: re-create", [&] { return cs(RS_BASE); }});
    for (int i = 1; i < RS_NGEN; ++i) rows.push_back({"cs rs: " + std::string(kRsNames[i]), [&, i] { return cs(i); }});

    if (proc == 4) {  // probe 3's situation: a fresh process whose first compile is a never-seen variant of cached shaders
        rows.clear();
        V("fresh proc: new layout (POSITION RGBA16_SNORM) first", [](FCfg& c) { c.il[0].Format = DXGI_FORMAT_R16G16B16A16_SNORM; });
        V("fresh proc:   then another new layout (RGBA16_UNORM)", [](FCfg& c) { c.il[0].Format = DXGI_FORMAT_R16G16B16A16_UNORM; });
        V("fresh proc:   then new blend (DEST_COLOR/ZERO)", [](FCfg& c) {
            c.g.BlendState.RenderTarget[0].SrcBlend = D3D12_BLEND_DEST_COLOR, c.g.BlendState.RenderTarget[0].DestBlend = D3D12_BLEND_ZERO;
        });
    }
    FILE* f = _wfopen(out.c_str(), L"w");
    CHECK(f);
    for (auto& r : rows) {
        double a = r.f();
        if (proc == 1) fprintf(f, "%s\t%.3f\t%.3f\n", r.name.c_str(), a, r.f());
        else fprintf(f, "%s\t%.3f\n", r.name.c_str(), a);
    }
    fclose(f);
    return 0;
}

static int run(const std::wstring& exe, const std::wstring& args) {
    fflush(stdout);
    std::wstring cmd = L"\"" + exe + L"\" " + args;
    STARTUPINFOW si = {sizeof si};
    PROCESS_INFORMATION pi;
    if (!CreateProcessW(nullptr, cmd.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi)) return 1;
    WaitForSingleObject(pi.hProcess, INFINITE);
    DWORD code = 1;
    GetExitCodeProcess(pi.hProcess, &code);
    CloseHandle(pi.hProcess), CloseHandle(pi.hThread);
    return (int)code;
}

// Columns: p1 = first seen (proc 1), p1b = again in proc 1, p2 = proc 2 (same exe name, same folder), od = a process
// with the same exe file name in another folder (runs between proc 1 and proc 2). Rows are medians when runs > 1.
// fa, fb: that exe in dir and in the other folder (fields_main).
static int fields_parent(const std::wstring& fa, const std::wstring& fb, const std::wstring& dir, int runs) {
    std::vector<std::string> order;
    Col col[4];  // p1, p1b, od, p2
    auto read = [&](const std::wstring& path, int c0, bool two) {
        read_rows(path, &order, [&](const std::string& name, const char* rest) {
            char* t2 = nullptr;
            col[c0][name].push_back(strtod(rest, &t2));
            if (two) col[c0 + 1][name].push_back(strtod(t2, nullptr));
        });
    };
    std::random_device rd;
    for (int r = 0; r < runs; ++r) {
        unsigned seed = 100000 + rd() % 8000000;  // < 2^24: the float constant stays exact
        std::wstring s = L" " + std::to_wstring(seed) + L" ";
        printf("probe 6 run %d/%d\n", r + 1, runs);
        run(fa, L"fields1" + s + L"\"" + dir + L"fields1.txt\"");
        run(fb, L"fields3" + s + L"\"" + dir + L"fields3.txt\"");
        run(fa, L"fields2" + s + L"\"" + dir + L"fields2.txt\"");
        run(fa, L"fields4" + s + L"\"" + dir + L"fields4.txt\"");
        read(dir + L"fields1.txt", 0, true), read(dir + L"fields3.txt", 2, false), read(dir + L"fields2.txt", 3, false);
        read(dir + L"fields4.txt", 0, false);
    }
    auto med = [&](int c, const std::string& n) {
        double x = col_median(col[c], n);
        char b[32];
        snprintf(b, sizeof b, x == -2 ? "      -" : x < 0 ? "   fail" : "%7.2f", x);
        return std::string(b);
    };
    // Classes from the first-seen time in a process whose compiler is already warm: FREE = like a re-create (the driver
    // maps it to a pipeline it has), CHEAP = a new pipeline linked from cached stage binaries, FULL = a stage recompiles
    // (costs about what a cold VS or PS alone costs here). "fresh proc" rows: first compile in a new process (probe 3).
    printf("probe 6: one PSO field changed around cached shaders; ms, median of %d run(s). FREE <0.25, CHEAP <1.2, FULL >=1.2\n", runs);
    printf("%-54s %7s %7s %7s %7s  %s\n", "variant", "p1", "p1again", "othrdir", "p2", "class");
    for (auto& n : order) {
        std::string cls;
        const double x = col_median(col[0], n), p2 = col_median(col[3], n);
        if (n.rfind("ctl", 0) && n.rfind("cs ctl", 0) && n.rfind("setup", 0) && n.rfind("fresh", 0) && x != -2) {
            auto c = [](double y) { return y < 0 ? "fail" : y < 0.25 ? "FREE" : y < 1.2 ? "CHEAP" : "FULL"; };
            cls = c(x);
            if (x >= 0.25 && p2 != -2) cls += p2 < 0.25 ? ", cached in p2" : ", NOT cached in p2";
            if (n.find("fresh") != std::string::npos) cls = "attribution: ~cold stage = other stage reused";
            // p1 is setup; the pair is new in od and in p2. od's first loads cost more even for exact hits (compare its
            // setup rows), so p2 decides: FREE/CHEAP = per-stage cache across processes, FULL = per pair.
            if (!n.rfind("xpair", 0) && p2 != -2) cls = std::string("p2 ") + c(p2) + " (od: compare its setup rows)";
        }
        printf("%-54s %s %s %s %s  %s\n", n.c_str(), med(0, n).c_str(), med(1, n).c_str(), med(2, n).c_str(), med(3, n).c_str(), cls.c_str());
    }
    return 0;
}

// Probe 7 (`selftest dxr`): ray tracing state objects in the driver's disk cache.
// Per run (fresh shader constants), four processes in order; X and Y are fresh exe names per invocation:
//   p1 = X, "the warm": every row's base objects, cold.   p2 = X, "the game": each row's variant, first seen (its base is
//   on disk from p1), then again in-process.   p3 = X: the variants again (were they written to disk?).
//   p4 = Y (another exe name): the rows p1 made too (is the cache per exe name?).   p5 = X from another folder (the
//   staged warm's situation: is the cache path-independent, D3DSCache's per-path folder included?).
// Each process starts with its own fresh warm-up object (compiler start-up). Every shader is its own DXIL library
// (lib_6_3, one export), as Unreal builds them; row r's shader constants are seed + r * 1000 + i, so rows share nothing.
// After each first create, timed apart: one shader identifier fetch ("id"), then a first use ("disp": SetPipelineState1 +
// a zero-size DispatchRays, executed and waited for). Either would show a compile a driver defers past the create.
// Vendor-neutral: the system d3d12.dll, the default adapter, no proxy.
struct DxrFn { char kind; std::string name; unsigned k; };  // G raygen, M miss, C/P closest hit (triangle/procedural), A any hit, I intersection
struct DxrHg { std::string name, ch, ah, is; };
struct DxrObj {
    std::string handle;              // live for the rest of the process under this name (collections, AddToStateObject bases)
    bool collection = false, additions = false, rename = false;  // rename: every export renamed via ExportToRename (+ "_r")
    std::vector<std::string> libs;   // library ids
    std::string rg;                  // raygen export, for the dispatch
    std::vector<DxrHg> hgs;
    std::vector<std::string> colls;  // existing collections (handles) linked in
    int grs = 0, lrs = 0;            // root signature variants
    UINT payload = 16, attr = 8, depth = 1;
};
struct DxrOp {
    std::string label;
    int mask;  // bit p-1 = process p
    std::vector<DxrObj> objs;
    std::string add_to;
    int threads = 1;  // > 1: objs created in parallel, the row is the wall time
};
struct DxrPlan { std::map<std::string, std::vector<DxrFn>> libs; std::vector<DxrOp> ops; };

static DxrPlan dxr_plan(unsigned seed) {
    DxrPlan P;
    enum { P1 = 1, P2 = 2, P3 = 4, P4 = 8, P5 = 16, P6 = 32 };  // P6: the recorder rows (dxrchild 6, through the proxy)
    auto fn = [&](const std::string& id, char kind, unsigned k) { P.libs[id] = {{kind, id, k}}; return id; };
    auto relib = [](DxrObj& o) {  // libraries = raygen + miss (the first two) + what the hit groups use
        std::vector<std::string> l(o.libs.begin(), o.libs.begin() + std::min<size_t>(2, o.libs.size()));
        for (auto& h : o.hgs)
            for (auto* s : {&h.ch, &h.ah, &h.is})
                if (!s->empty() && std::find(l.begin(), l.end(), *s) == l.end()) l.push_back(*s);
        o.libs = l;
    };
    // base shape: raygen + miss + nhg hit groups cycling triangle CH, triangle CH + AH, procedural IS + CH
    auto base = [&](int r, int nhg = 3) {
        std::string p = "r" + std::to_string(r) + "_";
        unsigned k = seed + r * 1000;
        DxrObj o;
        o.handle = p + "so", o.rg = p + "rg";
        o.libs = {fn(p + "rg", 'G', k), fn(p + "ms", 'M', k + 1)};
        for (int i = 0; i < nhg; ++i) {
            std::string n = std::to_string(i);
            DxrHg h{p + "hg" + n, fn(p + "ch" + n, i % 3 == 2 ? 'P' : 'C', k + 10 + i)};
            if (i % 3 == 1) h.ah = fn(p + "ah" + n, 'A', k + 300 + i);
            if (i % 3 == 2) h.is = fn(p + "is" + n, 'I', k + 600 + i);
            o.hgs.push_back(h);
        }
        relib(o);
        return o;
    };
    // one self-contained collection (root signatures + configs inside) per raygen, miss and hit group; then a link
    auto colls = [&](const DxrObj& b) {
        std::vector<DxrObj> v;
        for (int i = 0; i < 2; ++i) {
            DxrObj c = b;
            c.collection = true, c.additions = false, c.hgs.clear(), c.libs = {b.libs[i]}, c.handle = b.handle + "_c" + std::to_string(i);
            v.push_back(c);
        }
        for (auto& h : b.hgs) {
            DxrObj c = b;
            c.collection = true, c.additions = false, c.hgs = {h}, c.libs.clear(), c.handle = h.name + "_c";
            for (auto* s : {&h.ch, &h.ah, &h.is}) if (!s->empty()) c.libs.push_back(*s);
            v.push_back(c);
        }
        return v;
    };
    auto link = [](const DxrObj& b, const std::vector<DxrObj>& cs) {
        DxrObj o = b;
        o.libs.clear(), o.hgs.clear(), o.handle = b.handle + "_link";
        for (auto& c : cs) o.colls.push_back(c.handle);
        return o;
    };
    auto op = [&](const std::string& label, int mask, std::vector<DxrObj> objs, const std::string& add_to = "", int threads = 1) {
        P.ops.push_back({label, mask, std::move(objs), add_to, threads});
    };
    for (int p = 0; p < 6; ++p) op("ctl: warm-up (fresh, compiler start-up)", 1 << p, {base(90 + p, 1)});

    // Q1 + Q4: the same object in every process (p4: another exe name, p5: the same exe name in another folder)
    op("same object (p1 cold, p2/p3 same exe, p4 other exe)", P1 | P2 | P3 | P4 | P5, {base(1)});
    op("size: small, RG + MS + 1 HG", P1 | P2 | P3 | P4 | P5, {base(2, 1)});
    op("size: large, RG + MS + 64 HG", P1 | P2 | P3 | P4 | P5, {base(3, 64)});
    op("ctl: cold base shape, fresh (p2)", P2, {base(4)});
    op("ctl: cold RG + MS + 1 HG, fresh (p2)", P2, {base(5, 1)});

    // Q2: the base in p1 (setup row), one thing changed in p2 / p3
    int r = 10;
    auto var = [&](const std::string& label, std::function<void(DxrObj&, const std::string&, unsigned)> mod) {
        DxrObj b = base(r), v = b;
        op("setup: " + label, P1, {b});
        mod(v, "r" + std::to_string(r) + "_", seed + r * 1000);
        v.handle += "_v";
        op(label, P2 | P3, {v});
        ++r;
    };
    var("hg: subset (1 of the 3 hit groups)", [&](DxrObj& v, auto&, unsigned) { v.hgs.resize(1), relib(v); });
    var("hg: + 1 hit group, fresh CH", [&](DxrObj& v, const std::string& p, unsigned k) {
        v.hgs.push_back({p + "hgN", fn(p + "chN", 'C', k + 99)}), relib(v);
    });
    // attribution: if RG + MS recompile for another shader set, fresh RG + MS cost about what the two rows above cost
    var("hg: subset, fresh RG + MS", [&](DxrObj& v, const std::string& p, unsigned k) {
        v.hgs.resize(1), v.libs[0] = v.rg = fn(p + "rg3", 'G', k + 95), v.libs[1] = fn(p + "ms3", 'M', k + 96), relib(v);
    });
    var("hg: + 1 hit group, fresh CH, fresh RG + MS", [&](DxrObj& v, const std::string& p, unsigned k) {
        v.hgs.push_back({p + "hgN", fn(p + "chN", 'C', k + 99)}), v.libs[0] = v.rg = fn(p + "rg3", 'G', k + 95), v.libs[1] = fn(p + "ms3", 'M', k + 96), relib(v);
    });
    var("hg: + 1 hit group of cached CH0 + AH1", [](DxrObj& v, const std::string& p, unsigned) {
        v.hgs.push_back({p + "hgX", v.hgs[0].ch, v.hgs[1].ah});
    });
    var("hg: hit group names changed", [](DxrObj& v, auto&, unsigned) { for (auto& h : v.hgs) h.name += "_n"; });
    var("lib: exports renamed (ExportToRename)", [](DxrObj& v, auto&, unsigned) {
        v.rename = true, v.rg += "_r";
        for (auto& h : v.hgs)
            for (auto* s : {&h.ch, &h.ah, &h.is}) if (!s->empty()) *s += "_r";
    });
    var("lib: the same functions in one library", [&](DxrObj& v, const std::string& p, unsigned) {
        auto& all = P.libs[p + "all"];
        for (auto& l : v.libs) all.push_back(P.libs[l][0]);
        v.libs = {p + "all"};
    });
    var("lib: new raygen, same miss + hit groups", [&](DxrObj& v, const std::string& p, unsigned k) { v.libs[0] = v.rg = fn(p + "rg2", 'G', k + 98); });
    var("rs: global + unused root constant", [](DxrObj& v, auto&, unsigned) { v.grs = 1; });
    var("rs: local + unused root constant", [](DxrObj& v, auto&, unsigned) { v.lrs = 1; });
    var("cfg: MaxTraceRecursionDepth 1->2", [](DxrObj& v, auto&, unsigned) { v.depth = 2; });
    var("cfg: MaxPayloadSizeInBytes 16->32", [](DxrObj& v, auto&, unsigned) { v.payload = 32; });
    var("cfg: MaxAttributeSizeInBytes 8->16", [](DxrObj& v, auto&, unsigned) { v.attr = 16; });

    // Q2, collections: per-shader collections are the unit a warm could compile without knowing the game's pipelines
    {
        DxrObj b = base(30);
        auto cs = colls(b);
        op("coll A: 5 collections (p1 cold)", P1 | P2 | P3, cs);
        op("coll A:   link them (p2: never linked before)", P2 | P3, {link(b, cs)});
    }
    {
        DxrObj b = base(31);
        auto cs = colls(b);
        op("setup: coll B: flat pipeline", P1, {b});
        op("coll B: 5 collections of a cached flat pipeline", P2 | P3, cs);
        op("coll B:   link them", P2 | P3, {link(b, cs)});
    }
    {
        DxrObj b = base(32);
        op("setup: coll C: 5 collections", P1, colls(b));
        op("coll C: flat pipeline of cached collections", P2 | P3, {b});
    }
    {
        DxrObj b = base(33);
        auto cs = colls(b);
        op("ctl: coll D fresh: 5 collections (p2)", P2, cs);
        op("ctl: coll D:   link them (p2)", P2, {link(b, cs)});
    }

    // Q4: does the driver compile state objects in parallel (the warm's throughput)? Cold in p1, cached in p2.
    op("par: 26 collections, 1 thread", P1 | P2, colls(base(50, 24)));
    op("par: 26 collections, 8 threads", P1 | P2, colls(base(51, 24)), "", 8);

    // Q3: AddToStateObject. S = the base with additions allowed, A = one more hit group (fresh CH)
    auto add_pair = [&](int r) {
        DxrObj s = base(r), a;
        s.additions = true;
        std::string p = "r" + std::to_string(r) + "_";
        a.additions = true, a.handle = p + "add", a.rg = s.rg, a.hgs = {{p + "hgA", fn(p + "chA", 'C', seed + r * 1000 + 97)}}, a.libs = {p + "chA"};
        return std::pair{s, a};
    };
    {
        auto [s, a] = add_pair(40);
        op("add A: base S", P1 | P2 | P3, {s});
        op("add A:   Add(+1 HG), p1 did the same", P1 | P2 | P3, {a}, s.handle);
    }
    {
        auto [s, a] = add_pair(41);
        DxrObj sa = s;
        sa.hgs.push_back(a.hgs[0]), relib(sa), sa.handle += "_flat";
        op("setup: add B: flat S+A", P1, {sa});
        op("add B: base S (a flat S+A cached)", P2 | P3, {s});
        op("add B:   Add(+1 HG)", P2 | P3, {a}, s.handle);
    }
    {
        auto [s, a] = add_pair(42);
        DxrObj c = a;
        c.collection = true, c.additions = false, c.handle += "_c";
        op("setup: add C: A's hit group as a collection", P1, {c});
        op("add C: base S", P2 | P3, {s});
        op("add C:   Add(+1 HG) (its collection cached)", P2 | P3, {a}, s.handle);
    }
    {
        auto [s, a] = add_pair(43);
        op("ctl: add D fresh: base S (p2)", P2, {s});
        op("ctl: add D:   Add(+1 HG) (p2)", P2, {a}, s.handle);
    }

    // Recorder rows (process 6 only, created through the proxy): one of each kind the 'R' / 'A' records carry
    op("rec: flat pipeline", P6, {base(60)});
    {
        DxrObj b = base(61);
        auto cs = colls(b);
        op("rec: 5 collections", P6, cs);
        op("rec: link them", P6, {link(b, cs)});
    }
    {
        auto [s, a] = add_pair(62);
        op("rec: base S", P6, {s});
        op("rec: Add(+1 HG)", P6, {a}, s.handle);
    }
    {
        DxrObj v = base(63);
        v.rename = true, v.rg += "_r";
        for (auto& h : v.hgs)
            for (auto* x : {&h.ch, &h.ah, &h.is}) if (!x->empty()) *x += "_r";
        op("rec: exports renamed", P6, {v});
    }
    return P;
}

static std::string dxr_src(const std::vector<DxrFn>& fns) {
    std::string s = "RaytracingAccelerationStructure scene : register(t0); RWTexture2D<float4> outp : register(u0);\n"
                    "cbuffer L : register(b0, space1) { float4 lk; };\nstruct P { float4 c; };\nstruct Attr { float2 uv; };\n";
    for (auto& f : fns) {
        std::string k = std::to_string(f.k) + ".0", n = f.name;
        switch (f.kind) {
        case 'G':
            s += "[shader(\"raygeneration\")] void " + n + "() { RayDesc r; r.Origin = float3(DispatchRaysIndex().xy, " + k +
                 "); r.Direction = float3(0, 0, 1); r.TMin = 0; r.TMax = 1e4; P p; p.c = 0;"
                 " TraceRay(scene, 0, 0xFF, 0, 1, 0, r, p); outp[DispatchRaysIndex().xy] = p.c; }\n";
            break;
        case 'M': s += "[shader(\"miss\")] void " + n + "(inout P p) { p.c = float4(" + k + ", 0, 0, 1); }\n"; break;
        case 'C': case 'P':  // some ALU so a compile costs something, reading the local root signature's constants
            s += "[shader(\"closesthit\")] void " + n + "(inout P p, in " +
                 (f.kind == 'C' ? "BuiltInTriangleIntersectionAttributes a) { float3 x = float3(a.barycentrics, " : "Attr a) { float3 x = float3(a.uv, ") + k +
                 "); [unroll] for (int i = 0; i < 24; ++i) x = sin(x * 1.37 + float3(i, " + k + ", x.y)) * cos(x.zxy + " + k +
                 "); p.c = float4(x, 1) * lk; }\n";
            break;
        case 'A':
            s += "[shader(\"anyhit\")] void " + n + "(inout P p, in BuiltInTriangleIntersectionAttributes a) { if (a.barycentrics.x * " + k +
                 " > 0.5) IgnoreHit(); }\n";
            break;
        case 'I':
            s += "[shader(\"intersection\")] void " + n + "() { Attr a; a.uv = float2(frac(" + k + " * 0.001), 0.5); ReportHit(RayTCurrent() * 0.5, 0, a); }\n";
            break;
        }
    }
    return s;
}

// The folder of dxcompiler.dll + dxil.dll: next to the exe, else SELFTEST_DXC, else the newest Windows SDK's bin\<ver>\x64.
// A probe parent sets SELFTEST_DXC to it for its children: they run from their run folders, away from the exe's.
static std::wstring dxc_dir() {
    namespace fs = std::filesystem;
    wchar_t v[MAX_PATH];
    GetModuleFileNameW(nullptr, v, MAX_PATH);
    std::error_code ec;
    if (fs::path exe = fs::path(v).parent_path(); fs::exists(exe / L"dxcompiler.dll", ec)) return exe.wstring();
    if (GetEnvironmentVariableW(L"SELFTEST_DXC", v, MAX_PATH)) {  // absolute, and only if it has one: no other DXC instead
        wchar_t full[MAX_PATH];
        DWORD n = GetFullPathNameW(v, MAX_PATH, full, nullptr);
        if (n && n < MAX_PATH && fs::exists(fs::path(full) / L"dxcompiler.dll", ec)) return full;
        return printf("  SELFTEST_DXC=%ls has no dxcompiler.dll\n", v), L"";
    }
    std::vector<fs::path> vers;
    for (auto& e : fs::directory_iterator(L"C:\\Program Files (x86)\\Windows Kits\\10\\bin", ec))
        if (fs::exists(e.path() / L"x64" / L"dxcompiler.dll", ec) && fs::exists(e.path() / L"x64" / L"dxil.dll", ec)) vers.push_back(e.path() / L"x64");
    std::sort(vers.begin(), vers.end());
    return vers.empty() ? L"" : vers.back().wstring();
}

// dxcompiler.dll from dxc_dir() only, never another one on the search path; dxil.dll (signing) is loaded by dxcompiler
// from the DLL directory.
static HMODULE dxc_load() {
    std::wstring dir = dxc_dir();
    if (!dir.empty()) SetDllDirectoryW(dir.c_str());
    HMODULE m = dir.empty() ? nullptr : LoadLibraryExW((dir + L"\\dxcompiler.dll").c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    wchar_t p[MAX_PATH] = L"";
    if (m) GetModuleFileNameW(m, p, MAX_PATH);
    printf(m ? "  dxcompiler: %ls\n" : "  dxcompiler.dll not found (set SELFTEST_DXC to a folder with dxcompiler.dll + dxil.dll)\n", p);
    return m;
}

static bool dxc_compile(IDxcCompiler3* c, const std::string& src, std::string& out, LPCWSTR target = L"lib_6_3") {
    DxcBuffer b = {src.data(), src.size(), DXC_CP_UTF8};
    LPCWSTR args[] = {L"-T", target};
    IDxcResult* r = nullptr;
    HRESULT st = E_FAIL;
    if (FAILED(c->Compile(&b, args, 2, nullptr, IID_PPV_ARGS(&r))) || FAILED(r->GetStatus(&st)) || FAILED(st)) {
        IDxcBlobUtf8* e = nullptr;
        if (r && SUCCEEDED(r->GetOutput(DXC_OUT_ERRORS, IID_PPV_ARGS(&e), nullptr)) && e) printf("%s\n%s\n", src.c_str(), e->GetStringPointer());
        return false;
    }
    IDxcBlob* o = nullptr;
    r->GetOutput(DXC_OUT_OBJECT, IID_PPV_ARGS(&o), nullptr);
    out.assign((const char*)o->GetBufferPointer(), o->GetBufferSize());
    o->Release(), r->Release();
    static const char zero[16] = {};
    if (out.size() < 20 || !memcmp(out.data() + 4, zero, 16)) return printf("DXIL not signed: dxil.dll missing next to dxcompiler.dll\n"), false;
    return true;
}

static int dxr_child(int proc, unsigned seed, const std::wstring& blobs, const std::wstring& out) {
    std::wstring self(MAX_PATH, L'\0');
    self.resize(GetModuleFileNameW(nullptr, self.data(), MAX_PATH));
    // process 6 records: the proxy next to the exe (its mode from SCSKILLER_MODE / scskiller.ini); the probe: the real runtime
    HMODULE m = proc == 6 ? LoadLibraryW((self.substr(0, self.find_last_of(L'\\') + 1) + L"d3d12.dll").c_str()) : load_system(L"d3d12.dll");
    CHECK(m);
    auto create_device = (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice");
    auto ser = (PFN_D3D12_SERIALIZE_VERSIONED_ROOT_SIGNATURE)GetProcAddress(m, "D3D12SerializeVersionedRootSignature");
    CHECK(create_device && ser);
    bool debug = GetEnvironmentVariableW(L"SELFTEST_DXR_DEBUG", nullptr, 0) > 0;  // state object errors are opaque E_INVALIDARGs otherwise
    if (debug) {
        auto get_debug = (decltype(&D3D12GetDebugInterface))GetProcAddress(m, "D3D12GetDebugInterface");
        ID3D12Debug* dbg = nullptr;
        if (get_debug && SUCCEEDED(get_debug(IID_PPV_ARGS(&dbg)))) dbg->EnableDebugLayer();
    }
    ID3D12Device* dev = nullptr;
    IDXGIAdapter* adapter = nullptr;  // SELFTEST_WARP=1: WARP (the runtime's checks, no GPU driver or cache)
    if (GetEnvironmentVariableW(L"SELFTEST_WARP", nullptr, 0)) {
        IDXGIFactory4* f = nullptr;
        CHECK(SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumWarpAdapter(IID_PPV_ARGS(&adapter))));
    }
    CHECK(SUCCEEDED(create_device(adapter, D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&dev))));
    D3D12_FEATURE_DATA_D3D12_OPTIONS5 o5 = {};
    dev->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS5, &o5, sizeof o5);
    if (proc == 1) {
        DXGI_ADAPTER_DESC1 d;
        LARGE_INTEGER umd = {};
        if (IDXGIAdapter1* a = adapter_of(dev, d)) {
            a->CheckInterfaceSupport(__uuidof(IDXGIDevice), &umd);
            printf("  adapter: %ls, driver %u.%u.%u.%u, raytracing tier %d, seed %u\n", d.Description, HIWORD(umd.HighPart), LOWORD(umd.HighPart),
                   HIWORD(umd.LowPart), LOWORD(umd.LowPart), (int)o5.RaytracingTier, seed);
        }
    }
    if (o5.RaytracingTier < D3D12_RAYTRACING_TIER_1_0) return printf("  no DXR on this adapter\n"), 1;
    ID3D12Device5* dev5 = nullptr;
    ID3D12Device7* dev7 = nullptr;  // AddToStateObject: tier 1.1
    CHECK(SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&dev5))));
    if (o5.RaytracingTier >= D3D12_RAYTRACING_TIER_1_1) dev->QueryInterface(IID_PPV_ARGS(&dev7));
    ID3D12InfoQueue* iq = nullptr;
    if (debug) dev->QueryInterface(IID_PPV_ARGS(&iq));

    std::map<std::string, std::string> lib;
    FILE* bf = _wfopen(blobs.c_str(), L"rb");
    CHECK(bf);
    for (uint32_t n; fread(&n, 4, 1, bf) == 1;) {
        std::string id(n, '\0'), b;
        fread(id.data(), 1, n, bf), fread(&n, 4, 1, bf), b.resize(n), fread(b.data(), 1, n, bf);
        lib[id] = std::move(b);
    }
    fclose(bf);

    // global: t0 TLAS (root SRV), u0 table; variant 1 + an unused root constant. local: 4 constants b0 space1; variant 1 + b1.
    ID3D12RootSignature* grs[2] = {};
    ID3D12RootSignature* lrs[2] = {};
    for (int loc = 0; loc < 2; ++loc)
        for (int v = 0; v < 2; ++v) {
            D3D12_DESCRIPTOR_RANGE u0 = {D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 1, 0, 0, 0};
            std::vector<D3D12_ROOT_PARAMETER> p(2);
            if (!loc) {
                p[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV, p[0].Descriptor = {0, 0};
                p[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE, p[1].DescriptorTable = {1, &u0};
                if (v) p.push_back({}), p[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS, p[2].Constants = {0, 0, 1};
            } else {
                p.resize(1 + v);
                p[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS, p[0].Constants = {0, 1, 4};
                if (v) p[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS, p[1].Constants = {1, 1, 1};
            }
            D3D12_VERSIONED_ROOT_SIGNATURE_DESC vd = {D3D_ROOT_SIGNATURE_VERSION_1_0};
            vd.Desc_1_0 = {(UINT)p.size(), p.data(), 0, nullptr, loc ? D3D12_ROOT_SIGNATURE_FLAG_LOCAL_ROOT_SIGNATURE : D3D12_ROOT_SIGNATURE_FLAG_NONE};
            ID3DBlob *b = nullptr, *err = nullptr;
            CHECK(SUCCEEDED(ser(&vd, &b, &err)));
            CHECK(SUCCEEDED(dev->CreateRootSignature(0, b->GetBufferPointer(), b->GetBufferSize(), IID_PPV_ARGS(loc ? &lrs[v] : &grs[v]))));
        }

    // first use: SetPipelineState1 + a zero-size DispatchRays (no ray runs, no resource is read), executed and waited for
    ID3D12CommandQueue* q = nullptr;
    ID3D12CommandAllocator* ca = nullptr;
    ID3D12GraphicsCommandList4* cl = nullptr;
    ID3D12Fence* fence = nullptr;
    ID3D12Resource* table = nullptr;
    ID3D12DescriptorHeap* heap = nullptr;
    D3D12_COMMAND_QUEUE_DESC qd = {D3D12_COMMAND_LIST_TYPE_DIRECT};
    CHECK(SUCCEEDED(dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&q))) && SUCCEEDED(dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&ca))));
    CHECK(SUCCEEDED(dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, ca, nullptr, IID_PPV_ARGS(&cl))) && SUCCEEDED(cl->Close()));
    CHECK(SUCCEEDED(dev->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence))));
    D3D12_HEAP_PROPERTIES up = {D3D12_HEAP_TYPE_UPLOAD};
    D3D12_RESOURCE_DESC bd = {D3D12_RESOURCE_DIMENSION_BUFFER, 0, 65536, 1, 1, 1, DXGI_FORMAT_UNKNOWN, {1, 0}, D3D12_TEXTURE_LAYOUT_ROW_MAJOR};
    CHECK(SUCCEEDED(dev->CreateCommittedResource(&up, D3D12_HEAP_FLAG_NONE, &bd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&table))));
    uint8_t* tp = nullptr;
    CHECK(SUCCEEDED(table->Map(0, nullptr, (void**)&tp)));
    D3D12_DESCRIPTOR_HEAP_DESC hd = {D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV, 1, D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE};
    CHECK(SUCCEEDED(dev->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&heap))));
    D3D12_UNORDERED_ACCESS_VIEW_DESC uav = {DXGI_FORMAT_R8G8B8A8_UNORM, D3D12_UAV_DIMENSION_TEXTURE2D};
    dev->CreateUnorderedAccessView(nullptr, nullptr, &uav, heap->GetCPUDescriptorHandleForHeapStart());  // a null UAV
    UINT64 fv = 0;
    auto dispatch = [&](ID3D12StateObject* so, const void* rgid, int g) {
        memcpy(tp, rgid, D3D12_SHADER_IDENTIFIER_SIZE_IN_BYTES);
        return ms([&] {
            ca->Reset(), cl->Reset(ca, nullptr);
            cl->SetDescriptorHeaps(1, &heap);
            cl->SetComputeRootSignature(grs[g]);
            cl->SetComputeRootShaderResourceView(0, 0);
            cl->SetComputeRootDescriptorTable(1, heap->GetGPUDescriptorHandleForHeapStart());
            if (g) cl->SetComputeRoot32BitConstant(2, 0, 0);
            cl->SetPipelineState1(so);
            D3D12_DISPATCH_RAYS_DESC dr = {};
            dr.RayGenerationShaderRecord = {table->GetGPUVirtualAddress(), D3D12_SHADER_IDENTIFIER_SIZE_IN_BYTES};
            cl->DispatchRays(&dr);  // Width = Height = Depth = 0
            cl->Close();
            q->ExecuteCommandLists(1, (ID3D12CommandList**)&cl);
            q->Signal(fence, ++fv);
            fence->SetEventOnCompletion(fv, nullptr);  // no event: returns once the fence is reached
        });
    };

    DxrPlan P = dxr_plan(seed);
    std::map<std::string, ID3D12StateObject*> live;
    std::mutex live_mx;  // parallel rows are collections only: they add handles, never look one up
    // extra (first create only): {identifier fetch ms, first dispatch ms}
    auto create = [&](const DxrObj& o, const std::string& add_to, double* extra) -> double {
        std::vector<D3D12_STATE_SUBOBJECT> subs;
        subs.reserve(1024);  // the association points into it: must never reallocate
        std::deque<std::wstring> names;
        std::deque<std::vector<D3D12_EXPORT_DESC>> exps;
        std::deque<D3D12_DXIL_LIBRARY_DESC> libs;
        std::deque<D3D12_HIT_GROUP_DESC> hgs;
        std::deque<D3D12_EXISTING_COLLECTION_DESC> cols;
        std::vector<LPCWSTR> hgnames;
        D3D12_STATE_OBJECT_CONFIG cfg = {D3D12_STATE_OBJECT_FLAG_ALLOW_STATE_OBJECT_ADDITIONS};
        D3D12_GLOBAL_ROOT_SIGNATURE g = {grs[o.grs]};
        D3D12_LOCAL_ROOT_SIGNATURE l = {lrs[o.lrs]};
        D3D12_RAYTRACING_SHADER_CONFIG sc = {o.payload, o.attr};
        D3D12_RAYTRACING_PIPELINE_CONFIG pc = {o.depth};
        D3D12_SUBOBJECT_TO_EXPORTS_ASSOCIATION as = {};
        auto w = [&](const std::string& s) { return names.emplace_back(s.begin(), s.end()).c_str(); };
        auto add = [&](D3D12_STATE_SUBOBJECT_TYPE t, const void* d) { subs.push_back({t, d}); };
        if (o.additions) add(D3D12_STATE_SUBOBJECT_TYPE_STATE_OBJECT_CONFIG, &cfg);
        add(D3D12_STATE_SUBOBJECT_TYPE_GLOBAL_ROOT_SIGNATURE, &g);
        for (auto& id : o.libs) {
            auto it = lib.find(id);
            if (it == lib.end()) return printf("  p%d: library %s missing\n", proc, id.c_str()), -1.0;
            auto& e = exps.emplace_back();
            if (o.rename)
                for (auto& f : P.libs.at(id)) e.push_back({w(f.name + "_r"), w(f.name), D3D12_EXPORT_FLAG_NONE});
            libs.push_back({{it->second.data(), it->second.size()}, (UINT)e.size(), e.empty() ? nullptr : e.data()});
            add(D3D12_STATE_SUBOBJECT_TYPE_DXIL_LIBRARY, &libs.back());
        }
        for (auto& h : o.hgs) {
            hgs.push_back({w(h.name), h.is.empty() ? D3D12_HIT_GROUP_TYPE_TRIANGLES : D3D12_HIT_GROUP_TYPE_PROCEDURAL_PRIMITIVE,
                           h.ah.empty() ? nullptr : w(h.ah), w(h.ch), h.is.empty() ? nullptr : w(h.is)});
            add(D3D12_STATE_SUBOBJECT_TYPE_HIT_GROUP, &hgs.back());
            hgnames.push_back(hgs.back().HitGroupExport);
        }
        if (!hgnames.empty()) {  // the local root signature goes to the hit groups only (an unassociated one is everyone's default)
            add(D3D12_STATE_SUBOBJECT_TYPE_LOCAL_ROOT_SIGNATURE, &l);
            as = {&subs.back(), (UINT)hgnames.size(), hgnames.data()};
            add(D3D12_STATE_SUBOBJECT_TYPE_SUBOBJECT_TO_EXPORTS_ASSOCIATION, &as);
        }
        add(D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_SHADER_CONFIG, &sc);
        add(D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_PIPELINE_CONFIG, &pc);
        for (auto& c : o.colls) {
            auto it = live.find(c);
            if (it == live.end()) return printf("  p%d: collection %s missing\n", proc, c.c_str()), -1.0;
            cols.push_back({it->second, 0, nullptr});
            add(D3D12_STATE_SUBOBJECT_TYPE_EXISTING_COLLECTION, &cols.back());
        }
        if (subs.size() >= 1024) return -1.0;
        D3D12_STATE_OBJECT_DESC d = {o.collection ? D3D12_STATE_OBJECT_TYPE_COLLECTION : D3D12_STATE_OBJECT_TYPE_RAYTRACING_PIPELINE, (UINT)subs.size(), subs.data()};
        ID3D12StateObject* so = nullptr;
        HRESULT hr = E_NOINTERFACE;
        double t = ms([&] {
            if (add_to.empty()) hr = dev5->CreateStateObject(&d, IID_PPV_ARGS(&so));
            else if (dev7 && live.count(add_to)) hr = dev7->AddToStateObject(&d, live[add_to], IID_PPV_ARGS(&so));
        });
        if (FAILED(hr)) {
            printf("  p%d: %s %s failed hr=0x%08x\n", proc, add_to.empty() ? "CreateStateObject" : "AddToStateObject", o.handle.c_str(), (unsigned)hr);
            for (UINT64 i = 0, n = iq ? iq->GetNumStoredMessages() : 0; i < n; ++i) {
                SIZE_T len = 0;
                iq->GetMessage(i, nullptr, &len);
                std::vector<uint64_t> buf((len + 7) / 8);
                auto* msg = (D3D12_MESSAGE*)buf.data();
                if (SUCCEEDED(iq->GetMessage(i, msg, &len)) && msg->Severity <= D3D12_MESSAGE_SEVERITY_ERROR) printf("    %.*s\n", (int)msg->DescriptionByteLength, msg->pDescription);
            }
            if (iq) iq->ClearStoredMessages();
            return -1.0;
        }
        std::string ex = !o.hgs.empty() ? o.hgs[0].name : !o.libs.empty() ? P.libs.at(o.libs[0])[0].name + (o.rename ? "_r" : "") : "";
        ID3D12StateObjectProperties* pr = nullptr;
        if (extra && !o.collection && !ex.empty() && SUCCEEDED(so->QueryInterface(IID_PPV_ARGS(&pr)))) {
            std::wstring wx(ex.begin(), ex.end()), wr(o.rg.begin(), o.rg.end());
            const void* sid = nullptr;
            extra[0] += ms([&] { sid = pr->GetShaderIdentifier(wx.c_str()); });
            if (!sid) printf("  p%d: %s: no shader identifier for %s\n", proc, o.handle.c_str(), ex.c_str());
            if (const void* rid = pr->GetShaderIdentifier(wr.c_str())) extra[1] += dispatch(so, rid, o.grs);
            else printf("  p%d: %s: no raygen identifier for %s\n", proc, o.handle.c_str(), o.rg.c_str());
            pr->Release();
        }
        std::lock_guard lk(live_mx);
        auto& slot = live[o.handle];
        if (slot) slot->Release();
        slot = so;
        return t;
    };

    FILE* f = _wfopen(out.c_str(), L"w");
    CHECK(f);
    for (auto& op : P.ops) {
        if (!(op.mask >> (proc - 1) & 1)) continue;
        double t[2] = {0, 0}, extra[2] = {0, 0};
        for (int pass = 0; pass < 2; ++pass) {  // first seen, then again in this process
            if (op.threads > 1) {
                std::atomic<size_t> next{0};
                std::atomic<bool> bad{false};
                t[pass] = ms([&] {
                    std::vector<std::thread> ts;
                    for (int i = 0; i < op.threads; ++i)
                        ts.emplace_back([&] {
                            for (size_t j; (j = next++) < op.objs.size();)
                                if (!bad && create(op.objs[j], op.add_to, nullptr) < 0) bad = true;
                        });
                    for (auto& th : ts) th.join();
                });
                if (bad) t[pass] = -1;
                continue;
            }
            for (auto& o : op.objs) {
                double x = t[pass] < 0 ? -1 : create(o, op.add_to, pass ? nullptr : extra);
                t[pass] = x < 0 ? -1 : t[pass] + x;
            }
        }
        fprintf(f, "%s\t%.3f\t%.3f\t%.3f\t%.3f\n", op.label.c_str(), t[0], t[1], extra[0], extra[1]);
    }
    fclose(f);
    return 0;
}

// Driver cache folders (+ the runtime's D3DSCache, reported only: never ours to delete): path -> (size, mtime).
// mtimes are informative only: AMD writes through a memory map (ARCHITECTURE.md).
using DxrSnap = std::map<std::wstring, std::pair<uintmax_t, long long>>;
static std::wstring local_appdata() {
    wchar_t la[MAX_PATH] = L"";
    GetEnvironmentVariableW(L"LOCALAPPDATA", la, MAX_PATH);
    return la;
}
static DxrSnap dxr_snap() {
    namespace fs = std::filesystem;
    DxrSnap s;
    std::wstring la = local_appdata();
    for (auto d : {L"\\NVIDIA\\DXCache", L"\\NVIDIA\\GLCache", L"\\AMD\\DxcCache", L"\\AMD\\DxCache", L"\\AMD\\VkCache", L"\\D3DSCache"}) {
        std::error_code ec;
        for (fs::recursive_directory_iterator it(la + d, fs::directory_options::skip_permission_denied, ec), end; !ec && it != end; it.increment(ec))
            if (it->is_regular_file(ec)) s[it->path().wstring()] = {it->file_size(ec), it->last_write_time(ec).time_since_epoch().count()};
    }
    return s;
}
static void dxr_diff(const DxrSnap& a, const DxrSnap& b, const char* when) {
    size_t cut = local_appdata().size() + 1;
    int n = 0;
    printf("  cache files after %s:", when);
    for (auto& [p, v] : b) {
        auto it = a.find(p);
        const wchar_t* rel = p.c_str() + cut;
        if (it == a.end()) printf("\n    new     %ls (%llu)", rel, (unsigned long long)v.first), ++n;
        else if (it->second.first != v.first) printf("\n    grown   %ls %llu -> %llu", rel, (unsigned long long)it->second.first, (unsigned long long)v.first), ++n;
        else if (it->second.second != v.second) printf("\n    written %ls (mtime)", rel), ++n;
    }
    for (auto& [p, v] : a)
        if (!b.count(p)) printf("\n    gone    %ls", p.c_str() + cut), ++n;
    printf(n ? "\n" : " no change\n");
}

// The DXIL libraries of dxr_plan(seed) into a file the children read (u32 id length, id, u32 size, bytes); ms or -1.
static double dxr_blobs(IDxcCompiler3* comp, const DxrPlan& P, const std::wstring& path) {
    FILE* bf = _wfopen(path.c_str(), L"wb");
    if (!bf) return -1;
    bool ok = true;
    double t = ms([&] {
        for (auto& [id, fns] : P.libs) {
            std::string b;
            if (!(ok = dxc_compile(comp, dxr_src(fns), b))) return;
            uint32_t n = (uint32_t)id.size(), m = (uint32_t)b.size();
            fwrite(&n, 4, 1, bf), fwrite(id.data(), 1, n, bf), fwrite(&m, 4, 1, bf), fwrite(b.data(), 1, m, bf);
        }
    });
    fclose(bf);
    return ok ? t : -1;
}

static IDxcCompiler3* dxc_compiler() {
    HMODULE dxc = dxc_load();
    auto create = dxc ? (DxcCreateInstanceProc)GetProcAddress(dxc, "DxcCreateInstance") : nullptr;
    IDxcCompiler3* comp = nullptr;
    return create && SUCCEEDED(create(CLSID_DxcCompiler, IID_PPV_ARGS(&comp))) ? comp : nullptr;
}

// Where the lock is: SCSKILLER_DEV_DIR (the lock is <dir>\gpu.lock), and the GPU is also busy while the file named by
// SCSKILLER_GPU_BUSY_FILE exists. A development tree may set both defaults in an optional local
// header; else the lock lives under %TEMP%\scskiller-test and nothing else marks the GPU busy. As the tests' TestEnv.
#if __has_include("selftest_team.h")
#include "selftest_team.h"
#endif
static std::wstring env_or(const wchar_t* name, std::wstring fallback) {
    wchar_t v[MAX_PATH];
    DWORD n = GetEnvironmentVariableW(name, v, MAX_PATH);
    return n > 0 && n < MAX_PATH ? std::wstring(v, n) : fallback;
}

// Takes the dev GPU lock for a timing run: "" when the GPU is busy (another run holds the lock, a lock file another tool
// wrote less than 30 min ago, or the other tool's busy file, which has no staleness rule). The lock stays open without
// sharing until this process exits, and the OS deletes it then, also after a crash.
static HANDLE g_gpu_lock;
static std::wstring gpu_lock(const char* who) {
#ifdef SCSK_TEAM_GPU_BUSY_FILE
    std::wstring busy = env_or(L"SCSKILLER_GPU_BUSY_FILE", SCSK_TEAM_GPU_BUSY_FILE);
#else
    std::wstring busy = env_or(L"SCSKILLER_GPU_BUSY_FILE", L"");
#endif
    if (!busy.empty() && GetFileAttributesW(busy.c_str()) != INVALID_FILE_ATTRIBUTES)
        return printf("GPU busy: %ls\n", busy.c_str()), L"";
    wchar_t tmp[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
#ifdef SCSK_TEAM_DEV_DIR
    std::wstring lockdir = env_or(L"SCSKILLER_DEV_DIR", local_appdata() + SCSK_TEAM_DEV_DIR);
#else
    std::wstring lockdir = env_or(L"SCSKILLER_DEV_DIR", std::wstring(tmp) + L"scskiller-test");
#endif
    std::wstring lock = lockdir + L"\\gpu.lock";
    CreateDirectoryW(lockdir.c_str(), nullptr);
    auto open = [&](DWORD how) { return CreateFileW(lock.c_str(), GENERIC_WRITE | DELETE, 0, nullptr, how, FILE_FLAG_DELETE_ON_CLOSE, nullptr); };
    HANDLE h = open(CREATE_NEW);
    WIN32_FILE_ATTRIBUTE_DATA fa;
    if (h == INVALID_HANDLE_VALUE && GetLastError() == ERROR_FILE_EXISTS && GetFileAttributesExW(lock.c_str(), GetFileExInfoStandard, &fa)) {
        FILETIME now;
        GetSystemTimeAsFileTime(&now);
        ULARGE_INTEGER n{{now.dwLowDateTime, now.dwHighDateTime}}, w{{fa.ftLastWriteTime.dwLowDateTime, fa.ftLastWriteTime.dwHighDateTime}};
        if (n.QuadPart - w.QuadPart < 30ull * 60 * 10000000) return printf("GPU busy: %ls\n", lock.c_str()), L"";
        // older: a tool's lock file left behind, or a long run's that it still holds: then opening it without sharing fails
        if ((h = open(OPEN_EXISTING)) == INVALID_HANDLE_VALUE && GetLastError() == ERROR_SHARING_VIOLATION)
            return printf("GPU busy: %ls\n", lock.c_str()), L"";
    }
    if (h == INVALID_HANDLE_VALUE) return printf("GPU lock %ls can't be created\n", lock.c_str()), L"";
    DWORD n;
    WriteFile(h, who, (DWORD)strlen(who), &n, nullptr);
    g_gpu_lock = h;
    return lock;
}

// A probe run's own new folder, <dir><what>-<pid>-<n>\ (never one that already exists). The run makes every file it
// writes in there (exe copies, outputs, a redirected cache), and the folder goes at the end with all in it, unless kept.
struct RunDir {
    std::wstring path;
    bool keep;
    RunDir(const std::wstring& dir, const wchar_t* what, bool keep = false) : keep(keep) {
        static int n;
        std::wstring p = dir + what + L"-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(++n) + L"\\";
        if (CreateDirectoryW(p.c_str(), nullptr)) path = p;
        else printf("can't create a new folder %ls (error %lu)\n", p.c_str(), GetLastError());
    }
    ~RunDir() {
        std::error_code ec;
        if (!path.empty() && !keep) std::filesystem::remove_all(path, ec);
    }
};

// Lists the driver-cache files created since start (not D3DSCache: the runtime's). A probe never deletes them: no name
// proves which process made one (NVIDIA's key isn't derivable from the exe name, AMD's 32-bit name hash collides).
// ponytail: probe runs leave their throwaway names' cache files; delete by the keys the children hold open
// (NvidiaAppCache.KeysOpenBy) if they pile up
static void list_new_cache(const DxrSnap& start) {
    std::vector<std::wstring> made;
    for (auto& [p, v] : dxr_snap())
        if (!start.count(p) && p.find(L"\\D3DSCache\\") == std::wstring::npos) made.push_back(p);
    if (!made.empty()) printf("%zu new driver-cache files (left in place):\n", made.size());
    for (auto& p : made) printf("  %ls\n", p.c_str());
}

// `selftest ledgerdir`: stamped_recorder_dir on scskiller.ledger files as a copy writes them and as garbage.
static int ledger_dir_rules() {
    wchar_t tmp[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    RunDir run(tmp, L"scsk-ledger");
    CHECK(!run.path.empty());
    std::wstring out = L"x";
    CHECK(!stamped_recorder_dir(run.path, out) && out.empty());  // none: the installed copy's
    const auto with = [&](const std::string& text) {
        HANDLE f = CreateFileW((run.path + L"scskiller.ledger").c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
        DWORD n = 0;
        if (f != INVALID_HANDLE_VALUE) WriteFile(f, text.data(), (DWORD)text.size(), &n, nullptr), CloseHandle(f);
        return stamped_recorder_dir(run.path, out);
    };
    CHECK(with(R"(C:\SCSKiller\data\armed)") && out == LR"(C:\SCSKiller\data)");
    CHECK(with("D:\\Jeux\\donn\xc3\xa9" "es\\armed\r\n") && out == L"D:\\Jeux\\donn\u00e9es");
    CHECK(with(R"(\\server\share\data\ARMED)") && out == LR"(\\server\share\data)");
    CHECK(with(R"(C:\Users\x\.scs\armed)") && out == LR"(C:\Users\x\.scs)");
    for (const char* bad : {"", "\r\n", R"(data\armed)", R"(C:\armed)", R"(C:\data\other)", R"(C:\data\armed\)", "C:\\da\xff" "ta\\armed",
                            R"(\\?\C:\data\armed)", R"(\\?\C:\armed)", R"(\\.\C:\data\armed)", R"(C:\data\..\armed)", R"(C:\data\.\armed)",
                            R"(1:\data\armed)", R"(\\server\armed)", R"(\\\server\share\armed)", R"(\\server\\armed)", R"(C:\data/..\armed)",
                            R"(C:\data\.. \armed)", R"(C:\data\x.\armed)", R"(C:\data\armed:x)", R"(C:\data:x\armed)"})
        CHECK(with(bad) && out.empty());
    CHECK(with(std::string("C:\\data\0\\armed", 14)) && out.empty());
    CHECK(with(std::string(4 * MAX_PATH, 'a')) && out.empty());
    SetEnvironmentVariableW(L"SCSKILLER_TEST_RECORDER_DIR", nullptr);  // this process only: as in a game
    CHECK(with(R"(C:\SCSKiller\data\armed)") && recorder_dir(run.path) == LR"(C:\SCSKiller\data)");
    CHECK(with("armed") && recorder_dir(run.path).empty());
    CHECK(DeleteFileW((run.path + L"scskiller.ledger").c_str()) && CreateDirectoryW((run.path + L"scskiller.ledger").c_str(), nullptr));
    CHECK(stamped_recorder_dir(run.path, out) && out.empty());  // can't be read: refused, never the installed copy's
    CHECK(RemoveDirectoryW((run.path + L"scskiller.ledger").c_str()));
    const std::wstring target = run.path + L"target";
    if (CreateSymbolicLinkW((run.path + L"scskiller.ledger").c_str(), target.c_str(), SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE)) {
        CHECK(stamped_recorder_dir(run.path, out) && out.empty());  // a dangling link is there too
        HANDLE f = CreateFileW(target.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, 0, nullptr);
        DWORD n = 0;
        CHECK(f != INVALID_HANDLE_VALUE && WriteFile(f, "C:\\data\\armed", 13, &n, nullptr));
        CloseHandle(f);
        CHECK(stamped_recorder_dir(run.path, out) && out.empty());  // a link to a good marker is still no marker the app wrote
    } else printf("ledgerdir: no symbolic link here (error %lu), link cases skipped\n", GetLastError());
    printf("ledgerdir OK\n");
    return 0;
}

// `selftest admission` (no GPU, no proxy): the stamp sig= covers, risk='s form, and the verdict from what the checks found.
static int admission_rules() {
    CHECK(signed_stamp(L"1", L"2", L"", L"", L"") == L"exe_size=1\r\nexe_time=2\r\n");
    {
        const std::wstring deep = L"C:\\" + std::wstring(300, L'a') + L"\\b";
        CHECK(long_path(L"C:\\a\\b") == L"C:\\a\\b" && long_path(deep) == L"\\\\?\\" + deep && long_path(L"\\\\?\\" + deep) == L"\\\\?\\" + deep);
        CHECK(long_path(L"\\\\s\\share\\" + std::wstring(300, L'a')) == L"\\\\?\\UNC\\s\\share\\" + std::wstring(300, L'a'));
        for (const std::wstring& raw : {deep + L"\\..\\c", deep + L"\\.\\c", deep + L"\\..", deep + L"/c", L"a\\" + deep.substr(3)})
            CHECK(long_path(raw) == raw);   // not normalized or not absolute: as it is
        CHECK(!odd_name(L"C:\\Games\\Game\\") && !odd_name(L"\\\\s\\share\\Game\\") && !odd_name(L"C:\\"));
        CHECK(odd_name(L"C:\\Games\\Game.\\x\\") && odd_name(L"C:\\Games\\Game \\") && odd_name(L"C:\\Games\\Game."));
    }
    // ini_value against GetPrivateProfileStringW: equal wherever it reads a value, empty where the two could differ
    {
        wchar_t tmp[MAX_PATH];
        GetTempPathW(MAX_PATH, tmp);
        const std::wstring file = std::wstring(tmp) + L"scsk-ini-" + std::to_wstring(GetCurrentProcessId()) + L".ini";
        const auto native = [&](const std::string& bytes, const wchar_t* key) {
            if (FILE* f = _wfopen(file.c_str(), L"wb")) fwrite(bytes.data(), 1, bytes.size(), f), fclose(f);
            wchar_t v[256] = {};
            GetPrivateProfileStringW(L"scskiller", key, L"", v, 256, file.c_str());
            return std::wstring(v);
        };
        const std::string signed_ini = "[scskiller]\r\n; a comment\r\nmode=record\r\nroot=..\\..\r\nexe_size=1\r\nsig=ab\r\n";
        struct Case { std::string bytes; const wchar_t* key; bool same; };
        for (const Case& c : std::vector<Case>{
                 {signed_ini, L"sig", true}, {signed_ini, L"root", true}, {signed_ini, L"vendor", true},
                 {"[SCSKiller]\nsig = ab \nsig=cd\n", L"sig", true}, {"vendor=amd\r\n[scskiller]\r\nsig=ab\r\n", L"vendor", true},
                 {"[scskiller]\r\nab_tag=\"before\"\r\n", L"ab_tag", true},
                 {"[scskiller]\r\n;c\rvendor=amd\r\nsig=ab\r\n", L"vendor", false},   // a CR alone ends a line natively
                 {"[ scskiller ]\r\nsig=bad\r\n[scskiller]\r\nsig=ab\r\n", L"sig", false},
                 {"[scskiller]\r\nsig=ab\r\n[scskiller]\r\nvendor=amd\r\n", L"vendor", false},
                 {"[other]\r\nsig=x\r\n[scskiller]\r\nsig=ab\r\n", L"sig", false},
                 {std::string("[scskiller]\r\nsig=ab\0\r\nvendor=amd\r\n", 34), L"vendor", false}}) {
            const std::wstring mine = ini_value(c.bytes, c.key);
            CHECK(c.same ? mine == native(c.bytes, c.key) : mine.empty());
        }
        DeleteFileW(file.c_str());
    }
    CHECK(signed_stamp(L"1", L"2", L"3", L"4", L"") == L"exe_size=1\r\nexe_time=2\r\npid=3\r\npid_time=4\r\n");
    CHECK(signed_stamp(L"1", L"2", L"3", L"4", L"1") == L"exe_size=1\r\nexe_time=2\r\npid=3\r\npid_time=4\r\nrisk=1\r\n");  // ScsKiller.BoundTo + "risk=1"
    CHECK(signed_stamp(L"1", L"2", L"", L"", L"1") == L"exe_size=1\r\nexe_time=2\r\nrisk=1\r\n");
    // ScsKiller.SignedLines after the binding: vendor=, then the countdown's three lines
    CHECK(signed_stamp(L"1", L"2", L"", L"", L"", L"amd", L"1", L"420", L"before") == L"exe_size=1\r\nexe_time=2\r\nvendor=amd\r\noverlay=1\r\nab_play_s=420\r\nab_tag=before\r\n");
    CHECK(signed_stamp(L"1", L"2", L"3", L"4", L"1", L"amd") == L"exe_size=1\r\nexe_time=2\r\npid=3\r\npid_time=4\r\nrisk=1\r\nvendor=amd\r\n");
    CHECK(risk_well_formed(L"", L"") && risk_well_formed(L"3", L"") && risk_well_formed(L"3", L"1"));
    CHECK(!risk_well_formed(L"", L"1") && !risk_well_formed(L"3", L"2") && !risk_well_formed(L"3", L"yes"));
    // the verdict: a finding refuses unless the session takes the risk, and the log then names them all
    std::string seen;
    const char* beside = "anti-cheat next to the exe";
    const std::vector<std::string> none, be{"BEService_x64.exe (next to the exe)"}, client{"BEClient_x64.dll"},
        eac{"EasyAntiCheat (next to the exe)", "start_protected_game.exe (in the install folder)"};
    const char* vac = "VAC (a Valve game)";
    CHECK(!decide(false, false, none, nullptr, none, nullptr, seen) && seen.empty());
    CHECK(!strcmp(decide(false, false, be, beside, none, nullptr, seen), beside));
    CHECK(!strcmp(decide(false, false, eac, "anti-cheat in the install folder", none, nullptr, seen), "anti-cheat in the install folder"));
    CHECK(!strcmp(decide(false, false, none, nullptr, client, nullptr, seen), "anti-cheat client loaded"));
    CHECK(!strcmp(decide(false, true, none, nullptr, none, nullptr, seen), "Riot Games install"));
    CHECK(!strcmp(decide(false, false, none, nullptr, none, vac, seen), vac));
    CHECK(!decide(true, true, eac, beside, client, vac, seen)
          && seen == "EasyAntiCheat (next to the exe), start_protected_game.exe (in the install folder), BEClient_x64.dll loaded, Riot Games install, VAC");
    seen.clear();
    CHECK(!decide(true, false, be, beside, none, nullptr, seen) && seen == "BEService_x64.exe (next to the exe)");
    seen.clear();
    CHECK(!decide(true, false, none, nullptr, none, nullptr, seen) && seen == "none at the first device");
    seen.clear();
    CHECK(!strcmp(decide(true, false, {"?"}, beside, none, nullptr, seen), beside));  // a folder couldn't be listed
    CHECK(!strcmp(decide(true, false, none, nullptr, none, "VAC: Steam's files couldn't be checked", seen), "VAC: Steam's files couldn't be checked"));
    CHECK(seen.empty());
    printf("admission OK\n");
    return 0;
}

// `selftest warmmemory` (no GPU, no proxy): the warm's workers under its memory budget, tick by tick.
static int warm_memory_rules() {
    WarmMemory m{6000, 20, 19};
    double t = 0;
    auto run = [&](double mb, double held, bool low, double until) {  // one tick a second
        std::string steps;
        for (; t < until; t += 1000)
            if (char c = warm_memory_tick(m, mb, held, low, t)) steps += c;
        return steps;
    };
    CHECK(run(3000, 0, true, 120000) == "" && m.allowed == 19);  // low on memory, under the budget: the workers stay, none comes
    CHECK(run(3000, 0, false, 121000) == "u" && m.allowed == 20);  // no longer low: one comes back
    CHECK(run(6500, 0, true, 127000) == "d" && m.allowed == 19);  // over the budget: one goes, low or not
    CHECK(run(6400, 0, false, 133000) == "d" && m.allowed == 18);  // the drop lowered it, still over: one more 6 s later
    CHECK(run(6300, 0, false, 139000) == "d" && m.allowed == 17);
    CHECK(run(6500, 1000, false, 200000) == "" && m.allowed == 17);  // kept state objects count in the limit
    CHECK(run(7200, 1000, false, 206000) == "d" && m.allowed == 16);
    CHECK(run(7200, 1000, false, 212000) == "k" && m.allowed == 17 && m.kept == 3000);  // the drop freed nothing: undone, kept
    CHECK(run(7200, 1000, false, 240000) == "u" && m.allowed == 18);  // 0.3x under the new limit: the climb is open again
    CHECK(run(8100, 1000, false, 300000) == "");  // under it, not by the margin
    m = {2000, 16, 1};
    t = 0;
    CHECK(run(2600, 0, false, 31000) == "" && m.allowed == 1);  // the dbs' records alone over the budget: one worker
    CHECK(run(2600, 0, false, 600000) == "kuuuuuuuuuuuuuuu" && m.allowed == 16);  // over on one worker for 30 s: kept, then back
    m = {6000, 20, 20};
    t = 0;
    CHECK(run(6500, 0, true, 7000) == "d" && run(6500, 0, true, 13000) == "k" && m.allowed == 19);  // low: kept, the drop stays
    CHECK(run(6500, 0, true, 100000) == "" && m.allowed == 19);
    m = {1000, 16, 16};  // memory the driver keeps grows 20 MB/s whatever the workers: at most one worker goes at a time
    int fewest = 16, steps = 0;
    for (t = 0; t < 600000; t += 100)
        if (warm_memory_tick(m, 500 + t / 50, 0, false, t)) fewest = std::min(fewest, m.allowed), ++steps;
    CHECK(fewest == 15 && m.allowed >= 15 && steps < 2 * 600 / 15 + 2);  // a drop and its undo per 0.3x of growth (15 s) at most
    m = {1000, 8, 8};  // each drop frees 50 MB: one worker's 1250 MB counts as kept only 30 s after the last drop
    double last_drop = 0, kept_at = 0;
    for (t = 0; t < 120000 && !kept_at; t += 100)
        if (char c = warm_memory_tick(m, 1600 - 50 * (8 - m.allowed), 0, false, t)) (c == 'd' ? last_drop : kept_at) = t;
    CHECK(m.allowed == 1 && kept_at - last_drop > 30000 && m.kept == 1250 - 700);
    printf("warmmemory OK\n");
    return 0;
}

// `selftest gpulock` (no GPU): the GPU lock has one owner at a time and goes with its handle.
static int gpu_lock_rules() {
    wchar_t tmp[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    RunDir run(tmp, L"scsk-lock");
    CHECK(!run.path.empty());
    std::wstring dev = run.path.substr(0, run.path.size() - 1), lock = dev + L"\\gpu.lock";
    SetEnvironmentVariableW(L"SCSKILLER_DEV_DIR", dev.c_str());
    SetEnvironmentVariableW(L"SCSKILLER_GPU_BUSY_FILE", (dev + L"\\none").c_str());
    HANDLE f = CreateFileW(lock.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, 0, nullptr);  // another tool's lock file
    CHECK(f != INVALID_HANDLE_VALUE);
    CloseHandle(f);
    CHECK(gpu_lock("a").empty());  // young: respected
    f = CreateFileW(lock.c_str(), FILE_WRITE_ATTRIBUTES, 0, nullptr, OPEN_EXISTING, 0, nullptr);
    FILETIME now_ft;
    GetSystemTimeAsFileTime(&now_ft);
    ULARGE_INTEGER t{{now_ft.dwLowDateTime, now_ft.dwHighDateTime}};
    t.QuadPart -= 31ull * 60 * 10000000;
    FILETIME old = {t.LowPart, t.HighPart};
    CHECK(f != INVALID_HANDLE_VALUE && SetFileTime(f, nullptr, nullptr, &old));
    CloseHandle(f);
    CHECK(gpu_lock("b") == lock && gpu_lock("c").empty());  // left behind: taken over, then held
    f = CreateFileW(lock.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
    CHECK(f == INVALID_HANDLE_VALUE && GetLastError() == ERROR_SHARING_VIOLATION);  // held without sharing: nobody else opens it
    CloseHandle(g_gpu_lock);
    CHECK(GetFileAttributesW(lock.c_str()) == INVALID_FILE_ATTRIBUTES);  // gone with its handle
    CHECK(gpu_lock("d") == lock);
    CloseHandle(g_gpu_lock);
    SetEnvironmentVariableW(L"SCSKILLER_DEV_DIR", (dev + L"\\none\\sub").c_str());  // a folder that can't be made: no lock
    CHECK(gpu_lock("e").empty());
    printf("gpulock OK\n");
    return 0;
}

static int fields_main(const std::wstring& a, const std::wstring& dir, int runs, bool dxil) {
    if (dxil && !dxc_compiler()) return printf("fields dxil: no DXC (dxcompiler.dll + dxil.dll)\n"), 1;
    std::wstring lock = gpu_lock("selftest fields");
    if (lock.empty()) return 1;
    std::wstring name = L"scskf" + std::to_wstring(GetTickCount() % 1000000) + L".exe", odir = dir + L"fields_other\\";
    CreateDirectoryW(odir.c_str(), nullptr);
    CHECK(CopyFileW(a.c_str(), (dir + name).c_str(), FALSE) && CopyFileW(a.c_str(), (odir + name).c_str(), FALSE));
    if (dxil) SetEnvironmentVariableW(L"SELFTEST_FIELDS_DXIL", L"1");
    const DxrSnap start = dxr_snap();
    int rc = fields_parent(dir + name, odir + name, dir, runs);
    list_new_cache(start);
    return rc;
}

static int dxr_parent(const std::wstring& a, const std::wstring& dir, int runs) {
    std::wstring lock = gpu_lock("selftest dxr");
    if (lock.empty()) return 1;

    IDxcCompiler3* comp = dxc_compiler();
    CHECK(comp);

    std::wstring stamp = std::to_wstring(GetTickCount() % 1000000);  // fresh throwaway exe names, never a game's
    std::wstring odir = dir + L"dxr_other\\", x = dir + L"scskdxr" + stamp + L"x.exe", y = dir + L"scskdxr" + stamp + L"y.exe",
                 x5 = odir + L"scskdxr" + stamp + L"x.exe", blobs = dir + L"dxr_blobs.bin";
    CreateDirectoryW(odir.c_str(), nullptr);
    CHECK(CopyFileW(a.c_str(), x.c_str(), FALSE) && CopyFileW(a.c_str(), y.c_str(), FALSE) && CopyFileW(a.c_str(), x5.c_str(), FALSE));
    printf("probe 7: exe X = %ls, Y = %ls\n", x.substr(dir.size()).c_str(), y.substr(dir.size()).c_str());
    const DxrSnap start = dxr_snap();
    DxrSnap prev = start;
    std::vector<std::string> order;
    Col col[8];  // p1, p2, p2 again, p3, p4, p5, id, dispatch (max over processes)
    std::random_device rd;
    for (int r = 0; r < runs; ++r) {
        unsigned seed = 100000 + rd() % 8000000;  // + row * 1000 + i stays < 2^24: the float constants stay exact
        DxrPlan P = dxr_plan(seed);
        double tc = dxr_blobs(comp, P, blobs);
        CHECK(tc >= 0);
        printf("probe 7 run %d/%d: %zu libraries compiled in %.1f s\n", r + 1, runs, P.libs.size(), tc / 1000);
        for (int p = 1; p <= 5; ++p) {
            std::wstring out = dir + L"dxr" + std::to_wstring(p) + L".txt";
            int rc = run(p == 4 ? y : p == 5 ? x5 : x, L"dxrchild " + std::to_wstring(p) + L" " + std::to_wstring(seed) + L" \"" + blobs + L"\" \"" + out + L"\"");
            if (rc) printf("  p%d exited with %d\n", p, rc);
            DxrSnap s = dxr_snap();
            char when[32];
            sprintf_s(when, "p%d (%s)", p, p == 4 ? "Y" : p == 5 ? "X, other folder" : "X");
            dxr_diff(prev, s, when);
            prev = s;
            read_rows(out, &order, [&](const std::string& name, const char* rest) {
                double first = 0, again = 0, id = 0, disp = 0;
                sscanf_s(rest, "%lf\t%lf\t%lf\t%lf", &first, &again, &id, &disp);
                col[p == 1 ? 0 : p == 2 ? 1 : p][name].push_back(first);
                if (p == 2) col[2][name].push_back(again);
                col[6][name].push_back(id), col[7][name].push_back(disp);
            });
        }
    }
    auto med = [&](int c, const std::string& n) {
        if (c < 6) return col_median(col[c], n);
        auto it = col[c].find(n);
        return it == col[c].end() || it->second.empty() ? -2.0 : *std::max_element(it->second.begin(), it->second.end());
    };
    double cold = med(1, "ctl: cold base shape, fresh (p2)");
    printf("probe 7: CreateStateObject / AddToStateObject ms, median of %d run(s). p1-p3 exe X, p4 exe Y, p5 exe X in another folder;\n"
           "\"p2 %%cold\" = p2 / the p2 cold base-shape control; \"id\", \"disp\" = slowest identifier fetch / first dispatch after a create\n", runs);
    printf("%-56s %8s %8s %8s %8s %8s %8s %8s %8s %8s\n", "row", "p1", "p2", "p2again", "p3", "p4", "p5", "p2 %cold", "id", "disp");
    for (auto& n : order) {
        double p2 = med(1, n);
        char pc[16] = "       -";
        if (p2 >= 0 && cold > 0) snprintf(pc, sizeof pc, "%7.0f%%", 100 * p2 / cold);
        printf("%-56s%s%s%s%s%s%s %s%s%s\n", n.c_str(), cell(med(0, n)).c_str(), cell(p2).c_str(), cell(med(2, n)).c_str(), cell(med(3, n)).c_str(),
               cell(med(4, n)).c_str(), cell(med(5, n)).c_str(), pc, cell(med(6, n)).c_str(), cell(med(7, n)).c_str());
    }
    list_new_cache(start);
    return 0;
}

// Probe 8 (`selftest vulkan`): Vulkan pipelines in the driver's own disk cache.
// Per run (fresh SPIR-V: one OpConstant of each shader is the seed), seven processes in order; X, Y, Z are fresh exe
// names per invocation:
//   p1 = X: the base pipelines, cold.   p2 = X: the base again (same exe name: a disk hit?), then each variant around
//   them, first seen and again in-process.   p3 = X: the variants again (were they written to disk?).   p4 = Y: the base
//   (per exe name?).   p5 = X from another folder (path-independent?).   p6 = Y and p7 = Z with NVIDIA's
//   __GL_SHADER_DISK_CACHE_PATH (a folder of ours) and __GL_SHADER_DISK_CACHE_APP_NAME set, as Steam sets them for the games
//   it launches: p6 cold = the driver honours the redirect; p7 (a name that never ran) a hit = the app name, not the exe
//   name, keys the redirected cache. p8 = X, same folder, another app name: a hit = the driver reads every app's file in
//   the folder. Other vendors ignore those variables (p6 then hits like p4, p7 is cold, p8 hits like p2).
// No VkPipelineCache anywhere: only the driver-internal cache is measured. Every process starts with a fresh warm-up
// pipeline (compiler start-up) and a cold control. Implicit layers (overlays, Steam's Fossilize layer) are disabled.
// "fc" = a create with VK_PIPELINE_CREATE_FAIL_ON_PIPELINE_COMPILE_REQUIRED_BIT first: H = the driver created it without
// compiling (it says: cached), M = VK_PIPELINE_COMPILE_REQUIRED.
#define VKF(X)                                                                                                                   \
    X(vkDestroyInstance) X(vkEnumeratePhysicalDevices) X(vkGetPhysicalDeviceProperties2) X(vkGetPhysicalDeviceFeatures2)          \
    X(vkGetPhysicalDeviceQueueFamilyProperties) X(vkEnumerateDeviceExtensionProperties) X(vkCreateDevice) X(vkDestroyDevice)     \
    X(vkCreateShaderModule) X(vkDestroyShaderModule) X(vkCreateDescriptorSetLayout) X(vkCreatePipelineLayout)                    \
    X(vkCreateRenderPass) X(vkCreateGraphicsPipelines) X(vkCreateComputePipelines) X(vkDestroyPipeline)
#define VKDECL(n) static PFN_##n n;
VKF(VKDECL)

struct VkG {  // one graphics pipeline; the defaults are the base pipeline's state
    unsigned vs, fs;
    float k = 1;  // the FS's specialization constant
    VkFormat vfmt = VK_FORMAT_R32G32B32A32_SFLOAT;
    uint32_t voff = 0, stride = 16;
    VkVertexInputRate rate = VK_VERTEX_INPUT_RATE_VERTEX;
    VkPrimitiveTopology topo = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;
    VkCullModeFlags cull = VK_CULL_MODE_NONE;
    VkFrontFace ff = VK_FRONT_FACE_COUNTER_CLOCKWISE;
    bool blend = false, depth = false, dyncull = false, rp = false;
    VkFormat color = VK_FORMAT_R8G8B8A8_UNORM;
    VkSampleCountFlagBits samples = VK_SAMPLE_COUNT_1_BIT;
    int layout = 0;  // 0 base (b0 UBO, FS), 1 + push constants, 2 + unused b1 sampler, 3 b0 dynamic UBO, 4 b0 all graphics stages
};

static int vk_child(int proc, unsigned seed, const std::wstring& out) {
    HMODULE m = load_system(L"vulkan-1.dll");
    CHECK(m);
    auto gipa = (PFN_vkGetInstanceProcAddr)GetProcAddress(m, "vkGetInstanceProcAddr");
    CHECK(gipa);
    auto create_instance = (PFN_vkCreateInstance)gipa(nullptr, "vkCreateInstance");
    VkApplicationInfo app{VK_STRUCTURE_TYPE_APPLICATION_INFO};
    app.pApplicationName = "scskiller selftest";
    app.apiVersion = VK_API_VERSION_1_3;
    VkInstanceCreateInfo ici{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO};
    ici.pApplicationInfo = &app;
    VkInstance inst;
    CHECK(create_instance && create_instance(&ici, nullptr, &inst) == VK_SUCCESS);
#define VKLOAD(n) CHECK(n = (PFN_##n)gipa(inst, #n));
    VKF(VKLOAD)

    // The first discrete GPU, else the first integrated one (the AMD test machine's is an iGPU).
    uint32_t n = 0;
    vkEnumeratePhysicalDevices(inst, &n, nullptr);
    std::vector<VkPhysicalDevice> pds(n);
    vkEnumeratePhysicalDevices(inst, &n, pds.data());
    VkPhysicalDevice pd = VK_NULL_HANDLE;
    for (auto type : {VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU, VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU})
        for (auto d : pds) {
            VkPhysicalDeviceProperties2 p{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2};
            vkGetPhysicalDeviceProperties2(d, &p);
            if (!pd && p.properties.deviceType == type) pd = d;
        }
    CHECK(pd);
    vkEnumerateDeviceExtensionProperties(pd, nullptr, &n, nullptr);
    std::vector<VkExtensionProperties> exts(n);
    vkEnumerateDeviceExtensionProperties(pd, nullptr, &n, exts.data());
    auto has = [&](const char* e) { return std::any_of(exts.begin(), exts.end(), [&](auto& x) { return !strcmp(x.extensionName, e); }); };
    bool gpl = has(VK_EXT_GRAPHICS_PIPELINE_LIBRARY_EXTENSION_NAME) && has(VK_KHR_PIPELINE_LIBRARY_EXTENSION_NAME);
    bool bin = has(VK_KHR_PIPELINE_BINARY_EXTENSION_NAME);

    VkPhysicalDeviceProperties2 props{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2};
    VkPhysicalDeviceDriverProperties drv{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DRIVER_PROPERTIES};
    VkPhysicalDeviceGraphicsPipelineLibraryPropertiesEXT gplp{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_GRAPHICS_PIPELINE_LIBRARY_PROPERTIES_EXT};
    VkPhysicalDevicePipelineBinaryPropertiesKHR binp{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PIPELINE_BINARY_PROPERTIES_KHR};
    props.pNext = &drv;  // chain only what the device has
    void** tail = &drv.pNext;
    if (gpl) *tail = &gplp, tail = &gplp.pNext;
    if (bin) *tail = &binp, tail = &binp.pNext;
    vkGetPhysicalDeviceProperties2(pd, &props);
    CHECK(props.properties.apiVersion >= VK_API_VERSION_1_3);

    VkPhysicalDeviceFeatures2 feat{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2};
    VkPhysicalDeviceVulkan13Features f13{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES};
    VkPhysicalDeviceGraphicsPipelineLibraryFeaturesEXT gplf{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_GRAPHICS_PIPELINE_LIBRARY_FEATURES_EXT};
    feat.pNext = &f13;
    if (gpl) f13.pNext = &gplf;
    vkGetPhysicalDeviceFeatures2(pd, &feat);
    gpl = gpl && gplf.graphicsPipelineLibrary;
    CHECK(f13.dynamicRendering && f13.pipelineCreationCacheControl);
    if (proc == 1) {
        auto yn = [](VkBool32 b) { return b ? "yes" : "no"; };
        printf("  device: %s, %s %s, Vulkan %u.%u.%u, pipelineCacheUUID ", props.properties.deviceName, drv.driverName, drv.driverInfo,
               VK_API_VERSION_MAJOR(props.properties.apiVersion), VK_API_VERSION_MINOR(props.properties.apiVersion), VK_API_VERSION_PATCH(props.properties.apiVersion));
        for (auto b : props.properties.pipelineCacheUUID) printf("%02x", b);
        printf("\n  VK_EXT_graphics_pipeline_library: %s", gpl ? "yes" : "no");
        if (gpl) printf(" (fast linking %s, independent interpolation decoration %s)", yn(gplp.graphicsPipelineLibraryFastLinking),
                        yn(gplp.graphicsPipelineLibraryIndependentInterpolationDecoration));
        printf("\n  VK_KHR_pipeline_binary: %s", bin ? "yes" : "no");
        if (bin) printf(" (internal cache %s, internal cache control %s, prefers internal cache %s, precompiled internal cache %s, compressed data %s)",
                        yn(binp.pipelineBinaryInternalCache), yn(binp.pipelineBinaryInternalCacheControl), yn(binp.pipelineBinaryPrefersInternalCache),
                        yn(binp.pipelineBinaryPrecompiledInternalCache), yn(binp.pipelineBinaryCompressedData));
        printf("\n  VK_EXT_shader_module_identifier: %s\n", has(VK_EXT_SHADER_MODULE_IDENTIFIER_EXTENSION_NAME) ? "yes" : "no");
    }

    vkGetPhysicalDeviceQueueFamilyProperties(pd, &n, nullptr);
    std::vector<VkQueueFamilyProperties> qfs(n);
    vkGetPhysicalDeviceQueueFamilyProperties(pd, &n, qfs.data());
    uint32_t qf = 0;
    while (qf < n && !(qfs[qf].queueFlags & VK_QUEUE_GRAPHICS_BIT)) ++qf;
    CHECK(qf < n);
    float prio = 1;
    VkDeviceQueueCreateInfo qci{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO, nullptr, 0, qf, 1, &prio};
    VkPhysicalDeviceVulkan13Features e13{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES};
    e13.dynamicRendering = e13.pipelineCreationCacheControl = VK_TRUE;
    VkPhysicalDeviceGraphicsPipelineLibraryFeaturesEXT egpl{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_GRAPHICS_PIPELINE_LIBRARY_FEATURES_EXT};
    egpl.graphicsPipelineLibrary = VK_TRUE;
    if (gpl) e13.pNext = &egpl;
    const char* dext[] = {VK_KHR_PIPELINE_LIBRARY_EXTENSION_NAME, VK_EXT_GRAPHICS_PIPELINE_LIBRARY_EXTENSION_NAME};
    VkDeviceCreateInfo dci{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO, &e13, 0, 1, &qci};
    dci.enabledExtensionCount = gpl ? 2 : 0;
    dci.ppEnabledExtensionNames = dext;
    VkDevice dev;
    CHECK(vkCreateDevice(pd, &dci, nullptr, &dev) == VK_SUCCESS);

    // Shader modules by (stage, seed): the embedded SPIR-V with its marker constant replaced by the seed (< 2^24: exact).
    std::map<std::pair<int, unsigned>, VkShaderModule> mods;
    auto mod = [&](int stage, unsigned s) {
        auto& sm = mods[{stage, s}];
        if (sm) return sm;
        const uint32_t* src[] = {kSpv_vs, kSpv_fs, kSpv_cs};
        size_t size[] = {sizeof kSpv_vs, sizeof kSpv_fs, sizeof kSpv_cs};
        uint32_t marker[] = {kSpv_vs_marker, kSpv_fs_marker, kSpv_cs_marker};
        std::vector<uint32_t> w(src[stage], src[stage] + size[stage] / 4);
        float f = (float)s;
        memcpy(&*std::find(w.begin(), w.end(), marker[stage]), &f, 4);
        VkShaderModuleCreateInfo ci{VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO, nullptr, 0, w.size() * 4, w.data()};
        vkCreateShaderModule(dev, &ci, nullptr, &sm);
        return sm;
    };

    // Pipeline layouts: graphics 0..4 (VkG::layout), compute 5 (b0 SSBO) and 6 (+ push constants).
    auto set_layout = [&](std::vector<VkDescriptorSetLayoutBinding> b) {
        VkDescriptorSetLayoutCreateInfo ci{VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO, nullptr, 0, (uint32_t)b.size(), b.data()};
        VkDescriptorSetLayout l = VK_NULL_HANDLE;
        vkCreateDescriptorSetLayout(dev, &ci, nullptr, &l);
        return l;
    };
    const VkDescriptorSetLayoutBinding ubo{0, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, 1, VK_SHADER_STAGE_FRAGMENT_BIT};
    VkDescriptorSetLayout sl[] = {
        set_layout({ubo}), set_layout({ubo, {1, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, 1, VK_SHADER_STAGE_FRAGMENT_BIT}}),
        set_layout({{0, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER_DYNAMIC, 1, VK_SHADER_STAGE_FRAGMENT_BIT}}),
        set_layout({{0, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, 1, VK_SHADER_STAGE_ALL_GRAPHICS}}),
        set_layout({{0, VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, 1, VK_SHADER_STAGE_COMPUTE_BIT}})};
    auto pipeline_layout = [&](VkDescriptorSetLayout s, VkShaderStageFlags pc) {
        VkPushConstantRange r{pc, 0, 16};
        VkPipelineLayoutCreateInfo ci{VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO, nullptr, 0, 1, &s, pc ? 1u : 0u, &r};
        VkPipelineLayout l = VK_NULL_HANDLE;
        vkCreatePipelineLayout(dev, &ci, nullptr, &l);
        return l;
    };
    VkPipelineLayout pl[] = {pipeline_layout(sl[0], 0), pipeline_layout(sl[0], VK_SHADER_STAGE_VERTEX_BIT | VK_SHADER_STAGE_FRAGMENT_BIT),
                             pipeline_layout(sl[1], 0), pipeline_layout(sl[2], 0), pipeline_layout(sl[3], 0),
                             pipeline_layout(sl[4], 0), pipeline_layout(sl[4], VK_SHADER_STAGE_COMPUTE_BIT)};
    for (auto l : pl) CHECK(l);

    // The one render pass (the "render pass instead of dynamic rendering" row): a single RGBA8 color attachment.
    VkAttachmentDescription att{0, VK_FORMAT_R8G8B8A8_UNORM, VK_SAMPLE_COUNT_1_BIT, VK_ATTACHMENT_LOAD_OP_CLEAR, VK_ATTACHMENT_STORE_OP_STORE,
                                VK_ATTACHMENT_LOAD_OP_DONT_CARE, VK_ATTACHMENT_STORE_OP_DONT_CARE, VK_IMAGE_LAYOUT_UNDEFINED,
                                VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL};
    VkAttachmentReference aref{0, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL};
    VkSubpassDescription sub{0, VK_PIPELINE_BIND_POINT_GRAPHICS, 0, nullptr, 1, &aref};
    VkRenderPassCreateInfo rpci{VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO, nullptr, 0, 1, &att, 1, &sub};
    VkRenderPass rp;
    CHECK(vkCreateRenderPass(dev, &rpci, nullptr, &rp) == VK_SUCCESS);

    // Every state block of a graphics pipeline, filled from a VkG. Pointers into it stay valid while it lives.
    struct GState {
        VkSpecializationMapEntry se{0, 0, 4};
        VkSpecializationInfo spec{1, &se, 4, nullptr};
        VkPipelineShaderStageCreateInfo st[2];
        VkVertexInputBindingDescription vb;
        VkVertexInputAttributeDescription va;
        VkPipelineVertexInputStateCreateInfo vi{VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO, nullptr, 0, 1, &vb, 1, &va};
        VkPipelineInputAssemblyStateCreateInfo ia{VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO};
        VkPipelineViewportStateCreateInfo vp{VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO, nullptr, 0, 1, nullptr, 1, nullptr};
        VkPipelineRasterizationStateCreateInfo rs{VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO};
        VkPipelineMultisampleStateCreateInfo ms{VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO};
        VkPipelineDepthStencilStateCreateInfo ds{VK_STRUCTURE_TYPE_PIPELINE_DEPTH_STENCIL_STATE_CREATE_INFO};
        VkPipelineColorBlendAttachmentState cba{};
        VkPipelineColorBlendStateCreateInfo cb{VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO, nullptr, 0, VK_FALSE, VK_LOGIC_OP_COPY, 1, &cba};
        VkDynamicState dyn[3] = {VK_DYNAMIC_STATE_VIEWPORT, VK_DYNAMIC_STATE_SCISSOR, VK_DYNAMIC_STATE_CULL_MODE};
        VkPipelineDynamicStateCreateInfo dy{VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO, nullptr, 0, 2, dyn};
        VkFormat color;
        VkPipelineRenderingCreateInfo ri{VK_STRUCTURE_TYPE_PIPELINE_RENDERING_CREATE_INFO, nullptr, 0, 1, &color};
        VkGraphicsPipelineCreateInfo ci{VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO};
    };
    auto fill = [&](GState& g, const VkG& d) {
        g.spec.pData = &d.k;
        g.st[0] = {VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO, nullptr, 0, VK_SHADER_STAGE_VERTEX_BIT, mod(0, d.vs), "main"};
        g.st[1] = {VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO, nullptr, 0, VK_SHADER_STAGE_FRAGMENT_BIT, mod(1, d.fs), "main", &g.spec};
        g.vb = {0, d.stride, d.rate};
        g.va = {0, 0, d.vfmt, d.voff};
        g.ia.topology = d.topo;
        g.rs.cullMode = d.cull, g.rs.frontFace = d.ff, g.rs.lineWidth = 1;
        g.ms.rasterizationSamples = d.samples;
        g.ds.depthTestEnable = g.ds.depthWriteEnable = d.depth, g.ds.depthCompareOp = VK_COMPARE_OP_LESS;
        g.cba.blendEnable = d.blend;
        g.cba.srcColorBlendFactor = VK_BLEND_FACTOR_SRC_ALPHA, g.cba.dstColorBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA;
        g.cba.srcAlphaBlendFactor = VK_BLEND_FACTOR_ONE, g.cba.dstAlphaBlendFactor = VK_BLEND_FACTOR_ZERO;
        g.cba.colorWriteMask = 0xF;
        g.dy.dynamicStateCount = d.dyncull ? 3 : 2;
        g.color = d.color;
        g.ri.depthAttachmentFormat = d.depth ? VK_FORMAT_D32_SFLOAT : VK_FORMAT_UNDEFINED;
        auto& c = g.ci;
        c.pNext = d.rp ? nullptr : &g.ri;
        c.stageCount = 2, c.pStages = g.st, c.pVertexInputState = &g.vi, c.pInputAssemblyState = &g.ia, c.pViewportState = &g.vp;
        c.pRasterizationState = &g.rs, c.pMultisampleState = &g.ms, c.pDepthStencilState = &g.ds, c.pColorBlendState = &g.cb;
        c.pDynamicState = &g.dy, c.layout = pl[d.layout], c.renderPass = d.rp ? rp : VK_NULL_HANDLE;
    };
    // A create, timed: ms, -1 failed, -3 VK_PIPELINE_COMPILE_REQUIRED (only with the fail-on-compile flag).
    int fails = 0;
    auto timed = [&](auto&& create) {
        VkPipeline p = VK_NULL_HANDLE;
        VkResult r = VK_SUCCESS;
        double t = ms([&] { r = create(&p); });
        if (p) vkDestroyPipeline(dev, p, nullptr);
        if (r == VK_PIPELINE_COMPILE_REQUIRED) return -3.0;
        if (r != VK_SUCCESS && fails++ < 20) printf("  p%d: create failed, VkResult %d\n", proc, (int)r);
        return r == VK_SUCCESS ? t : -1.0;
    };
    auto graphics = [&](const VkG& d, VkPipelineCreateFlags flags) {
        GState g;
        fill(g, d);
        g.ci.flags = flags;
        return timed([&](VkPipeline* p) { return vkCreateGraphicsPipelines(dev, VK_NULL_HANDLE, 1, &g.ci, nullptr, p); });
    };
    auto compute = [&](unsigned cs, int layout, VkPipelineCreateFlags flags) {
        VkComputePipelineCreateInfo ci{VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO, nullptr, flags,
                                       {VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO, nullptr, 0, VK_SHADER_STAGE_COMPUTE_BIT, mod(2, cs), "main"}, pl[layout]};
        return timed([&](VkPipeline* p) { return vkCreateComputePipelines(dev, VK_NULL_HANDLE, 1, &ci, nullptr, p); });
    };

    // VK_EXT_graphics_pipeline_library: the four parts of a VkG's pipeline, kept for the link rows (the first set made).
    // Each part carries only its own state; RETAIN_LINK_TIME_OPTIMIZATION_INFO so both link kinds work.
    struct Libs { VkPipeline l[4] = {}; } gpl_g, gpl_m;
    auto libs = [&](const VkG& d, Libs& keep) -> double {
        if (!gpl) return -2;
        GState g;
        fill(g, d);
        double t = 0;
        Libs made;
        const VkGraphicsPipelineLibraryFlagsEXT part[4] = {
            VK_GRAPHICS_PIPELINE_LIBRARY_VERTEX_INPUT_INTERFACE_BIT_EXT, VK_GRAPHICS_PIPELINE_LIBRARY_PRE_RASTERIZATION_SHADERS_BIT_EXT,
            VK_GRAPHICS_PIPELINE_LIBRARY_FRAGMENT_SHADER_BIT_EXT, VK_GRAPHICS_PIPELINE_LIBRARY_FRAGMENT_OUTPUT_INTERFACE_BIT_EXT};
        for (int i = 0; i < 4 && t >= 0; ++i) {
            VkGraphicsPipelineLibraryCreateInfoEXT li{VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_LIBRARY_CREATE_INFO_EXT, &g.ri, part[i]};
            VkGraphicsPipelineCreateInfo c{VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO, &li,
                                           VK_PIPELINE_CREATE_LIBRARY_BIT_KHR | VK_PIPELINE_CREATE_RETAIN_LINK_TIME_OPTIMIZATION_INFO_BIT_EXT};
            if (i == 0) c.pVertexInputState = &g.vi, c.pInputAssemblyState = &g.ia;
            if (i == 1) c.stageCount = 1, c.pStages = &g.st[0], c.pViewportState = &g.vp, c.pRasterizationState = &g.rs, c.pDynamicState = &g.dy;
            if (i == 2) c.stageCount = 1, c.pStages = &g.st[1], c.pMultisampleState = &g.ms, c.pDepthStencilState = &g.ds;
            if (i == 3) c.pColorBlendState = &g.cb, c.pMultisampleState = &g.ms;
            if (i == 1 || i == 2) c.layout = g.ci.layout;
            VkResult r = VK_SUCCESS;
            t += ms([&] { r = vkCreateGraphicsPipelines(dev, VK_NULL_HANDLE, 1, &c, nullptr, &made.l[i]); });
            if (r != VK_SUCCESS) t = -1, printf("  p%d: library part %d failed, VkResult %d\n", proc, i, (int)r);
        }
        bool kept = !keep.l[0] && t >= 0;
        for (int i = 0; i < 4; ++i)
            if (kept) keep.l[i] = made.l[i];
            else if (made.l[i]) vkDestroyPipeline(dev, made.l[i], nullptr);
        return t;
    };
    auto link = [&](const Libs& k, bool lto) -> double {
        if (!gpl) return -2;
        if (!k.l[0]) return -1;
        VkPipelineLibraryCreateInfoKHR li{VK_STRUCTURE_TYPE_PIPELINE_LIBRARY_CREATE_INFO_KHR, nullptr, 4, k.l};
        VkGraphicsPipelineCreateInfo c{VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO, &li, lto ? VK_PIPELINE_CREATE_LINK_TIME_OPTIMIZATION_BIT_EXT : 0u};
        c.layout = pl[0];
        return timed([&](VkPipeline* p) { return vkCreateGraphicsPipelines(dev, VK_NULL_HANDLE, 1, &c, nullptr, p); });
    };

    // Seeds: s+1..s+11 are this run's shaders (a, b, c, g, m, the swaps); s+100+10p+i are fresh per process.
    const unsigned s = seed, f = seed + 100 + 10 * proc;
    const VkG A{s + 1, s + 2}, G{s + 6, s + 7}, M{s + 8, s + 9};
    auto var = [&](auto edit) { VkG d = A; edit(d); return d; };
    enum { ALL = 0x1FE, BASE = 1 << 1 | 1 << 2 | 1 << 4 | 1 << 5 | 1 << 6 | 1 << 7 | 1 << 8, VAR = 1 << 2 | 1 << 3, P3 = 1 << 3 };
    // fc: whether the row gets the fail-on-compile-required create first (monolithic pipelines only).
    struct Row { const char* name; int procs; bool fc; std::function<double(VkPipelineCreateFlags)> f; };
    auto G_ = [&](VkG d) { return [=, &graphics](VkPipelineCreateFlags fl) { return graphics(d, fl); }; };
    const std::vector<Row> rows = {
        {"ctl: warm-up, fresh VS+FS and CS (compiler start-up)", ALL, false, [&](auto) { return graphics({f, f + 1}, 0) + compute(f + 2, 5, 0); }},
        {"ctl: cold graphics, fresh VS + FS", ALL, true, G_({f + 3, f + 4})},
        {"ctl: cold compute, fresh CS", ALL, true, [&](auto fl) { return compute(f + 5, 5, fl); }},
        {"graphics VS a + FS a", BASE, true, G_(A)},
        {"compute CS a", BASE, true, [&](auto fl) { return compute(s + 3, 5, fl); }},
        {"graphics VS b + FS c", BASE, true, G_({s + 4, s + 5})},
        {"graphics VS m + FS m (for the gpl rows)", BASE, true, G_(M)},
        {"gpl: 4 libraries, VS g + FS g", BASE, false, [&](auto) { return libs(G, gpl_g); }},
        {"gpl: fast link (no LTO)", BASE, false, [&](auto) { return link(gpl_g, false); }},
        {"gpl: link with LTO", BASE, false, [&](auto) { return link(gpl_g, true); }},
        {"VS a + fresh FS", VAR, true, G_({s + 1, s + 10})},
        {"fresh VS + FS a", VAR, true, G_({s + 11, s + 2})},
        {"xpair: VS a + FS c (each cached, never paired)", VAR, true, G_({s + 1, s + 5})},
        {"state: blend on", VAR, true, G_(var([](VkG& d) { d.blend = true; }))},
        {"state: cull back, front face CW", VAR, true, G_(var([](VkG& d) { d.cull = VK_CULL_MODE_BACK_BIT, d.ff = VK_FRONT_FACE_CLOCKWISE; }))},
        {"state: depth test + D32 attachment", VAR, true, G_(var([](VkG& d) { d.depth = true; }))},
        {"state: line list", VAR, true, G_(var([](VkG& d) { d.topo = VK_PRIMITIVE_TOPOLOGY_LINE_LIST; }))},
        {"state: MSAA 4x", VAR, true, G_(var([](VkG& d) { d.samples = VK_SAMPLE_COUNT_4_BIT; }))},
        {"state: + dynamic cull mode", VAR, true, G_(var([](VkG& d) { d.dyncull = true; }))},
        {"vertex: R32G32B32 (the vec4 from 3 components)", VAR, true, G_(var([](VkG& d) { d.vfmt = VK_FORMAT_R32G32B32_SFLOAT, d.stride = 12; }))},
        {"vertex: offset 4, stride 20", VAR, true, G_(var([](VkG& d) { d.voff = 4, d.stride = 20; }))},
        {"vertex: R16G16B16A16_SFLOAT", VAR, true, G_(var([](VkG& d) { d.vfmt = VK_FORMAT_R16G16B16A16_SFLOAT, d.stride = 8; }))},
        {"vertex: per-instance", VAR, true, G_(var([](VkG& d) { d.rate = VK_VERTEX_INPUT_RATE_INSTANCE; }))},
        {"color: RGBA16F", VAR, true, G_(var([](VkG& d) { d.color = VK_FORMAT_R16G16B16A16_SFLOAT; }))},
        {"color: BGRA8", VAR, true, G_(var([](VkG& d) { d.color = VK_FORMAT_B8G8R8A8_UNORM; }))},
        {"color: R32_SFLOAT", VAR, true, G_(var([](VkG& d) { d.color = VK_FORMAT_R32_SFLOAT; }))},
        {"render pass (RGBA8) instead of dynamic rendering", VAR, true, G_(var([](VkG& d) { d.rp = true; }))},
        {"layout: + push-constant range (unused)", VAR, true, G_(var([](VkG& d) { d.layout = 1; }))},
        {"layout: + unused binding 1 (sampler)", VAR, true, G_(var([](VkG& d) { d.layout = 2; }))},
        {"layout: UBO -> dynamic UBO", VAR, true, G_(var([](VkG& d) { d.layout = 3; }))},
        {"layout: UBO stages FRAGMENT -> ALL_GRAPHICS", VAR, true, G_(var([](VkG& d) { d.layout = 4; }))},
        {"spec constant K 1 -> 2", VAR, true, G_(var([](VkG& d) { d.k = 2; }))},
        {"compute: + push-constant range", VAR, true, [&](auto fl) { return compute(s + 3, 6, fl); }},
        // p3 only: first seen in a process that never made the other form, which p1 and p2 put on disk.
        {"p3 xpair: VS b + FS a (on disk apart, never paired)", P3, true, G_({s + 4, s + 2})},
        {"p3 gpl: monolithic VS g + FS g (libraries + links on disk)", P3, true, G_(G)},
        {"p3 gpl: 4 libraries, VS m + FS m (monolithic on disk)", P3, false, [&](auto) { return libs(M, gpl_m); }},
        {"p3 gpl: link with LTO, VS m + FS m", P3, false, [&](auto) { return link(gpl_m, true); }},
        {"p3 gpl: fast link, VS m + FS m", P3, false, [&](auto) { return link(gpl_m, false); }},
    };
    FILE* o = _wfopen(out.c_str(), L"w");
    CHECK(o);
    for (auto& r : rows) {
        if (!(r.procs >> proc & 1)) continue;
        double fc = r.fc ? r.f(VK_PIPELINE_CREATE_FAIL_ON_PIPELINE_COMPILE_REQUIRED_BIT) : -2;
        double first = r.f(0), again = r.f(0);
        fprintf(o, "%s\t%.3f\t%.3f\t%.3f\n", r.name, fc, first, again);
    }
    fclose(o);
    for (auto& [k, sm] : mods) vkDestroyShaderModule(dev, sm, nullptr);
    for (auto* k : {&gpl_g, &gpl_m})
        for (auto l : k->l)
            if (l) vkDestroyPipeline(dev, l, nullptr);
    vkDestroyDevice(dev, nullptr);  // the driver may write its cache at device destruction: never skip it
    vkDestroyInstance(inst, nullptr);
    return 0;
}

static int vk_parent(const std::wstring& a, const std::wstring& dir, int runs) {
    std::wstring lock = gpu_lock("selftest vulkan");
    if (lock.empty()) return 1;
    SetEnvironmentVariableW(L"VK_LOADER_LAYERS_DISABLE", L"~all~");  // overlays and Steam's Fossilize layer would time themselves
    SetEnvironmentVariableW(L"DISABLE_VK_LAYER_VALVE_steam_fossilize_1", L"1");

    std::wstring stamp = std::to_wstring(GetTickCount() % 1000000);  // fresh throwaway exe names, never a game's
    std::wstring odir = dir + L"vk_other\\", redirect = dir + L"vk_glcache", app = L"scskvk" + stamp + L"app";
    std::wstring x = dir + L"scskvk" + stamp + L"x.exe", y = dir + L"scskvk" + stamp + L"y.exe", z = dir + L"scskvk" + stamp + L"z.exe",
                 x5 = odir + L"scskvk" + stamp + L"x.exe";
    CreateDirectoryW(odir.c_str(), nullptr);
    CreateDirectoryW(redirect.c_str(), nullptr);  // measured: a missing folder = no disk cache at all (cold, nothing written)
    for (auto& e : {x, y, z, x5}) CHECK(CopyFileW(a.c_str(), e.c_str(), FALSE));
    printf("probe 8: exe X = %ls, Y = %ls, Z = %ls; p6-p8 cache redirect %ls, app name %ls (p8: + \"b\")\n", x.substr(dir.size()).c_str(),
           y.substr(dir.size()).c_str(), z.substr(dir.size()).c_str(), redirect.c_str(), app.c_str());
    const DxrSnap start = dxr_snap();
    DxrSnap prev = start;
    std::vector<std::string> order;
    Col col[10];  // p1..p8 first seen, [9] = p2 again
    std::map<std::string, std::string> fc;               // H/M per process, last run
    std::random_device rd;
    const wchar_t* whom[] = {L"", L"X", L"X", L"X", L"Y", L"X, other folder", L"Y, redirected", L"Z, redirected", L"X, redirected, other app name"};
    for (int r = 0; r < runs; ++r) {
        unsigned seed = 100000 + rd() % 8000000;  // + 200 stays < 2^24: the seeded float constants stay exact
        printf("probe 8 run %d/%d\n", r + 1, runs);
        for (int p = 1; p <= 8; ++p) {
            SetEnvironmentVariableW(L"__GL_SHADER_DISK_CACHE_PATH", p >= 6 ? redirect.c_str() : nullptr);
            SetEnvironmentVariableW(L"__GL_SHADER_DISK_CACHE_APP_NAME", p >= 6 ? (p == 8 ? app + L"b" : app).c_str() : nullptr);
            std::wstring out = dir + L"vk" + std::to_wstring(p) + L".txt", exe = p == 4 || p == 6 ? y : p == 5 ? x5 : p == 7 ? z : x;
            int rc = run(exe, L"vkchild " + std::to_wstring(p) + L" " + std::to_wstring(seed) + L" \"" + out + L"\"");
            if (rc) printf("  p%d exited with %d\n", p, rc);
            DxrSnap s = dxr_snap();
            char when[64];
            sprintf_s(when, "p%d (%ls)", p, whom[p]);
            dxr_diff(prev, s, when);
            prev = s;
            read_rows(out, &order, [&](const std::string& name, const char* rest) {
                double c = 0, first = 0, again = 0;
                sscanf_s(rest, "%lf\t%lf\t%lf", &c, &first, &again);
                col[p][name].push_back(first);
                if (p == 2) col[9][name].push_back(again);
                auto& h = fc[name];
                h.resize(8, '.');
                h[p - 1] = c == -2 ? '.' : c == -3 ? 'M' : c < 0 ? 'x' : 'H';
            });
        }
        SetEnvironmentVariableW(L"__GL_SHADER_DISK_CACHE_PATH", nullptr);
        SetEnvironmentVariableW(L"__GL_SHADER_DISK_CACHE_APP_NAME", nullptr);
    }
    auto med = [&](int c, const std::string& n) { return col_median(col[c], n); };
    double cold = med(2, "ctl: cold graphics, fresh VS + FS");
    printf("probe 8: vkCreate*Pipelines ms, median of %d run(s). p1-p3 exe X, p4 Y, p5 X in another folder, p6 Y and p7 Z with the\n"
           "NVIDIA cache redirected (__GL_SHADER_DISK_CACHE_PATH + _APP_NAME), p8 X redirected under another app name;\n"
           "\"p2 %%cold\" = p2 / p2's cold graphics control; fc = fail-on-compile-required per process p1..p8 (H created without\n"
           "compiling, M compile required, . not asked)\n", runs);
    printf("%-54s %8s %8s %8s %8s %8s %8s %8s %8s %8s %8s  %s\n", "row", "p1", "p2", "p2again", "p3", "p4 Y", "p5 X od", "p6 Y rd", "p7 Z rd",
           "p8 X rd2", "p2 %cold", "fc");
    for (auto& n : order) {
        double p2 = med(2, n);
        char pc[16] = "       -";
        if (p2 >= 0 && cold > 0) snprintf(pc, sizeof pc, "%7.0f%%", 100 * p2 / cold);
        printf("%-54s%s%s%s%s%s%s%s%s%s %s  %s\n", n.c_str(), cell(med(1, n)).c_str(), cell(p2).c_str(), cell(med(9, n)).c_str(),
               cell(med(3, n)).c_str(), cell(med(4, n)).c_str(), cell(med(5, n)).c_str(), cell(med(6, n)).c_str(), cell(med(7, n)).c_str(),
               cell(med(8, n)).c_str(), pc, fc[n].c_str());
    }

    // The redirect folder, inside the run's: what the driver wrote there.
    namespace fs = std::filesystem;
    std::error_code ec;
    printf("files in the p6-p8 redirect folder:\n");
    for (fs::recursive_directory_iterator it(redirect, ec), end; !ec && it != end; it.increment(ec))
        if (it->is_regular_file(ec)) printf("  %ls (%llu)\n", it->path().wstring().substr(redirect.size()).c_str(), (unsigned long long)it->file_size(ec));
    list_new_cache(start);
    return 0;
}

// `selftest layoutrules`: which input layouts the D3D12 runtime accepts, on WARP (CPU only: no GPU driver, no GPU cache).
// One line per case: "<name> 0x<hr>". Planner rules built on it (ExactLayouts.OneClassPerSlot): one input classification
// per slot, slots 0..31, and whether per-instance elements of one slot may use different InstanceDataStepRates.
static int layout_rules() {
    HMODULE m = load_system(L"d3d12.dll");
    CHECK(m);
    auto create_device = (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice");
    auto ser = (PFN_D3D12_SERIALIZE_ROOT_SIGNATURE)GetProcAddress(m, "D3D12SerializeRootSignature");
    IDXGIFactory4* f = nullptr;
    IDXGIAdapter* warp = nullptr;
    CHECK(SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumWarpAdapter(IID_PPV_ARGS(&warp))));
    ID3D12Device* dev = nullptr;
    CHECK(SUCCEEDED(create_device(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))));
    D3D12_ROOT_SIGNATURE_DESC rd = {0, nullptr, 0, nullptr, D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT};
    ID3DBlob *rb = nullptr, *err = nullptr;
    ID3D12RootSignature* rs = nullptr;
    CHECK(SUCCEEDED(ser(&rd, D3D_ROOT_SIGNATURE_VERSION_1, &rb, &err)) && SUCCEEDED(dev->CreateRootSignature(0, rb->GetBufferPointer(), rb->GetBufferSize(), IID_PPV_ARGS(&rs))));
    ID3DBlob* vs = compile("float4 main(float4 a : ATTRIBUTE0, float4 b : ATTRIBUTE1, float4 c : ATTRIBUTE2) : SV_Position { return a + b + c; }", "vs_5_0");
    CHECK(vs);
    using E = D3D12_INPUT_ELEMENT_DESC;
    auto el = [](UINT idx, UINT slot, UINT off, bool inst, UINT step) {
        return E{"ATTRIBUTE", idx, DXGI_FORMAT_R32G32B32A32_FLOAT, slot, off,
                 inst ? D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA : D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, inst ? step : 0};
    };
    auto try_layout = [&](const char* name, std::vector<E> il) {
        D3D12_GRAPHICS_PIPELINE_STATE_DESC g = {};
        g.pRootSignature = rs, g.VS = {vs->GetBufferPointer(), vs->GetBufferSize()}, g.InputLayout = {il.data(), (UINT)il.size()};
        g.SampleMask = UINT_MAX, g.RasterizerState.FillMode = D3D12_FILL_MODE_SOLID, g.RasterizerState.CullMode = D3D12_CULL_MODE_NONE;
        g.RasterizerState.DepthClipEnable = TRUE, g.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
        g.DSVFormat = DXGI_FORMAT_D32_FLOAT, g.DepthStencilState.DepthEnable = TRUE, g.DepthStencilState.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ALL;
        g.DepthStencilState.DepthFunc = D3D12_COMPARISON_FUNC_LESS, g.SampleDesc = {1, 0};
        ID3D12PipelineState* pso = nullptr;
        HRESULT hr = dev->CreateGraphicsPipelineState(&g, IID_PPV_ARGS(&pso));
        if (pso) pso->Release();
        printf("%s 0x%08x\n", name, (unsigned)hr);
    };
    try_layout("control", {el(0, 0, 0, false, 0), el(1, 1, 0, true, 1), el(2, 1, 16, true, 1)});
    try_layout("mixed-class-one-slot", {el(0, 0, 0, false, 0), el(1, 1, 0, true, 1), el(2, 1, 16, false, 0)});
    try_layout("step-rates-1-2-one-slot", {el(0, 0, 0, false, 0), el(1, 1, 0, true, 1), el(2, 1, 16, true, 2)});
    try_layout("step-rates-0-1-one-slot", {el(0, 0, 0, false, 0), el(1, 1, 0, true, 0), el(2, 1, 16, true, 1)});
    try_layout("slot-31", {el(0, 0, 0, false, 0), el(1, 31, 0, false, 0), el(2, 30, 0, true, 1)});
    try_layout("slot-32", {el(0, 0, 0, false, 0), el(1, 32, 0, false, 0), el(2, 30, 0, true, 1)});
    return 0;
}

// `selftest so <seed>`: stream output pipelines created through the proxy d3d12.dll next to the exe (record mode), on WARP
// (no GPU cache touched): a VS without SV_Position feeding only stream output (NO_RASTERIZED_STREAM) as a graphics desc and
// as a stream. WARP also creates that desc without its declaration, so the replay is checked by round trip
// (SCSKILLER_WARM_ROUNDTRIP). Prints "warp-luid <hex>" (for scskiller_warm --adapter-luid) and one "<row> 0x<hr>" per create.
static int so_rows(const std::wstring& dir, unsigned seed) {
    SetEnvironmentVariableW(L"SCSKILLER_MODE", L"record");
    HMODULE m = LoadLibraryW((dir + L"d3d12.dll").c_str());
    CHECK(m);
    auto create_device = (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice");
    auto ser = (decltype(&D3D12SerializeRootSignature))GetProcAddress(m, "D3D12SerializeRootSignature");
    IDXGIFactory4* f = nullptr;
    IDXGIAdapter* warp = nullptr;
    CHECK(create_device && ser && SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumWarpAdapter(IID_PPV_ARGS(&warp))));
    DXGI_ADAPTER_DESC ad;
    CHECK(SUCCEEDED(warp->GetDesc(&ad)));
    printf("warp-luid %llx\n", ((unsigned long long)(uint32_t)ad.AdapterLuid.HighPart << 32) | ad.AdapterLuid.LowPart);
    ID3D12Device* dev = nullptr;
    ID3D12Device2* dev2 = nullptr;
    CHECK(SUCCEEDED(create_device(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))) && SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&dev2))));
    D3D12_ROOT_SIGNATURE_DESC rd = {0, nullptr, 0, nullptr, D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT | D3D12_ROOT_SIGNATURE_FLAG_ALLOW_STREAM_OUTPUT};
    ID3DBlob *rb = nullptr, *err = nullptr;
    ID3D12RootSignature* rs = nullptr;
    CHECK(SUCCEEDED(ser(&rd, D3D_ROOT_SIGNATURE_VERSION_1, &rb, &err)) && SUCCEEDED(dev->CreateRootSignature(0, rb->GetBufferPointer(), rb->GetBufferSize(), IID_PPV_ARGS(&rs))));
    std::string k = std::to_string(seed) + ".0";
    ID3DBlob* vs = compile("struct O { float4 a : TEXCOORD0; float2 b : TEXCOORD1; };"
                           "O main(float3 p : POSITION) { O o; o.a = float4(p * " + k + ", 1); o.b = p.xy; return o; }", "vs_5_0");
    CHECK(vs);
    D3D12_INPUT_ELEMENT_DESC ie = {"POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0};
    D3D12_SO_DECLARATION_ENTRY decl[3] = {{0, "TEXCOORD", 0, 0, 4, 0}, {0, nullptr, 0, 0, 2, 0}, {0, "TEXCOORD", 1, 0, 2, 1}};  // a 2-component gap in buffer 0
    UINT strides[2] = {24, 8};
    D3D12_GRAPHICS_PIPELINE_STATE_DESC g = {};
    g.pRootSignature = rs, g.VS = {vs->GetBufferPointer(), vs->GetBufferSize()}, g.InputLayout = {&ie, 1};
    g.StreamOutput = {decl, 3, strides, 2, D3D12_SO_NO_RASTERIZED_STREAM};
    g.SampleMask = UINT_MAX, g.RasterizerState = {D3D12_FILL_MODE_SOLID, D3D12_CULL_MODE_NONE}, g.RasterizerState.DepthClipEnable = TRUE;
    g.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_POINT, g.SampleDesc = {1, 0};
    ID3D12PipelineState* pso = nullptr;
    auto row = [&](const char* name, HRESULT hr) { if (pso) pso->Release(), pso = nullptr; printf("%s 0x%08x\n", name, (unsigned)hr); return hr; };
    CHECK(SUCCEEDED(row("so-gfx", dev->CreateGraphicsPipelineState(&g, IID_PPV_ARGS(&pso)))));
    struct {
        Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_ROOT_SIGNATURE, ID3D12RootSignature*> rs;
        Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_VS, D3D12_SHADER_BYTECODE> vs;
        Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_STREAM_OUTPUT, D3D12_STREAM_OUTPUT_DESC> so;
        Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_INPUT_LAYOUT, D3D12_INPUT_LAYOUT_DESC> il;
        Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_PRIMITIVE_TOPOLOGY, D3D12_PRIMITIVE_TOPOLOGY_TYPE> topo;
        Sub<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RENDER_TARGET_FORMATS, D3D12_RT_FORMAT_ARRAY> rtf;
    } s = {};
    UINT strides2[2] = {32, 8};  // another stride: another pipeline
    s.rs = {D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_ROOT_SIGNATURE, rs}, s.vs = {D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_VS, g.VS};
    s.so = {D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_STREAM_OUTPUT, {decl, 3, strides2, 2, D3D12_SO_NO_RASTERIZED_STREAM}};
    s.il = {D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_INPUT_LAYOUT, g.InputLayout};
    s.topo = {D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_PRIMITIVE_TOPOLOGY, D3D12_PRIMITIVE_TOPOLOGY_TYPE_POINT};
    s.rtf = {D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RENDER_TARGET_FORMATS, {}};
    D3D12_PIPELINE_STATE_STREAM_DESC sd = {sizeof s, &s};
    CHECK(SUCCEEDED(row("so-stream", dev2->CreatePipelineState(&sd, IID_PPV_ARGS(&pso)))));
    return 0;
}

// A VS + PS pair of seed k for SM 6.0; more loops = a longer compile.
static std::string fill_src(char stage, unsigned k, int loops) {
    std::string K = std::to_string(k) + ".0", n = std::to_string(loops);
    if (stage == 'v')
        return "cbuffer C : register(b0) { float4 kc[16]; }; struct O { float4 p : SV_Position; float4 a : TEXCOORD0; float4 b : TEXCOORD1; };"
               "O main(float3 p : POSITION, float2 uv : TEXCOORD0) { float4 x = float4(p, 1);"
               "[unroll] for (int i = 0; i < " + n + "; ++i) x = x * kc[i & 15] + sin(x.yzwx * " + K + " + i);"
               "O o; o.p = x; o.a = float4(uv, x.xy); o.b = x * " + K + "; return o; }";
    return "Texture2D t : register(t0); SamplerState s : register(s0); cbuffer C : register(b0) { float4 kc[16]; };"
           "float4 main(float4 pos : SV_Position, float4 a : TEXCOORD0, float4 b : TEXCOORD1) : SV_Target { float4 c = a;"
           "[unroll] for (int i = 0; i < " + n + "; ++i) c = c * t.Sample(s, c.xy * kc[i & 15].xy + b.zw) + kc[(i * 7) & 15] * " + K +
           " + sin(c.wzyx * (i + " + K + "));"
           "return c; }";
}

// `selftest nvext [runs]`: is NVAPI's shader-extension slot in NVIDIA's cache key of a ray tracing collection and of a PSO,
// and does the way it is set matter? A row "<a>><b>" creates its object with mode a in p1 and with mode b in p2 (same exe
// name X); each row has its own fresh shaders (a closest hit collection, a DXIL VS + PS). Modes: n none, t this thread
// (NvAPI_D3D12_SetNvShaderExtnSlotSpaceLocalThread u0 space 1001), u this thread u0 space 404, d the device
// (NvAPI_D3D12_SetNvShaderExtnSlotSpace u0 space 1001), x the PSO extension SET_SHADER_EXTENSION_SLOT_AND_SPACE (u0 space
// 1001, NvAPI_D3D12_CreateGraphicsPipelineState; PSOs only), o pipeline creation flags 0x11 (NvAPI_D3D12_SetCreatePipelineStateOptions,
// The Witcher 3's), O the same set from another thread. Every set is undone right after its create (slot ~0u, flags 0). A
// row "-b" is a p2-only cold control. p2 creates "t>t" before "n>n": a hit there also says the reset works. Kind A times an
// AddToStateObject (one more closest hit) on a pipeline created under the same mode just before it.
// The flags rows: a miss on "o>n" where "o>o" hits = the flags are in the key of that kind of object.
// The e2e rows (p3-p5) go through the proxy: p3 (exe R, record mode) creates each row with its first mode, p4 (exe Y, warm
// mode) replays that recording, p5 (exe Y, system d3d12.dll) creates each row with its second mode: a hit = the recording
// carried the state and the warm applied it.
// One closest hit of seed k, exported as CH<k> in hit group HG<k>: a collection, a ray tracing pipeline that allows
// additions, or an addition to one. Not copyable: the subobjects point into it.
struct SoOne {
    std::string lib;
    std::wstring ch, hg;
    D3D12_EXPORT_DESC ex{};
    D3D12_DXIL_LIBRARY_DESC ld{};
    D3D12_HIT_GROUP_DESC hgd{};
    D3D12_RAYTRACING_SHADER_CONFIG sc = {16, 8};
    D3D12_RAYTRACING_PIPELINE_CONFIG pc = {1};
    D3D12_GLOBAL_ROOT_SIGNATURE g{};
    D3D12_STATE_OBJECT_CONFIG cfg = {D3D12_STATE_OBJECT_FLAG_ALLOW_STATE_OBJECT_ADDITIONS};
    D3D12_STATE_SUBOBJECT subs[6]{};
    D3D12_STATE_OBJECT_DESC d{};
    SoOne(const SoOne&) = delete;
    SoOne(IDxcCompiler3* comp, unsigned k, ID3D12RootSignature* grs, D3D12_STATE_OBJECT_TYPE type) {
        std::string K = std::to_string(k) + ".0";
        if (!dxc_compile(comp,
                         "struct P { float4 c; }; cbuffer L : register(b0, space1) { float4 lk; };\n"
                         "[shader(\"closesthit\")] void CH(inout P p, in BuiltInTriangleIntersectionAttributes a) { float3 x = float3(a.barycentrics, " + K +
                             "); [unroll] for (int i = 0; i < 24; ++i) x = sin(x * 1.37 + float3(i, " + K + ", x.y)) * cos(x.zxy + " + K + "); p.c = float4(x, 1) * lk; }\n",
                         lib)) {
            lib.clear();
            return;
        }
        ch = L"CH" + std::to_wstring(k), hg = L"HG" + std::to_wstring(k);
        ex = {ch.c_str(), L"CH", D3D12_EXPORT_FLAG_NONE};
        ld = {{lib.data(), lib.size()}, 1, &ex};
        hgd = {hg.c_str(), D3D12_HIT_GROUP_TYPE_TRIANGLES, nullptr, ch.c_str(), nullptr};
        g = {grs};
        UINT n = 0;
        subs[n++] = {D3D12_STATE_SUBOBJECT_TYPE_DXIL_LIBRARY, &ld};
        subs[n++] = {D3D12_STATE_SUBOBJECT_TYPE_HIT_GROUP, &hgd};
        subs[n++] = {D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_SHADER_CONFIG, &sc};
        subs[n++] = {D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_PIPELINE_CONFIG, &pc};
        subs[n++] = {D3D12_STATE_SUBOBJECT_TYPE_GLOBAL_ROOT_SIGNATURE, &g};
        if (type != D3D12_STATE_OBJECT_TYPE_COLLECTION) subs[n++] = {D3D12_STATE_SUBOBJECT_TYPE_STATE_OBJECT_CONFIG, &cfg};
        d = {type, n, subs};
    }
};

struct NvApi {
    int (*slot)(IUnknown*, uint32_t, uint32_t) = nullptr;
    int (*slot_thread)(IUnknown*, uint32_t, uint32_t) = nullptr;
    int (*gfx)(ID3D12Device*, const D3D12_GRAPHICS_PIPELINE_STATE_DESC*, uint32_t, const void**, ID3D12PipelineState**) = nullptr;
    int (*opts)(ID3D12Device5*, const uint32_t*) = nullptr;
    bool load() {
        HMODULE m = LoadLibraryW(L"nvapi64.dll");
        auto qi = m ? (void* (*)(uint32_t))GetProcAddress(m, "nvapi_QueryInterface") : nullptr;
        if (!qi || ((int (*)())qi(0x0150E828))()) return false;  // NvAPI_Initialize
        slot = (decltype(slot))qi(0xAC2DFEB5), slot_thread = (decltype(slot_thread))qi(0x43D867C0), gfx = (decltype(gfx))qi(0x2FC28856);
        opts = (decltype(opts))qi(0x5C607A27);
        return slot && slot_thread && gfx;
    }
};
struct NvPsoExt { uint32_t base_version, extension, version, slot, space; };  // NVAPI_D3D12_PSO_SET_SHADER_EXTENSION_SLOT_DESC_V1

struct NvRow { const char* name; char kind, a, b; int phase; };  // kind R collection, A addition, P PSO; phase 0 direct (p1, p2), 1 e2e (p3-p5)
static const NvRow kNvRows[] = {
    {"R n>n", 'R', 'n', 'n', 0}, {"R t>t", 'R', 't', 't', 0}, {"R t>n", 'R', 't', 'n', 0}, {"R n>t", 'R', 'n', 't', 0},
    {"R d>t", 'R', 'd', 't', 0}, {"R t>d", 'R', 't', 'd', 0}, {"R t>u", 'R', 't', 'u', 0}, {"R -t", 'R', '-', 't', 0}, {"R -n", 'R', '-', 'n', 0},
    {"P n>n", 'P', 'n', 'n', 0}, {"P t>t", 'P', 't', 't', 0}, {"P t>n", 'P', 't', 'n', 0}, {"P n>t", 'P', 'n', 't', 0},
    {"P d>t", 'P', 'd', 't', 0}, {"P t>d", 'P', 't', 'd', 0}, {"P t>u", 'P', 't', 'u', 0}, {"P x>t", 'P', 'x', 't', 0},
    {"P t>x", 'P', 't', 'x', 0}, {"P x>x", 'P', 'x', 'x', 0}, {"P -t", 'P', '-', 't', 0}, {"P -n", 'P', '-', 'n', 0},
    {"R o>o", 'R', 'o', 'o', 0}, {"R o>n", 'R', 'o', 'n', 0}, {"R -o", 'R', '-', 'o', 0}, {"A o>o", 'A', 'o', 'o', 0},
    {"A o>n", 'A', 'o', 'n', 0}, {"A -o", 'A', '-', 'o', 0}, {"P o>n", 'P', 'o', 'n', 0},
    {"e2e R t>t", 'R', 't', 't', 1}, {"e2e R n>t", 'R', 'n', 't', 1}, {"e2e R n>n", 'R', 'n', 'n', 1}, {"e2e R -t", 'R', '-', 't', 1},
    {"e2e P t>t", 'P', 't', 't', 1}, {"e2e P x>x", 'P', 'x', 'x', 1}, {"e2e P n>t", 'P', 'n', 't', 1}, {"e2e P n>n", 'P', 'n', 'n', 1},
    {"e2e P -t", 'P', '-', 't', 1}, {"e2e R O>o", 'R', 'O', 'o', 1}, {"e2e A O>o", 'A', 'O', 'o', 1},
};

static int nvext_child(int proc, unsigned seed, const std::wstring& dir, const std::wstring& out) {
    if (proc == 3 || proc == 4) SetEnvironmentVariableW(L"SCSKILLER_MODE", proc == 4 ? L"warm" : L"record");
    HMODULE m = proc == 3 || proc == 4 ? LoadLibraryW((dir + L"d3d12.dll").c_str()) : load_system(L"d3d12.dll");
    CHECK(m);
    auto create_device = (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice");
    auto ser = (PFN_D3D12_SERIALIZE_VERSIONED_ROOT_SIGNATURE)GetProcAddress(m, "D3D12SerializeVersionedRootSignature");
    ID3D12Device* dev = nullptr;
    CHECK(create_device && ser && SUCCEEDED(create_device(nullptr, D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&dev))));
    FILE* f = _wfopen(out.c_str(), L"w");
    CHECK(f);
    if (proc == 4) {  // the warm replays the recording on this device, nothing else
        auto start = (void(WINAPI*)(IUnknown*))GetProcAddress(m, "SCSKiller_StartWarm");
        auto stats = (void(WINAPI*)(uint64_t*))GetProcAddress(m, "SCSKiller_Stats");
        CHECK(start && stats);
        uint64_t st[7];
        start(dev), stats(st);
        fprintf(f, "#warm\t%llu\t%llu\n", st[2], st[3]);
        fclose(f);
        return 0;
    }
    NvApi nv;
    CHECK(nv.load());
    ID3D12Device5* dev5 = nullptr;
    CHECK(SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&dev5))));
    ID3D12Device7* dev7 = nullptr;  // kind A
    dev->QueryInterface(IID_PPV_ARGS(&dev7));
    if (proc == 1 && nv.opts) {  // 0x5C607A27 is NvAPI_D3D12_SetCreatePipelineStateOptions: 0 for its {version, flags}, -9 (struct version) otherwise
        uint32_t good[2] = {8 | 1 << 16, 0}, bad[2] = {8 | 2 << 16, 0};
        printf("  SetCreatePipelineStateOptions: v1 %d, v2 %d\n", nv.opts(dev5, good), nv.opts(dev5, bad));
    }
    IDxcCompiler3* comp = dxc_compiler();
    CHECK(comp);

    D3D12_ROOT_PARAMETER cbv = {D3D12_ROOT_PARAMETER_TYPE_CBV};
    cbv.Descriptor = {0, 1};
    D3D12_VERSIONED_ROOT_SIGNATURE_DESC vd = {D3D_ROOT_SIGNATURE_VERSION_1_0};
    vd.Desc_1_0 = {1, &cbv, 0, nullptr, D3D12_ROOT_SIGNATURE_FLAG_NONE};
    ID3DBlob *b = nullptr, *err = nullptr;
    ID3D12RootSignature* grs = nullptr;
    CHECK(SUCCEEDED(ser(&vd, &b, &err)) && SUCCEEDED(dev->CreateRootSignature(0, b->GetBufferPointer(), b->GetBufferSize(), IID_PPV_ARGS(&grs))));
    ID3D12RootSignature* rs = fields_rs(dev, ser, RS_BASE);
    CHECK(rs);
    D3D12_INPUT_ELEMENT_DESC il[] = {{"POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0},
                                     {"TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 12, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0}};
    D3D12_GRAPHICS_PIPELINE_STATE_DESC base = {};
    base.pRootSignature = rs, base.InputLayout = {il, 2};
    base.BlendState.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;
    base.SampleMask = UINT_MAX;
    base.RasterizerState = {D3D12_FILL_MODE_SOLID, D3D12_CULL_MODE_NONE, FALSE, 0, 0.f, 0.f, TRUE, FALSE, FALSE, 0, D3D12_CONSERVATIVE_RASTERIZATION_MODE_OFF};
    base.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
    base.NumRenderTargets = 1, base.RTVFormats[0] = DXGI_FORMAT_R8G8B8A8_UNORM, base.SampleDesc = {1, 0};

    int bad_status = 0;
    auto nvcall = [&](const char* what, int st) {
        if (st && ++bad_status <= 8) printf("  p%d: %s: NvAPI status %d\n", proc, what, st);
    };
    auto flags = [&](uint32_t f, bool other_thread) {
        uint32_t p[2] = {8 | 1 << 16, f};
        auto call = [&] { nvcall(f ? "flags set" : "flags reset", nv.opts ? nv.opts(dev5, p) : -1); };
        if (other_thread) std::thread(call).join();
        else call();
    };
    auto set = [&](char mode) {
        if (mode == 't') nvcall("thread set", nv.slot_thread(dev, 0, 1001));
        if (mode == 'u') nvcall("thread set", nv.slot_thread(dev, 0, 404));
        if (mode == 'd') nvcall("device set", nv.slot(dev, 0, 1001));
        if (mode == 'o' || mode == 'O') flags(0x11, mode == 'O');
    };
    auto reset = [&](char mode) {
        if (mode == 't' || mode == 'u') nvcall("thread reset", nv.slot_thread(dev, ~0u, 0));
        if (mode == 'd') nvcall("device reset", nv.slot(dev, ~0u, 0));
        if (mode == 'o' || mode == 'O') flags(0, mode == 'O');
    };
    const bool e2e = proc >= 3;
    std::vector<size_t> order;
    for (size_t i = 0; i < std::size(kNvRows); ++i)
        if (kNvRows[i].phase == (int)e2e && (proc == 2 || proc == 5 || kNvRows[i].a != '-')) order.push_back(i);
    if (proc == 2) std::stable_partition(order.begin(), order.end(), [](size_t i) { return !strcmp(kNvRows[i].name + 2, "t>t"); });
    order.insert(order.begin(), {SIZE_MAX, SIZE_MAX - 1});  // a fresh collection and PSO first, untimed: the compiler's start-up
    for (size_t i : order) {
        const bool warmup = i >= SIZE_MAX - 1;
        const NvRow& row = warmup ? NvRow{"", i == SIZE_MAX ? 'R' : 'P', 'n', 'n', 0} : kNvRows[i];
        const char mode = proc == 1 || proc == 3 ? row.a : row.b;
        const unsigned k = warmup ? seed + 5000 + proc * 10 + (unsigned)(SIZE_MAX - i) : seed + (unsigned)i * 10;
        double t = -1;
        if (row.kind == 'R') {
            std::string K = std::to_string(k) + ".0", lib;
            CHECK(dxc_compile(comp,
                              "struct P { float4 c; }; cbuffer L : register(b0, space1) { float4 lk; };\n"
                              "[shader(\"closesthit\")] void CH(inout P p, in BuiltInTriangleIntersectionAttributes a) { float3 x = float3(a.barycentrics, " + K +
                                  "); [unroll] for (int i = 0; i < 24; ++i) x = sin(x * 1.37 + float3(i, " + K + ", x.y)) * cos(x.zxy + " + K +
                                  "); p.c = float4(x, 1) * lk; }\n",
                              lib));
            D3D12_DXIL_LIBRARY_DESC ld = {{lib.data(), lib.size()}, 0, nullptr};
            D3D12_HIT_GROUP_DESC hg = {L"HG", D3D12_HIT_GROUP_TYPE_TRIANGLES, nullptr, L"CH", nullptr};
            D3D12_RAYTRACING_SHADER_CONFIG sc = {16, 8};
            D3D12_RAYTRACING_PIPELINE_CONFIG pc = {1};
            D3D12_GLOBAL_ROOT_SIGNATURE g = {grs};
            D3D12_STATE_SUBOBJECT subs[] = {{D3D12_STATE_SUBOBJECT_TYPE_DXIL_LIBRARY, &ld}, {D3D12_STATE_SUBOBJECT_TYPE_HIT_GROUP, &hg},
                                            {D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_SHADER_CONFIG, &sc},
                                            {D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_PIPELINE_CONFIG, &pc},
                                            {D3D12_STATE_SUBOBJECT_TYPE_GLOBAL_ROOT_SIGNATURE, &g}};
            D3D12_STATE_OBJECT_DESC d = {D3D12_STATE_OBJECT_TYPE_COLLECTION, (UINT)std::size(subs), subs};
            ID3D12StateObject* so = nullptr;
            HRESULT hr = E_FAIL;
            set(mode);
            double x = ms([&] { hr = dev5->CreateStateObject(&d, IID_PPV_ARGS(&so)); });
            reset(mode);
            if (SUCCEEDED(hr)) so->Release(), t = x;
            else printf("  p%d: %s: CreateStateObject hr=0x%08x\n", proc, row.name, (unsigned)hr);
        } else if (row.kind == 'A') {
            SoOne base(comp, k, grs, D3D12_STATE_OBJECT_TYPE_RAYTRACING_PIPELINE), add(comp, k + 1, grs, D3D12_STATE_OBJECT_TYPE_RAYTRACING_PIPELINE);
            CHECK(!base.lib.empty() && !add.lib.empty());
            ID3D12StateObject *b = nullptr, *so = nullptr;
            HRESULT hr = E_NOINTERFACE;
            double x = 0;
            set(mode);
            if (dev7 && SUCCEEDED(hr = dev5->CreateStateObject(&base.d, IID_PPV_ARGS(&b)))) x = ms([&] { hr = dev7->AddToStateObject(&add.d, b, IID_PPV_ARGS(&so)); });
            reset(mode);
            if (SUCCEEDED(hr)) so->Release(), t = x;
            else printf("  p%d: %s: state object or addition hr=0x%08x\n", proc, row.name, (unsigned)hr);
            if (b) b->Release();
        } else {
            std::string vs, ps;
            CHECK(dxc_compile(comp, fill_src('v', k, 32), vs, L"vs_6_0") && dxc_compile(comp, fill_src('p', k, 32), ps, L"ps_6_0"));
            auto d = base;
            d.VS = {vs.data(), vs.size()}, d.PS = {ps.data(), ps.size()};
            ID3D12PipelineState* pso = nullptr;
            HRESULT hr = E_FAIL;
            if (mode == 'x') {
                NvPsoExt e = {8 | 1 << 16, 5, (uint32_t)sizeof e | 1 << 16, 0, 1001};
                const void* exts[] = {&e};
                int st = 0;
                t = ms([&] { st = nv.gfx(dev, &d, 1, exts, &pso); });
                nvcall("NvAPI_D3D12_CreateGraphicsPipelineState", st);
                if (st || !pso) t = -1;
                else pso->Release();
            } else {
                set(mode);
                double x = ms([&] { hr = dev->CreateGraphicsPipelineState(&d, IID_PPV_ARGS(&pso)); });
                reset(mode);
                if (SUCCEEDED(hr)) pso->Release(), t = x;
                else printf("  p%d: %s: CreateGraphicsPipelineState hr=0x%08x\n", proc, row.name, (unsigned)hr);
            }
        }
        if (!warmup) fprintf(f, "%s\t%.3f\n", row.name, t);
    }
    fclose(f);
    return 0;
}

static bool process_running(const wchar_t* exe) {
    HANDLE s = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    PROCESSENTRY32W e = {sizeof e};
    bool found = false;
    for (BOOL ok = Process32FirstW(s, &e); ok && !found; ok = Process32NextW(s, &e)) found = !_wcsicmp(e.szExeFile, exe);
    CloseHandle(s);
    return found;
}

static int nvext_parent(const std::wstring& a, const std::wstring& dir, int runs) {
    if (GetEnvironmentVariableW(L"SELFTEST_WARP", nullptr, 0) || !NvApi().load()) return printf("nvext: skipped, it needs the NVIDIA driver (NVAPI)\n"), 0;
    if (process_running(L"scskiller_warm.exe")) return printf("GPU busy: scskiller_warm.exe is running\n"), 1;
    std::wstring lock = gpu_lock("selftest nvext");
    if (lock.empty()) return 1;
    std::wstring stamp = std::to_wstring(GetTickCount() % 1000000), w = dir + L"nvext\\", x = dir + L"scsknv" + stamp + L"x.exe",
                 r = w + L"scsknv" + stamp + L"r.exe", y = w + L"scsknv" + stamp + L"y.exe", out = dir + L"nvext.txt";
    CreateDirectoryW(w.c_str(), nullptr);
    CHECK(CopyFileW(a.c_str(), x.c_str(), FALSE) && CopyFileW(a.c_str(), r.c_str(), FALSE) && CopyFileW(a.c_str(), y.c_str(), FALSE));
    CHECK(CopyFileW((a.substr(0, a.find_last_of(L'\\') + 1) + L"d3d12.dll").c_str(), (w + L"d3d12.dll").c_str(), FALSE));
    const DxrSnap start = dxr_snap();
    Col col[2];  // first mode's process, second mode's process
    std::random_device rd;
    for (int run_i = 0; run_i < runs; ++run_i) {
        unsigned seed = 100000 + rd() % 8000000;
        for (auto f : {L"scskiller.db", L"scskiller.log", L"scskiller_creates.csv"}) DeleteFileW((w + f).c_str());
        for (int p = 1; p <= 5; ++p) {
            int rc = run(p <= 2 ? x : p == 3 ? r : y, L"nvextchild " + std::to_wstring(p) + L" " + std::to_wstring(seed) + L" \"" + out + L"\"");
            if (rc) printf("  p%d exited with %d\n", p, rc);
            bool read = read_rows(out, nullptr, [&](const std::string& name, const char* rest) {
                if (name[0] == '#') printf("  run %d: warm %s", run_i + 1, rest);
                else col[p == 2 || p == 5][name].push_back(atof(rest));
            });
            if (!read) continue;
            if (p == 3) {  // what the recorder wrote: 'N' records per target tag
                std::map<char, int> tags;
                if (FILE* db = _wfopen((w + L"scskiller.db").c_str(), L"rb")) {
                    for (int tag; (tag = fgetc(db)) != EOF;) {
                        uint32_t n;
                        if (fread(&n, 4, 1, db) != 1) break;
                        ++tags[(char)tag];
                        uint32_t nv[4];  // an 'N' (key[20] + slot, space, scope, flags): one with flags counts as 'F' too
                        if (tag == 'N' && n == 36 && !_fseeki64(db, 20, SEEK_CUR) && fread(nv, 4, 4, db) == 4) tags['F'] += nv[3] != 0;
                        else _fseeki64(db, n, SEEK_CUR);
                    }
                    fclose(db);
                }
                printf("  run %d: recording:", run_i + 1);
                for (auto& [t, n] : tags) printf(" %c=%d", t, n);
                printf("\n");
            }
        }
    }
    auto med = [&](int c, const std::string& n) { return col_median(col[c], n); };
    printf("nvext: create ms, median of %d run(s); first = the row's first mode (p1 / p3), second = its second mode (p2 / p5)\n", runs);
    printf("%-12s %9s %9s\n", "row", "first", "second");
    for (auto& row : kNvRows) printf("%-12s %9.2f %9.2f\n", row.name, med(0, row.name), med(1, row.name));
    list_new_cache(start);
    return 0;
}

// `selftest cacheprobe`: what does NVIDIA's DXCache keep of a PSO (root signature? shader bytes? hashes?)
// Can SCSKiller learn a game's root signatures from the cache the game wrote, without the proxy? A throwaway exe name
// (scskcp<seed>.exe) creates PSOs with known, marked root signatures and shaders; the cache files it creates are copied
// to <run folder>\cacheprobe\ after each process (the folder is kept), to be searched for the blobs.
//   p1: RS A (v1.0) + RS B (v1.1); VS+PS and CS in DXBC (fxc) and, with dxcompiler, DXIL; each blob written to <out>.
//   p2: the same again (cached?), + the DXBC CS on RS C and on RS D (a used binding moved): the p1 -> p2 diff isolates
//   their records. `selftest cacheprobechild 0 <seed> <dir>` writes only the blobs (no device), to re-derive them.
// Markers: static sampler s5 in space 0x5CA1AB1E / 2E / 3E (A / B / C, no shader reads it), root constants b3 in space
// <seed>; shader constants 0x3FC0FFEE-style (see cp_src), + the seed so every run is cold. System d3d12.dll, no proxy.
static const uint32_t kCpSpace[4] = {0x5CA1AB1E, 0x5CA1AB2E, 0x5CA1AB3E, 0x5CA1AB4E};

static std::string cp_src(char stage, uint32_t mark, unsigned seed) {
    std::string m = "asfloat(" + std::to_string(mark) + "u)", k = std::to_string(seed) + ".0";
    if (stage == 'v') return "cbuffer C : register(b0) { float4 kc; }; float4 main(float3 p : POSITION) : SV_Position"
                             "{ return float4(p * " + m + " + " + k + ", 1) + kc; }";
    if (stage == 'p') return "Texture2D t : register(t0); SamplerState s : register(s0); cbuffer C : register(b0) { float4 kc; };"
                             "float4 main(float4 pos : SV_Position) : SV_Target { return t.Sample(s, pos.xy) * " + m + " + " + k + " + kc; }";
    return "RWBuffer<uint> b : register(u0); cbuffer C : register(b0) { uint4 kc; };"
           "[numthreads(8,1,1)] void main(uint i : SV_DispatchThreadID) { b[i] = kc.x * " + std::to_string(mark) + "u + " + std::to_string(seed) + "u; }";
}

static int cacheprobe_child(int proc, unsigned seed, const std::wstring& out) {
    HMODULE m = load_system(L"d3d12.dll");
    CHECK(m);
    auto create_device = (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice");
    auto ser = (PFN_D3D12_SERIALIZE_VERSIONED_ROOT_SIGNATURE)GetProcAddress(m, "D3D12SerializeVersionedRootSignature");
    CHECK(create_device && ser);
    ID3D12Device* dev = nullptr;
    if (proc) CHECK(SUCCEEDED(create_device(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))));  // p0: blobs only
    auto save = [&](const std::string& name, const void* p, size_t n) {
        FILE* f = _wfopen((out + L"\\" + std::wstring(name.begin(), name.end())).c_str(), L"wb");
        if (!f) return printf("  can't write %s\n", name.c_str()), void();
        fwrite(p, 1, n, f), fclose(f);
    };

    // RS i: [0] b0 constants x4, [1] PIXEL t0 table, [2] u0 table, [3] b3 constants x3 in space <seed>; s0 linear + s5 marker.
    // A-C differ only where no shader looks (s5's space, 1.0 vs 1.1); D moves a used binding (b0 to param 3, b3 to 0).
    ID3D12RootSignature* rs[4] = {};
    for (int i = 0; i < 4; ++i) {
        D3D12_DESCRIPTOR_RANGE1 r[2] = {{D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 0, 0, D3D12_DESCRIPTOR_RANGE_FLAG_DATA_VOLATILE, 0},
                                        {D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 1, 0, 0, D3D12_DESCRIPTOR_RANGE_FLAG_DATA_VOLATILE, 0}};
        D3D12_ROOT_PARAMETER1 p[4] = {};
        p[0].ParameterType = p[3].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        p[0].Constants = {0, 0, 4}, p[3].Constants = {3, seed, 3};
        p[1].ParameterType = p[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        p[1].DescriptorTable = {1, &r[0]}, p[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL, p[2].DescriptorTable = {1, &r[1]};
        D3D12_STATIC_SAMPLER_DESC smp[2] = {};
        for (auto& s : smp)
            s = {D3D12_FILTER_MIN_MAG_MIP_LINEAR, D3D12_TEXTURE_ADDRESS_MODE_WRAP, D3D12_TEXTURE_ADDRESS_MODE_WRAP, D3D12_TEXTURE_ADDRESS_MODE_WRAP,
                 0, 1, D3D12_COMPARISON_FUNC_NEVER, D3D12_STATIC_BORDER_COLOR_OPAQUE_BLACK, 0, D3D12_FLOAT32_MAX, 0, 0, D3D12_SHADER_VISIBILITY_ALL};
        smp[1].Filter = D3D12_FILTER_ANISOTROPIC, smp[1].MaxAnisotropy = 13, smp[1].MipLODBias = 1.25f, smp[1].ShaderRegister = 5;
        smp[1].RegisterSpace = kCpSpace[i];
        D3D12_VERSIONED_ROOT_SIGNATURE_DESC vd = {D3D_ROOT_SIGNATURE_VERSION_1_1};
        if (i == 3) std::swap(p[0], p[3]);
        vd.Desc_1_1 = {4, p, 2, smp, D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT};
        D3D12_ROOT_PARAMETER p0[4];
        D3D12_DESCRIPTOR_RANGE r0[2] = {{r[0].RangeType, 1, 0, 0, 0}, {r[1].RangeType, 1, 0, 0, 0}};
        if (i != 1) {  // A, C, D: version 1.0
            for (int j = 0; j < 4; ++j) p0[j] = {p[j].ParameterType, {}, p[j].ShaderVisibility}, p0[j].Constants = p[j].Constants;
            p0[1].DescriptorTable = {1, &r0[0]}, p0[2].DescriptorTable = {1, &r0[1]};  // tables stay params 1 and 2 in D
            vd.Version = D3D_ROOT_SIGNATURE_VERSION_1_0, vd.Desc_1_0 = {4, p0, 2, smp, vd.Desc_1_1.Flags};
        }
        ID3DBlob *b = nullptr, *err = nullptr;
        if (FAILED(ser(&vd, &b, &err))) return printf("  RS %d: serialize failed: %s\n", i, err ? (const char*)err->GetBufferPointer() : ""), 1;
        if (dev) CHECK(SUCCEEDED(dev->CreateRootSignature(0, b->GetBufferPointer(), b->GetBufferSize(), IID_PPV_ARGS(&rs[i]))));
        save("rs_" + std::string(1, char('a' + i)) + ".bin", b->GetBufferPointer(), b->GetBufferSize());
    }

    // Shaders: DXBC (fxc) and DXIL (dxc, when found). Markers: DXBC VS 0x3FC0FFEE, PS 0x3FBEEF01, CS 0xC0DE1234;
    // DXIL VS 0x3FC0FFE1, PS 0x3FBEEF02, CS 0xC0DE5678.
    struct Set { std::string vs, ps, cs; };
    std::vector<std::pair<std::string, Set>> sets;
    ID3DBlob *v = compile(cp_src('v', 0x3FC0FFEE, seed), "vs_5_0"), *pb = compile(cp_src('p', 0x3FBEEF01, seed), "ps_5_0"),
             *cb = compile(cp_src('c', 0xC0DE1234, seed), "cs_5_0");
    CHECK(v && pb && cb);
    auto str = [](ID3DBlob* b) { return std::string((const char*)b->GetBufferPointer(), b->GetBufferSize()); };
    sets.push_back({"dxbc", {str(v), str(pb), str(cb)}});
    IDxcCompiler3* comp = dxc_compiler();
    CHECK(comp || !GetEnvironmentVariableW(L"SELFTEST_DXC", nullptr, 0));  // the parent found one: this child must load it
    if (comp) {
        Set s;
        if (dxc_compile(comp, cp_src('v', 0x3FC0FFE1, seed), s.vs, L"vs_6_0") && dxc_compile(comp, cp_src('p', 0x3FBEEF02, seed), s.ps, L"ps_6_0") &&
            dxc_compile(comp, cp_src('c', 0xC0DE5678, seed), s.cs, L"cs_6_0"))
            sets.push_back({"dxil", s});
    }
    D3D12_INPUT_ELEMENT_DESC el = {"POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0};
    auto gfx = [&](const Set& s, ID3D12RootSignature* r) {
        D3D12_GRAPHICS_PIPELINE_STATE_DESC d = {};
        d.pRootSignature = r, d.VS = {s.vs.data(), s.vs.size()}, d.PS = {s.ps.data(), s.ps.size()};
        d.BlendState.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;
        d.SampleMask = UINT_MAX, d.RasterizerState = {D3D12_FILL_MODE_SOLID, D3D12_CULL_MODE_NONE};
        d.RasterizerState.DepthClipEnable = TRUE;
        d.InputLayout = {&el, 1}, d.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
        d.NumRenderTargets = 1, d.RTVFormats[0] = DXGI_FORMAT_R8G8B8A8_UNORM, d.SampleDesc.Count = 1;
        ID3D12PipelineState* pso = nullptr;
        double t = ms([&] { dev->CreateGraphicsPipelineState(&d, IID_PPV_ARGS(&pso)); });
        return pso ? (pso->Release(), t) : -1.0;
    };
    auto cmp = [&](const std::string& cs, ID3D12RootSignature* r) {
        D3D12_COMPUTE_PIPELINE_STATE_DESC d = {r, {cs.data(), cs.size()}};
        ID3D12PipelineState* pso = nullptr;
        double t = ms([&] { dev->CreateComputePipelineState(&d, IID_PPV_ARGS(&pso)); });
        return pso ? (pso->Release(), t) : -1.0;
    };
    for (auto& [kind, s] : sets)
        save("vs." + kind, s.vs.data(), s.vs.size()), save("ps." + kind, s.ps.data(), s.ps.size()), save("cs." + kind, s.cs.data(), s.cs.size());
    if (!dev) return 0;
    printf("  p%d (seed %u), create ms (-1 = failed):\n", proc, seed);
    for (auto& [kind, s] : sets) {
        printf("    %s VS+PS on RS A %7.2f   CS on RS A %7.2f   CS on RS B %7.2f\n", kind.c_str(), gfx(s, rs[0]), cmp(s.cs, rs[0]), cmp(s.cs, rs[1]));
    }
    if (proc == 2) {  // C before D (records in this order)
        double c = cmp(sets[0].second.cs, rs[2]);
        printf("    new in p2: dxbc CS on RS C %7.2f, on RS D %7.2f\n", c, cmp(sets[0].second.cs, rs[3]));
    }
    for (auto r : rs) r->Release();
    dev->Release();
    return 0;
}

// Copies the DXCache files one process created or changed to <out>\p<n>_<name>, and lists the new ones.
static int cacheprobe_parent(const std::wstring& a, const std::wstring& dir) {
    std::random_device rd;  // no GPU lock: 9 small PSOs, correctness only
    unsigned seed = 100000 + rd() % 8000000;
    std::wstring exe = dir + L"scskcp" + std::to_wstring(seed) + L".exe", out = dir + L"cacheprobe\\";
    CreateDirectoryW(out.c_str(), nullptr);
    CHECK(CopyFileW(a.c_str(), exe.c_str(), FALSE));
    auto nv = [] {
        DxrSnap s;
        for (auto& [p, v] : dxr_snap())
            if (p.find(L"\\NVIDIA\\DXCache\\") != std::wstring::npos) s[p] = v;
        return s;
    };
    DxrSnap before = nv(), prev = before;
    std::set<std::wstring> created;
    int r = 0;
    for (int proc = 1; proc <= 2 && !r; ++proc) {
        r = run(exe, L"cacheprobechild " + std::to_wstring(proc) + L" " + std::to_wstring(seed) + L" \"" + out + L".\"");  // "\." : "\"" would escape the quote
        DxrSnap now = nv();
        dxr_diff(prev, now, proc == 1 ? "p1" : "p2");
        for (auto& [p, v] : now) {
            auto it = prev.find(p);
            if (it != prev.end() && it->second == v) continue;
            std::wstring name = p.substr(p.find_last_of(L'\\') + 1);
            CopyFileW(p.c_str(), (out + L"p" + std::to_wstring(proc) + L"_" + name).c_str(), FALSE);
            if (!before.count(p)) created.insert(p);
        }
        prev = now;
    }
    DeleteFileW(exe.c_str());
    for (auto& p : created) printf("  new, left in place: %ls\n", p.c_str());  // never deleted: see list_new_cache
    printf("cacheprobe seed %u -> %ls\n", seed, out.c_str());
    return r;
}

// `selftest bindless [runs] [exe name]`: does NVIDIA's cache keep compute PSOs whose root signature has the heap
// directly-indexed flags (SM 6.6 ResourceDescriptorHeap[]), and does the D3D12 runtime matter? p1 creates each row, releases
// it and creates it again; then p2 (same exe name) does the same. Synthetic rows, fresh per run: ctrl (a u0 table), flags
// (ctrl's CS on its RS + both heap flags), heap (ResourceDescriptorHeap[]), rq (RayQuery on a root SRV), rq+heap (RayQuery on
// an acceleration structure from the heap). SELFTEST_BINDLESS=<dir>: a row per <label>.cs + <label>.rs pair there (a recorded
// pipeline's shader and root signature). SELFTEST_AGILITY=<dir with an Agility SDK D3D12Core.dll>: everything again on that
// runtime (its copy in a folder next to the exe, through ID3D12SDKConfiguration1::CreateDeviceFactory; the loader takes the
// system's runtime instead when that one is newer, so p1 prints the D3D12Core.dll it got). With an exe name the
// children run under it (e.g. one a warm just filled), create only the SELFTEST_BINDLESS rows.
static const char* kBlRows[] = {"ctrl", "flags", "heap", "rq", "rq+heap"};

static std::string bl_src(int row, unsigned seed) {
    std::string K = std::to_string(seed) + ".0";
    std::string s = row == 2 || row == 4 ? "cbuffer C : register(b0) { uint idx; };\n" : "cbuffer C : register(b0) { uint idx; }; RWBuffer<float> b : register(u0);\n";
    if (row == 3) s += "RaytracingAccelerationStructure as : register(t0);\n";
    s += "[numthreads(64, 1, 1)] void main(uint i : SV_DispatchThreadID) {\n";
    if (row == 2 || row == 4) s += "  RWBuffer<float> b = ResourceDescriptorHeap[idx];\n";
    if (row == 4) s += "  RaytracingAccelerationStructure as = ResourceDescriptorHeap[idx + 1];\n";
    s += "  float4 x = float4(i, idx, " + K + ", 1);\n"
         "  [unroll] for (int k = 0; k < 48; ++k) x = sin(x * 1.37 + float4(k, " + K + ", x.y, x.w)) * cos(x.zxyw + " + K + ");\n";
    if (row >= 3)
        s += "  RayQuery<RAY_FLAG_NONE> q; RayDesc r; r.Origin = x.xyz; r.Direction = normalize(x.yzw + 2); r.TMin = 0; r.TMax = 1e4;\n"
             "  q.TraceRayInline(as, 0, 0xff, r);\n"
             "  while (q.Proceed()) if (q.CandidateType() == CANDIDATE_NON_OPAQUE_TRIANGLE) q.CommitNonOpaqueTriangleHit();\n"
             "  x.x += q.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? q.CommittedRayT() : -1;\n";
    return s + "  b[i] = dot(x, x);\n}\n";
}

static std::string bl_cores() {
    std::string r;
    HANDLE s = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, GetCurrentProcessId());
    MODULEENTRY32W e = {sizeof e};
    for (BOOL ok = Module32FirstW(s, &e); ok; ok = Module32NextW(s, &e))
        if (!_wcsicmp(e.szModule, L"D3D12Core.dll")) {
            auto v = (const UINT*)GetProcAddress(e.hModule, "D3D12SDKVersion");
            char b[400];
            snprintf(b, sizeof b, "%s%ls (SDK %u)", r.empty() ? "" : ", ", e.szExePath, v ? *v : 0);
            r += b;
        }
    CloseHandle(s);
    return r;
}

// rt 's': the system runtime; 'a': the Agility runtime in <exe dir>\scskbl_d3d12\ (SDK version sdk). !synthetic: only the
// SELFTEST_BINDLESS rows, so a real game's cache gets nothing but its own pipelines.
static int bindless_child(int proc, unsigned seed, char rt, UINT sdk, const std::wstring& out, bool synthetic) {
    HMODULE m = load_system(L"d3d12.dll");
    CHECK(m);
    auto create_device = (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice");
    auto get_interface = (decltype(&D3D12GetInterface))GetProcAddress(m, "D3D12GetInterface");
    auto ser = (PFN_D3D12_SERIALIZE_VERSIONED_ROOT_SIGNATURE)GetProcAddress(m, "D3D12SerializeVersionedRootSignature");
    CHECK(create_device && get_interface && ser);
    ID3D12Device* dev = nullptr;
    if (rt == 'a') {
        ID3D12SDKConfiguration1* cfg = nullptr;
        ID3D12DeviceFactory* fac = nullptr;
        CHECK(SUCCEEDED(get_interface(CLSID_D3D12SDKConfiguration, IID_PPV_ARGS(&cfg))));
        HRESULT hr = cfg->CreateDeviceFactory(sdk, ".\\scskbl_d3d12\\", IID_PPV_ARGS(&fac));
        if (FAILED(hr)) return printf("  CreateDeviceFactory(%u) hr=0x%08x\n", sdk, (unsigned)hr), 1;
        CHECK(SUCCEEDED(fac->CreateDevice(nullptr, D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&dev))));
    } else CHECK(SUCCEEDED(create_device(nullptr, D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&dev))));
    if (proc == 1) printf("  runtime %c: %s\n", rt, bl_cores().c_str());
    IDxcCompiler3* comp = dxc_compiler();
    CHECK(comp);
    FILE* f = _wfopen(out.c_str(), L"w");
    CHECK(f);

    auto make_rs = [&](int row) -> ID3D12RootSignature* {
        D3D12_DESCRIPTOR_RANGE1 u = {D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 1, 0, 0, D3D12_DESCRIPTOR_RANGE_FLAG_DATA_VOLATILE, 0};
        D3D12_ROOT_PARAMETER1 p[3] = {};
        p[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS, p[0].Constants = {0, 0, 1};
        p[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE, p[1].DescriptorTable = {1, &u};
        p[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV, p[2].Descriptor = {0, 0, D3D12_ROOT_DESCRIPTOR_FLAG_NONE};
        const bool heap = row == 1 || row == 2 || row == 4;
        D3D12_VERSIONED_ROOT_SIGNATURE_DESC vd = {D3D_ROOT_SIGNATURE_VERSION_1_1};
        vd.Desc_1_1 = {row == 2 || row == 4 ? 1u : row == 3 ? 3u : 2u, p, 0, nullptr,
                       heap ? D3D12_ROOT_SIGNATURE_FLAG_CBV_SRV_UAV_HEAP_DIRECTLY_INDEXED | D3D12_ROOT_SIGNATURE_FLAG_SAMPLER_HEAP_DIRECTLY_INDEXED
                            : D3D12_ROOT_SIGNATURE_FLAG_NONE};
        ID3DBlob *b = nullptr, *err = nullptr;
        ID3D12RootSignature* rs = nullptr;
        if (FAILED(ser(&vd, &b, &err))) return printf("  %s: serialize: %s\n", kBlRows[row], err ? (const char*)err->GetBufferPointer() : ""), nullptr;
        dev->CreateRootSignature(0, b->GetBufferPointer(), b->GetBufferSize(), IID_PPV_ARGS(&rs));
        return rs;
    };
    auto twice = [&](const char* name, ID3D12RootSignature* rs, const std::string& cs) {
        double t[2] = {-1, -1};
        for (double& x : t) {
            D3D12_COMPUTE_PIPELINE_STATE_DESC d = {rs, {cs.data(), cs.size()}};
            ID3D12PipelineState* pso = nullptr;
            HRESULT hr = E_FAIL;
            double ms_ = ms([&] { hr = dev->CreateComputePipelineState(&d, IID_PPV_ARGS(&pso)); });
            if (SUCCEEDED(hr)) pso->Release(), x = ms_;
            else if (&x == t) printf("  p%d: %s: CreateComputePipelineState hr=0x%08x\n", proc, name, (unsigned)hr);
        }
        fprintf(f, "%s\t%.3f\t%.3f\n", name, t[0], t[1]);
    };

    if (synthetic) {  // the compiler's start-up, untimed; the same in p2, so what p2 adds to the cache comes from the rows
        std::string cs;
        ID3D12RootSignature* rs = make_rs(0);
        CHECK(rs && dxc_compile(comp, bl_src(0, seed + 7000), cs, L"cs_6_6"));
        D3D12_COMPUTE_PIPELINE_STATE_DESC d = {rs, {cs.data(), cs.size()}};
        ID3D12PipelineState* pso = nullptr;
        if (SUCCEEDED(dev->CreateComputePipelineState(&d, IID_PPV_ARGS(&pso)))) pso->Release();
    }
    for (int row = 0; synthetic && row < (int)std::size(kBlRows); ++row) {
        std::string cs;
        ID3D12RootSignature* rs = make_rs(row);
        if (!rs || !dxc_compile(comp, bl_src(row == 1 ? 0 : row, seed + (row == 1 ? 0 : row)), cs, L"cs_6_6")) {
            fprintf(f, "%s\t-1\t-1\n", kBlRows[row]);
            continue;
        }
        twice(kBlRows[row], rs, cs);
        rs->Release();
    }

    wchar_t real[MAX_PATH];
    if (GetEnvironmentVariableW(L"SELFTEST_BINDLESS", real, MAX_PATH)) {
        std::error_code ec;
        std::vector<std::filesystem::path> css;
        for (auto& e : std::filesystem::directory_iterator(real, ec))
            if (e.path().extension() == L".cs") css.push_back(e.path());
        std::sort(css.begin(), css.end());
        auto slurp = [](const std::filesystem::path& p) {
            std::string s;
            if (FILE* x = _wfopen(p.c_str(), L"rb")) {
                s.resize((size_t)std::filesystem::file_size(p));
                s.resize(fread(s.data(), 1, s.size(), x));
                fclose(x);
            }
            return s;
        };
        for (auto& p : css) {
            std::string name = p.stem().string(), cs = slurp(p), rsb = slurp(std::filesystem::path(p).replace_extension(L".rs"));
            ID3D12RootSignature* rs = nullptr;
            if (rsb.empty() || FAILED(dev->CreateRootSignature(0, rsb.data(), rsb.size(), IID_PPV_ARGS(&rs)))) {
                printf("  %s: no root signature\n", name.c_str());
                continue;
            }
            twice(name.c_str(), rs, cs);
            rs->Release();
        }
    }
    fclose(f);
    dev->Release();
    return 0;
}

// Bytes in use of each NVIDIA DXCache file (the u64 at offset 8 of a .nvph).
static std::map<std::wstring, uint64_t> nv_used() {
    std::map<std::wstring, uint64_t> r;
    for (auto& [p, v] : dxr_snap()) {
        if (p.find(L"\\NVIDIA\\DXCache\\") == std::wstring::npos) continue;
        HANDLE h = CreateFileW(p.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
        uint64_t hdr[2] = {};
        DWORD n = 0;
        if (h != INVALID_HANDLE_VALUE) ReadFile(h, hdr, sizeof hdr, &n, nullptr), CloseHandle(h);
        r[p] = hdr[1];
    }
    return r;
}

static void nv_used_diff(const std::map<std::wstring, uint64_t>& a, const std::map<std::wstring, uint64_t>& b, const char* when) {
    printf("  DXCache after %s:", when);
    int n = 0;
    for (auto& [p, u] : b) {
        auto it = a.find(p);
        if (it != a.end() && it->second == u) continue;
        printf("\n    %ls used %llu -> %llu", p.c_str() + p.find_last_of(L'\\') + 1, it == a.end() ? 0ull : (unsigned long long)it->second, (unsigned long long)u), ++n;
    }
    printf(n ? "\n" : " no change\n");
}

static int bindless_parent(const std::wstring& a, const std::wstring& dir, int runs, const std::wstring& name) {
    if (process_running(L"scskiller_warm.exe")) return printf("GPU busy: scskiller_warm.exe is running\n"), 1;
    if (name.find_first_of(L"\\/:") != std::wstring::npos || name.find(L"..") != std::wstring::npos || name == L".")
        return printf("bindless: %ls is not a file name\n", name.c_str()), 1;
    std::wstring lock = gpu_lock("selftest bindless");
    if (lock.empty()) return 1;
    std::wstring core = dir + L"scskbl_d3d12\\", out = dir + L"bindless.txt";
    std::string rts = "s";
    UINT sdk = 0;
    wchar_t ag[MAX_PATH];
    if (GetEnvironmentVariableW(L"SELFTEST_AGILITY", ag, MAX_PATH)) {
        CreateDirectoryW(core.c_str(), nullptr);
        CHECK(CopyFileW((std::wstring(ag) + L"\\D3D12Core.dll").c_str(), (core + L"D3D12Core.dll").c_str(), FALSE));
        HMODULE c = LoadLibraryExW((core + L"D3D12Core.dll").c_str(), nullptr, DONT_RESOLVE_DLL_REFERENCES);
        auto v = c ? (const UINT*)GetProcAddress(c, "D3D12SDKVersion") : nullptr;
        CHECK(v);
        sdk = *v, rts += 'a';
        FreeLibrary(c);
    }
    const DxrSnap start = dxr_snap();
    std::map<char, Col[4]> cols;  // per runtime: p1, p1 again, p2, p2 again
    std::vector<std::string> order;
    std::random_device rd;
    for (int run_i = 0; run_i < runs; ++run_i)
        for (char rt : rts) {
            unsigned seed = 100000 + rd() % 8000000;
            std::wstring exe = dir + (name.empty() ? L"scskbl" + std::to_wstring(seed) + L".exe" : name);
            if (!CopyFileW(a.c_str(), exe.c_str(), TRUE)) return printf("%ls exists already (or can't be written): not replaced\n", exe.c_str()), 1;
            for (int p = 1; p <= 2; ++p) {
                auto before = nv_used();
                int rc = run(exe, L"bindlesschild " + std::to_wstring(p) + L" " + std::to_wstring(seed) + L" " + std::wstring(1, rt) + L" " +
                                      std::to_wstring(sdk) + L" \"" + out + L"\"" + (name.empty() ? L"" : L" real"));
                if (rc) printf("  p%d exited with %d\n", p, rc);
                nv_used_diff(before, nv_used(), (std::string("run ") + std::to_string(run_i + 1) + " " + rt + " p" + std::to_string(p)).c_str());
                read_rows(out, &order, [&](const std::string& n, const char* rest) {
                    char* e;
                    double t1 = strtod(rest, &e), t2 = strtod(e, nullptr);
                    cols[rt][2 * (p - 1)][n].push_back(t1), cols[rt][2 * (p - 1) + 1][n].push_back(t2);
                });
            }
            DeleteFileW(exe.c_str());
        }
    for (char rt : rts) {
        printf("bindless, runtime %s: create ms, median of %d run(s)\n", rt == 's' ? "system" : "Agility", runs);
        printf("%-10s %9s %9s %9s %9s\n", "row", "p1", "p1 again", "p2", "p2 again");
        for (auto& n : order) {
            printf("%-10s", n.c_str());
            for (auto& c : cols[rt]) printf("%s", cell(col_median(c, n)).c_str());
            printf("\n");
        }
    }
    list_new_cache(start);
    return 0;
}

// A VS + PS pair of the seed; the "modded" PS is what fakenext swaps in (a shader-replacing mod's).
static std::string modswap_src(char stage, unsigned seed) {
    std::string k = std::to_string(seed) + ".0";
    if (stage == 'v') return "float4 main(float3 p : POSITION) : SV_Position { return float4(p * " + k + ", 1); }";
    return "float4 main(float4 pos : SV_Position) : SV_Target { float3 x = pos.xyz * " + k + (stage == 'm' ? " + 0.5" : "") +
           "; [unroll] for (int i = 0; i < 48; ++i) x = sin(x * 1.37 + float3(i, " + k + ", x.y)) * cos(x.zxy); return float4(x, 1); }";
}

static ID3D12PipelineState* modswap_pso(ID3D12Device* dev, PFN_D3D12_SERIALIZE_ROOT_SIGNATURE ser, unsigned seed, HRESULT& hr) {
    ID3DBlob *vs = compile(modswap_src('v', seed), "vs_5_0"), *ps = compile(modswap_src('p', seed), "ps_5_0");
    D3D12_ROOT_SIGNATURE_DESC rd = {0, nullptr, 0, nullptr, D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT};
    ID3DBlob *rb = nullptr, *err = nullptr;
    ID3D12RootSignature* rs = nullptr;
    hr = E_FAIL;
    if (!vs || !ps || FAILED(ser(&rd, D3D_ROOT_SIGNATURE_VERSION_1, &rb, &err)) ||
        FAILED(dev->CreateRootSignature(0, rb->GetBufferPointer(), rb->GetBufferSize(), IID_PPV_ARGS(&rs))))
        return nullptr;
    D3D12_INPUT_ELEMENT_DESC il = {"POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0};
    D3D12_GRAPHICS_PIPELINE_STATE_DESC d = {};
    d.pRootSignature = rs, d.VS = {vs->GetBufferPointer(), vs->GetBufferSize()}, d.PS = {ps->GetBufferPointer(), ps->GetBufferSize()};
    d.InputLayout = {&il, 1};
    d.BlendState.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;
    d.SampleMask = UINT_MAX;
    d.RasterizerState = {D3D12_FILL_MODE_SOLID, D3D12_CULL_MODE_NONE, FALSE, 0, 0.f, 0.f, TRUE, FALSE, FALSE, 0, D3D12_CONSERVATIVE_RASTERIZATION_MODE_OFF};
    d.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
    d.NumRenderTargets = 1, d.RTVFormats[0] = DXGI_FORMAT_R8G8B8A8_UNORM, d.SampleDesc = {1, 0};
    ID3D12PipelineState* pso = nullptr;
    hr = dev->CreateGraphicsPipelineState(&d, IID_PPV_ARGS(&pso));
    return pso;
}

// Hands fakenext (loaded as `mod`) the seed's modded PS: from then on it wraps the device it creates and swaps it in.
static bool modswap_arm(HMODULE mod, unsigned seed) {
    auto swap = mod ? (void(WINAPI*)(const void*, SIZE_T))GetProcAddress(mod, "FakeNext_SwapPs") : nullptr;
    ID3DBlob* m = compile(modswap_src('m', seed), "ps_5_0");
    if (!swap || !m) return false;
    swap(m->GetBufferPointer(), m->GetBufferSize());
    return true;
}

// `selftest chain <seed> [swap]`: a compute PSO created through the proxy d3d12.dll next to the exe on WARP (no GPU cache), with
// scskiller.ini's next= naming a mod's d3d12.dll (fakenext.dll renamed). Prints "next-calls <n>" (-1: that dll isn't loaded
// or isn't fakenext) and "created 0x<hr>"; the proxy records the PSO in scskiller.db either way. swap: a VS + PS graphics
// PSO instead, the mod replacing its PS (fakenext's wrapper device): the proxy records the game's and the modded desc.
static int chain_rows(const std::wstring& dir, unsigned seed, bool swap) {
    SetEnvironmentVariableW(L"SCSKILLER_MODE", L"record");
    HMODULE m = LoadLibraryW((dir + L"d3d12.dll").c_str());
    CHECK(m);
    auto create_device = (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice");
    auto ser = (decltype(&D3D12SerializeRootSignature))GetProcAddress(m, "D3D12SerializeRootSignature");
    IDXGIFactory4* f = nullptr;
    IDXGIAdapter* warp = nullptr;
    CHECK(create_device && ser && SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumWarpAdapter(IID_PPV_ARGS(&warp))));
    wchar_t next[64];
    GetPrivateProfileStringW(L"scskiller", L"next", L"", next, 64, (dir + L"scskiller.ini").c_str());
    HMODULE mod = *next ? GetModuleHandleW((dir + next).c_str()) : nullptr;
    CHECK(!swap || modswap_arm(mod, seed));
    ID3D12Device* dev = nullptr;
    CHECK(SUCCEEDED(create_device(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))));
    auto calls = mod ? (LONG(WINAPI*)())GetProcAddress(mod, "FakeNext_Calls") : nullptr;
    printf("next-calls %ld\n", calls ? calls() : -1L);
    if (swap) {
        HRESULT hr;
        ID3D12PipelineState* pso = modswap_pso(dev, ser, seed, hr);
        printf("created 0x%08x\n", (unsigned)hr);
        return pso ? 0 : 1;
    }
    ID3DBlob* cs = compile("RWByteAddressBuffer b : register(u0); [numthreads(1,1,1)] void main() { b.Store(0, " + std::to_string(seed) + "); }", "cs_5_0");
    CHECK(cs);
    D3D12_ROOT_PARAMETER up = {D3D12_ROOT_PARAMETER_TYPE_UAV};
    D3D12_ROOT_SIGNATURE_DESC rd = {1, &up};
    ID3DBlob *rb = nullptr, *err = nullptr;
    ID3D12RootSignature* rs = nullptr;
    CHECK(SUCCEEDED(ser(&rd, D3D_ROOT_SIGNATURE_VERSION_1, &rb, &err)) && SUCCEEDED(dev->CreateRootSignature(0, rb->GetBufferPointer(), rb->GetBufferSize(), IID_PPV_ARGS(&rs))));
    D3D12_COMPUTE_PIPELINE_STATE_DESC c = {};
    c.pRootSignature = rs, c.CS = {cs->GetBufferPointer(), cs->GetBufferSize()};
    ID3D12PipelineState* pso = nullptr;
    HRESULT hr = dev->CreateComputePipelineState(&c, IID_PPV_ARGS(&pso));
    printf("created 0x%08x\n", (unsigned)hr);
    return SUCCEEDED(hr) ? 0 : 1;
}

// `selftest layer <seed> [old]`: fakenext.dll as a layer like ReShade as dxgi.dll with a RenoDX addon (FakeNext_Layer: the
// system D3D12CreateDevice hands out a wrapper that adds a root constant to every root signature), loaded before the proxy,
// on WARP. Through the proxy: a compute PSO (the game's create), then one created on the real device directly (the layer's
// own, as a bind-time clone is). old: the wrapper doesn't answer IID_UnwrappedObject (ReShade before 6.8, other layers).
// Prints "created 0x<hr>" and "own 0x<hr>".
static int layer_rows(const std::wstring& dir, unsigned seed, bool old) {
    SetEnvironmentVariableW(L"SCSKILLER_MODE", L"record");
    HMODULE fake = LoadLibraryW((dir + L"fakenext.dll").c_str());
    auto layer = fake ? (BOOL(WINAPI*)(BOOL))GetProcAddress(fake, "FakeNext_Layer") : nullptr;
    CHECK(layer && layer(!old));
    HMODULE m = LoadLibraryW((dir + L"d3d12.dll").c_str());
    CHECK(m);
    auto create_device = (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice");
    auto ser = (decltype(&D3D12SerializeRootSignature))GetProcAddress(m, "D3D12SerializeRootSignature");
    IDXGIFactory4* f = nullptr;
    IDXGIAdapter* warp = nullptr;
    CHECK(create_device && ser && SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumWarpAdapter(IID_PPV_ARGS(&warp))));
    ID3D12Device* dev = nullptr;
    CHECK(SUCCEEDED(create_device(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))));
    D3D12_ROOT_PARAMETER up = {D3D12_ROOT_PARAMETER_TYPE_UAV};
    D3D12_ROOT_SIGNATURE_DESC rd = {1, &up};
    ID3DBlob *rb = nullptr, *err = nullptr;
    ID3D12RootSignature* rs = nullptr;
    CHECK(SUCCEEDED(ser(&rd, D3D_ROOT_SIGNATURE_VERSION_1, &rb, &err)) && SUCCEEDED(dev->CreateRootSignature(0, rb->GetBufferPointer(), rb->GetBufferSize(), IID_PPV_ARGS(&rs))));
    auto cs_pso = [&](ID3D12Device* d, unsigned s) {
        ID3DBlob* cs = compile("RWByteAddressBuffer b : register(u0); [numthreads(1,1,1)] void main() { b.Store(0, " + std::to_string(s) + "); }", "cs_5_0");
        D3D12_COMPUTE_PIPELINE_STATE_DESC c = {};
        c.pRootSignature = rs, c.CS = {cs->GetBufferPointer(), cs->GetBufferSize()};
        ID3D12PipelineState* pso = nullptr;
        return d->CreateComputePipelineState(&c, IID_PPV_ARGS(&pso));
    };
    HRESULT hr = cs_pso(dev, seed);
    printf("created 0x%08x\n", (unsigned)hr);
    ID3D12Device* real = nullptr;
    CHECK(SUCCEEDED(rs->GetDevice(IID_PPV_ARGS(&real))));  // the layer doesn't wrap root signatures
    HRESULT own = cs_pso(real, seed + 1);
    printf("own 0x%08x\n", (unsigned)own);
    return SUCCEEDED(hr) && SUCCEEDED(own) ? 0 : 1;
}

// `selftest layerwarm <kit dir> [n]`: on the GPU, under ReShade as dxgi.dll with a RenoDX addon that rewrites every root
// signature (the kit: dxgi.dll, *.addon64 and ReShade.ini from reshade.me and RenoDX's releases, never in the repo). Each
// phase runs this exe under its own throwaway name in its own folder, with the proxy and the kit: A records n compute PSOs
// the game creates through the layer and n the layer creates on the device under it (as bind-time clones are, "own");
// B is warmed by scskiller_warm from A's recording, C from it without what the layer changed (what the proxy recorded
// before it hooked under a layer), E like C but with the layer in the warm (--layer), F like B with it, D not at all; then
// each creates the same PSOs under the layer. Prints each one's create ms (median, max). The driver-cache files that
// appeared during the run are deleted at the end.
using Hash20 = std::array<uint8_t, 20>;
static Hash20 sha1_of(char tag, const std::string& p) {
    std::string s = tag + p;
    Hash20 h{};
    BCryptHash(BCRYPT_SHA1_ALG_HANDLE, nullptr, 0, (PUCHAR)s.data(), (ULONG)s.size(), h.data(), 20);
    return h;
}

static std::string read_all(const std::wstring& path) {
    std::string s;
    if (FILE* f = _wfopen(path.c_str(), L"rb")) {
        char b[65536];
        for (size_t n; (n = fread(b, 1, sizeof b, f)) > 0;) s.append(b, n);
        fclose(f);
    }
    return s;
}

static int layerwarm_child(const std::wstring& dir, unsigned seed, int n) {
    HMODULE m = LoadLibraryW((dir + L"d3d12.dll").c_str());  // ReShade (dxgi.dll, imported by this exe) is loaded by now
    CHECK(m);
    auto create_device = (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice");
    auto ser = (decltype(&D3D12SerializeRootSignature))GetProcAddress(m, "D3D12SerializeRootSignature");
    ID3D12Device* dev = nullptr;
    CHECK(create_device && ser && SUCCEEDED(create_device(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))));
    D3D12_ROOT_PARAMETER up = {D3D12_ROOT_PARAMETER_TYPE_UAV};
    D3D12_ROOT_SIGNATURE_DESC rd = {1, &up};
    ID3DBlob *rb = nullptr, *err = nullptr;
    ID3D12RootSignature* rs = nullptr;
    CHECK(SUCCEEDED(ser(&rd, D3D_ROOT_SIGNATURE_VERSION_1, &rb, &err)) && SUCCEEDED(dev->CreateRootSignature(0, rb->GetBufferPointer(), rb->GetBufferSize(), IID_PPV_ARGS(&rs))));
    ID3D12Device* real = nullptr;
    CHECK(SUCCEEDED(rs->GetDevice(IID_PPV_ARGS(&real))));  // ReShade doesn't wrap root signatures
    // the game's creates through the layer, then the layer's own on the device under it, as a bind-time clone is
    for (auto [d, base, what] : {std::tuple{dev, seed, "ms"}, std::tuple{real, seed + 1000000, " own"}}) {
        std::vector<double> t;
        for (int i = 0; i < n; ++i) {
            std::string K = std::to_string(base + i) + ".0";
            ID3DBlob* cs = compile("RWByteAddressBuffer b : register(u0); [numthreads(64,1,1)] void main(uint i : SV_DispatchThreadID) {"
                                   " float4 x = float4(asfloat(b.Load(i * 4)), " + K + ", i, 2); [unroll] for (int k = 0; k < 64; ++k) x = sin(x * 1.37 + float4(k, " + K +
                                   ", x.y, x.w)) * cos(x.zxyw + " + K + "); b.Store(i * 4, asuint(dot(x, x))); }", "cs_5_0");
            CHECK(cs);
            D3D12_COMPUTE_PIPELINE_STATE_DESC c = {};
            c.pRootSignature = rs, c.CS = {cs->GetBufferPointer(), cs->GetBufferSize()};
            ID3D12PipelineState* pso = nullptr;
            HRESULT hr = S_OK;
            t.push_back(ms([&] { hr = d->CreateComputePipelineState(&c, IID_PPV_ARGS(&pso)); }));
            CHECK(SUCCEEDED(hr));
            pso->Release(), cs->Release();
        }
        std::sort(t.begin(), t.end());
        printf("%s %.2f %.2f", what, t[t.size() / 2], t.back());
    }
    printf("\n");
    return 0;
}

static int layerwarm_phases(const std::wstring& dir, const std::wstring& kit, const std::wstring& run_dir, unsigned seed, int n, const std::function<int(wchar_t)>& create) {
    if (int r = create(L'A')) return r;
    std::string db = read_all(run_dir + L"A\\scskiller.db");
    // A's records: what the layer changed is the 'C' records the 'W' records name first
    std::set<Hash20> changed;
    std::vector<std::pair<char, std::string>> recs;
    for (size_t o = 0; o + 5 <= db.size();) {
        uint32_t len;
        memcpy(&len, db.data() + o + 1, 4);
        recs.push_back({db[o], db.substr(o + 5, len)});
        if (db[o] == 'W') changed.insert(*(const Hash20*)(db.data() + o + 5));
        o += 5 + len;
    }
    // ReShade passes a desc it changed on as a pipeline stream ('S')
    size_t cs = std::count_if(recs.begin(), recs.end(), [](auto& x) { return x.first == 'C'; });
    printf("A recorded %zu compute PSOs as the game asked, %zu as the layer changed or made them\n", cs, changed.size());
    CHECK(cs == 2 * (size_t)n && changed.size() == 2 * (size_t)n);  // the own ones are the layer's 'C' records
    for (wchar_t p : {L'B', L'C', L'E', L'F'}) {
        std::wstring work = run_dir + L"warm" + p + L"\\";
        CreateDirectoryW(work.c_str(), nullptr);
        FILE* f = _wfopen((work + L"scskiller.db").c_str(), L"wb");
        CHECK(f);
        for (auto& [tag, pl] : recs)
            if (p == L'B' || p == L'F' || (tag != 'W' && !changed.count(sha1_of(tag, pl)))) {
                uint32_t len = (uint32_t)pl.size();
                fputc(tag, f), fwrite(&len, 4, 1, f), fwrite(pl.data(), 1, len, f);
            }
        fclose(f);
        std::wstring layer = p == L'E' || p == L'F' ? L" --layer \"" + kit + L"\"" : L"";
        if (int r = run(dir + L"scskiller_warm.exe", L"\"" + work + L".\" scsklw" + std::to_wstring(seed) + p + L".exe --threads 4" + layer)) return r;
    }
    for (wchar_t p : {L'B', L'C', L'E', L'F', L'D'})
        if (int r = create(p)) return r;
    return 0;
}

static int layerwarm_parent(const std::wstring& a, const std::wstring& dir, const std::wstring& kit, int n) {
    namespace fs = std::filesystem;
    std::wstring lock = gpu_lock("selftest layerwarm");
    if (lock.empty()) return 1;
    std::vector<fs::path> kit_files;
    std::error_code ec;
    for (auto& e : fs::directory_iterator(kit, ec))
        if (e.is_regular_file()) kit_files.push_back(e.path());
    CHECK(fs::exists(fs::path(kit) / L"dxgi.dll") && kit_files.size() >= 2);
    RunDir run_dir(dir, L"layerwarm");
    CHECK(!run_dir.path.empty());
    std::random_device rd;
    unsigned seed = 100000 + rd() % 8000000;
    DxrSnap start = dxr_snap();
    auto create = [&](wchar_t p) {  // in its own folder: this exe under a throwaway name, the proxy recording, the kit
        std::wstring d = run_dir.path + p + L"\\", exe = d + L"scsklw" + std::to_wstring(seed) + p + L".exe";
        CreateDirectoryW(d.c_str(), nullptr);
        CopyFileW(a.c_str(), exe.c_str(), FALSE), CopyFileW((dir + L"d3d12.dll").c_str(), (d + L"d3d12.dll").c_str(), FALSE);
        for (auto& f : kit_files) CopyFileW(f.c_str(), (d + f.filename().wstring()).c_str(), FALSE);
        if (FILE* ini = _wfopen((d + L"scskiller.ini").c_str(), L"w")) fputs("[scskiller]\nmode=record\n", ini), fclose(ini);
        printf("%lc: ", p);
        return run(exe, L"layerwarmchild " + std::to_wstring(seed) + L" " + std::to_wstring(n));
    };
    int r = layerwarm_phases(dir, kit, run_dir.path, seed, n, create);
    // only files that weren't there when the run started; no other process uses these names
    for (auto& [path, v] : dxr_snap())
        if (!start.count(path) && path.find(L"\\D3DSCache\\") == std::wstring::npos && DeleteFileW(path.c_str())) printf("deleted %ls\n", path.c_str());
    if (r) run_dir.keep = true, printf("kept %ls\n", run_dir.path.c_str());
    return r;
}

// `selftest amwarm [n]` (SELFTEST_AFTERMATH=<a game's GFSDK_Aftermath_Lib.x64.dll, 2.23 or later>): does a warm that
// starts NVIDIA Aftermath as the game does (scskiller_warm --aftermath) fill the cache the game reads? Each phase runs
// this exe under its own throwaway name in its own folder with the proxy recording: A records n compute PSOs with
// Aftermath on (crash dumps, then DX12_Initialize with shader debug info and resource tracking, 0xa, as Mafia: The Old
// Country starts it). B, D, E and F are warmed from A's recording with --aftermath 0xa, C without, E with 0x8 only; then
// each creates the same PSOs: B, C and E with Aftermath 0xa, D without, F with 0xa and the DRED settings Unreal turns on
// with it (device flags 0xd8), Z (not warmed) with 0xa. Prints each one's create ms (median, max). B must hit, C and D miss.
static void am_drop(const void*, uint32_t, void*) {}

static int amwarm_child(const std::wstring& dir, unsigned seed, int n, uint32_t features, bool dred) {
    if (features) {
        wchar_t path[MAX_PATH];
        CHECK(GetEnvironmentVariableW(L"SELFTEST_AFTERMATH", path, MAX_PATH));
        HMODULE am = LoadLibraryW(path);
        auto dumps = am ? (int (*)(uint32_t, uint32_t, uint32_t, void*, void*, void*, void*, void*))GetProcAddress(am, "GFSDK_Aftermath_EnableGpuCrashDumps") : nullptr;
        CHECK(dumps && dumps(0x217, 3, 1, (void*)am_drop, (void*)am_drop, nullptr, nullptr, nullptr) == 1);   // Mafia's call
    }
    if (dred) {
        HMODULE sys = load_system(L"d3d12.dll");
        auto debug = sys ? (decltype(&D3D12GetDebugInterface))GetProcAddress(sys, "D3D12GetDebugInterface") : nullptr;
        ID3D12DeviceRemovedExtendedDataSettings2* s = nullptr;
        CHECK(debug && SUCCEEDED(debug(IID_PPV_ARGS(&s))));
        s->SetAutoBreadcrumbsEnablement(D3D12_DRED_ENABLEMENT_FORCED_ON), s->SetPageFaultEnablement(D3D12_DRED_ENABLEMENT_FORCED_ON);
        s->SetBreadcrumbContextEnablement(D3D12_DRED_ENABLEMENT_FORCED_ON), s->UseMarkersOnlyAutoBreadcrumbs(TRUE);
    }
    HMODULE m = LoadLibraryW((dir + L"d3d12.dll").c_str());
    CHECK(m);
    auto create_device = (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice");
    auto ser = (decltype(&D3D12SerializeRootSignature))GetProcAddress(m, "D3D12SerializeRootSignature");
    ID3D12Device* dev = nullptr;
    CHECK(create_device && ser && SUCCEEDED(create_device(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))));
    if (features) {
        auto init = (int (*)(uint32_t, uint32_t, ID3D12Device*))GetProcAddress(GetModuleHandleW(L"GFSDK_Aftermath_Lib.x64.dll"), "GFSDK_Aftermath_DX12_Initialize");
        CHECK(init && init(0x217, features, dev) == 1);
    }
    D3D12_ROOT_PARAMETER up = {D3D12_ROOT_PARAMETER_TYPE_UAV};
    D3D12_ROOT_SIGNATURE_DESC rd = {1, &up};
    ID3DBlob *rb = nullptr, *err = nullptr;
    ID3D12RootSignature* rs = nullptr;
    CHECK(SUCCEEDED(ser(&rd, D3D_ROOT_SIGNATURE_VERSION_1, &rb, &err)) && SUCCEEDED(dev->CreateRootSignature(0, rb->GetBufferPointer(), rb->GetBufferSize(), IID_PPV_ARGS(&rs))));
    std::vector<double> t;
    for (int i = 0; i < n; ++i) {
        std::string K = std::to_string(seed + i) + ".0";
        ID3DBlob* cs = compile("RWByteAddressBuffer b : register(u0); [numthreads(64,1,1)] void main(uint i : SV_DispatchThreadID) {"
                               " float4 x = float4(asfloat(b.Load(i * 4)), " + K + ", i, 2); [unroll] for (int k = 0; k < 64; ++k) x = sin(x * 1.37 + float4(k, " + K +
                               ", x.y, x.w)) * cos(x.zxyw + " + K + "); b.Store(i * 4, asuint(dot(x, x))); }", "cs_5_0");
        CHECK(cs);
        D3D12_COMPUTE_PIPELINE_STATE_DESC c = {};
        c.pRootSignature = rs, c.CS = {cs->GetBufferPointer(), cs->GetBufferSize()};
        ID3D12PipelineState* pso = nullptr;
        HRESULT hr = S_OK;
        t.push_back(ms([&] { hr = dev->CreateComputePipelineState(&c, IID_PPV_ARGS(&pso)); }));
        CHECK(SUCCEEDED(hr));
        pso->Release(), cs->Release();
    }
    std::sort(t.begin(), t.end());
    printf("%.2f %.2f\n", t[t.size() / 2], t.back());
    if (FILE* f = _wfopen((dir + L"median.txt").c_str(), L"w")) fprintf(f, "%f", t[t.size() / 2]), fclose(f);
    return 0;
}

static int amwarm_parent(const std::wstring& a, const std::wstring& dir, int n) {
    wchar_t am[MAX_PATH];
    if (!GetEnvironmentVariableW(L"SELFTEST_AFTERMATH", am, MAX_PATH)) return printf("set SELFTEST_AFTERMATH to a game's GFSDK_Aftermath_Lib.x64.dll\n"), 1;
    std::wstring lock = gpu_lock("selftest amwarm");
    if (lock.empty()) return 1;
    RunDir run_dir(dir, L"amwarm");
    CHECK(!run_dir.path.empty());
    std::random_device rd;
    unsigned seed = 100000 + rd() % 8000000;
    DxrSnap start = dxr_snap();
    std::map<wchar_t, double> median;
    std::map<wchar_t, std::string> log;
    auto exe_of = [&](wchar_t p) { return L"scsklam" + std::to_wstring(seed) + p + L".exe"; };
    auto game = [&](wchar_t p, uint32_t features, bool dred) {  // in its own folder: this exe under a throwaway name, the proxy recording
        std::wstring d = run_dir.path + p + L"\\";
        CreateDirectoryW(d.c_str(), nullptr);
        CopyFileW(a.c_str(), (d + exe_of(p)).c_str(), FALSE), CopyFileW((dir + L"d3d12.dll").c_str(), (d + L"d3d12.dll").c_str(), FALSE);
        if (FILE* ini = _wfopen((d + L"scskiller.ini").c_str(), L"w")) fputs("[scskiller]\nmode=record\n", ini), fclose(ini);
        printf("%lc (Aftermath 0x%x%s): ", p, features, dred ? ", DRED" : "");
        int r = run(d + exe_of(p), L"amwarmchild " + std::to_wstring(seed) + L" " + std::to_wstring(n) + L" " + std::to_wstring(features) + (dred ? L" dred" : L""));
        median[p] = strtod(read_all(d + L"median.txt").c_str(), nullptr), log[p] = read_all(d + L"scskiller.log");
        return r;
    };
    int r = [&] {
        if (int e = game(L'A', 0xa, false)) return e;
        std::string db = read_all(run_dir.path + L"A\\scskiller.db");
        CHECK(!db.empty());
        for (auto [p, init] : {std::pair{L'B', L"217,a,3,1"}, {L'C', L""}, {L'D', L"217,a,3,1"}, {L'E', L"217,8,3,1"}, {L'F', L"217,a,3,1"}}) {
            std::wstring work = run_dir.path + L"warm" + p + L"\\";
            CreateDirectoryW(work.c_str(), nullptr);
            FILE* f = _wfopen((work + L"scskiller.db").c_str(), L"wb");
            CHECK(f);
            fwrite(db.data(), 1, db.size(), f), fclose(f);
            std::wstring args = L"\"" + work + L".\" " + exe_of(p) + L" --threads 4";
            if (*init) args += L" --aftermath \"" + std::wstring(am) + L"\" --aftermath-init " + init;
            if (int e = run(dir + L"scskiller_warm.exe", args)) return e;
            if (*init) {  // the proxy's hook in the warm saw the init take
                std::string wlog;
                for (auto& e : std::filesystem::recursive_directory_iterator(work))
                    if (e.path().filename() == L"scskiller.log") wlog += read_all(e.path().wstring());
                std::string want = std::string("aftermath: DX12_Initialize(version 0x217, feature flags 0x") + (char)init[4] + ") = 0x1";
                if (wlog.find(want) == std::string::npos) return printf("FAIL: warm %lc's log has no \"%s\"\n", p, want.c_str()), 1;
            }
        }
        for (auto [p, features, dred] : {std::tuple{L'B', 0xau, false}, {L'C', 0xau, false}, {L'D', 0u, false}, {L'E', 0xau, false}, {L'F', 0xau, true}, {L'Z', 0xau, false}})
            if (int e = game(p, features, dred)) return e;
        for (wchar_t p : {L'A', L'B', L'C', L'E', L'F', L'Z'})
            if (log[p].find("aftermath: DX12_Initialize(version 0x217, feature flags 0xa) = 0x1") == std::string::npos)
                return printf("FAIL: %lc's log has no Aftermath init\n", p), 1;
        if (log[L'F'].find("flags 0x400000d8") == std::string::npos) printf("note: F's device flags aren't 0x400000d8\n");
        printf("hit: B %.2f ms; miss: C (warmed without Aftermath) %.2f, D (game without Aftermath) %.2f, Z (not warmed) %.2f; E (warmed 0x8) %.2f, F (DRED) %.2f\n",
               median[L'B'], median[L'C'], median[L'D'], median[L'Z'], median[L'E'], median[L'F']);
        // a hit costs a few ms at most, a compile of these tens
        if (!(median[L'B'] * 4 < std::min({median[L'C'], median[L'D'], median[L'Z']})))
            return printf("FAIL: B isn't a hit next to C, D and Z\n"), 1;
        return 0;
    }();
    list_new_cache(start);
    if (r) run_dir.keep = true, printf("kept %ls\n", run_dir.path.c_str());
    return r;
}

// `selftest burst <n> <threads> <rec|plain> [shaders] [compute]`: a game's startup compile on WARP: n graphics PSOs (or compute
// PSOs of <shaders> shaders, mostly repeats) created by <threads> threads at once, each released right away, every fourth
// the desc of an earlier one; through the proxy beside it recording (rec)
// or the system d3d12.dll (plain). Prints creates/s, the process's private bytes at the start, at their peak and at the end,
// and its file writes during the burst.
static int burst_rows(const std::wstring& dir, long n, unsigned threads, bool rec, int shaders, bool cs) {
    auto priv = [] {
        PROCESS_MEMORY_COUNTERS_EX m{sizeof m};
        return K32GetProcessMemoryInfo(GetCurrentProcess(), (PROCESS_MEMORY_COUNTERS*)&m, sizeof m) ? m.PrivateUsage / 1048576.0 : 0;
    };
    if (rec) SetEnvironmentVariableW(L"SCSKILLER_MODE", L"record");
    HMODULE m = rec ? LoadLibraryW((dir + L"d3d12.dll").c_str()) : load_system(L"d3d12.dll");
    CHECK(m);
    auto create_device = (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice");
    auto ser = (decltype(&D3D12SerializeRootSignature))GetProcAddress(m, "D3D12SerializeRootSignature");
    IDXGIFactory4* f = nullptr;
    IDXGIAdapter* warp = nullptr;
    CHECK(create_device && ser && SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumWarpAdapter(IID_PPV_ARGS(&warp))));
    ID3D12Device* dev = nullptr;
    CHECK(SUCCEEDED(create_device(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))));
    D3D12_ROOT_SIGNATURE_DESC rd = {0, nullptr, 0, nullptr, D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT};
    ID3DBlob *rb = nullptr, *err = nullptr;
    ID3D12RootSignature* rs = nullptr;
    CHECK(SUCCEEDED(ser(&rd, D3D_ROOT_SIGNATURE_VERSION_1, &rb, &err)) && SUCCEEDED(dev->CreateRootSignature(0, rb->GetBufferPointer(), rb->GetBufferSize(), IID_PPV_ARGS(&rs))));
    ID3DBlob* vs = compile("float4 main(float3 p : POSITION) : SV_Position { return float4(p, 1); }", "vs_5_0");
    CHECK(vs);
    D3D12_ROOT_PARAMETER up = {D3D12_ROOT_PARAMETER_TYPE_UAV};
    D3D12_ROOT_SIGNATURE_DESC crd = {1, &up};
    ID3D12RootSignature* crs = nullptr;
    CHECK(SUCCEEDED(ser(&crd, D3D_ROOT_SIGNATURE_VERSION_1, &rb, &err)) && SUCCEEDED(dev->CreateRootSignature(0, rb->GetBufferPointer(), rb->GetBufferSize(), IID_PPV_ARGS(&crs))));
    std::vector<ID3DBlob*> ps(shaders);
    std::atomic<int> next_ps{0};
    std::vector<std::thread> ts;
    for (unsigned t = 0; t < threads; ++t)
        ts.emplace_back([&] {
            for (int k; (k = next_ps++) < shaders;)
                ps[k] = cs ? compile("RWByteAddressBuffer b : register(u0); [numthreads(1, 1, 1)] void main() { b.Store(0, " + std::to_string(k + 1) + "); }", "cs_5_0")
                           : compile("float4 main(float4 p : SV_Position) : SV_Target { return float4(p.xy * " + std::to_string(k + 1) + ".0, 0, 1); }", "ps_5_0");
        });
    for (auto& t : ts) t.join();
    ts.clear();
    for (auto* b : ps) CHECK(b);
    D3D12_INPUT_ELEMENT_DESC ie = {"POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0};
    IO_COUNTERS io0{}, io1{};
    GetProcessIoCounters(GetCurrentProcess(), &io0);
    double start = priv();
    std::atomic<double> peak{start};
    std::atomic<bool> done{false};
    std::thread watch([&] {
        while (!done) {
            for (double p = priv(), q = peak; p > q && !peak.compare_exchange_weak(q, p);) {}
            Sleep(5);
        }
    });
    std::atomic<long> next{0}, bad{0};
    auto t0 = std::chrono::steady_clock::now();
    for (unsigned t = 0; t < threads; ++t)
        ts.emplace_back([&] {
            for (long i; (i = next++) < n;) {
                long j = i % 4 == 3 ? i / 2 : i;  // a repeat: the desc of an earlier create
                ID3D12PipelineState* pso = nullptr;
                if (cs) {
                    D3D12_COMPUTE_PIPELINE_STATE_DESC c = {crs, {ps[j % shaders]->GetBufferPointer(), ps[j % shaders]->GetBufferSize()}};
                    if (SUCCEEDED(dev->CreateComputePipelineState(&c, IID_PPV_ARGS(&pso)))) pso->Release();
                    else ++bad;
                    continue;
                }
                D3D12_GRAPHICS_PIPELINE_STATE_DESC g = {};
                g.pRootSignature = rs, g.InputLayout = {&ie, 1};
                g.VS = {vs->GetBufferPointer(), vs->GetBufferSize()};
                g.PS = {ps[j % shaders]->GetBufferPointer(), ps[j % shaders]->GetBufferSize()};
                g.SampleMask = UINT_MAX, g.RasterizerState = {D3D12_FILL_MODE_SOLID, D3D12_CULL_MODE_NONE}, g.RasterizerState.DepthClipEnable = TRUE;
                g.RasterizerState.DepthBias = (INT)(j / shaders);  // n distinct descs over a few shaders
                g.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE, g.NumRenderTargets = 1, g.RTVFormats[0] = DXGI_FORMAT_R8G8B8A8_UNORM;
                g.SampleDesc = {1, 0};
                if (SUCCEEDED(dev->CreateGraphicsPipelineState(&g, IID_PPV_ARGS(&pso)))) pso->Release();
                else ++bad;
            }
        });
    for (auto& t : ts) t.join();
    double s = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();
    done = true, watch.join();
    GetProcessIoCounters(GetCurrentProcess(), &io1);
    printf("burst %s: %ld creates on %u threads in %.1f s (%.0f/s), %ld failed, private MB start %.0f peak %.0f end %.0f, %llu writes\n",
           rec ? "rec" : "plain", n, threads, s, n / s, bad.load(), start, peak.load(), priv(), io1.WriteOperationCount - io0.WriteOperationCount);
    return bad ? 1 : 0;
}

// `selftest dxcfill <count> <unroll> <seed> [hold s]`: grows the D3D12 driver cache under this exe's name (copy it to a
// throwaway name first) with <count> distinct compute PSOs; on AMD, unroll 250 adds about 78 KB each. hold: keeps the
// device, and so its cache files, open that long (-1: until Enter). System d3d12.dll, no proxy.
static int dxcfill(long long count, int unroll, unsigned seed, int hold) {
    HMODULE m = load_system(L"d3d12.dll");
    CHECK(m);
    auto create_device = (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice");
    auto ser = (decltype(&D3D12SerializeRootSignature))GetProcAddress(m, "D3D12SerializeRootSignature");
    ID3D12Device* dev = nullptr;
    CHECK(create_device && ser && SUCCEEDED(create_device(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))));
    D3D12_ROOT_PARAMETER up = {D3D12_ROOT_PARAMETER_TYPE_UAV};
    D3D12_ROOT_SIGNATURE_DESC rd = {1, &up};
    ID3DBlob *rb = nullptr, *err = nullptr;
    ID3D12RootSignature* rs = nullptr;
    CHECK(SUCCEEDED(ser(&rd, D3D_ROOT_SIGNATURE_VERSION_1, &rb, &err)) && SUCCEEDED(dev->CreateRootSignature(0, rb->GetBufferPointer(), rb->GetBufferSize(), IID_PPV_ARGS(&rs))));
    std::atomic<long long> next{0}, ok{0}, bad{0};
    auto work = [&] {
        for (long long i; (i = next++) < count;) {
            unsigned long long s = seed * 1000003ull + i;
            char src[640];
            sprintf_s(src, "RWByteAddressBuffer b : register(u0); [numthreads(64,1,1)] void main(uint i : SV_DispatchThreadID) {"
                           " float4 v = asfloat(b.Load4(i * 16)), w = asfloat(b.Load4(i * 16 + 4096));"
                           " [unroll] for (int k = 0; k < %d; ++k) { v = v * float4(%llu.0 + k, k * 1.5 + 0.25, %llu.0 - k, k + 3.0) + w.yzwx;"
                           " w = w * float4(k + 0.5, %llu.0 + k * 2, k * 0.75 + 1, %llu.0) + v.wxyz; }"
                           " b.Store4(i * 16, asuint(v + w)); }",
                      unroll, s % 1000000, s / 7 % 1000000, s / 13 % 1000000, s / 17 % 1000000 + 1);
            ID3DBlob* cs = nullptr;
            D3DCompile(src, strlen(src), nullptr, nullptr, nullptr, "main", "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL0, 0, &cs, nullptr);
            D3D12_COMPUTE_PIPELINE_STATE_DESC c = {rs};
            if (cs) c.CS = {cs->GetBufferPointer(), cs->GetBufferSize()};
            ID3D12PipelineState* pso = nullptr;
            if (cs && SUCCEEDED(dev->CreateComputePipelineState(&c, IID_PPV_ARGS(&pso)))) ++ok, pso->Release();
            else ++bad;
            if (cs) cs->Release();
        }
    };
    std::vector<std::thread> ts;
    for (unsigned t = 0; t < std::max(1u, std::thread::hardware_concurrency()); ++t) ts.emplace_back(work);
    for (auto& t : ts) t.join();
    printf("dxcfill: %lld created, %lld failed\n", ok.load(), bad.load());
    fflush(stdout);
    if (hold < 0) getchar();
    else Sleep(hold * 1000);
    rs->Release();
    dev->Release();
    return bad ? 1 : 0;
}

// `selftest frames <n>`: presents on WARP through the proxy's DXGI hooks, with an overlay that hooked Present / Present1 on
// the swap chain vtable before the proxy did (a swap chain made on a device from the system d3d12.dll first). n Presents,
// n Present1s, one DXGI_PRESENT_TEST, three DO_NOT_WAIT Presents the overlay fails with DXGI_ERROR_WAS_STILL_DRAWING (no
// frame) and one Present that the overlay turns into a nested Present1: 2n + 1 frames. Then a
// second overlay hooks them after the proxy did, and a second swap chain on the same vtable presents once with each: 2
// more frames, through both overlays. Then a compute PSO on this thread and one on another (the csv's presents column: 1,
// then 0). Prints "overlay <calls>", "late <calls>" (the second overlay's) and "frames <n>" (scskiller_frames.bin's frames
// after its last launch record, -1 = no file).
static std::atomic<int> g_overlay, g_late;
static void* g_ov_present;
static void* g_ov_present1;
static void* g_late_present;
static void* g_late_present1;
static bool g_ov_nest, g_ov_busy;
static HRESULT STDMETHODCALLTYPE late_present(IDXGISwapChain* sc, UINT sync, UINT flags) {
    ++g_late;
    return ((HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT))g_late_present)(sc, sync, flags);
}
static HRESULT STDMETHODCALLTYPE late_present1(IDXGISwapChain1* sc, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* p) {
    ++g_late;
    return ((HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*))g_late_present1)(sc, sync, flags, p);
}
static HRESULT STDMETHODCALLTYPE ov_present(IDXGISwapChain* sc, UINT sync, UINT flags) {
    ++g_overlay;
    if (g_ov_busy) return DXGI_ERROR_WAS_STILL_DRAWING;
    IDXGISwapChain1* sc1;
    if (g_ov_nest && SUCCEEDED(sc->QueryInterface(IID_PPV_ARGS(&sc1)))) {  // through the vtable: the proxy's hook runs again, nested
        DXGI_PRESENT_PARAMETERS p = {};
        HRESULT hr = sc1->Present1(sync, flags, &p);
        sc1->Release();
        return hr;
    }
    return ((HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT))g_ov_present)(sc, sync, flags);
}
static HRESULT STDMETHODCALLTYPE ov_present1(IDXGISwapChain1* sc, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* p) {
    ++g_overlay;
    return ((HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*))g_ov_present1)(sc, sync, flags, p);
}

// scskiller_frames.bin's frames after its last launch record; -1: no file.
// chains: frames per swap chain index; game: the index a tagged record (0xF0000000 | index) marked as the game's frames
// (-1 none).
static long frames_in(const std::wstring& dir, std::vector<long>* chains = nullptr, long* game = nullptr) {
    FILE* fr = _wfopen((dir + L"scskiller_frames.bin").c_str(), L"rb");
    long frames = -1;
    if (chains) chains->assign(15, 0);
    if (game) *game = -1;
    for (uint32_t r; fr && fread(&r, 4, 1, fr) == 1;) {
        uint64_t head[4];
        if (r == 0xFFFFFFFF) {
            frames = fread(head, sizeof head, 1, fr) == 1 ? 0 : -1;
            if (chains) chains->assign(15, 0);
            if (game) *game = -1;
        } else if (r >> 28 == 15 && (r & 0x0FFFFFFF) < 15) { if (game) *game = r & 0x0FFFFFFF; }
        else if (r >> 28 != 15) {
            ++frames;
            if (chains) ++(*chains)[r >> 28];
        }
    }
    if (fr) fclose(fr);
    return frames;
}
// frames_in once it holds want frames (about 30 s at most), read again after one more of the writer's ticks so frames
// past want show; want < 0: none are hooked, read after 2 ticks. The proxy writes once a second from its own thread,
// which a loaded machine can run seconds late.
static long frames_after(const std::wstring& dir, long want, std::vector<long>* chains = nullptr, long* game = nullptr) {
    long n = frames_in(dir, chains, game);
    for (int i = 0; want >= 0 && n < want && i < 600; ++i) Sleep(50), n = frames_in(dir, chains, game);
    Sleep(want < 0 ? 2500 : 1500);
    return frames_in(dir, chains, game);
}

// `selftest framescapture`: an overlay that hooks the factory's CreateSwapChainForHwnd before the proxy does and, in
// each create, takes the swap chain vtable's Present as the original it would call (as Steam's overlay does at frame
// generation's swap chain). A swap chain is made and presented, then a second one. Prints "captured ours <0|1>": the
// second create's capture is the proxy's hook, and "after ours <0|1>": the vtable's Present after it. Then a third, whose
// create first makes another swap chain through the factory's vtable (a wrapper's create calling the real one) and
// captures after that returns: "nested ours <0|1>" and "nested after ours <0|1>".
using PFN_ScHwnd = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory2*, IUnknown*, HWND, const DXGI_SWAP_CHAIN_DESC1*, const DXGI_SWAP_CHAIN_FULLSCREEN_DESC*,
                                               IDXGIOutput*, IDXGISwapChain1**);
static PFN_ScHwnd g_cap_create;
static void** g_cap_vt;
static void* g_captured;
static int g_cap_nest;  // 1: the next create makes another swap chain first; 2: that inner create, which captures nothing; -1: it failed
static HWND cap_wnd() { return CreateWindowExW(0, L"STATIC", L"scskiller framescapture", WS_OVERLAPPEDWINDOW, 0, 0, 64, 64, nullptr, nullptr, nullptr, nullptr); }
static HRESULT STDMETHODCALLTYPE cap_create(IDXGIFactory2* f, IUnknown* q, HWND w, const DXGI_SWAP_CHAIN_DESC1* d, const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fs,
                                            IDXGIOutput* o, IDXGISwapChain1** pp) {
    if (g_cap_nest == 2) return g_cap_create(f, q, w, d, fs, o, pp);
    if (g_cap_nest == 1) {
        IDXGISwapChain1* inner = nullptr;
        g_cap_nest = 2;
        HRESULT hr = f->CreateSwapChainForHwnd(q, cap_wnd(), d, nullptr, nullptr, &inner);
        if (SUCCEEDED(hr)) inner->Release();
        g_cap_nest = SUCCEEDED(hr) ? 0 : -1;
    }
    if (g_cap_vt) g_captured = g_cap_vt[8];
    return g_cap_create(f, q, w, d, fs, o, pp);
}
static int frames_capture_rows(const std::wstring& dir) {
    SetEnvironmentVariableW(L"SCSKILLER_MODE", L"record");
    IDXGIFactory4* f = nullptr;
    IDXGIAdapter* warp = nullptr;
    CHECK(SUCCEEDED(CreateDXGIFactory2(0, IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumWarpAdapter(IID_PPV_ARGS(&warp))));
    void** fvt = *(void***)f;
    DWORD old;
    CHECK(VirtualProtect(&fvt[15], sizeof(void*), PAGE_READWRITE, &old));
    g_cap_create = (PFN_ScHwnd)fvt[15], fvt[15] = (void*)cap_create;
    VirtualProtect(&fvt[15], sizeof(void*), old, &old);
    HMODULE m = LoadLibraryW((dir + L"d3d12.dll").c_str());
    auto proxy_create = m ? (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice") : nullptr;
    ID3D12Device* dev = nullptr;
    CHECK(proxy_create && SUCCEEDED(proxy_create(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))));
    auto ours = [&](void* p) {
        HMODULE owner = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)p, &owner);
        return owner == m;
    };
    D3D12_COMMAND_QUEUE_DESC qd = {};
    ID3D12CommandQueue* q = nullptr;
    DXGI_SWAP_CHAIN_DESC1 d = {64, 64, DXGI_FORMAT_R8G8B8A8_UNORM, FALSE, {1, 0}, DXGI_USAGE_RENDER_TARGET_OUTPUT, 2};
    d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    IDXGISwapChain1 *sc = nullptr, *sc2 = nullptr, *sc3 = nullptr;
    CHECK(SUCCEEDED(dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&q))) && SUCCEEDED(f->CreateSwapChainForHwnd(q, cap_wnd(), &d, nullptr, nullptr, &sc)));
    sc->Present(0, 0);
    g_cap_vt = *(void***)sc;
    CHECK(SUCCEEDED(f->CreateSwapChainForHwnd(q, cap_wnd(), &d, nullptr, nullptr, &sc2)));
    printf("captured ours %d\nafter ours %d\n", ours(g_captured), ours(g_cap_vt[8]));
    g_captured = nullptr, g_cap_nest = 1;
    CHECK(SUCCEEDED(f->CreateSwapChainForHwnd(q, cap_wnd(), &d, nullptr, nullptr, &sc3)) && g_captured && !g_cap_nest);
    printf("nested ours %d\nnested after ours %d\n", ours(g_captured), ours(g_cap_vt[8]));
    return 0;
}

// `selftest framesdummy`: a hooker that, after frame generation's swap chain (a queue named "AMD FSR PresentQueue"), makes
// its own 2x2 swap chain with D3D11CreateDeviceAndSwapChain on a hidden window, takes that swap chain's Present slot after
// the create returns as the one original it calls, and inline-detours dxgi's Present (as Steam's overlay does). Then each
// swap chain presents once. Prints "dummy ours <0|1>" (the slot it took is the proxy's) and "recursed <0|1>" (its detour
// was entered again from inside its own call, cut at 64 deep instead of overflowing the stack).
using PFN_DdPresent = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
static void* g_dd_target;       // dxgi's Present, detoured
static void* g_dd_orig;         // what the hooker calls: the slot it read
static uint8_t g_dd_saved[12];
static thread_local int t_dd_depth;
static bool g_dd_recursed;
static void dd_patch(bool on);
static HRESULT STDMETHODCALLTYPE dd_present(IDXGISwapChain* sc, UINT sync, UINT flags) {
    if (t_dd_depth >= 64) return g_dd_recursed = true, E_FAIL;
    ++t_dd_depth;
    HRESULT hr;
    if (g_dd_orig == g_dd_target) dd_patch(false), hr = ((PFN_DdPresent)g_dd_target)(sc, sync, flags), dd_patch(true);  // its trampoline
    else hr = ((PFN_DdPresent)g_dd_orig)(sc, sync, flags);
    --t_dd_depth;
    return hr;
}
static void dd_patch(bool on) {
    auto t = (uint8_t*)g_dd_target;
    DWORD old;
    VirtualProtect(t, 12, PAGE_EXECUTE_READWRITE, &old);
    if (on) {
        void* to = (void*)dd_present;
        t[0] = 0x48, t[1] = 0xB8, memcpy(t + 2, &to, 8), t[10] = 0xFF, t[11] = 0xE0;  // mov rax, dd_present / jmp rax
    } else memcpy(t, g_dd_saved, 12);
    VirtualProtect(t, 12, old, &old);
    FlushInstructionCache(GetCurrentProcess(), t, 12);
}
static int frames_dummy_rows(const std::wstring& dir) {
    SetEnvironmentVariableW(L"SCSKILLER_MODE", L"record");
    wchar_t sys[MAX_PATH];
    GetSystemDirectoryW(sys, MAX_PATH);
    HMODULE d3d11 = LoadLibraryW((std::wstring(sys) + L"\\d3d11.dll").c_str());
    auto create11 = d3d11 ? (decltype(&D3D11CreateDeviceAndSwapChain))GetProcAddress(d3d11, "D3D11CreateDeviceAndSwapChain") : nullptr;
    auto dummy = [&](IDXGISwapChain** sc) {
        DXGI_SWAP_CHAIN_DESC d = {{2, 2, {0, 0}, DXGI_FORMAT_R8G8B8A8_UNORM}, {1, 0}, DXGI_USAGE_RENDER_TARGET_OUTPUT, 2, cap_wnd(), TRUE, DXGI_SWAP_EFFECT_FLIP_DISCARD};
        ID3D11Device* dev = nullptr;
        return create11 && SUCCEEDED(create11(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &d, sc, &dev, nullptr, nullptr));
    };
    IDXGISwapChain* before = nullptr;
    CHECK(dummy(&before));  // dxgi's own Present, before the proxy is in
    g_dd_target = (*(void***)before)[8];
    memcpy(g_dd_saved, g_dd_target, 12);
    IDXGIFactory4* f = nullptr;
    IDXGIAdapter* warp = nullptr;
    CHECK(SUCCEEDED(CreateDXGIFactory2(0, IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumWarpAdapter(IID_PPV_ARGS(&warp))));
    HMODULE m = LoadLibraryW((dir + L"d3d12.dll").c_str());
    auto proxy_create = m ? (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice") : nullptr;
    ID3D12Device* dev = nullptr;
    CHECK(proxy_create && SUCCEEDED(proxy_create(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))));
    auto chain = [&](IDXGISwapChain1** sc, const wchar_t* name) {
        D3D12_COMMAND_QUEUE_DESC qd = {};
        ID3D12CommandQueue* q = nullptr;
        DXGI_SWAP_CHAIN_DESC1 d = {64, 64, DXGI_FORMAT_R8G8B8A8_UNORM, FALSE, {1, 0}, DXGI_USAGE_RENDER_TARGET_OUTPUT, 2};
        d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        return SUCCEEDED(dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&q))) && (!name || SUCCEEDED(q->SetName(name))) &&
               SUCCEEDED(f->CreateSwapChainForHwnd(q, cap_wnd(), &d, nullptr, nullptr, sc));
    };
    IDXGISwapChain1 *game = nullptr, *fg = nullptr;
    CHECK(chain(&game, nullptr));
    game->Present(0, 0);
    CHECK(chain(&fg, L"AMD FSR PresentQueue"));
    IDXGISwapChain* sc11 = nullptr;
    CHECK(dummy(&sc11));
    g_dd_orig = (*(void***)sc11)[8];
    HMODULE owner = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)g_dd_orig, &owner);
    printf("dummy ours %d\n", owner == m);
    dd_patch(true);
    fg->Present(0, 0), game->Present(0, 0), sc11->Present(0, 0);
    dd_patch(false);
    printf("recursed %d\n", g_dd_recursed);
    return 0;
}

// `selftest framesstorm`: swap chain creates through the proxy's factory hook. An overlay hooked the factory's
// CreateSwapChainForHwnd before the proxy; inside the first create it makes another through the factory's vtable, 3 deep.
// Then each of a set of descs, valid and not, made with dxgi's own create and through the factory: "same <n> of <m>",
// those both gave the same result. Then 2000 creates on a window that already has a swap chain: "storm <failed> 0x<hr>",
// and "creates <n> failed <m>", all the creates through the factory. Waits for the proxy's once-a-second summary.
static int g_ov_reenter;
static HRESULT STDMETHODCALLTYPE ov_create(IDXGIFactory2* f, IUnknown* q, HWND w, const DXGI_SWAP_CHAIN_DESC1* d, const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fs,
                                           IDXGIOutput* o, IDXGISwapChain1** pp) {
    if (g_ov_reenter > 0) {
        --g_ov_reenter;
        IDXGISwapChain1* inner = nullptr;
        if (SUCCEEDED(f->CreateSwapChainForHwnd(q, cap_wnd(), d, nullptr, nullptr, &inner))) inner->Release();
    }
    return g_cap_create(f, q, w, d, fs, o, pp);
}
static int frames_storm_rows(const std::wstring& dir) {
    SetEnvironmentVariableW(L"SCSKILLER_MODE", L"record");
    IDXGIFactory4* f = nullptr;
    IDXGIAdapter* warp = nullptr;
    CHECK(SUCCEEDED(CreateDXGIFactory2(0, IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumWarpAdapter(IID_PPV_ARGS(&warp))));
    void** fvt = *(void***)f;
    DWORD old;
    CHECK(VirtualProtect(&fvt[15], sizeof(void*), PAGE_READWRITE, &old));
    g_cap_create = (PFN_ScHwnd)fvt[15], fvt[15] = (void*)ov_create;
    VirtualProtect(&fvt[15], sizeof(void*), old, &old);
    HMODULE m = LoadLibraryW((dir + L"d3d12.dll").c_str());
    auto proxy_create = m ? (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice") : nullptr;
    ID3D12Device* dev = nullptr;
    CHECK(proxy_create && SUCCEEDED(proxy_create(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))));
    CHECK(fvt[15] != (void*)ov_create);  // the proxy's hook went in over the overlay's
    D3D12_COMMAND_QUEUE_DESC qd = {};
    ID3D12CommandQueue* q = nullptr;
    CHECK(SUCCEEDED(dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&q))));
    q->SetName(L"C:\\Users\\someone\\queue");  // a debug name that is a path: the log reduces it
    int creates = 0, failed = 0;
    auto create = [&](HWND w, const DXGI_SWAP_CHAIN_DESC1& d, IDXGISwapChain1** sc) {
        HRESULT hr = f->CreateSwapChainForHwnd(q, w, &d, nullptr, nullptr, sc);
        ++creates, failed += FAILED(hr);
        return hr;
    };
    DXGI_SWAP_CHAIN_DESC1 d = {64, 64, DXGI_FORMAT_R8G8B8A8_UNORM, FALSE, {1, 0}, DXGI_USAGE_RENDER_TARGET_OUTPUT, 2};
    d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    IDXGISwapChain1* sc = nullptr;
    g_ov_reenter = 3;
    CHECK(SUCCEEDED(create(cap_wnd(), d, &sc)) && !g_ov_reenter);
    creates += 3;
    sc->Release();
    std::vector<DXGI_SWAP_CHAIN_DESC1> descs(5, d);
    descs[1].Format = DXGI_FORMAT_B8G8R8A8_UNORM, descs[1].BufferCount = 3, descs[1].SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
    descs[2].Width = descs[2].Height = 0;  // the window's size
    descs[3].BufferCount = 1;  // flip model needs 2
    descs[4].Format = DXGI_FORMAT_R32_FLOAT;
    int same = 0;
    for (auto& e : descs) {
        IDXGISwapChain1 *a = nullptr, *b = nullptr;
        HRESULT ha = g_cap_create(f, q, cap_wnd(), &e, nullptr, nullptr, &a), hb = create(cap_wnd(), e, &b);
        same += ha == hb;
        if (SUCCEEDED(ha)) a->Release();
        if (SUCCEEDED(hb)) b->Release();
    }
    printf("same %d of %zu\n", same, descs.size());
    HWND held = cap_wnd();
    CHECK(SUCCEEDED(create(held, d, &sc)));
    int storm = 0;
    HRESULT first = S_OK;
    for (int i = 0; i < 2000; ++i) {
        IDXGISwapChain1* again = nullptr;
        HRESULT hr = create(held, d, &again);
        if (SUCCEEDED(hr)) again->Release();
        else if (!storm++) first = hr;
    }
    printf("storm %d 0x%08x\ncreates %d failed %d\n", storm, (unsigned)first, creates, failed);
    char want[64];
    snprintf(want, sizeof want, "frames: %d swap chain creates, %d failed", creates, failed);
    for (int i = 0; i < 300; ++i, Sleep(100)) {  // the proxy's summary is once a second, from its own thread
        std::string log;
        if (FILE* lf = _wfopen((dir + L"scskiller.log").c_str(), L"rb")) {
            char b[4096];
            for (size_t n; (n = fread(b, 1, sizeof b, lf));) log.append(b, n);
            fclose(lf);
        }
        if (log.find(want) != std::string::npos) break;
    }
    return 0;
}

// `selftest framesfg ffx [late]`: fakenext.dll loaded as amd_fidelityfx_loader_dx12.dll before the first device (late:
// after it), a swap chain presented 3 times, then frame generation's swap chain from its ffxCreateContext (FOR_HWND) presented 3 times, each
// presenting its real swap chain once on this thread and twice from another. Prints "chains <frames of each>" and "game
// <index>". play: a compute PSO first, and frame generation's swap chain presents until the log says the in-game panel's
// play time is done or dxgi's Present went back (60 s at most).
static HRESULT compute_pso(ID3D12Device* dev, ID3D12RootSignature* rs, int k);
static int frames_ffx_rows(const std::wstring& dir, bool play, bool late) {
    SetEnvironmentVariableW(L"SCSKILLER_MODE", L"record");
    auto load_ffx = [&] { return LoadLibraryW((dir + L"amd_fidelityfx_loader_dx12.dll").c_str()); };
    HMODULE ffx = late ? nullptr : load_ffx();
    HMODULE m = LoadLibraryW((dir + L"d3d12.dll").c_str());
    auto proxy_create = m ? (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice") : nullptr;
    IDXGIFactory4* f = nullptr;
    IDXGIAdapter* warp = nullptr;
    ID3D12Device* dev = nullptr;
    CHECK(proxy_create && SUCCEEDED(CreateDXGIFactory2(0, IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumWarpAdapter(IID_PPV_ARGS(&warp))) &&
          SUCCEEDED(proxy_create(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))));
    if (late) ffx = load_ffx();
    auto ffx_create = ffx ? (uint32_t(*)(void**, void*, const void*))GetProcAddress(ffx, "ffxCreateContext") : nullptr;
    CHECK(ffx_create);
    D3D12_COMMAND_QUEUE_DESC qd = {};
    ID3D12CommandQueue* q = nullptr;
    DXGI_SWAP_CHAIN_DESC1 d = {64, 64, DXGI_FORMAT_R8G8B8A8_UNORM, FALSE, {1, 0}, DXGI_USAGE_RENDER_TARGET_OUTPUT, 2};
    d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    auto wnd = [] { return CreateWindowExW(0, L"STATIC", L"scskiller framesfg", WS_OVERLAPPEDWINDOW, 0, 0, 64, 64, nullptr, nullptr, nullptr, nullptr); };
    IDXGISwapChain1* sc = nullptr;
    CHECK(SUCCEEDED(dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&q))) && SUCCEEDED(f->CreateSwapChainForHwnd(q, wnd(), &d, nullptr, nullptr, &sc)));
    for (int i = 0; i < 3; ++i) sc->Present(0, 0);
    IDXGISwapChain* fg = nullptr;
    struct { uint64_t type; void* next; IDXGISwapChain** swapchain; HWND hwnd; DXGI_SWAP_CHAIN_DESC1* desc; void* fullscreen; IDXGIFactory* factory; ID3D12CommandQueue* queue; }
        fd = {0x30006, nullptr, &fg, wnd(), &d, nullptr, f, q};
    void* ctx = nullptr;
    CHECK(ffx_create(&ctx, &fd, nullptr) == 0 && fg);
    for (int i = 0; i < 3; ++i) fg->Present(0, 0);
    if (play) {
        auto ser = (decltype(&D3D12SerializeRootSignature))GetProcAddress(m, "D3D12SerializeRootSignature");
        D3D12_ROOT_PARAMETER up = {D3D12_ROOT_PARAMETER_TYPE_UAV};
        D3D12_ROOT_SIGNATURE_DESC rd = {1, &up};
        ID3DBlob *rb = nullptr, *err = nullptr;
        ID3D12RootSignature* rs = nullptr;
        CHECK(ser && SUCCEEDED(ser(&rd, D3D_ROOT_SIGNATURE_VERSION_1, &rb, &err)) &&
              SUCCEEDED(dev->CreateRootSignature(0, rb->GetBufferPointer(), rb->GetBufferSize(), IID_PPV_ARGS(&rs))) && SUCCEEDED(compute_pso(dev, rs, (int)GetTickCount())));
        for (ULONGLONG until = GetTickCount64() + 60000; GetTickCount64() < until;) {
            for (int i = 0; i < 10; ++i) fg->Present(0, 0), Sleep(15);
            std::string log = read_all(dir + L"scskiller.log");
            if (log.find("s of play counted, done") != std::string::npos || log.find("dxgi's Present unhooked") != std::string::npos) break;
        }
    }
    std::vector<long> chains;
    long game;
    frames_after(dir, 6, &chains, &game);  // 3 on the game's swap chain, 3 or more on frame generation's
    printf("chains");
    for (long n : chains) if (n) printf(" %ld", n);
    printf("\ngame %ld\n", game);
    return 0;
}

static int frames_rows(const std::wstring& dir, int n) {
    SetEnvironmentVariableW(L"SCSKILLER_MODE", L"record");
    HMODULE m = LoadLibraryW((dir + L"d3d12.dll").c_str());
    wchar_t sys[MAX_PATH];
    GetSystemDirectoryW(sys, MAX_PATH);
    HMODULE real = GetModuleHandleW((std::wstring(sys) + L"\\d3d12.dll").c_str());
    CHECK(m && real);
    auto proxy_create = (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice");
    auto real_create = (decltype(&D3D12CreateDevice))GetProcAddress(real, "D3D12CreateDevice");
    IDXGIFactory4* f = nullptr;
    IDXGIAdapter* warp = nullptr;
    CHECK(proxy_create && real_create && SUCCEEDED(CreateDXGIFactory2(0, IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumWarpAdapter(IID_PPV_ARGS(&warp))));
    auto chain = [&](ID3D12Device* dev, IDXGISwapChain1** sc) {
        HWND wnd = CreateWindowExW(0, L"STATIC", L"scskiller frames", WS_OVERLAPPEDWINDOW, 0, 0, 64, 64, nullptr, nullptr, nullptr, nullptr);
        D3D12_COMMAND_QUEUE_DESC qd = {};
        ID3D12CommandQueue* q = nullptr;
        DXGI_SWAP_CHAIN_DESC1 d = {64, 64, DXGI_FORMAT_R8G8B8A8_UNORM, FALSE, {1, 0}, DXGI_USAGE_RENDER_TARGET_OUTPUT, 2};
        d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        return wnd && SUCCEEDED(dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&q))) && SUCCEEDED(f->CreateSwapChainForHwnd(q, wnd, &d, nullptr, nullptr, sc));
    };
    ID3D12Device *dev0 = nullptr, *dev = nullptr;
    IDXGISwapChain1 *sc0 = nullptr, *sc = nullptr;
    CHECK(SUCCEEDED(real_create(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev0))) && chain(dev0, &sc0));
    void** vt = *(void***)sc0;
    DWORD old;
    CHECK(VirtualProtect(&vt[8], 15 * sizeof(void*), PAGE_READWRITE, &old));
    g_ov_present = vt[8], g_ov_present1 = vt[22];
    vt[8] = (void*)ov_present, vt[22] = (void*)ov_present1;
    VirtualProtect(&vt[8], 15 * sizeof(void*), old, &old);
    sc0->Release();
    CHECK(SUCCEEDED(proxy_create(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))) && chain(dev, &sc));
    bool hooked = module_of((*(void***)sc)[8]) == m;
    DXGI_PRESENT_PARAMETERS p = {};
    for (int i = 0; i < n; ++i) sc->Present(0, 0), sc->Present1(0, 0, &p);
    sc->Present(0, DXGI_PRESENT_TEST);
    g_ov_busy = true;
    for (int i = 0; i < 3; ++i) sc->Present(0, DXGI_PRESENT_DO_NOT_WAIT);
    g_ov_busy = false;
    g_ov_nest = true;
    sc->Present(0, 0);
    g_ov_nest = false;
    CHECK(VirtualProtect(&vt[8], 15 * sizeof(void*), PAGE_READWRITE, &old));
    g_late_present = vt[8], g_late_present1 = vt[22];
    vt[8] = (void*)late_present, vt[22] = (void*)late_present1;
    VirtualProtect(&vt[8], 15 * sizeof(void*), old, &old);
    IDXGISwapChain1* sc2 = nullptr;
    CHECK(chain(dev, &sc2) && *(void***)sc2 == vt);
    sc2->Present(0, 0), sc2->Present1(0, 0, &p);
    // a compute PSO on this (presenting) thread, then one on another: the csv's last column says 1, then 0
    auto ser = (decltype(&D3D12SerializeRootSignature))GetProcAddress(m, "D3D12SerializeRootSignature");
    D3D12_ROOT_PARAMETER up = {D3D12_ROOT_PARAMETER_TYPE_UAV};
    D3D12_ROOT_SIGNATURE_DESC rd = {1, &up};
    ID3DBlob *rb = nullptr, *err = nullptr;
    ID3D12RootSignature* rs = nullptr;
    CHECK(ser && SUCCEEDED(ser(&rd, D3D_ROOT_SIGNATURE_VERSION_1, &rb, &err)) && SUCCEEDED(dev->CreateRootSignature(0, rb->GetBufferPointer(), rb->GetBufferSize(), IID_PPV_ARGS(&rs))));
    auto pso = [&](int v) {
        ID3DBlob* cs = compile("RWByteAddressBuffer b : register(u0); [numthreads(1,1,1)] void main() { b.Store(0, " + std::to_string(GetTickCount() + v) + "); }", "cs_5_0");
        D3D12_COMPUTE_PIPELINE_STATE_DESC c = {};
        c.pRootSignature = rs, c.CS = {cs->GetBufferPointer(), cs->GetBufferSize()};
        ID3D12PipelineState* p = nullptr;
        dev->CreateComputePipelineState(&c, IID_PPV_ARGS(&p));
    };
    pso(1);
    std::thread(pso, 2).join();
    long frames = frames_after(dir, hooked ? 2 * n + 3 : -1);  // the nested present and sc2's two count; the test and failed ones don't
    printf("overlay %d\nlate %d\n", g_overlay.load(), g_late.load());
    printf("frames %ld\n", frames);
    return 0;
}

// `selftest framesfg plain|<queue name>`: a swap chain made and presented 3 times with each of Present and Present1.
// With a queue name, a swap chain on an unnamed queue is made and presented 3 times first, then the swap chain on a
// queue of that name (FSR 3's frame generation: "AMD FSR PresentQueue"). Prints "hooked <0|1>" (named: the first swap
// chain's Present is the proxy's), "after <0|1>" (the same for the last swap chain) and "frames <n>". overlay: an overlay
// hooks Present (slot 8) after the proxy, before the named queue's swap chain; then "slot8 overlay <0|1>" and
// "slot22 ours <0|1>" for that swap chain's vtable instead of "after".
static int frames_fg_rows(const std::wstring& dir, const wchar_t* queue, bool overlay) {
    SetEnvironmentVariableW(L"SCSKILLER_MODE", L"record");
    HMODULE m = LoadLibraryW((dir + L"d3d12.dll").c_str());
    auto proxy_create = m ? (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice") : nullptr;
    IDXGIFactory4* f = nullptr;
    IDXGIAdapter* warp = nullptr;
    ID3D12Device* dev = nullptr;
    CHECK(proxy_create && SUCCEEDED(CreateDXGIFactory2(0, IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumWarpAdapter(IID_PPV_ARGS(&warp))) &&
          SUCCEEDED(proxy_create(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))));
    auto chain = [&](IDXGISwapChain1** sc, const wchar_t* name) {
        HWND wnd = CreateWindowExW(0, L"STATIC", L"scskiller framesfg", WS_OVERLAPPEDWINDOW, 0, 0, 64, 64, nullptr, nullptr, nullptr, nullptr);
        D3D12_COMMAND_QUEUE_DESC qd = {};
        ID3D12CommandQueue* q = nullptr;
        DXGI_SWAP_CHAIN_DESC1 d = {64, 64, DXGI_FORMAT_R8G8B8A8_UNORM, FALSE, {1, 0}, DXGI_USAGE_RENDER_TARGET_OUTPUT, 2};
        d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        return wnd && SUCCEEDED(dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&q))) && (!name || SUCCEEDED(q->SetName(name))) &&
               SUCCEEDED(f->CreateSwapChainForHwnd(q, wnd, &d, nullptr, nullptr, sc));
    };
    auto ours = [&](IDXGISwapChain1* sc) {
        HMODULE owner = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)(*(void***)sc)[8], &owner);
        return owner == m;
    };
    IDXGISwapChain1* sc = nullptr;
    DXGI_PRESENT_PARAMETERS p = {};
    CHECK(chain(&sc, nullptr));
    if (queue) {
        printf("hooked %d\n", ours(sc));
        for (int i = 0; i < 3; ++i) sc->Present(0, 0);
        void** vt = *(void***)sc;
        DWORD old;
        if (overlay) {
            CHECK(VirtualProtect(&vt[8], sizeof(void*), PAGE_READWRITE, &old));
            g_late_present = vt[8], vt[8] = (void*)late_present;
            VirtualProtect(&vt[8], sizeof(void*), old, &old);
        }
        CHECK(chain(&sc, queue));
        if (overlay) {
            HMODULE owner = nullptr;
            GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)(*(void***)sc)[22], &owner);
            printf("slot8 overlay %d\nslot22 ours %d\n", (*(void***)sc)[8] == (void*)late_present, owner == m);
        }
    }
    if (!overlay) printf("after %d\n", ours(sc));
    int failed = 0;
    for (int i = 0; i < 3; ++i) failed += FAILED(sc->Present(0, 0)) + FAILED(sc->Present1(0, 0, &p));
    printf("presents failed %d\nframes %ld\n", failed, frames_after(dir, ours(sc) ? (queue ? 9 : 6) : -1));
    return 0;
}

// `selftest nvqi`: NVAPI resolution through the proxy's nvapi_QueryInterface hook, fakenext as nvapi64.dll (loaded by full
// path before the first device, as a game's driver is). 8 threads resolve each of 16 ids 2000 times before the hooks, then the
// first devices come from 4 threads at once, then the 8 threads again, each id also nested in another resolution through the
// hooked entry. Unknown ids must stay unresolved. Meanwhile a thread past the entry's first instruction waits there until
// the hooks are in, then goes on. Three hooked ids resolve to entries the hook must refuse (a 1-byte first instruction, too
// little padding, padding after a live byte), and one gets another hooker's jmp between the hook's read of it and its
// exchange (SCSKILLER_TEST_HOOK_RACE, a build with test hooks). Prints "hooked <0|1>" (the entry is the hook's jmp),
// "refused intact <0|1>" (those entries' bytes unchanged), "raced intact <0|1>" (the other jmp kept), "inside resolved <0|1>" (that thread's answer) and, for both phases, "<before|after>
// resolved <n> wrong <n>"; exit code 1 when the entry isn't hooked or an answer is wrong.
// at_exit: fakenext then resolves through the hook from its process detach, after the proxy's (FakeNext_NvAtExit).
static int nvqi_rows(const std::wstring& dir, bool at_exit) {
    SetEnvironmentVariableW(L"SCSKILLER_MODE", L"record");
    HMODULE nv = LoadLibraryW((dir + L"nvapi64.dll").c_str());
    auto qi = nv ? (void* (*)(uint32_t))GetProcAddress(nv, "nvapi_QueryInterface") : nullptr;
    HMODULE m = LoadLibraryW((dir + L"d3d12.dll").c_str());
    auto proxy_create = m ? (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice") : nullptr;
    IDXGIFactory4* f = nullptr;
    IDXGIAdapter* warp = nullptr;
    CHECK(qi && proxy_create && SUCCEEDED(CreateDXGIFactory2(0, IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumWarpAdapter(IID_PPV_ARGS(&warp))));
    void* want[16];
    for (uint32_t i = 0; i < 16; ++i) CHECK((want[i] = qi(0x5EE00000 | i)));
    auto storm = [&](const char* phase, bool nested) {
        std::atomic<int> n = 0, wrong = 0;
        std::vector<std::thread> ts;
        for (int t = 0; t < 8; ++t)
            ts.emplace_back([&, t] {
                for (int k = 0; k < 2000; ++k, ++n) {
                    uint32_t i = (t + k) & 15;
                    if (qi(0x5EE00000 | i) != want[i] || (nested && qi(0x5EE10000 | i) != want[i]) || qi(0x5EEF0000 | i)) ++wrong;
                }
            });
        for (auto& th : ts) th.join();
        printf("%s resolved %d wrong %d\n", phase, n.load(), wrong.load());
        return wrong.load();
    };
    int bad = storm("before", false);
    auto inside = (void* (*)(uint32_t))GetProcAddress(nv, "FakeNext_NvQiInside");
    auto hold = (void(WINAPI*)(bool))GetProcAddress(nv, "FakeNext_NvQiHold");
    CHECK(inside && hold);
    void* got = nullptr;
    std::thread held([&] { got = inside(0x5EE00003); });
    hold(false);
    // entries the hook must refuse: their first instruction and the 16 bytes before it stay as they were
    uint8_t* refused[] = {(uint8_t*)qi(0xAC2DFEB5), (uint8_t*)qi(0x43D867C0), (uint8_t*)qi(0x2FC28856)};
    uint8_t before[3][24];
    for (int i = 0; i < 3; ++i) {
        CHECK(refused[i]);
        memcpy(before[i], refused[i] - 16, 24);
    }
    // another hooker's jmp lands on CreateComputePipelineState's entry while the proxy hooks it: that jmp must stay
    static uint8_t* raced;
    static uint64_t foreign;
    CHECK((raced = (uint8_t*)qi(0x2762DEAC)));
    memcpy(&foreign, raced, 8);
    memcpy(&foreign, "\xE9\0\0\0\0", 5);  // jmp to the next instruction
    void (*race)(void*) = [](void* t) {
        DWORD old;
        if (t == raced && VirtualProtect(t, 8, PAGE_EXECUTE_READWRITE, &old))
            InterlockedExchange64((volatile LONG64*)t, (LONG64)foreign), VirtualProtect(t, 8, old, &old);
    };
    wchar_t race_at[20];
    swprintf_s(race_at, L"%llx", (unsigned long long)race);
    SetEnvironmentVariableW(L"SCSKILLER_TEST_HOOK_RACE", race_at);
    std::atomic<int> made = 0;
    std::vector<std::thread> ts;
    for (int i = 0; i < 4; ++i)
        ts.emplace_back([&] {
            ID3D12Device* d = nullptr;
            if (SUCCEEDED(proxy_create(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&d)))) ++made, d->Release();
        });
    for (auto& th : ts) th.join();
    CHECK(made == 4);
    bool hooked = *(uint8_t*)qi == 0xE9 || *(uint8_t*)qi == 0xEB;
    printf("hooked %d\n", hooked);
    bool intact = true;
    for (int i = 0; i < 3; ++i) intact &= !memcmp(before[i], refused[i] - 16, 24);
    printf("refused intact %d\n", intact);
    bad += !intact;
    bool kept = !memcmp(raced, &foreign, 8);
    printf("raced intact %d\n", kept);
    bad += !kept;
    hold(true);
    held.join();
    printf("inside resolved %d\n", got == want[3]);
    bad += got != want[3];
    bad += storm("after", true);
    auto nv_at_exit = (void(WINAPI*)())GetProcAddress(nv, "FakeNext_NvAtExit");
    if (at_exit) {
        CHECK(nv_at_exit);
        nv_at_exit();
    }
    return bad || !hooked;
}

// `selftest sysdxgi` (no proxy): sys_dxgi.h against a copy of System32's dxgi.dll mapped from another folder, as a game
// folder's dxgi.dll is. Prints "factory <0|1>" (a factory's vtable is in System32's), "copy <0|1>" (the copy's
// CreateDXGIFactory1 is), "exe <0|1>" (a function of this exe is) and "heap <0|1>".
static int sys_dxgi_rows() {
    HMODULE sys = system_dxgi();
    auto create = sys ? (decltype(&CreateDXGIFactory1))GetProcAddress(sys, "CreateDXGIFactory1") : nullptr;
    IDXGIFactory1* f = nullptr;
    CHECK(create && SUCCEEDED(create(IID_PPV_ARGS(&f))));
    printf("factory %d\n", module_of(*(void**)f) == sys);
    f->Release();
    wchar_t tmp[MAX_PATH], sysdir[MAX_PATH];
    CHECK(GetTempPathW(MAX_PATH, tmp) && GetSystemDirectoryW(sysdir, MAX_PATH));
    const std::wstring dir = std::wstring(tmp) + L"scskiller-sysdxgi-" + std::to_wstring(GetCurrentProcessId()), copy = dir + L"\\dxgi.dll";
    CHECK(CreateDirectoryW(dir.c_str(), nullptr) && CopyFileW((std::wstring(sysdir) + L"\\dxgi.dll").c_str(), copy.c_str(), FALSE));
    HMODULE c = LoadLibraryExW(copy.c_str(), nullptr, DONT_RESOLVE_DLL_REFERENCES);  // mapped as a module, its DllMain not run
    void* cf = c ? (void*)GetProcAddress(c, "CreateDXGIFactory1") : nullptr;
    CHECK(c && c != sys && module_of(cf) == c);
    printf("copy %d\n", module_of(cf) == sys);
    FreeLibrary(c), DeleteFileW(copy.c_str()), RemoveDirectoryW(dir.c_str());
    printf("exe %d\n", module_of((void*)&sys_dxgi_rows) == sys);
    std::vector<void*> heap(16);
    printf("heap %d\n", module_of(heap.data()) == sys);
    return 0;
}

// `selftest frameswrap <n>`: a mod's swap chain wrapper (OptiScaler as dxgi.dll) through the proxy's hooked factory. The
// factory's CreateSwapChainForHwnd is hooked before the proxy's device: it makes the real swap chain through the
// factory's vtable (the proxy's hook, nested) and returns a wrapper of it whose vtable is this exe's. n Presents and n
// Present1s through the wrapper. Prints "wrapper ours <0|1>" (its Present is the proxy's), "real ours <0|1>" (the real
// swap chain's is) and "frames <n>". sl: Streamline's proxy instead: the real swap chain made by dxgi's original
// function, not through the vtable, and the wrapper answering Streamline's IID with it.
// fwd: ReShade 6.5's proxy, made as sl's, answering no unwrap but passing the interfaces it doesn't have to the real one.
struct Wrap { void** vt; IDXGISwapChain1* real; LONG ref; };
static void* g_wrap_vt[23];
static void* g_wrap_create;
static thread_local bool t_wrapping;
static bool g_wrap_sl, g_wrap_fwd;
static const GUID kSlBase = {0xadec44e2, 0x61f0, 0x45c3, {0xad, 0x9f, 0x1b, 0x37, 0x37, 0x92, 0x84, 0xff}};
static HRESULT STDMETHODCALLTYPE wrap_qi(Wrap* w, REFIID riid, void** pp) {
    if (g_wrap_sl && riid == kSlBase) return w->real->AddRef(), *pp = w->real, S_OK;
    for (const IID& i : {__uuidof(IUnknown), __uuidof(IDXGIObject), __uuidof(IDXGIDeviceSubObject), __uuidof(IDXGISwapChain), __uuidof(IDXGISwapChain1)})
        if (riid == i) return InterlockedIncrement(&w->ref), *pp = w, S_OK;
    return g_wrap_fwd ? w->real->QueryInterface(riid, pp) : (*pp = nullptr, E_NOINTERFACE);
}
static ULONG STDMETHODCALLTYPE wrap_addref(Wrap* w) { return InterlockedIncrement(&w->ref); }
static ULONG STDMETHODCALLTYPE wrap_release(Wrap* w) {
    ULONG n = InterlockedDecrement(&w->ref);
    if (!n) w->real->Release(), delete w;
    return n;
}
static HRESULT STDMETHODCALLTYPE wrap_present(Wrap* w, UINT sync, UINT flags) { return w->real->Present(sync, flags); }
static HRESULT STDMETHODCALLTYPE wrap_present1(Wrap* w, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* p) { return w->real->Present1(sync, flags, p); }
static HRESULT STDMETHODCALLTYPE wrap_unused() { return E_NOTIMPL; }
static HRESULT STDMETHODCALLTYPE wrap_create(IDXGIFactory2* f, IUnknown* dev, HWND wnd, const DXGI_SWAP_CHAIN_DESC1* d,
                                             const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fs, IDXGIOutput* o, IDXGISwapChain1** pp) {
    if (t_wrapping) return ((decltype(&wrap_create))g_wrap_create)(f, dev, wnd, d, fs, o, pp);
    IDXGISwapChain1* real = nullptr;
    t_wrapping = true;
    HRESULT hr = g_wrap_sl || g_wrap_fwd ? ((decltype(&wrap_create))g_wrap_create)(f, dev, wnd, d, fs, o, &real) : f->CreateSwapChainForHwnd(dev, wnd, d, fs, o, &real);
    t_wrapping = false;
    if (SUCCEEDED(hr)) *pp = (IDXGISwapChain1*)new Wrap{g_wrap_vt, real, 1};
    return hr;
}
static int frames_wrap_rows(const std::wstring& dir, int n, const wchar_t* how) {
    g_wrap_sl = !wcscmp(how, L"sl"), g_wrap_fwd = !wcscmp(how, L"fwd");
    SetEnvironmentVariableW(L"SCSKILLER_MODE", L"record");
    HMODULE m = LoadLibraryW((dir + L"d3d12.dll").c_str());
    auto proxy_create = m ? (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice") : nullptr;
    IDXGIFactory4* f = nullptr;
    IDXGIAdapter* warp = nullptr;
    CHECK(proxy_create && SUCCEEDED(CreateDXGIFactory2(0, IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumWarpAdapter(IID_PPV_ARGS(&warp))));
    for (auto& s : g_wrap_vt) s = (void*)wrap_unused;
    g_wrap_vt[0] = (void*)wrap_qi, g_wrap_vt[1] = (void*)wrap_addref, g_wrap_vt[2] = (void*)wrap_release;
    g_wrap_vt[8] = (void*)wrap_present, g_wrap_vt[22] = (void*)wrap_present1;
    void** fvt = *(void***)f;
    DWORD old;
    CHECK(VirtualProtect(&fvt[15], sizeof(void*), PAGE_READWRITE, &old));
    g_wrap_create = fvt[15], fvt[15] = (void*)wrap_create;
    VirtualProtect(&fvt[15], sizeof(void*), old, &old);
    ID3D12Device* dev = nullptr;
    ID3D12CommandQueue* q = nullptr;
    D3D12_COMMAND_QUEUE_DESC qd = {};
    CHECK(SUCCEEDED(proxy_create(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))) && SUCCEEDED(dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&q))));
    HWND wnd = CreateWindowExW(0, L"STATIC", L"scskiller frameswrap", WS_OVERLAPPEDWINDOW, 0, 0, 64, 64, nullptr, nullptr, nullptr, nullptr);
    DXGI_SWAP_CHAIN_DESC1 d = {64, 64, DXGI_FORMAT_R8G8B8A8_UNORM, FALSE, {1, 0}, DXGI_USAGE_RENDER_TARGET_OUTPUT, 2};
    d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    IDXGISwapChain1* sc = nullptr;
    CHECK(wnd && SUCCEEDED(f->CreateSwapChainForHwnd(q, wnd, &d, nullptr, nullptr, &sc)) && *(void***)sc == g_wrap_vt);
    printf("wrapper ours %d\n", module_of(g_wrap_vt[8]) == m || module_of(g_wrap_vt[22]) == m);
    void** rvt = *(void***)((Wrap*)sc)->real;
    printf("real ours %d\n", module_of(rvt[8]) == m && module_of(rvt[22]) == m);
    DXGI_PRESENT_PARAMETERS p = {};
    for (int i = 0; i < n; ++i) sc->Present(0, 0), sc->Present1(0, 0, &p);
    printf("frames %ld\n", frames_after(dir, 2 * n));
    return 0;
}

// `selftest framesmod <n>`: a mod's dxgi.dll (fakenext copied as dxgi.dll beside this exe, as ReShade as dxgi.dll) whose
// CreateDXGIFactory1 gives its own factory object around System32's. The proxy hooks System32's factory instead, and a
// swap chain made through the mod's factory is System32's: n Presents and n Present1s on it. Prints "mod factory <0|1>"
// (the factory's vtable isn't System32's) and "frames <n>".
static int frames_mod_rows(const std::wstring& dir, int n) {
    SetEnvironmentVariableW(L"SCSKILLER_MODE", L"record");
    HMODULE m = LoadLibraryW((dir + L"d3d12.dll").c_str());
    auto proxy_create = m ? (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice") : nullptr;
    IDXGIFactory2* f = nullptr;
    IDXGIFactory4* sys = nullptr;
    IDXGIAdapter* warp = nullptr;
    CHECK(proxy_create && SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&f))) && SUCCEEDED(CreateDXGIFactory2(0, IID_PPV_ARGS(&sys))) &&
          SUCCEEDED(sys->EnumWarpAdapter(IID_PPV_ARGS(&warp))));
    printf("mod factory %d\n", module_of(*(void**)f) != system_dxgi());
    ID3D12Device* dev = nullptr;
    ID3D12CommandQueue* q = nullptr;
    D3D12_COMMAND_QUEUE_DESC qd = {};
    CHECK(SUCCEEDED(proxy_create(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))) && SUCCEEDED(dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&q))));
    HWND wnd = CreateWindowExW(0, L"STATIC", L"scskiller framesmod", WS_OVERLAPPEDWINDOW, 0, 0, 64, 64, nullptr, nullptr, nullptr, nullptr);
    DXGI_SWAP_CHAIN_DESC1 d = {64, 64, DXGI_FORMAT_R8G8B8A8_UNORM, FALSE, {1, 0}, DXGI_USAGE_RENDER_TARGET_OUTPUT, 2};
    d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    IDXGISwapChain1* sc = nullptr;
    CHECK(wnd && SUCCEEDED(f->CreateSwapChainForHwnd(q, wnd, &d, nullptr, nullptr, &sc)));
    DXGI_PRESENT_PARAMETERS p = {};
    for (int i = 0; i < n; ++i) sc->Present(0, 0), sc->Present1(0, 0, &p);
    printf("frames %ld\n", frames_after(dir, 2 * n));
    return 0;
}

// `selftest framesheld`: presents on WARP through the proxy while scskiller_frames.bin is held open by another handle, so
// the proxy's first writes of it fail, then one present after it is let go. Prints "drift_us <n>": how far that frame's
// time in the file is from its QueryPerformanceCounter, from the file's launch record (frames lost, never time).
static int frames_held(const std::wstring& dir) {
    SetEnvironmentVariableW(L"SCSKILLER_MODE", L"record");
    HMODULE m = LoadLibraryW((dir + L"d3d12.dll").c_str());
    CHECK(m);
    auto proxy_create = (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice");
    IDXGIFactory4* f = nullptr;
    IDXGIAdapter* warp = nullptr;
    CHECK(proxy_create && SUCCEEDED(CreateDXGIFactory2(0, IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumWarpAdapter(IID_PPV_ARGS(&warp))));
    ID3D12Device* dev = nullptr;
    CHECK(SUCCEEDED(proxy_create(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))));
    HANDLE held = CreateFileW((dir + L"scskiller_frames.bin").c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    CHECK(held != INVALID_HANDLE_VALUE);
    HWND wnd = CreateWindowExW(0, L"STATIC", L"scskiller frames", WS_OVERLAPPEDWINDOW, 0, 0, 64, 64, nullptr, nullptr, nullptr, nullptr);
    D3D12_COMMAND_QUEUE_DESC qd = {};
    ID3D12CommandQueue* q = nullptr;
    DXGI_SWAP_CHAIN_DESC1 d = {64, 64, DXGI_FORMAT_R8G8B8A8_UNORM, FALSE, {1, 0}, DXGI_USAGE_RENDER_TARGET_OUTPUT, 2};
    d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    IDXGISwapChain1* sc = nullptr;
    CHECK(wnd && SUCCEEDED(dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&q))) && SUCCEEDED(f->CreateSwapChainForHwnd(q, wnd, &d, nullptr, nullptr, &sc)));
    for (int i = 0; i < 10; ++i) sc->Present(0, 0), Sleep(30);
    Sleep(2200);  // the proxy's writer tries once a second
    CloseHandle(held);
    sc->Present(0, 0);
    LARGE_INTEGER at;
    QueryPerformanceCounter(&at);
    uint64_t head[4] = {}, last = 0;
    long long want = 0;
    // until the last frame is the one just presented: the 10 before it are 2 s older, written late or not at all
    for (int i = 0; i < 600 && !(head[3] && last && want - (long long)last < 1000000); ++i, Sleep(50)) {
        FILE* fr = _wfopen((dir + L"scskiller_frames.bin").c_str(), L"rb");
        CHECK(fr);
        uint64_t us = 0;
        head[3] = last = 0;
        for (uint32_t r; fread(&r, 4, 1, fr) == 1;)
            if (r == 0xFFFFFFFF) us = last = 0, head[3] = fread(head, sizeof head, 1, fr) == 1 ? head[3] : 0;
            else if (r >> 28 == 15) us += (r & 0x0FFFFFFF) < (1u << 28) / 1000 ? 0 : (uint64_t)(r & 0x0FFFFFFF) * 1000;  // a tag: no gap
            else last = us += r & 0x0FFFFFFF;
        fclose(fr);
        if (head[3]) want = (long long)((at.QuadPart - (long long)head[2]) * 1000000 / (long long)head[3]);
    }
    CHECK(head[3]);
    printf("drift_us %lld\n", want - (long long)last);
    return 0;
}

// A compute PSO with root signature rs on dev; k makes the shader (and so the record) its own.
static HRESULT compute_pso(ID3D12Device* dev, ID3D12RootSignature* rs, int k) {
    ID3DBlob* cs = compile("RWByteAddressBuffer b : register(u0); [numthreads(1,1,1)] void main() { b.Store(0, " + std::to_string(k) + "); }", "cs_5_0");
    if (!cs) return E_FAIL;
    D3D12_COMPUTE_PIPELINE_STATE_DESC c = {};
    c.pRootSignature = rs, c.CS = {cs->GetBufferPointer(), cs->GetBufferSize()};
    ID3D12PipelineState* p = nullptr;
    return dev->CreateComputePipelineState(&c, IID_PPV_ARGS(&p));
}

// LdrRegisterDllNotification's (ntdll) data for a load
struct LdrLoaded { ULONG flags; PCUNICODE_STRING full, base; PVOID dll_base; ULONG size; };
static std::wstring g_spoof_dir, g_spoof_to;
// REFramework's (kananlib's spoof_module_paths_in_exe_dir): a dll loaded from the exe's folder gets its loader entry's
// FullDllName rewritten to <folder>\_storage_\<name>, a copy, before its DllMain runs.
static void CALLBACK spoof_path(ULONG reason, const LdrLoaded* d, PVOID) {
    std::wstring full(d->full->Buffer, d->full->Length / sizeof(wchar_t));
    if (reason != 1 || _wcsicmp(full.c_str(), (g_spoof_dir + L"d3d12.dll").c_str())) return;
    LIST_ENTRY* head = &NtCurrentTeb()->ProcessEnvironmentBlock->Ldr->InMemoryOrderModuleList;
    for (LIST_ENTRY* e = head->Flink; e != head; e = e->Flink)
        if (auto t = CONTAINING_RECORD(e, LDR_DATA_TABLE_ENTRY, InMemoryOrderLinks); t->DllBase == d->dll_base)
            t->FullDllName.Buffer = g_spoof_to.data(), t->FullDllName.Length = t->FullDllName.MaximumLength = USHORT(g_spoof_to.size() * sizeof(wchar_t));
}

// `selftest anticheat <client dll | -> [folder | spoof]`: a compute PSO on a device made through the proxy d3d12.dll next to
// the exe (WARP), then the client dll is loaded (an anti-cheat client's module name; "-": none) and a second PSO. Prints
// "created 0x<hr> 0x<hr>". The proxy decided admission at the device: a client loaded after it doesn't change the run.
// "+<client dll>": the client is loaded before the device instead. folder: the proxy is loaded from that folder of the
// exe's (a mod's copy elsewhere). spoof: the proxy next to the exe has its loader path rewritten the way REFramework does
// (spoof_path); prints "path <the path GetModuleFileNameW reports>".
static int anticheat_rows(const std::wstring& dir, const wchar_t* client, const std::wstring& from = L"") {
    SetEnvironmentVariableW(L"SCSKILLER_MODE", L"record");
    const bool early = *client == L'+', spoof = from == L"spoof\\";
    if (early) CHECK(LoadLibraryW(client + 1));
    if (spoof) {
        g_spoof_dir = dir, g_spoof_to = dir + L"_storage_\\d3d12.dll";
        CreateDirectoryW((dir + L"_storage_").c_str(), nullptr);
        CHECK(CopyFileW((dir + L"d3d12.dll").c_str(), g_spoof_to.c_str(), FALSE));
        auto reg = (NTSTATUS(NTAPI*)(ULONG, decltype(&spoof_path), PVOID, PVOID*))GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "LdrRegisterDllNotification");
        PVOID cookie;
        CHECK(reg && reg(0, spoof_path, nullptr, &cookie) >= 0);
    }
    HMODULE m = LoadLibraryW((dir + (spoof ? L"" : from) + L"d3d12.dll").c_str());
    CHECK(m);
    if (spoof) {
        wchar_t p[MAX_PATH];
        GetModuleFileNameW(m, p, MAX_PATH);
        printf("path %ls\n", p);
    }
    auto create = (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice");
    auto ser = (decltype(&D3D12SerializeRootSignature))GetProcAddress(m, "D3D12SerializeRootSignature");
    IDXGIFactory4* f = nullptr;
    IDXGIAdapter* warp = nullptr;
    CHECK(create && ser && SUCCEEDED(CreateDXGIFactory2(0, IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumWarpAdapter(IID_PPV_ARGS(&warp))));
    ID3D12Device* dev = nullptr;
    CHECK(SUCCEEDED(create(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))));
    D3D12_ROOT_PARAMETER up = {D3D12_ROOT_PARAMETER_TYPE_UAV};
    D3D12_ROOT_SIGNATURE_DESC rd = {1, &up};
    ID3DBlob *rb = nullptr, *err = nullptr;
    ID3D12RootSignature* rs = nullptr;
    CHECK(SUCCEEDED(ser(&rd, D3D_ROOT_SIGNATURE_VERSION_1, &rb, &err)) && SUCCEEDED(dev->CreateRootSignature(0, rb->GetBufferPointer(), rb->GetBufferSize(), IID_PPV_ARGS(&rs))));
    HRESULT a = compute_pso(dev, rs, GetTickCount());
    if (wcscmp(client, L"-") && !early) CHECK(LoadLibraryW(client));
    HRESULT b = compute_pso(dev, rs, GetTickCount() + 1);
    printf("created 0x%08x 0x%08x\n", (unsigned)a, (unsigned)b);
    return 0;
}

// `selftest factoryrejected` (run unstamped): a device factory both through an SDK configuration the proxy got before its first
// device (its CreateDeviceFactory hook) and straight from D3D12GetInterface, each after that first device was rejected.
// Prints "config factory hooked <0|1>" (or "no config factory") and "factory hooked <0|1>": a rejected run hooks neither.
static int factory_rejected_rows(const std::wstring& dir) {
    HMODULE m = LoadLibraryW((dir + L"d3d12.dll").c_str());
    CHECK(m);
    auto create = (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice");
    auto get_interface = (decltype(&D3D12GetInterface))GetProcAddress(m, "D3D12GetInterface");
    IDXGIFactory4* f = nullptr;
    IDXGIAdapter* warp = nullptr;
    CHECK(create && get_interface && SUCCEEDED(CreateDXGIFactory2(0, IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumWarpAdapter(IID_PPV_ARGS(&warp))));
    ID3D12SDKConfiguration1* cfg = nullptr;
    bool has_cfg = SUCCEEDED(get_interface(CLSID_D3D12SDKConfiguration, IID_PPV_ARGS(&cfg)));
    ID3D12Device* dev = nullptr;
    CHECK(SUCCEEDED(create(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))));
    dev->Release();
    auto hooked = [&](ID3D12DeviceFactory* fac) {
        HMODULE owner = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)(*(void***)fac)[9], &owner);
        return owner == m;
    };
    ID3D12DeviceFactory* fac = nullptr;
    HRESULT chr = has_cfg ? cfg->CreateDeviceFactory(0, "", IID_PPV_ARGS(&fac)) : E_NOINTERFACE;  // 0, "": the system runtime's own
    if (SUCCEEDED(chr) && fac) printf("config factory hooked %d\n", hooked(fac)), fac->Release(), fac = nullptr;
    else printf("no config factory (0x%08x)\n", (unsigned)chr);
    if (SUCCEEDED(get_interface(CLSID_D3D12DeviceFactory, IID_PPV_ARGS(&fac)))) printf("factory hooked %d\n", hooked(fac)), fac->Release();
    else printf("no factory\n");
    return 0;
}

// `selftest factory [client dll]`: a device from ID3D12DeviceFactory (the proxy's D3D12GetInterface, CLSID_D3D12DeviceFactory)
// on WARP, and a compute PSO on it. The proxy is freed once before the device: its factory hook keeps it loaded. With a
// client dll, it is loaded (an anti-cheat client's module name) just before the device. Prints "loaded <0|1>" and
// "created 0x<hr>", or "no factory" where the runtime has none.
static int factory_rows(const std::wstring& dir, const wchar_t* client = nullptr) {
    SetEnvironmentVariableW(L"SCSKILLER_MODE", L"record");
    HMODULE m = LoadLibraryW((dir + L"d3d12.dll").c_str());
    CHECK(m);
    auto get_interface = (decltype(&D3D12GetInterface))GetProcAddress(m, "D3D12GetInterface");
    auto ser = (decltype(&D3D12SerializeRootSignature))GetProcAddress(m, "D3D12SerializeRootSignature");
    IDXGIFactory4* f = nullptr;
    IDXGIAdapter* warp = nullptr;
    CHECK(get_interface && ser && SUCCEEDED(CreateDXGIFactory2(0, IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumWarpAdapter(IID_PPV_ARGS(&warp))));
    ID3D12DeviceFactory* fac = nullptr;
    if (FAILED(get_interface(CLSID_D3D12DeviceFactory, IID_PPV_ARGS(&fac)))) return printf("no factory\n"), 0;
    FreeLibrary(m);
    printf("loaded %d\n", GetModuleHandleW((dir + L"d3d12.dll").c_str()) != nullptr);
    fflush(stdout);
    if (client) CHECK(LoadLibraryW(client));
    ID3D12Device* dev = nullptr;
    CHECK(SUCCEEDED(fac->CreateDevice(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))));
    D3D12_ROOT_PARAMETER up = {D3D12_ROOT_PARAMETER_TYPE_UAV};
    D3D12_ROOT_SIGNATURE_DESC rd = {1, &up};
    ID3DBlob *rb = nullptr, *err = nullptr;
    ID3D12RootSignature* rs = nullptr;
    CHECK(SUCCEEDED(ser(&rd, D3D_ROOT_SIGNATURE_VERSION_1, &rb, &err)) && SUCCEEDED(dev->CreateRootSignature(0, rb->GetBufferPointer(), rb->GetBufferSize(), IID_PPV_ARGS(&rs))));
    ID3DBlob* cs = compile("RWByteAddressBuffer b : register(u0); [numthreads(1,1,1)] void main() { b.Store(0, " + std::to_string(GetTickCount()) + "); }", "cs_5_0");
    CHECK(cs);
    D3D12_COMPUTE_PIPELINE_STATE_DESC c = {};
    c.pRootSignature = rs, c.CS = {cs->GetBufferPointer(), cs->GetBufferSize()};
    ID3D12PipelineState* p = nullptr;
    printf("created 0x%08x\n", (unsigned)dev->CreateComputePipelineState(&c, IID_PPV_ARGS(&p)));
    return 0;
}

// `selftest unload`: a device made through the proxy d3d12.dll next to the exe on WARP, released, then FreeLibrary (a game
// probing for D3D12). Prints "loaded <0|1>" (the proxy is still loaded: its hooks are in the runtime's vtables) and
// "created 0x<hr>" for a root signature on a device from the system dll, which runs the proxy's hook.
static int unload_rows(const std::wstring& dir) {
    SetEnvironmentVariableW(L"SCSKILLER_MODE", L"record");
    HMODULE m = LoadLibraryW((dir + L"d3d12.dll").c_str()), real = load_system(L"d3d12.dll");
    CHECK(m && real);
    auto proxy_create = (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice");
    auto real_create = (decltype(&D3D12CreateDevice))GetProcAddress(real, "D3D12CreateDevice");
    auto ser = (decltype(&D3D12SerializeRootSignature))GetProcAddress(real, "D3D12SerializeRootSignature");
    IDXGIFactory4* f = nullptr;
    IDXGIAdapter* warp = nullptr;
    CHECK(proxy_create && real_create && ser && SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumWarpAdapter(IID_PPV_ARGS(&warp))));
    ID3D12Device* dev = nullptr;
    CHECK(SUCCEEDED(proxy_create(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))));
    dev->Release();
    FreeLibrary(m);
    printf("loaded %d\n", GetModuleHandleW((dir + L"d3d12.dll").c_str()) != nullptr);
    fflush(stdout);
    CHECK(SUCCEEDED(real_create(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))));
    D3D12_ROOT_PARAMETER up = {D3D12_ROOT_PARAMETER_TYPE_UAV};
    D3D12_ROOT_SIGNATURE_DESC rd = {1, &up};
    ID3DBlob *rb = nullptr, *err = nullptr;
    ID3D12RootSignature* rs = nullptr;
    CHECK(SUCCEEDED(ser(&rd, D3D_ROOT_SIGNATURE_VERSION_1, &rb, &err)));
    HRESULT hr = dev->CreateRootSignature(0, rb->GetBufferPointer(), rb->GetBufferSize(), IID_PPV_ARGS(&rs));
    printf("created 0x%08x\n", (unsigned)hr);
    return SUCCEEDED(hr) ? 0 : 1;
}

// What the app writes after a clean anti-cheat check (ScsKiller.IniText, ScsKiller.Signed): this exe's size and write time
// in scskiller.ini here, root= (this folder) unless the ini names one, and sig= under the recorder folder's recorder.key
// (SCSKILLER_TEST_RECORDER_DIR's, made there when missing; else the app's own, never made). Without a key: unsigned.
static void arm_self(const std::wstring& dir, const std::wstring& exe) {
    WIN32_FILE_ATTRIBUTE_DATA self;
    if (!GetFileAttributesExW(exe.c_str(), GetFileExInfoStandard, &self)) return;
    const std::wstring ini = dir + L"scskiller.ini";
    const std::wstring size = std::to_wstring((uint64_t)self.nFileSizeHigh << 32 | self.nFileSizeLow);
    const std::wstring time = std::to_wstring((uint64_t)self.ftLastWriteTime.dwHighDateTime << 32 | self.ftLastWriteTime.dwLowDateTime);
    WritePrivateProfileStringW(L"scskiller", L"exe_size", size.c_str(), ini.c_str());
    WritePrivateProfileStringW(L"scskiller", L"exe_time", time.c_str(), ini.c_str());
    wchar_t root[1024];
    if (!GetPrivateProfileStringW(L"scskiller", L"root", L"", root, 1024, ini.c_str())) {
        WritePrivateProfileStringW(L"scskiller", L"root", L".", ini.c_str());
        wcscpy_s(root, L".");
    }
    const std::wstring rdir = recorder_dir(dir), key_file = rdir + L"\\recorder.key";
    if (rdir.empty()) return;
    char key[129] = {};
    DWORD n = 0;
    HANDLE h = CreateFileW(key_file.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
    if (h == INVALID_HANDLE_VALUE && GetEnvironmentVariableW(L"SCSKILLER_TEST_RECORDER_DIR", nullptr, 0)) {
        CreateDirectoryW(rdir.c_str(), nullptr);
        std::random_device rd;
        for (int i = 0; i < 64; ++i) key[i] = "0123456789abcdef"[rd() & 15];
        // written whole under a temp name, then moved in: a run reading it meanwhile never sees half a key
        const std::wstring temp = key_file + L"." + std::to_wstring(GetCurrentProcessId());
        h = CreateFileW(temp.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
        if (h != INVALID_HANDLE_VALUE) WriteFile(h, key, 64, &n, nullptr), CloseHandle(h);
        if (!MoveFileExW(temp.c_str(), key_file.c_str(), 0)) DeleteFileW(temp.c_str());  // another run's is there: that one
        h = CreateFileW(key_file.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
    }
    if (h == INVALID_HANDLE_VALUE) return;
    if (!ReadFile(h, key, 128, &n, nullptr)) n = 0;
    CloseHandle(h);
    if (n < 32) return;
    const std::wstring here = final_dir(dir);
    wchar_t full[MAX_PATH];
    if (here.empty() || !GetFullPathNameW((here + root).c_str(), MAX_PATH, full, nullptr)) return;
    std::wstring root_dir = full;
    if (root_dir.size() > 3 && root_dir.back() == L'\\') root_dir.pop_back();
    const auto line = [&](const wchar_t* k) {
        wchar_t v[64] = {};
        GetPrivateProfileStringW(L"scskiller", k, L"", v, 64, ini.c_str());
        return std::wstring(v);
    };
    const std::wstring stamp = signed_stamp(size, time, L"", L"", L"", line(L"vendor"), line(L"overlay"), line(L"ab_play_s"), line(L"ab_tag"));
    const std::wstring sig = recorder_sig(std::string(key, n), file_key(exe), file_key(dir.substr(0, dir.size() - 1)), stamp, root, file_key(root_dir));
    if (!sig.empty()) WritePrivateProfileStringW(L"scskiller", L"sig", sig.c_str(), ini.c_str());
}

// `selftest rec11 <client dll | ->`: on a WARP device made through the proxy d3d11.dll next to the exe, two input layouts
// of one VS, then draws (other shaders, a GS, a HS+DS pair, a deferred context: none of them recorded). The client dll ("-":
// none) is loaded before the device. Prints "drew <n>"; the test reads the 'D' records: one per layout, with the VS's bytes.
// "ctx": the device is asked for only through its immediate context (D3D11CreateDevice's ppDevice null), as some games do.
static int rec11_rows(const std::wstring& dir, const wchar_t* client, bool ctx_only = false) {
    SetEnvironmentVariableW(L"SCSKILLER_MODE", L"record");
    if (wcscmp(client, L"-")) CHECK(LoadLibraryW(client));
    HMODULE m = LoadLibraryW((dir + L"d3d11.dll").c_str());
    CHECK(m);
    auto create = (decltype(&D3D11CreateDevice))GetProcAddress(m, "D3D11CreateDevice");
    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* c = nullptr;
    D3D_FEATURE_LEVEL fl = D3D_FEATURE_LEVEL_11_0;
    CHECK(create && SUCCEEDED(create(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, &fl, 1, D3D11_SDK_VERSION, ctx_only ? nullptr : &dev, nullptr, &c)));
    if (ctx_only) c->GetDevice(&dev);
    unsigned k = GetTickCount();
    std::string kc = std::to_string(k % 100000) + ".5";
    ID3DBlob* vsa = compile("float4 main(float3 p:POSITION,float2 uv:TEXCOORD0):SV_Position{return float4(p*" + kc + ",uv.x);}", "vs_5_0");
    ID3DBlob* vsc = compile("struct O{float4 p:SV_Position;float2 uv:TEXCOORD0;};O main(uint i:SV_VertexID){O o;o.uv=float2(i," + kc + ");o.p=float4(o.uv,0,1);return o;}", "vs_5_0");
    ID3DBlob* gsb = compile("struct V{float4 p:SV_Position;float2 uv:TEXCOORD0;};[maxvertexcount(3)]void main(triangle V v[3],inout TriangleStream<V> s){"
                            "for(int i=0;i<3;++i){V o=v[i];o.p.x+=" + kc + ";s.Append(o);}}", "gs_5_0");
    const std::string cp = "struct CP{float3 p:POS;};struct PC{float e[3]:SV_TessFactor;float i:SV_InsideTessFactor;};";
    ID3DBlob* vst = compile(cp + "CP main(uint i:SV_VertexID){CP o;o.p=float3(i," + kc + ",0);return o;}", "vs_5_0");
    ID3DBlob* hsb = compile(cp + "PC pcf(InputPatch<CP,3> ip){PC o;o.e[0]=o.e[1]=o.e[2]=o.i=ip[0].p.x;return o;}[domain(\"tri\")][partitioning(\"integer\")]"
                            "[outputtopology(\"triangle_cw\")][outputcontrolpoints(3)][patchconstantfunc(\"pcf\")]CP main(InputPatch<CP,3> ip,uint i:SV_OutputControlPointID){return ip[i];}", "hs_5_0");
    ID3DBlob* dsb = compile(cp + "[domain(\"tri\")]float4 main(PC pc,float3 b:SV_DomainLocation,const OutputPatch<CP,3> p):SV_Position{return float4(p[0].p*b.x,1);}", "ds_5_0");
    ID3DBlob* psb = compile("float4 main(float4 p:SV_Position):SV_Target{return p;}", "ps_5_0");
    CHECK(vsa && vsc && gsb && vst && hsb && dsb && psb);
    ID3D11VertexShader *va = nullptr, *vc = nullptr, *vt = nullptr;
    ID3D11GeometryShader* gs = nullptr;
    ID3D11HullShader* hs = nullptr;
    ID3D11DomainShader* ds = nullptr;
    ID3D11PixelShader* ps = nullptr;
    CHECK(SUCCEEDED(dev->CreateVertexShader(vsa->GetBufferPointer(), vsa->GetBufferSize(), nullptr, &va)) &&
          SUCCEEDED(dev->CreateVertexShader(vsc->GetBufferPointer(), vsc->GetBufferSize(), nullptr, &vc)) &&
          SUCCEEDED(dev->CreateVertexShader(vst->GetBufferPointer(), vst->GetBufferSize(), nullptr, &vt)) &&
          SUCCEEDED(dev->CreateGeometryShader(gsb->GetBufferPointer(), gsb->GetBufferSize(), nullptr, &gs)) &&
          SUCCEEDED(dev->CreateHullShader(hsb->GetBufferPointer(), hsb->GetBufferSize(), nullptr, &hs)) &&
          SUCCEEDED(dev->CreateDomainShader(dsb->GetBufferPointer(), dsb->GetBufferSize(), nullptr, &ds)) &&
          SUCCEEDED(dev->CreatePixelShader(psb->GetBufferPointer(), psb->GetBufferSize(), nullptr, &ps)));
    D3D11_INPUT_ELEMENT_DESC l1[] = {{"POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0}, {"TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, D3D11_APPEND_ALIGNED_ELEMENT}};
    D3D11_INPUT_ELEMENT_DESC l2[] = {{"POSITION", 0, DXGI_FORMAT_R16G16B16A16_FLOAT, 0, 0}, {"TEXCOORD", 0, DXGI_FORMAT_R16G16_FLOAT, 1, 0}};
    ID3D11InputLayout *il1 = nullptr, *il2 = nullptr;
    CHECK(SUCCEEDED(dev->CreateInputLayout(l1, 2, vsa->GetBufferPointer(), vsa->GetBufferSize(), &il1)) &&
          SUCCEEDED(dev->CreateInputLayout(l2, 2, vsa->GetBufferPointer(), vsa->GetBufferSize(), &il2)));
    int drew = 0;
    auto draw = [&](ID3D11DeviceContext* x, ID3D11InputLayout* il, ID3D11VertexShader* v, ID3D11GeometryShader* g, ID3D11HullShader* h,
                    ID3D11DomainShader* d, ID3D11PixelShader* p, D3D11_PRIMITIVE_TOPOLOGY t) {
        x->IASetInputLayout(il), x->IASetPrimitiveTopology(t), x->VSSetShader(v, nullptr, 0), x->GSSetShader(g, nullptr, 0);
        x->HSSetShader(h, nullptr, 0), x->DSSetShader(d, nullptr, 0), x->PSSetShader(p, nullptr, 0);
        x->Draw(3, 0), ++drew;
    };
    draw(c, il1, va, nullptr, nullptr, nullptr, ps, D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);    // 1
    draw(c, il1, va, nullptr, nullptr, nullptr, ps, D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);    // the same: no record
    draw(c, il1, va, nullptr, nullptr, nullptr, ps, D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);   // a strip is a list: no record
    draw(c, il2, va, nullptr, nullptr, nullptr, ps, D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);    // 2: the other layout
    draw(c, il1, va, nullptr, nullptr, nullptr, ps, D3D11_PRIMITIVE_TOPOLOGY_LINELIST);        // 3: lines
    draw(c, nullptr, vc, gs, nullptr, nullptr, ps, D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);     // 4: VS -> GS
    draw(c, nullptr, vt, nullptr, hs, ds, nullptr, D3D11_PRIMITIVE_TOPOLOGY_3_CONTROL_POINT_PATCHLIST);  // 5: VS -> HS -> DS, no PS
    ID3D11DeviceContext* def = nullptr;
    CHECK(SUCCEEDED(dev->CreateDeferredContext(0, &def)));
    draw(def, il1, va, nullptr, nullptr, nullptr, ps, D3D11_PRIMITIVE_TOPOLOGY_POINTLIST);     // 6: points, on a deferred context
    ID3D11CommandList* cl = nullptr;
    CHECK(SUCCEEDED(def->FinishCommandList(FALSE, &cl)));
    c->ExecuteCommandList(cl, FALSE);
    c->ClearState();
    c->Draw(3, 0);  // nothing bound: no record
    c->Flush();
    printf("drew %d\n", drew);
    return 0;
}

// `selftest rec11frames <n>`: a WARP device and swap chain made by the proxy d3d11.dll's D3D11CreateDeviceAndSwapChain, one
// input layout, then n presents. Prints "frames <n>" (scskiller_frames.bin's); the test reads what the ini let it record.
static int rec11_frames(const std::wstring& dir, int n) {
    SetEnvironmentVariableW(L"SCSKILLER_MODE", L"record");
    HMODULE m = LoadLibraryW((dir + L"d3d11.dll").c_str());
    auto create = m ? (decltype(&D3D11CreateDeviceAndSwapChain))GetProcAddress(m, "D3D11CreateDeviceAndSwapChain") : nullptr;
    HWND wnd = CreateWindowExW(0, L"STATIC", L"scskiller frames", WS_OVERLAPPEDWINDOW, 0, 0, 64, 64, nullptr, nullptr, nullptr, nullptr);
    CHECK(create && wnd);
    DXGI_SWAP_CHAIN_DESC d = {{64, 64, {}, DXGI_FORMAT_R8G8B8A8_UNORM}, {1, 0}, DXGI_USAGE_RENDER_TARGET_OUTPUT, 2, wnd, TRUE, DXGI_SWAP_EFFECT_FLIP_DISCARD};
    D3D_FEATURE_LEVEL fl = D3D_FEATURE_LEVEL_11_0;
    IDXGISwapChain* sc = nullptr;
    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* c = nullptr;
    CHECK(SUCCEEDED(create(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, &fl, 1, D3D11_SDK_VERSION, &d, &sc, &dev, nullptr, &c)));
    ID3DBlob* vs = compile("float4 main(float3 p:POSITION):SV_Position{return float4(p*" + std::to_string(GetTickCount() % 100000) + ".5,1);}", "vs_5_0");
    D3D11_INPUT_ELEMENT_DESC l[] = {{"POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0}};
    ID3D11InputLayout* il = nullptr;
    CHECK(vs && SUCCEEDED(dev->CreateInputLayout(l, 1, vs->GetBufferPointer(), vs->GetBufferSize(), &il)));
    for (int i = 0; i < n; ++i) sc->Present(0, 0);
    Sleep(2500);  // the proxy writes the frames once a second
    printf("frames %ld\n", frames_in(dir));
    return 0;
}

// `selftest overlayunit` (no device): the overlay's font, layout, painting, formats, graph, feed, stutter rule and play
// clock (overlay.h). Prints "overlayunit ok".
static int overlay_unit() {
    for (auto [h, s] : {std::pair{480, 1}, {720, 1}, {1080, 2}, {1200, 2}, {1440, 3}, {2160, 4}, {4320, ov::kMaxScale}}) CHECK(ov::scale_for(h) == s);
    CHECK(ov::countdown(false, 222) == "SCSKiller before 3:42");
    CHECK(ov::countdown(true, 0.2) == "SCSKiller after 0:01");
    CHECK(ov::countdown(true, -1) == "SCSKiller after 0:00");
    CHECK(ov::countdown(false, 600) == "SCSKiller before 10:00");
    for (int s = 1; s <= ov::kMaxScale; ++s) {
        const auto& f = ov::font(s);
        for (const char* t : {"SCSKiller before after 0123456789:", ov::kDone, ov::kStutter})
            for (const char* c = t; *c; ++c) CHECK(*c == ' ' || ov::glyph(f, *c));
        for (char d = '1'; d <= '9'; ++d) CHECK(ov::glyph(f, d)->adv == ov::glyph(f, '0')->adv);  // the countdown doesn't shift as it ticks
        for (int i = 0; i < f.n; ++i) {  // every glyph inside its line and its pen step
            const auto& g = f.glyphs[i];
            CHECK(g.y >= 0 && g.y + g.h <= f.height && g.x + g.w <= g.adv + 2);
        }
        CHECK(ov::text_width("SCSKiller before 10:00", s) < ov::kMaxW && ov::text_width(ov::kStutter, s) <= ov::kMaxW - 8 * s);
    }
    ov::Layout L;
    ov::layout(1920, 1080, "SCSKiller before 5:00", true, L);
    CHECK(L.s == 2 && L.at.right == 1920 - 16 && L.at.top == 16 && L.at.right - L.at.left == L.w && L.at.bottom - L.at.top == L.h && !L.sx && !L.sy);
    CHECK(L.w == ov::text_width(ov::kStutter, 2) + 16 && L.track_y > L.text_y + ov::font(2).height && L.graph_y > L.track_y && L.flash_y > L.graph_y + L.graph_h);
    CHECK(L.w <= ov::kMaxW && L.h <= ov::kMaxH);
    ov::Layout L4;
    ov::layout(3840, 2160, "SCSKiller after 10:00", true, L4);
    CHECK(L4.s == ov::kMaxScale && L4.w <= ov::kMaxW && L4.h == ov::kMaxH);
    ov::Layout N;
    ov::layout(1920, 1080, "SCSKiller before 5:00", false, N);  // D3D11: no flash line, as wide as its texts
    CHECK(N.h == L.graph_y + L.graph_h + L.pad && N.w == ov::text_width(ov::kDone, 2) + 16);
    ov::layout(64, 64, ov::kDone, true, L);  // wider than the buffer: clipped into it
    CHECK(L.at.left == 0 && L.at.top == 8 && L.at.right == 64 - 8 && L.at.bottom == 64 && L.sx == L.w - 56);
    {  // painted: the panel, the text's coverage, the bar to 40%, the graph's bars and 50 ms line, the flash
        ov::Layout P;
        ov::layout(1920, 1080, "SCSKiller after 5:00", true, P);
        ov::Graph g;
        g.frame(0, 16), g.frame(16, 400), g.frame(400, 416), g.mark(16, 400);
        std::vector<uint32_t> img(P.w * P.h);
        ov::paint(P, "SCSKiller after 1:23", 0.4, g, true, img.data(), P.w);
        auto at = [&](long x, long y) { return img[y * P.w + x]; };
        CHECK(at(0, 0) == ov::kPanel && at(P.w - 1, P.h - 1) == ov::kPanel);
        long text = 0, mixed = 0, fill = 0, red = 0, slow = 0, line = 0;
        for (long y = P.text_y; y < P.text_y + ov::font(2).height; ++y)
            for (long x = 0; x < P.w; ++x) text += at(x, y) == ov::kText, mixed += at(x, y) != ov::kText && at(x, y) != ov::kPanel;
        CHECK(text > 100 && mixed > 100);  // antialiased: edge pixels between the two
        for (long x = 0; x < P.w; ++x) fill += at(x, P.track_y) == ov::kFill;
        CHECK(fill == std::lround((P.w - 2 * P.pad) * 0.4));
        for (long y = P.graph_y; y < P.graph_y + P.graph_h; ++y)
            for (long x = 0; x < P.w; ++x) red += at(x, y) == ov::kShader, slow += at(x, y) == ov::kSlow, line += at(x, y) == ov::kLine;
        CHECK(red > 0 && slow == 0 && line > 0);
        long iw = P.w - 2 * P.pad, col = iw / ov::Graph::kCols;  // the newest at the right: the 384 ms frame's 5 columns reach the top
        for (int i : {ov::Graph::kCols - 5, ov::Graph::kCols - 1}) CHECK(at(P.pad + iw * i / ov::Graph::kCols + col / 2, P.graph_y) == ov::kShader);
        CHECK(at(P.pad + col / 2, P.graph_y + P.graph_h - 1) == ov::kGraphBg);
        long flash = 0;
        for (long y = P.flash_y; y < P.flash_y + ov::font(2).height; ++y)
            for (long x = 0; x < P.w; ++x) flash += at(x, y) == ov::kShader;
        CHECK(flash > 50);
        wchar_t dir[MAX_PATH];
        if (GetEnvironmentVariableW(L"SCSKILLER_OVERLAY_DUMP", dir, MAX_PATH))  // the panel as painted, for a look
            if (FILE* o = _wfopen((std::wstring(dir) + L"\\panel.ppm").c_str(), L"wb")) {
                fprintf(o, "P6\n%ld %ld\n255\n", P.w, P.h);
                for (uint32_t p : img) fputc(p & 255, o), fputc(p >> 8 & 255, o), fputc(p >> 16 & 255, o);
                fclose(o);
            }
        ov::paint(P, "SCSKiller after 1:23", 0.4, g, false, img.data(), P.w);
        for (long y = P.flash_y; y < P.flash_y + ov::font(2).height; ++y)
            for (long x = 0; x < P.w; ++x) CHECK(at(x, y) == ov::kPanel);
    }
    {  // the graph: 5 s of 100 ms columns, a long frame in each it overlaps, scrolled by the newest frame's end
        ov::Graph g;
        g.frame(0, 10);
        CHECK(g.newest == 0 && g.ms[0] == 10);
        g.frame(10, 260);
        CHECK(g.newest == 2 && g.ms[0] == 250 && g.ms[1] == 250 && g.ms[2] == 250);
        g.frame(260, 6000);  // longer than the graph: every column
        CHECK(g.newest == 60 && std::all_of(g.ms, g.ms + ov::Graph::kCols, [](float m) { return m == 5740; }));
        g.frame(6000, 6016);
        CHECK(g.ms[60 % ov::Graph::kCols] == 5740);
        g.frame(6016, 6110);  // a new column starts empty
        CHECK(g.newest == 61 && g.ms[61 % ov::Graph::kCols] == 94 && !g.shader[61 % ov::Graph::kCols]);
    }
    {  // formats: SDR bytes as they are, 10-bit, HDR10 at BT.2408's white, scRGB at 203 / 80
        const uint32_t px[3] = {ov::kText, ov::kFill, ov::kPanel};
        uint32_t o4[3];
        uint64_t o8[3];
        ov::convert(px, 3, 3, 1, ov::kRgba8, (uint8_t*)o4, 12);
        CHECK(o4[0] == ov::kText && o4[1] == ov::kFill);
        ov::convert(px, 3, 3, 1, ov::kBgra8, (uint8_t*)o4, 12);
        CHECK(o4[1] == (255u | 166u << 8 | 64u << 16 | 0xFF000000u));
        ov::convert(px, 3, 3, 1, ov::kRgb10, (uint8_t*)o4, 12);
        CHECK(o4[0] == 0xFFFFFFFFu && o4[1] == (257u | 666u << 10 | 1023u << 20 | 3u << 30));
        ov::convert(px, 3, 3, 1, ov::kPq10, (uint8_t*)o4, 12);
        uint32_t w = o4[0] & 1023;
        CHECK(w > 570 && w < 600 && (o4[0] >> 10 & 1023) == w && (o4[0] >> 20 & 1023) == w && o4[0] >> 30 == 3);  // 203 nits: PQ 0.58
        CHECK((o4[2] & 1023) < 120 && (o4[1] >> 20 & 1023) > (o4[1] & 1023));  // the panel near black; the fill stays blue
        ov::convert(px, 3, 3, 1, ov::kScRgb, (uint8_t*)o8, 24);
        CHECK(std::abs(DirectX::PackedVector::XMConvertHalfToFloat((uint16_t)o8[0]) - 203.0f / 80) < 0.01f && o8[0] >> 48 == 0x3C00);
        CHECK(ov::out_of(DXGI_FORMAT_R10G10B10A2_UNORM, DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709, false) == ov::kNone);  // can't tell SDR from HDR10
        CHECK(ov::out_of(DXGI_FORMAT_R10G10B10A2_UNORM, DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709, true) == ov::kRgb10);
        CHECK(ov::out_of(DXGI_FORMAT_R10G10B10A2_UNORM, DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020, true) == ov::kPq10);
        CHECK(ov::out_of(DXGI_FORMAT_R10G10B10A2_UNORM, DXGI_COLOR_SPACE_YCBCR_STUDIO_G2084_LEFT_P2020, true) == ov::kNone);
        CHECK(ov::out_of(DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709, false) == ov::kScRgb);
        CHECK(ov::out_of(DXGI_FORMAT_B8G8R8A8_UNORM_SRGB, DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709, false) == ov::kBgra8);
        CHECK(!ov::drawn_format(DXGI_FORMAT_R11G11B10_FLOAT) && ov::drawn_format(DXGI_FORMAT_R16G16B16A16_FLOAT));
    }
    {  // the feed: in order; a writer that finds it full drops its frame, never waits
        auto f = std::make_unique<ov::Feed>();
        std::vector<double> got;
        for (int i = 0; i < 3; ++i) f->push(i);
        f->drain([&](double t) { got.push_back(t); });
        CHECK(got == std::vector<double>({0, 1, 2}));
        got.clear();
        for (int i = 0; i < (int)ov::Feed::N + 5; ++i) f->push(i);
        f->drain([&](double t) { got.push_back(t); });
        CHECK(got.size() == ov::Feed::N && got.back() == ov::Feed::N - 1);  // full: the newest dropped, none overwritten
    }
    {  // FrameLog's cause of a slow frame from the creates that end in it
        using C = ov::Creates;
        auto c = std::make_unique<C>();
        c->add(100, 5, 1, C::kCompile | C::kPresents);  // before the frame
        CHECK(!c->shader_stutter(200, 300));
        c->add(260, 40, 1, C::kCompile | C::kBlocks | C::kPresents);  // a compile that blocks inside it
        CHECK(c->shader_stutter(200, 300) && !c->shader_stutter(400, 500));
        auto hits = std::make_unique<C>();
        hits->add(250, 5, 1, C::kPresents);  // a cache hit
        CHECK(!hits->shader_stutter(200, 300));
        auto load = std::make_unique<C>();  // 100 creates, one blocking compile among them: a load
        for (int i = 0; i < 99; ++i) load->add(210 + i * 0.5, 1, 1, 0);
        load->add(290, 20, 1, C::kCompile | C::kBlocks);
        CHECK(!load->shader_stutter(200, 300));
        load->add(399, 300, 1, C::kCompile | C::kBlocks);  // compiles of 100 ms or more for half the frame, inside it: a shader stutter
        CHECK(load->shader_stutter(200, 400));
        auto span = std::make_unique<C>();  // one compile across two slow frames: only the frame it ends in
        span->add(350, 200, 1, C::kCompile | C::kBlocks | C::kPresents);
        CHECK(!span->shader_stutter(200, 300) && span->shader_stutter(300, 400));
        auto worker = std::make_unique<C>();  // a long compile on a worker thread overlapping a slow frame, ending later
        worker->add(10500, 10500, 2, C::kCompile | C::kBlocks);
        CHECK(!worker->shader_stutter(200, 300));
        auto par = std::make_unique<C>();  // a compile on 8 threads, none the presenter's: a precache
        for (uint32_t t = 1; t <= 8; ++t) par->add(250, 30, t, C::kCompile | C::kBlocks);
        CHECK(!par->shader_stutter(200, 300));
        par->add(260, 2, 9, C::kPresents);
        CHECK(par->shader_stutter(200, 300));
        ov::Slow slow;
        ov::Graph g;
        g.frame(0, 200), g.frame(200, 300);
        slow.frame(0, 200), slow.frame(200, 300), slow.frame(300, 340);
        CHECK(slow.n == 2);
        slow.check(*c, 310, -1, g);
        CHECK(slow.n == 2 && !slow.flashing(310));  // play's start not known yet
        slow.check(*hits, 310, 250, g);
        CHECK(slow.n == 1 && !slow.flashing(310));  // the one in startup gone; the other waits for a create still running
        slow.check(*c, 380, 250, g);
        CHECK(slow.n == 1);  // checked at most ten times a second
        slow.check(*c, 420, 250, g);
        CHECK(slow.n == 0 && slow.found == 1 && slow.flashing(420) && !slow.flashing(420 + ov::Slow::kFlashMs) && g.shader[2]);
        slow.frame(200, 300);
        slow.check(*hits, 300 + ov::Slow::kWaitMs + 1, 100, g);
        CHECK(slow.n == 0 && slow.found == 1);
        slow.frame(200, 300);
        slow.check(*c, 300 + ov::Slow::kFlashMs + 300, 100, g);  // judged late (play's start known late): marked, no flash
        CHECK(slow.found == 2 && !slow.flashing(300 + ov::Slow::kFlashMs + 300));
        ov::Slow full;
        for (int i = 0; i <= ov::Slow::kMax; ++i) full.frame(i * 100, i * 100 + 60);
        CHECK(full.n == ov::Slow::kMax && full.f[0].s == 100);  // full: the oldest goes
        const char dxil[] = "DXBC0123456789abcdef\1\0\0\0\x38\0\0\0\1\0\0\0\x24\0\0\0SFI0\x08\0\0\0\0\0\x10\0\0\0\0\0";
        CHECK(ov::inline_rt(dxil, sizeof dxil - 1));
        std::string plain(dxil, sizeof dxil - 1);
        plain[0x24 + 10] = 0;
        CHECK(!ov::inline_rt(plain.data(), plain.size()) && !ov::inline_rt("DXBC", 4));
    }
    CHECK(ov::fg_queue(L"AMD FSR PresentQueue") && ov::fg_queue(L"XefgInterpolationSwapChain::present_queue_"));
    CHECK(!ov::fg_queue(L"Game Queue") && !ov::fg_queue(L""));
    auto frames = [](ov::Clock& c, double from, double to) {
        for (double t = from; t <= to; t += 100) c.frame(t);
    };
    {  // play is every frame from the first present
        auto c = std::make_unique<ov::Clock>();
        CHECK(c->frame(0) && c->startup == 0 && c->played == 0);
        CHECK(!c->frame(100) && c->played == 100);
        frames(*c, 200, 20000);
        CHECK(c->startup == 0 && c->played == 20000);
        // shown: what FrameLog counts if the launch ended now, without the frames starting in its last 10 s
        CHECK(c->shown(20000) == 10000 && c->shown(20500) == 10000 && c->shown(21000) == 11000);
    }
    {  // a first frame of 8 s (the game's load after its first present) and a pause of 6 s are play too
        auto c = std::make_unique<ov::Clock>();
        CHECK(c->frame(1000) && c->startup == 1000);
        CHECK(!c->frame(9000) && c->played == 8000);
        frames(*c, 9100, 20000);
        c->frame(26000);
        CHECK(c->played == 25000);
    }
    {  // a 40 s frame: placed by its start, it is out of the quit window
        auto c = std::make_unique<ov::Clock>();
        c->frame(0);
        CHECK(!c->frame(40000) && c->played == 40000 && c->shown(40000) == 40000);
    }
    printf("overlayunit ok\n");
    return 0;
}

// `selftest overlay <draw|draw10|draw11|draw11_10|hdr|fp16|fg>`: the overlay through the proxy on WARP, with
// scskiller.ini's overlay lines (the test writes them). A hook taken before the proxy's reads the back buffer back after the
// overlay drew, before dxgi presents it. draw: a compute PSO, then a 1920x1080 R8G8B8A8 swap
// chain presents until the log says the play time is done; prints "first <n>" (the first frame against the countdown at
// full time), "done <n>" (against the done text, a full bar), then "resize 0x<hr>" and "small <n>" at 1280x720, "big <n>"
// at 3840x2160: n is the panel's pixels that don't match overlay.h's layout (-1 nothing read). draw11: the same through the
// proxy as d3d11.dll's D3D11CreateDeviceAndSwapChain. draw10, draw11_10: the same on R10G10B10A2. hdr: R10G10B10A2 set to
// HDR10 (prints "setcs 0x<hr>"); fp16: R16G16B16A16_FLOAT; fg: on FSR 3's present queue; these present a few times.
static void* g_spy_orig;
static bool g_spy_want;
static ID3D12Device* g_spy_dev;
static ID3D12CommandQueue* g_spy_q;
static ID3D11Device* g_spy_dev11;
static std::vector<uint8_t> g_spy_px;  // the back buffer read last, g_spy_bpp bytes a pixel
static UINT g_spy_bpp;
static UINT g_spy_w, g_spy_h;
static void spy_read(IDXGISwapChain* sc) {
    g_spy_px.clear();
    IDXGISwapChain3* s3 = nullptr;
    ID3D12Resource *buf = nullptr, *rb = nullptr;
    ID3D12CommandAllocator* a = nullptr;
    ID3D12GraphicsCommandList* l = nullptr;
    ID3D12Fence* f = nullptr;
    if (SUCCEEDED(sc->QueryInterface(IID_PPV_ARGS(&s3))) && SUCCEEDED(s3->GetBuffer(s3->GetCurrentBackBufferIndex(), IID_PPV_ARGS(&buf)))) {
        D3D12_RESOURCE_DESC d = buf->GetDesc();
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp;
        UINT64 total = 0;
        g_spy_dev->GetCopyableFootprints(&d, 0, 1, 0, &fp, nullptr, nullptr, &total);
        D3D12_HEAP_PROPERTIES hp = {D3D12_HEAP_TYPE_READBACK};
        D3D12_RESOURCE_DESC bd = {D3D12_RESOURCE_DIMENSION_BUFFER, 0, total, 1, 1, 1, DXGI_FORMAT_UNKNOWN, {1, 0}, D3D12_TEXTURE_LAYOUT_ROW_MAJOR};
        if (SUCCEEDED(g_spy_dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &bd, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&rb))) &&
            SUCCEEDED(g_spy_dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&a))) &&
            SUCCEEDED(g_spy_dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, a, nullptr, IID_PPV_ARGS(&l))) &&
            SUCCEEDED(g_spy_dev->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&f)))) {
            D3D12_RESOURCE_BARRIER b = {};
            b.Transition = {buf, 0, D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_SOURCE};
            l->ResourceBarrier(1, &b);
            D3D12_TEXTURE_COPY_LOCATION dst = {rb, D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT}, src = {buf, D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX};
            dst.PlacedFootprint = fp, src.SubresourceIndex = 0;
            l->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
            std::swap(b.Transition.StateBefore, b.Transition.StateAfter);
            l->ResourceBarrier(1, &b);
            ID3D12CommandList* cl = l;
            void* p = nullptr;
            HANDLE e = CreateEventW(nullptr, FALSE, FALSE, nullptr);
            if (SUCCEEDED(l->Close()) && (g_spy_q->ExecuteCommandLists(1, &cl), SUCCEEDED(g_spy_q->Signal(f, 1))) && SUCCEEDED(f->SetEventOnCompletion(1, e)) &&
                WaitForSingleObject(e, 10000) == WAIT_OBJECT_0 && SUCCEEDED(rb->Map(0, nullptr, &p))) {
                g_spy_w = (UINT)d.Width, g_spy_h = d.Height;
                g_spy_bpp = d.Format == DXGI_FORMAT_R16G16B16A16_FLOAT ? 8 : 4;
                g_spy_px.resize((size_t)g_spy_w * g_spy_h * g_spy_bpp);
                for (UINT y = 0; y < g_spy_h; ++y)
                    memcpy(&g_spy_px[(size_t)y * g_spy_w * g_spy_bpp], (char*)p + fp.Offset + (size_t)y * fp.Footprint.RowPitch, g_spy_w * g_spy_bpp);
                rb->Unmap(0, nullptr);
            }
            CloseHandle(e);
        }
    }
    for (IUnknown* u : {(IUnknown*)f, (IUnknown*)l, (IUnknown*)a, (IUnknown*)rb, (IUnknown*)buf, (IUnknown*)s3})
        if (u) u->Release();
}
static void spy_read11(IDXGISwapChain* sc) {
    g_spy_px.clear();
    ID3D11Texture2D *buf = nullptr, *st = nullptr;
    ID3D11DeviceContext* c = nullptr;
    D3D11_MAPPED_SUBRESOURCE m;
    g_spy_dev11->GetImmediateContext(&c);
    if (SUCCEEDED(sc->GetBuffer(0, IID_PPV_ARGS(&buf)))) {
        D3D11_TEXTURE2D_DESC d;
        buf->GetDesc(&d);
        d.Usage = D3D11_USAGE_STAGING, d.BindFlags = 0, d.CPUAccessFlags = D3D11_CPU_ACCESS_READ, d.MiscFlags = 0;
        if (SUCCEEDED(g_spy_dev11->CreateTexture2D(&d, nullptr, &st)) && (c->CopyResource(st, buf), SUCCEEDED(c->Map(st, 0, D3D11_MAP_READ, 0, &m)))) {
            g_spy_w = d.Width, g_spy_h = d.Height;
            g_spy_bpp = d.Format == DXGI_FORMAT_R16G16B16A16_FLOAT ? 8 : 4;
            g_spy_px.resize((size_t)g_spy_w * g_spy_h * g_spy_bpp);
            for (UINT y = 0; y < g_spy_h; ++y) memcpy(&g_spy_px[(size_t)y * g_spy_w * g_spy_bpp], (char*)m.pData + (size_t)y * m.RowPitch, g_spy_w * g_spy_bpp);
            c->Unmap(st, 0);
        }
    }
    for (IUnknown* u : {(IUnknown*)st, (IUnknown*)buf, (IUnknown*)c})
        if (u) u->Release();
}
static HRESULT STDMETHODCALLTYPE spy_present(IDXGISwapChain* sc, UINT sync, UINT flags) {
    if (g_spy_want) g_spy_want = false, g_spy_dev11 ? spy_read11(sc) : spy_read(sc);
    return ((HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT))g_spy_orig)(sc, sync, flags);
}
static void spy_on(void* sc) {
    void** vt = *(void***)sc;
    DWORD old;
    VirtualProtect(&vt[8], sizeof(void*), PAGE_READWRITE, &old);
    g_spy_orig = vt[8], vt[8] = (void*)spy_present;
    VirtualProtect(&vt[8], sizeof(void*), old, &old);
}
// With SCSKILLER_OVERLAY_DUMP=<folder>: the top right w x h of the back buffer read last, as <folder>\<name>.ppm, as an
// SDR screen would show it (HDR10 and scRGB brought back to SDR white).
static void spy_dump(const char* name, ov::Out o, long w, long h) {
    wchar_t dir[MAX_PATH];
    if (g_spy_px.empty() || !GetEnvironmentVariableW(L"SCSKILLER_OVERLAY_DUMP", dir, MAX_PATH)) return;
    w = std::min<long>(w, g_spy_w), h = std::min<long>(h, g_spy_h);
    FILE* f = _wfopen((std::wstring(dir) + L"\\" + std::wstring(name, name + strlen(name)) + L".ppm").c_str(), L"wb");
    if (!f) return;
    auto srgb = [](double v) { v = std::clamp(v, 0.0, 1.0); return (int)std::lround(255 * (v <= 0.0031308 ? v * 12.92 : 1.055 * std::pow(v, 1 / 2.4) - 0.055)); };
    auto unpq = [](uint32_t c) {  // a 10-bit PQ code as linear light, SDR white 1 (BT.2020 primaries left as they are)
        double e = std::pow(c / 1023.0, 1 / 78.84375), y = std::pow(std::max(e - 0.8359375, 0.0) / (18.8515625 - 18.6875 * e), 1 / 0.1593017578125);
        return y * 10000 / ov::kWhiteNits;
    };
    fprintf(f, "P6\n%ld %ld\n255\n", w, h);
    for (long y = 0; y < h; ++y)
        for (long x = g_spy_w - w; x < (long)g_spy_w; ++x) {
            const uint8_t* p = &g_spy_px[((size_t)y * g_spy_w + x) * g_spy_bpp];
            uint32_t v = *(const uint32_t*)p;
            int rgb[3];
            for (int c = 0; c < 3; ++c)
                rgb[c] = o == ov::kBgra8 ? p[2 - c] : o == ov::kRgb10 ? (v >> (10 * c + 2) & 255) : o == ov::kPq10 ? srgb(unpq(v >> (10 * c) & 1023))
                         : o == ov::kScRgb ? srgb(DirectX::PackedVector::XMConvertHalfToFloat(((const uint16_t*)p)[c]) * 80 / ov::kWhiteNits) : p[c];
            fputc(rgb[0], f), fputc(rgb[1], f), fputc(rgb[2], f);
        }
    fclose(f);
}
// The panel's pixels the back buffer read last doesn't have as overlay.h paints them with no frames in the graph and no
// flash (or with it), converted to o: the text, bar and flash rows exactly; the graph's pixels any of its colours.
static long spy_mismatch(const char* text, const char* widest, double frac, ov::Out o, bool flash = false) {
    if (g_spy_px.empty()) return -1;
    ov::Layout L;
    ov::layout(g_spy_w, g_spy_h, widest, o != ov::kNone && g_spy_dev11 == nullptr, L);
    std::vector<uint32_t> img(L.w * L.h);
    ov::paint(L, text, frac, ov::Graph{}, flash, img.data(), L.w);
    long bpp = ov::bytes(o), aw = L.at.right - L.at.left, ah = L.at.bottom - L.at.top;
    std::vector<uint8_t> want(aw * ah * bpp);
    ov::convert(img.data() + L.sy * L.w + L.sx, L.w, aw, ah, o, want.data(), aw * bpp);
    std::vector<std::vector<uint8_t>> graph;  // the graph's colours in o
    for (uint32_t c : {ov::kGraphBg, ov::kFill, ov::kSlow, ov::kShader, ov::kLine}) {
        graph.emplace_back(bpp);
        ov::convert(&c, 1, 1, 1, o, graph.back().data(), bpp);
    }
    long bad = 0;
    for (long y = 0; y < ah; ++y)
        for (long x = 0; x < aw; ++x) {
            const uint8_t* got = &g_spy_px[((size_t)(y + L.at.top) * g_spy_w + x + L.at.left) * bpp];
            long py = y + L.sy, px = x + L.sx;
            bool in_graph = py >= L.graph_y && py < L.graph_y + L.graph_h && px >= L.pad && px < L.w - L.pad;
            if (!in_graph) bad += memcmp(got, &want[(y * aw + x) * bpp], bpp) != 0;
            else bad += std::none_of(graph.begin(), graph.end(), [&](auto& c) { return !memcmp(got, c.data(), bpp); });
        }
    return bad;
}
// The flash row of the back buffer read last that doesn't have kStutter as overlay.h paints it (8-bit RGBA).
static long spy_flash(ov::Out o, const char* widest) {
    ov::Layout L;
    ov::layout(g_spy_w, g_spy_h, widest, true, L);
    std::vector<uint32_t> img(L.w * L.h);
    ov::paint(L, "", 0, ov::Graph{}, true, img.data(), L.w);
    long bad = 0, h = ov::font(L.s).height;
    for (long y = L.flash_y; y < L.flash_y + h; ++y)
        for (long x = 0; x < L.w; ++x) {
            uint32_t want;
            ov::convert(&img[y * L.w + x], 1, 1, 1, o, (uint8_t*)&want, 4);
            bad += *(const uint32_t*)&g_spy_px[((size_t)(y + L.at.top) * g_spy_w + x + L.at.left) * 4] != want;
        }
    return bad;
}
static int overlay_rows(const std::wstring& dir, const std::wstring& mode) {
    SetEnvironmentVariableW(L"SCSKILLER_MODE", L"record");
    const std::wstring ini = dir + L"scskiller.ini";
    int play_s = GetPrivateProfileIntW(L"scskiller", L"ab_play_s", 0, ini.c_str());
    wchar_t tag[16], sys[MAX_PATH];
    GetPrivateProfileStringW(L"scskiller", L"ab_tag", L"", tag, 16, ini.c_str());
    GetSystemDirectoryW(sys, MAX_PATH);
    auto wnd = [] { return CreateWindowExW(0, L"STATIC", L"scskiller overlay", WS_OVERLAPPEDWINDOW, 0, 0, 64, 64, nullptr, nullptr, nullptr, nullptr); };
    IDXGISwapChain* sc = nullptr;
    const DXGI_FORMAT fmt = mode == L"draw10" || mode == L"draw11_10" || mode == L"hdr" ? DXGI_FORMAT_R10G10B10A2_UNORM
                            : mode == L"fp16" ? DXGI_FORMAT_R16G16B16A16_FLOAT : DXGI_FORMAT_R8G8B8A8_UNORM;
    const ov::Out out = mode == L"hdr" ? ov::kPq10 : mode == L"fp16" ? ov::kScRgb : fmt == DXGI_FORMAT_R10G10B10A2_UNORM ? ov::kRgb10 : ov::kRgba8;
    ID3D12Device* dev = nullptr;
    ID3D12RootSignature* rs = nullptr;
    if (mode.starts_with(L"draw11")) {
        HMODULE m = LoadLibraryW((dir + L"d3d11.dll").c_str());
        HMODULE real = GetModuleHandleW((std::wstring(sys) + L"\\d3d11.dll").c_str());
        CHECK(m && real);
        using Create = decltype(&D3D11CreateDeviceAndSwapChain);
        auto chain = [&](Create create, IDXGISwapChain** sc, ID3D11Device** dev, DXGI_FORMAT f) {
            DXGI_SWAP_CHAIN_DESC d = {{1920, 1080, {}, f}, {1, 0}, DXGI_USAGE_RENDER_TARGET_OUTPUT, 2, wnd(), TRUE, DXGI_SWAP_EFFECT_FLIP_DISCARD};
            D3D_FEATURE_LEVEL fl = D3D_FEATURE_LEVEL_11_0;
            return create && d.OutputWindow && SUCCEEDED(create(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, &fl, 1, D3D11_SDK_VERSION, &d, sc, dev, nullptr, nullptr));
        };
        IDXGISwapChain* sc0 = nullptr;
        ID3D11Device* dev0 = nullptr;
        CHECK(chain((Create)GetProcAddress(real, "D3D11CreateDeviceAndSwapChain"), &sc0, &dev0, DXGI_FORMAT_R8G8B8A8_UNORM));
        spy_on(sc0);
        sc0->Release();
        CHECK(chain((Create)GetProcAddress(m, "D3D11CreateDeviceAndSwapChain"), &sc, &g_spy_dev11, fmt));
    } else {
        HMODULE m = LoadLibraryW((dir + L"d3d12.dll").c_str());
        HMODULE real = GetModuleHandleW((std::wstring(sys) + L"\\d3d12.dll").c_str());
        CHECK(m && real);
        auto proxy_create = (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice");
        auto real_create = (decltype(&D3D12CreateDevice))GetProcAddress(real, "D3D12CreateDevice");
        auto ser = (decltype(&D3D12SerializeRootSignature))GetProcAddress(m, "D3D12SerializeRootSignature");
        IDXGIFactory4* f = nullptr;
        IDXGIAdapter* warp = nullptr;
        CHECK(proxy_create && real_create && ser && SUCCEEDED(CreateDXGIFactory2(0, IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumWarpAdapter(IID_PPV_ARGS(&warp))));
        auto chain = [&](ID3D12Device* dev, DXGI_FORMAT fmt, const wchar_t* name, ID3D12CommandQueue** q, IDXGISwapChain1** sc) {
            HWND w = wnd();
            D3D12_COMMAND_QUEUE_DESC qd = {};
            DXGI_SWAP_CHAIN_DESC1 d = {1920, 1080, fmt, FALSE, {1, 0}, DXGI_USAGE_RENDER_TARGET_OUTPUT, 2};
            d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
            return w && SUCCEEDED(dev->CreateCommandQueue(&qd, IID_PPV_ARGS(q))) && (!name || SUCCEEDED((*q)->SetName(name))) &&
                   SUCCEEDED(f->CreateSwapChainForHwnd(*q, w, &d, nullptr, nullptr, sc));
        };
        ID3D12Device* dev0 = nullptr;
        ID3D12CommandQueue *q0 = nullptr, *q = nullptr;
        IDXGISwapChain1 *sc0 = nullptr, *sc1 = nullptr;
        // the D3D12 debug layer, when installed, checks the overlay's use of the queue: each error or warning is printed
        auto get_debug = (decltype(&D3D12GetDebugInterface))GetProcAddress(real, "D3D12GetDebugInterface");
        ID3D12Debug* dbg = nullptr;
        if (get_debug && SUCCEEDED(get_debug(IID_PPV_ARGS(&dbg)))) dbg->EnableDebugLayer();
        CHECK(SUCCEEDED(real_create(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev0))) && chain(dev0, DXGI_FORMAT_R8G8B8A8_UNORM, nullptr, &q0, &sc0));
        spy_on(sc0);
        sc0->Release();
        CHECK(SUCCEEDED(proxy_create(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))));
        ID3D12InfoQueue1* iq = nullptr;
        DWORD cookie;
        if (dbg && SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&iq))))
            iq->RegisterMessageCallback([](D3D12_MESSAGE_CATEGORY, D3D12_MESSAGE_SEVERITY sev, D3D12_MESSAGE_ID, LPCSTR text, void*) {
                if (sev <= D3D12_MESSAGE_SEVERITY_WARNING) printf("d3d12 debug: %s\n", text), fflush(stdout);
            }, D3D12_MESSAGE_CALLBACK_FLAG_NONE, nullptr, &cookie);
        D3D12_ROOT_PARAMETER up = {D3D12_ROOT_PARAMETER_TYPE_UAV};
        D3D12_ROOT_SIGNATURE_DESC rd = {1, &up};
        ID3DBlob *rb = nullptr, *err = nullptr;
        CHECK(SUCCEEDED(ser(&rd, D3D_ROOT_SIGNATURE_VERSION_1, &rb, &err)) && SUCCEEDED(dev->CreateRootSignature(0, rb->GetBufferPointer(), rb->GetBufferSize(), IID_PPV_ARGS(&rs))));
        CHECK(SUCCEEDED(compute_pso(dev, rs, (int)GetTickCount())));
        CHECK(chain(dev, fmt, mode == L"fg" ? L"AMD FSR PresentQueue" : nullptr, &q, &sc1));
        g_spy_dev = dev, g_spy_q = q, sc = sc1;
        if (mode == L"hdr") {  // HDR10: a 10-bit swap chain is SDR until it says otherwise
            IDXGISwapChain3* s3 = nullptr;
            CHECK(SUCCEEDED(sc->QueryInterface(IID_PPV_ARGS(&s3))));
            printf("setcs 0x%08lx\n", (unsigned long)s3->SetColorSpace1(DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020));
        }
    }
    const bool after = !wcscmp(tag, L"after");
    const std::string widest = ov::countdown(after, play_s);
    g_spy_want = true;
    sc->Present(0, 0);
    printf("first %ld\n", spy_mismatch(widest.c_str(), widest.c_str(), 0, out));
    spy_dump("first", out, 640, 200);
    bool stuttered = false;
    for (ULONGLONG until = GetTickCount64() + 60000; GetTickCount64() < until;) {
        for (int i = 0; i < 10; ++i) sc->Present(0, 0), Sleep(15);
        std::string log = read_all(dir + L"scskiller.log");
        if (mode == L"stutter" && !stuttered && log.find("play counted from") != std::string::npos) {
            // a frame of 50 ms or more that a compile on the presenting thread blocks: the flash and the red columns, read
            // while the flash shows; then presents until it is gone, so "done" is read without it
            stuttered = true;
            Sleep(60);
            SetEnvironmentVariableW(L"SCSKILLER_TEST_CREATE_MS", L"40");
            CHECK(SUCCEEDED(compute_pso(dev, rs, (int)GetTickCount() + 1)));
            SetEnvironmentVariableW(L"SCSKILLER_TEST_CREATE_MS", nullptr);
            for (int i = 0; i < 20; ++i) sc->Present(0, 0), Sleep(15);
            g_spy_want = true;
            sc->Present(0, 0);
            long red = 0;
            for (size_t i = 0; i + 4 <= g_spy_px.size(); i += 4) red += *(const uint32_t*)&g_spy_px[i] == ov::kShader;
            printf("flash %ld red %ld\n", spy_flash(out, widest.c_str()), red);
            spy_dump("stutter", out, 640, 200);
            for (ULONGLONG gone = GetTickCount64() + 2500; GetTickCount64() < gone;) sc->Present(0, 0), Sleep(15);
        }
        if (log.find("s of play counted, done") != std::string::npos || log.find("not drawn: frame generation") != std::string::npos) break;
    }
    g_spy_want = true;
    sc->Present(0, 0);
    printf("done %ld\n", spy_mismatch(ov::kDone, widest.c_str(), 1, out));
    spy_dump("done", out, 640, 200);
    // as a D3D12 game must before ResizeBuffers: its queue (the presents, the overlay's copies) done
    auto idle = [] {
        ID3D12Fence* f = nullptr;
        HANDLE e = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (g_spy_q && SUCCEEDED(g_spy_dev->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&f))) && SUCCEEDED(g_spy_q->Signal(f, 1)) && SUCCEEDED(f->SetEventOnCompletion(1, e)))
            WaitForSingleObject(e, 10000);
        if (f) f->Release();
        CloseHandle(e);
    };
    idle();
    HRESULT hr = sc->ResizeBuffers(0, 1280, 720, DXGI_FORMAT_UNKNOWN, 0);  // fails while anything holds a back buffer
    printf("resize 0x%08lx\n", (unsigned long)hr);
    g_spy_want = true;
    sc->Present(0, 0);
    printf("small %ld\n", spy_mismatch(ov::kDone, widest.c_str(), 1, out));
    idle();
    CHECK(SUCCEEDED(sc->ResizeBuffers(0, 3840, 2160, DXGI_FORMAT_UNKNOWN, 0)));
    g_spy_want = true;
    sc->Present(0, 0);
    printf("big %ld\n", spy_mismatch(ov::kDone, widest.c_str(), 1, out));
    spy_dump("big", out, 1280, 400);
    Sleep(1500);  // the frames writer's second: the csv's #overlay line
    return 0;
}

// `selftest overlaybench <seconds>` (GPU): the hook time per present through the proxy on the first hardware adapter, as
// a game sees it: a 1920x1080 R8G8B8A8 swap chain on a hidden window, about 250 presents a second, the overlay on or off
// as the test's scskiller.ini says. Prints the proxy's own "frames: ... hook" log line, written after a minute.
static int overlay_bench(const std::wstring& dir, int seconds) {
    if (gpu_lock("selftest overlaybench").empty()) return 1;
    SetEnvironmentVariableW(L"SCSKILLER_MODE", L"record");
    HMODULE m = LoadLibraryW((dir + L"d3d12.dll").c_str());
    auto create = m ? (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice") : nullptr;
    IDXGIFactory4* f = nullptr;
    IDXGIAdapter1* a = nullptr;
    ID3D12Device* dev = nullptr;
    ID3D12CommandQueue* q = nullptr;
    IDXGISwapChain1* sc = nullptr;
    D3D12_COMMAND_QUEUE_DESC qd = {};
    DXGI_SWAP_CHAIN_DESC1 d = {1920, 1080, DXGI_FORMAT_R8G8B8A8_UNORM, FALSE, {1, 0}, DXGI_USAGE_RENDER_TARGET_OUTPUT, 3};
    d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    HWND w = CreateWindowExW(0, L"STATIC", L"scskiller overlay bench", WS_OVERLAPPEDWINDOW, 0, 0, 64, 64, nullptr, nullptr, nullptr, nullptr);
    CHECK(create && w && SUCCEEDED(CreateDXGIFactory2(0, IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumAdapters1(0, &a)));
    CHECK(SUCCEEDED(create(a, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))) && SUCCEEDED(dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&q))));
    CHECK(SUCCEEDED(f->CreateSwapChainForHwnd(q, w, &d, nullptr, nullptr, &sc)));
    // the game's frame: each back buffer cleared on the same queue before its present
    IDXGISwapChain3* sc3 = nullptr;
    ID3D12DescriptorHeap* heap = nullptr;
    D3D12_DESCRIPTOR_HEAP_DESC hd = {D3D12_DESCRIPTOR_HEAP_TYPE_RTV, 3};
    CHECK(SUCCEEDED(sc->QueryInterface(IID_PPV_ARGS(&sc3))) && SUCCEEDED(dev->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&heap))));
    ID3D12GraphicsCommandList* frame[3] = {};
    for (UINT i = 0; i < 3; ++i) {
        ID3D12Resource* buf = nullptr;
        ID3D12CommandAllocator* al = nullptr;
        D3D12_CPU_DESCRIPTOR_HANDLE rtv = heap->GetCPUDescriptorHandleForHeapStart();
        rtv.ptr += i * dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        CHECK(SUCCEEDED(sc3->GetBuffer(i, IID_PPV_ARGS(&buf))) && SUCCEEDED(dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&al))) &&
              SUCCEEDED(dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, al, nullptr, IID_PPV_ARGS(&frame[i]))));
        dev->CreateRenderTargetView(buf, nullptr, rtv);
        D3D12_RESOURCE_BARRIER b = {};
        b.Transition = {buf, 0, D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_RENDER_TARGET};
        frame[i]->ResourceBarrier(1, &b);
        const float grey[4] = {0.3f, 0.3f, 0.3f, 1};
        frame[i]->ClearRenderTargetView(rtv, grey, 0, nullptr);
        std::swap(b.Transition.StateBefore, b.Transition.StateAfter);
        frame[i]->ResourceBarrier(1, &b);
        CHECK(SUCCEEDED(frame[i]->Close()));
        buf->Release();
    }
    HANDLE t = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    CHECK(t);
    for (ULONGLONG until = GetTickCount64() + seconds * 1000ull; GetTickCount64() < until;) {
        ID3D12CommandList* l = frame[sc3->GetCurrentBackBufferIndex()];
        q->ExecuteCommandLists(1, &l);
        sc->Present(0, 0);
        LARGE_INTEGER due{.QuadPart = -40000};  // 4 ms
        SetWaitableTimer(t, &due, 0, nullptr, nullptr, FALSE);
        WaitForSingleObject(t, INFINITE);
    }
    std::string log = read_all(dir + L"scskiller.log");
    size_t at = log.rfind(" presents, hook ");
    if (at != std::string::npos) at = log.rfind("frames: ", at);
    printf("%s\n", at == std::string::npos ? "no hook line" : log.substr(at, log.find('\n', at) - at).c_str());
    printf("drawn %d\n", log.find("overlay: first frame drawn") != std::string::npos);
    return 0;
}

// `selftest nvopts` (WARP, no GPU driver): NVAPI's pipeline creation flags are global (nvapi.h), so the recorder gives a
// state object the flags last set by any thread, and the warm creates each under its own. fakenext.dll, copied as
// nvapi64.dll next to the exe, stands in for NVAPI and keeps the flags of every set it accepts. p1 (record) creates on
// threads that never set flags, while other threads set them: R1 a collection and R2 a pipeline under none, 0x11 set, A1
// an addition to R2 and C1 a compute PSO, 0 set, R3 a collection, 0x11 set, R4 a collection. Their flags must be 0, 0,
// 0x11, 0 (a PSO's are never recorded), 0, 0x11, each state object's 'N' with scope bit 0x100. p2 (warm, one thread) replays them: the flags it sets, in order, must be
// 0x11 (A1), 0 (R3), 0x11 (R4).
static Hash20 sha1_raw(const std::string& p) {
    Hash20 h{};
    BCryptHash(BCRYPT_SHA1_ALG_HANDLE, nullptr, 0, (PUCHAR)p.data(), (ULONG)p.size(), h.data(), 20);
    return h;
}
static std::string hex(const Hash20& h) {
    char b[41];
    for (int i = 0; i < 20; ++i) snprintf(b + 2 * i, 3, "%02x", h[i]);
    return b;
}

static int nvopts_child(int proc, unsigned seed, const std::wstring& dir, const std::wstring& out) {
    SetEnvironmentVariableW(L"SCSKILLER_MODE", proc == 2 ? L"warm" : L"record");
    SetEnvironmentVariableW(L"SCSKILLER_THREADS", L"1");
    HMODULE m = LoadLibraryW((dir + L"d3d12.dll").c_str());
    CHECK(m);
    auto create_device = (decltype(&D3D12CreateDevice))GetProcAddress(m, "D3D12CreateDevice");
    auto ser = (decltype(&D3D12SerializeRootSignature))GetProcAddress(m, "D3D12SerializeRootSignature");
    IDXGIFactory4* f = nullptr;
    IDXGIAdapter* warp = nullptr;
    CHECK(create_device && ser && SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumWarpAdapter(IID_PPV_ARGS(&warp))));
    ID3D12Device* dev = nullptr;
    CHECK(SUCCEEDED(create_device(warp, D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&dev))));
    HMODULE nvm = GetModuleHandleW(L"nvapi64.dll");  // the proxy loaded it at the device
    auto sets = nvm ? (int(WINAPI*)(uint32_t*, int))GetProcAddress(nvm, "FakeNv_Opts") : nullptr;
    CHECK(sets);
    FILE* o = _wfopen(out.c_str(), L"w");
    CHECK(o);
    if (proc == 2) {
        auto start = (void(WINAPI*)(IUnknown*))GetProcAddress(m, "SCSKiller_StartWarm");
        auto stats = (void(WINAPI*)(uint64_t*))GetProcAddress(m, "SCSKiller_Stats");
        CHECK(start && stats);
        uint64_t st[7];
        start(dev), stats(st);
        CHECK(st[2] == 6 && !st[3]);  // created, failed
        uint32_t v[64];
        for (int i = 0, n = sets(v, 64); i < n; ++i) fprintf(o, "%x\n", v[i]);
        return fclose(o), 0;
    }
    ID3D12Device5* dev5 = nullptr;
    ID3D12Device7* dev7 = nullptr;
    CHECK(SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&dev5))) && SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&dev7))));
    auto qi = (void* (*)(uint32_t))GetProcAddress(nvm, "nvapi_QueryInterface");
    auto opts = qi ? (int (*)(ID3D12Device5*, const uint32_t*))qi(0x5C607A27) : nullptr;
    IDxcCompiler3* comp = dxc_compiler();
    CHECK(opts && comp);
    D3D12_ROOT_PARAMETER cbv = {D3D12_ROOT_PARAMETER_TYPE_CBV};
    cbv.Descriptor = {0, 1};
    D3D12_ROOT_SIGNATURE_DESC rd = {1, &cbv, 0, nullptr, D3D12_ROOT_SIGNATURE_FLAG_NONE};
    ID3DBlob *rb = nullptr, *err = nullptr;
    ID3D12RootSignature* grs = nullptr;
    CHECK(SUCCEEDED(ser(&rd, D3D_ROOT_SIGNATURE_VERSION_1, &rb, &err)) && SUCCEEDED(dev->CreateRootSignature(0, rb->GetBufferPointer(), rb->GetBufferSize(), IID_PPV_ARGS(&grs))));

    auto flags = [&](uint32_t v) {
        int st = -1;
        std::thread([&] { uint32_t p[2] = {8 | 1 << 16, v}; st = opts(dev5, p); }).join();
        return st == 0;
    };
    ID3D12StateObject* r2 = nullptr;
    auto so = [&](const char* name, unsigned k, D3D12_STATE_OBJECT_TYPE type, uint32_t want, ID3D12StateObject* to) {
        SoOne s(comp, k, grs, type);
        HRESULT hr = E_FAIL;
        ID3D12StateObject* obj = nullptr;
        if (!s.lib.empty()) std::thread([&] { hr = to ? dev7->AddToStateObject(&s.d, to, IID_PPV_ARGS(&obj)) : dev5->CreateStateObject(&s.d, IID_PPV_ARGS(&obj)); }).join();
        if (FAILED(hr)) return printf("  %s: hr=0x%08x\n", name, (unsigned)hr), false;
        if (!to && type != D3D12_STATE_OBJECT_TYPE_COLLECTION) r2 = obj;
        else obj->Release();
        fprintf(o, "%s %s %x\n", name, hex(sha1_raw(s.lib)).c_str(), want);
        return true;
    };
    CHECK(so("R1", seed, D3D12_STATE_OBJECT_TYPE_COLLECTION, 0, nullptr));
    CHECK(so("R2", seed + 1, D3D12_STATE_OBJECT_TYPE_RAYTRACING_PIPELINE, 0, nullptr));
    CHECK(flags(0x11));
    CHECK(so("A1", seed + 2, D3D12_STATE_OBJECT_TYPE_RAYTRACING_PIPELINE, 0x11, r2));
    ID3DBlob* cs = compile("[numthreads(1, 1, 1)] void main() {}", "cs_5_0");
    CHECK(cs);
    D3D12_COMPUTE_PIPELINE_STATE_DESC cd = {grs, {cs->GetBufferPointer(), cs->GetBufferSize()}};
    HRESULT hr = E_FAIL;
    ID3D12PipelineState* pso = nullptr;
    std::thread([&] { hr = dev->CreateComputePipelineState(&cd, IID_PPV_ARGS(&pso)); }).join();
    CHECK(SUCCEEDED(hr));
    pso->Release();
    fprintf(o, "C1 %s 0\n", hex(sha1_raw(std::string((const char*)cs->GetBufferPointer(), cs->GetBufferSize()))).c_str());
    CHECK(flags(0));
    CHECK(so("R3", seed + 3, D3D12_STATE_OBJECT_TYPE_COLLECTION, 0, nullptr));
    CHECK(flags(0x11));
    CHECK(so("R4", seed + 4, D3D12_STATE_OBJECT_TYPE_COLLECTION, 0x11, nullptr));
    r2->Release();
    return fclose(o), 0;
}

static int nvopts_parent(const std::wstring& a, const std::wstring& bin, const std::wstring& d) {
    CHECK(CopyFileW(a.c_str(), (d + L"selftest.exe").c_str(), TRUE) && CopyFileW((bin + L"d3d12.dll").c_str(), (d + L"d3d12.dll").c_str(), TRUE) &&
          CopyFileW((bin + L"fakenext.dll").c_str(), (d + L"nvapi64.dll").c_str(), TRUE));
    std::wstring seed = std::to_wstring(GetTickCount() % 1000000), out = d + L"nvopts.txt";
    CHECK(!run(d + L"selftest.exe", L"nvoptschild 1 " + seed + L" \"" + out + L"\""));
    struct Row { std::string name, hash; uint32_t want; };
    std::vector<Row> rows;
    FILE* f = _wfopen(out.c_str(), L"r");
    CHECK(f);
    char name[16], h[48];
    for (uint32_t w; fscanf(f, "%15s %47s %x", name, h, &w) == 3;) rows.push_back({name, h, w});
    fclose(f);
    CHECK(rows.size() == 6);
    std::string db = read_all(d + L"scskiller.db");
    std::map<std::string, std::string> key_of;  // the hash a row names -> the key of the record naming it
    std::map<std::string, std::pair<uint32_t, uint32_t>> nv;  // record key -> its last 'N''s scope and flags
    for (size_t i = 0; i + 5 <= db.size();) {
        uint32_t n;
        memcpy(&n, &db[i + 1], 4);
        if (i + 5 + n > db.size()) break;
        char tag = db[i];
        std::string p = db.substr(i + 5, n);
        if (tag == 'N' && n == 36) nv[hex(*(const Hash20*)p.data())] = {*(const uint32_t*)(p.data() + 28), *(const uint32_t*)(p.data() + 32)};
        else if (tag == 'R' || tag == 'A' || tag == 'C')
            for (auto& r : rows) {
                std::string raw(20, '\0');
                for (int b = 0; b < 20; ++b) raw[b] = (char)std::stoi(r.hash.substr(2 * b, 2), nullptr, 16);
                if (p.find(raw) != std::string::npos) key_of[r.hash] = hex(sha1_of(tag, p));
            }
        i += 5 + n;
    }
    int bad = 0;
    for (auto& r : rows) {
        auto k = key_of.find(r.hash);
        auto [scope, got] = k == key_of.end() ? std::pair{0u, ~0u} : nv.count(k->second) ? nv[k->second] : std::pair{0u, 0u};
        bool global = (scope & 0x100) != 0, so = r.name[0] != 'C';  // a state object's 'N' says its flags are the process's
        if (got == ~0u) printf("  %s: not recorded\n", r.name.c_str());
        else printf("  %s: recorded flags 0x%x%s, want 0x%x\n", r.name.c_str(), got, global ? " (global)" : "", r.want);
        bad += got != r.want || global != so;
    }
    CHECK(!run(d + L"selftest.exe", L"nvoptschild 2 " + seed + L" \"" + out + L"\""));
    std::string warm;
    for (char c : read_all(out))
        if (c != '\r') warm += c == '\n' ? ' ' : c;
    printf("  warm: flags set %s(want 11 0 11)\n", warm.c_str());
    bad += warm != "11 0 11 ";
    printf(bad ? "nvopts FAILED\n" : "nvopts OK\n");
    return bad ? 1 : 0;
}

int wmain(int argc, wchar_t** argv) {
    std::wstring a(32768, L'\0');  // a run from a path past MAX_PATH (\\?\) loads the proxy beside it
    a.resize(GetModuleFileNameW(nullptr, a.data(), (DWORD)a.size()));
    std::wstring dir = a.substr(0, a.find_last_of(L'\\') + 1);
    if (argc > 1 && !wcscmp(argv[1], L"sysdxgi")) return sys_dxgi_rows();  // no proxy: never stamped
    if (argc > 1 && !wcscmp(argv[1], L"load")) return LoadLibraryW((dir + L"d3d12.dll").c_str()) ? 0 : 1;  // the proxy beside it, no device: never stamped
    if (argc > 1 && !wcscmp(argv[1], L"admission")) return admission_rules();  // no proxy: values only
    if (argc > 1 && !wcscmp(argv[1], L"warmmemory")) return warm_memory_rules();  // no proxy: values only
    // The proxy records only for the exe the app checked (its stamp in scskiller.ini); here the selftest is the app. None
    // with SCSKILLER_SELFTEST_UNARMED set.
    // run by hand, outside the tests: its recorder key and the proxy's refusals go to a temp folder, never the user's
    if (wchar_t tmp[MAX_PATH]; !GetEnvironmentVariableW(L"SCSKILLER_TEST_RECORDER_DIR", nullptr, 0) && GetTempPathW(MAX_PATH, tmp))
        SetEnvironmentVariableW(L"SCSKILLER_TEST_RECORDER_DIR", (std::wstring(tmp) + L"scskiller-selftest-recorder").c_str());
    if (!GetEnvironmentVariableW(L"SCSKILLER_SELFTEST_UNARMED", nullptr, 0)) arm_self(dir, a);
    if (argc > 2 && !wcscmp(argv[1], L"frameswrap")) return frames_wrap_rows(dir, _wtoi(argv[2]), argc > 3 ? argv[3] : L"");
    if (argc > 1 && !wcscmp(argv[1], L"layoutrules")) return layout_rules();
    if (argc > 1 && !wcscmp(argv[1], L"overlayunit")) return overlay_unit();
    if (argc > 2 && !wcscmp(argv[1], L"overlay")) return overlay_rows(dir, argv[2]);
    if (argc > 2 && !wcscmp(argv[1], L"overlaybench")) return overlay_bench(dir, _wtoi(argv[2]));
    if (argc > 2 && !wcscmp(argv[1], L"so")) return so_rows(dir, (unsigned)_wtoi(argv[2]));
    if (argc > 4 && !wcscmp(argv[1], L"burst"))
        return burst_rows(dir, _wtol(argv[2]), std::max(1, _wtoi(argv[3])), !wcscmp(argv[4], L"rec"), argc > 5 ? std::max(1, _wtoi(argv[5])) : 256,
                          argc > 6 && !wcscmp(argv[6], L"compute"));
    if (argc > 2 && !wcscmp(argv[1], L"frames")) return frames_rows(dir, _wtoi(argv[2]));
    if (argc > 1 && !wcscmp(argv[1], L"unload")) return unload_rows(dir);
    if (argc > 1 && !wcscmp(argv[1], L"framesheld")) return frames_held(dir);
    if (argc > 2 && !wcscmp(argv[1], L"framesmod")) return frames_mod_rows(dir, _wtoi(argv[2]));
    if (argc > 2 && !wcscmp(argv[1], L"framesfg") && !wcscmp(argv[2], L"ffx"))
        return frames_ffx_rows(dir, argc > 3 && !wcscmp(argv[3], L"play"), argc > 3 && !wcscmp(argv[3], L"late"));
    if (argc > 1 && !wcscmp(argv[1], L"framescapture")) return frames_capture_rows(dir);
    if (argc > 1 && !wcscmp(argv[1], L"framesstorm")) return frames_storm_rows(dir);
    if (argc > 1 && !wcscmp(argv[1], L"framesdummy")) return frames_dummy_rows(dir);
    if (argc > 1 && !wcscmp(argv[1], L"nvqi")) return nvqi_rows(dir, argc > 2 && !wcscmp(argv[2], L"exit"));
    if (argc > 2 && !wcscmp(argv[1], L"framesfg")) return frames_fg_rows(dir, wcscmp(argv[2], L"plain") ? argv[2] : nullptr, argc > 3 && !wcscmp(argv[3], L"overlay"));
    if (argc > 1 && !wcscmp(argv[1], L"factory")) return factory_rows(dir, argc > 2 ? argv[2] : nullptr);
    if (argc > 2 && !wcscmp(argv[1], L"anticheat")) return anticheat_rows(dir, argv[2], argc > 3 ? argv[3] + std::wstring(L"\\") : L"");
    if (argc > 1 && !wcscmp(argv[1], L"factoryrejected")) return factory_rejected_rows(dir);
    if (argc > 2 && !wcscmp(argv[1], L"rec11frames")) return rec11_frames(dir, _wtoi(argv[2]));
    if (argc > 2 && !wcscmp(argv[1], L"rec11")) return rec11_rows(dir, argv[2], argc > 3 && !wcscmp(argv[3], L"ctx"));
    if (argc > 2 && !wcscmp(argv[1], L"chain")) return chain_rows(dir, (unsigned)_wtoi(argv[2]), argc > 3 && !wcscmp(argv[3], L"swap"));
    if (argc > 2 && !wcscmp(argv[1], L"layer")) return layer_rows(dir, (unsigned)_wtoi(argv[2]), argc > 3 && !wcscmp(argv[3], L"old"));
    if (argc > 2 && !wcscmp(argv[1], L"layerwarm")) return layerwarm_parent(a, dir, argv[2], argc > 3 ? std::max(1, _wtoi(argv[3])) : 24);
    if (argc > 1 && !wcscmp(argv[1], L"amwarm")) return amwarm_parent(a, dir, argc > 2 ? std::max(1, _wtoi(argv[2])) : 24);
    if (argc > 4 && !wcscmp(argv[1], L"amwarmchild")) return amwarm_child(dir, (unsigned)_wtoi(argv[2]), _wtoi(argv[3]), (uint32_t)_wtoi(argv[4]), argc > 5);
    if (argc > 3 && !wcscmp(argv[1], L"layerwarmchild")) return layerwarm_child(dir, (unsigned)_wtoi(argv[2]), _wtoi(argv[3]));
    if (argc > 1 && !wcscmp(argv[1], L"warpluid")) {  // for scskiller_warm --adapter-luid: WARP (the runtime's checks, no GPU cache)
        IDXGIFactory4* f = nullptr;
        IDXGIAdapter* w = nullptr;
        DXGI_ADAPTER_DESC d;
        CHECK(SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumWarpAdapter(IID_PPV_ARGS(&w))) && SUCCEEDED(w->GetDesc(&d)));
        printf("%llx\n", ((unsigned long long)(uint32_t)d.AdapterLuid.HighPart << 32) | d.AdapterLuid.LowPart);
        return 0;
    }
    if (argc > 4 && !wcscmp(argv[1], L"dxcfill")) return dxcfill(_wtoi64(argv[2]), _wtoi(argv[3]), (unsigned)_wtoi(argv[4]), argc > 5 ? _wtoi(argv[5]) : 0);
    // a probe works in a new folder of its own (RunDir); cacheprobe's stays: it holds the copies to search
    auto in_run = [&](const wchar_t* what, bool keep, auto&& probe) {
        if (std::wstring d = dxc_dir(); !d.empty()) SetEnvironmentVariableW(L"SELFTEST_DXC", d.c_str());
        RunDir r(dir, what, keep);
        return r.path.empty() ? 1 : probe(r.path);
    };
    if (argc > 1 && !wcscmp(argv[1], L"cacheprobe")) return in_run(L"cacheprobe", true, [&](const std::wstring& dir) { return cacheprobe_parent(a, dir); });
    if (argc > 4 && !wcscmp(argv[1], L"cacheprobechild")) return cacheprobe_child(_wtoi(argv[2]), (unsigned)_wtoi(argv[3]), argv[4]);
    if (argc > 1 && !wcscmp(argv[1], L"bindless")) return in_run(L"bindless", false, [&](const std::wstring& dir) { return bindless_parent(a, dir, argc > 2 ? std::max(1, _wtoi(argv[2])) : 1, argc > 3 ? argv[3] : L""); });
    if (argc > 6 && !wcscmp(argv[1], L"bindlesschild")) return bindless_child(_wtoi(argv[2]), (unsigned)_wtoi(argv[3]), (char)argv[4][0], (UINT)_wtoi(argv[5]), argv[6], argc <= 7);
    if (argc > 1 && !wcscmp(argv[1], L"nvext")) return in_run(L"nvext", false, [&](const std::wstring& dir) { return nvext_parent(a, dir, argc > 2 ? std::max(1, _wtoi(argv[2])) : 1); });
    if (argc > 1 && !wcscmp(argv[1], L"nvopts")) return in_run(L"nvopts", false, [&](const std::wstring& d) { return nvopts_parent(a, dir, d); });
    if (argc > 4 && !wcscmp(argv[1], L"nvoptschild")) return nvopts_child(_wtoi(argv[2]), (unsigned)_wtoi(argv[3]), dir, argv[4]);
    if (argc > 4 && !wcscmp(argv[1], L"nvextchild")) return nvext_child(_wtoi(argv[2]), (unsigned)_wtoi(argv[3]), dir, argv[4]);
    if (argc > 1 && !wcscmp(argv[1], L"dxr")) return in_run(L"dxr", false, [&](const std::wstring& dir) { return dxr_parent(a, dir, argc > 2 ? std::max(1, _wtoi(argv[2])) : 1); });
    if (argc > 3 && !wcscmp(argv[1], L"dxrblobs")) {
        IDxcCompiler3* comp = dxc_compiler();
        return comp && dxr_blobs(comp, dxr_plan((unsigned)_wtoi(argv[2])), argv[3]) >= 0 ? 0 : 1;
    }
    if (argc > 5 && !wcscmp(argv[1], L"dxrchild")) return dxr_child(_wtoi(argv[2]), (unsigned)_wtoi(argv[3]), argv[4], argv[5]);
    if (argc > 1 && !wcscmp(argv[1], L"vulkan")) return in_run(L"vulkan", false, [&](const std::wstring& dir) { return vk_parent(a, dir, argc > 2 ? std::max(1, _wtoi(argv[2])) : 1); });
    if (argc > 4 && !wcscmp(argv[1], L"vkchild")) return vk_child(_wtoi(argv[2]), (unsigned)_wtoi(argv[3]), argv[4]);
    if (argc > 1 && !wcscmp(argv[1], L"fields"))
        return in_run(L"fields", false, [&](const std::wstring& dir) { return fields_main(a, dir, argc > 2 ? std::max(1, _wtoi(argv[2])) : 1, argc > 3 && !wcscmp(argv[3], L"dxil")); });
    if (argc > 3 && !wcsncmp(argv[1], L"fields", 6)) return fields_child(argv[1][6] - L'0', (unsigned)_wtoi(argv[2]), argv[3]);
    if (argc > 1 && !wcscmp(argv[1], L"gpulock")) return gpu_lock_rules();
    if (argc > 1 && !wcscmp(argv[1], L"ledgerdir")) return ledger_dir_rules();
    if (argc > 2) return child(dir, argv[1], (unsigned)_wtoi(argv[2]));
    int r = in_run(L"selftest", false, [&](const std::wstring& d) {  // the proxy writes its db and log next to itself: in there
        if (!CopyFileW(a.c_str(), (d + L"selftest.exe").c_str(), TRUE) || !CopyFileW((dir + L"d3d12.dll").c_str(), (d + L"d3d12.dll").c_str(), TRUE)) return 1;
        std::wstring seed = L" " + std::to_wstring(GetTickCount() % 1000000);
        int rc = run(d + L"selftest.exe", L"record" + seed);
        // SELFTEST_AFTERMATH=<games' GFSDK_Aftermath_Lib.x64.dll, ';'-separated>, run with 2.10, 2.11, 2.18, 2.23, 2.24, 2.25,
        // 2.26 and 2.27 (1.3 has no EnableGpuCrashDumps). Each in a folder of its own, as a second device makes the runtime
        // create pipelines of its own, which would be recorded next to the phases' three
        if (DWORD n = rc ? 0 : GetEnvironmentVariableW(L"SELFTEST_AFTERMATH", nullptr, 0)) {
            std::wstring all(n, L'\0');
            all.resize(GetEnvironmentVariableW(L"SELFTEST_AFTERMATH", all.data(), n));
            int i = 0;
            for (size_t at = 0, semi; at <= all.size(); at = semi + 1, ++i) {
                semi = std::min(all.find(L';', at), all.size());
                SetEnvironmentVariableW(L"SELFTEST_AFTERMATH", all.substr(at, semi - at).c_str());   // the child loads this one
                std::wstring sub = d + L"aftermath" + std::to_wstring(i) + L"\\";
                CreateDirectoryW(sub.c_str(), nullptr);
                if (!CopyFileW((d + L"selftest.exe").c_str(), (sub + L"selftest.exe").c_str(), FALSE) || !CopyFileW((d + L"d3d12.dll").c_str(), (sub + L"d3d12.dll").c_str(), FALSE)) return 1;
                if (int arc = run(sub + L"selftest.exe", L"aftermath" + seed)) return arc;
                std::string log = read_all(sub + L"scskiller.log");
                size_t once = log.find("aftermath: EnableGpuCrashDumps hooked, DX12_Initialize hooked");
                if (once == std::string::npos || log.find("aftermath: EnableGpuCrashDumps", once + 1) != std::string::npos ||
                    log.find("DX12_Initialize(version 0x217, feature flags 0x8)") == std::string::npos || log.find("feature flags 0x4") != std::string::npos)
                    return printf("FAIL: the recorder didn't hook Aftermath once or didn't log its feature flags (%ls)\n", all.substr(at, semi - at).c_str()), 1;
                size_t line = log.find("aftermath: DX12_Initialize(");
                printf("%ls: %s\n", all.substr(at, semi - at).c_str(), log.substr(line, log.find('\n', line) - line).c_str());
            }
            SetEnvironmentVariableW(L"SELFTEST_AFTERMATH", all.c_str());
        }
        // SELFTEST_AGS=<a game's amd_ags_x64.dll, AGS 6>: the app name it registers reaches the csv as the app reads it
        // Another vendor in the app's ini (games ship the DLL on any GPU): no hook, no line. An attestation revoked before the
        // first device: admission refuses, and the hook, in place by then, writes nothing. vendor=amd present only while AGS
        // loads, outside the signed ini: admission takes the hook out.
        if (!rc && GetEnvironmentVariableW(L"SELFTEST_AGS", nullptr, 0)) {
            const char* const hooked = "ags: amd_ags_x64.dll agsDriverExtensionsDX12_CreateDevice hooked";
            using V = std::tuple<const wchar_t*, const wchar_t*, const char*>;
            for (auto [name, mode, vendor] : {V{L"ags", L"ags", "amd"}, V{L"agsnvidia", L"ags", "nvidia"}, V{L"agsrevoked", L"agsrevoked", "amd"},
                                              V{L"agsnoldr", L"agsnoldr", "amd"}, V{L"agsdisarmed", L"agsdisarmed", "amd"}, V{L"agsunsigned", L"agsunsigned", ""}}) {
                std::wstring sub = d + name + L"\\";
                CreateDirectoryW(sub.c_str(), nullptr);
                if (!CopyFileW((d + L"selftest.exe").c_str(), (sub + L"selftest.exe").c_str(), FALSE) || !CopyFileW((d + L"d3d12.dll").c_str(), (sub + L"d3d12.dll").c_str(), FALSE)) return 1;
                if (FILE* f = _wfopen((sub + L"scskiller.ini").c_str(), L"w")) fprintf(f, "[scskiller]\nmode=record\n%s%s%s", *vendor ? "vendor=" : "", vendor, *vendor ? "\n" : ""), fclose(f);
                // agsnoldr: the child inherits this, and its proxy's load-time registration "fails", so nothing is installed
                SetEnvironmentVariableW(L"SCSKILLER_TEST_NO_LDR", wcscmp(name, L"agsnoldr") ? nullptr : L"1");
                int arc = run(sub + L"selftest.exe", std::wstring(mode) + seed);
                SetEnvironmentVariableW(L"SCSKILLER_TEST_NO_LDR", nullptr);
                if (arc) return arc;
                std::string log = read_all(sub + L"scskiller.log"), csv = read_all(sub + L"scskiller_creates.csv");
                if (!wcscmp(name, L"agsunsigned")) {
                    if (GetFileAttributesW((sub + L"no-ags-device").c_str()) != INVALID_FILE_ATTRIBUTES) {
                        printf("ags: agsunsigned: no AGS device (no AMD driver): not checked\n");
                        continue;
                    }
                    if (log.find("hook: device") == std::string::npos || log.find("ags: unhooked (vendor=amd isn't in the signed ini)") == std::string::npos
                        || csv.find("#ags,") != std::string::npos)
                        return printf("FAIL: %ls: the recorder kept the AGS hook of an unsigned vendor=amd or wrote its line\n", name), 1;
                    continue;
                }
                if (wcscmp(name, L"ags")) {
                    if (log.find("ags: ") != std::string::npos || csv.find("#ags,") != std::string::npos)
                        return printf("FAIL: %ls: the recorder hooked AGS or wrote its line\n", name), 1;
                    if (!wcscmp(name, L"agsnvidia") && log.find("hook: device") == std::string::npos)
                        return printf("FAIL: %ls: the recorder wasn't admitted, so the check proves nothing\n", name), 1;
                    continue;
                }
                if (log.find(hooked) == std::string::npos)
                    return printf("FAIL: the recorder didn't hook the AGS DLL's agsDriverExtensionsDX12_CreateDevice\n"), 1;
                if (log.find("ags: DX12_CreateDevice(app \"SelftestAgsApp\"") == std::string::npos)
                    printf("ags: no AGS device (no AMD driver): the csv line isn't checked\n");
                else {
                    if (csv.find(",10203,40506,SelftestAgsApp,Selftest%002CEngine%0025") == std::string::npos || csv.find("#ags,") < csv.find("#session,"))
                        return printf("FAIL: the csv has no #ags line after its #session\n"), 1;
                    size_t lines = 0;
                    for (size_t at = 0; (at = csv.find("#ags,", at)) != std::string::npos; at += 5) ++lines;
                    if (lines != 3) return printf("FAIL: X,Y,X registrations gave %zu #ags lines, not 3 (consecutive-only dedup)\n", lines), 1;
                }
            }
        }
        if (!rc) rc = run(d + L"selftest.exe", L"warm" + seed);
        if (rc) return rc;
        // every item created: the warm's list of what it didn't create is there and empty
        if (GetFileAttributesW((d + L"scskiller_failed.keys").c_str()) == INVALID_FILE_ATTRIBUTES || !read_all(d + L"scskiller_failed.keys").empty())
            return printf("FAIL: scskiller_failed.keys missing or not empty after a clean warm\n"), 1;
        // what the app writes after a compile (Recordings.Warmed): known then means "compiled", not "in the db"
        std::string db = read_all(d + L"scskiller.db"), keys;
        for (size_t i = 0; i + 5 <= db.size();) {
            uint32_t n;
            memcpy(&n, &db[i + 1], 4);
            if (i + 5 + n > db.size()) break;
            if (db[i] == 'S') { auto h = sha1_of('S', db.substr(i + 5, n)); keys.append(h.begin(), h.end()); }
            i += 5 + n;
        }
        uint32_t count = (uint32_t)(keys.size() / 20);
        FILE* f = _wfopen((d + L"scskiller.warmed").c_str(), L"wb");
        if (!f) return 1;
        fwrite("SCSKWRM1", 1, 8, f), fwrite(&count, 4, 1, f), fwrite(keys.data(), 1, keys.size(), f), fclose(f);
        return run(d + L"selftest.exe", L"warmed" + seed);
    });
    printf(r ? "selftest FAILED\n" : "selftest OK\n");
    return r;
}
