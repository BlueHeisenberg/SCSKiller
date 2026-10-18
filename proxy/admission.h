// The parts of the recorder's admission that read values alone, shared with `selftest admission`: the stamp sig= covers and
// the verdict from what the checks found.
#pragma once
#include <windows.h>
#include <cstring>
#include <string>
#include <vector>

// The stamp lines sig= covers, as ScsKiller.Signed writes them: exe_size=, exe_time=, then pid= and pid_time= when pid= is
// there, then risk= when it is there.
inline std::wstring signed_stamp(const std::wstring& size, const std::wstring& time, const std::wstring& pid, const std::wstring& pid_time, const std::wstring& risk,
                                 const std::wstring& vendor = L"", const std::wstring& overlay = L"", const std::wstring& play_s = L"", const std::wstring& tag = L"") {
    std::wstring s = L"exe_size=" + size + L"\r\nexe_time=" + time + L"\r\n";
    if (!pid.empty()) s += L"pid=" + pid + L"\r\npid_time=" + pid_time + L"\r\n";
    if (!risk.empty()) s += L"risk=" + risk + L"\r\n";
    if (!vendor.empty()) s += L"vendor=" + vendor + L"\r\n";
    if (!overlay.empty()) s += L"overlay=" + overlay + L"\r\n";
    if (!play_s.empty()) s += L"ab_play_s=" + play_s + L"\r\n";
    if (!tag.empty()) s += L"ab_tag=" + tag + L"\r\n";
    return s;
}

// risk= is either missing, or 1 together with pid=.
inline bool risk_well_formed(const std::wstring& pid, const std::wstring& risk) { return risk.empty() || (!pid.empty() && risk == L"1"); }

// The verdict from what the checks found, in their order: a Riot Games install, the markers in the folders up to the install
// root ("name (where)"; {"?"} when one couldn't be listed whole) with found_why the reason for the first, the anti-cheat client
// modules loaded, vac_now's reason ("VAC: ..." when Steam's files couldn't tell). Without risk the first finding refuses.
// Under risk a definite finding admits and goes to seen as the log lists it; what couldn't be inspected refuses as without risk.
inline const char* decide(bool risk, bool riot, const std::vector<std::string>& found, const char* found_why, const std::vector<std::string>& loaded, const char* vac,
                          std::string& seen) {
    if (!risk) return riot ? "Riot Games install" : !found.empty() ? found_why : !loaded.empty() ? "anti-cheat client loaded" : vac;
    if (!found.empty() && found[0] == "?") return found_why;
    if (vac && !strncmp(vac, "VAC:", 4)) return vac;
    for (auto& m : found) seen += (seen.empty() ? "" : ", ") + m;
    for (auto& m : loaded) seen += (seen.empty() ? "" : ", ") + m + " loaded";
    if (riot) seen += (seen.empty() ? "" : ", ") + std::string("Riot Games install");
    if (vac) seen += (seen.empty() ? "" : ", ") + std::string("VAC");
    if (seen.empty()) seen = "none at the first device";
    return nullptr;
}

// The key's value in scskiller.ini's bytes, read as GetPrivateProfileStringW reads an ANSI file: the first [scskiller]
// section, the key's first line, trimmed, quotes stripped. A file whose lines the two could split differently (a CR without
// LF, a NUL, a section header other than [scskiller] exactly, or two of them) reads as empty: not signed.
inline std::wstring ini_value(const std::string& bytes, const wchar_t* key) {
    if (bytes.find('\0') != std::string::npos) return L"";
    for (size_t cr = bytes.find('\r'); cr != std::string::npos; cr = bytes.find('\r', cr + 1))
        if (cr + 1 == bytes.size() || bytes[cr + 1] != '\n') return L"";
    std::wstring text(bytes.size(), L'\0');
    const int n = bytes.empty() ? 0 : MultiByteToWideChar(CP_ACP, 0, bytes.data(), (int)bytes.size(), text.data(), (int)text.size());
    text.resize(n > 0 ? n : 0);
    const auto trim = [](const std::wstring& s) {
        const size_t a = s.find_first_not_of(L" \t\r"), b = s.find_last_not_of(L" \t\r");
        return a == std::wstring::npos ? std::wstring() : s.substr(a, b - a + 1);
    };
    int sections = 0;
    for (size_t at = 0; at <= text.size();) {
        const size_t end = text.find(L'\n', at);
        const std::wstring line = trim(text.substr(at, end == std::wstring::npos ? std::wstring::npos : end - at));
        at = end == std::wstring::npos ? text.size() + 1 : end + 1;
        if (!line.empty() && line[0] == L'[' && (_wcsicmp(line.c_str(), L"[scskiller]") || ++sections > 1)) return L"";
    }
    bool in = false;
    for (size_t at = 0; at <= text.size();) {
        const size_t end = text.find(L'\n', at);
        std::wstring line = text.substr(at, end == std::wstring::npos ? std::wstring::npos : end - at);
        at = end == std::wstring::npos ? text.size() + 1 : end + 1;
        if (!line.empty() && line.back() == L'\r') line.pop_back();
        line = trim(line);
        if (!line.empty() && line[0] == L'[') {
            if (in) break;
            in = !_wcsicmp(line.c_str(), L"[scskiller]");
            continue;
        }
        const size_t eq = line.find(L'=');
        if (!in || line.empty() || line[0] == L';' || eq == std::wstring::npos || _wcsicmp(trim(line.substr(0, eq)).c_str(), key)) continue;
        std::wstring v = trim(line.substr(eq + 1));
        if (v.size() >= 2 && (v[0] == L'"' || v[0] == L'\'') && v.back() == v[0]) v = v.substr(1, v.size() - 2);
        return v;
    }
    return L"";
}
