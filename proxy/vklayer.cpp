// SCSKiller's Vulkan recorder: the side of VkLayer_scskiller.dll (upstream Fossilize's capture layer, patched by
// third_party/fossilize/scskiller.patch) that decides and writes. A process records only when the D3D12 recorder's
// admission (admit.h) admits its exe; else the layer passes through and writes nothing. The archive, the timings and the
// log go to the recorder folder's vulkan\<exe file_key>\, never into the game's folders: scskiller.<hash>.<n>.foz,
// scskiller_creates.csv (#api,vulkan), scskiller_frames.bin and scskiller.log.
// On Linux (libVkLayer_scskiller.so) the same, through admit_posix.h: the folders vulkan/<exe file_key>/.
#ifdef _WIN32
#include "admit.h"
#else
#include "admit_posix.h"
#include <sys/file.h>
#include <sys/syscall.h>
#endif
#include "scskiller_layer.hpp"
#define VK_NO_PROTOTYPES
#include <vulkan/vulkan_core.h>
#include "vk_features.h"
#include "frame_enc.h"
#include "overlay.h"
#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <memory>
#include <mutex>
#include <thread>

#ifdef _WIN32
static std::wstring g_exe_dir;  // the exe's folder, trailing backslash: where its scskiller.ini is
static const std::wstring& admit_dir() { return g_exe_dir; }
#else
static std::string g_exe_dir;  // the exe's folder, trailing '/'
static const std::string& admit_dir() { return g_exe_dir; }
#endif

static std::atomic<int> g_admission;  // 0 undecided, 1 records, -1 passes through
static std::string g_dump;
static std::mutex g_csv_mx;
static FILE* g_csv;
static const auto g_t0 = std::chrono::steady_clock::now();
static long long g_session_unix;  // the csv's #session stamp, also the frames file's launch stamp
static thread_local bool t_presenting;  // this thread has presented: its creates stall frames

double scsk_layer_now() { return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - g_t0).count(); }
bool scsk_layer_recording() { return g_admission > 0; }
const char* scsk_layer_dump_path() { return g_dump.c_str(); }

#ifdef _WIN32
// Fossilize opens its archive with fopen: the path in the code page, only when it holds the path exactly.
static bool acp(const std::wstring& w, std::string& out) {
    BOOL lossy = FALSE;
    const bool utf8 = GetACP() == CP_UTF8;  // takes no lossy flag, and holds every path
    int n = WideCharToMultiByte(CP_ACP, utf8 ? 0 : WC_NO_BEST_FIT_CHARS, w.c_str(), -1, nullptr, 0, nullptr, utf8 ? nullptr : &lossy);
    if (n <= 0 || lossy) return false;
    out.resize(n - 1);
    WideCharToMultiByte(CP_ACP, utf8 ? 0 : WC_NO_BEST_FIT_CHARS, w.c_str(), -1, out.data(), n, nullptr, nullptr);
    return true;
}

// The driver-cache variables the game runs with (Steam redirects them into steamapps\shadercache\<appid>): the warm
// replays with the same ones.
static void cache_lines(std::string& out) {
    wchar_t* env = GetEnvironmentStringsW();
    if (!env) return;
    for (const wchar_t* e = env; *e; e += wcslen(e) + 1) {
        const std::wstring v = e;
        for (const wchar_t* p : {L"__GL_SHADER_DISK_CACHE", L"AMD_VK_PIPELINE_CACHE_", L"AMD_VK_USE_PIPELINE_CACHE"})
            if (!_wcsnicmp(v.c_str(), p, wcslen(p)) && v.size() < 4096) out += "#cache," + utf8(v) + "\n";
    }
    FreeEnvironmentStringsW(env);
}

#endif

// Frame times as the D3D12 recorder writes them (proxy.cpp's frame hooks, FrameEnc): the frame is the moment a
// vkQueuePresentKHR that presented a swapchain returns, every swapchain of a surface one swap chain index (a recreate
// keeps it, as D3D's ResizeBuffers keeps the swap chain). The present only queues the time, a thread writes the file once
// a second from the first frame on: the last launch that presented replaces it.
struct Frames {
    std::mutex mx;  // q and surfaces, never held across a call into the driver
    std::vector<FrameAt> q;
    std::vector<std::pair<uint64_t, uint64_t>> surfaces;  // swapchain and its surface, the latest last
    // the writer's
    FrameEnc enc;
    uint64_t head[4];
    bool opened, full, said;
#ifdef _WIN32
    std::wstring path, log;
    HANDLE h = INVALID_HANDLE_VALUE;
#else
    std::string path, log;
    int fd = -1;
#endif
};
static Frames& g_fr = *new Frames();  // never destroyed: the writer runs on while the process's exit destroys statics
static std::atomic<bool> g_frames_on;

static int64_t ticks() { return std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count(); }

static bool frames_open() {
#ifdef _WIN32
    g_fr.h = CreateFileW(g_fr.path.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    return g_fr.h != INVALID_HANDLE_VALUE;
#else
    // as append(): never through a link, only a regular file with no other name
    int fd = open(g_fr.path.c_str(), O_WRONLY | O_CREAT | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC, 0666);
    struct stat st;
    if (fd >= 0 && (fstat(fd, &st) || !S_ISREG(st.st_mode) || st.st_nlink != 1 || ftruncate(fd, 0))) close(fd), fd = -1;
    return (g_fr.fd = fd) >= 0;
#endif
}

#ifndef _WIN32
static FILE* append(const std::string& path);
#endif
// once a launch, in the folder's scskiller.log
static void frames_failed(const char* what) {
    if (g_fr.said) return;
    g_fr.said = true;
#ifdef _WIN32
    FILE* f = _wfopen(g_fr.log.c_str(), L"a");
#else
    FILE* f = append(g_fr.log);
#endif
    if (f) fprintf(f, "frames: scskiller_frames.bin %s, no more frames written\n", what), fclose(f);
}

static bool frames_put(const void* p, size_t n) {
#ifdef _WIN32
    DWORD done = 0;
    return WriteFile(g_fr.h, p, (DWORD)n, &done, nullptr) && done == n;
#else
    for (auto c = (const char*)p; n;) {
        const ssize_t w = write(g_fr.fd, c, n);
        if (w < 0 && errno == EINTR) continue;
        if (w <= 0) return false;
        c += w, n -= (size_t)w;
    }
    return true;
#endif
}

// proxy.cpp's frames_flush: a failed write ends the file, as every frame's time is the sum of the deltas before it
static void frames_flush(std::vector<FrameAt>& q) {
    if (q.empty() || g_fr.full) return q.clear();
    if (!g_fr.opened) {
        if (!frames_open()) return q.clear(), frames_failed("can't be opened");
        g_fr.opened = true;
        const uint32_t mark = 0xFFFFFFFF;
        if (!frames_put(&mark, 4) || !frames_put(g_fr.head, sizeof g_fr.head)) return q.clear(), g_fr.full = true, frames_failed("can't be written");
    }
    for (auto& f : q) g_fr.enc.put(f);
    q.clear();
    auto& o = g_fr.enc.out;
    const size_t n = o.size() * 4;
    if (g_fr.enc.bytes + n > kFramesCap) g_fr.full = true, frames_failed("is at its size cap");
    else if (!frames_put(o.data(), n)) g_fr.full = true, frames_failed("can't be written");
    else g_fr.enc.bytes += n;
    o.clear();
}

static void ov_flush();
static void frames_writer() {
    std::vector<FrameAt> q;
    for (;;) {
        std::this_thread::sleep_for(std::chrono::seconds(1));
        ov_flush();
        {
            std::lock_guard l(g_fr.mx);
            q.swap(g_fr.q);
        }
        try {
            frames_flush(q);
        } catch (...) {  // out of memory: no more frames, the game goes on
            g_frames_on = false, g_fr.full = true, q.clear(), frames_failed("ran out of memory");
        }
    }
}

// Pinned for the process, as the loader unloads a layer at vkDestroyInstance: the writer runs on, and a later instance
// stays in the same launch.
static bool pin_self() {
#ifdef _WIN32
    HMODULE m;
    return GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, (LPCWSTR)(void*)&frames_writer, &m);
#else
    Dl_info i{};
    return dladdr((void*)&frames_writer, &i) && i.dli_fname && dlopen(i.dli_fname, RTLD_NOW | RTLD_NOLOAD | RTLD_NODELETE);
#endif
}

// dir: the recording folder, with its trailing separator
template <class Path> static void frames_start(const Path& dir) {
    const int64_t t = ticks();
    const uint64_t head[4] = {(uint64_t)g_session_unix, (uint64_t)(scsk_layer_now() * 1000), (uint64_t)t, 1000000000ull};
    memcpy(g_fr.head, head, sizeof head);
#ifdef _WIN32
    g_fr.path = dir + L"scskiller_frames.bin", g_fr.log = dir + L"scskiller.log";
#else
    g_fr.path = dir + "scskiller_frames.bin", g_fr.log = dir + "scskiller.log";
#endif
    g_fr.enc.qpc0 = t, g_fr.enc.freq = 1000000000;
    if (!pin_self()) return;
    try {
        std::thread(frames_writer).detach();
    } catch (...) {
        return;
    }
    g_frames_on = true;
}

// Never throws into the game: out of memory turns the frames off.
void scsk_layer_swapchain(uint64_t sc, uint64_t surface) {
    if (!g_frames_on.load(std::memory_order_relaxed)) return;
    std::lock_guard l(g_fr.mx);
    auto& v = g_fr.surfaces;
    try {
        v.erase(std::remove_if(v.begin(), v.end(), [&](auto& e) { return e.first == sc; }), v.end());
        // a surface keeps its latest swapchain besides the new one: the one it replaces may still present what it acquired
        auto latest = std::find_if(v.rbegin(), v.rend(), [&](auto& e) { return e.second == surface; });
        if (latest != v.rend()) {
            const uint64_t keep = latest->first;
            v.erase(std::remove_if(v.begin(), v.end(), [&](auto& e) { return e.second == surface && e.first != keep; }), v.end());
        }
        if (v.size() >= 64) v.erase(v.begin());
        v.emplace_back(sc, surface);
    } catch (...) {
        g_frames_on = false;
    }
}

static void ov_presented(const VkPresentInfoKHR* pi, int32_t res);
void scsk_layer_presented(const VkPresentInfoKHR* pi, int32_t res) {
    t_presenting = true;
    ov_presented(pi, res);
    if (!g_frames_on.load(std::memory_order_relaxed) || !pi || !pi->pSwapchains || (res < 0 && !pi->pResults)) return;
    // the time under the lock: two queues' frames go in the order they returned
    std::lock_guard l(g_fr.mx);
    const int64_t t = ticks();
    for (uint32_t i = 0; i < pi->swapchainCount && i < 16; ++i) {
        if (pi->pResults && pi->pResults[i] < 0) continue;
        const uint64_t sc = (uint64_t)pi->pSwapchains[i];
        auto& v = g_fr.surfaces;
        auto it = std::find_if(v.rbegin(), v.rend(), [&](auto& e) { return e.first == sc; });
        try {
            g_fr.q.push_back({t, (void*)(uintptr_t)(it != v.rend() ? it->second : sc), false});
        } catch (...) {
            return (void)(g_frames_on = false);
        }
    }
}

static struct End {
    ~End() {  // at the process's exit: the launch's end, as the D3D12 recorder's #end
        // ExitProcess ended the other threads where they were: one that held the lock never lets it go, so no #end then
        if (!g_csv_mx.try_lock()) return;
        if (g_csv) fprintf(g_csv, "#end,%lld,%.1f\n", unix_ms(), scsk_layer_now()), fclose(g_csv), g_csv = nullptr;
        g_csv_mx.unlock();
    }
} g_end;

static void ov_config();
#ifdef _WIN32
void scsk_layer_admit() {
    static std::once_flag once;
    std::call_once(once, [] {
        const std::wstring exe = module_path(nullptr);
        g_exe_dir = exe.substr(0, exe.find_last_of(L'\\') + 1);  // npos + 1 = 0: none, which refuses
        std::string read;
        const bool whole = !g_exe_dir.empty() && ini_bytes(g_exe_dir + L"scskiller.ini", read);
        t_ini = &read;
        struct Live {
            ~Live() { t_ini = nullptr; }
        } live;
        bool risk = false;
        std::string seen;
        // the layer is loaded from where its registration names: "elsewhere" doesn't apply
        const char* why = admission_verdict(whole, read, false, risk, seen);
        const std::wstring rec = why ? L"" : recorder_dir(g_exe_dir), out = rec.empty() ? L"" : rec + L"\\vulkan\\" + file_key(exe) + L"\\";
        if (!why && out.empty()) why = "SCSKiller's recorder folder can't be had";
        if (!why) CreateDirectoryW((rec + L"\\vulkan").c_str(), nullptr), CreateDirectoryW(out.c_str(), nullptr);
        if (!why && GetFileAttributesW(out.c_str()) == INVALID_FILE_ATTRIBUTES) why = "the recording folder can't be made";
        if (!why && !acp(out + L"scskiller", g_dump)) why = "the recording folder's path doesn't fit the code page";
        if (!why) {
            g_session_unix = unix_ms();
            std::string head = "#session," + std::to_string(g_session_unix) + "," + utf8(exe.substr(exe.find_last_of(L'\\') + 1)) + "\n";
            char clock[48];
            snprintf(clock, sizeof clock, "#clock,%.1f\n#api,vulkan\n", scsk_layer_now());
            head += clock;
            cache_lines(head);
            std::lock_guard l(g_csv_mx);
            if ((g_csv = _wfopen((out + L"scskiller_creates.csv").c_str(), L"a"))) fputs(head.c_str(), g_csv), fflush(g_csv), frames_start(out), ov_config();
            if (risk)
                if (FILE* log = _wfopen((out + L"scskiller.log").c_str(), L"a"))
                    fprintf(log, "risk=1: recording at the user's own risk, anti-cheat seen: %s\n", seen.c_str()), fclose(log);
        }
        g_admission = why ? -1 : 1;
        refused(why);
    });
}
#else
// A file of the recording folder opened to append: never through a link, only a regular file with no other name.
static FILE* append(const std::string& path) {
    const int fd = open(path.c_str(), O_WRONLY | O_APPEND | O_CREAT | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC, 0666);
    struct stat st;
    if (fd >= 0 && (fstat(fd, &st) || !S_ISREG(st.st_mode) || st.st_nlink != 1)) close(fd);
    else if (fd >= 0)
        if (FILE* f = fdopen(fd, "a")) return f;
    return nullptr;
}

static bool real_dir(const std::string& path) {
    struct stat st;
    return !lstat(path.c_str(), &st) && S_ISDIR(st.st_mode);
}

void scsk_layer_admit() {
    static std::once_flag once;
    std::call_once(once, [] {
        const std::string exe = module_path();
        g_exe_dir = exe.substr(0, exe.find_last_of('/') + 1);  // npos + 1 = 0: none, which refuses
        std::string read;
        const bool whole = !g_exe_dir.empty() && ini_bytes(g_exe_dir + "scskiller.ini", read);
        const std::string rec = recorder_dir_now(g_exe_dir);
        t_ini = &read, t_rec = &rec;
        struct Live {
            ~Live() { t_ini = nullptr, t_rec = nullptr; }
        } live;
        bool risk = false;
        std::string seen;
        const char* why = admission_verdict(whole, read, false, risk, seen);
        const std::string out = why || rec.empty() ? "" : rec + "/vulkan/" + file_key(exe) + "/";
        if (!why && out.empty()) why = "SCSKiller's recorder folder can't be had";
        if (!why) mkdir((rec + "/vulkan").c_str(), 0777), mkdir(out.c_str(), 0777);
        if (!why && (!real_dir(rec + "/vulkan") || !real_dir(out))) why = "the recording folder can't be made";
        if (!why) {
            g_session_unix = unix_ms();
            std::string head = "#session," + std::to_string(g_session_unix) + "," + base_name(exe) + "\n";
            char clock[48];
            snprintf(clock, sizeof clock, "#clock,%.1f\n#api,vulkan\n", scsk_layer_now());
            head += clock;
            head += g_load_env.cache;
            std::lock_guard l(g_csv_mx);
            // held shared while the process lives: the app deletes no archive of the folder while it is (VulkanPlan.Import)
            // through a descriptor of its own, never closed: the lock outlasts the csv's close and Fossilize's last write
            if (!(g_csv = append(out + "scskiller_creates.csv")) || flock(fileno(g_csv), LOCK_SH) || fcntl(fileno(g_csv), F_DUPFD_CLOEXEC, 3) < 0)
                why = "the recording's csv can't be opened";
            else {
                fputs(head.c_str(), g_csv), fflush(g_csv);
                g_dump = out + "scskiller";
                frames_start(out);
                ov_config();
                if (risk)
                    if (FILE* log = append(out + "scskiller.log"))
                        fprintf(log, "risk=1: recording at the user's own risk, anti-cheat seen: %s\n", seen.c_str()), fclose(log);
            }
            if (why && g_csv) fclose(g_csv), g_csv = nullptr;
        }
        g_admission = why ? -1 : 1;
        refused(why);
    });
}
#endif

// A name field of scskiller_layer.hpp's #vkinstance: empty for null, else "=" and the bytes with %, comma and control bytes
// as %XX.
static std::string field(const char* s) {
    if (!s) return "";
    std::string r = "=";
    for (const unsigned char* p = (const unsigned char*)s; *p && r.size() < 1024; ++p) {
        char b[4];
        if (*p < 0x20 || *p == '%' || *p == ',' || *p == 0x7f) snprintf(b, sizeof b, "%%%02X", *p), r += b;
        else r += (char)*p;
    }
    return r;
}

static std::string names(uint32_t n, const char* const* v) {
    std::string r;
    for (uint32_t i = 0; i < n && v; ++i)
        if (v[i] && !strpbrk(v[i], ", \r\n")) r += (r.empty() ? "" : " ") + std::string(v[i]);
    return r;
}

static void line(const std::string& s) {
    std::lock_guard l(g_csv_mx);
    if (g_csv) fputs(s.c_str(), g_csv), fflush(g_csv);
}

void scsk_layer_instance(const VkInstanceCreateInfo* ci) {
    if (g_admission <= 0 || !ci) return;
    const VkApplicationInfo* a = ci->pApplicationInfo;
    char v[64];
    snprintf(v, sizeof v, "%u,%u,%u,", a ? a->apiVersion : 0, a ? a->applicationVersion : 0, a ? a->engineVersion : 0);
    line("#vkinstance," + std::string(a ? v : ",,,") + field(a ? a->pApplicationName : nullptr) + "," + field(a ? a->pEngineName : nullptr) + "," +
         names(ci->enabledExtensionCount, ci->ppEnabledExtensionNames) + "\n");
}

void scsk_layer_device(const VkDeviceCreateInfo* ci) {
    if (g_admission <= 0 || !ci) return;
    std::string q;
    for (uint32_t i = 0; i < ci->queueCreateInfoCount && ci->pQueueCreateInfos; ++i) {
        const VkDeviceQueueCreateInfo& d = ci->pQueueCreateInfos[i];
        char b[64];
        snprintf(b, sizeof b, "%s%u:%u:%u:", q.empty() ? "" : " ", d.queueFamilyIndex, d.queueCount, d.flags);
        q += b;
        for (uint32_t k = 0; k < d.queueCount && d.pQueuePriorities && k < 64; ++k) snprintf(b, sizeof b, "%s%g", k ? "/" : "", d.pQueuePriorities[k]), q += b;
    }
    std::string f;
    auto bits = [&](uint32_t type, const VkBool32* b, uint32_t n) {
        f += (f.empty() ? "" : " ") + std::to_string(type) + ":";
        for (uint32_t i = 0; i < n; ++i) f += b[i] ? '1' : '0';
    };
    constexpr uint32_t core = sizeof(VkPhysicalDeviceFeatures) / sizeof(VkBool32);
    if (ci->pEnabledFeatures) bits(VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2, &ci->pEnabledFeatures->robustBufferAccess, core);
    int depth = 0;
    for (auto* s = (const VkBaseInStructure*)ci->pNext; s && depth++ < 256; s = s->pNext) {
        if (s->sType == VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2) {
            bits(s->sType, &((const VkPhysicalDeviceFeatures2*)s)->features.robustBufferAccess, core);
            continue;
        }
        if (s->sType == VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO) continue;  // the loader's own link, not the game's
        auto* k = std::find_if(std::begin(kVkFeatureStructs), std::end(kVkFeatureStructs), [&](auto& e) { return e.type == s->sType; });
        if (k != std::end(kVkFeatureStructs)) bits(s->sType, (const VkBool32*)(s + 1), k->bools);  // the bools follow the header
        else f += (f.empty() ? "" : " ") + std::to_string((uint32_t)s->sType) + ":?";
    }
    line("#vkdevice," + names(ci->enabledExtensionCount, ci->ppEnabledExtensionNames) + "," + q + "," + f + "\n");
}

static void ov_created(double end, double ms, uint32_t tid, uint32_t count);
void scsk_layer_created(char kind, uint32_t count, double t0, double t1) {
    if (g_admission <= 0 || !count) return;
    // ponytail: a batch's time split evenly over its pipelines; per pipeline needs a timer inside Fossilize's loop
    const double each = (t1 - t0) / count;
#ifdef _WIN32
    const unsigned long tid = GetCurrentThreadId();
#else
    const unsigned long tid = (unsigned long)syscall(SYS_gettid);
#endif
    std::lock_guard l(g_csv_mx);
    if (!g_csv) return;
    ov_created(t1, each, (uint32_t)tid, count);
    for (uint32_t i = 0; i < count; ++i) fprintf(g_csv, "%.1f,%c,0,0,%.3f,,0.000,%lu,%d\n", t1, kind, each, tid, (int)t_presenting);
    fflush(g_csv);
}

// The in-game panel of a before and after run, as the D3D recorder draws it (overlay.h, proxy.cpp's overlay_present):
// asked for by the signed ini's overlay=1, ab_play_s and ab_tag. Painted on the CPU only when it changes, copied into a
// host-visible buffer, and copied into the presented image by a command buffer submitted on the present's queue: no
// shader, pipeline or render pass. The submit waits on the present's semaphores and signals the overlay's own, which the
// present waits on instead. Per swapchain image one command buffer, fence, semaphore and buffer region: a present whose
// image's last copy isn't done yet goes without the panel, nothing waits. Any failure turns it off for the launch, and
// the present goes down as the game made it.
static struct { bool on, after; int play_s; } g_ovc;
static thread_local int t_ov_index = -1;  // the swapchain of this thread's present the overlay draws on
static std::atomic<bool> g_ov_off, g_ov_drew, g_ov_done_seen;
static std::atomic<uint32_t> g_ov_stutters;
static std::atomic<double> g_ov_startup{-1};
// never destroyed, as g_fr: presents and the writer run on while the exit destroys statics
static std::mutex& g_ovmx = *new std::mutex;  // every overlay record, never waited on in a present: one that finds it taken skips the draw
static ov::Feed g_ovfeed;
static ov::Creates g_ovcreates;  // under g_csv_mx for its writes
// under g_ovmx
static ov::Clock g_ovclock;
static ov::Graph g_ovgraph;
static ov::Slow g_ovslow;
static ov::Layout g_ovl;
static bool g_ov_done;
static std::mutex& g_ovlog_mx = *new std::mutex;
static std::string& g_ovlog = *new std::string;  // lines for scskiller.log, written by the frames writer

static void ovlog(const char* fmt, ...) {
    char b[320];
    va_list a;
    va_start(a, fmt);
    vsnprintf(b, sizeof b, fmt, a);
    va_end(a);
    std::lock_guard l(g_ovlog_mx);
    try {
        if (g_ovlog.size() < 65536) g_ovlog += std::string("overlay: ") + b + "\n";
    } catch (...) {
    }
}
static void ov_off(const char* what, int r) {
    if (!g_ov_off.exchange(true)) ovlog("off for this launch: %s (%d)", what, r);
}

static void ov_config() {
    if (wcstol(ini_string(L"overlay").c_str(), nullptr, 10) == 0) return;  // not a before and after run
    const long play_s = wcstol(ini_string(L"ab_play_s").c_str(), nullptr, 10);
    const std::wstring tag = ini_string(L"ab_tag");
    const char* why = play_s <= 0 || play_s > 86400 ? "no ab_play_s" : tag != L"before" && tag != L"after" ? "ab_tag isn't before or after" : !g_frames_on ? "frame times aren't taken" : nullptr;
    if (why) return ovlog("off (%s)", why);
    ov::luts();  // made here: a present never computes them
    g_ovc = {true, tag == L"after", (int)play_s};
    ovlog("on (%s, %d s of play)", g_ovc.after ? "after" : "before", g_ovc.play_s);
}

static void ov_created(double end, double ms, uint32_t tid, uint32_t count) {
    if (!g_ovc.on) return;
    const uint8_t f = ov::Creates::kCompile | (ms >= 10 ? ov::Creates::kBlocks : 0) | (t_presenting ? ov::Creates::kPresents : 0);
    for (uint32_t i = 0; i < count && i < 256; ++i) g_ovcreates.add(end, ms, tid, f);
}

// the frames writer's, once a second
static void ov_flush() {
    std::string lines;
    {
        std::lock_guard l(g_ovlog_mx);
        lines.swap(g_ovlog);
    }
    if (static bool startup; !startup && g_ov_startup >= 0) startup = true, lines += "overlay: play counted from the first present\n";
    if (static uint32_t logged; g_ov_stutters != logged) logged = g_ov_stutters, lines += "overlay: a shader compilation stutter (" + std::to_string(logged) + " so far)\n";
    if (static bool done; !done && g_ov_done_seen) done = true, lines += "overlay: " + std::to_string(g_ovc.play_s) + " s of play counted, done\n";
    if (static bool wrote; !wrote && g_ov_drew) wrote = true, lines += "overlay: first frame drawn\n", line("#overlay,1\n");
    if (lines.empty()) return;
#ifdef _WIN32
    FILE* f = _wfopen(g_fr.log.c_str(), L"a");
#else
    FILE* f = append(g_fr.log);
#endif
    if (f) fputs(lines.c_str(), f), fclose(f);
}

using PFN_SetLoaderData = VkResult(VKAPI_PTR*)(VkDevice, void*);
#define OV_DEVICE_FNS(X)                                                                                                          \
    X(GetSwapchainImagesKHR) X(CreateCommandPool) X(DestroyCommandPool) X(AllocateCommandBuffers) X(BeginCommandBuffer)            \
    X(EndCommandBuffer) X(CmdPipelineBarrier) X(CmdCopyBufferToImage) X(QueueSubmit) X(CreateFence) X(DestroyFence) X(GetFenceStatus) \
    X(ResetFences) X(WaitForFences) X(CreateSemaphore) X(DestroySemaphore) X(CreateBuffer) X(DestroyBuffer)                           \
    X(GetBufferMemoryRequirements) X(AllocateMemory) X(FreeMemory) X(BindBufferMemory) X(MapMemory)
struct OvDev {
    VkDevice dev;
    VkPhysicalDevice gpu;
    PFN_SetLoaderData set_loader = nullptr;
    PFN_vkGetPhysicalDeviceSurfaceCapabilitiesKHR caps = nullptr;
    VkPhysicalDeviceMemoryProperties mem{};
    std::vector<VkQueueFamilyProperties> families;
#define X(n) PFN_vk##n n = nullptr;
    OV_DEVICE_FNS(X)
#undef X
};
struct OvKey {
    long secs = -1, fill = 0, column = -1;
    uint32_t found = 0;
    bool done = false, flash = false;
    bool operator==(const OvKey&) const = default;
};
constexpr VkDeviceSize kOvRegion = ((VkDeviceSize)ov::kMaxW * ov::kMaxH * 8 + 255) & ~(VkDeviceSize)255;  // FP16 has 8 bytes a pixel
constexpr uint32_t kOvImages = 8;  // the staging memory's bound: a swapchain of more images isn't drawn
struct OvImg {
    VkImage img = VK_NULL_HANDLE;
    VkCommandBuffer cb = VK_NULL_HANDLE;
    VkFence fence = VK_NULL_HANDLE;
    VkSemaphore sem = VK_NULL_HANDLE;
    bool used = false;   // its fence has a submit
    uint64_t gen = 0;    // the panel its region holds
};
struct OvChain {
    uint64_t sc = 0;
    OvDev* d = nullptr;
    VkExtent2D ext{};
    ov::Out out = ov::kNone;
    VkBuffer buf = VK_NULL_HANDLE;
    VkDeviceMemory mem = VK_NULL_HANDLE;
    uint8_t* mapped = nullptr;
    std::vector<OvImg> imgs;
    VkCommandPool pool = VK_NULL_HANDLE;
    uint32_t family = ~0u;
    bool said = false;  // a present it can't be drawn in, logged
    std::vector<uint32_t> canvas;
    std::vector<uint8_t> px;  // the panel in the image's format, rows of its width
    OvKey key;
    RECT at{};
    uint64_t gen = 0;
};
static auto& g_ovdevs = *new std::vector<std::unique_ptr<OvDev>>;
static auto& g_ovqueues = *new std::vector<std::pair<VkQueue, std::pair<OvDev*, uint32_t>>>;
static auto& g_ovchains = *new std::vector<std::unique_ptr<OvChain>>;

static OvDev* ov_dev(VkDevice dev) {
    for (auto& d : g_ovdevs)
        if (d->dev == dev) return d.get();
    return nullptr;
}

void scsk_layer_device_made(VkPhysicalDevice_T* gpu, VkDevice_T* dev, VkInstance_T* inst, void* gipa_, void* gdpa_, void* set_loader) {
    if (!g_ovc.on || g_ov_off) return;
    auto gipa = (PFN_vkGetInstanceProcAddr)gipa_;
    auto gdpa = (PFN_vkGetDeviceProcAddr)gdpa_;
    std::lock_guard l(g_ovmx);
    try {
        auto d = std::make_unique<OvDev>();
        d->dev = dev, d->gpu = gpu, d->set_loader = (PFN_SetLoaderData)set_loader;
        d->caps = (PFN_vkGetPhysicalDeviceSurfaceCapabilitiesKHR)gipa(inst, "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
        auto memp = (PFN_vkGetPhysicalDeviceMemoryProperties)gipa(inst, "vkGetPhysicalDeviceMemoryProperties");
        auto qfp = (PFN_vkGetPhysicalDeviceQueueFamilyProperties)gipa(inst, "vkGetPhysicalDeviceQueueFamilyProperties");
        bool all = d->caps && memp && qfp;
#define X(n) all &= (d->n = (PFN_vk##n)gdpa(dev, "vk" #n)) != nullptr;
        OV_DEVICE_FNS(X)
#undef X
        if (!all) return ovlog("device %p not drawn on: a function it needs is missing", (void*)dev);
        memp(gpu, &d->mem);
        uint32_t n = 0;
        qfp(gpu, &n, nullptr);
        d->families.resize(n);
        qfp(gpu, &n, d->families.data());
        d->families.resize(n);
        std::erase_if(g_ovdevs, [&](auto& e) { return e->dev == dev; });
        g_ovdevs.push_back(std::move(d));
    } catch (...) {
        ov_off("out of memory", 0);
    }
}

void scsk_layer_queue(VkDevice_T* dev, uint32_t family, VkQueue_T* q) {
    if (!g_ovc.on || g_ov_off || !q) return;
    std::lock_guard l(g_ovmx);
    OvDev* d = ov_dev(dev);
    if (!d) return;
    try {
        for (auto& e : g_ovqueues)
            if (e.first == q) return (void)(e.second = {d, family});
        g_ovqueues.push_back({q, {d, family}});
    } catch (...) {
        ov_off("out of memory", 0);
    }
}

// Waits on its own fences only, a second at most: a GPU still on them leaks the chain's objects rather than free them under it.
static void ov_free(OvChain& c) {
    OvDev& d = *c.d;
    VkFence used[kOvImages];
    uint32_t n = 0;
    for (auto& m : c.imgs)
        if (m.used) used[n++] = m.fence;
    if (n && d.WaitForFences(d.dev, n, used, VK_TRUE, 1000000000ull) != VK_SUCCESS) return ovlog("swapchain %llx: its copies aren't done, its objects left", (unsigned long long)c.sc);
    for (auto& m : c.imgs) {
        if (m.fence) d.DestroyFence(d.dev, m.fence, nullptr);
        if (m.sem) d.DestroySemaphore(d.dev, m.sem, nullptr);
    }
    if (c.pool) d.DestroyCommandPool(d.dev, c.pool, nullptr);  // frees its command buffers
    if (c.buf) d.DestroyBuffer(d.dev, c.buf, nullptr);
    if (c.mem) d.FreeMemory(d.dev, c.mem, nullptr);  // unmaps it
}
static void ov_drop(uint64_t sc, OvDev* d) {
    for (auto it = g_ovchains.begin(); it != g_ovchains.end(); ++it)
        if ((*it)->sc == sc && (*it)->d == d) {
            ov_free(**it);
            g_ovchains.erase(it);
            return;
        }
}

void scsk_layer_device_gone(VkDevice_T* dev) {
    if (!g_ovc.on) return;
    std::lock_guard l(g_ovmx);
    OvDev* d = ov_dev(dev);
    if (!d) return;
    for (size_t i = g_ovchains.size(); i--;)
        if (g_ovchains[i]->d == d) ov_free(*g_ovchains[i]), g_ovchains.erase(g_ovchains.begin() + i);
    std::erase_if(g_ovqueues, [&](auto& e) { return e.second.first == d; });
    std::erase_if(g_ovdevs, [&](auto& e) { return e.get() == d; });
}

const VkSwapchainCreateInfoKHR* scsk_layer_swapchain_info(VkDevice_T* dev, const VkSwapchainCreateInfoKHR* ci, VkSwapchainCreateInfoKHR* copy) {
    if (!g_ovc.on || g_ov_off || !ci || (ci->imageUsage & VK_IMAGE_USAGE_TRANSFER_DST_BIT)) return ci;
    std::lock_guard l(g_ovmx);
    OvDev* d = ov_dev(dev);
    VkSurfaceCapabilitiesKHR caps{};
    if (!d || d->caps(d->gpu, ci->surface, &caps) != VK_SUCCESS || !(caps.supportedUsageFlags & VK_IMAGE_USAGE_TRANSFER_DST_BIT)) return ci;
    *copy = *ci;
    copy->imageUsage |= VK_IMAGE_USAGE_TRANSFER_DST_BIT;
    return copy;
}

// A2B10G10R10 has R in the low bits, as DXGI's R10G10B10A2. FP16 is drawn only as extended linear sRGB: in sRGB nonlinear
// its values aren't scRGB's.
static ov::Out ov_out(VkFormat f, VkColorSpaceKHR cs) {
    const bool srgb = cs == VK_COLOR_SPACE_SRGB_NONLINEAR_KHR;
    switch (f) {
    case VK_FORMAT_B8G8R8A8_UNORM:
    case VK_FORMAT_B8G8R8A8_SRGB: return srgb ? ov::kBgra8 : ov::kNone;
    case VK_FORMAT_R8G8B8A8_UNORM:
    case VK_FORMAT_R8G8B8A8_SRGB:
    case VK_FORMAT_A8B8G8R8_UNORM_PACK32:
    case VK_FORMAT_A8B8G8R8_SRGB_PACK32: return srgb ? ov::kRgba8 : ov::kNone;
    case VK_FORMAT_A2B10G10R10_UNORM_PACK32: return srgb ? ov::kRgb10 : cs == VK_COLOR_SPACE_HDR10_ST2084_EXT ? ov::kPq10 : ov::kNone;
    case VK_FORMAT_R16G16B16A16_SFLOAT: return cs == VK_COLOR_SPACE_EXTENDED_SRGB_LINEAR_EXT ? ov::kScRgb : ov::kNone;
    default: return ov::kNone;
    }
}

void scsk_layer_swapchain_made(VkDevice_T* dev, uint64_t sc, const VkSwapchainCreateInfoKHR* ci, bool added) {
    if (!g_ovc.on || g_ov_off) return;
    std::lock_guard l(g_ovmx);
    OvDev* d = ov_dev(dev);
    if (!d) return;
    ov_drop(sc, d);  // a handle a destroyed swapchain had
    const ov::Out out = ov_out(ci->imageFormat, ci->imageColorSpace);
    const bool shared = ci->presentMode == VK_PRESENT_MODE_SHARED_DEMAND_REFRESH_KHR || ci->presentMode == VK_PRESENT_MODE_SHARED_CONTINUOUS_REFRESH_KHR;
    const char* why = !(ci->imageUsage & VK_IMAGE_USAGE_TRANSFER_DST_BIT) ? "the surface takes no transfer writes"
                      : ci->flags & VK_SWAPCHAIN_CREATE_PROTECTED_BIT_KHR ? "protected"
                      : shared ? "a shared present mode"
                      : !out ? "a format or colour space it doesn't draw"
                      : nullptr;
    if (why) return ovlog("swapchain %llx (format %d, colour space %d) not drawn: %s", (unsigned long long)sc, (int)ci->imageFormat, (int)ci->imageColorSpace, why);
    VkSwapchainKHR h = (VkSwapchainKHR)sc;
    uint32_t n = 0;
    VkImage imgs[kOvImages];
    VkResult r = d->GetSwapchainImagesKHR(d->dev, h, &n, nullptr);
    if (r != VK_SUCCESS || !n || n > kOvImages) return ovlog("swapchain %llx not drawn: %u images (%d)", (unsigned long long)sc, n, (int)r);
    if ((r = d->GetSwapchainImagesKHR(d->dev, h, &n, imgs)) != VK_SUCCESS) return ov_off("vkGetSwapchainImagesKHR", r);
    std::unique_ptr<OvChain> c;
    try {
        c = std::make_unique<OvChain>();
        c->imgs.resize(n), c->canvas.resize((size_t)ov::kMaxW * ov::kMaxH), c->px.resize((size_t)ov::kMaxW * ov::kMaxH * ov::bytes(out));
    } catch (...) {
        return ov_off("out of memory", 0);
    }
    c->sc = sc, c->d = d, c->ext = ci->imageExtent, c->out = out;
    const char* what = "vkCreateBuffer";
    VkBufferCreateInfo bi{VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO, nullptr, 0, kOvRegion * n, VK_BUFFER_USAGE_TRANSFER_SRC_BIT, VK_SHARING_MODE_EXCLUSIVE};
    r = d->CreateBuffer(d->dev, &bi, nullptr, &c->buf);
    if (r == VK_SUCCESS) {
        VkMemoryRequirements req;
        d->GetBufferMemoryRequirements(d->dev, c->buf, &req);
        const VkMemoryPropertyFlags want = VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT;
        uint32_t type = 0;
        while (type < d->mem.memoryTypeCount && !((req.memoryTypeBits >> type & 1) && (d->mem.memoryTypes[type].propertyFlags & want) == want)) ++type;
        VkMemoryAllocateInfo ai{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO, nullptr, req.size, type};
        what = "no host-visible memory";
        r = type == d->mem.memoryTypeCount ? VK_ERROR_FEATURE_NOT_PRESENT : VK_SUCCESS;
        if (r == VK_SUCCESS) what = "vkAllocateMemory", r = d->AllocateMemory(d->dev, &ai, nullptr, &c->mem);
        if (r == VK_SUCCESS) what = "vkBindBufferMemory", r = d->BindBufferMemory(d->dev, c->buf, c->mem, 0);
        if (r == VK_SUCCESS) what = "vkMapMemory", r = d->MapMemory(d->dev, c->mem, 0, VK_WHOLE_SIZE, 0, (void**)&c->mapped);
    }
    for (uint32_t i = 0; i < n && r == VK_SUCCESS; ++i) {
        c->imgs[i].img = imgs[i];
        VkFenceCreateInfo fi{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
        VkSemaphoreCreateInfo si{VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO};
        what = "vkCreateFence", r = d->CreateFence(d->dev, &fi, nullptr, &c->imgs[i].fence);
        if (r == VK_SUCCESS) what = "vkCreateSemaphore", r = d->CreateSemaphore(d->dev, &si, nullptr, &c->imgs[i].sem);
    }
    if (r != VK_SUCCESS) return ov_free(*c), ov_off(what, r);
    try {
        g_ovchains.push_back(std::move(c));
    } catch (...) {
        return ov_free(*c), ov_off("out of memory", 0);
    }
    ovlog("swapchain %llx %ux%u format %d colour space %d%s: drawn on", (unsigned long long)sc, ci->imageExtent.width, ci->imageExtent.height, (int)ci->imageFormat, (int)ci->imageColorSpace,
          added ? ", transfer usage added" : "");
}

void scsk_layer_swapchain_gone(VkDevice_T* dev, uint64_t sc) {
    if (!g_ovc.on) return;
    std::lock_guard l(g_ovmx);
    if (OvDev* d = ov_dev(dev)) ov_drop(sc, d);
}

// The command buffers, at the chain's first present: its queue's family is known only then.
static VkResult ov_pool(OvChain& c, uint32_t family) {
    OvDev& d = *c.d;
    VkCommandPoolCreateInfo pi{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO, nullptr, VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT, family};
    VkResult r = d.CreateCommandPool(d.dev, &pi, nullptr, &c.pool);
    for (auto& m : c.imgs) {
        VkCommandBufferAllocateInfo ai{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO, nullptr, c.pool, VK_COMMAND_BUFFER_LEVEL_PRIMARY, 1};
        if (r == VK_SUCCESS) r = d.AllocateCommandBuffers(d.dev, &ai, &m.cb);
        if (r != VK_SUCCESS) break;
        if (d.set_loader) r = d.set_loader(d.dev, m.cb);
        else *(void**)m.cb = *(void**)d.dev;  // a loader without the callback: its dispatch is the device's
    }
    c.family = family;
    return r;
}

// Under g_ovmx: the frames fed since, the panel's text, and the draw into c's image index i of pi.
static const VkPresentInfoKHR* ov_draw(VkQueue q, const VkPresentInfoKHR* pi, VkPresentInfoKHR* copy, OvChain& c, uint32_t i) {
    g_ovfeed.drain([](double t) {
        double prev = g_ovclock.last, played = g_ovclock.played;
        if (g_ovclock.frame(t)) g_ov_startup.store(g_ovclock.startup, std::memory_order_relaxed);
        if (prev < 0) return;
        g_ovgraph.frame(prev, t);
        if (played < g_ovc.play_s * 1000.0) g_ovslow.frame(prev, t);
    });
    const double t = scsk_layer_now();
    g_ovslow.check(g_ovcreates, t, g_ovclock.startup, g_ovgraph);
    g_ov_stutters.store(g_ovslow.found, std::memory_order_relaxed);
    const double total = g_ovc.play_s * 1000.0, played = g_ovclock.shown(t), frac = played / total;
    const bool done = played >= total;
    if (done && !g_ov_done) g_ov_done = true, g_ov_done_seen.store(true, std::memory_order_relaxed);

    OvDev& d = *c.d;
    uint32_t family = ~0u;
    for (auto& e : g_ovqueues)
        if (e.first == q) family = e.second.second;
    const VkQueueFlags can = VK_QUEUE_GRAPHICS_BIT | VK_QUEUE_COMPUTE_BIT | VK_QUEUE_TRANSFER_BIT;
    const char* why = family >= d.families.size() ? "its queue isn't known"
                      : !(d.families[family].queueFlags & can) ? "its queue takes no copies"
                      : c.family != ~0u && c.family != family ? "presented from another queue family"
                      : pi->waitSemaphoreCount > 16 ? "more than 16 wait semaphores"
                      : nullptr;
    if (why) {
        if (!c.said) c.said = true, ovlog("swapchain %llx: a present not drawn in: %s", (unsigned long long)c.sc, why);
        return pi;
    }
    VkResult r;
    if (c.family == ~0u && (r = ov_pool(c, family)) != VK_SUCCESS) return ov_off("its command buffers", r), pi;
    const uint32_t idx = pi->pImageIndices[i];
    if (idx >= c.imgs.size()) return pi;
    OvImg& m = c.imgs[idx];
    if (m.used && (r = d.GetFenceStatus(d.dev, m.fence)) != VK_SUCCESS) {
        if (r != VK_NOT_READY) ov_off("vkGetFenceStatus", r);
        return pi;  // its last copy isn't done: this frame goes without the panel
    }

    char left[48], widest[48];
    ov::countdown(g_ovc.after, (total - played) / 1000, left), ov::countdown(g_ovc.after, g_ovc.play_s, widest);
    const bool flash = g_ovslow.flashing(t);
    OvKey k;
    k.secs = done ? 0 : (long)std::ceil((total - played) / 1000), k.done = done, k.flash = flash;
    k.column = g_ovgraph.newest, k.found = g_ovslow.found, k.fill = std::lround(std::clamp(frac, 0.0, 1.0) * 1000);
    if (!(c.key == k) || !c.gen) {
        ov::layout(c.ext.width, c.ext.height, widest, true, g_ovl);
        c.at = g_ovl.at, c.key = k, ++c.gen;
        const long w = c.at.right - c.at.left, h = c.at.bottom - c.at.top;
        if (w > 0) {
            ov::paint(g_ovl, done ? ov::kDone : left, frac, g_ovgraph, flash, c.canvas.data(), ov::kMaxW);
            ov::convert(c.canvas.data() + g_ovl.sy * ov::kMaxW + g_ovl.sx, ov::kMaxW, w, h, c.out, c.px.data(), w * ov::bytes(c.out));
        }
    }
    const long w = c.at.right - c.at.left, h = c.at.bottom - c.at.top;
    if (w <= 0 || h <= 0) return pi;  // nothing of the panel fits
    if (m.gen != c.gen) memcpy(c.mapped + kOvRegion * idx, c.px.data(), (size_t)w * h * ov::bytes(c.out)), m.gen = c.gen;

    if (m.used && (r = d.ResetFences(d.dev, 1, &m.fence)) != VK_SUCCESS) return ov_off("vkResetFences", r), pi;
    m.used = false;
    VkCommandBufferBeginInfo bi{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO, nullptr, VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT};
    if ((r = d.BeginCommandBuffer(m.cb, &bi)) != VK_SUCCESS) return ov_off("vkBeginCommandBuffer", r), pi;
    VkImageMemoryBarrier b{VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER, nullptr, 0, VK_ACCESS_TRANSFER_WRITE_BIT, VK_IMAGE_LAYOUT_PRESENT_SRC_KHR,
                           VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_QUEUE_FAMILY_IGNORED, VK_QUEUE_FAMILY_IGNORED, m.img,
                           {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, VK_REMAINING_ARRAY_LAYERS}};
    d.CmdPipelineBarrier(m.cb, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0, nullptr, 0, nullptr, 1, &b);
    VkBufferImageCopy region{kOvRegion * idx, (uint32_t)w, (uint32_t)h, {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1}, {(int32_t)c.at.left, (int32_t)c.at.top, 0},
                             {(uint32_t)w, (uint32_t)h, 1}};
    d.CmdCopyBufferToImage(m.cb, c.buf, m.img, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region);
    b.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT, b.dstAccessMask = 0;
    b.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, b.newLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    d.CmdPipelineBarrier(m.cb, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT, 0, 0, nullptr, 0, nullptr, 1, &b);
    if ((r = d.EndCommandBuffer(m.cb)) != VK_SUCCESS) return ov_off("vkEndCommandBuffer", r), pi;
    VkPipelineStageFlags stages[16];
    std::fill(stages, stages + 16, (VkPipelineStageFlags)VK_PIPELINE_STAGE_TRANSFER_BIT);
    VkSubmitInfo si{VK_STRUCTURE_TYPE_SUBMIT_INFO, nullptr, pi->waitSemaphoreCount, pi->pWaitSemaphores, stages, 1, &m.cb, 1, &m.sem};
    // a failed submit leaves the game's semaphores as they were: the present goes down as the game made it
    if ((r = d.QueueSubmit(q, 1, &si, m.fence)) != VK_SUCCESS) return ov_off("vkQueueSubmit", r), pi;
    m.used = true;
    g_ov_drew.store(true, std::memory_order_relaxed);
    *copy = *pi;
    copy->waitSemaphoreCount = 1, copy->pWaitSemaphores = &m.sem;
    return copy;
}

// The game's external sync of q for its vkQueuePresentKHR covers the submit on q here.
const VkPresentInfoKHR* scsk_layer_present(VkQueue_T* q, const VkPresentInfoKHR* pi, VkPresentInfoKHR* copy) {
    t_ov_index = -1;
    if (!g_ovc.on || g_ov_off.load(std::memory_order_relaxed) || !pi || !pi->pSwapchains || !pi->pImageIndices) return pi;
    std::unique_lock l(g_ovmx, std::try_to_lock);
    if (!l) return pi;
    // a present of several swapchains: drawn on the first the overlay knows
    for (uint32_t i = 0; i < pi->swapchainCount; ++i)
        for (auto& c : g_ovchains)
            if (c->sc == (uint64_t)pi->pSwapchains[i]) {
                t_ov_index = (int)i;
                try {
                    return ov_draw(q, pi, copy, *c, i);
                } catch (...) {
                    return ov_off("out of memory", 0), pi;
                }
            }
    return pi;
}

static void ov_presented(const VkPresentInfoKHR* pi, int32_t res) {
    const int i = t_ov_index;
    t_ov_index = -1;
    if (i < 0 || !g_ovc.on) return;
    if ((pi->pResults ? pi->pResults[i] : res) >= 0) g_ovfeed.push(scsk_layer_now());
}
