// scskiller: d3d12.dll proxy. Records every PSO the game creates into scskiller.db and,
// in warm mode, replays the whole db into the driver shader cache so play never compiles.
// Loaded as d3d11.dll (the app's name for it in a DirectX 11 game) it forwards to the system d3d11.dll and records
// each input layout with its vertex shader ('D'); see "D3D11 recorder" below.
//
// Install: copy d3d12.dll next to the game's *-Win64-Shipping.exe.
// Config:  scskiller.ini [scskiller] mode=record|warm threads=N next=<a mod's d3d12.dll, renamed>  (next to the dll), or env
//          SCSKILLER_MODE / SCSKILLER_THREADS (Steam launch options: SCSKILLER_MODE=warm %command%).
//          max_db_bytes=N (ini only; missing = no limit): once scskiller.db has N bytes, no record is appended. Past 3/4
//          of N only records that bring a new pipeline or shader tuple are (store).
//          frames=0 (ini only): no frame times.
//          traversal_suballocate=1 and its other traversal_ keys (ini only): see traversal.h.
//          frames_only=1 (ini only, as d3d11.dll): frame times and the csv's session marker only: no db, no keys, no hook.
// Output:  scskiller.db (append-only), scskiller.log, scskiller_creates.csv (t_ms,kind,known,tuple_known,ms,key,proxy_ms,tid,presents),
//          scskiller_frames.bin (frame_hooks).
// Input:   scskiller.keys (optional, record mode): what the app already imported, never recorded again, and the shaders
//          the game ships, recorded by hash only (load_keys).
// Input:   scskiller_gen.db (optional): generated plan, replayed by warm on top of the db.
//
// db record: u8 tag, u32 len, payload.  'B' blob = sha1[20] + bytes (shader container or root sig).
// 'G' CreateGraphicsPipelineState, 'C' CreateComputePipelineState, 'S' CreatePipelineState stream.
// PSO payloads are canonical (pointers -> blob hashes, padding zeroed); sha1(tag+payload) is the PSO key.
// Their fields are the io_gfx / io_cs / io_sub lists, e.g. 'C' = root sig[20] + CS[20] + u32 NodeMask + u32 Flags (48 bytes).
// Stream output is recorded only when it has entries (Writer::so): 'G' appends it after the last field, 'S' writes it as
// subobject kSoDecl; without entries both are byte-identical to records from before SO was recorded (same keys).
// '1' (gen db only) D3D11 item = u32 stage (1 VS, 2 PS, 3 DS, 4 HS, 5 GS, 6 CS) + sha1[20] of a 'B' DXBC blob:
// warm compiles that one shader on a D3D11 device (warm11.cpp), after all D3D12 items. '2' (gen db only) D3D11 tessellation
// pair = HS sha1[20] + DS sha1[20]: warm draws the two together behind a generated VS (a HS or DS can't be drawn alone).
// 'D' (the D3D11 recorder, d3d11.dll) vertex stages = VS[20] GS[20] HS[20] DS[20] (zero: none), u32
// D3D11_PRIMITIVE_TOPOLOGY, u32 flags (1: a PS), then the input layout as 'G' writes it. The recorder writes one per input
// layout the game creates: its VS, a triangle list, a PS. '3' (gen db only) the same payload: warm draws those stages with
// that layout and topology (warm11_draw).
// 'R' CreateStateObject (ray tracing pipeline or collection), 'A' AddToStateObject = base key[20] + the same body; the
// body and the replay rules are at write_so.
// 'N' NVAPI state of a record's create = the record's key[20] + u32 extension slot (~0u none), u32 space, u32 scope
// (1 device, 2 thread, 3 PSO extension; | 0x100 on every state object's while NVAPI is loaded: its flags are the process's,
// which a recording without the bit may have missed), u32 pipeline creation flags (NvExt); the warm creates that record with it.
// 'W' a create a layer changed (hook_below) = the key[20] of the record the driver got + the key[20] of the record the
// game asked for (zero: the layer's own create, e.g. an add-on's or a bind-time clone). Written only when the two differ.
#define NOMINMAX
#include <windows.h>
#include <shlobj.h>
#include <psapi.h>
#include <share.h>
#include <d3d12.h>
#include <d3d11_3.h>
#include <dxgi1_4.h>
#include <bcrypt.h>
#include <io.h>
#include <intrin.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <charconv>
#include <chrono>
#include <condition_variable>
#include <cstdarg>
#include <cstdio>
#include <deque>
#include <format>
#include <functional>
#include <list>
#include <map>
#include <mutex>
#include <set>
#include <shared_mutex>
#include <string>
#include <string_view>
#include <thread>
#include <tuple>
#include <unordered_map>
#include <unordered_set>
#include <vector>
#include "recorder_key.h"
#include "overlay.h"
#include "frame_enc.h"
#include "admission.h"
#include "admit.h"
#include "sys_dxgi.h"
#include "warm_items.h"
#include "warm_memory.h"

#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "advapi32.lib")  // RegGetValueW: Steam's folder (vac_now)
#pragma comment(lib, "dxguid.lib")  // CLSID_D3D12DeviceFactory, CLSID_D3D12SDKConfiguration
#pragma comment(lib, "shell32.lib")  // SHGetKnownFolderPath (delay-loaded: only at the first device)
#pragma comment(lib, "ole32.lib")    // CoTaskMemFree (likewise)

using Hash = std::array<uint8_t, 20>;
struct HashH { size_t operator()(const Hash& h) const { size_t v; memcpy(&v, h.data(), sizeof v); return v; } };
using HashSet = std::unordered_set<Hash, HashH>;
static const Hash kZero{};

static Hash sha1(const void* p, size_t n) {
    Hash h;
    BCryptHash(BCRYPT_SHA1_ALG_HANDLE, nullptr, 0, (PUCHAR)p, (ULONG)n, h.data(), 20);
    return h;
}
static std::array<char, 41> hex(const Hash& h) {
    std::array<char, 41> x;
    for (int i = 0; i < 20; ++i) sprintf_s(x.data() + 2 * i, 3, "%02x", h[i]);
    return x;
}
static Hash key_of(char tag, const std::string& payload) { return sha1((std::string(1, tag) + payload).data(), payload.size() + 1); }
static size_t align(size_t v, size_t a) { return (v + a - 1) & ~(a - 1); }

// UE appends optional data after the DXBC/DXIL container; the container's own size is at offset 24.
static size_t shader_len(const D3D12_SHADER_BYTECODE& b) {
    auto p = (const uint8_t*)b.pShaderBytecode;
    uint32_t n;
    if (b.BytecodeLength >= 28 && !memcmp(p, "DXBC", 4) && (memcpy(&n, p + 24, 4), n <= b.BytecodeLength)) return n;
    return b.BytecodeLength;
}

static HMODULE g_real;
static bool g_next;  // scskiller.ini next= loaded a mod's d3d12.dll: the device comes from it
static bool g_api11;  // loaded as d3d11.dll: the D3D11 recorder
static bool g_frames_only;  // scskiller.ini frames_only=1 (d3d11.dll only): nothing is recorded
static bool g_elsewhere;  // this dll's folder isn't the exe's: a mod loaded a copy of it from elsewhere (own_dir)
static std::wstring g_dir;
static const std::wstring& admit_dir() { return g_dir; }
static bool g_warm;
static int g_threads;
static FILE *g_db, *g_log, *g_csv;
static const auto g_t0 = std::chrono::steady_clock::now();
static std::mutex g_mx;                                      // ponytail: one lock for db/csv/stats, PSO creation dwarfs it
static HashSet g_blobs_on_disk, g_known /*keys in db at start*/, g_keys /*all keys written*/;
static std::unordered_map<void*, Hash> g_rs_of;              // live root signature object -> blob hash
static std::unordered_map<void*, Hash> g_rs_below_of;        // the same, as created on the device under a layer (hook_below)
static std::unordered_map<Hash, std::string, HashH> g_rs_bytes;   // root sig blobs seen this session
static std::unordered_map<void*, Hash> g_so_key;             // live state object (its ID3D12StateObject pointer) -> record key
struct Rec { char tag; std::string payload; };
static std::vector<Rec> g_recs;                              // every recorded PSO payload (templates for plans)
static std::unordered_map<Hash, std::string_view, HashH> g_blob_bytes; // warm mode only: views into the mapped dbs (map_blobs)
static std::unordered_map<Hash, size_t, HashH> g_rec_idx;       // PSO key -> g_recs index
static std::vector<std::string> g_plan;                          // warm mode only: 'P' payloads from scskiller_gen.db
static std::vector<std::string> g_items11;                       // warm mode only: D3D11 items, the tag ('1', '2', '3') + the payload
static Hash key11(size_t k) { return sha1(g_items11[k].data(), g_items11[k].size()); }  // key_of(tag, payload)
static HashSet g_known_tuples;                                   // (stages + root sig) tuples known at launch
static uint64_t g_lib_loads, g_db_records_at_start, g_creates, g_known_hits, g_tuple_hits, g_slow_known, g_slow_tuple, g_slow_unknown, g_unsupported;
static std::atomic<uint64_t> g_create_fails;
static std::atomic<uint64_t> g_dd_hits, g_dd_held, g_dd_hits_slow, g_dd_held_slow;  // dedupe: re-creates answered, and those whose kept
                                                                                  // pipeline the game still held (driver call); slow: >= 10 ms
static uint64_t g_repeats, g_slow_repeats;  // creates of a desc this process created before, and those over the slow mark
static HashSet g_created;                    // every key this process created (note)
static std::atomic<uint64_t> g_warm_ok, g_warm_fail, g_warm_crash;  // crash: skipped, it removed the device in an earlier run
static std::atomic<uint64_t> g_warm_other;                       // items of another pass: counted done, never created
static std::atomic<uint64_t> g_warm_unsupported;  // of g_warm_ok: refused for blending on a format this GPU can't blend
// SCSKiller_CountCached: d3dscache's ShaderCache_AddValue calls on this thread. A create that made none stored no compile:
// the runtime served it from a cache, the game's delivery when one is registered for the staged exe
static thread_local uint64_t t_cache_adds;
static std::atomic<bool> g_count_cached;
static std::atomic<uint64_t> g_warm_cached;  // of g_warm_ok: pipelines created without a compile stored
static std::atomic<uint64_t> g_warm_skipped;  // skipped for this compile only: a PSO or state object that hung, or a state object that faulted or builds on a skipped one
// work finished inside an item that counts done only at its end: a state object built as another's dependency, a D3D11
// item of a batch (scskiller_warm's progress "work", which the app's stall watchdog reads with done)
static std::atomic<uint64_t> g_work;
static uint64_t warm_done() { return g_warm_ok + g_warm_fail + g_warm_crash + g_warm_other + g_warm_skipped; }
static uint64_t g_total, g_start;                                // warm items; first item to replay (scskiller_warm --start)
static std::vector<uint8_t> g_pass_of;                           // scskiller_warm --pass: each item's pass (empty = no passes)
static uint8_t g_pass;
static bool other_pass(size_t j) { return j < g_pass_of.size() && g_pass_of[j] != g_pass; }
static int g_prio = THREAD_PRIORITY_BELOW_NORMAL;
enum { RUN, PAUSE, STOP };
static std::atomic<int> g_state;                                 // set by scskiller_warm; in-game always RUN
static HANDLE g_warm_done = CreateEventW(nullptr, TRUE, FALSE, nullptr);
static bool g_staged;         // SCSKiller_WarmOptions was called: this is scskiller_warm's staged child, not a play session
static bool g_wrote_session;  // a "#session" line was written to the csv, so DLL_PROCESS_DETACH owes it a matching "#end"
static long long g_session_unix;  // that line's stamp, also the frames file's launch stamp: the two name one launch
static uint64_t g_db_bytes, g_db_cap;  // scskiller.db's size; max_db_bytes
static bool g_db_capped, g_db_full, g_db_selective;
static uint64_t g_db_skipped;  // past 3/4 of max_db_bytes: creates left out as already known (store)

static double now_ms() { return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - g_t0).count(); }
// The stuck limits' clock: now_ms() without the time the process spent suspended (a paused warm). Only the running
// supervisor adds to it (D3D11's starts after D3D12's ends): a gap of over 5 s between its passes (50-100 ms apart) is
// a suspension, and counting it would wake every item in flight stuck for the pause's length.
static std::atomic<double> g_frozen_ms;
static double live_ms() { return now_ms() - g_frozen_ms; }
// A stamp later than a pass's now was made after a resume, before the pass counted the gap: it carries the pause, so
// the item counts from that now, or a hang in it would be found the pause's length late.
void logf(const char* fmt, ...);
static double started(std::atomic<double>& since, double now) {
    double t = since;
    if (!(t > now && since.compare_exchange_strong(t, now))) return t;
    if (t - now > 5000) logf("warm: a call stamped after a resume counts from the pass's now, %.1f s before its stamp", (t - now) / 1000);
    return now;
}
static double live_pass(double& last) {
    double now = now_ms();
    if (now - last > 5000) {
        g_frozen_ms = g_frozen_ms + (now - last);
        logf("warm: suspended for %.1f s (a pause): not counted toward the stuck limits", (now - last) / 1000);
    }
    last = now;
    return now - g_frozen_ms;
}
static std::wstring exe_name() {
    const std::wstring s = module_path(nullptr);
    return s.substr(s.find_last_of(L"\\/") + 1);
}
// Until the first device decides admission, the log isn't opened (a pass-through writes nothing): lines wait here.
// g_log and g_log_deferred change under g_early_mx (admitted), and logf reads them under it.
// Never destroyed (nor are the hooks' own sets): a dll that detaches after this one may still log through a hook.
static std::mutex& g_early_mx = *new std::mutex;
static std::string& g_early = *new std::string;
static bool g_log_deferred;
void logf(const char* fmt, ...) {  // also used by warm11.cpp
    va_list a;
    va_start(a, fmt);
    int n = vsnprintf(nullptr, 0, fmt, a);
    va_end(a);
    if (n < 0) return;
    char t[24];
    snprintf(t, sizeof t, "[%9.1fs] ", now_ms() / 1000);
    std::string line(n + 1, '\0');
    va_start(a, fmt);
    vsnprintf(line.data(), line.size(), fmt, a);
    va_end(a);
    line.back() = '\n';
    std::lock_guard l(g_early_mx);
    if (g_log) fputs((t + line).c_str(), g_log), fflush(g_log);
    else if (g_log_deferred) g_early += t + line;
}

// Matched in the lowercase "<parent folder>\<file name>" of each loaded module.
static const struct { const char* part; const char* what; } kThirdParty[] = {
    {"\\reshade", "ReShade"}, {"\\specialk", "Special K"}, {"\\optiscaler", "OptiScaler"}, {"shaderinjector", "ShaderInjector"},
    {"\\rtsshooks", "RTSS / Afterburner"}, {"\\gameoverlayrenderer", "Steam overlay"}, {"\\discordhook", "Discord overlay"},
    {"ubisoft game launcher\\", "Ubisoft Connect"}, {"\\nvspcap", "NVIDIA overlay"}, {"\\eosovh", "Epic overlay"},
    {"\\graphics-hook", "OBS capture"}, {"\\sl.interposer.dll", "Streamline"},
};
static const struct { const char* name; const char* vendor; } kUmds[] = {  // user-mode drivers: which GPU vendor's is loaded
    {"nvwgf2umx.dll", "NVIDIA"}, {"nvldumdx.dll", "NVIDIA"}, {"amdxc64.dll", "AMD"}, {"amdxx64.dll", "AMD"}, {"atidxx64.dll", "AMD"},
    {"igd12umd64.dll", "Intel"}, {"igd10iumd64.dll", "Intel"}, {"d3d10warp.dll", "WARP"},
};
// At most n bytes of s from at, cut back to a UTF-8 character boundary.
static size_t utf8_cut(const std::string& s, size_t at, size_t n) {
    size_t end = std::min(s.size(), at + n);
    while (end > at + 1 && end < s.size() && ((unsigned char)s[end] & 0xC0) == 0x80) --end;
    return end;
}
// A path as a shared log shows it, never in full: "<system32>\name", "<windows>\name", "<game>\name" (the exe's own folder),
// else "<parent folder>\name" with "~" for a folder right under Users and "(root)" for a drive's root. by_parent: always the
// last form. cls: 0 the game's folder, 1 System32, 2 Windows, 3 other.
static std::string shown(std::wstring path, bool by_parent = false, int* cls = nullptr) {
    static const auto plain = [](std::wstring s) {  // the loader may report a \\?\ spelling
        if (!s.compare(0, 8, L"\\\\?\\UNC\\")) s.erase(0, 6), s[0] = L'\\';
        else if (!s.compare(0, 4, L"\\\\?\\")) s.erase(0, 4);
        return s;
    };
    static const struct Dirs { std::wstring sys, win, game; } d = [] {
        wchar_t b[MAX_PATH];
        auto get = [&](DWORD n) { return n && n < MAX_PATH ? plain(std::wstring(b, n)) : std::wstring(); };
        Dirs r{get(GetSystemDirectoryW(b, MAX_PATH)), get(GetWindowsDirectoryW(b, MAX_PATH)), get(GetModuleFileNameW(nullptr, b, MAX_PATH))};
        r.game.resize(r.game.find_last_of(L'\\') + 1);  // npos + 1 = 0: no game folder
        return r;
    }();
    path = plain(std::move(path));
    if (cls) *cls = 3;
    const size_t slash = path.find_last_of(L'\\');
    if (slash == std::wstring::npos || !slash) return utf8(path);
    // Windows' folders first: a game exe in a drive's root has the whole drive as its folder
    const int c = !d.sys.empty() && under(path, d.sys) ? 1 : !d.win.empty() && under(path, d.win) ? 2 : !d.game.empty() && under(path, d.game) ? 0 : 3;
    if (cls) *cls = c;
    const std::string name = utf8(path.substr(slash + 1));
    if (!by_parent && (c == 1 || c == 2 || (c == 0 && slash + 1 == d.game.size()))) return (c == 1 ? "<system32>\\" : c == 2 ? "<windows>\\" : "<game>\\") + name;
    const size_t from = path.find_last_of(L'\\', slash - 1) + 1;  // npos + 1 = 0: a drive's root
    const size_t above = from > 1 ? path.find_last_of(L'\\', from - 2) + 1 : 0;
    const bool home = from > 1 && !_wcsnicmp(path.c_str() + above, L"Users\\", 6) && above + 6 == from;
    return (!from ? "(root)" : home ? "~" : utf8(path.substr(from, slash - from))) + "\\" + name;
}
// The modules loaded by now, by folder (the game's, System32, Windows, other as shown() has them), and the third-party ones
// support asks about. Bounded: 256 modules, 2 KB of names per folder class, lines of at most 200 bytes of names.
static void log_modules() {
    LARGE_INTEGER t0, t1, fq;
    QueryPerformanceCounter(&t0);
    HMODULE mods[256], self = nullptr;
    DWORD need = 0;
    if (!K32EnumProcessModules(GetCurrentProcess(), mods, sizeof mods, &need)) return logf("modules: not listed (error %lu)", GetLastError());
    const size_t total = need / sizeof(HMODULE), n = std::min(total, std::size(mods));
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)&log_modules, &self);
    static const char* const kClass[] = {"game", "system32", "windows", "other"};
    std::string list[4], flags[16], gpu;
    size_t count[4] = {}, left[4] = {}, nflags = 0;
    for (size_t i = 0; i < n; ++i) {
        wchar_t p[MAX_PATH];
        const DWORD len = GetModuleFileNameW(mods[i], p, MAX_PATH);
        if (!len || len >= MAX_PATH) continue;  // unloaded since the enumeration, or a path too long to classify
        int c;
        std::string name = shown(std::wstring(p, len), false, &c), low = shown(std::wstring(p, len), true);  // low: "<parent>\<name>"
        if (name[0] == '<') name.erase(0, name.find('\\') + 1);  // the class is the line's
        const bool in_sys = c == 1;
        for (auto& ch : low) ch = (char)tolower((unsigned char)ch);
        const std::string file = low.substr(low.find_last_of('\\') + 1);
        ++count[c];
        if (list[c].size() + name.size() < 2048) list[c] += (list[c].empty() ? "" : " ") + name;
        else ++left[c];
        for (auto& k : kUmds)
            if (file == k.name && gpu.find(k.vendor) == std::string::npos) gpu += (gpu.empty() ? "" : ", ") + std::string(k.vendor);
        const char* what = nullptr;
        for (auto& k : kThirdParty)
            if (low.find(k.part) != std::string::npos) what = k.what;
        const bool proxy_name = file == "dxgi.dll" || file == "d3d12.dll" || file == "d3d11.dll" || file == "dinput8.dll";
        if (!what && proxy_name && !in_sys && mods[i] != self)
            what = file == "dinput8.dll" && c == 0 ? "dinput8.dll in the game folder: REFramework or an ASI loader" : "not from System32: a proxy or injector";
        if (what && nflags < std::size(flags)) flags[nflags++] = name + " (" + kClass[c] + "): " + what;
    }
    logf("modules: %zu loaded%s: game %zu, system32 %zu, windows %zu, other %zu (as parent folder\\name); GPU driver: %s", total,
         total > n ? " (the first 256 listed)" : "", count[0], count[1], count[2], count[3], gpu.empty() ? "none loaded yet" : gpu.c_str());
    for (int c = 0; c < 4; ++c)
        for (size_t at = 0; at < list[c].size();) {  // cut at a space, else at a character boundary
            size_t end = list[c].size() - at <= 200 ? list[c].size() : list[c].rfind(' ', at + 200);
            if (end == std::string::npos || end <= at) end = utf8_cut(list[c], at, 200);
            const bool last = end == list[c].size();
            logf("modules %s: %.*s%s", kClass[c], (int)(end - at), list[c].c_str() + at, last && left[c] ? (" +" + std::to_string(left[c]) + " more").c_str() : "");
            at = end + (end < list[c].size() && list[c][end] == ' ');
        }
    for (size_t i = 0; i < nflags; ++i) logf("modules: third-party: %.*s", (int)utf8_cut(flags[i], 0, 200), flags[i].c_str());
    QueryPerformanceCounter(&t1), QueryPerformanceFrequency(&fq);
    logf("modules: listed in %.2f ms", (t1.QuadPart - t0.QuadPart) * 1000.0 / fq.QuadPart);
}

static std::atomic<int> g_admission;  // 0 undecided, 1 records, -1 pass-through
static std::atomic<bool> g_ags_refused;  // set before g_admission: admission saw no signed vendor=amd, or refused
// The lines admission verified inside the signature: the AGS hook and the panel go by these, never by a later read.
struct SignedLines {
    std::wstring vendor, overlay, play_s, tag;
    std::wstring traversal;  // the traversal_ lines (traversal_lines): traversal.h reads its keys here, never the ini
};
static SignedLines g_signed;  // written once, before g_admission
static void unhook_ags(const char* why);
static void ldr_off();
static thread_local bool t_early;    // this thread asks from D3D12GetInterface, before any device
static std::atomic<bool> g_recheck;  // admitted early: the first device runs the verdict again
static bool admitted() {
    if (g_warm) return true;  // scskiller_warm's staged child replays; it is never a game
    static std::once_flag once;
    std::call_once(once, [] {
        const std::wstring ini = g_dir + L"scskiller.ini";
        std::string read;
        const bool whole = ini_bytes(ini, read);
        t_ini = &read;
        struct Live {
            ~Live() { t_ini = nullptr; }
        } live;
        bool risk = false;
        std::string seen;
        const char* why = admission_verdict(whole, read, g_elsewhere, risk, seen);
        {
            std::lock_guard l(g_early_mx);
            if (!why && (g_log = _wfopen((g_dir + L"scskiller.log").c_str(), L"a"))) fputs(g_early.c_str(), g_log), fflush(g_log);
            g_early.clear(), g_log_deferred = false;
            if (!why) g_signed = {ini_string(L"vendor"), ini_string(L"overlay"), ini_string(L"ab_play_s"), ini_string(L"ab_tag"), traversal_lines(ini_string)};
            // the load-time gate can't check sig=: only a vendor=amd inside the ini this admission verified keeps the AGS hook
            g_ags_refused = why || g_signed.vendor != L"amd";
            if (!why && t_early) g_recheck = true;
            g_admission = why ? -1 : 1;
        }
        if (why) unhook_ags("admission refused"), ldr_off();   // an AGS patch from the load path must not stay in a refused process
        else if (g_signed.vendor != L"amd") unhook_ags("vendor=amd isn't in the signed ini");
        if (!why && risk) logf("risk=1: recording at the user's own risk, anti-cheat seen: %s", seen.c_str());
        refused(why);
    });
    // Outside call_once: GetModuleFileNameW takes the loader lock, which a thread waiting on call_once may hold.
    static std::atomic<bool> listed;
    if (g_admission > 0 && !listed.exchange(true)) log_modules();
    return g_admission > 0;
}

// Original device methods; vtable slots are fixed by the COM ABI.
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
// vtslots.cpp checks these against d3d12.h at compile time
enum { SLOT_SIGNAL = 14, SLOT_GFX = 10, SLOT_CS = 11, SLOT_RS = 16, SLOT_CREATELIB = 44, SLOT_STREAM = 47, SLOT_CSO = 62, SLOT_ADDSO = 66 };
enum { LIB_LOADGFX = 9, LIB_LOADCS = 10, LIB_LOADSTREAM = 13 };  // ID3D12PipelineLibrary(1)
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

// Canonical serialization: one field list (io_*) drives both Writer and Reader.
template <class T> static void zero_gap(T& v, size_t from, size_t to) { memset((char*)&v + from, 0, to - from); }
#define GAP_AFTER(T, a, b) offsetof(T, a) + sizeof(((T*)0)->a), offsetof(T, b)

// NVAPI state NVIDIA's compiler keys on (selftest nvext): the shader-extension slot, set for the device
// (NvAPI_D3D12_SetNvShaderExtnSlotSpace), for the calling thread (...LocalThread) or for one PSO (the SET_SHADER_EXTENSION_SLOT_AND_SPACE
// extension of NvAPI_D3D12_Create*PipelineState), and a state object's pipeline creation flags (NvAPI_D3D12_SetCreatePipelineStateOptions:
// opacity micromaps, linear swept spheres). nvapi.h: the flags are global, apply to every later create, and only ray tracing pipelines use them.
struct NvExt { uint32_t slot = ~0u, space = 0, scope = 0, opts = 0; };
static thread_local NvExt t_nv;
static thread_local bool t_presenting;  // this thread has presented a frame (timed_present): its creates stall frames
static ov::Clock g_ovclock;  // the overlay's play time
static ov::Creates g_ovcreates;  // the overlay's stutter flash: the last creates the csv got
static std::atomic<bool> g_ov_nvidia;  // a device on NVIDIA's adapter: a RayQuery PSO's create has its floor
static std::unordered_map<Hash, NvExt, HashH> g_nvext;  // 'N' records by the key of the record they apply to
static uint64_t g_nv_creates;
static std::atomic<uint64_t> g_nv_dev{~0u};  // slot | space << 32. ponytail: one for the process; per device if a game sets two
static std::atomic<uint32_t> g_nv_opts;       // ponytail: one for the process, like the slot
static std::atomic<bool> g_nv_loaded;          // NVAPI is in the process (nv_hooks)
static NvExt nv_now(char tag) {
    NvExt x = t_nv;
    if (x.slot != ~0u) x.scope = 2;
    else if (uint64_t d = g_nv_dev; (uint32_t)d != ~0u) x.slot = (uint32_t)d, x.space = (uint32_t)(d >> 32), x.scope = 1;
    if ((tag == 'R' || tag == 'A') && g_nv_loaded.load(std::memory_order_relaxed)) x.opts = g_nv_opts.load(std::memory_order_relaxed), x.scope |= 0x100;
    return x;
}

// The driver compiles per (shader stages + root signature), ignoring fixed-function state and input layout,
// so the "tuple" of those hashes is what decides whether a create hits the driver cache.
using Tuple = std::vector<std::pair<uint32_t, Hash>>;  // (subobject type of the stage, shader hash)
static Hash tuple_hash(const Hash& rs, Tuple t) {
    std::sort(t.begin(), t.end());
    std::string b(rs.begin(), rs.end());
    for (auto& [st, h] : t) b += (char)st, b.append(h.begin(), h.end());
    return sha1(b.data(), b.size());
}

struct Writer {
    char tag;
    std::chrono::steady_clock::time_point t0 = std::chrono::steady_clock::now();  // hook entry: the proxy's own time starts here
    std::string s;
    bool ok = true;
    const char* why = "";  // reason when !ok, logged for the first few
    std::vector<std::pair<Hash, std::string_view>> blobs;  // shader bytes live in game memory during the call
    std::vector<Hash> rsigs;
    Hash rsh{};
    void* rsp = nullptr;  // the root signature object: the dedupe key's identity, never written to the record
    D3D12_CACHED_PIPELINE_STATE cached{};  // the CachedPSO blob, likewise: hashed only for a dedupe
    uint32_t rast = 0;    // the stream output's rasterized stream, which a desc without entries doesn't record
    bool no_dedupe = false;  // a stream with two CachedPSO subobjects: the runtime refuses it, a hit mustn't
    Tuple tup;
    NvExt nv = nv_now(tag);  // at the hook's entry, on the creating thread
    bool below = false;   // a create under a layer: its root signatures are the ones the layer gave the device

    void raw(const void* p, size_t n) { s.append((const char*)p, n); }
    template <class T> void pod(T& v) { raw(&v, sizeof v); }
    void u32(uint32_t v) { raw(&v, 4); }
    void hash(const Hash& h) { raw(h.data(), 20); }
    void cached_pso(const D3D12_CACHED_PIPELINE_STATE& c) { no_dedupe |= cached.pCachedBlob != nullptr, cached = c; }
    void sh(D3D12_SHADER_BYTECODE& b, uint32_t stage) {
        if (!b.pShaderBytecode || !b.BytecodeLength) return hash(kZero);
        size_t n = shader_len(b);
        Hash h = sha1(b.pShaderBytecode, n);
        blobs.push_back({h, {(const char*)b.pShaderBytecode, n}});
        tup.push_back({stage, h});
        hash(h);
    }
    void rs(ID3D12RootSignature*& p) {
        if (!p) return hash(kZero);
        std::lock_guard l(g_mx);
        // a layer that rewrites root signatures returns the object it created from its own blob, which the game's hook
        // then maps to the game's blob: under the layer that object is its own
        const Hash* h = nullptr;
        if (auto it = g_rs_below_of.find(p); below && it != g_rs_below_of.end()) h = &it->second;
        if (auto it = g_rs_of.find(p); !h && it != g_rs_of.end()) h = &it->second;
        if (!h) { ok = false, why = "root signature was not created through a hooked device"; return; }
        rsigs.push_back(*h);
        rsh = *h, rsp = p;
        hash(*h);
    }
    // Stream output, only when it declares entries (NumEntries 0 = no SO, written as nothing: older records keep their
    // keys): u32 n, n x (u32 stream, str8 semantic, u32 index, start component, component count, output slot),
    // u32 strides n + strides, u32 rasterized stream. 'G': after the desc's last field; 'S': as subobject kSoDecl.
    void so(D3D12_STREAM_OUTPUT_DESC& d) {
        rast = d.RasterizedStream;
        if (!d.NumEntries) return;
        if (!d.pSODeclaration) { ok = false, why = "stream output entries without a declaration"; return; }
        u32(d.NumEntries);
        for (UINT i = 0; i < d.NumEntries; ++i) {
            auto& e = d.pSODeclaration[i];
            u32(e.Stream);
            if (!e.SemanticName) u32(0xFFFFFFFF);  // a gap in the buffer
            else { uint32_t n = (uint32_t)strlen(e.SemanticName); u32(n), raw(e.SemanticName, n); }
            u32(e.SemanticIndex), u32(e.StartComponent), u32(e.ComponentCount), u32(e.OutputSlot);
        }
        u32(d.pBufferStrides ? d.NumStrides : 0);
        for (UINT i = 0; d.pBufferStrides && i < d.NumStrides; ++i) u32(d.pBufferStrides[i]);
        u32(d.RasterizedStream);
    }
    void so_tail(D3D12_STREAM_OUTPUT_DESC& d) { so(d); }
    void wstr(LPCWSTR p) {  // u32 UTF-16 length (0xFFFFFFFF = null) + the characters
        if (!p) return u32(0xFFFFFFFF);
        uint32_t n = (uint32_t)wcslen(p);
        u32(n), raw(p, 2 * n);
    }
    void lib(const D3D12_SHADER_BYTECODE& b) {  // a DXIL library: a blob like a shader, but no stage (not in the tuple)
        if (!b.pShaderBytecode || !b.BytecodeLength) { ok = false, why = "empty DXIL library"; return hash(kZero); }
        size_t n = shader_len(b);
        Hash h = sha1(b.pShaderBytecode, n);
        blobs.push_back({h, {(const char*)b.pShaderBytecode, n}});
        hash(h);
    }
    void so_ref(ID3D12StateObject* p);  // a collection / base: the key of its record
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
        for (UINT i = 0; i < 8; ++i) u32(i < n ? f[i] : DXGI_FORMAT_UNKNOWN);  // slots past n may hold garbage
    }
    void ds(D3D12_DEPTH_STENCIL_DESC& v) { auto c = v; zero_gap(c, GAP_AFTER(D3D12_DEPTH_STENCIL_DESC, StencilWriteMask, FrontFace)); pod(c); }
    void ds(D3D12_DEPTH_STENCIL_DESC1& v) { auto c = v; zero_gap(c, GAP_AFTER(D3D12_DEPTH_STENCIL_DESC1, StencilWriteMask, FrontFace)); pod(c); }
    void ds(D3D12_DEPTH_STENCIL_DESC2& v) {
        auto c = v;
        for (auto* f : {&c.FrontFace, &c.BackFace}) zero_gap(*f, offsetof(D3D12_DEPTH_STENCILOP_DESC1, StencilWriteMask) + 1, sizeof *f);
        pod(c);
    }
};

static ID3D12RootSignature* warm_rootsig(const Hash& h);

// A generated plan item: replay a recorded template with these shaders / root signature / input layout swapped in.
struct Override {
    Hash rs{};
    Hash sh[32]{};  // by subobject type (VS=1 .. MS=25)
    bool has_il = false;
    D3D12_INPUT_LAYOUT_DESC il{};
};

struct Reader {
    const std::string& s;
    std::deque<std::wstring> wstrs;
    size_t pos = 0;
    bool ok = true;
    bool resolve = true;  // false: only collect the tuple, create nothing
    const Override* ovr = nullptr;
    const char* why = "";  // why ok went false (the first reason), for the warm's failure log
    Hash rsh{};
    Tuple tup;
    std::deque<std::string> strs;  // deques: element addresses stay valid as they grow
    std::deque<std::vector<D3D12_INPUT_ELEMENT_DESC>> ies;
    std::deque<std::vector<D3D12_VIEW_INSTANCE_LOCATION>> vils;
    D3D12_BLEND_DESC blend{};  // copies: a stream's buffer moves while it's read
    UINT nrt = 0;
    DXGI_FORMAT rtv[8]{};
    bool unblendable = false;

    void fail(const char* w) { if (ok) why = w; ok = false; }

    void raw(void* p, size_t n) {
        if (pos + n > s.size()) { fail("record shorter than its fields (truncated or mis-encoded payload)"); memset(p, 0, n); return; }
        memcpy(p, s.data() + pos, n);
        pos += n;
    }
    template <class T> void pod(T& v) { raw(&v, sizeof v); }
    void pod(D3D12_BLEND_DESC& v) { raw(&v, sizeof v), blend = v; }
    uint32_t u32() { uint32_t v; raw(&v, 4); return v; }
    uint32_t count() { uint32_t n = u32(); if (n > s.size()) fail("malformed element count"); return ok ? n : 0; }
    Hash hash() { Hash h; raw(h.data(), 20); return h; }
    void sh(D3D12_SHADER_BYTECODE& b, uint32_t stage) {
        Hash h = hash();
        if (ovr && stage < 32 && ovr->sh[stage] != kZero) h = ovr->sh[stage];
        b = {};
        if (h == kZero) return;
        tup.push_back({stage, h});
        if (!resolve) return;
        auto it = g_blob_bytes.find(h);
        if (it == g_blob_bytes.end()) { fail("shader blob not in the db"); return; }
        b = {it->second.data(), it->second.size()};
    }
    void rs(ID3D12RootSignature*& p) {
        Hash h = hash();
        if (ovr && ovr->rs != kZero) h = ovr->rs;
        rsh = h;
        p = h == kZero || !resolve ? nullptr : warm_rootsig(h);
        if (h != kZero && resolve && !p) fail(g_blob_bytes.count(h) ? "root signature creation failed" : "root signature blob not in the db");
    }
    bool so_decl = false;  // read_stream: the next stream output subobject was written as kSoDecl (has a body)
    std::deque<std::vector<D3D12_SO_DECLARATION_ENTRY>> soes;
    std::deque<std::vector<UINT>> strides;
    void so(D3D12_STREAM_OUTPUT_DESC& d) {
        d = {};
        if (!so_decl) return;
        so_decl = false;
        auto& v = soes.emplace_back(count());
        for (auto& e : v) {
            e.Stream = u32();
            uint32_t n = u32();
            if (n != 0xFFFFFFFF) {
                if (n > s.size()) { fail("malformed stream output semantic"); return; }
                auto& name = strs.emplace_back(n, '\0');
                raw(name.data(), n);
                e.SemanticName = name.c_str();
            }
            e.SemanticIndex = u32(), e.StartComponent = (BYTE)u32(), e.ComponentCount = (BYTE)u32(), e.OutputSlot = (BYTE)u32();
        }
        auto& st = strides.emplace_back(count());
        for (auto& x : st) x = u32();
        d = {v.data(), (UINT)v.size(), st.empty() ? nullptr : st.data(), (UINT)st.size(), u32()};
    }
    void so_tail(D3D12_STREAM_OUTPUT_DESC& d) { so_decl = pos < s.size(); so(d); }  // older 'G' records end before it
    void il(D3D12_INPUT_LAYOUT_DESC& d) {
        auto& v = ies.emplace_back(count());
        for (auto& e : v) {
            auto& name = strs.emplace_back(count(), '\0');
            raw(name.data(), name.size());
            e.SemanticName = name.c_str();
            e.SemanticIndex = u32(), e.Format = (DXGI_FORMAT)u32(), e.InputSlot = u32(), e.AlignedByteOffset = u32();
            e.InputSlotClass = (D3D12_INPUT_CLASSIFICATION)u32(), e.InstanceDataStepRate = u32();
        }
        d = {v.empty() ? nullptr : v.data(), (UINT)v.size()};
        if (ovr && ovr->has_il) d = ovr->il;
    }
    void vi(D3D12_VIEW_INSTANCING_DESC& d) {
        auto& v = vils.emplace_back(count());
        for (auto& l : v) pod(l);
        d.ViewInstanceCount = (UINT)v.size();
        d.pViewInstanceLocations = v.empty() ? nullptr : v.data();
        d.Flags = (D3D12_VIEW_INSTANCING_FLAGS)u32();
    }
    void rtfmt(UINT& n, DXGI_FORMAT* f) { n = nrt = u32(); for (UINT i = 0; i < 8; ++i) rtv[i] = f[i] = (DXGI_FORMAT)u32(); }
    template <class T> void ds(T& v) { pod(v); }
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
        if (!resolve) return;
        auto it = g_blob_bytes.find(h);
        if (it == g_blob_bytes.end()) { fail("DXIL library blob not in the db"); return; }
        b = {it->second.data(), it->second.size()};
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
    io.so_tail(d.StreamOutput);  // last: a record without stream output ends before it (the format before SO was recorded)
    // CachedPSO deliberately dropped: replay must compile, not load a blob.
}

// A stream's stream output subobject with entries is written under this type (its body follows); one without entries
// keeps D3D12's own type 7 and no body, as before stream output was recorded.
static const uint32_t kSoDecl = 0x10007;

template <class IO> static void io_cs(IO& io, D3D12_COMPUTE_PIPELINE_STATE_DESC& d) {
    io.rs(d.pRootSignature), io.sh(d.CS, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_CS), io.pod(d.NodeMask), io.pod(d.Flags);
}

// Stream subobjects are laid out like CD3DX12's alignas(void*) { TYPE; Inner; } pairs.
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
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_BLEND: io.pod(*(D3D12_BLEND_DESC*)p); break;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RENDER_TARGET_FORMATS: {
        auto& r = *(D3D12_RT_FORMAT_ARRAY*)p;
        io.rtfmt(r.NumRenderTargets, r.RTFormats);
        break;
    }
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL: io.ds(*(D3D12_DEPTH_STENCIL_DESC*)p); break;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL1: io.ds(*(D3D12_DEPTH_STENCIL_DESC1*)p); break;
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL2: io.ds(*(D3D12_DEPTH_STENCIL_DESC2*)p); break;
    default: { Sub s; sub_info(t, s); io.raw(p, s.size); }  // plain-old-data subobjects
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
        if (!sub_info(t, s)) { w.ok = false, w.why = "unknown stream subobject type"; logf("unknown stream subobject type %u", t); return; }
        size_t in = align(pos + 4, s.align);
        bool decl = t == D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_STREAM_OUTPUT && ((D3D12_STREAM_OUTPUT_DESC*)(base + in))->NumEntries;
        if (t != D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_CACHED_PSO) w.u32(decl ? kSoDecl : t), io_sub(w, t, base + in), ++count;
        else w.cached_pso(*(D3D12_CACHED_PIPELINE_STATE*)(base + in));
        pos = align(in + s.size, sizeof(void*));
    }
    memcpy(&w.s[count_at], &count, 4);
}

static D3D12_PIPELINE_STATE_STREAM_DESC read_stream(Reader& r, std::vector<uint64_t>& buf) {
    size_t pos = 0;
    for (uint32_t i = 0, n = r.count(); i < n && r.ok; ++i) {
        uint32_t t = r.u32();
        if ((r.so_decl = t == kSoDecl)) t = D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_STREAM_OUTPUT;
        Sub s;
        if (!sub_info(t, s)) { r.fail("unknown stream subobject type"); break; }
        size_t in = align(pos + 4, s.align), end = align(in + s.size, sizeof(void*));
        buf.resize(end / 8);
        auto b = (uint8_t*)buf.data();
        memcpy(b + pos, &t, 4);
        io_sub(r, t, b + in);
        pos = end;
    }
    return {pos, buf.data()};
}

// State object body ('R', or 'A' after its base key): u32 D3D12_STATE_OBJECT_TYPE (collection or ray tracing pipeline),
// u32 n, then n subobjects in the game's order, each u32 D3D12_STATE_SUBOBJECT_TYPE + its canonical form:
//   STATE_OBJECT_CONFIG u32 flags | GLOBAL/LOCAL_ROOT_SIGNATURE root sig[20] | NODE_MASK u32
//   DXIL_LIBRARY library blob[20] + exports | EXISTING_COLLECTION the collection's record key[20] + exports
//   SUBOBJECT_TO_EXPORTS_ASSOCIATION u32 index of the associated subobject in this desc + names
//   DXIL_SUBOBJECT_TO_EXPORTS_ASSOCIATION str subobject name + names
//   RAYTRACING_SHADER_CONFIG u32 payload, u32 attributes | RAYTRACING_PIPELINE_CONFIG u32 depth | CONFIG1 u32 depth, u32 flags
//   HIT_GROUP str export, u32 type, str any hit, str closest hit, str intersection
// exports = u32 n + n x (str name, str rename, u32 flags); names = u32 n + n x str; str = u32 UTF-16 length
// (0xFFFFFFFF = null) + characters. Work graphs, generic programs and unknown subobjects are not recorded (logged).
static ID3D12StateObject* so_id(IUnknown* p) {  // identity: a game may hold another interface of the same object
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
        w.ok = false, w.why = "state object type not supported (work graph / executable)";
        return;
    }
    w.u32(d.Type), w.u32(d.NumSubobjects);
    auto exports = [&](UINT n, const D3D12_EXPORT_DESC* e) {
        w.u32(n);
        for (UINT i = 0; i < n; ++i) w.wstr(e[i].Name), w.wstr(e[i].ExportToRename), w.u32(e[i].Flags);
    };
    auto names = [&](UINT n, LPCWSTR const* s) {
        w.u32(n);
        for (UINT i = 0; i < n; ++i) w.wstr(s[i]);
    };
    for (UINT i = 0; i < d.NumSubobjects && w.ok; ++i) {
        auto& so = d.pSubobjects[i];
        auto p = so.pDesc;
        w.u32(so.Type);
        switch (so.Type) {
        case D3D12_STATE_SUBOBJECT_TYPE_STATE_OBJECT_CONFIG: w.u32(((const D3D12_STATE_OBJECT_CONFIG*)p)->Flags); break;
        case D3D12_STATE_SUBOBJECT_TYPE_GLOBAL_ROOT_SIGNATURE:
        case D3D12_STATE_SUBOBJECT_TYPE_LOCAL_ROOT_SIGNATURE: { auto r = *(ID3D12RootSignature* const*)p; w.rs(r); break; }
        case D3D12_STATE_SUBOBJECT_TYPE_NODE_MASK: w.u32(((const D3D12_NODE_MASK*)p)->NodeMask); break;
        case D3D12_STATE_SUBOBJECT_TYPE_DXIL_LIBRARY: {
            auto& l = *(const D3D12_DXIL_LIBRARY_DESC*)p;
            w.lib(l.DXILLibrary), exports(l.NumExports, l.pExports);
            break;
        }
        case D3D12_STATE_SUBOBJECT_TYPE_EXISTING_COLLECTION: {
            auto& c = *(const D3D12_EXISTING_COLLECTION_DESC*)p;
            w.so_ref(c.pExistingCollection), exports(c.NumExports, c.pExports);
            break;
        }
        case D3D12_STATE_SUBOBJECT_TYPE_SUBOBJECT_TO_EXPORTS_ASSOCIATION: {
            auto& a = *(const D3D12_SUBOBJECT_TO_EXPORTS_ASSOCIATION*)p;
            ptrdiff_t k = a.pSubobjectToAssociate - d.pSubobjects;
            if (k < 0 || k >= (ptrdiff_t)d.NumSubobjects) { w.ok = false, w.why = "association to a subobject outside the desc"; break; }
            w.u32((uint32_t)k), names(a.NumExports, a.pExports);
            break;
        }
        case D3D12_STATE_SUBOBJECT_TYPE_DXIL_SUBOBJECT_TO_EXPORTS_ASSOCIATION: {
            auto& a = *(const D3D12_DXIL_SUBOBJECT_TO_EXPORTS_ASSOCIATION*)p;
            w.wstr(a.SubobjectToAssociate), names(a.NumExports, a.pExports);
            break;
        }
        case D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_SHADER_CONFIG: {
            auto& c = *(const D3D12_RAYTRACING_SHADER_CONFIG*)p;
            w.u32(c.MaxPayloadSizeInBytes), w.u32(c.MaxAttributeSizeInBytes);
            break;
        }
        case D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_PIPELINE_CONFIG: w.u32(((const D3D12_RAYTRACING_PIPELINE_CONFIG*)p)->MaxTraceRecursionDepth); break;
        case D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_PIPELINE_CONFIG1: {
            auto& c = *(const D3D12_RAYTRACING_PIPELINE_CONFIG1*)p;
            w.u32(c.MaxTraceRecursionDepth), w.u32(c.Flags);
            break;
        }
        case D3D12_STATE_SUBOBJECT_TYPE_HIT_GROUP: {
            auto& h = *(const D3D12_HIT_GROUP_DESC*)p;
            w.wstr(h.HitGroupExport), w.u32(h.Type), w.wstr(h.AnyHitShaderImport), w.wstr(h.ClosestHitShaderImport), w.wstr(h.IntersectionShaderImport);
            break;
        }
        default: w.ok = false, w.why = "state subobject type not supported (work graph / generic program)";
        }
    }
}

// Storage for a replayed desc: every pointer the subobjects hold stays valid while this lives.
struct SoDesc {
    D3D12_STATE_OBJECT_TYPE type{};
    std::vector<D3D12_STATE_SUBOBJECT> subs;  // sized once: associations point into it
    std::deque<std::vector<D3D12_EXPORT_DESC>> exps;
    std::deque<std::vector<LPCWSTR>> lists;
    std::deque<std::array<uint32_t, 2>> pods;  // configs, node mask: one or two u32s each
    std::deque<ID3D12RootSignature*> rsigs;    // D3D12_GLOBAL/LOCAL_ROOT_SIGNATURE hold one pointer
    std::deque<D3D12_DXIL_LIBRARY_DESC> libs;
    std::deque<D3D12_EXISTING_COLLECTION_DESC> colls;
    std::deque<D3D12_SUBOBJECT_TO_EXPORTS_ASSOCIATION> assoc;
    std::deque<D3D12_DXIL_SUBOBJECT_TO_EXPORTS_ASSOCIATION> dassoc;
    std::deque<D3D12_HIT_GROUP_DESC> hgs;
};

// The body of an 'R' (or an 'A' after its base key) back into a desc; dep resolves a collection's record key.
static void read_so(Reader& r, SoDesc& d, const std::function<ID3D12StateObject*(const Hash&)>& dep) {
    d.type = (D3D12_STATE_OBJECT_TYPE)r.u32();
    UINT n = r.count();
    d.subs.assign(n, {});
    auto exports = [&](UINT& num, const D3D12_EXPORT_DESC*& ptr) {
        auto& v = d.exps.emplace_back(r.count());
        for (auto& e : v) e.Name = r.wstr(), e.ExportToRename = r.wstr(), e.Flags = (D3D12_EXPORT_FLAGS)r.u32();
        num = (UINT)v.size(), ptr = v.empty() ? nullptr : v.data();
    };
    auto names = [&](UINT& num, LPCWSTR*& ptr) {
        auto& v = d.lists.emplace_back(r.count());
        for (auto& x : v) x = r.wstr();
        num = (UINT)v.size(), ptr = v.empty() ? nullptr : v.data();
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
        case D3D12_STATE_SUBOBJECT_TYPE_STATE_OBJECT_CONFIG: case D3D12_STATE_SUBOBJECT_TYPE_NODE_MASK:
        case D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_PIPELINE_CONFIG: desc = pod(1); break;
        case D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_SHADER_CONFIG: case D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_PIPELINE_CONFIG1: desc = pod(2); break;
        case D3D12_STATE_SUBOBJECT_TYPE_GLOBAL_ROOT_SIGNATURE:
        case D3D12_STATE_SUBOBJECT_TYPE_LOCAL_ROOT_SIGNATURE: { auto& x = d.rsigs.emplace_back(); r.rs(x); desc = &x; break; }
        case D3D12_STATE_SUBOBJECT_TYPE_DXIL_LIBRARY: { auto& x = d.libs.emplace_back(); r.lib(x.DXILLibrary), exports(x.NumExports, x.pExports); desc = &x; break; }
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
            x.SubobjectToAssociate = r.wstr(), names(x.NumExports, x.pExports);
            desc = &x;
            break;
        }
        case D3D12_STATE_SUBOBJECT_TYPE_HIT_GROUP: {
            auto& x = d.hgs.emplace_back();
            x.HitGroupExport = r.wstr(), x.Type = (D3D12_HIT_GROUP_TYPE)r.u32();
            x.AnyHitShaderImport = r.wstr(), x.ClosestHitShaderImport = r.wstr(), x.IntersectionShaderImport = r.wstr();
            desc = &x;
            break;
        }
        default: r.fail("unknown state subobject type");
        }
        d.subs[i] = {t, desc};
    }
    if (r.ok && r.pos != r.s.size()) r.fail("state object record longer than its subobjects");
}

// The records an 'R' / 'A' builds on (collections, the base): what the warm must create first and keep alive.
static std::vector<Hash> so_deps(const std::string& payload, char tag) {
    std::vector<Hash> deps;
    Reader r{payload};
    r.resolve = false;
    if (tag == 'A') deps.push_back(r.hash());
    SoDesc d;
    read_so(r, d, [&](const Hash& k) { deps.push_back(k); return (ID3D12StateObject*)nullptr; });
    return deps;
}

// scskiller.db: what the game created (append-only). scskiller_gen.db (optional, read-only, the materialized plan):
// 'B' blobs + 'P' plan items = template PSO key[20], root sig[20], u32 n, n x (u32 stage, hash[20]),
// then u32 0xFFFFFFFF (keep the template's input layout) or a canonical input layout.
// The db's and the csv's bytes, under g_mx, written to the stream's handle (never through the stream, whose locks a
// thread killed at exit may hold). A fixed buffer: nothing is allocated, and DllMain can read it whatever thread died
// mid-append (len is stored after the bytes). A byte range leaves it only once its WriteFile returned, so DllMain writes
// a range again that the exit stopped the flusher in: at its offset for the db (this process's alone, deny-write), and
// for the csv (appended, another process may share it) only when the file didn't grow by it.
struct Out {
    HANDLE h = nullptr;
    uint64_t at = ~0ull;  // the db: the file offset of buf[0]; ~0: appended at the file's end
    std::atomic<size_t> len{0}, inflight{0};
    uint64_t inflight_end = 0;
    bool failed = false;  // a write failed: nothing more is written
    size_t cap;
    char* buf;
};
static char g_dbbuf[2 << 20], g_csvbuf[256 << 10];
static bool g_flushing;  // under g_mx: the flusher runs, else each record is written as it comes
static Out g_dbo{.cap = sizeof g_dbbuf, .buf = g_dbbuf}, g_csvo{.cap = sizeof g_csvbuf, .buf = g_csvbuf};

static bool out_raw(Out& o, const char* p, size_t n, uint64_t at) {
    for (DWORD done = 0; n && !o.failed; p += done, n -= done, at += at == ~0ull ? 0 : done) {
        OVERLAPPED ov{};
        ov.Offset = (DWORD)at, ov.OffsetHigh = (DWORD)(at >> 32);
        if (!WriteFile(o.h, p, (DWORD)std::min<size_t>(n, 1u << 30), &done, &ov) || !done) o.failed = true;
    }
    return !o.failed;
}
static void out_write(Out& o) {
    size_t n = o.len;
    if (!o.h || o.failed || !n) return;
    LARGE_INTEGER size{};
    if (o.at == ~0ull && !GetFileSizeEx(o.h, &size)) { o.failed = true; return; }
    o.inflight_end = (o.at == ~0ull ? (uint64_t)size.QuadPart : o.at) + n;
    o.inflight = n;
    if (out_raw(o, o.buf, n, o.at)) {
        o.len = 0;  // before at moves: DllMain never writes these bytes at the next offset
        if (o.at != ~0ull) o.at += n;
    }
    o.inflight = 0;
}
static void out_add(Out& o, const void* p, size_t n) {
    if (!o.h || o.failed) return;
    if (o.len + n > o.cap) out_write(o);
    if (n > o.cap) {
        if (out_raw(o, (const char*)p, n, o.at) && o.at != ~0ull) o.at += n;
    } else if (!o.failed) memcpy(o.buf + o.len, p, n), o.len.store(o.len + n, std::memory_order_release);
}
// DllMain at exit: no lock, nothing allocated, the other threads are gone.
static void out_exit(Out& o) {
    size_t n = o.len, skip = 0;
    if (!o.h || o.failed || !n) return;
    if (size_t f = o.inflight; f && o.at == ~0ull) {
        LARGE_INTEGER size{};
        if (!GetFileSizeEx(o.h, &size) || (uint64_t)size.QuadPart >= o.inflight_end) skip = f;
    }
    if (n > skip) out_raw(o, o.buf + skip, n - skip, o.at);
}
// Lines as the text-mode stream wrote them: \n as \r\n. One cut at 511 characters keeps its end of line.
static void csv_line(const char* fmt, ...) {
    char b[512], crlf[1024];
    va_list a;
    va_start(a, fmt);
    int n = vsnprintf(b, sizeof b, fmt, a);
    va_end(a);
    if (n <= 0) return;
    if (n >= (int)sizeof b) n = sizeof b - 1, b[n - 1] = '\n';
    size_t m = 0;
    for (int i = 0; i < n; ++i) {
        if (b[i] == '\n') crlf[m++] = '\r';
        crlf[m++] = b[i];
    }
    out_add(g_csvo, crlf, m);
    if (!g_flushing) out_write(g_csvo);
}

static void put(char tag, const void* a, size_t an, const void* b = nullptr, size_t bn = 0) {
    uint32_t len = (uint32_t)(an + bn);
    out_add(g_dbo, &tag, 1), out_add(g_dbo, &len, 4), out_add(g_dbo, a, an);
    if (bn) out_add(g_dbo, b, bn);
    g_db_bytes += 5 + an + bn;
    if (!g_flushing) out_write(g_dbo);
}

static ID3D12Device* g_warm_dev;  // null in a warm of D3D11 items only (SCSKiller_StartWarm11)
static LUID g_warm_luid;          // the adapter the warm's D3D11 devices are created on
static ID3D12Device2* g_warm_dev2;
// A warm run with the game's layer (scskiller_warm --layer): the device under it, for what the layer made ('W'), which
// is replayed there as recorded (build)
static ID3D12Device* g_warm_real;
static ID3D12Device2* g_warm_real2;
static HashSet g_layer_made;
static thread_local bool t_real;
static uintptr_t g_layer_lo, g_layer_hi;  // the layer's module: a PSO object of its own may still compile on its threads
static ID3D12Device5* g_warm_dev5;
static ID3D12Device7* g_warm_dev7;

static int (*g_nv_set_thread)(IUnknown*, uint32_t, uint32_t);
static int (*g_nv_set_opts)(ID3D12Device5*, const uint32_t*);  // {version, flags}

static const NvExt* nvext_of(char tag, const std::string& payload) {
    if (g_nvext.empty()) return nullptr;
    auto it = g_nvext.find(key_of(tag, payload));
    return it == g_nvext.end() ? nullptr : &it->second;
}

// Every slot scope is replayed on the creating thread: NVIDIA's key doesn't hold the scope (selftest nvext), and a
// device-wide slot would reach the other workers' creates.
static int nv_slot(uint32_t slot, uint32_t space) { return g_nv_set_thread ? g_nv_set_thread(g_warm_dev, slot, space) : 0; }
static thread_local bool t_nv_stale;  // left set on this thread by a failed reset: every create here fails until a reset succeeds

// The pipeline creation flags are global (nvapi.h), so state objects created at once must share them: a create waits
// until the ones under other flags are done.
static std::mutex g_opts_mx;
static std::condition_variable g_opts_cv;
static int64_t g_opts_now;  // -1: unknown (a set failed)
static int g_opts_users;

// why: set when nothing may be created. A create without its own state would fill another cache key.
// so: a state object, the only creates the flags apply to (nvapi.h); one without an 'N' is created under none.
struct NvScope {
    const char* why = nullptr;
    bool slot = false, opts = false;
    explicit NvScope(const NvExt* x, bool so = false) {
        if (t_nv_stale && (t_nv_stale = nv_slot(~0u, 0) != 0)) {
            why = "an NVAPI state reset failed on its thread";
            return;
        }
        if (x && x->slot != ~0u && g_nv_set_thread && (slot = true, nv_slot(x->slot, x->space))) why = "its NVAPI state could not be set";
        else if (so && g_nv_set_opts && g_warm_dev5) {
            uint32_t want = x ? x->opts : 0;
            std::unique_lock l(g_opts_mx);
            g_opts_cv.wait(l, [&] { return !g_opts_users || g_opts_now == want; });
            if (g_opts_now != want) {
                uint32_t o[2] = {8 | 1 << 16, want};
                g_opts_now = g_nv_set_opts(g_warm_dev5, o) ? -1 : want;
            }
            if (g_opts_now != want) why = "its NVAPI state could not be set";
            else ++g_opts_users, opts = true;
        }
    }
    ~NvScope() {
        if (opts) {
            std::lock_guard l(g_opts_mx);
            if (!--g_opts_users) g_opts_cv.notify_all();
        }
        if (slot && nv_slot(~0u, 0)) {
            t_nv_stale = true;
            static std::atomic<bool> logged;
            if (!logged.exchange(true)) logf("warm: an NVAPI state reset failed: that thread creates nothing more until one succeeds");
        }
    }
};

static void nv_warm_init() {
    if (g_nvext.empty()) return;
    HMODULE m = LoadLibraryW(L"nvapi64.dll");
    auto qi = m ? (void* (*)(uint32_t))GetProcAddress(m, "nvapi_QueryInterface") : nullptr;
    auto init = qi ? (int (*)())qi(0x0150E828) : nullptr;  // NvAPI_Initialize
    if (init && !init()) g_nv_set_thread = (decltype(g_nv_set_thread))qi(0x43D867C0), g_nv_set_opts = (decltype(g_nv_set_opts))qi(0x5C607A27);
    logf("warm: %zu records carry NVAPI state%s", g_nvext.size(), g_nv_set_thread ? "" : "; NVAPI unavailable: they replay without it");
    // Proton's dxvk-nvapi answers NVAPI_NO_IMPLEMENTATION (-3) unless DXVK_NVAPI_D3D12_NV_SHADER_EXTN=1, so the game's own
    // creates carry no slot there. Probed with a reset, which changes nothing.
    if (g_nv_set_thread && g_warm_dev && g_nv_set_thread(g_warm_dev, ~0u, 0) == -3)
        g_nv_set_thread = nullptr, logf("warm: NVAPI doesn't implement the shader-extension slot here: they replay without it");
}
// SCSKILLER_WARM_ROUNDTRIP=1 (development): each recorded 'G' / 'C' / 'S' desc the warm decodes is serialized again and
// compared with its record (the log's "round trip" line); =only: compare without creating anything (fast, any adapter).
static int g_roundtrip;
static std::atomic<uint64_t> g_roundtrip_ok, g_roundtrip_bad;

static std::atomic<int8_t> g_blendable[256];  // by DXGI format: 0 not asked yet, 1 blendable, -1 not
static bool blendable(DXGI_FORMAT f) {
    if ((unsigned)f >= 256 || !g_warm_dev) return true;
    int8_t v = g_blendable[f];
    if (!v) {
        D3D12_FEATURE_DATA_FORMAT_SUPPORT s{f};
        v = FAILED(g_warm_dev->CheckFeatureSupport(D3D12_FEATURE_FORMAT_SUPPORT, &s, sizeof s)) || (s.Support1 & D3D12_FORMAT_SUPPORT1_BLENDABLE) ? 1 : -1;
        g_blendable[f] = v;
    }
    return v > 0;
}
static bool blends_unblendable(const Reader& r) {
    for (UINT i = 0; i < std::min(r.nrt, 8u); ++i)
        if (r.blend.RenderTarget[r.blend.IndependentBlendEnable ? i : 0].BlendEnable && r.rtv[i] != DXGI_FORMAT_UNKNOWN && !blendable(r.rtv[i])) return true;
    return false;
}

// Decode a record (with an optional plan override) into its desc; create the PSO when pso is given.
static HRESULT build(const Rec& rec, Reader& r, ID3D12PipelineState** pso, const NvExt* nv = nullptr) {
    // what a layer made goes to the device under the layer in the warm too: through it, it would be changed again
    struct Real {
        Real(bool on) { t_real = on; }
        ~Real() { t_real = false; }
    } real(g_warm_real && !r.ovr && g_layer_made.count(key_of(rec.tag, rec.payload)));
    std::vector<uint64_t> buf;
    D3D12_GRAPHICS_PIPELINE_STATE_DESC g{};
    D3D12_COMPUTE_PIPELINE_STATE_DESC c{};
    D3D12_PIPELINE_STATE_STREAM_DESC sd{};
    if (rec.tag == 'G') io_gfx(r, g);
    else if (rec.tag == 'C') io_cs(r, c);
    else sd = read_stream(r, buf);
    if (!pso || !r.ok) return E_INVALIDARG;
    if (g_roundtrip && !r.ovr) {  // the desc handed to the driver serializes back to the record: nothing was dropped
        Writer w{rec.tag};
        if (rec.tag == 'G') io_gfx(w, g);
        else if (rec.tag == 'C') io_cs(w, c);
        else write_stream(w, sd);
        bool same = w.ok && w.s == rec.payload;
        (same ? g_roundtrip_ok : g_roundtrip_bad)++;
        if (!same && g_roundtrip_bad <= 5) logf("warm: round trip differs for a '%c' record (%zu -> %zu bytes%s%s)", rec.tag, rec.payload.size(), w.s.size(), w.ok ? "" : ", ", w.why);
        if (g_roundtrip == 2) return same ? S_OK : E_FAIL;  // "only": compare, create nothing
    }
    r.unblendable = rec.tag != 'C' && blends_unblendable(r);
    NvScope scope(nv);
    if (scope.why) return r.fail(scope.why), E_INVALIDARG;
    if (t_real) {
        if (rec.tag == 'G') return g_warm_real->CreateGraphicsPipelineState(&g, IID_PPV_ARGS(pso));
        if (rec.tag == 'C') return g_warm_real->CreateComputePipelineState(&c, IID_PPV_ARGS(pso));
        return g_warm_real2 ? g_warm_real2->CreatePipelineState(&sd, IID_PPV_ARGS(pso)) : E_NOINTERFACE;
    }
    if (rec.tag == 'G') return o_gfx(g_warm_dev, &g, IID_PPV_ARGS(pso));
    if (rec.tag == 'C') return o_cs(g_warm_dev, &c, IID_PPV_ARGS(pso));
    return g_warm_dev2 ? o_stream(g_warm_dev2, &sd, IID_PPV_ARGS(pso)) : E_NOINTERFACE;
}

static Hash rec_tuple(const Rec& rec, const Override* ovr = nullptr) {
    Reader r{rec.payload};
    r.resolve = false, r.ovr = ovr;
    build(rec, r, nullptr);
    return tuple_hash(r.rsh, r.tup);
}

// Plan item -> its template record (nullptr if absent) with the override filled in; layout storage lives in r.
static const Rec* parse_plan(Reader& r, Override& o) {
    Hash tmpl = r.hash();
    o.rs = r.hash();
    for (uint32_t i = 0, n = r.count(); i < n && r.ok; ++i) {
        uint32_t st = r.u32();
        Hash h = r.hash();
        if (st < 32) o.sh[st] = h;
    }
    uint32_t keep = 0xFFFFFFFF;
    if (r.pos + 4 <= r.s.size() && memcmp(r.s.data() + r.pos, &keep, 4)) r.il(o.il), o.has_il = true;
    auto it = g_rec_idx.find(tmpl);
    return r.ok && it != g_rec_idx.end() ? &g_recs[it->second] : nullptr;
}

// Shader bytes are not read into memory: the dbs are mapped read-only and each blob is a view into its file (Jedi Survivor:
// ~2 GB of shaders). Mapped pages are the OS's to drop and reload; they don't count in the process's private bytes, which
// the memory budget watches (SCSKiller_WarmMemory). The views stay valid until the process exits.
static void map_blobs(const std::wstring& path, const std::vector<std::pair<Hash, std::pair<long long, uint32_t>>>& at) {
    if (at.empty()) return;
    HANDLE f = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
    HANDLE m = f == INVALID_HANDLE_VALUE ? nullptr : CreateFileMappingW(f, nullptr, PAGE_READONLY, 0, 0, nullptr);
    auto base = m ? (const char*)MapViewOfFile(m, FILE_MAP_READ, 0, 0, 0) : nullptr;
    if (f != INVALID_HANDLE_VALUE) CloseHandle(f);  // the mapping keeps the file
    if (!base) {
        const DWORD e = GetLastError();
        return logf("db: mapping %s failed (error %lu): its shader blobs are missing", shown(path).c_str(), e);
    }
    for (auto& [h, o] : at) g_blob_bytes[h] = std::string_view(base + o.first, o.second);
}

static void load_file(const std::wstring& path, bool main, bool with_bytes) {
    FILE* f = _wfopen(path.c_str(), L"rb");
    if (!f) return;
    long long good = 0, flen = _filelengthi64(_fileno(f));
    std::vector<std::pair<Hash, std::pair<long long, uint32_t>>> blobs;  // with_bytes: each blob's offset and size
    for (;;) {
        int tag = fgetc(f);
        uint32_t len;
        if (tag == EOF || !tag || fread(&len, 4, 1, f) != 1) break;  // no record has tag 0: a zero-filled tail after a power loss
        if (tag == 'B') {
            Hash h;
            if (len < 20 || fread(h.data(), 1, 20, f) != 20) break;
            long long at = _ftelli64(f);
            if (_fseeki64(f, len - 20, SEEK_CUR) || _ftelli64(f) > flen) break;
            if (with_bytes) blobs.push_back({h, {at, len - 20}});
            if (main) g_blobs_on_disk.insert(h);  // gen blobs aren't in the main db; its records must stay self-contained
        } else {
            if (len > flen - good - 5) break;  // a torn or corrupt length: never allocate past the file
            std::string p(len, '\0');
            if (fread(p.data(), 1, len, f) != len) break;
            if (tag == 'N') {
                if (len == 36) {
                    Hash t;
                    NvExt x;
                    memcpy(t.data(), p.data(), 20), memcpy(&x, p.data() + 20, 16);
                    g_nvext[t] = x;  // ponytail: one state per record; a PSO a game also creates without it replays with it only
                }
                if (main) g_keys.insert(key_of('N', p));
            } else if (tag == 'W') {  // nothing to replay itself
                if (main) g_keys.insert(key_of('W', p));
                if (with_bytes && len == 40) g_layer_made.insert(*(const Hash*)p.data());
            } else if (tag == '1' || tag == '2' || tag == '3') {  // D3D11 items: nothing to record against, only warm needs them
                if (with_bytes) g_items11.push_back((char)tag + p);
            } else if (tag == 'D') {  // D3D11 draws: the warm gets them from the plan ('3')
                if (main) g_keys.insert(key_of('D', p));
            } else if (tag == 'P') {  // templates (main db, or gen records written before the items) are known here
                Reader r{p};
                Override o;
                if (const Rec* t = parse_plan(r, o)) g_known_tuples.insert(rec_tuple(*t, &o));
                if (with_bytes) g_plan.push_back(std::move(p));
            } else {  // gen templates (synthesized, recording-free) replay like recorded PSOs but aren't main db keys
                Hash k = key_of((char)tag, p);
                if (main) g_keys.insert(k);
                if (g_rec_idx.try_emplace(k, g_recs.size()).second) {
                    g_known.insert(k);
                    g_recs.push_back({(char)tag, std::move(p)});
                    if (tag != 'R' && tag != 'A') g_known_tuples.insert(rec_tuple(g_recs.back()));  // state objects have no tuple
                }
            }
        }
        good = _ftelli64(f);
    }
    long long size = (_fseeki64(f, 0, SEEK_END), _ftelli64(f));
    fclose(f);
    // torn tail from a crash mid-write: cut it so appends stay parseable; only while this process holds the db (load_db)
    if (main && size > good && g_db && !_chsize_s(_fileno(g_db), good)) logf("db: truncated a torn tail (%lld -> %lld bytes)", size, good);
    while (!blobs.empty() && blobs.back().second.first + blobs.back().second.second > good) blobs.pop_back();  // in the torn tail
    map_blobs(path, blobs);  // after the truncation: a mapped file can't be shortened
}

// scskiller.keys (record mode, written by the app next to scskiller.ini): "SCSKKEY1", then the 20-byte record keys and
// blob hashes the app already imported, and the hashes of the shaders the game's files ship. The app empties scskiller.db
// after an import and reads a shipped shader back from the install, so a record naming these goes in without their bytes.
// Kept as the file's own array, sorted: a big game ships 300,000 shaders (6 MB here, 70 MB as hash set entries).
static std::vector<Hash> g_imported;
static bool imported(const Hash& h) { return std::binary_search(g_imported.begin(), g_imported.end(), h); }
static bool fresh(HashSet& s, const Hash& h) { return !imported(h) && s.insert(h).second; }
// scskiller.warmed (record mode, written by the app after a complete compile): "SCSKWRM1", u32 n, then n sorted 20-byte
// keys of the pipelines and state objects the compile replayed, then sorted (stages + root sig) tuple hashes
// (tuple_hash) of every pipeline it compiled, planner-made ones included. With it, the csv's known / tuple_known and the
// log's counters mean "the last compile created this", not "this PC recorded it".
static std::vector<Hash> g_warmed, g_warmed_tuples;
static bool g_has_warmed;
static void load_warmed(const std::wstring& path) {
    FILE* f = _wfopen(path.c_str(), L"rb");
    if (!f) return;
    char magic[8];
    uint32_t n = 0;
    long long len = _filelengthi64(_fileno(f));
    if (len >= 12 && fread(magic, 1, 8, f) == 8 && !memcmp(magic, "SCSKWRM1", 8) && fread(&n, 4, 1, f) == 1 && 12 + 20ll * n <= len) {
        g_warmed.resize(n), g_warmed_tuples.resize((size_t)(len - 12 - 20ll * n) / 20);
        g_has_warmed = fread(g_warmed.data(), 20, n, f) == n && fread(g_warmed_tuples.data(), 20, g_warmed_tuples.size(), f) == g_warmed_tuples.size();
        std::sort(g_warmed.begin(), g_warmed.end()), std::sort(g_warmed_tuples.begin(), g_warmed_tuples.end());
    }
    fclose(f);
    if (g_has_warmed) logf("db: the last compile created %zu pipelines and %zu shader tuples (scskiller.warmed): known means compiled", g_warmed.size(), g_warmed_tuples.size());
    else g_warmed.clear(), g_warmed_tuples.clear();
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

static void load_db(bool with_bytes) {
    // deny-write, before the load: what is read and repaired can't change under it, and a second process of the folder
    // (another instance, a launcher) records and repairs nothing rather than interleaving with or cutting this one's records
    g_db = _wfsopen((g_dir + L"scskiller.db").c_str(), L"ab", _SH_DENYWR);
    if (!g_db) logf("db: scskiller.db is open in another process: nothing is recorded in this one");
    load_file(g_dir + L"scskiller.db", true, with_bytes);
    if (size_t n = with_bytes ? 0 : load_keys(g_dir + L"scskiller.keys")) logf("db: %zu keys of records and blobs imported earlier or shipped with the game", n);
    if (!with_bytes) load_warmed(g_dir + L"scskiller.warmed");
    g_db_records_at_start = g_keys.size();
    size_t rec_tuples = g_known_tuples.size();
    load_file(g_dir + L"scskiller_gen.db", false, with_bytes);
    g_total = g_recs.size() + g_plan.size() + g_items11.size();
    WIN32_FILE_ATTRIBUTE_DATA fa;
    if (GetFileAttributesExW((g_dir + L"scskiller.db").c_str(), GetFileExInfoStandard, &fa)) g_db_bytes = (uint64_t)fa.nFileSizeHigh << 32 | fa.nFileSizeLow;
    wchar_t cap[32];  // not cfg(): a staged warm child may inherit SCSKILLER_* variables
    GetPrivateProfileStringW(L"scskiller", L"max_db_bytes", L"", cap, 32, (g_dir + L"scskiller.ini").c_str());
    g_db_capped = *cap, g_db_cap = _wcstoui64(cap, nullptr, 10);
    if (g_db_capped) logf("db: %llu bytes, limit %llu", g_db_bytes, g_db_cap);
    logf("db: %llu PSOs / %zu shader tuples recorded, +%zu tuples generated, %zu blobs (mode=%s)", g_db_records_at_start,
         rec_tuples, g_known_tuples.size() - rec_tuples, g_blobs_on_disk.size(), g_warm ? "warm" : "record");
}

// A failed write (a full disk) may leave part of a record: nothing more is appended, so it stays the tail the next launch cuts.
static void db_check() {
    if (!g_dbo.failed) return;
    logf("db: writing scskiller.db failed (disk full?): nothing more is recorded in this launch");
    g_db = nullptr;  // not closed: DllMain may still read the stream's handle
}

// Writes the db and the csv out every 100 ms: a write per create under g_mx held a game's startup burst to about
// 10,000 creates/s. A crash or a kill loses at most these 100 ms; at a normal exit DllMain writes the rest.
static void flusher() {
    for (;;) try {
        Sleep(100);
        std::lock_guard l(g_mx);
        out_write(g_csvo);
        if (g_db) out_write(g_dbo), db_check();
    } catch (...) {}  // logf's allocation: an exception leaving a thread ends the game
}

static HashSet g_session_tuples;  // under g_mx: the (stages + root sig) tuples of the records this launch wrote

// Past 3/4 of the limit: a record the last compile created, or a PSO whose tuple the compile, the db at launch or this
// launch already has, isn't new to the plan. A dense game fills the limit in minutes with those, and the new shaders the
// rest of the session brings would be lost. D3D11 layouts and state objects go by their exact key only.
// t: the PSO's tuple, null for other records or when it couldn't be hashed.
static bool novel(const Hash& k, const Hash* t) {
    if (g_has_warmed && std::binary_search(g_warmed.begin(), g_warmed.end(), k)) return false;
    return !t || !(g_has_warmed && std::binary_search(g_warmed_tuples.begin(), g_warmed_tuples.end(), *t)) && !g_known_tuples.count(*t) && !g_session_tuples.count(*t);
}

// Under g_mx: the record, the blobs it names and its NVAPI state, each written once. At the limit none of them is: a
// record is never split, and the csv row is still written (note).
static void store(const Writer& w, const Hash& k) {
    if (g_db_capped && g_db_bytes >= g_db_cap) {
        if (!g_db_full) g_db_full = true, logf("db: recording limit reached (%llu of %llu bytes): new pipelines are not recorded", g_db_bytes, g_db_cap);
        return;
    }
    // allocations here never reach the game: a tuple not hashed or not kept only makes a later create look new
    Hash t{};
    bool tuple = false;
    if (g_db_capped && g_db && (w.tag == 'G' || w.tag == 'C' || w.tag == 'S') && !g_keys.count(k) && !imported(k))
        try { t = tuple_hash(w.rsh, w.tup), tuple = true; } catch (...) {}
    // ponytail: a fixed 3/4 kept for new shaders, a share from measured sessions if dense games still lose some
    if (g_db_capped && g_db_bytes >= g_db_cap / 4 * 3 && g_db) {
        if (!g_db_selective) {
            g_db_selective = true;
            try { logf("db: 3/4 of the recording limit (%llu of %llu bytes): only pipelines with a shader tuple not recorded or compiled yet are recorded", g_db_bytes, g_db_cap); } catch (...) {}
        }
        if (!g_keys.count(k) && !imported(k) && !novel(k, tuple ? &t : nullptr)) {
            ++g_db_skipped;
            return;
        }
    }
    if (fresh(g_keys, k) && g_db) {
        for (auto& [h, b] : w.blobs)
            if (fresh(g_blobs_on_disk, h)) put('B', h.data(), 20, b.data(), b.size());
        for (auto& h : w.rsigs)
            if (auto it = g_rs_bytes.find(h); it != g_rs_bytes.end()) {
                if (fresh(g_blobs_on_disk, h)) put('B', h.data(), 20, it->second.data(), it->second.size());
                g_rs_bytes.erase(it);  // written now or already kept: only what may still be written stays in memory
            }
        put(w.tag, w.s.data(), w.s.size());
        db_check();
        if (tuple) try { g_session_tuples.insert(t); } catch (...) {}
    }
    if (w.nv.slot != ~0u || w.nv.opts || w.nv.scope) {
        std::string n(36, '\0');
        memcpy(n.data(), k.data(), 20), memcpy(n.data() + 20, &w.nv, 16);
        if (fresh(g_keys, key_of('N', n)) && g_db) put('N', n.data(), n.size()), db_check();
    }
}

// known = exact PSO was in the db at launch; tknown = its (stages + root sig) tuple was, recorded or generated.
// On NVIDIA tknown is what predicts a driver cache hit.
// lib: the game got this PSO from its own ID3D12PipelineLibrary (UE4's driver-optimized PSO disk cache), not a create.
// pre_ms: the hook's time before the create call (serializing and hashing the desc); the csv's proxy_ms adds this call's.
static void note(Writer& w, double ms, bool lib, double pre_ms) {
    auto n0 = std::chrono::steady_clock::now();
    double t_ret = now_ms();  // the csv's t_ms: when the create returned, not when the lock and the db write let the row out
    // outside the lock: these sets and lists are only written by load_db, before the hooks
    Hash k{};
    bool so = w.tag == 'R' || w.tag == 'A', known = false, tknown = false;
    if (w.ok) {
        k = key_of(w.tag, w.s);
        if (g_has_warmed) {
            known = std::binary_search(g_warmed.begin(), g_warmed.end(), k);
            tknown = known || (!so && std::binary_search(g_warmed_tuples.begin(), g_warmed_tuples.end(), tuple_hash(w.rsh, w.tup)));
        } else known = g_known.count(k) || imported(k), tknown = known || (!so && g_known_tuples.count(tuple_hash(w.rsh, w.tup)));
    }
    std::lock_guard l(g_mx);
    ++g_creates, g_lib_loads += lib;
    if (g_creates == 1) logf("first PSO seen (%s)", lib ? "pipeline library load" : "create");
    if (!w.ok) {
        if (++g_unsupported <= 10) logf("PSO not recorded ('%c', %s): %s", w.tag, lib ? "library" : "create", w.why);
        return;
    }
    // the same desc created again in this process: a driver that keeps its cache serves it at hit cost
    bool repeat = !g_created.insert(k).second;
    store(w, k);
    // SessionLog.IsCompile: on NVIDIA a PSO that traces rays inline has a driver floor of up to 60 ms, even cached
    bool compile = !lib && (so ? ms >= 25 : ms > 3) &&
                   !(!so && ms <= 60 && g_ov_nvidia.load(std::memory_order_relaxed) &&
                     std::any_of(w.blobs.begin(), w.blobs.end(), [](auto& b) { return ov::inline_rt(b.second.data(), b.second.size()); }));
    g_ovcreates.add(t_ret, ms, GetCurrentThreadId(),  // FrameLog.Blocks: a state object, or 10 ms
                    (compile ? ov::Creates::kCompile : 0) | (so || ms >= 10 ? ov::Creates::kBlocks : 0) | (t_presenting ? ov::Creates::kPresents : 0));
    if (w.nv.slot != ~0u || w.nv.opts) {
        if (++g_nv_creates <= 5)
            logf("nvapi: a '%c' created with extension slot u%d space %u (%s), pipeline creation flags 0x%x", w.tag, (int)w.nv.slot, w.nv.space,
                 (w.nv.scope & 0xFF) == 1 ? "device" : (w.nv.scope & 0xFF) == 2 ? "thread" : w.nv.scope == 3 ? "PSO extension" : "none", w.nv.opts);
    }
    const double slow = 3.0;  // ms; a PSO cache hit is well under this, a compile well over (a cached state object isn't: SessionLog.StateObjectCompileMs)
    g_known_hits += known, g_tuple_hits += tknown;
    (known ? g_slow_known : tknown ? g_slow_tuple : g_slow_unknown) += ms > slow;
    g_repeats += repeat, g_slow_repeats += repeat && ms > slow;
    if (g_csv) {  // key: the PSO key (sha1 of tag + payload, as the db's record); proxy_ms: the hook's own time, outside ms
        double own = pre_ms + std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - n0).count();
        csv_line("%.1f,%c,%d,%d,%.3f,%s,%.3f,%lu,%d\n", t_ret, lib ? w.tag | 0x20 : w.tag, (int)known, (int)tknown, ms, hex(k).data(), own,
                GetCurrentThreadId(), (int)t_presenting);
    }
    if (g_creates % 500 == 0) {
        logf("creates=%llu (library loads %llu) known=%llu tuple_known=%llu | slow: known=%llu tuple_only=%llu unknown=%llu | repeats=%llu slow=%llu | unsupported=%llu failed=%llu db=%zu nvapi_state=%llu dedupe=%llu held=%llu slow: dedupe=%llu held=%llu db_skipped=%llu",
             g_creates, g_lib_loads, g_known_hits, g_tuple_hits, g_slow_known, g_slow_tuple, g_slow_unknown, g_repeats, g_slow_repeats, g_unsupported, g_create_fails.load(), g_keys.size(),
             g_nv_creates, g_dd_hits.load(), g_dd_held.load(), g_dd_hits_slow.load(), g_dd_held_slow.load(), g_db_skipped);
    }
}

// Warm: replay the whole db on the game's own device, in the game's own process.
static std::mutex g_rsmx;
static std::unordered_map<Hash, ID3D12RootSignature*, HashH> g_warm_rs, g_warm_rs_real;

static __declspec(noinline) HRESULT rs_call(std::string_view b, ID3D12RootSignature** out) {
    __try {
        if (t_real) return g_warm_real->CreateRootSignature(0, b.data(), b.size(), IID_PPV_ARGS(out));
        return o_rs(g_warm_dev, 0, b.data(), b.size(), IID_PPV_ARGS(out));
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return E_UNEXPECTED;
    }
}

// Created outside the lock: a driver call that faults or hangs while holding it would stop every worker (a fault
// unwound by SEH never releases a std::mutex). Two workers may create the same one at once; the loser's is released.
static ID3D12RootSignature* warm_rootsig(const Hash& h) {
    auto& made = t_real ? g_warm_rs_real : g_warm_rs;
    {
        std::lock_guard l(g_rsmx);
        if (auto it = made.find(h); it != made.end()) return it->second;
    }
    ID3D12RootSignature* rs = nullptr;
    auto b = g_blob_bytes.find(h);  // read-only during the warm
    if (b != g_blob_bytes.end()) {
        HRESULT hr = rs_call(b->second, &rs);
        if (FAILED(hr)) rs = nullptr, logf("warm: root signature %02x%02x%02x%02x (%zu bytes) creation failed hr=0x%08x", h[0], h[1], h[2], h[3],
                                           b->second.size(), (unsigned)hr);
    }
    std::lock_guard l(g_rsmx);
    auto [it, fresh] = made.try_emplace(h, rs);
    if (!fresh && rs) rs->Release();
    else if (fresh && rs && g_roundtrip) { std::lock_guard l2(g_mx); g_rs_of[rs] = h; }  // a Writer names it by its blob
    return it->second;
}

// SCSKILLER_D3D12_DEBUG=1 (development): the D3D12 debug layer (Windows' optional "Graphics Tools" feature) is enabled
// before the device is created, and its messages are logged: after each failed item and at the end of the warm.
static bool g_debug12;
static ID3D12InfoQueue* g_iq;
static std::mutex g_iqmx;

static void drain_debug(const char* when) {
    if (!g_iq) return;
    std::lock_guard l(g_iqmx);
    static int logged;
    for (UINT64 i = 0, n = g_iq->GetNumStoredMessages(); i < n; ++i) {
        SIZE_T len = 0;
        if (FAILED(g_iq->GetMessage(i, nullptr, &len)) || !len) continue;
        std::vector<uint64_t> buf((len + 7) / 8);
        auto m = (D3D12_MESSAGE*)buf.data();
        if (SUCCEEDED(g_iq->GetMessage(i, m, &len)) && ++logged <= 200)
            logf("d3d12 debug (%s): severity %d id %d: %.*s", when, (int)m->Severity, (int)m->ID, (int)m->DescriptionByteLength, m->pDescription);
    }
    g_iq->ClearStoredMessages();
}

// Every failed item is counted by what failed (record kind + HRESULT or decode reason), logged at the end of the warm;
// the first 50 are also logged one by one with their item number.
static std::mutex g_failmx;
static std::map<std::string, uint64_t> g_fail_tally;

static void note_fail(size_t j, const std::string& what) {
    uint64_t n;
    {
        std::lock_guard l(g_failmx);
        n = ++g_fail_tally[what];
        static uint64_t logged;
        if (++logged > 50) n = 0;
    }
    if (n) logf("warm: item %zu failed: %s", j, what.c_str());
    char when[32];
    sprintf_s(when, "item %zu", j);
    drain_debug(when);
}

// State objects: each 'R' / 'A' is created once (call_once), by the worker that takes it or earlier by a worker whose
// record builds on it (so file order, parallel workers and --start all work); the ones a later record builds on are kept
// alive until the warm ends. Their failure fails what builds on them, their skip skips it.
struct SoSlot {
    std::once_flag once;
    HRESULT hr = E_FAIL;
    std::string why;  // decode / dependency failure; empty = the driver's hr
    bool skipped = false;  // it, or one it builds on, crashed or hung the driver: skipped, not failed
    bool known = false;    // ...in an earlier compile on this driver (--known-keys)
    ID3D12StateObject* obj = nullptr;
    std::atomic<bool> building{false}, done{false};  // done is set before building clears, both under g_defer_mx
};
static std::vector<std::unique_ptr<SoSlot>> g_so;  // by g_recs index, 'R' / 'A' only
static std::vector<std::vector<size_t>> g_so_deps;  // by g_recs index: the state objects an 'R' / 'A' builds on

// A chain of additions (each 'A' builds on the one before) can only be created one link at a time. A worker whose item
// builds on a state object another worker is creating doesn't wait for it: the item waits here, the worker takes another
// one, and the item goes back to the workers once that state object is done.
static std::mutex g_defer_mx;
static std::unordered_map<size_t, std::vector<size_t>> g_waiting;  // a state object being created -> the items deferred on it
static std::deque<size_t> g_ready;                                  // deferred items whose state object is done
static std::atomic<size_t> g_deferred;                              // deferred and not taken again

// A worker's own work creating a state object (decoding, root signatures, the driver call): when it began and how long it
// may take, since 0 = none. Paused while it waits on a state object another worker creates or for a ray tracing thread: a
// wait is no hang. One atomic pair, so the supervisor never reads one segment's start with another's limit.
struct SoClock {
    struct Seg {
        double since, limit;
    };
    std::atomic<Seg> seg{Seg{0, 0}};
    std::atomic<size_t> creating{SIZE_MAX};  // the state object so_create is on: the item's own, or one it builds on
};
static thread_local SoClock* t_so_clock;
static double g_stuck_ms = 60000;
static void so_time(double limit) {
    if (t_so_clock) t_so_clock->seg = {live_ms(), limit};
}
static void so_untime() {
    if (t_so_clock) t_so_clock->seg = {0, 0};
}
// A create links every state object it builds on. NVIDIA compiled Jedi Survivor's recorded collections in 42.7 ms each,
// so 250 ms per one named (about 6x) on top of the hang limit: a 400-collection addition gets 100 s more.
static constexpr double kSoLinkMs = 250;
static std::vector<char> g_so_keep;

// After a fault or a hang in a state object call this process's driver can't be trusted (the NVIDIA case at g_parked). The
// process is "poisoned": state objects taken after that are left unfinished without calling the driver, PSOs go on, and the
// run ends with a retry (SCSKiller_Retry): a new process replays from the first unfinished item. The state object that
// faulted or hung, and what builds on it, are skipped for the rest of this compile (SCSKiller_WarmRt), and the retry keeps
// the ray tracing threads; only an item that can't be named lowers them (a quarter: 32 -> 8 -> 2 -> 1).
static std::atomic<bool> g_rt_off;                 // poisoned
static int g_rt_threads;                           // SCSKiller_WarmRt: concurrent state object creates, 0 = no limit
static std::unordered_set<size_t> g_skip;          // --skip: counted skipped, never replayed
static std::atomic<size_t> g_rt_fatal = SIZE_MAX;  // the state object whose fault / hang poisoned this run; the others are retried
// An item that crashed or hung this driver in an earlier compile (--known-keys, the app keeps them per game and driver) is
// skipped like --skip, it and what builds on it counted apart (SCSKiller_Known). This run's own such items, never what
// builds on them, go back to the app as keys.
static HashSet g_known_keys;
static std::unordered_set<size_t> g_known_items;   // this run's items with one of those keys
static std::atomic<uint64_t> g_warm_known;   // of g_warm_skipped: g_known_items and what builds on them
static uint64_t g_retry_known;               // ...those among the items before the retry's first
static std::mutex g_culprit_mx;
static std::vector<size_t> g_culprits;       // this run's items skipped for hanging or faulting the driver themselves
static std::string g_culprit_json;
static void culprit(size_t j) {
    std::lock_guard l(g_culprit_mx);
    g_culprits.push_back(j);
}
static constexpr size_t kNoBlame = SIZE_MAX - 1;  // rt_stop: the hang can't be pinned on one state object, none is remembered
static void rt_stop(const char* why, size_t j, size_t blame = SIZE_MAX) {
    bool was = false;  // only the poisoning call names the culprit: one after an unknown poison may be its victim
    if (!g_rt_off.compare_exchange_strong(was, true)) return;
    if (j != SIZE_MAX && blame != kNoBlame) culprit(blame != SIZE_MAX ? blame : j);
    if (j != SIZE_MAX) g_rt_fatal = j, logf("warm: item %zu: %s: it and what builds on it are skipped for this compile", j, why);
    logf("warm: item %zu: %s: this process's driver can't be trusted for ray tracing any more; the state objects left run in a new process%s", j, why, j == SIZE_MAX ? " with fewer threads" : "");
}

// A removed device (DXGI_ERROR_DEVICE_REMOVED; AMD: two compute PSOs a game never used) fails every later create of the
// process. The replay stops and the run ends with a retry: the new process creates the items that were in flight alone
// before its workers start (SCSKiller_WarmCrash), and the one that removes the device alone is skipped by its key from then
// on (--skip-keys; the app keeps them per game). One item in flight is blamed at once.
static std::atomic<bool> g_removed;
static HashSet g_crash_keys;                    // keys of items that removed the device in an earlier run
static std::unordered_set<size_t> g_crash;      // this run's items with one of those keys: never replayed
static std::vector<size_t> g_alone;             // in flight when an earlier process's device was removed
static std::vector<size_t> g_blamed, g_alone_next;
static std::string g_crash_json, g_alone_json;  // SCSKiller_Crashes
static size_t g_remove_item = SIZE_MAX;         // SCSKILLER_WARM_REMOVE=<item>[-<last>][:shared] (development): its create removes the device first (a range with SCSK_TEST_HOOKS)
static size_t g_remove_last = SIZE_MAX;         // ...and each one up to last's
static size_t g_remove_step = 1;                // ...every step-th of them (SCSKILLER_WARM_REMOVE=<item>-<last>/<step>)
static bool g_remove_shared;                    // ...only when a worker creates it, never alone (a crash no single item makes)
static thread_local bool t_alone;
// An exception replaying a PSO ends the process as a removal does (g_removed): unwinding a fault out of the driver releases
// none of its locks or half-built state (the NVIDIA teardown at g_parked), so nothing after it can be trusted.
static std::atomic<DWORD> g_fault_code;
static std::atomic<size_t> g_faulted{SIZE_MAX};  // its item: read once, where the retry is planned; a later fault changes no outcome
// Windows bugchecks after TdrLimitCount (5) GPU resets within TdrLimitTime (60 s): while 3 device losses fall in the last 60 s,
// no new work starts. A D3D12 loss ends its process, so the times (GetTickCount64, the same in every process) also go to a file
// every warm process shares; this process's own are kept in memory whatever the file does.
static std::mutex g_loss_mx;
static std::vector<ULONGLONG> g_loss_mem;
static size_t losses(bool add) {  // the losses in the last 60 s, after adding one now
    std::lock_guard l(g_loss_mx);
    if (add) g_loss_mem.push_back(GetTickCount64());
    wchar_t tmp[MAX_PATH + 1];
    const DWORD n = GetTempPathW(MAX_PATH + 1, tmp);
    HANDLE f = INVALID_HANDLE_VALUE;
    for (int i = 0; n && n <= MAX_PATH && i < 50; ++i, Sleep(10))  // another process has it for a moment
        if ((f = CreateFileW((std::wstring(tmp, n) + L"scskiller_device_losses.bin").c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_ALWAYS, 0,
                             nullptr)) != INVALID_HANDLE_VALUE || GetLastError() != ERROR_SHARING_VIOLATION) break;
    const ULONGLONG now = GetTickCount64();  // after the file is ours: no other process adds a later one meanwhile
    auto old = [&](ULONGLONG x) { return x > now || now - x >= 60000; };  // later than now: from before a reboot
    std::erase_if(g_loss_mem, old);
    std::vector<ULONGLONG> t(64);
    DWORD got = 0;
    const bool read = f != INVALID_HANDLE_VALUE && ReadFile(f, t.data(), DWORD(t.size() * 8), &got, nullptr);
    t.resize(read ? got / 8 : 0);
    std::erase_if(t, old);
    for (ULONGLONG x : g_loss_mem)
        if (std::find(t.begin(), t.end(), x) == t.end()) t.push_back(x);
    if (read && add && t.size() <= 64 && SetFilePointer(f, 0, nullptr, FILE_BEGIN) == 0 && WriteFile(f, t.data(), DWORD(t.size() * 8), &got, nullptr) &&
        got == t.size() * 8)
        SetEndOfFile(f);
    if (f != INVALID_HANDLE_VALUE) CloseHandle(f);
    return t.size();
}
static void lost() { losses(true); }
// false: stopped while waiting. Callers wait with no call in flight, so no supervisor takes the wait for a hang.
static bool pace_losses() {
    for (bool said = false;; Sleep(250)) {
        const size_t n = losses(false);
        if (n < 3) return true;
        if (g_state == STOP) return false;
        if (!said) said = true, logf("warm: %zu device losses in the last 60 s: no new work until one is older", n);
    }
}

static bool removed() {
    if (g_removed) return true;
    HRESULT why = g_warm_dev ? g_warm_dev->GetDeviceRemovedReason() : S_OK;
    if (why == S_OK) return false;
    if (!g_removed.exchange(true)) logf("warm: the device was removed (reason 0x%08x): the replay stops, a new process goes on", (unsigned)why), lost();
    return true;
}

static SIZE_T g_fault_item = SIZE_MAX;  // SCSKILLER_WARM_FAULT=<item>[-<last>]:<av|hang|die|hold<ms>> (development): a fault / hang in that state object; in PSOs (each up to last) with SCSK_TEST_HOOKS
static SIZE_T g_fault_last = SIZE_MAX;
static int g_fault_kind;                // 1 access violation after the create, 2 a create that never returns, 3 the process dies, 4 a PSO create that takes g_fault_hold_ms unless created alone (PSOs, with SCSK_TEST_HOOKS)
static DWORD g_fault_hold_ms;

// SCSKILLER_WARM_SO_MS=<ms> (development): no driver call for state objects; each create takes ms per state object it
// builds on (plus one) and succeeds, so chains of any depth run on WARP. SCSKILLER_WARM_SO_KB=<kb>: each also commits kb that is
// never freed, as NVIDIA keeps memory after a create that Release doesn't return
static int g_so_fake_ms = -1, g_so_fake_kb;
struct FakeSo : IUnknown {  // only Release is called on a created state object here
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, void** pp) override { return *pp = nullptr, E_NOINTERFACE; }
    ULONG STDMETHODCALLTYPE AddRef() override { return 1; }
    ULONG STDMETHODCALLTYPE Release() override { return 1; }
};
static FakeSo g_fake_so;

static __declspec(noinline) HRESULT so_call(const Rec& rec, const D3D12_STATE_OBJECT_DESC* d, ID3D12StateObject* base, ID3D12StateObject** out, size_t j, int linked) {
    __try {  // a fault in the driver must not leave the once_flag half done (waiters would hang)
        if (j == g_fault_item && g_fault_kind == 2) Sleep(INFINITE);
        if (g_so_fake_ms >= 0 && g_so_fake_kb) VirtualAlloc(nullptr, size_t(g_so_fake_kb) << 10, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (g_so_fake_ms >= 0) return Sleep(DWORD(g_so_fake_ms) * (linked + 1)), *out = (ID3D12StateObject*)(IUnknown*)&g_fake_so, S_OK;
        HRESULT hr = rec.tag == 'A' ? o_addso && g_warm_dev7 ? o_addso(g_warm_dev7, d, base, IID_PPV_ARGS(out)) : E_NOINTERFACE
                                    : o_cso && g_warm_dev5 ? o_cso(g_warm_dev5, d, IID_PPV_ARGS(out)) : E_NOINTERFACE;
        if (j == g_fault_item && g_fault_kind == 1) *(volatile int*)nullptr = 0;
        return hr;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        rt_stop("an exception in CreateStateObject / AddToStateObject", j);
        return E_UNEXPECTED;
    }
}

static __declspec(noinline) void so_release(ID3D12StateObject* o) {  // the driver's teardown is guarded like its create
    __try {
        o->Release();
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        rt_stop("an exception releasing a state object", SIZE_MAX);
    }
}

// State objects are never released on worker threads while others create them: the NVIDIA driver (610.88, Jedi Survivor's
// v15 warm) took an access violation in a collection's teardown on 32 threads and left its exclusive lock held (every worker
// then waited in D3D12Core!CStateObject::FinalRelease -> nvwgf2umx NVDEV_Thunk). Created objects are parked and released on
// one thread after the workers join; over SCSKILLER_WARM_RT_PARK_MB of private memory (default 2048: Jedi's 12,792 objects
// peak at ~8.3 GB kept alive, ~0.65 MB each; 0 = release as you go, for A/B runs) they are released in a batch while no
// create runs (g_rt_gate). NVIDIA writes its cache at create, so parking costs only memory.
static std::mutex g_parkmx;
static std::vector<ID3D12StateObject*> g_parked;
static std::shared_mutex g_rt_gate;  // creates shared, a batch release exclusive
static size_t g_park_mb = 2048;
// SCSKiller_WarmMemory: the staged process's private memory budget in MB (0 = none). Shader bytes are mapped, not counted
// (map_blobs); parked state objects get a quarter of it; over it the supervisor releases them, then lowers the number of
// workers allowed to run (g_allowed) until it drops back, and raises it again once well under. State objects later ones
// build on stay alive whatever the worker count, so what a release leaves above the start (g_held_mb) is added to the budget.
static uint32_t g_mem_mb;
static double g_start_mb;
static std::atomic<double> g_held_mb;
static std::atomic<int> g_allowed{1 << 30}, g_active;
static std::atomic<bool> g_d12_over;  // the D3D12 workers are done: the budget no longer limits any
// The warm stopped itself to free memory (warm_restart_tick): private and free MB then. SCSKILLER_WARM_MEM_RESTART=1 (the
// child sets it for --memory-restart 1) allows it, else it is logged once and the warm goes on.
static std::atomic<bool> g_mem_stop;
static double g_mem_stop_mb, g_mem_stop_free;
static HANDLE g_rt_sem;              // g_rt_threads at a time; nullptr = no limit
static std::atomic<uint64_t> g_released, g_release_batches;

// the commit charge, as PrivateUsage is on Windows: Wine reports PrivateUsage as 0 and this as the anonymous resident memory
static double private_mb() {
    PROCESS_MEMORY_COUNTERS m{sizeof m};
    return K32GetProcessMemoryInfo(GetCurrentProcess(), &m, sizeof m) ? m.PagefileUsage / 1048576.0 : 0;
}

// SCSKILLER_WARM_RELEASE_HANG=1 (development): one more state object at the end of the warm, whose Release never returns
struct HangSo : IUnknown {
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, void** pp) override { return *pp = nullptr, E_NOINTERFACE; }
    ULONG STDMETHODCALLTYPE AddRef() override { return 1; }
    ULONG STDMETHODCALLTYPE Release() override { return Sleep(INFINITE), 0; }
};
static HangSo g_hang_so;

static void release_parked(const char* why) {
    if (g_rt_off || g_fault_code) return;  // a poisoned driver may hang on it: the process exit frees them
    std::unique_lock gate(g_rt_gate);  // no create runs while the driver tears objects down
    std::vector<ID3D12StateObject*> v;
    {
        std::lock_guard l(g_parkmx);
        v.swap(g_parked);
    }
    if (v.empty()) return;
    double before = private_mb(), t = now_ms();
    for (auto* o : v) so_release(o), ++g_released;
    ++g_release_batches;
    g_held_mb = std::max(0.0, private_mb() - g_start_mb);  // no create runs under the gate: nothing parked meanwhile
    logf("warm: released %zu parked state objects (%s): private memory %.0f -> %.0f MB in %.1f s", v.size(), why, before, private_mb(), (now_ms() - t) / 1000);
}

static void park(ID3D12StateObject* o) {
    if (!g_park_mb) return so_release(o);
    size_t n;
    {
        std::lock_guard l(g_parkmx);
        g_parked.push_back(o);
        n = g_parked.size();
    }
    if (n % 256 == 0 && private_mb() > g_park_mb) release_parked("over the memory cap");
}

static SoSlot& so_make(size_t j);

static void so_create(size_t j, SoSlot& s) {
    struct Creating {
        size_t was;
        ~Creating() { if (t_so_clock) t_so_clock->creating = was; }
    } creating{t_so_clock ? t_so_clock->creating.exchange(j) : SIZE_MAX};
    const Rec& rec = g_recs[j];
    Reader r{rec.payload};
    std::string dep_why;
    int linked = 0;  // the state objects it builds on
    so_time(g_stuck_ms);
    auto dep = [&](const Hash& k) -> ID3D12StateObject* {
        auto it = g_rec_idx.find(k);
        if (it == g_rec_idx.end() || !g_so[it->second]) { r.fail("the state object it builds on is not in the db"); return nullptr; }
        if (g_rt_off) { dep_why = "ray tracing replay stopped", r.fail("dependency"); return nullptr; }  // never wait on a poisoned driver
        so_untime();  // created here (timed on its own) or by another worker
        SoSlot& d = so_make(it->second);
        so_time(g_stuck_ms);
        if (!d.obj) { dep_why = "the state object it builds on failed", s.skipped = d.skipped, s.known = d.known, r.fail("dependency"); return nullptr; }
        return ++linked, d.obj;
    };
    ID3D12StateObject* base = rec.tag == 'A' ? dep(r.hash()) : nullptr;
    SoDesc d;
    if (r.ok) read_so(r, d, dep);
    if (!r.ok) { s.hr = E_INVALIDARG, s.why = dep_why.empty() ? r.why : dep_why, so_untime(); return; }
    D3D12_STATE_OBJECT_DESC desc = {d.type, (UINT)d.subs.size(), d.subs.data()};
    ID3D12StateObject* obj = nullptr;
    if (g_rt_sem) so_untime(), WaitForSingleObject(g_rt_sem, INFINITE);
    so_time(g_stuck_ms + kSoLinkMs * linked);
    {
        std::shared_lock gate(g_rt_gate);  // taken out here: so_call catches any fault, so this always unlocks
        NvScope nv(nvext_of(rec.tag, rec.payload), true);
        if (!nv.why) s.hr = so_call(rec, &desc, base, &obj, j, linked);
        else s.hr = E_INVALIDARG, s.why = nv.why;
    }
    if (g_rt_sem) ReleaseSemaphore(g_rt_sem, 1, nullptr);
    if (SUCCEEDED(s.hr) && obj) {
        if (g_so_keep[j]) s.obj = obj;
        else park(obj);  // may release a batch into the driver: still timed
    }
    so_untime();
}

// A fault anywhere in the create (decoding, root signatures, the driver) completes the once_flag with a failure instead of
// leaving it pending, which would hang every record built on this one.
static __declspec(noinline) void so_create_guarded(size_t j, SoSlot& s) {
    __try {
        so_create(j, s);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        s.hr = E_UNEXPECTED;
        so_untime();
        rt_stop("an exception creating a state object", j);
    }
}

static SoSlot& so_make(size_t j) {
    SoSlot& s = *g_so[j];
    std::call_once(s.once, [&] {
        s.building = true;
        so_create_guarded(j, s);
        ++g_work;
        std::lock_guard l(g_defer_mx);
        s.done = true, s.building = false;
        if (auto it = g_waiting.find(j); it != g_waiting.end()) g_ready.insert(g_ready.end(), it->second.begin(), it->second.end()), g_waiting.erase(it);
    });
    return s;
}

// A driver rejection's item key and code, for the app's diagnostics (the tallies count them all): at most 500 a process.
static void note_rejected(size_t j, char tag, HRESULT hr) {
    static std::atomic<int> n;
    if (++n > 500) return;
    size_t nrec = g_recs.size();
    Hash k = j < nrec ? key_of(g_recs[j].tag, g_recs[j].payload) : key_of('P', g_plan[j - nrec]);
    logf("warm: rejected '%c' hr=0x%08x key %s", tag, (unsigned)hr, hex(k).data());
}

// A state object item j builds on, directly or through one not created yet (this worker would create that itself), that
// another worker is creating now; SIZE_MAX: none. Each state object is looked at once: collections are shared widely.
static thread_local std::vector<uint32_t> t_seen;
static thread_local uint32_t t_seen_gen;
static size_t so_blocker(size_t j) {
    if (t_seen.size() != g_so.size()) t_seen.assign(g_so.size(), 0), t_seen_gen = 0;
    const uint32_t gen = ++t_seen_gen;
    std::vector<size_t> todo{j};
    while (!todo.empty()) {
        size_t k = todo.back();
        todo.pop_back();
        for (size_t d : g_so_deps[k]) {
            if (t_seen[d] == gen) continue;
            t_seen[d] = gen;
            SoSlot& s = *g_so[d];
            if (s.done) continue;
            if (s.building) return d;
            todo.push_back(d);
        }
    }
    return SIZE_MAX;
}

// nv: a plan item's NVAPI state (an 'N' on its 'P'); a record's own is looked up here.
static int replay(size_t j, const Rec& rec, const Override* ovr = nullptr, const NvExt* nv = nullptr) {
    if (rec.tag == 'R' || rec.tag == 'A') {
        if (g_roundtrip == 2) return 1;  // round trip only: PSO descs are compared, state objects not created
        if (g_rt_off) return -1;  // poisoned: never call the driver for it here, a new process does
        SoSlot& s = so_make(j);
        if (FAILED(s.hr) && removed()) return -2;
        if (FAILED(s.hr) && g_rt_off && g_rt_fatal != j) return -1;  // the fault (or one it builds on): retried
        if (FAILED(s.hr) && (s.skipped || g_rt_fatal == j)) return 2;
        if (FAILED(s.hr)) {
            char what[200];
            if (!s.why.empty()) sprintf_s(what, "replay of '%c' (state object, %zu-byte payload) not created: %s", rec.tag, rec.payload.size(), s.why.c_str());
            else sprintf_s(what, "replay of '%c' (state object) hr=0x%08x", rec.tag, (unsigned)s.hr), note_rejected(j, rec.tag, s.hr);
            note_fail(j, what);
        }
        return SUCCEEDED(s.hr) ? 1 : 0;
    }
    Reader r{rec.payload};
    r.ovr = ovr;
    ID3D12PipelineState* pso = nullptr;
    const uint64_t adds = t_cache_adds;
    HRESULT hr = build(rec, r, &pso, ovr ? nv : nvext_of(rec.tag, rec.payload));
    // an async-PSO ReShade returns its own object at once and compiles on its threads: its GetCachedBlob waits for that
    // compile and returns its result, which keeps the item in flight here for the stuck supervisor and a death's culprit
    const bool layers = pso && *(uintptr_t*)pso - g_layer_lo < g_layer_hi - g_layer_lo;
    if (layers) {
        ID3DBlob* blob = nullptr;
        // asked twice on a failure: a failed compile's result repeats, a blob allocation that failed after a good compile may not
        if (FAILED(hr = pso->GetCachedBlob(&blob))) hr = pso->GetCachedBlob(&blob);
        if (blob) blob->Release();
    }
    if (pso) pso->Release();  // the driver keeps the compiled result in its disk cache
    if (SUCCEEDED(hr) && g_count_cached && !layers && t_cache_adds == adds) ++g_warm_cached;  // a layer's thread compiled it: unseen
    if (FAILED(hr) && removed()) return -2;  // its root signature failed on the removed device too: retried, never failed
    // a recording from another vendor's GPU: not for this one, and done, so a recompile on this driver doesn't retry it
    if (hr == E_INVALIDARG && r.ok && r.unblendable) return g_warm_unsupported++, 1;
    if (FAILED(hr)) {
        char what[160];
        const char* kind = rec.tag == 'C' ? "compute desc" : rec.tag == 'G' ? "graphics desc" : "pipeline stream";
        if (!r.ok) sprintf_s(what, "%s '%c' (%s, %zu-byte payload) not created: %s", ovr ? "plan item on" : "replay of", rec.tag, kind, rec.payload.size(), r.why);
        else sprintf_s(what, "%s '%c' (%s) hr=0x%08x", ovr ? "plan item on" : "replay of", rec.tag, kind, (unsigned)hr), note_rejected(j, rec.tag, hr);
        note_fail(j, what);
    }
    return SUCCEEDED(hr) ? 1 : 0;
}

static int replay_plan(size_t j, const std::string& p) {
    Reader r{p};
    Override o;
    const Rec* t = parse_plan(r, o);
    if (!t) note_fail(j, r.ok ? "plan item: its template is not in the db" : std::string("plan item malformed: ") + r.why);
    return t ? replay(j, *t, &o, nvext_of('P', p)) : 0;
}

// A fault inside a layer or driver we call into must not take the process down: a state object's poisons ray tracing
// (rt_stop), a PSO's ends the replay (g_fault_code), a D3D11 item's is skipped and a few stop the warm.
static std::atomic<int> g_warm_faults;
static bool is_so(size_t j, size_t nrec) { return j < nrec && (g_recs[j].tag == 'R' || g_recs[j].tag == 'A'); }
static bool known_skip(size_t j, size_t nrec) { return g_known_items.count(j) || (is_so(j, nrec) && g_so[j] && g_so[j]->known); }
// 1 created, 2 skipped (SoSlot::skipped), 0 failed, -1 left for a new process (ray tracing poisoned), -2 not created: the
// device was removed or it faulted
static __declspec(noinline) int replay_one(size_t j, size_t nrec) {
    if (g_skip.count(j) || g_known_items.count(j)) return 2;
    if (j == g_remove_item && !g_remove_shared && g_warm_dev5) g_warm_dev5->RemoveDevice();
#ifdef SCSK_TEST_HOOKS
    if (j > g_remove_item && j <= g_remove_last && (j - g_remove_item) % g_remove_step == 0 && !g_remove_shared && g_warm_dev5) g_warm_dev5->RemoveDevice();   // alone too
    if (j >= g_remove_item && j <= g_remove_last && g_remove_shared && !t_alone && g_warm_dev5) return g_warm_dev5->RemoveDevice(), removed(), -2;
    if (j == g_fault_item && g_fault_kind == 1 && !is_so(j, nrec)) *(volatile int*)nullptr = 0;
    if (j >= g_fault_item && j <= g_fault_last && g_fault_kind == 2 && !is_so(j, nrec)) Sleep(INFINITE);
    if (j == g_fault_item && g_fault_kind == 3) TerminateProcess(GetCurrentProcess(), 0xC0000005);  // as a driver fault no handler sees ends it
    if (j >= g_fault_item && j <= g_fault_last && g_fault_kind == 4 && !t_alone && !is_so(j, nrec)) Sleep(g_fault_hold_ms);
#endif
    return j < nrec ? replay(j, g_recs[j]) : replay_plan(j, g_plan[j - nrec]);
}
// This run's items: 0 not taken, 1 created (or skipped: g_crash), 2 failed, 3 unfinished (left for the retry), 4 not created:
// the device was removed or a replay faulted while it was in flight, 5 skipped (replay returned 2), 6 in flight.
// SCSKiller_Retry reads them, and the parent does from scskiller_items.bin when this process dies without a last line.
struct ItemStates {
    uint8_t* p = nullptr;
    size_t n = 0;
    std::vector<uint8_t> heap;  // the file couldn't be mapped: a death then ends the warm as before
    void open(const std::wstring& path, uint64_t first, size_t n12, uint64_t total) {
        const uint64_t bytes = sizeof(ItemsHeader) + n12;
        HANDLE f = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, CREATE_ALWAYS, 0, nullptr);
        HANDLE m = f == INVALID_HANDLE_VALUE ? nullptr : CreateFileMappingW(f, nullptr, PAGE_READWRITE, (DWORD)(bytes >> 32), (DWORD)bytes, nullptr);
        auto* v = m ? (uint8_t*)MapViewOfFile(m, FILE_MAP_WRITE, 0, 0, (SIZE_T)bytes) : nullptr;
        if (m) CloseHandle(m);  // the view keeps the mapping and the file
        if (f != INVALID_HANDLE_VALUE) CloseHandle(f);
        n = n12;
        if (!v) {
            logf("warm: %ls not mapped (error %lu): a death without a last line can't be resumed", kItemsFile, GetLastError());
            heap.assign(n12, 0), p = heap.data();
            return;
        }
        p = v + sizeof(ItemsHeader);
        memset(p, 0, n12);  // an extended file's new bytes are the file system's to fill
        *(ItemsHeader*)v = {kItemsMagic, GetCurrentProcessId(), first, n12, total};
    }
    uint8_t& operator[](size_t i) { return p[i]; }
    uint8_t* begin() { return p; }
    uint8_t* end() { return p + n; }
    size_t size() const { return n; }
};
static ItemStates g_item_state;
static uint8_t state_of(int ok) { return ok == 1 ? 1 : ok == 2 ? 5 : ok == 0 ? 2 : ok == -2 ? 4 : 3; }
static bool finished(uint8_t state) { return item_finished(state); }
static int replay_guarded(size_t j, size_t nrec) {
    __try {
        return replay_one(j, nrec);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        if (is_so(j, nrec)) {
            rt_stop("an exception replaying a state object", j);
            return g_rt_fatal == j ? 2 : -1;
        }
        DWORD none = 0, code = GetExceptionCode();
        g_fault_code.compare_exchange_strong(none, code);
        size_t no_item = SIZE_MAX;
        g_faulted.compare_exchange_strong(no_item, j);  // before g_removed: whoever sees the removal sees the item
        g_removed = true;
        logf("warm: exception 0x%08x replaying item %zu: the replay stops, a new process goes on", code, j);
        return -2;
    }
}

static size_t g_retry_from = SIZE_MAX;  // set when the warm ends poisoned with items unfinished
static uint64_t g_retry_failed;         // failures among the items before g_retry_from (the rest are counted by the retry)
static std::atomic<uint64_t> g_retry_skipped;  // ...and skips: SCSKiller_Skipped reads it while the warm runs
// Nine PSOs hung, or the stuck-worker replacements ran out, with the device intact (a mass hang, which may be transient): a new process goes on
// from the first hung item and creates each hung PSO alone first, and only one that hangs again alone is a culprit.
static std::atomic<bool> g_hung;
static std::vector<size_t> g_hung_items;  // the PSOs the supervisor abandoned: culprits, unless the run ends in a mass hang
static std::string g_alone_skip;  // SCSKiller_Hung: the items that hung alone, joined by commas: a new process skips them

// D3D11 items (warm11.cpp): one draw/dispatch per shader on a D3D11 device on the warm device's adapter.
struct Dev11;
Dev11* warm11_open(LUID adapter, bool debug);
bool warm11_item(Dev11* d, uint32_t stage, const void* bytes, size_t n, const char** why);
bool warm11_pair(Dev11* d, const void* hs, size_t hsn, const void* ds, size_t dsn, const char** why);  // '2': HS + DS
bool warm11_draw(Dev11* d, std::string_view item, std::string_view (*blob)(const uint8_t*), const char** why);  // '3'
void warm11_drain(Dev11* d, uint32_t stage);  // debug layer on: collect the messages the last item caused
HRESULT warm11_flush(Dev11* d);               // Flush + wait on an event query: everything drawn so far is compiled
void warm11_close(Dev11* d);                  // logs the collected debug-layer messages

static std::string_view blob11(const uint8_t* h) {
    auto it = g_blob_bytes.find(*(const Hash*)h);
    return it == g_blob_bytes.end() ? std::string_view() : it->second;
}

static bool item11_guarded(Dev11* d, uint32_t stage, const std::string_view* b, const std::string_view* ds, const char** why) {
    __try {
        return stage == 8 ? warm11_draw(d, *b, blob11, why)
             : ds ? warm11_pair(d, b->data(), b->size(), ds->data(), ds->size(), why) : warm11_item(d, stage, b->data(), b->size(), why);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        if (++g_warm_faults <= 3) logf("warm11: exception 0x%08x on a stage %u shader, skipped", GetExceptionCode(), stage);
        return *why = "exception", false;
    }
}

// D3D11 has no PSOs: the driver compiles at the first draw. Each worker thread owns a device (the immediate context
// is single-threaded) and takes batches of items; a batch counts as done once flushed and waited for, so a stop still
// leaves every item below "done" compiled. A supervisor (the calling thread) abandons a worker stuck in one D3D11 call:
// its batch's uncounted items are skipped, and on this driver from now on unless it keeps hanging (as hung PSOs), a
// fresh worker with a fresh device carries on, and the stuck thread (and its device) is left alone for good. So the run
// always ends, and a stop is honoured within seconds: a batch abandoned then is left for the resume, which starts at the
// first one (g_retry_from). After 8 replacements the driver keeps hanging: a new process goes on from the next batch
// (g_hung).
struct Worker11 {
    std::thread t;
    std::atomic<double> busy{0};           // now_ms() when the current D3D11 call began, 0 = between calls
    std::atomic<bool> claimed{false};      // the current batch's outcome is taken: by the worker (counted) or the supervisor (abandoned)
    std::atomic<bool> finished{false};
    std::atomic<size_t> j{0}, end{0}, at{0};  // current batch, the item being drawn (= end: the flush)
    std::map<std::string, uint64_t> tally;  // "<stage> ok" / "<stage> failed: <why>"; read after finished
};

static size_t g_hang11 = SIZE_MAX;  // tests (SCSKILLER_TEST_HANG11): this item's call never returns, like a hung driver
static size_t g_hang11_last;        // ...and each one after it up to this (SCSKILLER_TEST_HANG11_LAST)
static DWORD g_hold11 = INFINITE;   // or returns after this many ms
static int g_hold11_wall;           // or (1) until the process has been suspended (hold ignored), and then the next item's call never returns
static double g_hold11_frozen;      // g_frozen_ms as that item began
// Under Wine, DXVK keeps every shader and pipeline a D3D11 device made until the device goes (about 0.29 MB an item), so a
// worker recreates its device every this many items, after a flushed batch: what the draws compiled is in the cache by then.
static size_t g_recycle11 = SIZE_MAX;
#ifdef SCSK_TEST_HOOKS
static size_t g_lost11 = SIZE_MAX, g_lost11_last;  // SCSKILLER_TEST_LOST11=<item>[,0[,<last>]]: the flush of its batch loses the device, and the own flush of each up to last too unless 0
static int g_lost11_alone = 1;
#endif

static std::mutex g_mx11;
static std::atomic<size_t> g_left11{SIZE_MAX};  // the first item a worker stopped while pacing left for the resume
static std::vector<std::array<uint64_t, 4>> g_failed11;  // the batches (or items) done with failures or skips: first item, failures, skips, known

static void worker11(Worker11& w, std::atomic<size_t>& next, size_t n, bool debug) {
    static const char* names[] = {"?", "VS", "PS", "DS", "HS", "GS", "CS", "HS+DS", "VS+layout"};
    const size_t batch = 16, n12 = g_recs.size() + g_plan.size();
    SetThreadPriority(GetCurrentThread(), g_prio);
    w.busy = live_ms();
    Dev11* d = warm11_open(g_warm_luid, debug);
    w.busy = 0;
    if (!d) { g_warm_faults = 3; w.finished = true; return; }
    size_t made = 0;  // items on this device
    uint32_t stage = 0;
    const char* why = "";
    auto tally = [&](int r, uint32_t s, const char* y) {
        if (r != 2) w.tally[names[s < std::size(names) ? s : 0] + std::string(r == 1 ? " ok" : r == 3 ? " skipped: it crashed this driver before" : " failed: ") + (r ? "" : y)]++;
    };
    // 1 drawn, 0 failed (why), 2 another pass's, 3 skipped: it crashed this driver in an earlier compile, -1 abandoned while stuck in it
    auto item = [&](size_t k) {
        if (other_pass(n12 + k)) return 2;
        const char tag = g_items11[k][0];
        const std::string_view it = std::string_view(g_items11[k]).substr(1);
        Hash h{}, h2{};
        stage = 0;
        const bool pair = tag == '2' && it.size() == 40, draw = tag == '3';  // '2': HS + DS; '3': vertex stages + layout
        if (tag == '1' && it.size() == 24) memcpy(&stage, it.data(), 4), memcpy(h.data(), it.data() + 4, 20);
        if (pair) stage = 7, memcpy(h.data(), it.data(), 20), memcpy(h2.data(), it.data() + 20, 20);
        auto b = g_blob_bytes.find(h), b2 = pair ? g_blob_bytes.find(h2) : g_blob_bytes.end();
        why = draw ? "" : !stage ? "malformed item" : b == g_blob_bytes.end() || (pair && b2 == g_blob_bytes.end()) ? "missing blob" : "";
        if (draw) stage = 8;
        if (!g_known_keys.empty() && g_known_keys.count(key11(k))) return 3;
        if (k == g_hang11) g_hold11_frozen = g_frozen_ms;
        // the next item's stamp is pinned to one made after the resume, before the supervisor counted the pause
        w.at = k, w.busy = g_hold11_wall && k == g_hang11 + 1 ? now_ms() - g_hold11_frozen : live_ms();
        if (k == g_hang11 && g_hold11_wall) for (ULONGLONG t = GetTickCount64(), u; (u = GetTickCount64()) - t < 5000; t = u) Sleep(1);
        else if (k == g_hang11 || (g_hold11_wall && k == g_hang11 + 1)) Sleep(k == g_hang11 ? g_hold11 : INFINITE);
        else if (k > g_hang11 && k <= g_hang11_last) Sleep(g_hold11);
        bool good = !*why && item11_guarded(d, stage, draw ? &it : &b->second, pair ? &b2->second : nullptr, &why);
        w.busy = 0;
        if (w.claimed) return -1;  // abandoned while stuck in that call: touch nothing shared, leak the device
        warm11_drain(d, stage);
        ++g_work;
        return good ? 1 : 0;
    };
    auto add = [&](size_t at, const std::array<uint64_t, 4>& c, uint64_t crashed) {  // c: items by item()'s outcome
        g_warm_fail += c[0], g_warm_ok += c[1], g_warm_other += c[2], g_warm_skipped += c[3] + crashed, g_warm_known += c[3];
        if (c[0] || c[3] || crashed) {
            std::lock_guard l(g_mx11);
            g_failed11.push_back({at, c[0], c[3] + crashed, c[3]});
        }
    };
    for (size_t j; g_warm_faults < 3;) {
        while (g_state == PAUSE) Sleep(50);
        if (g_state == STOP || g_hung || !pace_losses() || (j = next.fetch_add(batch)) >= n) break;
        const size_t end = std::min(j + batch, n);
        w.j = j, w.end = end, w.claimed = false;
        int res[batch];
        uint32_t st[batch];
        const char* whys[batch];
        std::array<uint64_t, 4> c{};
        for (size_t k = j; k < end; ++k) {
            if ((res[k - j] = item(k)) < 0) return;
            st[k - j] = stage, whys[k - j] = why, c[res[k - j]]++;
        }
        w.at = end, w.busy = live_ms();
        HRESULT hr = warm11_flush(d);
#ifdef SCSK_TEST_HOOKS
        if (g_lost11 - j < end - j) hr = DXGI_ERROR_DEVICE_REMOVED;
#endif
        w.busy = 0;
        if (w.claimed.exchange(true)) return;
        if (FAILED(hr)) lost();
        if (SUCCEEDED(hr)) {
            for (size_t k = j; k < end; ++k) tally(res[k - j], st[k - j], whys[k - j]);
            add(j, c, 0);
            if ((made += end - j) >= g_recycle11 && next < n && g_state != STOP) {
                const double before = private_mb();
                w.j = end, w.claimed = false, w.busy = live_ms();  // no batch: a hang in here abandons nothing
                warm11_close(d);
                d = warm11_open(g_warm_luid, debug);
                w.busy = 0;
                if (w.claimed.exchange(true)) return;
                if (!d) { g_warm_faults = 3; w.finished = true; return; }
                logf("warm11: device recreated after %zu items: private memory %.0f -> %.0f MB", made, before, private_mb());
                made = 0;
            }
            continue;
        }
        // device removed (e.g. a TDR): each item goes again alone on a new device (a failure may be the removal's), and one that
        // removes it again is skipped as a crash. [w.j, end) stays the items not counted yet: a stuck call abandons only those.
        logf("warm11: D3D11 device lost (0x%08x) on items %zu-%zu; recreating it, they go again one by one", (unsigned)hr, j, end - 1);
        made = 0;
        for (size_t k = j;; ++k) {
            if (FAILED(hr)) {
                w.j = k, w.at = end, w.claimed = false, w.busy = live_ms();
                warm11_close(d);
                d = warm11_open(g_warm_luid, debug);
                w.busy = 0;
                if (w.claimed.exchange(true)) return;  // owned until the next draw: what is counted here can't be abandoned too
                if (!d) {
                    g_warm_fail += end - k, w.tally["batch failed: device lost"] += end - k;
                    std::lock_guard l(g_mx11);
                    g_failed11.push_back({k, end - k, 0, 0});
                    g_warm_faults = 3, w.finished = true;
                    return;
                }
                hr = S_OK;
            }
            if (k == end) break;
            std::array<uint64_t, 4> one{};
            if (res[k - j] >= 2) {
                tally(res[k - j], st[k - j], whys[k - j]), one[res[k - j]] = 1, w.j = k + 1, add(k, one, 0);
                continue;
            }
            if (!pace_losses()) {  // stopped while waiting: the items left go to the resume
                for (size_t m = g_left11; k < m && !g_left11.compare_exchange_weak(m, k);) {}
                warm11_close(d);
                w.finished = true;
                return;
            }
            w.j = k, w.claimed = false;
            const int r = item(k);
            if (r < 0) return;
            w.at = end, w.busy = live_ms();
            hr = warm11_flush(d);
#ifdef SCSK_TEST_HOOKS
            if (k >= g_lost11 && k <= g_lost11_last && g_lost11_alone) hr = DXGI_ERROR_DEVICE_REMOVED;
#endif
            w.busy = 0;
            if (w.claimed.exchange(true)) return;
            w.j = k + 1;
            if (FAILED(hr)) {
                lost();
                logf("warm11: D3D11 device lost (0x%08x) by item %zu alone: skipped, and on this driver from now on", (unsigned)hr, k);
                w.tally["item skipped: device lost"]++;
                culprit(n12 + k);
                add(k, one, 1);
                continue;
            }
            tally(r, stage, why), one[r] = 1, add(k, one, 0);
        }
    }
    warm11_close(d);
    w.finished = true;
}

static bool g_abandoned11;  // a stuck D3D11 worker may still wake up: keep what it could touch alive
static bool g_abandoned12;  // likewise a stuck D3D12 worker (warm_main)
static thread_local bool t_replay;  // a warm worker: its creates reach the hooks below a mod (hook_below) too

// Where a resume or a new process goes on in the D3D11 items: the first batch a stop left, else the next one after a mass
// hang; SIZE_MAX: at the count. With the failures and skips below it.
static size_t g_from11 = SIZE_MAX;
static uint64_t g_failed11_below, g_skipped11_below, g_known11_below;

static void warm11_main(size_t first) {
    const size_t n = g_items11.size();
    const bool debug = GetEnvironmentVariableW(L"SCSKILLER_D3D11_DEBUG", nullptr, 0) > 0;  // D3D11 debug layer, for development
    // Devices = threads. Measured (NVIDIA, 3500 cold Orcs Must Die 3 shaders, zero-count indirect draws): 2: 714/s,
    // 4: 1030/s, 8: 1130/s; the full 17752 cached: ~3600/s. Idle or below-normal priority: same on an idle machine.
    const int threads = std::min(g_threads, 4);
    // One call (a create + draw, or the flush of a batch of 16) taking this long is a hang, not a slow compile.
    double stuck_ms = 60000, stuck_stop_ms = 2000;  // ponytail: fixed limits, measure the slowest real batch if they bite
    wchar_t test[64];
    if (env(L"SCSKILLER_TEST_HANG11", test)) swscanf_s(test, L"%zu,%lf,%lu,%d", &g_hang11, &stuck_ms, &g_hold11, &g_hold11_wall);  // "<item>,<limit ms>[,<hold ms>[,1: hold until suspended]]"
    if (env(L"SCSKILLER_TEST_HANG11_LAST", test)) g_hang11_last = _wtoi64(test);
    // tests: Wine played by SCSKILLER_TEST_WINE only in a process exporting SCSKiller_WarmHost (selftest)
    if (GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "wine_get_version") ||
        (GetProcAddress(GetModuleHandleW(nullptr), "SCSKiller_WarmHost") && GetEnvironmentVariableW(L"SCSKILLER_TEST_WINE", nullptr, 0)))
        g_recycle11 = env(L"SCSKILLER_TEST_RECYCLE11", test) ? std::max(1, _wtoi(test)) : 1024;
    logf("warm11: private memory %.0f MB at the start", private_mb());
#ifdef SCSK_TEST_HOOKS
    if (env(L"SCSKILLER_TEST_LOST11", test)) swscanf_s(test, L"%zu,%d,%zu", &g_lost11, &g_lost11_alone, &g_lost11_last), g_lost11_last = std::max(g_lost11_last, g_lost11);
#endif
    size_t left = SIZE_MAX;                         // the first batch a stop left
    std::vector<std::pair<size_t, uint64_t>> hung;  // the batches skipped for hanging: first item, items
    std::vector<std::pair<size_t, uint64_t>> stuck; // ...of those, the ones whose call was still the one timed out: culprits
    auto next = new std::atomic<size_t>(first);  // leaked with the workers if one is abandoned
    std::vector<Worker11*> ws;
    std::map<std::string, uint64_t> tally;
    auto t0 = now_ms();
    auto spawn = [&] { auto w = new Worker11; w->t = std::thread(worker11, std::ref(*w), std::ref(*next), n, debug); ws.push_back(w); };
    for (int i = 0; i < threads; ++i) spawn();
    int replaced = 0;
    double pass = now_ms();
    while (!ws.empty()) {
        Sleep(50);
        double now = live_pass(pass);
        for (size_t i = 0; i < ws.size();) {
            Worker11* w = ws[i];
            double since = started(w->busy, now);
            if (w->finished) {
                w->t.join();
                for (auto& [k, c] : w->tally) tally[k] += c;
                delete w;
                ws.erase(ws.begin() + i);
            } else if (since && now - since > (g_state == STOP ? stuck_stop_ms : stuck_ms) && !w->claimed.exchange(true)) {
                size_t j = w->j, end = w->end, at = w->at;
                bool stopping = g_state == STOP;  // our stop cut the batch short, no hang: the resume draws it
                std::string call = at < end ? "drawing item " + std::to_string(at) : end > j ? "waiting for the GPU after items "
                                   + std::to_string(j) + "-" + std::to_string(end - 1) : "creating its device";
                logf("warm11: a D3D11 worker is stuck (%.0f s %s): abandoned, its %zu items %s", (now - since) / 1000, call.c_str(), end - j,
                     stopping ? "are left for a new process" : "are skipped for this compile");
                if (stopping && end > j) left = std::min(left, j);
                else if (end > j) {
                    g_warm_skipped += end - j, tally["batch skipped: device stuck"] += end - j, hung.push_back({j, end - j});
                    if (w->busy == since) stuck.push_back({j, end - j});  // else that call ended before the claim: j and end may be a later batch's
                }
                g_abandoned11 = true;
                w->t.detach();  // w and its device are leaked on purpose: the thread may still return into them
                ws.erase(ws.begin() + i);
                if (g_state != STOP && *next < n && ++replaced <= 8) spawn();
                else if (replaced > 8 && !g_hung.exchange(true)) logf("warm11: the driver keeps hanging: a new process goes on from the next batch, the hung ones skipped for this compile");
            } else ++i;
        }
    }
    for (auto& [k, c] : tally) logf("warm11: %s x%llu", k.c_str(), c);
    logf("warm11: %zu D3D11 items on %d threads in %.1f s, private memory %.0f MB", std::min(next->load(), n) - first, threads, (now_ms() - t0) / 1000, private_mb());
    // with the workers gone every batch below next is done or abandoned
    left = std::min(left, g_left11.load());
    if (size_t from = left != SIZE_MAX ? left : (g_hung || g_mem_stop) && *next < n ? next->load() : SIZE_MAX; from != SIZE_MAX) {
        g_from11 = from;
        for (auto& [j, f, s, k] : g_failed11)
            if (j < from) g_failed11_below += f, g_skipped11_below += s, g_known11_below += k;
        for (auto& [j, c] : hung) g_skipped11_below += j < from ? c : 0;
    }
    if (!g_hung)
        for (auto& [j, c] : stuck)
            for (size_t k = j; k < j + c; ++k) culprit(g_recs.size() + g_plan.size() + k);
    if (!g_abandoned11) delete next;
}

// Items go in file order (db records, gen templates, plan items, D3D11 items), so a stopped run resumes at item g_start.
static void warm_main() {
    size_t nrec = g_recs.size(), n12 = nrec + g_plan.size(), n = n12 + g_items11.size(), first = std::min<size_t>(g_start, n);
    std::atomic<size_t> next{first};
    g_so.resize(nrec), g_so_keep.assign(nrec, 0), g_so_deps.resize(nrec);
    size_t nso = 0;
    for (size_t j = 0; j < nrec; ++j)
        if (g_recs[j].tag == 'R' || g_recs[j].tag == 'A') {
            g_so[j] = std::make_unique<SoSlot>(), ++nso;
            for (auto& k : so_deps(g_recs[j].payload, g_recs[j].tag))
                if (auto it = g_rec_idx.find(k); it != g_rec_idx.end()) g_so_keep[it->second] = 1, g_so_deps[j].push_back(it->second);
        }
    for (auto& deps : g_so_deps) std::erase_if(deps, [](size_t d) { return !g_so[d]; });
    nv_warm_init();
    if (nso) logf("warm: %zu of the recorded items are ray tracing state objects%s", nso,
                  !g_warm_dev5 ? " (this device has no ID3D12Device5: they fail)" : !g_warm_dev7 ? " (no ID3D12Device7: additions fail)" : "");
    auto t0 = now_ms();
    logf("warm: compiling %zu recorded + %zu generated PSOs + %zu D3D11 shaders from item %zu on %d threads", nrec, g_plan.size(),
         g_items11.size(), first, g_threads);
    wchar_t core[MAX_PATH] = L"none";
    HMODULE cm = GetModuleHandleW(L"D3D12Core.dll");
    auto sdk = cm ? (const UINT*)GetProcAddress(cm, "D3D12SDKVersion") : nullptr;
    if (cm) GetModuleFileNameW(cm, core, MAX_PATH);
    logf("warm: D3D12 runtime %s (SDK %u)", shown(core).c_str(), sdk ? *sdk : 0);
    auto staged = (const UINT*)GetProcAddress(GetModuleHandleW(nullptr), "D3D12SDKVersion");  // scskiller_warm's, set from --sdk
    if (cm && staged && *staged && _wcsicmp(core, (g_dir + L"D3D12\\D3D12Core.dll").c_str()))
        logf("warm: the game's D3D12 runtime (SDK %u) staged in D3D12\\ isn't the one loaded", *staged);
    // the signature at the process heap's base, as warm.manifest opts in: an IFEO setting on the exe name can override it
    const uint32_t heap_sig = *(const uint32_t*)((const uint8_t*)GetProcessHeap() + 0x10);
    if (heap_sig == 0xDDEEDDEE) logf("warm: process heap: segment");
    else if (heap_sig == 0xFFEEFFEE) logf("warm: process heap: NT (0x%08x)", heap_sig);
    else logf("warm: process heap: unknown (0x%08x)", heap_sig);
    // SCSKILLER_WARM_TIMES=1 (development): stage\scskiller_warm_times.csv, "item,ms,ok" per PSO (A/B tests of what a warm leaves cached)
    FILE* times = GetEnvironmentVariableW(L"SCSKILLER_WARM_TIMES", nullptr, 0) ? _wfopen((g_dir + L"scskiller_warm_times.csv").c_str(), L"w") : nullptr;
    wchar_t rt[8] = {};
    if (env(L"SCSKILLER_WARM_ROUNDTRIP", rt)) g_roundtrip = wcscmp(rt, L"only") ? 1 : 2;
    wchar_t park[16] = {};
    if (g_mem_mb) g_park_mb = std::max<uint32_t>(256, g_mem_mb / 4);
    if (env(L"SCSKILLER_WARM_RT_PARK_MB", park)) g_park_mb = _wtoi(park);
    g_allowed = g_threads;
    g_start_mb = private_mb();
    if (g_mem_mb && private_mb() > g_mem_mb)  // the dbs' records alone are over it: start with one worker
        g_allowed = 1, logf("warm: private memory %.0f MB over the %u MB budget at the start: 1 worker", private_mb(), g_mem_mb);
    if (g_rt_threads > 0 && g_rt_threads < g_threads) {
        g_rt_sem = CreateSemaphoreW(nullptr, g_rt_threads, g_rt_threads, nullptr);
        logf("warm: ray tracing on %d thread%s (a retry after a driver fault or hang)", g_rt_threads, g_rt_threads == 1 ? "" : "s");
    }
    if (g_threads == 1) g_rt_threads = 1;  // one thread: a fault is its item's alone
    for (size_t k : g_skip)
        if (k < nrec && g_so[k]) std::call_once(g_so[k]->once, [&] { g_so[k]->hr = E_FAIL, g_so[k]->why = "it faulted or hung in the driver alone in an earlier run", g_so[k]->skipped = g_so[k]->done = true; });
    g_item_state.open(g_dir + kItemsFile, first, n12, n);
    auto key = [&](size_t j) { return j < nrec ? key_of(g_recs[j].tag, g_recs[j].payload) : key_of('P', g_plan[j - nrec]); };
    HashSet crash_found;
    if (!g_crash_keys.empty() || !g_known_keys.empty()) {
        for (size_t j = 0; j < n12; ++j)
            if (Hash k = key(j); g_crash_keys.count(k)) g_crash.insert(j), crash_found.insert(k);
            else if (g_known_keys.count(k)) g_known_items.insert(j);
        for (size_t k : g_crash)  // what builds on one is skipped instead of created
            if (k < nrec && g_so[k]) std::call_once(g_so[k]->once, [&] { g_so[k]->hr = E_FAIL, g_so[k]->why = "it removed the device in an earlier run", g_so[k]->skipped = g_so[k]->done = true; });
        for (size_t k : g_known_items)
            if (k < nrec && g_so[k]) std::call_once(g_so[k]->once, [&] { g_so[k]->hr = E_FAIL, g_so[k]->why = "it crashed or hung this driver in an earlier compile", g_so[k]->skipped = g_so[k]->known = g_so[k]->done = true; });
        if (!g_crash_keys.empty()) logf("warm: %zu items skipped: they removed the device in an earlier run", g_crash.size());
        if (!g_known_keys.empty()) logf("warm: %zu items skipped: they crashed or hung this driver in an earlier compile", g_known_items.size());
    }
    wchar_t fault[32] = {};  // SCSKILLER_WARM_FAULT (development): see g_fault_item
    if (env(L"SCSKILLER_WARM_FAULT", fault)) {
        const wchar_t* hold = wcsstr(fault, L"hold");
        g_fault_item = _wtoi64(fault), g_fault_kind = wcsstr(fault, L"hang") ? 2 : wcsstr(fault, L"die") ? 3 : hold ? 4 : 1;
        if (hold) g_fault_hold_ms = (DWORD)_wtoi(hold + 4);
        const wchar_t* last = wcschr(fault, L'-');
        g_fault_last = last ? _wtoi64(last + 1) : g_fault_item;
    }
    if (env(L"SCSKILLER_WARM_SO_MS", fault)) g_so_fake_ms = _wtoi(fault);
    if (env(L"SCSKILLER_WARM_SO_KB", fault)) g_so_fake_kb = _wtoi(fault);
    if (env(L"SCSKILLER_WARM_REMOVE", fault)) {
        g_remove_item = g_remove_last = _wtoi64(fault), g_remove_shared = wcsstr(fault, L"shared");
        if (const wchar_t* last = wcschr(fault, L'-')) g_remove_last = _wtoi64(last + 1);
        if (const wchar_t* step = wcschr(fault, L'/')) g_remove_step = std::max<size_t>(1, _wtoi64(step + 1));
    }
    // In flight when an earlier process's device was removed, or hung in an earlier process's mass hang: each alone, before
    // the workers, so a removal or a hang names its item. Each runs on a thread of its own, given up on as the supervisor
    // gives up on a stuck worker: one that hangs alone is skipped for this compile and is a culprit, and the run goes on.
    wchar_t stuck_env[16] = {};  // SCSKILLER_WARM_STUCK_S: the per-item limit, default 60 s (Warmer raises it when hung items compile alone)
    double stuck_ms = env(L"SCSKILLER_WARM_STUCK_S", stuck_env) ? 1000.0 * _wtoi(stuck_env) : 60000, stuck_stop_ms = 2000;
    g_stuck_ms = stuck_ms;
    struct Alone {
        size_t k;
        SoClock so;
        std::atomic<bool> done{false};
        int ok = 0;
    };
    std::vector<Alone*> given_up;  // leaked: their stuck threads may still return into them
    size_t blamed_alone = SIZE_MAX, alone_at = 0;
    t_replay = true;
    double alone_pass = now_ms();
    for (; alone_at < g_alone.size() && g_state != STOP; ++alone_at) {
        size_t k = g_alone[alone_at];
        if (k < first || k >= n12 || g_crash.count(k) || other_pass(k)) continue;
        while (g_state == PAUSE) Sleep(50);
        if (!pace_losses()) break;
        g_item_state[k] = kItemInFlight;
        auto* a = new Alone{k};
        std::thread t([a, nrec] {
            SetThreadPriority(GetCurrentThread(), g_prio);
            t_replay = t_alone = true;
            t_so_clock = &a->so;
            a->ok = replay_guarded(a->k, nrec);
            a->done = true;
        });
        const bool so = is_so(k, nrec);
        const double since = live_pass(alone_pass);
        size_t creating = SIZE_MAX;
        auto stuck = [&] {  // as the workers' supervisor judges one, its pass skipping a suspension
            double now = live_pass(alone_pass);
            creating = a->so.creating;  // before the segment judged: one it moves past meanwhile blames nothing
            if (!so) return now - since > (g_state == STOP ? stuck_stop_ms : stuck_ms);
            auto g = a->so.seg.load();
            if (g.since > now && a->so.seg.compare_exchange_strong(g, {now, g.limit})) g.since = now;
            if (g_state == STOP) return now - (g.since ? g.since : since) > stuck_stop_ms;
            if (g.since) return now - g.since > g.limit;
            return g_rt_off && now - since > stuck_stop_ms;
        };
        while (!a->done && !stuck()) Sleep(20);
        if (!a->done) {
            t.detach();
            given_up.push_back(a);
            g_abandoned12 = true;
            if (g_state == STOP) {
                g_item_state[k] = 3;
                logf("warm: item %zu, created alone, stopped: left for the resume", k);
                break;
            }
            if (so && (a->so.seg.load().since || !g_rt_off)) rt_stop("a state object create hung alone", k, a->so.creating == creating ? creating : kNoBlame);
            // a PSO stuck behind a poisoned driver or a removed device is its victim: left for a new process
            const bool fatal = so ? g_rt_fatal == k : !g_rt_off && !removed();
            if (fatal && !so) culprit(k);
            if (fatal) g_alone_skip += (g_alone_skip.empty() ? "" : ",") + std::to_string(k);
            g_item_state[k] = fatal ? kItemInFlight : g_removed ? 4 : 3;  // 5 once no later item is alone with it (a death names no item then)
            ++g_work;  // the app's stall watchdog sees the alone items move
            logf("warm: item %zu, created alone, stuck for %.0f s: %s", k, (live_pass(alone_pass) - since) / 1000,
                 fatal ? "skipped for this compile, later runs on this driver skip it" : "left for a new process");
            continue;
        }
        t.join();
        int ok = a->ok;
        delete a;
        if (ok == -2 || removed()) {
            // after a create was given up on, it may have removed the device: then only an exception names its item, else all of
            // them are created alone again by the next process
            if (size_t f = g_faulted; f != SIZE_MAX) blamed_alone = f;
            else if (given_up.empty()) blamed_alone = k;
            else {
                g_item_state[k] = 4;
                for (Alone* u : given_up) g_item_state[u->k] = 4;
                logf("warm: the device was removed after a create alone was given up on: no item blamed");
            }
            break;
        }
        g_item_state[k] = ok >= 0 ? state_of(ok) : 0;  // counted when a worker takes it
        ++g_work;
        logf("warm: item %zu created alone: %s", k, ok == 1 ? "ok" : ok == 2 ? "skipped" : ok == 0 ? "failed" : "left");
    }
    for (Alone* u : given_up)
        if (g_item_state[u->k] == kItemInFlight) g_item_state[u->k] = 5;  // counted when a worker takes it
    if (blamed_alone != SIZE_MAX && g_fault_code) logf("warm: item %zu faulted (0x%08x) alone: later runs skip it", blamed_alone, g_fault_code.load());
    else if (blamed_alone != SIZE_MAX) logf("warm: item %zu removed the device alone: later runs skip it", blamed_alone);

    // Workers take items in file order, deferred state objects first once ready. A supervisor (this thread) abandons a worker
    // stuck in one item for over stuck_ms (2 s once stopping: left for the resume), as warm11 does: the item is skipped for this compile, a stuck
    // state object stops the ray tracing phase (rt_stop), a new worker carries on (at most 8 times, then the run ends with
    // a retry, g_hung). The stuck thread is left alone. A state object is stuck when its own work passes its
    // deadline (SoClock), and waiting on another worker's only once nothing will finish that (poisoned, or stopping).
    struct Worker12 {
        std::thread t;
        std::atomic<double> since{0};       // when its current item started; 0 = between items
        std::atomic<size_t> j{0};
        std::atomic<uint64_t> claim{1};    // the current item's number << 1, | 1 once its outcome is taken: by the worker (counted) or the supervisor (abandoned)
        uint64_t items = 0;
        std::atomic<bool> finished{false};
        SoClock so;
    };
    std::mutex tmx;
    std::vector<Worker12*> ws;
    auto take = [&](size_t& j) {
        if (g_deferred) {
            std::lock_guard l(g_defer_mx);
            if (g_rt_off) {  // poisoned: what they wait on may never finish; replay leaves them for the retry at once
                for (auto& [_, v] : g_waiting) g_ready.insert(g_ready.end(), v.begin(), v.end());
                g_waiting.clear();
            }
            if (!g_ready.empty()) return j = g_ready.front(), g_ready.pop_front(), --g_deferred, true;
        }
        return (j = next++) < n12;
    };
    auto defer = [&](size_t j) {
        if (!is_so(j, nrec) || g_rt_off || g_item_state[j] || g_crash.count(j) || g_skip.count(j) || g_known_items.count(j)) return false;
        for (;;) {
            size_t b = so_blocker(j);
            if (b == SIZE_MAX) return false;
            std::lock_guard l(g_defer_mx);
            if (g_rt_off) return false;
            if (g_so[b]->done) continue;  // done meanwhile: look again
            g_waiting[b].push_back(j), ++g_deferred;
            return true;
        }
    };
    auto spawn = [&] {
        auto* w = new Worker12;
        w->t = std::thread([&, w] {
            SetThreadPriority(GetCurrentThread(), g_prio);
            t_replay = true;
            t_so_clock = &w->so;
            for (size_t j; g_warm_faults < 3 && !g_hung;) {
                while (g_state == PAUSE) Sleep(50);
                for (int a = g_active; g_state != STOP && !g_removed && (a >= g_allowed || !g_active.compare_exchange_weak(a, a + 1)); a = g_active)
                    if (a >= g_allowed) Sleep(20);  // over the memory budget: fewer workers run
                if (g_state == STOP || g_removed) break;
                if (!take(j)) {
                    --g_active;
                    if (!g_deferred) break;
                    Sleep(20);  // deferred items wait on state objects other workers are creating
                    continue;
                }
                if (other_pass(j)) { --g_active, g_item_state[j] = 1, ++g_warm_other; continue; }
                if (defer(j)) { --g_active; continue; }
                w->j = j;
                double t = live_ms();
                w->since = t;
                const uint64_t mine = ++w->items << 1;
                w->claim = mine;
                bool crash = g_crash.count(j), alone = g_item_state[j];
                if (!crash && !alone) g_item_state[j] = kItemInFlight;
                const uint8_t was = g_item_state[j];
                int ok = crash ? 1 : alone ? (was == 1 ? 1 : was == 5 ? 2 : was == 2 ? 0 : -1) : replay_guarded(j, nrec);  // created alone: its outcome, or left for a new process
                if (g_fault_code && !crash && !alone && ok >= 0) ok = -2;  // in flight at a fault: a suspect, the next process creates it alone
                w->since = 0;
                if (uint64_t c = mine; !w->claim.compare_exchange_strong(c, mine | 1)) return;  // abandoned meanwhile: counted by the supervisor, its replacement carries on
                --g_active;
                g_item_state[j] = state_of(ok);
                if (crash) g_warm_crash++;
                else if (ok >= 0) (ok == 1 ? g_warm_ok : ok == 2 ? g_warm_skipped : g_warm_fail)++;
                if (ok == 2 && known_skip(j, nrec)) g_warm_known++;
                if (times) { std::lock_guard l(tmx); fprintf(times, "%zu,%.3f,%d\n", j, live_ms() - t, ok); }
            }
            w->finished = true;
        });
        ws.push_back(w);
    };
    pace_losses();
    for (int i = 0; i < g_threads && blamed_alone == SIZE_MAX; ++i) spawn();
    int replaced = 0;
    double pass = now_ms();
    auto stuck = [&](Worker12* w, double since, double now) {
        size_t j = w->j;
        if (w->since != since) return false;  // another item meanwhile: its j and since may not match
        if (!is_so(j, nrec)) return now - since > (g_state == STOP ? stuck_stop_ms : stuck_ms);
        auto g = w->so.seg.load();
        if (g.since > now && w->so.seg.compare_exchange_strong(g, {now, g.limit})) g.since = now;  // as started() does
        if (g_state == STOP) return now - (g.since ? g.since : since) > stuck_stop_ms;
        if (g.since) return now - g.since > g.limit;
        return g_rt_off && now - since > stuck_stop_ms;
    };
    auto supervise = [&] {
        double now = live_pass(pass);
        for (size_t i = 0; i < ws.size();) {
            Worker12* w = ws[i];
            uint64_t claim = w->claim;
            size_t creating = w->so.creating;  // before the segment stuck() judges: one it moved past meanwhile blames nothing
            double since = started(w->since, now);
            if (w->finished) {
                w->t.join();
                delete w;
                ws.erase(ws.begin() + i);
            } else if (!(claim & 1) && since && stuck(w, since, now) && w->claim.compare_exchange_strong(claim, claim | 1)) {
                size_t j = w->j;
                bool stopping = g_state == STOP;  // our stop cut the item short, no hang: the resume creates it
                // short of a poison or a stop only a driver call makes a state object stuck, also one that has just returned
                bool so = is_so(j, nrec), call = so && (w->so.seg.load().since || !(g_rt_off || stopping));
                if (call && !stopping) rt_stop("a state object create hung", j, w->so.creating == creating ? creating : kNoBlame);
                // a PSO stuck behind a poisoned driver or a removed device is its victim: retried too; otherwise it's the PSO's own hang
                bool fatal = so ? g_rt_fatal == j : !stopping && !g_rt_off && !removed();
                logf("warm: item %zu (%s) stuck for %.0f s: its worker is abandoned, the item %s", j,
                     !so ? "a PSO" : call ? "a state object" : "a state object waiting on one it builds on", (now - since) / 1000,
                     fatal ? "is skipped for this compile" : "is left for a new process");
                if (fatal) g_warm_skipped++;
                if (fatal && !so) g_hung_items.push_back(j);  // a state object's is named by rt_stop
                if (g_hung_items.size() > 8 && !g_hung.exchange(true)) logf("warm: %zu PSOs hung: a new process goes on, creating each alone first", g_hung_items.size());
                g_item_state[j] = fatal ? 5 : g_removed ? 4 : 3;
                g_abandoned12 = true;
                --g_active;     // its slot: the stuck thread never gives it back
                w->t.detach();  // w is leaked on purpose: the thread may still return into it
                ws.erase(ws.begin() + i);
                if (g_rt_off || g_removed) {}  // poisoned: no new workers into the driver; the others finish, the retry does the rest
                else if (g_state != STOP && (next < n12 || g_deferred) && ++replaced <= 8) spawn();
                else if (replaced > 8 && !g_hung.exchange(true)) logf("warm: the driver keeps hanging: a new process goes on, the hung items skipped for this compile");
            } else ++i;
        }
    };
    std::atomic<bool> d12done{false};
    std::thread t11([&] {  // D3D11 items after the PSOs: done stays "every item below it was compiled"
        while (!d12done) Sleep(20);
        // the retry (or a stopped run's resume) takes over from the first item not done (D3D11 items after it): a stop may
        // leave deferred or abandoned items below others done
        if (g_rt_off || g_removed || g_state == STOP || g_hung) {
            if (size_t k = g_faulted; k != SIZE_MAX) g_item_state[k] = 4;  // a worker the supervisor abandoned on it never writes its state
            const bool mass = g_hung && !g_rt_off && !g_removed && g_state != STOP;
            if (mass)
                for (size_t j : g_hung_items) g_item_state[j] = 4;  // not created: the retry creates them alone first
            size_t from = first;
            while (from < n12 && finished(g_item_state[from])) ++from;
            if (from < n12 || (g_mem_stop && from < n)) {  // a memory stop after the last PSO: the D3D11 items in a new process
                g_retry_failed = std::count(g_item_state.begin() + first, g_item_state.begin() + from, 2);
                g_retry_skipped = std::count(g_item_state.begin() + first, g_item_state.begin() + from, 5);
                for (size_t j = first; j < from; ++j) g_retry_known += g_item_state[j] == 5 && known_skip(j, nrec);
                g_retry_from = from;
                if (mass) g_alone_next = g_hung_items;
                else if (blamed_alone != SIZE_MAX) {
                    g_blamed = {blamed_alone};
                    for (size_t i = alone_at + 1; i < g_alone.size(); ++i)
                        if (g_alone[i] >= from && g_alone[i] < n12) g_alone_next.push_back(g_alone[i]);
                } else if (g_removed) {
                    for (size_t j = from; j < n12; ++j)
                        if (g_item_state[j] == 4) g_alone_next.push_back(j);
                    if (g_alone_next.size() == 1) g_blamed.swap(g_alone_next);
                }
                char fault[48];
                int rt_next = std::max(1, (g_rt_threads ? g_rt_threads : g_threads) / 4);
                sprintf_s(fault, " after an exception (0x%08x)", g_fault_code.load());
                logf("warm: retry from item %zu%s: %llu of %zu D3D12 items left; %zu blamed, %zu to create alone first", from,
                     g_fault_code ? fault : g_removed ? " after the device was removed" : !g_rt_off ? (g_mem_stop ? " (stopped to free memory)" : g_state == STOP ? " (stopped)" : " after repeated hangs")
                                  : g_rt_fatal != SIZE_MAX ? " (a state object skipped)"
                                  : (" with " + std::to_string(rt_next) + " ray tracing thread" + (rt_next == 1 ? "" : "s")).c_str(),
                     (unsigned long long)(n12 - from - std::count_if(g_item_state.begin() + from, g_item_state.end(), finished)),
                     n12 - first, g_blamed.size(), g_alone_next.size());
                for (size_t j : g_blamed) {
                    crash_found.insert(key(j));
                    Override o;  // a plan item's template and the shaders it replaces, by D3D12 subobject type (VS 1, PS 2, CS 6, MS 25)
                    Reader r{j < nrec ? g_recs[j].payload : g_plan[j - nrec]};
                    const Rec* t = j < nrec ? &g_recs[j] : parse_plan(r, o);
                    unsigned stages = 0;
                    for (unsigned s = 0; s < 32; ++s) stages |= o.sh[s] != kZero ? 1u << s : 0;
                    char tag = t ? t->tag : '?', plan[48] = "";
                    if (j >= nrec) sprintf_s(plan, "a plan item replacing stages 0x%x on ", stages);
                    logf("warm: item %zu blamed: %s'%c' (%s), key %s", j, plan, tag,
                         tag == 'G' ? "graphics" : tag == 'C' ? "compute" : tag == 'S' ? "pipeline stream" : tag == 'R' || tag == 'A' ? "state object" : "unknown",
                         hex(key(j)).data());
                }
                return;
            }
        }
        // a memory stop after the check above: warm11_main's workers stop at once and it sets where the new process goes on
        if (n > n12 && (g_state != STOP || g_mem_stop) && g_warm_faults < 3) warm11_main(std::max(first, n12) - n12);
        if (g_from11 != SIZE_MAX) {
            auto d12 = g_item_state.begin() + std::min(first, n12);
            g_retry_failed = std::count(d12, g_item_state.end(), 2) + g_failed11_below;
            g_retry_skipped = std::count(d12, g_item_state.end(), 5) + g_skipped11_below;
            g_retry_known += g_known11_below;
            for (size_t j = std::min(first, n12); j < n12; ++j) g_retry_known += g_item_state[j] == 5 && known_skip(j, nrec);
            g_retry_from = n12 + g_from11;
            logf("warm11: %s from item %zu", g_hung || g_mem_stop ? "a new process goes on" : "stopped: the resume goes on", n12 + g_from11);
        }
    });
    // progress every 5 s from the last 5 s's rate; a stall (nothing done for 30 s) says what is in flight, and no eta
    double last_done = double(first), last_t = t0, moved_t = t0, moved_done = double(first), peak_mb = 0, mem_released = t0;
    WarmMemory wm{double(g_mem_mb), g_threads, g_allowed, t0};
    WarmRestart wr;
    wchar_t restart_env[8] = {}, restart_at_env[24] = {};
    const bool may_restart = env(L"SCSKILLER_WARM_MEM_RESTART", restart_env) && restart_env[0] == L'1';
    // SCSKILLER_TEST_WARM_MEM_RESTART_AT=N (tests): the memory stop once this process has done N items, whatever its memory
    const uint64_t restart_at = env(L"SCSKILLER_TEST_WARM_MEM_RESTART_AT", restart_at_env) ? _wtoi64(restart_at_env) : 0;
    bool restart_said = false;
    bool was_low = false;
    double low_said = -1e9;
    for (int tick = 1;; ++tick) {
        Sleep(100);
        if (!d12done) {
            removed();
            supervise();
            if (ws.empty()) d12done = true, g_d12_over = true;
        }
        double mb = private_mb();
        peak_mb = std::max(peak_mb, mb);  // the D3D11 phase too: its workers aren't held to the budget
        MEMORYSTATUSEX sys{sizeof sys};
        const bool got = GlobalMemoryStatusEx(&sys);
        bool low = got && sys.ullAvailPhys < sys.ullTotalPhys / 8;  // the PC itself is short
        double free_mb = sys.ullAvailPhys / 1048576.0;
        if (!d12done) {  // the memory budget, every tick
            double now = now_ms();
            bool parked;
            {
                std::lock_guard l(g_parkmx);
                parked = !g_parked.empty();
            }
            // on its own thread: the gate waits for every create, and a hung one must not stop this loop's timeouts
            static std::atomic<bool> releasing;
            double held = std::max<double>(g_held_mb, wm.kept), limit = g_mem_mb + held;
            if (g_mem_mb && mb > limit && parked && now - mem_released > 1000 && !releasing.exchange(true))
                mem_released = now, std::thread([] { release_parked("over the memory budget"), releasing = false; }).detach();
            wm.allowed = g_allowed;
            char step = g_mem_mb ? warm_memory_tick(wm, mb, held, low, now) : 0;
            g_allowed = wm.allowed;
            if (step == 'd')
                logf("warm: private memory %.0f MB over the %u MB budget (+%.0f MB kept), %.0f MB of RAM free: %d workers", mb, g_mem_mb, held, free_mb,
                     wm.allowed);
            else if (step == 'u')
                logf("warm: private memory %.0f MB, under the %u MB budget (+%.0f MB kept) again, %.0f MB of RAM free: %d workers", mb, g_mem_mb, held,
                     free_mb, wm.allowed);
            else if (step == 'k')
                logf("warm: private memory %.0f MB stays over the %u MB budget with fewer workers: %.0f MB counted as kept, %.0f MB of RAM free: %d workers",
                     mb, g_mem_mb, wm.kept, free_mb, wm.allowed);
            if (g_mem_mb && low != was_low && now - low_said > 30000)  // at most every 30 s while it flickers: the state it settles in
                was_low = low, low_said = now, logf(low ? "warm: the PC is low on memory (%.0f MB of RAM free, private memory %.0f MB): no worker is added until it isn't, %d workers"
                                        : "warm: the PC has %.0f MB of RAM free again (private memory %.0f MB), %d workers",
                                    free_mb, mb, wm.allowed);
        }
        // the D3D11 phase too: a warm of D3D11 items only ends the block above on its first tick
        if (g_state == RUN && g_mem_mb && !restart_said &&
            (warm_restart_tick(wr, mb, g_start_mb, g_mem_mb, got ? sys.ullTotalPhys / 1048576.0 : 0, low, now_ms()) || (restart_at && warm_done() >= restart_at))) {
            int run = RUN;
            if (!may_restart)
                restart_said = true, logf("warm: private memory %.0f MB (%.0f MB at the start), %.0f MB of RAM free: no restart left, this process goes on", mb, g_start_mb, free_mb);
            else if (!g_rt_off && !g_removed && !g_hung && (g_mem_stop = true, g_state.compare_exchange_strong(run, STOP))) {  // set before STOP: the threads stopping read it
                restart_said = true, g_mem_stop_mb = mb, g_mem_stop_free = free_mb;
                logf("warm: private memory %.0f MB (%.0f MB at the start), %.0f MB of RAM free: stopping, a new process goes on from the first unfinished item",
                     mb, g_start_mb, free_mb);
            } else g_mem_stop = false;
        }
        if (d12done && (first + warm_done() >= n || g_warm_faults >= 3 || g_state == STOP || n == n12 || g_retry_from != SIZE_MAX)) break;
        double now = live_ms(), done = double(first + warm_done());
        if (done != moved_done) moved_done = done, moved_t = now;
        if (tick % 50) continue;
        double rate = (done - last_done) * 1000 / (now - last_t);
        last_done = done, last_t = now;
        if (g_state == PAUSE) { logf("warm: %.0f/%zu (%.1f%%) paused", done, n, 100 * done / n); continue; }
        if (now - moved_t < 30000) {
            if (rate > 0) logf("warm: %.0f/%zu (%.1f%%) %.1f PSO/s, eta %.1f min", done, n, 100 * done / n, rate, (n - done) / rate / 60);
            else logf("warm: %.0f/%zu (%.1f%%) nothing done in the last 5 s", done, n, 100 * done / n);
            continue;
        }
        size_t busy = 0, oldest = 0;
        double oldest_since = now;
        for (auto* w : ws)
            if (double since = w->since; since && since < oldest_since) oldest_since = since, oldest = w->j, ++busy;
            else if (since) ++busy;
        logf("warm: %.0f/%zu (%.1f%%) STALLED: nothing done for %.0f s, %zu items in flight%s", done, n, 100 * done / n, (now - moved_t) / 1000, busy,
             busy ? (", the oldest item " + std::to_string(oldest) + " for " + std::to_string(int((now - oldest_since) / 1000)) + " s").c_str() : "");
    }
    t11.join();
    if (times) fclose(times);
    // what this process didn't create (failed, abandoned, skipped as a crash), by record key ('P' for a plan item): the app
    // leaves them out of scskiller.warmed
    if (FILE* f = _wfopen((g_dir + L"scskiller_failed.keys").c_str(), L"wb")) {
        for (size_t j = 0; j < g_item_state.size(); ++j)
            if (g_item_state[j] == 2 || g_item_state[j] == 5 || g_crash.count(j)) fwrite(key(j).data(), 1, 20, f);
        fclose(f);
    }
    for (auto& h : crash_found) g_crash_json += (g_crash_json.empty() ? "\"" : ",\"") + std::string(hex(h).data()) + "\"";
    for (size_t j : g_alone_next) g_alone_json += (g_alone_json.empty() ? "" : ",") + std::to_string(j);
    {
        std::lock_guard l(g_culprit_mx);
        if (!g_hung) g_culprits.insert(g_culprits.end(), g_hung_items.begin(), g_hung_items.end());
        for (size_t j : g_culprits)
            if (j < n) g_culprit_json += (g_culprit_json.empty() ? "\"" : ",\"") + std::string(hex(j < n12 ? key(j) : key11(j - n12)).data()) + "\"";
    }
    if (g_warm_faults >= 3) logf("warm: stopped after repeated faults; the game is unaffected, report scskiller.log");
    if (g_state == STOP) logf("warm: stopped at item %llu of %zu", first + warm_done(), n);
    if (!g_abandoned11 && !g_abandoned12) {  // shader bytes and plan are only needed for the replay
        decltype(g_blob_bytes)().swap(g_blob_bytes);
        decltype(g_plan)().swap(g_plan);
        decltype(g_items11)().swap(g_items11);
    }
    {
        std::lock_guard l(g_failmx);
        for (auto& [k, c] : g_fail_tally) logf("warm: failed x%llu: %s", c, k.c_str());
    }
    if (g_warm_unsupported) logf("warm: not for this GPU x%llu: they blend on a render target format it can't blend", g_warm_unsupported.load());
    if (!g_abandoned12) {  // an abandoned worker may still be linking one of them; the workers are done: one thread releases
        size_t kept = 0;
        {
            std::lock_guard l(g_parkmx);
            for (auto& s : g_so)
                if (s && s->obj) g_parked.push_back(s->obj), s->obj = nullptr, ++kept;
            if (!g_parked.empty())
                logf("warm: %zu state objects alive at the end (%zu parked, %zu built on), private memory %.0f MB; %llu released in %llu batches before",
                     g_parked.size(), g_parked.size() - kept, kept, private_mb(), g_released.load(), g_release_batches.load());
        }
        if (wchar_t hang[4]; env(L"SCSKILLER_WARM_RELEASE_HANG", hang)) {
            std::lock_guard l(g_parkmx);
            g_parked.push_back((ID3D12StateObject*)(IUnknown*)&g_hang_so);
        }
        // a Release that hangs in the driver (or a batch behind one) must not keep the warm from ending: watched by its
        // progress in 100 ms turns, as a pause suspends this process; what's left goes with the process exit
        std::thread rel([] { release_parked("end of the warm"); });
        uint64_t seen = g_released;
        for (int idle = 0; WaitForSingleObject(rel.native_handle(), 100) == WAIT_TIMEOUT;)
            if (g_released != seen) seen = g_released, idle = 0;
            else if (++idle * 100.0 > (g_state == STOP ? stuck_stop_ms : stuck_ms)) break;
        if (WaitForSingleObject(rel.native_handle(), 0) == WAIT_OBJECT_0) rel.join();
        else logf("warm: releasing the state objects left made no progress for %.0f s: the rest go with the process exit", (g_state == STOP ? stuck_stop_ms : stuck_ms) / 1000), rel.detach();
    }
    drain_debug("end of warm");
    if (g_roundtrip) logf("warm: round trip: %llu same, %llu differ", g_roundtrip_ok.load(), g_roundtrip_bad.load());
    logf("warm: peak private memory %.0f MB (budget %s), %d of %d workers allowed at the end", std::max(peak_mb, private_mb()),
         g_mem_mb ? (std::to_string(g_mem_mb) + " MB").c_str() : "none", g_allowed.load(), g_threads);
    logf("warm: done ok=%llu fail=%llu skipped=%llu skipped-this-compile=%llu%s in %.1f s. Quit the game normally so the driver flushes its cache.", g_warm_ok.load(),
         g_warm_fail.load(), g_warm_crash.load(), g_warm_skipped.load(), g_pass_of.empty() ? "" : (" (pass " + std::to_string(g_pass) + ", " + std::to_string(g_warm_other.load()) + " items of other passes)").c_str(),
         (now_ms() - t0) / 1000);
    SetEvent(g_warm_done);
}

static void** g_hooked_vt;                          // the device vtable our o_* originals were taken from
static std::unordered_map<void*, ID3D12Device*> g_lib_dev;  // pipeline library -> the (hooked) device that opened it

static ID3D12Device* unwrapped(IUnknown* unk);

static std::once_flag g_warm_once;

static void on_first_pso(ID3D12Device* dev) {
    // Only warm on a device whose vtable we hooked: o_* belong to that class. A layer like ReShade wraps the device,
    // and e.g. ID3D12PipelineLibrary::GetDevice returns the unwrapped one; calling ReShade's methods on it crashes.
    if (!g_warm || !dev || *(void***)dev != g_hooked_vt) return;
    std::call_once(g_warm_once, [dev] {
        if (!g_warm) return;
        g_warm_dev = dev, dev->AddRef();  // the game's real device: the first one that builds a PSO
        g_warm_luid = dev->GetAdapterLuid();
        dev->QueryInterface(IID_PPV_ARGS(&g_warm_dev2));
        if ((g_warm_real = unwrapped(dev))) {
            g_warm_real->QueryInterface(IID_PPV_ARGS(&g_warm_real2));
            logf("warm: under a layer; %zu pipelines it made are created on the device under it", g_layer_made.size());
            HMODULE lm;
            if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)*(void**)dev, &lm)) {
                auto nt = (const IMAGE_NT_HEADERS*)((const BYTE*)lm + ((const IMAGE_DOS_HEADER*)lm)->e_lfanew);
                g_layer_lo = (uintptr_t)lm, g_layer_hi = g_layer_lo + nt->OptionalHeader.SizeOfImage;
            }
        }
        dev->QueryInterface(IID_PPV_ARGS(&g_warm_dev5)), dev->QueryInterface(IID_PPV_ARGS(&g_warm_dev7));  // ray tracing (null: no DXR runtime)
        if (g_debug12 && FAILED(dev->QueryInterface(IID_PPV_ARGS(&g_iq)))) logf("d3d12 debug: the device has no info queue (debug layer not active)");
        std::thread(warm_main).detach();
    });
}

static thread_local bool t_in;  // guards against the runtime routing one create through another
static thread_local double t_below_ms;  // the below-wrapper hook's own time inside the current create (a chained mod's)

static HRESULT remember_rs(HRESULT hr, const void* blob, SIZE_T n, void** pp, std::unordered_map<void*, Hash>& of = g_rs_of) {
    if (SUCCEEDED(hr) && pp && *pp) {
        Hash h = sha1(blob, n);
        std::lock_guard l(g_mx);
        of[*pp] = h;
        if (g_db && !(g_db_capped && g_db_bytes >= g_db_cap) && !g_blobs_on_disk.count(h) && !imported(h)) g_rs_bytes.try_emplace(h, (const char*)blob, n);
    }
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_rs(ID3D12Device* dev, UINT mask, const void* blob, SIZE_T n, REFIID riid, void** pp) {
    return remember_rs(o_rs(dev, mask, blob, n, riid, pp), blob, n, pp);
}

static thread_local Hash t_asked;  // the key of the game's create in flight on this thread (zero: none, or not recordable)

// Never recorded: a warm would plan it and fail again. A game may retry the same one every frame: the first distinct
// (key, hr) pairs are logged, the rest only counted. Not under g_mx, and nothing allocated but the log line.
static void create_failed(const Writer& w, const Hash& k, HRESULT hr) {
    ++g_create_fails;
    static std::atomic<bool> full;
    if (full) return;
    static std::mutex& mx = *new std::mutex;
    static std::pair<Hash, HRESULT> seen[20];
    static size_t n;
    bool last;
    {
        std::lock_guard l(mx);
        if (n == std::size(seen) || std::find(seen, seen + n, std::pair{k, hr}) != seen + n) return;
        seen[n++] = {k, hr};
        if ((last = n == std::size(seen))) full = true;
    }
    if (w.ok) logf("create failed ('%c') hr=0x%08x key %s", w.tag, (unsigned)hr, hex(k).data());
    else logf("create failed ('%c') hr=0x%08x, not recordable: %s", w.tag, (unsigned)hr, w.why);
    if (last) logf("create failed: 20 distinct logged, further ones are only counted");
}

static std::wstring cfg(const wchar_t* var, const wchar_t* key, const wchar_t* def);
template <class T> static void patch(void** vt, int slot, void* hook, T& orig);

// Re-create dedupe: a create identical to one made before on the same device, of a pipeline nothing but us holds any more,
// gets that object back (QueryInterface, so AddRef'd and in the interface asked for) with no driver call. On NVIDIA from
// 621 such re-creates stall together, all creating threads for ~50 ms every ~2 s in a game that re-creates 40% of its
// PSOs. The key is the record key plus what the record leaves out: the device, the root signature object, the NVAPI state
// and the CachedPSO blob. Never with a layer under our hooks (a mod's d3d12.dll, a wrapping layer): a hit would skip it.
// SCSKILLER_DEDUPE / ini dedupe=: 0 off, 1 on, unset: on for NVIDIA. SCSKILLER_DEDUPE_MAX: objects kept (default 4096).
// Private data other than a name on a kept object turns it off for the process: a layer's destruction callback
// (ReShade's SetPrivateDataInterface) would fire while the object lives on, or reach the next holder.
struct Dd { ID3D12PipelineState* o; std::list<Hash>::iterator at; };
static std::mutex g_ddmx;  // map and LRU only: nothing under it calls out but D3D12Core's own AddRef
static std::unordered_map<Hash, Dd, HashH> g_dd;
static std::unordered_map<void*, Hash> g_dd_obj;  // kept object -> its key
static std::list<Hash> g_ddlru;                   // front: least recently asked for
static std::atomic<int> g_dd_mode{2};             // 0 off, 1 on, -1 NVIDIA only, 2 not read yet
static std::atomic<bool> g_dd_layer;              // a layer's device is under our hooks (hook_below)
static size_t g_dd_cap = 4096;
static void** g_pso_vt;  // the PSO vtable whose private-data methods are hooked: only its objects are kept

using PFN_SetPD = HRESULT(STDMETHODCALLTYPE*)(ID3D12Object*, REFGUID, UINT, const void*);
using PFN_SetPDI = HRESULT(STDMETHODCALLTYPE*)(ID3D12Object*, REFGUID, const IUnknown*);
enum { OBJ_SETPD = 4, OBJ_SETPDI = 5 };  // ID3D12Object, vtslots.cpp checks these
static PFN_SetPD o_setpd;
static PFN_SetPDI o_setpdi;

static void dd_drop(std::unordered_map<Hash, Dd, HashH>::iterator it, std::vector<ID3D12PipelineState*>& out) {
    out.push_back(it->second.o), g_dd_obj.erase(it->second.o), g_ddlru.erase(it->second.at), g_dd.erase(it);
}
static void dd_flush() {
    std::vector<ID3D12PipelineState*> v;
    {
        std::lock_guard l(g_ddmx);
        while (!g_dd.empty()) dd_drop(g_dd.begin(), v);
    }
    for (auto* o : v) o->Release();  // outside the lock: a PSO's teardown is the driver's
}
static void dd_private(void* o, REFGUID g) {
    if (g == WKPDID_D3DDebugObjectName || g == WKPDID_D3DDebugObjectNameW || g_dd_mode == 0) return;
    {
        std::lock_guard l(g_ddmx);
        if (!g_dd_obj.count(o)) return;
    }
    if (g_dd_mode.exchange(0) != 0) logf("dedupe: off, the game or a layer gave a pipeline private data");
    dd_flush();
}
static HRESULT STDMETHODCALLTYPE hk_setpd(ID3D12Object* o, REFGUID g, UINT n, const void* p) { return dd_private(o, g), o_setpd(o, g, n, p); }
static HRESULT STDMETHODCALLTYPE hk_setpdi(ID3D12Object* o, REFGUID g, const IUnknown* p) { return dd_private(o, g), o_setpdi(o, g, p); }

static bool dd_on() {
    static std::once_flag once;
    std::call_once(once, [] {
        std::wstring v = cfg(L"SCSKILLER_DEDUPE", L"dedupe", L"");
        // a selftest host measures the driver's own re-creates: only when asked
        bool host = GetProcAddress(GetModuleHandleW(nullptr), "SCSKiller_WarmHost") != nullptr;
        int m = v == L"0" ? 0 : v == L"1" ? 1 : host ? 0 : -1;
        if (wchar_t c[16]; env(L"SCSKILLER_DEDUPE_MAX", c) && _wtoi(c) >= 0) g_dd_cap = _wtoi(c);
        int was = 2;
        g_dd_mode.compare_exchange_strong(was, m);
        logf("dedupe: %s, up to %zu kept", g_next || g_dd_layer ? "off (a layer is under the hooks)" : m == 0 ? "off" : m == 1 ? "on"
                                           : g_ov_nvidia ? "on (NVIDIA)" : "off (not NVIDIA)", g_dd_cap);
    });
    int m = g_dd_mode;
    return !g_next && !g_dd_layer && (m > 0 || (m < 0 && g_ov_nvidia));
}

static thread_local int t_dd;  // this create: 1 a dedupe hit, 2 its kept pipeline was still held
template <class F> static HRESULT dd_create(ID3D12Device* dev, const Writer& w, const Hash& rk, REFIID riid, void** pp, F& call) {
    if (rk == kZero || w.no_dedupe || !dd_on()) return call();
    const Hash blob = w.cached.pCachedBlob && w.cached.CachedBlobSizeInBytes ? sha1(w.cached.pCachedBlob, w.cached.CachedBlobSizeInBytes) : kZero;
    char b[20 + 2 * sizeof(void*) + sizeof(NvExt) + 4 + 20];
    char* e = b;
    for (auto [p, n] : {std::pair<const void*, size_t>{rk.data(), 20}, {&dev, sizeof dev}, {&w.rsp, sizeof w.rsp}, {&w.nv, sizeof w.nv},
                        {&w.rast, 4}, {blob.data(), 20}})
        memcpy(e, p, n), e += n;
    const Hash k = sha1(b, e - b);
    ID3D12PipelineState* pin = nullptr;
    bool free = false;
    t_dd = 0;
    {
        std::lock_guard l(g_ddmx);
        if (auto it = g_dd.find(k); it != g_dd.end()) {
            pin = it->second.o;
            // ours + this pin: no holder left, so it's never shared with one. Claimed here: a second hit counts this pin.
            free = pin->AddRef() == 2;
            g_ddlru.splice(g_ddlru.end(), g_ddlru, it->second.at);
        }
    }
    if (pin) {
        // a removed device's create must fail as the driver's would: the game's recovery starts there
        HRESULT hr = free && dev->GetDeviceRemovedReason() == S_OK ? pin->QueryInterface(riid, pp) : E_NOINTERFACE;
        pin->Release();
        if (SUCCEEDED(hr)) return t_dd = 1, ++g_dd_hits, hr;
        if (!free) t_dd = 2, ++g_dd_held;
    }
    HRESULT hr = call();
    ID3D12PipelineState* o = nullptr;
    if (FAILED(hr) || !*pp || FAILED(((IUnknown*)*pp)->QueryInterface(IID_PPV_ARGS(&o)))) return hr;
    std::vector<ID3D12PipelineState*> drop;
    {
        std::lock_guard l(g_ddmx);
        if (!g_pso_vt) {
            // only D3D12Core's own pipelines: one count for the whole object, which the claim's AddRef reads
            void** vt = *(void***)o;
            HMODULE m = nullptr;
            wchar_t name[MAX_PATH] = {};
            GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)vt, &m);
            if (m) GetModuleFileNameW(m, name, MAX_PATH);
            const wchar_t* base = wcsrchr(name, L'\\');
            if (base && !_wcsicmp(base + 1, L"D3D12Core.dll")) patch(vt, OBJ_SETPD, (void*)hk_setpd, o_setpd), patch(vt, OBJ_SETPDI, (void*)hk_setpdi, o_setpdi);
            if (vt[OBJ_SETPD] == (void*)hk_setpd && vt[OBJ_SETPDI] == (void*)hk_setpdi) g_pso_vt = vt;
            else g_dd_mode = 0, logf("dedupe: off, the pipelines aren't D3D12Core's own or their private data methods couldn't be hooked");
        }
        // a holder still has the kept one, or another thread made the same at once: the first stays
        if (!g_pso_vt || *(void***)o != g_pso_vt || g_dd_mode == 0 || g_dd.count(k)) drop.push_back(o);
        else g_dd[k] = {o, g_ddlru.insert(g_ddlru.end(), k)}, g_dd_obj[o] = k;
        while (g_dd.size() > g_dd_cap) dd_drop(g_dd.find(g_ddlru.front()), drop);
    }
    for (auto* x : drop) x->Release();
    return hr;
}

template <class F> static HRESULT timed_create(ID3D12Device* dev, void** pp, Writer& w, F&& call, bool lib = false, const IID* riid = nullptr) {
    if (t_in || !pp) return call();
    t_in = true;
    on_first_pso(dev);
    t_below_ms = 0;
    t_asked = w.ok && !w.s.empty() ? key_of(w.tag, w.s) : kZero;
    auto t0 = std::chrono::steady_clock::now();
    // tests: a create that takes this long, as a cold compile does on a GPU (WARP compiles in about a millisecond)
    static const bool host = GetProcAddress(GetModuleHandleW(nullptr), "SCSKiller_WarmHost") != nullptr;
    if (wchar_t v[16]; host && env(L"SCSKILLER_TEST_CREATE_MS", v)) Sleep(_wtoi(v));
    t_dd = 0;
    HRESULT hr = riid ? dd_create(dev, w, t_asked, *riid, pp, call) : call();
    const Hash k = t_asked;
    t_asked = kZero;
    // a chained mod's create reaches hk_*_below, whose recording is ours, not the driver's: proxy_ms, not ms
    double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count() - t_below_ms;
    if (ms >= 10 && t_dd) ++(t_dd == 1 ? g_dd_hits_slow : g_dd_held_slow);
    if (SUCCEEDED(hr)) note(w, ms, lib, std::chrono::duration<double, std::milli>(t0 - w.t0).count() + t_below_ms);
    else if (!lib) create_failed(w, k, hr);  // a failed library load is a miss: the game creates it next
    t_in = false;
    return hr;
}

static HRESULT STDMETHODCALLTYPE hk_gfx(ID3D12Device* dev, const D3D12_GRAPHICS_PIPELINE_STATE_DESC* d, REFIID riid, void** pp) {
    Writer w{'G'};
    if (!t_in && pp) io_gfx(w, const_cast<D3D12_GRAPHICS_PIPELINE_STATE_DESC&>(*d)), w.cached_pso(d->CachedPSO);
    return timed_create(dev, pp, w, [&] { return o_gfx(dev, d, riid, pp); }, false, &riid);
}
static HRESULT STDMETHODCALLTYPE hk_cs(ID3D12Device* dev, const D3D12_COMPUTE_PIPELINE_STATE_DESC* d, REFIID riid, void** pp) {
    Writer w{'C'};
    if (!t_in && pp) io_cs(w, const_cast<D3D12_COMPUTE_PIPELINE_STATE_DESC&>(*d)), w.cached_pso(d->CachedPSO);
    return timed_create(dev, pp, w, [&] { return o_cs(dev, d, riid, pp); }, false, &riid);
}
static HRESULT STDMETHODCALLTYPE hk_stream(ID3D12Device2* dev, const D3D12_PIPELINE_STATE_STREAM_DESC* d, REFIID riid, void** pp) {
    Writer w{'S'};
    if (!t_in && pp) write_stream(w, *d);
    return timed_create(dev, pp, w, [&] { return o_stream(dev, d, riid, pp); }, false, &riid);
}

// A created state object's identity -> its record key, so records that build on it (links, additions) can name it.
static void remember_so(const Writer& w, HRESULT hr, void** pp) {
    if (FAILED(hr) || !pp || !*pp) return;
    if (auto* so = so_id((IUnknown*)*pp)) {
        std::lock_guard l(g_mx);
        if (w.ok && !w.s.empty()) g_so_key[so] = key_of(w.tag, w.s);
        else g_so_key.erase(so);  // a released object's key at this address would name the wrong base
    }
}
static HRESULT STDMETHODCALLTYPE hk_cso(ID3D12Device5* dev, const D3D12_STATE_OBJECT_DESC* d, REFIID riid, void** pp) {
    Writer w{'R'};
    if (!t_in && pp && d) write_so(w, *d);
    HRESULT hr = timed_create(dev, pp, w, [&] { return o_cso(dev, d, riid, pp); });
    remember_so(w, hr, pp);
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_addso(ID3D12Device7* dev, const D3D12_STATE_OBJECT_DESC* d, ID3D12StateObject* base, REFIID riid, void** pp) {
    Writer w{'A'};
    if (!t_in && pp && d) w.so_ref(base), write_so(w, *d);
    HRESULT hr = timed_create(dev, pp, w, [&] { return o_addso(dev, d, base, riid, pp); });
    remember_so(w, hr, pp);
    return hr;
}

template <class F> static HRESULT timed_load(ID3D12PipelineLibrary* lib, void** pp, Writer& w, F&& call) {
    ID3D12Device* dev;
    {
        std::lock_guard l(g_mx);
        auto it = g_lib_dev.find(lib);
        dev = it == g_lib_dev.end() ? nullptr : it->second;
    }
    return timed_create(dev, pp, w, call, true);
}
static HRESULT STDMETHODCALLTYPE hk_loadgfx(ID3D12PipelineLibrary* lib, LPCWSTR name, const D3D12_GRAPHICS_PIPELINE_STATE_DESC* d, REFIID riid, void** pp) {
    Writer w{'G'};
    if (!t_in && pp) io_gfx(w, const_cast<D3D12_GRAPHICS_PIPELINE_STATE_DESC&>(*d));
    return timed_load(lib, pp, w, [&] { return o_loadgfx(lib, name, d, riid, pp); });
}
static HRESULT STDMETHODCALLTYPE hk_loadcs(ID3D12PipelineLibrary* lib, LPCWSTR name, const D3D12_COMPUTE_PIPELINE_STATE_DESC* d, REFIID riid, void** pp) {
    Writer w{'C'};
    if (!t_in && pp) io_cs(w, const_cast<D3D12_COMPUTE_PIPELINE_STATE_DESC&>(*d));
    return timed_load(lib, pp, w, [&] { return o_loadcs(lib, name, d, riid, pp); });
}
static HRESULT STDMETHODCALLTYPE hk_loadstream(ID3D12PipelineLibrary1* lib, LPCWSTR name, const D3D12_PIPELINE_STATE_STREAM_DESC* d, REFIID riid, void** pp) {
    Writer w{'S'};
    if (!t_in && pp) write_stream(w, *d);
    return timed_load(lib, pp, w, [&] { return o_loadstream(lib, name, d, riid, pp); });
}

template <class T> static void patch(void** vt, int slot, void* hook, T& orig) {
    if (vt[slot] == hook) return;
    if (orig && (void*)orig != vt[slot]) { logf("hook: slot %d has a different original on another vtable, skipped", slot); return; }
    orig = (T)vt[slot];
    DWORD old;
    VirtualProtect(&vt[slot], sizeof(void*), PAGE_READWRITE, &old);
    vt[slot] = hook;
    VirtualProtect(&vt[slot], sizeof(void*), old, &old);
}

static HRESULT STDMETHODCALLTYPE hk_createlib(ID3D12Device1* dev, const void* blob, SIZE_T n, REFIID riid, void** pp) {
    HRESULT hr = o_createlib(dev, blob, n, riid, pp);
    logf("hook: game opened a pipeline library (%zu bytes) hr=0x%08x", (size_t)n, (unsigned)hr);
    ID3D12PipelineLibrary* lib;
    if (SUCCEEDED(hr) && pp && *pp && SUCCEEDED(((IUnknown*)*pp)->QueryInterface(IID_PPV_ARGS(&lib)))) {
        std::lock_guard l(g_mx);
        g_lib_dev[lib] = (ID3D12Device*)dev;  // same object: ID3D12Device1 derives from ID3D12Device
        void** vt = *(void***)lib;
        patch(vt, LIB_LOADGFX, (void*)hk_loadgfx, o_loadgfx);
        patch(vt, LIB_LOADCS, (void*)hk_loadcs, o_loadcs);
        ID3D12PipelineLibrary1* l1;
        if (SUCCEEDED(lib->QueryInterface(IID_PPV_ARGS(&l1)))) patch(*(void***)l1, LIB_LOADSTREAM, (void*)hk_loadstream, o_loadstream), l1->Release();
        lib->Release();
    }
    return hr;
}

// At the first hooked device: the db and the csv's session marker.
static void open_session() {
    static std::once_flag once;
    std::call_once(once, [] {
        if (!g_frames_only) load_db(g_warm);  // load_db creates scskiller.db
        g_csv = _wfopen((g_dir + L"scskiller_creates.csv").c_str(), L"a");
        std::lock_guard l(g_mx);
        if (LARGE_INTEGER size; g_db && GetFileSizeEx(g_dbo.h = (HANDLE)_get_osfhandle(_fileno(g_db)), &size)) g_dbo.at = size.QuadPart;
        else g_dbo.h = nullptr, g_db = nullptr;
        if (g_csv) g_csvo.h = (HANDLE)_get_osfhandle(_fileno(g_csv));
        if (g_csv && !g_staged) {  // a staged scskiller_warm run is a warm-up, not a play session: no marker
            g_session_unix = unix_ms();
            double t = now_ms();  // #clock: the stamp on the t_ms clock, which starts when the recorder loads
            csv_line("#session,%lld,%ls\n#clock,%.1f\n%s", g_session_unix, exe_name().c_str(), t, g_api11 ? "#api,d3d11\n" : "");
            g_wrote_session = true;
        }
        if (g_csv || g_db) try {
            std::thread(flusher).detach();
            g_flushing = true;
        } catch (...) {
            logf("db: no writer thread: the db and the csv are written when their buffers fill and at exit");
        }
    });
}

#include "traversal.h"

static void install_hooks(IUnknown* unk) {
    ID3D12Device* dev;
    if (FAILED(unk->QueryInterface(IID_PPV_ARGS(&dev)))) return;
    open_session();
    std::lock_guard l(g_mx);
    void** vt = *(void***)dev;
    if (!g_hooked_vt) g_hooked_vt = vt;
    logf("hook: device %p vtable %p%s", (void*)dev, (void*)vt, vt[SLOT_GFX] == (void*)hk_gfx ? " (already hooked)" : "");
    patch(vt, SLOT_GFX, (void*)hk_gfx, o_gfx);
    patch(vt, SLOT_CS, (void*)hk_cs, o_cs);
    patch(vt, SLOT_RS, (void*)hk_rs, o_rs);
    ID3D12Device2* d2;
    if (SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&d2)))) patch(*(void***)d2, SLOT_STREAM, (void*)hk_stream, o_stream), d2->Release();
    ID3D12Device1* d1;
    if (SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&d1)))) patch(*(void***)d1, SLOT_CREATELIB, (void*)hk_createlib, o_createlib), d1->Release();
    ID3D12Device5* d5;  // ray tracing: absent on a runtime / driver without DXR
    if (SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&d5)))) patch(*(void***)d5, SLOT_CSO, (void*)hk_cso, o_cso), d5->Release();
    ID3D12Device7* d7;
    if (SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&d7)))) patch(*(void***)d7, SLOT_ADDSO, (void*)hk_addso, o_addso), d7->Release();
    if (!g_warm && !g_staged) trav::install(dev);
    dev->Release();
}

// NVAPI hooks: a game gets NVAPI's functions from nvapi64.dll's nvapi_QueryInterface, so each one is patched at its entry
// (a jmp to a relay near it, the moved first instruction in a trampoline). Only the first instructions nvapi64.dll's functions
// use are moved (push, and mov / lea / sub / xor / add / test with a ModRM, no rip-relative operand); a function starting
// otherwise is left alone and logged.
static size_t insn_len(const uint8_t* p) {
    size_t n = (p[0] & 0xF0) == 0x40;  // REX
    uint8_t op = p[n++];
    if (op >= 0x50 && op <= 0x5F) return n;
    size_t imm = op == 0x83 ? 1 : op == 0x81 ? 4 : 0;
    if (!imm && op != 0x89 && op != 0x8B && op != 0x8D && op != 0x33 && op != 0x2B && op != 0x03 && op != 0x85) return 0;
    uint8_t modrm = p[n++], mod = modrm >> 6, rm = modrm & 7;
    if (mod != 3 && rm == 4) {
        if (mod == 0 && (p[n] & 7) == 5) n += 4;
        ++n;
    } else if (mod == 0 && rm == 5) return 0;
    return n + (mod == 1 ? 1 : mod == 2 ? 4 : 0) + imm;
}

// Every inline hook (NVAPI, Aftermath) and the relay pages they share go through this lock: two device-creating threads
// may install at once. Recursive: an installer holds it across its own hook_fn calls.
static std::recursive_mutex g_hook_mx;

// What hook_fn wrote, so an inline hook can be taken out (the AGS one, on refusal or a late anti-cheat dll): the entry and,
// for a hot patch, the padding, each with the bytes expected and written. t null = not hooked.
struct HookPatch {
    std::atomic<uint8_t*> t{nullptr};
    uint8_t *lo, *hi;        // the exact range hook_fn made writable, so unhook restores one protection over it
    uint64_t was, now;       // the entry's 8 bytes before and after; the padding jmp (hot patch) is left in place
};

// Reads n bytes only when every page of [p, p+n) is committed, executable and not a guard page, under SEH so a race that
// changes it mid-read fails rather than faults. For hook_ags's thunk and prologue reads (a RVA inside SizeOfImage can be
// an uncommitted or guard page); the shared hook_fn read is games-orch's one aligned snapshot.
static bool mem_exec_readable(const uint8_t* p, size_t n) {
    MEMORY_BASIC_INFORMATION mi;
    for (const uint8_t* q = p; q < p + n; q = (const uint8_t*)mi.BaseAddress + mi.RegionSize)
        if (!VirtualQuery(q, &mi, sizeof mi) || mi.State != MEM_COMMIT || (mi.Protect & PAGE_GUARD)
            || !(mi.Protect & (PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY))) return false;
    return true;
}
static bool safe_code_read(void* dst, const uint8_t* src, size_t n) {
    if (!mem_exec_readable(src, n)) return false;
    __try { memcpy(dst, src, n); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
// A single aligned 8-byte load under SEH, in its own function (hook_fn has C++ objects, so it can't hold a __try). A stub
// at a page end that becomes unreadable between the VirtualQuery and the read must fail, not fault.
static bool read8(const void* p, uint64_t& out) {
    __try { out = *(volatile uint64_t*)p; return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

// An 8-byte pointer slot in another hooker's memory: committed, readable and no guard page at both ends, one load under SEH.
static bool data_read8(const uint8_t* p, uint64_t& out) {
    MEMORY_BASIC_INFORMATION mi;
    for (const uint8_t* q : {p, p + 7})
        if (!VirtualQuery(q, &mi, sizeof mi) || mi.State != MEM_COMMIT || (mi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) ||
            !(mi.Protect & (PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)))
            return false;
    return read8(p, out);
}
// The relay an entry's jmp rel32 leads to, from bytes read at t (a private page near the entry); null for any other entry.
static uint8_t* jmp_relay(const uint8_t* b, uint8_t* t) {
    int32_t r;
    if (b[0] != 0xE9) return nullptr;
    memcpy(&r, b + 1, 4);
    return t + 5 + r;
}
// Where an entry starting with a jmp rel32 (another overlay's hook) goes: one hop through the hooker's relay if it is
// jmp [rip+disp] (Ubisoft Connect's overlay: disp 0), mov rax or r11 then jmp to it, push/mov/ret, or another jmp rel32;
// any other relay is returned itself (outside a module, so a chain through it is refused). Null for an entry that isn't a
// jmp rel32.
static uint8_t* jmp_target(const uint8_t* b, uint8_t* t) {
    uint8_t *d = jmp_relay(b, t), x[16];
    if (!d) return nullptr;
    auto hop = [&]() -> uint64_t {
        int got = 0;
        for (int n : {16, 14, 13, 12, 6, 5})
            if (safe_code_read(x, d, n)) { got = n; break; }
        int32_t r;
        uint64_t a = 0;
        if (got >= 6 && x[0] == 0xFF && x[1] == 0x25) {
            if (memcpy(&r, x + 2, 4), !data_read8(d + 6 + r, a)) a = 0;
        } else if (got >= 12 && x[0] == 0x48 && x[1] == 0xB8 && x[10] == 0xFF && x[11] == 0xE0) memcpy(&a, x + 2, 8);
        else if (got >= 13 && x[0] == 0x49 && x[1] == 0xBB && x[10] == 0x41 && x[11] == 0xFF && x[12] == 0xE3) memcpy(&a, x + 2, 8);
        else if (got >= 5 && x[0] == 0xE9) memcpy(&r, x + 1, 4), a = (uint64_t)(d + 5 + r);
        // push lo32, mov dword [rsp+4], hi32, ret
        else if (got >= 14 && x[0] == 0x68 && x[5] == 0xC7 && x[6] == 0x44 && x[7] == 0x24 && x[8] == 0x04 && x[13] == 0xC3) {
            uint32_t lo, hi;
            memcpy(&lo, x + 1, 4), memcpy(&hi, x + 9, 4), a = lo | (uint64_t)hi << 32;
        }
        return a;
    };
    // decoded twice the same (a relay or slot rewritten meanwhile can be read torn), to committed executable memory
    const uint64_t a = hop();
    return a && a == hop() && mem_exec_readable((const uint8_t*)a, 1) ? (uint8_t*)a : d;
}
// keep: the trampoline runs whole instructions up to at least that many bytes before it jumps back, so a hooker that later
// patches that much of the entry (a 12- or 14-byte absolute jmp) doesn't cut into where it resumes. Only with a first
// instruction of 5 bytes or more; one it can't move refuses the hook.
static void* hook_fn(void* target, void* hook, void** orig, bool idle = false, HookPatch* rec = nullptr, int max_tries = 0, size_t keep = 0, bool chain = false) {
    std::lock_guard l(g_hook_mx);
    static uint8_t *page, *end;
    auto t = (uint8_t*)target;
    // Only the first instruction is replaced, in one aligned 8-byte compare-exchange: a thread already past it runs the rest
    // unchanged, and bytes another hooker changed since they were read fail the exchange. A 2- to 4-byte first instruction gets
    // a 2-byte jmp to a 5-byte one in the hot-patch padding right before the entry (t-5..t-1 int3, after an int3 or a ret):
    // int3 bytes further away may sit inside a live instruction's immediate. A 1-byte one, or any other entry, isn't hooked.
    // One read each of the entry and its padding: the decode, the trampoline and both exchanges see the same bytes.
    if ((uintptr_t)t & 7) return nullptr;
    uint64_t was, pad_was = 0, pad_now = 0;
    if (!read8(t, was)) return nullptr;   // the one snapshot: decode, trampoline and both CASes use it, no second read
    uint64_t now = was;
    // chain: an entry another overlay already jumps away from is hooked in front of that jmp, its target (in a module) the
    // original. That module is pinned before our jmp can lead into it, so it can't unload under it; kept pinned if the hook fails.
    uint8_t* via = chain ? jmp_target((const uint8_t*)&was, t) : nullptr;
    HMODULE pinned;
    if (via && !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, (LPCWSTR)via, &pinned)) return nullptr;
    size_t n = via ? 5 : insn_len((const uint8_t*)&was);
    if (n < 2 || n > 8) return nullptr;
#ifdef SCSK_TEST_HOOKS
    // another hooker writes here: only in a process exporting SCSKiller_WarmHost (selftest), never a game
    if (wchar_t race[20]; GetProcAddress(GetModuleHandleW(nullptr), "SCSKiller_WarmHost") && env(L"SCSKILLER_TEST_HOOK_RACE", race))
        ((void (*)(void*))_wcstoui64(race, nullptr, 16))(t);
#endif
    if (idle && n < 5) {
        // AGS-specific (idle = the module isn't initialized, no thread runs the entry): a first instruction too short for
        // the 5-byte jmp gets whole push/pop reg instructions (<=8 bytes, never rip-relative) relocated with it instead of a
        // hot patch. One already 5+ bytes takes the normal non-hot path. Decoded from the one snapshot (was).
        auto s = (const uint8_t*)&was;
        n = 0;
        while (n < 5) {
            size_t k = n + (s[n] >= 0x40 && s[n] <= 0x4F);   // REX prefix
            if (k < 8 && s[k] >= 0x50 && s[k] <= 0x5F) n = k + 1;
            else return nullptr;
        }
    }
    bool hot = n < 5;
    uint8_t moved[32];
    size_t k = via ? 0 : n;
    if (keep && !via) {
        if (hot || keep > 16 || !safe_code_read(moved, t, sizeof moved)) return nullptr;
        for (size_t m; k < keep; k += m)
            if (!(m = insn_len(moved + k))) return nullptr;
        memcpy(moved, &was, 8);  // the snapshot's bytes: the exchange checks those
    }
    // an entry can start a page: the padding page must then be code of the same allocation, protected as the entry's page is
    // (one VirtualProtect spans both and restores one protection), and no guard page
    MEMORY_BASIC_INFORMATION mp, mt;
    bool code_before = ((uintptr_t)t & 0xFFF) >= 8 ||
                       (VirtualQuery(t - 8, &mp, sizeof mp) && VirtualQuery(t, &mt, sizeof mt) && mp.State == MEM_COMMIT &&
                        mp.AllocationBase == mt.AllocationBase && mp.Protect == mt.Protect && !(mp.Protect & PAGE_GUARD) &&
                        (mp.Protect & (PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)));
    if (hot && code_before && !read8(t - 8, pad_was)) return nullptr;
    auto pad = (const uint8_t*)&pad_was;
    if (hot && (!code_before || (pad[2] != 0xCC && pad[2] != 0xC3) || !std::all_of(pad + 3, pad + 8, [](uint8_t c) { return c == 0xCC; })))
        return nullptr;
    uint8_t* jmp_at = hot ? t - 5 : t;
    uint8_t *lo = hot ? t - 8 : t, *hi = t + 8;
    auto near_t = [&](const uint8_t* q) { long long d = q - (jmp_at + 5); return d > -0x7FF00000ll && d < 0x7FF00000ll; };
    if (!page || end - page < 64 || !near_t(page)) {  // relays and trampolines within a rel32 jmp of the target (another module's may be far)
        SYSTEM_INFO si;
        GetSystemInfo(&si);
        uintptr_t g = si.dwAllocationGranularity, base = (uintptr_t)t & ~(g - 1);
        page = nullptr;
        int tries = 0;   // max_tries>0 (the AGS path) caps the search so it can't make tens of thousands of VirtualAlloc calls under the loader lock
        for (uintptr_t d = g; !page && d < 0x7FF00000 && (!max_tries || tries < max_tries); d += g)
            for (uintptr_t a : {base - d, base + d}) {
                if (max_tries && tries++ >= max_tries) break;
                if (!page && (page = (uint8_t*)VirtualAlloc((void*)a, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE))) end = page + 4096;
            }
        if (!page) return nullptr;
    }
    auto abs_jmp = [](uint8_t* at, const void* to) { at[0] = 0xFF, at[1] = 0x25, memset(at + 2, 0, 4), memcpy(at + 6, &to, 8); };
    uint8_t *relay = page, *tramp = page + 14;
    long long rel = relay - (jmp_at + 5);
    if (rel != (int32_t)rel) return nullptr;
    int32_t r32 = (int32_t)rel;
    auto b = (uint8_t*)&now;
    if (hot) {
        pad_now = pad_was;
        auto pb = (uint8_t*)&pad_now;
        pb[3] = 0xE9, memcpy(pb + 4, &r32, 4);
        b[0] = 0xEB, b[1] = (uint8_t)(jmp_at - (t + 2));
    } else {
        b[0] = 0xE9, memcpy(b + 1, &r32, 4);
    }
    abs_jmp(relay, hook);
    memcpy(tramp, keep ? moved : (const uint8_t*)&was, k), abs_jmp(tramp + k, via ? via : t + k);
    FlushInstructionCache(GetCurrentProcess(), relay, tramp + k + 14 - relay);
    DWORD old;
    if (!VirtualProtect(lo, hi - lo, PAGE_EXECUTE_READWRITE, &old)) return nullptr;
    // the padding's jmp is in place and flushed before anything jumps to it
    bool ok = !hot || (uint64_t)InterlockedCompareExchange64((volatile LONG64*)(t - 8), (LONG64)pad_now, (LONG64)pad_was) == pad_was;
    void* prev = *orig;
    bool spent = false;
    if (ok) {
        FlushInstructionCache(GetCurrentProcess(), lo, hi - lo);
        *orig = tramp;  // before the jmp: a call through it may run at once
        ok = (uint64_t)InterlockedCompareExchange64((volatile LONG64*)t, (LONG64)now, (LONG64)was) == was;
        if (!ok) {
            // a live original (a hook moved to another entry) may have handed it out meanwhile: never reused
            *orig = prev, spent = true;
            if (hot) InterlockedCompareExchange64((volatile LONG64*)(t - 8), (LONG64)pad_was, (LONG64)pad_now);
        }
    }
    // bytes past the exchanged 8 that another hooker changed meanwhile: the trampoline's copy is stale, the entry goes back
    // Our jmp was in, so another hooker may have taken it into its own trampoline: *orig and the trampoline stay valid
    // and reserved, never a null original or a reused one behind a reachable jmp.
    uint8_t after[24];
    if (ok && k > 8 && !(safe_code_read(after, t + 8, k - 8) && !memcmp(after, moved + 8, k - 8))) {
        InterlockedCompareExchange64((volatile LONG64*)t, (LONG64)was, (LONG64)now);
        ok = false, spent = true;
    }
    VirtualProtect(lo, hi - lo, old, &old);
    FlushInstructionCache(GetCurrentProcess(), lo, hi - lo);
    if (spent) page += (14 + k + 14 + 15) & ~(size_t)15;
    if (!ok) return nullptr;
    // published last, after both CASes took: unhook only restores a patch that is actually in
    if (rec) { rec->lo = lo, rec->hi = hi, rec->was = was, rec->now = now, rec->t = t; }
    page += (14 + k + 14 + 15) & ~(size_t)15;
    return tramp;
}

// Puts the entry's original bytes back (reverse CAS over exactly the span hook_fn made writable), plain cleanup of our own
// code in a process whose admission refused us. Returns false, keeping the record, if the protection or the CAS fails, so
// the caller can report it honestly. Only one caller claims the patch; no g_hook_mx, so it can run under the loader lock.
static bool unhook(HookPatch& p) {
    uint8_t* t = p.t.load();
    if (!t || !p.t.compare_exchange_strong(t, nullptr)) return true;   // nothing in, or another caller has it
    DWORD old;
    if (!VirtualProtect(p.lo, p.hi - p.lo, PAGE_EXECUTE_READWRITE, &old)) { p.t = t; return false; }
    // If the CAS fails, another hooker changed the entry since: left as not unhooked (an accepted limit; no retry under the
    // loader lock). The protection is put back either way, and that restore is checked too.
    bool restored = (uint64_t)InterlockedCompareExchange64((volatile LONG64*)t, (LONG64)p.was, (LONG64)p.now) == p.now;
    if (!VirtualProtect(p.lo, p.hi - p.lo, old, &old)) restored = false;
    FlushInstructionCache(GetCurrentProcess(), p.lo, p.hi - p.lo);
    if (!restored) p.t = t;   // keep the record; the caller logs it honestly
    return restored;
}

// NVIDIA Aftermath: with GPU crash dumps on, DX12_Initialize's GenerateShaderDebugInfo (0x8) makes the driver compile
// every pipeline under another cache key (selftest-measured), so a warm without it never reaches the game. Logged once each.
// DX12_Initialize (1.3 to 2.27) is the stub mov r9,r8 / mov r8d,edx / mov edx,ecx / mov ecx,1 / jmp init, where
// init(api, version, flags, device) is shared with DX11_Initialize (api 0). The stub's 3-byte first instruction has no
// hot-patch padding before it, so init is hooked instead.
static int (*o_am_init)(int, int, uint32_t, void*);
static int (*o_am_dumps)(int, uint32_t, uint32_t, void*, void*, void*, void*, void*);
static int hk_am_init(int api, int version, uint32_t flags, void* dev) {
    int r = o_am_init(api, version, flags, dev);
    if (api == 1) logf("aftermath: DX12_Initialize(version 0x%x, feature flags 0x%x) = 0x%x%s", version, flags, r, flags & 8 ? ": shader debug info (another driver cache key while crash dumps are on)" : "");
    return r;
}
static uint32_t image_size(HMODULE m);
// init's address, only when both stubs match byte for byte and jump to the same init (a torn read of one displacement
// can't agree with the other), a 16-aligned function start after int3 padding in the module's executable image; else null
static uint8_t* am_init_of(HMODULE m) {
    auto base = (uint8_t*)m, end = base + image_size(m);
    auto target = [&](const char* name, const uint8_t* want, size_t n) -> uint8_t* {   // want ends with the jmp's E9
        auto p = (uint8_t*)GetProcAddress(m, name);
        uint8_t b[32];
        if (!p || p < base || p + n + 4 > end || !safe_code_read(b, p, n + 4) || memcmp(b, want, n)) return nullptr;
        int32_t r;
        memcpy(&r, b + n, 4);
        return p + n + 4 + r;
    };
    static const uint8_t dx12[] = {0x4D, 0x8B, 0xC8, 0x44, 0x8B, 0xC2, 0x8B, 0xD1, 0xB9, 0x01, 0x00, 0x00, 0x00, 0xE9};
    static const uint8_t dx11[] = {0x4D, 0x8B, 0xC8, 0x44, 0x8B, 0xC2, 0x8B, 0xD1, 0x33, 0xC9, 0xE9};
    uint8_t* x = target("GFSDK_Aftermath_DX12_Initialize", dx12, sizeof dx12);
    uint8_t pad;
    if (!x || x != target("GFSDK_Aftermath_DX11_Initialize", dx11, sizeof dx11) || x - 1 < base || x + 8 > end ||
        ((uintptr_t)x & 15) || !safe_code_read(&pad, x - 1, 1) || pad != 0xCC || !mem_exec_readable(x, 8)) return nullptr;
    return x;
}
static int hk_am_dumps(int version, uint32_t apis, uint32_t flags, void* dump, void* debug_info, void* desc, void* marker, void* user) {
    int r = o_am_dumps(version, apis, flags, dump, debug_info, desc, marker, user);
    logf("aftermath: EnableGpuCrashDumps(version 0x%x, apis 0x%x, flags 0x%x, shader debug info callback %s) = 0x%x", version, apis, flags, debug_info ? "set" : "none", r);
    return r;
}
static void hook_aftermath() {
    HMODULE m = GetModuleHandleW(L"GFSDK_Aftermath_Lib.x64.dll");
    std::lock_guard l(g_hook_mx);   // a second device's thread waits until the first has installed (or failed) these
    static bool done;
    if (!m || done) return;
    done = true;
    auto at = [&](uint8_t* p, void* hook, void** orig) { return p && hook_fn(p, hook, orig) ? "hooked" : "not hooked"; };
    auto p = (uint8_t*)GetProcAddress(m, "GFSDK_Aftermath_EnableGpuCrashDumps");
    for (int i = 0; p && p[0] == 0xE9 && i < 4; ++i) { int32_t r; memcpy(&r, p + 1, 4); p += 5 + r; }  // an export that is a jmp thunk
    const char* a = at(p, (void*)hk_am_dumps, (void**)&o_am_dumps);
    const char* b = at(am_init_of(m), (void*)hk_am_init, (void**)&o_am_init);
    logf("aftermath: EnableGpuCrashDumps %s, DX12_Initialize %s", a, b);
}

// AMD AGS: a device created through agsDriverExtensionsDX12_CreateDevice with an app name has the DxcCache keyed on
// FNV-1a of that name, not of the exe name; the csv's "#ags" line tells the app which name the game registers. amd_ags.h,
// AGS 6: (context, creation params, extension params {app, engine, app version, engine version, ...}, returned params).
struct AgsExtParams { const wchar_t *app, *engine; unsigned app_version, engine_version; };
static int (*o_ags_create)(void*, const void*, const AgsExtParams*, void*);
static int (*g_ags_version)();
static std::atomic<HMODULE> g_ags_module;  // set under g_hook_mx; log_device reads it without

// A module's export by name, from its image alone: under the loader lock, before the module is initialized. Every RVA, name
// and table is bounded by SizeOfImage (a crafted or truncated image mustn't read out of bounds), the lot under SEH.
static void* export_of(HMODULE m, const char* name) {
    auto b = (const uint8_t*)m;
    __try {
        auto dos = (const IMAGE_DOS_HEADER*)b;
        if (!b || dos->e_magic != IMAGE_DOS_SIGNATURE) return nullptr;
        auto nt = (const IMAGE_NT_HEADERS64*)(b + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC) return nullptr;
        uint32_t size = nt->OptionalHeader.SizeOfImage;
        auto in = [&](uint64_t rva, uint64_t len) { return rva && rva + len <= size; };
        auto& dd = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];
        if (!in(dd.VirtualAddress, dd.Size) || dd.Size < sizeof(IMAGE_EXPORT_DIRECTORY)) return nullptr;
        auto ed = (const IMAGE_EXPORT_DIRECTORY*)(b + dd.VirtualAddress);
        if (!in(ed->AddressOfNames, (uint64_t)ed->NumberOfNames * 4) || !in(ed->AddressOfNameOrdinals, (uint64_t)ed->NumberOfNames * 2)
            || !in(ed->AddressOfFunctions, (uint64_t)ed->NumberOfFunctions * 4)) return nullptr;
        auto names = (const DWORD*)(b + ed->AddressOfNames);
        auto ords = (const WORD*)(b + ed->AddressOfNameOrdinals);
        auto fns = (const DWORD*)(b + ed->AddressOfFunctions);
        size_t want = strlen(name);
        for (DWORD i = 0; i < ed->NumberOfNames; ++i) {
            if (!in(names[i], want + 1)) continue;
            if (memcmp((const char*)(b + names[i]), name, want + 1)) continue;   // name is NUL-bounded within the image
            DWORD rva = ords[i] < ed->NumberOfFunctions ? fns[ords[i]] : 0;
            // a forwarder's RVA points back into the export directory; a code RVA must leave room for the prologue read
            return !in(rva, 8) || (rva >= dd.VirtualAddress && rva < dd.VirtualAddress + dd.Size) ? nullptr : (void*)(b + rva);
        }
        return nullptr;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

static uint32_t image_size(HMODULE m) {
    __try {
        auto b = (const uint8_t*)m;
        auto nt = (const IMAGE_NT_HEADERS64*)(b + ((const IMAGE_DOS_HEADER*)b)->e_lfanew);
        return nt->Signature == IMAGE_NT_SIGNATURE ? nt->OptionalHeader.SizeOfImage : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}


// The game's own pointers: a bad one must not take the game down, so they are read under SEH.
static bool ags_copy(const AgsExtParams* ep, wchar_t (&app)[129], wchar_t (&engine)[129], unsigned& av, unsigned& ev) {
    __try {
        auto copy = [](const wchar_t* s, wchar_t (&to)[129]) {
            size_t n = 0;
            for (; s && n < 128 && s[n]; ++n) to[n] = s[n];
            to[n] = 0;
            return !s || !s[n];
        };
        av = ep->app_version, ev = ep->engine_version;
        return copy(ep->app, app) & copy(ep->engine, engine);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
static unsigned ags_extensions(const void* rp) {  // AGSDX12ReturnedParams: {ID3D12Device*, extensions supported}
    __try {
        return rp ? *(const unsigned*)((const uint8_t*)rp + sizeof(void*)) : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

// Printable ASCII but ',' and '%' as is, any other UTF-16 unit as %XXXX: the app reads the name back exactly.
static std::string ags_escape(const wchar_t* s) {
    std::string out;
    for (; *s; ++s)
        if (*s >= 0x20 && *s <= 0x7E && *s != L',' && *s != L'%') out += (char)*s;
        else out += std::format("%{:04X}", (unsigned)*s);
    return out;
}

static int hk_ags_create(void* ctx, const void* cp, const AgsExtParams* ep, void* rp) {
    wchar_t app[129] = {}, engine[129] = {};
    unsigned av = 0, ev = 0;
    bool read = ep && ags_copy(ep, app, engine, av, ev);
    int r = o_ags_create(ctx, cp, ep, rp);
    // the device was created through the proxy's D3D12CreateDevice: admission is decided and the session opened (a call
    // already in here when admission took the hook out writes nothing)
    if (g_admission <= 0 || g_ags_refused) return r;
    int version = g_ags_version ? g_ags_version() : 0;
    unsigned ext = r == 0 ? ags_extensions(rp) : 0;
    const char* registered = version >> 22 == 6 ? (ext & 1u << 3 ? "1" : "0") : "?";  // AGS 6: appRegistration
    logf("ags: DX12_CreateDevice(app \"%s\", engine \"%s\", app version 0x%x, engine version 0x%x) = %d; AGS %d.%d.%d, app registration %s%s",
         read ? ags_escape(app).c_str() : "?", read ? ags_escape(engine).c_str() : "?", av, ev, r, version >> 22, version >> 12 & 0x3FF, version & 0xFFF,
         registered, read ? "" : "; the names couldn't be read");
    if (r != 0 || !read || !*app || !g_wrote_session || !g_csv) return r;
    std::string line = std::format("#ags,{},{:x},{:x},{},{}\n", registered, av, ev, ags_escape(app), ags_escape(engine));
    // only a consecutive repeat is dropped: a game that registers A, then B, then A again holds A last, so all three go
    static std::string& last = *new std::string;
    std::lock_guard l(g_mx);
    if (line != last) csv_line("%s", line.c_str()), last = line;
    return r;
}

// The AGS section runs under the loader lock (DllMain, the LDR callback), where logf's g_early_mx could deadlock with
// admitted()'s flush: notes wait here and go to the log at the first device.
static std::mutex& g_ags_log_mx = *new std::mutex;
static std::vector<std::string>& g_ags_notes = *new std::vector<std::string>;
static void ags_note(std::string s) {
    std::lock_guard l(g_ags_log_mx);
    g_ags_notes.push_back(std::move(s));
}
static void ags_flush_notes() {
    std::vector<std::string> notes;
    { std::lock_guard l(g_ags_log_mx); notes.swap(g_ags_notes); }
    for (auto& s : notes) logf("%s", s.c_str());
}

static HookPatch g_ags_patch;
static void unhook_ags(const char* why) {
    if (!g_ags_patch.t) return;
    ags_note(std::string("ags: ") + (unhook(g_ags_patch) ? "unhooked (" : "NOT unhooked, left our code in (") + why + ")");
}

// Cheap enough for the loader lock (no directory walk, no key): the ini reads, the stamp's exe lines (the app's own anti-cheat
// check of the install) and a sig= of the right length, and GetModuleHandle for anti-cheat clients. The signature (which
// covers vendor=), VAC, Riot and the rest stay in admission, which takes the hook out when it refuses.
static bool ags_cheap_ok() {
    wchar_t v[64];
    const std::wstring file = g_dir + L"scskiller.ini";
    GetPrivateProfileStringW(L"scskiller", L"vendor", L"", v, 32, file.c_str());
    if (wcscmp(v, L"amd")) return false;   // the app signs vendor=amd only where the recorder is admitted (AMD, see AgsEligible)
    if (GetPrivateProfileStringW(L"scskiller", L"sig", L"", v, 64, file.c_str()) != 40) return false;
    auto number = [&](const wchar_t* key) { return GetPrivateProfileStringW(L"scskiller", key, L"", v, 32, file.c_str()) ? _wcstoui64(v, nullptr, 10) : 0; };
    wchar_t exe[MAX_PATH];
    WIN32_FILE_ATTRIBUTE_DATA a;
    DWORD n = GetModuleFileNameW(nullptr, exe, MAX_PATH);
    if (!n || n >= MAX_PATH || !GetFileAttributesExW(exe, GetFileExInfoStandard, &a)) return false;
    if (number(L"exe_size") != ((uint64_t)a.nFileSizeHigh << 32 | a.nFileSizeLow) || number(L"exe_time") != ((uint64_t)a.ftLastWriteTime.dwHighDateTime << 32 | a.ftLastWriteTime.dwLowDateTime))
        return false;
    return anti_cheat_loaded().empty();   // never install where an anti-cheat client is already in the process
}

// Installed only at load, under the loader lock: the proxy's own load when the exe imports AGS, or the LDR LOADED callback
// when AGS loads later. Idle, non-hot only (no padding is ever written for AGS). Only amd_ags_x64.dll, only the first one.
static void pin_self();
static void hook_ags(HMODULE m, bool idle) {
    std::unique_lock l(g_hook_mx, std::try_to_lock);
    if (!l.owns_lock()) return ags_note("ags: not hooked (another hook was being installed)");
    if (!m || g_ags_module || g_admission < 0 || g_ags_refused || !ags_cheap_ok()) return;
    HMODULE pinned;   // pin AGS (by an address in it) before reading or patching: it must not unload under us
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, (LPCWSTR)m, &pinned)) return;
    uint32_t size = image_size(m);
    auto end = (uint8_t*)m + size;
    auto p = (uint8_t*)export_of(m, "agsDriverExtensionsDX12_CreateDevice");
    // an export that is a jmp thunk, only while the 5-byte thunk and its E9 target stay inside AGS's own image; each read
    // is guarded (committed, executable, under SEH), since a RVA inside SizeOfImage can still be an uncommitted or guard page
    for (int i = 0; p && i < 4; ++i) {
        uint8_t thunk[5];
        if (p + 5 > end || !safe_code_read(thunk, p, 5)) { p = nullptr; break; }   // couldn't read: abort, don't hook a half-read entry
        if (thunk[0] != 0xE9) break;   // a real instruction read cleanly: this is the entry
        int32_t r;
        memcpy(&r, thunk + 1, 4);
        uint8_t* to = p + 5 + r;
        if (to < (uint8_t*)m || to + 8 > end) { p = nullptr; break; }
        p = to;
    }
    if (p && !mem_exec_readable(p, 8)) p = nullptr;   // the prologue hook_fn will read must be committed and executable
    pin_self();  // the patched entry jumps into this dll: a game that frees d3d12.dll again must not unload it
    g_ags_module = m;
    g_ags_version = (int (*)())export_of(m, "agsGetVersionNumber");  // absent from older AGS
    bool hooked = p && hook_fn(p, (void*)hk_ags_create, (void**)&o_ags_create, idle, &g_ags_patch, 64);
    ags_note(std::string("ags: amd_ags_x64.dll agsDriverExtensionsDX12_CreateDevice ") + (!p ? "not exported" : hooked ? "hooked" : "not hooked (prologue not recognized)"));
    // Dekker: the installer published rec->t, then reads g_ags_refused; the refuser stores it, then unhooks. With
    // seq_cst, one side always sees the other, so a refusal that raced the install still takes our code back out.
    if (hooked && g_ags_refused) unhook_ags("admission refused during install");
}

struct LdrString { USHORT length, max; const wchar_t* buffer; };
struct LdrNotification { ULONG flags; const LdrString *full, *base; void* dll_base; ULONG size; };
static void* g_ldr_cookie;
static bool g_ags_late;  // ags_hooks registered: an AGS that loads later is hooked as it loads
static void ffx_loaded(const LdrString* name, HMODULE m);
static void CALLBACK on_dll(ULONG reason, const LdrNotification* d, void*) {
    if (reason != 1 || !d || !d->base || !d->base->buffer) return;  // LDR_DLL_NOTIFICATION_REASON_LOADED
    if (d->base->length == 15 * sizeof(wchar_t) && !_wcsnicmp(d->base->buffer, L"amd_ags_x64.dll", 15)) {
        if (g_ags_late) hook_ags((HMODULE)d->dll_base, true);
    } else ffx_loaded(d->base, (HMODULE)d->dll_base);
}
// The dll-load notification, registered once: by ags_hooks at load, else by frame_hooks at the first device.
static bool ldr_notify() {
    if (g_ldr_cookie) return true;
    auto reg = (LONG(NTAPI*)(ULONG, decltype(&on_dll), void*, void**))GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "LdrRegisterDllNotification");
    return reg && reg(0, on_dll, nullptr, &g_ldr_cookie) == 0 && g_ldr_cookie;
}
// A refused process keeps no callback of ours.
static void ldr_off() {
    auto unreg = (LONG(NTAPI*)(void*))GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "LdrUnregisterDllNotification");
    if (g_ldr_cookie && unreg && unreg(g_ldr_cookie) == 0) g_ldr_cookie = nullptr;
}

// static_load: this dll loads with the process's imports, before any of the game's code runs. The notification must
// register (a cookie, STATUS_SUCCESS) or nothing is installed: without it a later AGS load would be missed and there is
// no first-device fallback.
static void ags_hooks(bool static_load) {
#ifdef SCSK_AGS_WRAP
    return;  // this dll is amd_ags_x64.dll: Wrap_AgsCreateDevice already stands in front of the game's AGS
#endif
    if (g_api11 || g_warm || g_elsewhere || !ags_cheap_ok()) return;
    // SCSKILLER_TEST_NO_LDR (selftest only): act as if registration failed, so the "install nothing" path is exercised
    bool noLdr = GetEnvironmentVariableW(L"SCSKILLER_TEST_NO_LDR", nullptr, 0) && GetProcAddress(GetModuleHandleW(nullptr), "SCSKiller_WarmHost");
    if (noLdr || !ldr_notify()) return ags_note("ags: not installed (no dll-load notification)");
    g_ags_late = true;
    // Only a static load (the exe imports AGS, so it's already loaded and still uninitialized) hooks the present module
    // idle. On a dynamic proxy load AGS may already be running, which can't be hooked idle; later loads go through on_dll.
    if (static_load)
        if (HMODULE m = GetModuleHandleW(L"amd_ags_x64.dll")) hook_ags(m, true);
}

struct NvPsoExt { uint32_t base_version, extension, version, slot, space; };  // NVAPI_D3D12_PSO_SET_SHADER_EXTENSION_SLOT_DESC_V1
static int (*o_nv_slot)(IUnknown*, uint32_t, uint32_t);
static int (*o_nv_slot_thread)(IUnknown*, uint32_t, uint32_t);
static int (*o_nv_opts)(ID3D12Device5*, const uint32_t*);
static int (*o_nv_gfx)(ID3D12Device*, const D3D12_GRAPHICS_PIPELINE_STATE_DESC*, uint32_t, const NvPsoExt* const*, ID3D12PipelineState**);
static int (*o_nv_cs)(ID3D12Device*, const D3D12_COMPUTE_PIPELINE_STATE_DESC*, uint32_t, const NvPsoExt* const*, ID3D12PipelineState**);
static void* (*o_nv_qi)(uint32_t);

static int hk_nv_slot(IUnknown* dev, uint32_t slot, uint32_t space) {
    int st = o_nv_slot(dev, slot, space);
    if (!st) g_nv_dev = slot | (uint64_t)space << 32;
    static std::atomic<int> logged;
    if (++logged <= 5) logf("nvapi: SetNvShaderExtnSlotSpace(u%d, space %u) = %d", (int)slot, space, st);
    return st;
}
static int hk_nv_slot_thread(IUnknown* dev, uint32_t slot, uint32_t space) {
    int st = o_nv_slot_thread(dev, slot, space);
    if (!st) t_nv.slot = slot, t_nv.space = space;
    return st;
}
static int hk_nv_opts(ID3D12Device5* dev, const uint32_t* p) {  // NVAPI_D3D12_SET_CREATE_PIPELINE_STATE_OPTIONS_PARAMS {version, flags, ...}
    int st = o_nv_opts(dev, p);
    // version = struct size | version << 16: every version so far starts with {version, flags}; the warm sets them as V1.
    // flags are read only from a struct the driver accepted and whose size holds them
    bool flags = !st && p && (p[0] & 0xFFFF) >= 8;
    if (flags) g_nv_opts = p[1];
    static std::mutex& mx = *new std::mutex;
    static auto& seen = *new std::set<std::tuple<uint32_t, uint32_t, int>>;
    std::tuple<uint32_t, uint32_t, int> call{p ? p[0] : 0, flags ? p[1] : 0, st};
    bool fresh;
    {
        std::lock_guard l(mx);
        fresh = seen.insert(call).second;
    }
    if (fresh) logf("nvapi: SetCreatePipelineStateOptions(struct version 0x%08x, flags %s) = %d", std::get<0>(call),
                    flags ? ("0x" + std::format("{:x}", std::get<1>(call))).c_str() : "not read", st);
    return st;
}
static void nv_pso_ext(Writer& w, uint32_t n, const NvPsoExt* const* e) {
    static std::atomic<int> other;
    for (uint32_t i = 0; e && i < n; ++i)
        if (e[i] && e[i]->extension == 5) w.nv.slot = e[i]->slot, w.nv.space = e[i]->space, w.nv.scope = 3;  // NV_PSO_SET_SHADER_EXTENSION_SLOT_AND_SPACE
        else if (e[i] && ++other <= 5) logf("nvapi: PSO extension %u not recorded: the warm creates that PSO without it", e[i]->extension);
}
static int hk_nv_gfx(ID3D12Device* dev, const D3D12_GRAPHICS_PIPELINE_STATE_DESC* d, uint32_t n, const NvPsoExt* const* e, ID3D12PipelineState** pp) {
    Writer w{'G'};
    if (!t_in && pp && d) io_gfx(w, const_cast<D3D12_GRAPHICS_PIPELINE_STATE_DESC&>(*d)), nv_pso_ext(w, n, e);
    int st = 0;
    timed_create(dev, (void**)pp, w, [&] { return (st = o_nv_gfx(dev, d, n, e, pp)) ? E_FAIL : S_OK; });
    return st;
}
static int hk_nv_cs(ID3D12Device* dev, const D3D12_COMPUTE_PIPELINE_STATE_DESC* d, uint32_t n, const NvPsoExt* const* e, ID3D12PipelineState** pp) {
    Writer w{'C'};
    if (!t_in && pp && d) io_cs(w, const_cast<D3D12_COMPUTE_PIPELINE_STATE_DESC&>(*d)), nv_pso_ext(w, n, e);
    int st = 0;
    timed_create(dev, (void**)pp, w, [&] { return (st = o_nv_cs(dev, d, n, e, pp)) ? E_FAIL : S_OK; });
    return st;
}
static void* hk_nv_qi(uint32_t id) {  // which NVAPI functions the process uses, once each: the log says what else it may set
    void* p = o_nv_qi(id);
    static std::mutex& mx = *new std::mutex;
    static auto& seen = *new std::unordered_set<uint32_t>;
    bool fresh;
    {
        std::lock_guard l(mx);
        fresh = seen.insert(id).second;
    }
    if (fresh) logf("nvapi: function 0x%08X resolved%s", id, p ? "" : " (not in this driver)");
    return p;
}

static void nv_hooks() {
    HMODULE m = LoadLibraryW(L"nvapi64.dll");  // NVIDIA only; the game may load it later, the same module then
    auto qi = m ? (void* (*)(uint32_t))GetProcAddress(m, "nvapi_QueryInterface") : nullptr;
    if (!qi) return;
    g_nv_loaded = true;
    std::lock_guard l(g_hook_mx);
    struct { uint32_t id; void* hook; void** orig; const char* name; } fns[] = {
        {0xAC2DFEB5, (void*)hk_nv_slot, (void**)&o_nv_slot, "SetNvShaderExtnSlotSpace"},
        {0x43D867C0, (void*)hk_nv_slot_thread, (void**)&o_nv_slot_thread, "SetNvShaderExtnSlotSpaceLocalThread"},
        {0x5C607A27, (void*)hk_nv_opts, (void**)&o_nv_opts, "SetCreatePipelineStateOptions"},
        {0x2FC28856, (void*)hk_nv_gfx, (void**)&o_nv_gfx, "CreateGraphicsPipelineState"},
        {0x2762DEAC, (void*)hk_nv_cs, (void**)&o_nv_cs, "CreateComputePipelineState"},
        {0, (void*)hk_nv_qi, (void**)&o_nv_qi, "nvapi_QueryInterface"},  // last: our own lookups above aren't the game's
    };
    std::string missed;
    for (auto& f : fns) {
        void* p = f.id ? qi(f.id) : (void*)qi;
        if (!p || !hook_fn(p, f.hook, f.orig)) missed += std::string(missed.empty() ? "" : ", ") + f.name + (p ? " (prologue not recognized)" : " (not in this driver)");
    }
    logf("nvapi: hooks installed%s%s", missed.empty() ? "" : "; not hooked: ", missed.c_str());
}

// A layer hands the game its own device and passes creates on to the real one, after any change its add-ons make (RenoDX
// replaces shaders and adds a parameter to every root signature): a mod's d3d12.dll (next=), or ReShade as dxgi.dll, which
// hooks the system D3D12CreateDevice. The exact desc the driver compiles reaches the real device's vtable, shared by every
// device of the runtime. Those creates are recorded too (not counted as the game's: note), so the warm compiles what the
// modded game asks the driver for; a desc the layer passes on unchanged has the game's key (one record), a changed one
// gets a 'W' record naming both.
static PFN_Gfx b_gfx;
static PFN_Cs b_cs;
static PFN_Rs b_rs;
static PFN_Stream b_stream;
static thread_local bool t_below;  // the runtime may route one create through another
static uint64_t g_changed;

template <class F> static HRESULT below(Writer& w, void** pp, F&& call) {
    if (t_below || t_replay || !pp) return call();
    std::deque<std::string> own;  // a mod may free its replacement shader once the create returns
    {
        std::lock_guard l(g_mx);
        for (auto& [h, b] : w.blobs)
            if (!g_blobs_on_disk.count(h)) b = own.emplace_back(b);
    }
    t_below = true;
    auto a = std::chrono::steady_clock::now();
    HRESULT hr = call();
    auto b = std::chrono::steady_clock::now();
    t_below = false;
    if (SUCCEEDED(hr) && w.ok && !w.s.empty()) {
        Hash k = key_of(w.tag, w.s);
        std::lock_guard l(g_mx);
        store(w, k);
        if (k != t_asked && g_db && (g_keys.count(k) || imported(k))) {  // imported: an earlier recorder's, without its 'W'
            std::string c(40, '\0');
            memcpy(c.data(), k.data(), 20), memcpy(c.data() + 20, t_asked.data(), 20);
            if (fresh(g_keys, key_of('W', c))) {
                if (++g_changed <= 5) logf("below: the layer changed a create (%s)", t_asked == kZero ? "its own" : "the game's");
                put('W', c.data(), c.size()), db_check();
            }
        }
    }
    t_below_ms += std::chrono::duration<double, std::milli>(a - w.t0 + (std::chrono::steady_clock::now() - b)).count();
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_gfx_below(ID3D12Device* dev, const D3D12_GRAPHICS_PIPELINE_STATE_DESC* d, REFIID riid, void** pp) {
    Writer w{'G'};
    w.below = true;
    if (!t_below && !t_replay && pp && d) io_gfx(w, const_cast<D3D12_GRAPHICS_PIPELINE_STATE_DESC&>(*d));
    return below(w, pp, [&] { return b_gfx(dev, d, riid, pp); });
}
static HRESULT STDMETHODCALLTYPE hk_cs_below(ID3D12Device* dev, const D3D12_COMPUTE_PIPELINE_STATE_DESC* d, REFIID riid, void** pp) {
    Writer w{'C'};
    w.below = true;
    if (!t_below && !t_replay && pp && d) io_cs(w, const_cast<D3D12_COMPUTE_PIPELINE_STATE_DESC&>(*d));
    return below(w, pp, [&] { return b_cs(dev, d, riid, pp); });
}
static HRESULT STDMETHODCALLTYPE hk_stream_below(ID3D12Device2* dev, const D3D12_PIPELINE_STATE_STREAM_DESC* d, REFIID riid, void** pp) {
    Writer w{'S'};
    w.below = true;
    if (!t_below && !t_replay && pp && d) write_stream(w, *d);
    return below(w, pp, [&] { return b_stream(dev, d, riid, pp); });
}
static HRESULT STDMETHODCALLTYPE hk_rs_below(ID3D12Device* dev, UINT mask, const void* blob, SIZE_T n, REFIID riid, void** pp) {
    return remember_rs(b_rs(dev, mask, blob, n, riid, pp), blob, n, pp, g_rs_below_of);
}

static void hook_below(ID3D12Device* dev, const char* layer) {
    std::lock_guard l(g_mx);
    void** vt = *(void***)dev;
    if (vt == g_hooked_vt) logf("below: the %s returned the system device itself: its creates are recorded as the game's", layer);
    else {
        logf("below: device %p vtable %p under the %s", (void*)dev, (void*)vt, layer);
        g_dd_layer = true;
        auto put_below = [&](void** t, int slot, void* hook, auto& orig) {
            // the mod handed the game this interface of the system device: the game's hook is already there and records it
            if (t[slot] == (void*)hk_gfx || t[slot] == (void*)hk_cs || t[slot] == (void*)hk_rs || t[slot] == (void*)hk_stream) return;
            patch(t, slot, hook, orig);
        };
        put_below(vt, SLOT_GFX, (void*)hk_gfx_below, b_gfx);
        put_below(vt, SLOT_CS, (void*)hk_cs_below, b_cs);
        put_below(vt, SLOT_RS, (void*)hk_rs_below, b_rs);
        ID3D12Device2* d2;
        if (SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&d2)))) put_below(*(void***)d2, SLOT_STREAM, (void*)hk_stream_below, b_stream), d2->Release();
    }
}

// Frame times: the game's presents, hooked on the vtables of the swap chains the process's DXGI factory creates (every
// factory of a dxgi.dll shares one vtable). The frame is the QPC when the outermost Present / Present1 of a thread
// returns, so a wrapper's swap chain (a mod, an overlay, Streamline) calling the real one counts once; PresentMon's
// FrameTime is the same return-to-return interval. The hook only queues the timestamp; frame_writer writes the file.
// scskiller_frames.bin, the last launch that presented, u32 records (little-endian):
//   0xFFFFFFFF + u64 unix_ms (the csv's #session stamp), u64 us since the recorder loaded (the csv's t_ms clock), u64 QPC, u64 QPC frequency:
//     a launch, taken together; the frames after it count from that instant.
//   top 4 bits 0-14: a frame of that swap chain (0 = the first seen, 14 = the 15th and later), the low 28 bits the
//     microseconds since the previous frame (or the launch record);
//   top 4 bits 15: no frame for the low 28 bits' milliseconds (a gap longer than 28 bits of microseconds holds).
//   top 4 bits 15 with the low 28 bits under FrameEnc::kTagEnd: a tagged record, no frame (0-14: that swap chain is frame
//     generation's, its frames the ones the game renders).
using PFN_Present = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
using PFN_Present1 = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);
using PFN_CreateSc = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory*, IUnknown*, DXGI_SWAP_CHAIN_DESC*, IDXGISwapChain**);
using PFN_CreateScHwnd = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory2*, IUnknown*, HWND, const DXGI_SWAP_CHAIN_DESC1*,
                                                     const DXGI_SWAP_CHAIN_FULLSCREEN_DESC*, IDXGIOutput*, IDXGISwapChain1**);
using PFN_CreateScCw = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory2*, IUnknown*, IUnknown*, const DXGI_SWAP_CHAIN_DESC1*, IDXGIOutput*, IDXGISwapChain1**);
using PFN_CreateScComp = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory2*, IUnknown*, const DXGI_SWAP_CHAIN_DESC1*, IDXGIOutput*, IDXGISwapChain1**);
// vtslots.cpp checks these against dxgi1_2.h at compile time
enum { SLOT_PRESENT = 8, SLOT_PRESENT1 = 22, SLOT_SETCS = 38, SLOT_CREATESC = 10, SLOT_CREATESC_HWND = 15, SLOT_CREATESC_CW = 16, SLOT_CREATESC_COMP = 24 };
static PFN_CreateSc o_createsc;
static PFN_CreateScHwnd o_createsc_hwnd;
static PFN_CreateScCw o_createsc_cw;
static PFN_CreateScComp o_createsc_comp;

// A wrapper's swap chain and the real one have different vtables and originals: one entry per vtable.
struct ScVt { std::atomic<void**> vt; std::atomic<void*> present, present1, setcs; };
static ScVt g_scvt[4];
static std::atomic<int> g_nscvt;
static void* sc_orig(void* sc, std::atomic<void*> ScVt::*slot) {
    void** vt = *(void***)sc;
    int n = g_nscvt.load(std::memory_order_acquire);
    for (int i = 0; i < n; ++i)
        if (g_scvt[i].vt == vt) return g_scvt[i].*slot;
    for (int i = 0; i < n; ++i)  // an overlay that copied a hooked vtable into the object: the first original of the slot
        if (void* p = g_scvt[i].*slot) return p;
    return nullptr;
}

static std::mutex g_fmx;
static std::vector<FrameAt> g_fq;  // under g_fmx: presents not yet written
static std::atomic<int64_t> g_hook_ticks, g_hook_max;  // the hooks' own QPC ticks, for the log
static std::atomic<uint64_t> g_presents;
// Nesting depth: only the outermost present of a thread is a frame. Frame generation's swap chain has its own: FSR 3
// presents the displayed frame inside its Present on the game's thread when it generates none (menus).
static thread_local int t_present, t_fg_present;


// In-game panel of a before and after run (overlay.h), asked for by SCSKiller's own scskiller.ini: overlay=1,
// ab_play_s=<seconds>, ab_tag=before|after. The countdown, a graph of the last 5 s of frame times and, on D3D12, a flash
// for each shader compilation stutter. Drawn in the outermost Present of a swap chain made through the hooked factory,
// before the original Present, as one copy of the panel's pixels into the back buffer: painted on the CPU only when they
// change (a second ticks, the bar grows, the graph moves a column, a stutter, a resize), at most ten times a second.
// D3D12: an upload buffer of three regions, one shown while another is written once the GPU is past the last frame that
// showed it, and per back buffer and region a few command lists recorded once (barrier, CopyTextureRegion, barrier),
// each run again once the GPU is past its last run; a frame costs one ExecuteCommandLists and one Signal, and the fence
// is only read, never waited on. D3D11: a texture updated when the pixels change, and CopySubresourceRegion on the
// immediate context. No back buffer is held between presents, so ResizeBuffers needs no hook: a buffer the overlay
// hasn't tagged is a new one. SDR 8-bit and 10-bit, HDR10 and scRGB, by the colour space SetColorSpace1 (hooked) set.
// With frame generation the frames are the game's (FSR 3's swap chain, game presents) and the panel goes on the
// displayed swap chain, which FSR 3 makes on its present queue: a direct queue, where it writes the back buffer and
// leaves it in PRESENT before each Present. Any failure or fault turns it off for the launch. Its time is in the hook
// time the frames log reports.
static struct { bool on, after, stutters; int play_s; } g_ovc;
static std::atomic<bool> g_ov_off;   // for the launch: a failure
static bool g_ov_exiting;            // process detach: a swap chain freed now leaks its OvChain
static std::atomic<bool> g_ov_drew;  // frame_writer writes the csv's #overlay line
// The draw path try-locks it (a present that finds it taken skips the draw). ov_replace alone waits, bounded, and calls
// nothing under it: a draw holding it may wait on a layer's lock that a create waiting for it holds.
static std::mutex g_ovmx;
static ov::Feed g_ovfeed;            // frame ends, from the presents that count to the draw
static std::atomic<bool> g_ov_game;  // frame generation's swap chain presented: only its frames count
static std::atomic<uint32_t> g_ov_stutters;  // shader compilation stutters flagged, for frame_writer's log
static std::atomic<double> g_ov_startup{-1};  // the first present, where play starts, for frame_writer's log
static std::atomic<bool> g_ov_done_seen;      // the play time is counted, for frame_writer's log
// under g_ovmx
static ov::Layout g_ovl;
static ov::Graph g_ovgraph;
static ov::Slow g_ovslow;
static bool g_ov_done;
// the swap chain's private data holding its OvChain
static const GUID kOvChain = {0x8f3b0a8e, 0x4f0b, 0x4a5e, {0x9c, 0x4d, 0x5c, 0x53, 0xc4, 0xb1, 0xe0, 0xa1}};
// a back buffer's private data: the generation the overlay recorded its command lists for
static const GUID kOvBuffer = {0x3c1d6f52, 0x9a7e, 0x4b2c, {0x8e, 0x11, 0x6a, 0x0f, 0xd2, 0x47, 0x93, 0xb8}};
// a back buffer's tag from before its swap chain's first present proved the queue (proof_tag)
static const GUID kOvProof = {0x9e4a71d3, 0x2b6c, 0x4f05, {0xb8, 0x3d, 0x17, 0xe2, 0x60, 0xa9, 0x4c, 0x5f}};

// What the pixels show: a change repaints them.
struct OvKey {
    long secs = -1, fill = 0, column = -1;
    uint32_t found = 0;
    bool done = false, flash = false;
    uint64_t w = 0;
    uint32_t h = 0;
    DXGI_FORMAT f = DXGI_FORMAT_UNKNOWN;
    ov::Out out = ov::kNone;
    bool operator==(const OvKey&) const = default;
};
constexpr long kOvPitch = (ov::kMaxW * 8 + 255) & ~255L;  // D3D12_TEXTURE_DATA_PITCH_ALIGNMENT; FP16 has 8 bytes a pixel
constexpr long kOvRegion = (kOvPitch * ov::kMaxH + 511) & ~511L;  // D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT
// What the panel shows this frame.
struct OvText { const char *text, *widest; double frac; bool done, flash; long secs; };
static OvKey ov_key(uint64_t w, uint32_t h, DXGI_FORMAT f, ov::Out out, const OvText& t) {
    OvKey k;
    k.secs = t.secs, k.done = t.done, k.flash = t.flash, k.w = w, k.h = h, k.f = f, k.out = out;
    k.column = g_ovgraph.newest, k.found = g_ovslow.found;
    k.fill = std::lround(std::clamp(t.frac, 0.0, 1.0) * 1000);
    return k;
}

// What the overlay draws a swap chain with. The swap chain's private data holds it, so it goes with the swap chain.
struct OvChain final : IUnknown {
    std::atomic<ULONG> refs{1};
    std::atomic<int> cs{DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709};  // SetColorSpace1's; a swap chain starts in this one
    bool cs_known = false;  // our SetColorSpace1 hook is the swap chain's
    // its queue came from a present's Signal: only buffers tagged with this before that present are drawn on (a resize
    // since may name other queues)
    uint32_t proof = 0;
    std::atomic<bool> retired{false};  // no longer its swap chain's: a present that got it before isn't drawn with it
    std::vector<uint32_t> img = std::vector<uint32_t>(ov::kMaxW * ov::kMaxH);  // the panel painted
    std::vector<uint8_t> px11;  // D3D11: the panel in the buffer's format
    // D3D12
    ID3D12CommandQueue* queue = nullptr;  // held: the destructor signals on it
    ID3D12Device* dev = nullptr;
    ID3D12Fence* fence = nullptr;
    uint64_t value = 0;
    ID3D12Resource* upload = nullptr;
    uint8_t* mapped = nullptr;
    static constexpr int kRegions = 3;  // two changes a frame apart (a resize) still find one the GPU is past
    struct Region { uint64_t free_at = 0; OvKey key; RECT at = {}; } region[kRegions];  // free_at: the fence value past its last use
    int cur = -1;
    // a list runs again only once the GPU is past its last run: a few of each, for the frames the CPU is ahead
    static constexpr int kCopies = 4;
    struct List {
        ID3D12CommandAllocator* alloc = nullptr;
        ID3D12GraphicsCommandList* list = nullptr;
        RECT at = {};  // the panel it copies; empty: not recorded
        uint64_t sent = 0;  // the fence value past its last run
    };
    struct Buf {
        uint32_t gen = 0;
        List lists[kRegions][kCopies];
    } buf[DXGI_MAX_SWAP_CHAIN_BUFFERS];
    uint32_t gens = 0;
    // D3D11: the immediate context, and none of the D3D12 members
    ID3D11DeviceContext* ctx11 = nullptr;
    ID3D11Texture2D* tex11 = nullptr;
    DXGI_FORMAT fmt11 = DXGI_FORMAT_UNKNOWN;
    OvKey key11;
    RECT at11 = {};

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** pp) override {
        if (riid != __uuidof(IUnknown)) return *pp = nullptr, E_NOINTERFACE;
        return *pp = this, AddRef(), S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override {
        ULONG r = --refs;
        if (!r) delete this;
        return r;
    }
    ~OvChain() {
        if (g_ov_exiting) return;  // the runtime may be gone already
        if (fence && queue) {  // ponytail: the GPU still has our lists, or that can't be known: leaked, not freed under it
            if (FAILED(queue->Signal(fence, ++value))) return;
            if (fence->GetCompletedValue() < value) {
                HANDLE e = CreateEventW(nullptr, FALSE, FALSE, nullptr);
                if (e && SUCCEEDED(fence->SetEventOnCompletion(value, e))) WaitForSingleObject(e, 500);
                if (e) CloseHandle(e);
                if (fence->GetCompletedValue() < value) return;
            }
        }
        for (auto& b : buf)
            for (auto& region : b.lists)
                for (auto& l : region) drop(l);
        for (IUnknown* u : {(IUnknown*)upload, (IUnknown*)fence, (IUnknown*)dev, (IUnknown*)queue, (IUnknown*)tex11, (IUnknown*)ctx11})
            if (u) u->Release();
    }
    HRESULT make(List& l) {
        HRESULT hr = dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&l.alloc));
        if (SUCCEEDED(hr)) hr = dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, l.alloc, nullptr, IID_PPV_ARGS(&l.list));
        return SUCCEEDED(hr) ? l.list->Close() : hr;
    }
    static void drop(List& l) {
        if (l.list) l.list->Release();
        if (l.alloc) l.alloc->Release();
        l = {};
    }
    ov::Out out(DXGI_FORMAT f) const { return ov::out_of(f, cs, cs_known); }
};

static void overlay_off(const char* what, HRESULT hr) {
    if (!g_ov_off.exchange(true)) logf("overlay: off for this launch: %s (0x%08x)", what, (unsigned)hr);
}
static const wchar_t* fg_module() {
    for (const wchar_t* m : ov::kFgModules)
        if (GetModuleHandleW(m)) return m;
    return nullptr;
}

// The panel laid out for k, painted, into dst as k's pixels (rows of kOvPitch): g_ovl.at is where it goes, empty when the
// buffer is too small for any of it.
static void overlay_paint(OvChain* c, const OvKey& k, const OvText& t, uint8_t* dst) {
    ov::layout(k.w, k.h, t.widest, g_ovc.stutters, g_ovl);
    const RECT& a = g_ovl.at;
    if (a.right <= a.left) return;
    ov::paint(g_ovl, t.text, t.frac, g_ovgraph, t.flash, c->img.data(), ov::kMaxW);
    ov::convert(c->img.data() + g_ovl.sy * ov::kMaxW + g_ovl.sx, ov::kMaxW, a.right - a.left, a.bottom - a.top, k.out, dst, kOvPitch);
}

// S_FALSE: not drawn this time (the GPU isn't past what a change needs yet, a format or colour space that isn't SDR).
static HRESULT overlay_draw12(OvChain* c, ID3D12Resource* buf, UINT i, const OvText& t) {
    D3D12_RESOURCE_DESC d = buf->GetDesc();
    // a back buffer filled by copies or UAV writes can't take a wrong barrier: the device could be removed
    const ov::Out out = c->out(d.Format);
    if (!out || !(d.Flags & D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET) || d.SampleDesc.Count > 1) return S_FALSE;
    const OvKey k = ov_key(d.Width, d.Height, d.Format, out, t);
    uint64_t past = c->fence->GetCompletedValue();
    HRESULT hr;
    auto& b = c->buf[i];
    uint32_t gen = 0;
    UINT size = sizeof gen;
    if (uint32_t tag = 0; c->proof && (FAILED(buf->GetPrivateData(kOvProof, &size, &tag)) || size != sizeof tag || tag != c->proof)) return E_CHANGED_STATE;
    size = sizeof gen;
    if (FAILED(buf->GetPrivateData(kOvBuffer, &size, &gen)) || size != sizeof gen || gen != b.gen) {  // not the buffer its lists are for
        for (auto& region : b.lists)
            for (auto& l : region) l.at = {};  // recorded again once the GPU is past them: they hold no reference to the old one
        b.gen = ++c->gens;
        if (FAILED(hr = buf->SetPrivateData(kOvBuffer, sizeof b.gen, &b.gen))) return hr;
    }
    int from = -1, r = c->cur;
    if (r < 0 || !(c->region[r].key == k)) {
        int n = -1;
        for (int x = 0; x < OvChain::kRegions; ++x)
            if (x != r && past >= c->region[x].free_at && (n < 0 || c->region[x].free_at < c->region[n].free_at)) n = x;
        if (n >= 0) {
            overlay_paint(c, k, t, c->mapped + n * kOvRegion);
            c->region[n].key = k, c->region[n].at = g_ovl.at;
            from = r, c->cur = r = n;
        }
    }
    const auto& now = c->region[r].key;
    const RECT& at = c->region[r].at;
    if (now.w != k.w || now.h != k.h || now.f != k.f || at.right <= at.left) return S_FALSE;  // laid out for another buffer, or nothing fits
    // one the GPU is past: as recorded for this panel, else recorded again
    OvChain::List* use = nullptr;
    for (auto& l : b.lists[r])
        if (past >= l.sent && EqualRect(&l.at, &at)) { use = &l; break; }
    for (auto& l : b.lists[r])
        if (!use && past >= l.sent) use = &l;
    if (!use) return S_FALSE;
    if (!EqualRect(&use->at, &at)) {
        if (!use->alloc && FAILED(hr = c->make(*use))) return hr;  // a buffer index past the ones made at the start
        if (FAILED(hr = use->alloc->Reset()) || FAILED(hr = use->list->Reset(use->alloc, nullptr))) return hr;
        auto* l = use->list;
        D3D12_RESOURCE_BARRIER rb = {};
        rb.Transition = {buf, D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_DEST};
        l->ResourceBarrier(1, &rb);
        D3D12_TEXTURE_COPY_LOCATION dst = {buf, D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX}, src = {c->upload, D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT};
        dst.SubresourceIndex = 0;
        src.PlacedFootprint = {(UINT64)r * kOvRegion, {d.Format, UINT(at.right - at.left), UINT(at.bottom - at.top), 1, (UINT)kOvPitch}};
        l->CopyTextureRegion(&dst, at.left, at.top, 0, &src, nullptr);
        std::swap(rb.Transition.StateBefore, rb.Transition.StateAfter);
        l->ResourceBarrier(1, &rb);
        if (FAILED(hr = l->Close())) return hr;
        use->at = at;
    }
    ID3D12CommandList* l = use->list;
    c->queue->ExecuteCommandLists(1, &l);
    if (FAILED(hr = c->queue->Signal(c->fence, use->sent = ++c->value))) return hr;
    if (from >= 0) c->region[from].free_at = c->value;
    return S_OK;
}
// The D3D11 panel texture, in the swap chain's format: CopySubresourceRegion copies only between formats of a family.
static HRESULT overlay_tex11(OvChain* c, DXGI_FORMAT f) {
    if (c->tex11) c->tex11->Release(), c->tex11 = nullptr;
    D3D11_TEXTURE2D_DESC td = {(UINT)ov::kMaxW, (UINT)ov::kMaxH, 1, 1, f, {1, 0}, D3D11_USAGE_DEFAULT};
    ID3D11Device* dev = nullptr;
    c->ctx11->GetDevice(&dev);
    HRESULT hr = dev->CreateTexture2D(&td, nullptr, &c->tex11);
    dev->Release();
    c->fmt11 = SUCCEEDED(hr) ? f : DXGI_FORMAT_UNKNOWN, c->key11 = {};
    return hr;
}
// D3D11. Present uses the immediate context too, so the game already keeps its own calls on it apart from this one.
static HRESULT overlay_draw11(OvChain* c, ID3D11Texture2D* buf, const OvText& t) {
    D3D11_TEXTURE2D_DESC d;
    buf->GetDesc(&d);
    const ov::Out out = c->out(d.Format);
    if (!out || !(d.BindFlags & D3D11_BIND_RENDER_TARGET) || d.SampleDesc.Count > 1) return S_FALSE;
    const OvKey k = ov_key(d.Width, d.Height, d.Format, out, t);
    HRESULT hr;
    if (c->fmt11 != d.Format && FAILED(hr = overlay_tex11(c, d.Format))) return hr;  // a ResizeBuffers to another format
    if (!(c->key11 == k)) {
        overlay_paint(c, k, t, c->px11.data());
        const RECT& p = g_ovl.at;
        D3D11_BOX box = {0, 0, 0, UINT(p.right - p.left), UINT(p.bottom - p.top), 1};
        if (p.right > p.left) c->ctx11->UpdateSubresource(c->tex11, 0, &box, c->px11.data(), kOvPitch, 0);
        c->key11 = k, c->at11 = p;
    }
    const RECT& at = c->at11;
    if (at.right <= at.left) return S_FALSE;
    D3D11_BOX box = {0, 0, 0, UINT(at.right - at.left), UINT(at.bottom - at.top), 1};
    c->ctx11->CopySubresourceRegion(buf, 0, at.left, at.top, 0, c->tex11, 0, &box);
    return S_OK;
}
static __declspec(noinline) HRESULT overlay_draw(void* sc, OvChain* c, const OvText& t) {
    HRESULT hr;
    if (c->ctx11) {
        ID3D11Texture2D* buf = nullptr;
        if (FAILED(hr = ((IDXGISwapChain*)sc)->GetBuffer(0, IID_PPV_ARGS(&buf)))) return hr;
        hr = overlay_draw11(c, buf, t);
        buf->Release();
        return hr;
    }
    UINT i = ((IDXGISwapChain3*)sc)->GetCurrentBackBufferIndex();
    ID3D12Resource* buf = nullptr;
    if (i >= DXGI_MAX_SWAP_CHAIN_BUFFERS) return S_FALSE;
    if (FAILED(hr = ((IDXGISwapChain3*)sc)->GetBuffer(i, IID_PPV_ARGS(&buf)))) return hr;
    hr = overlay_draw12(c, buf, i, t);
    buf->Release();
    return hr;
}
static HRESULT overlay_draw_guarded(void* sc, OvChain* c, const OvText& t) {
    __try {
        return overlay_draw(sc, c, t);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return E_UNEXPECTED;
    }
}

static void entry_forget(void* sc, OvChain* c);
// A present of a swap chain the overlay draws on, before the original Present: the frames fed since, then the panel.
// Nothing here allocates or waits. True when it is such a swap chain.
static bool overlay_present(void* sc) {
    if (!g_ovc.on || g_ov_off.load(std::memory_order_relaxed)) return false;
    IUnknown* u = nullptr;
    UINT size = sizeof u;
    if (FAILED(((IDXGIObject*)sc)->GetPrivateData(kOvChain, &size, &u)) || !u) return false;  // GetPrivateData AddRefs it
    if (std::unique_lock l(g_ovmx, std::try_to_lock); l && !((OvChain*)u)->retired.load(std::memory_order_relaxed)) {
        g_ovfeed.drain([](double t) {
            double prev = g_ovclock.last, played = g_ovclock.played;
            if (g_ovclock.frame(t)) g_ov_startup.store(g_ovclock.startup, std::memory_order_relaxed);
            if (prev < 0) return;
            g_ovgraph.frame(prev, t);
            if (played < g_ovc.play_s * 1000.0) g_ovslow.frame(prev, t);  // the report's play cap: a frame starting after it isn't counted
        });
        double t = now_ms();
        g_ovslow.check(g_ovcreates, t, g_ovclock.startup, g_ovgraph);
        g_ov_stutters.store(g_ovslow.found, std::memory_order_relaxed);
        double total = g_ovc.play_s * 1000.0, played = g_ovclock.shown(t), frac = played / total;
        bool done = played >= total;
        if (done && !g_ov_done) g_ov_done = true, g_ov_done_seen.store(true, std::memory_order_relaxed);
        char left[48], widest[48];
        ov::countdown(g_ovc.after, (total - played) / 1000, left), ov::countdown(g_ovc.after, g_ovc.play_s, widest);
        OvText text = {done ? ov::kDone : left, widest, frac, done, g_ovslow.flashing(t), done ? 0 : (long)std::ceil((total - played) / 1000)};
        HRESULT hr = overlay_draw_guarded(sc, (OvChain*)u, text);
        if (hr == E_CHANGED_STATE) entry_forget(sc, (OvChain*)u);
        else if (hr == E_UNEXPECTED) overlay_off("a fault in its draw", hr);
        else if (FAILED(hr)) overlay_off("its draw failed", hr);
        else if (hr == S_OK) g_ov_drew = true;
    }
    u->Release();
    return true;
}

// The original Present one level deeper on this thread. No objects to unwind here (__try): the depth comes back on a
// structured exception an outer handler catches too.
static HRESULT present_in(HRESULT (*body)(void*), void* call, int& depth) {
    HRESULT hr = E_FAIL;
    ++depth;
    __try {
        hr = body(call);
    } __finally {
        --depth;
    }
    return hr;
}
static bool g_entry;  // Present is hooked at dxgi's entry (entry_hooks): no slot of a dxgi vtable is ever ours
// dxgi's entry draws the panel once frame generation took the slots back (entry_hooks with Signal): never its swap chains
static bool g_entry_draws;
template <class F> static HRESULT timed_present(void* sc, UINT flags, F&& call, bool game = false) {
    LARGE_INTEGER a, b, c, t;
    QueryPerformanceCounter(&a);
    int& depth = game ? t_fg_present : t_present;
    using Call = std::remove_reference_t<F>;
    HRESULT (*body)(void*) = [](void* p) { return (*(Call*)p)(); };
    if (depth || (flags & DXGI_PRESENT_TEST)) return present_in(body, (void*)&call, depth);
    bool feeds = game;
    if (game) {
        g_ov_game.store(true, std::memory_order_relaxed);
        if (!g_entry && !g_entry_draws) overlay_present(sc);  // the game's frame, before frame generation takes it
    } else feeds = overlay_present(sc) && !g_ov_game.load(std::memory_order_relaxed);
    QueryPerformanceCounter(&b);
    HRESULT hr = present_in(body, (void*)&call, depth);
    QueryPerformanceCounter(&c);
    trav::frame(now_ms());
    if (feeds && g_ovc.on && SUCCEEDED(hr)) g_ovfeed.push(now_ms());
    t_presenting = true;
    {
        // a failed present (DXGI_ERROR_WAS_STILL_DRAWING from a DO_NOT_WAIT retry) shows no frame; the frame's time is
        // taken under the lock so two threads' frames queue in the order they returned
        std::lock_guard l(g_fmx);
        QueryPerformanceCounter(&t);
        if (SUCCEEDED(hr)) g_fq.push_back({t.QuadPart, sc, game});
    }
    LARGE_INTEGER d;
    QueryPerformanceCounter(&d);
    int64_t own = (b.QuadPart - a.QuadPart) + (d.QuadPart - c.QuadPart);
    g_hook_ticks += own, ++g_presents;
    for (int64_t m = g_hook_max; own > m && !g_hook_max.compare_exchange_weak(m, own);) {}
    return hr;
}
// The panel only: frame generation's frames are timed elsewhere.
template <class F> static HRESULT drawn_present(void* sc, UINT flags, F&& call, int& depth = t_present) {
    using Call = std::remove_reference_t<F>;
    if (!depth && !(flags & DXGI_PRESENT_TEST)) overlay_present(sc);
    return present_in([](void* p) { return (*(Call*)p)(); }, (void*)&call, depth);
}
static std::atomic<bool> g_fg_off;  // set under g_mx: frame generation made its swap chain, dxgi's slots are never hooked again
// A Present of ours entered once dxgi's slots went back for frame generation: a hooker kept ours as its original.
static void fg_late_present(void* sc, void* caller, const char* api);
// Our Present entered 64 deep on its thread (Present and Present1 together): hooks that call each other through it. That
// present fails instead of the stack overflowing.
static HRESULT present_cut(const char* api) {
    static std::atomic<bool> logged;
    if (!logged.exchange(true)) logf("frames: %s 64 deep on thread %lu: refused (DXGI_ERROR_INVALID_CALL), the hooks call each other", api, GetCurrentThreadId());
    return DXGI_ERROR_INVALID_CALL;
}
static HRESULT STDMETHODCALLTYPE hk_present(IDXGISwapChain* sc, UINT sync, UINT flags) {
    if (t_present >= 63) return present_cut("Present");
    if (g_fg_off.load(std::memory_order_relaxed)) fg_late_present(sc, _ReturnAddress(), "Present");
    auto o = (PFN_Present)sc_orig(sc, &ScVt::present);
    return timed_present(sc, flags, [&] { return o(sc, sync, flags); });
}
static HRESULT STDMETHODCALLTYPE hk_present1(IDXGISwapChain1* sc, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* p) {
    if (t_present >= 63) return present_cut("Present1");
    if (g_fg_off.load(std::memory_order_relaxed)) fg_late_present(sc, _ReturnAddress(), "Present1");
    auto o = (PFN_Present1)sc_orig(sc, &ScVt::present1);
    return timed_present(sc, flags, [&] { return o(sc, sync, flags, p); });
}

// Only System32's dxgi.dll's vtables: a wrapper's (OptiScaler's, Streamline's) Present expects its own object, and sc_orig
// may hand a vtable's original an object of another vtable.
static HMODULE g_sysdxgi;
static bool g_dxgi_layer;  // the game's dxgi.dll isn't System32's, though its factory's vtable is
static bool sys_vtable(void** vt, const char* what, int slot, bool loud = true) {
    HMODULE m = module_of(vt);
    if (m && m == g_sysdxgi) return true;
    if (!loud) return false;
    wchar_t path[MAX_PATH] = L"no module";
    if (m) GetModuleFileNameW(m, path, MAX_PATH);
    logf("frames: %s vtable %p slot %d is in %s, not System32's dxgi.dll: not hooked", what, (void*)vt, slot, shown(path).c_str());
    return false;
}

static int g_creating;  // under g_mx: creates in flight (a wrapper's create calls the real one)
// Under g_mx: ffxCreateContext calls in flight, on any thread. No slot goes back until the last returns: a frame generation
// swap chain its create makes is known only once it has (hk_ffx_create), whatever thread or factory proxy made it.
static int g_ffx_in;
static std::atomic<bool> g_ffx_hooked;  // an ffxCreateContext is hooked: fg_hook times frame generation's own swap chain
// This thread's create is one the log shows: the create path's other lines are capped with it.
static thread_local bool t_sc_loud = true;
static void sc_patch(void** vt, int slot, void* hook, std::atomic<void*> ScVt::*orig) {
    if (g_entry || g_fg_off || vt[slot] == hook || !sys_vtable(vt, "swap chain", slot, t_sc_loud)) return;
    int n = g_nscvt, i = 0;
    while (i < n && g_scvt[i].vt != vt) ++i;
    // an overlay hooked the slot after us and calls our hook: taking it as the original would make Present call itself
    if (i < n && g_scvt[i].*orig && g_scvt[i].*orig != vt[slot]) {
        if (t_sc_loud) logf("frames: swap chain vtable %p slot %d is another hook's since ours went in: not hooked again", (void*)vt, slot);
        return;
    }
    if (i == std::size(g_scvt)) {
        if (t_sc_loud) logf("frames: a %zuth swap chain vtable, not hooked", std::size(g_scvt) + 1);
        return;
    }
    g_scvt[i].*orig = vt[slot];  // before the slot: a present may run as soon as it's patched
    g_scvt[i].vt = vt;
    if (i == n) g_nscvt.store(n + 1, std::memory_order_release);
    if (g_creating || g_ffx_in) return;  // inside a create or an ffxCreateContext: the last of them to end puts the hook in
    DWORD old;
    VirtualProtect(&vt[slot], sizeof(void*), PAGE_READWRITE, &old);
    vt[slot] = hook;
    VirtualProtect(&vt[slot], sizeof(void*), old, &old);
    logf("frames: swap chain vtable %p slot %d hooked", (void*)vt, slot);
}
// OptiScaler.ini naming a frame generation output, whether or not its menu has it on: Opti makes frame generation's swap
// chain at the game's create either way. 0.9.4's FGOutput: nofg, fsrfg, xefg, nukems (auto: nofg); any other value counts
// as an output. An older ini without FGOutput: an FGType key but nofg.
static bool g_opti_fgout;          // opti_fg_output(), read once in frame_hooks
static std::string g_opti_fgname;  // its key and value, for the log
static bool opti_fg_output() {
    const std::wstring ini = g_dir + L"OptiScaler.ini";
    wchar_t v[32];
    if (GetPrivateProfileStringW(L"FrameGen", L"FGOutput", L"", v, 32, ini.c_str()))
        return g_opti_fgname = "FGOutput=" + utf8(v), _wcsicmp(v, L"nofg") && _wcsicmp(v, L"auto");
    return GetPrivateProfileStringW(L"FrameGen", L"FGType", L"", v, 32, ini.c_str()) && (g_opti_fgname = "FGType=" + utf8(v), _wcsicmp(v, L"nofg"));
}
static std::wstring queue_name(IUnknown* dev) {
    ID3D12CommandQueue* q;
    if (!dev || FAILED(dev->QueryInterface(IID_PPV_ARGS(&q)))) return L"";
    wchar_t n[128] = {};
    UINT size = sizeof n - sizeof(wchar_t);
    if (FAILED(q->GetPrivateData(WKPDID_D3DDebugObjectNameW, &size, n))) size = 0;
    q->Release();
    return std::wstring(n, size / sizeof(wchar_t));
}
using PFN_SetCs = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain3*, DXGI_COLOR_SPACE_TYPE);
// IDXGISwapChain3::SetColorSpace1: an overlaid swap chain's colour space, the one way to tell 10-bit SDR from HDR10.
// Never under the draw path's lock, as the original may wait on presents: a present racing the change draws one frame
// with the colour space before.
// SetColorSpace1 is hooked at dxgi's entry (entry_hooks) before any swap chain is made: each one's colour space is kept in
// its private data, none there is the one a swap chain starts in.
static const GUID kOvCs = {0x5b7e2c41, 0x0d3a, 0x4e8f, {0xa6, 0x19, 0x72, 0xc4, 0x3e, 0x8b, 0x51, 0xd0}};
static PFN_SetCs o_entry_setcs;
static thread_local bool t_setcs;  // our slot hook's original runs: dxgi's entry hook under it only keeps the colour space
static HRESULT setcs_by(IDXGISwapChain3* sc, DXGI_COLOR_SPACE_TYPE cs, PFN_SetCs o) {
    if (t_setcs) {
        HRESULT hr = o(sc, cs);
        IUnknown* u = nullptr;
        UINT size = sizeof u;
        if (const int v = cs; SUCCEEDED(hr)) sc->SetPrivateData(kOvCs, sizeof v, &v);
        if (SUCCEEDED(hr) && SUCCEEDED(sc->GetPrivateData(kOvChain, &size, &u)) && u) ((OvChain*)u)->cs = cs, u->Release();
        return hr;
    }
    IUnknown* u = nullptr;
    UINT size = sizeof u;
    if (FAILED(sc->GetPrivateData(kOvChain, &size, &u))) u = nullptr;
    t_setcs = true;
    HRESULT hr = o(sc, cs);
    t_setcs = false;
    if (const int v = cs; SUCCEEDED(hr)) sc->SetPrivateData(kOvCs, sizeof v, &v);
    if (u && SUCCEEDED(hr) && ((OvChain*)u)->cs.exchange(cs) != (int)cs)
        logf("overlay: swap chain %p colour space %d%s", (void*)sc, (int)cs,
             cs == DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709 ? " (SDR)" : cs == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 ? " (HDR10)"
             : cs == DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709 ? " (scRGB)" : ": not drawn");
    if (u) u->Release();
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_setcs(IDXGISwapChain3* sc, DXGI_COLOR_SPACE_TYPE cs) { return setcs_by(sc, cs, (PFN_SetCs)sc_orig(sc, &ScVt::setcs)); }
static HRESULT STDMETHODCALLTYPE hk_entry_setcs(IDXGISwapChain3* sc, DXGI_COLOR_SPACE_TYPE cs) { return setcs_by(sc, cs, o_entry_setcs); }

// The OvChain goes into the swap chain's private data, then SetColorSpace1 is hooked on its vtable (inside another
// create, by its outermost swap_slots). Without IDXGISwapChain3 the colour space stays unknown: 10-bit isn't drawn.
// The swap chain's panel replaced by c (null: none). The one before is retired under the draw path's lock before c is
// published, so no present draws with it after. Released after: its destructor may wait for the GPU.
static HRESULT ov_replace(IDXGIObject* o, OvChain* c) {
    IUnknown* old = nullptr;
    UINT size = sizeof old;
    if (FAILED(o->GetPrivateData(kOvChain, &size, &old))) old = nullptr;
    if (old) {
        std::unique_lock l(g_ovmx, std::defer_lock);
        for (const double end = now_ms() + 200; !l.try_lock() && now_ms() < end;) Sleep(1);
        if (!l) logf("overlay: swap chain %p: its panel retired while a present drew", (void*)o);
        ((OvChain*)old)->retired = true;
    }
    HRESULT hr = o->SetPrivateDataInterface(kOvChain, c);
    if (old) old->Release();
    return hr;
}
static HRESULT overlay_chain(IUnknown* p, OvChain* c) {
    IDXGISwapChain* sc = nullptr;
    IDXGISwapChain3* sc3 = nullptr;
    HRESULT hr = p->QueryInterface(IID_PPV_ARGS(&sc));
    if (SUCCEEDED(hr) && SUCCEEDED(hr = ov_replace(sc, c)) && SUCCEEDED(p->QueryInterface(IID_PPV_ARGS(&sc3)))) {
        std::lock_guard l(g_mx);
        void** vt = *(void***)sc3;
        sc_patch(vt, SLOT_SETCS, (void*)hk_setcs, &ScVt::setcs);
        c->cs_known = vt[SLOT_SETCS] == (void*)hk_setcs || ((g_creating || g_ffx_in) && std::any_of(g_scvt, g_scvt + g_nscvt, [&](const ScVt& e) { return e.vt == vt && e.setcs; }));
        if (int v; o_entry_setcs && module_of(vt) == g_sysdxgi) {
            UINT n = sizeof v;
            if (SUCCEEDED(sc3->GetPrivateData(kOvCs, &n, &v)) && n == sizeof v) c->cs = v;
            c->cs_known = true;
        }
        sc3->Release();
    }
    if (sc) sc->Release();
    return hr;
}

// A D3D11 swap chain: drawn on with its device's immediate context.
static void overlay_attach11(IUnknown* dev, IUnknown* p) {
    IDXGISwapChain* sc = nullptr;
    ID3D11Device* d = nullptr;
    DXGI_SWAP_CHAIN_DESC sd = {};
    const char* why = FAILED(p->QueryInterface(IID_PPV_ARGS(&sc))) || FAILED(sc->GetDesc(&sd)) ? "no IDXGISwapChain"
                      : FAILED(dev ? dev->QueryInterface(IID_PPV_ARGS(&d)) : sc->GetDevice(IID_PPV_ARGS(&d))) ? "not a D3D11 device"
                      : !ov::drawn_format(sd.BufferDesc.Format) ? "a format it doesn't draw"
                      : !(sd.BufferUsage & DXGI_USAGE_RENDER_TARGET_OUTPUT) ? "not a render target"
                      : sd.SampleDesc.Count > 1 ? "multisampled"
                      : nullptr;
    if (why) logf("overlay: swap chain %p (format %u) not drawn: %s", (void*)p, (unsigned)sd.BufferDesc.Format, why);
    else {
        auto* c = new OvChain;
        c->px11.resize(kOvPitch * ov::kMaxH);
        d->GetImmediateContext(&c->ctx11);
        const char* what = "CreateTexture2D";
        HRESULT hr = overlay_tex11(c, sd.BufferDesc.Format);
        if (SUCCEEDED(hr)) what = "SetPrivateDataInterface", hr = overlay_chain(p, c);
        c->Release();
        if (FAILED(hr)) overlay_off(what, hr);
        else logf("overlay: swap chain %p %ux%u format %u (D3D11): drawn on", (void*)p, sd.BufferDesc.Width, sd.BufferDesc.Height, (unsigned)sd.BufferDesc.Format);
    }
    if (d) d->Release();
    if (sc) sc->Release();
}

// A swap chain the hooked factory made (dev null: D3D11CreateDeviceAndSwapChain's): drawn on when every gate passes, else
// logged why not.
// fg: drawn on under frame generation too (its own swap chains, Streamline's proxy, a queue dxgi's entry proved).
// proof: its queue was proven by a present after its buffers got this tag (proof_tag).
static void overlay_attach(IUnknown* dev, IUnknown* p, bool fg = false, uint32_t proof = 0) {
    if (!g_ovc.on || g_ov_off) return;
    if (g_fg_off && !g_entry && !fg && g_entry_draws) return;  // its first present at dxgi's entry decides
    if (g_fg_off && !g_entry && !fg) return logf("overlay: swap chain %p not drawn: frame generation's swap chain unhooked dxgi's Present", (void*)p);
    std::wstring q = queue_name(dev);
    if (g_api11) return overlay_attach11(dev, p);
    DXGI_SWAP_CHAIN_DESC sd = {};
    ID3D12CommandQueue* queue = nullptr;
    IDXGISwapChain3* sc = nullptr;
    const char* why = !dev || FAILED(dev->QueryInterface(IID_PPV_ARGS(&queue))) ? "not a D3D12 queue"
                      : queue->GetDesc().Type != D3D12_COMMAND_LIST_TYPE_DIRECT ? "not a direct queue"
                      : FAILED(p->QueryInterface(IID_PPV_ARGS(&sc))) || FAILED(sc->GetDesc(&sd)) ? "no IDXGISwapChain3"
                      : !ov::drawn_format(sd.BufferDesc.Format) ? "a format it doesn't draw"
                      : !(sd.BufferUsage & DXGI_USAGE_RENDER_TARGET_OUTPUT) ? "not a render target"
                      : nullptr;
    if (sc) sc->Release();
    if (why) {
        if (queue) queue->Release();
        return logf("overlay: swap chain %p (format %u) not drawn: %s", (void*)p, (unsigned)sd.BufferDesc.Format, why);
    }
    auto* c = new OvChain;
    c->queue = queue;  // its reference: the OvChain signals on it as it goes
    c->proof = proof;
    D3D12_HEAP_PROPERTIES hp = {D3D12_HEAP_TYPE_UPLOAD};
    D3D12_RESOURCE_DESC bd = {D3D12_RESOURCE_DIMENSION_BUFFER, 0, OvChain::kRegions * (UINT64)kOvRegion, 1, 1, 1, DXGI_FORMAT_UNKNOWN, {1, 0}, D3D12_TEXTURE_LAYOUT_ROW_MAJOR};
    D3D12_RANGE none = {};
    const char* what = "GetDevice";
    HRESULT hr = queue->GetDevice(IID_PPV_ARGS(&c->dev));
    if (SUCCEEDED(hr)) what = "CreateFence", hr = c->dev->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&c->fence));
    if (SUCCEEDED(hr)) what = "CreateCommittedResource", hr = c->dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &bd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&c->upload));
    if (SUCCEEDED(hr)) what = "Map", hr = c->upload->Map(0, &none, (void**)&c->mapped);
    for (UINT i = 0; i < std::min<UINT>(sd.BufferCount, DXGI_MAX_SWAP_CHAIN_BUFFERS) && SUCCEEDED(hr); ++i)  // none made in a present
        for (auto& region : c->buf[i].lists)
            for (auto& l : region)
                if (SUCCEEDED(hr)) what = "CreateCommandList", hr = c->make(l);
    if (SUCCEEDED(hr)) what = "SetPrivateDataInterface", hr = overlay_chain(p, c);
    c->Release();
    if (FAILED(hr)) return overlay_off(what, hr);
    logf("overlay: swap chain %p %ux%u format %u on queue \"%s\": drawn on", (void*)p, sd.BufferDesc.Width, sd.BufferDesc.Height,
         (unsigned)sd.BufferDesc.Format, shown(q).c_str());
}
// Steam's overlay inline-detours dxgi's Present and, when a swap chain is made (frame generation's: FSR 3, DLSS-G), takes
// the vtable slot as the original it calls: with our hook there, its Present and ours call each other until the stack
// overflows. So every CreateSwapChain* runs with dxgi's originals in the slots and ours go back after, by
// compare-exchange: a slot another hook took meanwhile stays theirs. Frame generation's own present queue is logged by
// name (FSR 3, also under OptiScaler and dlssg-to-fsr3: "AMD FSR PresentQueue").
// Once frame generation has made its swap chain, the originals stay for the launch (g_fg_off): a hooker that makes its own
// swap chain (D3D11CreateDeviceAndSwapChain on a hidden window) reads the slot after that create returns, so only dxgi's
// own Present there keeps it from taking ours. The game's frames then come from ffxCreateContext's swap chain (fg_hook),
// and the in-game panel, drawn in dxgi's Present, isn't drawn any more.
static bool fg_queue(const std::wstring& name) {
    for (auto p : {L"AMD FSR PresentQueue", L"XefgInterpolationSwapChain::present_queue_"})
        if (!name.compare(0, wcslen(p), p)) return true;
    return false;
}
static void swap_slots(bool ours) {
    if (ours && g_fg_off) return;
    for (int i = 0, n = g_nscvt; i < n; ++i)
        for (auto [slot, hook, orig] : {std::tuple{SLOT_PRESENT, (void*)hk_present, &ScVt::present}, std::tuple{SLOT_PRESENT1, (void*)hk_present1, &ScVt::present1},
                                        std::tuple{SLOT_SETCS, (void*)hk_setcs, &ScVt::setcs}}) {
            void** vt = g_scvt[i].vt;
            void* o = g_scvt[i].*orig;
            if (!o) continue;
            DWORD old;
            VirtualProtect(&vt[slot], sizeof(void*), PAGE_READWRITE, &old);
            void* was = InterlockedCompareExchangePointer(&vt[slot], ours ? hook : o, ours ? o : hook);
            VirtualProtect(&vt[slot], sizeof(void*), old, &old);
            if (was != (ours ? o : hook) && was != (ours ? hook : o) && t_sc_loud)
                logf("frames: swap chain vtable %p slot %d is another hook's, left as it is", (void*)vt, slot);
        }
}
// A create's arguments, for its log lines. window: the HWND or CoreWindow, null for composition.
struct ScArgs {
    const char* api;
    UINT w, h, format, buffers, effect, flags;
    void *window, *caller;
    uint32_t n;  // the process's nth create
    bool loud, outer_loud;  // this create's line is written; the create it is nested in, the same
};
static ScArgs sc_args(const char* api, const DXGI_SWAP_CHAIN_DESC* d, void* caller) {
    if (!d) return {api, 0, 0, 0, 0, 0, 0, nullptr, caller};
    return {api, d->BufferDesc.Width, d->BufferDesc.Height, (UINT)d->BufferDesc.Format, d->BufferCount, (UINT)d->SwapEffect, d->Flags, d->OutputWindow, caller};
}
static ScArgs sc_args(const char* api, const DXGI_SWAP_CHAIN_DESC1* d, void* window, void* caller) {
    if (!d) return {api, 0, 0, 0, 0, 0, 0, window, caller};
    return {api, d->Width, d->Height, (UINT)d->Format, d->BufferCount, (UINT)d->SwapEffect, d->Flags, window, caller};
}
static std::atomic<uint32_t> g_sc_creates, g_sc_failed;
// This thread's creates in flight: a hook that calls back into ours from inside the create climbs it.
static thread_local int t_sc_depth;
static thread_local bool t_fg_said;  // fg_said waits for the create around this one: fg_made_in
static thread_local bool t_fs30;     // fg_said waits for fs30_tick: fg_made_in hands it this create's swap chain
// The first 8 creates, then each power of two: a create storm can't flood the log or slow the creates.
static bool sc_logged(uint32_t n) { return n <= 8 || !(n & (n - 1)); }
// The file name only: a log line carries no user's folders.
static const wchar_t* module_file(const void* p, wchar_t (&path)[MAX_PATH]) {
    HMODULE m = module_of(p);
    if (!m || !GetModuleFileNameW(m, path, MAX_PATH)) return L"no module";
    const wchar_t* name = wcsrchr(path, L'\\');
    return name ? name + 1 : path;
}
// The file name of the module p is in, whatever its path's length: "" outside any.
static std::wstring module_name(const void* p) {
    HMODULE m = module_of(p);
    const std::wstring s = m ? module_path(m) : L"";
    return s.substr(s.find_last_of(L'\\') + 1);
}
// A create called from FidelityFX or the driver's FSR 4 module.
static bool fg_caller(const void* caller) {
    const std::wstring name = module_name(caller);
    return !_wcsnicmp(name.c_str(), L"amdxcffx", 8) || !_wcsnicmp(name.c_str(), L"amd_fidelityfx_", 15);
}
// Streamline making a swap chain for a window one of ours already made one for, with a frame generation dll loaded:
// DLSS-G (or another frame generation under Streamline) remaking the game's, which it then presents through dxgi's.
// A game that remakes its own swap chain through Streamline with that dll loaded loses its frame times from there.
// On this create's call stack, also under another layer's factory (ReShade's forwards Streamline's create). Takes the
// loader lock.
static bool sl_caller() {
    void* frames[32];
    const USHORT n = RtlCaptureStackBackTrace(1, 32, frames, nullptr);
    HMODULE seen = nullptr;
    for (USHORT i = 0; i < n; ++i)
        if (HMODULE m = module_of(frames[i]); m && m != seen && (seen = m, !_wcsicmp(module_name(frames[i]).c_str(), L"sl.interposer.dll"))) return true;
    return false;
}
// A window a create of ours made a swap chain for: a property on the window itself, gone with it, so a recycled handle has
// none. One atom for every window. A CoreWindow (not an HWND) isn't marked: Streamline's remake rule never fires there.
static bool sc_hwnd(const ScArgs& a) { return a.window && strcmp(a.api, "CreateSwapChainForCoreWindow"); }
static LPCWSTR sc_window_prop() {
    static const ATOM atom = GlobalAddAtomW(L"SCSKiller.swapchain");
    return atom ? (LPCWSTR)(ULONG_PTR)atom : nullptr;
}
static bool sc_window_seen(const ScArgs& a) { return sc_hwnd(a) && sc_window_prop() && GetPropW((HWND)a.window, sc_window_prop()); }
static void fg_late_present(void* sc, void* caller, const char* api) {
    static std::atomic<bool> logged;
    if (logged.exchange(true)) return;  // once a launch: the first is the hooker's, the rest repeat it
    wchar_t path[MAX_PATH];
    logf("frames: our %s entered after frame generation unhooked dxgi's (swap chain %p, called from %s): another hook kept ours as its original",
         api, sc, utf8(module_file(caller, path)).c_str());
}
static void sc_line(IUnknown* dev, const ScArgs& a, const char* result) {
    wchar_t qcode[MAX_PATH], from[MAX_PATH];
    // the queue's debug name is the game's: reduced if it is a path
    logf("frames: a swap chain on queue \"%s\"%s (%s, create %u, thread %lu %d deep, %u failed so far; %ux%u format %u, %u buffers, "
         "swap effect %u, flags 0x%x, window %p; queue's code in %s; called from %s)",
         shown(queue_name(dev)).c_str(), result, a.api, a.n, GetCurrentThreadId(), t_sc_depth, g_sc_failed.load(), a.w, a.h, a.format,
         a.buffers, a.effect, a.flags, a.window, utf8(module_file(dev ? *(void**)dev : nullptr, qcode)).c_str(), utf8(module_file(a.caller, from)).c_str());
}
// A code address as module+offset (the file name only), or the bare address outside any module.
static std::string code_at(const void* p) {
    wchar_t path[MAX_PATH];
    HMODULE m = module_of(p);
    char off[32];
    if (!m) return snprintf(off, sizeof off, "%p", p), off;
    snprintf(off, sizeof off, "+0x%llx", (unsigned long long)((const BYTE*)p - (const BYTE*)m));
    return utf8(module_file(p, path)) + off;
}
// A create hook entered again on its own thread, inside its own create: at 3 deep, once per process, who calls whom (the
// original about to be called, whose hook holds the slot now, the caller, the stack). At 64 deep the create is refused
// without calling the original, so a cycle of hooks fails the create instead of overflowing the stack. True: refuse it.
static std::atomic<bool> g_sc_traced, g_sc_cut;
static bool sc_cycle(const char* api, void** vt, int slot, const void* orig, const void* caller) {
    if (t_sc_depth == 2 && !g_sc_traced.exchange(true)) {
        void* frames[24];
        USHORT n = CaptureStackBackTrace(0, 24, frames, nullptr);
        std::string stack;
        for (USHORT i = 0; i < n; ++i) stack += (i ? " < " : "") + code_at(frames[i]);
        logf("frames: %s 3 deep on thread %lu: calls its original %p (%s), the slot holds %s, called from %s", api, GetCurrentThreadId(), orig,
             code_at(orig).c_str(), vt[slot] == orig ? "that original" : code_at(vt[slot]).c_str(), code_at(caller).c_str());
        logf("frames: %s 3 deep, stack: %s", api, stack.c_str());
    }
    if (t_sc_depth < 63) return false;
    if (!g_sc_cut.exchange(true)) logf("frames: %s 64 deep on thread %lu: refused (DXGI_ERROR_INVALID_CALL), the hooks call each other", api, GetCurrentThreadId());
    return true;
}
static void fg_off_now(bool timed);
// The swap chain the game presents to once a rendered frame under Streamline's frame generation: its proxy (from
// slUpgradeInterface, its factory's creates, or a create of ours that returned it). Not held: the game's to release.
static std::atomic<void*> g_sl_obj;
static std::atomic<uintptr_t> g_sl_vt;  // its vtable, bit 0 set when it answers IDXGISwapChain1 with it: read as one
static void** sl_vt(uintptr_t t, bool& one) { return one = t & 1, (void**)(t & ~(uintptr_t)1); }
static std::atomic<bool> g_sl_fg;  // Streamline's frame generation took dxgi's slots: its proxy, once known, is timed
// Streamline's PCL marker function (slPCLSetMarker, slReflexSetMarker before PCL), hooked where it lives once the game
// fetches it: after its frame generation takes dxgi's slots, each PresentEnd is a frame the game rendered. g_slm: 0 off,
// 1 the function to fetch on the game's next slSetConstants, 2 on until the first PresentEnd, 3 on, 4 none came, 5 being
// fetched.
static void* volatile o_sl_marker[2];
static void* volatile o_sl_getfn;
static void* volatile o_sl_consts;
static std::atomic<int> g_slm;
static void slm_said(bool known);
static bool fg_vt_ok(void** vt);
static bool fg_hook_vt(const void* sc, void** vt, bool one);
static bool sc_has1(IUnknown* p);
static void sl_upgraded(IUnknown* p, const char* from, IUnknown* dev = nullptr);
// Streamline with DLSS frame generation loaded: only then do its hooks go in. An sl.interposer.dll without it (a game's
// Streamline on AMD under OptiScaler) is left as it is.
static std::atomic<bool> g_sl_on, g_dlssg_seen;
static void creating(IUnknown* dev, ScArgs& a) {
    a.n = ++g_sc_creates;
    ++t_sc_depth;
    a.outer_loud = t_sc_loud;
    t_sc_loud = a.loud = sc_logged(a.n);
    if (t_sc_loud) sc_line(dev, a, "");
    // OptiScaler with a frame generation output: a create nested in ours is its detour making that output's swap chain,
    // whichever module calls it (libxess_fg.dll for XeFG)
    const bool by_fg = fg_caller(a.caller);
    bool fg = by_fg || fg_queue(queue_name(dev)) || (g_opti_fgout && t_sc_depth > 1);
    // here, not under g_mx: GetModuleHandle takes the loader lock. sl_fg: no swap chain of its own to time.
    const bool sl_fg = !fg && sl_caller() && fg_module() && sc_window_seen(a);
    const bool slm = sl_fg && (o_sl_marker[0] || o_sl_marker[1]);
    bool one = false;
    void** const slvt = sl_fg && !slm ? sl_vt(g_sl_vt, one) : nullptr;  // markers time: the proxy is only drawn on
    const bool sl_timed = slvt && fg_vt_ok(slvt);
    {
        std::lock_guard l(g_mx);
        if (!g_creating++) swap_slots(false);
        if (!(fg || sl_fg) || g_fg_off) return;
        if (sl_fg) g_sl_fg = true;
        if (sl_fg && (slm || (!sl_timed && o_sl_getfn && o_sl_consts))) {  // markers win over the proxy: nothing counted twice
            g_fg_off = true, g_slm = slm ? 2 : 1;
            slm_said(slm);
        } else if (t_sc_depth > 1) g_fg_off = t_fg_said = true;  // nested in a create of ours: as the chain the outermost one hands back is hooked or not
        else if (!sl_fg && !by_fg && !g_ffx_hooked && !g_opti_fgout && (g_entry || g_entry_draws)) g_fg_off = t_fs30 = true;  // FSR 3.0 built in: said once fs30_tick looked
        else fg_off_now(sl_fg ? sl_timed : g_ffx_hooked.load());
    }
    // sl_chain may publish the proxy before g_sl_fg is set and leave it to this
    if (void** v = sl_timed ? slvt : sl_fg && (!g_slm || !g_entry_draws) ? sl_vt(g_sl_vt, one) : nullptr) fg_hook_vt(g_sl_obj, v, one);
}
// Under g_mx. timed: the game's frames come on from frame generation's own swap chain.
static void fg_said(bool timed) {
    if (g_csv) csv_line(timed ? "#framegen,1\n" : "#framegen,1\n#frames,stopped\n");
    logf("frames: frame generation's swap chain: dxgi's Present unhooked for this launch, %s%s", timed ? "the game's frames timed on frame generation's own swap chain"
                                                                                                 : "no frame times from here on (no swap chain of frame generation's own hooked)",
         !timed && g_opti_fgout ? (", OptiScaler.ini " + g_opti_fgname).c_str() : "");
}
// Under g_mx, dxgi's slots holding the originals.
static void fg_off_now(bool timed) {
    g_fg_off = true;
    fg_said(timed);
}
// Under g_mx. known: the marker function is hooked.
static void slm_said(bool known) {
    if (g_csv) csv_line("#framegen,1\n");
    logf("frames: frame generation's swap chain: dxgi's Present unhooked for this launch, the game's rendered frames timed from Streamline's "
         "frame markers (PresentEnd) once %s", known ? "the game sends them" : "the marker function is found and the game sends them");
}
// Streamline's proxies (sl.interposer.dll's swap chain, queue, device) answer it with the object they wrap, AddRef'd
static const GUID kSlBase = {0xadec44e2, 0x61f0, 0x45c3, {0xad, 0x9f, 0x1b, 0x37, 0x37, 0x92, 0x84, 0xff}};
// ReShade 6.8+'s proxies (device, factory, swap chain) answer IID_UnwrappedObject with the object they wrap, AddRef'd
static const GUID kUnwrapped = {0x7f2c9a11, 0x3b4e, 0x4d6a, {0x81, 0x2f, 0x5e, 0x9c, 0xd3, 0x7a, 0x1b, 0x42}};
// A proxy that passes the interfaces it doesn't have to the object it wraps (ReShade's before 6.6) leads through
// ID3DDestructionNotifier to the innermost object, which System32's swap chains and D3D12's queues answer. AddRef'd
// iid of that, or null.
static IUnknown* through(IUnknown* p, REFIID iid) {
    IUnknown *d = nullptr, *n = nullptr;
    if (FAILED(p->QueryInterface(__uuidof(ID3DDestructionNotifier), (void**)&d)) || !d) return nullptr;
    if (FAILED(d->QueryInterface(iid, (void**)&n))) n = nullptr;
    d->Release();
    return n;
}
// The D3D12 runtime's own object (the debug layer's included), not a layer's wrapper of one.
static bool d3d12_own(IUnknown* o) {
    wchar_t path[MAX_PATH];
    const wchar_t* name = module_file(*(void**)o, path);
    HMODULE m = module_of(*(void**)o);
    return !_wcsicmp(name, L"D3D12Core.dll") || !_wcsicmp(name, L"d3d12SDKLayers.dll") || (m && m == g_real);
}
// Streamline makes the native swap chain inside dxgi's own CreateSwapChain* (an inline detour, below the factory's
// vtable), so the factory's hook only sees its proxy, which presents through the native one. ReShade before 6.6 (and
// later with Ubisoft's overlay loaded) hooks the factory's slots under ours and returns its proxy, which answers no
// unwrap: the native one is reached through it. AddRef'd: the native object, or p when it is System32's or no wrapper
// that leads to it. forwarded: found through.
static IUnknown* native_of(IUnknown* p, bool& forwarded) {
    IUnknown* n = nullptr;
    forwarded = false;
    if (module_of(*(void***)p) == g_sysdxgi) return p->AddRef(), p;
    wchar_t path[MAX_PATH];
    if (SUCCEEDED(p->QueryInterface(kSlBase, (void**)&n)) && n) {
        if (t_sc_loud) logf("frames: swap chain %p (%s) wraps %p", (void*)p, utf8(module_file(*(void**)p, path)).c_str(), (void*)n);
        return n;
    }
    if ((n = through(p, __uuidof(IDXGISwapChain))) && module_of(*(void***)n) == g_sysdxgi) {
        forwarded = true;
        if (t_sc_loud) logf("frames: swap chain %p (%s) forwards to %p", (void*)p, utf8(module_file(*(void**)p, path)).c_str(), (void*)n);
        return n;
    }
    if (n) n->Release();
    return p->AddRef(), p;
}
// What Streamline's proxy wraps (it answers kSlBase), else p itself; AddRef'd, null for null.
static IUnknown* sl_base(IUnknown* p) {
    IUnknown* n = nullptr;
    if (!p || (SUCCEEDED(p->QueryInterface(kSlBase, (void**)&n)) && n)) return n;
    return p->AddRef(), p;
}
// The queue's device is the swap chain's (a frame generation swap chain answers with its real one's).
static bool same_device(IUnknown* q, IUnknown* p) {
    ID3D12Device *a = nullptr, *b = nullptr;
    ID3D12DeviceChild* c = nullptr;
    IDXGIDeviceSubObject* s = nullptr;
    if (SUCCEEDED(q->QueryInterface(IID_PPV_ARGS(&c)))) c->GetDevice(IID_PPV_ARGS(&a)), c->Release();
    if (SUCCEEDED(p->QueryInterface(IID_PPV_ARGS(&s)))) s->GetDevice(IID_PPV_ARGS(&b)), s->Release();
    const bool same = a && a == b;
    if (a) a->Release();
    if (b) b->Release();
    return same;
}
// The panel on a frame generation swap chain the game presents its own frames to, on the queue the game gave for it. Its
// private data goes first: it may forward to its real swap chain, which frame generation made on its own queue.
static void fg_attach(IUnknown* chain, IUnknown* q) {
    if (!g_ovc.on || g_ov_off || g_entry || g_entry_draws || g_api11) return;
    IDXGIObject* o = nullptr;
    if (SUCCEEDED(chain->QueryInterface(IID_PPV_ARGS(&o)))) ov_replace(o, nullptr), o->Release();
    IUnknown* n = sl_base(q);  // a layer's queue would take only its own command lists
    const char* why = !n ? "no queue" : !d3d12_own(n) ? "its queue is a layer's" : !same_device(n, chain) ? "its queue is another device's" : nullptr;
    if (why) logf("overlay: frame generation swap chain %p not drawn: %s", (void*)chain, why);
    else overlay_attach(n, chain, true);
    if (n) n->Release();
}
// A failure the log has shown: one line each, the rest only counted.
using ScFail = std::tuple<const char*, HRESULT, UINT, UINT, UINT, UINT>;
static ScFail g_scfail[16];  // under g_mx
static int g_nscfail;
static std::atomic<uint32_t> g_sc11;  // D3D11CreateDeviceAndSwapChain's swap chains: their lines take the same cap
// a: a create creating() ran for; D3D11CreateDeviceAndSwapChain's isn't, and must not end another thread's
static bool loader_lock_held();
static std::atomic<bool> g_sc_under;  // sc_under ran: once a launch
// A swap chain that leads to no System32 one (OptiScaler's wrapper, made by its detour inside dxgi's create, under our
// hook): System32's swap chain class's Present and Present1 are hooked from a composition swap chain made for that on the
// game's queue through the factory's original (no create hook of ours, and Opti doesn't detour ForComposition), released
// at once. The wrapper presents through that class. Counted in g_creating as a create is: dxgi's slots hold the originals
// meanwhile, so a hook under the factory's slot can't take ours for one.
static void sc_under(IUnknown* fac, IUnknown* dev, IUnknown* p) {
    if (g_sc_under.exchange(true)) return;
    wchar_t path[MAX_PATH];
    const std::string what = utf8(module_file(*(void**)p, path));
    IDXGIFactory2* f2 = nullptr;
    IDXGISwapChain1* d = nullptr;
    DXGI_SWAP_CHAIN_DESC1 desc = {8, 8, DXGI_FORMAT_B8G8R8A8_UNORM, FALSE, {1, 0}, DXGI_USAGE_RENDER_TARGET_OUTPUT, 2, DXGI_SCALING_STRETCH,
                                  DXGI_SWAP_EFFECT_FLIP_DISCARD, DXGI_ALPHA_MODE_IGNORE};
    {
        std::lock_guard l(g_mx);
        if (!g_creating++) swap_slots(false);
    }
    HRESULT hr = SUCCEEDED(fac->QueryInterface(IID_PPV_ARGS(&f2))) ? o_createsc_comp(f2, dev, &desc, nullptr, &d) : E_NOINTERFACE;
    if (f2) f2->Release();
    if (SUCCEEDED(hr) && d && module_of(*(void**)d) != g_sysdxgi) d->Release(), d = nullptr, hr = E_UNEXPECTED;
    if (FAILED(hr) || !d)
        logf("frames: swap chain %p (%s) leads to no System32 swap chain, and a composition swap chain to hook its Present through failed 0x%08x: "
             "frame times not measured", (void*)p, what.c_str(), (unsigned)hr);
    else
        logf("frames: swap chain %p (%s) leads to no System32 swap chain: Present hooked through a composition swap chain's vtable", (void*)p, what.c_str());
    {
        std::lock_guard l(g_mx);
        if (d) {
            void** vt = *(void***)d;
            sc_patch(vt, SLOT_PRESENT, (void*)hk_present, &ScVt::present);
            sc_patch(vt, SLOT_PRESENT1, (void*)hk_present1, &ScVt::present1);
        }
        if (!--g_creating && !g_ffx_in) swap_slots(true);
    }
    if (d) d->Release();
}
static void hook_swapchain(HRESULT hr, void* p, IUnknown* dev = nullptr, const ScArgs* a = nullptr, IUnknown* fac = nullptr) {
    bool fresh = false, outer_loud = t_sc_loud, under = false;
    if (!a) t_sc_loud = sc_logged(++g_sc11);
    bool forwarded = false;
    IUnknown* n = SUCCEEDED(hr) && p ? native_of((IUnknown*)p, forwarded) : nullptr;
    if (n && n != p) sl_upgraded((IUnknown*)p, "a create");
    if (a && SUCCEEDED(hr) && p && sc_hwnd(*a) && sc_window_prop()) SetPropW((HWND)a->window, sc_window_prop(), (HANDLE)1);
    {
        std::lock_guard l(g_mx);
        if (a && g_creating && !--g_creating && !g_ffx_in) swap_slots(true);
        if (FAILED(hr) && a) {
            ScFail k{a->api, hr, a->format, a->buffers, a->effect, a->flags};
            fresh = g_nscfail < (int)std::size(g_scfail) && std::find(g_scfail, g_scfail + g_nscfail, k) == g_scfail + g_nscfail;
            if (fresh) g_scfail[g_nscfail++] = k;
        } else if (SUCCEEDED(hr) && !p) {
            if (a && t_sc_loud) logf("frames: a create succeeded with no swap chain: not hooked");
        } else if (SUCCEEDED(hr)) {
            IDXGISwapChain* sc;
            IDXGISwapChain1* sc1;
            bool any = false;
            DXGI_SWAP_CHAIN_DESC d;
            under = fac && dev && o_createsc_comp && !g_fg_off && !g_sc_under && module_of(*(void***)n) != g_sysdxgi;
            if (SUCCEEDED(n->QueryInterface(IID_PPV_ARGS(&sc)))) {
                any = true;
                if (!under) sc_patch(*(void***)sc, SLOT_PRESENT, (void*)hk_present, &ScVt::present);
                if (g_csv && SUCCEEDED(sc->GetDesc(&d)))
                    csv_line("#swapchain,%u,%u,%u\n", d.BufferDesc.Width, d.BufferDesc.Height, (unsigned)d.BufferDesc.Format);
                sc->Release();
            }
            if (SUCCEEDED(n->QueryInterface(IID_PPV_ARGS(&sc1)))) {
                any = true;
                if (!under) sc_patch(*(void***)sc1, SLOT_PRESENT1, (void*)hk_present1, &ScVt::present1);
                sc1->Release();
            }
            if (!any && t_sc_loud) logf("frames: swap chain %p answers neither IDXGISwapChain nor IDXGISwapChain1: not hooked", (void*)n);
        }
    }
    if (n) {
        // the panel's lists go on the native queue, not a proxy of it: Streamline's wraps it, ReShade's leads to it. D3D11:
        // none, the swap chain's own device.
        IUnknown* q = nullptr;
        if (!forwarded) q = sl_base(dev);
        else if (!g_api11 && dev && (q = through(dev, __uuidof(ID3D12CommandQueue)))) {
            ID3D12Device *qd = nullptr, *sd = nullptr;
            ((ID3D12CommandQueue*)q)->GetDevice(IID_PPV_ARGS(&qd)), ((IDXGISwapChain*)n)->GetDevice(IID_PPV_ARGS(&sd));
            if (!qd || qd != sd || !d3d12_own(q)) q->Release(), q = nullptr;  // not the runtime's queue of its device: not drawn
            if (qd) qd->Release();
            if (sd) sd->Release();
        }
        // Streamline's proxy back: the game's create, drawn through the proxy under frame generation, unless dxgi's entry draws
        // (n may then be frame generation's own swap chain on its own queue)
        overlay_attach(q, n, n != p && !forwarded && !g_entry_draws);
        if (q) q->Release();
        n->Release();
    }
    if (under && !loader_lock_held()) sc_under(fac, dev, (IUnknown*)p);
    if (!a) {
        t_sc_loud = outer_loud;
        return;
    }
    if (FAILED(hr)) {
        ++g_sc_failed;
        char r[32];
        snprintf(r, sizeof r, " failed 0x%08x", (unsigned)hr);
        if (fresh) sc_line(dev, *a, r);
    }
    --t_sc_depth;
    t_sc_loud = a->outer_loud;
}
static HRESULT STDMETHODCALLTYPE hk_fg_present(IDXGISwapChain* sc, UINT sync, UINT flags);
// A frame generation swap chain handed back by a create of ours once frame generation stepped aside: OptiScaler's own
// frame generation (its detour of dxgi's create makes FidelityFX's or XeFG's chain, whose real swap chain's create, nested
// here on this thread, stepped aside), and every chain the game makes after (a resize, frame generation toggled). The
// game's frames on that chain's own Present: first the one behind Streamline's proxy, which OptiScaler presents to
// directly too (its FSR frame generation input); a nested call through the proxy counts once (timed_present's depth).
static bool fg_chain(IUnknown* c) {
    void** vt = *(void***)c;
    return module_of(vt) != g_sysdxgi &&
           (vt[SLOT_PRESENT] == (void*)hk_fg_present || (!loader_lock_held() && fg_hook_vt(c, vt, sc_has1(c))));
}
// FSR 3.0 built into a game (FidelityFX SDK 1.x, no ffxCreateContext): its frame interpolation swap chain makes the real one
// on FSR's present queue, then registers itself in that one's private data under this IID, never cleared. fsr3-v3.0.3
// passes `this` as the data, so the 8 bytes stored are the wrapper's vtable pointer (measured in a game); a build that
// passes &this stores the wrapper's address. The game presents that wrapper, its vtable the game's own. It is looked up inside a present of the real swap chain at
// dxgi's entry (FSR's presenter thread, or the game's thread when nothing is generated), while the wrapper presenting it
// is alive; frame_writer then times its Present as an ffxCreateContext swap chain's, or says the frames stopped.
static const GUID kFfxFiChain = {0xbeed74b2, 0x282e, 0x4aa3, {0xbb, 0xf7, 0x53, 0x45, 0x60, 0x50, 0x7a, 0x45}};
// 0 none, 1 waiting for the real swap chain's present, 2 one looking, 3 found (g_fs30_vt), 4 refused (g_fs30_why)
static std::atomic<int> g_fs30;
static void* g_fs30_sc;  // the real swap chain, only compared until it presents
static uintptr_t g_fs30_lo, g_fs30_hi;  // the module whose code made it
static void** g_fs30_vt;
static bool g_fs30_obj;  // the registered value was the wrapper's address, not its vtable
static const char* g_fs30_why;
static double g_fs30_at;
static void fs30_made(HRESULT hr, void* p, const void* caller) {
    t_fs30 = false;
    HMODULE by = nullptr;  // pinned: the vtable checked inside a present is patched later on frame_writer
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, (LPCWSTR)caller, &by);
    g_fs30_sc = p, g_fs30_at = now_ms();
    g_fs30_lo = (uintptr_t)by, g_fs30_hi = by ? g_fs30_lo + image_size(by) : 0;
    if (SUCCEEDED(hr) && p && by) return g_fs30.store(1, std::memory_order_release);
    g_fs30_why = by ? "its swap chain wasn't made" : "the module that made it wasn't pinned";
    g_fs30.store(4, std::memory_order_release);
}
static bool fs30_in(uint64_t a) { return a >= g_fs30_lo && a < g_fs30_hi; }
static bool fs30_mem(uint64_t a, DWORD want) {
    MEMORY_BASIC_INFORMATION mi{};
    return VirtualQuery((void*)a, &mi, sizeof mi) && mi.State == MEM_COMMIT && !(mi.Protect & PAGE_GUARD) && (mi.Protect & want);
}
// A vtable of the wrapper: in non-executable data of the module that made sc, its QueryInterface, Present and Present1
// that module's code (Present a function start in its unwind data). Null when it is one.
static const char* fs30_vt(uint64_t v) {
    uint64_t fn[3];
    if (!fs30_in(v) || (v & 7) || !fs30_mem(v, PAGE_READONLY | PAGE_READWRITE)) return "its wrapper's vtable isn't data of the module that made it";
    for (int i = 0, s[3] = {0, SLOT_PRESENT, SLOT_PRESENT1}; i < 3; ++i)
        if (!read8((void**)v + s[i], fn[i]) || !fs30_in(fn[i]) || !fs30_mem(fn[i], PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY))
            return "its wrapper's methods aren't code of the module that made it";
    DWORD64 base = 0;
    const RUNTIME_FUNCTION* rf = RtlLookupFunctionEntry(fn[1], &base, nullptr);
    return !rf || base + rf->BeginAddress != fn[1] ? "its wrapper's Present isn't a function of that module" : nullptr;
}
// The wrapper's vtable from what is registered on sc, read only: the value itself when it is one, else the first qword of
// committed read-write memory it points to. Nothing of the wrapper is called. Null why and null vt: not registered yet. No
// loader lock: this runs inside a present.
static const char* fs30_wrapper(IDXGISwapChain* sc, void**& vt, bool& obj) {
    vt = nullptr, obj = false;
    uint64_t v = 0, o;
    UINT size = sizeof v;
    const HRESULT hr = sc->GetPrivateData(kFfxFiChain, &size, &v);
    if (hr == DXGI_ERROR_NOT_FOUND) return nullptr;
    if (FAILED(hr) || size != sizeof v || !v) return "no wrapper registered on it";
    const char* why = fs30_vt(v);
    if (why && !fs30_in(v) && !(v & 7) && fs30_mem(v, PAGE_READWRITE) && read8((void*)v, o)) {
        if (!(why = fs30_vt(o))) obj = true, v = o;
    }
    if (!why) vt = (void**)v;
    return why;
}
// From entry_present, for each present of FSR's real swap chain until one thread has looked.
static void fs30_look(IDXGISwapChain* sc) {
    int one = 1;
    if (!g_fs30.compare_exchange_strong(one, 2, std::memory_order_acquire)) return;
    void** vt;
    bool obj;
    const char* why = fs30_wrapper(sc, vt, obj);
    if (!why && !vt) return g_fs30.store(1, std::memory_order_release);
    g_fs30_vt = vt, g_fs30_obj = obj, g_fs30_why = why;
    g_fs30.store(vt ? 3 : 4, std::memory_order_release);
}
// frame_writer, once a second: the wrapper found is hooked; none in 5 s (or a check failing) and the frames stop as before.
static void fs30_tick() {
    int s = g_fs30.load(std::memory_order_acquire);
    if (s == 1 && now_ms() - g_fs30_at > 5000) {
        if (!g_fs30.compare_exchange_strong(s, 4)) return;
        s = 4, g_fs30_why = "no wrapper registered on it, or it didn't present, in 5 s";
    } else if (s != 3 && s != 4) return;
    bool timed = false;
    if (s == 3) timed = fg_hook_vt(nullptr, g_fs30_vt, true);  // an IDXGISwapChain4: Present1 checked to be its code
    wchar_t path[MAX_PATH] = L"";
    GetModuleFileNameW((HMODULE)g_fs30_lo, path, MAX_PATH);
    logf("frames: FSR 3 frame generation built into %s: its swap chain %p, %s%s", shown(path).c_str(), g_fs30_sc,
         s == 4 ? "not timed: " : g_fs30_obj ? "the wrapper registered on it" : "the wrapper's vtable registered on it",
         s == 4 ? g_fs30_why : timed ? " timed" : " not hooked");
    g_fs30.store(0);
    std::lock_guard l(g_mx);
    fg_said(timed);
}
static void fg_made_in(HRESULT hr, void* p, IUnknown* dev, const void* caller) {
    if (t_fs30 && !t_sc_depth) return fs30_made(hr, p, caller);
    if (!g_fg_off || t_sc_depth) return;  // the outermost create of ours on this thread decides
    bool timed = false;
    if (!g_slm && SUCCEEDED(hr) && p) {  // Streamline's markers: nothing else times
        IUnknown* n = sl_base((IUnknown*)p);
        IUnknown* c = n != p && fg_chain(n) ? n : fg_chain((IUnknown*)p) ? (IUnknown*)p : nullptr;
        if ((timed = c)) fg_attach(c, dev);
        n->Release();
    }
    if (!t_fg_said) return;
    t_fg_said = false;
    std::lock_guard l(g_mx);
    fg_said(timed);
}
static HRESULT STDMETHODCALLTYPE hk_createsc(IDXGIFactory* f, IUnknown* dev, DXGI_SWAP_CHAIN_DESC* d, IDXGISwapChain** pp) {
    if (sc_cycle("CreateSwapChain", *(void***)f, SLOT_CREATESC, (void*)o_createsc, _ReturnAddress())) return DXGI_ERROR_INVALID_CALL;
    ScArgs a = sc_args("CreateSwapChain", d, _ReturnAddress());
    creating(dev, a);
    HRESULT hr = o_createsc(f, dev, d, pp);
    hook_swapchain(hr, pp ? *pp : nullptr, dev, &a, f);
    fg_made_in(hr, pp ? *pp : nullptr, dev, a.caller);
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_createsc_hwnd(IDXGIFactory2* f, IUnknown* dev, HWND w, const DXGI_SWAP_CHAIN_DESC1* d,
                                                  const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fs, IDXGIOutput* o, IDXGISwapChain1** pp) {
    if (sc_cycle("CreateSwapChainForHwnd", *(void***)f, SLOT_CREATESC_HWND, (void*)o_createsc_hwnd, _ReturnAddress())) return DXGI_ERROR_INVALID_CALL;
    ScArgs a = sc_args("CreateSwapChainForHwnd", d, w, _ReturnAddress());
    creating(dev, a);
    HRESULT hr = o_createsc_hwnd(f, dev, w, d, fs, o, pp);
    hook_swapchain(hr, pp ? *pp : nullptr, dev, &a, f);
    fg_made_in(hr, pp ? *pp : nullptr, dev, a.caller);
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_createsc_cw(IDXGIFactory2* f, IUnknown* dev, IUnknown* w, const DXGI_SWAP_CHAIN_DESC1* d, IDXGIOutput* o, IDXGISwapChain1** pp) {
    if (sc_cycle("CreateSwapChainForCoreWindow", *(void***)f, SLOT_CREATESC_CW, (void*)o_createsc_cw, _ReturnAddress())) return DXGI_ERROR_INVALID_CALL;
    ScArgs a = sc_args("CreateSwapChainForCoreWindow", d, w, _ReturnAddress());
    creating(dev, a);
    HRESULT hr = o_createsc_cw(f, dev, w, d, o, pp);
    hook_swapchain(hr, pp ? *pp : nullptr, dev, &a, f);
    fg_made_in(hr, pp ? *pp : nullptr, dev, a.caller);
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_createsc_comp(IDXGIFactory2* f, IUnknown* dev, const DXGI_SWAP_CHAIN_DESC1* d, IDXGIOutput* o, IDXGISwapChain1** pp) {
    if (sc_cycle("CreateSwapChainForComposition", *(void***)f, SLOT_CREATESC_COMP, (void*)o_createsc_comp, _ReturnAddress())) return DXGI_ERROR_INVALID_CALL;
    ScArgs a = sc_args("CreateSwapChainForComposition", d, nullptr, _ReturnAddress());
    creating(dev, a);
    HRESULT hr = o_createsc_comp(f, dev, d, o, pp);
    hook_swapchain(hr, pp ? *pp : nullptr, dev, &a, f);
    fg_made_in(hr, pp ? *pp : nullptr, dev, a.caller);
    return hr;
}

// The frames the game renders under FSR 3 frame generation: the Present of the swap chain ffxCreateContext hands it
// (FidelityFX API, desc types WRAP 0x30001 / NEW 0x30005 / FOR_HWND 0x30006, the swap chain pointer right after the
// 16-byte header). dxgi's Present then has the displayed frames, the generated ones too. FSR 4's swap chain doesn't
// answer the SDK's IFfxFrameInterpolationSwapChain, so it is taken by where its vtable is: read-only data of a module
// other than dxgi.dll and this one.
static PFN_Present o_fg_present;
static PFN_Present1 o_fg_present1;
static bool slm_on() { const int s = g_slm.load(std::memory_order_relaxed); return s == 2 || s == 3; }
// Frame generation presenting its own generated frames through its chain's Present (a caller in a FidelityFX, AMD driver
// FSR or XeFG module): not a frame the game rendered. The last caller module's verdict per thread; each new one logged.
static bool fg_own_call(const void* ret) {
    thread_local uintptr_t lo, hi;
    thread_local bool own;
    if (const auto r = (uintptr_t)ret; r < lo || r >= hi) {
        HMODULE m = module_of(ret);
        lo = (uintptr_t)m, hi = m ? lo + image_size(m) : 0;
        const std::wstring name = module_name(ret);
        own = m && (fg_caller(ret) || !_wcsicmp(name.c_str(), L"libxess_fg.dll"));
        static std::atomic<int> said;
        if (said++ < 4) logf("frames: frame generation's Present called from %s on thread %lu: %s", m ? utf8(name).c_str() : "no module",
                             GetCurrentThreadId(), own ? "its own frames, not timed" : "the game's frames");
    }
    return own;
}
// Markers time the frames: frame generation's swap chain, and Streamline's proxy hooked beside them, only drawn on.
static bool fg_untimed(void* sc) {
    const int s = g_slm.load(std::memory_order_relaxed);
    return s == 2 || s == 3 || (s && (uintptr_t)*(void***)sc == (g_sl_vt.load(std::memory_order_relaxed) & ~(uintptr_t)1));
}
template <class F> static HRESULT fg_present(void* sc, UINT flags, const void* ret, F&& call) {
    if (fg_own_call(ret)) return call();
    if (!fg_untimed(sc)) return timed_present(sc, flags, call, true);
    return g_entry || g_entry_draws ? call() : drawn_present(sc, flags, call, t_fg_present);
}
static HRESULT STDMETHODCALLTYPE hk_fg_present(IDXGISwapChain* sc, UINT sync, UINT flags) {
    return fg_present(sc, flags, _ReturnAddress(), [&] { return o_fg_present(sc, sync, flags); });
}
static HRESULT STDMETHODCALLTYPE hk_fg_present1(IDXGISwapChain1* sc, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* p) {
    return fg_present(sc, flags, _ReturnAddress(), [&] { return o_fg_present1(sc, sync, flags, p); });
}
// A frame generation swap chain's vtable we may time the game's frames on: another module's data, never dxgi's or ours,
// and its Present not already ours (one of ours there would call itself through the original). Takes the loader lock.
static bool fg_vt_ok(void** vt) {
    HMODULE m = module_of(vt), self = module_of((void*)&fg_hook_vt);
    MEMORY_BASIC_INFORMATION mi{};
    bool data = VirtualQuery(vt, &mi, sizeof mi) && (mi.Protect == PAGE_READONLY || mi.Protect == PAGE_READWRITE);
    return data && m && m != g_sysdxgi && m != GetModuleHandleW(L"dxgi.dll") && m != self &&
           (vt[SLOT_PRESENT] == (void*)hk_fg_present || module_of(vt[SLOT_PRESENT]) != self);
}
// The game's frames under frame generation, whoever made its swap chain: that swap chain's own Present and Present1.
// one: the vtable is an IDXGISwapChain1's, so it has a Present1 slot (an IDXGISwapChain's ends at slot 17).
static bool fg_hook_vt(const void* sc, void** vt, bool one) {
    wchar_t path[MAX_PATH] = L"";
    if (HMODULE m = module_of(vt)) GetModuleFileNameW(m, path, MAX_PATH);
    bool ok = fg_vt_ok(vt);
    if (ok) {
        std::lock_guard l(g_mx);
        patch(vt, SLOT_PRESENT, (void*)hk_fg_present, o_fg_present);
        if (one) patch(vt, SLOT_PRESENT1, (void*)hk_fg_present1, o_fg_present1);
        ok = vt[SLOT_PRESENT] == (void*)hk_fg_present;  // patch refuses a Present other than the original it holds
    }
    logf("frames: frame generation swap chain %p, vtable %p (%s)%s", sc, (void*)vt, shown(path).c_str(), ok ? ": the game's frames" : ": not hooked");
    return ok;
}
// Called on a live object only: it answers IDXGISwapChain1 as itself, with this vtable.
static bool sc_has1(IUnknown* p) {
    IDXGISwapChain1* s = nullptr;
    const bool one = SUCCEEDED(p->QueryInterface(IID_PPV_ARGS(&s))) && (void*)s == (void*)p && *(void***)s == *(void***)p;
    if (s) s->Release();
    return one;
}
static bool fg_hook(IUnknown* sc) { return fg_hook_vt(sc, *(void***)sc, sc_has1(sc)); }
// Streamline's proxy swap chain: the one to time once its frame generation takes dxgi's slots (now, if it has).
static void sl_chain(IUnknown* p, const char* from) {
    void** vt = *(void***)p;
    const bool one = sc_has1(p);
    g_sl_obj = p;
    if (const uintptr_t t = (uintptr_t)vt | (uintptr_t)one; g_sl_vt.exchange(t) == t) return;
    wchar_t path[MAX_PATH] = L"";
    if (HMODULE m = module_of(vt)) GetModuleFileNameW(m, path, MAX_PATH);
    logf("frames: Streamline's swap chain %p, vtable %p (%s), from %s", (void*)p, (void*)vt, shown(path).c_str(), from);
    if (g_sl_fg && (!g_slm || !g_entry_draws)) fg_hook_vt(p, vt, one);
}
using PFN_SlUpgrade = int (*)(void**);
static PFN_SlUpgrade o_sl_upgrade;
static PFN_CreateSc o_slf_createsc;
static PFN_CreateScHwnd o_slf_hwnd;
static PFN_CreateScCw o_slf_cw;
static PFN_CreateScComp o_slf_comp;
// Our Streamline factory hooks on this thread: a hooker keeping one as its original under a detour of what it calls loops
// through them. At 64 deep the create fails instead of the stack overflowing.
static thread_local int t_slf;
struct SlfIn {
    SlfIn() { ++t_slf; }
    ~SlfIn() { --t_slf; }
    bool cut() const {
        static std::atomic<bool> logged;
        if (t_slf < 64) return false;
        if (!logged.exchange(true)) logf("frames: Streamline's factory create 64 deep on thread %lu: refused (DXGI_ERROR_INVALID_CALL), the hooks call each other", GetCurrentThreadId());
        return true;
    }
};
static HRESULT STDMETHODCALLTYPE hk_slf_createsc(IDXGIFactory* f, IUnknown* dev, DXGI_SWAP_CHAIN_DESC* d, IDXGISwapChain** pp) {
    SlfIn in;
    if (in.cut()) return DXGI_ERROR_INVALID_CALL;
    HRESULT hr = o_slf_createsc(f, dev, d, pp);
    if (SUCCEEDED(hr) && pp && *pp) sl_upgraded(*pp, "Streamline's factory", dev);
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_slf_hwnd(IDXGIFactory2* f, IUnknown* dev, HWND w, const DXGI_SWAP_CHAIN_DESC1* d,
                                             const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fs, IDXGIOutput* o, IDXGISwapChain1** pp) {
    SlfIn in;
    if (in.cut()) return DXGI_ERROR_INVALID_CALL;
    HRESULT hr = o_slf_hwnd(f, dev, w, d, fs, o, pp);
    if (SUCCEEDED(hr) && pp && *pp) sl_upgraded(*pp, "Streamline's factory", dev);
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_slf_cw(IDXGIFactory2* f, IUnknown* dev, IUnknown* w, const DXGI_SWAP_CHAIN_DESC1* d, IDXGIOutput* o, IDXGISwapChain1** pp) {
    SlfIn in;
    if (in.cut()) return DXGI_ERROR_INVALID_CALL;
    HRESULT hr = o_slf_cw(f, dev, w, d, o, pp);
    if (SUCCEEDED(hr) && pp && *pp) sl_upgraded(*pp, "Streamline's factory", dev);
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_slf_comp(IDXGIFactory2* f, IUnknown* dev, const DXGI_SWAP_CHAIN_DESC1* d, IDXGIOutput* o, IDXGISwapChain1** pp) {
    SlfIn in;
    if (in.cut()) return DXGI_ERROR_INVALID_CALL;
    HRESULT hr = o_slf_comp(f, dev, d, o, pp);
    if (SUCCEEDED(hr) && pp && *pp) sl_upgraded(*pp, "Streamline's factory", dev);
    return hr;
}
// What slUpgradeInterface or a create handed out, when it is Streamline's proxy (it answers kSlBase): a swap chain is the
// game's under frame generation; a factory's creates hand out such swap chains (they pass ours below, which sees the
// native one only).
static void sl_upgraded(IUnknown* p, const char* from, IUnknown* dev) {
    IUnknown* n = nullptr;
    if (!g_sl_on || !p || FAILED(p->QueryInterface(kSlBase, (void**)&n)) || !n) return;
    n->Release();
    IDXGISwapChain* sc = nullptr;
    IDXGIFactory2* f2 = nullptr;
    if (SUCCEEDED(p->QueryInterface(IID_PPV_ARGS(&sc))) && sc) {
        sc->Release(), sl_chain(p, from);
        if (dev && g_fg_off && !g_entry_draws) {  // the game's create through Streamline's factory: its queue
            IUnknown* q = sl_base(dev);
            overlay_attach(q, p, true);
            if (q) q->Release();
        }
        return;
    }
    if (FAILED(p->QueryInterface(IID_PPV_ARGS(&f2))) || !f2) return;
    void** vt = *(void***)f2;
    if (fg_vt_ok(vt)) {
        std::lock_guard l(g_mx);
        if (vt[SLOT_CREATESC_HWND] != (void*)hk_slf_hwnd) logf("frames: Streamline's factory %p, vtable %p: its creates watched", (void*)f2, (void*)vt);
        patch(vt, SLOT_CREATESC, (void*)hk_slf_createsc, o_slf_createsc);
        patch(vt, SLOT_CREATESC_HWND, (void*)hk_slf_hwnd, o_slf_hwnd);
        patch(vt, SLOT_CREATESC_CW, (void*)hk_slf_cw, o_slf_cw);
        patch(vt, SLOT_CREATESC_COMP, (void*)hk_slf_comp, o_slf_comp);
    }
    f2->Release();
}
static int hk_sl_upgrade(void** base) {
    const int r = o_sl_upgrade(base);
    if (r == 0 && base && *base) sl_upgraded((IUnknown*)*base, "slUpgradeInterface");
    return r;
}
using PFN_FfxCreate = uint32_t (*)(void*, uint64_t*, const void*);
static constexpr int kFfxSlots = 8;  // modules exporting ffxCreateContext: loader, single dll, effect dlls, a proxy and its target
static PFN_FfxCreate o_ffx_create[kFfxSlots];
static bool fg_desc(uint32_t t) { return t == 0x30001 || t == 0x30005 || t == 0x30006; }
// A created context's frame generation swap chains: timed (fg_hook). True when it has one.
static bool ffx_fg_chains(uint64_t* desc) {
    bool fg = false;
    int n = 0;
    for (uint64_t* h = desc; h && n < 32; h = (uint64_t*)h[1], ++n)
        if (const uint32_t t = (uint32_t)h[0]; fg_desc(t) && h[2] && *(IUnknown**)h[2]) {
            fg = true;
            // gameQueue (ffx_api_framegeneration_dx12.h): after swapchain (WRAP), desc and dxgiFactory (NEW), or hwnd, desc,
            // fullscreenDesc and dxgiFactory (FOR_HWND)
            if (IUnknown* c = *(IUnknown**)h[2]; fg_hook(c)) fg_attach(c, (IUnknown*)h[t == 0x30001 ? 3 : t == 0x30005 ? 5 : 7]);
        }
    return fg;
}
static void ffx_enter() {
    std::lock_guard l(g_mx);
    if (!g_ffx_in++ && !g_creating) swap_slots(false);  // dxgi's from the start: a create may bypass the factory's hooks
}
static void ffx_leave(bool fg) {
    std::lock_guard l(g_mx);  // one hold: a create ending on another thread meanwhile leaves the put-back to this
    --g_ffx_in;
    if (fg && !g_fg_off) swap_slots(false), fg_off_now(g_ffx_hooked);
    else if (!g_ffx_in && !g_creating) swap_slots(true);
}
// No objects to unwind here (__try): the window closes on a structured exception too.
template <int I> static uint32_t hk_ffx_create(void* ctx, uint64_t* desc, const void* cb) {
    uint32_t r = 1;
    bool fg = false;
    ffx_enter();
    __try {
        r = o_ffx_create[I](ctx, desc, cb);
        if (!r) fg = ffx_fg_chains(desc);
    } __finally {
        ffx_leave(fg);
    }
    return r;
}
// Every module that exports ffxCreateContext, whatever its name (FidelityFX's loader, its single or effect dlls, a proxy such
// as HelixSR and the renamed dll it forwards to): as loaded at the first device, else as they load (on_dll). One slot per
// module; a forwarded export (the same entry through another module) is hooked once.
static std::atomic<HMODULE> g_ffx_mod[kFfxSlots];  // slot i's module, claimed before its hook goes in; kept when it fails
static void* g_ffx_fn[kFfxSlots];                  // under ffx_mx: slot i's entry
static std::atomic<bool> g_ffx_late;        // frame_hooks ran: a FidelityFX dll that loads from now on is hooked as it loads
static std::atomic<bool> g_ffx_retry;       // one couldn't be hooked as it loaded: frame_writer tries again
// loading: under the loader lock, the module not initialized yet (no thread runs it): its export read from the image, the
// hook lock only tried, and the line deferred like AGS's. ffx_mx guards only the claim: nothing under it waits on another lock.
static void ffx_hook(HMODULE m, const std::wstring& name, bool loading) {
    static void* const hooks[kFfxSlots] = {(void*)hk_ffx_create<0>, (void*)hk_ffx_create<1>, (void*)hk_ffx_create<2>, (void*)hk_ffx_create<3>,
                                           (void*)hk_ffx_create<4>, (void*)hk_ffx_create<5>, (void*)hk_ffx_create<6>, (void*)hk_ffx_create<7>};
    static std::mutex ffx_mx;
    void* p = !m ? nullptr : loading ? export_of(m, "ffxCreateContext") : (void*)GetProcAddress(m, "ffxCreateContext");
    if (!p || m == module_of((void*)&ffx_hook) || m == g_sysdxgi || m == GetModuleHandleW(L"dxgi.dll")) return;
    if (!loading) m = module_of(p);  // a forwarder's target: the module pinned below, so the slot's key can't go stale
    int i = 0;
    {
        std::lock_guard c(ffx_mx);
        for (int k = 0; k < kFfxSlots; ++k)
            if (g_ffx_mod[k] == m || g_ffx_fn[k] == p) return;
        while (i < kFfxSlots && g_ffx_mod[i]) ++i;
        if (i == kFfxSlots) {
            static bool said;
            if (!said) said = true, ags_note("frames: " + utf8(name) + " ffxCreateContext not hooked: every slot taken");
            return;
        }
        g_ffx_mod[i] = m, g_ffx_fn[i] = p;
    }
    std::unique_lock l(g_hook_mx, std::defer_lock);
    if (loading && !l.try_lock()) {
        {
            std::lock_guard c(ffx_mx);
            g_ffx_mod[i] = nullptr, g_ffx_fn[i] = nullptr;
        }
        g_ffx_retry = true;
        return ags_note("frames: " + utf8(name) + " ffxCreateContext not hooked as it loaded (another hook was being installed): tried again");
    }
    // both pinned: the patched entry jumps into this dll, and the FidelityFX dll never unloads (no write into unmapped code,
    // no reload coming back unhooked)
    HMODULE pinned;
    bool ok = GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, (LPCWSTR)p, &pinned);
    pin_self();
    ok = ok && hook_fn(p, hooks[i], (void**)&o_ffx_create[i], false, nullptr, loading ? 64 : 0);
    if (ok) g_ffx_hooked = true;
    std::string line = "frames: " + utf8(name) + " ffxCreateContext " + (ok ? "hooked" : "not hooked") + (loading ? " as it loaded" : "");
    if (loading) ags_note(line);
    else logf("%s", line.c_str());
}
// Streamline's slUpgradeInterface (sl.interposer.dll), as ffx_hook does ffxCreateContext.
// A PresentEnd once markers time the game's frames: a frame it rendered. Never calls out, so nothing re-enters.
static void slm_frame() {
    int s = g_slm.load(std::memory_order_relaxed);
    if (s == 2 && g_slm.compare_exchange_strong(s, 3)) s = 3;
    if (s != 3) return;
    g_ov_game.store(true, std::memory_order_relaxed);
    trav::frame(now_ms());  // dxgi's Presents pass through untimed now: the traversal probe and reuse count these
    if (g_ovc.on) g_ovfeed.push(now_ms());
    LARGE_INTEGER t;
    std::lock_guard l(g_fmx);
    QueryPerformanceCounter(&t);
    g_fq.push_back({t.QuadPart, (void*)&g_slm, true});
}
using PFN_SlMarker = int (*)(uint32_t, const void*);
static thread_local int t_slm_in, t_sl_in;  // our marker hooks', and our other Streamline hooks', originals running here
template <int I> static int hk_sl_marker(uint32_t marker, const void* frame) {
    if (marker == 5 && !t_slm_in) slm_frame();  // sl::PCLMarker::ePresentEnd; PCL forwards it to Reflex's: counted once
    ++t_slm_in;
    const int r = ((PFN_SlMarker)o_sl_marker[I])(marker, frame);
    return --t_slm_in, r;
}
static const char* const kSlMarkers[2] = {"slPCLSetMarker", "slReflexSetMarker"};
// Beside Ubisoft Connect's loader no create hook sees Streamline's frame generation swap chain: once the marker function is
// hooked its PresentEnds time the game's rendered frames, frame generation on or off. dxgi's entry hooks time until the first
// one arrives (some launches never send one) and draw the panel throughout.
static void entry_markers() {
    std::lock_guard l(g_mx);
    if (g_fg_off || g_slm) return;
    g_slm = 2;
    logf("frames: beside Ubisoft Connect's loader the game's rendered frames are timed from Streamline's frame markers (PresentEnd) once "
         "the first arrives, at dxgi's entry until then");
}
static void sl_marker_hook(const char* name, void* fn) {
    static std::mutex mx;  // one hook per function: a second hook_fn would take our jmp for its original
    for (int i = 0; i < 2; ++i) {
        if (strcmp(name, kSlMarkers[i]) || o_sl_marker[i] || loader_lock_held()) continue;
        HMODULE pinned;  // its plugin may unload at an SL shutdown: our jmp stays in it. Pinned before mx: it takes the loader lock
        bool ok = GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, (LPCWSTR)fn, &pinned);
        pin_self();
        std::lock_guard l(mx);
        if (o_sl_marker[i]) return;
        ok = ok && hook_fn(fn, i ? (void*)hk_sl_marker<1> : (void*)hk_sl_marker<0>, (void**)&o_sl_marker[i]);
        wchar_t path[MAX_PATH] = L"";
        if (ok) GetModuleFileNameW(pinned, path, MAX_PATH);
        logf("frames: Streamline's %s (%s) %s", name, shown(path).c_str(), ok ? "hooked" : "not hooked");
        if (ok && g_entry) entry_markers();
        return;
    }
}
using PFN_SlGetFn = int (*)(uint32_t, const char*, void**);
static int hk_sl_getfn(uint32_t feature, const char* name, void** fn) {
    ++t_sl_in;
    const int r = ((PFN_SlGetFn)o_sl_getfn)(feature, name, fn);
    if (--t_sl_in, !r && name && fn && *fn) sl_marker_hook(name, *fn);
    return r;
}
// Frame generation came before the game fetched the marker function through us: fetched once on the game's own thread,
// after one of its calls into Streamline returned (never in that create, under Streamline's own call, nor beside it).
using PFN_SlConsts = int (*)(const void*, const void*, const void*);
static int hk_sl_consts(const void* values, const void* frame, const void* viewport) {
    ++t_sl_in;
    const int r = ((PFN_SlConsts)o_sl_consts)(values, frame, viewport);
    if (int one = 1; !--t_sl_in && g_slm == 1 && g_slm.compare_exchange_strong(one, 5)) {
        for (auto [feature, name] : {std::pair{4u, kSlMarkers[0]}, std::pair{3u, kSlMarkers[1]}})  // sl::kFeaturePCL, kFeatureReflex
            if (void* fn = nullptr; !o_sl_marker[0] && !o_sl_marker[1] && !((PFN_SlGetFn)o_sl_getfn)(feature, name, &fn) && fn) sl_marker_hook(name, fn);
        const bool ok = o_sl_marker[0] || o_sl_marker[1];
        g_slm = ok ? 2 : 4;
        if (!ok) {
            std::lock_guard l(g_mx);
            if (g_csv) csv_line("#frames,stopped\n");
            logf("frames: Streamline's marker function not found: no frame times from here on");
        }
    }
    return r;
}
// frame_writer's tick: the lines markers owe.
static void slm_tick() {
    static int waited;
    static bool told;
    if (int s = g_slm; (s == 1 || s == 2) && ++waited >= 10 && g_slm.compare_exchange_strong(s, 4)) {
        std::lock_guard l(g_mx);
        if (g_entry && !g_fg_off) return logf("frames: no PresentEnd marker in 10 s: the game's frames stay timed at dxgi's entry");
        if (g_csv) csv_line("#frames,stopped\n");
        logf("frames: no PresentEnd marker in 10 s after frame generation's swap chain: no frame times from here on");
    }
    if (g_slm == 3 && !told) {
        told = true;
        std::lock_guard l(g_mx);
        if (g_csv) csv_line("#frames,markers\n");
        logf("frames: the game's first PresentEnd marker: its rendered frames timed");
    }
}
static std::atomic<bool> g_sl_claimed;
static void sl_hook(HMODULE m, bool loading) {
    if (!g_dlssg_seen && !GetModuleHandleW(L"sl.dlss_g.dll") && !GetModuleHandleW(L"nvngx_dlssg.dll")) return;
    void* p = !m ? nullptr : loading ? export_of(m, "slUpgradeInterface") : (void*)GetProcAddress(m, "slUpgradeInterface");
    if (!p || g_sl_claimed.exchange(true)) return;
    std::unique_lock l(g_hook_mx, std::defer_lock);
    if (loading && !l.try_lock()) {
        g_sl_claimed = false, g_ffx_retry = true;
        return ags_note("frames: sl.interposer.dll slUpgradeInterface not hooked as it loaded (another hook was being installed): tried again");
    }
    HMODULE pinned;
    bool ok = GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, (LPCWSTR)p, &pinned);
    pin_self();
    ok = ok && hook_fn(p, (void*)hk_sl_upgrade, (void**)&o_sl_upgrade, false, nullptr, loading ? 64 : 0);
    void* g = !ok ? nullptr : loading ? export_of(m, "slGetFeatureFunction") : (void*)GetProcAddress(m, "slGetFeatureFunction");
    const bool got = g && hook_fn(g, (void*)hk_sl_getfn, (void**)&o_sl_getfn, false, nullptr, loading ? 64 : 0);
    void* c = !got ? nullptr : loading ? export_of(m, "slSetConstants") : (void*)GetProcAddress(m, "slSetConstants");
    const bool consts = c && hook_fn(c, (void*)hk_sl_consts, (void**)&o_sl_consts, false, nullptr, loading ? 64 : 0);
    std::string line = std::string("frames: sl.interposer.dll slUpgradeInterface ") + (ok ? "hooked" : "not hooked") + (loading ? " as it loaded" : "") +
                       (got ? ", slGetFeatureFunction hooked" : g ? ", slGetFeatureFunction not hooked" : "") +
                       (consts ? ", slSetConstants hooked" : c ? ", slSetConstants not hooked" : "");
    g_sl_on = ok;
    if (loading) ags_note(line);
    else logf("%s", line.c_str());
}
static void ffx_hooks() {
    if (HMODULE m; GetModuleHandleExW(0, L"sl.interposer.dll", &m)) sl_hook(m, false), FreeLibrary(m);
    HMODULE mods[1024];
    DWORD bytes = 0;
    if (!K32EnumProcessModules(GetCurrentProcess(), mods, sizeof mods, &bytes)) return;
    for (DWORD k = 0; k < std::min<DWORD>(bytes, sizeof mods) / sizeof(HMODULE); ++k) {
        HMODULE m;  // counted until its pin: it can't unload while its export is read and patched
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, (LPCWSTR)mods[k], &m)) continue;
        if (GetProcAddress(m, "ffxCreateContext")) ffx_hook(m, module_name(m), false);
        FreeLibrary(m);
    }
}
static void ffx_loaded(const LdrString* name, HMODULE m) {
    if (!g_ffx_late) return;
    if (name->length == 17 * sizeof(wchar_t) && !_wcsnicmp(name->buffer, L"sl.interposer.dll", 17)) sl_hook(m, true);
    for (const wchar_t* g : {L"sl.dlss_g.dll", L"nvngx_dlssg.dll"})
        if (size_t n = wcslen(g); name->length == n * sizeof(wchar_t) && !_wcsnicmp(name->buffer, g, n))
            if (g_dlssg_seen = true; HMODULE i = GetModuleHandleW(L"sl.interposer.dll")) sl_hook(i, true);
    if (export_of(m, "ffxCreateContext")) ffx_hook(m, std::wstring(name->buffer, name->length / sizeof(wchar_t)), true);
}

// Ubisoft Connect's overlay inline-detours dxgi's CreateSwapChainForHwnd and takes what the factory's slot holds as its
// original: with our hook there, our hook and its detour call each other on every create. Beside its loader no slot of a
// dxgi vtable is ours: Present and Present1 are inline-hooked at their entry, found on a swap chain of our own on a hidden
// window, and a detour another overlay puts there later runs ours as part of the original it calls. The panel goes on a
// swap chain after its first present, with the queue it presents on (entry_present).
static PFN_Present o_entry_present;
static PFN_Present1 o_entry_present1;
static std::atomic<void*> g_entry_tried[8];  // the swap chains the panel was tried on
// System32's D3D12 swap chain doesn't name its queue (GetDevice gives its device), but its Present signals a fence on it
// through the queue's vtable, called from dxgi.dll (WARP-measured). Inside the swap chain's first present, not nested, every
// Signal on this thread must come from dxgi.dll on one queue: that queue is its own, alive and in use there. Any other Signal
// there (another module's, a nested present's, a second queue) and the swap chain gets no panel. Signal is hooked at its
// entry in D3D12Core, as Present is.
using PFN_Signal = HRESULT(STDMETHODCALLTYPE*)(ID3D12CommandQueue*, ID3D12Fence*, UINT64);
static PFN_Signal o_signal;
static thread_local int t_find_queue;  // 1: looking, 2: refused
static thread_local ID3D12CommandQueue* t_found_queue;  // AddRef'd
static thread_local const void* t_find_other;  // the first Signal from another module there
static HRESULT STDMETHODCALLTYPE hk_signal(ID3D12CommandQueue* q, ID3D12Fence* f, UINT64 v) {
    if (t_find_queue == 1) {
        // another module's (an overlay chained below ours drawing its own) tells nothing of dxgi's queue
        if (module_of(_ReturnAddress()) != g_sysdxgi) t_find_other = t_find_other ? t_find_other : _ReturnAddress();
        else if (t_present != 1 || (t_found_queue && t_found_queue != q)) t_find_queue = 2;
        else if (!t_found_queue) (t_found_queue = q)->AddRef();
    }
    return o_signal(q, f, v);
}
// Under g_ovmx: c found a buffer made after its queue was proven: no panel until a present proves it again.
static void entry_forget(void* sc, OvChain* c) {
    c->retired = true;
    IUnknown* now = nullptr;
    UINT size = sizeof now;
    if (FAILED(((IDXGIObject*)sc)->GetPrivateData(kOvChain, &size, &now)) || !now) return;
    if (now == (IUnknown*)c) {
        ((IDXGIObject*)sc)->SetPrivateDataInterface(kOvChain, nullptr);
        for (auto& t : g_entry_tried)
            if (void* p = sc; t.compare_exchange_strong(p, nullptr)) break;
    }
    now->Release();  // never the last: the caller holds c, the swap chain any other
}
// Every buffer the swap chain has now gets a new tag, before the present that proves its queue. 0: not tagged.
static uint32_t proof_tag(IDXGISwapChain* sc) {
    static std::atomic<uint32_t> n;
    const uint32_t tag = ++n;
    DXGI_SWAP_CHAIN_DESC d;
    if (FAILED(sc->GetDesc(&d))) return 0;
    for (UINT i = 0; i < d.BufferCount; ++i) {
        ID3D12Resource* b = nullptr;
        if (FAILED(sc->GetBuffer(i, IID_PPV_ARGS(&b)))) return 0;
        const HRESULT hr = b->SetPrivateData(kOvProof, sizeof tag, &tag);
        b->Release();
        if (FAILED(hr)) return 0;
    }
    return tag;
}
// True once per swap chain, while the panel is on.
static bool entry_first(IDXGISwapChain* sc) {
    if (!g_ovc.on || g_ov_off.load(std::memory_order_relaxed)) return false;
    for (auto& t : g_entry_tried) {
        void* p = nullptr;
        if (t.compare_exchange_strong(p, sc)) return true;
        if (p == sc) return false;
    }
    return false;
}
// The search ends with the present that started it, also when a structured exception leaves through it.
static HRESULT find_in(HRESULT (*body)(void*), void* call) {
    HRESULT hr = E_FAIL;
    __try {
        hr = body(call);
    } __finally {
        if (AbnormalTermination()) {
            t_find_queue = 0;
            if (ID3D12CommandQueue* q = std::exchange(t_found_queue, nullptr)) q->Release();
        }
    }
    return hr;
}
// D3D11: the panel goes on before the first present, on the swap chain's own device. D3D12: on the queue the swap chain
// answers for (DXVK's), else the one its first present signalled, of the swap chain's single-node device.
template <class F> static HRESULT entry_present(IDXGISwapChain* sc, UINT flags, F&& call) {
    IUnknown* q = nullptr;
    if (g_fs30.load(std::memory_order_acquire) == 1 && sc == g_fs30_sc) fs30_look(sc);
    if (!g_entry && !g_fg_off.load(std::memory_order_relaxed))  // the slot hooks time and draw: counted only
        return present_in([](void* c) { return (*(std::remove_reference_t<F>*)c)(); }, (void*)&call, t_present);
    bool first = !(flags & DXGI_PRESENT_TEST) && !t_find_queue && !t_present && entry_first(sc), find = false;
    uint32_t tag = 0;
    if (first && (g_api11 || SUCCEEDED(sc->GetDevice(__uuidof(ID3D12CommandQueue), (void**)&q)))) {
        overlay_attach(q, sc, true);  // once per swap chain: the panel's lists are made here
        if (q) q->Release(), q = nullptr;
    } else if (first && o_signal && !t_find_queue) {
        if ((tag = proof_tag(sc))) find = true, t_find_queue = 1, t_find_other = nullptr;
        else logf("overlay: swap chain %p not drawn: its buffers couldn't be tagged", (void*)sc);
    }
    // markers or frame generation's own swap chain time the frames: the panel only
    const bool timed = !g_fg_off.load(std::memory_order_relaxed) && g_slm.load(std::memory_order_relaxed) != 3;
    auto present = [&] { return timed ? timed_present(sc, flags, call) : drawn_present(sc, flags, call); };
    if (!find) return present();
    HRESULT hr = find_in([](void* p) { return (*(decltype(present)*)p)(); }, &present);
    bool one = t_find_queue == 1;
    t_find_queue = 0;
    q = std::exchange(t_found_queue, nullptr);
    const char* why = !one ? "a nested present's or a second queue's Signal" : !q ? "no Signal from dxgi.dll" : "a queue of another device or node";
    ID3D12Device *qd = nullptr, *sd = nullptr;
    if (q) ((ID3D12CommandQueue*)q)->GetDevice(IID_PPV_ARGS(&qd)), sc->GetDevice(IID_PPV_ARGS(&sd));
    if (q && (!one || !qd || qd != sd || qd->GetNodeCount() != 1)) q->Release(), q = nullptr;
    if (qd) qd->Release();
    if (sd) sd->Release();
    const std::string other = t_find_other ? ", " + code_at(t_find_other) + "'s Signal left out" : "";
    if (!q) return logf("overlay: swap chain %p not drawn: its first present signalled no single queue of its own device (%s%s)", (void*)sc,
                        why, other.c_str()), hr;
    if (!other.empty()) logf("overlay: swap chain %p: its queue from dxgi.dll's Signal%s", (void*)sc, other.c_str());
    overlay_attach(q, sc, true, tag);  // its own queue, proven: drawn after frame generation too
    q->Release();
    return hr;
}
// An entry hooked in front of another hooker's jmp. A hooker that puts the entry's original bytes back for its own call
// and its own jmp after it (RivaTuner) wipes ours inside the first present through it, and a hooker's below it too: ours
// then goes at the entry of the function the jmp now there leads to, once. Only when our jmp was there as our call began
// and isn't once it returned: a hooker that went in over ours later and calls ours as its original is left alone.
// state: 0 not chained, 1 watching, 2 settled.
struct EntryChain {
    uint8_t *at, *via;
    std::atomic<int> state;
};
static EntryChain g_ech[2];
static HRESULT STDMETHODCALLTYPE hk_entry_present(IDXGISwapChain* sc, UINT sync, UINT flags);
static HRESULT STDMETHODCALLTYPE hk_entry_present1(IDXGISwapChain1* sc, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* p);
// The jmp at a watched entry is ours: its target, through our relay, is our hook.
static bool entry_ours(int i) {
    EntryChain& e = g_ech[i];
    uint64_t now;
    return e.state.load(std::memory_order_acquire) == 1 && read8(e.at, now) &&
           jmp_target((const uint8_t*)&now, e.at) == (i ? (uint8_t*)hk_entry_present1 : (uint8_t*)hk_entry_present);
}
// After a present whose call began with our jmp at the entry.
static void entry_kept(int i) {
    EntryChain& e = g_ech[i];
    if (e.state.load(std::memory_order_acquire) != 1) return;
    uint64_t now;
    if (!read8(e.at, now)) return;
    uint8_t* to = jmp_target((const uint8_t*)&now, e.at);
    // ours still in, or not a jmp (a hooker mid-call on another thread): read again next present
    if (!to || to == (i ? (uint8_t*)hk_entry_present1 : (uint8_t*)hk_entry_present) || e.state.exchange(2) != 1) return;
    const char* name = i ? "Present1" : "Present";
    HMODULE pinned;
    const bool ok = GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, (LPCWSTR)to, &pinned) &&
                    hook_fn(to, i ? (void*)hk_entry_present1 : (void*)hk_entry_present, i ? (void**)&o_entry_present1 : (void**)&o_entry_present,
                            false, nullptr, 0, 14);
    logf("overlay: dxgi's %s entry: %s's jmp went back over ours after a call%s, %s", name, code_at(to).c_str(), to == e.via ? "" : (" (ours was in front of " + code_at(e.via) + ")").c_str(),
         ok ? "hooked at its function's entry instead" : "its function's entry not hooked: no panel under frame generation");
}
static HRESULT STDMETHODCALLTYPE hk_entry_present(IDXGISwapChain* sc, UINT sync, UINT flags) {
    if (t_present >= 63) return present_cut("Present");  // a chained overlay that later hooks over us calls us back
    const bool ours = entry_ours(0);
    const HRESULT hr = entry_present(sc, flags, [&] { return o_entry_present(sc, sync, flags); });
    if (ours) entry_kept(0);
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_entry_present1(IDXGISwapChain1* sc, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* p) {
    if (t_present >= 63) return present_cut("Present1");
    const bool ours = entry_ours(1);
    const HRESULT hr = entry_present(sc, flags, [&] { return o_entry_present1(sc, sync, flags, p); });
    if (ours) entry_kept(1);
    return hr;
}
// A refused entry's jmp for the log: its relay's first bytes and where they lead.
static std::string relay_seen(void* p) {
    uint8_t b[5], x[16];
    uint8_t* d = safe_code_read(b, (uint8_t*)p, 5) ? jmp_relay(b, (uint8_t*)p) : nullptr;
    if (!d) return "";
    char hex[3 * sizeof x + 1] = "";
    for (int n : {16, 14, 13, 12, 6, 5})
        if (safe_code_read(x, d, n)) {
            for (int i = 0; i < n; ++i) snprintf(hex + 3 * i, 4, "%02x ", x[i]);
            break;
        }
    return code_at(d) + " (" + (*hex ? std::string(hex, strlen(hex) - 1) : "unreadable") + ") to " + code_at(jmp_target(b, (uint8_t*)p));
}
static bool entry_hooks(IUnknown* dev) {
    auto create = (decltype(&CreateDXGIFactory1))GetProcAddress(g_sysdxgi, "CreateDXGIFactory1");
    ID3D12Device* d12 = nullptr;
    ID3D12CommandQueue* q = nullptr;
    IUnknown* on = dev;  // D3D11: the device; D3D12: a direct queue of it
    if (dev && SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&d12)))) {
        D3D12_COMMAND_QUEUE_DESC qd = {};
        trav::t_own_queue = true;
        if (FAILED(d12->CreateCommandQueue(&qd, IID_PPV_ARGS(&q)))) q = nullptr;
        trav::t_own_queue = false;
        on = q;
    }
    void* signal = nullptr;  // a before and after run's panel: D3D12Core's Signal, from our queue's own vtable
    if (IUnknown* n = q && _wtoi(g_signed.overlay.c_str()) ? sl_base(q) : nullptr) {
        void** vt = *(void***)n;
        if (d3d12_own(n) && module_of(vt[SLOT_SIGNAL]) == module_of(vt)) signal = vt[SLOT_SIGNAL];
        n->Release();
    }
    HWND w = CreateWindowExW(0, L"STATIC", L"", WS_POPUP, 0, 0, 8, 8, nullptr, nullptr, nullptr, nullptr);
    DXGI_SWAP_CHAIN_DESC1 d = {8, 8, DXGI_FORMAT_R8G8B8A8_UNORM, FALSE, {1, 0}, DXGI_USAGE_RENDER_TARGET_OUTPUT, 2};
    d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    IDXGIFactory2* f = nullptr;
    IDXGISwapChain1* sc = nullptr;
    HRESULT hr = !create || !on || !w ? E_FAIL : create(IID_PPV_ARGS(&f));
    if (SUCCEEDED(hr)) hr = f->CreateSwapChainForHwnd(on, w, &d, nullptr, nullptr, &sc);
    void *present = nullptr, *present1 = nullptr, *setcs = nullptr;
    if (SUCCEEDED(hr)) {
        bool forwarded;
        IUnknown* n = native_of(sc, forwarded);  // Streamline's detour hands back its proxy
        if (void** vt = *(void***)n; sys_vtable(vt, "swap chain", SLOT_PRESENT)) present = vt[SLOT_PRESENT], present1 = vt[SLOT_PRESENT1], setcs = vt[SLOT_SETCS];
        n->Release();
    }
    for (IUnknown* u : {(IUnknown*)sc, (IUnknown*)f, (IUnknown*)q, (IUnknown*)d12})
        if (u) u->Release();
    if (w) DestroyWindow(w);
    if (!present) return logf("frames: not measured: no swap chain of our own to find dxgi's Present at (0x%08x)", (unsigned)hr), false;
    pin_self();
    std::string at0 = code_at(present), at1 = code_at(present1);
    // only dxgi's own code: a slot another overlay hooked holds its function, never patched
    auto why = [](void* p) -> std::string {
        uint8_t b[5];
        uint8_t* v = safe_code_read(b, (uint8_t*)p, 5) ? jmp_target(b, (uint8_t*)p) : nullptr;
        return module_of(p) != g_sysdxgi ? "not hooked (another module's function)"
             : v && !module_of(v)       ? "not hooked (a jmp into memory outside any module: " + relay_seen(p) + ")"
                                        : "not hooked (its first instructions aren't ones the hook moves)";
    };
    auto jmp = [](void* p) { uint8_t b[5]; uint8_t* v = safe_code_read(b, (uint8_t*)p, 5) ? jmp_target(b, (uint8_t*)p) : nullptr; return v ? code_at(v) : std::string(); };
    const std::string v0 = jmp(present), v1 = jmp(present1);
    // 14: the longest jmp another hooker writes at an entry, which then runs ours as part of its original
    // watched from before our jmp can run: the first present through it may already be the one that wipes it
    auto watch = [](int i, void* p) {
        uint8_t b[5];
        EntryChain& e = g_ech[i];
        if (safe_code_read(b, (uint8_t*)p, 5) && (e.via = jmp_target(b, (uint8_t*)p))) e.at = (uint8_t*)p, e.state.store(1, std::memory_order_release);
    };
    // the jmp hook_fn chained through: the one watched, or the entry isn't
    auto chained = [](int i, const HookPatch& r) {
        EntryChain& e = g_ech[i];
        if (e.state.load() == 1 && (!r.t.load() || jmp_target((const uint8_t*)&r.was, r.t.load()) != e.via)) e.state.store(r.t.load() ? 2 : 0);
    };
    HookPatch r0, r1;
    if (module_of(present) == g_sysdxgi) watch(0, present);
    bool p0 = module_of(present) == g_sysdxgi && hook_fn(present, (void*)hk_entry_present, (void**)&o_entry_present, false, &r0, 0, 14, true);
    chained(0, r0);
    if (present1 != present && module_of(present1) == g_sysdxgi) watch(1, present1);
    bool p1 = present1 != present && module_of(present1) == g_sysdxgi && hook_fn(present1, (void*)hk_entry_present1, (void**)&o_entry_present1, false, &r1, 0, 14, true);
    chained(1, r1);
    auto said = [&](bool ok, void* p, const std::string& v) {
        return ok ? (v.empty() ? "hooked at its entry" : "hooked at its entry, chained through " + v) : why(p) + (v.empty() ? "" : ", a jmp to " + v);
    };
    logf("%s: Present at %s %s, Present1 at %s %s", g_entry ? "frames: beside Ubisoft Connect's loader no dxgi slot is hooked" : "overlay: for the panel under frame generation",
         at0.c_str(), said(p0, present, v0).c_str(), at1.c_str(), said(p1, present1, v1).c_str());
    if (!p0 && !p1) logf(g_entry ? "frames: not measured: neither of dxgi's presents could be hooked at its entry" : "overlay: no panel under frame generation");
    else if (signal) logf("overlay: D3D12's Signal at %s %s", code_at(signal).c_str(),
                          !p0 || (!p1 && present1 != present) ? "not hooked: a present through dxgi's other entry wouldn't be seen"
                          : hook_fn(signal, (void*)hk_signal, (void**)&o_signal, false, nullptr, 0, 14) ? "hooked at its entry, to find a swap chain's queue" : "not hooked: the panel needs a queue its swap chain names");
    g_entry_draws = o_signal != nullptr;
    if (o_signal && module_of(setcs) == g_sysdxgi)
        logf("overlay: dxgi's SetColorSpace1 at %s %s", code_at(setcs).c_str(),
             hook_fn(setcs, (void*)hk_entry_setcs, (void**)&o_entry_setcs, false, nullptr, 0, 14, true) ? "hooked at its entry" : "not hooked: no panel on 10-bit swap chains");
    return p0 || p1;
}

static HANDLE g_frames = INVALID_HANDLE_VALUE;
static std::atomic<bool> g_frames_on;  // frame_writer runs: a hooked present's frame gets written, not just queued
static FrameEnc g_fenc;  // the writer thread's
static uint64_t g_fhead[4];  // the launch record, written when the first frame is
static bool g_frames_full;

// Frames are encoded only once the file is open, and a failed write ends the file: each frame's time is the sum of the
// deltas before it, so a batch encoded but not written would shift every later frame.
static void frames_flush(std::vector<FrameAt>& q) {
    if (q.empty() || g_frames_full) return q.clear();
    if (g_frames == INVALID_HANDLE_VALUE) {  // the last launch that presented replaces the file: a probe that never presents keeps it
        g_frames = CreateFileW((g_dir + L"scskiller_frames.bin").c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr,
                               CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (g_frames == INVALID_HANDLE_VALUE) return q.clear(), logf("frames: scskiller_frames.bin can't be written (%lu)", GetLastError());
        uint32_t mark = 0xFFFFFFFF;
        DWORD a = 0, b = 0;
        if (!WriteFile(g_frames, &mark, 4, &a, nullptr) || !WriteFile(g_frames, g_fhead, sizeof g_fhead, &b, nullptr) || a + b != 4 + sizeof g_fhead)
            return q.clear(), g_frames_full = true, logf("frames: writing scskiller_frames.bin failed (%lu), no frames written", GetLastError());
    }
    for (auto& f : q) g_fenc.put(f);
    q.clear();
    auto& o = g_fenc.out;
    DWORD n = (DWORD)(o.size() * 4), done = 0;
    if (g_fenc.bytes + n > kFramesCap) g_frames_full = true, logf("frames: %llu bytes this launch, no more frames written", g_fenc.bytes);
    else if (!WriteFile(g_frames, o.data(), n, &done, nullptr) || done != n)
        g_frames_full = true, logf("frames: writing scskiller_frames.bin failed (%lu), no more frames written", GetLastError());
    else g_fenc.bytes += n;
    o.clear();
}

static void frame_writer() {
    std::vector<FrameAt> q;
    uint32_t creates = 0, failed = 0;
    for (int tick = 1;; ++tick) {
        Sleep(1000);
        {
            std::lock_guard l(g_fmx);
            q.swap(g_fq);
        }
        slm_tick();
        fs30_tick();
        frames_flush(q);
        trav::tick();
        if (g_ffx_retry.exchange(false)) ffx_hooks();
        ags_flush_notes();  // what a hook put in as its dll loaded couldn't log under the loader lock
        // here, not in Present: GetModuleHandle takes the loader's lock
        if (static bool fg; !fg && (fg = fg_module() != nullptr)) {  // DLSS-G has no swap chain the hooks tell apart: the report's mark
            logf("frames: %ls loaded (frame generation)", fg_module());
            std::lock_guard l(g_mx);
            if (g_csv) csv_line("#framegen,1\n");
        }
        if (static bool game; !game && g_ov_game && g_ovc.on) game = true, logf("overlay: the frames counted are frame generation's game frames");
        if (static bool startup; !startup && g_ov_startup >= 0) startup = true, logf("overlay: play counted from %.1f s, the first present", g_ov_startup / 1000);
        if (static uint32_t logged; g_ov_stutters != logged) logged = g_ov_stutters, logf("overlay: a shader compilation stutter (%u so far)", logged);
        if (static bool done; !done && g_ov_done_seen) done = true, logf("overlay: %d s of play counted, done", g_ovc.play_s);
        if (static bool wrote; !wrote && g_ov_drew) {  // here: the draw path never takes g_mx
            wrote = true;
            logf("overlay: first frame drawn");
            std::lock_guard l(g_mx);
            if (g_csv) csv_line("#overlay,1\n");
        }
        if (uint32_t f = g_sc_failed, n = g_sc_creates; (n != creates || f != failed) && n > 8)  // failed first: never more than the creates
            logf("frames: %u swap chain creates, %u failed", creates = n, failed = f);
        if (tick % 300 == 60 && g_presents) {  // after a minute, then every 5
            uint64_t n = g_presents;
            logf("frames: %llu presents, hook %.0f ns each on average, %.0f ns at most", n, g_hook_ticks * 1e9 / g_fenc.freq / n, g_hook_max * 1e9 / g_fenc.freq);
        }
    }
}

static std::wstring module_dir(HMODULE m);
// dev: the game's first device
static void frame_hooks(IUnknown* dev) {
    if (g_warm) return;
    wchar_t on[8];  // not cfg(): a staged warm child may inherit SCSKILLER_* variables
    GetPrivateProfileStringW(L"scskiller", L"frames", L"1", on, 8, (g_dir + L"scskiller.ini").c_str());
    if (!wcscmp(on, L"0")) return logf("frames: off (scskiller.ini frames=0)");
    // Ubisoft Connect's loader ships beside the exe (entry_hooks)
    g_entry = GetModuleHandleW(L"upc_r2_loader64.dll") || GetFileAttributesW((module_dir(nullptr) + L"upc_r2_loader64.dll").c_str()) != INVALID_FILE_ATTRIBUTES;
    g_opti_fgout = opti_fg_output();
    // the dxgi.dll the game uses: a mod's in the game folder, if one is loaded by that name
    HMODULE m = GetModuleHandleW(L"dxgi.dll");
    if (!m) m = LoadLibraryW(L"dxgi.dll");
    auto create = m ? (decltype(&CreateDXGIFactory1))GetProcAddress(m, "CreateDXGIFactory1") : nullptr;
    IDXGIFactory* f = nullptr;
    if (!create || FAILED(create(IID_PPV_ARGS(&f)))) return logf("frames: no DXGI factory, frame times not measured");
    g_sysdxgi = system_dxgi();
    HMODULE fm = m;
    if (g_entry) f->Release(), f = nullptr;
    else if (!sys_vtable(*(void***)f, "DXGI factory", SLOT_CREATESC)) {
        // A mod's own factory object (ReShade as dxgi.dll) creates through System32's: its class's vtable is hooked instead,
        // never the mod's. Checked again: System32's export may be the mod's hook.
        f->Release(), f = nullptr;
        auto sys_create = g_sysdxgi ? (decltype(&CreateDXGIFactory1))GetProcAddress(g_sysdxgi, "CreateDXGIFactory1") : nullptr;
        if (!sys_create || FAILED(sys_create(IID_PPV_ARGS(&f)))) return logf("frames: no DXGI factory from System32's dxgi.dll, frame times not measured");
        if (!sys_vtable(*(void***)f, "DXGI factory", SLOT_CREATESC)) {
            // ReShade as d3d12.dll or d3d11.dll hooks System32's exports too: the factory its proxy wraps
            IUnknown* u = nullptr;
            IDXGIFactory* real = nullptr;
            if (SUCCEEDED(f->QueryInterface(kUnwrapped, (void**)&u)) && u) u->QueryInterface(IID_PPV_ARGS(&real)), u->Release();
            f->Release(), f = real;
            if (!f) return logf("frames: System32's factory is a proxy that doesn't say what it wraps, frame times not measured");
            if (!sys_vtable(*(void***)f, "DXGI factory", SLOT_CREATESC)) return (void)f->Release();
        }
        logf("frames: System32's dxgi.dll's factory hooked instead");
        fm = g_sysdxgi;
    }
    LARGE_INTEGER q, fq;
    QueryPerformanceFrequency(&fq);
    QueryPerformanceCounter(&q);
    uint64_t head[4] = {(uint64_t)(g_session_unix ? g_session_unix : unix_ms()), (uint64_t)(now_ms() * 1000), (uint64_t)q.QuadPart, (uint64_t)fq.QuadPart};
    memcpy(g_fhead, head, sizeof head);
    g_fenc.qpc0 = q.QuadPart, g_fenc.freq = fq.QuadPart;
    if (g_entry) {
        if (!g_sysdxgi || !entry_hooks(dev)) return;
    } else {
        if (g_sysdxgi && !g_api11 && _wtoi(g_signed.overlay.c_str())) entry_hooks(dev);  // before the factory's hooks see its own swap chain
        std::lock_guard l(g_mx);
        void** vt = *(void***)f;
        patch(vt, SLOT_CREATESC, (void*)hk_createsc, o_createsc);
        IDXGIFactory2* f2;
        if (SUCCEEDED(f->QueryInterface(IID_PPV_ARGS(&f2)))) {
            void** vt2 = *(void***)f2;
            if (vt2 == vt || sys_vtable(vt2, "DXGI factory", SLOT_CREATESC_HWND)) {
                patch(vt2, SLOT_CREATESC_HWND, (void*)hk_createsc_hwnd, o_createsc_hwnd);
                patch(vt2, SLOT_CREATESC_CW, (void*)hk_createsc_cw, o_createsc_cw);
                patch(vt2, SLOT_CREATESC_COMP, (void*)hk_createsc_comp, o_createsc_comp);
            }
            f2->Release();
        }
        wchar_t path[MAX_PATH], from[MAX_PATH];
        GetModuleFileNameW(g_sysdxgi, path, MAX_PATH), GetModuleFileNameW(fm, from, MAX_PATH);
        g_dxgi_layer = m != g_sysdxgi;
        logf("frames: DXGI factory vtable %p hooked (%s; the factory of %s)", (void*)vt, shown(path).c_str(), shown(from).c_str());
        f->Release();
    }
    g_ffx_late = true;  // before the look below: a dll loading meanwhile is hooked by one of the two (g_ffx_mod)
    if (!ldr_notify()) logf("frames: no dll-load notification: a FidelityFX dll that loads later isn't hooked");
    ffx_hooks();
    std::thread(frame_writer).detach();
    g_frames_on = true;
}

// After frame_hooks: whether this launch draws the overlay at all.
static void overlay_config() {
    if (!_wtoi(g_signed.overlay.c_str())) return;  // not a before and after run
    const int play_s = _wtoi(g_signed.play_s.c_str());
    const wchar_t* tag = g_signed.tag.c_str();
    const char* why = play_s <= 0 ? "no ab_play_s"
                      : wcscmp(tag, L"before") && wcscmp(tag, L"after") ? "ab_tag isn't before or after"
                      : !g_frames_on ? "frame times aren't hooked"
                      : nullptr;
    if (why) return logf("overlay: off (%s)", why);
    // Under a layer (ReShade or OptiScaler as dxgi.dll, a chained d3d12.dll) the panel goes on the real swap chain, in the
    // Present the layer calls after its own drawing, with lists on the real queue the swap chain was made on: the layer
    // never sees them.
    if (g_next || b_gfx || g_dxgi_layer) logf("overlay: under a layer (%s), drawn on the system's swap chain", g_dxgi_layer ? "dxgi.dll" : g_next ? "next=" : "the device");
    ov::luts();  // made here: a present never allocates or computes them
    g_ovc = {true, !wcscmp(tag, L"after"), !g_api11, play_s};  // a D3D11 csv has no creates: no stutter to flash
    logf("overlay: on (%ls, %d s of play)", tag, play_s);
}

// Everything except D3D12CreateDevice is a raw jmp to the system dll (stubs.asm), so unknown
// signatures pass through untouched.
#define REAL_EXPORTS(X)                                                                                         \
    X(D3D12CoreCreateLayeredDevice) X(D3D12CoreGetLayeredDeviceSize) X(D3D12CoreRegisterLayers)                 \
    X(D3D12CreateRootSignatureDeserializer) X(D3D12CreateVersionedRootSignatureDeserializer)                    \
    X(D3D12DeviceRemovedExtendedData) X(D3D12EnableExperimentalFeatures) X(D3D12GetDebugInterface)              \
    X(D3D12GetInterface) X(D3D12PIXEventsReplaceBlock) X(D3D12PIXGetThreadInfo) X(D3D12PIXNotifyWakeFromFenceSignal) \
    X(D3D12PIXReportCounter) X(D3D12SerializeRootSignature) X(D3D12SerializeVersionedRootSignature)            \
    X(GetBehaviorValue) X(SetAppCompatStringPointer)
extern "C" {
#define DECL(n) void* real_##n;
REAL_EXPORTS(DECL)
#undef DECL
void* real_Ordinal99;
}

using PFN_CreateDevice = HRESULT(WINAPI*)(IUnknown*, D3D_FEATURE_LEVEL, REFIID, void**);
static PFN_CreateDevice real_CreateDevice;

// Every export works through any calling convention on x64: E_NOTIMPL in rax, which no caller takes for success.
static HRESULT WINAPI not_here() { return E_NOTIMPL; }

// Before any hook: they stay in the runtime's and nvapi64.dll's code for the life of the process, so what they point at
// is never unloaded.
static void pin_self() {
    HMODULE self;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, (LPCWSTR)&pin_self, &self);
}

// The device under a layer that wraps the one the game gets (ReShade as dxgi.dll hooks the system D3D12CreateDevice, so
// even the device we create comes wrapped); null when the game's device is the runtime's own.
static ID3D12Device* unwrapped(IUnknown* unk) {
    ID3D12Device *dev = nullptr, *real = nullptr;
    if (FAILED(unk->QueryInterface(IID_PPV_ARGS(&dev)))) return nullptr;
    if (d3d12_own(dev)) return dev->Release(), nullptr;  // no layer to look under, nothing created to find one
    IUnknown* u = nullptr;
    if (SUCCEEDED(dev->QueryInterface(kUnwrapped, (void**)&u)) && u) u->QueryInterface(IID_PPV_ARGS(&real)), u->Release();
    if (!real) {
        // a layer without that interface: root signatures aren't wrapped (ReShade, Streamline), so one's device is the real one
        auto ser = (decltype(&D3D12SerializeRootSignature))real_D3D12SerializeRootSignature;
        D3D12_ROOT_SIGNATURE_DESC rd{};
        ID3DBlob *blob = nullptr, *err = nullptr;
        ID3D12RootSignature* rs = nullptr;
        if (ser && SUCCEEDED(ser(&rd, D3D_ROOT_SIGNATURE_VERSION_1, &blob, &err)) &&
            SUCCEEDED(dev->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), IID_PPV_ARGS(&rs))))
            rs->GetDevice(IID_PPV_ARGS(&real)), rs->Release();
        if (blob) blob->Release();
        if (err) err->Release();
    }
    if (real && *(void***)real == *(void***)dev) real->Release(), real = nullptr;
    dev->Release();
    return real;
}

// Every device the game gets, however it asked for one: the hooks, once per vtable.
// Once per device: what besides the PSO desc could change what the driver compiles (experimental features, device flags,
// NVIDIA Aftermath, whose shader debug info and error reporting change the compiled code).
static void log_device(IUnknown* unk) {
    std::string feats;
    ID3D12DeviceConfiguration* cfg = nullptr;  // Agility SDK 1.610 and later
    if (SUCCEEDED(unk->QueryInterface(IID_PPV_ARGS(&cfg)))) {
        auto d = cfg->GetDesc();
        GUID g[16];
        if (d.NumEnabledExperimentalFeatures && d.NumEnabledExperimentalFeatures <= std::size(g) && SUCCEEDED(cfg->GetEnabledExperimentalFeatures(g, d.NumEnabledExperimentalFeatures)))
            for (UINT i = 0; i < d.NumEnabledExperimentalFeatures; ++i) {
                char b[40];
                snprintf(b, sizeof b, " %08lX-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X", g[i].Data1, g[i].Data2, g[i].Data3, g[i].Data4[0], g[i].Data4[1],
                         g[i].Data4[2], g[i].Data4[3], g[i].Data4[4], g[i].Data4[5], g[i].Data4[6], g[i].Data4[7]);
                feats += b;
            }
        char b[96];
        snprintf(b, sizeof b, "SDK %u, flags 0x%x, %u experimental features", d.SDKVersion, d.Flags, d.NumEnabledExperimentalFeatures);
        feats = b + feats;
        cfg->Release();
    } else feats = "no device configuration (a runtime before Agility 1.610)";
    logf("device: %s; NVIDIA Aftermath %s", feats.c_str(), GetModuleHandleW(L"GFSDK_Aftermath_Lib.x64.dll") ? "loaded" : "not loaded");
    hook_aftermath();   // a game initializes it on the device it just created
    ags_flush_notes();   // what the AGS load-lock path couldn't log (it installs only at load, never here)
}

static bool refused_late();
static HRESULT device_created(HRESULT hr, IUnknown* adapter, D3D_FEATURE_LEVEL fl, void** pp) {
    if (FAILED(hr) || !pp || !*pp) return hr;
    const bool ok = admitted();
    if (refused_late() || !ok) return hr;  // joined even when admitted() reads the late refusal: its cleanup comes first
    pin_self();
    if (ID3D12Device* d; !g_ov_nvidia && SUCCEEDED(((IUnknown*)*pp)->QueryInterface(IID_PPV_ARGS(&d)))) {
        IDXGIFactory4* f = nullptr;
        IDXGIAdapter1* a = nullptr;
        DXGI_ADAPTER_DESC1 ad = {};
        if (SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&f))) && SUCCEEDED(f->EnumAdapterByLuid(d->GetAdapterLuid(), IID_PPV_ARGS(&a))) &&
            SUCCEEDED(a->GetDesc1(&ad)) && ad.VendorId == 0x10DE)
            g_ov_nvidia = true;
        for (IUnknown* u : {(IUnknown*)a, (IUnknown*)f, (IUnknown*)d})
            if (u) u->Release();
    }
    install_hooks((IUnknown*)*pp);
    log_device((IUnknown*)*pp);
    if (g_next) {
        auto create = (decltype(&D3D12CreateDevice))GetProcAddress(g_real, "D3D12CreateDevice");
        ID3D12Device* dev = nullptr;
        if (create && SUCCEEDED(create(adapter, fl, IID_PPV_ARGS(&dev)))) hook_below(dev, "mod's"), dev->Release();
        else logf("below: no device from the system d3d12.dll");
    } else if (!g_warm) {
        if (ID3D12Device* real = unwrapped((IUnknown*)*pp)) hook_below(real, "layer's"), real->Release();
    }
    static std::once_flag nv, frames;
    std::call_once(nv, nv_hooks), std::call_once(frames, [pp] { frame_hooks((IUnknown*)*pp), overlay_config(); });
    return hr;
}

extern "C" HRESULT WINAPI Proxy_D3D12CreateDevice(IUnknown* adapter, D3D_FEATURE_LEVEL fl, REFIID riid, void** pp) {
    if (!real_CreateDevice) return E_NOTIMPL;  // loaded as d3d11.dll
    if (pp) dd_flush();  // our refs keep a released device alive, and the runtime hands out a live device again
    static std::once_flag once;
    std::call_once(once, [] {
        if (!(g_debug12 = GetEnvironmentVariableW(L"SCSKILLER_D3D12_DEBUG", nullptr, 0) > 0)) return;
        ID3D12Debug* dbg = nullptr;
        HRESULT dhr = ((decltype(&D3D12GetDebugInterface))real_D3D12GetDebugInterface)(IID_PPV_ARGS(&dbg));
        if (SUCCEEDED(dhr)) dbg->EnableDebugLayer(), dbg->Release();
        logf("d3d12 debug: %s (hr=0x%08x)", SUCCEEDED(dhr) ? "debug layer enabled" : "debug layer unavailable: install the Graphics Tools optional feature", (unsigned)dhr);
    });
    // only in an admitted run: a pass-through changes nothing. The raw ini only as a filter (the signed lines decide in
    // dred_settings): without both keys admission isn't asked here, before the device, so the off path is as before.
    static std::once_flag dred;
    std::call_once(dred, [] {
        const std::wstring ini = g_dir + L"scskiller.ini";
        if (!g_warm && !g_staged && GetPrivateProfileIntW(L"scskiller", L"traversal_suballocate", 0, ini.c_str()) == 1 &&
            GetPrivateProfileIntW(L"scskiller", L"traversal_dred", 0, ini.c_str()) == 1 && admitted())
            trav::dred_settings(real_D3D12GetDebugInterface);
    });
    return device_created(real_CreateDevice(adapter, fl, riid, pp), adapter, fl, pp);
}

#ifdef SCSK_AGS_WRAP
// Built as the game's amd_ags_x64.dll in front of its own AGS (renamed amd_ags_x64_scskiller.dll, linked by name: ags_exports.txt),
// for a game whose AGS makes the device through System32's d3d12.dll, which no d3d12.dll beside the exe sees.
static int (*g_ags_real_create)(void*, const void*, const AgsExtParams*, void*);
static int ags_real_create(void* ctx, const void* cp, const AgsExtParams* ep, void* rp) {
    int r = g_ags_real_create(ctx, cp, ep, rp);
    if (r == 0 && rp) device_created(S_OK, nullptr, D3D_FEATURE_LEVEL_11_0, (void**)rp);  // AGSDX12ReturnedParams starts with the device
    return r;
}
extern "C" int Wrap_AgsCreateDevice(void* ctx, const void* cp, const AgsExtParams* ep, void* rp) {
    static std::once_flag once;
    std::call_once(once, [] {
        HMODULE real = GetModuleHandleW(L"amd_ags_x64_scskiller.dll");
        g_ags_real_create = (decltype(g_ags_real_create))GetProcAddress(real, "agsDriverExtensionsDX12_CreateDevice");
        g_ags_version = (int (*)())GetProcAddress(real, "agsGetVersionNumber");  // AGS 6 and later
        o_ags_create = ags_real_create;
    });
    if (!g_ags_real_create) return 1;  // AGS_FAILURE
    return hk_ags_create(ctx, cp, ep, rp);  // the device, then the #ags line
}
#endif

// A device from ID3D12DeviceFactory::CreateDevice (D3D12GetInterface: CLSID_D3D12DeviceFactory, or
// ID3D12SDKConfiguration1::CreateDeviceFactory) never passes D3D12CreateDevice: its factory's vtable is hooked instead.
// Each SDK's D3D12Core.dll has its own factory vtable and original: one entry per vtable. A device of a second D3D12Core
// in the same process isn't recorded: the device hooks keep one original per method (patch logs the skip).
using PFN_FactoryCreateDevice = HRESULT(STDMETHODCALLTYPE*)(ID3D12DeviceFactory*, IUnknown*, D3D_FEATURE_LEVEL, REFIID, void**);
using PFN_CreateDeviceFactory = HRESULT(STDMETHODCALLTYPE*)(ID3D12SDKConfiguration1*, UINT, LPCSTR, REFIID, void**);
enum { SLOT_FACTORY_CREATEDEVICE = 9, SLOT_CREATEDEVICEFACTORY = 4 };  // vtslots.cpp checks these
struct FacVt { std::atomic<void**> vt; std::atomic<PFN_FactoryCreateDevice> orig; };
static FacVt g_facvt[8];
static std::atomic<int> g_nfacvt;
static PFN_CreateDeviceFactory o_createdevicefactory;
static std::atomic<void**> g_cfg_vt;  // the SDK configuration vtable whose CreateDeviceFactory slot is ours
static BOOL(NTAPI* g_cs_locked)(void*);  // RtlIsCriticalSectionLockedByThread, resolved at attach
// A vtable inside a loaded module: a late refusal can hold that module while it puts the slot back. Takes the loader lock.
static bool in_module(void* vt) {
    HMODULE m;
    return GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)vt, &m);
}
static HRESULT STDMETHODCALLTYPE hk_factory_createdevice(ID3D12DeviceFactory* f, IUnknown* adapter, D3D_FEATURE_LEVEL fl, REFIID riid, void** pp) {
    void** vt = *(void***)f;
    PFN_FactoryCreateDevice o = nullptr;
    for (int i = 0, n = g_nfacvt.load(std::memory_order_acquire); i < n && !o; ++i)
        if (g_facvt[i].vt == vt) o = g_facvt[i].orig;
    if (!o) return E_FAIL;  // unreachable: only a vtable in the table points here
    if (pp) dd_flush();
    return device_created(o(f, adapter, fl, riid, pp), adapter, fl, pp);
}
static void hook_factory(IUnknown* unk) {
    ID3D12DeviceFactory* f;
    if (!unk || g_admission < 0 || FAILED(unk->QueryInterface(IID_PPV_ARGS(&f)))) return;  // a pass-through installs nothing more
    pin_self();
    void** const vt = *(void***)f;  // read once: another hooker may swap the object's vtable meanwhile
    if (!in_module(vt)) {
        logf("hook: device factory vtable %p isn't in a module, not hooked", (void*)vt);
        return (void)f->Release();
    }
    {
        std::lock_guard l(g_mx);
        int n = g_nfacvt;
        if (g_admission > 0 && vt[SLOT_FACTORY_CREATEDEVICE] != (void*)hk_factory_createdevice) {
            if (n == (int)std::size(g_facvt)) logf("hook: a %zuth device factory vtable, not hooked", std::size(g_facvt) + 1);
            else {
                g_facvt[n].orig = (PFN_FactoryCreateDevice)vt[SLOT_FACTORY_CREATEDEVICE], g_facvt[n].vt = vt;  // before the slot
                g_nfacvt.store(n + 1, std::memory_order_release);
                DWORD old;
                VirtualProtect(&vt[SLOT_FACTORY_CREATEDEVICE], sizeof(void*), PAGE_READWRITE, &old);
                vt[SLOT_FACTORY_CREATEDEVICE] = (void*)hk_factory_createdevice;
                VirtualProtect(&vt[SLOT_FACTORY_CREATEDEVICE], sizeof(void*), old, &old);
                logf("hook: device factory vtable %p", (void*)vt);
            }
        }
    }
    f->Release();
}
static HRESULT STDMETHODCALLTYPE hk_createdevicefactory(ID3D12SDKConfiguration1* c, UINT sdk, LPCSTR path, REFIID riid, void** pp) {
    HRESULT hr = o_createdevicefactory(c, sdk, path, riid, pp);
    if (SUCCEEDED(hr) && pp) hook_factory((IUnknown*)*pp);
    return hr;
}
// PEB->LoaderLock (x64 offset 0x110) owned by this thread: a DllMain chain. Unknown counts as held.
static bool loader_lock_held() {
    void* cs = *(void* const*)((const uint8_t*)__readgsqword(0x60) + 0x110);
    return !g_cs_locked || !cs || g_cs_locked(cs);
}
extern "C" HRESULT WINAPI Proxy_D3D12GetInterface(REFCLSID clsid, REFIID riid, void** pp) {
    if (real_D3D12GetInterface == (void*)not_here) return E_NOINTERFACE;  // a runtime from before it
    HRESULT hr = ((decltype(&D3D12GetInterface))real_D3D12GetInterface)(clsid, riid, pp);
    if (FAILED(hr) || !pp || !*pp || (clsid != CLSID_D3D12DeviceFactory && clsid != CLSID_D3D12SDKConfiguration)) return hr;
    // decided before the first patch: a refused process never has its SDK configuration or factory hooked
    if (loader_lock_held()) return hr;  // admission may wait on a thread that needs the loader lock
    t_early = true;
    const bool ok = admitted();
    t_early = false;
    if (!ok) return hr;
    if (clsid == CLSID_D3D12DeviceFactory) hook_factory((IUnknown*)*pp);
    ID3D12SDKConfiguration1* c = nullptr;
    void** vt = nullptr;
    if (clsid == CLSID_D3D12SDKConfiguration && SUCCEEDED(((IUnknown*)*pp)->QueryInterface(IID_PPV_ARGS(&c))) && !in_module(vt = *(void***)c))
        logf("hook: SDK configuration vtable %p isn't in a module, not hooked", (void*)vt), c->Release(), c = nullptr;
    if (c) {
        pin_self();
        {
            std::lock_guard l(g_mx);
            if (g_admission > 0 && g_cfg_vt && g_cfg_vt != vt) logf("hook: a second SDK configuration vtable %p, not hooked", (void*)vt);
            else if (g_admission > 0) {
                patch(vt, SLOT_CREATEDEVICEFACTORY, (void*)hk_createdevicefactory, o_createdevicefactory);
                if (vt[SLOT_CREATEDEVICEFACTORY] == (void*)hk_createdevicefactory) g_cfg_vt = vt;
            }
        }
        c->Release();
    }
    return hr;
}

// At the first device after an early admission: the verdict as it is now, which every device waits for.
static bool refused_late() {
    if (!g_recheck) return false;
    static std::once_flag once;
    std::call_once(once, [] {
        std::string read, seen;
        bool risk = false;
        const char* why;
        {
            const bool whole = ini_bytes(g_dir + L"scskiller.ini", read);
            t_ini = &read;
            struct Live {
                ~Live() { t_ini = nullptr; }
            } live;
            why = admission_verdict(whole, read, g_elsewhere, risk, seen);
        }
        if (!why) return;
        {
            std::lock_guard l(g_mx);  // from here no slot is patched again (hook_factory and the SDK configuration check it under g_mx)
            g_ags_refused = true, g_admission = -1;
        }
        // each slot's module held across its put-back: an unused SDK can unload meanwhile (FreeUnusedSDKs). Outside g_mx:
        // GetModuleHandleExW takes the loader lock.
        struct Slot { void** vt; int slot; void* hook; void* orig; HMODULE m; };
        std::vector<Slot> slots;
        const auto add = [&](void** vt, int slot, void* hook, void* orig) {
            HMODULE m = nullptr;
            if (vt && orig && GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, (LPCWSTR)vt, &m)) slots.push_back({vt, slot, hook, orig, m});
        };
        add(g_cfg_vt.load(), SLOT_CREATEDEVICEFACTORY, (void*)hk_createdevicefactory, (void*)o_createdevicefactory);
        for (int i = 0, n = g_nfacvt.load(std::memory_order_acquire); i < n; ++i)
            add(g_facvt[i].vt, SLOT_FACTORY_CREATEDEVICE, (void*)hk_factory_createdevice, (void*)g_facvt[i].orig.load());
        bool back = true;
        {
            std::lock_guard l(g_mx);
            for (auto& s : slots) {
                DWORD old;
                if (!VirtualProtect(&s.vt[s.slot], sizeof(void*), PAGE_READWRITE, &old)) {
                    back = false;
                    continue;
                }
                back &= InterlockedCompareExchangePointer(&s.vt[s.slot], s.orig, s.hook) == s.hook;
                VirtualProtect(&s.vt[s.slot], sizeof(void*), old, &old);
            }
        }
        for (auto& s : slots) FreeLibrary(s.m);
        unhook_ags("refused at the first device"), ldr_off();
        std::string line = std::string(why) + " (at the first device, after the early decision)";
        if (!back) line += "; a slot another hook took meanwhile still forwards through ours";
        char t[24];
        snprintf(t, sizeof t, "[%9.1fs] ", now_ms() / 1000);
        {
            std::lock_guard l(g_early_mx);  // the last line, in the hold that ends the log: a later log call writes nothing
            if (g_log) fprintf(g_log, "%srefused: %s; nothing is recorded from here on\n", t, line.c_str()), fflush(g_log);
            g_log = nullptr, g_log_deferred = false;
        }
        refused(line.c_str());
    });
    return g_admission < 0;
}

// For scskiller_warm: start the replay on a device created through this dll (no PSO create needed to trigger it).
extern "C" void WINAPI SCSKiller_StartWarm(IUnknown* unk) {
    ID3D12Device* dev;
    if (SUCCEEDED(unk->QueryInterface(IID_PPV_ARGS(&dev)))) on_first_pso(dev), dev->Release();
}

// For scskiller_warm, after SCSKiller_WarmOptions and before its device: reads the dbs, which the device's creation would.
extern "C" void WINAPI SCSKiller_WarmLoad() { open_session(); }

// For scskiller_warm, after SCSKiller_WarmLoad: the dbs' D3D12 items (read here if they weren't). With none, the warm runs
// through SCSKiller_StartWarm11 and no D3D12 device is made: on AMD that alone leaves a DxcCache file under the exe's key.
extern "C" uint64_t WINAPI SCSKiller_D3D12Items() {
    open_session();
    return g_recs.size() + g_plan.size();
}

// For scskiller_warm: SCSKiller_StartWarm without a D3D12 device, the D3D11 items on this adapter.
extern "C" void WINAPI SCSKiller_StartWarm11(LUID adapter) {
    if (!g_warm) return;
    std::call_once(g_warm_once, [adapter] {
        g_warm_luid = adapter;
        logf("warm: D3D11 items only: no D3D12 device");
        std::thread(warm_main).detach();
    });
}

// For scskiller_warm, before SCSKiller_StartWarm: threads (0 = default), idle priority, first item to replay.
// Only scskiller_warm's staged child calls this, so it also marks the run as a warm-up, not a play session.
extern "C" void WINAPI SCSKiller_WarmOptions(int threads, BOOL idle, uint64_t start) {
    g_staged = true;
    if (threads > 0) g_threads = threads;
    g_prio = idle ? THREAD_PRIORITY_IDLE : THREAD_PRIORITY_ABOVE_NORMAL;  // in the child's below-normal class: base 7, below-normal as in a normal one
    g_start = start;
}

// For scskiller_warm, before SCSKiller_StartWarm (a retry): concurrent state object creates (0 = as many as the workers)
// and the items an earlier run found faulting or hanging alone (counted skipped, never replayed).
extern "C" void WINAPI SCSKiller_WarmRt(int rt_threads, const uint64_t* skip, uint32_t nskip) {
    g_rt_threads = rt_threads;
    for (uint32_t i = 0; i < nskip; ++i) g_skip.insert((size_t)skip[i]);
}

// For scskiller_warm, before SCSKiller_StartWarm: the keys (20 bytes each) of items that removed the device in an earlier run,
// never replayed, and the items to create alone before the workers start (in flight when an earlier process's device was removed).
extern "C" void WINAPI SCSKiller_WarmCrash(const uint8_t* keys, uint32_t nkeys, const uint64_t* alone, uint32_t nalone) {
    for (uint32_t i = 0; i < nkeys; ++i) {
        Hash h;
        memcpy(h.data(), keys + 20 * i, 20);
        g_crash_keys.insert(h);
    }
    g_alone.assign(alone, alone + nalone);
    std::sort(g_alone.begin(), g_alone.end());
}

// For scskiller_warm, after the warm finished: 2 a replay faulted, 1 the device was removed, 0 neither; the keys of this run's
// items that crash the driver (skipped, or blamed now) as JSON strings joined by commas; the items the retry creates alone
// first, joined by commas.
extern "C" BOOL WINAPI SCSKiller_Crashes(const char** keys, const char** alone) {
    *keys = g_crash_json.c_str(), *alone = g_alone_json.c_str();
    return g_fault_code ? 2 : g_removed ? 1 : 0;
}

// For scskiller_warm, after the warm finished: whether it stopped itself to free memory (g_mem_stop), {private MB, free MB} then.
extern "C" BOOL WINAPI SCSKiller_MemoryStop(uint64_t out[2]) {
    out[0] = (uint64_t)g_mem_stop_mb, out[1] = (uint64_t)g_mem_stop_free;
    return g_mem_stop && !g_rt_off && !g_removed && !g_hung && !g_fault_code;
}

// For scskiller_warm, before SCSKiller_StartWarm: the process's private memory budget in MB (0 = none), see g_mem_mb.
extern "C" void WINAPI SCSKiller_WarmMemory(uint32_t mb) { g_mem_mb = mb; }

// For scskiller_warm, before SCSKiller_StartWarm: each item's pass (n = the item count) and the one this process creates.
extern "C" void WINAPI SCSKiller_WarmPass(const uint8_t* pass_of, uint64_t n, uint32_t pass) {
    g_pass_of.assign(pass_of, pass_of + n);
    g_pass = (uint8_t)pass;
}

// For scskiller_warm, after the warm finished: {1 = the run ended poisoned and a new process must go on, the first item to
// replay (--start), its ray tracing threads, failures among the items before it, this run's item that failed alone or ~0}.
extern "C" void WINAPI SCSKiller_Retry(uint64_t out[5]) {
    int cur = g_rt_threads ? g_rt_threads : g_threads;
    int rt = g_removed || !g_rt_off ? g_rt_threads : g_rt_fatal != SIZE_MAX ? cur : std::max(1, cur / 4);
    uint64_t v[5] = {g_retry_from != SIZE_MAX, g_retry_from, (uint64_t)rt, g_retry_failed, g_rt_fatal == SIZE_MAX ? ~0ull : (uint64_t)g_rt_fatal};
    memcpy(out, v, sizeof v);
}

// For scskiller_warm, after the warm finished: whether its retry is for hangs alone (g_hung), and the items the next process
// skips (--skip), joined by commas: those that hung alone (a mass hang's are created alone, SCSKiller_Crashes).
extern "C" BOOL WINAPI SCSKiller_Hung(const char** skip) {
    *skip = g_alone_skip.c_str();
    return g_hung && !g_rt_off && !g_removed;
}

// For scskiller_warm, before SCSKiller_StartWarm: the keys (20 bytes each) of items that crashed or hung this driver in an
// earlier compile (g_known_keys).
extern "C" void WINAPI SCSKiller_WarmKnown(const uint8_t* keys, uint32_t nkeys) {
    for (uint32_t i = 0; i < nkeys; ++i) {
        Hash h;
        memcpy(h.data(), keys + 20 * i, 20);
        g_known_keys.insert(h);
    }
}

// For scskiller_warm, after the warm finished: {of the skipped, g_known_items and what builds on them, those among the items
// before the retry's first}; the keys of this run's items that hung or faulted the driver themselves, as JSON strings
// joined by commas.
extern "C" void WINAPI SCSKiller_Known(uint64_t out[2], const char** culprits) {
    out[0] = g_warm_known, out[1] = g_retry_known;
    *culprits = g_culprit_json.c_str();
}

// For scskiller_warm, never blocks: {skipped for this compile (SoSlot::skipped), those among the items before the retry's first}.
extern "C" void WINAPI SCSKiller_Skipped(uint64_t out[2]) {
    out[0] = g_warm_skipped, out[1] = g_retry_skipped;
}

extern "C" uint64_t WINAPI SCSKiller_Unsupported() { return g_warm_unsupported; }

using ScAdd = INT_PTR(WINAPI*)(INT_PTR, INT_PTR, INT_PTR, INT_PTR, INT_PTR, INT_PTR, INT_PTR, INT_PTR);
static ScAdd o_sc_add[2];
template <int I>
static INT_PTR WINAPI hk_sc_add(INT_PTR a, INT_PTR b, INT_PTR c, INT_PTR d, INT_PTR e, INT_PTR f, INT_PTR g, INT_PTR h) {
    ++t_cache_adds;
    return o_sc_add[I](a, b, c, d, e, f, g, h);
}

// For scskiller_warm, before its device: counts the pipelines created without a compile stored (SCSKiller_Cached). False
// when d3dscache.dll's stores can't be hooked: then nothing is counted.
extern "C" BOOL WINAPI SCSKiller_CountCached() {
    HMODULE m = LoadLibraryExW(L"d3dscache.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    void* add = m ? (void*)GetProcAddress(m, "ShaderCache_AddValue") : nullptr;
    void* add1 = m ? (void*)GetProcAddress(m, "ShaderCache_AddValue1") : nullptr;
    if (!add || !hook_fn(add, (void*)hk_sc_add<0>, (void**)&o_sc_add[0])) return FALSE;
    if (add1 && !hook_fn(add1, (void*)hk_sc_add<1>, (void**)&o_sc_add[1])) return FALSE;
    g_count_cached = true;
    return TRUE;
}

extern "C" uint64_t WINAPI SCSKiller_Cached() { return g_warm_cached; }

// For scskiller_warm, after the warm finished: the items from --start up to `to` it passed over without a create, for an
// earlier compile's crash or known key or as another pass's (counted done all the same).
extern "C" uint64_t WINAPI SCSKiller_PassedOver(uint64_t to) {
    uint64_t n = 0;
    for (uint64_t j = std::min(g_start, g_total); j < std::min(to, g_total); ++j) n += g_crash.count(j) || g_known_items.count(j) || other_pass(j);
    return n;
}

// For scskiller_warm, never blocks: {done (items before the start offset count as done), total, failed, warm finished}.
extern "C" uint64_t WINAPI SCSKiller_Work() { return g_work + g_released; }  // the parked state objects' release at the end too

extern "C" void WINAPI SCSKiller_Progress(uint64_t out[4]) {
    uint64_t v[4] = {std::min(g_start, g_total) + warm_done(), g_total, g_warm_fail, WaitForSingleObject(g_warm_done, 0) == WAIT_OBJECT_0};
    memcpy(out, v, sizeof v);
}

// For scskiller_warm's progress line: the D3D12 workers the memory budget lets run now, and the threads asked for.
extern "C" void WINAPI SCSKiller_Workers(uint32_t out[2]) {
    out[0] = g_d12_over ? g_threads : std::min<int>(g_allowed, g_threads), out[1] = g_threads;
}

// For scskiller_warm: RUN, PAUSE (workers wait before their next item) or STOP (workers take no more items, in-flight
// ones finish, the warm ends normally). STOP is final.
extern "C" void WINAPI SCSKiller_Control(int state) {
    for (int s = g_state; s != STOP && !g_state.compare_exchange_weak(s, state);) {}
}

// For the self-test: {db PSOs at start, db PSOs now, warm ok, warm fail, known hits, tuple-known hits, library loads}. Blocks until warm is done.
extern "C" void WINAPI SCSKiller_Stats(uint64_t out[7]) {
    WaitForSingleObject(g_warm_done, INFINITE);
    std::lock_guard l(g_mx);
    uint64_t v[7] = {g_db_records_at_start, g_keys.size(), g_warm_ok, g_warm_fail, g_known_hits, g_tuple_hits, g_lib_loads};
    memcpy(out, v, sizeof v);
}

static std::wstring cfg(const wchar_t* var, const wchar_t* key, const wchar_t* def) {
    wchar_t v[64];
    if (env(var, v)) return v;
    GetPrivateProfileStringW(L"scskiller", key, def, v, 64, (g_dir + L"scskiller.ini").c_str());
    return v;
}

// Not through the CRT: at exit the other threads are gone, and one may have died holding the csv stream's lock.
static void write_end_marker() {
    char b[64] = "#end,";
    char* e = std::to_chars(b + 5, b + sizeof b - 2, unix_ms()).ptr;
    *e++ = ',';  // then the same instant on the t_ms clock
    e = std::to_chars(e, b + sizeof b - 2, now_ms(), std::chars_format::fixed, 1).ptr;
    memcpy(e, "\r\n", 2);  // the csv is a text-mode stream
    out_raw(g_csvo, b, e + 2 - b, ~0ull);
}

// D3D11 recorder (this dll loaded as d3d11.dll): each input layout the game creates, with the vertex shader it's created
// against, as a 'D' record (VS, the layout, a triangle list behind a PS): what AMD compiles a VS for. Only the device is
// hooked: the runtime keeps rewriting a context's vtable (it lives in the context object, and its entries change with the
// context's state: multithread protection, the first draw), so a context's methods can't be hooked by their slots.
// Engines pass the VS's own bytecode (Unreal's bound shader states); a blob that isn't a VS still gives its layout to the
// planner's projection (Draws11).
using PFN_CreateIL11 = HRESULT(STDMETHODCALLTYPE*)(ID3D11Device*, const D3D11_INPUT_ELEMENT_DESC*, UINT, const void*, SIZE_T, ID3D11InputLayout**);
enum { D11_IL = 11 };  // vtslots.cpp checks it against d3d11.h at compile time
static PFN_CreateIL11 o11_il;
static uint64_t g11_layouts;

static size_t dxbc_len(const void* p, SIZE_T n) {  // a DXBC container's own size (offset 24); UE may append data after it
    uint32_t len = 0;
    if (n >= 28 && !memcmp(p, "DXBC", 4)) memcpy(&len, (const char*)p + 24, 4);
    return len && len <= n ? len : n;
}

static HRESULT STDMETHODCALLTYPE hk11_il(ID3D11Device* d, const D3D11_INPUT_ELEMENT_DESC* e, UINT n, const void* code, SIZE_T cn, ID3D11InputLayout** pp) {
    HRESULT hr = o11_il(d, e, n, code, cn, pp);
    if (FAILED(hr) || !pp || !*pp || (!e && n) || !code || !cn) return hr;
    Writer w{'D'};
    const size_t len = dxbc_len(code, cn);
    Hash vs = sha1(code, len);
    w.blobs.push_back({vs, {(const char*)code, len}});
    w.hash(vs), w.hash(kZero), w.hash(kZero), w.hash(kZero);
    w.u32(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST), w.u32(1);
    D3D12_INPUT_LAYOUT_DESC ld{(const D3D12_INPUT_ELEMENT_DESC*)e, n};  // the same fields in the same order (vtslots.cpp)
    w.il(ld);
    std::lock_guard l(g_mx);
    size_t before = g_keys.size();
    store(w, key_of('D', w.s));
    if (g_keys.size() > before && ++g11_layouts % 200 == 1) logf("d3d11: %llu input layouts recorded", g11_layouts);
    return hr;
}

// Every D3D11 device the game gets: its input layout creates (the device's vtable is the runtime's, shared and fixed).
static void device11_created(HRESULT hr, ID3D11Device* dev) {
    if (FAILED(hr) || !dev) return;
    const bool ok = admitted();
    if (refused_late() || !ok) return;
    pin_self();
    open_session();
    {
        std::lock_guard l(g_mx);
        void** vt = *(void***)dev;
        if (g_frames_only) logf("d3d11: device %p, frame times only (scskiller.ini frames_only=1)", (void*)dev);
        else {
            logf("d3d11: device %p vtable %p%s", (void*)dev, (void*)vt, vt[D11_IL] == (void*)hk11_il ? " (already hooked)" : "");
            patch(vt, D11_IL, (void*)hk11_il, o11_il);
        }
    }
    static std::once_flag frames;
    std::call_once(frames, [dev] { frame_hooks(dev), overlay_config(); });
}

// The device a create gave: its out-parameter, else (a game that asked only for the context or the swap chain) theirs.
static void device11_from(HRESULT hr, ID3D11Device** dev, ID3D11DeviceContext** ctx, IDXGISwapChain** sc) {
    if (FAILED(hr)) return;
    if (dev && *dev) return device11_created(hr, *dev);
    ID3D11Device* d = nullptr;
    if (ctx && *ctx) (*ctx)->GetDevice(&d);
    else if (sc && *sc) (*sc)->GetDevice(IID_PPV_ARGS(&d));
    if (d) device11_created(hr, d), d->Release();
}

using PFN_D3D11CreateDevice = decltype(&D3D11CreateDevice);
using PFN_D3D11CreateDeviceAndSwapChain = decltype(&D3D11CreateDeviceAndSwapChain);
static PFN_D3D11CreateDevice real11_CreateDevice;
static PFN_D3D11CreateDeviceAndSwapChain real11_CreateDeviceAndSwapChain;

extern "C" HRESULT WINAPI Proxy_D3D11CreateDevice(IDXGIAdapter* a, D3D_DRIVER_TYPE t, HMODULE sw, UINT flags, const D3D_FEATURE_LEVEL* fl,
                                                 UINT nfl, UINT sdk, ID3D11Device** dev, D3D_FEATURE_LEVEL* got, ID3D11DeviceContext** ctx) {
    if (!real11_CreateDevice) return E_NOTIMPL;  // loaded as d3d12.dll
    HRESULT hr = real11_CreateDevice(a, t, sw, flags, fl, nfl, sdk, dev, got, ctx);
    device11_from(hr, dev, ctx, nullptr);
    return hr;
}

extern "C" HRESULT WINAPI Proxy_D3D11CreateDeviceAndSwapChain(IDXGIAdapter* a, D3D_DRIVER_TYPE t, HMODULE sw, UINT flags, const D3D_FEATURE_LEVEL* fl,
                                                             UINT nfl, UINT sdk, const DXGI_SWAP_CHAIN_DESC* scd, IDXGISwapChain** sc,
                                                             ID3D11Device** dev, D3D_FEATURE_LEVEL* got, ID3D11DeviceContext** ctx) {
    if (!real11_CreateDeviceAndSwapChain) return E_NOTIMPL;
    HRESULT hr = real11_CreateDeviceAndSwapChain(a, t, sw, flags, fl, nfl, sdk, scd, sc, dev, got, ctx);
    device11_from(hr, dev, ctx, sc);
    if (g_frames_on && !g_entry) hook_swapchain(hr, sc ? *sc : nullptr);  // made inside the call, before the factory's hooks were in
    return hr;
}

// Every other d3d11.dll export is a raw jmp to the system dll (stubs.asm), at the system dll's ordinals (d3d12.def).
#define REAL11_EXPORTS(X)                                                                                                      \
    X(CreateDirect3D11DeviceFromDXGIDevice) X(CreateDirect3D11SurfaceFromDXGISurface) X(D3D11CoreCreateDevice)                 \
    X(D3D11CoreCreateLayeredDevice) X(D3D11CoreGetLayeredDeviceSize) X(D3D11CoreRegisterLayers) X(D3D11CreateDeviceForD3D12)   \
    X(D3D11On12CreateDevice) X(D3DKMTCloseAdapter) X(D3DKMTCreateAllocation) X(D3DKMTCreateContext) X(D3DKMTCreateDevice)      \
    X(D3DKMTCreateSynchronizationObject) X(D3DKMTDestroyAllocation) X(D3DKMTDestroyContext) X(D3DKMTDestroyDevice)             \
    X(D3DKMTDestroySynchronizationObject) X(D3DKMTEscape) X(D3DKMTGetContextSchedulingPriority) X(D3DKMTGetDeviceState)        \
    X(D3DKMTGetDisplayModeList) X(D3DKMTGetMultisampleMethodList) X(D3DKMTGetRuntimeData) X(D3DKMTGetSharedPrimaryHandle)      \
    X(D3DKMTLock) X(D3DKMTOpenAdapterFromHdc) X(D3DKMTOpenResource) X(D3DKMTPresent) X(D3DKMTQueryAdapterInfo)                 \
    X(D3DKMTQueryAllocationResidency) X(D3DKMTQueryResourceInfo) X(D3DKMTRender) X(D3DKMTSetAllocationPriority)                \
    X(D3DKMTSetContextSchedulingPriority) X(D3DKMTSetDisplayMode) X(D3DKMTSetDisplayPrivateDriverFormat) X(D3DKMTSetGammaRamp) \
    X(D3DKMTSetVidPnSourceOwner) X(D3DKMTSignalSynchronizationObject) X(D3DKMTUnlock) X(D3DKMTWaitForSynchronizationObject)    \
    X(D3DKMTWaitForVerticalBlankEvent) X(D3DPerformance_BeginEvent) X(D3DPerformance_EndEvent) X(D3DPerformance_GetStatus)     \
    X(D3DPerformance_SetMarker) X(EnableFeatureLevelUpgrade) X(OpenAdapter10) X(OpenAdapter10_2)
extern "C" {
#define DECL(n) void* real_##n;
REAL11_EXPORTS(DECL)
#undef DECL
}

// The folder of a module's path as the loader reports it, with the trailing backslash.
static std::wstring module_dir(HMODULE m) {
    const std::wstring s = module_path(m);
    return s.substr(0, s.find_last_of(L"\\/") + 1);
}
static bool same_file(const std::wstring& a, const std::wstring& b) {
    FILE_ID_INFO id[2];
    for (int i = 0; i < 2; ++i) {
        HANDLE h = CreateFileW((i ? b : a).c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
        if (h == INVALID_HANDLE_VALUE) return false;
        BOOL ok = GetFileInformationByHandleEx(h, FileIdInfo, &id[i], sizeof id[i]);
        CloseHandle(h);
        if (!ok) return false;
    }
    return !memcmp(&id[0], &id[1], sizeof id[0]);
}
// This dll's folder. The loader's path can be rewritten in user mode: REFramework points every dll in the exe's folder at a
// copy under <folder>\_storage_ (where no scskiller.ini is). The kernel's name of the mapped image can't be, so the exe's
// folder is taken when the image really is that folder's file, else the reported one.
static std::wstring own_dir(HMODULE self) {
    std::wstring dir = module_dir(self), exe_dir = module_dir(nullptr);
    if (!_wcsicmp(dir.c_str(), exe_dir.c_str())) return dir;
    std::wstring mapped(32768, L'\0');  // an NT path (\Device\HarddiskVolumeN\...), not bounded by MAX_PATH
    DWORD n = K32GetMappedFileNameW(GetCurrentProcess(), self, mapped.data(), (DWORD)mapped.size());
    if (!n || n >= mapped.size() - 1) return dir;
    mapped.resize(n);
    return same_file(L"\\\\?\\GLOBALROOT" + mapped, exe_dir + mapped.substr(mapped.find_last_of(L'\\') + 1)) ? exe_dir : dir;
}

BOOL WINAPI DllMain(HINSTANCE self, DWORD reason, LPVOID reserved) {
    if (reason == DLL_PROCESS_DETACH) {
        // reserved != nullptr: the process is terminating (not a plain FreeLibrary). Best effort, no lock: the loader lock
        // is held here and must never wait on anything.
        g_ov_exiting = true;
        if (reserved) out_exit(g_dbo), out_exit(g_csvo);  // before the "#end"
        if (reserved && g_wrote_session && !g_csvo.failed) write_end_marker();
        auto unreg = (LONG(NTAPI*)(void*))GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "LdrUnregisterDllNotification");
        if (!reserved && g_ldr_cookie && unreg) unreg(g_ldr_cookie);  // unloaded, not exiting: the callback goes with this dll
        g_log = nullptr;  // the CRT closes its streams after this returns: a later line (another dll's detach) is dropped
        return TRUE;
    }
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(self);
    g_dir = own_dir(self);
    g_elsewhere = _wcsicmp(g_dir.c_str(), module_dir(nullptr).c_str()) != 0;
    // loaded as d3d11.dll (the app installs it under that name in a D3D11 game): the D3D11 recorder, over the system d3d11.dll
    const std::wstring own = module_path(self);
    g_api11 = !_wcsicmp(own.c_str() + (own.find_last_of(L'\\') + 1), L"d3d11.dll");
    wchar_t p[MAX_PATH];
    g_frames_only = g_api11 && GetPrivateProfileIntW(L"scskiller", L"frames_only", 0, (g_dir + L"scskiller.ini").c_str()) == 1;  // not cfg(): ini only
    const wchar_t* sys_dll = g_api11 ? L"d3d11.dll" : L"d3d12.dll";
    const char* create_export = g_api11 ? "D3D11CreateDevice" : "D3D12CreateDevice";
    // the system d3d12's DllMain faults on a D3D12SDKPath the exe sets only after static init: the game's later load records
    const HMODULE exe = GetModuleHandleW(nullptr);
    const auto sdk_path = (const char* const*)GetProcAddress(exe, "D3D12SDKPath");
    GetSystemDirectoryW(p, MAX_PATH);
    const std::wstring sys_path = std::wstring(p) + L"\\" + sys_dll;
    if (!g_api11 && sdk_path && !*sdk_path && GetProcAddress(exe, "D3D12SDKVersion") && !GetModuleHandleW(sys_path.c_str()))
        return FALSE;
    g_real = LoadLibraryW(sys_path.c_str());
    if (!g_real) return FALSE;
    g_cs_locked = (decltype(g_cs_locked))GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlIsCriticalSectionLockedByThread");
    // next=<file name>: a mod's d3d12.dll (d3d11.dll) renamed next to us (the app's "record alongside"); every export goes
    // to it first. A bare file name only. If it doesn't load or has no device create, the system dll serves alone.
    wchar_t next[64];
    GetPrivateProfileStringW(L"scskiller", L"next", L"", next, 64, (g_dir + L"scskiller.ini").c_str());
    HMODULE mod = nullptr;
    const char* next_why = nullptr;
    if (*next && (wcspbrk(next, L"\\/:") || wcsstr(next, L"..")))
        next_why = "not a file name next to this dll";
    else if (*next && !(mod = LoadLibraryW((g_dir + next).c_str())))
        next_why = "failed to load";
    else if (mod == self)  // its device create would call itself
        next_why = "is this dll", mod = nullptr;
    else if (mod && !GetProcAddress(mod, create_export))
        next_why = g_api11 ? "has no D3D11CreateDevice" : "has no D3D12CreateDevice", mod = nullptr;
    auto res = [mod](const char* n) { void* f = mod ? (void*)GetProcAddress(mod, n) : nullptr; return f ? f : (void*)GetProcAddress(g_real, n); };
#define RES(n) real_##n = res(#n);
    REAL_EXPORTS(RES)
    REAL11_EXPORTS(RES)
    real11_CreateDevice = (PFN_D3D11CreateDevice)res("D3D11CreateDevice");
    real11_CreateDeviceAndSwapChain = (PFN_D3D11CreateDeviceAndSwapChain)res("D3D11CreateDeviceAndSwapChain");
#undef RES
    real_Ordinal99 = (void*)GetProcAddress(g_real, MAKEINTRESOURCEA(99));  // ordinals are per dll: a mod's 99 is something else
    // the other system dll's exports (and any this runtime lacks): a call fails instead of jumping to null
#define FALLBACK(n) if (!real_##n) real_##n = (void*)not_here;
    REAL_EXPORTS(FALLBACK)
    REAL11_EXPORTS(FALLBACK)
    FALLBACK(Ordinal99)
#undef FALLBACK
    real_CreateDevice = (PFN_CreateDevice)res("D3D12CreateDevice");
    // Only scskiller_warm (and selftest) export SCSKiller_WarmHost; its staged child runs under the game's exe name, so the
    // name can't tell. A game given mode=warm records instead.
    const bool warm_asked = cfg(L"SCSKILLER_MODE", L"mode", L"record") == L"warm";
    g_warm = !g_api11 && warm_asked && GetProcAddress(GetModuleHandleW(nullptr), "SCSKiller_WarmHost");  // the warm runs as d3d12.dll
    g_threads = _wtoi(cfg(L"SCSKILLER_THREADS", L"threads", L"0").c_str());
    if (g_threads <= 0) g_threads = std::max(1, (int)std::thread::hardware_concurrency() - 2);
    if (!g_warm) SetEvent(g_warm_done);
    if (g_warm) g_log = _wfopen((g_dir + L"scskiller.log").c_str(), L"a");
    else g_log_deferred = true;  // opened by admitted(), at the first device
    GetModuleFileNameW(nullptr, p, MAX_PATH);
    logf("loaded into %s", shown(p, true).c_str());
    if (warm_asked && !g_warm) logf("mode warm ignored: this process isn't scskiller_warm, it records");
    g_next = mod;
    if (mod) logf("next: %ls (the device and every export it has come from it)", next);
    else if (next_why) logf("next: %ls %s: the system %ls is used without it", wcspbrk(next, L"\\/:") ? L"(a path)" : next, next_why, sys_dll);
    ags_hooks(reserved != nullptr);
    return TRUE;
}
