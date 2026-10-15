// scskiller (Linux/Proton port): d3d12.dll recorder proxy. Record mode of SCSKiller-main/proxy/proxy.cpp.
// Loaded by Wine from the game's exe folder (native,builtin), it forwards to the prefix's system32 d3d12.dll
// (vkd3d-proton) and records every PSO the game creates into scskiller.db with the exact same canonical format
// as the Windows proxy, so SCSKiller.Core's Recordings / Planner / ExactLayouts read it unchanged.
#define NOMINMAX
#include <windows.h>
#include <d3d12.h>
#include <bcrypt.h>
#include <share.h>
#include <io.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#ifndef D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL2
#define D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL2 ((D3D12_PIPELINE_STATE_SUBOBJECT_TYPE)28)
#define D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RASTERIZER1 ((D3D12_PIPELINE_STATE_SUBOBJECT_TYPE)29)
#define D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RASTERIZER2 ((D3D12_PIPELINE_STATE_SUBOBJECT_TYPE)30)
struct D3D12_DEPTH_STENCILOP_DESC1 { D3D12_STENCIL_OP StencilFailOp, StencilDepthFailOp, StencilPassOp; D3D12_COMPARISON_FUNC StencilFunc; UINT8 StencilReadMask, StencilWriteMask; };
struct D3D12_DEPTH_STENCIL_DESC2 { BOOL DepthEnable; D3D12_DEPTH_WRITE_MASK DepthWriteMask; D3D12_COMPARISON_FUNC DepthFunc; BOOL StencilEnable;
    D3D12_DEPTH_STENCILOP_DESC1 FrontFace, BackFace; BOOL DepthBoundsTestEnable; };
struct D3D12_RASTERIZER_DESC1 { D3D12_FILL_MODE FillMode; D3D12_CULL_MODE CullMode; BOOL FrontCounterClockwise; FLOAT DepthBias, DepthBiasClamp, SlopeScaledDepthBias;
    BOOL DepthClipEnable, MultisampleEnable, AntialiasedLineEnable; UINT ForcedSampleCount; D3D12_CONSERVATIVE_RASTERIZATION_MODE ConservativeRaster; };
struct D3D12_RASTERIZER_DESC2 { D3D12_FILL_MODE FillMode; D3D12_CULL_MODE CullMode; BOOL FrontCounterClockwise; FLOAT DepthBias, DepthBiasClamp, SlopeScaledDepthBias;
    BOOL DepthClipEnable; int LineRasterizationMode; UINT ForcedSampleCount; D3D12_CONSERVATIVE_RASTERIZATION_MODE ConservativeRaster; };
#endif

static const GUID kClsidDeviceFactory = {0x114863bf, 0xc386, 0x4aee, {0xb3, 0x9d, 0x8f, 0x0b, 0xbb, 0x06, 0x29, 0x55}};
static const GUID kClsidSdkConfig = {0x7cda6aca, 0xa03e, 0x49c8, {0x94, 0x58, 0x03, 0x34, 0xd2, 0x0e, 0x07, 0xce}};

using Hash = std::array<uint8_t, 20>;
struct HashH { size_t operator()(const Hash& h) const { size_t v; memcpy(&v, h.data(), sizeof v); return v; } };
using HashSet = std::unordered_set<Hash, HashH>;
static const Hash kZero{};

static Hash sha1(const void* p, size_t n) {
    Hash h{};
    BCRYPT_ALG_HANDLE alg = nullptr;
    if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA1_ALGORITHM, nullptr, 0) == 0) {
        BCryptHash(alg, nullptr, 0, (PUCHAR)p, (ULONG)n, h.data(), 20);
        BCryptCloseAlgorithmProvider(alg, 0);
    }
    return h;
}
static std::string hex(const Hash& h) {
    char x[41];
    for (int i = 0; i < 20; ++i) snprintf(x + 2 * i, 3, "%02x", h[i]);
    return std::string(x, 40);
}
static Hash key_of(char tag, const std::string& payload) { std::string s(1, tag); s += payload; return sha1(s.data(), s.size()); }
static size_t align(size_t v, size_t a) { return (v + a - 1) & ~(a - 1); }

static size_t shader_len(const D3D12_SHADER_BYTECODE& b) {
    auto p = (const uint8_t*)b.pShaderBytecode;
    uint32_t n;
    if (b.BytecodeLength >= 28 && !memcmp(p, "DXBC", 4) && (memcpy(&n, p + 24, 4), n <= b.BytecodeLength)) return n;
    return b.BytecodeLength;
}

static HMODULE g_real;
static std::wstring g_dir;
static FILE *g_db, *g_log, *g_csv;
static const auto g_t0 = std::chrono::steady_clock::now();
static std::mutex g_mx;
static HashSet g_blobs_on_disk, g_known, g_keys;
static std::unordered_map<void*, Hash> g_rs_of;
static std::unordered_map<Hash, std::string, HashH> g_rs_bytes;
static std::unordered_map<void*, Hash> g_so_key;
static std::unordered_map<void*, ID3D12Device*> g_lib_dev;
static uint64_t g_creates, g_known_hits, g_unsupported, g_db_bytes, g_db_cap;
static bool g_db_capped, g_db_full;
static std::vector<Hash> g_imported;

static double now_ms() { return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - g_t0).count(); }
static void logf(const char* fmt, ...) {
    if (!g_log) return;
    va_list a;
    va_start(a, fmt);
    std::lock_guard l(g_mx);
    fprintf(g_log, "[%9.1fs] ", now_ms() / 1000);
    vfprintf(g_log, fmt, a);
    fputc('\n', g_log);
    fflush(g_log);
    va_end(a);
}
static bool imported(const Hash& h) { return std::binary_search(g_imported.begin(), g_imported.end(), h); }
static bool fresh(HashSet& s, const Hash& h) { return !imported(h) && s.insert(h).second; }

template <class T> static void zero_gap(T& v, size_t from, size_t to) { memset((char*)&v + from, 0, to - from); }
#define GAP_AFTER(T, a, b) offsetof(T, a) + sizeof(((T*)0)->a), offsetof(T, b)

struct NvExt { uint32_t slot = ~0u, space = 0, scope = 0, opts = 0; };

struct Writer {
    char tag;
    std::string s;
    bool ok = true;
    const char* why = "";
    std::vector<std::pair<Hash, std::string_view>> blobs;
    std::vector<Hash> rsigs;
    NvExt nv;

    void raw(const void* p, size_t n) { s.append((const char*)p, n); }
    template <class T> void pod(T& v) { raw(&v, sizeof v); }
    void u32(uint32_t v) { raw(&v, 4); }
    void hash(const Hash& h) { raw(h.data(), 20); }
    void sh(D3D12_SHADER_BYTECODE& b, uint32_t) {
        if (!b.pShaderBytecode || !b.BytecodeLength) return hash(kZero);
        size_t n = shader_len(b);
        Hash h = sha1(b.pShaderBytecode, n);
        blobs.push_back({h, {(const char*)b.pShaderBytecode, n}});
        hash(h);
    }
    void rs(ID3D12RootSignature*& p) {
        if (!p) return hash(kZero);
        std::lock_guard l(g_mx);
        auto it = g_rs_of.find(p);
        if (it == g_rs_of.end()) { ok = false, why = "root signature was not created through a hooked device"; return; }
        rsigs.push_back(it->second);
        hash(it->second);
    }
    void so(D3D12_STREAM_OUTPUT_DESC& d) {
        if (!d.NumEntries) return;
        if (!d.pSODeclaration) { ok = false, why = "stream output entries without a declaration"; return; }
        u32(d.NumEntries);
        for (UINT i = 0; i < d.NumEntries; ++i) {
            auto& e = d.pSODeclaration[i];
            u32(e.Stream);
            if (!e.SemanticName) u32(0xFFFFFFFF);
            else { uint32_t n = (uint32_t)strlen(e.SemanticName); u32(n), raw(e.SemanticName, n); }
            u32(e.SemanticIndex), u32(e.StartComponent), u32(e.ComponentCount), u32(e.OutputSlot);
        }
        u32(d.pBufferStrides ? d.NumStrides : 0);
        for (UINT i = 0; d.pBufferStrides && i < d.NumStrides; ++i) u32(d.pBufferStrides[i]);
        u32(d.RasterizedStream);
    }
    void so_tail(D3D12_STREAM_OUTPUT_DESC& d) { so(d); }
    void wstr(LPCWSTR p) {
        if (!p) return u32(0xFFFFFFFF);
        uint32_t n = (uint32_t)wcslen(p);
        u32(n), raw(p, 2 * n);
    }
    void lib(const D3D12_SHADER_BYTECODE& b) {
        if (!b.pShaderBytecode || !b.BytecodeLength) { ok = false, why = "empty DXIL library"; return hash(kZero); }
        size_t n = shader_len(b);
        Hash h = sha1(b.pShaderBytecode, n);
        blobs.push_back({h, {(const char*)b.pShaderBytecode, n}});
        hash(h);
    }
    void so_ref(ID3D12StateObject* p);
    void il(D3D12_INPUT_LAYOUT_DESC& d) {
        u32(d.NumElements);
        for (UINT i = 0; i < d.NumElements; ++i) {
            auto& e = d.pInputElementDescs[i];
            uint32_t n = e.SemanticName ? (uint32_t)strlen(e.SemanticName) : 0;
            u32(n), raw(e.SemanticName, n);
            u32(e.SemanticIndex), u32(e.Format), u32(e.InputSlot), u32(e.AlignedByteOffset), u32(e.InputSlotClass), u32(e.InstanceDataStepRate);
        }
    }
    void vi(D3D12_VIEW_INSTANCING_DESC& d) {
        u32(d.ViewInstanceCount);
        for (UINT i = 0; i < d.ViewInstanceCount; ++i) pod(const_cast<D3D12_VIEW_INSTANCE_LOCATION&>(d.pViewInstanceLocations[i]));
        u32(d.Flags);
    }
    void rtfmt(UINT& n, DXGI_FORMAT* f) {
        u32(n);
        for (UINT i = 0; i < 8; ++i) u32(i < n ? f[i] : DXGI_FORMAT_UNKNOWN);
    }
    void ds(D3D12_DEPTH_STENCIL_DESC& v) { auto c = v; zero_gap(c, GAP_AFTER(D3D12_DEPTH_STENCIL_DESC, StencilWriteMask, FrontFace)); pod(c); }
    void ds(D3D12_DEPTH_STENCIL_DESC1& v) { auto c = v; zero_gap(c, GAP_AFTER(D3D12_DEPTH_STENCIL_DESC1, StencilWriteMask, FrontFace)); pod(c); }
    void ds(D3D12_DEPTH_STENCIL_DESC2& v) {
        auto c = v;
        for (auto* f : {&c.FrontFace, &c.BackFace}) zero_gap(*f, offsetof(D3D12_DEPTH_STENCILOP_DESC1, StencilWriteMask) + 1, sizeof *f);
        pod(c);
    }
};

template <class IO> static void io_gfx(IO& io, D3D12_GRAPHICS_PIPELINE_STATE_DESC& d) {
    io.rs(d.pRootSignature);
    io.sh(d.VS, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_VS), io.sh(d.PS, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_PS);
    io.sh(d.DS, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DS), io.sh(d.HS, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_HS);
    io.sh(d.GS, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_GS);
    io.pod(d.BlendState), io.pod(d.SampleMask), io.pod(d.RasterizerState), io.ds(d.DepthStencilState);
    io.il(d.InputLayout);
    io.pod(d.IBStripCutValue), io.pod(d.PrimitiveTopologyType);
    io.rtfmt(d.NumRenderTargets, d.RTVFormats);
    io.pod(d.DSVFormat), io.pod(d.SampleDesc), io.pod(d.NodeMask), io.pod(d.Flags);
    io.so_tail(d.StreamOutput);
}
static const uint32_t kSoDecl = 0x10007;
template <class IO> static void io_cs(IO& io, D3D12_COMPUTE_PIPELINE_STATE_DESC& d) {
    io.rs(d.pRootSignature), io.sh(d.CS, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_CS), io.pod(d.NodeMask), io.pod(d.Flags);
}

struct Sub { size_t size, align; };
static bool sub_info(uint32_t t, Sub& o) {
#define S(E, T) case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_##E: o = {sizeof(T), alignof(T)}; return true;
    switch (t) {
        S(ROOT_SIGNATURE, ID3D12RootSignature*) S(VS, D3D12_SHADER_BYTECODE) S(PS, D3D12_SHADER_BYTECODE)
        S(DS, D3D12_SHADER_BYTECODE) S(HS, D3D12_SHADER_BYTECODE) S(GS, D3D12_SHADER_BYTECODE) S(CS, D3D12_SHADER_BYTECODE)
        S(AS, D3D12_SHADER_BYTECODE) S(MS, D3D12_SHADER_BYTECODE) S(STREAM_OUTPUT, D3D12_STREAM_OUTPUT_DESC)
        S(BLEND, D3D12_BLEND_DESC) S(SAMPLE_MASK, UINT) S(RASTERIZER, D3D12_RASTERIZER_DESC)
        S(DEPTH_STENCIL, D3D12_DEPTH_STENCIL_DESC) S(INPUT_LAYOUT, D3D12_INPUT_LAYOUT_DESC)
        S(IB_STRIP_CUT_VALUE, D3D12_INDEX_BUFFER_STRIP_CUT_VALUE) S(PRIMITIVE_TOPOLOGY, D3D12_PRIMITIVE_TOPOLOGY_TYPE)
        S(RENDER_TARGET_FORMATS, D3D12_RT_FORMAT_ARRAY) S(DEPTH_STENCIL_FORMAT, DXGI_FORMAT) S(SAMPLE_DESC, DXGI_SAMPLE_DESC)
        S(NODE_MASK, UINT) S(CACHED_PSO, D3D12_CACHED_PIPELINE_STATE) S(FLAGS, D3D12_PIPELINE_STATE_FLAGS)
        S(DEPTH_STENCIL1, D3D12_DEPTH_STENCIL_DESC1) S(VIEW_INSTANCING, D3D12_VIEW_INSTANCING_DESC)
        S(DEPTH_STENCIL2, D3D12_DEPTH_STENCIL_DESC2) S(RASTERIZER1, D3D12_RASTERIZER_DESC1) S(RASTERIZER2, D3D12_RASTERIZER_DESC2)
    }
#undef S
    return false;
}
template <class IO> static void io_sub(IO& io, uint32_t t, void* p) {
    switch (t) {
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_ROOT_SIGNATURE: io.rs(*(ID3D12RootSignature**)p); break;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_VS: case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_PS:
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DS: case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_HS:
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_GS: case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_CS:
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_AS: case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_MS:
        io.sh(*(D3D12_SHADER_BYTECODE*)p, t); break;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_STREAM_OUTPUT: io.so(*(D3D12_STREAM_OUTPUT_DESC*)p); break;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_INPUT_LAYOUT: io.il(*(D3D12_INPUT_LAYOUT_DESC*)p); break;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_VIEW_INSTANCING: io.vi(*(D3D12_VIEW_INSTANCING_DESC*)p); break;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RENDER_TARGET_FORMATS: { auto& r = *(D3D12_RT_FORMAT_ARRAY*)p; io.rtfmt(r.NumRenderTargets, r.RTFormats); break; }
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL: io.ds(*(D3D12_DEPTH_STENCIL_DESC*)p); break;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL1: io.ds(*(D3D12_DEPTH_STENCIL_DESC1*)p); break;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL2: io.ds(*(D3D12_DEPTH_STENCIL_DESC2*)p); break;
    default: { Sub s; sub_info(t, s); io.raw(p, s.size); }
    }
}
static void write_stream(Writer& w, const D3D12_PIPELINE_STATE_STREAM_DESC& d) {
    auto base = (uint8_t*)d.pPipelineStateSubobjectStream;
    size_t count_at = w.s.size();
    uint32_t count = 0;
    w.u32(0);
    for (size_t pos = 0; pos + 4 <= d.SizeInBytes;) {
        uint32_t t;
        memcpy(&t, base + pos, 4);
        Sub s;
        if (!sub_info(t, s)) { w.ok = false, w.why = "unknown stream subobject type"; return; }
        size_t in = align(pos + 4, s.align);
        bool decl = t == D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_STREAM_OUTPUT && ((D3D12_STREAM_OUTPUT_DESC*)(base + in))->NumEntries;
        if (t != D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_CACHED_PSO) w.u32(decl ? kSoDecl : t), io_sub(w, t, base + in), ++count;
        pos = align(in + s.size, sizeof(void*));
    }
    memcpy(&w.s[count_at], &count, 4);
}

static ID3D12StateObject* so_id(IUnknown* p) {
    ID3D12StateObject* s = nullptr;
    if (p && SUCCEEDED(p->QueryInterface(IID_PPV_ARGS(&s)))) s->Release();
    return s;
}
void Writer::so_ref(ID3D12StateObject* p) {
    std::lock_guard l(g_mx);
    auto it = g_so_key.find(so_id(p));
    if (it == g_so_key.end()) { ok = false, why = "state object it builds on was not recorded"; return hash(kZero); }
    hash(it->second);
}
static void write_so(Writer& w, const D3D12_STATE_OBJECT_DESC& d) {
    if (d.Type != D3D12_STATE_OBJECT_TYPE_COLLECTION && d.Type != D3D12_STATE_OBJECT_TYPE_RAYTRACING_PIPELINE) {
        w.ok = false, w.why = "state object type not supported";
        return;
    }
    w.u32(d.Type), w.u32(d.NumSubobjects);
    auto exports = [&](UINT n, const D3D12_EXPORT_DESC* e) {
        w.u32(n);
        for (UINT i = 0; i < n; ++i) w.wstr(e[i].Name), w.wstr(e[i].ExportToRename), w.u32(e[i].Flags);
    };
    auto names = [&](UINT n, LPCWSTR const* s) { w.u32(n); for (UINT i = 0; i < n; ++i) w.wstr(s[i]); };
    for (UINT i = 0; i < d.NumSubobjects && w.ok; ++i) {
        auto& so = d.pSubobjects[i];
        auto p = so.pDesc;
        w.u32(so.Type);
        switch (so.Type) {
        case D3D12_STATE_SUBOBJECT_TYPE_STATE_OBJECT_CONFIG: w.u32(((const D3D12_STATE_OBJECT_CONFIG*)p)->Flags); break;
        case D3D12_STATE_SUBOBJECT_TYPE_GLOBAL_ROOT_SIGNATURE:
        case D3D12_STATE_SUBOBJECT_TYPE_LOCAL_ROOT_SIGNATURE: { auto r = *(ID3D12RootSignature* const*)p; w.rs(r); break; }
        case D3D12_STATE_SUBOBJECT_TYPE_NODE_MASK: w.u32(((const D3D12_NODE_MASK*)p)->NodeMask); break;
        case D3D12_STATE_SUBOBJECT_TYPE_DXIL_LIBRARY: { auto& l = *(const D3D12_DXIL_LIBRARY_DESC*)p; w.lib(l.DXILLibrary), exports(l.NumExports, l.pExports); break; }
        case D3D12_STATE_SUBOBJECT_TYPE_EXISTING_COLLECTION: { auto& c = *(const D3D12_EXISTING_COLLECTION_DESC*)p; w.so_ref(c.pExistingCollection), exports(c.NumExports, c.pExports); break; }
        case D3D12_STATE_SUBOBJECT_TYPE_SUBOBJECT_TO_EXPORTS_ASSOCIATION: {
            auto& a = *(const D3D12_SUBOBJECT_TO_EXPORTS_ASSOCIATION*)p;
            ptrdiff_t k = a.pSubobjectToAssociate - d.pSubobjects;
            if (k < 0 || k >= (ptrdiff_t)d.NumSubobjects) { w.ok = false, w.why = "association to a subobject outside the desc"; break; }
            w.u32((uint32_t)k), names(a.NumExports, a.pExports);
            break;
        }
        case D3D12_STATE_SUBOBJECT_TYPE_DXIL_SUBOBJECT_TO_EXPORTS_ASSOCIATION: { auto& a = *(const D3D12_DXIL_SUBOBJECT_TO_EXPORTS_ASSOCIATION*)p; w.wstr(a.SubobjectToAssociate), names(a.NumExports, a.pExports); break; }
        case D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_SHADER_CONFIG: { auto& c = *(const D3D12_RAYTRACING_SHADER_CONFIG*)p; w.u32(c.MaxPayloadSizeInBytes), w.u32(c.MaxAttributeSizeInBytes); break; }
        case D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_PIPELINE_CONFIG: w.u32(((const D3D12_RAYTRACING_PIPELINE_CONFIG*)p)->MaxTraceRecursionDepth); break;
        case D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_PIPELINE_CONFIG1: { auto& c = *(const D3D12_RAYTRACING_PIPELINE_CONFIG1*)p; w.u32(c.MaxTraceRecursionDepth), w.u32(c.Flags); break; }
        case D3D12_STATE_SUBOBJECT_TYPE_HIT_GROUP: {
            auto& h = *(const D3D12_HIT_GROUP_DESC*)p;
            w.wstr(h.HitGroupExport), w.u32(h.Type), w.wstr(h.AnyHitShaderImport), w.wstr(h.ClosestHitShaderImport), w.wstr(h.IntersectionShaderImport);
            break;
        }
        default: w.ok = false, w.why = "state subobject type not supported";
        }
    }
}

// db record: u8 tag, u32 len, payload (as proxy.cpp put)
static void put(char tag, const void* a, size_t an, const void* b = nullptr, size_t bn = 0) {
    uint32_t len = (uint32_t)(an + bn);
    fputc(tag, g_db), fwrite(&len, 4, 1, g_db), fwrite(a, 1, an, g_db);
    if (bn) fwrite(b, 1, bn, g_db);
    g_db_bytes += 5 + an + bn;
}
static void db_flush() {
    if (!fflush(g_db) && !ferror(g_db)) return;
    logf("db: writing scskiller.db failed (disk full?): nothing more is recorded in this launch");
    fclose(g_db), g_db = nullptr;
}

static void load_file(const std::wstring& path) {
    FILE* f = _wfopen(path.c_str(), L"rb");
    if (!f) return;
    long long good = 0, flen = _filelengthi64(_fileno(f));
    for (;;) {
        int tag = fgetc(f);
        uint32_t len;
        if (tag == EOF || !tag || fread(&len, 4, 1, f) != 1) break;
        if (tag == 'B') {
            Hash h;
            if (len < 20 || fread(h.data(), 1, 20, f) != 20) break;
            if (_fseeki64(f, len - 20, SEEK_CUR) || _ftelli64(f) > flen) break;
            g_blobs_on_disk.insert(h);
        } else {
            if (len > flen - good - 5) break;
            std::string p(len, '\0');
            if (fread(p.data(), 1, len, f) != len) break;
            Hash k = key_of((char)tag, p);
            g_keys.insert(k);
            if (tag != 'N' && tag != 'W') g_known.insert(k);
        }
        good = _ftelli64(f);
    }
    long long size = (_fseeki64(f, 0, SEEK_END), _ftelli64(f));
    fclose(f);
    if (size > good && g_db && !_chsize_s(_fileno(g_db), good)) logf("db: truncated a torn tail (%lld -> %lld bytes)", size, good);
}
static size_t load_keys(const std::wstring& path) {
    FILE* f = _wfopen(path.c_str(), L"rb");
    if (!f) return 0;
    char magic[8];
    long long len = _filelengthi64(_fileno(f));
    if (len > 8 && fread(magic, 1, 8, f) == 8 && !memcmp(magic, "SCSKKEY1", 8)) {
        g_imported.resize((size_t)(len - 8) / 20);
        g_imported.resize(fread(g_imported.data(), 20, g_imported.size(), f));
        std::sort(g_imported.begin(), g_imported.end());
    }
    fclose(f);
    return g_imported.size();
}
static void load_db() {
    g_db = _wfsopen((g_dir + L"scskiller.db").c_str(), L"ab", _SH_DENYWR);
    if (!g_db) logf("db: scskiller.db is open in another process: nothing is recorded in this one");
    load_file(g_dir + L"scskiller.db");
    if (size_t n = load_keys(g_dir + L"scskiller.keys")) logf("db: %zu keys of records and blobs imported earlier or shipped with the game", n);
    WIN32_FILE_ATTRIBUTE_DATA fa;
    if (GetFileAttributesExW((g_dir + L"scskiller.db").c_str(), GetFileExInfoStandard, &fa)) g_db_bytes = (uint64_t)fa.nFileSizeHigh << 32 | fa.nFileSizeLow;
    wchar_t cap[32];
    GetPrivateProfileStringW(L"scskiller", L"max_db_bytes", L"", cap, 32, (g_dir + L"scskiller.ini").c_str());
    g_db_capped = *cap, g_db_cap = wcstoull(cap, nullptr, 10);
    logf("db: %zu PSOs recorded, %zu blobs (mode=record, Linux/Proton)", g_known.size(), g_blobs_on_disk.size());
}

static void store(const Writer& w, const Hash& k) {
    if (g_db_capped && g_db_bytes >= g_db_cap) {
        if (!g_db_full) g_db_full = true;
        return;
    }
    if (fresh(g_keys, k) && g_db) {
        for (auto& [h, b] : w.blobs)
            if (fresh(g_blobs_on_disk, h)) put('B', h.data(), 20, b.data(), b.size());
        for (auto& h : w.rsigs)
            if (auto it = g_rs_bytes.find(h); it != g_rs_bytes.end()) {
                if (fresh(g_blobs_on_disk, h)) put('B', h.data(), 20, it->second.data(), it->second.size());
                g_rs_bytes.erase(it);
            }
        put(w.tag, w.s.data(), w.s.size());
        db_flush();
    }
}

static void note(Writer& w, double ms, bool lib, double pre_ms) {
    double t_ret = now_ms();
    std::unique_lock l(g_mx);
    ++g_creates;
    if (!w.ok) {
        ++g_unsupported;
        return;
    }
    Hash k = key_of(w.tag, w.s);
    bool known = g_known.count(k) || imported(k);
    store(w, k);
    g_known_hits += known;
    if (g_csv) {
        fprintf(g_csv, "%.1f,%c,%d,%d,%.3f,%s,%.3f,%lu,0\n", t_ret, lib ? w.tag | 0x20 : w.tag, (int)known, (int)known, ms, hex(k).c_str(), pre_ms,
                GetCurrentThreadId());
        fflush(g_csv);
    }
    uint64_t c = g_creates;
    l.unlock();
    if (c % 500 == 0) logf("creates=%llu known=%llu unsupported=%llu db=%zu", (unsigned long long)c, (unsigned long long)g_known_hits,
                           (unsigned long long)g_unsupported, g_keys.size());
}

// ---- device hooks (vtable slots fixed by the COM ABI, same as proxy.cpp) ----
using PFN_Gfx = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device*, const D3D12_GRAPHICS_PIPELINE_STATE_DESC*, REFIID, void**);
using PFN_Cs = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device*, const D3D12_COMPUTE_PIPELINE_STATE_DESC*, REFIID, void**);
using PFN_Rs = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device*, UINT, const void*, SIZE_T, REFIID, void**);
using PFN_Stream = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device2*, const D3D12_PIPELINE_STATE_STREAM_DESC*, REFIID, void**);
using PFN_CreateLib = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device1*, const void*, SIZE_T, REFIID, void**);
using PFN_LoadGfx = HRESULT(STDMETHODCALLTYPE*)(ID3D12PipelineLibrary*, LPCWSTR, const D3D12_GRAPHICS_PIPELINE_STATE_DESC*, REFIID, void**);
using PFN_LoadCs = HRESULT(STDMETHODCALLTYPE*)(ID3D12PipelineLibrary*, LPCWSTR, const D3D12_COMPUTE_PIPELINE_STATE_DESC*, REFIID, void**);
using PFN_LoadStream = HRESULT(STDMETHODCALLTYPE*)(ID3D12PipelineLibrary1*, LPCWSTR, const D3D12_PIPELINE_STATE_STREAM_DESC*, REFIID, void**);
using PFN_Cso = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device5*, const D3D12_STATE_OBJECT_DESC*, REFIID, void**);
using PFN_AddSo = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device7*, const D3D12_STATE_OBJECT_DESC*, ID3D12StateObject*, REFIID, void**);
enum { SLOT_GFX = 10, SLOT_CS = 11, SLOT_RS = 16, SLOT_CREATELIB = 44, SLOT_STREAM = 47, SLOT_CSO = 62, SLOT_ADDSO = 66 };
enum { LIB_LOADGFX = 9, LIB_LOADCS = 10, LIB_LOADSTREAM = 13 };
static PFN_Gfx o_gfx;
static PFN_Cs o_cs;
static PFN_Rs o_rs;
static PFN_Stream o_stream;
static PFN_CreateLib o_createlib;
static PFN_LoadGfx o_loadgfx;
static PFN_LoadCs o_loadcs;
static PFN_LoadStream o_loadstream;
static PFN_Cso o_cso;
static PFN_AddSo o_addso;
static thread_local bool t_in;

template <class T> static void patch(void** vt, int slot, void* hook, T& orig) {
    if (vt[slot] == hook) return;
    if (orig && (void*)orig != vt[slot]) return;
    orig = (T)vt[slot];
    DWORD old;
    VirtualProtect(&vt[slot], sizeof(void*), PAGE_READWRITE, &old);
    vt[slot] = hook;
    VirtualProtect(&vt[slot], sizeof(void*), old, &old);
}

static HRESULT STDMETHODCALLTYPE hk_rs(ID3D12Device* dev, UINT mask, const void* blob, SIZE_T n, REFIID riid, void** pp) {
    HRESULT hr = o_rs(dev, mask, blob, n, riid, pp);
    if (SUCCEEDED(hr) && pp && *pp) {
        Hash h = sha1(blob, n);
        std::lock_guard l(g_mx);
        g_rs_of[*pp] = h;
        if (g_db && !(g_db_capped && g_db_bytes >= g_db_cap) && !g_blobs_on_disk.count(h) && !imported(h)) g_rs_bytes.try_emplace(h, (const char*)blob, n);
    }
    return hr;
}

template <class F> static HRESULT timed_create(void** pp, Writer& w, std::chrono::steady_clock::time_point t_entry, F&& call, bool lib = false) {
    if (t_in || !pp) return call();
    t_in = true;
    auto t0 = std::chrono::steady_clock::now();
    HRESULT hr = call();
    double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
    if (SUCCEEDED(hr)) note(w, ms, lib, std::chrono::duration<double, std::milli>(t0 - t_entry).count());
    t_in = false;
    return hr;
}
#define ENTRY auto t_entry = std::chrono::steady_clock::now()

static HRESULT STDMETHODCALLTYPE hk_gfx(ID3D12Device* dev, const D3D12_GRAPHICS_PIPELINE_STATE_DESC* d, REFIID riid, void** pp) {
    ENTRY; Writer w{'G'};
    if (!t_in && pp && d) io_gfx(w, const_cast<D3D12_GRAPHICS_PIPELINE_STATE_DESC&>(*d));
    return timed_create(pp, w, t_entry, [&] { return o_gfx(dev, d, riid, pp); });
}
static HRESULT STDMETHODCALLTYPE hk_cs(ID3D12Device* dev, const D3D12_COMPUTE_PIPELINE_STATE_DESC* d, REFIID riid, void** pp) {
    ENTRY; Writer w{'C'};
    if (!t_in && pp && d) io_cs(w, const_cast<D3D12_COMPUTE_PIPELINE_STATE_DESC&>(*d));
    return timed_create(pp, w, t_entry, [&] { return o_cs(dev, d, riid, pp); });
}
static HRESULT STDMETHODCALLTYPE hk_stream(ID3D12Device2* dev, const D3D12_PIPELINE_STATE_STREAM_DESC* d, REFIID riid, void** pp) {
    ENTRY; Writer w{'S'};
    if (!t_in && pp && d) write_stream(w, *d);
    return timed_create(pp, w, t_entry, [&] { return o_stream(dev, d, riid, pp); });
}
static void remember_so(const Writer& w, HRESULT hr, void** pp) {
    if (FAILED(hr) || !pp || !*pp) return;
    if (auto* so = so_id((IUnknown*)*pp)) {
        std::lock_guard l(g_mx);
        if (w.ok && !w.s.empty()) g_so_key[so] = key_of(w.tag, w.s);
        else g_so_key.erase(so);
    }
}
static HRESULT STDMETHODCALLTYPE hk_cso(ID3D12Device5* dev, const D3D12_STATE_OBJECT_DESC* d, REFIID riid, void** pp) {
    ENTRY; Writer w{'R'};
    if (!t_in && pp && d) write_so(w, *d);
    HRESULT hr = timed_create(pp, w, t_entry, [&] { return o_cso(dev, d, riid, pp); });
    remember_so(w, hr, pp);
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_addso(ID3D12Device7* dev, const D3D12_STATE_OBJECT_DESC* d, ID3D12StateObject* base, REFIID riid, void** pp) {
    ENTRY; Writer w{'A'};
    if (!t_in && pp && d) w.so_ref(base), write_so(w, *d);
    HRESULT hr = timed_create(pp, w, t_entry, [&] { return o_addso(dev, d, base, riid, pp); });
    remember_so(w, hr, pp);
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_loadgfx(ID3D12PipelineLibrary* lib, LPCWSTR name, const D3D12_GRAPHICS_PIPELINE_STATE_DESC* d, REFIID riid, void** pp) {
    ENTRY; Writer w{'G'};
    if (!t_in && pp && d) io_gfx(w, const_cast<D3D12_GRAPHICS_PIPELINE_STATE_DESC&>(*d));
    return timed_create(pp, w, t_entry, [&] { return o_loadgfx(lib, name, d, riid, pp); }, true);
}
static HRESULT STDMETHODCALLTYPE hk_loadcs(ID3D12PipelineLibrary* lib, LPCWSTR name, const D3D12_COMPUTE_PIPELINE_STATE_DESC* d, REFIID riid, void** pp) {
    ENTRY; Writer w{'C'};
    if (!t_in && pp && d) io_cs(w, const_cast<D3D12_COMPUTE_PIPELINE_STATE_DESC&>(*d));
    return timed_create(pp, w, t_entry, [&] { return o_loadcs(lib, name, d, riid, pp); }, true);
}
static HRESULT STDMETHODCALLTYPE hk_loadstream(ID3D12PipelineLibrary1* lib, LPCWSTR name, const D3D12_PIPELINE_STATE_STREAM_DESC* d, REFIID riid, void** pp) {
    ENTRY; Writer w{'S'};
    if (!t_in && pp && d) write_stream(w, *d);
    return timed_create(pp, w, t_entry, [&] { return o_loadstream(lib, name, d, riid, pp); }, true);
}
static HRESULT STDMETHODCALLTYPE hk_createlib(ID3D12Device1* dev, const void* blob, SIZE_T n, REFIID riid, void** pp) {
    HRESULT hr = o_createlib(dev, blob, n, riid, pp);
    ID3D12PipelineLibrary* lib;
    if (SUCCEEDED(hr) && pp && *pp && SUCCEEDED(((IUnknown*)*pp)->QueryInterface(IID_PPV_ARGS(&lib)))) {
        std::lock_guard l(g_mx);
        void** vt = *(void***)lib;
        patch(vt, LIB_LOADGFX, (void*)hk_loadgfx, o_loadgfx);
        patch(vt, LIB_LOADCS, (void*)hk_loadcs, o_loadcs);
        ID3D12PipelineLibrary1* l1;
        if (SUCCEEDED(lib->QueryInterface(IID_PPV_ARGS(&l1)))) patch(*(void***)l1, LIB_LOADSTREAM, (void*)hk_loadstream, o_loadstream), l1->Release();
        lib->Release();
    }
    return hr;
}

static void install_hooks(IUnknown* unk) {
    ID3D12Device* dev;
    if (FAILED(unk->QueryInterface(IID_PPV_ARGS(&dev)))) return;
    static std::once_flag once;
    std::call_once(once, [] {
        load_db();
        g_csv = _wfopen((g_dir + L"scskiller_creates.csv").c_str(), L"a");
        if (g_csv) {
            wchar_t p[MAX_PATH];
            GetModuleFileNameW(nullptr, p, MAX_PATH);
            std::wstring s = p;
            s = s.substr(s.find_last_of(L"\\/") + 1);
            long long unix = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
            fprintf(g_csv, "#session,%lld,%ls\n#clock,%.1f\n", unix, s.c_str(), now_ms());
            fflush(g_csv);
        }
    });
    std::lock_guard l(g_mx);
    void** vt = *(void***)dev;
    patch(vt, SLOT_GFX, (void*)hk_gfx, o_gfx);
    patch(vt, SLOT_CS, (void*)hk_cs, o_cs);
    patch(vt, SLOT_RS, (void*)hk_rs, o_rs);
    ID3D12Device1* d1;
    if (SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&d1)))) patch(*(void***)d1, SLOT_CREATELIB, (void*)hk_createlib, o_createlib), d1->Release();
    ID3D12Device2* d2;
    if (SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&d2)))) patch(*(void***)d2, SLOT_STREAM, (void*)hk_stream, o_stream), d2->Release();
    ID3D12Device5* d5;
    if (SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&d5)))) patch(*(void***)d5, SLOT_CSO, (void*)hk_cso, o_cso), d5->Release();
    ID3D12Device7* d7;
    if (SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&d7)))) patch(*(void***)d7, SLOT_ADDSO, (void*)hk_addso, o_addso), d7->Release();
    dev->Release();
}

// ---- admission: records only when the app armed this folder and no anti-cheat is beside the exe or loaded ----
static const wchar_t* const kAntiCheat[] = {L"EasyAntiCheat", L"EasyAntiCheat_EOS", L"start_protected_game.exe", L"BattlEye", L"BEService.exe",
    L"BEService_x64.exe", L"BEClient_x64.dll", L"BEClient.dll", L"GameGuard", L"XIGNCODE", L"nProtect", L"EAAntiCheat.Installer.exe",
    L"mhypbase.dll", L"vgk.sys", L"PunkBuster", L"DenuvoAC", L"denuvo-anti-cheat.sys"};
static std::atomic<int> g_admission;
static bool admitted() {
    static std::once_flag once;
    std::call_once(once, [] {
        const char* why = nullptr;
        wchar_t v[16];
        GetPrivateProfileStringW(L"scskiller", L"armed", L"", v, 16, (g_dir + L"scskiller.armed").c_str());
        if (wcscmp(v, L"1")) why = "not armed";
        for (auto m : kAntiCheat)
            if (!why && GetFileAttributesW((g_dir + m).c_str()) != INVALID_FILE_ATTRIBUTES) why = "anti-cheat next to the exe";
        for (auto m : {L"EasyAntiCheat_x64.dll", L"EasyAntiCheat_EOS.dll", L"BEClient_x64.dll", L"BEClient.dll"})
            if (!why && GetModuleHandleW(m)) why = "anti-cheat client loaded";
        if (!why) g_log = _wfopen((g_dir + L"scskiller.log").c_str(), L"a");
        g_admission = why ? -1 : 1;
        if (!why) {
            wchar_t p[MAX_PATH];
            GetModuleFileNameW(nullptr, p, MAX_PATH);
            logf("loaded into %ls (Linux/Proton recorder)", p);
        }
    });
    return g_admission > 0;
}

// raw jmp forwarders (stubs.asm on Windows)
#define REAL_EXPORTS(X)                                                                                         \
    X(D3D12CoreCreateLayeredDevice) X(D3D12CoreGetLayeredDeviceSize) X(D3D12CoreRegisterLayers)                 \
    X(D3D12CreateRootSignatureDeserializer) X(D3D12CreateVersionedRootSignatureDeserializer)                    \
    X(D3D12DeviceRemovedExtendedData) X(D3D12EnableExperimentalFeatures) X(D3D12GetDebugInterface)              \
    X(D3D12PIXEventsReplaceBlock) X(D3D12PIXGetThreadInfo) X(D3D12PIXNotifyWakeFromFenceSignal)                 \
    X(D3D12PIXReportCounter) X(D3D12SerializeRootSignature) X(D3D12SerializeVersionedRootSignature)            \
    X(GetBehaviorValue) X(SetAppCompatStringPointer) X(Ordinal99)
extern "C" {
#define DECL(n) void* real_##n;
REAL_EXPORTS(DECL)
#undef DECL
void* real_D3D12GetInterface;
}
#define STUB(n) asm(".text\n.globl stub_" #n "\nstub_" #n ":\n  jmp *real_" #n "(%rip)\n");
REAL_EXPORTS(STUB)
#undef STUB

using PFN_CreateDevice = HRESULT(WINAPI*)(IUnknown*, D3D_FEATURE_LEVEL, REFIID, void**);
static PFN_CreateDevice real_CreateDevice;

static void pin_self() {
    HMODULE self;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, (LPCWSTR)&pin_self, &self);
}
static HRESULT device_created(HRESULT hr, void** pp) {
    if (FAILED(hr) || !pp || !*pp || !admitted()) return hr;
    pin_self();
    install_hooks((IUnknown*)*pp);
    return hr;
}
extern "C" HRESULT WINAPI Proxy_D3D12CreateDevice(IUnknown* adapter, D3D_FEATURE_LEVEL fl, REFIID riid, void** pp) {
    if (!real_CreateDevice) return E_FAIL;
    return device_created(real_CreateDevice(adapter, fl, riid, pp), pp);
}

using PFN_FactoryCreateDevice = HRESULT(STDMETHODCALLTYPE*)(IUnknown*, IUnknown*, D3D_FEATURE_LEVEL, REFIID, void**);
using PFN_CreateDeviceFactory = HRESULT(STDMETHODCALLTYPE*)(IUnknown*, UINT, LPCSTR, REFIID, void**);
enum { SLOT_FACTORY_CREATEDEVICE = 9, SLOT_CREATEDEVICEFACTORY = 4 };
static PFN_FactoryCreateDevice o_factory_createdevice;
static PFN_CreateDeviceFactory o_createdevicefactory;
static HRESULT STDMETHODCALLTYPE hk_factory_createdevice(IUnknown* f, IUnknown* adapter, D3D_FEATURE_LEVEL fl, REFIID riid, void** pp) {
    return device_created(o_factory_createdevice(f, adapter, fl, riid, pp), pp);
}
static void hook_factory(IUnknown* unk) {
    ID3D12DeviceFactory* f;
    if (!unk || g_admission < 0 || FAILED(unk->QueryInterface(IID_PPV_ARGS(&f)))) return;
    pin_self();
    { std::lock_guard l(g_mx); patch(*(void***)f, SLOT_FACTORY_CREATEDEVICE, (void*)hk_factory_createdevice, o_factory_createdevice); }
    f->Release();
}
static HRESULT STDMETHODCALLTYPE hk_createdevicefactory(IUnknown* c, UINT sdk, LPCSTR path, REFIID riid, void** pp) {
    HRESULT hr = o_createdevicefactory(c, sdk, path, riid, pp);
    if (SUCCEEDED(hr) && pp) hook_factory((IUnknown*)*pp);
    return hr;
}
extern "C" HRESULT WINAPI Proxy_D3D12GetInterface(REFCLSID clsid, REFIID riid, void** pp) {
    if (!real_D3D12GetInterface) return E_NOINTERFACE;
    HRESULT hr = ((HRESULT(WINAPI*)(REFCLSID, REFIID, void**))real_D3D12GetInterface)(clsid, riid, pp);
    if (FAILED(hr) || !pp || !*pp || g_admission < 0) return hr;
    if (IsEqualGUID(clsid, kClsidDeviceFactory)) hook_factory((IUnknown*)*pp);
    ID3D12SDKConfiguration1* c;
    if (IsEqualGUID(clsid, kClsidSdkConfig) && SUCCEEDED(((IUnknown*)*pp)->QueryInterface(IID_PPV_ARGS(&c)))) {
        pin_self();
        { std::lock_guard l(g_mx); patch(*(void***)c, SLOT_CREATEDEVICEFACTORY, (void*)hk_createdevicefactory, o_createdevicefactory); }
        c->Release();
    }
    return hr;
}

// ScsKiller.IsOurProxy looks for this export's name; the warm side (scskiller_warm.exe) uses its own replay on Linux.
extern "C" void WINAPI SCSKiller_StartWarm(IUnknown*) {}
extern "C" void WINAPI SCSKiller_WarmOptions(int, BOOL, uint64_t) {}
extern "C" void WINAPI SCSKiller_WarmRt(int, const uint64_t*, uint32_t) {}
extern "C" void WINAPI SCSKiller_WarmCrash(const uint8_t*, uint32_t, const uint64_t*, uint32_t) {}
extern "C" BOOL WINAPI SCSKiller_Crashes(const char** k, const char** a) { *k = *a = ""; return FALSE; }
extern "C" void WINAPI SCSKiller_WarmMemory(uint32_t) {}
extern "C" void WINAPI SCSKiller_WarmPass(const uint8_t*, uint64_t, uint32_t) {}
extern "C" void WINAPI SCSKiller_Retry(uint64_t out[5]) { memset(out, 0, 5 * sizeof(uint64_t)); out[4] = ~0ull; }
extern "C" void WINAPI SCSKiller_Progress(uint64_t out[4]) { memset(out, 0, 4 * sizeof(uint64_t)); out[3] = 1; }
extern "C" void WINAPI SCSKiller_Control(int) {}
extern "C" void WINAPI SCSKiller_Stats(uint64_t out[7]) {
    std::lock_guard l(g_mx);
    uint64_t v[7] = {g_known.size(), g_keys.size(), 0, 0, g_known_hits, g_known_hits, 0};
    memcpy(out, v, sizeof v);
}

BOOL WINAPI DllMain(HINSTANCE self, DWORD reason, LPVOID) {
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(self);
    wchar_t p[MAX_PATH];
    std::wstring dir(p, GetModuleFileNameW(self, p, MAX_PATH));
    g_dir = dir.substr(0, dir.find_last_of(L"\\/") + 1);
    GetSystemDirectoryW(p, MAX_PATH);
    g_real = LoadLibraryW((std::wstring(p) + L"\\d3d12.dll").c_str());
    if (!g_real) return FALSE;
    // next=<file name>: a mod's d3d12.dll renamed next to us; its exports first
    wchar_t next[64];
    GetPrivateProfileStringW(L"scskiller", L"next", L"", next, 64, (g_dir + L"scskiller.ini").c_str());
    HMODULE mod = nullptr;
    if (*next && !wcspbrk(next, L"\\/:") && !wcsstr(next, L"..")) mod = LoadLibraryW((g_dir + next).c_str());
    if (mod == self || (mod && !GetProcAddress(mod, "D3D12CreateDevice"))) mod = nullptr;
    auto res = [mod](const char* n) { void* f = mod ? (void*)GetProcAddress(mod, n) : nullptr; return f ? f : (void*)GetProcAddress(g_real, n); };
#define RES(n) real_##n = res(#n);
    REAL_EXPORTS(RES)
#undef RES
    real_Ordinal99 = (void*)GetProcAddress(g_real, MAKEINTRESOURCEA(99));
    real_D3D12GetInterface = res("D3D12GetInterface");
    real_CreateDevice = (PFN_CreateDevice)res("D3D12CreateDevice");
    return TRUE;
}
