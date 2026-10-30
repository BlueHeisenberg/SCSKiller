// The recorder's admission on Linux (the Vulkan layer's, libVkLayer_scskiller.so): admit.h's checks, in its order, on
// POSIX paths. Paths are the bytes as the kernel names them, folders with a trailing '/'. What the app compares
// (ScsKiller.FileKey, ScsKiller.Signed on Linux) is made as admit.h makes it under Wine: keys of the file's identity there,
// so the same ini signs on both. The includer defines admit_dir(): the folder of the exe and its ini.
// Under Wine (Proton) the process is Wine's loader: the exe is the Windows exe its command line names, at its Unix path
// through Wine's drives, else the one Wine mapped it from (ProcessTree.ImagePath).
#pragma once
#include <dirent.h>
#include <dlfcn.h>
#include <fcntl.h>
#include <fnmatch.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>
#include <algorithm>
#include <cctype>
#include <cerrno>
#include <chrono>
#include <climits>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>
#include "admission.h"

extern char** environ;

static const std::string& admit_dir();

static long long unix_ms() { return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count(); }

// SHA-1 (FIPS 180-4), for the keys and the HMAC: nothing the layer links but libc.
struct Sha1 {
    uint32_t h[5] = {0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476, 0xc3d2e1f0};
    unsigned char block[64];
    uint64_t length = 0;
    static uint32_t rol(uint32_t x, int n) { return x << n | x >> (32 - n); }
    void compress() {
        uint32_t w[80];
        for (int i = 0; i < 16; ++i) w[i] = (uint32_t)block[4 * i] << 24 | block[4 * i + 1] << 16 | block[4 * i + 2] << 8 | block[4 * i + 3];
        for (int i = 16; i < 80; ++i) w[i] = rol(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
        uint32_t a = h[0], b = h[1], c = h[2], d = h[3], e = h[4];
        for (int i = 0; i < 80; ++i) {
            const uint32_t f = i < 20 ? (b & c) | (~b & d) : i < 40 ? b ^ c ^ d : i < 60 ? (b & c) | (b & d) | (c & d) : b ^ c ^ d;
            const uint32_t k = i < 20 ? 0x5a827999 : i < 40 ? 0x6ed9eba1 : i < 60 ? 0x8f1bbcdc : 0xca62c1d6;
            const uint32_t t = rol(a, 5) + f + e + k + w[i];
            e = d, d = c, c = rol(b, 30), b = a, a = t;
        }
        h[0] += a, h[1] += b, h[2] += c, h[3] += d, h[4] += e;
    }
    void add(const void* p, size_t n) {
        for (const unsigned char* s = (const unsigned char*)p; n--; ++s) {
            block[length++ % 64] = *s;
            if (length % 64 == 0) compress();
        }
    }
    void done(unsigned char out[20]) {
        const uint64_t bits = length * 8;
        const unsigned char one = 0x80, zero = 0;
        add(&one, 1);
        while (length % 64 != 56) add(&zero, 1);
        for (int i = 7; i >= 0; --i) {
            const unsigned char b = (unsigned char)(bits >> (8 * i));
            add(&b, 1);
        }
        for (int i = 0; i < 20; ++i) out[i] = (unsigned char)(h[i / 4] >> (24 - 8 * (i % 4)));
    }
};

static std::string hex(const unsigned char* p, size_t n) {
    std::string s;
    for (size_t i = 0; i < n; ++i) s += "0123456789abcdef"[p[i] >> 4], s += "0123456789abcdef"[p[i] & 15];
    return s;
}

static std::string sha1_hex(const std::string& bytes) {
    Sha1 s;
    unsigned char d[20];
    s.add(bytes.data(), bytes.size()), s.done(d);
    return hex(d, 20);
}

static std::string hmac_sha1_hex(const std::string& key, const std::string& msg) {
    unsigned char k[64] = {}, d[20];
    if (key.size() > 64) {
        Sha1 s;
        s.add(key.data(), key.size()), s.done(k);
    } else memcpy(k, key.data(), key.size());
    unsigned char pad[64];
    Sha1 inner, outer;
    for (int i = 0; i < 64; ++i) pad[i] = k[i] ^ 0x36;
    inner.add(pad, 64), inner.add(msg.data(), msg.size()), inner.done(d);
    for (int i = 0; i < 64; ++i) pad[i] = k[i] ^ 0x5c;
    outer.add(pad, 64), outer.add(d, 20), outer.done(d);
    return hex(d, 20);
}

// UTF-16LE of the code points, as .NET's Encoding.Unicode writes a string.
static std::string utf16le(const std::wstring& w) {
    std::string s;
    auto unit = [&](uint32_t u) { s += (char)(u & 0xff), s += (char)(u >> 8); };
    for (wchar_t wc : w) {
        const uint32_t c = (uint32_t)wc;
        if (c < 0x10000) unit(c);
        else unit(0xd800 + ((c - 0x10000) >> 10)), unit(0xdc00 + ((c - 0x10000) & 0x3ff));
    }
    return s;
}

// The path's bytes as code points; "" when they aren't UTF-8 (admission refuses such a path: utf8_path).
static std::wstring widen(const std::string& s) {
    std::wstring w;
    return from_utf8(s, w) ? w : L"";
}

static bool utf8_path(const std::string& s) {
    std::wstring w;
    return from_utf8(s, w);
}

// The environment, read once as the layer loads: what the recorder folder, Steam's and Wine's prefix are found by, the
// driver-cache variables, and the tests' variables (honoured only in a process exporting SCSKiller_WarmHost: the selftest,
// never a game).
struct LoadEnv {
    std::string home, xdg_data, wine_prefix, test_recorder, test_steam, cache;
    bool has_test_recorder = false, has_test_steam = false;
    LoadEnv() {
        auto get = [](const char* n, std::string& v) {
            const char* s = getenv(n);
            return s ? (v = s, true) : false;
        };
        get("HOME", home), get("XDG_DATA_HOME", xdg_data), get("WINEPREFIX", wine_prefix);
        void* self = dlopen(nullptr, RTLD_LAZY);
        const bool test = self && dlsym(self, "SCSKiller_WarmHost");
        if (self) dlclose(self);
        if (test) has_test_recorder = get("SCSKILLER_TEST_RECORDER_DIR", test_recorder), has_test_steam = get("SCSKILLER_TEST_STEAM_ROOT", test_steam);
        // the driver-cache variables the game runs with (Steam redirects them into steamapps/shadercache/<appid>): the warm
        // replays with the same ones. Named as on Windows, case and all.
        for (char** e = environ; e && *e; ++e)
            for (const char* p : {"__GL_SHADER_DISK_CACHE", "AMD_VK_PIPELINE_CACHE_", "AMD_VK_USE_PIPELINE_CACHE"})
                if (!strncasecmp(*e, p, strlen(p)) && strlen(*e) < 4096) cache += "#cache," + std::string(*e) + "\n";
    }
};
static LoadEnv g_load_env;

static std::string base_name(const std::string& p) { return p.substr(p.find_last_of('/') + 1); }

static std::string real_path(const std::string& p) {
    char* r = realpath(p.c_str(), nullptr);
    std::string s = r ? r : "";
    free(r);
    return s;
}

// A regular file's bytes, read whole: false when it can't be opened, isn't a regular file (a FIFO never blocks it), fails
// in between or holds more than cap. errno is the open's when that failed.
static bool read_file(const std::string& path, std::string& s, size_t cap = 1 << 20) {
    s.clear();
    const int fd = open(path.c_str(), O_RDONLY | O_NONBLOCK | O_CLOEXEC);
    if (fd < 0) return false;
    struct stat st;
    bool ok = !fstat(fd, &st) && S_ISREG(st.st_mode);
    char b[65536];
    for (ssize_t n; ok && (n = read(fd, b, sizeof b));) {
        if (n < 0 && errno == EINTR) continue;
        ok = n > 0 && s.size() + n <= cap;
        if (ok) s.append(b, (size_t)n);
    }
    close(fd);
    return ok;
}

static std::string read_all(const std::string& path, size_t cap = 1 << 20) {
    std::string s;
    return read_file(path, s, cap) ? s : "";
}

// A folder's entry names but . and .. (at most 100,000); false when it can't be listed whole (errno opendir's when that
// failed).
static bool list_dir(const std::string& dir, std::vector<std::string>& names) {
    names.clear();
    DIR* d = opendir(dir.c_str());
    if (!d) return false;
    bool ok = true;
    for (;;) {
        errno = 0;
        const dirent* e = readdir(d);
        if (!e) { ok = errno == 0; break; }
        if (names.size() >= 100000) { ok = false; break; }
        if (strcmp(e->d_name, ".") && strcmp(e->d_name, "..")) names.push_back(e->d_name);
    }
    closedir(d);
    return ok;
}

// The Wine prefix as Wine finds it: $WINEPREFIX, else ~/.wine. "" without either.
static std::string wine_prefix_dir() {
    return !g_load_env.wine_prefix.empty() ? g_load_env.wine_prefix : g_load_env.home.empty() ? "" : g_load_env.home + "/.wine";
}

// A Wine path's Linux one, "" when it names no drive: Z: is the root, any other drive goes through the prefix's dosdevices
// link.
static std::string unix_path(const std::string& p) {
    if (p.size() < 3 || !isalpha((unsigned char)p[0]) || p[1] != ':' || (p[2] != '\\' && p[2] != '/')) return "";
    std::string rest = p.substr(2);
    std::replace(rest.begin(), rest.end(), '\\', '/');
    const char drive = (char)tolower((unsigned char)p[0]);
    if (drive == 'z') return rest;
    const std::string prefix = wine_prefix_dir();
    return prefix.empty() ? "" : prefix + "/dosdevices/" + drive + ":" + rest;
}

// Wine runs every Windows process as its loader (ProcessTree.IsWine).
static bool is_wine(const std::string& image) {
    const std::string n = base_name(image);
    return n == "wine" || n == "wine64" || n == "wine-preloader" || n == "wine64-preloader";
}

// A path as Wine opens it, real: a name that isn't there is the one entry of its folder that matches it in any case
// (lower_az). "" when none or several do, or it can't be resolved.
static std::string wine_real_path(const std::string& path) {
    if (std::string r = real_path(path); !r.empty() || path.empty() || path[0] != '/') return r;
    std::string at;
    std::vector<std::string> names;
    for (size_t i = 1; i <= path.size();) {
        const size_t end = std::min(path.find('/', i), path.size());
        std::string part = path.substr(i, end - i);
        i = end + 1;
        if (part.empty() || part == ".") continue;
        if (part == "..") return "";
        struct stat st;
        if (lstat((at + "/" + part).c_str(), &st)) {
            if (errno != ENOENT || !list_dir(at.empty() ? "/" : at, names)) return "";
            std::string found;
            for (auto& n : names)
                if (lower_az(n) == lower_az(part)) {
                    if (!found.empty()) return "";
                    found = n;
                }
            if (found.empty()) return "";
            part = found;
        }
        at += "/" + part;
    }
    return real_path(at);
}

// The exe's path: the executable's, or under Wine the file its command line names through Wine's drives (.exe added when
// left out), only when that is the file Wine mapped by that name and no other file by that name is mapped: the command
// line is the caller's to set. "" when it can't be had or isn't. named: that file's path through Wine's drives, links
// left in ("" when not under Wine).
static std::string module_path_uncached(std::string& named) {
    std::string image(PATH_MAX, '\0');
    const ssize_t n = readlink("/proc/self/exe", image.data(), image.size());
    if (n <= 0 || n >= (ssize_t)image.size()) return "";
    image.resize(n);
    if (!is_wine(image)) return image;
    const std::string cmdline = read_all("/proc/self/cmdline", 1 << 16), argv0 = cmdline.substr(0, cmdline.find('\0'));
    std::string name = argv0.substr(argv0.find_last_of("\\/") + 1);
    if (name.empty()) return "";
    if (name.find('.') == std::string::npos) name += ".exe";
    std::string local = unix_path(argv0);
    if (local.empty()) return "";
    named = local.substr(0, local.find_last_of('/') + 1) + name;
    local = wine_real_path(named);
    struct stat st;
    if (local.empty() || stat(local.c_str(), &st) || !S_ISREG(st.st_mode) || !st.st_size) return "";
    // its device and inode as /proc/self/maps shows them (on btrfs another device than stat's): a page of it mapped to look
    const int fd = open(local.c_str(), O_RDONLY | O_NONBLOCK | O_CLOEXEC);
    void* page = fd < 0 ? MAP_FAILED : mmap(nullptr, 1, PROT_READ, MAP_PRIVATE, fd, 0);
    if (fd >= 0) close(fd);
    if (page == MAP_FAILED) return "";
    const std::string maps = read_all("/proc/self/maps", 64 << 20);
    munmap(page, 1);
    char own[32];
    snprintf(own, sizeof own, "%lx-", (unsigned long)(uintptr_t)page);
    std::string id;
    std::vector<std::string> same;
    for (size_t at = 0; at < maps.size();) {
        const size_t end = std::min(maps.find('\n', at), maps.size());
        const std::string line = maps.substr(at, end - at);
        at = end + 1;
        char dev[16];
        unsigned long long ino;
        int path_at = 0;
        if (sscanf(line.c_str(), "%*s %*s %*s %15s %llu %n", dev, &ino, &path_at) < 2) continue;
        const std::string key = std::string(dev) + " " + std::to_string(ino);
        if (!line.compare(0, strlen(own), own)) id = key;
        else if (path_at > 0 && line[path_at] == '/' && !strcasecmp(base_name(line.substr(path_at)).c_str(), name.c_str())) same.push_back(key);
    }
    if (id.empty() || same.empty()) return "";
    for (auto& k : same)
        if (k != id) return "";
    return local;
}

static std::string module_path(std::string* named = nullptr) {
    static std::string argv;
    static const std::string exe = module_path_uncached(argv);
    if (named) *named = argv;
    return exe;
}

// A path's key: SHA-1 in hex of its UTF-16LE, A-Z lowered (recorder_key.h's path_key).
static std::string path_key(const std::string& path) { return sha1_hex(utf16le(widen(lower_az(path)))); }

// A file's or folder's key, as ScsKiller.FileKey makes it on Linux: SHA-1 in hex of the identity the proxy has for it under
// Wine (GameFiles.WineFileId: "wine-ino", then the inode of its real path, links resolved), else path_key of its real path,
// else of the path as given.
static std::string file_key(const std::string& path) {
    const std::string r = real_path(path);
    struct stat st;
    if (r.empty() || stat(r.c_str(), &st)) return path_key(r.empty() ? path : r);
    unsigned char id[24] = {'w', 'i', 'n', 'e', '-', 'i', 'n', 'o'};
    for (int i = 0; i < 8; ++i) id[8 + i] = (unsigned char)((uint64_t)st.st_ino >> (8 * i));
    return sha1_hex(std::string((const char*)id, sizeof id));
}

// The folder's real path with a trailing '/'; "" when it can't be had.
static std::string final_dir(const std::string& dir) {
    const std::string r = real_path(dir);
    return r.empty() ? "" : r.back() == '/' ? r : r + "/";
}

// The installed copy's data folder, as .NET's LocalApplicationData on Linux: $XDG_DATA_HOME when absolute, else
// $HOME/.local/share. "" without a home.
static std::string local_app_data() {
    if (!g_load_env.xdg_data.empty() && g_load_env.xdg_data[0] == '/') return g_load_env.xdg_data;
    return g_load_env.home.empty() ? "" : g_load_env.home + "/.local/share";
}

// The data folder of the copy that placed the recorder in <dir> (trailing '/'): the parent of the armed/ path its
// scskiller.ledger names (absolute, one line). False: there is none (the installed copy's). There but unusable: true with "".
static bool stamped_recorder_dir(const std::string& dir, std::string& out) {
    out.clear();
    const std::string file = dir + "scskiller.ledger";
    struct stat st;
    if (lstat(file.c_str(), &st)) return errno != ENOENT && errno != ENOTDIR;
    if (!S_ISREG(st.st_mode)) return true;  // the app writes a plain file
    const int fd = open(file.c_str(), O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC);
    if (fd < 0) return true;
    char b[4 * 4096];
    const ssize_t got = fstat(fd, &st) || !S_ISREG(st.st_mode) ? -1 : read(fd, b, sizeof b);
    close(fd);
    if (got <= 0 || got == (ssize_t)sizeof b) return true;
    size_t n = (size_t)got;
    while (n && (b[n - 1] == '\r' || b[n - 1] == '\n' || b[n - 1] == ' ')) --n;
    std::string ledger(b, n);
    if (const std::string local = unix_path(ledger); !local.empty()) ledger = local;  // the D3D proxy's form, which InstallVulkan writes too
    // as Path.GetFullPath writes it: absolute, segments none empty, "." or "..", no NUL
    bool ok = ledger.size() > 1 && ledger[0] == '/' && ledger.find('\0') == std::string::npos;
    size_t parts = 0;
    for (size_t at = 1; ok && at <= ledger.size(); ++parts) {
        size_t end = ledger.find('/', at);
        if (end == std::string::npos) end = ledger.size();
        const std::string seg = ledger.substr(at, end - at);
        ok = !seg.empty() && seg != "." && seg != "..";
        at = end + 1;
    }
    const size_t cut = ledger.find_last_of('/');
    if (ok && parts >= 2 && !strcasecmp(ledger.c_str() + cut + 1, "armed")) out = ledger.substr(0, cut);
    return true;
}

// The recorder folder (recorder.key, recorder.revoked, refused/, vulkan/) of the copy whose recorder is in <dir>:
// stamped_recorder_dir's, else the installed copy's <local app data>/SCSKiller; "" when it can't be had.
static thread_local const std::string* t_rec;  // while admission decides: the recorder folder it read first

static std::string recorder_dir_now(const std::string& dir) {
    if (g_load_env.has_test_recorder) return g_load_env.test_recorder;
    if (dir.empty()) return "";
    std::string stamped;
    if (stamped_recorder_dir(dir, stamped)) return stamped;
    const std::string local = local_app_data();
    return local.empty() ? "" : local + "/SCSKiller";
}

// recorder_dir_now's, the same one for the whole decision (t_rec): a ledger changed in between can't move the checks
// after the signature's, or the recording, to another folder.
static std::string recorder_dir(const std::string& dir) { return t_rec ? *t_rec : recorder_dir_now(dir); }

// sig= as recorder_key.h's recorder_sig makes it: the HMAC-SHA1 under key of the message's UTF-16LE.
static std::string recorder_sig(const std::string& key, const std::string& exe_key, const std::string& dir_key, const std::wstring& stamp, const std::wstring& root,
                                const std::string& root_key) {
    if (key.empty()) return "";
    const std::wstring msg = widen(exe_key) + L"\n" + widen(dir_key) + L"\n" + stamp + L"root=" + root + L"\n" + widen(root_key) + L"\n";
    return hmac_sha1_hex(key, utf16le(msg));
}

// The anti-cheat client libraries mapped by now, by file name: the Windows modules (under Wine) and their Linux builds,
// also one deleted since it was mapped. {"?"}: the mappings can't be read whole.
static std::vector<std::string> anti_cheat_loaded() {
    std::vector<std::string> found;
    std::string maps;
    if (!read_file("/proc/self/maps", maps, 64 << 20)) return {"?"};
    for (size_t at = 0; at < maps.size();) {
        size_t end = maps.find('\n', at);
        if (end == std::string::npos) end = maps.size();
        const size_t slash = maps.find('/', at);
        std::string n = slash < end ? lower_az(base_name(maps.substr(slash, end - slash))) : "";
        if (n.size() > 10 && !n.compare(n.size() - 10, 10, " (deleted)")) n.resize(n.size() - 10);
        at = end + 1;
        if (n.empty() || std::find(found.begin(), found.end(), n) != found.end()) continue;
        bool hit = false;
        for (const char* m : {"easyanticheat_x64.dll", "easyanticheat_eos.dll", "beclient_x64.dll", "beclient.dll", "mhypbase.dll", "core_ets2mp.dll",
                              "core_atsmp.dll", "denuvo-anti-cheat-runtime.dll"})
            hit |= n == m;
        for (const char* p : {"easyanticheat", "libeasyanticheat", "beclient", "libbeclient"})
            hit |= !n.compare(0, strlen(p), p) && n.size() > 3 && !n.compare(n.size() - 3, 3, ".so");
        if (hit) found.push_back(n);
    }
    return found;
}

// The exe's path has a "Riot Games" folder (GameFiles.RiotGames).
static bool riot_games() {
    const std::string exe = module_path();
    return exe.empty() || lower_az(exe).find("/riot games/") != std::string::npos;
}

static bool is_marker(const char* name, const std::vector<std::string>& markers) {
    const size_t n = strlen(name);
    for (auto& m : markers)
        if (m[0] == '*' ? n >= m.size() - 1 && !strcasecmp(name + n - (m.size() - 1), m.c_str() + 1) : !strcasecmp(name, m.c_str())) return true;
    return false;
}

static thread_local const std::string* t_ini;

// scskiller.ini's value of key, from the bytes admission read ("" when missing or longer than 1022).
static std::wstring ini_string(const wchar_t* key) {
    if (!t_ini) return L"";
    std::wstring v = ini_value(*t_ini, key);
    return v.size() < 1022 ? v : L"";
}

// False: an addition in markers= that isn't a plain name (empty, a path, a wildcard other than a leading '*').
static bool anti_cheat_markers(std::vector<std::string>& out) {
    out.clear();
    for (auto m : kAntiCheatMarkers) out.push_back(to_utf8(m));
    const std::wstring s = t_ini ? ini_value(*t_ini, L"markers") : L"";
    if (s.size() >= 4094) return false;
    if (s.empty()) return true;
    for (size_t at = 0;;) {
        size_t bar = s.find(L'|', at);
        std::wstring m = s.substr(at, bar == std::wstring::npos ? std::wstring::npos : bar - at);
        if (m.empty() || m.find_first_of(L"\\/:?\"<>") != std::wstring::npos || m.find(L'*', 1) != std::wstring::npos || m == L"*") return false;
        out.push_back(to_utf8(m));
        if (bar == std::wstring::npos) return true;
        at = bar + 1;
    }
}

// The markers among dir's entries, by name (at most 100,000 read): all of them, or the first. {"?"}: more entries than that,
// or a listing that fails before it ends.
static std::vector<std::string> anti_cheat_in(const std::string& dir, const std::vector<std::string>& markers, bool all) {
    DIR* d = opendir(dir.c_str());
    if (!d) return {"?"};
    std::vector<std::string> found;
    bool listed = true;
    for (size_t read = 0;;) {
        errno = 0;
        const dirent* e = readdir(d);
        if (!e) { listed = errno == 0; break; }
        if (++read > 100000) { listed = false; break; }
        if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, "..")) continue;
        if (is_marker(e->d_name, markers)) {
            found.push_back(e->d_name);
            if (!all) break;
        }
    }
    closedir(d);
    if (!listed) found.assign(1, "?");
    return found;
}

static bool ini_number(const wchar_t* key, uint64_t& out) {
    std::wstring v = ini_string(key);
    if (v.empty() || v.size() >= 30 || !iswdigit(v[0])) return false;
    wchar_t* end;
    errno = 0;
    out = wcstoull(v.c_str(), &end, 10);
    return !*end && !errno;
}

// exe_size= and exe_time= (FILETIME, UTC, as .NET's ToFileTimeUtc of the mtime) are this process's exe as it is now.
static bool exe_as_checked() {
    const std::string exe = module_path();
    struct stat st;
    uint64_t size, time;
    if (exe.empty() || stat(exe.c_str(), &st) || !ini_number(L"exe_size", size) || !ini_number(L"exe_time", time)) return false;
    const uint64_t ft = ((uint64_t)st.st_mtim.tv_sec + 11644473600ull) * 10000000ull + (uint64_t)st.st_mtim.tv_nsec / 100;
    return size == (uint64_t)st.st_size && time == ft;
}

// pid= and pid_time= (its start in clock ticks since boot, as ProcessTree.Created on Linux) name this process.
static bool bound_here() {
    uint64_t pid, at;
    if (!ini_number(L"pid", pid) || !ini_number(L"pid_time", at) || pid != (uint64_t)getpid()) return false;
    const std::string stat = read_all("/proc/self/stat", 4096);
    const size_t close = stat.rfind(')');
    if (close == std::string::npos) return false;
    // after "pid (name)": state is field 3, starttime field 22
    size_t p = close + 1;
    for (int field = 3; field < 22 && p != std::string::npos; ++field) p = stat.find(' ', p + 1);
    return p != std::string::npos && strtoull(stat.c_str() + p + 1, nullptr, 10) == at;
}

// The install root's real path, as Wine opens it, with a trailing '/'; "" when root= is missing or can't be resolved. root=
// is relative to here (the folder's real path), or absolute: as the app writes it for Wine too, "..\.." steps or a drive's
// path (unix_path). Real, so the folders between here and the root are the ones walked whatever form root= has.
static std::string install_root(const std::string& here) {
    std::string v = to_utf8(ini_string(L"root"));
    if (v.empty()) return "";
    if (const std::string local = unix_path(v); !local.empty()) v = local;
    else if (v[0] != '/') std::replace(v.begin(), v.end(), '\\', '/');
    const std::string real = wine_real_path(v[0] == '/' ? v : here + v);
    return real.empty() || real.back() == '/' ? real : real + "/";
}

// here and each folder above it up to the install root; the root alone besides here when it isn't above it. Empty when
// the root is more than 64 folders up.
static std::vector<std::string> folders_to(const std::string& here, const std::string& root) {
    if (here.compare(0, root.size(), root)) return {here, root};
    std::vector<std::string> dirs;
    for (std::string d = here; dirs.size() < 64;) {
        dirs.push_back(d);
        if (d.size() <= root.size()) return dirs;
        d.resize(d.find_last_of('/', d.size() - 2) + 1);
    }
    return {};
}

static std::string no_slash(const std::string& d) { return d.size() > 1 && d.back() == '/' ? d.substr(0, d.size() - 1) : d; }

// <recorder folder>/refused/<this exe's file_key>; "" when it can't be had.
static std::string refused_path() {
    const std::string exe = module_path(), dir = recorder_dir(admit_dir());
    return exe.empty() || dir.empty() ? "" : dir + "/refused/" + file_key(exe);
}

// A pass-through writes nothing in the game folder: its reason goes to the app's recorder folder ("unix_ms why"), where the
// app shows it on the game's page; an admitted run deletes it.
static void refused(const char* why) {
    const std::string file = refused_path();
    if (file.empty()) return;
    if (!why) return (void)unlink(file.c_str());
    const std::string dir = file.substr(0, file.find_last_of('/'));
    mkdir(dir.substr(0, dir.find_last_of('/')).c_str(), 0777), mkdir(dir.c_str(), 0777);
    const int fd = open(file.c_str(), O_WRONLY | O_CREAT | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC, 0666);
    if (fd < 0) return;
    struct stat st;
    if (fstat(fd, &st) || !S_ISREG(st.st_mode) || st.st_nlink != 1 || ftruncate(fd, 0)) return (void)close(fd);
    char b[1024];
    const int n = snprintf(b, sizeof b, "%lld %s", unix_ms(), why);
    if (write(fd, b, (size_t)std::clamp(n, 0, (int)sizeof b - 1)) < 0) {}
    close(fd);
}

static bool under(const std::string& path, std::string dir) {
    while (dir.size() > 1 && dir.back() == '/') dir.pop_back();
    return path.size() > dir.size() + 1 && path[dir.size()] == '/' && !path.compare(0, dir.size(), dir);
}

// An absolute path as text: repeated '/', "." and ".." folded, no link followed.
static std::string lexical(const std::string& p) {
    std::vector<std::string> parts;
    for (size_t i = 0; i <= p.size();) {
        const size_t end = std::min(p.find('/', i), p.size());
        const std::string part = p.substr(i, end - i);
        i = end + 1;
        if (part == "..") { if (!parts.empty()) parts.pop_back(); }
        else if (!part.empty() && part != ".") parts.push_back(part);
    }
    std::string s;
    for (auto& part : parts) s += "/" + part;
    return s.empty() ? "/" : s;
}

// path is dir or in it as text (lexical, any case of A-Z): a folder that doesn't exist can't be checked another way.
static bool holds_text(const std::string& dir, const std::string& path) {
    if (dir.empty() || dir[0] != '/' || path.empty()) return !path.empty();
    std::string d = lower_az(lexical(dir));
    if (d.back() != '/') d += '/';
    return !(lower_az(lexical(path)) + "/").compare(0, d.size(), d);
}

// Steam's folder, as SteamSource.NativeRoot: ~/.steam/root's target, else ~/.local/share/Steam. "" without a home.
static std::string steam_root() {
    if (g_load_env.has_test_steam) return g_load_env.test_steam;
    if (g_load_env.home.empty()) return "";
    const std::string r = real_path(g_load_env.home + "/.steam/root");
    return r.empty() ? g_load_env.home + "/.local/share/Steam" : r;
}

// The Steam app's VAC: a Valve VAC title, or appinfo.vdf's info about it marks VAC or can't be had. nullptr: no VAC.
static const char* vac_of(uint32_t app, const std::string& steam) {
    if (std::find(std::begin(kValveVac), std::end(kValveVac), app) != std::end(kValveVac)) return "VAC (a Valve game)";
    // appinfo.vdf: as admit.h reads it
    const int fd = open((steam + "/appcache/appinfo.vdf").c_str(), O_RDONLY | O_NONBLOCK | O_CLOEXEC);
    if (fd < 0 && errno == ENOENT) return kVacNoAppinfo;
    struct stat st;
    FILE* f = fd < 0 || fstat(fd, &st) || !S_ISREG(st.st_mode) ? nullptr : fdopen(fd, "rb");
    if (!f) {
        if (fd >= 0) close(fd);
        return "VAC: Steam's appinfo.vdf can't be read";
    }
    const int64_t flen = (int64_t)st.st_size;
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
            if (!a[0]) { if (hd[0] != 0x07564429 || (int64_t)ftello(f) <= table) why = kVacNotListed; break; }
            if (fread(a + 1, 4, 1, f) != 1) break;
            const int64_t left = (hd[0] == 0x07564429 ? table : flen) - (int64_t)ftello(f);
            if (a[1] > left) { why = "VAC: Steam's appinfo.vdf is damaged"; break; }
            if (a[0] != app) { if (fseeko(f, a[1], SEEK_CUR)) break; continue; }
            if (a[1] < 61 || a[1] > (16u << 20)) { why = "VAC: the game's entry in appinfo.vdf is damaged"; break; }
            entry.resize(a[1]);
            if (fread(entry.data(), 1, a[1], f) != a[1]) { entry.clear(), why = "VAC: Steam's appinfo.vdf can't be read"; }
            break;
        }
    if (!entry.empty() && hd[0] == 0x07564429) {
        uint32_t count = 0;
        std::string t;
        if (fseeko(f, table, SEEK_SET) || fread(&count, 4, 1, f) != 1 || flen - table > (64 << 20) || count > (1u << 24)) entry.clear();
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

// A Steam app id as Steam writes one (steam_appid.txt, a compatdata folder): decimal, 1 to 2^32-1. 0: not one.
static uint32_t app_id(const std::string& s) {
    if (s.empty() || s.size() > 10 || s.find_first_not_of("0123456789") != std::string::npos) return 0;
    const unsigned long long v = strtoull(s.c_str(), nullptr, 10);
    return v > UINT32_MAX ? 0 : (uint32_t)v;
}

static bool is_or_under(const std::string& path, const std::string& dir) { return path == dir || under(path, dir); }

// A Steam game's folder or prefix: what its resolution refuses with.
struct Owned {
    const char *held, *loop, *other;
};
static const Owned kPrefix = {"VAC: a dangling Steam prefix link holds the game's path", "VAC: a Steam prefix's links loop",
                              "VAC: a Steam prefix can't be resolved"};
static const Owned kFolder = {"VAC: a Steam game's folder that doesn't exist holds the game's path", "VAC: a Steam game folder's links loop",
                              "VAC: a Steam game's folder can't be resolved"};

// folder + name's real path in r. "" when it doesn't exist (ENOENT) and holds neither the exe's real path nor its argv path
// as text: the path itself, and for a dangling link, the link's text against its folder's real path (a drive that is gone,
// an uninstalled game). Else why it refuses.
static const char* owned_path(const std::string& folder, const std::string& name, const std::string& exe, const std::string& argv,
                              const Owned& why, std::string& r) {
    const std::string path = folder + name;
    char* p = realpath(path.c_str(), nullptr);
    const int err = errno;
    r = p ? p : "";
    free(p);
    if (!r.empty()) return nullptr;
    if (err == ELOOP) return why.loop;
    if (err != ENOENT) return why.other;
    if (holds_text(path, exe) || holds_text(path, argv)) return why.held;
    struct stat st;
    if (lstat(path.c_str(), &st)) return errno == ENOENT ? nullptr : why.other;
    std::string text(PATH_MAX, '\0');
    const ssize_t n = S_ISLNK(st.st_mode) ? readlink(path.c_str(), text.data(), text.size()) : -1;
    if (n <= 0 || n >= (ssize_t)text.size()) return why.other;
    text.resize(n);
    const std::string base = real_path(folder);
    if (text[0] != '/' && base.empty()) return why.other;
    const std::string target = text[0] == '/' ? text : base + "/" + text;
    return holds_text(target, exe) || holds_text(target, argv) ? why.held : nullptr;
}

// The way to the exe along its argv path as Wine opens it, links expanded one at a time: every real folder entered and
// every link passed (the drive's own link aside, from its real folder), with the name it was entered by, ending at the exe.
// A name of the argv path that isn't there matches in any case (wine_real_path), a link's text doesn't. Only the exe when
// not under Wine. false when it can't be followed.
static bool argv_walk(const std::string& argv, const std::string& exe, std::vector<std::pair<std::string, std::string>>& walk) {
    walk.clear();
    if (argv.empty()) return walk.push_back({exe, ""}), true;
    const std::string prefix = wine_prefix_dir(), drives = prefix.empty() ? "" : prefix + "/dosdevices/";
    const bool drive = !drives.empty() && !argv.compare(0, drives.size(), drives);
    std::string cur = drive ? real_path(drives) : "/";
    if (cur.empty()) return false;
    std::vector<std::pair<std::string, bool>> todo;  // the names still to enter, last first, and whether any case matches
    const auto ahead = [&](const std::string& p, bool any_case) {
        std::vector<std::pair<std::string, bool>> parts;
        for (size_t i = 0; i <= p.size();) {
            const size_t end = std::min(p.find('/', i), p.size());
            parts.push_back({p.substr(i, end - i), any_case});
            i = end + 1;
        }
        todo.insert(todo.end(), parts.rbegin(), parts.rend());
    };
    ahead(drive ? argv.substr(drives.size()) : argv, true);
    if (!drive) walk.push_back({cur, ""});
    bool quiet = drive;  // the drive's own link, in the prefix the game runs in
    std::vector<std::string> names;
    for (int links = 0; !todo.empty();) {
        auto [name, any_case] = todo.back();
        todo.pop_back();
        if (name.empty() || name == ".") continue;
        if (name == "..") {
            if (any_case) return false;
            cur = cur.substr(0, std::max<size_t>(cur.rfind('/'), 1));
            walk.push_back({cur, name});
            continue;
        }
        std::string next = (cur == "/" ? "" : cur) + "/" + name;
        struct stat st;
        if (lstat(next.c_str(), &st)) {
            if (errno != ENOENT || !any_case || !list_dir(cur, names)) return false;
            std::string found;
            for (auto& n : names)
                if (lower_az(n) == lower_az(name)) {
                    if (!found.empty()) return false;
                    found = n;
                }
            if (found.empty()) return false;
            name = found, next = (cur == "/" ? "" : cur) + "/" + name;
            if (lstat(next.c_str(), &st)) return false;
        }
        if (!quiet) walk.push_back({next, name});
        quiet = false;
        if (!S_ISLNK(st.st_mode)) {
            cur = next;
            continue;
        }
        std::string text(PATH_MAX, '\0');
        const ssize_t n = ++links > 40 ? -1 : readlink(next.c_str(), text.data(), text.size());
        if (n <= 0 || n >= (ssize_t)text.size()) return false;
        text.resize(n);
        if (text[0] == '/') cur = "/";
        ahead(text, false);
    }
    return !walk.empty() && walk.back().first == exe;
}

static bool reaches(const std::vector<std::pair<std::string, std::string>>& walk, const std::string& dir) {
    for (auto& step : walk)
        if (is_or_under(step.first, dir)) return true;
    return false;
}

// The walk is in a game folder of the drive C whose real path is drive: past it, the folder it goes on into isn't windows (any
// case), and the exe isn't in drive's windows either.
static bool in_game_of(const std::vector<std::pair<std::string, std::string>>& walk, const std::string& drive) {
    const auto first = [&](const std::string& p) { return lower_az(p.substr(drive.size() + 1, p.find('/', drive.size() + 1) - drive.size() - 1)); };
    if (under(walk.back().first, drive) && first(walk.back().first) == "windows") return false;
    for (size_t k = 0; k < walk.size(); ++k) {
        if (under(walk[k].first, drive)) return first(walk[k].first) != "windows";
        if (walk[k].first == drive) return k + 1 < walk.size() && lower_az(walk[k + 1].second) != "windows";
    }
    return false;
}

// Valve Anti-Cheat (Games.Vac), as admit.h's vac_now decides it, from the Steam that runs the game, once every library
// Steam lists is read whole: a library, manifest or prefix that can't be read refuses. libraryfolders.vdf is one
// "libraryfolders" object whose objects each hold one path (or, written before 2021, digit keys with a path), a manifest
// one "AppState" object with one appid and one installdir. The exe is the app's whose installdir holds it, or the
// id's whose compatdata folder, pfx or drive_c holds it, each through any link: its real path or any real folder on the
// way to it along its argv path (a link anywhere beneath them) counts, in a game folder of drive C for a prefix. A
// shortcut's id (2^31 and up) is a non-Steam game's, and so is an exe outside Steam's install and its libraries'
// steamapps folders (admit.h passes one outside every library). A game run in a Steam app's Wine prefix is that app's
// too, on any drive. A listed library or a game folder that doesn't exist (a drive not mounted, an uninstalled game) and
// a prefix link whose target doesn't exist are skipped when the game's path isn't in them as text.
static const char* vac_now_unguarded() {
    std::string argv_path;
    const std::string exe = module_path(&argv_path);
    if (exe.empty()) return "VAC: the game's path can't be read";
    std::vector<std::pair<std::string, std::string>> walk;
    if (!argv_walk(argv_path, exe, walk)) return "VAC: the game's path can't be resolved";
    std::string runs_in;  // the Wine prefix the game runs in
    if (!argv_path.empty() && (runs_in = real_path(wine_prefix_dir())).empty()) return "VAC: the game's Wine prefix can't be resolved";
    const std::string steam = steam_root();
    if (steam.empty()) return "VAC: Steam's folder is unknown";
    std::string text;
    Vdf top;
    if (!read_file(steam + "/steamapps/libraryfolders.vdf", text) || !vdf_root(text, "libraryfolders", top))
        return "VAC: Steam's libraryfolders.vdf can't be read";
    std::vector<std::string> libs = {steam}, names;
    for (auto& e : top.kids) {
        std::string p = e.value;
        if (e.object ? !vdf_one(e, "path", p) || p.empty() : all_digits(e.key) && p.empty())
            return "VAC: a library in Steam's libraryfolders.vdf has no single path";
        if (e.object || all_digits(e.key)) libs.push_back(p);
    }
    const size_t listed = libs.size();
    const size_t sc = lower_az(exe).find("/steamapps/common/");
    std::string folder;  // the steamapps/common folder in the exe's path: its library is the last one
    if (sc != std::string::npos) {
        const std::string rest = exe.substr(sc + strlen("/steamapps/common/"));
        folder = rest.substr(0, rest.find('/'));
        libs.push_back(exe.substr(0, sc));
    }
    // every app whose game folder holds the exe, every id whose prefix does and every id whose prefix the game runs in: more
    // than one Steam app, which runs can't be told
    std::vector<uint32_t> apps, prefixes, running;
    const std::string root = real_path(steam);
    bool in_steam = !root.empty() && reaches(walk, root), in_game = true;
    for (size_t i = 0; i < libs.size(); ++i) {
        const std::string dir = libs[i] + "/steamapps/", data = dir + "compatdata/";
        struct stat st;
        if (!list_dir(dir, names)) {
            if (!i || i >= listed || libs[i][0] != '/' || !stat((libs[i] + "/steamapps").c_str(), &st) || errno != ENOENT)
                return "VAC: a Steam library's folder can't be read";
            if (holds_text(libs[i], exe) || holds_text(libs[i], argv_path)) return "VAC: a Steam library that doesn't exist holds the game's path";
            continue;
        }
        if (const std::string r = real_path(dir); !r.empty() && reaches(walk, r)) in_steam = true;
        for (auto& n : names) {
            if (fnmatch("appmanifest_*.acf", n.c_str(), 0)) continue;
            Vdf app;
            std::string d, a, real;
            if (!read_file(dir + n, text) || !vdf_root(text, "appstate", app)) return with_path("VAC: a Steam manifest can't be read", dir + n);
            const uint32_t id = vdf_one(app, "appid", a) ? app_id(a) : 0;
            if (!id || !vdf_one(app, "installdir", d) || d.empty() || d == "." || d == ".." || d.find('/') != std::string::npos)
                return with_path("VAC: a Steam manifest names no single folder and app", dir + n);
            if (const char* why = owned_path(dir + "common/", d, exe, argv_path, kFolder, real)) return why;
            const bool named = i + 1 == libs.size() && !folder.empty() && d == folder;
            if ((named || (!real.empty() && reaches(walk, real))) && std::find(apps.begin(), apps.end(), id) == apps.end()) apps.push_back(id);
        }
        if (!list_dir(data, names)) {
            if (lstat(data.c_str(), &st) && errno == ENOENT) continue;
            return "VAC: a Steam library's prefixes can't be read";
        }
        for (auto& n : names) {
            std::string cd, pfx, drive;
            const char* why = owned_path(data, n, exe, argv_path, kPrefix, cd);
            if (why) return why;
            if (cd.empty()) continue;
            if (stat(cd.c_str(), &st)) return kPrefix.other;
            if (S_ISDIR(st.st_mode) && (why = owned_path(data + n + "/", "pfx", exe, argv_path, kPrefix, pfx))) return why;
            if (!pfx.empty() && stat(pfx.c_str(), &st)) return kPrefix.other;
            if (!pfx.empty() && S_ISDIR(st.st_mode) && (why = owned_path(data + n + "/pfx/", "drive_c", exe, argv_path, kPrefix, drive))) return why;
            const bool holds = reaches(walk, cd) || (!pfx.empty() && reaches(walk, pfx)) || (!drive.empty() && reaches(walk, drive));
            const bool runs = !runs_in.empty() && (is_or_under(runs_in, cd) || (!pfx.empty() && is_or_under(runs_in, pfx)));
            if (!holds && !runs) continue;
            const uint32_t id = app_id(n);
            if (!id) return "VAC: the prefix's Steam id is malformed";
            if (holds && std::find(prefixes.begin(), prefixes.end(), id) == prefixes.end()) prefixes.push_back(id);
            if (holds) in_game = in_game && !drive.empty() && in_game_of(walk, drive);
            if (runs && std::find(running.begin(), running.end(), id) == running.end()) running.push_back(id);
        }
    }
    if (apps.size() > 1) return "VAC: two Steam manifests name the game's folder";
    if (prefixes.size() > 1) return "VAC: two Steam prefixes hold the game's folder";
    if (running.size() > 1) return "VAC: the game's Wine prefix is two Steam prefixes";
    if (!prefixes.empty() && !in_game) return "VAC: in a Steam prefix, not in a game folder of its drive C";
    std::vector<uint32_t> ids;  // the Steam apps among them
    for (const auto* list : {&apps, &prefixes, &running})
        for (uint32_t id : *list)
            if (id < 0x80000000u && std::find(ids.begin(), ids.end(), id) == ids.end()) ids.push_back(id);
    if (ids.size() > 1) return "VAC: two Steam apps hold the game's folder or its Wine prefix";
    if (!ids.empty()) return vac_of(ids[0], steam);
    if (!prefixes.empty()) return nullptr;  // a shortcut's prefix holds it
    if (sc != std::string::npos) return "VAC: no Steam manifest names the game's folder";
    return in_steam ? "VAC: in Steam's own folder, not a Steam game's" : nullptr;
}

// The VAC of the app a steam_appid.txt in dirs names (Steamworks starts a game as that app), any case of its name as Wine
// finds it. A file that can't be read refuses, and so does one whose leading digits are no app id. One without them names none.
static const char* vac_named(const std::vector<std::string>& dirs) {
    const char* vac = nullptr;
    std::vector<std::string> names;
    for (auto& d : dirs) {
        if (!list_dir(d, names)) return "VAC: a folder can't be checked for steam_appid.txt";
        for (auto& n : names) {
            if (lower_az(n) != "steam_appid.txt") continue;
            std::string text;
            if (!read_file(d + n, text, 4096)) return "VAC: a steam_appid.txt can't be read";
            if (!text.rfind("\xEF\xBB\xBF", 0)) text.erase(0, 3);
            // Steamworks takes the leading digits: a file without them names no app
            const size_t a = text.find_first_not_of(" \t\r\n\v\f+");
            const std::string digits = a == std::string::npos ? "" : text.substr(a, text.find_first_not_of("0123456789", a) - a);
            if (digits.empty()) continue;
            const uint32_t app = app_id(digits);
            if (!app) return "VAC: a steam_appid.txt is malformed";
            const std::string steam = steam_root();
            const char* why = named_vac(steam.empty() ? kVacNoSteam : vac_of(app, steam));
            if (why && !strncmp(why, "VAC:", 4)) return why;
            if (why) vac = why;
        }
    }
    return vac;
}

// The exe's own app's VAC, else a steam_appid.txt's: a finding that refuses even a risk session ("VAC:") comes first.
static const char* vac_now(const std::vector<std::string>& dirs) {
    try {
        const char* own = vac_now_unguarded();
        if (own && !strncmp(own, "VAC:", 4)) return own;
        const char* named = vac_named(dirs);
        return named && (!own || !strncmp(named, "VAC:", 4)) ? named : own;
    } catch (...) {
        return "VAC: Steam's files couldn't be checked";
    }
}

// This folder's key on the app's revocation list (recorder.revoked), or the list can't be read whole (only a list that
// doesn't exist is empty): why it refuses; nullptr: neither.
static const char* revoked_here() {
    const std::string dir = recorder_dir(admit_dir()), file = dir + "/recorder.revoked";
    if (dir.empty()) return "SCSKiller's revocation list can't be read";
    std::string list;
    if (!read_file(file, list, 1 << 20)) {
        struct stat st;
        return lstat(file.c_str(), &st) && (errno == ENOENT || errno == ENOTDIR) ? nullptr : "SCSKiller's revocation list can't be read";
    }
    const std::string key = file_key(no_slash(admit_dir()));
    for (size_t at = 0; at < list.size();) {
        size_t end = list.find('\n', at);
        std::string line = list.substr(at, end == std::string::npos ? std::string::npos : end - at);
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line == key) return "revoked by SCSKiller";
        if (end == std::string::npos) break;
        at = end + 1;
    }
    return nullptr;
}

// sig= is recorder_sig of this exe's and this folder's file_key, the stamp lines, root= and the root's file_key under the
// recorder folder's recorder.key (admit.h's signed_by_app).
static bool signed_by_app(const std::string& root) {
    const std::string dir = recorder_dir(admit_dir()), exe = module_path();
    const std::wstring sig = ini_string(L"sig"), pid = ini_string(L"pid");
    if (dir.empty() || exe.empty() || sig.size() != 40) return false;
    const std::string key = read_all(dir + "/recorder.key", 256);
    if (key.size() < 32 || key.size() > 128) return false;
    const std::wstring stamp = signed_stamp(ini_string(L"exe_size"), ini_string(L"exe_time"), pid, ini_string(L"pid_time"), ini_string(L"risk"), ini_string(L"vendor"),
                                            ini_string(L"overlay"), ini_string(L"ab_play_s"), ini_string(L"ab_tag"));
    const std::string want = recorder_sig(key, file_key(exe), file_key(no_slash(admit_dir())), stamp, ini_string(L"root"), file_key(no_slash(root)));
    return !want.empty() && to_utf8(sig) == want;
}

// scskiller.ini's bytes, read whole; false when it can't be, or is over 64 KB.
static bool ini_bytes(const std::string& path, std::string& out) { return read_file(path, out, 65536); }

// admit.h's admission_verdict on POSIX paths, in its order. A folder name ending in a dot or a space names one folder here:
// no odd_name check.
static const char* admission_verdict(bool whole, const std::string& read, bool elsewhere, bool& risk, std::string& seen) {
    const std::string ini = admit_dir() + "scskiller.ini";
    std::string again;
    std::vector<std::string> markers;
    const std::string here = final_dir(admit_dir()), root = here.empty() ? "" : install_root(here);
    const bool bound = !ini_string(L"pid").empty();
    risk = !ini_string(L"risk").empty();
    const char* why = admit_dir().empty() ? "the recorder's folder can't be read"
                    : elsewhere ? "loaded from another folder than the exe"
                    : !exe_as_checked() ? "exe not as checked"
                    : !whole ? "scskiller.ini can't be read whole"
                    : here.empty() ? "the exe's folder can't be resolved"
                    : root.empty() ? (ini_string(L"root").empty() ? "no install root" : "the install root can't be resolved")
                    : !utf8_path(module_path()) || !utf8_path(here) || !utf8_path(root) ? "a folder or file name isn't UTF-8"
                    : !signed_by_app(root) ? "not signed by SCSKiller"
                    : bound && !bound_here() ? "bound to another process"
                    : !risk_well_formed(ini_string(L"pid"), ini_string(L"risk")) ? "risk= malformed"
                    : !anti_cheat_markers(markers) ? "markers= malformed" : nullptr;
    if (!why) why = revoked_here();
    if (!why && bound && !risk) markers.erase(markers.begin(), markers.begin() + kEasyAntiCheatMarkers);
    const std::vector<std::string> dirs = why ? std::vector<std::string>{} : folders_to(here, root);
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
        const std::vector<std::string> loaded = anti_cheat_loaded();
        why = loaded.size() == 1 && loaded[0] == "?" ? "the process's libraries can't be listed" : decide(risk, riot_games(), found, found_why, loaded, vac_now(dirs), seen);
    }
    if (!why && (!ini_bytes(ini, again) || again != read)) why = "scskiller.ini changed while deciding";
    if (!why && t_rec && recorder_dir_now(admit_dir()) != *t_rec) why = "SCSKiller's recorder folder changed while deciding";
    if (!why) why = revoked_here();
    return why;
}
