// scskiller_warm on Linux: warm.cpp's Vulkan warm (--vulkan), natively. The work folder's scskiller.foz is replayed by
// fossilize-replay from this exe's folder in the environment the caller gives it: with __GL_SHADER_DISK_CACHE_APP_NAME
// NVIDIA keys a process's cache on that name, not on its exe, so nothing is staged under the game's name. The
// arguments, the protocol and the passes are warm.cpp's, and its other options are taken and change nothing here:
//
//   scskiller_warm <workdir> <game exe file name> --vulkan [--threads N] --adapter-luid <PCI address, hex>
//                  [--stop-file <path>] [--alive-file <path> --alive-turns N] [--out <file>] [--err <file>]
//
// --adapter-luid: the PCI address the app's adapter list gives (GpuBackends.PciAddress), matched among the devices as the
// replayer's instance lists them (every layer off), so the replay never runs on another GPU. The stop file, and the alive
// file once its write time has stayed the same for --alive-turns of the 100 ms turns, stop the replayer through the
// patch's SCSKILLER_REPLAY_STOP file (third_party/fossilize): it creates nothing more and exits as when done. A
// "stopping" line says so, and the replayer is ended only if it hasn't exited SCSKILLER_WARM_EXIT_S after that.
#define VK_NO_PROTOTYPES
#include <vulkan/vulkan_core.h>
#include <dirent.h>
#include <dlfcn.h>
#include <fcntl.h>
#include <limits.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>
#include <algorithm>
#include <cerrno>
#include <cinttypes>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <set>
#include <string>
#include <thread>
#include <vector>
#include "fossilize_db.hpp"
#include "fossilize_external_replayer.hpp"

struct Opts {
    int threads = 0;
    uint64_t luid = 0;
    std::string stop_file, alive_file;
    int alive_turns = 0;
};

static bool parse(int argc, char** argv, int i, Opts& o) {
    for (; i + 1 < argc; i += 2) {
        std::string k = argv[i], v = argv[i + 1];
        char* end = nullptr;
        if (k == "--threads") o.threads = (int)strtol(v.c_str(), &end, 10);
        else if (k == "--adapter-luid") o.luid = strtoull(v.c_str(), &end, 16);
        else if (k == "--stop-file") o.stop_file = v;
        else if (k == "--alive-file") o.alive_file = v;
        else if (k == "--alive-turns") o.alive_turns = (int)strtol(v.c_str(), &end, 10);
        else if (k == "--out" || k == "--err") continue;  // opened by main, before anything prints
        else if (k == "--priority" || k == "--start" || k == "--memory-mb" || k == "--rt-threads" || k == "--skip" || k == "--stage-path" ||
                 k == "--skip-keys" || k == "--isolate" || k == "--pass")
            continue;  // Core's launcher sets the priority of the whole tree, and the replayer has no items of the D3D plan
        else return false;
        if (end && (*end || v.empty())) return false;
    }
    return i == argc;
}

static std::string json(const std::string& u) {  // JSON string literal
    std::string s = "\"";
    for (unsigned char c : u) {
        char esc[8];
        if (c == '"' || c == '\\') s += '\\', s += (char)c;
        else if (c < 0x20) snprintf(esc, sizeof esc, "\\u%04x", c), s += esc;
        else s += (char)c;
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

static int fail(const std::string& msg) { return emit("{\"event\":\"error\",\"message\":%s}", json(msg).c_str()), 1; }

static uint32_t rol(uint32_t x, int n) { return x << n | x >> (32 - n); }

static void sha1(const char* data, size_t len, uint8_t out[20]) {
    uint32_t h[5] = {0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476, 0xC3D2E1F0};
    std::vector<uint8_t> m(data, data + len);
    m.push_back(0x80);
    while (m.size() % 64 != 56) m.push_back(0);
    for (int i = 7; i >= 0; --i) m.push_back((uint8_t)((uint64_t)len * 8 >> i * 8));
    for (size_t c = 0; c < m.size(); c += 64) {
        uint32_t w[80];
        for (int i = 0; i < 16; ++i) w[i] = (uint32_t)m[c + 4 * i] << 24 | (uint32_t)m[c + 4 * i + 1] << 16 | (uint32_t)m[c + 4 * i + 2] << 8 | m[c + 4 * i + 3];
        for (int i = 16; i < 80; ++i) w[i] = rol(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
        uint32_t a = h[0], b = h[1], d = h[3], e = h[4], cc = h[2];
        for (int i = 0; i < 80; ++i) {
            const uint32_t f = i < 20 ? (b & cc) | (~b & d) : i < 40 || i >= 60 ? b ^ cc ^ d : (b & cc) | (b & d) | (cc & d);
            const uint32_t k = i < 20 ? 0x5A827999 : i < 40 ? 0x6ED9EBA1 : i < 60 ? 0x8F1BBCDC : 0xCA62C1D6;
            const uint32_t t = rol(a, 5) + f + e + k + w[i];
            e = d, d = cc, cc = rol(b, 30), b = a, a = t;
        }
        h[0] += a, h[1] += b, h[2] += cc, h[3] += d, h[4] += e;
    }
    for (int i = 0; i < 20; ++i) out[i] = (uint8_t)(h[i / 4] >> (24 - 8 * (i % 4)));
}

// fossilize-replay's --device-index of the device at this PCI address, counted as its instance counts them (the same
// loader and environment); -1 when none is. driver: its driver's version and UUID, which a driver update changes.
static int vk_device_index(uint64_t pci, std::string* driver = nullptr) {
    void* m = dlopen("libvulkan.so.1", RTLD_NOW | RTLD_LOCAL);
    auto gpa = m ? (PFN_vkGetInstanceProcAddr)dlsym(m, "vkGetInstanceProcAddr") : nullptr;
    auto create = gpa ? (PFN_vkCreateInstance)gpa(nullptr, "vkCreateInstance") : nullptr;
    VkApplicationInfo app{VK_STRUCTURE_TYPE_APPLICATION_INFO};
    app.apiVersion = VK_API_VERSION_1_1;
    VkInstanceCreateInfo ci{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO};
    ci.pApplicationInfo = &app;
    VkInstance inst = VK_NULL_HANDLE;
    int found = -1;
    if (create && create(&ci, nullptr, &inst) == VK_SUCCESS) {
        auto list = (PFN_vkEnumeratePhysicalDevices)gpa(inst, "vkEnumeratePhysicalDevices");
        auto base = (PFN_vkGetPhysicalDeviceProperties)gpa(inst, "vkGetPhysicalDeviceProperties");
        auto props = (PFN_vkGetPhysicalDeviceProperties2)gpa(inst, "vkGetPhysicalDeviceProperties2");
        auto exts = (PFN_vkEnumerateDeviceExtensionProperties)gpa(inst, "vkEnumerateDeviceExtensionProperties");
        uint32_t n = 0;
        std::vector<VkPhysicalDevice> gpus;
        if (list && base && props && exts && list(inst, &n, nullptr) == VK_SUCCESS && (gpus.resize(n), list(inst, &n, gpus.data())) >= 0)
            for (uint32_t i = 0; i < n && found < 0; ++i) {
                VkPhysicalDeviceProperties p1;
                base(gpus[i], &p1);
                uint32_t e = 0;
                std::vector<VkExtensionProperties> have;
                if (p1.apiVersion < VK_API_VERSION_1_1 || exts(gpus[i], nullptr, &e, nullptr) != VK_SUCCESS) continue;
                have.resize(e);
                if (exts(gpus[i], nullptr, &e, have.data()) < 0) continue;
                if (std::none_of(have.begin(), have.begin() + e, [](auto& x) { return !strcmp(x.extensionName, VK_EXT_PCI_BUS_INFO_EXTENSION_NAME); })) continue;
                VkPhysicalDeviceIDProperties id{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES};
                VkPhysicalDevicePCIBusInfoPropertiesEXT bus{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PCI_BUS_INFO_PROPERTIES_EXT, &id};
                VkPhysicalDeviceProperties2 p{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2, &bus};
                props(gpus[i], &p);
                // as GpuBackends.PciAddress packs it
                const uint64_t at = (uint64_t)bus.pciDomain << 16 | (bus.pciBus & 0xff) << 8 | (bus.pciDevice & 0x1f) << 3 | (bus.pciFunction & 7);
                if (at == pci) found = (int)i;
                if (found == (int)i && driver)
                    *driver = std::to_string(p.properties.driverVersion) + ":" + std::string((const char*)id.driverUUID, VK_UUID_SIZE);
            }
        if (auto destroy = (PFN_vkDestroyInstance)gpa(inst, "vkDestroyInstance")) destroy(inst, nullptr);
    }
    if (m) dlclose(m);
    return found;
}

// A copy of a .foz without the pipelines in out: entries are copied as they are (16-byte magic, then each entry's
// 40-character name, 16-byte header with its stored size first, payload). False if it can't be read or written.
static bool foz_without(const std::string& from, const std::string& to, const std::set<std::pair<unsigned, Fossilize::Hash>>& out) {
    FILE* in = fopen(from.c_str(), "rbe");
    FILE* o = in ? fopen(to.c_str(), "wbe") : nullptr;
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
        if (out.count({(unsigned)strtoul(tag, nullptr, 16), (Fossilize::Hash)strtoull(hash, nullptr, 16)})) continue;
        ok = fwrite(head, 1, 56, o) == 56 && (!stored || fwrite(payload.data(), 1, stored, o) == stored);
    }
    if (in) fclose(in);
    if (o && fclose(o)) ok = false;
    return ok;
}

static bool copy(const std::string& from, const std::string& to) {
    FILE* in = fopen(from.c_str(), "rbe");
    FILE* o = in ? fopen(to.c_str(), "wbxe") : nullptr;
    bool ok = o != nullptr;
    char buf[1 << 16];
    for (size_t n; ok && (n = fread(buf, 1, sizeof buf, in)) > 0;) ok = fwrite(buf, 1, n, o) == n;
    ok = ok && !ferror(in);
    if (in) fclose(in);
    if (o && fclose(o)) ok = false;
    return ok;
}

static bool exists(const std::string& path) {
    struct stat s;
    return !path.empty() && stat(path.c_str(), &s) == 0;
}

static void sleep_turn() {
    timespec t{0, 100 * 1000 * 1000};
    while (nanosleep(&t, &t) && errno == EINTR) {}
}

// The work folder's scskiller.foz in passes, as warm.cpp's vulkan_warm: Fossilize's master restarts a worker that faults
// or hangs on a pipeline, which is skipped by the driver for this run, and the passes replay what its recovery left out.
// Only pipelines the driver refused, or that didn't parse in a run without faults, count as failed. One naming a shader
// module that failed SPIR-V validation is invalid: Steam's replayer skips it too. One needing a feature the device lacks
// (Fossilize's feature filter) is unsupported.
static int vulkan_warm(const Opts& o, const std::string& work, const std::string& exe, const std::string& flat) {
    using namespace Fossilize;
    char self[PATH_MAX];
    const ssize_t len = readlink("/proc/self/exe", self, sizeof self - 1);
    if (len <= 0) return fail("can't find this exe's own path");
    std::string replayer(self, (size_t)len);
    replayer.resize(replayer.rfind('/') + 1);
    replayer += "fossilize-replay";
    if (access(replayer.c_str(), X_OK)) return fail(replayer + " not found");
    const std::string foz = work + "scskiller.foz";
    struct Unstage {
        std::string dir;
        std::vector<std::string> files;
        ~Unstage() {
            for (auto& f : files) unlink(f.c_str());
            // the whitelist's copy and what the replayer appended to it, scskiller_validated.<n>.foz
            if (DIR* d = opendir(dir.c_str())) {
                while (dirent* e = readdir(d))
                    if (!strncmp(e->d_name, "scskiller_validated.", 20)) unlink((dir + e->d_name).c_str());
                closedir(d);
            }
        }
    } unstage{flat};
    // Shader modules are validated as Steam's replayer validates them (--spirv-val), but those the plan's whitelist names
    // (recorded on this PC): a copy of it in the stage, which takes what the replayer appends
    std::string whitelist;
    if (exists(work + "scskiller_validated.foz")) {
        whitelist = flat + "scskiller_validated";
        if (link((work + "scskiller_validated.foz").c_str(), (whitelist + ".foz").c_str()) && !copy(work + "scskiller_validated.foz", whitelist + ".foz"))
            return fail("staging scskiller_validated.foz failed (" + std::string(strerror(errno)) + ")");
    }

    // every pipeline to replay, by kind: its total, and the failed keys of the ones it didn't create
    const ResourceTag kinds[] = {RESOURCE_GRAPHICS_PIPELINE, RESOURCE_COMPUTE_PIPELINE, RESOURCE_RAYTRACING_PIPELINE};
    std::vector<Hash> all[3];
    {
        std::unique_ptr<DatabaseInterface> db(create_stream_archive_database(foz.c_str(), DatabaseMode::ReadOnly));
        if (!db || !db->prepare()) return fail("scskiller.foz can't be read");
        for (int k = 0; k < 3; ++k) {
            size_t n = 0;
            if (!db->get_hash_list_for_resource_tag(kinds[k], &n, nullptr)) return fail("scskiller.foz can't be read");
            all[k].resize(n);
            if (n && !db->get_hash_list_for_resource_tag(kinds[k], &n, all[k].data())) return fail("scskiller.foz can't be read");
        }
    }
    const uint64_t total = all[0].size() + all[1].size() + all[2].size();

    // the replayer inherits this environment as is
    setenv("VK_LOADER_LAYERS_DISABLE", "~all~", 1);  // overlays and recorders, Steam's Fossilize layer among them
    setenv("DISABLE_VK_LAYER_VALVE_steam_fossilize_1", "1", 1);
    unsetenv("VK_INSTANCE_LAYERS");  // a loader without VK_LOADER_LAYERS_DISABLE still loads what this names
    unsetenv("VK_LOADER_LAYERS_ENABLE");  // enabling wins over disabling
    const std::string stop_path = flat + "stop";
    unstage.files.push_back(stop_path);
    setenv("SCSKILLER_REPLAY_STOP", stop_path.c_str(), 1);
    setenv("SCSKILLER_REPLAY_CREATED", flat.substr(0, flat.size() - 1).c_str(), 1);
    if (!o.luid) return fail("no adapter given (--adapter-luid)");
    std::string driver;
    const int device = vk_device_index(o.luid, &driver);
    if (device < 0) return fail("no Vulkan device is at the adapter's PCI address");

    using Ids = std::set<std::pair<unsigned, Hash>>;
    auto lists = [&](const char* what, bool remove) {  // the patch's <what>.<pid>.txt files of the stage
        Ids got;
        const std::string head = std::string(what) + ".";
        if (DIR* d = opendir(flat.c_str())) {
            while (dirent* e = readdir(d)) {
                const std::string n = e->d_name;
                if (n.size() <= head.size() + 4 || n.compare(0, head.size(), head) || n.compare(n.size() - 4, 4, ".txt")) continue;
                const std::string f = flat + n;
                if (FILE* in = fopen(f.c_str(), "rbe")) {
                    unsigned tag;
                    unsigned long long hash;
                    while (fscanf(in, "%u %llx", &tag, &hash) == 2) got.insert({tag, (Hash)hash});
                    fclose(in);
                }
                if (remove) unlink(f.c_str());
                else if (std::find(unstage.files.begin(), unstage.files.end(), f) == unstage.files.end()) unstage.files.push_back(f);
            }
            closedir(d);
        }
        return got;
    };
    const char* lim = getenv("SCSKILLER_REPLAY_TIMEOUT_S");
    const unsigned hang_s = lim && atoi(lim) > 0 ? (unsigned)atoi(lim) : 300;
    // counted in turns, not wall time: the caller stops the processes to pause the warm
    const char* exit_s = getenv("SCSKILLER_WARM_EXIT_S");
    const uint64_t exit_turns = 10ull * (exit_s ? std::max(1, atoi(exit_s)) : 600);
    constexpr int max_passes = 16;
    // A crash reports its one pipeline, a hang every one in flight: a pass's lone new fault is confirmed, several are
    // suspects, each replayed later without the other suspects and confirmed only if it faults again.
    Ids created, confirmed, suspects, refused, unparsed, invalid, unsupported;
    std::pair<unsigned, Hash> trying{};
    bool alone = false;
    timespec t0;
    clock_gettime(CLOCK_MONOTONIC, &t0);
    auto seconds = [&] {
        timespec t;
        clock_gettime(CLOCK_MONOTONIC, &t);
        return (double)(t.tv_sec - t0.tv_sec) + (t.tv_nsec - t0.tv_nsec) / 1e9;
    };
    uint64_t shown = 0;  // the progress shown: later passes never move it back
    uint64_t turns = 0, alive_turn = 0, stop_turn = 0;  // alive_turn: the turn the alive file's write time last changed
    timespec alive{};
    bool stopping = false, stopped = false;
    // the caller's stop file, or the alive file gone stale, stop the replayer through its own stop file
    auto turn = [&] {
        if (!o.alive_file.empty()) {
            struct stat a;
            const timespec t = stat(o.alive_file.c_str(), &a) == 0 ? a.st_mtim : timespec{};
            if (t.tv_sec != alive.tv_sec || t.tv_nsec != alive.tv_nsec) alive = t, alive_turn = turns;
        }
        const bool stale = o.alive_turns && turns - alive_turn >= (uint64_t)o.alive_turns;
        if (!stopping && (stale || exists(o.stop_file))) {
            const int f = open(stop_path.c_str(), O_WRONLY | O_CREAT | O_CLOEXEC, 0644);
            if (f >= 0) {
                close(f);
                stopping = true, stop_turn = turns;
                // the caller's stop timer ends here: the replayer may write the driver cache for minutes as it exits
                emit("{\"event\":\"stopping\"}");
            }
        }
        return stale && turns - alive_turn >= (uint64_t)o.alive_turns + exit_turns;  // orphaned: it never exited
    };
    turn();
    // A driver updated or reset under a running replay refuses everything after it: nothing of such a run counts as
    // failed. The lists are read once a second, the driver checked when refusals came in (at most every 10 s) and
    // after each pass.
    bool gone = false;
    size_t refusals_seen = 0;
    uint64_t checked = 0, looked = 0;
    auto driver_gone = [&](bool now) {
        if (gone) return true;
        if (!now && turns - looked < 10) return false;
        looked = turns;
        const size_t refusals = lists("failed", false).size();
        const bool lost = !lists("lost", false).empty();
        if (!now && !lost && (refusals == refusals_seen || turns - checked < 100)) return false;
        refusals_seen = refusals, checked = turns;
        std::string d;
        gone = lost || vk_device_index(o.luid, &d) < 0 || d != driver;
        if (gone) close(open(stop_path.c_str(), O_WRONLY | O_CREAT | O_CLOEXEC, 0644)), stopping = true, stop_turn = turns;
        return gone;
    };
    int code = 0;
    for (int pass = 1;; ++pass) {
        std::string archive = foz;
        if (pass > 1) {
            archive = flat + "pass" + std::to_string(pass) + ".foz";
            unstage.files.push_back(archive);
            Ids out = confirmed;
            out.insert(suspects.begin(), suspects.end());
            out.insert(refused.begin(), refused.end());
            out.insert(invalid.begin(), invalid.end());
            out.insert(unsupported.begin(), unsupported.end());
            if (alone) out.erase(trying);
            if (!foz_without(foz, archive, out)) return fail("writing " + archive + " failed");
        }
        lists("failed", true), lists("unparsed", true), lists("invalid", true), lists("unsupported", true);  // this pass's own

        // the replayer's output goes to the stage's log, never into these JSON lines
        const int log = open((flat + "scskiller.log").c_str(), O_WRONLY | O_CREAT | O_APPEND | O_CLOEXEC, 0644);
        fflush(stdout), fflush(stderr);
        const int out = fcntl(1, F_DUPFD_CLOEXEC, 3), err = fcntl(2, F_DUPFD_CLOEXEC, 3);
        ExternalReplayer r;
        ExternalReplayer::Options ro{};
        const char* dbs[] = {archive.c_str()};
        ro.external_replayer_path = replayer.c_str();
        ro.databases = dbs, ro.num_databases = 1;
        ro.num_threads = o.threads > 0 ? (unsigned)o.threads : std::max(1u, std::thread::hardware_concurrency());  // threads of one worker process (the patch)
        ro.device_index = (unsigned)device;
        ro.spirv_validate = true;
        ro.on_disk_validation_whitelist = whitelist.empty() ? nullptr : whitelist.c_str();
        ro.disable_rate_limiter = true;  // else it sets nice 19 and idle IO over the priority Core chose, and stops the worker on IO stalls
        ro.timeout_seconds = hang_s;  // no pipeline done for this long: the worker is hung, ended and started again past it
        ro.quiet = log < 0 || out < 0 || err < 0;
        if (!ro.quiet) dup2(log, 1), dup2(log, 2);
        const bool started = r.start(ro);
        if (!ro.quiet) dup2(out, 1), dup2(err, 2);
        for (int fd : {log, out, err})
            if (fd >= 0) close(fd);
        if (!started) return fail("fossilize-replay didn't start");
        if (pass == 1) emit("{\"event\":\"start\",\"total\":%" PRIu64 ",\"adapter\":\"\",\"exe\":%s}", total, json(exe).c_str());

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
        for (uint64_t printed = turns;; sleep_turn()) {
            const bool exited = r.is_process_complete(&code);
            r.poll_progress(p);  // also takes in the faulty pipelines the replayer reports
            if (exited) break;
            ++turns;
            if (turn()) {
                r.kill();
                for (int i = 0; i < 50 && !r.is_process_complete(nullptr); ++i) sleep_turn();
                return fail("the alive file " + o.alive_file + " stayed unchanged for " + std::to_string(((uint64_t)o.alive_turns + exit_turns) / 10) +
                            " s: ended the warm process");
            }
            if (stopping && turns - stop_turn >= exit_turns) {
                r.kill();
                for (int i = 0; i < 50 && !r.is_process_complete(nullptr); ++i) sleep_turn();
                return fail("fossilize-replay didn't exit " + std::to_string(exit_turns / 10) +
                            " s after the stop: ended it, so what it compiled may not be in the driver cache");
            }
            driver_gone(false);
            if (turns - printed < 5) continue;
            printed = turns;
            // skipped holds the ones that failed to parse too
            const uint64_t done = std::min<uint64_t>(total, sum(&ExternalReplayer::TypeProgress::completed) + sum(&ExternalReplayer::TypeProgress::skipped) +
                                                                sum(&ExternalReplayer::TypeProgress::cached));
            shown = std::max(shown, done);
            const double s = seconds();
            emit("{\"event\":\"progress\",\"done\":%" PRIu64 ",\"total\":%" PRIu64 ",\"failed\":%" PRIu64 ",\"driverSkipped\":%" PRIu64 ",\"invalid\":%" PRIu64
                 ",\"unsupported\":%" PRIu64 ",\"rate\":%.1f}", shown, total, (uint64_t)(refused.size() + refusals_seen),
                 (uint64_t)(confirmed.size() + suspects.size()) + faulty_now(false, nullptr), (uint64_t)invalid.size(), (uint64_t)unsupported.size(),
                 s > 0 ? shown / s : 0.0);
        }
        if (driver_gone(true)) return fail("The graphics driver changed or reset during the compile, compile again");
        refusals_seen = 0;
        stopped = exists(stop_path);
        Ids fresh;
        faulty_now(true, &fresh);
        if (alone) {
            suspects.erase(trying);
            if (fresh.erase(trying)) confirmed.insert(trying);
        }
        const bool fault = !fresh.empty();
        if (fresh.size() == 1) confirmed.insert(*fresh.begin());
        else suspects.insert(fresh.begin(), fresh.end());
        const Ids made_now = lists("created", false), refused_now = lists("failed", false), invalid_now = lists("invalid", false),
                  unsupported_now = lists("unsupported", false);
        created.insert(made_now.begin(), made_now.end());
        refused.insert(refused_now.begin(), refused_now.end());
        invalid.insert(invalid_now.begin(), invalid_now.end());
        unsupported.insert(unsupported_now.begin(), unsupported_now.end());
        unparsed = lists("unparsed", false);
        if (stopped || code != 0) break;
        bool left = false;
        for (int k = 0; k < 3 && !left; ++k)
            for (Hash h : all[k]) {
                const std::pair<unsigned, Hash> id{(unsigned)kinds[k], h};
                if (!created.count(id) && !confirmed.count(id) && !suspects.count(id) && !refused.count(id) && !invalid.count(id) &&
                    !unsupported.count(id)) {
                    left = true;
                    break;
                }
            }
        alone = !(fault && left) && !suspects.empty();  // a pass with a new fault first replays what it left out
        if (alone) trying = *suspects.begin();
        else if (!(fault && left)) break;
        if (pass == max_passes) {
            fprintf(stderr, "%d passes: what wasn't replayed counts as skipped by the driver\n", max_passes);
            break;
        }
    }
    if (code != 0 && !stopped) return fail("fossilize-replay exited with code " + std::to_string(code) + ", see stage/scskiller.log");
    Ids faulty = confirmed;
    faulty.insert(suspects.begin(), suspects.end());

    // Not created: refused by the driver, or unparsable in a replay without any fault, is failed (deterministic), a faulty
    // one is skipped by the driver, naming a module that failed validation is invalid, needing a feature the device lacks
    // is unsupported, and every other one is skipped by the driver. On a stop only the faulty ones count as skipped: the
    // rest is what the stop left. Faulty before invalid: a thread's current pipeline outlives a skip, so a fault right after
    // one names the skipped pipeline.
    uint64_t made = 0, failed = 0, skipped = 0, invalids = 0, unsupporteds = 0;
    std::string keys;
    for (int k = 0; k < 3; ++k)
        for (Hash h : all[k]) {
            const std::pair<unsigned, Hash> id{(unsigned)kinds[k], h};
            if (created.count(id)) {
                ++made;
                continue;
            }
            if (refused.count(id) || (faulty.empty() && unparsed.count(id))) ++failed;
            else if (faulty.count(id)) ++skipped;
            else if (invalid.count(id)) ++invalids;
            else if (unsupported.count(id)) ++unsupporteds;
            else if (!stopped) ++skipped;
            // the plan item's key (VulkanPlan.Key): SHA-1 of 'V' and the archive entry's 40-character name
            char rec[42];
            snprintf(rec, sizeof rec, "V%024x%016" PRIx64, (unsigned)kinds[k], (uint64_t)h);
            uint8_t sha[20];
            sha1(rec, 41, sha);
            keys.append((const char*)sha, 20);
        }
    if (!keys.empty()) {
        FILE* f = fopen((flat + "scskiller_failed.keys").c_str(), "wbe");
        const bool ok = f && fwrite(keys.data(), 1, keys.size(), f) == keys.size();
        if (f && fclose(f)) return fail("writing scskiller_failed.keys failed");
        if (!ok) return fail("writing scskiller_failed.keys failed");
    }
    if (total && !made && faulty.empty() && refused.empty() && invalid.empty() && unsupported.empty() && !stopped)
        return fail("fossilize-replay created no pipeline, see stage/scskiller.log");
    emit("{\"event\":\"done\",\"done\":%" PRIu64 ",\"total\":%" PRIu64 ",\"failed\":%" PRIu64 ",\"driverSkipped\":%" PRIu64 ",\"invalid\":%" PRIu64
         ",\"unsupported\":%" PRIu64 ",\"seconds\":%.1f,\"stopped\":%s,\"crashed\":[]}", stopped ? made + failed + skipped + invalids + unsupporteds : total,
         total, failed, skipped, invalids, unsupporteds, seconds(), stopped ? "true" : "false");
    return 0;
}

static int run(int argc, char** argv, bool vulkan) {
    Opts o;
    if (argc < 3 || !parse(argc, argv, 3, o) || o.alive_turns < 0 || (o.alive_turns > 0) == o.alive_file.empty()) {
        fputs("usage: scskiller_warm <workdir> <game exe file name> --vulkan [--threads N] --adapter-luid <PCI address, hex>\n"
              "                      [--stop-file <path>] [--alive-file <path> --alive-turns N] [--out <file>] [--err <file>]\n", stderr);
        return fail("bad arguments");
    }
    if (!vulkan) return fail("on Linux scskiller_warm replays Vulkan alone (--vulkan)");
    std::string work = argv[1], exe = argv[2];
    if (work.back() != '/') work += '/';
    struct stat s;
    if (stat(work.c_str(), &s) || !S_ISDIR(s.st_mode) || exe.empty() || exe.find('/') != std::string::npos)
        return fail("workdir not found or exe is not a file name");
    std::string flat;  // this run's own new staging folder: nothing in it was there before
    int err = 0;
    for (int n = 1; n < 100 && flat.empty(); ++n) {
        std::string d = work + "stage-" + std::to_string(getpid()) + "-" + std::to_string(n) + "/";
        if (mkdir(d.c_str(), 0755) == 0) flat = d;
        else if ((err = errno) != EEXIST) break;
    }
    if (flat.empty()) return fail("can't create a staging folder in " + work + " (" + strerror(err) + ")");
    emit("{\"event\":\"stage\",\"stage\":%s}", json(flat.substr(0, flat.size() - 1)).c_str());  // first: a failure from here on has its log there
    return vulkan_warm(o, work, exe, flat);
}

int main(int argc, char** argv) {
    // --vulkan takes no value: out of the pairs parse() and the --out scan read
    bool vulkan = false;
    std::vector<char*> args;
    for (int i = 0; i < argc; ++i)
        if (strcmp(argv[i], "--vulkan")) args.push_back(argv[i]);
        else vulkan = true;
    argc = (int)args.size(), argv = args.data();
    bool out = false;
    int code = -1;
    // first: a bad command line's error goes there too. --out before --err: a failed --err is reported in --out
    for (int fd : {1, 2})
        for (int i = 3; code < 0 && i + 1 < argc; i += 2) {
            if (strcmp(argv[i], fd == 1 ? "--out" : "--err")) continue;
            const int h = open(argv[i + 1], O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0644);
            if (h < 0) {
                code = fail(std::string("can't create ") + argv[i + 1] + " (" + strerror(errno) + ")");
                break;
            }
            fflush(fd == 1 ? stdout : stderr);
            dup2(h, fd), close(h);
            out = out || fd == 1;
        }
    if (code < 0) code = run(argc, argv, vulkan);
    if (out) emit("{\"event\":\"exit\",\"code\":%d}", code);
    return code;
}
