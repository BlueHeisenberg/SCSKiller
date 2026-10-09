// D3D11 warm: make the driver compile single shaders (scskiller_gen.db '1' items, see proxy.cpp) by using each once.
// Measured (probe11.cpp, NVIDIA 610.88): CreateXShader is lazy, the compile happens at the first draw/dispatch that uses
// the shader, is cached per shader (not per VS+PS pair) and doesn't depend on blend/RT format/input layout/depth/MSAA.
// So an item = one draw on a 1x1 target with a generated partner stage (a trivial PS for a VS, a pass-through VS whose
// outputs line up with a PS's or GS's inputs) and dummy resources in every slot the bytecode declares, or one dispatch.
// A '2' item is a HS and a DS of one map drawn together (warm11_pair): neither can be drawn alone.
// Everything is read from the SHEX/SHDR declarations and the signatures: UE strips RDEF.
// The draw/dispatch is indirect with zero counts from a GPU buffer: the driver can't know it's empty, so it compiles
// the shaders, but no game shader code ever runs. Running it did hang: with zeroed cbuffers, loops like
// "for (i = 0; i < (n - 1) >> 1; ++i)" in Orcs Must Die 3's compute shaders go 2^31 times, and with compute preemption
// Windows never resets the GPU for it (no TDR), so the event query just never signalled.
#define NOMINMAX
#include <windows.h>
#include <d3d11_1.h>
#include <d3dcompiler.h>
#include <dxgi1_4.h>
#include <wrl/client.h>
#include <algorithm>
#include <atomic>
#include <bit>
#include <cstdio>
#include <map>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "d3dcompiler.lib")
#pragma comment(lib, "dxgi.lib")

using Microsoft::WRL::ComPtr;
void logf(const char* fmt, ...);  // proxy.cpp

static uint32_t u32(std::string_view s, size_t o) { uint32_t v = 0; if (o + 4 <= s.size()) memcpy(&v, s.data() + o, 4); return v; }

static std::string_view chunk(std::string_view c, const char* fourcc) {
    if (c.size() < 32 || c.substr(0, 4) != "DXBC") return {};
    for (uint32_t i = 0, n = u32(c, 28); i < n && i < 64; ++i) {
        size_t o = u32(c, 32 + 4 * i), len = u32(c, o + 4);
        if (o + 8 + len <= c.size() && !memcmp(c.data() + o, fourcc, 4)) return c.substr(o + 8, len);
    }
    return {};
}

struct Sig { std::string name; uint32_t index, sv, type, reg, minp; uint8_t mask; };  // sv: D3D10_SB_NAME, type: 1 uint 2 int 3 float
enum { SV_POSITION = 1, SV_RENDER_TARGET_ARRAY_INDEX = 4, SV_VIEWPORT_ARRAY_INDEX = 5, SV_VERTEX_ID = 6, SV_INSTANCE_ID = 8, SV_SAMPLE_INDEX = 10 };

// ISGN/OSGN: 24-byte elements; ISG1/OSG1: + leading stream and trailing min precision (32); OSG5: + stream (28).
static std::vector<Sig> signature(std::string_view c, bool in, std::string* raw = nullptr) {
    for (const char* k : in ? std::initializer_list<const char*>{"ISG1", "ISGN"} : std::initializer_list<const char*>{"OSG1", "OSG5", "OSGN"}) {
        std::string_view d = chunk(c, k);
        if (d.empty()) continue;
        size_t size = k[3] == '1' ? 32 : k[3] == '5' ? 28 : 24, lead = size == 24 ? 0 : 4;
        std::vector<Sig> v;
        for (uint32_t e = 0, n = u32(d, 0); e < n && e < 128; ++e) {
            size_t b = u32(d, 4) + e * size + lead, name = u32(d, b);
            if (b + size - lead > d.size() || name >= d.size()) break;
            v.push_back({std::string(d.data() + name, strnlen(d.data() + name, d.size() - name)), u32(d, b + 4), u32(d, b + 8),
                         u32(d, b + 12), u32(d, b + 16), size == 32 ? u32(d, b + 24) : 0, (uint8_t)d[b + 20]});
        }
        if (raw) *raw = std::string(k, 4) + std::string(d);
        return v;
    }
    return {};
}

struct Slot { uint32_t op, reg, dim, ret, extra; };  // extra: structure stride, or sample count of a Texture2DMS
struct Shader {
    uint32_t type = ~0u;  // D3D10_SB program type: 0 PS, 1 VS, 2 GS, 3 HS, 4 DS, 5 CS
    std::vector<Sig> in, out;
    std::string in_raw;   // the input signature chunk: with interp, all a pass-through partner VS depends on
    std::vector<Slot> srv, uav;
    uint32_t cmp_samplers = 0, gs_prim = 0;
    uint32_t cp_in = 0, cp_out = 0, domain = 0;  // HS/DS: control points per input / output patch, tessellator domain (1 isoline 2 tri 3 quad)
    uint8_t interp[32] = {};  // PS: D3D10_SB_INTERPOLATION_MODE per input register (0 = not declared)
    bool interfaces = false;
};
enum { OP_CUSTOMDATA = 0x35, OP_DCL_RESOURCE = 0x58, OP_DCL_SAMPLER = 0x5A, OP_DCL_GS_INPUT = 0x5D, OP_DCL_FUNCTION_BODY = 0x90,
       OP_DCL_INPUT_PS = 0x62, OP_DCL_INPUT_PS_SIV = 0x64, OP_DCL_INTERFACE = 0x92, OP_DCL_INPUT_CONTROL_POINT_COUNT = 0x93,
       OP_DCL_OUTPUT_CONTROL_POINT_COUNT = 0x94, OP_DCL_TESS_DOMAIN = 0x95, OP_DCL_UAV_TYPED = 0x9C, OP_DCL_UAV_RAW = 0x9D, OP_DCL_UAV_STRUCTURED = 0x9E,
       OP_DCL_RESOURCE_RAW = 0xA1, OP_DCL_RESOURCE_STRUCTURED = 0xA2, OP_IMM_ATOMIC_ALLOC = 0xB2, OP_IMM_ATOMIC_CONSUME = 0xB3 };

static bool parse(std::string_view c, Shader& s) {
    std::string_view shex = chunk(c, "SHEX");
    if (shex.empty()) shex = chunk(c, "SHDR");
    auto t = (const uint32_t*)shex.data();
    size_t nt = shex.size() / 4;
    if (nt < 2) return false;
    s.type = t[0] >> 16;
    for (size_t p = 2; p < nt;) {
        uint32_t op = t[p] & 0x7FF;
        size_t n = op == OP_CUSTOMDATA ? (p + 1 < nt ? t[p + 1] : 0) : (t[p] >> 24) & 0x7F;
        if (!n || p + n > nt) break;
        size_t q = p + 1, end = p + n;
        if (t[p] >> 31) while (q < end && t[q++] >> 31) {}  // extended opcode tokens
        if (q < end && t[q++] >> 31) while (q < end && t[q++] >> 31) {}  // operand token + extended operand tokens
        uint32_t reg = q < end ? t[q] : ~0u, next = q + 1 < end ? t[q + 1] : 0, ctl = t[p] >> 11;
        switch (op) {  // SM5.0 declarations: the operand's first index is the register
        case OP_DCL_RESOURCE: s.srv.push_back({op, reg, ctl & 0x1F, next & 0xF, (ctl >> 5) & 0x7F}); break;
        case OP_DCL_RESOURCE_RAW: s.srv.push_back({op, reg, 0, 0, 0}); break;
        case OP_DCL_RESOURCE_STRUCTURED: s.srv.push_back({op, reg, 0, 0, next}); break;
        case OP_DCL_UAV_TYPED: s.uav.push_back({op, reg, ctl & 0x1F, next & 0xF, 0}); break;
        case OP_DCL_UAV_RAW: s.uav.push_back({op, reg, 0, 0, 0}); break;
        case OP_DCL_UAV_STRUCTURED: s.uav.push_back({op, reg, (ctl >> 12) & 1 /*IncrementCounter*/, 0, next}); break;
        case OP_DCL_SAMPLER: if ((ctl & 0xF) == 1 && reg < 16) s.cmp_samplers |= 1u << reg; break;
        case OP_DCL_GS_INPUT: s.gs_prim = ctl & 0x3F; break;
        case OP_DCL_INPUT_CONTROL_POINT_COUNT: if (!s.cp_in) s.cp_in = ctl & 0x3F; break;  // the count sits in the opcode token, bits 11-16
        case OP_DCL_OUTPUT_CONTROL_POINT_COUNT: s.cp_out = ctl & 0x3F; break;
        case OP_DCL_TESS_DOMAIN: s.domain = ctl & 0x3; break;
        case OP_DCL_INPUT_PS: case OP_DCL_INPUT_PS_SIV: if (reg < 32) s.interp[reg] = ctl & 0xF; break;
        case OP_IMM_ATOMIC_ALLOC: case OP_IMM_ATOMIC_CONSUME:  // Append/Consume: the other structured UAVs need an append counter
            for (auto& u : s.uav) if (u.op == OP_DCL_UAV_STRUCTURED && !u.dim) u.dim = 2;  // ponytail: all of them, not just the one used
            break;
        default: s.interfaces |= op >= OP_DCL_FUNCTION_BODY && op <= OP_DCL_INTERFACE;
        }
        p = end;
    }
    s.in = signature(c, true, &s.in_raw);
    s.out = signature(c, false);
    return true;
}

static bool linked(const Sig& e) { return e.sv <= 5; }  // user, position, clip/cull distance, RT/viewport index; the rest the rasterizer makes

static std::string hlsl_type(const Sig& e, int w) {
    const char* t = e.minp == 1 ? "min16float" : e.minp == 2 ? "min10float" : e.minp == 4 ? "min16int" : e.minp == 5 ? "min16uint"
                  : e.type == 1 ? "uint" : e.type == 2 ? "int" : "float";
    return w > 1 ? t + std::to_string(w) : t;
}

// A VS writing exactly the consumer's inputs (same semantics, registers, components, types). fxc packs VS outputs
// into the first free space of a register with the same interpolation mode (ints default to constant), in declaration
// order: so every register is declared in order, in the consumer's mode, and filled up with padding.
static std::string partner_vs(const Shader& s) {
    static const char* modes[] = {"", "nointerpolation ", "", "centroid ", "noperspective ", "noperspective centroid ", "sample ", "noperspective sample "};
    std::string f;
    int pads = 0, fields = 0;
    bool pos = false;
    uint32_t regs = 0;
    for (auto& e : s.in) if (linked(e) && e.reg < 32) regs = std::max(regs, e.reg + 1);
    for (uint32_t r = 0; r < regs; ++r) {
        std::vector<const Sig*> es;
        for (auto& e : s.in) if (linked(e) && e.reg == r && e.mask) es.push_back(&e);
        std::sort(es.begin(), es.end(), [](auto a, auto b) { return std::countr_zero(a->mask) < std::countr_zero(b->mask); });
        uint32_t mode = s.interp[r] < 8 ? s.interp[r] : 0;  // undeclared (unused, or a GS input): ints force constant
        if (!mode) mode = std::any_of(es.begin(), es.end(), [](auto e) { return e->type != 3; }) ? 1 : 2;
        std::string m = modes[mode];
        auto pad = [&](int w) { f += m + "float" + (w > 1 ? std::to_string(w) : "") + " p" + std::to_string(pads) + ":SCSKPAD" + std::to_string(pads) + ";", ++pads; };
        int c = 0;
        for (auto* e : es) {
            int start = std::countr_zero(e->mask), w = std::popcount(e->mask);
            if (start < c) return {};  // overlapping elements: not a signature fxc produced
            if (start > c) pad(start - c);
            if (e->sv == SV_POSITION) f += "float4 pos:SV_Position;", pos = true, w = 4 - start;
            else {
                // These system-value semantics have no numeric suffix in HLSL. Appending the DXBC
                // signature index turns them into user semantics, so the partner VS no longer links.
                std::string semantic = e->name;
                if (e->sv != SV_RENDER_TARGET_ARRAY_INDEX && e->sv != SV_VIEWPORT_ARRAY_INDEX) semantic += std::to_string(e->index);
                f += (e->sv ? "" : m) + hlsl_type(*e, w) + " e" + std::to_string(fields++) + ":" + semantic + ";";
            }
            c = start + w;
        }
        if (c < 4) pad(4 - c);
    }
    // only a PS needs a position from it; a HS or GS may already take all 32 registers
    if (!pos && (s.type == 0 || f.empty())) f += "float4 pos:SV_Position;", pos = true;
    return "struct O{" + f + "};O main(uint i:SV_VertexID){O o=(O)0;" + (pos ? "o.pos=float4(i==1?3:-1,i==2?3:-1,0.5,1);" : "") + "return o;}";
}

static bool iequal(const std::string& a, const std::string& b) { return _stricmp(a.c_str(), b.c_str()) == 0; }

// Every consumer input has its element at the same register, with the components, type and precision.
static bool lines_up(const std::vector<Sig>& in, const std::vector<Sig>& out) {
    for (auto& e : in) {
        if (!linked(e)) continue;
        bool ok = false;
        for (auto& o : out)
            ok |= o.reg == e.reg && o.index == e.index && iequal(o.name, e.name) && (o.mask & e.mask) == e.mask && o.type == e.type && o.minp == e.minp;
        if (!ok) return false;
    }
    return true;
}

static ComPtr<ID3DBlob> compile(const std::string& src, const char* target, std::string* err = nullptr) {
    ComPtr<ID3DBlob> code, msg;
    D3DCompile(src.data(), src.size(), "scskiller", nullptr, nullptr, "main", target, 0, 0, &code, &msg);
    if (msg && err) *err = std::string((const char*)msg->GetBufferPointer(), msg->GetBufferSize());
    return code;
}

struct Dev11 {
    ComPtr<ID3D11Device> d;
    ComPtr<ID3D11DeviceContext> c;
    ComPtr<ID3D11InfoQueue> iq;
    ComPtr<ID3D11Buffer> cb, vb, args;  // args: zeroed DrawInstancedIndirect / DispatchIndirect arguments
    ComPtr<ID3D11SamplerState> smp, cmp;
    ComPtr<ID3D11DepthStencilView> dsv;
    ComPtr<ID3D11DepthStencilState> depth_on, depth_off;
    ComPtr<ID3D11RasterizerState> rs;
    ComPtr<ID3D11Query> q;
    ComPtr<ID3D11PixelShader> null_ps;
    UINT uav_slots = 8;
    std::unordered_map<uint64_t, ComPtr<ID3D11View>> views;            // dummy SRVs/UAVs/RTVs by what the slot declares
    std::unordered_map<std::string, ComPtr<ID3D11VertexShader>> partner;  // by consumer input signature; null = can't build
    std::map<std::pair<int, UINT>, std::pair<uint64_t, std::string>> msgs;  // debug layer: (severity, id) -> count, first text
};

static const UINT kBuf = 4096, kElems = 1024;  // dummy buffers: 4 KB, 1024 four-byte elements

static DXGI_FORMAT view_format(uint32_t ret) {  // D3D10_SB_RESOURCE_RETURN_TYPE: 1 unorm 2 snorm 3 sint 4 uint 5 float
    return ret == 1 ? DXGI_FORMAT_R8G8B8A8_UNORM : ret == 2 ? DXGI_FORMAT_R8G8B8A8_SNORM : ret == 3 ? DXGI_FORMAT_R32_SINT
         : ret == 4 ? DXGI_FORMAT_R32_UINT : DXGI_FORMAT_R32_FLOAT;
}

static ComPtr<ID3D11Buffer> buffer(ID3D11Device* d, UINT bind, UINT misc = 0, UINT stride = 0) {
    std::vector<char> zero(stride ? stride * 16 : kBuf);
    D3D11_BUFFER_DESC bd = {(UINT)zero.size(), D3D11_USAGE_DEFAULT, bind, 0, misc, stride};
    D3D11_SUBRESOURCE_DATA init = {zero.data()};
    ComPtr<ID3D11Buffer> b;
    d->CreateBuffer(&bd, &init, &b);
    return b;
}

// SB resource dimension: 1 buffer 2 1D 3 2D 4 2DMS 5 3D 6 cube 7 1D array 8 2D array 9 2DMS array 10 cube array
static ComPtr<ID3D11Resource> texture(ID3D11Device* d, uint32_t dim, DXGI_FORMAT f, UINT bind, UINT samples) {
    ComPtr<ID3D11Resource> r;
    if (dim == 2 || dim == 7) {
        D3D11_TEXTURE1D_DESC td = {1, 1, 1, f, D3D11_USAGE_DEFAULT, bind};
        d->CreateTexture1D(&td, nullptr, (ID3D11Texture1D**)r.GetAddressOf());
    } else if (dim == 5) {
        D3D11_TEXTURE3D_DESC td = {1, 1, 1, 1, f, D3D11_USAGE_DEFAULT, bind};
        d->CreateTexture3D(&td, nullptr, (ID3D11Texture3D**)r.GetAddressOf());
    } else {
        bool cube = dim == 6 || dim == 10, ms = dim == 4 || dim == 9;
        D3D11_TEXTURE2D_DESC td = {1, 1, 1, cube ? 6u : 1u, f, {ms ? samples : 1, 0}, D3D11_USAGE_DEFAULT,
                                   bind | (ms ? D3D11_BIND_RENDER_TARGET : 0), 0, cube ? (UINT)D3D11_RESOURCE_MISC_TEXTURECUBE : 0};
        d->CreateTexture2D(&td, nullptr, (ID3D11Texture2D**)r.GetAddressOf());
    }
    return r;
}

static ID3D11ShaderResourceView* srv(Dev11& v, const Slot& s) {
    uint32_t samples = s.dim == 4 || s.dim == 9 ? std::clamp(s.extra, 1u, 32u) : 0;  // Texture2DMS<T> without a count: 1 sample
    uint64_t key = (uint64_t)s.op << 56 | (uint64_t)s.dim << 48 | (uint64_t)s.ret << 40 | (s.op == OP_DCL_RESOURCE_STRUCTURED ? s.extra : samples);
    auto& view = v.views[key];
    if (view) return (ID3D11ShaderResourceView*)view.Get();
    D3D11_SHADER_RESOURCE_VIEW_DESC vd = {view_format(s.ret)};
    ComPtr<ID3D11Resource> r;
    if (s.op == OP_DCL_RESOURCE_RAW) {
        r = buffer(v.d.Get(), D3D11_BIND_SHADER_RESOURCE, D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS);
        vd.Format = DXGI_FORMAT_R32_TYPELESS, vd.ViewDimension = D3D11_SRV_DIMENSION_BUFFEREX, vd.BufferEx = {0, kElems, D3D11_BUFFEREX_SRV_FLAG_RAW};
    } else if (s.op == OP_DCL_RESOURCE_STRUCTURED) {
        if (!s.extra || s.extra > 2048) return nullptr;
        r = buffer(v.d.Get(), D3D11_BIND_SHADER_RESOURCE, D3D11_RESOURCE_MISC_BUFFER_STRUCTURED, s.extra);
        vd.Format = DXGI_FORMAT_UNKNOWN, vd.ViewDimension = D3D11_SRV_DIMENSION_BUFFER, vd.Buffer = {0, 16};
    } else if (s.dim == 1) {
        r = buffer(v.d.Get(), D3D11_BIND_SHADER_RESOURCE);
        vd.ViewDimension = D3D11_SRV_DIMENSION_BUFFER, vd.Buffer = {0, kElems};
    } else if (s.dim >= 2 && s.dim <= 10) {
        static const D3D11_SRV_DIMENSION dims[] = {D3D11_SRV_DIMENSION_TEXTURE1D, D3D11_SRV_DIMENSION_TEXTURE2D, D3D11_SRV_DIMENSION_TEXTURE2DMS,
            D3D11_SRV_DIMENSION_TEXTURE3D, D3D11_SRV_DIMENSION_TEXTURECUBE, D3D11_SRV_DIMENSION_TEXTURE1DARRAY,
            D3D11_SRV_DIMENSION_TEXTURE2DARRAY, D3D11_SRV_DIMENSION_TEXTURE2DMSARRAY, D3D11_SRV_DIMENSION_TEXTURECUBEARRAY};
        r = texture(v.d.Get(), s.dim, vd.Format, D3D11_BIND_SHADER_RESOURCE, samples);
        vd.ViewDimension = dims[s.dim - 2];
        vd.Texture2DArray = {0, 1, 0, 1};  // every texture view's union starts {mip 0, 1 mip, slice 0, 1 slice/cube}; 2DMS ignores it
        if (s.dim == 9) vd.Texture2DMSArray = {0, 1};
    }
    ComPtr<ID3D11ShaderResourceView> out;
    if (r) v.d->CreateShaderResourceView(r.Get(), &vd, &out);
    view = out;
    return out.Get();
}

static ID3D11UnorderedAccessView* uav(Dev11& v, const Slot& s, bool cs) {  // one per slot and pipeline: never bound twice at once
    uint64_t key = 1ull << 63 | (uint64_t)s.op << 48 | (uint64_t)s.dim << 40 | (uint64_t)s.ret << 32 | s.extra << 8 | s.reg << 1 | (uint64_t)cs;
    auto& view = v.views[key];
    if (view) return (ID3D11UnorderedAccessView*)view.Get();
    D3D11_UNORDERED_ACCESS_VIEW_DESC vd = {view_format(s.ret)};
    ComPtr<ID3D11Resource> r;
    if (s.op == OP_DCL_UAV_RAW) {
        r = buffer(v.d.Get(), D3D11_BIND_UNORDERED_ACCESS, D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS);
        vd.Format = DXGI_FORMAT_R32_TYPELESS, vd.ViewDimension = D3D11_UAV_DIMENSION_BUFFER, vd.Buffer = {0, kElems, D3D11_BUFFER_UAV_FLAG_RAW};
    } else if (s.op == OP_DCL_UAV_STRUCTURED) {
        if (!s.extra || s.extra > 2048) return nullptr;
        r = buffer(v.d.Get(), D3D11_BIND_UNORDERED_ACCESS, D3D11_RESOURCE_MISC_BUFFER_STRUCTURED, s.extra);
        vd.Format = DXGI_FORMAT_UNKNOWN, vd.ViewDimension = D3D11_UAV_DIMENSION_BUFFER, vd.Buffer = {0, 16, s.dim == 1 ? (UINT)D3D11_BUFFER_UAV_FLAG_COUNTER : s.dim == 2 ? (UINT)D3D11_BUFFER_UAV_FLAG_APPEND : 0};
    } else if (s.dim == 1) {
        r = buffer(v.d.Get(), D3D11_BIND_UNORDERED_ACCESS);
        vd.ViewDimension = D3D11_UAV_DIMENSION_BUFFER, vd.Buffer = {0, kElems};
    } else if (s.dim == 2 || s.dim == 3 || s.dim == 5 || s.dim == 7 || s.dim == 8) {
        r = texture(v.d.Get(), s.dim, vd.Format, D3D11_BIND_UNORDERED_ACCESS, 1);
        vd.ViewDimension = s.dim == 2 ? D3D11_UAV_DIMENSION_TEXTURE1D : s.dim == 3 ? D3D11_UAV_DIMENSION_TEXTURE2D : s.dim == 5 ? D3D11_UAV_DIMENSION_TEXTURE3D
                         : s.dim == 7 ? D3D11_UAV_DIMENSION_TEXTURE1DARRAY : D3D11_UAV_DIMENSION_TEXTURE2DARRAY;
        vd.Texture3D = {0, 0, 1};  // {mip 0, first slice 0, 1 slice} in every texture UAV's union
    }
    ComPtr<ID3D11UnorderedAccessView> out;
    if (r) v.d->CreateUnorderedAccessView(r.Get(), &vd, &out);
    view = out;
    return out.Get();
}

static ID3D11RenderTargetView* rtv(Dev11& v, uint32_t slot, uint32_t type) {  // type: 1 uint 2 int 3 float
    auto& view = v.views[2ull << 62 | type << 8 | slot];
    if (!view) {
        DXGI_FORMAT f = type == 1 ? DXGI_FORMAT_R32G32B32A32_UINT : type == 2 ? DXGI_FORMAT_R32G32B32A32_SINT : DXGI_FORMAT_R16G16B16A16_FLOAT;
        ComPtr<ID3D11Resource> r = texture(v.d.Get(), 3, f, D3D11_BIND_RENDER_TARGET, 1);
        ComPtr<ID3D11RenderTargetView> out;
        if (r) v.d->CreateRenderTargetView(r.Get(), nullptr, &out);
        view = out;
    }
    return (ID3D11RenderTargetView*)view.Get();
}

Dev11* warm11_open(LUID luid, bool debug) {
    auto v = new Dev11;
    ComPtr<IDXGIFactory4> f;
    ComPtr<IDXGIAdapter> a;
    D3D_FEATURE_LEVEL fls[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0}, fl;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&f))) || FAILED(f->EnumAdapterByLuid(luid, IID_PPV_ARGS(&a)))) return delete v, nullptr;
    HRESULT hr = D3D11CreateDevice(a.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, debug ? D3D11_CREATE_DEVICE_DEBUG : 0, fls, 2, D3D11_SDK_VERSION, &v->d, &fl, &v->c);
    if (FAILED(hr) && debug) {
        logf("warm11: no D3D11 debug layer (hr=0x%08x; install Graphics Tools), continuing without", (unsigned)hr);
        hr = D3D11CreateDevice(a.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, fls, 2, D3D11_SDK_VERSION, &v->d, &fl, &v->c);
    }
    if (FAILED(hr)) return logf("warm11: D3D11CreateDevice failed hr=0x%08x", (unsigned)hr), delete v, nullptr;
    v->uav_slots = fl >= D3D_FEATURE_LEVEL_11_1 ? D3D11_1_UAV_SLOT_COUNT : D3D11_PS_CS_UAV_REGISTER_COUNT;
    if (debug && SUCCEEDED(v->d.As(&v->iq))) v->iq->SetMessageCountLimit(4096);
    auto d = v->d.Get();
    std::vector<char> zero(65536);  // a constant buffer's maximum (4096 float4): big enough for any cbuffer, in all 14 slots
    D3D11_BUFFER_DESC cbd = {65536, D3D11_USAGE_IMMUTABLE, D3D11_BIND_CONSTANT_BUFFER};
    D3D11_SUBRESOURCE_DATA init = {zero.data()};
    d->CreateBuffer(&cbd, &init, &v->cb);
    D3D11_BUFFER_DESC vbd = {32 * 16 * 3, D3D11_USAGE_IMMUTABLE, D3D11_BIND_VERTEX_BUFFER};  // 3 vertices x 32 float4 attributes
    d->CreateBuffer(&vbd, &init, &v->vb);
    D3D11_BUFFER_DESC abd = {32, D3D11_USAGE_DEFAULT, 0, 0, D3D11_RESOURCE_MISC_DRAWINDIRECT_ARGS};
    d->CreateBuffer(&abd, &init, &v->args);
    D3D11_SAMPLER_DESC sd = {D3D11_FILTER_MIN_MAG_MIP_LINEAR, D3D11_TEXTURE_ADDRESS_WRAP, D3D11_TEXTURE_ADDRESS_WRAP, D3D11_TEXTURE_ADDRESS_WRAP, 0, 1,
                             D3D11_COMPARISON_NEVER, {}, 0, D3D11_FLOAT32_MAX};
    d->CreateSamplerState(&sd, &v->smp);
    sd.Filter = D3D11_FILTER_COMPARISON_MIN_MAG_MIP_LINEAR, sd.ComparisonFunc = D3D11_COMPARISON_LESS_EQUAL;
    d->CreateSamplerState(&sd, &v->cmp);
    ComPtr<ID3D11Resource> ds = texture(d, 3, DXGI_FORMAT_D24_UNORM_S8_UINT, D3D11_BIND_DEPTH_STENCIL, 1);
    if (ds) d->CreateDepthStencilView(ds.Get(), nullptr, &v->dsv);
    D3D11_DEPTH_STENCIL_DESC dd = {TRUE, D3D11_DEPTH_WRITE_MASK_ALL, D3D11_COMPARISON_ALWAYS};
    d->CreateDepthStencilState(&dd, &v->depth_on);
    dd.DepthEnable = FALSE;
    d->CreateDepthStencilState(&dd, &v->depth_off);
    D3D11_RASTERIZER_DESC rd = {D3D11_FILL_SOLID, D3D11_CULL_NONE};
    rd.DepthClipEnable = TRUE;
    d->CreateRasterizerState(&rd, &v->rs);
    D3D11_QUERY_DESC qd = {D3D11_QUERY_EVENT};
    d->CreateQuery(&qd, &v->q);
    if (auto ps = compile("float4 main():SV_Target{return 0;}", "ps_5_0")) d->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), nullptr, &v->null_ps);
    if (!v->cb || !v->vb || !v->args || !v->smp || !v->cmp || !v->dsv || !v->depth_on || !v->depth_off || !v->rs || !v->q || !v->null_ps)
        return logf("warm11: creating the dummy resources failed"), delete v, nullptr;
    D3D11_VIEWPORT vp = {0, 0, 1, 1, 0, 1};
    v->c->RSSetViewports(1, &vp);
    v->c->RSSetState(v->rs.Get());
    return v;
}

static ID3D11VertexShader* partner(Dev11& v, const Shader& s, const char** why) {
    auto [it, fresh] = v.partner.try_emplace(s.in_raw + std::string((const char*)s.interp, sizeof s.interp) + (char)s.type);
    if (fresh) {
        std::string src = partner_vs(s), err;
        ComPtr<ID3DBlob> b = src.empty() ? nullptr : compile(src, "vs_5_0", &err);
        std::string_view bc = b ? std::string_view((const char*)b->GetBufferPointer(), b->GetBufferSize()) : std::string_view();
        if (b && lines_up(s.in, signature(bc, false))) v.d->CreateVertexShader(bc.data(), bc.size(), nullptr, &it->second);
        else if (static std::atomic<int> logged; logged++ < 10) logf("warm11: no pass-through VS for an input signature (%s): %s", b ? "outputs don't line up" : err.c_str(), src.c_str());
    }
    if (!it->second) *why = "no pass-through VS for its inputs";
    return it->second.Get();
}

// Record the stage's use; the driver compiles it at this draw/dispatch. stage: 1 VS 2 PS 3 DS 4 HS 5 GS 6 CS (as in the item).
bool warm11_item(Dev11* v, uint32_t stage, const void* bytes, size_t n, const char** why) {
    static const uint32_t program_type[] = {~0u, 1, 0, 4, 3, 2, 5};
    std::string_view bc((const char*)bytes, n);
    Shader s;
    auto d = v->d.Get();
    auto c = v->c.Get();
    if (!parse(bc, s)) return *why = "not a DXBC shader", false;
    if (stage > 6 || s.type != program_type[stage]) return *why = "stage doesn't match the bytecode", false;
    if (s.interfaces) return *why = "uses class linkage", false;
    if (stage == 3 || stage == 4) return *why = "a HS or DS alone (they come in '2' pairs)", false;

    ID3D11ShaderResourceView* srvs[D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT] = {};
    ID3D11SamplerState* smps[D3D11_COMMONSHADER_SAMPLER_SLOT_COUNT];
    ID3D11Buffer* cbs[D3D11_COMMONSHADER_CONSTANT_BUFFER_API_SLOT_COUNT];
    ID3D11UnorderedAccessView* uavs[D3D11_1_UAV_SLOT_COUNT] = {};
    UINT counts[D3D11_1_UAV_SLOT_COUNT] = {}, nuav = 0, uav_from = 0;
    for (auto& r : s.srv) if (r.reg < std::size(srvs) && !(srvs[r.reg] = srv(*v, r))) return *why = "unsupported SRV declaration", false;
    for (UINT i = 0; i < std::size(smps); ++i) smps[i] = (s.cmp_samplers >> i & 1 ? v->cmp : v->smp).Get();
    for (auto& b : cbs) b = v->cb.Get();
    for (auto& u : s.uav) {
        if (u.reg >= v->uav_slots || !(uavs[u.reg] = uav(*v, u, stage == 6))) return *why = "unsupported UAV declaration", false;
        nuav = std::max(nuav, u.reg + 1);
    }

    ComPtr<ID3D11DeviceChild> sh;
    HRESULT hr = E_FAIL;
    switch (stage) {
    case 1: hr = d->CreateVertexShader(bytes, n, nullptr, (ID3D11VertexShader**)sh.GetAddressOf()); break;
    case 2: hr = d->CreatePixelShader(bytes, n, nullptr, (ID3D11PixelShader**)sh.GetAddressOf()); break;
    case 5: hr = d->CreateGeometryShader(bytes, n, nullptr, (ID3D11GeometryShader**)sh.GetAddressOf()); break;
    case 6: hr = d->CreateComputeShader(bytes, n, nullptr, (ID3D11ComputeShader**)sh.GetAddressOf()); break;
    }
    if (FAILED(hr)) return *why = "the runtime rejects the bytecode", false;

    if (stage == 6) {
        c->CSSetShader((ID3D11ComputeShader*)sh.Get(), nullptr, 0);
        c->CSSetShaderResources(0, (UINT)std::size(srvs), srvs), c->CSSetSamplers(0, (UINT)std::size(smps), smps);
        c->CSSetConstantBuffers(0, (UINT)std::size(cbs), cbs), c->CSSetUnorderedAccessViews(0, v->uav_slots, uavs, counts);
        c->DispatchIndirect(v->args.Get(), 0);
        return true;
    }

    // graphics: VS -> (GS) -> PS into a 1x1 target
    ID3D11VertexShader* vs = stage == 1 ? (ID3D11VertexShader*)sh.Get() : partner(*v, s, why);
    if (!vs) return false;
    ID3D11PixelShader* ps = stage == 2 ? (ID3D11PixelShader*)sh.Get() : v->null_ps.Get();
    ID3D11RenderTargetView* rtvs[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT] = {};
    UINT nrt = 0;
    ComPtr<ID3D11InputLayout> il;
    D3D11_PRIMITIVE_TOPOLOGY topo = D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST;
    if (stage == 1) {  // input layout from the input signature, one float4/uint4/int4 per attribute; outputs go to a trivial PS
        std::vector<D3D11_INPUT_ELEMENT_DESC> ie;
        for (auto& e : s.in)
            if (e.sv != SV_VERTEX_ID && e.sv != SV_INSTANCE_ID)
                ie.push_back({e.name.c_str(), e.index, e.type == 1 ? DXGI_FORMAT_R32G32B32A32_UINT : e.type == 2 ? DXGI_FORMAT_R32G32B32A32_SINT
                              : DXGI_FORMAT_R32G32B32A32_FLOAT, 0, D3D11_APPEND_ALIGNED_ELEMENT});
        if (ie.size() > 32 || (!ie.empty() && FAILED(d->CreateInputLayout(ie.data(), (UINT)ie.size(), bytes, n, &il)))) return *why = "no input layout for its inputs", false;
        UINT stride = (UINT)ie.size() * 16, zero = 0;
        c->IASetVertexBuffers(0, 1, v->vb.GetAddressOf(), &stride, &zero);
        if (std::none_of(s.out.begin(), s.out.end(), [](auto& e) { return e.sv == SV_POSITION; })) ps = nullptr;  // feeds tessellation/GS: no rasterization
    } else if (stage == 5) {
        static const D3D11_PRIMITIVE_TOPOLOGY topos[] = {D3D11_PRIMITIVE_TOPOLOGY_UNDEFINED, D3D11_PRIMITIVE_TOPOLOGY_POINTLIST, D3D11_PRIMITIVE_TOPOLOGY_LINELIST,
            D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST, D3D11_PRIMITIVE_TOPOLOGY_UNDEFINED, D3D11_PRIMITIVE_TOPOLOGY_UNDEFINED,
            D3D11_PRIMITIVE_TOPOLOGY_LINELIST_ADJ, D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST_ADJ};
        if (s.gs_prim >= std::size(topos) || topos[s.gs_prim] == D3D11_PRIMITIVE_TOPOLOGY_UNDEFINED) return *why = "GS input is a patch (needs tessellation)", false;
        topo = topos[s.gs_prim];
    }
    if (stage == 2) {
        for (auto& e : s.out)
            if (iequal(e.name, "SV_Target") && e.index < std::size(rtvs)) rtvs[e.index] = rtv(*v, e.index, e.type), nrt = std::max(nrt, e.index + 1);
    } else if (ps) rtvs[0] = rtv(*v, 0, 3), nrt = 1;
    for (auto& u : s.uav) if (u.reg < nrt) return *why = "UAV slot overlaps a render target", false;  // graphics UAVs share the OM slots
    uav_from = nrt;

    c->IASetInputLayout(il.Get());
    c->IASetPrimitiveTopology(topo);
    c->VSSetShader(vs, nullptr, 0);
    c->HSSetShader(nullptr, nullptr, 0), c->DSSetShader(nullptr, nullptr, 0);
    c->GSSetShader(stage == 5 ? (ID3D11GeometryShader*)sh.Get() : nullptr, nullptr, 0);
    c->PSSetShader(ps, nullptr, 0);
#define BIND(S) c->S##SetShaderResources(0, (UINT)std::size(srvs), srvs), c->S##SetSamplers(0, (UINT)std::size(smps), smps), \
                c->S##SetConstantBuffers(0, (UINT)std::size(cbs), cbs)
    if (stage == 1) BIND(VS);
    else if (stage == 2) BIND(PS);
    else BIND(GS);
#undef BIND
    c->OMSetDepthStencilState((ps ? v->depth_on : v->depth_off).Get(), 0);
    c->OMSetRenderTargetsAndUnorderedAccessViews(nrt, rtvs, ps ? v->dsv.Get() : nullptr, uav_from, nuav > uav_from ? nuav - uav_from : 0,
                                                 uavs + uav_from, counts);
    c->DrawInstancedIndirect(v->args.Get(), 0);
    return true;
}

// A '2' item: a hull and a domain shader of one map, drawn together (neither can be drawn alone) behind a generated
// pass-through VS writing the HS's inputs, patch list of the HS's input control points, no PS (like a VS feeding a GS).
bool warm11_pair(Dev11* v, const void* hsb, size_t hsn, const void* dsb, size_t dsn, const char** why) {
    Shader hs, ds;
    auto d = v->d.Get();
    auto c = v->c.Get();
    if (!parse({(const char*)hsb, hsn}, hs) || !parse({(const char*)dsb, dsn}, ds)) return *why = "not a DXBC shader", false;
    if (hs.type != 3 || ds.type != 4) return *why = "stage doesn't match the bytecode", false;
    if (hs.interfaces || ds.interfaces) return *why = "uses class linkage", false;
    if (!hs.uav.empty() || !ds.uav.empty()) return *why = "UAV in a tessellation stage", false;
    if (!hs.cp_in || hs.cp_in > 32 || hs.cp_out != ds.cp_in || hs.domain != ds.domain) return *why = "HS and DS don't match (control points or domain)", false;
    ID3D11ShaderResourceView* srvs[2][D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT] = {};
    ID3D11SamplerState* smps[2][D3D11_COMMONSHADER_SAMPLER_SLOT_COUNT];
    ID3D11Buffer* cbs[D3D11_COMMONSHADER_CONSTANT_BUFFER_API_SLOT_COUNT];
    for (auto& b : cbs) b = v->cb.Get();
    for (int k = 0; k < 2; ++k) {
        const Shader& s = k ? ds : hs;
        for (auto& r : s.srv) if (r.reg < std::size(srvs[k]) && !(srvs[k][r.reg] = srv(*v, r))) return *why = "unsupported SRV declaration", false;
        for (UINT i = 0; i < std::size(smps[k]); ++i) smps[k][i] = (s.cmp_samplers >> i & 1 ? v->cmp : v->smp).Get();
    }
    ComPtr<ID3D11HullShader> h;
    ComPtr<ID3D11DomainShader> dd;
    if (FAILED(d->CreateHullShader(hsb, hsn, nullptr, &h)) || FAILED(d->CreateDomainShader(dsb, dsn, nullptr, &dd))) return *why = "the runtime rejects the bytecode", false;
    ID3D11VertexShader* vs = partner(*v, hs, why);
    if (!vs) return false;
    c->IASetInputLayout(nullptr);
    c->IASetPrimitiveTopology((D3D11_PRIMITIVE_TOPOLOGY)(D3D11_PRIMITIVE_TOPOLOGY_1_CONTROL_POINT_PATCHLIST + hs.cp_in - 1));
    c->VSSetShader(vs, nullptr, 0), c->HSSetShader(h.Get(), nullptr, 0), c->DSSetShader(dd.Get(), nullptr, 0);
    c->GSSetShader(nullptr, nullptr, 0), c->PSSetShader(nullptr, nullptr, 0);
    c->HSSetShaderResources(0, (UINT)std::size(srvs[0]), srvs[0]), c->HSSetSamplers(0, (UINT)std::size(smps[0]), smps[0]), c->HSSetConstantBuffers(0, (UINT)std::size(cbs), cbs);
    c->DSSetShaderResources(0, (UINT)std::size(srvs[1]), srvs[1]), c->DSSetSamplers(0, (UINT)std::size(smps[1]), smps[1]), c->DSSetConstantBuffers(0, (UINT)std::size(cbs), cbs);
    c->OMSetDepthStencilState(v->depth_off.Get(), 0);
    c->OMSetRenderTargetsAndUnorderedAccessViews(0, nullptr, nullptr, 0, 0, nullptr, nullptr);
    c->DrawInstancedIndirect(v->args.Get(), 0);  // warm11_item unsets HS/DS for the next non-tessellated draw
    return true;
}

// Debug layer: keep the errors and warnings this item's calls produced (count + first text per message id).
void warm11_drain(Dev11* v, uint32_t stage) {
    if (!v->iq) return;
    for (UINT64 i = 0, n = v->iq->GetNumStoredMessages(); i < n; ++i) {
        SIZE_T len = 0;
        v->iq->GetMessage(i, nullptr, &len);
        std::vector<char> buf(len);
        auto m = (D3D11_MESSAGE*)buf.data();
        if (FAILED(v->iq->GetMessage(i, m, &len)) || m->Severity > D3D11_MESSAGE_SEVERITY_WARNING) continue;
        auto& [count, text] = v->msgs[{m->Severity, (UINT)m->ID}];
        if (!count++) text = "stage " + std::to_string(stage) + ": " + std::string(m->pDescription, strnlen(m->pDescription, m->DescriptionByteLength));
    }
    v->iq->ClearStoredMessages();
}

// Flush and wait until the GPU passed every draw so far: the driver has compiled them. Failure = device lost.
HRESULT warm11_flush(Dev11* v) {
    v->c->End(v->q.Get());
    v->c->Flush();
    HRESULT hr;
    while ((hr = v->c->GetData(v->q.Get(), nullptr, 0, 0)) == S_FALSE) Sleep(1);
    return FAILED(hr) ? hr : v->d->GetDeviceRemovedReason();
}

void warm11_close(Dev11* v) {
    static const char* sev[] = {"corruption", "error", "warning"};
    for (auto& [k, m] : v->msgs) logf("warm11: debug layer %s id=%u x%llu, first: %s", sev[k.first], k.second, m.first, m.second.c_str());
    if (v->c) v->c->ClearState(), v->c->Flush();
    delete v;
}
