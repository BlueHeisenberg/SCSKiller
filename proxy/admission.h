// The parts of the recorder's admission that read values alone, shared with `selftest admission`: the stamp sig= covers and
// the verdict from what the checks found.
#pragma once
#ifdef _WIN32
#include <windows.h>
#else
#include <strings.h>
#include <wchar.h>
static inline int _wcsicmp(const wchar_t* a, const wchar_t* b) { return wcscasecmp(a, b); }
static inline int _stricmp(const char* a, const char* b) { return strcasecmp(a, b); }
static inline int _wcsnicmp(const wchar_t* a, const wchar_t* b, size_t n) { return wcsncasecmp(a, b, n); }
// UTF-8 into out (one wchar_t a code point); false for anything that isn't well-formed UTF-8.
static inline bool from_utf8(const std::string& s, std::wstring& out) {
    out.clear();
    for (size_t i = 0; i < s.size();) {
        const unsigned char c = (unsigned char)s[i];
        const int n = c < 0x80 ? 0 : (c & 0xe0) == 0xc0 ? 1 : (c & 0xf0) == 0xe0 ? 2 : (c & 0xf8) == 0xf0 ? 3 : -1;
        if (n < 0 || (n && i + n >= s.size())) return false;
        uint32_t cp = n ? c & (0x3f >> n) : c;
        for (int k = 1; k <= n; ++k) {
            const unsigned char d = (unsigned char)s[i + k];
            if ((d & 0xc0) != 0x80) return false;
            cp = cp << 6 | (d & 0x3f);
        }
        static const uint32_t least[] = {0, 0x80, 0x800, 0x10000};
        if (cp < least[n] || cp > 0x10ffff || (cp >= 0xd800 && cp < 0xe000)) return false;
        out += (wchar_t)cp, i += n + 1;
    }
    return true;
}
// A code point a wchar_t into UTF-8.
static inline std::string to_utf8(const std::wstring& w) {
    std::string s;
    for (wchar_t wc : w) {
        const uint32_t c = (uint32_t)wc;
        if (c < 0x80) s += (char)c;
        else if (c < 0x800) s += (char)(0xc0 | c >> 6), s += (char)(0x80 | (c & 0x3f));
        else if (c < 0x10000) s += (char)(0xe0 | c >> 12), s += (char)(0x80 | (c >> 6 & 0x3f)), s += (char)(0x80 | (c & 0x3f));
        else s += (char)(0xf0 | c >> 18), s += (char)(0x80 | (c >> 12 & 0x3f)), s += (char)(0x80 | (c >> 6 & 0x3f)), s += (char)(0x80 | (c & 0x3f));
    }
    return s;
}
#endif
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

// why with the path of the one file it is about, for the app to show ("<why>: <path>"), until this thread's next call. The
// path is the file system's: a byte that isn't UTF-8, a control or bidi character or a line break becomes '?', and a longer
// one keeps its last 400 bytes.
inline const char* with_path(const char* why, const std::string& p) {
    std::string s;
    for (size_t i = 0; i < p.size();) {
        const unsigned char c = (unsigned char)p[i];
        const size_t n = c < 0x80 ? 1 : (c & 0xe0) == 0xc0 ? 2 : (c & 0xf0) == 0xe0 ? 3 : (c & 0xf8) == 0xf0 ? 4 : 0;
        uint32_t cp = n == 1 ? c : c & (0x7f >> n);
        bool ok = n && i + n <= p.size();
        for (size_t k = 1; ok && k < n; ++k) ok = ((unsigned char)p[i + k] & 0xc0) == 0x80, cp = cp << 6 | (p[i + k] & 0x3f);
        static const uint32_t least[] = {0, 0, 0x80, 0x800, 0x10000};
        const bool whole = ok && cp >= least[n] && cp <= 0x10ffff && (cp < 0xd800 || cp >= 0xe000);
        const bool shown = whole && cp >= 0x20 && (cp < 0x7f || cp >= 0xa0) && cp != 0x61c && (cp < 0x200e || cp > 0x200f) && (cp < 0x2028 || cp > 0x202e)
                           && (cp < 0x2066 || cp > 0x2069);
        if (shown) s.append(p, i, n);
        else s += '?';
        i += whole ? n : 1;
    }
    if (s.size() > 400) {
        size_t cut = s.size() - 400;
        while (((unsigned char)s[cut] & 0xc0) == 0x80) ++cut;
        s = "..." + s.substr(cut);
    }
    static thread_local char b[512];
    snprintf(b, sizeof b, "%s: %s", why, s.c_str());
    return b;
}

// The stamp lines sig= covers, as ScsKiller.Signed writes them: exe_size=, exe_time=, then pid= and pid_time= when pid= is
// there, then risk= when it is there, then the others and the traversal_ lines (traversal_lines).
inline std::wstring signed_stamp(const std::wstring& size, const std::wstring& time, const std::wstring& pid, const std::wstring& pid_time, const std::wstring& risk,
                                 const std::wstring& vendor = L"", const std::wstring& overlay = L"", const std::wstring& play_s = L"", const std::wstring& tag = L"",
                                 const std::wstring& traversal = L"") {
    std::wstring s = L"exe_size=" + size + L"\r\nexe_time=" + time + L"\r\n";
    if (!pid.empty()) s += L"pid=" + pid + L"\r\npid_time=" + pid_time + L"\r\n";
    if (!risk.empty()) s += L"risk=" + risk + L"\r\n";
    if (!vendor.empty()) s += L"vendor=" + vendor + L"\r\n";
    if (!overlay.empty()) s += L"overlay=" + overlay + L"\r\n";
    if (!play_s.empty()) s += L"ab_play_s=" + play_s + L"\r\n";
    if (!tag.empty()) s += L"ab_tag=" + tag + L"\r\n";
    return s + traversal;
}

// traversal.h's keys, in the order ScsKiller.TraversalKeys writes them: sig= covers their lines, so a hand edit can't turn the
// fix on. get(key): the ini's value, "" without the line.
template <class Get> std::wstring traversal_lines(Get get) {
    static const wchar_t* const keys[] = {L"suballocate", L"buffers", L"textures", L"upload", L"readback", L"uav_buffers", L"uav_textures", L"reuse_frames",
                                          L"reuse_ms", L"heap_mb", L"spare_heaps", L"max_pool_mb", L"max_resource_mb", L"probe_ms", L"dred", L"zeroed", L"zeroed_textures",
                                          L"zeroed_upload", L"zeroed_reuse", L"copy_high", L"test_budget_mb"};
    std::wstring s;
    for (auto* k : keys) {
        const std::wstring key = std::wstring(L"traversal_") + k, v = get(key.c_str());
        if (!v.empty()) s += key + L"=" + v + L"\r\n";
    }
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
#ifdef _WIN32
    const int n = bytes.empty() ? 0 : MultiByteToWideChar(CP_ACP, 0, bytes.data(), (int)bytes.size(), text.data(), (int)text.size());
#else
    // the app writes it as UTF-8: bytes that aren't read as empty
    const int n = from_utf8(bytes, text) ? (int)text.size() : 0;
#endif
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

// The anti-cheat markers of the admission's folder listings (admit.h, admit_posix.h).
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
    L"NEP2.dll", L"NEPCleaner.exe",
    L"BlackCall.aes", L"BlackCall64.aes", L"BlackCat64.sys", L"HShield", L"PunkBuster", L"PnkBstrA.exe", L"pbsvc.exe", L"pbsv.dll",
    L"equ8_conf.json", L".build.info", L".product.db", L"Warframe.x64.exe", L"ONCE_HUMAN.exe", L"forzahorizon5.exe", L"forzahorizon6.exe",
    L"RobloxPlayerBeta.exe", L"RobloxPlayerBeta.dll", L"RobloxPlayerLauncher.exe", L"RobloxPlayerInstaller.exe",
    L"arbiter.dll", L"gameguard.des", L"DenuvoAC", L"denuvo-anti-cheat.sys", L"denuvo-anti-cheat-runtime.dll",
    L"denuvo-anti-cheat-update-service.exe", L"Denuvo Anti-Cheat Installer.exe", L"Elytra", L"Elytra.exe", L"Elytra-Setup.exe", L"*.xem", L"*_BE.exe"};
static const size_t kEasyAntiCheatMarkers = 5;  // the list's first entries

static std::string lower_az(std::string s) {
    for (auto& c : s)
        if (c >= 'A' && c <= 'Z') c += 'a' - 'A';
    return s;
}

// A text VDF as Steam writes it (libraryfolders.vdf, appmanifest_*.acf): quoted strings with backslash escapes and braces,
// nothing else. A node is a "key" "value" pair or a "key" { ... } object, in file order with duplicate keys kept.
struct Vdf {
    std::string key, value;
    bool object = false;
    std::vector<Vdf> kids;
};

static void vdf_space(const std::string& t, size_t& i) {
    while (i < t.size() && (t[i] == ' ' || t[i] == '\t' || t[i] == '\r' || t[i] == '\n')) ++i;
}

static bool vdf_string(const std::string& t, size_t& i, std::string& s) {
    if (i >= t.size() || t[i] != '"') return false;
    s.clear();
    for (++i; i < t.size() && t[i]; ++i) {
        if (t[i] == '"') return ++i, true;
        if (t[i] != '\\') { s += t[i]; continue; }
        if (++i >= t.size() || !t[i]) return false;
        s += t[i] == 'n' ? '\n' : t[i] == 't' ? '\t' : t[i] == 'r' ? '\r' : t[i];
    }
    return false;
}

// The pairs up to the end of the text (depth 0) or the object's closing brace.
static bool vdf_pairs(const std::string& t, size_t& i, std::vector<Vdf>& out, int depth) {
    for (;;) {
        vdf_space(t, i);
        if (i >= t.size()) return !depth;
        if (t[i] == '}') return depth && (++i, true);
        Vdf n;
        if (!vdf_string(t, i, n.key)) return false;
        vdf_space(t, i);
        if (i < t.size() && t[i] == '{') {
            if (depth >= 32) return false;
            n.object = true, ++i;
            if (!vdf_pairs(t, i, n.kids, depth + 1)) return false;
        } else if (!vdf_string(t, i, n.value)) return false;
        out.push_back(std::move(n));
    }
}

// The file's one top-level object, named root (any case of A-Z): false when the text isn't that.
static bool vdf_root(const std::string& t, const char* root, Vdf& top) {
    std::vector<Vdf> all;
    size_t i = 0;
    if (!vdf_pairs(t, i, all, 0) || all.size() != 1 || !all[0].object || lower_az(all[0].key) != root) return false;
    top = std::move(all[0]);
    return true;
}

// The one string value of key directly in o (any case of A-Z): false when it is missing, an object or there twice.
static bool vdf_one(const Vdf& o, const char* key, std::string& v) {
    int n = 0;
    for (auto& k : o.kids) {
        if (lower_az(k.key) != key) continue;
        if (k.object || n++) return false;
        v = k.value;
    }
    return n == 1;
}

static bool all_digits(const std::string& s) { return !s.empty() && s.find_first_not_of("0123456789") == std::string::npos; }
static const uint32_t kValveVac[] = {10, 80, 240, 300, 320, 440, 500, 550, 570, 730, 4000, 1422450};  // Games.Vac.Valve
// vac_of's findings that Steam has no info about the app, compared by address
static const char kVacNoSteam[] = "VAC: Steam's folder is unknown";
static const char kVacNoAppinfo[] = "VAC: Steam has no appinfo.vdf";
static const char kVacNotListed[] = "VAC: the game isn't in Steam's appinfo.vdf";
static const char kVacNoInfo[] = "VAC: Steam has no info about the game";
// A steam_appid.txt naming an app Steam has no info about (Games.Vac.Named): refused, but a risk session may record it.
static const char* named_vac(const char* why) {
    return why == kVacNoSteam || why == kVacNoAppinfo || why == kVacNotListed || why == kVacNoInfo ? "VAC (Steam has no info about the app steam_appid.txt names)" : why;
}

// A binary KeyValues object (appinfo.vdf's app entries), walked with every read bounded: keys are inline names (v28) or
// u32 indexes into the file's string table (v29). Finds root/[appinfo/]common and its category object. False: malformed.
struct Kv {
    const std::string& b;
    size_t at;
    const std::vector<std::string>* names;  // v29's string table; null: v28
    bool common = false, category = false, vac = false;
    bool key(std::string& out) {
        if (names) {
            uint32_t i;
            if (at + 4 > b.size()) return false;
            memcpy(&i, b.data() + at, 4), at += 4;
            if (i >= names->size()) return false;
            return out = (*names)[i], true;
        }
        size_t z = b.find('\0', at);
        if (z == std::string::npos) return false;
        return out = b.substr(at, z - at), at = z + 1, true;
    }
    // path: 0 root, 1 appinfo, 2 common, 3 common/category, 4 anything else
    bool object(int path, int depth) {
        if (depth > 32) return false;
        for (;;) {
            if (at >= b.size()) return false;
            uint8_t t = (uint8_t)b[at++];
            if (t == 8) return true;
            std::string k;
            if (!key(k)) return false;
            if (path == 3 && !_stricmp(k.c_str(), "category_8")) vac = true;
            switch (t) {
            case 0: {
                int sub = path <= 1 && !_stricmp(k.c_str(), "common") ? 2 : path == 0 && !_stricmp(k.c_str(), "appinfo") ? 1
                        : path == 2 && !_stricmp(k.c_str(), "category") ? 3 : 4;
                if (sub == 2) common = true;
                if (sub == 3) category = true;
                if (!object(sub, depth + 1)) return false;
                break;
            }
            case 1: {
                size_t z = b.find('\0', at);
                if (z == std::string::npos) return false;
                at = z + 1;
                break;
            }
            case 2: case 3: case 4: case 6: at += 4; break;
            case 7: case 10: at += 8; break;
            default: return false;
            }
            if (at > b.size()) return false;
        }
    }
};
