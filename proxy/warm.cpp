// scskiller_warm: fill a game's driver shader cache from outside the game, no injection.
// NVIDIA keys its D3D12 cache on the exe *file name* (measured: folder, file contents and D3D12 runtime version don't
// matter), so a copy of this exe named like the game, replaying the game's scskiller.db + scskiller_gen.db, warms it.
// Its D3D11 cache is keyed the same way: the gen db's D3D11 items are drawn once each on a D3D11 device (warm11.cpp).
//
//   scskiller_warm <workdir> <game exe file name> [--threads N] [--priority below|idle] [--start N]
//                  [--stop-event <name>] [--adapter-luid <hex>] [--rt-threads N] [--skip i,j,...]
//                  [--memory-mb N] [--memory-restart 1] [--package <app user model id>] [--stage-path <install folder>\<dir>\<exe>]
//                  [--skip-keys <sha1 hex>,...|@<file>] [--known-keys <sha1 hex>,...|@<file>] [--isolate i,j,...]
//                  [--ags <amd_ags_x64.dll> --ags-app <name> --ags-engine <name> [--ags-version <app hex>,<engine hex>]]
//                  [--pass K] [--layer <folder>]
//                  [--d3d12 <the game's Agility SDK folder>] [--d3d11 <the game's d3d11.dll>] [--vulkan]
//                  [--aftermath <the game's GFSDK_Aftermath_Lib.x64.dll> --aftermath-init <version>,<features>,<apis>,<dump flags>]
//                  [--out <file>] [--err <file>] [--stop-file <path>] [--alive-file <path> --alive-turns N] [--d3dscache-noflush 1]
//                  [--asd-name <name> --asd-engine <name> --asd-version <version>,<engine version> --asd-psdb <family>=<db>...]
//
// Protocol (JSON lines on stdout, exit codes): ARCHITECTURE.md. Stages <workdir>\stage-<pid>-<n>\<exe name> (a new folder
// of this run's) + d3d12.dll (the proxy) + the dbs and runs that copy (the child), which prints the JSON. The caller only
// knows this process, so it beats a shared counter every 100 ms: while it is suspended (pause) the child's workers wait,
// if it dies the child stops gracefully. --package: the child runs with that packaged app's identity (NVIDIA keys a
// profiled packaged game's cache on it, not on the exe name); such a child inherits no handles, so it finds everything by
// this process's id and writes through named pipes. --stage-path (ignored with --package): the child runs from <staging
// folder>\<that path>, since some AMD app profiles match the tail of the launched path, not just the name; afterwards the
// proxy's outputs are moved up to the staging folder and the rest of that tree is removed. --ags: the child creates its
// device through that AGS 6 DLL, registering the game's app and engine names and versions (unspecified without
// --ags-version), since AMD keys the cache of such a device on what was registered; if that fails it says why on stderr and creates a plain device. --pass K: <workdir>\scskiller_pass.bin
// holds each item's pass (one byte per item); only pass K's items are created, the others count as done. --d3d12: its
// Agility SDK DLLs are staged in D3D12\ next to the exe and the child asks the D3D12 loader for that runtime (the exports
// below), as the game does, so a recording made on it replays where the system's runtime is older (Windows 10's has no
// SM 6.6 and none of the newer pipeline subobjects). The loader takes the system's when that one is as new. A failed
// staging runs on the system's, and so does a child whose device the game's runtime didn't make (run again). --d3d11: the
// game's DXVK d3d11.dll, staged next to the exe, so the D3D11 items compile through it into the Vulkan cache the game
// reads (ARCHITECTURE.md "DXVK"). --aftermath (hex values, as the game passes them): the child starts NVIDIA Aftermath
// as the game does, crash dumps (apis 0: none) before its device and DX12_Initialize on it, since with crash dumps on its
// GenerateShaderDebugInfo (0x8) puts every pipeline under another driver cache key; if that fails it says why on stderr
// and compiles without it. --out, --err: stdout and stderr go to these files (created anew), for a caller that can't read
// this process's output (Proton's launcher drops it), and --out's last line is {"event":"exit","code":N}. --stop-file:
// once it exists the child stops as on the stop event. --alive-file: once its write time has stayed the same for
// --alive-turns of the 100 ms turns below (a caller that died stops touching it), the child stops. It is ended only if it
// hasn't printed its last line SCSKILLER_WARM_EXIT_S after that: a stopping replay abandons a call stuck for 2 s, so by
// then it hung. These five are this process's own, never passed to the child.
// --memory-restart 1: the child may end with a "memory" retry when the PC has stayed low on memory while it grew
// (proxy.cpp warm_restart_tick), so a new process starts without what the driver kept.
// --d3dscache-noflush 1: the child's d3dscache.dll (NVIDIA from 621 keeps compiled pipelines in its SQLite dbs) skips its
// FlushFileBuffers while it creates, since those fsyncs under its writer lock cap the compile; they flush again before the
// exit's checkpoint.
// --known-keys: items that crashed or hung this driver in an earlier compile, skipped like --skip and counted apart ("known"
// in the retry and done lines, of driverSkipped). Those lines' "culprits" are this run's own such items, by key.
// --asd-*: the game's Advanced Shader Delivery registration (its identity, and per adapter family its delivered db), made
// again for the staged exe in the user's hive while the child runs: the runtime then serves what the delivery holds and the
// driver compiles only the rest, as for the game. Under this process's own installer key SCSKiller-Warm-<pid>, removed when
// it returns; the app removes one whose process is gone. These four are this process's own too.
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <fcntl.h>
#include <io.h>
#include <algorithm>
#include <cstdarg>
#include <cstdio>
#include <filesystem>
#include <functional>
#include <string>
#include <thread>
#include <atomic>
#include <vector>
#include <bcrypt.h>
#include <memory>
#include <set>
#define VK_NO_PROTOTYPES
#include <vulkan/vulkan.h>
#include "fossilize_db.hpp"
#include "fossilize_external_replayer.hpp"
#include "warm_items.h"

#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "ole32.lib")

extern "C" __declspec(dllexport) const int SCSKiller_WarmHost = 1;  // the proxy warms only in a process exporting it
// Read by the system d3d12.dll when the proxy loads it. 0 (no --d3d12) is older than any system runtime: the system's.
extern "C" __declspec(dllexport) UINT D3D12SDKVersion = 0;
extern "C" __declspec(dllexport) const char* D3D12SDKPath = ".\\D3D12\\";

// What Invoke-CommandInDesktopPackage uses (Microsoft.Windows.Appx.PackageManager.Commands.dll).
struct __declspec(uuid("F158268A-D5A5-45CE-99CF-00D6C3F3FC0A")) IDesktopAppXActivator : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE Activate(LPCWSTR, LPCWSTR, LPCWSTR, HANDLE*) = 0;
    virtual HRESULT STDMETHODCALLTYPE ActivateWithOptions(LPCWSTR, LPCWSTR, LPCWSTR, DWORD, DWORD, HANDLE*) = 0;
    virtual HRESULT STDMETHODCALLTYPE ActivateWithOptionsAndArgs(LPCWSTR, LPCWSTR, LPCWSTR, DWORD, DWORD, IUnknown*, HANDLE*) = 0;
    virtual HRESULT STDMETHODCALLTYPE ActivateWithOptionsArgsWorkingDirectoryShowWindow(LPCWSTR aumid, LPCWSTR exe, LPCWSTR args,
        DWORD options, DWORD parent_pid, IUnknown*, LPCWSTR dir, DWORD show, HANDLE* process) = 0;
};
static const CLSID CLSID_DesktopAppXActivator = {0x168EB462, 0x775F, 0x42AE, {0x91, 0x11, 0xD7, 0x14, 0xB2, 0x30, 0x6C, 0x2E}};

static std::wstring beat_name(DWORD parent) { return L"Local\\SCSKiller.Beat." + std::to_wstring(parent); }
static std::wstring final_name(DWORD parent) { return L"Local\\SCSKiller.Final." + std::to_wstring(parent); }
static std::wstring started_name(DWORD parent) { return L"Local\\SCSKiller.Started." + std::to_wstring(parent); }
static std::wstring loading_name(DWORD parent) { return L"Local\\SCSKiller.Loading." + std::to_wstring(parent); }
static std::wstring stop_name(DWORD parent) { return L"Local\\SCSKiller.Stop." + std::to_wstring(parent); }
static std::wstring pipe_name(DWORD parent, int fd) { return L"\\\\.\\pipe\\SCSKiller.Warm." + std::to_wstring(parent) + L"." + std::to_wstring(fd); }

enum { RUN, PAUSE, STOP };  // SCSKiller_Control states (proxy.cpp)
// The child's exit code when the game's D3D12 runtime (--sdk) made no device; it printed nothing on stdout. The parent
// runs it again on the system's runtime.
constexpr int EXIT_SYSTEM_RUNTIME = 4;

struct Opts {
    int threads = 0;
    bool idle = false;
    uint64_t start = 0, luid = 0;
    std::wstring stop_event;
    int rt_threads = 0;          // a retry: concurrent ray tracing state object creates
    uint32_t memory_mb = 0;      // the staged process's private memory budget (0 = none)
    bool memory_restart = false;
    std::vector<uint64_t> skip;  // a retry: items an earlier run found faulting alone
    std::vector<uint8_t> skip_keys;  // 20 bytes each: items that removed the device in an earlier run
    std::vector<uint8_t> known_keys;  // 20 bytes each: items that crashed or hung this driver in an earlier compile
    std::vector<uint64_t> isolate;   // a retry: in flight when the device was removed, created alone first
    std::wstring package;        // app user model id whose package identity the child runs with
    std::wstring stage_path;
    std::wstring ags, ags_app, ags_engine;
    unsigned ags_app_version = 0xFFFFAD00, ags_engine_version = 0xFFFFAD00;  // AGS_UNSPECIFIED_VERSION
    std::wstring layer;          // a copy of the game's layer (ReShade's dll, its ini, add-ons), staged next to the exe
    std::wstring d3d12;          // the game's Agility SDK folder (its D3D12Core.dll)
    std::wstring d3d11;          // the game's d3d11.dll (DXVK)
    std::wstring aftermath;      // the game's GFSDK_Aftermath_Lib.x64.dll
    uint32_t am[4] = {};         // version, feature flags, crash dump apis, crash dump flags
    UINT sdk = 0;                // set by the parent for its child: the staged D3D12Core.dll's SDK version
    int pass = -1;
    bool ds_noflush = false;
    std::wstring stop_file, alive_file;
    int alive_turns = 0;
    std::wstring asd_name, asd_engine;
    uint64_t asd_version = 0, asd_engine_version = 0;
    std::vector<std::pair<std::wstring, std::wstring>> asd_psdbs;  // adapter family, delivered db
};

// a child given --out would create the file anew over the parent's lines
static bool host_only(const std::wstring& k) {
    return k == L"--out" || k == L"--err" || k == L"--stop-file" || k == L"--alive-file" || k == L"--alive-turns" ||
           k == L"--asd-name" || k == L"--asd-engine" || k == L"--asd-version" || k == L"--asd-psdb";
}

static bool parse(int argc, wchar_t** argv, int i, Opts& o) {
    for (; i + 1 < argc; i += 2) {
        std::wstring k = argv[i], v = argv[i + 1];
        wchar_t* end = nullptr;
        if (k == L"--threads") o.threads = (int)wcstol(v.c_str(), &end, 10);
        else if (k == L"--start") o.start = _wcstoui64(v.c_str(), &end, 10);
        else if (k == L"--adapter-luid") o.luid = _wcstoui64(v.c_str(), &end, 16);
        else if (k == L"--priority" && (v == L"idle" || v == L"below")) o.idle = v == L"idle";
        else if (k == L"--stop-event") o.stop_event = v;
        else if (k == L"--rt-threads") o.rt_threads = (int)wcstol(v.c_str(), &end, 10);
        else if (k == L"--memory-mb") o.memory_mb = (uint32_t)wcstoul(v.c_str(), &end, 10);
        else if (k == L"--memory-restart") o.memory_restart = v == L"1";
        else if (k == L"--package") o.package = v;
        else if (k == L"--stage-path") o.stage_path = v;
        else if (k == L"--ags") o.ags = v;
        else if (k == L"--ags-app") o.ags_app = v;
        else if (k == L"--ags-engine") o.ags_engine = v;
        else if (k == L"--ags-version") {
            uint64_t app = _wcstoui64(v.c_str(), &end, 16);
            if (end == v.c_str() || *end != L',' || app > 0xFFFFFFFF) return false;
            const wchar_t* c = end + 1;
            uint64_t engine = _wcstoui64(c, &end, 16);
            if (end == c || engine > 0xFFFFFFFF) return false;
            o.ags_app_version = (unsigned)app, o.ags_engine_version = (unsigned)engine;
        }
        else if (k == L"--pass") o.pass = (int)wcstol(v.c_str(), &end, 10);
        else if (k == L"--d3dscache-noflush") o.ds_noflush = v == L"1";
        else if (k == L"--layer") o.layer = v;
        else if (k == L"--d3d12") o.d3d12 = v;
        else if (k == L"--d3d11") o.d3d11 = v;
        else if (k == L"--sdk") o.sdk = wcstoul(v.c_str(), &end, 10);
        else if (k == L"--aftermath") o.aftermath = v;
        else if (k == L"--aftermath-init") {
            const wchar_t* c = v.c_str();
            for (int j = 0; j < 4; ++j, c = end + 1) {
                o.am[j] = wcstoul(c, &end, 16);
                if (end == c || *end != (j < 3 ? L',' : L'\0')) return false;
            }
            end = nullptr;
        }
        else if (k == L"--stop-file") o.stop_file = v;
        else if (k == L"--alive-file") o.alive_file = v;
        else if (k == L"--alive-turns") o.alive_turns = (int)wcstol(v.c_str(), &end, 10);
        else if (k == L"--asd-name") o.asd_name = v;
        else if (k == L"--asd-engine") o.asd_engine = v;
        else if (k == L"--asd-version") {
            o.asd_version = _wcstoui64(v.c_str(), &end, 10);
            if (end == v.c_str() || *end != L',') return false;
            const wchar_t* c = end + 1;
            o.asd_engine_version = _wcstoui64(c, &end, 10);
            if (end == c) return false;
        } else if (k == L"--asd-psdb") {
            size_t e = v.find(L'=');
            if (e == 0 || e == std::wstring::npos || e + 1 == v.size()) return false;
            o.asd_psdbs.emplace_back(v.substr(0, e), v.substr(e + 1));
        }
        else if (k == L"--out" || k == L"--err") continue;  // opened by wmain, before anything prints
        else if (k == L"--skip" || k == L"--isolate") {
            for (const wchar_t* c = v.c_str(); *c;) {
                (k == L"--skip" ? o.skip : o.isolate).push_back(_wcstoui64(c, &end, 10));
                if (end == c || (*end && *end != L',')) return false;
                c = *end ? end + 1 : end;
            }
            end = nullptr;
        } else if (k == L"--skip-keys" || k == L"--known-keys") {
            if (v.size() > 1 && v[0] == L'@') {  // @<file>: the same list, too long for a command line
                FILE* f = _wfopen(v.c_str() + 1, L"rb");
                if (!f) return false;
                std::string s;
                char buf[4096];
                for (size_t n; (n = fread(buf, 1, sizeof buf, f)) > 0;) s.append(buf, n);
                fclose(f);
                while (!s.empty() && (s.back() == '\r' || s.back() == '\n')) s.pop_back();
                v.assign(s.begin(), s.end());
            }
            for (size_t b = 0; b < v.size(); b += 41) {
                if (v.size() - b < 40 || (b + 40 < v.size() && v[b + 40] != L',')) return false;
                for (size_t c = b; c < b + 40; c += 2) {
                    if (!iswxdigit(v[c]) || !iswxdigit(v[c + 1])) return false;
                    auto nib = [](wchar_t x) { return x <= L'9' ? x - L'0' : (x | 0x20) - L'a' + 10; };
                    (k == L"--skip-keys" ? o.skip_keys : o.known_keys).push_back((uint8_t)(nib(v[c]) << 4 | nib(v[c + 1])));
                }
            }
        } else return false;
        if (end && (*end || v.empty())) return false;
    }
    return i == argc;
}

static std::string json(const std::wstring& w) {  // JSON string literal
    std::string u(WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr), '\0'), s = "\"";
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), u.data(), (int)u.size(), nullptr, nullptr);
    for (char c : u) {
        char esc[8];
        if (c == '"' || c == '\\') s += '\\', s += c;
        else if ((unsigned char)c < 0x20) sprintf_s(esc, "\\u%04x", c), s += esc;
        else s += c;
    }
    return s + '"';
}

static void emit(const char* fmt, ...) {
    va_list a;
    va_start(a, fmt);
    vprintf(fmt, a);
    va_end(a);
    putchar('\n');
    fflush(stdout);
}

static int fail(const std::wstring& msg) { return emit("{\"event\":\"error\",\"message\":%s}", json(msg).c_str()), 1; }

// stderr takes UTF-8 bytes: wide output would go to a binary --err file as UTF-16, to a text pipe through the C locale
static void err_printf(const wchar_t* fmt, ...) {
    va_list a;
    va_start(a, fmt);
    const int n = _vscwprintf(fmt, a);
    va_end(a);
    if (n <= 0) return;
    std::wstring w(n + 1, L'\0');
    va_start(a, fmt);
    vswprintf_s(w.data(), w.size(), fmt, a);
    va_end(a);
    std::string u(WideCharToMultiByte(CP_UTF8, 0, w.c_str(), n, nullptr, 0, nullptr, nullptr), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), n, u.data(), (int)u.size(), nullptr, nullptr);
    fwrite(u.data(), 1, u.size(), stderr);
    fflush(stderr);
}

// The child died without a last line: the retry it couldn't print, from its item states (warm_items.h). The items in flight
// are created alone first; one that dies again alone is skipped for this compile (never a crash key: the death may not be
// its own). False: no states to go by (it died before its items).
static bool retry_after_death(const std::wstring& file, DWORD pid, const Opts& o, const wchar_t* code, double seconds) {
    HANDLE f = CreateFileW(file.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
    LARGE_INTEGER size{};
    bool sized = f != INVALID_HANDLE_VALUE && GetFileSizeEx(f, &size) && size.QuadPart >= (LONGLONG)sizeof(ItemsHeader);
    // mapped, as the child wrote it: ReadFile isn't guaranteed to see a view's writes
    HANDLE m = sized ? CreateFileMappingW(f, nullptr, PAGE_READONLY, 0, 0, nullptr) : nullptr;
    auto* v = m ? (const uint8_t*)MapViewOfFile(m, FILE_MAP_READ, 0, 0, 0) : nullptr;
    if (m) CloseHandle(m);
    if (f != INVALID_HANDLE_VALUE) CloseHandle(f);
    if (!v) return false;
    struct Unmap {
        const void* v;
        ~Unmap() { UnmapViewOfFile(v); }
    } unmap{v};
    const ItemsHeader h = *(const ItemsHeader*)v;
    if (h.magic != kItemsMagic || h.pid != pid || (uint64_t)size.QuadPart != sizeof h + h.n12 || h.first > h.n12) return false;
    const uint8_t* s = v + sizeof h;
    uint64_t from = h.first, failed = 0, skipped = 0;
    for (; from < h.n12 && item_finished(s[from]); ++from) failed += s[from] == 2, skipped += s[from] == 5;
    std::vector<uint64_t> flight, isolate, skip;
    for (uint64_t j = from; j < h.n12; ++j)
        if (s[j] == kItemInFlight || s[j] == 4) flight.push_back(j);
    // one in flight that was created alone (--isolate): the death is its own; any other is created alone first
    bool alone = flight.size() == 1 && std::find(o.isolate.begin(), o.isolate.end(), flight[0]) != o.isolate.end();
    if (alone) skip = flight;
    else isolate = flight;
    for (uint64_t k : o.isolate)  // the ones the dead process didn't get to
        if (k >= from && k < h.n12 && !item_finished(s[k]) && std::find(flight.begin(), flight.end(), k) == flight.end()) isolate.push_back(k);
    auto list = [](const std::vector<uint64_t>& v) {
        std::string r;
        for (uint64_t k : v) r += (r.empty() ? "" : ",") + std::to_string(k);
        return r;
    };
    err_printf(L"the warm process died (exit code %ls): a new process goes on from item %llu, %zu item%ls in flight %ls\n", code,
             (unsigned long long)from, flight.size(), flight.size() == 1 ? L"" : L"s", alone ? L"skipped (it died alone)" : L"created alone first");
    emit("{\"event\":\"retry\",\"from\":%llu,\"rtThreads\":%d,\"failedItem\":-1,\"done\":%llu,\"total\":%llu,\"failed\":%llu,\"driverSkipped\":%llu,"
         "\"seconds\":%.1f,\"reason\":\"died\",\"crashed\":[],\"isolate\":[%s],\"skip\":[%s]}",
         from, o.rt_threads, from, h.total, failed, skipped, seconds, list(isolate).c_str(), list(skip).c_str());
    return true;
}

// One argument as CommandLineToArgvW reads it back: backslashes are literal except in a run before a quote, which doubles.
static std::wstring quoted(const std::wstring& a) {
    std::wstring q = L"\"";
    for (size_t i = 0;; ++i) {
        size_t n = 0;
        for (; i < a.size() && a[i] == L'\\'; ++i) ++n;
        if (i == a.size()) return q.append(2 * n, L'\\') + L"\"";
        q.append(a[i] == L'"' ? 2 * n + 1 : n, L'\\') += a[i];
    }
}

// AGS 6.x ABI (amd_ags.h, GPUOpen AGS SDK, MIT): only what device creation needs
struct AgsDeviceParams { IDXGIAdapter* adapter; IID iid; D3D_FEATURE_LEVEL level; };
struct AgsExtensionParams { const wchar_t *app, *engine; unsigned app_version, engine_version, uav_slot; };
struct AgsReturnedParams { ID3D12Device* device; unsigned extensions; unsigned char later[60]; };  // room for newer fields
constexpr unsigned AGS_APP_REGISTRATION = 1u << 3;

// the device through the game's AGS registration (UE: its project name, "UnrealEngine5.6", versions unspecified);
// on failure the reason, and *dev stays null
static std::wstring ags_device(const Opts& o, IDXGIAdapter* adapter, ID3D12Device** dev) {
    // by path, its own imports from System32 only: a game folder may hold mods named like system DLLs
    HMODULE m = LoadLibraryExW(o.ags.c_str(), nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!m) return L"loading " + o.ags + L" failed (error " + std::to_wstring(GetLastError()) + L")";
    auto version = (int(*)())GetProcAddress(m, "agsGetVersionNumber");
    auto init = (int(*)(int, const void*, void**, void*))GetProcAddress(m, "agsInitialize");
    auto create = (int(*)(void*, const AgsDeviceParams*, const AgsExtensionParams*, AgsReturnedParams*))GetProcAddress(m, "agsDriverExtensionsDX12_CreateDevice");
    int v = version ? version() : 0;
    if (!init || !create || v >> 22 != 6)
        return o.ags + L" is not AGS 6 (version " + std::to_wstring(v >> 22) + L"." + std::to_wstring(v >> 12 & 0x3ff) + L")";
    void* ctx = nullptr;  // kept until exit, like the game's
    if (int r = init(v, nullptr, &ctx, nullptr)) return L"agsInitialize returned " + std::to_wstring(r);
    AgsDeviceParams dp = {adapter, __uuidof(ID3D12Device), D3D_FEATURE_LEVEL_11_0};
    AgsExtensionParams ep = {o.ags_app.c_str(), o.ags_engine.c_str(), o.ags_app_version, o.ags_engine_version, 0};
    AgsReturnedParams rp = {};
    if (int r = create(ctx, &dp, &ep, &rp)) return L"agsDriverExtensionsDX12_CreateDevice returned " + std::to_wstring(r);
    *dev = rp.device;
    return rp.extensions & AGS_APP_REGISTRATION ? L"" : L"the driver doesn't support AGS app registration";
}

// NVIDIA Aftermath (GFSDK_Aftermath.h, GFSDK_Aftermath_GpuCrashDump.h): the callbacks drop what the driver hands over,
// so nothing is written anywhere
static void am_drop(const void*, uint32_t, void*) {}
static HMODULE g_am;

static std::wstring am_result(const char* call, int r) {   // 1: GFSDK_Aftermath_Result_Success
    wchar_t b[96];
    swprintf_s(b, L"GFSDK_Aftermath_%hs returned 0x%x", call, (unsigned)r);
    return r == 1 ? L"" : b;
}

// crash dumps on, before the device, as the game starts them; on failure the reason
static std::wstring aftermath_dumps(const Opts& o) {
    // by path, its own imports from System32 only, as --ags
    g_am = LoadLibraryExW(o.aftermath.c_str(), nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!g_am) return L"loading " + o.aftermath + L" failed (error " + std::to_wstring(GetLastError()) + L")";
    if (!o.am[2]) return L"";
    auto dumps = (int (*)(uint32_t, uint32_t, uint32_t, void*, void*, void*, void*, void*))GetProcAddress(g_am, "GFSDK_Aftermath_EnableGpuCrashDumps");
    if (!dumps) return o.aftermath + L" has no GFSDK_Aftermath_EnableGpuCrashDumps";
    return am_result("EnableGpuCrashDumps", dumps(o.am[0], o.am[2], o.am[3], (void*)am_drop, (void*)am_drop, nullptr, nullptr, nullptr));
}

static std::wstring aftermath_init(const Opts& o, ID3D12Device* dev) {
    auto init = (int (*)(uint32_t, uint32_t, ID3D12Device*))GetProcAddress(g_am, "GFSDK_Aftermath_DX12_Initialize");
    return init ? am_result("DX12_Initialize", init(o.am[0], o.am[1], dev)) : o.aftermath + L" has no GFSDK_Aftermath_DX12_Initialize";
}

// runs as <game exe name>; exit 0 = done printed, 1 = error printed, 3 = retry printed (a new process goes on from there)
static std::atomic<bool> g_ds_noflush{false};
static void* g_ds_flush;
// A process that dies keeps what it wrote (the OS has it), only a crash of Windows itself may lose the unflushed tail.
static BOOL WINAPI ds_flush(HANDLE f) { return g_ds_noflush || ((decltype(&FlushFileBuffers))g_ds_flush)(f); }

// Points d3dscache.dll's FlushFileBuffers import at ds_flush; why not, empty when it did.
static std::wstring ds_noflush() {
    auto base = (BYTE*)LoadLibraryExW(L"d3dscache.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!base) return L"no d3dscache.dll";
    auto& dir = ((IMAGE_NT_HEADERS*)(base + ((IMAGE_DOS_HEADER*)base)->e_lfanew))->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    for (auto d = (IMAGE_IMPORT_DESCRIPTOR*)(base + dir.VirtualAddress); dir.Size && d->Name; ++d) {
        if (!d->OriginalFirstThunk) continue;
        auto slot = (IMAGE_THUNK_DATA*)(base + d->FirstThunk);
        for (auto name = (IMAGE_THUNK_DATA*)(base + d->OriginalFirstThunk); name->u1.AddressOfData; ++name, ++slot) {
            if (IMAGE_SNAP_BY_ORDINAL(name->u1.Ordinal) || strcmp(((IMAGE_IMPORT_BY_NAME*)(base + name->u1.AddressOfData))->Name, "FlushFileBuffers")) continue;
            DWORD was;
            if (!VirtualProtect(&slot->u1.Function, sizeof slot->u1.Function, PAGE_READWRITE, &was)) return L"its import table can't be written";
            g_ds_flush = (void*)slot->u1.Function;
            g_ds_noflush = true;
            slot->u1.Function = (ULONG_PTR)ds_flush;
            VirtualProtect(&slot->u1.Function, sizeof slot->u1.Function, was, &was);
            return L"";
        }
    }
    return L"d3dscache.dll imports no FlushFileBuffers";
}

static std::wstring asd_key(DWORD pid = GetCurrentProcessId()) { return L"Software\\Microsoft\\DirectX\\ShaderCache\\SCSKiller-Warm-" + std::to_wstring(pid); }

// --asd-*: registers the delivery for exe_path (the staged exe), as Gaming Services registers it for the game; why not,
// empty when it did. Nothing is left behind on failure.
static std::wstring asd_register(const Opts& o, const std::wstring& exe_path, std::wstring exe) {
    if (o.asd_name.empty() || o.asd_name.find(L'\\') != std::wstring::npos) return L"the delivery's app name can't name a registry key";
    // the runtime matches the launched exe's final path
    HANDLE h = CreateFileW(exe_path.c_str(), 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
    wchar_t fin[MAX_PATH + 8];
    DWORD n = h == INVALID_HANDLE_VALUE ? 0 : GetFinalPathNameByHandleW(h, fin, MAX_PATH + 8, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    if (h != INVALID_HANDLE_VALUE) CloseHandle(h);
    if (!n || n >= MAX_PATH + 8 || wcsncmp(fin, L"\\\\?\\", 4) || !wcsncmp(fin + 4, L"UNC\\", 4)) return L"the staged exe's final path can't be read";
    CharLowerBuffW(exe.data(), (DWORD)exe.size());  // as Gaming Services writes it
    const std::wstring app = asd_key() + L"\\" + o.asd_name, comp = app + L"\\Components\\default";
    auto sz = [](HKEY k, const wchar_t* name, const std::wstring& v) {
        return !RegSetValueExW(k, name, 0, REG_SZ, (const BYTE*)v.c_str(), DWORD((v.size() + 1) * sizeof(wchar_t)));
    };
    auto qw = [](HKEY k, const wchar_t* name, uint64_t v) { return !RegSetValueExW(k, name, 0, REG_QWORD, (const BYTE*)&v, sizeof v); };
    auto make = [](const std::wstring& path, const std::function<bool(HKEY)>& fill) {
        HKEY k;
        if (RegCreateKeyExW(HKEY_CURRENT_USER, path.c_str(), 0, nullptr, 0, KEY_SET_VALUE, nullptr, &k, nullptr)) return false;
        bool ok = fill(k);
        RegCloseKey(k);
        return ok;
    };
    RegDeleteTreeW(HKEY_CURRENT_USER, asd_key().c_str());  // a dead process's of the same id
    FILETIME created, x;  // the app's sweep tells this process from a later one with its id by it
    bool ok = GetProcessTimes(GetCurrentProcess(), &created, &x, &x, &x) &&
              make(asd_key(), [&](HKEY k) { return qw(k, L"SCSKillerOwner", (uint64_t)created.dwHighDateTime << 32 | created.dwLowDateTime); });
    ok = ok && make(comp, [&](HKEY k) { return sz(k, L"Name", L"default") && sz(k, L"SODBPath", L""); });
    for (auto& [family, db] : o.asd_psdbs)
        ok = ok && family.find(L'\\') == std::wstring::npos && make(comp + L"\\AdapterFamilies\\" + family, [&](HKEY k) { return sz(k, L"PSDBPath", db); });
    // the app key's values last: the runtime goes by them
    ok = ok && make(app, [&](HKEY k) {
        return sz(k, L"ExeFilename", exe) && sz(k, L"Name", o.asd_name) && qw(k, L"Version", o.asd_version) &&
               sz(k, L"EngineName", o.asd_engine) && qw(k, L"EngineVersion", o.asd_engine_version) && sz(k, L"ExePath", fin + 4);
    });
    if (ok) return L"";
    RegDeleteTreeW(HKEY_CURRENT_USER, asd_key().c_str());
    return L"the user's shader cache registration can't be written";
}

static int child(DWORD parent_pid, const Opts& o) {
    for (int fd : {1, 2}) {  // a packaged child's output goes through the parent's pipes
        HANDLE h = CreateFileW(pipe_name(parent_pid, fd).c_str(), GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (h == INVALID_HANDLE_VALUE) continue;
        SetStdHandle(fd == 1 ? STD_OUTPUT_HANDLE : STD_ERROR_HANDLE, h);
        _dup2(_open_osfhandle((intptr_t)h, _O_WRONLY), fd);
    }
    SetPriorityClass(GetCurrentProcess(), o.idle ? IDLE_PRIORITY_CLASS : BELOW_NORMAL_PRIORITY_CLASS);  // the driver's own compile threads start at the class's normal
    HANDLE parent = OpenProcess(SYNCHRONIZE, FALSE, parent_pid), beat_map = OpenFileMappingW(FILE_MAP_READ, FALSE, beat_name(parent_pid).c_str());
    wchar_t self[MAX_PATH];
    GetModuleFileNameW(nullptr, self, MAX_PATH);
    std::wstring dir = self, exe = dir.substr(dir.find_last_of(L'\\') + 1);
    dir.resize(dir.size() - exe.size());
    SetEnvironmentVariableW(L"SCSKILLER_MODE", L"warm");
    SetEnvironmentVariableW(L"SCSKILLER_WARM_MEM_RESTART", o.memory_restart ? L"1" : nullptr);
    if (o.ds_noflush) {
        std::wstring why = ds_noflush();
        if (!why.empty()) err_printf(L"D3DSCache flushes stay on, so the compile runs at normal speed: %ls\n", why.c_str());
    }
    D3D12SDKVersion = o.sdk;  // before the proxy loads the system d3d12.dll
    HMODULE m = LoadLibraryW((dir + L"d3d12.dll").c_str());
    auto proc = [m](const char* n) { return m ? (void*)GetProcAddress(m, n) : nullptr; };
    auto create_device = (decltype(&D3D12CreateDevice))proc("D3D12CreateDevice");
    auto options = (void(WINAPI*)(int, BOOL, uint64_t))proc("SCSKiller_WarmOptions");
    auto start = (void(WINAPI*)(IUnknown*))proc("SCSKiller_StartWarm");
    auto progress = (void(WINAPI*)(uint64_t*))proc("SCSKiller_Progress");
    auto control = (void(WINAPI*)(int))proc("SCSKiller_Control");
    auto warm_rt = (void(WINAPI*)(int, const uint64_t*, uint32_t))proc("SCSKiller_WarmRt");
    auto retry = (void(WINAPI*)(uint64_t*))proc("SCSKiller_Retry");
    auto memory = (void(WINAPI*)(uint32_t))proc("SCSKiller_WarmMemory");
    auto warm_crash = (void(WINAPI*)(const uint8_t*, uint32_t, const uint64_t*, uint32_t))proc("SCSKiller_WarmCrash");
    auto crashes = (BOOL(WINAPI*)(const char**, const char**))proc("SCSKiller_Crashes");
    auto warm_pass = (void(WINAPI*)(const uint8_t*, uint64_t, uint32_t))proc("SCSKiller_WarmPass");
    auto workers = (void(WINAPI*)(uint32_t*))proc("SCSKiller_Workers");  // optional: the progress line then has no worker counts
    auto work = (uint64_t(WINAPI*)())proc("SCSKiller_Work");  // optional: then no work count
    auto skipped = (void(WINAPI*)(uint64_t*))proc("SCSKiller_Skipped");
    auto hung = (BOOL(WINAPI*)(const char**))proc("SCSKiller_Hung");
    auto passed_over = (uint64_t(WINAPI*)(uint64_t))proc("SCSKiller_PassedOver");  // -1 below: a proxy without it
    auto unsupported = (uint64_t(WINAPI*)())proc("SCSKiller_Unsupported");  // optional: then 0
    auto items12 = (uint64_t(WINAPI*)())proc("SCSKiller_D3D12Items");
    auto start11 = (void(WINAPI*)(LUID))proc("SCSKiller_StartWarm11");
    auto load = (void(WINAPI*)())proc("SCSKiller_WarmLoad");
    auto count_cached = (BOOL(WINAPI*)())proc("SCSKiller_CountCached");  // optional: then no cached count
    auto cached = (uint64_t(WINAPI*)())proc("SCSKiller_Cached");
    auto warm_known = (void(WINAPI*)(const uint8_t*, uint32_t))proc("SCSKiller_WarmKnown");
    auto known = (void(WINAPI*)(uint64_t*, const char**))proc("SCSKiller_Known");
    auto memory_stop = (BOOL(WINAPI*)(uint64_t*))proc("SCSKiller_MemoryStop");  // optional: then never a memory retry
    if (!create_device || !options || !start || !progress || !control || !warm_rt || !retry || !memory || !warm_crash || !crashes || !skipped || !hung || !load ||
        !warm_known || !known ||
        (o.pass >= 0 && !warm_pass))
        return fail(L"proxy d3d12.dll missing or too old");
    HKEY asd;
    if (count_cached && cached && !RegOpenKeyExW(HKEY_CURRENT_USER, asd_key(parent_pid).c_str(), 0, KEY_READ, &asd)) {  // the parent registered the delivery
        RegCloseKey(asd);
        if (!count_cached()) err_printf(L"the pipelines the game's Advanced Shader Delivery serves can't be counted: d3dscache.dll's stores can't be hooked\n");
    }
    std::vector<uint8_t> pass_of;
    if (o.pass >= 0) {
        HANDLE pf = CreateFileW((dir + L"scskiller_pass.bin").c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
        LARGE_INTEGER size{};
        DWORD got = 0;
        bool read = pf != INVALID_HANDLE_VALUE && GetFileSizeEx(pf, &size) && size.QuadPart < (1ll << 31);
        if (read) pass_of.resize((size_t)size.QuadPart);
        read = read && (pass_of.empty() || (ReadFile(pf, pass_of.data(), (DWORD)pass_of.size(), &got, nullptr) && got == pass_of.size()));
        if (pf != INVALID_HANDLE_VALUE) CloseHandle(pf);
        if (!read) return fail(L"--pass: scskiller_pass.bin not readable");
    }

    // The game renders on the discrete GPU: default to the adapter with the most dedicated VRAM.
    IDXGIFactory1* f = nullptr;
    IDXGIAdapter1 *best = nullptr, *a;
    DXGI_ADAPTER_DESC1 bd{}, d;
    CreateDXGIFactory1(IID_PPV_ARGS(&f));
    for (UINT i = 0; f && f->EnumAdapters1(i, &a) != DXGI_ERROR_NOT_FOUND; ++i) {
        a->GetDesc1(&d);
        uint64_t luid = (uint64_t)(uint32_t)d.AdapterLuid.HighPart << 32 | d.AdapterLuid.LowPart;
        if (o.luid ? luid == o.luid : !(d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) && (!best || d.DedicatedVideoMemory > bd.DedicatedVideoMemory)) best = a, bd = d;
    }
    ID3D12Device* dev = nullptr;
    if (!best) return fail(o.luid ? L"no adapter with that LUID" : L"no GPU adapter");
    options(o.threads, o.idle, o.start);
    warm_rt(o.rt_threads, o.skip.data(), (uint32_t)o.skip.size());
    memory(o.memory_mb);
    warm_crash(o.skip_keys.data(), (uint32_t)(o.skip_keys.size() / 20), o.isolate.data(), (uint32_t)o.isolate.size());
    warm_known(o.known_keys.data(), (uint32_t)(o.known_keys.size() / 20));
    // the dbs before the device, which would read them: the parent's start window leaves this out (millions of records take minutes)
    HANDLE loading = OpenEventW(EVENT_MODIFY_STATE, FALSE, loading_name(parent_pid).c_str());
    if (loading) SetEvent(loading);
    load();
    if (loading) ResetEvent(loading), CloseHandle(loading);
    const bool d3d11_only = items12 && start11 && !items12();
    std::wstring am_why = o.aftermath.empty() || d3d11_only ? L"" : aftermath_dumps(o);
    if (!o.ags.empty() && !d3d11_only) {
        std::wstring why = ags_device(o, best, &dev);
        if (!why.empty()) err_printf(L"AGS app %ls: %ls%ls\n", o.ags_app.c_str(), why.c_str(), dev ? L"" : L": a plain device (the exe name's cache)");
    }
    HRESULT hr = dev || d3d11_only ? S_OK : create_device(best, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev));
    if (FAILED(hr) && o.sdk) {
        err_printf(L"D3D12 device creation failed on the game's D3D12 runtime (SDK %u, 0x%08X): the system's runs\n", o.sdk, (unsigned)hr);
        return EXIT_SYSTEM_RUNTIME;
    }
    if (FAILED(hr)) {
        wchar_t why[16];
        swprintf_s(why, L" (0x%08X)", (unsigned)hr);
        return fail(L"D3D12 device creation failed on " + std::wstring(bd.Description) + why);
    }
    // every return from here on sets it after the last line: the driver may write its cache at exit
    HANDLE final_event = OpenEventW(EVENT_MODIFY_STATE, FALSE, final_name(parent_pid).c_str());
    auto ended = [final_event](int code) {
        if (final_event) SetEvent(final_event);
        return code;
    };

    if (!o.aftermath.empty() && dev) {
        if (am_why.empty()) am_why = aftermath_init(o, dev);
        if (am_why.empty()) err_printf(L"NVIDIA Aftermath started as the game starts it (version 0x%x, feature flags 0x%x)\n", o.am[0], o.am[1]);
        else err_printf(L"NVIDIA Aftermath: %ls, compiling without it\n", am_why.c_str());
    }

    uint64_t p[4];  // done, total, failed, finished
    progress(p);
    if (o.pass >= 0 && pass_of.size() != p[1])
        return ended(fail(L"scskiller_pass.bin has " + std::to_wstring(pass_of.size()) + L" items, the dbs " + std::to_wstring(p[1])));
    if (o.pass >= 0) warm_pass(pass_of.data(), pass_of.size(), (uint32_t)o.pass);
    const uint64_t first = p[0];
    emit("{\"event\":\"start\",\"total\":%llu,\"adapter\":%s,\"exe\":%s}", p[1], json(bd.Description).c_str(), json(exe).c_str());
    // ReShade unloads its add-ons with the last device: one kept for the process's life unloads them at exit, as in a game
    // (Luma's unload mid-process crashed the driver's teardown)
    if (d3d11_only && !o.layer.empty())
        if (auto create = (PFN_D3D11_CREATE_DEVICE)GetProcAddress(LoadLibraryW(L"d3d11.dll"), "D3D11CreateDevice")) {
            ID3D11Device* kept = nullptr;
            create(best, D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &kept, nullptr, nullptr);
        }
    if (d3d11_only) start11(bd.AdapterLuid);
    else start(dev);  // looks for a layer's device under it: a layer's code still runs
    if (HANDLE started = OpenEventW(EVENT_MODIFY_STATE, FALSE, started_name(parent_pid).c_str())) SetEvent(started), CloseHandle(started);

    HANDLE stop = o.stop_event.empty() ? nullptr : CreateEventW(nullptr, TRUE, FALSE, o.stop_event.c_str());
    auto beat = (volatile LONG*)MapViewOfFile(beat_map, FILE_MAP_READ, 0, 0, sizeof(LONG));
    ULONGLONG t0 = GetTickCount64(), seen = t0, printed = t0;
    LONG last = beat ? *beat : 0;
    bool stopping = false;
    uint64_t s[2];  // skipped for this compile, those before the retry's first item
    for (;; Sleep(100)) {
        progress(p);
        if (p[3]) break;
        ULONGLONG now = GetTickCount64();
        if (beat && *beat != last) last = *beat, seen = now;
        stopping = stopping || (stop && WaitForSingleObject(stop, 0) == WAIT_OBJECT_0) || WaitForSingleObject(parent, 0) != WAIT_TIMEOUT;
        control(stopping ? STOP : beat && now - seen > 1000 ? PAUSE : RUN);
        if (now - printed < 500) continue;
        printed = now;
        uint32_t w[2] = {0, 0};
        if (workers) workers(w);
        skipped(s);
        emit("{\"event\":\"progress\",\"done\":%llu,\"total\":%llu,\"failed\":%llu,\"driverSkipped\":%llu,\"rate\":%.1f,\"workers\":%u,\"threads\":%u,\"work\":%llu,\"unsupported\":%llu,\"cached\":%llu}",
             p[0], p[1], p[2], s[0], (p[0] - first) * 1000.0 / (now - t0), w[0], w[1], work ? work() : 0ull, unsupported ? unsupported() : 0ull,
             cached ? cached() : 0ull);
    }
    g_ds_noflush = false;  // d3dscache's checkpoints from here on, its last at exit too, reach the disk
    uint64_t r[5];  // needed, from, ray tracing threads, failed before from, the item that failed alone (~0: none)
    retry(r);
    skipped(s);
    const char *crashed, *isolate;  // JSON array bodies
    BOOL removed = crashes(&crashed, &isolate);  // 2: a replay faulted
    const char* hung_skip;  // JSON array body
    BOOL hang = hung(&hung_skip);
    uint64_t kn[2];  // of the skipped, those that crashed or hung this driver in an earlier compile; those before the retry's first
    const char* culprits;  // JSON array body
    known(kn, &culprits);
    uint64_t mem[2] = {0, 0};  // private and free MB when it stopped to free memory
    const bool freed = memory_stop && memory_stop(mem);
    if (r[0] && !stopping) {
        emit("{\"event\":\"retry\",\"from\":%llu,\"rtThreads\":%llu,\"failedItem\":%lld,\"done\":%llu,\"total\":%llu,\"failed\":%llu,\"driverSkipped\":%llu,\"seconds\":%.1f,"
             "\"privateMB\":%llu,\"freeMB\":%llu,\"reason\":\"%s\",\"crashed\":[%s],\"isolate\":[%s],\"skip\":[%s],\"passedOver\":%lld,\"unsupported\":%llu,\"cached\":%llu,\"known\":%llu,\"culprits\":[%s]}",
             r[1], r[2], r[4] == ~0ull ? -1ll : (long long)r[4], r[1], p[1], r[3], s[1], (GetTickCount64() - t0) / 1000.0, mem[0], mem[1],
             removed == 2 ? "fault" : removed ? "removed" : hang ? "hang" : freed ? "memory" : "rt", crashed, isolate, hung_skip, passed_over ? (long long)passed_over(r[1]) : -1ll,
             unsupported ? unsupported() : 0ull, cached ? cached() : 0ull, kn[1], culprits);
        return ended(3);
    }
    if (p[0] < p[1] && !stopping)
        return ended((emit("{\"event\":\"error\",\"message\":%s,\"culprits\":[%s]}", json(L"replay aborted after repeated faults, see stage\\scskiller.log").c_str(), culprits), 1));
    // stopped poisoned: items after the first unfinished one may be done, so it resumes from that one, not from the count
    const uint64_t done = r[0] ? r[1] : p[0], failed = r[0] ? r[3] : p[2], skips = r[0] ? s[1] : s[0];
    // noflush: d3dscache.dll's flushes were skipped while it created (ds_noflush hooked them)
    emit("{\"event\":\"done\",\"done\":%llu,\"total\":%llu,\"failed\":%llu,\"driverSkipped\":%llu,\"seconds\":%.1f,\"stopped\":%s,\"crashed\":[%s],\"noflush\":%s,\"passedOver\":%lld,\"unsupported\":%llu,\"cached\":%llu,"
         "\"known\":%llu,\"culprits\":[%s]}", done,
         p[1], failed, skips, (GetTickCount64() - t0) / 1000.0, done < p[1] ? "true" : "false", crashed, g_ds_flush ? "true" : "false",
         passed_over ? (long long)passed_over(done) : -1ll, unsupported ? unsupported() : 0ull, cached ? cached() : 0ull, r[0] ? kn[1] : kn[0], culprits);
    return ended(0);  // a normal exit: the driver writes its cache now
}

// The replayer takes ANSI paths (its workers start from GetModuleFileNameA): only a path the code page holds exactly.
static bool acp(const std::wstring& w, std::string& out) {
    BOOL lossy = FALSE;
    const bool utf8 = GetACP() == CP_UTF8;  // takes no lossy flag, and holds every path
    int n = WideCharToMultiByte(CP_ACP, utf8 ? 0 : WC_NO_BEST_FIT_CHARS, w.c_str(), -1, nullptr, 0, nullptr, utf8 ? nullptr : &lossy);
    if (n <= 0 || lossy) return false;
    out.resize(n - 1);
    WideCharToMultiByte(CP_ACP, utf8 ? 0 : WC_NO_BEST_FIT_CHARS, w.c_str(), -1, out.data(), n, nullptr, nullptr);
    return true;
}

// fossilize-replay's --device-index of the adapter with this LUID, counted as its instance counts them (the same loader
// and layer settings), the first one without a LUID; -1 when none has it. driver: its driver's version and UUID, which a driver update changes.
static int vk_device_index(uint64_t luid, std::string* driver = nullptr) {
    HMODULE m = LoadLibraryExW(L"vulkan-1.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    auto gpa = m ? (PFN_vkGetInstanceProcAddr)GetProcAddress(m, "vkGetInstanceProcAddr") : nullptr;
    auto create = gpa ? (PFN_vkCreateInstance)gpa(nullptr, "vkCreateInstance") : nullptr;
    VkApplicationInfo app{VK_STRUCTURE_TYPE_APPLICATION_INFO};
    app.apiVersion = VK_API_VERSION_1_1;
    VkInstanceCreateInfo ci{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO};
    ci.pApplicationInfo = &app;
    VkInstance inst = VK_NULL_HANDLE;
    int found = -1;
    if (create && create(&ci, nullptr, &inst) == VK_SUCCESS) {
        auto list = (PFN_vkEnumeratePhysicalDevices)gpa(inst, "vkEnumeratePhysicalDevices");
        auto props = (PFN_vkGetPhysicalDeviceProperties2)gpa(inst, "vkGetPhysicalDeviceProperties2");
        uint32_t n = 0;
        std::vector<VkPhysicalDevice> gpus;
        if (list && props && list(inst, &n, nullptr) == VK_SUCCESS && (gpus.resize(n), list(inst, &n, gpus.data())) >= 0)
            for (uint32_t i = 0; i < n && found < 0; ++i) {
                VkPhysicalDeviceIDProperties id{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES};
                VkPhysicalDeviceProperties2 p{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2, &id};
                props(gpus[i], &p);
                uint64_t l = 0;
                memcpy(&l, id.deviceLUID, sizeof l);  // LUID { LowPart, HighPart }, as the D3D12 path reads it
                if ((id.deviceLUIDValid && l == luid) || (!luid && i == 0)) found = (int)i;
                if (found == (int)i && driver)
                    *driver = std::to_string(p.properties.driverVersion) + ":" + std::string((const char*)id.driverUUID, VK_UUID_SIZE);
            }
        if (auto destroy = (PFN_vkDestroyInstance)gpa(inst, "vkDestroyInstance")) destroy(inst, nullptr);
    }
    if (m) FreeLibrary(m);
    return found;
}

// A copy of a .foz without the pipelines in out: entries are copied as they are (16-byte magic, then each entry's
// 40-character name, 16-byte header with its stored size first, payload). False if it can't be read or written.
static bool foz_without(const std::wstring& from, const std::wstring& to, const std::set<std::pair<unsigned, Fossilize::Hash>>& out) {
    FILE* in = _wfopen(from.c_str(), L"rb");
    FILE* o = in ? _wfopen(to.c_str(), L"wb") : nullptr;
    bool ok = o != nullptr;
    char magic[16];
    ok = ok && fread(magic, 1, 16, in) == 16 && fwrite(magic, 1, 16, o) == 16;
    std::vector<char> payload;
    for (char head[56]; ok && fread(head, 1, 56, in) == 56;) {
        uint32_t stored;
        memcpy(&stored, head + 40, 4);
        payload.resize(stored);
        if (stored && fread(payload.data(), 1, stored, in) != stored) break;  // a torn tail: Fossilize stops there too
        char tag[25] = {}, hash[17] = {};
        memcpy(tag, head, 24), memcpy(hash, head + 24, 16);
        if (out.count({(unsigned)strtoul(tag, nullptr, 16), (Fossilize::Hash)_strtoui64(hash, nullptr, 16)})) continue;
        ok = fwrite(head, 1, 56, o) == 56 && (!stored || fwrite(payload.data(), 1, stored, o) == stored);
    }
    if (in) fclose(in);
    if (o && fclose(o)) ok = false;
    return ok;
}

// --vulkan: the work folder's scskiller.foz, replayed by our fossilize-replay.exe staged under the game's exe name (NVIDIA
// keys its Vulkan cache on the process's file name, and the replayer's worker runs from the same file), in --stage-path's
// folders when given (AMD keys its own Vulkan cache on the exe's path in part). Fossilize's master restarts a worker that faults or hangs on a pipeline, which is skipped by the driver for this run; the passes below
// replay what its recovery left out. Only pipelines the driver refused, or that didn't parse in a run without faults,
// count as failed. The stop event reaches the replayer through the patch (third_party/fossilize): it creates nothing
// more and exits as when done. Its master runs at idle priority, its own.
static int vulkan_warm(const Opts& o, const std::wstring& work, const std::wstring& exe, const std::wstring& bin, const std::wstring& flat,
                       const std::wstring& stage, const std::vector<std::wstring>& dirs) {
    using namespace Fossilize;
    std::wstring replayer = bin + L"fossilize-replay.exe";
    if (GetFileAttributesW(replayer.c_str()) == INVALID_FILE_ATTRIBUTES) replayer = bin + L"..\\fossilize-replay.exe";  // the segheap\ build uses its parent's
    std::wstring staged = stage + exe;
    const std::wstring foz = flat + L"scskiller.foz";
    std::string staged_a;
    if (stage != flat && !acp(staged, staged_a)) {
        err_printf(L"--stage-path isn't in the system code page: staging in %ls\n", flat.c_str());
        staged = flat + exe;
    }
    if (staged.size() >= MAX_PATH || !acp(staged, staged_a))
        return fail(L"the Vulkan compile needs its staging path in the system code page and under 260 characters: " + staged);
    struct Unstage {
        std::vector<std::wstring> files;
        const std::vector<std::wstring>& dirs;
        ~Unstage() {
            for (auto& f : files) SetFileAttributesW(f.c_str(), FILE_ATTRIBUTE_NORMAL), DeleteFileW(f.c_str());
            for (auto d = dirs.rbegin(); d != dirs.rend(); ++d) RemoveDirectoryW(d->c_str());  // only if empty
        }
    } unstage{{}, dirs};
    if (!CopyFileW(replayer.c_str(), staged.c_str(), TRUE)) return fail(L"staging " + replayer + L" failed (error " + std::to_wstring(GetLastError()) + L")");
    unstage.files.push_back(staged);
    const std::wstring input = work + L"scskiller.foz";
    if (!CreateHardLinkW(foz.c_str(), input.c_str(), nullptr) && !CopyFileW(input.c_str(), foz.c_str(), TRUE))
        return fail(L"staging " + input + L" failed (error " + std::to_wstring(GetLastError()) + L")");
    unstage.files.push_back(foz);
    if (!SetCurrentDirectoryW(flat.c_str())) return fail(L"can't enter " + flat);  // the replayer's archive path is relative

    // every pipeline to replay, by kind: its total, and the failed keys of the ones it didn't create
    const ResourceTag kinds[] = {RESOURCE_GRAPHICS_PIPELINE, RESOURCE_COMPUTE_PIPELINE, RESOURCE_RAYTRACING_PIPELINE};
    std::vector<Hash> all[3];
    {
        std::unique_ptr<DatabaseInterface> db(create_stream_archive_database("scskiller.foz", DatabaseMode::ReadOnly));
        if (!db || !db->prepare()) return fail(L"scskiller.foz can't be read");
        for (int k = 0; k < 3; ++k) {
            size_t n = 0;
            if (!db->get_hash_list_for_resource_tag(kinds[k], &n, nullptr)) return fail(L"scskiller.foz can't be read");
            all[k].resize(n);
            if (n && !db->get_hash_list_for_resource_tag(kinds[k], &n, all[k].data())) return fail(L"scskiller.foz can't be read");
        }
    }
    const uint64_t total = all[0].size() + all[1].size() + all[2].size();

    // the replayer inherits this environment as is (Fossilize's own environment option goes through the ANSI block)
    SetEnvironmentVariableW(L"VK_LOADER_LAYERS_DISABLE", L"~all~");  // overlays and recorders, Steam's Fossilize layer among them
    SetEnvironmentVariableW(L"DISABLE_VK_LAYER_VALVE_steam_fossilize_1", L"1");
    using Ids = std::set<std::pair<unsigned, Hash>>;
    auto lists = [&](const wchar_t* what, bool remove) {  // the patch's <what>.<pid>.txt files of the stage
        Ids got;
        WIN32_FIND_DATAW fd;
        HANDLE find = FindFirstFileW((flat + what + L".*.txt").c_str(), &fd);
        for (BOOL more = find != INVALID_HANDLE_VALUE; more; more = FindNextFileW(find, &fd)) {
            std::wstring f = flat + fd.cFileName;
            if (FILE* in = _wfopen(f.c_str(), L"rb")) {
                unsigned tag;
                unsigned long long hash;
                while (fscanf(in, "%u %llx", &tag, &hash) == 2) got.insert({tag, (Hash)hash});
                fclose(in);
            }
            if (remove) DeleteFileW(f.c_str());
            else if (std::find(unstage.files.begin(), unstage.files.end(), f) == unstage.files.end()) unstage.files.push_back(f);
        }
        if (find != INVALID_HANDLE_VALUE) FindClose(find);
        return got;
    };
    // the caller's stop event, else one of this process's own: a driver change stops the replay too
    const std::wstring stop_event = o.stop_event.empty() ? stop_name(GetCurrentProcessId()) : o.stop_event;
    SetEnvironmentVariableW(L"SCSKILLER_REPLAY_STOP", stop_event.c_str());
    SetEnvironmentVariableW(L"SCSKILLER_REPLAY_CREATED", flat.substr(0, flat.size() - 1).c_str());
    std::string driver;
    const int device = vk_device_index(o.luid, &driver);
    if (device < 0) return fail(L"no Vulkan device has that adapter's LUID");
    HANDLE stop = CreateEventW(nullptr, TRUE, FALSE, stop_event.c_str());
    if (!stop) return fail(L"stop event setup failed (error " + std::to_wstring(GetLastError()) + L")");
    // A driver updated or reset under a running replay refuses everything after it: nothing of such a run counts as
    // failed. The lists are read once a second, the driver checked when refusals came in (at most every 10 s) and
    // after each pass.
    bool gone = false;
    size_t refusals_seen = 0;
    ULONGLONG checked = 0, looked = 0;
    auto driver_gone = [&](bool now) {
        if (gone) return true;
        if (!now && GetTickCount64() - looked < 1000) return false;
        looked = GetTickCount64();
        const size_t refusals = lists(L"failed", false).size();
        const bool lost = !lists(L"lost", false).empty();
        if (!now && !lost && (refusals == refusals_seen || GetTickCount64() - checked < 10000)) return false;
        refusals_seen = refusals, checked = GetTickCount64();
        std::string d;
        gone = lost || vk_device_index(o.luid, &d) != device || d != driver;
        if (gone) SetEvent(stop);
        return gone;
    };

    // The archive is replayed in passes, each by a fresh replayer: Fossilize's own recovery leaves out more than the
    // faulty pipeline (what shares its shaders, what it hadn't reached yet), so a pass with a fault is followed by one
    // without every faulty, suspect and refused pipeline so far. Created ones replay as cache hits (about 0.1 ms each).
    wchar_t lim[16] = {};
    const unsigned hang_s = GetEnvironmentVariableW(L"SCSKILLER_REPLAY_TIMEOUT_S", lim, 16) && _wtoi(lim) > 0 ? _wtoi(lim) : 300;
    constexpr int max_passes = 16;
    // A crash reports its one pipeline, a hang every one in flight: a pass's lone new fault is confirmed, several are
    // suspects, each replayed later without the other suspects and confirmed only if it faults again.
    Ids created, confirmed, suspects, refused, unparsed;
    std::pair<unsigned, Hash> trying{};
    bool alone = false;
    const ULONGLONG t0 = GetTickCount64();
    uint64_t shown = 0;  // the progress shown: later passes never move it back
    bool stopped = false;
    int code = 0;
    for (int pass = 1;; ++pass) {
        std::string archive = "scskiller.foz";
        if (pass > 1) {
            archive = "pass" + std::to_string(pass) + ".foz";
            const std::wstring to = flat + std::wstring(archive.begin(), archive.end());
            unstage.files.push_back(to);
            Ids out = confirmed;
            out.insert(suspects.begin(), suspects.end());
            out.insert(refused.begin(), refused.end());
            if (alone) out.erase(trying);
            if (!foz_without(foz, to, out)) return fail(L"writing " + to + L" failed");
        }
        lists(L"failed", true), lists(L"unparsed", true);  // this pass's own

        // the replayer's output goes to the stage's log, never into these JSON lines
        SECURITY_ATTRIBUTES sa{sizeof sa, nullptr, TRUE};
        HANDLE log = CreateFileW((flat + L"scskiller.log").c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, &sa, OPEN_ALWAYS, 0, nullptr);
        HANDLE out = GetStdHandle(STD_OUTPUT_HANDLE), err = GetStdHandle(STD_ERROR_HANDLE);
        for (HANDLE h : {out, err}) SetHandleInformation(h, HANDLE_FLAG_INHERIT, 0);
        ExternalReplayer r;
        ExternalReplayer::Options ro{};
        const char* dbs[] = {archive.c_str()};
        ro.external_replayer_path = staged_a.c_str();
        ro.databases = dbs, ro.num_databases = 1;
        ro.num_threads = o.threads > 0 ? (unsigned)o.threads : std::thread::hardware_concurrency();  // threads of one worker process (the patch)
        ro.device_index = (unsigned)device;
        ro.timeout_seconds = hang_s;  // no pipeline done for this long: the worker is hung, ended and started again past it
        ro.quiet = log == INVALID_HANDLE_VALUE;
        if (!ro.quiet) SetStdHandle(STD_OUTPUT_HANDLE, log), SetStdHandle(STD_ERROR_HANDLE, log);
        const bool started = r.start(ro);
        SetStdHandle(STD_OUTPUT_HANDLE, out), SetStdHandle(STD_ERROR_HANDLE, err);
        if (log != INVALID_HANDLE_VALUE) CloseHandle(log);
        if (!started) return fail(L"fossilize-replay didn't start");
        if (pass == 1) emit("{\"event\":\"start\",\"total\":%llu,\"adapter\":\"\",\"exe\":%s}", total, json(exe).c_str());

        ExternalReplayer::Progress p{};
        auto sum = [&](uint32_t ExternalReplayer::TypeProgress::* f) { return (uint64_t)(p.graphics.*f) + p.compute.*f + p.raytracing.*f; };
        auto faulty_now = [&](bool fill, Ids* into) {
            uint64_t n = 0;
            for (int k = 0; k < 3; ++k) {
                auto get = k == 0 ? &ExternalReplayer::get_faulty_graphics_pipelines : k == 1 ? &ExternalReplayer::get_faulty_compute_pipelines : &ExternalReplayer::get_faulty_raytracing_pipelines;
                size_t c = 0;
                if (!(r.*get)(&c, nullptr, nullptr)) continue;
                n += c;
                if (!fill || !c) continue;
                std::vector<unsigned> index(c);
                std::vector<Hash> hashes(c);
                if ((r.*get)(&c, index.data(), hashes.data()))
                    for (Hash h : hashes) into->insert({(unsigned)kinds[k], h});
            }
            return n;
        };
        ULONGLONG printed = GetTickCount64();
        for (;; Sleep(100)) {
            const bool exited = r.is_process_complete(&code);
            r.poll_progress(p);  // also takes in the faulty pipelines the replayer reports
            if (exited) break;
            driver_gone(false);
            ULONGLONG now = GetTickCount64();
            if (now - printed < 500) continue;
            printed = now;
            // skipped holds the ones that failed to parse too
            uint64_t done = std::min<uint64_t>(total, sum(&ExternalReplayer::TypeProgress::completed) + sum(&ExternalReplayer::TypeProgress::skipped) +
                                                          sum(&ExternalReplayer::TypeProgress::cached));
            shown = std::max(shown, done);
            emit("{\"event\":\"progress\",\"done\":%llu,\"total\":%llu,\"failed\":%llu,\"driverSkipped\":%llu,\"rate\":%.1f}", shown, total,
                 (uint64_t)(refused.size() + refusals_seen), (uint64_t)(confirmed.size() + suspects.size()) + faulty_now(false, nullptr), shown * 1000.0 / (now - t0));
        }
        if (driver_gone(true)) return fail(L"The graphics driver changed or reset during the compile, compile again");
        refusals_seen = 0;
        stopped = WaitForSingleObject(stop, 0) == WAIT_OBJECT_0;
        Ids fresh;
        faulty_now(true, &fresh);
        if (alone) {
            suspects.erase(trying);
            if (fresh.erase(trying)) confirmed.insert(trying);
        }
        const bool fault = !fresh.empty();
        if (fresh.size() == 1) confirmed.insert(*fresh.begin());
        else suspects.insert(fresh.begin(), fresh.end());
        const Ids made_now = lists(L"created", false), refused_now = lists(L"failed", false);
        created.insert(made_now.begin(), made_now.end());
        refused.insert(refused_now.begin(), refused_now.end());
        unparsed = lists(L"unparsed", false);
        if (stopped || code != 0) break;
        bool left = false;
        for (int k = 0; k < 3 && !left; ++k)
            for (Hash h : all[k]) {
                const std::pair<unsigned, Hash> id{(unsigned)kinds[k], h};
                if (!created.count(id) && !confirmed.count(id) && !suspects.count(id) && !refused.count(id)) { left = true; break; }
            }
        alone = !(fault && left) && !suspects.empty();  // a pass with a new fault first replays what it left out
        if (alone) trying = *suspects.begin();
        else if (!(fault && left)) break;
        if (pass == max_passes) {
            err_printf(L"%d passes: what wasn't replayed counts as skipped by the driver\n", max_passes);
            break;
        }
    }
    if (code != 0 && !stopped) return fail(L"fossilize-replay exited with code " + std::to_wstring(code) + L", see stage\\scskiller.log");
    Ids faulty = confirmed;
    faulty.insert(suspects.begin(), suspects.end());

    // Not created: refused by the driver, or unparsable in a replay without any fault, is failed (deterministic); every
    // other one is skipped by the driver (faulty or still a suspect, behind a faulty shader or library, not reached at the pass cap). On a
    // stop only the faulty ones count as skipped: the rest is what the stop left.
    uint64_t made = 0, failed = 0, skipped = 0;
    std::string keys;
    for (int k = 0; k < 3; ++k)
        for (Hash h : all[k]) {
            const std::pair<unsigned, Hash> id{(unsigned)kinds[k], h};
            if (created.count(id)) { ++made; continue; }
            if (refused.count(id) || faulty.empty() && unparsed.count(id)) ++failed;
            else if (!stopped || faulty.count(id)) ++skipped;
            // the plan item's key (VulkanPlan.Key): SHA-1 of 'V' and the archive entry's 40-character name
            char rec[42];
            snprintf(rec, sizeof rec, "V%024x%016llx", (unsigned)kinds[k], (unsigned long long)h);
            UCHAR sha[20];
            if (BCryptHash(BCRYPT_SHA1_ALG_HANDLE, nullptr, 0, (PUCHAR)rec, 41, sha, 20)) return fail(L"hashing a failed pipeline's key failed");
            keys.append((const char*)sha, 20);
        }
    if (!keys.empty()) {
        FILE* f = _wfopen((flat + L"scskiller_failed.keys").c_str(), L"wb");
        if (!f || fwrite(keys.data(), 1, keys.size(), f) != keys.size()) return fail(L"writing scskiller_failed.keys failed");
        fclose(f);
    }
    if (total && !made && faulty.empty() && refused.empty() && !stopped) return fail(L"fossilize-replay created no pipeline, see stage\\scskiller.log");
    emit("{\"event\":\"done\",\"done\":%llu,\"total\":%llu,\"failed\":%llu,\"driverSkipped\":%llu,\"seconds\":%.1f,\"stopped\":%s,\"crashed\":[]}",
         stopped ? made + failed + skipped : total, total, failed, skipped, (GetTickCount64() - t0) / 1000.0, stopped ? "true" : "false");
    return 0;
}

static int parent(int argc, wchar_t** argv, bool vulkan) {
    Opts o;
    if (argc < 3 || !parse(argc, argv, 3, o) || o.alive_turns < 0 || (o.alive_turns > 0) == o.alive_file.empty()) {
        fputs("usage: scskiller_warm <workdir> <game exe file name> [--threads N] [--priority below|idle] [--start N]\n"
              "                      [--stop-event <name>] [--adapter-luid <hex>] [--rt-threads N] [--skip i,j,...] [--memory-mb N]\n"
              "                      [--package <app user model id>] [--stage-path <install folder>\\<dir>\\<exe>]\n"
              "                      [--skip-keys <sha1 hex>,...|@<file>] [--known-keys <sha1 hex>,...|@<file>] [--isolate i,j,...]\n"
              "                      [--ags <amd_ags_x64.dll> --ags-app <name> --ags-engine <name> [--ags-version <hex>,<hex>]]\n"
              "                      [--pass K] [--layer <folder>] [--d3d12 <folder>] [--d3d11 <dll>] [--vulkan]\n"
              "                      [--aftermath <dll> --aftermath-init <version>,<features>,<apis>,<dump flags>]\n"
              "                      [--out <file>] [--err <file>] [--stop-file <path>] [--alive-file <path> --alive-turns N]\n"
              "                      [--d3dscache-noflush 1]\n", stderr);
        return fail(L"bad arguments");
    }
    std::wstring work = argv[1], exe = argv[2];
    if (work.back() != L'\\' && work.back() != L'/') work += L'\\';
    wchar_t self[MAX_PATH];
    GetModuleFileNameW(nullptr, self, MAX_PATH);
    std::wstring bin = self;
    bin.resize(bin.find_last_of(L'\\') + 1);
    DWORD at = GetFileAttributesW(work.c_str());
    if (at == INVALID_FILE_ATTRIBUTES || !(at & FILE_ATTRIBUTE_DIRECTORY) || exe.find_first_of(L"\\/") != std::wstring::npos)
        return fail(L"workdir not found or exe is not a file name");

    std::wstring flat;  // this run's own new staging folder: nothing in it was there before
    for (int n = 1; n < 100 && flat.empty(); ++n) {
        std::wstring d = work + L"stage-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(n) + L"\\";
        if (CreateDirectoryW(d.c_str(), nullptr)) flat = d;
        else if (GetLastError() != ERROR_ALREADY_EXISTS) break;
    }
    if (flat.empty()) return fail(L"can't create a staging folder in " + work + L" (error " + std::to_wstring(GetLastError()) + L")");
    emit("{\"event\":\"stage\",\"stage\":%s}", json(flat.substr(0, flat.size() - 1)).c_str());  // first: a failure from here on has its log there
    if (vulkan && (!o.stop_file.empty() || !o.alive_file.empty())) return fail(L"--vulkan stops only through --stop-event");
    std::wstring stage = flat;
    std::vector<std::wstring> dirs;  // the --stage-path folders, outermost first
    if (!o.stage_path.empty() && o.package.empty()) {
        std::wstring sub;
        for (size_t b = 0;;) {
            size_t e = o.stage_path.find_first_of(L"\\/", b);
            std::wstring c = o.stage_path.substr(b, e == std::wstring::npos ? e : e - b);
            bool bad = c.empty() || c == L"." || c == L".." || c.find_first_of(L"<>:\"|?*") != std::wstring::npos;
            for (wchar_t ch : c) bad = bad || ch < 32;
            if (bad || (e == std::wstring::npos && c != exe)) return fail(L"bad --stage-path " + o.stage_path);
            if (e == std::wstring::npos) break;
            sub += c + L"\\", b = e + 1;
        }
        // the child's and the proxy's GetModuleFileNameW, and CreateProcessW, take at most MAX_PATH
        size_t longest = std::max(exe.size(), wcslen(L"scskiller_warm_times.csv")), full = GetFullPathNameW((flat + sub).c_str(), 0, nullptr, nullptr);
        if (!full || full - 1 + longest >= MAX_PATH)
            err_printf(L"--stage-path too long (%zu characters with the longest file name): staging in %ls\n", full - 1 + longest, flat.c_str());
        else
            for (size_t e = 0; (e = sub.find(L'\\', e)) != std::wstring::npos; ++e)
                dirs.push_back(flat + sub.substr(0, e)), CreateDirectoryW(dirs.back().c_str(), nullptr), stage = dirs.back() + L"\\";
    }
    if (vulkan) return vulkan_warm(o, work, exe, bin, flat, stage, dirs);
    std::vector<std::wstring> layer;  // the --layer files staged (CopyFileW never overwrites a staged input)
    std::vector<std::wstring> agility;  // the --d3d12 files staged, as D3D12\<name>
    auto unput = [&](const std::wstring& name) {
        std::wstring p = stage + name;
        SetFileAttributesW(p.c_str(), FILE_ATTRIBUTE_NORMAL);  // a copy keeps its source's read-only attribute
        DWORD err = DeleteFileW(p.c_str()) ? 0 : GetLastError();
        if (err && err != ERROR_FILE_NOT_FOUND) err_printf(L"removing the staged %ls failed (error %lu)\n", p.c_str(), err);
    };
    struct Unstage {
        std::function<void()> f;
        ~Unstage() { f(); }
    } unstage{[&] {  // only inside this run's folder: the staged inputs go, the proxy's outputs stay in flat
        for (auto n : {exe, std::wstring(L"d3d12.dll"), std::wstring(L"scskiller.db"), std::wstring(L"scskiller_gen.db"), std::wstring(L"scskiller_pass.bin")})
            unput(n);
        for (auto& n : layer) unput(n);
        for (auto& n : agility) unput(n);
        if (!o.d3d11.empty()) unput(L"d3d11.dll");
        if (!o.d3d12.empty() && !RemoveDirectoryW((stage + L"D3D12").c_str()))
            err_printf(L"removing the staged %lsD3D12 failed (error %lu)\n", stage.c_str(), GetLastError());
        DeleteFileW((stage + kItemsFile).c_str());
        if (stage == flat) return;
        for (auto n : {L"scskiller.log", L"scskiller_creates.csv", L"scskiller_warm_times.csv", L"scskiller_failed.keys"})
            MoveFileExW((stage + n).c_str(), (flat + n).c_str(), 0);
        for (auto d = dirs.rbegin(); d != dirs.rend(); ++d) RemoveDirectoryW(d->c_str());  // only if empty
    }};
    auto put = [&](const std::wstring& from, const std::wstring& name, bool optional) {
        std::wstring to = stage + name;
        if (optional && GetFileAttributesW(from.c_str()) == INVALID_FILE_ATTRIBUTES) return true;
        // the generated plan is big and only read: hard link; the recorded db is copied (the proxy may append to it)
        return (name == L"scskiller_gen.db" && CreateHardLinkW(to.c_str(), from.c_str(), nullptr)) || CopyFileW(from.c_str(), to.c_str(), TRUE);
    };
    std::wstring proxy = bin + L"d3d12.dll";
    if (GetFileAttributesW(proxy.c_str()) == INVALID_FILE_ATTRIBUTES) proxy = bin + L"..\\d3d12.dll";  // the segheap\ build uses its parent's
    if (!put(self, exe, false) || !put(proxy, L"d3d12.dll", false) || !put(work + L"scskiller.db", L"scskiller.db", true) ||
        !put(work + L"scskiller_gen.db", L"scskiller_gen.db", true) || !put(work + L"scskiller_pass.bin", L"scskiller_pass.bin", true))
        return fail(L"staging into " + stage + L" failed (error " + std::to_wstring(GetLastError()) + L")");
    if (!o.layer.empty()) {
        // the layer's log stays in the stage, with the proxy's outputs
        std::error_code ec;
        for (auto& e : std::filesystem::directory_iterator(o.layer, ec)) {
            std::wstring n = e.path().filename().wstring(), x = e.path().extension().wstring();
            // the layer's dll, its ini, its add-ons and the dlls they load: never an exe or the game's data
            if (!e.is_regular_file(ec) || (_wcsicmp(x.c_str(), L".dll") && _wcsicmp(x.c_str(), L".ini") && _wcsnicmp(x.c_str(), L".addon", 6))) continue;
            if (!put(e.path().wstring(), n, false)) return fail(L"staging the layer's " + n + L" failed (error " + std::to_wstring(GetLastError()) + L")");
            layer.push_back(n);
            // ReShade reads add-ons and its config from these when set: the game's folder, not the stage
            wchar_t v[8];
            for (auto [sec, key] : {std::pair{L"ADDON", L"AddonPath"}, std::pair{L"INSTALL", L"BasePath"}})
                if (!_wcsicmp(x.c_str(), L".ini") && GetPrivateProfileStringW(sec, key, L"", v, 8, (stage + n).c_str()))
                    return fail(L"the layer's " + n + L" sets [" + sec + L"] " + key + L": its files would come from outside the stage");
        }
        if (ec) return fail(L"--layer " + o.layer + L" can't be listed");
    }
    if (!o.d3d11.empty() && !put(o.d3d11, L"d3d11.dll", false))
        return fail(L"staging the game's d3d11.dll failed (error " + std::to_wstring(GetLastError()) + L")");
    UINT sdk = 0;
    if (!o.d3d12.empty()) {
        CreateDirectoryW((stage + L"D3D12").c_str(), nullptr);
        // the Agility SDK redistributable's DLLs only (1.619's largest is 5 MB), whatever else the folder holds
        for (const wchar_t* n : {L"D3D12Core.dll", L"d3d12SDKLayers.dll", L"D3D12StateObjectCompiler.dll"}) {
            std::wstring from = o.d3d12 + L"\\" + n, to = std::wstring(L"D3D12\\") + n;
            WIN32_FILE_ATTRIBUTE_DATA a;
            if (!GetFileAttributesExW(from.c_str(), GetFileExInfoStandard, &a) || (a.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) ||
                a.nFileSizeHigh || a.nFileSizeLow > 64u << 20)
                continue;
            if (!put(from, to, false)) break;
            agility.push_back(to);
        }
        // read here, not in the child: there a D3D12Core.dll loaded without its references would be the one the loader gets
        HMODULE c = LoadLibraryExW((stage + L"D3D12\\D3D12Core.dll").c_str(), nullptr, DONT_RESOLVE_DLL_REFERENCES);
        auto v = c ? (const UINT*)GetProcAddress(c, "D3D12SDKVersion") : nullptr;
        DWORD err = v ? 0 : GetLastError();
        if (v) sdk = *v;
        if (c) FreeLibrary(c);
        if (!sdk) err_printf(L"staging the game's D3D12 runtime from %ls failed (error %lu): the system's runs\n", o.d3d12.c_str(), err);
    }

    struct Unregister {
        bool on = false;
        ~Unregister() {
            if (on && RegDeleteTreeW(HKEY_CURRENT_USER, asd_key().c_str())) err_printf(L"removing the delivery's registration for the staged exe failed\n");
        }
    } unregister;
    if (!o.asd_psdbs.empty()) {
        std::wstring why = asd_register(o, stage + exe, exe);
        unregister.on = why.empty();
        if (!why.empty()) err_printf(L"the compile runs without the game's Advanced Shader Delivery, so it compiles what the delivery holds too: %ls\n", why.c_str());
    }

    const DWORD me = GetCurrentProcessId();
    HANDLE map = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(LONG), beat_name(me).c_str());
    auto beat = map ? (volatile LONG*)MapViewOfFile(map, FILE_MAP_WRITE, 0, 0, sizeof(LONG)) : nullptr;
    if (!beat) return fail(L"heartbeat setup failed");
    // The child sets this after its last line; created before the child starts, so it can't miss it. A process whose driver
    // was poisoned (a thread stuck in it) may never finish exiting: after SCSKILLER_WARM_EXIT_S (default 600 s: a big cache
    // flush at exit takes minutes) it is terminated, and whatever it hadn't written of the driver cache is lost (ARCHITECTURE.md).
    HANDLE final_event = CreateEventW(nullptr, TRUE, FALSE, final_name(me).c_str());
    // set by the child once its device exists: a layer's code (--layer) runs before that, and may hang
    HANDLE started = CreateEventW(nullptr, TRUE, FALSE, started_name(me).c_str());
    HANDLE loading = CreateEventW(nullptr, TRUE, FALSE, loading_name(me).c_str());  // set while the child reads the dbs
    // ReShade reads these before the child runs: RESHADE_BASE_PATH_OVERRIDE would load its config and add-ons from elsewhere
    if (wchar_t* env = GetEnvironmentStringsW()) {
        std::vector<std::wstring> reshade;
        for (wchar_t* e = env; *e; e += wcslen(e) + 1)
            if (!_wcsnicmp(e, L"RESHADE_", 8) && wcschr(e, L'=')) reshade.emplace_back(e, wcschr(e, L'='));
        FreeEnvironmentStringsW(env);
        for (auto& n : reshade) SetEnvironmentVariableW(n.c_str(), nullptr);
    }
    // the stop file and the alive file stop the child through the stop event: this process's own unless the caller named one
    HANDLE stop = o.stop_file.empty() && o.alive_file.empty() ? nullptr : CreateEventW(nullptr, TRUE, FALSE, (o.stop_event.empty() ? stop_name(me) : o.stop_event).c_str());
    if (!stop && (!o.stop_file.empty() || !o.alive_file.empty())) return fail(L"stop event setup failed (error " + std::to_wstring(GetLastError()) + L")");
    auto stop_file = [&] { return !o.stop_file.empty() && GetFileAttributesW(o.stop_file.c_str()) != INVALID_FILE_ATTRIBUTES; };
    for (;;) {  // twice when the game's D3D12 runtime makes no device: then on the system's
        std::wstring args = L"--child " + std::to_wstring(me);
        for (int i = 3; i + 1 < argc; i += 2)
            if (!host_only(argv[i])) args += L" " + quoted(argv[i]) + L" " + quoted(argv[i + 1]);
        if (stop && o.stop_event.empty()) args += L" --stop-event " + quoted(stop_name(me));
        if (sdk) args += L" --sdk " + std::to_wstring(sdk);
        if (stop_file()) SetEvent(stop);
        DeleteFileW((stage + kItemsFile).c_str());  // a child that dies before its items leaves none to go by
        if (loading) ResetEvent(loading);  // a child that died reading the dbs left it set
        fflush(stdout);
        // joined on every return: a joinable std::thread's destructor calls std::terminate, which fails fast (0xC0000409)
        struct Relays : std::vector<std::thread> {
            void join() {
                for (auto& t : *this) {  // a child that never connected leaves its relay in ConnectNamedPipe
                    if (WaitForSingleObject(t.native_handle(), 1000) == WAIT_TIMEOUT) CancelSynchronousIo(t.native_handle());
                    t.join();
                }
                clear();
            }
            ~Relays() { join(); }
        } relays;
        PROCESS_INFORMATION pi = {};
        if (!o.package.empty()) {
            for (int fd : {1, 2}) {
                HANDLE p = CreateNamedPipeW(pipe_name(me, fd).c_str(), PIPE_ACCESS_INBOUND, PIPE_TYPE_BYTE | PIPE_WAIT, 1, 0, 1 << 16, 0, nullptr);
                if (p == INVALID_HANDLE_VALUE) return fail(L"output pipe setup failed (error " + std::to_wstring(GetLastError()) + L")");
                relays.emplace_back([p, out = GetStdHandle(fd == 1 ? STD_OUTPUT_HANDLE : STD_ERROR_HANDLE)] {
                    char buf[4096];
                    DWORD n, w;
                    if (ConnectNamedPipe(p, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED)
                        while (ReadFile(p, buf, sizeof buf, &n, nullptr) && n) WriteFile(out, buf, n, &w, nullptr);
                    CloseHandle(p);
                });
            }
            IDesktopAppXActivator* act = nullptr;
            HANDLE h = nullptr;
            CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            HRESULT hr = CoCreateInstance(CLSID_DesktopAppXActivator, nullptr, CLSCTX_ALL, __uuidof(IDesktopAppXActivator), (void**)&act);
            // 0x24: DAXAO_NONPACKAGED_EXE_PROCESS_TREE | DAXAO_CENTENNIAL_PROCESS (Invoke-CommandInDesktopPackage's, minus its App Installer check)
            if (SUCCEEDED(hr))
                hr = act->ActivateWithOptionsArgsWorkingDirectoryShowWindow(o.package.c_str(), (stage + exe).c_str(), args.c_str(), 0x24, 0, nullptr, stage.c_str(), SW_HIDE, &h);
            if (act) act->Release();
            if (SUCCEEDED(hr) && h) {
                pi.dwProcessId = GetProcessId(h);
                CloseHandle(h);
                pi.hProcess = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE | PROCESS_SET_QUOTA, FALSE, pi.dwProcessId);
                // the activator's process isn't in our caller's job: it dies with this process instead
                HANDLE job = CreateJobObjectW(nullptr, nullptr);
                JOBOBJECT_EXTENDED_LIMIT_INFORMATION li = {};
                li.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                if (pi.hProcess && (!job || !SetInformationJobObject(job, JobObjectExtendedLimitInformation, &li, sizeof li) || !AssignProcessToJobObject(job, pi.hProcess)))
                    TerminateProcess(pi.hProcess, 1), pi.hProcess = nullptr;
            }
            if (!pi.hProcess) err_printf(L"running in the package %ls failed (0x%08X): warming under the exe name only\n", o.package.c_str(), (unsigned)hr);
        }
        if (!pi.hProcess) {
            std::wstring cmd = L"\"" + stage + exe + L"\" " + args;
            STARTUPINFOW si = {sizeof si};
            si.dwFlags = STARTF_USESTDHANDLES;
            si.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE), si.hStdError = GetStdHandle(STD_ERROR_HANDLE);
            for (HANDLE h : {si.hStdOutput, si.hStdError}) SetHandleInformation(h, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT);
            if (!CreateProcessW(nullptr, cmd.data(), nullptr, nullptr, TRUE, o.idle ? IDLE_PRIORITY_CLASS : 0, nullptr, stage.c_str(), &si, &pi))
                return fail(L"launching the staged exe failed (error " + std::to_wstring(GetLastError()) + L")");
        }
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);  // a starved heartbeat would read as a pause
        wchar_t lim[16] = {};
        // counted in this loop's turns, not wall time: the caller suspends this process to pause the warm
        uint64_t exit_turns = 10ull * (GetEnvironmentVariableW(L"SCSKILLER_WARM_EXIT_S", lim, 16) ? std::max(1, _wtoi(lim)) : 600), final_turn = 0;
        uint64_t start_turns = 10ull * (GetEnvironmentVariableW(L"SCSKILLER_WARM_START_S", lim, 16) ? _wtoi(lim) : 180), turns = 0, waited = 0;
        bool killed = false, stuck = false, orphaned = false;
        FILETIME alive = {};
        uint64_t alive_turn = 0;  // the turn the alive file's write time last changed
        while (WaitForSingleObject(pi.hProcess, 100) == WAIT_TIMEOUT) {
            InterlockedIncrement(beat);
            ++turns;
            if (!loading || WaitForSingleObject(loading, 0) == WAIT_TIMEOUT) ++waited;
            if (waited > start_turns && started && WaitForSingleObject(started, 0) == WAIT_TIMEOUT) {
                TerminateProcess(pi.hProcess, 1), stuck = true;
                WaitForSingleObject(pi.hProcess, 5000);
                break;
            }
            if (final_event && !final_turn && WaitForSingleObject(final_event, 0) == WAIT_OBJECT_0) final_turn = turns;
            if (final_turn && turns - final_turn > exit_turns) {
                err_printf(L"the warm process did not exit %llu s after its last line: terminated (its unwritten driver cache is lost)\n", exit_turns / 10);
                TerminateProcess(pi.hProcess, 3), killed = true;
                WaitForSingleObject(pi.hProcess, 5000);
                break;
            }
            if (!o.alive_file.empty()) {
                WIN32_FILE_ATTRIBUTE_DATA a;
                FILETIME t = GetFileAttributesExW(o.alive_file.c_str(), GetFileExInfoStandard, &a) ? a.ftLastWriteTime : FILETIME{};
                if (CompareFileTime(&t, &alive)) alive = t, alive_turn = turns;
            }
            bool stale = o.alive_turns && turns - alive_turn >= (uint64_t)o.alive_turns;
            if (stale || stop_file()) SetEvent(stop);
            // stopping, it finishes or abandons its calls within seconds; after its last line it is writing the driver cache
            if (stale && !final_turn && turns - alive_turn >= (uint64_t)o.alive_turns + exit_turns && !(final_event && WaitForSingleObject(final_event, 0) == WAIT_OBJECT_0)) {
                TerminateProcess(pi.hProcess, 1), orphaned = true;
                WaitForSingleObject(pi.hProcess, 5000);
                break;
            }
        }
        relays.join();
        if (stuck)
            return fail(L"the warm process didn't start replaying within " + std::to_wstring(start_turns / 10) + L" s" +
                        (o.layer.empty() ? L"" : L" (an add-on of the game's layer may hang outside the game)") + L", so it was ended");
        if (orphaned)
            return fail(L"the alive file " + o.alive_file + L" stayed unchanged for " + std::to_wstring(((uint64_t)o.alive_turns + exit_turns) / 10) + L" s: ended the warm process");
        DWORD code = 1;
        GetExitCodeProcess(pi.hProcess, &code);
        if (killed) return 3;  // its last line was printed; the caller goes by it
        if (code == EXIT_SYSTEM_RUNTIME && sdk) {
            CloseHandle(pi.hProcess);
            if (pi.hThread) CloseHandle(pi.hThread);
            sdk = 0;
            continue;
        }
        wchar_t hex[16];
        swprintf_s(hex, L"0x%08X", code);
        // 3 is a retry only after the child's last line: abort() exits with 3 too
        bool last_line = final_event && WaitForSingleObject(final_event, 0) == WAIT_OBJECT_0;
        // a crash after the last line is in teardown (a layer's add-on unloading): the line stands, as for a kill
        if (last_line && code > 3) {
            err_printf(L"the warm process crashed while closing (exit code %ls), after its last item: its result stands\n", hex);
            return 0;  // the caller goes by the line: done, retry or error
        }
        if (code > 1 && (code != 3 || !last_line)) {  // it printed no error line
            if (retry_after_death(stage + kItemsFile, pi.dwProcessId, o, hex, turns / 10.0)) return 3;
            return fail(L"the warm process died (exit code " + std::wstring(hex) + L")");
        }
        return (int)code;
    }
}

int wmain(int argc, wchar_t** argv) {
    // --vulkan takes no value: out of the pairs parse() and the --out scan read
    bool vulkan = false;
    std::vector<wchar_t*> args(argv, argv + argc);
    std::erase_if(args, [&](wchar_t* a) {
        bool v = !wcscmp(a, L"--vulkan");
        vulkan |= v;
        return v;
    });
    argc = (int)args.size(), argv = args.data();
    if (argc >= 3 && !wcscmp(argv[1], L"--child")) {  // --child <parent process id> <options>
        Opts o;
        return parse(argc, argv, 3, o) ? child((DWORD)wcstoul(argv[2], nullptr, 10), o) : fail(L"bad child arguments");
    }
    bool out = false;
    int code = -1;
    // first: a bad command line's error goes there too. --out before --err: a failed --err is reported in --out
    for (int fd : {1, 2})
        for (int i = 3; code < 0 && i + 1 < argc; i += 2) {
            if (wcscmp(argv[i], fd == 1 ? L"--out" : L"--err")) continue;
            SECURITY_ATTRIBUTES sa = {sizeof sa, nullptr, TRUE};  // the child writes through the same handles
            HANDLE h = CreateFileW(argv[i + 1], GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, &sa, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (h == INVALID_HANDLE_VALUE) {
                code = fail(std::wstring(L"can't create ") + argv[i + 1] + L" (error " + std::to_wstring(GetLastError()) + L")");
                break;
            }
            SetStdHandle(fd == 1 ? STD_OUTPUT_HANDLE : STD_ERROR_HANDLE, h);
            _dup2(_open_osfhandle((intptr_t)h, _O_WRONLY), fd);
            out = out || fd == 1;
        }
    if (code < 0) code = parent(argc, argv, vulkan);
    if (out) emit("{\"event\":\"exit\",\"code\":%d}", code);
    return code;
}
