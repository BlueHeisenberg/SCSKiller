// scskiller: d3d12.dll proxy. Records every PSO the game creates into scskiller.db and,
// in warm mode, replays the whole db into the driver shader cache so play never compiles.
//
// Install: copy d3d12.dll next to the game's *-Win64-Shipping.exe.
// Config:  scskiller.ini [scskiller] mode=record|warm threads=N next=<a mod's d3d12.dll, renamed>  (next to the dll), or env
//          SCSKILLER_MODE / SCSKILLER_THREADS (Steam launch options: SCSKILLER_MODE=warm %command%).
//          max_db_bytes=N (ini only; missing = no limit): once scskiller.db has N bytes, no record is appended.
//          frames=0 (ini only): no frame times.
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
// 'R' CreateStateObject (ray tracing pipeline or collection), 'A' AddToStateObject = base key[20] + the same body; the
// body and the replay rules are at write_so and in ARCHITECTURE.md ("Ray tracing state objects").
// 'N' NVAPI state of a record's create = the record's key[20] + u32 extension slot (~0u none), u32 space, u32 scope
// (1 device, 2 thread, 3 PSO extension), u32 pipeline creation flags (NvExt); the warm creates that record with it.
// 'W' a create a layer changed (hook_below) = the key[20] of the record the driver got + the key[20] of the record the
// game asked for (zero: the layer's own create, e.g. an add-on's or a bind-time clone). Written only when the two differ.
#define NOMINMAX
#include <windows.h>
#include <shlobj.h>
#include <psapi.h>
#include <share.h>
#include <d3d12.h>
#include <dxgi1_2.h>
#include <bcrypt.h>
#include <io.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <charconv>
#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <deque>
#include <format>
#include <functional>
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
#include "ledger_key.h"

#pragma comment(lib, "bcrypt.lib")
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
static std::wstring g_dir;
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
static std::vector<std::string> g_items11;                       // warm mode only: '1' and '2' payloads (D3D11 items; 24 / 40 bytes)
static HashSet g_known_tuples;                                   // (stages + root sig) tuples known at launch
static uint64_t g_lib_loads, g_db_records_at_start, g_creates, g_known_hits, g_tuple_hits, g_slow_known, g_slow_tuple, g_slow_unknown, g_unsupported;
static uint64_t g_repeats, g_slow_repeats;  // creates of a desc this process created before, and those over the slow mark
static HashSet g_created;                    // every key this process created (note)
static std::atomic<uint64_t> g_warm_ok, g_warm_fail, g_warm_crash;  // crash: skipped, it removed the device in an earlier run
static std::atomic<uint64_t> g_warm_other;                       // items of another pass: counted done, never created
static uint64_t warm_done() { return g_warm_ok + g_warm_fail + g_warm_crash + g_warm_other; }
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
static HANDLE g_csv_h;        // the csv's file handle, for that "#end"
static long long g_session_unix;  // that line's stamp, also the frames file's launch stamp: the two name one launch
static uint64_t g_db_bytes, g_db_cap;  // scskiller.db's size; max_db_bytes
static bool g_db_capped, g_db_full;

static double now_ms() { return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - g_t0).count(); }
// The stuck limits' clock: now_ms() without the time the process spent suspended (a paused warm). Only the running
// supervisor adds to it (D3D11's starts after D3D12's ends): a gap of over 5 s between its passes (50-100 ms apart) is
// a suspension, and counting it would wake every item in flight stuck for the pause's length.
static std::atomic<double> g_frozen_ms;
static double live_ms() { return now_ms() - g_frozen_ms; }
// A stamp later than a pass's now was made after a resume, before the pass counted the gap: it carries the pause, so
// the item counts from that now, or a hang in it would be found the pause's length late.
static double started(std::atomic<double>& since, double now) {
    double t = since;
    return t > now && since.compare_exchange_strong(t, now) ? now : t;
}
static double live_pass(double& last) {
    double now = now_ms();
    if (now - last > 5000) g_frozen_ms = g_frozen_ms + (now - last);
    last = now;
    return now - g_frozen_ms;
}
static long long unix_ms() { return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count(); }
// An environment variable into v; false when unset, empty or too long for v (the call then leaves v unwritten).
template <size_t N> static bool env(const wchar_t* name, wchar_t (&v)[N]) {
    DWORD n = GetEnvironmentVariableW(name, v, N);
    return n && n < N;
}
static std::wstring exe_name() {
    wchar_t p[MAX_PATH];
    GetModuleFileNameW(nullptr, p, MAX_PATH);
    std::wstring s = p;
    return s.substr(s.find_last_of(L"\\/") + 1);
}
// Until the first device decides admission, the log isn't opened (a pass-through writes nothing): lines wait here.
// g_log and g_log_deferred change under g_early_mx (admitted), and logf reads them under it.
static std::mutex g_early_mx;
static std::string g_early;
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

// Admission, decided once at the first device (any path: D3D12CreateDevice, a device factory; D3D12GetInterface's hooks
// only forward until then). The recorder records for the whole run only when all hold, else it is a pure pass-through
// (no device hooked, nothing written):
//  - scskiller.armed has [scskiller] armed=1 and this process's exe as it was then: the app wrote it after a clean full
//    anti-cheat check of the install and deletes it on any change there (GameFiles.DetectAntiCheat walks the install;
//    this dll doesn't);
//  - no anti-cheat marker in this folder: the built-in list, plus scskiller.ini markers= (it only adds; malformed: no);
//    an attestation bound to this process (pid= and pid_time=, below) drops EasyAntiCheat's names from that list;
//  - no "Riot Games" folder in the exe's path;
//  - no anti-cheat client module loaded.
// pid= and pid_time= (its creation FILETIME, UTC) bind the attestation to the one process the app started suspended
// itself, without EasyAntiCheat, for an offline session: in both files, equal, and this process's. Any other launch
// with the same files there (Steam's start_protected_game.exe, then EasyAntiCheat, then the game) is a pass-through.
// scskiller.armed is read again after the rest: changed or gone (the app disarmed it meanwhile) is a pass-through.
// Accepted limits: a client that loads later in an admitted run isn't caught here (the app removes the recorder and the
// next launch isn't armed); the app arms after its check and its install watcher's events arrive milliseconds after the
// change, so a launch inside that window may be admitted (the exe fingerprint covers updates); an anti-cheat added while
// SCSKiller isn't running, without any change to the game's exe (the armed file stays valid with the app closed, so the
// recorder keeps working then); another program deliberately holding SCSKiller's own ledger entry open (read-shared)
// together with the game-folder file (the app's <entry>.revoked mark beside it still refuses, when it can be made).
static const wchar_t* const kAntiCheatMarkers[] = {  // GameFiles.Markers; "*x": a name ending in x
    L"EasyAntiCheat", L"EasyAntiCheat_EOS", L"start_protected_game.exe", L"EasyAntiCheat_EOS_Setup.exe", L"EasyAntiCheat_Setup.exe",
    L"BattlEye", L"BEService.exe", L"BEService_x64.exe", L"BELauncher.exe", L"BEClient_x64.dll", L"BEClient.dll",
    L"EAAntiCheat.Installer.exe", L"GameGuard", L"XIGNCODE", L"nProtect", L"randgrid.sys", L"NCGuardSDK", L"NCGuard", L"AntiCheatExpert",
    L"AceAntibotClient", L"TP3Helper.exe", L"SGuard", L"SGuard64.exe", L"SGuardSvc64.exe", L"ACE-Base64.dll", L"ACE-Base.dat",
    L"ACE-Setup64.exe", L"ACE-Service64.exe", L"ACE-ATS64.dll", L"ACE-CSI64.dll", L"ACE-DFS64.dll", L"TenProtect", L"TesSafe.sys",
    L"HoYoKProtect.sys", L"mhypbase.dll", L"mhyprot.sys", L"mhyprot2.sys", L"mhyprot3.sys", L"ACE-BASE.sys",
    L"GenshinImpact.exe", L"YuanShen.exe", L"StarRail.exe", L"ZenlessZoneZero.exe", L"BH3.exe", L"HYP.exe", L"HYPHelper.exe",
    L"HYPWorker.exe", L"EAAntiCheat.GameServiceLauncher.exe", L"EAAntiCheat.GameServiceLauncher.dll", L"vgk.sys", L"vgc.exe",
    L"VALORANT.exe", L"VALORANT-Win64-Shipping.exe", L"League of Legends.exe", L"LeagueClient.exe", L"LeagueClientUx.exe",
    L"LeagueClientUxRender.exe", L"LoR.exe", L"Lion-Win64-Shipping.exe", L"RiotClientServices.exe", L"RiotClientUx.exe",
    L"RiotClientUxRender.exe",
    L"NeacClient.exe", L"NeacSafe64.sys", L"NeacSafe64_ex.sys",
    L"BlackCall.aes", L"BlackCall64.aes", L"BlackCat64.sys", L"HShield", L"PunkBuster", L"PnkBstrA.exe", L"pbsvc.exe", L"pbsv.dll",
    L"equ8_conf.json", L".build.info", L".product.db", L".build.config", L"Warframe.x64.exe", L"gameguard.des", L"DenuvoAC", L"denuvo-anti-cheat.sys", L"denuvo-anti-cheat-runtime.dll",
    L"denuvo-anti-cheat-update-service.exe", L"Denuvo Anti-Cheat Installer.exe", L"*.xem", L"*_BE.exe"};
static const size_t kEasyAntiCheatMarkers = 5;  // the list's first entries
static std::atomic<int> g_admission;  // 0 undecided, 1 records, -1 pass-through
static bool anti_cheat_loaded() {
    for (auto m : {L"EasyAntiCheat_x64.dll", L"EasyAntiCheat_EOS.dll", L"BEClient_x64.dll", L"BEClient.dll", L"mhypbase.dll"})
        if (GetModuleHandleW(m)) return true;
    return false;
}
// The exe's path has a "Riot Games" folder (GameFiles.RiotGames): the Riot Client installs every Riot title under one.
static bool riot_games() {
    std::wstring exe(32768, L'\0');
    DWORD n = GetModuleFileNameW(nullptr, exe.data(), (DWORD)exe.size());
    if (!n || n >= exe.size()) return true;
    exe.resize(n);
    for (auto& c : exe)
        if (c >= L'A' && c <= L'Z') c += L'a' - L'A';
    return exe.find(L"\\riot games\\") != std::wstring::npos;
}
static bool is_marker(const wchar_t* name, const std::vector<std::wstring>& markers) {
    size_t n = wcslen(name);
    for (auto& m : markers)
        if (m[0] == L'*' ? n >= m.size() - 1 && !_wcsicmp(name + n - (m.size() - 1), m.c_str() + 1) : !_wcsicmp(name, m.c_str())) return true;
    return false;
}
// False: an addition in markers= that isn't a plain name (empty, a path, a wildcard other than a leading '*').
static bool anti_cheat_markers(std::vector<std::wstring>& out) {
    out.assign(std::begin(kAntiCheatMarkers), std::end(kAntiCheatMarkers));
    wchar_t v[4096];
    DWORD n = GetPrivateProfileStringW(L"scskiller", L"markers", L"", v, 4096, (g_dir + L"scskiller.ini").c_str());
    if (n >= 4094) return false;
    if (!n) return true;
    std::wstring s = v;
    for (size_t at = 0;;) {
        size_t bar = s.find(L'|', at);
        std::wstring m = s.substr(at, bar == std::wstring::npos ? std::wstring::npos : bar - at);
        if (m.empty() || m.find_first_of(L"\\/:?\"<>") != std::wstring::npos || m.find(L'*', 1) != std::wstring::npos || m == L"*") return false;
        out.push_back(m);
        if (bar == std::wstring::npos) return true;
        at = bar + 1;
    }
}
// Any marker among this folder's entries (at most 100,000 read; more, or a listing that fails, counts as one).
static bool anti_cheat_beside(const std::vector<std::wstring>& markers) {
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileExW((g_dir + L"*").c_str(), FindExInfoBasic, &fd, FindExSearchNameMatch, nullptr, FIND_FIRST_EX_LARGE_FETCH);
    if (h == INVALID_HANDLE_VALUE) return true;
    bool found = false;
    size_t seen = 0;
    do found = ++seen > 100000 || is_marker(fd.cFileName, markers);
    while (!found && FindNextFileW(h, &fd));
    bool listed = found || GetLastError() == ERROR_NO_MORE_FILES;
    FindClose(h);
    return found || !listed;
}
// scskiller.armed as it is now (at most 4 KB; "" when missing or unreadable).
static std::string small_file(const std::wstring& path) {
    HANDLE h = path.empty() ? INVALID_HANDLE_VALUE : CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
    if (h == INVALID_HANDLE_VALUE) return "";
    std::string s(4096, '\0');
    DWORD n = 0;
    if (!ReadFile(h, s.data(), (DWORD)s.size(), &n, nullptr)) n = 0;
    CloseHandle(h);
    s.resize(n);
    return s;
}
// The app's ledger entry for this process's exe: %LOCALAPPDATA%\SCSKiller\armed\<the first of ledger_keys>. The app writes
// it with the nonce it puts in scskiller.armed and deletes it first when it disarms: its own folder, which nothing in the
// game holds open. "" when the folder or the exe path can't be had.
static std::wstring ledger_path() {
    std::wstring exe(32768, L'\0');
    DWORD n = GetModuleFileNameW(nullptr, exe.data(), (DWORD)exe.size());
    if (!n || n >= exe.size()) return L"";
    exe.resize(n);
    const std::wstring key = ledger_keys(exe).front();
    PWSTR local = nullptr;
    if (key.empty() || FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local))) return CoTaskMemFree(local), L"";
    std::wstring dir = local;
    CoTaskMemFree(local);
    return dir + L"\\SCSKiller\\armed\\" + key;
}
// Both attestations as they are now: scskiller.armed and the ledger entry.
static std::string armed_text() {
    const std::wstring ledger = ledger_path();
    return small_file(g_dir + L"scskiller.armed") + '\0' + small_file(ledger) + (GetFileAttributesW((ledger + L".revoked").c_str()) != INVALID_FILE_ATTRIBUTES ? "|revoked" : "");
}
// armed=1, checked= present, and the install the app checked is the one running: exe_size= and exe_time= (FILETIME, UTC)
// are this process's exe as it was then (the app closed while the game updated has no watcher to disarm; an update that
// adds anti-cheat ships a changed exe). bound: pid= and pid_time= name this process.
static bool armed(bool& bound) {
    bound = false;
    const std::wstring file = g_dir + L"scskiller.armed";
    wchar_t v[32];
    GetPrivateProfileStringW(L"scskiller", L"armed", L"", v, 32, file.c_str());
    if (wcscmp(v, L"1")) return false;
    auto number = [&](const wchar_t* key, uint64_t& out) {
        DWORD n = GetPrivateProfileStringW(L"scskiller", key, L"", v, 32, file.c_str());
        if (!n || n >= 30 || !iswdigit(v[0])) return false;
        wchar_t* end;
        errno = 0;
        out = wcstoull(v, &end, 10);
        return !*end && !errno;
    };
    if (!GetPrivateProfileStringW(L"scskiller", L"checked", L"", v, 32, file.c_str())) return false;
    std::wstring exe(32768, L'\0');
    DWORD n = GetModuleFileNameW(nullptr, exe.data(), (DWORD)exe.size());
    WIN32_FILE_ATTRIBUTE_DATA a;
    uint64_t size, time;
    if (!n || n >= exe.size() || !GetFileAttributesExW(exe.c_str(), GetFileExInfoStandard, &a) || !number(L"exe_size", size) || !number(L"exe_time", time))
        return false;
    if (size != ((uint64_t)a.nFileSizeHigh << 32 | a.nFileSizeLow) || time != ((uint64_t)a.ftLastWriteTime.dwHighDateTime << 32 | a.ftLastWriteTime.dwLowDateTime))
        return false;
    // the same nonce in the app's ledger: a scskiller.armed the app couldn't revoke (held open) has none there
    const std::wstring ledger = ledger_path();
    if (GetFileAttributesW((ledger + L".revoked").c_str()) != INVALID_FILE_ATTRIBUTES) return false;  // the app's mark beside an entry it couldn't revoke
    wchar_t nonce[64], kept[64];
    DWORD a1 = GetPrivateProfileStringW(L"scskiller", L"nonce", L"", nonce, 64, file.c_str());
    DWORD a2 = ledger.empty() ? 0 : GetPrivateProfileStringW(L"scskiller", L"nonce", L"", kept, 64, ledger.c_str());
    if (a1 != 32 || a2 != 32 || wcscmp(nonce, kept)) return false;
    // the process binding: the same in both files (none in both: unbound), and this process
    for (auto key : {L"pid", L"pid_time"}) {
        wchar_t here[32], there[32];
        DWORD n1 = GetPrivateProfileStringW(L"scskiller", key, L"", here, 32, file.c_str());
        DWORD n2 = GetPrivateProfileStringW(L"scskiller", key, L"", there, 32, ledger.c_str());
        if (n1 != n2 || wcscmp(here, there)) return false;
    }
    if (!GetPrivateProfileStringW(L"scskiller", L"pid", L"", v, 32, file.c_str())) return true;
    FILETIME created, x1, x2, x3;
    uint64_t pid, at;
    if (!number(L"pid", pid) || !number(L"pid_time", at) || pid != GetCurrentProcessId() || !GetProcessTimes(GetCurrentProcess(), &created, &x1, &x2, &x3)
        || at != ((uint64_t)created.dwHighDateTime << 32 | created.dwLowDateTime))
        return false;
    return bound = true;
}
// A pass-through writes nothing in the game folder: its reason goes beside the ledger entry (<entry>.refused, "unix_ms why"),
// where the app shows it on the game's page; an admitted run deletes it.
static void refused(const char* why) {
    const std::wstring ledger = ledger_path();
    if (ledger.empty()) return;
    const std::wstring file = ledger + L".refused";
    if (!why) return (void)DeleteFileW(file.c_str());
    // a PC the app never armed a game on has no ledger folder yet
    const std::wstring dir = ledger.substr(0, ledger.find_last_of(L'\\'));
    CreateDirectoryW(dir.substr(0, dir.find_last_of(L'\\')).c_str(), nullptr), CreateDirectoryW(dir.c_str(), nullptr);
    HANDLE h = CreateFileW(file.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;
    char b[160];
    int n = snprintf(b, sizeof b, "%lld %s", unix_ms(), why);
    DWORD w;
    WriteFile(h, b, (DWORD)std::clamp(n, 0, (int)sizeof b - 1), &w, nullptr);
    CloseHandle(h);
}
static bool admitted() {
    if (g_warm) return true;  // scskiller_warm's staged child replays; it is never a game
    static std::once_flag once;
    std::call_once(once, [] {
        std::vector<std::wstring> markers;
        const std::string attested = armed_text();
        bool bound;
        const char* why = !armed(bound) ? "not armed" : !anti_cheat_markers(markers) ? "markers= malformed" : nullptr;
        if (!why && bound) markers.erase(markers.begin(), markers.begin() + kEasyAntiCheatMarkers);  // the app started this process without EasyAntiCheat
        if (!why && riot_games()) why = "Riot Games install";
        if (!why) why = anti_cheat_beside(markers) ? "anti-cheat next to the exe" : anti_cheat_loaded() ? "anti-cheat client loaded" : nullptr;
        // tests: a change in between. SCSKILLER_TEST_ADMIT_GATE=<name>: sets the event <name>.checked, waits for <name>.go.
        // Only in a process exporting SCSKiller_WarmHost (selftest), never a game.
        if (wchar_t gate[64]; !why && GetProcAddress(GetModuleHandleW(nullptr), "SCSKiller_WarmHost") && env(L"SCSKILLER_TEST_ADMIT_GATE", gate)) {
            HANDLE checked = OpenEventW(EVENT_MODIFY_STATE, FALSE, (gate + std::wstring(L".checked")).c_str());
            HANDLE go = OpenEventW(SYNCHRONIZE, FALSE, (gate + std::wstring(L".go")).c_str());
            if (checked) SetEvent(checked), CloseHandle(checked);
            if (go) WaitForSingleObject(go, 60000), CloseHandle(go);
        }
        if (!why && armed_text() != attested) why = "disarmed while deciding";  // the app disarmed it meanwhile (an install change)
        {
            std::lock_guard l(g_early_mx);
            if (!why && (g_log = _wfopen((g_dir + L"scskiller.log").c_str(), L"a"))) fputs(g_early.c_str(), g_log), fflush(g_log);
            g_early.clear(), g_log_deferred = false;
            g_admission = why ? -1 : 1;
        }
        refused(why);
    });
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
enum { SLOT_GFX = 10, SLOT_CS = 11, SLOT_RS = 16, SLOT_CREATELIB = 44, SLOT_STREAM = 47, SLOT_CSO = 62, SLOT_ADDSO = 66 };
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
// extension of NvAPI_D3D12_Create*PipelineState), and the thread's pipeline creation flags (NvAPI_D3D12_SetCreatePipelineStateOptions:
// opacity micromaps, displaced micro-meshes).
struct NvExt { uint32_t slot = ~0u, space = 0, scope = 0, opts = 0; };
static thread_local NvExt t_nv;
static thread_local bool t_presenting;  // this thread has presented a frame (timed_present): its creates stall frames
static std::unordered_map<Hash, NvExt, HashH> g_nvext;  // 'N' records by the key of the record they apply to
static uint64_t g_nv_creates;
static std::atomic<uint64_t> g_nv_dev{~0u};  // slot | space << 32. ponytail: one for the process; per device if a game sets two
static NvExt nv_now() {
    NvExt x = t_nv;
    if (x.slot != ~0u) x.scope = 2;
    else if (uint64_t d = g_nv_dev; (uint32_t)d != ~0u) x.slot = (uint32_t)d, x.space = (uint32_t)(d >> 32), x.scope = 1;
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
    Tuple tup;
    NvExt nv = nv_now();  // at the hook's entry, on the creating thread
    bool below = false;   // a create under a layer: its root signatures are the ones the layer gave the device

    void raw(const void* p, size_t n) { s.append((const char*)p, n); }
    template <class T> void pod(T& v) { raw(&v, sizeof v); }
    void u32(uint32_t v) { raw(&v, 4); }
    void hash(const Hash& h) { raw(h.data(), 20); }
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
        rsh = *h;
        hash(*h);
    }
    // Stream output, only when it declares entries (NumEntries 0 = no SO, written as nothing: older records keep their
    // keys): u32 n, n x (u32 stream, str8 semantic, u32 index, start component, component count, output slot),
    // u32 strides n + strides, u32 rasterized stream. 'G': after the desc's last field; 'S': as subobject kSoDecl.
    void so(D3D12_STREAM_OUTPUT_DESC& d) {
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

    void fail(const char* w) { if (ok) why = w; ok = false; }

    void raw(void* p, size_t n) {
        if (pos + n > s.size()) { fail("record shorter than its fields (truncated or mis-encoded payload)"); memset(p, 0, n); return; }
        memcpy(p, s.data() + pos, n);
        pos += n;
    }
    template <class T> void pod(T& v) { raw(&v, sizeof v); }
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
    void rtfmt(UINT& n, DXGI_FORMAT* f) { n = u32(); for (UINT i = 0; i < 8; ++i) f[i] = (DXGI_FORMAT)u32(); }
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
static void put(char tag, const void* a, size_t an, const void* b = nullptr, size_t bn = 0) {
    uint32_t len = (uint32_t)(an + bn);
    fputc(tag, g_db), fwrite(&len, 4, 1, g_db), fwrite(a, 1, an, g_db);
    if (bn) fwrite(b, 1, bn, g_db);
    g_db_bytes += 5 + an + bn;
}

static ID3D12Device* g_warm_dev;
static ID3D12Device2* g_warm_dev2;
// A warm run with the game's layer (scskiller_warm --layer): the device under it, for what the layer made ('W'), which
// is replayed there as recorded (build)
static ID3D12Device* g_warm_real;
static ID3D12Device2* g_warm_real2;
static HashSet g_layer_made;
static thread_local bool t_real;
static ID3D12Device5* g_warm_dev5;
static ID3D12Device7* g_warm_dev7;

static int (*g_nv_set_thread)(IUnknown*, uint32_t, uint32_t);
static int (*g_nv_set_opts)(ID3D12Device5*, const uint32_t*);  // {version, flags}

static const NvExt* nvext_of(char tag, const std::string& payload) {
    if (g_nvext.empty()) return nullptr;
    auto it = g_nvext.find(key_of(tag, payload));
    return it == g_nvext.end() ? nullptr : &it->second;
}

// Every scope is replayed on the creating thread: NVIDIA's key doesn't hold the scope (selftest nvext), and a device-wide
// slot would reach the other workers' creates.
static int nv_slot(uint32_t slot, uint32_t space) { return g_nv_set_thread ? g_nv_set_thread(g_warm_dev, slot, space) : 0; }
static int nv_opts(uint32_t opts) {
    uint32_t o[2] = {8 | 1 << 16, opts};
    return g_nv_set_opts && g_warm_dev5 ? g_nv_set_opts(g_warm_dev5, o) : 0;
}
// what: 1 the slot, 2 the options. Returns what is still set.
static int nv_reset(int what) {
    if (what & 1 && !nv_slot(~0u, 0)) what &= ~1;
    if (what & 2 && !nv_opts(0)) what &= ~2;
    return what;
}
static thread_local int t_nv_stale;  // left set on this thread by a failed reset: every create here fails until a reset succeeds

// why: set when nothing may be created. A create without its own state would fill another cache key.
struct NvScope {
    const char* why = nullptr;
    int set = 0;
    explicit NvScope(const NvExt* x) {
        if (t_nv_stale && (t_nv_stale = nv_reset(t_nv_stale))) {
            why = "an NVAPI state reset failed on its thread";
            return;
        }
        if (!x) return;
        if (x->slot != ~0u && g_nv_set_thread && (set |= 1, nv_slot(x->slot, x->space))) why = "its NVAPI state could not be set";
        else if (x->opts && g_nv_set_opts && g_warm_dev5 && (set |= 2, nv_opts(x->opts))) why = "its NVAPI state could not be set";
    }
    ~NvScope() {
        if (int left = set ? nv_reset(set) : 0) {
            t_nv_stale |= left;
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
}
// SCSKILLER_WARM_ROUNDTRIP=1 (development): each recorded 'G' / 'C' / 'S' desc the warm decodes is serialized again and
// compared with its record (the log's "round trip" line); =only: compare without creating anything (fast, any adapter).
static int g_roundtrip;
static std::atomic<uint64_t> g_roundtrip_ok, g_roundtrip_bad;

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
    if (!base) { logf("db: mapping %ls failed (error %lu): its shader blobs are missing", path.c_str(), GetLastError()); return; }
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
            } else if (tag == '1' || tag == '2') {  // D3D11 items: nothing to record against, only warm needs them
                if (with_bytes) g_items11.push_back(std::move(p));
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
static void db_flush() {
    if (!fflush(g_db) && !ferror(g_db)) return;
    logf("db: writing scskiller.db failed (disk full?): nothing more is recorded in this launch");
    fclose(g_db), g_db = nullptr;
}

// Under g_mx: the record, the blobs it names and its NVAPI state, each written once. At the limit none of them is: a
// record is never split, and the csv row is still written (note).
static void store(const Writer& w, const Hash& k) {
    if (g_db_capped && g_db_bytes >= g_db_cap) {
        if (!g_db_full) g_db_full = true, logf("db: recording limit reached (%llu of %llu bytes): new pipelines are not recorded", g_db_bytes, g_db_cap);
        return;
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
        db_flush();
    }
    if (w.nv.slot != ~0u || w.nv.opts) {
        std::string n(36, '\0');
        memcpy(n.data(), k.data(), 20), memcpy(n.data() + 20, &w.nv, 16);
        if (fresh(g_keys, key_of('N', n)) && g_db) put('N', n.data(), n.size()), db_flush();
    }
}

// known = exact PSO was in the db at launch; tknown = its (stages + root sig) tuple was, recorded or generated.
// On NVIDIA tknown is what predicts a driver cache hit.
// lib: the game got this PSO from its own ID3D12PipelineLibrary (UE4's driver-optimized PSO disk cache), not a create.
// pre_ms: the hook's time before the create call (serializing and hashing the desc); the csv's proxy_ms adds this call's.
static void note(Writer& w, double ms, bool lib, double pre_ms) {
    auto n0 = std::chrono::steady_clock::now();
    double t_ret = now_ms();  // the csv's t_ms: when the create returned, not when the lock and the db write let the row out
    std::lock_guard l(g_mx);
    ++g_creates, g_lib_loads += lib;
    if (g_creates == 1) logf("first PSO seen (%s)", lib ? "pipeline library load" : "create");
    if (!w.ok) {
        if (++g_unsupported <= 10) logf("PSO not recorded ('%c', %s): %s", w.tag, lib ? "library" : "create", w.why);
        return;
    }
    Hash k = key_of(w.tag, w.s);
    bool so = w.tag == 'R' || w.tag == 'A';
    bool known, tknown;
    if (g_has_warmed) {
        known = std::binary_search(g_warmed.begin(), g_warmed.end(), k);
        tknown = known || (!so && std::binary_search(g_warmed_tuples.begin(), g_warmed_tuples.end(), tuple_hash(w.rsh, w.tup)));
    } else known = g_known.count(k) || imported(k), tknown = known || (!so && g_known_tuples.count(tuple_hash(w.rsh, w.tup)));
    // the same desc created again in this process: a driver that keeps its cache serves it at hit cost
    bool repeat = !g_created.insert(k).second;
    store(w, k);
    if (w.nv.slot != ~0u || w.nv.opts) {
        if (++g_nv_creates <= 5)
            logf("nvapi: a '%c' created with extension slot u%d space %u (%s), pipeline creation flags 0x%x", w.tag, (int)w.nv.slot, w.nv.space,
                 w.nv.scope == 1 ? "device" : w.nv.scope == 2 ? "thread" : w.nv.scope == 3 ? "PSO extension" : "none", w.nv.opts);
    }
    const double slow = 3.0;  // ms; a PSO cache hit is well under this, a compile well over (a cached state object isn't: ARCHITECTURE.md)
    g_known_hits += known, g_tuple_hits += tknown;
    (known ? g_slow_known : tknown ? g_slow_tuple : g_slow_unknown) += ms > slow;
    g_repeats += repeat, g_slow_repeats += repeat && ms > slow;
    if (g_csv) {  // key: the PSO key (sha1 of tag + payload, as the db's record); proxy_ms: the hook's own time, outside ms
        double own = pre_ms + std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - n0).count();
        fprintf(g_csv, "%.1f,%c,%d,%d,%.3f,%s,%.3f,%lu,%d\n", t_ret, lib ? w.tag | 0x20 : w.tag, (int)known, (int)tknown, ms, hex(k).data(), own,
                GetCurrentThreadId(), (int)t_presenting);
    }
    if (g_csv) fflush(g_csv);  // a crash or hard exit must not lose the timings
    if (g_creates % 500 == 0) {
        logf("creates=%llu (library loads %llu) known=%llu tuple_known=%llu | slow: known=%llu tuple_only=%llu unknown=%llu | repeats=%llu slow=%llu | unsupported=%llu db=%zu nvapi_state=%llu",
             g_creates, g_lib_loads, g_known_hits, g_tuple_hits, g_slow_known, g_slow_tuple, g_slow_unknown, g_repeats, g_slow_repeats, g_unsupported, g_keys.size(), g_nv_creates);
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
// alive until the warm ends. Their failure fails what builds on them.
struct SoSlot {
    std::once_flag once;
    HRESULT hr = E_FAIL;
    std::string why;  // decode / dependency failure; empty = the driver's hr
    ID3D12StateObject* obj = nullptr;
};
static std::vector<std::unique_ptr<SoSlot>> g_so;  // by g_recs index, 'R' / 'A' only
static std::vector<char> g_so_keep;

// After a fault or a hang in a state object call this process's driver can't be trusted (the NVIDIA case at g_parked). The
// process is "poisoned": state objects taken after that are left unfinished without calling the driver, PSOs go on, and the
// run ends with a retry (SCSKiller_Retry): a new process replays from the first unfinished item with a quarter of the ray
// tracing threads (32 -> 8 -> 2 -> 1). Only a state object that faults or hangs with 1 ray tracing thread counts as failed,
// and later runs skip it (SCSKiller_WarmRt).
static std::atomic<bool> g_rt_off;                 // poisoned
static int g_rt_threads;                           // SCSKiller_WarmRt: concurrent state object creates, 0 = no limit
static std::unordered_set<size_t> g_skip;          // --skip: counted failed, never replayed
static std::atomic<size_t> g_rt_fatal = SIZE_MAX;  // this run's item that faulted / hung alone
static void rt_stop(const char* why, size_t j) {
    if (g_rt_threads == 1 && j != SIZE_MAX) {
        size_t none = SIZE_MAX;
        if (g_rt_fatal.compare_exchange_strong(none, j)) logf("warm: item %zu: %s with 1 ray tracing thread: it counts as failed, later runs skip it", j, why);
    }
    bool was = false;
    if (g_rt_off.compare_exchange_strong(was, true))
        logf("warm: item %zu: %s: this process's driver can't be trusted for ray tracing any more; the state objects left run in a new process with fewer threads", j, why);
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
static size_t g_remove_item = SIZE_MAX;         // SCSKILLER_WARM_REMOVE=<item> (development): its create removes the device first
static bool removed() {
    if (g_removed) return true;
    HRESULT why = g_warm_dev ? g_warm_dev->GetDeviceRemovedReason() : S_OK;
    if (why == S_OK) return false;
    if (!g_removed.exchange(true)) logf("warm: the device was removed (reason 0x%08x): the replay stops, a new process goes on", (unsigned)why);
    return true;
}

static SIZE_T g_fault_item = SIZE_MAX;  // SCSKILLER_WARM_FAULT=<item>:<av|hang> (development): a fault / hang in that state object
static int g_fault_kind;                // 1 access violation after the create, 2 a create that never returns

static __declspec(noinline) HRESULT so_call(const Rec& rec, const D3D12_STATE_OBJECT_DESC* d, ID3D12StateObject* base, ID3D12StateObject** out, size_t j) {
    __try {  // a fault in the driver must not leave the once_flag half done (waiters would hang)
        if (j == g_fault_item && g_fault_kind == 2) Sleep(INFINITE);
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
// workers allowed to run (g_allowed) until it drops back, and raises it again once well under.
static uint32_t g_mem_mb;
static std::atomic<int> g_allowed{1 << 30}, g_active;
static HANDLE g_rt_sem;              // g_rt_threads at a time; nullptr = no limit
static std::atomic<uint64_t> g_released, g_release_batches;

static double private_mb() {
    PROCESS_MEMORY_COUNTERS_EX m{sizeof m};
    return K32GetProcessMemoryInfo(GetCurrentProcess(), (PROCESS_MEMORY_COUNTERS*)&m, sizeof m) ? m.PrivateUsage / 1048576.0 : 0;
}

static void release_parked(const char* why) {
    if (g_rt_off) return;  // a poisoned driver may hang on it: the process exit frees them
    std::unique_lock gate(g_rt_gate);  // no create runs while the driver tears objects down
    std::vector<ID3D12StateObject*> v;
    {
        std::lock_guard l(g_parkmx);
        v.swap(g_parked);
    }
    if (v.empty()) return;
    double before = private_mb(), t = now_ms();
    for (auto* o : v) so_release(o);
    g_released += v.size(), ++g_release_batches;
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
    const Rec& rec = g_recs[j];
    Reader r{rec.payload};
    std::string dep_why;
    auto dep = [&](const Hash& k) -> ID3D12StateObject* {
        auto it = g_rec_idx.find(k);
        if (it == g_rec_idx.end() || !g_so[it->second]) { r.fail("the state object it builds on is not in the db"); return nullptr; }
        if (g_rt_off) { dep_why = "ray tracing replay stopped", r.fail("dependency"); return nullptr; }  // never wait on a poisoned driver
        SoSlot& d = so_make(it->second);
        if (!d.obj) { dep_why = "the state object it builds on failed", r.fail("dependency"); return nullptr; }
        return d.obj;
    };
    ID3D12StateObject* base = rec.tag == 'A' ? dep(r.hash()) : nullptr;
    SoDesc d;
    if (r.ok) read_so(r, d, dep);
    if (!r.ok) { s.hr = E_INVALIDARG, s.why = dep_why.empty() ? r.why : dep_why; return; }
    D3D12_STATE_OBJECT_DESC desc = {d.type, (UINT)d.subs.size(), d.subs.data()};
    ID3D12StateObject* obj = nullptr;
    {
        std::shared_lock gate(g_rt_gate);  // taken out here: so_call catches any fault, so this always unlocks
        if (g_rt_sem) WaitForSingleObject(g_rt_sem, INFINITE);
        NvScope nv(nvext_of(rec.tag, rec.payload));
        if (!nv.why) s.hr = so_call(rec, &desc, base, &obj, j);
        else s.hr = E_INVALIDARG, s.why = nv.why;
        if (g_rt_sem) ReleaseSemaphore(g_rt_sem, 1, nullptr);
    }
    if (SUCCEEDED(s.hr) && obj) {
        if (g_so_keep[j]) s.obj = obj;
        else park(obj);
    }
}

// A fault anywhere in the create (decoding, root signatures, the driver) completes the once_flag with a failure instead of
// leaving it pending, which would hang every record built on this one.
static __declspec(noinline) void so_create_guarded(size_t j, SoSlot& s) {
    __try {
        so_create(j, s);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        s.hr = E_UNEXPECTED;
        rt_stop("an exception creating a state object", j);
    }
}

static SoSlot& so_make(size_t j) {
    SoSlot& s = *g_so[j];
    std::call_once(s.once, [&] { so_create_guarded(j, s); });
    return s;
}

// nv: a plan item's NVAPI state (an 'N' on its 'P'); a record's own is looked up here.
static int replay(size_t j, const Rec& rec, const Override* ovr = nullptr, const NvExt* nv = nullptr) {
    if (rec.tag == 'R' || rec.tag == 'A') {
        if (g_roundtrip == 2) return 1;  // round trip only: PSO descs are compared, state objects not created
        if (g_rt_off) return -1;  // poisoned: never call the driver for it here, a new process does
        SoSlot& s = so_make(j);
        if (FAILED(s.hr) && removed()) return -2;
        if (FAILED(s.hr) && g_rt_off && g_rt_fatal != j) return -1;  // the fault (or one it builds on): retried
        if (FAILED(s.hr)) {
            char what[200];
            if (!s.why.empty()) sprintf_s(what, "replay of '%c' (state object, %zu-byte payload) not created: %s", rec.tag, rec.payload.size(), s.why.c_str());
            else sprintf_s(what, "replay of '%c' (state object) hr=0x%08x", rec.tag, (unsigned)s.hr);
            note_fail(j, what);
        }
        return SUCCEEDED(s.hr) ? 1 : 0;
    }
    Reader r{rec.payload};
    r.ovr = ovr;
    ID3D12PipelineState* pso = nullptr;
    HRESULT hr = build(rec, r, &pso, ovr ? nv : nvext_of(rec.tag, rec.payload));
    if (pso) pso->Release();  // the driver keeps the compiled result in its disk cache
    if (FAILED(hr) && r.ok && removed()) return -2;
    if (FAILED(hr)) {
        char what[160];
        const char* kind = rec.tag == 'C' ? "compute desc" : rec.tag == 'G' ? "graphics desc" : "pipeline stream";
        if (!r.ok) sprintf_s(what, "%s '%c' (%s, %zu-byte payload) not created: %s", ovr ? "plan item on" : "replay of", rec.tag, kind, rec.payload.size(), r.why);
        else sprintf_s(what, "%s '%c' (%s) hr=0x%08x", ovr ? "plan item on" : "replay of", rec.tag, kind, (unsigned)hr);
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

// A fault inside a layer we call into (e.g. an overlay) must not take the game down: skip the PSO, stop after a few.
static std::atomic<int> g_warm_faults;
static bool is_so(size_t j, size_t nrec) { return j < nrec && (g_recs[j].tag == 'R' || g_recs[j].tag == 'A'); }
// 1 created, 0 failed, -1 left for a new process (ray tracing poisoned), -2 not created: the device was removed
static __declspec(noinline) int replay_one(size_t j, size_t nrec) {
    if (g_skip.count(j)) {
        note_fail(j, is_so(j, nrec) ? "state object faulted or hung in the driver alone in an earlier run" : "skipped (--skip)");
        return 0;
    }
    if (j == g_remove_item && g_warm_dev5) g_warm_dev5->RemoveDevice();
    return j < nrec ? replay(j, g_recs[j]) : replay_plan(j, g_plan[j - nrec]);
}
static int replay_guarded(size_t j, size_t nrec) {
    __try {
        return replay_one(j, nrec);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        if (is_so(j, nrec)) {
            rt_stop("an exception replaying a state object", j);
            return g_rt_fatal == j ? 0 : -1;
        }
        if (++g_warm_faults <= 3) logf("warm: exception 0x%08x replaying item %zu, skipped", GetExceptionCode(), j);
        return 0;
    }
}

// This run's items: 0 not taken, 1 created (or skipped: g_crash), 2 failed, 3 unfinished (left for the retry), 4 not created:
// the device was removed while it was in flight. SCSKiller_Retry reads them.
static std::vector<uint8_t> g_item_state;
static size_t g_retry_from = SIZE_MAX;  // set when the warm ends poisoned with items unfinished
static uint64_t g_retry_failed;         // failures among the items before g_retry_from (the rest are counted by the retry)

// D3D11 items (warm11.cpp): one draw/dispatch per shader on a D3D11 device on the warm device's adapter.
struct Dev11;
Dev11* warm11_open(LUID adapter, bool debug);
bool warm11_item(Dev11* d, uint32_t stage, const void* bytes, size_t n, const char** why);
bool warm11_pair(Dev11* d, const void* hs, size_t hsn, const void* ds, size_t dsn, const char** why);  // '2': HS + DS
void warm11_drain(Dev11* d, uint32_t stage);  // debug layer on: collect the messages the last item caused
HRESULT warm11_flush(Dev11* d);               // Flush + wait on an event query: everything drawn so far is compiled
void warm11_close(Dev11* d);                  // logs the collected debug-layer messages

static bool item11_guarded(Dev11* d, uint32_t stage, const std::string_view* b, const std::string_view* ds, const char** why) {
    __try {
        return ds ? warm11_pair(d, b->data(), b->size(), ds->data(), ds->size(), why) : warm11_item(d, stage, b->data(), b->size(), why);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        if (++g_warm_faults <= 3) logf("warm11: exception 0x%08x on a stage %u shader, skipped", GetExceptionCode(), stage);
        return *why = "exception", false;
    }
}

// D3D11 has no PSOs: the driver compiles at the first draw. Each worker thread owns a device (the immediate context
// is single-threaded) and takes batches of items; a batch counts as done once flushed and waited for, so a stop still
// leaves every item below "done" compiled. A supervisor (the calling thread) abandons a worker stuck in one D3D11 call:
// its batch counts as failed, a fresh worker with a fresh device carries on, and the stuck thread (and its device) is
// left alone for good. So the run always ends, and a stop is honoured within seconds.
struct Worker11 {
    std::thread t;
    std::atomic<double> busy{0};           // now_ms() when the current D3D11 call began, 0 = between calls
    std::atomic<bool> claimed{false};      // the current batch's outcome is taken: by the worker (counted) or the supervisor (abandoned)
    std::atomic<bool> finished{false};
    std::atomic<size_t> j{0}, end{0}, at{0};  // current batch, the item being drawn (= end: the flush)
    std::map<std::string, uint64_t> tally;  // "<stage> ok" / "<stage> failed: <why>"; read after finished
};

static size_t g_hang11 = SIZE_MAX;  // tests (SCSKILLER_TEST_HANG11): this item's call never returns, like a hung driver
static DWORD g_hold11 = INFINITE;   // or returns after this many ms
static int g_hold11_wall;           // ... of wall time (a suspension counts), and then the next item's call never returns

static void worker11(Worker11& w, std::atomic<size_t>& next, size_t n, bool debug) {
    static const char* names[] = {"?", "VS", "PS", "DS", "HS", "GS", "CS", "HS+DS"};
    const size_t batch = 16;
    SetThreadPriority(GetCurrentThread(), g_prio);
    w.busy = live_ms();
    Dev11* d = warm11_open(g_warm_dev->GetAdapterLuid(), debug);
    w.busy = 0;
    if (!d) { g_warm_faults = 3; w.finished = true; return; }
    for (size_t j; g_warm_faults < 3;) {
        while (g_state == PAUSE) Sleep(50);
        if (g_state == STOP || (j = next.fetch_add(batch)) >= n) break;
        w.j = j, w.end = std::min(j + batch, n), w.claimed = false;
        uint64_t ok = 0, bad = 0, other = 0;
        for (size_t k = j; k < w.end; ++k) {
            if (other_pass(g_recs.size() + g_plan.size() + k)) { ++other; continue; }
            const std::string& it = g_items11[k];
            uint32_t stage = 0;
            Hash h{}, h2{};
            const bool pair = it.size() == 40;  // '2': HS + DS
            if (it.size() == 24) memcpy(&stage, it.data(), 4), memcpy(h.data(), it.data() + 4, 20);
            if (pair) stage = 7, memcpy(h.data(), it.data(), 20), memcpy(h2.data(), it.data() + 20, 20);
            auto b = g_blob_bytes.find(h), b2 = pair ? g_blob_bytes.find(h2) : g_blob_bytes.end();
            const char* why = it.size() != 24 && !pair ? "malformed item" : b == g_blob_bytes.end() || (pair && b2 == g_blob_bytes.end()) ? "missing blob" : "";
            w.at = k, w.busy = live_ms();
            if (k == g_hang11 && g_hold11_wall) for (ULONGLONG end = GetTickCount64() + g_hold11; GetTickCount64() < end;) Sleep(1);
            else if (k == g_hang11 || (g_hold11_wall && k == g_hang11 + 1)) Sleep(k == g_hang11 ? g_hold11 : INFINITE);
            bool good = !*why && item11_guarded(d, stage, &b->second, pair ? &b2->second : nullptr, &why);
            w.busy = 0;
            if (w.claimed) return;  // abandoned while stuck in that call: touch nothing shared, leak the device
            warm11_drain(d, stage);
            (good ? ok : bad)++;
            w.tally[std::string(names[stage < 8 ? stage : 0]) + (good ? " ok" : std::string(" failed: ") + why)]++;
        }
        w.at = w.end.load(), w.busy = live_ms();
        HRESULT hr = warm11_flush(d);
        w.busy = 0;
        if (w.claimed.exchange(true)) return;
        if (FAILED(hr)) {  // device removed (e.g. a TDR): the batch failed, go on with a new device
            logf("warm11: D3D11 device lost (0x%08x) on items %zu-%zu; recreating it", (unsigned)hr, j, w.end - 1);
            g_warm_fail += w.end - j - other, g_warm_other += other;
            w.tally["batch failed: device lost"] += w.end - j - other;
            w.j = w.end = w.at = 0, w.claimed = false, w.busy = live_ms();  // no batch: a hang in here abandons nothing twice
            warm11_close(d);
            d = warm11_open(g_warm_dev->GetAdapterLuid(), debug);
            w.busy = 0;
            if (w.claimed) return;
            if (!d) { g_warm_faults = 3; w.finished = true; return; }
            continue;
        }
        g_warm_ok += ok, g_warm_fail += bad, g_warm_other += other;
    }
    warm11_close(d);
    w.finished = true;
}

static bool g_abandoned11;  // a stuck D3D11 worker may still wake up: keep what it could touch alive
static bool g_abandoned12;  // likewise a stuck D3D12 worker (warm_main)
static thread_local bool t_replay;  // a warm worker: its creates reach the hooks below a mod (hook_below) too

static void warm11_main(size_t first) {
    const size_t n = g_items11.size();
    const bool debug = GetEnvironmentVariableW(L"SCSKILLER_D3D11_DEBUG", nullptr, 0) > 0;  // D3D11 debug layer, for development
    // Devices = threads. Measured (NVIDIA, 3500 cold Orcs Must Die 3 shaders, zero-count indirect draws): 2: 714/s,
    // 4: 1030/s, 8: 1130/s; the full 17752 cached: ~3600/s. Idle or below-normal priority: same on an idle machine.
    const int threads = std::min(g_threads, 4);
    // One call (a create + draw, or the flush of a batch of 16) taking this long is a hang, not a slow compile.
    double stuck_ms = 60000, stuck_stop_ms = 2000;  // ponytail: fixed limits, measure the slowest real batch if they bite
    wchar_t test[64];
    if (env(L"SCSKILLER_TEST_HANG11", test)) swscanf_s(test, L"%zu,%lf,%lu,%d", &g_hang11, &stuck_ms, &g_hold11, &g_hold11_wall);  // "<item>,<limit ms>[,<hold ms>[,1]]"
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
                std::string call = at < end ? "drawing item " + std::to_string(at) : end > j ? "waiting for the GPU after items "
                                   + std::to_string(j) + "-" + std::to_string(end - 1) : "creating its device";
                logf("warm11: a D3D11 worker is stuck (%.0f s %s): abandoned, its %zu items count as failed", (now - since) / 1000, call.c_str(), end - j);
                g_warm_fail += end - j;
                tally["batch failed: device stuck"] += end - j;
                g_abandoned11 = true;
                w->t.detach();  // w and its device are leaked on purpose: the thread may still return into them
                ws.erase(ws.begin() + i);
                if (g_state != STOP && *next < n && ++replaced <= 8) spawn();
                else if (replaced > 8) g_warm_faults = 3;  // the driver keeps hanging: give up, the run ends with an error
            } else ++i;
        }
    }
    for (auto& [k, c] : tally) logf("warm11: %s x%llu", k.c_str(), c);
    logf("warm11: %zu D3D11 items on %d threads in %.1f s", std::min(next->load(), n) - first, threads, (now_ms() - t0) / 1000);
    if (!g_abandoned11) delete next;
}

// Items go in file order (db records, gen templates, plan items, D3D11 items), so a stopped run resumes at item g_start.
static void warm_main() {
    size_t nrec = g_recs.size(), n12 = nrec + g_plan.size(), n = n12 + g_items11.size(), first = std::min<size_t>(g_start, n);
    std::atomic<size_t> next{first};
    g_so.resize(nrec), g_so_keep.assign(nrec, 0);
    size_t nso = 0;
    for (size_t j = 0; j < nrec; ++j)
        if (g_recs[j].tag == 'R' || g_recs[j].tag == 'A') {
            g_so[j] = std::make_unique<SoSlot>(), ++nso;
            for (auto& k : so_deps(g_recs[j].payload, g_recs[j].tag))
                if (auto it = g_rec_idx.find(k); it != g_rec_idx.end()) g_so_keep[it->second] = 1;
        }
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
    logf("warm: D3D12 runtime %ls (SDK %u)", core, sdk ? *sdk : 0);
    // SCSKILLER_WARM_TIMES=1 (development): stage\scskiller_warm_times.csv, "item,ms,ok" per PSO (A/B tests of what a warm leaves cached)
    FILE* times = GetEnvironmentVariableW(L"SCSKILLER_WARM_TIMES", nullptr, 0) ? _wfopen((g_dir + L"scskiller_warm_times.csv").c_str(), L"w") : nullptr;
    wchar_t rt[8] = {};
    if (env(L"SCSKILLER_WARM_ROUNDTRIP", rt)) g_roundtrip = wcscmp(rt, L"only") ? 1 : 2;
    wchar_t park[16] = {};
    if (g_mem_mb) g_park_mb = std::max<uint32_t>(256, g_mem_mb / 4);
    if (env(L"SCSKILLER_WARM_RT_PARK_MB", park)) g_park_mb = _wtoi(park);
    g_allowed = g_threads;
    if (g_mem_mb && private_mb() > g_mem_mb)  // the dbs' records alone are over it: start with one worker
        g_allowed = 1, logf("warm: private memory %.0f MB over the %u MB budget at the start: 1 worker", private_mb(), g_mem_mb);
    if (g_rt_threads > 0 && g_rt_threads < g_threads) {
        g_rt_sem = CreateSemaphoreW(nullptr, g_rt_threads, g_rt_threads, nullptr);
        logf("warm: ray tracing on %d threads (a retry after a driver fault or hang)", g_rt_threads);
    }
    if (g_threads == 1) g_rt_threads = 1;  // one thread: a fault is its item's alone
    for (size_t k : g_skip)
        if (k < nrec && g_so[k]) std::call_once(g_so[k]->once, [&] { g_so[k]->hr = E_FAIL, g_so[k]->why = "it faulted or hung in the driver alone in an earlier run"; });
    g_item_state.assign(n12, 0);
    auto key = [&](size_t j) { return j < nrec ? key_of(g_recs[j].tag, g_recs[j].payload) : key_of('P', g_plan[j - nrec]); };
    HashSet crash_found;
    if (!g_crash_keys.empty()) {
        for (size_t j = 0; j < n12; ++j)
            if (Hash k = key(j); g_crash_keys.count(k)) g_crash.insert(j), crash_found.insert(k);
        for (size_t k : g_crash)  // what builds on one fails instead of creating it
            if (k < nrec && g_so[k]) std::call_once(g_so[k]->once, [&] { g_so[k]->hr = E_FAIL, g_so[k]->why = "it removed the device in an earlier run"; });
        logf("warm: %zu items skipped: they removed the device in an earlier run", g_crash.size());
    }
    wchar_t fault[32] = {};  // SCSKILLER_WARM_FAULT=<item>:<av|hang> (development): see so_call
    if (env(L"SCSKILLER_WARM_FAULT", fault)) g_fault_item = _wtoi64(fault), g_fault_kind = wcsstr(fault, L"hang") ? 2 : 1;
    if (env(L"SCSKILLER_WARM_REMOVE", fault)) g_remove_item = _wtoi64(fault);
    // In flight when an earlier process's device was removed: each alone, before the workers, so a removal names its item.
    // ponytail: no supervisor over these few; a create that hangs here stalls the run like a hung driver does anywhere
    size_t blamed_alone = SIZE_MAX, alone_at = 0;
    t_replay = true;
    for (; alone_at < g_alone.size() && g_state != STOP; ++alone_at) {
        size_t k = g_alone[alone_at];
        if (k < first || k >= n12 || g_crash.count(k) || other_pass(k)) continue;
        while (g_state == PAUSE) Sleep(50);
        int ok = replay_guarded(k, nrec);
        if (ok == -2 || removed()) { blamed_alone = k; break; }
        g_item_state[k] = ok == 1 ? 1 : ok == 0 ? 2 : 0;  // counted when a worker takes it
        logf("warm: item %zu, in flight when the device was removed, created alone: %s", k, ok == 1 ? "ok" : ok == 0 ? "failed" : "left");
    }
    if (blamed_alone != SIZE_MAX) logf("warm: item %zu removed the device alone: later runs skip it", blamed_alone);
    wchar_t stuck_env[16] = {};  // SCSKILLER_WARM_STUCK_S (development): the per-item limit, default 60 s
    double stuck_ms = env(L"SCSKILLER_WARM_STUCK_S", stuck_env) ? 1000.0 * _wtoi(stuck_env) : 60000, stuck_stop_ms = 2000;

    // Workers take items in file order. A supervisor (this thread) abandons a worker stuck in one item for over stuck_ms (2 s
    // once stopping), as warm11 does: the item counts as failed, a stuck state object stops the ray tracing phase (rt_stop),
    // a new worker carries on (at most 8 times, then the run ends with an error). The stuck thread is left alone.
    struct Worker12 {
        std::thread t;
        std::atomic<double> since{0};       // when its current item started; 0 = between items
        std::atomic<size_t> j{0};
        std::atomic<bool> claimed{false};  // the current item's outcome is taken: by the worker (counted) or the supervisor (abandoned)
        std::atomic<bool> finished{false};
    };
    std::mutex tmx;
    std::vector<Worker12*> ws;
    auto spawn = [&] {
        auto* w = new Worker12;
        w->t = std::thread([&, w] {
            SetThreadPriority(GetCurrentThread(), g_prio);
            t_replay = true;
            for (size_t j; g_warm_faults < 3;) {
                while (g_state == PAUSE) Sleep(50);
                for (int a = g_active; g_state != STOP && !g_removed && (a >= g_allowed || !g_active.compare_exchange_weak(a, a + 1)); a = g_active)
                    if (a >= g_allowed) Sleep(20);  // over the memory budget: fewer workers run
                if (g_state == STOP || g_removed) break;
                if ((j = next++) >= n12) { --g_active; break; }
                if (other_pass(j)) { --g_active, g_item_state[j] = 1, ++g_warm_other; continue; }
                w->claimed = false, w->j = j;
                double t = live_ms();
                w->since = t;
                bool crash = g_crash.count(j);
                int ok = crash ? 1 : g_item_state[j] ? 2 - g_item_state[j] : replay_guarded(j, nrec);  // created alone: its outcome
                w->since = 0;
                if (w->claimed.exchange(true)) return;  // abandoned meanwhile: counted by the supervisor, its replacement carries on
                --g_active;
                g_item_state[j] = ok > 0 ? 1 : ok == 0 ? 2 : ok == -2 ? 4 : 3;
                if (crash) g_warm_crash++;
                else if (ok >= 0) (ok ? g_warm_ok : g_warm_fail)++;
                if (times) { std::lock_guard l(tmx); fprintf(times, "%zu,%.3f,%d\n", j, live_ms() - t, ok); }
            }
            w->finished = true;
        });
        ws.push_back(w);
    };
    for (int i = 0; i < g_threads && blamed_alone == SIZE_MAX; ++i) spawn();
    int replaced = 0;
    double pass = now_ms();
    auto supervise = [&] {
        double now = live_pass(pass);
        for (size_t i = 0; i < ws.size();) {
            Worker12* w = ws[i];
            double since = started(w->since, now);
            if (w->finished) {
                w->t.join();
                delete w;
                ws.erase(ws.begin() + i);
            } else if (since && now - since > (g_state == STOP ? stuck_stop_ms : stuck_ms) && !w->claimed.exchange(true)) {
                size_t j = w->j;
                bool so = is_so(j, nrec);
                if (so) rt_stop("a state object create hung", j);
                // a PSO stuck behind a poisoned driver or a removed device is its victim: retried too; otherwise it's the PSO's own hang
                bool fatal = so ? g_rt_fatal == j : !g_rt_off && !removed();
                logf("warm: item %zu (%s) stuck for %.0f s: its worker is abandoned, the item %s", j, so ? "a state object" : "a PSO", (now - since) / 1000,
                     fatal ? "counts as failed" : "is left for a new process");
                if (fatal) note_fail(j, so ? "state object create stuck alone (abandoned)" : "PSO create stuck (abandoned)"), g_warm_fail++;
                g_item_state[j] = fatal ? 2 : g_removed ? 4 : 3;
                g_abandoned12 = true;
                --g_active;     // its slot: the stuck thread never gives it back
                w->t.detach();  // w is leaked on purpose: the thread may still return into it
                ws.erase(ws.begin() + i);
                if (g_rt_off || g_removed) {}  // poisoned: no new workers into the driver; the others finish, the retry does the rest
                else if (g_state != STOP && next < n12 && ++replaced <= 8) spawn();
                else if (replaced > 8) g_warm_faults = 3;  // the driver keeps hanging: give up, the run ends with an error
            } else ++i;
        }
    };
    std::atomic<bool> d12done{false};
    std::thread t11([&] {  // D3D11 items after the PSOs: done stays "every item below it was compiled"
        while (!d12done) Sleep(20);
        if (g_rt_off || g_removed) {  // the retry (or a stopped run's resume) takes over from the first item not done (D3D11 items after it)
            size_t from = first;
            while (from < n12 && (g_item_state[from] == 1 || g_item_state[from] == 2)) ++from;
            if (from < n12) {
                g_retry_failed = std::count(g_item_state.begin() + first, g_item_state.begin() + from, 2);
                g_retry_from = from;
                if (blamed_alone != SIZE_MAX) {
                    g_blamed = {blamed_alone};
                    for (size_t i = alone_at + 1; i < g_alone.size(); ++i)
                        if (g_alone[i] >= from && g_alone[i] < n12) g_alone_next.push_back(g_alone[i]);
                } else if (g_removed) {
                    for (size_t j = from; j < n12; ++j)
                        if (g_item_state[j] == 4) g_alone_next.push_back(j);
                    if (g_alone_next.size() == 1) g_blamed.swap(g_alone_next);
                }
                logf("warm: retry from item %zu%s: %llu of %zu D3D12 items left; %zu blamed, %zu to create alone first", from,
                     g_removed ? " after the device was removed" : (" with " + std::to_string(std::max(1, (g_rt_threads ? g_rt_threads : g_threads) / 4)) + " ray tracing thread(s)").c_str(),
                     (unsigned long long)(n12 - from - std::count(g_item_state.begin() + from, g_item_state.end(), 1) - std::count(g_item_state.begin() + from, g_item_state.end(), 2)),
                     n12 - first, g_blamed.size(), g_alone_next.size());
                for (size_t j : g_blamed) crash_found.insert(key(j));
                return;
            }
        }
        if (n > n12 && g_state != STOP && g_warm_faults < 3) warm11_main(std::max(first, n12) - n12);
    });
    // progress every 5 s from the last 5 s's rate; a stall (nothing done for 30 s) says what is in flight, and no eta
    double last_done = double(first), last_t = t0, moved_t = t0, moved_done = double(first), peak_mb = 0, mem_changed = t0, mem_released = t0;
    for (int tick = 1;; ++tick) {
        Sleep(100);
        if (!d12done) {
            removed();
            supervise();
            if (ws.empty()) d12done = true;
        }
        if (!d12done) {  // the memory budget, every tick
            double mb = private_mb(), now = now_ms();
            peak_mb = std::max(peak_mb, mb);
            bool parked;
            {
                std::lock_guard l(g_parkmx);
                parked = !g_parked.empty();
            }
            // on its own thread: the gate waits for every create, and a hung one must not stop this loop's timeouts
            static std::atomic<bool> releasing;
            if (g_mem_mb && mb > g_mem_mb && parked && now - mem_released > 1000 && !releasing.exchange(true))
                mem_released = now, std::thread([] { release_parked("over the memory budget"), releasing = false; }).detach();
            if (g_mem_mb && mb > g_mem_mb && g_allowed > 1 && now - mem_changed > 5000)
                --g_allowed, mem_changed = now, logf("warm: private memory %.0f MB over the %u MB budget: %d workers", mb, g_mem_mb, g_allowed.load());
            else if (g_mem_mb && mb < 0.8 * g_mem_mb && g_allowed < g_threads && now - mem_changed > 30000)
                ++g_allowed, mem_changed = now, logf("warm: private memory %.0f MB, under the %u MB budget again: %d workers", mb, g_mem_mb, g_allowed.load());
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
            if (g_item_state[j] == 2 || g_crash.count(j)) fwrite(key(j).data(), 1, 20, f);
        fclose(f);
    }
    for (auto& h : crash_found) g_crash_json += (g_crash_json.empty() ? "\"" : ",\"") + std::string(hex(h).data()) + "\"";
    for (size_t j : g_alone_next) g_alone_json += (g_alone_json.empty() ? "" : ",") + std::to_string(j);
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
        release_parked("end of the warm");
    }
    drain_debug("end of warm");
    if (g_roundtrip) logf("warm: round trip: %llu same, %llu differ", g_roundtrip_ok.load(), g_roundtrip_bad.load());
    logf("warm: peak private memory %.0f MB (budget %s), %d of %d workers allowed at the end", std::max(peak_mb, private_mb()),
         g_mem_mb ? (std::to_string(g_mem_mb) + " MB").c_str() : "none", g_allowed.load(), g_threads);
    logf("warm: done ok=%llu fail=%llu skipped=%llu%s in %.1f s. Quit the game normally so the driver flushes its cache.", g_warm_ok.load(), g_warm_fail.load(),
         g_warm_crash.load(), g_pass_of.empty() ? "" : (" (pass " + std::to_string(g_pass) + ", " + std::to_string(g_warm_other.load()) + " items of other passes)").c_str(),
         (now_ms() - t0) / 1000);
    SetEvent(g_warm_done);
}

static void** g_hooked_vt;                          // the device vtable our o_* originals were taken from
static std::unordered_map<void*, ID3D12Device*> g_lib_dev;  // pipeline library -> the (hooked) device that opened it

static ID3D12Device* unwrapped(IUnknown* unk);

static void on_first_pso(ID3D12Device* dev) {
    // Only warm on a device whose vtable we hooked: o_* belong to that class. A layer like ReShade wraps the device,
    // and e.g. ID3D12PipelineLibrary::GetDevice returns the unwrapped one; calling ReShade's methods on it crashes.
    if (!g_warm || !dev || *(void***)dev != g_hooked_vt) return;
    static std::once_flag once;
    std::call_once(once, [dev] {
        if (!g_warm) return;
        g_warm_dev = dev, dev->AddRef();  // the game's real device: the first one that builds a PSO
        dev->QueryInterface(IID_PPV_ARGS(&g_warm_dev2));
        if ((g_warm_real = unwrapped(dev))) {
            g_warm_real->QueryInterface(IID_PPV_ARGS(&g_warm_real2));
            logf("warm: under a layer; %zu pipelines it made are created on the device under it", g_layer_made.size());
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

template <class F> static HRESULT timed_create(ID3D12Device* dev, void** pp, Writer& w, F&& call, bool lib = false) {
    if (t_in || !pp) return call();
    t_in = true;
    on_first_pso(dev);
    t_below_ms = 0;
    t_asked = w.ok && !w.s.empty() ? key_of(w.tag, w.s) : kZero;
    auto t0 = std::chrono::steady_clock::now();
    HRESULT hr = call();
    t_asked = kZero;
    // a chained mod's create reaches hk_*_below, whose recording is ours, not the driver's: proxy_ms, not ms
    double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count() - t_below_ms;
    if (SUCCEEDED(hr)) note(w, ms, lib, std::chrono::duration<double, std::milli>(t0 - w.t0).count() + t_below_ms);  // a failed library load is a miss: the game creates it next
    t_in = false;
    return hr;
}

static HRESULT STDMETHODCALLTYPE hk_gfx(ID3D12Device* dev, const D3D12_GRAPHICS_PIPELINE_STATE_DESC* d, REFIID riid, void** pp) {
    Writer w{'G'};
    if (!t_in && pp) io_gfx(w, const_cast<D3D12_GRAPHICS_PIPELINE_STATE_DESC&>(*d));
    return timed_create(dev, pp, w, [&] { return o_gfx(dev, d, riid, pp); });
}
static HRESULT STDMETHODCALLTYPE hk_cs(ID3D12Device* dev, const D3D12_COMPUTE_PIPELINE_STATE_DESC* d, REFIID riid, void** pp) {
    Writer w{'C'};
    if (!t_in && pp) io_cs(w, const_cast<D3D12_COMPUTE_PIPELINE_STATE_DESC&>(*d));
    return timed_create(dev, pp, w, [&] { return o_cs(dev, d, riid, pp); });
}
static HRESULT STDMETHODCALLTYPE hk_stream(ID3D12Device2* dev, const D3D12_PIPELINE_STATE_STREAM_DESC* d, REFIID riid, void** pp) {
    Writer w{'S'};
    if (!t_in && pp) write_stream(w, *d);
    return timed_create(dev, pp, w, [&] { return o_stream(dev, d, riid, pp); });
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

static void install_hooks(IUnknown* unk) {
    ID3D12Device* dev;
    if (FAILED(unk->QueryInterface(IID_PPV_ARGS(&dev)))) return;
    static std::once_flag once;
    std::call_once(once, [] {
        load_db(g_warm);
        g_csv = _wfopen((g_dir + L"scskiller_creates.csv").c_str(), L"a");
        if (g_csv && !g_staged) {  // a staged scskiller_warm run is a warm-up, not a play session: no marker
            g_session_unix = unix_ms();
            double t = now_ms();  // #clock: the stamp on the t_ms clock, which starts when the recorder loads
            fprintf(g_csv, "#session,%lld,%ls\n#clock,%.1f\n", g_session_unix, exe_name().c_str(), t);
            fflush(g_csv);
            g_wrote_session = true;
            g_csv_h = (HANDLE)_get_osfhandle(_fileno(g_csv));
        }
    });
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
    dev->Release();
}

// NVAPI hooks: a game gets NVAPI's functions from nvapi64.dll's nvapi_QueryInterface, so each one is patched at its entry
// (a jmp to a relay near it, the moved instructions in a trampoline). Only the prologue forms nvapi64.dll's functions use are
// moved (push, and mov / lea / sub / xor / add / test with a ModRM, no rip-relative operand); a function starting otherwise
// is left alone and logged. The 5-byte jmp goes in with one aligned 8-byte store, so no thread runs a torn entry.
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

static void* hook_fn(void* target, void* hook, void** orig) {
    std::lock_guard l(g_hook_mx);
    static uint8_t *page, *end;
    auto t = (uint8_t*)target;
    size_t n = 0;
    for (size_t l = 1; n < 5 && l; n += l) l = insn_len(t + n);
    if (n < 5 || ((uintptr_t)t & 7)) return nullptr;
    auto near_t = [&](const uint8_t* q) { long long d = q - (t + 5); return d > -0x7FF00000ll && d < 0x7FF00000ll; };
    if (!page || end - page < 64 || !near_t(page)) {  // relays and trampolines within a rel32 jmp of the target (another module's may be far)
        SYSTEM_INFO si;
        GetSystemInfo(&si);
        uintptr_t g = si.dwAllocationGranularity, base = (uintptr_t)t & ~(g - 1);
        page = nullptr;
        for (uintptr_t d = g; !page && d < 0x7FF00000; d += g)
            for (uintptr_t a : {base - d, base + d})
                if (!page && (page = (uint8_t*)VirtualAlloc((void*)a, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE))) end = page + 4096;
        if (!page) return nullptr;
    }
    auto abs_jmp = [](uint8_t* at, const void* to) { at[0] = 0xFF, at[1] = 0x25, memset(at + 2, 0, 4), memcpy(at + 6, &to, 8); };
    uint8_t *relay = page, *tramp = page + 14;
    long long rel = relay - (t + 5);
    if (rel != (int32_t)rel) return nullptr;
    abs_jmp(relay, hook);
    memcpy(tramp, t, n), abs_jmp(tramp + n, t + n);
    page += (14 + n + 14 + 15) & ~(size_t)15;
    FlushInstructionCache(GetCurrentProcess(), relay, tramp + n + 14 - relay);
    uint64_t v;
    memcpy(&v, t, 8);
    auto b = (uint8_t*)&v;
    int32_t r32 = (int32_t)rel;
    b[0] = 0xE9, memcpy(b + 1, &r32, 4);
    DWORD old;
    if (!VirtualProtect(t, 8, PAGE_EXECUTE_READWRITE, &old)) return nullptr;
    *orig = tramp;  // before the jmp: a call through it may run at once
    InterlockedExchange64((volatile LONG64*)t, (LONG64)v);
    VirtualProtect(t, 8, old, &old);
    FlushInstructionCache(GetCurrentProcess(), t, 8);
    return tramp;
}

// NVIDIA Aftermath: with GPU crash dumps on, DX12_Initialize's GenerateShaderDebugInfo (0x8) makes the driver compile
// every pipeline under another cache key (selftest-measured), so a warm without it never reaches the game. Logged once each.
static int (*o_am_init)(int, uint32_t, void*);
static int (*o_am_dumps)(int, uint32_t, uint32_t, void*, void*, void*, void*, void*);
static int hk_am_init(int version, uint32_t flags, void* dev) {
    int r = o_am_init(version, flags, dev);
    logf("aftermath: DX12_Initialize(version 0x%x, feature flags 0x%x) = 0x%x%s", version, flags, r, flags & 8 ? ": shader debug info (another driver cache key while crash dumps are on)" : "");
    return r;
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
    auto at = [&](const char* name, void* hook, void** orig) {
        auto p = (uint8_t*)GetProcAddress(m, name);
        for (int i = 0; p && p[0] == 0xE9 && i < 4; ++i) { int32_t r; memcpy(&r, p + 1, 4); p += 5 + r; }  // an export that is a jmp thunk
        return p && hook_fn(p, hook, orig) ? "hooked" : "not hooked";
    };
    const char* a = at("GFSDK_Aftermath_EnableGpuCrashDumps", (void*)hk_am_dumps, (void**)&o_am_dumps);
    const char* b = at("GFSDK_Aftermath_DX12_Initialize", (void*)hk_am_init, (void**)&o_am_init);
    logf("aftermath: EnableGpuCrashDumps %s, DX12_Initialize %s", a, b);
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
    if (flags) t_nv.opts = p[1];
    static std::mutex mx;
    static std::set<std::tuple<uint32_t, uint32_t, int>> seen;
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
    static std::mutex mx;
    static std::unordered_set<uint32_t> seen;
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
                put('W', c.data(), c.size()), db_flush();
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
using PFN_Present = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
using PFN_Present1 = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);
using PFN_CreateSc = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory*, IUnknown*, DXGI_SWAP_CHAIN_DESC*, IDXGISwapChain**);
using PFN_CreateScHwnd = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory2*, IUnknown*, HWND, const DXGI_SWAP_CHAIN_DESC1*,
                                                     const DXGI_SWAP_CHAIN_FULLSCREEN_DESC*, IDXGIOutput*, IDXGISwapChain1**);
using PFN_CreateScCw = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory2*, IUnknown*, IUnknown*, const DXGI_SWAP_CHAIN_DESC1*, IDXGIOutput*, IDXGISwapChain1**);
using PFN_CreateScComp = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory2*, IUnknown*, const DXGI_SWAP_CHAIN_DESC1*, IDXGIOutput*, IDXGISwapChain1**);
// vtslots.cpp checks these against dxgi1_2.h at compile time
enum { SLOT_PRESENT = 8, SLOT_PRESENT1 = 22, SLOT_CREATESC = 10, SLOT_CREATESC_HWND = 15, SLOT_CREATESC_CW = 16, SLOT_CREATESC_COMP = 24 };
static PFN_CreateSc o_createsc;
static PFN_CreateScHwnd o_createsc_hwnd;
static PFN_CreateScCw o_createsc_cw;
static PFN_CreateScComp o_createsc_comp;

// A wrapper's swap chain and the real one have different vtables and originals: one entry per vtable.
struct ScVt { std::atomic<void**> vt; std::atomic<void*> present, present1; };
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

struct FrameAt { int64_t qpc; void* sc; };
static std::mutex g_fmx;
static std::vector<FrameAt> g_fq;  // under g_fmx: presents not yet written
static std::atomic<int64_t> g_hook_ticks, g_hook_max;  // the hooks' own QPC ticks, for the log
static std::atomic<uint64_t> g_presents;
static thread_local int t_present;  // nesting depth: only the outermost present of a thread is a frame

static std::atomic<bool> g_frames_off;  // frame generation made its swap chain (frames_fg)

template <class F> static HRESULT timed_present(void* sc, UINT flags, F&& call) {
    if (g_frames_off) return call();
    LARGE_INTEGER a, b, c, t;
    QueryPerformanceCounter(&a);
    if (t_present++ || (flags & DXGI_PRESENT_TEST)) {
        HRESULT hr = call();
        --t_present;
        return hr;
    }
    QueryPerformanceCounter(&b);
    HRESULT hr = call();
    QueryPerformanceCounter(&c);
    --t_present;
    t_presenting = true;
    {
        // a failed present (DXGI_ERROR_WAS_STILL_DRAWING from a DO_NOT_WAIT retry) shows no frame; the frame's time is
        // taken under the lock so two threads' frames queue in the order they returned
        std::lock_guard l(g_fmx);
        QueryPerformanceCounter(&t);
        if (SUCCEEDED(hr)) g_fq.push_back({t.QuadPart, sc});
    }
    LARGE_INTEGER d;
    QueryPerformanceCounter(&d);
    int64_t own = (b.QuadPart - a.QuadPart) + (d.QuadPart - c.QuadPart);
    g_hook_ticks += own, ++g_presents;
    for (int64_t m = g_hook_max; own > m && !g_hook_max.compare_exchange_weak(m, own);) {}
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_present(IDXGISwapChain* sc, UINT sync, UINT flags) {
    auto o = (PFN_Present)sc_orig(sc, &ScVt::present);
    return timed_present(sc, flags, [&] { return o(sc, sync, flags); });
}
static HRESULT STDMETHODCALLTYPE hk_present1(IDXGISwapChain1* sc, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* p) {
    auto o = (PFN_Present1)sc_orig(sc, &ScVt::present1);
    return timed_present(sc, flags, [&] { return o(sc, sync, flags, p); });
}

static void sc_patch(void** vt, int slot, void* hook, std::atomic<void*> ScVt::*orig) {
    if (vt[slot] == hook) return;
    int n = g_nscvt, i = 0;
    while (i < n && g_scvt[i].vt != vt) ++i;
    // an overlay hooked the slot after us and calls our hook: taking it as the original would make Present call itself
    if (i < n && g_scvt[i].*orig && g_scvt[i].*orig != vt[slot]) return;
    if (i == std::size(g_scvt)) return logf("frames: a %zuth swap chain vtable, not hooked", std::size(g_scvt) + 1);
    g_scvt[i].*orig = vt[slot];  // before the slot: a present may run as soon as it's patched
    g_scvt[i].vt = vt;
    if (i == n) g_nscvt.store(n + 1, std::memory_order_release);
    DWORD old;
    VirtualProtect(&vt[slot], sizeof(void*), PAGE_READWRITE, &old);
    vt[slot] = hook;
    VirtualProtect(&vt[slot], sizeof(void*), old, &old);
    logf("frames: swap chain vtable %p slot %d hooked", (void*)vt, slot);
}
// With frame generation's swap chain, Steam's overlay and our Present hook call each other until the stack overflows (FSR 3
// frame generation, the overlay on): no Present hook once frame generation makes its swap chain. FSR 3 (also under
// OptiScaler and dlssg-to-fsr3) and XeSS frame generation make it on a present queue they name before the create.
static bool fg_queue(IUnknown* dev, std::wstring& name) {
    ID3D12CommandQueue* q;
    if (!dev || FAILED(dev->QueryInterface(IID_PPV_ARGS(&q)))) return false;
    wchar_t n[128] = {};
    UINT size = sizeof n - sizeof(wchar_t);
    if (FAILED(q->GetPrivateData(WKPDID_D3DDebugObjectNameW, &size, n))) size = 0;
    q->Release();
    name.assign(n, size / sizeof(wchar_t));
    for (auto p : {L"AMD FSR PresentQueue", L"XefgInterpolationSwapChain::present_queue_"})
        if (!name.compare(0, wcslen(p), p)) return true;
    return false;
}
// OptiScaler.ini beside the exe with its frame generation on: it may make its swap chain through a factory the recorder
// doesn't hook (OptiScaler as dxgi.dll). [FrameGen] Enabled (default false) with an FGOutput (default nofg); in older
// versions FGType (default optifg) with [OptiFG] Enabled (default false), or nukems (on with the game's DLSS-G).
static bool opti_fg() {
    const std::wstring ini = g_dir + L"OptiScaler.ini";
    auto is = [&](const wchar_t* section, const wchar_t* key, const wchar_t* value) {
        wchar_t v[32];
        GetPrivateProfileStringW(section, key, L"auto", v, 32, ini.c_str());
        return !_wcsicmp(v, value);
    };
    wchar_t out[32];
    if (GetPrivateProfileStringW(L"FrameGen", L"FGOutput", L"", out, 32, ini.c_str()))
        return is(L"FrameGen", L"Enabled", L"true") && _wcsicmp(out, L"auto") && _wcsicmp(out, L"nofg");
    if (is(L"FrameGen", L"FGType", L"nofg")) return false;
    return is(L"FrameGen", L"FGType", L"nukems") || is(L"OptiFG", L"Enabled", L"true");
}
// The originals go back before frame generation's swap chain is made, so no hook installed on it can take ours as its
// original; g_frames_off is set only after, under g_mx, which hook_swapchain checks under it. A slot another hook took
// after ours (even meanwhile: compare-exchange) stays as it is.
static void frames_fg(IUnknown* dev) {
    if (g_frames_off) return;
    std::wstring name;
    bool fg = fg_queue(dev, name);
    logf("frames: a swap chain on queue \"%ls\"%s", name.c_str(), fg ? ": frame generation's" : "");
    if (!fg) return;
    std::lock_guard l(g_mx);
    if (g_frames_off) return;
    logf("frames: off (frame generation swap chain)");
    for (int i = 0, n = g_nscvt; i < n; ++i)
        for (auto [slot, hook, orig] : {std::tuple{SLOT_PRESENT, (void*)hk_present, &ScVt::present}, std::tuple{SLOT_PRESENT1, (void*)hk_present1, &ScVt::present1}}) {
            void** vt = g_scvt[i].vt;
            void* o = g_scvt[i].*orig;
            if (!o) continue;
            DWORD old;
            VirtualProtect(&vt[slot], sizeof(void*), PAGE_READWRITE, &old);
            void* was = InterlockedCompareExchangePointer(&vt[slot], o, hook);
            VirtualProtect(&vt[slot], sizeof(void*), old, &old);
            if (was != hook) logf("frames: swap chain vtable %p slot %d is another hook's, left as it is", (void*)vt, slot);
        }
    g_frames_off = true;
}
static void hook_swapchain(HRESULT hr, void* p) {
    if (FAILED(hr) || !p) return;
    std::lock_guard l(g_mx);
    if (g_frames_off) return;
    IDXGISwapChain* sc;
    IDXGISwapChain1* sc1;
    if (SUCCEEDED(((IUnknown*)p)->QueryInterface(IID_PPV_ARGS(&sc)))) sc_patch(*(void***)sc, SLOT_PRESENT, (void*)hk_present, &ScVt::present), sc->Release();
    if (SUCCEEDED(((IUnknown*)p)->QueryInterface(IID_PPV_ARGS(&sc1)))) sc_patch(*(void***)sc1, SLOT_PRESENT1, (void*)hk_present1, &ScVt::present1), sc1->Release();
}
static HRESULT STDMETHODCALLTYPE hk_createsc(IDXGIFactory* f, IUnknown* dev, DXGI_SWAP_CHAIN_DESC* d, IDXGISwapChain** pp) {
    frames_fg(dev);
    HRESULT hr = o_createsc(f, dev, d, pp);
    hook_swapchain(hr, pp ? *pp : nullptr);
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_createsc_hwnd(IDXGIFactory2* f, IUnknown* dev, HWND w, const DXGI_SWAP_CHAIN_DESC1* d,
                                                  const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fs, IDXGIOutput* o, IDXGISwapChain1** pp) {
    frames_fg(dev);
    HRESULT hr = o_createsc_hwnd(f, dev, w, d, fs, o, pp);
    hook_swapchain(hr, pp ? *pp : nullptr);
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_createsc_cw(IDXGIFactory2* f, IUnknown* dev, IUnknown* w, const DXGI_SWAP_CHAIN_DESC1* d, IDXGIOutput* o, IDXGISwapChain1** pp) {
    frames_fg(dev);
    HRESULT hr = o_createsc_cw(f, dev, w, d, o, pp);
    hook_swapchain(hr, pp ? *pp : nullptr);
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_createsc_comp(IDXGIFactory2* f, IUnknown* dev, const DXGI_SWAP_CHAIN_DESC1* d, IDXGIOutput* o, IDXGISwapChain1** pp) {
    frames_fg(dev);
    HRESULT hr = o_createsc_comp(f, dev, d, o, pp);
    hook_swapchain(hr, pp ? *pp : nullptr);
    return hr;
}

static HANDLE g_frames = INVALID_HANDLE_VALUE;
static const uint64_t kFramesCap = 32ull << 20;  // 8M frames: over 7 hours at 300 FPS
struct FrameEnc {
    int64_t qpc0 = 0, freq = 1;
    uint64_t last_us = 0, bytes = 0;
    std::vector<void*> scs;
    std::vector<uint32_t> out;
    void put(const FrameAt& f) {
        int64_t t = f.qpc - qpc0;
        uint64_t us = t <= 0 ? 0 : (uint64_t)(t / freq * 1000000 + t % freq * 1000000 / freq);
        uint64_t d = us > last_us ? us - last_us : 0;
        last_us += d;
        while (d >> 28) {
            uint64_t ms = std::min<uint64_t>(d / 1000, 0x0FFFFFFE);
            out.push_back(0xF0000000u | (uint32_t)ms);
            d -= ms * 1000;
        }
        size_t i = std::find(scs.begin(), scs.end(), f.sc) - scs.begin();
        if (i == scs.size() && i < 14) scs.push_back(f.sc);
        out.push_back((uint32_t)std::min<size_t>(i, 14) << 28 | (uint32_t)d);
    }
};
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
    for (int tick = 1;; ++tick) {
        Sleep(1000);
        {
            std::lock_guard l(g_fmx);
            q.swap(g_fq);
        }
        frames_flush(q);
        if (tick % 300 == 60 && g_presents) {  // after a minute, then every 5
            uint64_t n = g_presents;
            logf("frames: %llu presents, hook %.0f ns each on average, %.0f ns at most", n, g_hook_ticks * 1e9 / g_fenc.freq / n, g_hook_max * 1e9 / g_fenc.freq);
        }
    }
}

static void frame_hooks() {
    if (g_warm) return;
    wchar_t on[8];  // not cfg(): a staged warm child may inherit SCSKILLER_* variables
    GetPrivateProfileStringW(L"scskiller", L"frames", L"1", on, 8, (g_dir + L"scskiller.ini").c_str());
    if (!wcscmp(on, L"0")) return logf("frames: off (scskiller.ini frames=0)");
    if (opti_fg()) return logf("frames: off (OptiScaler.ini has frame generation on)");
    // the dxgi.dll the game uses: a mod's in the game folder, if one is loaded by that name
    HMODULE m = GetModuleHandleW(L"dxgi.dll");
    if (!m) m = LoadLibraryW(L"dxgi.dll");
    auto create = m ? (decltype(&CreateDXGIFactory1))GetProcAddress(m, "CreateDXGIFactory1") : nullptr;
    IDXGIFactory* f = nullptr;
    if (!create || FAILED(create(IID_PPV_ARGS(&f)))) return logf("frames: no DXGI factory, frame times not measured");
    LARGE_INTEGER q, fq;
    QueryPerformanceFrequency(&fq);
    QueryPerformanceCounter(&q);
    uint64_t head[4] = {(uint64_t)(g_session_unix ? g_session_unix : unix_ms()), (uint64_t)(now_ms() * 1000), (uint64_t)q.QuadPart, (uint64_t)fq.QuadPart};
    memcpy(g_fhead, head, sizeof head);
    g_fenc.qpc0 = q.QuadPart, g_fenc.freq = fq.QuadPart;
    {
        std::lock_guard l(g_mx);
        void** vt = *(void***)f;
        patch(vt, SLOT_CREATESC, (void*)hk_createsc, o_createsc);
        IDXGIFactory2* f2;
        if (SUCCEEDED(f->QueryInterface(IID_PPV_ARGS(&f2)))) {
            void** vt2 = *(void***)f2;
            patch(vt2, SLOT_CREATESC_HWND, (void*)hk_createsc_hwnd, o_createsc_hwnd);
            patch(vt2, SLOT_CREATESC_CW, (void*)hk_createsc_cw, o_createsc_cw);
            patch(vt2, SLOT_CREATESC_COMP, (void*)hk_createsc_comp, o_createsc_comp);
            f2->Release();
        }
        wchar_t path[MAX_PATH];
        GetModuleFileNameW(m, path, MAX_PATH);
        logf("frames: DXGI factory vtable %p hooked (%ls)", (void*)vt, path);
    }
    f->Release();
    std::thread(frame_writer).detach();
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
    HMODULE m = nullptr;
    wchar_t path[MAX_PATH] = L"";
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)*(void***)dev, &m);
    if (m) GetModuleFileNameW(m, path, MAX_PATH);
    const wchar_t* name = wcsrchr(path, L'\\') ? wcsrchr(path, L'\\') + 1 : path;
    // the runtime's own device (the debug layer's included): no layer to look under, nothing created to find one
    if (!_wcsicmp(name, L"D3D12Core.dll") || !_wcsicmp(name, L"d3d12SDKLayers.dll") || (m && m == g_real)) return dev->Release(), nullptr;
    static const GUID kUnwrapped = {0x7f2c9a11, 0x3b4e, 0x4d6a, {0x81, 0x2f, 0x5e, 0x9c, 0xd3, 0x7a, 0x1b, 0x42}};  // ReShade 6.8+: IID_UnwrappedObject
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
}

static HRESULT device_created(HRESULT hr, IUnknown* adapter, D3D_FEATURE_LEVEL fl, void** pp) {
    if (FAILED(hr) || !pp || !*pp || !admitted()) return hr;
    pin_self();
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
    std::call_once(nv, nv_hooks), std::call_once(frames, frame_hooks);
    return hr;
}

extern "C" HRESULT WINAPI Proxy_D3D12CreateDevice(IUnknown* adapter, D3D_FEATURE_LEVEL fl, REFIID riid, void** pp) {
    static std::once_flag once;
    std::call_once(once, [] {
        if (!(g_debug12 = GetEnvironmentVariableW(L"SCSKILLER_D3D12_DEBUG", nullptr, 0) > 0)) return;
        ID3D12Debug* dbg = nullptr;
        HRESULT dhr = ((decltype(&D3D12GetDebugInterface))real_D3D12GetDebugInterface)(IID_PPV_ARGS(&dbg));
        if (SUCCEEDED(dhr)) dbg->EnableDebugLayer(), dbg->Release();
        logf("d3d12 debug: %s (hr=0x%08x)", SUCCEEDED(dhr) ? "debug layer enabled" : "debug layer unavailable: install the Graphics Tools optional feature", (unsigned)dhr);
    });
    return device_created(real_CreateDevice(adapter, fl, riid, pp), adapter, fl, pp);
}

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
static HRESULT STDMETHODCALLTYPE hk_factory_createdevice(ID3D12DeviceFactory* f, IUnknown* adapter, D3D_FEATURE_LEVEL fl, REFIID riid, void** pp) {
    void** vt = *(void***)f;
    PFN_FactoryCreateDevice o = nullptr;
    for (int i = 0, n = g_nfacvt.load(std::memory_order_acquire); i < n && !o; ++i)
        if (g_facvt[i].vt == vt) o = g_facvt[i].orig;
    if (!o) return E_FAIL;  // unreachable: only a vtable in the table points here
    return device_created(o(f, adapter, fl, riid, pp), adapter, fl, pp);
}
static void hook_factory(IUnknown* unk) {
    ID3D12DeviceFactory* f;
    if (!unk || g_admission < 0 || FAILED(unk->QueryInterface(IID_PPV_ARGS(&f)))) return;  // a pass-through installs nothing more
    pin_self();
    {
        std::lock_guard l(g_mx);
        void** vt = *(void***)f;
        int n = g_nfacvt;
        if (vt[SLOT_FACTORY_CREATEDEVICE] != (void*)hk_factory_createdevice) {
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
extern "C" HRESULT WINAPI Proxy_D3D12GetInterface(REFCLSID clsid, REFIID riid, void** pp) {
    if (!real_D3D12GetInterface) return E_NOINTERFACE;  // a runtime from before it
    HRESULT hr = ((decltype(&D3D12GetInterface))real_D3D12GetInterface)(clsid, riid, pp);
    if (FAILED(hr) || !pp || !*pp || g_admission < 0) return hr;  // undecided: the factory hooks forward to device_created
    if (clsid == CLSID_D3D12DeviceFactory) hook_factory((IUnknown*)*pp);
    ID3D12SDKConfiguration1* c;
    if (clsid == CLSID_D3D12SDKConfiguration && SUCCEEDED(((IUnknown*)*pp)->QueryInterface(IID_PPV_ARGS(&c)))) {
        pin_self();
        {
            std::lock_guard l(g_mx);
            patch(*(void***)c, SLOT_CREATEDEVICEFACTORY, (void*)hk_createdevicefactory, o_createdevicefactory);
        }
        c->Release();
    }
    return hr;
}

// For scskiller_warm: start the replay on a device created through this dll (no PSO create needed to trigger it).
extern "C" void WINAPI SCSKiller_StartWarm(IUnknown* unk) {
    ID3D12Device* dev;
    if (SUCCEEDED(unk->QueryInterface(IID_PPV_ARGS(&dev)))) on_first_pso(dev), dev->Release();
}

// For scskiller_warm, before SCSKiller_StartWarm: threads (0 = default), idle priority, first item to replay.
// Only scskiller_warm's staged child calls this, so it also marks the run as a warm-up, not a play session.
extern "C" void WINAPI SCSKiller_WarmOptions(int threads, BOOL idle, uint64_t start) {
    g_staged = true;
    if (threads > 0) g_threads = threads;
    g_prio = idle ? THREAD_PRIORITY_IDLE : THREAD_PRIORITY_BELOW_NORMAL;
    g_start = start;
}

// For scskiller_warm, before SCSKiller_StartWarm (a retry): concurrent state object creates (0 = as many as the workers)
// and the items an earlier run found faulting or hanging alone (counted failed, never replayed).
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

// For scskiller_warm, after the warm finished: whether the device was removed; the keys of this run's items that remove it
// (skipped, or blamed now) as JSON strings joined by commas; the items the retry creates alone first, joined by commas.
extern "C" BOOL WINAPI SCSKiller_Crashes(const char** keys, const char** alone) {
    *keys = g_crash_json.c_str(), *alone = g_alone_json.c_str();
    return g_removed;
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
    int rt = g_removed ? g_rt_threads : std::max(1, (g_rt_threads ? g_rt_threads : g_threads) / 4);
    uint64_t v[5] = {g_retry_from != SIZE_MAX, g_retry_from, (uint64_t)rt, g_retry_failed, g_rt_fatal == SIZE_MAX ? ~0ull : (uint64_t)g_rt_fatal};
    memcpy(out, v, sizeof v);
}

// For scskiller_warm, never blocks: {done (items before the start offset count as done), total, failed, warm finished}.
extern "C" void WINAPI SCSKiller_Progress(uint64_t out[4]) {
    uint64_t v[4] = {std::min(g_start, g_total) + warm_done(), g_total, g_warm_fail, WaitForSingleObject(g_warm_done, 0) == WAIT_OBJECT_0};
    memcpy(out, v, sizeof v);
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
    OVERLAPPED at{};
    at.Offset = at.OffsetHigh = 0xFFFFFFFF;  // append at the file's end
    DWORD n;
    WriteFile(g_csv_h, b, DWORD(e + 2 - b), &n, &at);
}

// The folder of a module's path as the loader reports it, with the trailing backslash.
static std::wstring module_dir(HMODULE m) {
    wchar_t p[MAX_PATH];
    std::wstring s(p, GetModuleFileNameW(m, p, MAX_PATH));
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
// copy under <folder>\_storage_ (where no scskiller.armed is). The kernel's name of the mapped image can't be, so the exe's
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
        if (reserved && g_wrote_session) write_end_marker();
        return TRUE;
    }
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(self);
    g_dir = own_dir(self);
    wchar_t p[MAX_PATH];
    GetSystemDirectoryW(p, MAX_PATH);
    g_real = LoadLibraryW((std::wstring(p) + L"\\d3d12.dll").c_str());
    if (!g_real) return FALSE;
    // next=<file name>: a mod's d3d12.dll renamed next to us (the app's "record alongside"); every export goes to it first.
    // A bare file name only. If it doesn't load or has no D3D12CreateDevice, the system dll serves alone (the game still runs).
    wchar_t next[64];
    GetPrivateProfileStringW(L"scskiller", L"next", L"", next, 64, (g_dir + L"scskiller.ini").c_str());
    HMODULE mod = nullptr;
    const char* next_why = nullptr;
    if (*next && (wcspbrk(next, L"\\/:") || wcsstr(next, L"..")))
        next_why = "not a file name next to this dll";
    else if (*next && !(mod = LoadLibraryW((g_dir + next).c_str())))
        next_why = "failed to load";
    else if (mod == self)  // its D3D12CreateDevice would call itself
        next_why = "is this dll", mod = nullptr;
    else if (mod && !GetProcAddress(mod, "D3D12CreateDevice"))
        next_why = "has no D3D12CreateDevice", mod = nullptr;
    auto res = [mod](const char* n) { void* f = mod ? (void*)GetProcAddress(mod, n) : nullptr; return f ? f : (void*)GetProcAddress(g_real, n); };
#define RES(n) real_##n = res(#n);
    REAL_EXPORTS(RES)
#undef RES
    real_Ordinal99 = (void*)GetProcAddress(g_real, MAKEINTRESOURCEA(99));  // ordinals are per dll: a mod's 99 is something else
    real_CreateDevice = (PFN_CreateDevice)res("D3D12CreateDevice");
    // Only scskiller_warm (and selftest) export SCSKiller_WarmHost; its staged child runs under the game's exe name, so the
    // name can't tell. A game given mode=warm records instead.
    const bool warm_asked = cfg(L"SCSKILLER_MODE", L"mode", L"record") == L"warm";
    g_warm = warm_asked && GetProcAddress(GetModuleHandleW(nullptr), "SCSKiller_WarmHost");
    g_threads = _wtoi(cfg(L"SCSKILLER_THREADS", L"threads", L"0").c_str());
    if (g_threads <= 0) g_threads = std::max(1, (int)std::thread::hardware_concurrency() - 2);
    if (!g_warm) SetEvent(g_warm_done);
    if (g_warm) g_log = _wfopen((g_dir + L"scskiller.log").c_str(), L"a");
    else g_log_deferred = true;  // opened by admitted(), at the first device
    GetModuleFileNameW(nullptr, p, MAX_PATH);
    logf("loaded into %ls", p);
    if (warm_asked && !g_warm) logf("mode warm ignored: this process isn't scskiller_warm, it records");
    g_next = mod;
    if (mod) logf("next: %ls (the device and every export it has come from it)", next);
    else if (next_why) logf("next: %ls %s: the system d3d12.dll is used without it", next, next_why);
    return TRUE;
}
