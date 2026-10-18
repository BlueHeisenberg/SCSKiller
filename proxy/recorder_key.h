// What the proxy and the app (ScsKiller.FileKey, ScsKiller.Signed) share about the recorder's stamp in scskiller.ini.
#pragma once
#include <windows.h>
#include <shlobj.h>
#include <bcrypt.h>
#include <string>

// The data folder of the SCSKiller copy that placed the recorder in <dir> (trailing backslash): the parent of the
// armed\ path its scskiller.ledger names (UTF-8, absolute, one line; a portable or test copy's, ScsKiller.LedgerStamp). False:
// there is none (the installed copy's). There but unusable: true with "".
inline bool stamped_recorder_dir(const std::wstring& dir, std::wstring& out) {
    out.clear();
    const std::wstring file = dir + L"scskiller.ledger";
    const DWORD a = GetFileAttributesW(file.c_str());   // a link's own attributes: a dangling one is there
    if (a == INVALID_FILE_ATTRIBUTES) {
        const DWORD e = GetLastError();
        return e != ERROR_FILE_NOT_FOUND && e != ERROR_PATH_NOT_FOUND;
    }
    if (a & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) return true;   // the app writes a plain file
    HANDLE h = CreateFileW(file.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (h == INVALID_HANDLE_VALUE) return true;
    FILE_ATTRIBUTE_TAG_INFO tag{};
    char b[4 * MAX_PATH];
    DWORD n = 0;
    const BOOL read = GetFileInformationByHandleEx(h, FileAttributeTagInfo, &tag, sizeof tag) && !(tag.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)
                      && ReadFile(h, b, sizeof b, &n, nullptr);
    CloseHandle(h);
    if (!read || n == sizeof b) return true;
    while (n && (b[n - 1] == '\r' || b[n - 1] == '\n' || b[n - 1] == ' ')) --n;
    const int m = n ? MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, b, (int)n, nullptr, 0) : 0;
    if (m <= 3 || m >= MAX_PATH) return true;
    std::wstring ledger(m, L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, b, (int)n, ledger.data(), m);
    // as Path.GetFullPath writes it: X:\ or \\server\share, then segments none empty or ending in . or space (Windows drops
    // those: .. and "x." alias other folders), no device path, stream, wildcard or NUL
    const bool drive = (ledger[0] | 0x20) >= L'a' && (ledger[0] | 0x20) <= L'z' && ledger[1] == L':' && ledger[2] == L'\\';
    bool ok = (drive || (ledger[0] == L'\\' && ledger[1] == L'\\')) && ledger.find_first_of(L"/?*<>|\"") == std::wstring::npos
              && ledger.find(L':', 2) == std::wstring::npos && ledger.find(L'\0') == std::wstring::npos;
    size_t parts = 0;
    for (size_t at = drive ? 3 : 2; ok && at <= ledger.size(); ++parts) {
        size_t end = ledger.find(L'\\', at);
        if (end == std::wstring::npos) end = ledger.size();
        ok = end > at && ledger[end - 1] != L'.' && ledger[end - 1] != L' ';
        at = end + 1;
    }
    const size_t cut = ledger.find_last_of(L'\\');
    if (ok && parts >= (drive ? 2u : 3u) && !_wcsicmp(ledger.c_str() + cut + 1, L"armed")) out = ledger.substr(0, cut);
    return true;
}

// The recorder folder (recorder.key, recorder.revoked, refused\) of the copy whose recorder is in <dir> (trailing
// backslash): stamped_recorder_dir's, else the installed copy's %LOCALAPPDATA%\SCSKiller; "" when it can't be had.
// SCSKILLER_TEST_RECORDER_DIR replaces it only in a process exporting SCSKiller_WarmHost (selftest, scskiller_warm), never
// in a game: an environment a launcher or another program passes down can't move where a game's admission reads. Set
// there but unusable: "", never the real one.
inline std::wstring recorder_dir(const std::wstring& dir) {
    if (GetProcAddress(GetModuleHandleW(nullptr), "SCSKiller_WarmHost")) {
        wchar_t v[MAX_PATH];
        if (DWORD n = GetEnvironmentVariableW(L"SCSKILLER_TEST_RECORDER_DIR", v, MAX_PATH)) return n < MAX_PATH ? v : L"";
    }
    if (dir.empty()) return L"";
    std::wstring stamped;
    if (stamped_recorder_dir(dir, stamped)) return stamped;
    PWSTR local = nullptr;
    if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local))) return CoTaskMemFree(local), L"";
    std::wstring installed = local;
    CoTaskMemFree(local);
    return installed + L"\\SCSKiller";
}

inline std::wstring key_hex(const void* p, size_t n) {
    UCHAR h[20];
    if (BCryptHash(BCRYPT_SHA1_ALG_HANDLE, nullptr, 0, (PUCHAR)p, (ULONG)n, h, 20)) return L"";
    wchar_t hex[41] = {};
    for (int i = 0; i < 20; ++i) swprintf(hex + 2 * i, 3, L"%02x", h[i]);
    return hex;
}

inline std::wstring path_key(std::wstring path) {
    for (auto& c : path)
        if (c >= L'A' && c <= L'Z') c += L'a' - L'A';
    return key_hex(path.data(), path.size() * sizeof(wchar_t));
}

// The path to open, past MAX_PATH in a process that isn't long-path aware: \\?\ (\\?\UNC\ for a share) in front of an
// absolute one. \\?\ turns off normalization, so a path with / or a . or .. segment is left as it is (it fails to open).
inline std::wstring long_path(const std::wstring& p) {
    if (p.size() < MAX_PATH || p.rfind(L"\\\\?\\", 0) == 0 || p.find(L'/') != std::wstring::npos) return p;
    for (const wchar_t* dots : {L"\\.\\", L"\\..\\"})
        if (p.find(dots) != std::wstring::npos) return p;
    if (p.ends_with(L"\\.") || p.ends_with(L"\\..")) return p;
    if (p.rfind(L"\\\\", 0) == 0) return L"\\\\?\\UNC\\" + p.substr(2);
    return p.size() > 2 && p[1] == L':' && p[2] == L'\\' ? L"\\\\?\\" + p : p;
}

// A folder name in the path ends in a dot or a space: opened with or without \\?\ it can name two folders, so what a check
// lists could depend on the path's length.
inline bool odd_name(const std::wstring& p) {
    for (size_t at = p.find(L'\\', 1); at != std::wstring::npos; at = p.find(L'\\', at + 1))
        if (p[at - 1] == L'.' || p[at - 1] == L' ') return true;
    return !p.empty() && (p.back() == L'.' || p.back() == L' ');
}

// A file's or folder's key: SHA-1 in hex of its file identity (FILE_ID_INFO, 24 bytes: the same through any link, junction
// or 8.3 name, where a packaged app's process names its exe by the package's WindowsApps link), else of its final path
// (no \\?\), else of the path as given (UTF-16LE, A-Z lowered).
inline std::wstring file_key(const std::wstring& path) {
    HANDLE f = CreateFileW(long_path(path).c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    if (f != INVALID_HANDLE_VALUE) {
        FILE_ID_INFO id;
        std::wstring key;
        if (GetFileInformationByHandleEx(f, FileIdInfo, &id, sizeof id)) key = key_hex(&id, sizeof id);
        std::wstring final(32768, L'\0');
        DWORD m = key.empty() ? GetFinalPathNameByHandleW(f, final.data(), (DWORD)final.size(), 0) : 0;
        CloseHandle(f);
        if (!key.empty()) return key;
        if (m && m < final.size()) {
            final.resize(m);
            return path_key(final.rfind(L"\\\\?\\UNC\\", 0) == 0 ? L"\\\\" + final.substr(8) : final.rfind(L"\\\\?\\", 0) == 0 ? final.substr(4) : final);
        }
    }
    return path_key(path);
}

// The folder's final path (links, junctions and 8.3 names resolved; no \\?\) with a trailing backslash; "" when it can't
// be had (no drive letter maps its volume, for one).
inline std::wstring final_dir(const std::wstring& dir) {
    HANDLE f = CreateFileW(long_path(dir).c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    if (f == INVALID_HANDLE_VALUE) return L"";
    std::wstring final(32768, L'\0');
    DWORD m = GetFinalPathNameByHandleW(f, final.data(), (DWORD)final.size(), 0);
    CloseHandle(f);
    if (!m || m >= final.size()) return L"";
    final.resize(m);
    final = final.rfind(L"\\\\?\\UNC\\", 0) == 0 ? L"\\\\" + final.substr(8) : final.rfind(L"\\\\?\\", 0) == 0 ? final.substr(4) : final;
    return final.back() == L'\\' ? final : final + L'\\';
}

// sig=: the HMAC-SHA1 (lower-case hex) under key of "<exe's file_key>\n<its folder's file_key>\n" + stamp +
// "root=<root>\n<root's file_key>\n", UTF-16LE, where stamp is admission.h's signed_stamp, and the root is
// root= resolved from the exe's final_dir. "" when it can't be made.
inline std::wstring recorder_sig(const std::string& key, const std::wstring& exe_key, const std::wstring& dir_key, const std::wstring& stamp, const std::wstring& root,
                                 const std::wstring& root_key) {
    const std::wstring msg = exe_key + L"\n" + dir_key + L"\n" + stamp + L"root=" + root + L"\n" + root_key + L"\n";
    UCHAR mac[20];
    if (key.empty() || BCryptHash(BCRYPT_HMAC_SHA1_ALG_HANDLE, (PUCHAR)key.data(), (ULONG)key.size(), (PUCHAR)msg.data(), (ULONG)(msg.size() * sizeof(wchar_t)), mac, 20))
        return L"";
    wchar_t hex[41] = {};
    for (int i = 0; i < 20; ++i) swprintf(hex + 2 * i, 3, L"%02x", mac[i]);
    return hex;
}
