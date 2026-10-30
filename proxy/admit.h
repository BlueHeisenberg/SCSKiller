// The recorder's admission (the D3D12 proxy's and the Vulkan layer's): which process records, read from the exe's
// scskiller.ini, the install and Steam's files. The includer defines admit_dir(): the folder of the exe and its ini.
#pragma once
#include <windows.h>
#include <shellapi.h>
#include <aclapi.h>
#include <winternl.h>
#include <share.h>
#include <io.h>
#include <algorithm>
#include <cerrno>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>
#include "recorder_key.h"
#include "admission.h"

#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "advapi32.lib")  // RegGetValueW: Steam's folder (vac_now)
#pragma comment(lib, "shell32.lib")   // SHGetKnownFolderPath (recorder_dir), CommandLineToArgvW (vac_now)

static const std::wstring& admit_dir();

static long long unix_ms() { return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count(); }
// An environment variable into v; false when unset, empty or too long for v (the call then leaves v unwritten).
template <size_t N> static bool env(const wchar_t* name, wchar_t (&v)[N]) {
    DWORD n = GetEnvironmentVariableW(name, v, N);
    return n && n < N;
}
// A module's path as the loader reports it, at any length; "" when it can't be had or passes 32,768 characters.
static std::wstring module_path(HMODULE m) {
    for (DWORD size = MAX_PATH;; size = std::min<DWORD>(size * 2, 32768)) {
        std::wstring s(size, L'\0');
        const DWORD n = GetModuleFileNameW(m, s.data(), size);
        if (!n) return L"";
        if (n < size) return s.resize(n), s;
        if (size == 32768) return L"";
    }
}
// Admission, decided once at the first device (any path: D3D12CreateDevice, a device factory, a D3D11 device), or earlier
// when D3D12GetInterface hands out an SDK configuration or a device factory, before either is hooked. The recorder records
// for the whole run only when all hold, else it is a pure pass-through (no device hooked, nothing written in the game
// folder):
//  - scskiller.ini's exe_size= and exe_time= are this process's exe as it is now, and its sig= is the HMAC the app made of
//    them, the exe's file key, root= (and pid=/pid_time=) with its own key (recorder.key in its recorder folder, outside
//    every game folder; recorder_sig). The app takes the stamp before a full anti-cheat check of the install
//    (GameFiles.DetectAntiCheat walks it; this dll doesn't) and signs it only when that check is clean, so an exe updated
//    since, or during it, is refused, and so is an ini that SCSKiller didn't write, or wrote for another exe or root;
//  - no anti-cheat marker in this folder or in any folder above it up to the install root (root=, relative to this folder;
//    more than 64 up refuses), each listed whole: the built-in list, plus scskiller.ini markers= (it only adds; malformed:
//    no). pid= and pid_time= (its creation FILETIME, UTC; under Wine its Linux pid and /proc start time, wine_bound) name
//    the one process the app started suspended itself for an offline session: for that process EasyAntiCheat's names
//    leave the list; any other process with that ini is refused;
//  - no "Riot Games" folder in the exe's path;
//  - no anti-cheat client module loaded, and not a VAC game by Steam's files (vac_now);
//  - this dll was loaded from the exe's folder;
//  - this folder isn't on the app's revocation list (recorder.revoked: folders whose scskiller.ini it couldn't take out);
//  - scskiller.ini is still as it was read when the decision is made (the app takes it out when it finds anti-cheat).
// risk=1 with that binding, inside the signed stamp (a session the user confirmed at their own risk, beside the anti-cheat):
// the marker, Riot, client and VAC checks still run but refuse on nothing they find, and the log names it all once, as it
// opens. Nothing is hidden from the anti-cheat. risk= without pid= or with another value is refused ("risk= malformed").
// What a check couldn't inspect (a folder that can't be listed, Steam files that can't be read) refuses as without risk.
// Decided early at D3D12GetInterface, the same verdict runs again at the first device, and every device waits for it: a run
// it refuses becomes a pass-through there, before any device hook, with the SDK configuration's and the factories' slots
// put back (one another hook took meanwhile stays, forwarding through ours, and the refused line says so). Such a run has
// already opened scskiller.log, which then ends with that refusal and records nothing after it. A thread already in one of
// those hooks, or entering one later, forwards: the originals it reads stay valid and this dll stays loaded (pin_self).
// Inside a DllMain chain (this thread holds the loader lock) D3D12GetInterface decides and hooks nothing: a device factory
// got there isn't recorded, nor one whose vtable lies outside every module (a wrapper's heap copy), which couldn't be put back,
// nor one from a second SDK configuration vtable (only one is tracked for the put-back).
// Accepted limits: a client that loads later in an admitted run isn't caught here, under risk or not (the app removes the
// recorder at its next check); anti-cheat an update adds deeper in the install than those listings, without changing the
// exe, is admitted until the app's next check (every 2 minutes while it runs, at its start otherwise) takes the recorder out.
static std::string utf8(const std::wstring& w) {
    std::string s(WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), nullptr, 0, nullptr, nullptr), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), s.data(), (int)s.size(), nullptr, nullptr);
    return s;
}
// The anti-cheat client modules loaded by now, by name.
static std::vector<std::string> anti_cheat_loaded() {
    std::vector<std::string> found;
    for (auto m : {L"EasyAntiCheat_x64.dll", L"EasyAntiCheat_EOS.dll", L"BEClient_x64.dll", L"BEClient.dll", L"mhypbase.dll",
                   L"core_ets2mp.dll", L"core_atsmp.dll", L"denuvo-anti-cheat-runtime.dll"})
        if (GetModuleHandleW(m)) found.push_back(utf8(m));
    return found;
}
// The exe's path has a "Riot Games" folder (GameFiles.RiotGames): the Riot Client installs every Riot title under one.
static bool riot_games() {
    std::wstring exe = module_path(nullptr);
    if (exe.empty()) return true;
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
    DWORD n = GetPrivateProfileStringW(L"scskiller", L"markers", L"", v, 4096, (admit_dir() + L"scskiller.ini").c_str());
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
// The markers among dir's entries, by name (at most 100,000 read): all of them, or the first. {"?"}: more entries than that,
// or a listing that fails before it ends.
static std::vector<std::string> anti_cheat_in(const std::wstring& dir, const std::vector<std::wstring>& markers, bool all) {
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileExW(long_path(dir + L"*").c_str(), FindExInfoBasic, &fd, FindExSearchNameMatch, nullptr, FIND_FIRST_EX_LARGE_FETCH);
    if (h == INVALID_HANDLE_VALUE) return {"?"};
    std::vector<std::string> found;
    bool listed = true;
    for (size_t read = 0;;) {
        if (++read > 100000) { listed = false; break; }
        if (is_marker(fd.cFileName, markers)) {
            found.push_back(utf8(fd.cFileName));
            if (!all) break;
        }
        if (!FindNextFileW(h, &fd)) { listed = GetLastError() == ERROR_NO_MORE_FILES; break; }
    }
    FindClose(h);
    if (!listed) found.assign(1, "?");
    return found;
}
// While admission decides, ini_string reads this copy of scskiller.ini's bytes (the ones it compares again at the end), so
// the values it checks and the signed lines it hands on come from one read: a file changed and put back meanwhile can't
// split them (admission.h's ini_value).
static thread_local const std::string* t_ini;

// scskiller.ini's value of key, as written ("" when missing or longer than 1022).
static std::wstring ini_string(const wchar_t* key) {
    if (t_ini) {
        std::wstring v = ini_value(*t_ini, key);
        return v.size() < 1022 ? v : L"";
    }
    wchar_t v[1024];
    DWORD n = GetPrivateProfileStringW(L"scskiller", key, L"", v, 1024, (admit_dir() + L"scskiller.ini").c_str());
    return n && n < 1022 ? std::wstring(v, n) : L"";
}
static bool ini_number(const wchar_t* key, uint64_t& out) {
    std::wstring v = ini_string(key);
    if (v.empty() || v.size() >= 30 || !iswdigit(v[0])) return false;
    wchar_t* end;
    errno = 0;
    out = wcstoull(v.c_str(), &end, 10);
    return !*end && !errno;
}
// exe_size= and exe_time= (FILETIME, UTC) are this process's exe as it is now; missing or malformed: false.
static bool exe_as_checked() {
    const std::wstring exe = module_path(nullptr);
    WIN32_FILE_ATTRIBUTE_DATA a;
    uint64_t size, time;
    return !exe.empty() && GetFileAttributesExW(exe.c_str(), GetFileExInfoStandard, &a) && ini_number(L"exe_size", size) && ini_number(L"exe_time", time)
           && size == ((uint64_t)a.nFileSizeHigh << 32 | a.nFileSizeLow) && time == ((uint64_t)a.ftLastWriteTime.dwHighDateTime << 32 | a.ftLastWriteTime.dwLowDateTime);
}
// The install root, with a trailing backslash; "" when root= is missing. root= is relative to here, this folder's final_dir
// (ASCII "..\..", which the ini's ANSI read keeps), or absolute when the root is on another drive.
static std::wstring install_root(const std::wstring& here) {
    std::wstring v = ini_string(L"root");
    if (v.empty()) return L"";
    std::wstring p = (v.size() > 1 && (v[1] == L':' || (v[0] == L'\\' && v[1] == L'\\'))) ? v : here + v;
    std::wstring full(32768, L'\0');
    DWORD m = GetFullPathNameW(p.c_str(), (DWORD)full.size(), full.data(), nullptr);
    if (!m || m >= full.size()) return L"";
    full.resize(m);
    return full.back() == L'\\' ? full : full + L'\\';
}
// here and each folder above it up to the install root; the root alone besides here when it isn't above it. Empty when
// the root is more than 64 folders up: not all can be listed.
static std::vector<std::wstring> folders_to(const std::wstring& here, const std::wstring& root) {
    if (here.size() < root.size() || _wcsnicmp(here.c_str(), root.c_str(), root.size())) return {here, root};
    std::vector<std::wstring> dirs;
    for (std::wstring d = here; dirs.size() < 64;) {
        dirs.push_back(d);
        if (d.size() <= root.size()) return dirs;
        d.resize(d.find_last_of(L'\\', d.size() - 2) + 1);
    }
    return {};
}
// <recorder folder>\refused\<this exe's file_key>; "" when it can't be had.
static std::wstring refused_path() {
    const std::wstring exe = module_path(nullptr), dir = recorder_dir(admit_dir());
    return exe.empty() || dir.empty() ? L"" : dir + L"\\refused\\" + file_key(exe);
}
// A pass-through writes nothing in the game folder: its reason goes to the app's recorder folder (refused_path, "unix_ms
// why"), where the app shows it on the game's page; an admitted run deletes it.
static void refused(const char* why) {
    const std::wstring file = refused_path();
    if (file.empty()) return;
    if (!why) return (void)DeleteFileW(file.c_str());
    // a PC the app never stamped a game on may have neither folder yet
    const std::wstring dir = file.substr(0, file.find_last_of(L'\\'));
    CreateDirectoryW(dir.substr(0, dir.find_last_of(L'\\')).c_str(), nullptr), CreateDirectoryW(dir.c_str(), nullptr);
    HANDLE h = CreateFileW(file.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;
    char b[1024];
    int n = snprintf(b, sizeof b, "%lld %s", unix_ms(), why);
    DWORD w;
    WriteFile(h, b, (DWORD)std::clamp(n, 0, (int)sizeof b - 1), &w, nullptr);
    CloseHandle(h);
}
// Valve Anti-Cheat (Games.Vac): VAC runs in Steam's client and bans for a foreign DLL in the game, and no file of the game
// says so. Decided here at each launch, from Steam's own files as they are now (the app may have been closed while Steam
// added VAC): an exe in a Steam library is the app whose appmanifest names its folder, and it records only when that app
// isn't one of Valve's VAC titles and appinfo.vdf has its info without the "Valve Anti-Cheat enabled" category (category_8).
// Anything that can't be read or found refuses. An exe outside every Steam library isn't Steam's: no VAC, unless a
// steam_appid.txt between it and the install root names a Steam app (Steamworks starts the game as that app). One naming an
// app Steam has no info about (named_vac) refuses only outside a risk session.
static std::string read_all(const std::wstring& path, size_t cap = 1 << 20) {
    std::string s;
    if (FILE* f = _wfsopen(path.c_str(), L"rb", _SH_DENYNO)) {
        char b[65536];
        for (size_t n; s.size() < cap && (n = fread(b, 1, sizeof b, f));) s.append(b, n);
        fclose(f);
    }
    return s;
}
// UTF-8 as Steam writes it; "" when it isn't valid UTF-8.
static std::wstring widen(const std::string& s) {
    const int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s.data(), (int)s.size(), nullptr, 0);
    std::wstring w(n, L'\0');
    if (n) MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s.data(), (int)s.size(), w.data(), n);
    return w;
}
static bool under(const std::wstring& path, std::wstring dir) {
    for (auto& c : dir) if (c == L'/') c = L'\\';
    while (!dir.empty() && dir.back() == L'\\') dir.pop_back();
    return path.size() > dir.size() + 1 && path[dir.size()] == L'\\' && !_wcsnicmp(path.c_str(), dir.c_str(), dir.size());
}
// A failed call's error, never ERROR_SUCCESS.
static DWORD last_error() {
    const DWORD e = GetLastError();
    return e ? e : ERROR_GEN_FAILURE;
}
// Windows' ENOENT (the file, or a folder on its way, isn't there), or a drive without its medium (an unplugged USB drive,
// an empty card reader): neither can hold the running exe.
static bool absent(DWORD e) { return e == ERROR_FILE_NOT_FOUND || e == ERROR_PATH_NOT_FOUND || e == ERROR_NOT_READY; }
static bool absolute(const std::wstring& p) {
    return (p.size() >= 3 && (p[0] | 0x20) >= L'a' && (p[0] | 0x20) <= L'z' && p[1] == L':' && p[2] == L'\\') || (p.size() > 2 && p[0] == L'\\' && p[1] == L'\\');
}
// Two names or paths alike as Windows compares them (any case).
static bool same_name(const std::wstring& a, const std::wstring& b) { return CompareStringOrdinal(a.c_str(), (int)a.size(), b.c_str(), (int)b.size(), TRUE) == CSTR_EQUAL; }
// A \\?\ or \\.\ path as its plain form (\\?\C:\x as C:\x, \\?\UNC\s\x as \\s\x), any other as it is.
static std::wstring plain_path(const std::wstring& p) {
    for (const wchar_t* pre : {L"\\\\?\\UNC\\", L"\\\\.\\UNC\\"})
        if (!_wcsnicmp(p.c_str(), pre, 8)) return L"\\\\" + p.substr(8);
    for (const wchar_t* pre : {L"\\\\?\\", L"\\\\.\\"})
        if (!p.compare(0, 4, pre) && p.size() >= 6 && p[5] == L':') return p.substr(4);
    return p;
}
// Backslashed, without a trailing one (a drive's root keeps it).
static std::wstring trimmed(std::wstring p) {
    for (auto& c : p) if (c == L'/') c = L'\\';
    while (p.size() > 3 && p.back() == L'\\') p.pop_back();
    return p.size() == 2 && p[1] == L':' ? p + L'\\' : p;
}
static std::wstring join(const std::wstring& dir, const wchar_t* name) { return dir + (dir.back() == L'\\' ? L"" : L"\\") + name; }
// An absolute path as text: GetFullPathNameW's ('/', "." and ".." folded, no link followed); "" when it can't be had.
static std::wstring full_path(const std::wstring& p) {
    std::wstring full(p.empty() ? 0 : MAX_PATH, L'\0');
    for (int tries = 0; tries < 2 && !p.empty(); ++tries) {
        const DWORD m = GetFullPathNameW(p.c_str(), (DWORD)full.size(), full.data(), nullptr);
        if (!m || m > 32768) break;
        if (m < full.size()) return full.resize(m), full;
        full.resize(m);
    }
    return L"";
}
// path is dir or in it as text (plain_path, full_path, any case): a folder that isn't there can't be checked another way.
// A dir that can't be made absolute holds every path.
static bool holds_text(const std::wstring& dir, const std::wstring& path) {
    if (path.empty()) return false;
    std::wstring d = full_path(plain_path(dir)), p = full_path(plain_path(path));
    if (d.empty() || p.empty()) return true;
    if (d.back() != L'\\') d += L'\\';
    if (p.back() != L'\\') p += L'\\';
    return p.size() >= d.size() && same_name(p.substr(0, d.size()), d);
}
// A path's final path (final_dir's, without its trailing backslash but a drive's) in r: ERROR_SUCCESS, else why not.
static DWORD real_of(const std::wstring& path, std::wstring& r) {
    r.clear();
    HANDLE f = CreateFileW(long_path(path).c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    if (f == INVALID_HANDLE_VALUE) return last_error();
    std::wstring final(MAX_PATH, L'\0');
    DWORD err = ERROR_FILENAME_EXCED_RANGE;
    for (int tries = 0; tries < 2; ++tries) {
        const DWORD m = GetFinalPathNameByHandleW(f, final.data(), (DWORD)final.size(), 0);
        if (!m) { err = last_error(); break; }
        if (m > 32768) break;
        if (m < final.size()) { final.resize(m), err = ERROR_SUCCESS; break; }
        final.resize(m);
    }
    CloseHandle(f);
    if (err) return err;
    r = trimmed(final.rfind(L"\\\\?\\UNC\\", 0) == 0 ? L"\\\\" + final.substr(8) : final.rfind(L"\\\\?\\", 0) == 0 ? final.substr(4) : final);
    return ERROR_SUCCESS;
}
// A folder's identity, to tell two library paths apart: its volume serial and file id, and its final path (two volumes of
// one serial, as a cloned disk, can repeat an id). "" when either can't be had, and under Wine, whose id is an inode
// without its device.
static std::wstring folder_key(const std::wstring& path) {
    if (under_wine()) return L"";
    HANDLE f = CreateFileW(long_path(path).c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    if (f == INVALID_HANDLE_VALUE) return L"";
    FILE_ID_INFO id;
    const bool ok = GetFileInformationByHandleEx(f, FileIdInfo, &id, sizeof id);
    CloseHandle(f);
    std::wstring r;
    return ok && !real_of(path, r) ? id_key(id, false) + L"|" + r : L"";
}
// A file's bytes, read whole: ERROR_SUCCESS, else why not (the open's or a read's error, a file that isn't one on disk,
// more than cap bytes).
static DWORD read_whole(const std::wstring& path, std::string& s, size_t cap = 1 << 20) {
    s.clear();
    HANDLE h = CreateFileW(long_path(path).c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
    if (h == INVALID_HANDLE_VALUE) return last_error();
    DWORD err = GetFileType(h) == FILE_TYPE_DISK ? ERROR_SUCCESS : ERROR_BAD_FILE_TYPE;
    char b[65536];
    for (DWORD n; !err;) {
        if (!ReadFile(h, b, sizeof b, &n, nullptr)) err = last_error();
        else if (!n) break;
        else if (s.size() + n > cap) err = ERROR_FILE_TOO_LARGE;
        else s.append(b, n);
    }
    CloseHandle(h);
    return err;
}
// The user owns the open folder and its DACL lets no one else but SYSTEM write (under Wine: its Unix owner and other users' bits).
static bool users_own(HANDLE folder) {
    HANDLE token;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return false;
    alignas(TOKEN_USER) BYTE user[sizeof(TOKEN_USER) + SECURITY_MAX_SID_SIZE];
    DWORD n;
    const bool got = GetTokenInformation(token, TokenUser, user, sizeof user, &n);
    CloseHandle(token);
    PSID owner = nullptr;
    PACL dacl = nullptr;
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (!got || GetSecurityInfo(folder, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION, &owner, nullptr, &dacl, nullptr, &sd)) return false;
    const PSID me = reinterpret_cast<TOKEN_USER*>(user)->User.Sid;
    constexpr ACCESS_MASK write = FILE_ADD_FILE | FILE_ADD_SUBDIRECTORY | FILE_DELETE_CHILD | DELETE | WRITE_DAC | WRITE_OWNER | GENERIC_WRITE | GENERIC_ALL;
    bool mine = owner && dacl && EqualSid(owner, me);   // a null DACL lets everyone in
    for (DWORD i = 0; mine && i < dacl->AceCount; ++i) {
        void* p;
        if (!GetAce(dacl, i, &p)) {
            mine = false;
            break;
        }
        const auto* ace = static_cast<ACCESS_ALLOWED_ACE*>(p);
        if (ace->Header.AceFlags & INHERIT_ONLY_ACE || ace->Header.AceType == ACCESS_DENIED_ACE_TYPE) continue;
        const PSID sid = (PSID)&ace->SidStart;
        // an object or callback ACE isn't read here
        mine = ace->Header.AceType == ACCESS_ALLOWED_ACE_TYPE && (!(ace->Mask & write) || EqualSid(sid, me) || IsWellKnownSid(sid, WinLocalSystemSid));
    }
    LocalFree(sd);
    return mine;
}
// /proc/<pid>/<what> read whole through Wine's DOS name for it.
static DWORD proc_read(uint64_t pid, const char* what, std::string& s, size_t cap) {
    using DosName = WCHAR*(CDECL*)(LPCSTR);
    const auto dos = reinterpret_cast<DosName>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "wine_get_dos_file_name"));
    char unix_name[64];
    snprintf(unix_name, sizeof unix_name, "/proc/%llu/%s", (unsigned long long)pid, what);
    WCHAR* name = dos ? dos(unix_name) : nullptr;
    if (!name) return ERROR_PATH_NOT_FOUND;
    const std::wstring path = name;
    HeapFree(GetProcessHeap(), 0, name);
    return read_whole(path, s, cap);
}
// Under Wine pid= and pid_time= are the Linux process's (Core reads /proc), and /proc/self is the wineserver's, which opens
// every file: a file only this process maps, under a random name, shows in /proc/<pid>/maps. nullptr: bound, else why not.
static const char* wine_bound(uint64_t pid, uint64_t at) {
    struct Held {
        HANDLE folder = INVALID_HANDLE_VALUE, file = INVALID_HANDLE_VALUE, map = nullptr;
        const void* view = nullptr;
        ~Held() {
            if (view) UnmapViewOfFile(view);
            if (map) CloseHandle(map);
            if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
            if (folder != INVALID_HANDLE_VALUE) CloseHandle(folder);
        }
    } h;
    const char* const unchecked = "the binding couldn't be checked";
    std::wstring dir(MAX_PATH + 1, L'\0');
    const DWORD n = GetTempPathW(MAX_PATH + 1, dir.data());
    if (!n || n > MAX_PATH) return unchecked;
    dir.resize(n);
    h.folder = CreateFileW(long_path(dir).c_str(), READ_CONTROL | FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    BY_HANDLE_FILE_INFORMATION info;
    if (h.folder == INVALID_HANDLE_VALUE || !GetFileInformationByHandle(h.folder, &info) || !(info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) return unchecked;
    if (!users_own(h.folder)) return "the temp folder isn't the user's own";
    UCHAR r[16];
    if (BCryptGenRandom(nullptr, r, sizeof r, BCRYPT_USE_SYSTEM_PREFERRED_RNG)) return unchecked;
    std::wstring name = L"scskiller-bind-";
    for (UCHAR c : r) name += L"0123456789abcdef"[c >> 4], name += L"0123456789abcdef"[c & 15];
    name += L".tmp";
    // made new in the folder the handle holds, whatever its path names by now (CREATE_NEW: FILE_CREATE)
    const auto create = reinterpret_cast<decltype(&NtCreateFile)>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtCreateFile"));
    UNICODE_STRING relative{USHORT(name.size() * sizeof(WCHAR)), USHORT(name.size() * sizeof(WCHAR)), name.data()};
    OBJECT_ATTRIBUTES oa;
    InitializeObjectAttributes(&oa, &relative, OBJ_CASE_INSENSITIVE, h.folder, nullptr);
    IO_STATUS_BLOCK io;
    HANDLE file = INVALID_HANDLE_VALUE;
    if (!create || create(&file, GENERIC_READ | GENERIC_WRITE | DELETE | SYNCHRONIZE, &oa, &io, nullptr, FILE_ATTRIBUTE_TEMPORARY, 0, FILE_CREATE,
                          FILE_NON_DIRECTORY_FILE | FILE_SYNCHRONOUS_IO_NONALERT | FILE_DELETE_ON_CLOSE, nullptr, 0) < 0)
        return unchecked;
    h.file = file;
    DWORD wrote = 0;
    if (!WriteFile(h.file, "s", 1, &wrote, nullptr) || wrote != 1) return unchecked;
    h.map = CreateFileMappingW(h.file, nullptr, PAGE_READONLY, 0, 0, nullptr);
    h.view = h.map ? MapViewOfFile(h.map, FILE_MAP_READ, 0, 0, 0) : nullptr;
    std::string maps, stat;
    if (!h.view || proc_read(pid, "maps", maps, 64 << 20)) return unchecked;
    // a line ends in the path of the file it maps
    const std::string tail = "/" + utf8(name);
    int lines = 0;
    for (size_t from = 0, end; from < maps.size(); from = end + 1) {
        if ((end = maps.find('\n', from)) == std::string::npos) end = maps.size();
        lines += end - from > tail.size() && !maps.compare(end - tail.size(), tail.size(), tail);
    }
    if (lines != 1) return lines ? unchecked : "bound to another process";
    if (proc_read(pid, "stat", stat, 4096)) return unchecked;
    const size_t close = stat.rfind(')');
    if (close == std::string::npos) return unchecked;
    // after "pid (name)": state is field 3, starttime field 22, and more fields follow it
    size_t p = close + 1;
    for (int field = 3; field < 22 && p != std::string::npos; ++field) p = stat.find(' ', p + 1);
    const size_t end = p == std::string::npos ? p : stat.find(' ', p + 1);
    if (end == std::string::npos || end == p + 1 || end - p - 1 > 20) return unchecked;
    for (size_t i = p + 1; i < end; ++i)
        if (stat[i] < '0' || stat[i] > '9') return unchecked;
    char* stop;
    errno = 0;
    const uint64_t start = strtoull(stat.c_str() + p + 1, &stop, 10);
    if (errno || stop != stat.c_str() + end) return unchecked;
    return start == at ? nullptr : "bound to another process";
}
// pid= and pid_time= name this process: nullptr, else why not.
static const char* bound_here() {
    uint64_t pid, at;
    if (!ini_number(L"pid", pid) || !ini_number(L"pid_time", at)) return "bound to another process";
    if (under_wine()) return wine_bound(pid, at);
    FILETIME created, x1, x2, x3;
    return pid == GetCurrentProcessId() && GetProcessTimes(GetCurrentProcess(), &created, &x1, &x2, &x3)
                   && at == ((uint64_t)created.dwHighDateTime << 32 | created.dwLowDateTime)
               ? nullptr
               : "bound to another process";
}
// A folder's (trailing backslash) entry names but . and .. (at most 100,000): ERROR_SUCCESS, else why it can't be listed
// whole.
static DWORD list_names(const std::wstring& dir, std::vector<std::wstring>& names) {
    names.clear();
    WIN32_FIND_DATAW fd;
    HANDLE f = FindFirstFileExW(long_path(dir + L"*").c_str(), FindExInfoBasic, &fd, FindExSearchNameMatch, nullptr, FIND_FIRST_EX_LARGE_FETCH);
    if (f == INVALID_HANDLE_VALUE) return last_error();
    DWORD err = ERROR_SUCCESS;
    for (;;) {
        if (names.size() >= 100000) { err = ERROR_BUFFER_OVERFLOW; break; }
        if (wcscmp(fd.cFileName, L".") && wcscmp(fd.cFileName, L"..")) names.push_back(fd.cFileName);
        if (!FindNextFileW(f, &fd)) {
            const DWORD e = GetLastError();
            err = e == ERROR_NO_MORE_FILES ? ERROR_SUCCESS : e ? e : ERROR_GEN_FAILURE;
            break;
        }
    }
    FindClose(f);
    return err;
}
// The exe's path as its command line names it (argv[0], made absolute as text); "" without one. The caller sets it.
static std::wstring command_path() {
    int n = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &n);
    const std::wstring a = argv && n > 0 && argv[0] ? argv[0] : L"";
    if (argv) LocalFree(argv);
    return a.empty() ? L"" : full_path(a);
}
// Steam's appinfo.vdf (Games.SteamSource.AppInfoPath). Under Wine (Proton) it is the one of the Steam that started the game
// (STEAM_COMPAT_CLIENT_INSTALL_PATH, through Wine's drives), even when a Steam in the prefix has its own, which can be stale.
// Never the game's library: it can be an abandoned Steam's folder. The variable empty: the registry's Steam's, which
// Proton's stand-in doesn't have. "" when the variable has no Windows path: nothing reads, VAC.
static std::wstring appinfo_path(const std::wstring& steam) {
    const std::wstring own = steam + L"\\appcache\\appinfo.vdf";
    const bool wine = GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "wine_get_version");  // Vendors.Wine.Running
    // tests: Wine without its drives (the client path as it is), only in a process exporting SCSKiller_WarmHost (selftest)
    const bool test = !wine && GetProcAddress(GetModuleHandleW(nullptr), "SCSKiller_WarmHost") && GetEnvironmentVariableW(L"SCSKILLER_TEST_WINE", nullptr, 0);
    const DWORD n = wine || test ? GetEnvironmentVariableW(L"STEAM_COMPAT_CLIENT_INSTALL_PATH", nullptr, 0) : 0;
    if (n <= 1) return own;
    std::wstring v(n, L'\0'), r;
    const DWORD got = GetEnvironmentVariableW(L"STEAM_COMPAT_CLIENT_INSTALL_PATH", v.data(), n);
    if (!got || got >= n) return L"";  // changed in between
    v.resize(got);
    auto dos = (wchar_t* (*)(const char*))GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "wine_get_dos_file_name");
    if (test) r = v;
    else if (wchar_t* d = dos ? dos(utf8(v).c_str()) : nullptr) r = d, HeapFree(GetProcessHeap(), 0, d);
    while (!r.empty() && r.back() == L'\\') r.pop_back();  // a drive's root: "S:\"
    return r.empty() ? L"" : r + L"\\appcache\\appinfo.vdf";
}

// Steam's folder, backslashed; false when it is unknown.
static bool steam_root(std::wstring& steam) {
    wchar_t root[MAX_PATH];
    DWORD n = sizeof root;
    // tests: another Steam folder, only in a process exporting SCSKiller_WarmHost (selftest), never a game
    if (GetProcAddress(GetModuleHandleW(nullptr), "SCSKiller_WarmHost") && GetEnvironmentVariableW(L"SCSKILLER_TEST_STEAM_ROOT", root, MAX_PATH)) {
    } else if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\Valve\\Steam", L"SteamPath", RRF_RT_REG_SZ, nullptr, root, &n) != ERROR_SUCCESS) return false;
    steam = root;
    for (auto& c : steam) if (c == L'/') c = L'\\';
    return true;
}

// The Steam app's VAC: a Valve VAC title, or appinfo.vdf's info about it marks VAC or can't be had (steam "": Steam's
// folder is unknown). nullptr: no VAC.
static const char* vac_of(uint32_t app, const std::wstring& steam) {
    if (std::find(std::begin(kValveVac), std::end(kValveVac), app) != std::end(kValveVac)) return "VAC (a Valve game)";
    if (steam.empty()) return kVacNoSteam;
    // appinfo.vdf: u32 magic (v28 0x07564428, v29 0x07564429), u32 universe, [v29: i64 offset of the key string table],
    // then entries {u32 app, u32 size, u32 info state (2: Steam has the info), 56 more bytes, binary KeyValues} until app 0
    const std::wstring path = appinfo_path(steam);
    FILE* f = path.empty() ? nullptr : _wfsopen(path.c_str(), L"rb", _SH_DENYNO);
    if (!f) return !path.empty() && errno == ENOENT ? kVacNoAppinfo : "VAC: Steam's appinfo.vdf can't be read";
    const int64_t flen = _filelengthi64(_fileno(f));
    const char* why = "VAC: Steam's appinfo.vdf can't be read";
    uint32_t hd[2] = {};
    int64_t table = 0;
    std::string entry;
    std::vector<std::string> names;
    if (fread(hd, 4, 2, f) != 2 || (hd[0] != 0x07564428 && hd[0] != 0x07564429) || (hd[0] == 0x07564429 && (fread(&table, 8, 1, f) != 1 || table < 16 || table >= flen)))
        why = "VAC: Steam's appinfo.vdf isn't understood";
    else
        for (uint32_t a[2]; fread(a, 4, 1, f) == 1;) {
            // only a list read to its end, its app 0 before v29's string table, says the app isn't in it
            if (!a[0]) { if (hd[0] != 0x07564429 || _ftelli64(f) <= table) why = kVacNotListed; break; }
            if (fread(a + 1, 4, 1, f) != 1) break;
            const int64_t left = (hd[0] == 0x07564429 ? table : flen) - _ftelli64(f);
            if (a[1] > left) { why = "VAC: Steam's appinfo.vdf is damaged"; break; }
            if (a[0] != app) { if (_fseeki64(f, a[1], SEEK_CUR)) break; continue; }
            if (a[1] < 61 || a[1] > (16u << 20)) { why = "VAC: the game's entry in appinfo.vdf is damaged"; break; }
            entry.resize(a[1]);
            if (fread(entry.data(), 1, a[1], f) != a[1]) { entry.clear(), why = "VAC: Steam's appinfo.vdf can't be read"; }
            break;
        }
    if (!entry.empty() && hd[0] == 0x07564429) {  // the string table: u32 count, then NUL-terminated names to the end
        uint32_t count = 0;
        std::string t;
        if (_fseeki64(f, table, SEEK_SET) || fread(&count, 4, 1, f) != 1 || flen - table > (64 << 20) || count > (1u << 24)) entry.clear();
        else {
            t.resize((size_t)(flen - table - 4));
            if (fread(t.data(), 1, t.size(), f) != t.size()) entry.clear();
            for (size_t p = 0; !entry.empty() && names.size() < count;) {
                size_t z = t.find('\0', p);
                if (z == std::string::npos) entry.clear();
                else names.push_back(t.substr(p, z - p)), p = z + 1;
            }
        }
        if (entry.empty()) why = "VAC: Steam's appinfo.vdf can't be read";
    }
    fclose(f);
    if (entry.empty()) return why;
    uint32_t state;
    memcpy(&state, entry.data(), 4);
    Kv kv{entry, 60, hd[0] == 0x07564429 ? &names : nullptr};
    if (!kv.object(0, 0)) return "VAC: the game's entry in appinfo.vdf is damaged";
    if (!kv.common) return kVacNoInfo;
    if (kv.vac) return "VAC (Steam marks the game)";
    if (!kv.category && state != 2) return kVacNoInfo;
    return nullptr;
}

// A Steam app id as Steam writes one in steam_appid.txt: decimal, 1 to 2^32-1. 0: not one.
static uint32_t app_id(const std::string& s) {
    if (s.empty() || s.size() > 10 || s.find_first_not_of("0123456789") != std::string::npos) return 0;
    const unsigned long long v = strtoull(s.c_str(), nullptr, 10);
    return v > UINT32_MAX ? 0 : (uint32_t)v;
}

// The VAC of the app each steam_appid.txt in dirs names, by any case of its name: a case-sensitive folder can hold several.
// A folder that can't be listed, or a file that can't be read, refuses, and so does one whose leading digits are no app id. One without them names none.
static const char* vac_named(const std::vector<std::wstring>& dirs) {
    const char* vac = nullptr;
    for (auto& d : dirs) {
        std::vector<std::wstring> files;
        WIN32_FIND_DATAW fd;
        HANDLE f = FindFirstFileExW(long_path(d + L"*").c_str(), FindExInfoBasic, &fd, FindExSearchNameMatch, nullptr, FIND_FIRST_EX_LARGE_FETCH);
        if (f == INVALID_HANDLE_VALUE) return "VAC: a folder can't be checked for steam_appid.txt";
        bool listed = true;
        for (size_t read = 0;;) {
            if (++read > 100000) { listed = false; break; }
            if (!_wcsicmp(fd.cFileName, L"steam_appid.txt")) files.push_back(d + fd.cFileName);
            if (!FindNextFileW(f, &fd)) { listed = GetLastError() == ERROR_NO_MORE_FILES; break; }
        }
        FindClose(f);
        if (!listed) return "VAC: a folder can't be checked for steam_appid.txt";
        for (auto& file : files) {
            HANDLE h = CreateFileW(long_path(file).c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
            if (h == INVALID_HANDLE_VALUE) return "VAC: a steam_appid.txt can't be read";
            LARGE_INTEGER size;
            std::string text;
            DWORD got = 0;
            bool ok = GetFileType(h) == FILE_TYPE_DISK && GetFileSizeEx(h, &size) && size.QuadPart <= 4096;
            if (ok) text.resize((size_t)size.QuadPart), ok = text.empty() || (ReadFile(h, text.data(), (DWORD)text.size(), &got, nullptr) && got == text.size());
            CloseHandle(h);
            if (!ok) return "VAC: a steam_appid.txt can't be read";
            if (!text.rfind("\xEF\xBB\xBF", 0)) text.erase(0, 3);
            // Steamworks takes the leading digits: a file without them names no app
            const size_t a = text.find_first_not_of(" \t\r\n\v\f+");
            const std::string digits = a == std::string::npos ? "" : text.substr(a, text.find_first_not_of("0123456789", a) - a);
            if (digits.empty()) continue;
            const uint32_t app = app_id(digits);
            if (!app) return "VAC: a steam_appid.txt is malformed";
            std::wstring steam;
            const char* why = named_vac(vac_of(app, steam_root(steam) ? steam : L""));
            if (why && !strncmp(why, "VAC:", 4)) return why;
            if (why) vac = why;
        }
    }
    return vac;
}

// nullptr: records; else why not. Never throws. As admit_posix.h decides it: every library Steam lists and each of its
// manifests is read whole and parsed (admission.h's Vdf), else it refuses. The exe is the app's whose installdir its
// steamapps\common folder names or whose game folder's final path holds it. A listed library without its steamapps or a
// game folder that isn't there (absent) is skipped when none of the exe's loader, final and command-line paths is in it,
// as text or through the final path of its nearest folder that is there, and so is Steam's folder without its steamapps
// (an uninstalled Steam, Proton's stand-in in the prefix). Library paths are taken in their plain form, and one with a
// folder name ending in a dot or a space refuses: Windows would read another folder for it.
static const char* vac_now_unguarded() {
    const std::wstring exe = module_path(nullptr);
    if (exe.empty()) return "VAC: the game's path can't be read";  // a cut path could hide its steamapps\common folder
    std::wstring real;
    if (real_of(exe, real)) return "VAC: the game's path can't be resolved";
    const std::wstring named = command_path();
    const auto holds_exe = [&](const std::wstring& dir) { return holds_text(dir, exe) || holds_text(dir, real); };
    const auto holds = [&](const std::wstring& dir) { return holds_exe(dir) || holds_text(dir, named); };
    // path, absent: one of the exe's paths is in it all the same, as text or under the final path of its nearest folder that
    // is there (an 8.3 name or a link on the way). ERROR_SUCCESS with in set, else the error resolving that folder.
    const auto held = [&](std::wstring path, bool& in) -> DWORD {
        in = holds(path);
        std::wstring rest, r;
        for (int up = 0; !in && up < 64; ++up) {
            if (const DWORD e = real_of(path, r); !e) return in = holds(rest.empty() ? r : join(r, rest.c_str())), ERROR_SUCCESS;
            else if (!absent(e)) return e;
            const size_t cut = path.find_last_of(L'\\');
            const bool root = path.size() <= 3 || (path[0] == L'\\' && std::count(path.begin(), path.end(), L'\\') <= 3);
            if (root || cut == std::wstring::npos) break;
            rest = path.substr(cut + 1) + (rest.empty() ? L"" : L"\\" + rest);
            path = trimmed(path.substr(0, cut));
        }
        return ERROR_SUCCESS;
    };
    struct Lib {
        std::wstring path;
        bool listed;
        std::vector<std::wstring> folders;  // the steamapps\common folders the exe's paths name in it
        std::wstring key;                   // folder_key
    };
    std::vector<Lib> libs, common;
    const auto add = [&](Lib l) {
        l.key = folder_key(l.path);
        for (auto& o : libs)
            if (!l.key.empty() && o.key == l.key) {
                o.folders.insert(o.folders.end(), l.folders.begin(), l.folders.end());
                return;
            }
        libs.push_back(std::move(l));
    };
    // a game in a steamapps\common folder is Steam's wherever its library is: the library is the path itself
    for (const std::wstring& p : {exe, real}) {
        std::wstring lower = p;
        for (auto& c : lower) c = c == L'/' ? L'\\' : towlower(c);
        const size_t sc = lower.find(L"\\steamapps\\common\\");
        if (sc == std::wstring::npos) continue;
        const std::wstring rest = p.substr(sc + wcslen(L"\\steamapps\\common\\"));
        common.push_back({trimmed(plain_path(p.substr(0, sc))), false, {rest.substr(0, rest.find(L'\\'))}, {}});
    }
    std::wstring steam, there;
    bool in = false;
    if (!steam_root(steam) || (steam = trimmed(plain_path(steam))).empty()) return !common.empty() ? "VAC: in a Steam game folder, and Steam's folder is unknown" : nullptr;
    if (odd_name(steam)) return "VAC: Steam's folder has a name ending in a dot or a space";
    std::string text;
    Vdf top;
    if (const DWORD err = read_whole(join(steam, L"steamapps\\libraryfolders.vdf"), text)) {
        if (!absent(err) || !absolute(steam) || !absent(real_of(join(steam, L"steamapps"), there))) return "VAC: Steam's libraryfolders.vdf can't be read";
        if (held(steam, in)) return "VAC: Steam's folder can't be resolved";
        if (in) return "VAC: Steam's folder without its steamapps holds the game's path";
        // an uninstalled Steam's registry entry, or Proton's stand-in: no library but the game's own
    } else {
        if (!vdf_root(text, "libraryfolders", top)) return "VAC: Steam's libraryfolders.vdf is malformed";
        add({steam, false, {}, {}});
        for (auto& e : top.kids) {
            std::string p = e.value;
            if (e.object ? !vdf_one(e, "path", p) || p.empty() : all_digits(e.key) && p.empty())
                return "VAC: a library in Steam's libraryfolders.vdf has no single path";
            if (!e.object && !all_digits(e.key)) continue;
            const std::wstring w = trimmed(plain_path(widen(p)));
            if (w.empty()) return "VAC: a library in Steam's libraryfolders.vdf has no single path";
            if (odd_name(w)) return "VAC: a Steam library's path has a name ending in a dot or a space";
            add({w, true, {}, {}});
        }
    }
    for (auto& l : common) add(l);
    std::vector<uint32_t> apps;
    std::vector<std::wstring> names;
    bool in_library = false;
    for (auto& lib : libs) {
        const std::wstring dir = join(lib.path, L"steamapps\\");
        if (const DWORD err = list_names(dir, names)) {
            const DWORD e = real_of(dir, there);
            if (e || err != ERROR_FILE_NOT_FOUND) {  // FindFirstFileExW's for no entry at all, not even . and .. (a volume's root)
                if (!lib.listed || !absolute(lib.path) || !absent(e) || held(lib.path, in)) return "VAC: a Steam library's folder can't be read";
                if (in) return "VAC: a Steam library without its steamapps holds the game's path";
                continue;
            }
        }
        if (real_of(lib.path, there)) return "VAC: a Steam library's folder can't be read";
        in_library = in_library || holds_exe(lib.path) || holds_exe(there);
        for (auto& n : names) {
            if (n.size() < 16 || !same_name(n.substr(0, 12), L"appmanifest_") || !same_name(n.substr(n.size() - 4), L".acf")) continue;
            Vdf app;
            std::string a, d;
            if (read_whole(dir + n, text)) return with_path("VAC: a Steam manifest can't be read", utf8(dir + n));
            if (!vdf_root(text, "appstate", app)) return with_path("VAC: a Steam manifest is malformed", utf8(dir + n));
            const uint32_t id = vdf_one(app, "appid", a) ? app_id(a) : 0;
            const std::wstring w = vdf_one(app, "installdir", d) ? widen(d) : L"";
            if (!id || w.empty() || w == L"." || w == L".." || w.find_first_of(L"\\/:") != std::wstring::npos)
                return with_path("VAC: a Steam manifest names no single folder and app", utf8(dir + n));
            const std::wstring folder = dir + L"common\\" + w;
            const DWORD e = real_of(folder, there);
            if (e == ERROR_CANT_RESOLVE_FILENAME) return "VAC: a Steam game folder's links loop";
            if (e && (!absent(e) || held(folder, in))) return "VAC: a Steam game's folder can't be resolved";
            if (e && in) return "VAC: a Steam game's folder that doesn't exist holds the game's path";
            bool owns = !e && holds_exe(there);
            for (auto& f : lib.folders) owns = owns || same_name(f, w);
            if (owns && std::find(apps.begin(), apps.end(), id) == apps.end()) apps.push_back(id);
        }
    }
    if (apps.size() > 1) return "VAC: two Steam manifests name the game's folder";
    if (!apps.empty()) return vac_of(apps[0], steam);
    if (!common.empty()) return "VAC: no Steam manifest names the game's folder";
    return in_library ? "VAC: in a Steam library, not a Steam game folder" : nullptr;
}

// The exe's own app's VAC, else a steam_appid.txt's: a finding that refuses even a risk session ("VAC:") comes first.
static const char* vac_now(const std::vector<std::wstring>& dirs) {
    try {
        const char* own = vac_now_unguarded();
        if (own && !strncmp(own, "VAC:", 4)) return own;
        const char* named = vac_named(dirs);
        return named && (!own || !strncmp(named, "VAC:", 4)) ? named : own;
    } catch (...) {
        return "VAC: Steam's files couldn't be checked";
    }
}

static bool g_by_path;  // signed_by_app admitted by the path form
// This folder's key on the app's revocation list (recorder.revoked in its recorder folder: the file_key of each folder whose
// scskiller.ini it couldn't take out, one a line, as ScsKiller.Revoke writes them), or the list can't be read whole (only
// a list that doesn't exist is empty): why it refuses; nullptr: neither.
static const char* revoked_here() {
    const std::wstring dir = recorder_dir(admit_dir()), file = dir + L"\\recorder.revoked";
    if (dir.empty()) return "SCSKiller's revocation list can't be read";
    std::string list;
    FILE* f = _wfsopen(file.c_str(), L"rb", _SH_DENYNO);
    if (!f) {
        const DWORD e = GetFileAttributesW(file.c_str()) == INVALID_FILE_ATTRIBUTES ? GetLastError() : ERROR_ACCESS_DENIED;
        return e == ERROR_FILE_NOT_FOUND || e == ERROR_PATH_NOT_FOUND ? nullptr : "SCSKiller's revocation list can't be read";
    }
    char b[65536];
    for (size_t n; list.size() <= (1 << 20) && (n = fread(b, 1, sizeof b, f));) list.append(b, n);
    const bool bad = ferror(f) || list.size() > (1 << 20);
    fclose(f);
    if (bad) return "SCSKiller's revocation list can't be read";
    // admitted by the path form, any revoked folder refuses: this process can't tell the folder at this path from one revoked
    // elsewhere and moved here, by identity or by path
    if (g_by_path && list.find_first_not_of(" \t\r\n") != std::string::npos) return "SCSKiller's revocation list isn't empty";
    std::wstring here = admit_dir();
    if (here.size() > 3 && here.back() == L'\\') here.pop_back();
    // the path form too (path_folder_key), as the app writes it beside the identity
    std::string key, path;
    for (wchar_t c : file_key(here)) key += (char)c;
    for (wchar_t c : path_folder_key(here)) path += (char)c;
    if (path.empty()) return "SCSKiller's revocation list can't be read";
    for (size_t at = 0; at < list.size();) {
        size_t end = list.find('\n', at);
        std::string line = list.substr(at, end == std::string::npos ? std::string::npos : end - at);
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line == key || line == path) return "revoked by SCSKiller";
        if (end == std::string::npos) break;
        at = end + 1;
    }
    return nullptr;
}
// sig= is recorder_sig of this exe's and this folder's file_key, the stamp lines (signed_stamp: vendor= and the countdown's lines
// too, so a hand edit can't turn the AGS hook or the panel on), root= and the root's file_key under the
// recorder folder's recorder.key: an ini copied beside another exe or a hard link of it in another folder, or reached
// through a link whose root= resolves to another folder, fails.
static bool signed_by_app(const std::wstring& root) {
    const std::wstring dir = recorder_dir(admit_dir()), exe = module_path(nullptr), sig = ini_string(L"sig"), pid = ini_string(L"pid");
    g_by_path = false;
    if (dir.empty() || exe.empty() || sig.size() != 40) return false;
    const std::string key = read_all(dir + L"\\recorder.key", 256);
    if (key.size() < 32 || key.size() > 128) return false;
    const std::wstring stamp = signed_stamp(ini_string(L"exe_size"), ini_string(L"exe_time"), pid, ini_string(L"pid_time"), ini_string(L"risk"), ini_string(L"vendor"),
                                            ini_string(L"overlay"), ini_string(L"ab_play_s"), ini_string(L"ab_tag"), traversal_lines(ini_string));
    const auto no_slash = [](const std::wstring& d) { return d.substr(0, d.size() > 3 ? d.size() - 1 : d.size()); };
    const std::wstring exe_key = file_key(exe), want = recorder_sig(key, exe_key, folder_file_key(no_slash(admit_dir())), stamp, ini_string(L"root"),
                                                                      folder_file_key(no_slash(root)));
    if (!want.empty() && sig == want) return true;
    // a process that makes up folder identities (made_up_here), root=. and an exe that is one file with one name (no other
    // hard link, no link of its own): the same HMAC by the folder's path_folder_key for both folders, which the app keeps in
    // stamps\<path_folder_key>, one a line (ScsKiller.Signed), never in the game's folder
    if (ini_string(L"root") != L"." || !made_up_here(no_slash(admit_dir()), exe) || hard_links(exe) != 1
        || (GetFileAttributesW(long_path(exe).c_str()) & FILE_ATTRIBUTE_REPARSE_POINT)) return false;
    const std::wstring here = path_folder_key(no_slash(admit_dir()));
    if (here.empty()) return false;
    const std::string kept = read_all(dir + L"\\stamps\\" + here, 4096);
    for (size_t at = 0; at < kept.size();) {
        size_t end = kept.find('\n', at);
        std::string line = kept.substr(at, end == std::string::npos ? std::string::npos : end - at);
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line.size() == 40 && std::wstring(line.begin(), line.end()) == recorder_sig(key, exe_key, here, stamp, L".", here)) return g_by_path = true;
        if (end == std::string::npos) break;
        at = end + 1;
    }
    return false;
}

// Admitted by the path form, the exe's folder must list as the app saw it from this process too: the exe, scskiller.ini and,
// when it was loaded from there, this module, by long or 8.3 name. The anti-cheat walk lists it through the same layer.
static bool listed_here() {
    HMODULE self = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)&listed_here, &self);
    const std::wstring exe = module_path(nullptr), own = module_path(self), dir = admit_dir();
    const std::wstring want[3] = {exe.substr(exe.find_last_of(L'\\') + 1), L"scskiller.ini", own.substr(own.find_last_of(L'\\') + 1)};
    bool seen[3] = {false, false, _wcsnicmp(own.c_str(), dir.c_str(), dir.size()) || own.find(L'\\', dir.size()) != std::wstring::npos};
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileExW(long_path(dir + L"*").c_str(), FindExInfoStandard, &fd, FindExSearchNameMatch, nullptr, FIND_FIRST_EX_LARGE_FETCH);
    if (h == INVALID_HANDLE_VALUE) return false;
    for (size_t n = 0; n < 100000; ++n) {
        for (int i = 0; i < 3; ++i) seen[i] |= !_wcsicmp(fd.cFileName, want[i].c_str()) || !_wcsicmp(fd.cAlternateFileName, want[i].c_str());
        if (!FindNextFileW(h, &fd)) break;
    }
    FindClose(h);
    return seen[0] && seen[1] && seen[2];
}

// scskiller.ini's bytes, read whole; false when it can't be, or is over 64 KB (the app's is a few hundred bytes).
static bool ini_bytes(const std::wstring& path, std::string& out) {
    out.clear();
    FILE* f = _wfsopen(path.c_str(), L"rb", _SH_DENYNO);
    if (!f) return false;
    char b[4096];
    for (size_t n; out.size() <= 65536 && (n = fread(b, 1, sizeof b, f));) out.append(b, n);
    const bool ok = !ferror(f) && out.size() <= 65536;
    fclose(f);
    return ok;
}

// The verdict for the exe beside the recorder (admit_dir()), whose scskiller.ini read is the bytes ini_string reads
// (t_ini, set by the caller), whole false when it couldn't be read whole: nullptr records, else why not. elsewhere: the
// recorder wasn't loaded from that folder. risk and seen: for the log, as it opens.
static const char* admission_verdict(bool whole, const std::string& read, bool elsewhere, bool& risk, std::string& seen) {
    const std::wstring ini = admit_dir() + L"scskiller.ini";
    std::string again;
    std::vector<std::wstring> markers;
    // the folders as they are on disk: a launch through a link or junction lists the real exe-to-root chain
    const std::wstring here = final_dir(admit_dir()), root = here.empty() ? L"" : install_root(here);
    const bool bound = !ini_string(L"pid").empty();
    risk = !ini_string(L"risk").empty();
    const char* unbound = nullptr;
    const char* why = admit_dir().empty() ? "the recorder's folder can't be read"
                    : elsewhere ? "loaded from another folder than the exe"
                    : !exe_as_checked() ? "exe not as checked"
                    : !whole ? "scskiller.ini can't be read whole"
                    : here.empty() ? "the exe's folder can't be resolved"
                    : root.empty() ? "no install root"
                    : odd_name(here) || odd_name(root) ? "a folder name ends in a dot or a space"
                    : !signed_by_app(root) ? "not signed by SCSKiller"
                    : g_by_path && (ini_string(L"root") != L"." || !listed_here()) ? "the game's folder reads differently inside the game"
                    : bound && (unbound = bound_here()) ? unbound
                    : !risk_well_formed(ini_string(L"pid"), ini_string(L"risk")) ? "risk= malformed"
                    : !anti_cheat_markers(markers) ? "markers= malformed" : nullptr;
    if (!why) why = revoked_here();
    if (!why && bound && !risk) markers.erase(markers.begin(), markers.begin() + kEasyAntiCheatMarkers);  // the app started this process without EasyAntiCheat
    const std::vector<std::wstring> dirs = why ? std::vector<std::wstring>{} : folders_to(here, root);
    if (!why && dirs.empty()) why = "install root too far up";
    if (!why) {
        std::vector<std::string> found;
        const char* found_why = nullptr;
        for (auto& d : dirs) {
            const std::vector<std::string> names = anti_cheat_in(d, markers, risk);
            if (names.empty()) continue;
            const bool beside = d == here;
            if (!found_why || names[0] == "?") found_why = beside ? "anti-cheat next to the exe" : "anti-cheat in the install folder";
            if (names[0] == "?") {
                found.assign(1, "?");
                break;
            }
            for (auto& n : names) found.push_back(n + (beside ? " (next to the exe)" : " (in the install folder)"));
            if (!risk) break;
        }
        why = decide(risk, riot_games(), found, found_why, anti_cheat_loaded(), vac_now(dirs), seen);
    }
    // tests: a change in between. SCSKILLER_TEST_ADMIT_GATE=<name>: sets the event <name>.checked, waits for <name>.go.
    // Only in a process exporting SCSKiller_WarmHost (selftest), never a game.
    if (wchar_t gate[64]; !why && GetProcAddress(GetModuleHandleW(nullptr), "SCSKiller_WarmHost") && env(L"SCSKILLER_TEST_ADMIT_GATE", gate)) {
        HANDLE checked = OpenEventW(EVENT_MODIFY_STATE, FALSE, (gate + std::wstring(L".checked")).c_str());
        HANDLE go = OpenEventW(SYNCHRONIZE, FALSE, (gate + std::wstring(L".go")).c_str());
        if (checked) SetEvent(checked), CloseHandle(checked);
        if (go) WaitForSingleObject(go, 60000), CloseHandle(go);
    }
    if (!why && (!ini_bytes(ini, again) || again != read)) why = "scskiller.ini changed while deciding";  // the app took it out meanwhile
    if (!why) why = revoked_here();
    return why;
}
