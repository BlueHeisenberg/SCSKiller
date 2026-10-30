// scskiller: the DirectX 9 recorder, d3d9.dll beside the game's exe (x86 and x64). Every export goes to the system's
// d3d9.dll. When admission (admit.h) admits the exe at its first device, it records each shader the game creates and every
// (VS, PS, state) it draws with into scskiller.db beside it, for the DirectX 9 warm to draw again: AMD's driver compiles a
// D3D9 shader at its first draw and again for each fixed-function state that changes the compiled code (J59).
// Not admitted: a pass-through. A pure device's state can't be read back: it is kept from the game's own sets.
//
// db records, proxy.cpp's format (u8 tag, u32 len, payload): 'B' blob = sha1[20] + the shader's bytecode (GetFunction's).
// '9' draw, once per distinct payload, little-endian u32s unless noted:
//   VS sha1[20], PS sha1[20] (zero: none, fixed function), primitive type,
//   vertex input: kind (0 declaration, 1 FVF), FVF, element count n, n D3DVERTEXELEMENT9 (8 bytes each, D3DDECL_END left out),
//   render target 0 format, its multisample type, depth-stencil format (0: none),
//   kStates.size() render state values in kStates order,
//   kSamplers samplers (PS s0..s15, then VS D3DVERTEXTEXTURESAMPLER0..3): texture type (0 none, else D3DRESOURCETYPE),
//   its top level's format, D3DSAMP_SRGBTEXTURE (0 without a texture).
// Values a driver takes as constants (ALPHAREF, fog colour and range, blend factors) aren't in it: they'd multiply the
// draws without changing a compile. Nor is the fixed-function half of a draw with one shader (lighting, texture stage
// states): it is drawn again with the defaults. ponytail: add those states if AMD shows misses on such draws.
// Two threads setting the same state at once may leave a draw recorded with the other's value: a variant missed, no harm.
// Config: scskiller.ini [scskiller] max_db_bytes=N as proxy.cpp reads it. Input: scskiller.keys as proxy.cpp (load_keys).
#define NOMINMAX
#include <windows.h>
#include <d3d9.h>
#include <bcrypt.h>
#include <fcntl.h>
#include <io.h>
#include <share.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <mutex>
#include <type_traits>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>
#include "admit.h"

#pragma comment(lib, "bcrypt.lib")

using Hash = std::array<uint8_t, 20>;
struct HashH { size_t operator()(const Hash& h) const { size_t v; memcpy(&v, h.data(), sizeof v); return v; } };
static const Hash kZero{};

static Hash sha1(const void* p, size_t n) {
    Hash h;
    BCryptHash(BCRYPT_SHA1_ALG_HANDLE, nullptr, 0, (PUCHAR)p, (ULONG)n, h.data(), 20);
    return h;
}

static std::wstring g_dir;  // this dll's folder, trailing backslash
static const std::wstring& admit_dir() { return g_dir; }
static bool g_elsewhere;    // not the exe's folder: a mod loaded a copy from elsewhere

// ---- exports: raw jumps to the system's d3d9.dll, but Direct3DCreate9 and Direct3DCreate9Ex
#define D3D9_STUBS(X) X(Ord16) X(Ord17) X(Ord18) X(Ord19) X(Direct3DCreate9On12) X(Direct3DCreate9On12Ex) X(Ord22) X(Ord23) \
    X(Direct3DShaderValidatorCreate9) X(PSGPError) X(PSGPSampleTexture) X(D3DPERF_BeginEvent) X(D3DPERF_EndEvent) \
    X(D3DPERF_GetStatus) X(D3DPERF_QueryRepeatFrame) X(D3DPERF_SetMarker) X(D3DPERF_SetOptions) X(D3DPERF_SetRegion) \
    X(DebugSetLevel) X(DebugSetMute) X(Direct3D9EnableMaximizedWindowedModeShim)
extern "C" {
#define REAL(n) void* real9_##n;
D3D9_STUBS(REAL)
#undef REAL
}
#ifndef _WIN64
// x64: d3d9stubs.asm
#define STUB(n) extern "C" __declspec(naked) void stub9_##n() { __asm jmp dword ptr [real9_##n] }
D3D9_STUBS(STUB)
#undef STUB
#endif

static HMODULE g_real;

// Inside the system d3d9.dll's image: a function of the runtime's own, which never calls another hook.
static bool in_runtime(const void* p) {
    auto base = (const uint8_t*)g_real;
    if (!base) return false;
    auto nt = (const IMAGE_NT_HEADERS*)(base + ((const IMAGE_DOS_HEADER*)base)->e_lfanew);
    return p >= base && p < base + nt->OptionalHeader.SizeOfImage;
}
static IDirect3D9*(WINAPI* real_create9)(UINT);
static HRESULT(WINAPI* real_create9ex)(UINT, IDirect3D9Ex**);

// False: the system's d3d9.dll or one of its exports isn't there, so a jump would go nowhere: the dll then fails to load,
// as a game without d3d9.dll would.
static bool resolve(HMODULE self) {
    wchar_t sys[MAX_PATH];
    UINT n = GetSystemDirectoryW(sys, MAX_PATH);
    if (!n || n >= MAX_PATH - 10) return false;
    g_real = LoadLibraryW((std::wstring(sys) + L"\\d3d9.dll").c_str());
    if (!g_real || g_real == self) return false;
    auto at = [](const char* name) { return (void*)GetProcAddress(g_real, name); };
    real9_Ord16 = at(MAKEINTRESOURCEA(16)), real9_Ord17 = at(MAKEINTRESOURCEA(17)), real9_Ord18 = at(MAKEINTRESOURCEA(18));
    real9_Ord19 = at(MAKEINTRESOURCEA(19)), real9_Ord22 = at(MAKEINTRESOURCEA(22)), real9_Ord23 = at(MAKEINTRESOURCEA(23));
    bool all = true;
#define NAMED(n) if (!real9_##n) real9_##n = at(#n); all = all && real9_##n;
    D3D9_STUBS(NAMED)
#undef NAMED
    real_create9 = (decltype(real_create9))at("Direct3DCreate9");
    real_create9ex = (decltype(real_create9ex))at("Direct3DCreate9Ex");
    return all && real_create9 && real_create9ex;
}

// ---- recording
static std::mutex g_mx;  // the db, the shader and declaration maps; never taken while waiting on a device's lock
static FILE *g_db, *g_log;
static constexpr uint32_t kMaxRecord = (64u << 20) + 20;  // a shader blob's limit, far above any D3D9 shader
static uint64_t g_db_bytes, g_db_cap;
static bool g_db_capped;
static std::unordered_set<Hash, HashH> g_keys;  // records and blobs in the db, written this run, or imported (scskiller.keys)
static std::vector<Hash> g_imported;            // scskiller.keys, sorted
static std::unordered_map<void*, Hash> g_shader_of;  // live shader object -> its blob hash (an address reused: the new one)
static std::unordered_map<std::string, uint32_t> g_decl_id;  // vertex declaration elements -> id (1-based index in g_decls)
static std::vector<std::string> g_decls;
static bool g_shader_logged, g_drawn_logged;  // under g_mx: the first of each is logged, so a log tells a menu without either

static void logf(const char* fmt, ...) {
    if (!g_log) return;
    va_list a;
    va_start(a, fmt);
    vfprintf(g_log, fmt, a);
    va_end(a);
    fputc('\n', g_log), fflush(g_log);
}

static bool imported(const Hash& h) { return std::binary_search(g_imported.begin(), g_imported.end(), h); }

static void load_keys(const std::wstring& path) {
    FILE* f = _wfopen(path.c_str(), L"rb");
    if (!f) return;
    char magic[8];
    long long len = _filelengthi64(_fileno(f));
    if (len > 8 && fread(magic, 1, 8, f) == 8 && !memcmp(magic, "SCSKKEY1", 8)) {
        g_imported.resize((size_t)(len - 8) / 20);
        g_imported.resize(fread(g_imported.data(), 20, g_imported.size(), f));
        std::sort(g_imported.begin(), g_imported.end());
    }
    fclose(f);
}

// A file of the exe's folder opened to append: never through a link or a second name (a hard link out of the folder),
// only a regular file. share: FILE_SHARE_* for others.
static FILE* own_file(const std::wstring& path, DWORD share) {
    HANDLE h = CreateFileW(path.c_str(), GENERIC_WRITE | FILE_READ_ATTRIBUTES, share, nullptr, OPEN_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (h == INVALID_HANDLE_VALUE) return nullptr;
    FILE_ATTRIBUTE_TAG_INFO tag{};
    BY_HANDLE_FILE_INFORMATION info{};
    const bool plain = GetFileInformationByHandleEx(h, FileAttributeTagInfo, &tag, sizeof tag) && !(tag.FileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY))
                       && GetFileInformationByHandle(h, &info) && info.nNumberOfLinks == 1 && GetFileType(h) == FILE_TYPE_DISK;
    const int fd = plain ? _open_osfhandle((intptr_t)h, _O_APPEND | _O_BINARY) : -1;
    if (fd < 0) return CloseHandle(h), nullptr;
    FILE* f = _fdopen(fd, "ab");
    if (!f) _close(fd);
    return f;
}

// The keys the db holds. A torn tail (a crash mid-write) is cut so appends stay parseable, as proxy.cpp's load_file does;
// when it can't be read or cut, nothing is recorded.
static void load_db() {
    const std::wstring path = g_dir + L"scskiller.db";
    // deny-write: a second process of the folder records nothing rather than interleaving with this one's records
    g_db = own_file(path, FILE_SHARE_READ);
    if (!g_db) return logf("d3d9: scskiller.db can't be opened (another process has it?): nothing is recorded in this one");
    long long good = 0, size = -1;
    bool unread = false;  // a read failed or a length no recorder writes: not a torn tail, so nothing is cut
    if (FILE* f = _wfsopen(path.c_str(), L"rb", _SH_DENYNO)) {
        const long long flen = _filelengthi64(_fileno(f));
        unread = flen < 0;
        while (!unread) {
            int tag = fgetc(f);
            uint32_t len;
            if (tag == EOF || !tag || fread(&len, 4, 1, f) != 1 || len > flen - good - 5) break;
            if (len > kMaxRecord) {
                unread = true;
                break;
            }
            if (tag == 'B') {
                Hash h;
                if (len < 20 || fread(h.data(), 1, 20, f) != 20) break;
                if (_fseeki64(f, len - 20, SEEK_CUR)) {
                    unread = true;
                    break;
                }
                if (_ftelli64(f) > flen) break;
                g_keys.insert(h);
            } else {
                std::string p((size_t)len + 1, '\0');
                p[0] = (char)tag;
                if (fread(p.data() + 1, 1, len, f) != len) break;
                g_keys.insert(sha1(p.data(), p.size()));
            }
            good = _ftelli64(f);
        }
        unread = unread || ferror(f);
        size = _fseeki64(f, 0, SEEK_END) ? -1 : _ftelli64(f);
        fclose(f);
    }
    if (size < 0 || unread || (size > good && _chsize_s(_fileno(g_db), good))) {
        logf("d3d9: scskiller.db can't be read or its torn tail cut: nothing is recorded in this launch");
        return (void)(g_db = nullptr);  // not closed: kept denying writes, so no other process appends behind the damage
    }
    if (size > good) logf("d3d9: truncated a torn tail (%lld -> %lld bytes)", size, good);
    g_db_bytes = (uint64_t)good;
    load_keys(g_dir + L"scskiller.keys");
    wchar_t cap[32];
    GetPrivateProfileStringW(L"scskiller", L"max_db_bytes", L"", cap, 32, (g_dir + L"scskiller.ini").c_str());
    g_db_capped = *cap, g_db_cap = _wcstoui64(cap, nullptr, 10);
    logf("d3d9: db %llu bytes, %zu records and blobs%s", g_db_bytes, g_keys.size(), g_db_capped ? ", limited" : "");
}

// Under g_mx. False: not written (no db, full, a failed write: then nothing more is, so no draw names a blob left out).
static bool put(char tag, const void* head, size_t head_n, const void* body, size_t body_n) {
    const uint32_t len = (uint32_t)(head_n + body_n);
    if (!g_db) return false;
    if (g_db_capped && g_db_bytes + 5 + len > g_db_cap) {
        logf("d3d9: scskiller.db reached max_db_bytes: nothing more is recorded in this launch");
        return (void)(g_db = nullptr), false;
    }
    bool ok = fputc(tag, g_db) != EOF && fwrite(&len, 4, 1, g_db) == 1 && fwrite(head, 1, head_n, g_db) == head_n
              && (!body_n || fwrite(body, 1, body_n, g_db) == body_n) && !fflush(g_db);
    if (!ok) {
        logf("d3d9: writing scskiller.db failed (disk full?): nothing more is recorded in this launch");
        return (void)(g_db = nullptr), false;  // not closed: what was cut is the tail the next launch cuts
    }
    g_db_bytes += 5 + len;
    return true;
}

// A shader the game made: its bytecode as the runtime keeps it (GetFunction), whatever the game passed.
template <class S> static void shader_made(S* s) try {
    UINT n = 0;
    if (!s || FAILED(s->GetFunction(nullptr, &n)) || !n || n > kMaxRecord - 20) return;
    std::string code(n, '\0');
    if (FAILED(s->GetFunction(code.data(), &n)) || n > code.size()) return;
    code.resize(n);
    const Hash h = sha1(code.data(), code.size());
    std::lock_guard l(g_mx);
    g_shader_of[s] = h;
    if (g_keys.insert(h).second && !imported(h) && put('B', h.data(), 20, code.data(), code.size()) && !g_shader_logged)
        g_shader_logged = true, logf("d3d9: the first shader is recorded");
} catch (...) {}  // out of memory: not recorded, the game's call goes on

static Hash shader_hash(void* s) {
    if (!s) return kZero;
    std::lock_guard l(g_mx);
    auto it = g_shader_of.find(s);
    return it == g_shader_of.end() ? kZero : it->second;
}

// ---- the state a draw is recorded with
static const D3DRENDERSTATETYPE kStates[] = {D3DRS_ALPHATESTENABLE, D3DRS_ALPHAFUNC, D3DRS_FOGENABLE, D3DRS_FOGTABLEMODE,
    D3DRS_FOGVERTEXMODE, D3DRS_RANGEFOGENABLE, D3DRS_SRGBWRITEENABLE, D3DRS_CLIPPLANEENABLE, D3DRS_POINTSPRITEENABLE,
    D3DRS_SHADEMODE, D3DRS_FILLMODE, D3DRS_COLORWRITEENABLE, D3DRS_COLORWRITEENABLE1, D3DRS_COLORWRITEENABLE2,
    D3DRS_COLORWRITEENABLE3, D3DRS_MULTISAMPLEANTIALIAS};
static constexpr int kSamplers = 20;
static DWORD sampler_index(int i) { return i < 16 ? (DWORD)i : D3DVERTEXTEXTURESAMPLER0 + (DWORD)(i - 16); }
// The kSamplers index of a D3D9 sampler or texture stage number; -1 for none of them.
static int sampler_slot(DWORD i) {
    return i < 16 ? (int)i : i >= D3DVERTEXTEXTURESAMPLER0 && i <= D3DVERTEXTEXTURESAMPLER3 ? 16 + (int)(i - D3DVERTEXTEXTURESAMPLER0) : -1;
}
static int state_index(D3DRENDERSTATETYPE s) {
    for (size_t i = 0; i < std::size(kStates); ++i) if (kStates[i] == s) return (int)i;
    return -1;
}

// What a draw binds, as values taken when the game set them (no object is kept, so none is touched once the game frees it).
// Kept by the Set* hooks after the runtime took the call, read back whole at device creation, a successful Reset and a
// state block's Apply (which sets state past every hook). A pure device answers no Get*: there it starts from D3D9's
// defaults at creation and Reset, and a state block's Apply sets what the block holds by the sets it was recorded or
// captured with. Hashed first, so a draw like an earlier one costs a hash.
struct Bound {
    Hash vs, ps;
    uint32_t vs_set, ps_set;  // a shader is bound (its hash is zero when made before admission)
    uint32_t decl, fvf, prim;  // decl: g_decls id, 0 for an FVF
    DWORD rt_format, rt_msaa, ds_format;
    DWORD rs[std::size(kStates)];
    DWORD tex[kSamplers][2];  // type, format
    DWORD srgb[kSamplers];
};
// The Bound values a state block holds, by mask bit: vertex shader, pixel shader, vertex input, kStates, textures, sRGB.
enum : uint64_t { kMaskVs = 1, kMaskPs = 2, kMaskInput = 4 };
static constexpr uint64_t mask_rs(int i) { return 8ull << i; }
static constexpr uint64_t mask_tex(int k) { return 8ull << (std::size(kStates) + k); }
static constexpr uint64_t mask_srgb(int k) { return 8ull << (std::size(kStates) + kSamplers + k); }
static_assert(3 + std::size(kStates) + 2 * kSamplers <= 64);
static constexpr uint64_t kMaskAll = ~0ull >> (64 - 3 - std::size(kStates) - 2 * kSamplers);
static void copy_masked(Bound& to, const Bound& from, uint64_t m) {
    if (m & kMaskVs) to.vs = from.vs, to.vs_set = from.vs_set;
    if (m & kMaskPs) to.ps = from.ps, to.ps_set = from.ps_set;
    if (m & kMaskInput) to.decl = from.decl, to.fvf = from.fvf;
    for (int i = 0; i < (int)std::size(kStates); ++i) if (m & mask_rs(i)) to.rs[i] = from.rs[i];
    for (int k = 0; k < kSamplers; ++k) {
        if (m & mask_tex(k)) to.tex[k][0] = from.tex[k][0], to.tex[k][1] = from.tex[k][1];
        if (m & mask_srgb(k)) to.srgb[k] = from.srgb[k];
    }
}
// D3DSBT_PIXELSTATE's and D3DSBT_VERTEXSTATE's share of Bound (textures are only D3DSBT_ALL's), per D3D9's state block lists
// ponytail: the block types split by those lists only; a misfiled state records a variant the warm draws once more
static uint64_t block_mask(D3DSTATEBLOCKTYPE t) {
    static const bool pixel[] = {true, true, false, false, false, false, true, false, false, true, true, true, true, true, true, false};
    static_assert(std::size(pixel) == std::size(kStates));
    if (t == D3DSBT_ALL) return kMaskAll;
    uint64_t m = t == D3DSBT_PIXELSTATE ? kMaskPs : kMaskVs | kMaskInput;
    for (int i = 0; i < (int)std::size(kStates); ++i) if (pixel[i] == (t == D3DSBT_PIXELSTATE)) m |= mask_rs(i);
    if (t == D3DSBT_PIXELSTATE) for (int k = 0; k < kSamplers; ++k) m |= mask_srgb(k);
    return m;
}
struct Block { Bound b; uint64_t mask; };
struct Dev {
    std::mutex mx;  // b, seen, recording, rec, rec_mask, blocks
    bool pure = false, ex = false, recording = false;  // recording: between BeginStateBlock and EndStateBlock, sets don't bind
    Bound b;
    std::unordered_set<uint64_t> seen;  // the Bound hashes drawn
    // a pure device's: the sets of the state block being recorded, and its state blocks by address (a freed one's address
    // is taken again by the next block made there)
    Bound rec;
    uint64_t rec_mask = 0;
    std::unordered_map<void*, Block> blocks;
    Dev() { memset(&b, 0, sizeof b), memset(&rec, 0, sizeof rec); }
};
static std::unordered_map<void*, Dev*> g_devs;  // under g_mx; never freed: a few per run
static thread_local void* t_dev;
static thread_local Dev* t_state;

// The state of a device we hooked; null for any other (one made before admission decided, or by another runtime path).
static Dev* dev_of(void* d) {
    if (d == t_dev) return t_state;
    std::lock_guard l(g_mx);
    auto it = g_devs.find(d);
    if (it == g_devs.end()) return nullptr;  // not cached: it may become one of ours
    t_dev = d, t_state = it->second;
    return t_state;
}

static uint32_t decl_id(IDirect3DVertexDeclaration9* v) try {
    D3DVERTEXELEMENT9 el[MAXD3DDECLLENGTH + 1];
    UINT n = MAXD3DDECLLENGTH + 1;
    if (!v || FAILED(v->GetDeclaration(el, &n)) || !n) return 0;
    std::string key((const char*)el, (n - 1) * sizeof el[0]);  // D3DDECL_END left out
    std::lock_guard l(g_mx);
    if (auto it = g_decl_id.find(key); it != g_decl_id.end()) return it->second;
    g_decls.push_back(key);
    try {
        g_decl_id.emplace(key, (uint32_t)g_decls.size());
    } catch (...) {
        g_decls.pop_back();
        throw;
    }
    return (uint32_t)g_decls.size();
} catch (...) { return 0; }

static void texture_of(IDirect3DBaseTexture9* t, DWORD out[2]) {
    out[0] = out[1] = 0;
    if (!t) return;
    out[0] = t->GetType();
    if (out[0] == D3DRTYPE_TEXTURE) {
        D3DSURFACE_DESC d;
        if (SUCCEEDED(((IDirect3DTexture9*)t)->GetLevelDesc(0, &d))) out[1] = d.Format;
    } else if (out[0] == D3DRTYPE_CUBETEXTURE) {
        D3DSURFACE_DESC d;
        if (SUCCEEDED(((IDirect3DCubeTexture9*)t)->GetLevelDesc(0, &d))) out[1] = d.Format;
    } else if (out[0] == D3DRTYPE_VOLUMETEXTURE) {
        D3DVOLUME_DESC d;
        if (SUCCEEDED(((IDirect3DVolumeTexture9*)t)->GetLevelDesc(0, &d))) out[1] = d.Format;
    }
}

static void surface_of(IDirect3DSurface9* s, DWORD& format, DWORD* msaa = nullptr) {
    D3DSURFACE_DESC d{};
    if (s) s->GetDesc(&d);
    format = s ? d.Format : 0;
    if (msaa) *msaa = s ? d.MultiSampleType : 0;
}

// A pure device's state after its creation or a Reset: D3D9's defaults, and its render target and depth-stencil, which
// even a pure device returns.
static void pure_defaults(IDirect3DDevice9* d, Dev* s) {
    static const DWORD kDefaults[] = {FALSE, D3DCMP_ALWAYS, FALSE, D3DFOG_NONE, D3DFOG_NONE, FALSE, FALSE, 0, FALSE,
                                      D3DSHADE_GOURAUD, D3DFILL_SOLID, 0xF, 0xF, 0xF, 0xF, TRUE};
    static_assert(std::size(kDefaults) == std::size(kStates));
    Bound b;
    memset(&b, 0, sizeof b);
    memcpy(b.rs, kDefaults, sizeof b.rs);
    IDirect3DSurface9 *rt = nullptr, *ds = nullptr;
    if (SUCCEEDED(d->GetRenderTarget(0, &rt)) && rt) surface_of(rt, b.rt_format, &b.rt_msaa), rt->Release();
    if (SUCCEEDED(d->GetDepthStencilSurface(&ds)) && ds) surface_of(ds, b.ds_format), ds->Release();
    std::lock_guard l(s->mx);
    b.prim = s->b.prim;
    s->b = b;
    s->rec_mask = 0;
}
// After ResetEx, which keeps the device's state: a pure device's new back buffer and depth-stencil only.
static void pure_surfaces(IDirect3DDevice9* d, Dev* s) {
    DWORD f = 0, m = 0, z = 0;
    IDirect3DSurface9 *rt = nullptr, *ds = nullptr;
    if (SUCCEEDED(d->GetRenderTarget(0, &rt)) && rt) surface_of(rt, f, &m), rt->Release();
    if (SUCCEEDED(d->GetDepthStencilSurface(&ds)) && ds) surface_of(ds, z), ds->Release();
    std::lock_guard l(s->mx);
    s->b.rt_format = f, s->b.rt_msaa = m, s->b.ds_format = z;
}

// Reads the bound state back from the device; a pure one's defaults (its Get* calls fail).
static void sync(IDirect3DDevice9* d, Dev* s) {
    if (!s) return;
    if (s->pure) return pure_defaults(d, s);
    Bound b;
    memset(&b, 0, sizeof b);
    IDirect3DVertexShader9* vs = nullptr;
    IDirect3DPixelShader9* ps = nullptr;
    IDirect3DVertexDeclaration9* decl = nullptr;
    IDirect3DSurface9 *rt = nullptr, *ds = nullptr;
    if (SUCCEEDED(d->GetVertexShader(&vs)) && vs) b.vs = shader_hash(vs), b.vs_set = 1, vs->Release();
    if (SUCCEEDED(d->GetPixelShader(&ps)) && ps) b.ps = shader_hash(ps), b.ps_set = 1, ps->Release();
    if (SUCCEEDED(d->GetVertexDeclaration(&decl)) && decl) b.decl = decl_id(decl), decl->Release();
    if (!b.decl) d->GetFVF((DWORD*)&b.fvf);
    if (SUCCEEDED(d->GetRenderTarget(0, &rt)) && rt) surface_of(rt, b.rt_format, &b.rt_msaa), rt->Release();
    if (SUCCEEDED(d->GetDepthStencilSurface(&ds)) && ds) surface_of(ds, b.ds_format), ds->Release();
    for (size_t i = 0; i < std::size(kStates); ++i) d->GetRenderState(kStates[i], &b.rs[i]);
    for (int i = 0; i < kSamplers; ++i) {
        IDirect3DBaseTexture9* t = nullptr;
        if (SUCCEEDED(d->GetTexture(sampler_index(i), &t)) && t) texture_of(t, b.tex[i]), t->Release();
        d->GetSamplerState(sampler_index(i), D3DSAMP_SRGBTEXTURE, &b.srgb[i]);
    }
    std::lock_guard l(s->mx);
    b.prim = s->b.prim;
    s->b = b;
}

static void put_u32(std::string& p, uint32_t v) { p.append((const char*)&v, 4); }

// The '9' payload of b; false for a draw not recorded: fixed function only, or a shader made before admission.
static bool draw_payload(const Bound& b, std::string& p) {
    if ((!b.vs_set && !b.ps_set) || (b.vs_set && b.vs == kZero) || (b.ps_set && b.ps == kZero)) return false;
    p.assign((const char*)b.vs.data(), 20).append((const char*)b.ps.data(), 20);
    put_u32(p, b.prim);
    std::string decl;
    if (b.decl) {
        std::lock_guard l(g_mx);
        decl = g_decls[b.decl - 1];
    }
    put_u32(p, b.decl ? 0 : 1), put_u32(p, b.decl ? 0 : b.fvf), put_u32(p, (uint32_t)(decl.size() / sizeof(D3DVERTEXELEMENT9)));
    p += decl;
    put_u32(p, b.rt_format), put_u32(p, b.rt_msaa), put_u32(p, b.ds_format);
    DWORD rs[std::size(kStates)];
    memcpy(rs, b.rs, sizeof rs);
    if (!rs[0]) rs[1] = 0;                  // ALPHAFUNC means nothing without alpha test
    if (!rs[2]) rs[3] = rs[4] = rs[5] = 0;  // nor the fog modes without fog
    for (DWORD v : rs) put_u32(p, v);
    for (int i = 0; i < kSamplers; ++i) put_u32(p, b.tex[i][0]), put_u32(p, b.tex[i][1]), put_u32(p, b.tex[i][0] ? b.srgb[i] : 0);
    return true;
}

static uint64_t bound_hash(const Bound& b) {
    uint64_t h = 0xcbf29ce484222325ull;
    auto w = (const uint32_t*)&b;
    for (size_t i = 0; i < sizeof b / 4; ++i) h = (h ^ w[i]) * 0x100000001b3ull;
    return h;
}

static void drawn(IDirect3DDevice9* d, D3DPRIMITIVETYPE prim) try {
    Dev* s = dev_of(d);
    if (!s) return;
    std::string p;
    {
        std::lock_guard l(s->mx);
        s->b.prim = prim;
        if (!s->seen.insert(bound_hash(s->b)).second) return;
        if (!draw_payload(s->b, p)) {
            static std::atomic<bool> fixed, early;  // each logged once: a log then tells why a launch recorded no draw
            const bool ff = !s->b.vs_set && !s->b.ps_set;
            if (!(ff ? fixed : early).exchange(true))
                logf(ff ? "d3d9: fixed-function draws aren't recorded" : "d3d9: draws with a shader made before recording started aren't recorded");
            return;
        }
    }
    const Hash k = sha1((std::string(1, '9') + p).data(), p.size() + 1);
    std::lock_guard l(g_mx);
    if (g_keys.insert(k).second && !imported(k) && put('9', p.data(), p.size(), nullptr, 0) && !g_drawn_logged)
        g_drawn_logged = true, logf("d3d9: the first draw is recorded");
} catch (...) {}

// Sets a bound value after the runtime took the call, unless a state block is recording it (mask: the state block's
// share it is, 0 for render targets and depth-stencil, which no block keeps): a pure device keeps it for the block's Apply.
template <class F> static void bind(void* d, HRESULT hr, F&& set, uint64_t mask) {
    if (FAILED(hr)) return;
    if (Dev* s = dev_of(d)) {
        std::lock_guard l(s->mx);
        if (!s->recording || !mask) set(s->b);
        else if (s->pure) set(s->rec), s->rec_mask |= mask;
    }
}

// ---- vtable hooks. A vtable is shared by every object of its class: each is patched once, its originals kept with it.
// Slots: vtslots9.cpp checks them against d3d9.h's C interfaces.
enum : int {
    kD3DCreateDevice = 16, kD3DCreateDeviceEx = 20,
    kReset = 16, kSetRenderTarget = 37, kSetDepthStencil = 39, kSetRenderState = 57, kCreateStateBlock = 59,
    kBeginStateBlock = 60, kEndStateBlock = 61, kSetTexture = 65, kSetSamplerState = 69, kDrawPrimitive = 81,
    kDrawIndexedPrimitive = 82, kDrawPrimitiveUP = 83, kDrawIndexedPrimitiveUP = 84, kSetVertexDeclaration = 87, kSetFVF = 89,
    kCreateVertexShader = 91, kSetVertexShader = 92, kCreatePixelShader = 106, kSetPixelShader = 107, kResetEx = 132,
    kDeviceSlots = 134, kCapture = 4, kApply = 5,
    // method counts (vtslots9.cpp): IDirect3D9, IDirect3D9Ex, IDirect3DDevice9, IDirect3DDevice9Ex, IDirect3DStateBlock9
    kD3D9Methods = 17, kD3D9ExMethods = 22, kDevice9Methods = 119, kDevice9ExMethods = 134, kBlockMethods = 6,
};
enum Kind : int { kKindD3D, kKindDevice, kKindBlock };  // IDirect3D9(Ex), IDirect3DDevice9(Ex), IDirect3DStateBlock9
// size: the interface's method count; was: the vtable's first size entries when it was first patched, kept here (a
// device's vtable is freed with it)
struct Table { void** vt; Kind kind; size_t size; void* was[kDeviceSlots]; std::atomic<void*> orig[kDeviceSlots]; };
// Added under g_mx, read without it: an entry is complete before the count covers it, and a slot's original is stored
// (release) before the slot points at the hook, never cleared. Never freed: this dll is pinned before the first patch. A
// device's vtable is its own (on the heap), so each device takes one; vt is only compared, never read (it may be freed).
static Table* g_tables[64];
static std::atomic<size_t> g_ntables;

static Table* table_of(void* obj) {
    void** vt = *(void***)obj;
    for (size_t i = 0, n = g_ntables.load(std::memory_order_acquire); i < n; ++i)
        if (g_tables[i]->vt == vt) return g_tables[i];
    return nullptr;
}
// A vtable that reached our hook but holds no original for its slot: another hooker swapped the object's vtable pointer for
// a copy of one of ours (a table patch() makes for such a copy lacks the slots the copy took over with our hooks). It
// forwards as the table of its interface it was copied from: of those with this slot's original, the one whose first
// entries it matches most. The copy holds at least the base interface's methods (an Ex one may be either), so only those are read.
static Table* copied_from(void** vt, Kind kind, int slot) {
    struct Hit { void** vt; Table* t; };
    thread_local Hit hits[3][kDeviceSlots];  // per interface and slot: a found table always holds that slot's original
    Hit& h = hits[kind][slot];
    if (h.vt == vt) return h.t;
    const size_t base = kind == kKindD3D ? kD3D9Methods : kind == kKindDevice ? kDevice9Methods : kBlockMethods;
    Table* best = nullptr;
    size_t best_same = 0;
    for (size_t i = 0, n = g_ntables.load(std::memory_order_acquire); i < n; ++i) {
        Table* t = g_tables[i];
        if (t->kind != kind || !t->orig[slot].load(std::memory_order_acquire)) continue;
        size_t same = 0;
        for (size_t k = 0; k < base; ++k) same += vt[k] == t->was[k];
        if (!best || same > best_same) best = t, best_same = same;
    }
    h = {vt, best};
    return best;
}
template <class F> struct KindOf;
template <class R, class A, class... More> struct KindOf<R(STDMETHODCALLTYPE*)(A, More...)> {
    static constexpr Kind value = std::is_same_v<A, IDirect3DStateBlock9*> ? kKindBlock
                                  : std::is_same_v<A, IDirect3D9*> || std::is_same_v<A, IDirect3D9Ex*> ? kKindD3D : kKindDevice;
};
// F: the hook's own type, which tells its interface
template <class F> static F orig(void* obj, int slot) {
    Table* t = table_of(obj);
    void* f = t ? t->orig[slot].load(std::memory_order_acquire) : nullptr;
    // never null: the hook was reached, so some table of its interface patched this slot
    return (F)(f ? f : copied_from(*(void***)obj, KindOf<F>::value, slot)->orig[slot].load(std::memory_order_acquire));
}

// Patches the slots of obj's vtable that aren't yet: slots[i] -> fns[i]. With before (what slots[i] held before a runtime
// call): the slots the call wrote a runtime function into (one written over a hook on top of ours too: that hook is out of
// the chain then). A slot holding another hook is left to it: its original may be ours, so taking it would call round in
// a circle.
static void patch(void* obj, Kind kind, size_t size, const int* slots, void* const* fns, size_t n, void* const* before = nullptr) {
    static std::once_flag pinned;
    std::call_once(pinned, [] {
        HMODULE self;  // the vtables point here for the rest of the process: never unloaded
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, (LPCWSTR)&patch, &self);
    });
    void** vt = *(void***)obj;
    std::lock_guard l(g_mx);
    Table* t = table_of(obj);
    if (!t) {
        const size_t c = g_ntables.load();
        if (c == std::size(g_tables)) return;
        t = g_tables[c] = new (std::nothrow) Table{vt, kind, size, {}, {}};
        if (!t) return;
        memcpy(t->was, vt, size * sizeof(void*));
        g_ntables.store(c + 1, std::memory_order_release);
    }
    for (size_t i = 0; i < n; ++i) {
        // a runtime function in a slot already patched: a new table at a freed one's address
        if (vt[slots[i]] == fns[i] || (before ? vt[slots[i]] == before[i] || !in_runtime(vt[slots[i]]) : t->orig[slots[i]].load() != nullptr && !in_runtime(vt[slots[i]])))
            continue;
        DWORD old;
        if (!VirtualProtect(&vt[slots[i]], sizeof(void*), PAGE_READWRITE, &old)) continue;
        t->orig[slots[i]].store(vt[slots[i]], std::memory_order_release);
        InterlockedExchangePointer(&vt[slots[i]], fns[i]);
        VirtualProtect(&vt[slots[i]], sizeof(void*), old, &old);
    }
}

#define DEV IDirect3DDevice9* d
static HRESULT STDMETHODCALLTYPE hk_SetRenderState(DEV, D3DRENDERSTATETYPE s, DWORD v) {
    HRESULT hr = orig<decltype(&hk_SetRenderState)>(d, kSetRenderState)(d, s, v);
    if (int i = state_index(s); i >= 0) bind(d, hr, [&](Bound& b) { b.rs[i] = v; }, mask_rs(i));
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_SetSamplerState(DEV, DWORD i, D3DSAMPLERSTATETYPE s, DWORD v) {
    HRESULT hr = orig<decltype(&hk_SetSamplerState)>(d, kSetSamplerState)(d, i, s, v);
    if (int k = sampler_slot(i); s == D3DSAMP_SRGBTEXTURE && k >= 0) bind(d, hr, [&](Bound& b) { b.srgb[k] = v; }, mask_srgb(k));
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_SetTexture(DEV, DWORD i, IDirect3DBaseTexture9* t) {
    HRESULT hr = orig<decltype(&hk_SetTexture)>(d, kSetTexture)(d, i, t);
    if (int k = sampler_slot(i); k >= 0 && SUCCEEDED(hr) && dev_of(d)) {
        DWORD v[2];
        texture_of(t, v);
        bind(d, hr, [&](Bound& b) { b.tex[k][0] = v[0], b.tex[k][1] = v[1]; }, mask_tex(k));
    }
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_SetRenderTarget(DEV, DWORD i, IDirect3DSurface9* s) {
    HRESULT hr = orig<decltype(&hk_SetRenderTarget)>(d, kSetRenderTarget)(d, i, s);
    if (!i && SUCCEEDED(hr) && dev_of(d)) {
        DWORD f, m;
        surface_of(s, f, &m);
        bind(d, hr, [&](Bound& b) { b.rt_format = f, b.rt_msaa = m; }, 0);
    }
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_SetDepthStencil(DEV, IDirect3DSurface9* s) {
    HRESULT hr = orig<decltype(&hk_SetDepthStencil)>(d, kSetDepthStencil)(d, s);
    if (SUCCEEDED(hr) && dev_of(d)) {
        DWORD f;
        surface_of(s, f);
        bind(d, hr, [&](Bound& b) { b.ds_format = f; }, 0);
    }
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_SetVertexDeclaration(DEV, IDirect3DVertexDeclaration9* v) {
    HRESULT hr = orig<decltype(&hk_SetVertexDeclaration)>(d, kSetVertexDeclaration)(d, v);
    if (SUCCEEDED(hr) && dev_of(d)) {
        const uint32_t id = decl_id(v);
        bind(d, hr, [&](Bound& b) { b.decl = id, b.fvf = 0; }, kMaskInput);
    }
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_SetFVF(DEV, DWORD f) {
    HRESULT hr = orig<decltype(&hk_SetFVF)>(d, kSetFVF)(d, f);
    bind(d, hr, [&](Bound& b) { b.fvf = f, b.decl = 0; }, kMaskInput);
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_SetVertexShader(DEV, IDirect3DVertexShader9* s) {
    HRESULT hr = orig<decltype(&hk_SetVertexShader)>(d, kSetVertexShader)(d, s);
    if (SUCCEEDED(hr) && dev_of(d)) {
        const Hash h = shader_hash(s);
        bind(d, hr, [&](Bound& b) { b.vs = h, b.vs_set = s != nullptr; }, kMaskVs);
    }
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_SetPixelShader(DEV, IDirect3DPixelShader9* s) {
    HRESULT hr = orig<decltype(&hk_SetPixelShader)>(d, kSetPixelShader)(d, s);
    if (SUCCEEDED(hr) && dev_of(d)) {
        const Hash h = shader_hash(s);
        bind(d, hr, [&](Bound& b) { b.ps = h, b.ps_set = s != nullptr; }, kMaskPs);
    }
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_CreateVertexShader(DEV, const DWORD* fn, IDirect3DVertexShader9** out) {
    HRESULT hr = orig<decltype(&hk_CreateVertexShader)>(d, kCreateVertexShader)(d, fn, out);
    if (SUCCEEDED(hr) && out && dev_of(d)) shader_made(*out);
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_CreatePixelShader(DEV, const DWORD* fn, IDirect3DPixelShader9** out) {
    HRESULT hr = orig<decltype(&hk_CreatePixelShader)>(d, kCreatePixelShader)(d, fn, out);
    if (SUCCEEDED(hr) && out && dev_of(d)) shader_made(*out);
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_DrawPrimitive(DEV, D3DPRIMITIVETYPE t, UINT a, UINT b) {
    drawn(d, t);
    return orig<decltype(&hk_DrawPrimitive)>(d, kDrawPrimitive)(d, t, a, b);
}
static HRESULT STDMETHODCALLTYPE hk_DrawIndexedPrimitive(DEV, D3DPRIMITIVETYPE t, INT a, UINT b, UINT c, UINT e, UINT f) {
    drawn(d, t);
    return orig<decltype(&hk_DrawIndexedPrimitive)>(d, kDrawIndexedPrimitive)(d, t, a, b, c, e, f);
}
static HRESULT STDMETHODCALLTYPE hk_DrawPrimitiveUP(DEV, D3DPRIMITIVETYPE t, UINT a, const void* p, UINT s) {
    drawn(d, t);
    return orig<decltype(&hk_DrawPrimitiveUP)>(d, kDrawPrimitiveUP)(d, t, a, p, s);
}
static HRESULT STDMETHODCALLTYPE hk_DrawIndexedPrimitiveUP(DEV, D3DPRIMITIVETYPE t, UINT a, UINT b, UINT c, const void* i, D3DFORMAT f, const void* v, UINT s) {
    drawn(d, t);
    return orig<decltype(&hk_DrawIndexedPrimitiveUP)>(d, kDrawIndexedPrimitiveUP)(d, t, a, b, c, i, f, v, s);
}
// BeginStateBlock, EndStateBlock and Reset write the runtime's own functions into the device's vtable (measured, NULLREF
// device): the slots that were ours before such a call are patched again after it, or every set and draw after it would
// pass by.
struct Slots { void* at[24]; };
static void snap(void* d, Slots& was);
static void rehook(void* d, const Slots& was);
// after a failed Reset only Reset, TestCooperativeLevel and Release may be called: no read back then
static HRESULT STDMETHODCALLTYPE hk_Reset(DEV, D3DPRESENT_PARAMETERS* pp) {
    Slots was;
    snap(d, was);
    HRESULT hr = orig<decltype(&hk_Reset)>(d, kReset)(d, pp);
    rehook(d, was);
    if (SUCCEEDED(hr)) sync(d, dev_of(d));
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_ResetEx(IDirect3DDevice9Ex* d, D3DPRESENT_PARAMETERS* pp, D3DDISPLAYMODEEX* m) {
    Slots was;
    snap(d, was);
    HRESULT hr = orig<decltype(&hk_ResetEx)>(d, kResetEx)(d, pp, m);
    rehook(d, was);
    if (Dev* s = SUCCEEDED(hr) ? dev_of(d) : nullptr) s->pure ? pure_surfaces(d, s) : sync(d, s);
    return hr;
}

static Dev* dev_of_block(IDirect3DStateBlock9* b) {
    IDirect3DDevice9* d = nullptr;
    if (FAILED(b->GetDevice(&d)) || !d) return nullptr;
    Dev* s = dev_of(d);
    d->Release();
    return s;
}
static HRESULT STDMETHODCALLTYPE hk_Apply(IDirect3DStateBlock9* b) {
    HRESULT hr = orig<decltype(&hk_Apply)>(b, kApply)(b);
    IDirect3DDevice9* d = nullptr;
    if (SUCCEEDED(hr) && SUCCEEDED(b->GetDevice(&d)) && d) {
        if (Dev* s = dev_of(d); s && s->pure) {
            std::lock_guard l(s->mx);
            if (auto it = s->blocks.find(b); it != s->blocks.end()) copy_masked(s->b, it->second.b, it->second.mask);
        } else sync(d, s);
        d->Release();
    }
    return hr;
}
// a block's Capture takes the device's values of the states it holds
static HRESULT STDMETHODCALLTYPE hk_Capture(IDirect3DStateBlock9* b) {
    HRESULT hr = orig<decltype(&hk_Capture)>(b, kCapture)(b);
    if (Dev* s = SUCCEEDED(hr) ? dev_of_block(b) : nullptr; s && s->pure) {
        std::lock_guard l(s->mx);
        if (auto it = s->blocks.find(b); it != s->blocks.end()) copy_masked(it->second.b, s->b, it->second.mask);
    }
    return hr;
}
static void hook_block(IDirect3DStateBlock9* b) {
    static const int slots[] = {kCapture, kApply};
    static void* const fns[] = {(void*)&hk_Capture, (void*)&hk_Apply};
    if (b) patch(b, kKindBlock, kBlockMethods, slots, fns, 2);
}
// Under s->mx. ponytail: a game making a block per frame and never at the same address is kept to 4096, then forgotten
// (their Applies then miss a variant until a Reset)
static void keep_block(Dev* s, void* b, const Bound& v, uint64_t mask) {
    if (s->blocks.size() >= 4096 && !s->blocks.count(b)) s->blocks.clear();
    s->blocks[b] = Block{v, mask};
}
static HRESULT STDMETHODCALLTYPE hk_CreateStateBlock(DEV, D3DSTATEBLOCKTYPE t, IDirect3DStateBlock9** out) {
    HRESULT hr = orig<decltype(&hk_CreateStateBlock)>(d, kCreateStateBlock)(d, t, out);
    Dev* s = SUCCEEDED(hr) && out ? dev_of(d) : nullptr;
    if (!s) return hr;
    hook_block(*out);
    if (s->pure) try {
        std::lock_guard l(s->mx);
        keep_block(s, *out, s->b, block_mask(t));
    } catch (...) {}
    return hr;
}
static void recording(void* d, bool on) {
    if (Dev* s = dev_of(d)) {
        std::lock_guard l(s->mx);
        s->recording = on;
    }
}
static HRESULT STDMETHODCALLTYPE hk_BeginStateBlock(DEV) {
    Slots was;
    snap(d, was);
    HRESULT hr = orig<decltype(&hk_BeginStateBlock)>(d, kBeginStateBlock)(d);
    if (SUCCEEDED(hr)) recording(d, true);
    if (Dev* s = SUCCEEDED(hr) ? dev_of(d) : nullptr) {
        std::lock_guard l(s->mx);
        s->rec_mask = 0;
    }
    rehook(d, was);
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_EndStateBlock(DEV, IDirect3DStateBlock9** out) {
    Slots was;
    snap(d, was);
    HRESULT hr = orig<decltype(&hk_EndStateBlock)>(d, kEndStateBlock)(d, out);
    recording(d, false);
    rehook(d, was);
    Dev* s = SUCCEEDED(hr) && out ? dev_of(d) : nullptr;
    if (!s) return hr;
    hook_block(*out);
    if (s->pure) try {
        std::lock_guard l(s->mx);
        keep_block(s, *out, s->rec, s->rec_mask);
    } catch (...) {}
    return hr;
}
#undef DEV

static const int g_dev_slots[] = {kReset, kSetRenderTarget, kSetDepthStencil, kSetRenderState, kCreateStateBlock, kBeginStateBlock,
        kEndStateBlock, kSetTexture, kSetSamplerState, kDrawPrimitive, kDrawIndexedPrimitive, kDrawPrimitiveUP, kDrawIndexedPrimitiveUP,
        kSetVertexDeclaration, kSetFVF, kCreateVertexShader, kSetVertexShader, kCreatePixelShader, kSetPixelShader, kResetEx};
static void* const g_dev_fns[] = {(void*)&hk_Reset, (void*)&hk_SetRenderTarget, (void*)&hk_SetDepthStencil, (void*)&hk_SetRenderState,
        (void*)&hk_CreateStateBlock, (void*)&hk_BeginStateBlock, (void*)&hk_EndStateBlock, (void*)&hk_SetTexture,
        (void*)&hk_SetSamplerState, (void*)&hk_DrawPrimitive, (void*)&hk_DrawIndexedPrimitive, (void*)&hk_DrawPrimitiveUP,
        (void*)&hk_DrawIndexedPrimitiveUP, (void*)&hk_SetVertexDeclaration, (void*)&hk_SetFVF, (void*)&hk_CreateVertexShader,
        (void*)&hk_SetVertexShader, (void*)&hk_CreatePixelShader, (void*)&hk_SetPixelShader, (void*)&hk_ResetEx};
static_assert(std::size(g_dev_slots) == std::size(g_dev_fns) && std::size(g_dev_slots) <= std::size(Slots{}.at));
// ResetEx (last) only in an Ex device's vtable
static size_t dev_slots(bool ex) { return std::size(g_dev_slots) - (ex ? 0 : 1); }
static size_t dev_methods(size_t slots) { return slots == dev_slots(true) ? kDevice9ExMethods : kDevice9Methods; }
static void patch_device(void* d, bool ex) { patch(d, kKindDevice, ex ? kDevice9ExMethods : kDevice9Methods, g_dev_slots, g_dev_fns, dev_slots(ex)); }

static bool ex_of(void* d, size_t& n) {
    Dev* s = dev_of(d);
    if (!s) return false;
    std::lock_guard l(s->mx);
    n = dev_slots(s->ex);
    return true;
}
static void snap(void* d, Slots& was) {
    size_t n;
    if (!ex_of(d, n)) return;
    void** vt = *(void***)d;
    for (size_t i = 0; i < n; ++i) was.at[i] = vt[g_dev_slots[i]];
}
static void rehook(void* d, const Slots& was) {
    size_t n;
    if (ex_of(d, n)) patch(d, kKindDevice, dev_methods(n), g_dev_slots, g_dev_fns, n, was.at);
}

static void hook_device(IDirect3DDevice9* d, bool ex, bool pure) try {
    Dev* s;
    {
        std::lock_guard l(g_mx);
        Dev*& e = g_devs[d];
        if (!e) e = new Dev;
        s = e;
        t_dev = nullptr;
    }
    {
        std::lock_guard l(s->mx);
        s->seen.clear(), s->blocks.clear();  // another device at a freed one's address
        s->pure = pure, s->ex = ex, s->recording = false;
    }
    sync(d, s);
    patch_device(d, ex);
    if (pure) logf("d3d9: a pure device: its state is kept from the game's sets");
} catch (...) {}

// ---- admission, once, at the first device
static std::once_flag g_once;
static bool g_admitted;
static void admit() {
    std::call_once(g_once, [] {
        std::string read;
        const bool whole = !g_dir.empty() && ini_bytes(g_dir + L"scskiller.ini", read);
        t_ini = &read;
        bool risk = false;
        std::string seen;
        const char* why = admission_verdict(whole, read, g_elsewhere, risk, seen);
        t_ini = nullptr;
        refused(why);
        if (why) return;
        g_log = own_file(g_dir + L"scskiller.log", FILE_SHARE_READ | FILE_SHARE_WRITE);
        if (risk) logf("risk=1: recording at the user's own risk, anti-cheat seen: %s", seen.c_str());
        logf("d3d9: recording (%d-bit)", (int)sizeof(void*) * 8);
        load_db();
        g_admitted = g_db != nullptr;
    });
}

static void device_made(IDirect3DDevice9* d, DWORD flags, bool ex) {
    try { admit(); } catch (...) { return; }  // out of memory: this launch isn't recorded
    if (g_admitted && d) hook_device(d, ex, (flags & D3DCREATE_PUREDEVICE) != 0);
}

static HRESULT STDMETHODCALLTYPE hk_CreateDevice(IDirect3D9* self, UINT a, D3DDEVTYPE t, HWND w, DWORD f, D3DPRESENT_PARAMETERS* pp, IDirect3DDevice9** out) {
    HRESULT hr = orig<decltype(&hk_CreateDevice)>(self, kD3DCreateDevice)(self, a, t, w, f, pp, out);
    if (SUCCEEDED(hr) && out) device_made(*out, f, false);
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_CreateDeviceEx(IDirect3D9Ex* self, UINT a, D3DDEVTYPE t, HWND w, DWORD f, D3DPRESENT_PARAMETERS* pp, D3DDISPLAYMODEEX* m, IDirect3DDevice9Ex** out) {
    HRESULT hr = orig<decltype(&hk_CreateDeviceEx)>(self, kD3DCreateDeviceEx)(self, a, t, w, f, pp, m, out);
    if (SUCCEEDED(hr) && out) device_made(*out, f, true);
    return hr;
}

static void hook_d3d(IDirect3D9* d3d, bool ex) {
    static const int slots[] = {kD3DCreateDevice, kD3DCreateDeviceEx};
    static void* const fns[] = {(void*)&hk_CreateDevice, (void*)&hk_CreateDeviceEx};
    if (d3d) patch(d3d, kKindD3D, ex ? kD3D9ExMethods : kD3D9Methods, slots, fns, ex ? 2 : 1);
}

extern "C" IDirect3D9* WINAPI rec9_Direct3DCreate9(UINT v) {
    IDirect3D9* d3d = real_create9(v);
    hook_d3d(d3d, false);
    return d3d;
}
extern "C" HRESULT WINAPI rec9_Direct3DCreate9Ex(UINT v, IDirect3D9Ex** out) {
    HRESULT hr = real_create9ex(v, out);
    if (SUCCEEDED(hr) && out) hook_d3d(*out, true);
    return hr;
}
// The app knows its recorder by this export's name (ScsKiller.ProxyMarker): nothing here warms.
extern "C" void WINAPI rec9_StartWarm() {}

static std::wstring module_dir(HMODULE m) {
    const std::wstring p = module_path(m);
    return p.substr(0, p.find_last_of(L'\\') + 1);
}

BOOL WINAPI DllMain(HINSTANCE self, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_DETACH) return g_log = nullptr, TRUE;  // the CRT closes the streams after this
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(self);
    g_dir = module_dir(self);
    g_elsewhere = _wcsicmp(g_dir.c_str(), module_dir(nullptr).c_str()) != 0;
    return resolve(self);
}
