#define NOMINMAX
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <bcrypt.h>
#include <iostream>
#include <fstream>
#include <vector>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <deque>
#include <array>
#include <thread>
#include <atomic>
#include <mutex>
#include <functional>
#include <algorithm>
#include <iomanip>
#include <sstream>
#include <cstring>
#include <cstdarg>
#include <cstdio>

extern "C" {
    __declspec(dllexport) extern const UINT D3D12SDKVersion = 614;
    __declspec(dllexport) extern const char* D3D12SDKPath = ".\\D3D12\\";
}

// Required by warm11.cpp
void logf(const char* fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    vfprintf(stderr, fmt, ap);
    fprintf(stderr, "\n");
    va_end(ap);
}

// D3D11 warmer entrypoints from warm11.cpp
struct Dev11;
Dev11* warm11_open(LUID adapter, bool debug);
bool warm11_item(Dev11* d, uint32_t stage, const void* bytes, size_t n, const char** why);
bool warm11_pair(Dev11* d, const void* hs, size_t hsn, const void* ds, size_t dsn, const char** why);
void warm11_drain(Dev11* d, uint32_t stage);
HRESULT warm11_flush(Dev11* d);
void warm11_close(Dev11* d);

#ifndef D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL2
#define D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL2 ((D3D12_PIPELINE_STATE_SUBOBJECT_TYPE)28)
#define D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RASTERIZER1 ((D3D12_PIPELINE_STATE_SUBOBJECT_TYPE)29)
#define D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RASTERIZER2 ((D3D12_PIPELINE_STATE_SUBOBJECT_TYPE)30)

struct D3D12_DEPTH_STENCILOP_DESC1 {
    D3D12_STENCIL_OP StencilFailOp;
    D3D12_STENCIL_OP StencilDepthFailOp;
    D3D12_STENCIL_OP StencilPassOp;
    D3D12_COMPARISON_FUNC StencilFunc;
    UINT8 StencilReadMask;
    UINT8 StencilWriteMask;
};

struct D3D12_DEPTH_STENCIL_DESC2 {
    BOOL DepthEnable;
    D3D12_DEPTH_WRITE_MASK DepthWriteMask;
    D3D12_COMPARISON_FUNC DepthFunc;
    BOOL StencilEnable;
    D3D12_DEPTH_STENCILOP_DESC1 FrontFace;
    D3D12_DEPTH_STENCILOP_DESC1 BackFace;
    BOOL DepthBoundsTestEnable;
};

struct D3D12_RASTERIZER_DESC1 {
    D3D12_FILL_MODE FillMode;
    D3D12_CULL_MODE CullMode;
    BOOL FrontCounterClockwise;
    FLOAT DepthBias;
    FLOAT DepthBiasClamp;
    FLOAT SlopeScaledDepthBias;
    BOOL DepthClipEnable;
    BOOL MultisampleEnable;
    BOOL AntialiasedLineEnable;
    UINT ForcedSampleCount;
    D3D12_CONSERVATIVE_RASTERIZATION_MODE ConservativeRaster;
};

struct D3D12_RASTERIZER_DESC2 {
    D3D12_FILL_MODE FillMode;
    D3D12_CULL_MODE CullMode;
    BOOL FrontCounterClockwise;
    FLOAT DepthBias;
    FLOAT DepthBiasClamp;
    FLOAT SlopeScaledDepthBias;
    BOOL DepthClipEnable;
    int LineRasterizationMode;
    UINT ForcedSampleCount;
    D3D12_CONSERVATIVE_RASTERIZATION_MODE ConservativeRaster;
};
#endif

using Hash = std::array<uint8_t, 20>;
struct HashH { size_t operator()(const Hash& h) const { size_t v; memcpy(&v, h.data(), sizeof v); return v; } };
static const Hash kZero{};

static Hash sha1(const void* p, size_t n) {
    Hash h{};
    BCryptHash(BCRYPT_SHA1_ALG_HANDLE, nullptr, 0, (PUCHAR)p, (ULONG)n, h.data(), 20);
    return h;
}

static Hash key_of(char tag, std::string_view payload) {
    std::string s;
    s.reserve(payload.size() + 1);
    s.push_back(tag);
    s.append(payload.data(), payload.size());
    return sha1(s.data(), s.size());
}

static size_t align_up(size_t v, size_t a) { return (v + a - 1) & ~(a - 1); }

struct Rec {
    char tag;
    std::string_view payload;
};

struct Override {
    Hash rs{};
    Hash sh[32]{};
    bool has_il = false;
    D3D12_INPUT_LAYOUT_DESC il{};
};

struct Sub { size_t size, align; };
static bool sub_info(uint32_t t, Sub& o) {
    switch (t) {
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_ROOT_SIGNATURE: o = {sizeof(ID3D12RootSignature*), alignof(ID3D12RootSignature*)}; return true;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_VS:
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_PS:
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DS:
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_HS:
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_GS:
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_CS:
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_AS:
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_MS: o = {sizeof(D3D12_SHADER_BYTECODE), alignof(D3D12_SHADER_BYTECODE)}; return true;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_STREAM_OUTPUT: o = {sizeof(D3D12_STREAM_OUTPUT_DESC), alignof(D3D12_STREAM_OUTPUT_DESC)}; return true;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_BLEND: o = {sizeof(D3D12_BLEND_DESC), alignof(D3D12_BLEND_DESC)}; return true;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_SAMPLE_MASK: o = {sizeof(UINT), alignof(UINT)}; return true;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RASTERIZER: o = {sizeof(D3D12_RASTERIZER_DESC), alignof(D3D12_RASTERIZER_DESC)}; return true;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL: o = {sizeof(D3D12_DEPTH_STENCIL_DESC), alignof(D3D12_DEPTH_STENCIL_DESC)}; return true;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_INPUT_LAYOUT: o = {sizeof(D3D12_INPUT_LAYOUT_DESC), alignof(D3D12_INPUT_LAYOUT_DESC)}; return true;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_IB_STRIP_CUT_VALUE: o = {sizeof(D3D12_INDEX_BUFFER_STRIP_CUT_VALUE), alignof(D3D12_INDEX_BUFFER_STRIP_CUT_VALUE)}; return true;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_PRIMITIVE_TOPOLOGY: o = {sizeof(D3D12_PRIMITIVE_TOPOLOGY_TYPE), alignof(D3D12_PRIMITIVE_TOPOLOGY_TYPE)}; return true;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RENDER_TARGET_FORMATS: o = {sizeof(D3D12_RT_FORMAT_ARRAY), alignof(D3D12_RT_FORMAT_ARRAY)}; return true;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL_FORMAT: o = {sizeof(DXGI_FORMAT), alignof(DXGI_FORMAT)}; return true;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_SAMPLE_DESC: o = {sizeof(DXGI_SAMPLE_DESC), alignof(DXGI_SAMPLE_DESC)}; return true;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_NODE_MASK: o = {sizeof(UINT), alignof(UINT)}; return true;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_CACHED_PSO: o = {sizeof(D3D12_CACHED_PIPELINE_STATE), alignof(D3D12_CACHED_PIPELINE_STATE)}; return true;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_FLAGS: o = {sizeof(D3D12_PIPELINE_STATE_FLAGS), alignof(D3D12_PIPELINE_STATE_FLAGS)}; return true;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL1: o = {sizeof(D3D12_DEPTH_STENCIL_DESC1), alignof(D3D12_DEPTH_STENCIL_DESC1)}; return true;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_VIEW_INSTANCING: o = {sizeof(D3D12_VIEW_INSTANCING_DESC), alignof(D3D12_VIEW_INSTANCING_DESC)}; return true;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL2: o = {sizeof(D3D12_DEPTH_STENCIL_DESC2), alignof(D3D12_DEPTH_STENCIL_DESC2)}; return true;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RASTERIZER1: o = {sizeof(D3D12_RASTERIZER_DESC1), alignof(D3D12_RASTERIZER_DESC1)}; return true;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RASTERIZER2: o = {sizeof(D3D12_RASTERIZER_DESC2), alignof(D3D12_RASTERIZER_DESC2)}; return true;
    }
    return false;
}

static const uint32_t kSoDecl = 0x10007;

#define DXBC_CHECKSUM_BLOCK_SIZE 64

struct md5_ctx {
    unsigned int i[2];
    unsigned int buf[4];
    unsigned char in[DXBC_CHECKSUM_BLOCK_SIZE];
    unsigned char digest[16];
};

#define F1(x, y, z) (z ^ (x & (y ^ z)))
#define F2(x, y, z) F1(z, x, y)
#define F3(x, y, z) (x ^ y ^ z)
#define F4(x, y, z) (y ^ (x | ~z))

#define MD5STEP(f, w, x, y, z, data, s) \
        (w += f(x, y, z) + data,  w = w << s | w >> (32 - s),  w += x)

static void md5_transform(unsigned int buf[4], const unsigned int in[16]) {
    unsigned int a = buf[0], b = buf[1], c = buf[2], d = buf[3];

    MD5STEP(F1, a, b, c, d, in[0] + 0xd76aa478, 7);
    MD5STEP(F1, d, a, b, c, in[1] + 0xe8c7b756, 12);
    MD5STEP(F1, c, d, a, b, in[2] + 0x242070db, 17);
    MD5STEP(F1, b, c, d, a, in[3] + 0xc1bdceee, 22);
    MD5STEP(F1, a, b, c, d, in[4] + 0xf57c0faf, 7);
    MD5STEP(F1, d, a, b, c, in[5] + 0x4787c62a, 12);
    MD5STEP(F1, c, d, a, b, in[6] + 0xa8304613, 17);
    MD5STEP(F1, b, c, d, a, in[7] + 0xfd469501, 22);
    MD5STEP(F1, a, b, c, d, in[8] + 0x698098d8, 7);
    MD5STEP(F1, d, a, b, c, in[9] + 0x8b44f7af, 12);
    MD5STEP(F1, c, d, a, b, in[10] + 0xffff5bb1, 17);
    MD5STEP(F1, b, c, d, a, in[11] + 0x895cd7be, 22);
    MD5STEP(F1, a, b, c, d, in[12] + 0x6b901122, 7);
    MD5STEP(F1, d, a, b, c, in[13] + 0xfd987193, 12);
    MD5STEP(F1, c, d, a, b, in[14] + 0xa679438e, 17);
    MD5STEP(F1, b, c, d, a, in[15] + 0x49b40821, 22);

    MD5STEP(F2, a, b, c, d, in[1] + 0xf61e2562, 5);
    MD5STEP(F2, d, a, b, c, in[6] + 0xc040b340, 9);
    MD5STEP(F2, c, d, a, b, in[11] + 0x265e5a51, 14);
    MD5STEP(F2, b, c, d, a, in[0] + 0xe9b6c7aa, 20);
    MD5STEP(F2, a, b, c, d, in[5] + 0xd62f105d, 5);
    MD5STEP(F2, d, a, b, c, in[10] + 0x02441453, 9);
    MD5STEP(F2, c, d, a, b, in[15] + 0xd8a1e681, 14);
    MD5STEP(F2, b, c, d, a, in[4] + 0xe7d3fbc8, 20);
    MD5STEP(F2, a, b, c, d, in[9] + 0x21e1cde6, 5);
    MD5STEP(F2, d, a, b, c, in[14] + 0xc33707d6, 9);
    MD5STEP(F2, c, d, a, b, in[3] + 0xf4d50d87, 14);
    MD5STEP(F2, b, c, d, a, in[8] + 0x455a14ed, 20);
    MD5STEP(F2, a, b, c, d, in[13] + 0xa9e3e905, 5);
    MD5STEP(F2, d, a, b, c, in[2] + 0xfcefa3f8, 9);
    MD5STEP(F2, c, d, a, b, in[7] + 0x676f02d9, 14);
    MD5STEP(F2, b, c, d, a, in[12] + 0x8d2a4c8a, 20);

    MD5STEP(F3, a, b, c, d, in[5] + 0xfffa3942, 4);
    MD5STEP(F3, d, a, b, c, in[8] + 0x8771f681, 11);
    MD5STEP(F3, c, d, a, b, in[11] + 0x6d9d6122, 16);
    MD5STEP(F3, b, c, d, a, in[14] + 0xfde5380c, 23);
    MD5STEP(F3, a, b, c, d, in[1] + 0xa4beea44, 4);
    MD5STEP(F3, d, a, b, c, in[4] + 0x4bdecfa9, 11);
    MD5STEP(F3, c, d, a, b, in[7] + 0xf6bb4b60, 16);
    MD5STEP(F3, b, c, d, a, in[10] + 0xbebfbc70, 23);
    MD5STEP(F3, a, b, c, d, in[13] + 0x289b7ec6, 4);
    MD5STEP(F3, d, a, b, c, in[0] + 0xeaa127fa, 11);
    MD5STEP(F3, c, d, a, b, in[3] + 0xd4ef3085, 16);
    MD5STEP(F3, b, c, d, a, in[6] + 0x04881d05, 23);
    MD5STEP(F3, a, b, c, d, in[9] + 0xd9d4d039, 4);
    MD5STEP(F3, d, a, b, c, in[12] + 0xe6db99e5, 11);
    MD5STEP(F3, c, d, a, b, in[15] + 0x1fa27cf8, 16);
    MD5STEP(F3, b, c, d, a, in[2] + 0xc4ac5665, 23);

    MD5STEP(F4, a, b, c, d, in[0] + 0xf4292244, 6);
    MD5STEP(F4, d, a, b, c, in[7] + 0x432aff97, 10);
    MD5STEP(F4, c, d, a, b, in[14] + 0xab9423a7, 15);
    MD5STEP(F4, b, c, d, a, in[5] + 0xfc93a039, 21);
    MD5STEP(F4, a, b, c, d, in[12] + 0x655b59c3, 6);
    MD5STEP(F4, d, a, b, c, in[3] + 0x8f0ccc92, 10);
    MD5STEP(F4, c, d, a, b, in[10] + 0xffeff47d, 15);
    MD5STEP(F4, b, c, d, a, in[1] + 0x85845dd1, 21);
    MD5STEP(F4, a, b, c, d, in[8] + 0x6fa87e4f, 6);
    MD5STEP(F4, d, a, b, c, in[15] + 0xfe2ce6e0, 10);
    MD5STEP(F4, c, d, a, b, in[6] + 0xa3014314, 15);
    MD5STEP(F4, b, c, d, a, in[13] + 0x4e0811a1, 21);
    MD5STEP(F4, a, b, c, d, in[4] + 0xf7537e82, 6);
    MD5STEP(F4, d, a, b, c, in[11] + 0xbd3af235, 10);
    MD5STEP(F4, c, d, a, b, in[2] + 0x2ad7d2bb, 15);
    MD5STEP(F4, b, c, d, a, in[9] + 0xeb86d391, 21);

    buf[0] += a; buf[1] += b; buf[2] += c; buf[3] += d;
}

static void byte_reverse(unsigned char *buf, unsigned longs) {
    uint32_t t;
    do {
        t = (uint32_t)buf[0] | ((uint32_t)buf[1] << 8) | ((uint32_t)buf[2] << 16) | ((uint32_t)buf[3] << 24);
        *(uint32_t *)buf = t;
        buf += 4;
    } while (--longs);
}

static void md5_init(struct md5_ctx *ctx) {
    ctx->buf[0] = 0x67452301;
    ctx->buf[1] = 0xefcdab89;
    ctx->buf[2] = 0x98badcfe;
    ctx->buf[3] = 0x10325476;
    ctx->i[0] = ctx->i[1] = 0;
}

static void md5_update(struct md5_ctx *ctx, const unsigned char *buf, unsigned int len) {
    unsigned int t = ctx->i[0];
    if ((ctx->i[0] = t + (len << 3)) < t) ctx->i[1]++;
    ctx->i[1] += len >> 29;
    t = (t >> 3) & 0x3f;
    if (t) {
        unsigned char *p = (unsigned char *)ctx->in + t;
        t = DXBC_CHECKSUM_BLOCK_SIZE - t;
        if (len < t) { memcpy(p, buf, len); return; }
        memcpy(p, buf, t);
        byte_reverse(ctx->in, 16);
        md5_transform(ctx->buf, (unsigned int *)ctx->in);
        buf += t; len -= t;
    }
    while (len >= DXBC_CHECKSUM_BLOCK_SIZE) {
        memcpy(ctx->in, buf, DXBC_CHECKSUM_BLOCK_SIZE);
        byte_reverse(ctx->in, 16);
        md5_transform(ctx->buf, (unsigned int *)ctx->in);
        buf += DXBC_CHECKSUM_BLOCK_SIZE; len -= DXBC_CHECKSUM_BLOCK_SIZE;
    }
    memcpy(ctx->in, buf, len);
}

static void md5_final_dxbc(struct md5_ctx *ctx) {
    unsigned int count = (ctx->i[0] >> 3) & 0x3F;
    unsigned char *p = ctx->in + count;
    *p++ = 0x80;
    ++count;
    unsigned int padding = DXBC_CHECKSUM_BLOCK_SIZE - count;
    if (padding < 8) {
        memset(p, 0, padding);
        byte_reverse(ctx->in, 16);
        md5_transform(ctx->buf, (unsigned int *)ctx->in);
        memset(ctx->in, 0, DXBC_CHECKSUM_BLOCK_SIZE);
    } else {
        memmove(&ctx->in[4], ctx->in, count);
        memset(p + 4, 0, padding - 4);
    }
    unsigned int length = ctx->i[0];
    memcpy(&ctx->in[0], &length, sizeof(length));
    byte_reverse(&ctx->in[4], 14);
    length = (ctx->i[0] >> 2) | 0x1;
    memcpy(&ctx->in[DXBC_CHECKSUM_BLOCK_SIZE - 4], &length, sizeof(length));
    md5_transform(ctx->buf, (unsigned int *)ctx->in);
    byte_reverse((unsigned char *)ctx->buf, 4);
    memcpy(ctx->digest, ctx->buf, 16);
}

static void compute_dxbc_checksum(const void *data, size_t size, uint32_t checksum[4]) {
    struct md5_ctx ctx;
    md5_init(&ctx);
    md5_update(&ctx, (const uint8_t*)data, size);
    md5_final_dxbc(&ctx);
    memcpy(checksum, ctx.digest, 16);
}

// Zero-copy memory-mapped blob table mapping hash -> string_view in OS page cache
static std::unordered_map<Hash, std::string_view, HashH> g_blobs;
static std::mutex g_rsMutex;
static std::unordered_map<Hash, ID3D12RootSignature*, HashH> g_rootSigs;

// Lock is ONLY held during map lookup/insert, NOT during CreateRootSignature!
static ID3D12RootSignature* get_root_sig(ID3D12Device* device, const Hash& h) {
    if (h == kZero) return nullptr;
    {
        std::lock_guard<std::mutex> lock(g_rsMutex);
        auto it = g_rootSigs.find(h);
        if (it != g_rootSigs.end()) return it->second;
    }

    auto bit = g_blobs.find(h);
    if (bit == g_blobs.end()) return nullptr;

    std::string blob(bit->second.data(), bit->second.size());
    if (blob.size() >= 20 && memcmp(blob.data(), "DXBC", 4) == 0) {
        uint32_t* chk = (uint32_t*)(blob.data() + 4);
        if (chk[0] == 0 && chk[1] == 0 && chk[2] == 0 && chk[3] == 0) {
            compute_dxbc_checksum(blob.data() + 20, blob.size() - 20, chk);
        }
    } else if (blob.size() >= 24 && memcmp(blob.data(), "DXBC", 4) != 0) {
        uint32_t totalSize = (uint32_t)(32 + 4 + 8 + blob.size());
        std::string wrapped(totalSize, '\0');
        memcpy(wrapped.data(), "DXBC", 4);
        *(uint32_t*)(wrapped.data() + 20) = 1;
        *(uint32_t*)(wrapped.data() + 24) = totalSize;
        *(uint32_t*)(wrapped.data() + 28) = 1;
        *(uint32_t*)(wrapped.data() + 32) = 36;
        memcpy(wrapped.data() + 36, "RTS0", 4);
        *(uint32_t*)(wrapped.data() + 40) = (uint32_t)blob.size();
        memcpy(wrapped.data() + 44, blob.data(), blob.size());
        compute_dxbc_checksum(wrapped.data() + 20, wrapped.size() - 20, (uint32_t*)(wrapped.data() + 4));
        blob = std::move(wrapped);
    }

    ID3D12RootSignature* rs = nullptr;
    HRESULT hr = device->CreateRootSignature(0, blob.data(), blob.size(), IID_PPV_ARGS(&rs));
    if (FAILED(hr) || !rs) {
        ID3D12VersionedRootSignatureDeserializer* deser = nullptr;
        if (SUCCEEDED(D3D12CreateVersionedRootSignatureDeserializer(blob.data(), blob.size(), IID_PPV_ARGS(&deser))) && deser) {
            const D3D12_VERSIONED_ROOT_SIGNATURE_DESC* desc = nullptr;
            if (SUCCEEDED(deser->GetRootSignatureDescAtVersion(D3D_ROOT_SIGNATURE_VERSION_1_1, &desc)) ||
                SUCCEEDED(deser->GetRootSignatureDescAtVersion(D3D_ROOT_SIGNATURE_VERSION_1_0, &desc))) {
                ID3DBlob* serBlob = nullptr;
                ID3DBlob* errBlob = nullptr;
                if (SUCCEEDED(D3D12SerializeVersionedRootSignature(desc, &serBlob, &errBlob)) && serBlob) {
                    hr = device->CreateRootSignature(0, serBlob->GetBufferPointer(), serBlob->GetBufferSize(), IID_PPV_ARGS(&rs));
                    serBlob->Release();
                }
                if (errBlob) errBlob->Release();
            }
            deser->Release();
        }
    }

    if (SUCCEEDED(hr) && rs) {
        std::lock_guard<std::mutex> lock(g_rsMutex);
        auto [it, fresh] = g_rootSigs.try_emplace(h, rs);
        if (!fresh) rs->Release();
        return it->second;
    }
    return nullptr;
}

struct Reader {
    std::string_view s;
    size_t pos = 0;
    bool ok = true;
    const Override* ovr = nullptr;
    ID3D12Device* device = nullptr;
    std::deque<std::string> strs;
    std::deque<std::wstring> wstrs;
    std::deque<std::vector<D3D12_INPUT_ELEMENT_DESC>> ies;
    std::deque<std::vector<D3D12_SO_DECLARATION_ENTRY>> soes;
    std::deque<std::vector<UINT>> strides;
    std::deque<std::vector<D3D12_VIEW_INSTANCE_LOCATION>> vils;
    bool so_decl = false;
    std::string why;
    void fail(const char* w) { if (ok) why = w; ok = false; }

    void raw(void* p, size_t n) {
        if (pos + n > s.size()) { fail("record shorter than its fields"); memset(p, 0, n); return; }
        memcpy(p, s.data() + pos, n);
        pos += n;
    }
    template <class T> void pod(T& v) { raw(&v, sizeof v); }
    uint32_t u32() { uint32_t v = 0; raw(&v, 4); return v; }
    uint32_t count() { uint32_t n = u32(); if (n > s.size()) fail("malformed count"); return ok ? n : 0; }
    Hash hash() { Hash h{}; raw(h.data(), 20); return h; }

    LPCWSTR wstr() {
        uint32_t n = u32();
        if (n == 0xFFFFFFFF || !ok) return nullptr;
        if (2ull * n > s.size() - std::min(pos, s.size())) { fail("malformed string length"); return nullptr; }
        auto& w = wstrs.emplace_back(n, L'\0');
        raw(w.data(), 2 * n);
        return w.c_str();
    }

    void lib(D3D12_SHADER_BYTECODE& b) {
        Hash h = hash();
        b = {};
        auto it = g_blobs.find(h);
        if (it == g_blobs.end()) { fail("DXIL library blob not in db"); return; }
        b = { it->second.data(), it->second.size() };
    }

    void sh(D3D12_SHADER_BYTECODE& b, uint32_t stage) {
        Hash h = hash();
        if (ovr && stage < 32 && ovr->sh[stage] != kZero) h = ovr->sh[stage];
        b = {};
        if (h == kZero) return;
        auto it = g_blobs.find(h);
        if (it != g_blobs.end()) {
            b = { it->second.data(), it->second.size() };
        } else {
            fail("shader blob missing in db");
        }
    }

    void rs(ID3D12RootSignature*& p) {
        Hash h = hash();
        if (ovr && ovr->rs != kZero) h = ovr->rs;
        p = (h == kZero) ? nullptr : get_root_sig(device, h);
        if (h != kZero && !p) fail("root signature creation/lookup failed");
    }

    void il(D3D12_INPUT_LAYOUT_DESC& d) {
        auto& v = ies.emplace_back(count());
        for (auto& e : v) {
            auto& name = strs.emplace_back(count(), '\0');
            raw(name.data(), name.size());
            e.SemanticName = name.c_str();
            e.SemanticIndex = u32(); e.Format = (DXGI_FORMAT)u32(); e.InputSlot = u32(); e.AlignedByteOffset = u32();
            e.InputSlotClass = (D3D12_INPUT_CLASSIFICATION)u32(); e.InstanceDataStepRate = u32();
        }
        d = { v.empty() ? nullptr : v.data(), (UINT)v.size() };
        if (ovr && ovr->has_il) d = ovr->il;
    }

    void so(D3D12_STREAM_OUTPUT_DESC& d) {
        d = {};
        if (!so_decl) return;
        so_decl = false;
        auto& v = soes.emplace_back(count());
        for (auto& e : v) {
            e.Stream = u32();
            uint32_t n = u32();
            if (n != 0xFFFFFFFF) {
                if (n > s.size()) { ok = false; return; }
                auto& name = strs.emplace_back(n, '\0');
                raw(name.data(), n);
                e.SemanticName = name.c_str();
            }
            e.SemanticIndex = u32(); e.StartComponent = (BYTE)u32(); e.ComponentCount = (BYTE)u32(); e.OutputSlot = (BYTE)u32();
        }
        auto& st = strides.emplace_back(count());
        for (auto& x : st) x = u32();
        d = { v.data(), (UINT)v.size(), st.empty() ? nullptr : st.data(), (UINT)st.size(), u32() };
    }

    void so_tail(D3D12_STREAM_OUTPUT_DESC& d) { so_decl = pos < s.size(); so(d); }

    void vi(D3D12_VIEW_INSTANCING_DESC& d) {
        auto& v = vils.emplace_back(count());
        for (auto& l : v) pod(l);
        d.ViewInstanceCount = (UINT)v.size();
        d.pViewInstanceLocations = v.empty() ? nullptr : v.data();
        d.Flags = (D3D12_VIEW_INSTANCING_FLAGS)u32();
    }

    void rtfmt(UINT& n, DXGI_FORMAT* f) { n = u32(); for (UINT i = 0; i < 8; ++i) f[i] = (DXGI_FORMAT)u32(); }
    template <class T> void ds(T& v) { pod(v); }
};

static void io_sub(Reader& r, uint32_t t, void* p) {
    switch (t) {
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_ROOT_SIGNATURE: r.rs(*(ID3D12RootSignature**)p); break;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_VS: case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_PS:
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DS: case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_HS:
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_GS: case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_CS:
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_AS: case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_MS:
        r.sh(*(D3D12_SHADER_BYTECODE*)p, t); break;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_STREAM_OUTPUT: r.so(*(D3D12_STREAM_OUTPUT_DESC*)p); break;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_INPUT_LAYOUT: r.il(*(D3D12_INPUT_LAYOUT_DESC*)p); break;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_VIEW_INSTANCING: r.vi(*(D3D12_VIEW_INSTANCING_DESC*)p); break;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RENDER_TARGET_FORMATS: {
        auto& res = *(D3D12_RT_FORMAT_ARRAY*)p;
        r.rtfmt(res.NumRenderTargets, res.RTFormats);
        break;
    }
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL: r.ds(*(D3D12_DEPTH_STENCIL_DESC*)p); break;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL1: r.ds(*(D3D12_DEPTH_STENCIL_DESC1*)p); break;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL2: r.ds(*(D3D12_DEPTH_STENCIL_DESC2*)p); break;
    default: { Sub s{}; if (sub_info(t, s)) r.raw(p, s.size); }
    }
}

static D3D12_PIPELINE_STATE_STREAM_DESC read_stream(Reader& r, std::vector<uint64_t>& buf) {
    size_t pos = 0;
    for (uint32_t i = 0, n = r.count(); i < n && r.ok; ++i) {
        uint32_t t = r.u32();
        if ((r.so_decl = (t == kSoDecl))) t = D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_STREAM_OUTPUT;
        Sub s{};
        if (!sub_info(t, s)) { r.fail("unknown stream subobject type"); break; }
        size_t in = align_up(pos + 4, s.align), end = align_up(in + s.size, sizeof(void*));
        buf.resize(end / 8);
        auto b = (uint8_t*)buf.data();
        memcpy(b + pos, &t, 4);
        io_sub(r, t, b + in);
        pos = end;
    }
    return { pos, buf.data() };
}

static void io_gfx(Reader& r, D3D12_GRAPHICS_PIPELINE_STATE_DESC& d) {
    r.rs(d.pRootSignature);
    r.sh(d.VS, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_VS); r.sh(d.PS, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_PS);
    r.sh(d.DS, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DS); r.sh(d.HS, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_HS);
    r.sh(d.GS, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_GS);
    r.pod(d.BlendState); r.pod(d.SampleMask); r.pod(d.RasterizerState); r.ds(d.DepthStencilState);
    r.il(d.InputLayout);
    r.pod(d.IBStripCutValue); r.pod(d.PrimitiveTopologyType);
    r.rtfmt(d.NumRenderTargets, d.RTVFormats);
    r.pod(d.DSVFormat); r.pod(d.SampleDesc); r.pod(d.NodeMask); r.pod(d.Flags);
    r.so_tail(d.StreamOutput);
}

static void io_cs(Reader& r, D3D12_COMPUTE_PIPELINE_STATE_DESC& d) {
    r.rs(d.pRootSignature); r.sh(d.CS, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_CS); r.pod(d.NodeMask); r.pod(d.Flags);
}

// DXR Ray Tracing State Object ('R' / 'A') support
struct SoDesc {
    D3D12_STATE_OBJECT_TYPE type{};
    std::vector<D3D12_STATE_SUBOBJECT> subs;
    std::deque<std::vector<D3D12_EXPORT_DESC>> exps;
    std::deque<std::vector<LPCWSTR>> lists;
    std::deque<std::array<uint32_t, 2>> pods;
    std::deque<ID3D12RootSignature*> rsigs;
    std::deque<D3D12_DXIL_LIBRARY_DESC> libs;
    std::deque<D3D12_EXISTING_COLLECTION_DESC> colls;
    std::deque<D3D12_SUBOBJECT_TO_EXPORTS_ASSOCIATION> assoc;
    std::deque<D3D12_DXIL_SUBOBJECT_TO_EXPORTS_ASSOCIATION> dassoc;
    std::deque<D3D12_HIT_GROUP_DESC> hgs;
};

static void read_so(Reader& r, SoDesc& d, const std::function<ID3D12StateObject*(const Hash&)>& dep) {
    d.type = (D3D12_STATE_OBJECT_TYPE)r.u32();
    UINT n = r.count();
    d.subs.assign(n, {});
    auto exports = [&](UINT& num, auto& ptr) {
        auto& v = d.exps.emplace_back(r.count());
        for (auto& e : v) { e.Name = r.wstr(); e.ExportToRename = r.wstr(); e.Flags = (D3D12_EXPORT_FLAGS)r.u32(); }
        num = (UINT)v.size(); ptr = v.empty() ? nullptr : v.data();
    };
    auto names = [&](UINT& num, auto& ptr) {
        auto& v = d.lists.emplace_back(r.count());
        for (auto& x : v) x = r.wstr();
        num = (UINT)v.size(); ptr = v.empty() ? nullptr : v.data();
    };
    auto pod = [&](int words) {
        auto& a = d.pods.emplace_back();
        for (int i = 0; i < words; ++i) a[i] = r.u32();
        return (const void*)a.data();
    };
    for (UINT i = 0; i < n && r.ok; ++i) {
        auto t = (D3D12_STATE_SUBOBJECT_TYPE)r.u32();
        const void* desc = nullptr;
        switch (t) {
        case D3D12_STATE_SUBOBJECT_TYPE_STATE_OBJECT_CONFIG:
        case D3D12_STATE_SUBOBJECT_TYPE_NODE_MASK:
        case D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_PIPELINE_CONFIG: desc = pod(1); break;
        case D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_SHADER_CONFIG:
        case D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_PIPELINE_CONFIG1: desc = pod(2); break;
        case D3D12_STATE_SUBOBJECT_TYPE_GLOBAL_ROOT_SIGNATURE:
        case D3D12_STATE_SUBOBJECT_TYPE_LOCAL_ROOT_SIGNATURE: { auto& x = d.rsigs.emplace_back(); r.rs(x); desc = &x; break; }
        case D3D12_STATE_SUBOBJECT_TYPE_DXIL_LIBRARY: { auto& x = d.libs.emplace_back(); r.lib(x.DXILLibrary); exports(x.NumExports, x.pExports); desc = &x; break; }
        case D3D12_STATE_SUBOBJECT_TYPE_EXISTING_COLLECTION: {
            auto& x = d.colls.emplace_back();
            Hash k = r.hash();
            if (r.ok) x.pExistingCollection = dep(k);
            exports(x.NumExports, x.pExports);
            desc = &x;
            break;
        }
        case D3D12_STATE_SUBOBJECT_TYPE_SUBOBJECT_TO_EXPORTS_ASSOCIATION: {
            auto& x = d.assoc.emplace_back();
            uint32_t k = r.u32();
            if (k >= n) r.fail("association to a subobject outside the desc");
            else x.pSubobjectToAssociate = &d.subs[k];
            names(x.NumExports, x.pExports);
            desc = &x;
            break;
        }
        case D3D12_STATE_SUBOBJECT_TYPE_DXIL_SUBOBJECT_TO_EXPORTS_ASSOCIATION: {
            auto& x = d.dassoc.emplace_back();
            x.SubobjectToAssociate = r.wstr(); names(x.NumExports, x.pExports);
            desc = &x;
            break;
        }
        case D3D12_STATE_SUBOBJECT_TYPE_HIT_GROUP: {
            auto& x = d.hgs.emplace_back();
            x.HitGroupExport = r.wstr(); x.Type = (D3D12_HIT_GROUP_TYPE)r.u32();
            x.AnyHitShaderImport = r.wstr(); x.ClosestHitShaderImport = r.wstr(); x.IntersectionShaderImport = r.wstr();
            desc = &x;
            break;
        }
        default: r.fail("unknown state subobject type");
        }
        d.subs[i] = {t, desc};
    }
    if (r.ok && r.pos != r.s.size()) r.fail("state object record longer than its subobjects");
}

struct SoSlot {
    std::once_flag once;
    HRESULT hr = E_FAIL;
    ID3D12StateObject* obj = nullptr;
};

static const Rec* parse_plan(Reader& r, Override& o, const std::unordered_map<Hash, size_t, HashH>& recIdx, const std::vector<Rec>& recs) {
    Hash tmpl = r.hash();
    o.rs = r.hash();
    for (uint32_t i = 0, n = r.count(); i < n && r.ok; ++i) {
        uint32_t st = r.u32();
        Hash h = r.hash();
        if (st < 32) o.sh[st] = h;
    }
    uint32_t keep = 0xFFFFFFFF;
    if (r.pos + 4 <= r.s.size() && memcmp(r.s.data() + r.pos, &keep, 4)) {
        r.il(o.il);
        o.has_il = true;
    }
    auto it = recIdx.find(tmpl);
    return (r.ok && it != recIdx.end()) ? &recs[it->second] : nullptr;
}

struct MappedFile {
    const char* base = nullptr;
    size_t size = 0;
};

int wmain(int argc, wchar_t** argv) {
    std::wstring workDir = L".";
    std::wstring exeName = L"game.exe";
    int threads = std::max(2u, std::thread::hardware_concurrency());
    size_t startAt = 0;

    if (argc > 1) workDir = argv[1];
    if (argc > 2) exeName = argv[2];
    for (int i = 3; i < argc; ++i) {
        std::wstring arg = argv[i];
        if (arg == L"--threads" && i + 1 < argc) {
            threads = _wtoi(argv[++i]);
            if (threads <= 0) threads = 4;
        } else if (arg == L"--start" && i + 1 < argc) {
            startAt = (size_t)_wtoi64(argv[++i]);
        }
    }

    std::cout << "{\"event\":\"init\",\"threads\":" << threads << "}" << std::endl;

    IDXGIFactory1* factory = nullptr;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) {
        std::cout << "{\"event\":\"error\",\"message\":\"Failed to create DXGI factory\"}" << std::endl;
        return 1;
    }

    IDXGIAdapter1* bestAdapter = nullptr;
    IDXGIAdapter1* adapter = nullptr;
    SIZE_T maxVram = 0;

    for (UINT i = 0; factory->EnumAdapters1(i, &adapter) != DXGI_ERROR_NOT_FOUND; ++i) {
        DXGI_ADAPTER_DESC1 desc{};
        adapter->GetDesc1(&desc);
        if (!(desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE)) {
            if (!bestAdapter || desc.DedicatedVideoMemory > maxVram) {
                bestAdapter = adapter;
                maxVram = desc.DedicatedVideoMemory;
            }
        }
    }

    if (!bestAdapter) {
        std::cout << "{\"event\":\"error\",\"message\":\"No hardware GPU adapter found\"}" << std::endl;
        return 1;
    }

    ID3D12Device* device = nullptr;
    if (FAILED(D3D12CreateDevice(bestAdapter, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device))) || !device) {
        std::cout << "{\"event\":\"error\",\"message\":\"Failed to create D3D12 device\"}" << std::endl;
        return 1;
    }

    ID3D12Device2* device2 = nullptr;
    ID3D12Device5* device5 = nullptr;
    ID3D12Device7* device7 = nullptr;
    device->QueryInterface(IID_PPV_ARGS(&device2));
    device->QueryInterface(IID_PPV_ARGS(&device5));
    device->QueryInterface(IID_PPV_ARGS(&device7));

    std::vector<MappedFile> mappedFiles;
    std::vector<Rec> recs;
    std::unordered_map<Hash, size_t, HashH> recIdx;
    std::vector<std::string_view> planItems;
    std::vector<std::string_view> items11;

    auto loadDbMapped = [&](const std::wstring& path) {
        HANDLE hFile = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
        if (hFile == INVALID_HANDLE_VALUE) return;
        LARGE_INTEGER li{};
        if (!GetFileSizeEx(hFile, &li) || li.QuadPart <= 0) { CloseHandle(hFile); return; }
        size_t flen = (size_t)li.QuadPart;

        HANDLE hMap = CreateFileMappingW(hFile, nullptr, PAGE_READONLY, 0, 0, nullptr);
        CloseHandle(hFile);
        if (!hMap) return;

        auto base = (const char*)MapViewOfFile(hMap, FILE_MAP_READ, 0, 0, 0);
        CloseHandle(hMap);
        if (!base) return;
        mappedFiles.push_back({ base, flen });

        size_t pos = 0;
        while (pos + 5 <= flen) {
            char tag = base[pos];
            if (!tag) break;
            uint32_t len = 0;
            memcpy(&len, base + pos + 1, 4);
            pos += 5;
            if (pos + len > flen) break;
            std::string_view body(base + pos, len);
            pos += len;

            if (tag == 'B') {
                if (len >= 20) {
                    Hash h{};
                    memcpy(h.data(), body.data(), 20);
                    g_blobs.try_emplace(h, body.substr(20));
                }
            } else if (tag == 'P') {
                planItems.push_back(body);
            } else if (tag == '1' || tag == '2') {
                items11.push_back(body);
            } else if (tag == 'G' || tag == 'C' || tag == 'S' || tag == 'R' || tag == 'A') {
                Hash k = key_of(tag, body);
                if (recIdx.try_emplace(k, recs.size()).second) {
                    recs.push_back({ tag, body });
                }
            }
        }
    };

    loadDbMapped(workDir + L"/scskiller.db");
    loadDbMapped(workDir + L"/scskiller_gen.db");

    size_t n12 = recs.size() + planItems.size();
    size_t total = n12 + items11.size();
    if (total == 0) {
        std::cout << "{\"event\":\"done\",\"done\":0,\"total\":0,\"failed\":0}" << std::endl;
        if (device7) device7->Release();
        if (device5) device5->Release();
        if (device2) device2->Release();
        device->Release();
        bestAdapter->Release();
        factory->Release();
        for (auto& mf : mappedFiles) UnmapViewOfFile(mf.base);
        return 0;
    }

    std::vector<std::unique_ptr<SoSlot>> soSlots(recs.size());
    for (size_t i = 0; i < recs.size(); ++i) {
        if (recs[i].tag == 'R' || recs[i].tag == 'A') {
            soSlots[i] = std::make_unique<SoSlot>();
        }
    }

    std::function<ID3D12StateObject*(size_t)> makeSo = [&](size_t j) -> ID3D12StateObject* {
        if (j >= recs.size() || !soSlots[j]) return nullptr;
        SoSlot& s = *soSlots[j];
        std::call_once(s.once, [&]() {
            const Rec& rec = recs[j];
            Reader r{ rec.payload, 0, true, nullptr, device };
            auto dep = [&](const Hash& k) -> ID3D12StateObject* {
                auto it = recIdx.find(k);
                if (it == recIdx.end() || !soSlots[it->second]) { r.fail("missing base state object"); return nullptr; }
                ID3D12StateObject* obj = makeSo(it->second);
                if (!obj) r.fail("base state object failed");
                return obj;
            };
            ID3D12StateObject* base = (rec.tag == 'A') ? dep(r.hash()) : nullptr;
            SoDesc d{};
            if (r.ok) read_so(r, d, dep);
            if (!r.ok) { s.hr = E_INVALIDARG; return; }
            D3D12_STATE_OBJECT_DESC desc{ d.type, (UINT)d.subs.size(), d.subs.data() };
            if (rec.tag == 'A') {
                s.hr = device7 ? device7->AddToStateObject(&desc, base, IID_PPV_ARGS(&s.obj)) : E_NOINTERFACE;
            } else {
                s.hr = device5 ? device5->CreateStateObject(&desc, IID_PPV_ARGS(&s.obj)) : E_NOINTERFACE;
            }
        });
        return s.obj;
    };

    std::cout << "{\"event\":\"start\",\"total\":" << total << "}" << std::endl;

    std::atomic<size_t> currentIndex{ std::min(startAt, n12) };
    std::atomic<size_t> doneCount{ std::min(startAt, total) };
    std::atomic<size_t> failCount{ 0 };
    std::atomic<bool> deviceRemoved{ false };

    auto replayItem12 = [&](size_t idx) -> bool {
        if (deviceRemoved.load(std::memory_order_relaxed)) return false;
        bool ok = false;
        try {
            if (idx < recs.size()) {
                const auto& rec = recs[idx];
                if (rec.tag == 'R' || rec.tag == 'A') {
                    ok = (makeSo(idx) != nullptr);
                } else {
                    Reader r{ rec.payload, 0, true, nullptr, device };
                    std::vector<uint64_t> buf;
                    ID3D12PipelineState* pso = nullptr;

                    if (rec.tag == 'G') {
                        D3D12_GRAPHICS_PIPELINE_STATE_DESC g{};
                        io_gfx(r, g);
                        if (r.ok && SUCCEEDED(device->CreateGraphicsPipelineState(&g, IID_PPV_ARGS(&pso)))) {
                            ok = true;
                            pso->Release();
                        }
                    } else if (rec.tag == 'C') {
                        D3D12_COMPUTE_PIPELINE_STATE_DESC c{};
                        io_cs(r, c);
                        if (r.ok && SUCCEEDED(device->CreateComputePipelineState(&c, IID_PPV_ARGS(&pso)))) {
                            ok = true;
                            pso->Release();
                        }
                    } else if (rec.tag == 'S' && device2) {
                        auto sd = read_stream(r, buf);
                        if (r.ok && SUCCEEDED(device2->CreatePipelineState(&sd, IID_PPV_ARGS(&pso)))) {
                            ok = true;
                            pso->Release();
                        }
                    }
                }
            } else {
                size_t pidx = idx - recs.size();
                Reader r{ planItems[pidx], 0, true, nullptr, device };
                Override o{};
                const Rec* t = parse_plan(r, o, recIdx, recs);
                if (t) {
                    Reader tr{ t->payload, 0, true, &o, device };
                    std::vector<uint64_t> buf;
                    ID3D12PipelineState* pso = nullptr;

                    if (t->tag == 'S' && device2) {
                        auto sd = read_stream(tr, buf);
                        if (tr.ok && SUCCEEDED(device2->CreatePipelineState(&sd, IID_PPV_ARGS(&pso)))) {
                            ok = true;
                            pso->Release();
                        }
                    } else if (t->tag == 'G') {
                        D3D12_GRAPHICS_PIPELINE_STATE_DESC g{};
                        io_gfx(tr, g);
                        if (tr.ok && SUCCEEDED(device->CreateGraphicsPipelineState(&g, IID_PPV_ARGS(&pso)))) {
                            ok = true;
                            pso->Release();
                        }
                    } else if (t->tag == 'C') {
                        D3D12_COMPUTE_PIPELINE_STATE_DESC c{};
                        io_cs(tr, c);
                        if (tr.ok && SUCCEEDED(device->CreateComputePipelineState(&c, IID_PPV_ARGS(&pso)))) {
                            ok = true;
                            pso->Release();
                        }
                    }
                }
            }
            if (!ok && device->GetDeviceRemovedReason() != S_OK) {
                deviceRemoved.store(true, std::memory_order_relaxed);
            }
        } catch (...) {
            ok = false;
        }
        return ok;
    };

    std::vector<std::thread> workers;
    if (currentIndex.load() < n12) {
        for (int t = 0; t < threads; ++t) {
            workers.emplace_back([&]() {
                while (!deviceRemoved.load(std::memory_order_relaxed)) {
                    size_t i = currentIndex.fetch_add(1, std::memory_order_relaxed);
                    if (i >= n12) break;
                    if (replayItem12(i)) doneCount.fetch_add(1, std::memory_order_relaxed);
                    else failCount.fetch_add(1, std::memory_order_relaxed);
                }
            });
        }
    }

    // D3D11 worker pool (up to 4 threads, matching Windows proxy.cpp)
    std::atomic<size_t> next11{ startAt > n12 ? startAt - n12 : 0 };
    std::vector<std::thread> workers11;
    if (!items11.empty() && next11.load() < items11.size()) {
        int threads11 = std::min(threads, 4);
        LUID luid = device->GetAdapterLuid();
        for (int t = 0; t < threads11; ++t) {
            workers11.emplace_back([&, luid]() {
                Dev11* d11 = warm11_open(luid, false);
                if (!d11) return;
                const size_t batch = 16;
                while (true) {
                    size_t j = next11.fetch_add(batch, std::memory_order_relaxed);
                    if (j >= items11.size()) break;
                    size_t end = std::min(j + batch, items11.size());
                    size_t ok = 0, bad = 0;
                    for (size_t k = j; k < end; ++k) {
                        std::string_view it = items11[k];
                        uint32_t stage = 0;
                        Hash h{}, h2{};
                        bool pair = (it.size() == 40);
                        if (it.size() == 24) { memcpy(&stage, it.data(), 4); memcpy(h.data(), it.data() + 4, 20); }
                        else if (pair) { stage = 7; memcpy(h.data(), it.data(), 20); memcpy(h2.data(), it.data() + 20, 20); }
                        auto b = g_blobs.find(h);
                        auto b2 = pair ? g_blobs.find(h2) : g_blobs.end();
                        const char* why = "";
                        bool good = false;
                        if ((it.size() == 24 || pair) && b != g_blobs.end() && (!pair || b2 != g_blobs.end())) {
                            good = pair
                                ? warm11_pair(d11, b->second.data(), b->second.size(), b2->second.data(), b2->second.size(), &why)
                                : warm11_item(d11, stage, b->second.data(), b->second.size(), &why);
                        }
                        if (good) ++ok; else ++bad;
                    }
                    if (SUCCEEDED(warm11_flush(d11))) {
                        doneCount.fetch_add(ok, std::memory_order_relaxed);
                        failCount.fetch_add(bad, std::memory_order_relaxed);
                    } else {
                        failCount.fetch_add(end - j, std::memory_order_relaxed);
                        warm11_close(d11);
                        d11 = warm11_open(luid, false);
                        if (!d11) break;
                    }
                }
                if (d11) warm11_close(d11);
            });
        }
    }

    while (true) {
        size_t done = doneCount.load(std::memory_order_relaxed);
        size_t fail = failCount.load(std::memory_order_relaxed);
        size_t processed = done + fail;

        std::cout << "{\"event\":\"progress\",\"done\":" << processed << ",\"total\":" << total << ",\"failed\":" << fail << "}" << std::endl;

        if (processed >= total || deviceRemoved.load(std::memory_order_relaxed)) break;
        std::this_thread::sleep_for(std::chrono::milliseconds(250));
    }

    for (auto& w : workers) {
        if (w.joinable()) w.join();
    }
    for (auto& w : workers11) {
        if (w.joinable()) w.join();
    }

    for (auto& s : soSlots) {
        if (s && s->obj) s->obj->Release();
    }
    for (auto& pair : g_rootSigs) {
        if (pair.second) pair.second->Release();
    }

    std::cout << "{\"event\":\"done\",\"done\":" << doneCount.load() << ",\"total\":" << total << ",\"failed\":" << failCount.load() << "}" << std::endl;

    if (device7) device7->Release();
    if (device5) device5->Release();
    if (device2) device2->Release();
    device->Release();
    bestAdapter->Release();
    factory->Release();
    for (auto& mf : mappedFiles) UnmapViewOfFile(mf.base);

    return deviceRemoved.load() ? 2 : 0;
}
