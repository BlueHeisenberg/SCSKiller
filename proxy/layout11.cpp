// D3D11 compile-key probe: which input-layout fields, partner stages and output-merger state make the driver compile a
// shader again, whether those compiles reach the driver's disk cache, and whether a zero-count indirect draw (what
// warm11.cpp issues) compiles at all. Standalone: one file, system d3d11.dll, no SCSKiller code.
//   cl /std:c++17 /O2 /EHsc layout11.cpp d3d11.lib dxgi.lib d3dcompiler.lib
// No args: the orchestrator. Copies itself to fresh exe names (a new name = an empty driver cache), runs the children
// 3x with fresh seeds and prints medians. Children:
//   vs <seed> [again]  heavy VS + trivial PS: the first draw, then one draw per variant (layout field, topology,
//                      partner PS). "again" = a second process of the same name: the variants in reverse order.
//   ps <seed> [again]  heavy PS + trivial VS (no input layout): render-target formats, blend, depth, MSAA, SRV formats,
//                      samplers, partner VS.
//   warm <seed> <gen|exact|direct>  the heavy VS, PS and CS used once: gen = warm11's way (float4 per element, a pass-
//                      through VS for the PS, RGBA16F + D24S8, zero-count indirect); exact = the game's layout and PS,
//                      still indirect; direct = exact with a real draw.
//   game <seed> <order>  the "game": heavy VS with its layout, heavy PS, heavy CS, each timed on its own (child_game).
//   real <vs.bin> [again]  a game's VS: a layout from its input signature (32-bit formats), then one element at a
//                      time in a 16- or 8-bit format.
//   geo <seed> [again] a heavy GS and a heavy HS+DS pair behind other VSs and before other PSs.
//   real-dir <dir>     the orchestrator for every *.bin in dir (run twice per file, under one fresh name).
//   pspair <seed> <warm|game> <l|t> <a|b>  the heavy PS behind a VS with or without an input layout (child_pspair).
//   only <vs|ps|geo|pspair|warmps|warm>  the orchestrator for one section.
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi.h>
#include <algorithm>
#include <bit>
#include <chrono>
#include <cstdio>
#include <functional>
#include <map>
#include <string>
#include <vector>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "d3dcompiler.lib")

static ID3D11Device* dev;
static ID3D11DeviceContext* ctx;

template <class F> static double ms(F&& f) {
    auto t0 = std::chrono::steady_clock::now();
    f();
    return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
}

static ID3DBlob* compile(const std::string& src, const char* target) {
    ID3DBlob *code = nullptr, *err = nullptr;
    D3DCompile(src.data(), src.size(), nullptr, nullptr, nullptr, "main", target, 0, 0, &code, &err);
    if (err) printf("# fxc: %s\n", (const char*)err->GetBufferPointer()), err->Release();
    return code;
}

// Issue + Flush + wait on an event query: includes a compile the driver does lazily at the first draw.
static double timed(const std::function<void()>& issue) {
    ID3D11Query* q = nullptr;
    D3D11_QUERY_DESC qd = {D3D11_QUERY_EVENT, 0};
    dev->CreateQuery(&qd, &q);
    double t = ms([&] {
        issue();
        ctx->End(q);
        ctx->Flush();
        while (ctx->GetData(q, nullptr, 0, 0) == S_FALSE) Sleep(0);
    });
    q->Release();
    return t;
}

// The hardware adapter with the most dedicated VRAM, as scskiller_warm picks it.
static bool make_device() {
    IDXGIFactory1* f = nullptr;
    IDXGIAdapter1 *best = nullptr, *a;
    DXGI_ADAPTER_DESC1 bd{}, d;
    CreateDXGIFactory1(IID_PPV_ARGS(&f));
    for (UINT i = 0; f && f->EnumAdapters1(i, &a) != DXGI_ERROR_NOT_FOUND; ++i) {
        a->GetDesc1(&d);
        if (!(d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) && (!best || d.DedicatedVideoMemory > bd.DedicatedVideoMemory)) std::swap(best, a), bd = d;
        if (a) a->Release();
    }
    D3D_FEATURE_LEVEL fl[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0}, got;
    if (best) D3D11CreateDevice(best, D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, fl, 2, D3D11_SDK_VERSION, &dev, &got, &ctx), best->Release();
    if (f) f->Release();
    if (dev) printf("# adapter %ls\n", bd.Description);
    return dev != nullptr;
}

template <class T> static void rel(T*& p) { if (p) p->Release(), p = nullptr; }

static ID3D11Buffer *g_cb, *g_vb, *g_args, *g_ub;
static ID3D11UnorderedAccessView* g_uav;
static ID3D11SamplerState* g_smp;

static void common() {
    std::vector<char> zero(65536);
    D3D11_SUBRESOURCE_DATA init = {zero.data()};
    D3D11_BUFFER_DESC cbd = {65536, D3D11_USAGE_IMMUTABLE, D3D11_BIND_CONSTANT_BUFFER};
    dev->CreateBuffer(&cbd, &init, &g_cb);
    D3D11_BUFFER_DESC vbd = {4096, D3D11_USAGE_IMMUTABLE, D3D11_BIND_VERTEX_BUFFER};
    dev->CreateBuffer(&vbd, &init, &g_vb);
    D3D11_BUFFER_DESC abd = {32, D3D11_USAGE_DEFAULT, 0, 0, D3D11_RESOURCE_MISC_DRAWINDIRECT_ARGS};
    dev->CreateBuffer(&abd, &init, &g_args);
    D3D11_BUFFER_DESC ubd = {256, D3D11_USAGE_DEFAULT, D3D11_BIND_UNORDERED_ACCESS};
    dev->CreateBuffer(&ubd, &init, &g_ub);
    D3D11_UNORDERED_ACCESS_VIEW_DESC uvd = {DXGI_FORMAT_R32_FLOAT, D3D11_UAV_DIMENSION_BUFFER};
    uvd.Buffer.NumElements = 64;
    if (g_ub) dev->CreateUnorderedAccessView(g_ub, &uvd, &g_uav);
    D3D11_SAMPLER_DESC sd = {D3D11_FILTER_MIN_MAG_MIP_LINEAR, D3D11_TEXTURE_ADDRESS_WRAP, D3D11_TEXTURE_ADDRESS_WRAP, D3D11_TEXTURE_ADDRESS_WRAP};
    sd.MaxLOD = D3D11_FLOAT32_MAX;
    dev->CreateSamplerState(&sd, &g_smp);
    ID3D11Buffer* cbs[14];
    for (auto& b : cbs) b = g_cb;
    ctx->VSSetConstantBuffers(0, 14, cbs), ctx->PSSetConstantBuffers(0, 14, cbs), ctx->CSSetConstantBuffers(0, 14, cbs);
    D3D11_VIEWPORT vp = {0, 0, 16, 16, 0, 1};
    ctx->RSSetViewports(1, &vp);
}

static ID3D11Texture2D* tex2d(DXGI_FORMAT f, UINT bind, UINT samples = 1, UINT size = 16) {
    D3D11_TEXTURE2D_DESC td = {size, size, 1, 1, f, {samples, 0}, D3D11_USAGE_DEFAULT, bind};
    ID3D11Texture2D* t = nullptr;
    dev->CreateTexture2D(&td, nullptr, &t);
    return t;
}
static ID3D11RenderTargetView* make_rtv(DXGI_FORMAT f, UINT samples = 1) {
    ID3D11Texture2D* t = tex2d(f, D3D11_BIND_RENDER_TARGET, samples);
    ID3D11RenderTargetView* v = nullptr;
    if (t) dev->CreateRenderTargetView(t, nullptr, &v), t->Release();
    return v;
}
static ID3D11DepthStencilView* make_dsv(DXGI_FORMAT f, UINT samples = 1) {
    ID3D11Texture2D* t = tex2d(f, D3D11_BIND_DEPTH_STENCIL, samples);
    ID3D11DepthStencilView* v = nullptr;
    if (t) dev->CreateDepthStencilView(t, nullptr, &v), t->Release();
    return v;
}
static ID3D11ShaderResourceView* make_srv(DXGI_FORMAT f) {
    ID3D11Texture2D* t = tex2d(f, D3D11_BIND_SHADER_RESOURCE);
    ID3D11ShaderResourceView* v = nullptr;
    if (t) dev->CreateShaderResourceView(t, nullptr, &v), t->Release();
    return v;
}

// ---- shaders. `K` (the seed) makes the heavy ones bytecode no driver cache has seen.
static std::string K(unsigned seed) { return std::to_string(seed) + ".0"; }
static std::string heavy_vs(unsigned s) {  // TANGENT is declared and never read
    return "cbuffer C:register(b0){float4 kc;};"
           "struct I{float3 p:POSITION;float3 n:NORMAL;float2 uv:TEXCOORD0;float4 c:COLOR0;float4 t:TANGENT0;};"
           "struct O{float4 pos:SV_Position;float4 a:TEXCOORD0;float4 b:TEXCOORD1;};"
           "O main(I i){O o;float3 pp=i.p+i.n;[unroll]for(int k=0;k<64;++k){pp=sin(pp*(" + K(s) + "+k))+cos(pp.yzx)*i.c.rgb;}"
           "o.pos=float4(pp,1)+kc;o.a=float4(pp*i.uv.x,i.uv.y);o.b=i.c*kc;return o;}";
}
static std::string heavy_ps(unsigned s) {
    return "Texture2D t0:register(t0);Texture2D t1:register(t1);Texture2D t2:register(t2);Texture2D t3:register(t3);"
           "SamplerState ss:register(s0);cbuffer C:register(b0){float4 kc;};"
           "float4 main(float4 pos:SV_Position,float2 uv:TEXCOORD0):SV_Target{"
           "float4 c=t0.Sample(ss,uv)+t1.Sample(ss,uv*2)+t2.Sample(ss,uv*3)+t3.Sample(ss,uv*4);"
           "[unroll]for(int i=0;i<64;++i){c=sin(c*(" + K(s) + "+i))+pow(abs(c)+0.001,1.3);} return c+kc;}";
}
static std::string heavy_cs(unsigned s) {
    return "RWBuffer<float> b:register(u0);[numthreads(64,1,1)]void main(uint3 id:SV_DispatchThreadID){"
           "float4 v=float4(id.x," + K(s) + ",2,3);[unroll]for(int i=0;i<160;++i){v=sin(v*(" + K(s) + "+i))+pow(abs(v.yzwx)+0.001,1.3);} b[id.x]=dot(v,1);}";
}
// Partner PSs of the heavy VS (index = VsVar::ps; 5 = no PS).
static const char* kPartnerPs[] = {
    "float4 main(float4 p:SV_Position,float4 a:TEXCOORD0,float4 b:TEXCOORD1):SV_Target{return a+b;}",
    "float4 main(float4 p:SV_Position,float4 a:TEXCOORD0):SV_Target{return a.x;}",
    "float4 main(float4 p:SV_Position,nointerpolation float4 a:TEXCOORD0,nointerpolation float4 b:TEXCOORD1):SV_Target{return a+b;}",
    "float4 main(float4 p:SV_Position,float4 a:TEXCOORD0,float4 b:TEXCOORD1):SV_Target{return (a+b)*2;}",
    "float4 main():SV_Target{return 0;}",  // warm11's null_ps
};
// Partner VSs of the heavy PS (index = PsVar::vs): no input layout.
static const char* kPartnerVs[] = {
    "struct O{float4 p:SV_Position;float2 uv:TEXCOORD0;};O main(uint i:SV_VertexID){O o;o.uv=float2(i&1,i>>1);o.p=float4(o.uv*2-1,0.5,1);return o;}",
    "struct O{float4 p:SV_Position;float2 uv:TEXCOORD0;float4 c:COLOR0;};O main(uint i:SV_VertexID){O o;o.uv=float2(i>>1,i&1);o.p=float4(o.uv*4-1,0.5,1);o.c=i;return o;}",
    "struct O{float4 p:SV_Position;float2 uv:TEXCOORD0;};O main(uint i:SV_VertexID){O o;o.uv=float2(i,i*0.5);o.p=float4(o.uv,0,1);return o;}",
};

// ---- input signature (ISGN/ISG1) of DXBC, enough to build a layout for a game's VS
static uint32_t u32(const std::string& s, size_t o) { uint32_t v = 0; if (o + 4 <= s.size()) memcpy(&v, s.data() + o, 4); return v; }
struct Sig { std::string name; uint32_t index, sv, type, reg; uint8_t mask, rw; };
static std::vector<Sig> isgn(const std::string& c) {
    std::vector<Sig> v;
    if (c.size() < 32 || c.compare(0, 4, "DXBC")) return v;
    for (uint32_t i = 0, n = u32(c, 28); i < n && i < 64; ++i) {
        size_t o = u32(c, 32 + 4 * i), len = u32(c, o + 4);
        bool g1 = !c.compare(o, 4, "ISG1");
        if (!g1 && c.compare(o, 4, "ISGN")) continue;
        std::string d = c.substr(o + 8, len);
        size_t size = g1 ? 32 : 24, lead = g1 ? 4 : 0;
        for (uint32_t e = 0, ne = u32(d, 0); e < ne && e < 64; ++e) {
            size_t b = u32(d, 4) + e * size + lead, nm = u32(d, b);
            if (b + size - lead > d.size() || nm >= d.size()) break;
            v.push_back({std::string(d.c_str() + nm), u32(d, b + 4), u32(d, b + 8), u32(d, b + 12), u32(d, b + 16), (uint8_t)d[b + 20], (uint8_t)d[b + 21]});
        }
    }
    return v;
}

// ---- VS variants
struct El { std::string sem; UINT idx; DXGI_FORMAT f; UINT slot, off; UINT step; };  // step 0 = per vertex
struct VsVar { std::string name; std::vector<El> el; UINT stride[3] = {64, 16, 16}; UINT vboff = 0;
               D3D11_PRIMITIVE_TOPOLOGY topo = D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST; int ps = 0; };

static std::vector<El> base_layout() {
    return {{"POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, 0},  {"NORMAL", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 12, 0},
            {"TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 24, 0},    {"COLOR", 0, DXGI_FORMAT_R8G8B8A8_UNORM, 0, 32, 0},
            {"TANGENT", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 0, 36, 0}};
}
static El& el(VsVar& v, const char* sem) { for (auto& e : v.el) if (_stricmp(e.sem.c_str(), sem) == 0) return e; return v.el[0]; }

static std::vector<VsVar> vs_variants() {
    std::vector<VsVar> r;
    auto add = [&](const char* name, const std::function<void(VsVar&)>& f) { VsVar v{name, base_layout()}; f(v); r.push_back(v); };
    add("base_again", [](VsVar&) {});  // the same desc, a new layout object
    add("order", [](VsVar& v) { std::reverse(v.el.begin(), v.el.end()); });
    add("extra_elem", [](VsVar& v) { v.el.push_back({"BLENDWEIGHT", 0, DXGI_FORMAT_R32_FLOAT, 1, 0, 0}); });
    add("sem_case", [](VsVar& v) { v.el[0].sem = "position"; v.el[1].sem = "Normal"; });
    add("stride", [](VsVar& v) { v.stride[0] = 80; });
    add("vb_offset", [](VsVar& v) { v.vboff = 16; });
    add("off_read", [](VsVar& v) { el(v, "COLOR").off = 24; el(v, "TEXCOORD").off = 28; });
    add("off_unread", [](VsVar& v) { el(v, "TANGENT").off = 48; });
    add("fmt_unread", [](VsVar& v) { el(v, "TANGENT").f = DXGI_FORMAT_R16G16B16A16_FLOAT; });
    add("pos_f32x4", [](VsVar& v) { el(v, "POSITION").f = DXGI_FORMAT_R32G32B32A32_FLOAT; el(v, "POSITION").off = 48; });
    add("pos_f16x4", [](VsVar& v) { el(v, "POSITION").f = DXGI_FORMAT_R16G16B16A16_FLOAT; el(v, "POSITION").off = 52; });
    add("pos_snorm16x4", [](VsVar& v) { el(v, "POSITION").f = DXGI_FORMAT_R16G16B16A16_SNORM; el(v, "POSITION").off = 52; });
    add("nrm_unorm8x4", [](VsVar& v) { el(v, "NORMAL").f = DXGI_FORMAT_R8G8B8A8_UNORM; });
    add("nrm_snorm8x4", [](VsVar& v) { el(v, "NORMAL").f = DXGI_FORMAT_R8G8B8A8_SNORM; });
    add("nrm_rgb10a2", [](VsVar& v) { el(v, "NORMAL").f = DXGI_FORMAT_R10G10B10A2_UNORM; });
    add("nrm_f16x4", [](VsVar& v) { el(v, "NORMAL").f = DXGI_FORMAT_R16G16B16A16_FLOAT; el(v, "NORMAL").off = 52; });
    add("uv_f16x2", [](VsVar& v) { el(v, "TEXCOORD").f = DXGI_FORMAT_R16G16_FLOAT; });
    add("uv_unorm16x2", [](VsVar& v) { el(v, "TEXCOORD").f = DXGI_FORMAT_R16G16_UNORM; });
    add("uv_snorm16x2", [](VsVar& v) { el(v, "TEXCOORD").f = DXGI_FORMAT_R16G16_SNORM; });
    add("col_bgra8", [](VsVar& v) { el(v, "COLOR").f = DXGI_FORMAT_B8G8R8A8_UNORM; });
    add("col_f32x4", [](VsVar& v) { el(v, "COLOR").f = DXGI_FORMAT_R32G32B32A32_FLOAT; el(v, "COLOR").off = 48; });
    add("slot_read", [](VsVar& v) { el(v, "TEXCOORD").slot = 1; el(v, "TEXCOORD").off = 0; });
    add("slot_unread", [](VsVar& v) { el(v, "TANGENT").slot = 2; el(v, "TANGENT").off = 0; });
    add("slot_all1", [](VsVar& v) { for (auto& e : v.el) e.slot = 1; v.stride[1] = 64; });
    add("inst_read", [](VsVar& v) { el(v, "COLOR").slot = 1; el(v, "COLOR").off = 0; el(v, "COLOR").step = 1; });
    add("inst_rate2", [](VsVar& v) { el(v, "COLOR").slot = 1; el(v, "COLOR").off = 0; el(v, "COLOR").step = 2; });
    add("inst_unread", [](VsVar& v) { el(v, "TANGENT").slot = 2; el(v, "TANGENT").off = 0; el(v, "TANGENT").step = 1; });
    add("topo_strip", [](VsVar& v) { v.topo = D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP; });
    add("topo_line", [](VsVar& v) { v.topo = D3D11_PRIMITIVE_TOPOLOGY_LINELIST; });
    add("topo_point", [](VsVar& v) { v.topo = D3D11_PRIMITIVE_TOPOLOGY_POINTLIST; });
    add("ps_reads_x", [](VsVar& v) { v.ps = 1; });
    add("ps_flat", [](VsVar& v) { v.ps = 2; });
    add("ps_other_same_reads", [](VsVar& v) { v.ps = 3; });
    add("ps_warm11_null", [](VsVar& v) { v.ps = 4; });
    add("ps_none", [](VsVar& v) { v.ps = 5; });
    add("base_end", [](VsVar&) {});
    return r;
}

static ID3D11InputLayout* make_layout(const std::vector<El>& el, ID3DBlob* vsb) {
    std::vector<D3D11_INPUT_ELEMENT_DESC> d;
    for (auto& e : el) d.push_back({e.sem.c_str(), e.idx, e.f, e.slot, e.off, e.step ? D3D11_INPUT_PER_INSTANCE_DATA : D3D11_INPUT_PER_VERTEX_DATA, e.step});
    ID3D11InputLayout* il = nullptr;
    dev->CreateInputLayout(d.data(), (UINT)d.size(), vsb->GetBufferPointer(), vsb->GetBufferSize(), &il);
    return il;
}

static ID3D11RenderTargetView* g_rt;

// One draw of vs (+ps) with this variant's layout and state; -1 if the layout can't be created.
static double draw_vs(ID3D11VertexShader* vs, ID3DBlob* vsb, ID3D11PixelShader* const* ps, const VsVar& v, bool indirect = false) {
    ID3D11InputLayout* il = make_layout(v.el, vsb);
    if (!il) return -1;
    ID3D11Buffer* vbs[3] = {g_vb, g_vb, g_vb};
    UINT off[3] = {v.vboff, 0, 0};
    ctx->IASetInputLayout(il);
    ctx->IASetVertexBuffers(0, 3, vbs, v.stride, off);
    ctx->IASetPrimitiveTopology(v.topo);
    ctx->VSSetShader(vs, nullptr, 0);
    ctx->PSSetShader(v.ps < 5 ? ps[v.ps] : nullptr, nullptr, 0);
    ctx->OMSetRenderTargets(1, &g_rt, nullptr);
    double t = timed([&] { indirect ? ctx->DrawInstancedIndirect(g_args, 0) : ctx->DrawInstanced(3, 2, 0, 0); });
    il->Release();
    return t;
}

static void print_isgn(ID3DBlob* b) {
    printf("# isgn:");
    for (auto& s : isgn(std::string((const char*)b->GetBufferPointer(), b->GetBufferSize())))
        printf(" %s%u(r%u mask=%x read=%x)", s.name.c_str(), s.index, s.reg, s.mask, s.rw);
    printf("\n");
}

static int child_vs(unsigned seed, bool again) {
    if (!make_device()) return 1;
    common();
    g_rt = make_rtv(DXGI_FORMAT_R8G8B8A8_UNORM);
    ID3DBlob* vsb = compile(heavy_vs(seed), "vs_5_0");
    if (!vsb) return 1;
    print_isgn(vsb);
    ID3D11VertexShader* vs = nullptr;
    printf("VS create=%.3f\n", ms([&] { dev->CreateVertexShader(vsb->GetBufferPointer(), vsb->GetBufferSize(), nullptr, &vs); }));
    ID3D11PixelShader* ps[5] = {};
    for (int i = 0; i < 5; ++i)
        if (ID3DBlob* b = compile(kPartnerPs[i], "ps_5_0")) dev->CreatePixelShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &ps[i]), b->Release();
    // the partner PSs' own compiles, behind a throwaway VS, so a variant's time is the heavy VS's
    if (ID3DBlob* b = compile("struct O{float4 p:SV_Position;float4 a:TEXCOORD0;float4 b:TEXCOORD1;};O main(uint i:SV_VertexID){O o=(O)i;o.p=float4(i,0,0,1);return o;}", "vs_5_0")) {
        ID3D11VertexShader* t = nullptr;
        dev->CreateVertexShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &t);
        ctx->IASetInputLayout(nullptr), ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST), ctx->VSSetShader(t, nullptr, 0);
        ctx->OMSetRenderTargets(1, &g_rt, nullptr);
        for (auto* p : ps) ctx->PSSetShader(p, nullptr, 0), timed([&] { ctx->Draw(3, 0); });
        rel(t), b->Release();
    }
    auto vars = vs_variants();
    if (again) std::reverse(vars.begin(), vars.end());
    else printf("VS v=base ms=%.3f\n", draw_vs(vs, vsb, ps, VsVar{"base", base_layout()}));
    for (auto& v : vars) printf("VS v=%s ms=%.3f\n", v.name.c_str(), draw_vs(vs, vsb, ps, v));
    return 0;
}

// ---- PS variants
struct PsVar { std::string name; DXGI_FORMAT rt = DXGI_FORMAT_R8G8B8A8_UNORM; UINT samples = 1; int nrt = 1; bool extra_rt = false;
               int blend = 0; UINT8 wmask = D3D11_COLOR_WRITE_ENABLE_ALL; bool a2c = false; DXGI_FORMAT ds = DXGI_FORMAT_UNKNOWN; bool stencil = false;
               DXGI_FORMAT srv = DXGI_FORMAT_R8G8B8A8_UNORM; int smp = 0; int vs = 0; };

static std::vector<PsVar> ps_variants() {
    std::vector<PsVar> r;
    auto add = [&](const char* name, const std::function<void(PsVar&)>& f) { PsVar v{name}; f(v); r.push_back(v); };
    add("base_again", [](PsVar&) {});
    add("rt_bgra8", [](PsVar& v) { v.rt = DXGI_FORMAT_B8G8R8A8_UNORM; });
    add("rt_srgb", [](PsVar& v) { v.rt = DXGI_FORMAT_R8G8B8A8_UNORM_SRGB; });
    add("rt_rgba16f", [](PsVar& v) { v.rt = DXGI_FORMAT_R16G16B16A16_FLOAT; });
    add("rt_r11g11b10", [](PsVar& v) { v.rt = DXGI_FORMAT_R11G11B10_FLOAT; });
    add("rt_rgb10a2", [](PsVar& v) { v.rt = DXGI_FORMAT_R10G10B10A2_UNORM; });
    add("rt_rgba16unorm", [](PsVar& v) { v.rt = DXGI_FORMAT_R16G16B16A16_UNORM; });
    add("rt_rgba32f", [](PsVar& v) { v.rt = DXGI_FORMAT_R32G32B32A32_FLOAT; });
    add("rt_rg16f", [](PsVar& v) { v.rt = DXGI_FORMAT_R16G16_FLOAT; });
    add("rt_r32f", [](PsVar& v) { v.rt = DXGI_FORMAT_R32_FLOAT; });
    add("rt_r8", [](PsVar& v) { v.rt = DXGI_FORMAT_R8_UNORM; });
    add("rt_rg8", [](PsVar& v) { v.rt = DXGI_FORMAT_R8G8_UNORM; });
    add("rt_none", [](PsVar& v) { v.nrt = 0; });
    add("rt_extra_slot", [](PsVar& v) { v.extra_rt = true; });
    add("blend_alpha", [](PsVar& v) { v.blend = 1; });
    add("blend_add", [](PsVar& v) { v.blend = 2; });
    add("wmask_rgb", [](PsVar& v) { v.wmask = D3D11_COLOR_WRITE_ENABLE_RED | D3D11_COLOR_WRITE_ENABLE_GREEN | D3D11_COLOR_WRITE_ENABLE_BLUE; });
    add("wmask_0", [](PsVar& v) { v.wmask = 0; });
    add("alpha_to_coverage", [](PsVar& v) { v.a2c = true; });
    add("msaa4", [](PsVar& v) { v.samples = 4; });
    add("depth_d32", [](PsVar& v) { v.ds = DXGI_FORMAT_D32_FLOAT; });
    add("depth_d24s8_stencil", [](PsVar& v) { v.ds = DXGI_FORMAT_D24_UNORM_S8_UINT; v.stencil = true; });
    add("srv_rgba16f", [](PsVar& v) { v.srv = DXGI_FORMAT_R16G16B16A16_FLOAT; });
    add("srv_bc1", [](PsVar& v) { v.srv = DXGI_FORMAT_BC1_UNORM; });
    add("srv_r32f", [](PsVar& v) { v.srv = DXGI_FORMAT_R32_FLOAT; });
    add("smp_point", [](PsVar& v) { v.smp = 1; });
    add("smp_aniso", [](PsVar& v) { v.smp = 2; });
    add("vs_more_outputs", [](PsVar& v) { v.vs = 1; });
    add("vs_other_same_outputs", [](PsVar& v) { v.vs = 2; });
    add("base_end", [](PsVar&) {});
    return r;
}

static double draw_ps(ID3D11PixelShader* ps, ID3D11VertexShader* const* vss, const PsVar& v, bool indirect = false) {
    ID3D11RenderTargetView* rt[2] = {v.nrt ? make_rtv(v.rt, v.samples) : nullptr, v.extra_rt ? make_rtv(DXGI_FORMAT_R8G8B8A8_UNORM) : nullptr};
    if (v.nrt && !rt[0]) return -1;
    ID3D11DepthStencilView* dsv = v.ds ? make_dsv(v.ds, v.samples) : nullptr;
    ID3D11DepthStencilState* dss = nullptr;
    if (v.ds) {
        D3D11_DEPTH_STENCIL_DESC d = {TRUE, D3D11_DEPTH_WRITE_MASK_ALL, D3D11_COMPARISON_LESS_EQUAL, v.stencil, 0xFF, 0xFF,
                                      {D3D11_STENCIL_OP_KEEP, D3D11_STENCIL_OP_KEEP, D3D11_STENCIL_OP_REPLACE, D3D11_COMPARISON_ALWAYS},
                                      {D3D11_STENCIL_OP_KEEP, D3D11_STENCIL_OP_KEEP, D3D11_STENCIL_OP_REPLACE, D3D11_COMPARISON_ALWAYS}};
        dev->CreateDepthStencilState(&d, &dss);
    }
    D3D11_BLEND_DESC bd = {v.a2c};
    bd.RenderTarget[0] = {v.blend != 0, v.blend == 1 ? D3D11_BLEND_SRC_ALPHA : D3D11_BLEND_ONE, v.blend == 1 ? D3D11_BLEND_INV_SRC_ALPHA : v.blend ? D3D11_BLEND_ONE : D3D11_BLEND_ZERO,
                          D3D11_BLEND_OP_ADD, D3D11_BLEND_ONE, D3D11_BLEND_ZERO, D3D11_BLEND_OP_ADD, v.wmask};
    ID3D11BlendState* bs = nullptr;
    dev->CreateBlendState(&bd, &bs);
    ID3D11ShaderResourceView* srv = make_srv(v.srv);
    ID3D11ShaderResourceView* srvs[4] = {srv, srv, srv, srv};
    D3D11_SAMPLER_DESC sd = {v.smp == 1 ? D3D11_FILTER_MIN_MAG_MIP_POINT : v.smp == 2 ? D3D11_FILTER_ANISOTROPIC : D3D11_FILTER_MIN_MAG_MIP_LINEAR,
                             D3D11_TEXTURE_ADDRESS_WRAP, D3D11_TEXTURE_ADDRESS_WRAP, D3D11_TEXTURE_ADDRESS_WRAP, 0, 16};
    sd.MaxLOD = D3D11_FLOAT32_MAX;
    ID3D11SamplerState* smp = nullptr;
    dev->CreateSamplerState(&sd, &smp);
    ctx->IASetInputLayout(nullptr);
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    ctx->VSSetShader(vss[v.vs], nullptr, 0);
    ctx->PSSetShader(ps, nullptr, 0);
    ctx->PSSetShaderResources(0, 4, srvs);
    ctx->PSSetSamplers(0, 1, &smp);
    ctx->OMSetRenderTargets(v.extra_rt ? 2 : v.nrt, rt, dsv);
    ctx->OMSetDepthStencilState(dss, 1);
    ctx->OMSetBlendState(bs, nullptr, 0xFFFFFFFF);
    D3D11_VIEWPORT vp = {0, 0, 16, 16, 0, 1};
    ctx->RSSetViewports(1, &vp);
    double t = timed([&] { indirect ? ctx->DrawInstancedIndirect(g_args, 0) : ctx->Draw(3, 0); });
    ctx->OMSetRenderTargets(0, nullptr, nullptr);
    ctx->OMSetDepthStencilState(nullptr, 0), ctx->OMSetBlendState(nullptr, nullptr, 0xFFFFFFFF);
    rel(rt[0]), rel(rt[1]), rel(dsv), rel(dss), rel(bs), rel(srv), rel(smp);
    return t;
}

static int child_ps(unsigned seed, bool again) {
    if (!make_device()) return 1;
    common();
    ID3DBlob* psb = compile(heavy_ps(seed), "ps_5_0");
    if (!psb) return 1;
    ID3D11PixelShader* ps = nullptr;
    printf("PS create=%.3f\n", ms([&] { dev->CreatePixelShader(psb->GetBufferPointer(), psb->GetBufferSize(), nullptr, &ps); }));
    ID3D11VertexShader* vss[3] = {};
    for (int i = 0; i < 3; ++i)
        if (ID3DBlob* b = compile(kPartnerVs[i], "vs_5_0")) dev->CreateVertexShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &vss[i]), b->Release();
    // the partner VSs' own compiles, behind a throwaway PS
    if (ID3DBlob* b = compile("float4 main(float4 p:SV_Position,float2 uv:TEXCOORD0):SV_Target{return uv.xyxy;}", "ps_5_0")) {
        ID3D11PixelShader* t = nullptr;
        dev->CreatePixelShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &t);
        for (int i = 0; i < 3; ++i) { PsVar v{"pre"}; v.vs = i; draw_ps(t, vss, v); }
        rel(t), b->Release();
    }
    auto vars = ps_variants();
    if (again) std::reverse(vars.begin(), vars.end());
    else printf("PS v=base ms=%.3f\n", draw_ps(ps, vss, PsVar{"base"}));
    for (auto& v : vars) printf("PS v=%s ms=%.3f\n", v.name.c_str(), draw_ps(ps, vss, v));
    return 0;
}

// ---- GS and HS+DS: do they compile again for another VS before them or another PS after them?
static std::string heavy_gs(unsigned s) {
    return "struct V{float4 p:SV_Position;float2 uv:TEXCOORD0;};[maxvertexcount(3)]void main(triangle V v[3],inout TriangleStream<V> s){"
           "[unroll]for(int i=0;i<3;++i){V o=v[i];float3 pp=o.p.xyz;[unroll]for(int k=0;k<48;++k){pp=sin(pp*(" + K(s) + "+k))+cos(pp.yzx);}"
           "o.p.xyz=pp;s.Append(o);}}";
}
static const char* kTessCp = "struct CP{float3 p:POS;float2 uv:TEXCOORD0;};struct PC{float e[3]:SV_TessFactor;float i:SV_InsideTessFactor;};";
static std::string heavy_hs(unsigned s) {
    return std::string(kTessCp) + "PC pcf(InputPatch<CP,3> ip){PC o;float3 a=ip[0].p+ip[1].p+ip[2].p;[unroll]for(int j=0;j<32;++j){a=sin(a*(" + K(s) +
           "+j))+cos(a);}o.e[0]=a.x;o.e[1]=a.y;o.e[2]=a.z;o.i=a.x+a.y;return o;}"
           "[domain(\"tri\")][partitioning(\"fractional_odd\")][outputtopology(\"triangle_cw\")][outputcontrolpoints(3)][patchconstantfunc(\"pcf\")]"
           "CP main(InputPatch<CP,3> ip,uint i:SV_OutputControlPointID){CP o=ip[i];float3 a=o.p;[unroll]for(int j=0;j<32;++j){a=sin(a*(" + K(s) + "+j))+cos(a);}o.p=a;return o;}";
}
static std::string heavy_ds(unsigned s) {
    return std::string(kTessCp) + "struct O{float4 pos:SV_Position;float2 uv:TEXCOORD0;};[domain(\"tri\")]O main(PC pc,float3 b:SV_DomainLocation,const OutputPatch<CP,3> p){"
           "O o;float3 a=p[0].p*b.x+p[1].p*b.y+p[2].p*b.z;[unroll]for(int j=0;j<48;++j){a=sin(a*(" + K(s) + "+j))+cos(a);}"
           "o.pos=float4(a,1);o.uv=p[0].uv*b.x+p[1].uv*b.y+p[2].uv*b.z;return o;}";
}
// VSs before the GS (0-2) and before the HS (3-5): the same outputs, other code with the same outputs, more outputs.
static const char* kGeoVs[] = {
    "struct O{float4 p:SV_Position;float2 uv:TEXCOORD0;};O main(uint i:SV_VertexID){O o;o.uv=float2(i&1,i>>1);o.p=float4(o.uv*2-1,0.5,1);return o;}",
    "struct O{float4 p:SV_Position;float2 uv:TEXCOORD0;};O main(uint i:SV_VertexID){O o;o.uv=float2(i,i*0.5);o.p=float4(o.uv,0,1);return o;}",
    "struct O{float4 p:SV_Position;float2 uv:TEXCOORD0;float4 c:COLOR0;};O main(uint i:SV_VertexID){O o;o.uv=float2(i>>1,i&1);o.p=float4(o.uv*4-1,0.5,1);o.c=i;return o;}",
    "struct CP{float3 p:POS;float2 uv:TEXCOORD0;};CP main(uint i:SV_VertexID){CP o;o.uv=float2(i&1,i>>1);o.p=float3(o.uv*2-1,0.5);return o;}",
    "struct CP{float3 p:POS;float2 uv:TEXCOORD0;};CP main(uint i:SV_VertexID){CP o;o.uv=float2(i,i*0.5);o.p=float3(o.uv,i);return o;}",
    "struct CP{float3 p:POS;float2 uv:TEXCOORD0;float4 c:COLOR0;};CP main(uint i:SV_VertexID){CP o;o.uv=float2(i>>1,i&1);o.p=float3(o.uv*4-1,0.5);o.c=i;return o;}",
};
// PSs after: reads all, reads less, none
static const char* kGeoPs[] = {
    "float4 main(float4 p:SV_Position,float2 uv:TEXCOORD0):SV_Target{return uv.xyxy;}",
    "float4 main(float4 p:SV_Position,float2 uv:TEXCOORD0):SV_Target{return uv.x;}",
};

static int child_geo(unsigned seed, bool again) {
    if (!make_device()) return 1;
    common();
    g_rt = make_rtv(DXGI_FORMAT_R8G8B8A8_UNORM);
    ID3D11GeometryShader* gs = nullptr;
    ID3D11HullShader* hs = nullptr;
    ID3D11DomainShader* ds = nullptr;
    if (ID3DBlob* b = compile(heavy_gs(seed), "gs_5_0")) dev->CreateGeometryShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &gs), b->Release();
    if (ID3DBlob* b = compile(heavy_hs(seed), "hs_5_0")) dev->CreateHullShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &hs), b->Release();
    if (ID3DBlob* b = compile(heavy_ds(seed + 1), "ds_5_0")) dev->CreateDomainShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &ds), b->Release();
    ID3D11VertexShader* vs[6] = {};
    ID3D11PixelShader* ps[2] = {};
    for (int i = 0; i < 6; ++i)
        if (ID3DBlob* b = compile(kGeoVs[i], "vs_5_0")) dev->CreateVertexShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &vs[i]), b->Release();
    for (int i = 0; i < 2; ++i)
        if (ID3DBlob* b = compile(kGeoPs[i], "ps_5_0")) dev->CreatePixelShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &ps[i]), b->Release();
    if (!gs || !hs || !ds || !vs[5] || !ps[1]) return printf("# shader creation failed\n"), 1;
    ctx->IASetInputLayout(nullptr);
    ctx->OMSetRenderTargets(1, &g_rt, nullptr);
    // the partners' own compiles first, without the heavy stages
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    for (int v = 0; v < 3; ++v) for (int p = 0; p < 2; ++p) ctx->VSSetShader(vs[v], nullptr, 0), ctx->PSSetShader(ps[p], nullptr, 0), timed([&] { ctx->Draw(3, 0); });
    struct Var { const char* name; bool tess; int vs, ps; };  // ps -1 = none
    std::vector<Var> vars = {{"gs_base_again", false, 0, 0}, {"gs_vs_other_same_outputs", false, 1, 0}, {"gs_vs_more_outputs", false, 2, 0},
                             {"gs_ps_reads_less", false, 0, 1}, {"gs_ps_none", false, 0, -1}, {"gs_base_end", false, 0, 0},
                             {"tess_base_again", true, 3, 0}, {"tess_vs_other_same_outputs", true, 4, 0}, {"tess_vs_more_outputs", true, 5, 0},
                             {"tess_ps_reads_less", true, 3, 1}, {"tess_ps_none", true, 3, -1}, {"tess_base_end", true, 3, 0}};
    auto draw = [&](const Var& v) {
        ctx->IASetPrimitiveTopology(v.tess ? D3D11_PRIMITIVE_TOPOLOGY_3_CONTROL_POINT_PATCHLIST : D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        ctx->VSSetShader(vs[v.vs], nullptr, 0);
        ctx->GSSetShader(v.tess ? nullptr : gs, nullptr, 0);
        ctx->HSSetShader(v.tess ? hs : nullptr, nullptr, 0);
        ctx->DSSetShader(v.tess ? ds : nullptr, nullptr, 0);
        ctx->PSSetShader(v.ps < 0 ? nullptr : ps[v.ps], nullptr, 0);
        return timed([&] { ctx->Draw(3, 0); });
    };
    if (again) std::reverse(vars.begin(), vars.end());
    else printf("GEO v=gs_base ms=%.3f\nGEO v=tess_base ms=%.3f\n", draw(vars[0]), draw(vars[6]));
    for (auto& v : vars) printf("GEO v=%s ms=%.3f\n", v.name, draw(v));
    return 0;
}

// ---- warm / game
static ID3D11ComputeShader* make_cs(unsigned seed) {
    ID3D11ComputeShader* cs = nullptr;
    if (ID3DBlob* b = compile(heavy_cs(seed), "cs_5_0")) dev->CreateComputeShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &cs), b->Release();
    return cs;
}
static double dispatch(ID3D11ComputeShader* cs, bool indirect) {
    ctx->CSSetShader(cs, nullptr, 0);
    ctx->CSSetUnorderedAccessViews(0, 1, &g_uav, nullptr);
    return timed([&] { indirect ? ctx->DispatchIndirect(g_args, 0) : ctx->Dispatch(1, 1, 1); });
}

static int child_warm(unsigned seed, const std::wstring& kind) {
    if (!make_device()) return 1;
    common();
    bool gen = kind == L"gen", indirect = kind != L"direct";
    ID3DBlob *vsb = compile(heavy_vs(seed), "vs_5_0"), *psb = compile(heavy_ps(seed), "ps_5_0");
    ID3D11VertexShader* vs = nullptr;
    ID3D11PixelShader* ps = nullptr;
    if (!vsb || !psb) return 1;
    dev->CreateVertexShader(vsb->GetBufferPointer(), vsb->GetBufferSize(), nullptr, &vs);
    dev->CreatePixelShader(psb->GetBufferPointer(), psb->GetBufferSize(), nullptr, &ps);
    ID3D11PixelShader* pps[5] = {};
    for (int i = 0; i < 5; ++i)
        if (ID3DBlob* b = compile(kPartnerPs[i], "ps_5_0")) dev->CreatePixelShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &pps[i]), b->Release();
    ID3D11VertexShader* vss[3] = {};
    for (int i = 0; i < 3; ++i)
        if (ID3DBlob* b = compile(kPartnerVs[i], "vs_5_0")) dev->CreateVertexShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &vss[i]), b->Release();
    // warm11: RGBA16F target, D24S8 depth (always), float4 per input element, its null PS behind a VS, a pass-through VS
    g_rt = make_rtv(gen ? DXGI_FORMAT_R16G16B16A16_FLOAT : DXGI_FORMAT_R8G8B8A8_UNORM);
    VsVar v{"warm", base_layout()};
    if (gen) {
        UINT off = 0;
        for (auto& e : v.el) e.f = DXGI_FORMAT_R32G32B32A32_FLOAT, e.off = off, off += 16;
        v.stride[0] = off, v.ps = 4;
    }
    PsVar p{"warm"};
    if (gen) p.rt = DXGI_FORMAT_R16G16B16A16_FLOAT, p.ds = DXGI_FORMAT_D24_UNORM_S8_UINT, p.vs = 2;
    // one PS difference from the game's state at a time: depth, vs2 (another partner VS), rt=<DXGI format>
    if (kind == L"depth") p.ds = DXGI_FORMAT_D24_UNORM_S8_UINT;
    if (kind == L"vs2") p.vs = 2;
    if (kind.rfind(L"rt=", 0) == 0) p.rt = (DXGI_FORMAT)_wtoi(kind.c_str() + 3);
    double a = draw_vs(vs, vsb, pps, v, indirect), b = draw_ps(ps, vss, p, indirect), c = dispatch(make_cs(seed), indirect);
    printf("WARM kind=%ls vs=%.3f ps=%.3f cs=%.3f\n", kind.c_str(), a, b, c);
    return 0;
}

// order: vsfirst = create everything, then VS, PS, CS; isolated = each heavy shader created right before its own first
// use, PS, CS, VS (a compile started at create can't hide behind another's); sleep = create everything, wait 500 ms, then
// PS, CS, VS (a compile done at create, in the background, is then already over).
static int child_game(unsigned seed, const std::wstring& order) {
    if (!make_device()) return 1;
    common();
    g_rt = make_rtv(DXGI_FORMAT_R8G8B8A8_UNORM);
    ID3DBlob *vsb = compile(heavy_vs(seed), "vs_5_0"), *psb = compile(heavy_ps(seed), "ps_5_0");
    if (!vsb || !psb) return 1;
    ID3D11PixelShader* pps[5] = {};
    for (int i = 0; i < 5; ++i)
        if (ID3DBlob* b = compile(kPartnerPs[i], "ps_5_0")) dev->CreatePixelShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &pps[i]), b->Release();
    ID3D11VertexShader* vss[3] = {};
    for (int i = 0; i < 3; ++i)
        if (ID3DBlob* b = compile(kPartnerVs[i], "vs_5_0")) dev->CreateVertexShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &vss[i]), b->Release();
    ID3D11VertexShader* vs = nullptr;
    ID3D11PixelShader* ps = nullptr;
    ID3D11ComputeShader* cs = nullptr;
    auto mk_vs = [&] { dev->CreateVertexShader(vsb->GetBufferPointer(), vsb->GetBufferSize(), nullptr, &vs); };
    auto mk_ps = [&] { dev->CreatePixelShader(psb->GetBufferPointer(), psb->GetBufferSize(), nullptr, &ps); };
    double a, b, c;
    if (order == L"isolated") {
        mk_ps(), b = draw_ps(ps, vss, PsVar{"game"});
        cs = make_cs(seed), c = dispatch(cs, false);
        mk_vs(), a = draw_vs(vs, vsb, pps, VsVar{"game", base_layout()});
    } else {
        mk_vs(), mk_ps(), cs = make_cs(seed);
        if (order == L"sleep") Sleep(500);
        if (order == L"vsfirst") a = draw_vs(vs, vsb, pps, VsVar{"game", base_layout()}), b = draw_ps(ps, vss, PsVar{"game"}), c = dispatch(cs, false);
        else b = draw_ps(ps, vss, PsVar{"game"}), c = dispatch(cs, false), a = draw_vs(vs, vsb, pps, VsVar{"game", base_layout()});
    }
    printf("GAME vs=%.3f ps=%.3f cs=%.3f\n", a, b, c);
    return 0;
}

// ---- the PS's partner VS across processes: a light VS with an input layout (POSITION, TEXCOORD0) before the heavy PS.
// pspair <seed> <warm|game> <vs: l = the light VS, t = a VS without inputs> <layout: a = 32-bit floats, b = 16-bit>
// The game (game) compiles its VS variant first behind a trivial PS, then times the heavy PS's first draw.
static int child_pspair(unsigned seed, const std::wstring& role, const std::wstring& vsk, const std::wstring& lk) {
    if (!make_device()) return 1;
    common();
    g_rt = make_rtv(DXGI_FORMAT_R8G8B8A8_UNORM);
    ID3DBlob* vsb = compile(vsk == L"l" ? "float4 main(float3 p:POSITION,float2 uv:TEXCOORD0,out float2 o:TEXCOORD0):SV_Position{o=uv;return float4(p,1);}"
                                        : kPartnerVs[0], "vs_5_0");
    ID3DBlob *psb = compile(heavy_ps(seed), "ps_5_0"), *tpb = compile("float4 main(float4 p:SV_Position,float2 uv:TEXCOORD0):SV_Target{return uv.xyxy;}", "ps_5_0");
    if (!vsb || !psb || !tpb) return 1;
    ID3D11VertexShader* vs = nullptr;
    ID3D11PixelShader *ps = nullptr, *tp = nullptr;
    dev->CreateVertexShader(vsb->GetBufferPointer(), vsb->GetBufferSize(), nullptr, &vs);
    dev->CreatePixelShader(tpb->GetBufferPointer(), tpb->GetBufferSize(), nullptr, &tp);
    VsVar v{"pair", {}};
    if (vsk == L"l") {
        bool b = lk == L"b";
        v.el = {{"POSITION", 0, b ? DXGI_FORMAT_R16G16B16A16_FLOAT : DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, 0},
                {"TEXCOORD", 0, b ? DXGI_FORMAT_R16G16_FLOAT : DXGI_FORMAT_R32G32_FLOAT, 0, b ? 8u : 12u, 0}};
        v.stride[0] = b ? 12 : 20;
    }
    ID3D11PixelShader* pre[5] = {tp};
    auto draw = [&](ID3D11PixelShader* p) {
        pre[0] = p;
        if (vsk == L"l") return draw_vs(vs, vsb, pre, v, role == L"warm");
        ctx->IASetInputLayout(nullptr), ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        ctx->VSSetShader(vs, nullptr, 0), ctx->PSSetShader(p, nullptr, 0), ctx->OMSetRenderTargets(1, &g_rt, nullptr);
        return timed([&] { role == L"warm" ? ctx->DrawInstancedIndirect(g_args, 0) : ctx->Draw(3, 0); });
    };
    draw(tp);  // the VS variant's own compile
    dev->CreatePixelShader(psb->GetBufferPointer(), psb->GetBufferSize(), nullptr, &ps);
    ID3D11ShaderResourceView* srv = make_srv(DXGI_FORMAT_R8G8B8A8_UNORM);
    ID3D11ShaderResourceView* srvs[4] = {srv, srv, srv, srv};
    ctx->PSSetShaderResources(0, 4, srvs), ctx->PSSetSamplers(0, 1, &g_smp);
    printf("PAIR ps=%.3f\n", draw(ps));
    return 0;
}

// ---- a game's VS
static DXGI_FORMAT fmt32(const Sig& s) {
    static const DXGI_FORMAT f[3][4] = {
        {DXGI_FORMAT_R32_UINT, DXGI_FORMAT_R32G32_UINT, DXGI_FORMAT_R32G32B32_UINT, DXGI_FORMAT_R32G32B32A32_UINT},
        {DXGI_FORMAT_R32_SINT, DXGI_FORMAT_R32G32_SINT, DXGI_FORMAT_R32G32B32_SINT, DXGI_FORMAT_R32G32B32A32_SINT},
        {DXGI_FORMAT_R32_FLOAT, DXGI_FORMAT_R32G32_FLOAT, DXGI_FORMAT_R32G32B32_FLOAT, DXGI_FORMAT_R32G32B32A32_FLOAT}};
    int w = 32 - std::countl_zero((unsigned)s.mask);  // components up to the highest declared one
    return f[std::clamp((int)s.type, 1, 3) - 1][std::clamp(w, 1, 4) - 1];
}
static DXGI_FORMAT fmt_small(const Sig& s) {  // a 4-component 16-bit float / 8-bit uint, what engines pack normals, UVs and indices in
    return s.type == 3 ? DXGI_FORMAT_R16G16B16A16_FLOAT : s.type == 1 ? DXGI_FORMAT_R8G8B8A8_UINT : DXGI_FORMAT_R8G8B8A8_SINT;
}

static int child_real(const wchar_t* path, bool again) {
    std::string code;
    if (FILE* f = _wfopen(path, L"rb")) { char buf[65536]; for (size_t n; (n = fread(buf, 1, sizeof buf, f));) code.append(buf, n); fclose(f); }
    std::vector<Sig> in;
    for (auto& s : isgn(code)) if (!s.sv) in.push_back(s);  // SV_VertexID / SV_InstanceID come from the IA, not a buffer
    if (in.empty() || in.size() > 16 || !make_device()) return printf("# no usable input signature\n"), 1;
    common();
    g_rt = make_rtv(DXGI_FORMAT_R8G8B8A8_UNORM);
    ID3DBlob* vsb = nullptr;
    D3DCreateBlob(code.size(), &vsb);
    memcpy(vsb->GetBufferPointer(), code.data(), code.size());
    ID3D11VertexShader* vs = nullptr;
    double create = ms([&] { dev->CreateVertexShader(code.data(), code.size(), nullptr, &vs); });
    if (!vs) return printf("# the runtime rejects the VS\n"), 1;
    ID3D11PixelShader* ps[5] = {};
    if (ID3DBlob* b = compile(kPartnerPs[4], "ps_5_0")) dev->CreatePixelShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &ps[0]), b->Release();
    // every element 16 bytes apart in slot 0, so a smaller format never moves another element
    VsVar base{"base", {}};
    for (UINT i = 0; i < in.size(); ++i) base.el.push_back({in[i].name, in[i].index, fmt32(in[i]), 0, 16 * i, 0});
    base.stride[0] = 16 * (UINT)in.size();
    std::vector<VsVar> vars = {base};
    vars[0].name = "base_again";
    for (size_t i = 0; i < in.size(); ++i) {
        VsVar v = base;
        v.el[i].f = fmt_small(in[i]);
        v.name = "fmt_" + in[i].name + std::to_string(in[i].index) + (in[i].rw ? "" : "_unread");
        vars.push_back(v);
    }
    printf("REAL create=%.3f elements=%zu\n", create, in.size());
    if (again) std::reverse(vars.begin(), vars.end());
    else printf("REAL v=base ms=%.3f\n", draw_vs(vs, vsb, ps, base, true));  // zero-count indirect: none of its code runs
    for (auto& v : vars) printf("REAL v=%s ms=%.3f\n", v.name.c_str(), draw_vs(vs, vsb, ps, v, true));
    return 0;
}

// ---- orchestrator
static std::string run(const std::wstring& exe, const std::wstring& args) {
    SECURITY_ATTRIBUTES sa = {sizeof sa, nullptr, TRUE};
    HANDLE r = nullptr, w = nullptr;
    if (!CreatePipe(&r, &w, &sa, 0)) return {};
    SetHandleInformation(r, HANDLE_FLAG_INHERIT, 0);
    STARTUPINFOW si = {sizeof si};
    si.dwFlags = STARTF_USESTDHANDLES, si.hStdOutput = w, si.hStdError = w, si.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    PROCESS_INFORMATION pi = {};
    std::wstring cmd = L"\"" + exe + L"\" " + args;
    std::string out;
    if (CreateProcessW(nullptr, cmd.data(), nullptr, nullptr, TRUE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi)) {
        CloseHandle(w), w = nullptr;
        char buf[4096];
        DWORD n;
        while (ReadFile(r, buf, sizeof buf, &n, nullptr) && n) out.append(buf, n);
        WaitForSingleObject(pi.hProcess, INFINITE);
        CloseHandle(pi.hProcess), CloseHandle(pi.hThread);
    }
    if (w) CloseHandle(w);
    CloseHandle(r);
    return out;
}

// Bytes and files of the driver's D3D11 cache folders, read after a child exits (the driver maps the files).
static std::pair<long long, int> cache_size() {
    long long bytes = 0;
    int files = 0;
    wchar_t la[MAX_PATH];
    if (!GetEnvironmentVariableW(L"LOCALAPPDATA", la, MAX_PATH)) return {0, 0};
    for (const wchar_t* sub : {L"\\AMD\\DxCache\\*", L"\\NVIDIA\\DXCache\\*"}) {
        WIN32_FIND_DATAW fd;
        HANDLE h = FindFirstFileW((std::wstring(la) + sub).c_str(), &fd);
        if (h == INVALID_HANDLE_VALUE) continue;
        do if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) bytes += (long long)fd.nFileSizeHigh << 32 | fd.nFileSizeLow, ++files;
        while (FindNextFileW(h, &fd));
        FindClose(h);
    }
    return {bytes, files};
}

static std::string traced(const std::wstring& exe, const std::wstring& args) {
    auto before = cache_size();
    std::string out = run(exe, args);
    auto after = cache_size();
    char buf[128];
    snprintf(buf, sizeof buf, "CACHE delta_bytes=%lld delta_files=%d\n", after.first - before.first, after.second - before.second);
    return out + buf;
}

static double median(std::vector<double> v) { std::sort(v.begin(), v.end()); return v.empty() ? -1 : v[v.size() / 2]; }
static double after(const std::string& s, const char* key) { size_t p = s.find(key); return p == std::string::npos ? -1 : atof(s.c_str() + p + strlen(key)); }

// "<TAG> v=<name> ms=<x>" lines -> name -> ms, in order
static std::vector<std::pair<std::string, double>> rows(const std::string& out, const char* tag) {
    std::vector<std::pair<std::string, double>> r;
    std::string t = std::string(tag) + " v=";
    for (size_t p = 0; (p = out.find(t, p)) != std::string::npos;) {
        size_t s = p + t.size(), e = out.find(' ', s);
        r.emplace_back(out.substr(s, e - s), after(out.substr(e), "ms="));
        p = e;
    }
    return r;
}

static std::wstring g_dir, g_self;
static int g_names = 0;
static std::wstring fresh_exe() {  // a copy under a name no driver cache has seen
    std::wstring exe = g_dir + L"l11_" + std::to_wstring(GetTickCount() % 100000) + L"_" + std::to_wstring(++g_names) + L".exe";
    CopyFileW(g_self.c_str(), exe.c_str(), TRUE);
    return exe;
}

static void table(const char* tag, const std::vector<std::string>& outs1, const std::vector<std::string>& outs2) {
    std::vector<std::string> names;
    std::map<std::string, std::vector<double>> p1, p2;
    for (auto& o : outs1) for (auto& [n, t] : rows(o, tag)) { if (!p1.count(n)) names.push_back(n); p1[n].push_back(t); }
    for (auto& o : outs2) for (auto& [n, t] : rows(o, tag)) p2[n].push_back(t);
    double cold = median(p1["base"]), floor = median(p1["base_again"]);
    printf("  %-24s %10s %10s %8s\n", "variant", "proc1 ms", "proc2 ms", "p1/cold");
    for (auto& n : names) {
        double a = median(p1[n]), b = p2.count(n) ? median(p2[n]) : -1;
        printf("  %-24s %10.2f %10.2f %8.2f\n", n.c_str(), a, b, cold > 0 ? a / cold : -1);
    }
    printf("  (cold = proc1 base %.2f ms, in-process floor = base_again %.2f ms; -1 = layout not created)\n", cold, floor);
}

int wmain(int argc, wchar_t** argv) {
    std::wstring mode = argc > 1 ? argv[1] : L"";
    bool again = argc > 3 && std::wstring(argv[3]) == L"again";
    if (mode == L"vs" && argc > 2) return child_vs((unsigned)_wtoi(argv[2]), again);
    if (mode == L"ps" && argc > 2) return child_ps((unsigned)_wtoi(argv[2]), again);
    if (mode == L"geo" && argc > 2) return child_geo((unsigned)_wtoi(argv[2]), again);
    if (mode == L"pspair" && argc > 5) return child_pspair((unsigned)_wtoi(argv[2]), argv[3], argv[4], argv[5]);
    if (mode == L"warm" && argc > 3) return child_warm((unsigned)_wtoi(argv[2]), argv[3]);
    if (mode == L"game" && argc > 3) return child_game((unsigned)_wtoi(argv[2]), argv[3]);
    if (mode == L"real" && argc > 2) return child_real(argv[2], again);

    wchar_t p[MAX_PATH];
    GetModuleFileNameW(nullptr, p, MAX_PATH);
    g_self = p;
    g_dir = g_self.substr(0, g_self.find_last_of(L'\\') + 1) + L"layout11-" + std::to_wstring(GetCurrentProcessId()) + L"\\";
    if (!CreateDirectoryW(g_dir.c_str(), nullptr)) return printf("can't create %ls\n", g_dir.c_str()), 1;
    unsigned base = GetTickCount() % 1000000;
    int reps = 3;

    if (mode == L"real-dir" && argc > 2) {
        WIN32_FIND_DATAW fd;
        HANDLE h = FindFirstFileW((std::wstring(argv[2]) + L"\\*.bin").c_str(), &fd);
        for (bool ok = h != INVALID_HANDLE_VALUE; ok; ok = FindNextFileW(h, &fd)) {
            std::wstring f = std::wstring(argv[2]) + L"\\" + fd.cFileName, exe = fresh_exe();
            std::string o1 = traced(exe, L"real \"" + f + L"\" x"), o2 = traced(exe, L"real \"" + f + L"\" again");
            printf("=== %ls (%s) ===\n%s", fd.cFileName, o1.substr(0, o1.find('\n')).c_str(), o1.find("REAL create") != std::string::npos ? "" : o1.c_str());
            auto r1 = rows(o1, "REAL"), r2 = rows(o2, "REAL");
            double cold = r1.empty() ? -1 : r1[0].second;
            printf("  create=%.2f  %s", after(o1, "REAL create="), o1.substr(o1.find("CACHE")).c_str());
            for (auto& [n, t] : r1) {
                double t2 = -1;
                for (auto& [n2, x] : r2) if (n2 == n) t2 = x;
                printf("  %-28s p1=%8.2f p2=%8.2f p1/cold=%5.2f\n", n.c_str(), t, t2, cold > 0 ? t / cold : -1);
            }
            DeleteFileW(exe.c_str());
        }
        if (h != INVALID_HANDLE_VALUE) FindClose(h);
        RemoveDirectoryW(g_dir.c_str());
        return 0;
    }

    std::wstring only = mode == L"only" && argc > 2 ? argv[2] : L"";  // one section: vs, ps, geo or warm
    auto want = [&](const wchar_t* s) { return only.empty() || only == s; };

    if (want(L"geo")) {
        printf("=== GS and HS+DS: one heavy GS, one heavy HS+DS pair, a draw per partner VS/PS (proc1), then reverse (proc2) ===\n");
        std::vector<std::string> o1, o2;
        for (int i = 0; i < reps; ++i) {
            std::wstring exe = fresh_exe(), s = std::to_wstring(base + i * 7 + 301);
            o1.push_back(traced(exe, L"geo " + s + L" x")), o2.push_back(traced(exe, L"geo " + s + L" again"));
            DeleteFileW(exe.c_str());
        }
        table("GEO", o1, o2);
    }
    if (want(L"vs")) {
    printf("=== VS: one heavy VS, a draw per variant (proc1), then the same name again in reverse order (proc2) ===\n");
    std::vector<std::string> v1, v2;
    for (int i = 0; i < reps; ++i) {
        std::wstring exe = fresh_exe(), s = std::to_wstring(base + i * 7 + 1);
        v1.push_back(traced(exe, L"vs " + s + L" x")), v2.push_back(traced(exe, L"vs " + s + L" again"));
        printf("  rep %d: %s proc1 %s  proc2 %s", i, v1.back().substr(0, v1.back().find('\n')).c_str(),
               v1.back().substr(v1.back().find("CACHE")).c_str(), v2.back().substr(v2.back().find("CACHE")).c_str());
        DeleteFileW(exe.c_str());
    }
    printf("%s", v1[0].substr(v1[0].find("# isgn"), v1[0].find('\n', v1[0].find("# isgn")) - v1[0].find("# isgn") + 1).c_str());
    printf("  VS create (proc1) median %.2f ms\n", median([&] { std::vector<double> c; for (auto& o : v1) c.push_back(after(o, "VS create=")); return c; }()));
    table("VS", v1, v2);
    }

    if (want(L"ps")) {
    printf("=== PS: one heavy PS behind a trivial VS, a draw per variant (proc1), then reverse (proc2) ===\n");
    std::vector<std::string> q1, q2;
    for (int i = 0; i < reps; ++i) {
        std::wstring exe = fresh_exe(), s = std::to_wstring(base + i * 7 + 101);
        q1.push_back(traced(exe, L"ps " + s + L" x")), q2.push_back(traced(exe, L"ps " + s + L" again"));
        printf("  rep %d: proc1 %s  proc2 %s", i, q1.back().substr(q1.back().find("CACHE")).c_str(), q2.back().substr(q2.back().find("CACHE")).c_str());
        DeleteFileW(exe.c_str());
    }
    printf("  PS create (proc1) median %.2f ms\n", median([&] { std::vector<double> c; for (auto& o : q1) c.push_back(after(o, "PS create=")); return c; }()));
    table("PS", q1, q2);
    }

    if (want(L"pspair")) {
        printf("=== WARM -> GAME, PS behind a VS with an input layout: the game draws the heavy PS behind the light VS, layout b ===\n");
        const wchar_t* kinds[] = {L"cold", L"l b", L"l a", L"t a"};  // warm: same VS + layout, same VS + other layout, a VS without inputs
        std::map<std::wstring, std::vector<double>> t;
        for (int i = 0; i < reps; ++i) {
            std::wstring s = std::to_wstring(base + i * 7 + 501);
            for (const wchar_t* kind : kinds) {
                std::wstring exe = fresh_exe();
                if (kind != std::wstring(L"cold")) traced(exe, L"pspair " + s + L" warm " + kind);
                std::string o = traced(exe, L"pspair " + s + L" game l b");
                t[kind].push_back(after(o, "PAIR ps="));
                DeleteFileW(exe.c_str());
            }
        }
        for (const wchar_t* kind : kinds) {
            printf("  warm %-4ls game ps:", kind);
            for (double x : t[kind]) printf(" %7.2f", x);
            printf("  median %7.2f\n", median(t[kind]));
        }
    }
    if (want(L"warmps")) {
        printf("=== WARM -> GAME, PS and CS: the warm's PS state differs from the game's (RGBA8, no depth, partner VS 0) in one thing; game isolated ===\n");
        const wchar_t* kinds[] = {L"cold", L"exact", L"gen", L"depth", L"vs2", L"rt=87", L"rt=29", L"rt=10", L"rt=26", L"rt=24", L"rt=11", L"rt=2", L"rt=34", L"rt=41", L"rt=61"};
        std::map<std::wstring, std::vector<double>> ps, cs;
        for (int i = 0; i < reps; ++i) {
            std::wstring s = std::to_wstring(base + i * 7 + 401);
            for (const wchar_t* kind : kinds) {
                std::wstring exe = fresh_exe();
                if (kind != std::wstring(L"cold")) traced(exe, L"warm " + s + L" " + kind);
                std::string o = traced(exe, L"game " + s + L" isolated");
                ps[kind].push_back(after(o.substr(o.find("GAME")), "ps=")), cs[kind].push_back(after(o.substr(o.find("GAME")), "cs="));
                DeleteFileW(exe.c_str());
            }
        }
        for (const wchar_t* kind : kinds) {
            printf("  warm %-6ls game ps:", kind);
            for (double t : ps[kind]) printf(" %7.2f", t);
            printf("  median %7.2f   cs median %6.2f\n", median(ps[kind]), median(cs[kind]));
        }
        printf("  (rt=<DXGI_FORMAT>: 87 BGRA8, 29 RGBA8 sRGB, 10 RGBA16F, 26 R11G11B10F, 24 RGB10A2, 11 RGBA16 UNORM, 2 RGBA32F, 34 RG16F, 41 R32F, 61 R8)\n");
    }
    if (want(L"warm")) {
    printf("=== WARM -> GAME: a warm process, then the game's first use under the same name (fresh name = cold) ===\n");
    std::map<std::string, std::vector<double>> g;
    const wchar_t* orders[] = {L"vsfirst", L"isolated", L"sleep"};
    for (int i = 0; i < reps; ++i) {
        std::wstring s = std::to_wstring(base + i * 7 + 201);
        for (const wchar_t* order : orders)
            for (const wchar_t* kind : {L"cold", L"gen", L"exact", L"direct"}) {
                std::wstring exe = fresh_exe();
                std::string w = kind == std::wstring(L"cold") ? "" : traced(exe, L"warm " + s + L" " + kind);
                std::string o = traced(exe, L"game " + s + L" " + order);
                char k[40];
                snprintf(k, sizeof k, "%ls %ls", order, kind);
                printf("  rep %d %-15s warm: %s game: %s", i, k, w.empty() ? "-\n" : w.substr(w.find("WARM")).c_str(), o.substr(o.find("GAME")).c_str());
                for (const char* f : {"vs=", "ps=", "cs="}) g[std::string(k) + "_" + f].push_back(after(o.substr(o.find("GAME")), f));
                DeleteFileW(exe.c_str());
            }
    }
    for (const wchar_t* order : orders)
        for (const wchar_t* kind : {L"cold", L"gen", L"exact", L"direct"}) {
            char k[40];
            snprintf(k, sizeof k, "%ls %ls", order, kind);
            printf("  MEDIAN game (%-15s) vs=%.2f ps=%.2f cs=%.2f\n", k, median(g[std::string(k) + "_vs="]), median(g[std::string(k) + "_ps="]), median(g[std::string(k) + "_cs="]));
        }
    }
    RemoveDirectoryW(g_dir.c_str());
    return 0;
}
