// The keys of an exe's ledger entries, as ScsKiller.LedgerFiles makes them: SHA-1 in hex of its file identity (FILE_ID_INFO,
// 24 bytes: the same through any link or junction, where a packaged app's process names its exe by the package's
// WindowsApps link), then of its final path (no \\?\), then of the path as given (UTF-16LE, A-Z lowered); each only when
// it can be read. The proxy reads the first; the app writes them all.
#pragma once
#include <windows.h>
#include <shlobj.h>
#include <bcrypt.h>
#include <string>
#include <vector>

// The app's ledger folder, %LOCALAPPDATA%\SCSKiller\armed ("" when it can't be had). SCSKILLER_TEST_LEDGER_DIR replaces it
// only in a process exporting SCSKiller_WarmHost (selftest, scskiller_warm), never in a game: an environment a launcher or
// another program passes down can't move where a game's admission reads. Set there but unusable: "", never the real one.
inline std::wstring ledger_dir() {
    if (GetProcAddress(GetModuleHandleW(nullptr), "SCSKiller_WarmHost")) {
        wchar_t v[MAX_PATH];
        if (DWORD n = GetEnvironmentVariableW(L"SCSKILLER_TEST_LEDGER_DIR", v, MAX_PATH)) return n < MAX_PATH ? v : L"";
    }
    PWSTR local = nullptr;
    if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local))) return CoTaskMemFree(local), L"";
    std::wstring dir = local;
    CoTaskMemFree(local);
    return dir + L"\\SCSKiller\\armed";
}

inline std::wstring ledger_hex(const void* p, size_t n) {
    UCHAR h[20];
    if (BCryptHash(BCRYPT_SHA1_ALG_HANDLE, nullptr, 0, (PUCHAR)p, (ULONG)n, h, 20)) return L"";
    wchar_t hex[41] = {};
    for (int i = 0; i < 20; ++i) swprintf(hex + 2 * i, 3, L"%02x", h[i]);
    return hex;
}

inline std::wstring ledger_path_key(std::wstring path) {
    for (auto& c : path)
        if (c >= L'A' && c <= L'Z') c += L'a' - L'A';
    return ledger_hex(path.data(), path.size() * sizeof(wchar_t));
}

inline std::vector<std::wstring> ledger_keys(const std::wstring& exe) {
    std::vector<std::wstring> keys;
    HANDLE f = CreateFileW(exe.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    if (f != INVALID_HANDLE_VALUE) {
        FILE_ID_INFO id;
        if (GetFileInformationByHandleEx(f, FileIdInfo, &id, sizeof id)) keys.push_back(ledger_hex(&id, sizeof id));
        std::wstring final(32768, L'\0');
        DWORD m = GetFinalPathNameByHandleW(f, final.data(), (DWORD)final.size(), 0);
        CloseHandle(f);
        if (m && m < final.size()) {
            final.resize(m);
            keys.push_back(ledger_path_key(final.rfind(L"\\\\?\\UNC\\", 0) == 0 ? L"\\\\" + final.substr(8) : final.rfind(L"\\\\?\\", 0) == 0 ? final.substr(4) : final));
        }
    }
    keys.push_back(ledger_path_key(exe));
    return keys;
}
