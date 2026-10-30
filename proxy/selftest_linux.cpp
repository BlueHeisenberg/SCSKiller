// `selftest_linux vkrec`: the Vulkan recorder layer (libVkLayer_scskiller.so, beside this exe) on Linux, in a copy of this
// exe in a temp folder, made by `selftest_linux vkchild` on the system's loader (lavapipe will do). As selftest.cpp's vkrec:
// X armed (a signed scskiller.ini) with the layer explicit through VK_LAYER_PATH, FOSSILIZE_DUMP_PATH aimed at the copy's
// folder and a cache redirect set: the archive and the csv (#api,vulkan, #cache, #vkinstance, #vkdevice, a row per
// pipeline) go to the recorder folder's vulkan/<exe key>/, nothing but the ini is in the copy's folder. T the exe touched
// since it was signed, A an EasyAntiCheat folder beside it, U unarmed: each passes through, writes nothing there and
// leaves its refusal in refused/. Then the layer as the app writes it per game, an implicit one enabled by SteamAppId=4242,
// in a temp $XDG_DATA_HOME/vulkan/implicit_layer.d: I0 without SteamAppId isn't loaded, I1 with it records.
// P presents on a headless surface (SCSK_VK_PRESENT), recreates the swapchain and presents again: the presents return
// what P0, the same without the layer, gets, and scskiller_frames.bin holds every frame on one swap chain index, its launch
// record stamped as the csv's #session. PU presents unarmed: no frames file. PO is P with the ini's countdown lines: the
// image read back after each round shows the panel's "SCSKiller before" as overlay.h paints it, and nothing else of the
// image changed, while P and P0 leave the panel's place as the game cleared it. Its presents return what P0's do, and its
// second round's swapchain, made without transfer usage, gets it from the layer.
// Also the keys and the HMAC against values Python's hashlib made, as ScsKiller.FileKey and Signed make them.
#include "admit_posix.h"
#define VK_NO_PROTOTYPES
#include <vulkan/vulkan_core.h>
#include <sys/wait.h>
#include <filesystem>
#include <functional>
#include <random>
#include "vk_spirv.h"
#include "overlay.h"

extern "C" __attribute__((visibility("default"))) const int SCSKiller_WarmHost = 1;  // the tests' variables count here (LoadEnv)

static std::string g_dir;
static const std::string& admit_dir() { return g_dir; }

#define CHECK(x) \
    if (!(x)) return printf("FAILED %s:%d: %s\n", __FILE__, __LINE__, #x), 1

// SCSK_VK_PRESENT: 20 presents, a recreate (oldSwapchain), 20 more, then the frames writer's second. Each image is
// cleared to magenta (round 0, whose usage takes transfer writes) or only made presentable (round 1, whose usage doesn't:
// the overlay adds it) by a submit whose semaphore the present waits on. SCSK_VK_PIXELS: after each round an image is
// acquired again and read back into <SCSK_VK_PIXELS>.<round> (width, height, format, then its BGRA or RGBA bytes); Mesa's
// headless WSI keeps a presented image's contents. 0 when every present returned VK_SUCCESS, 4 when one was VK_SUBOPTIMAL_KHR.
static int presents(PFN_vkGetInstanceProcAddr gipa, VkInstance inst, VkPhysicalDevice gpu, VkDevice dev, uint32_t family, PFN_vkGetDeviceProcAddr gdpa) {
#define IFN(name) \
    auto name = (PFN_##name)gipa(inst, #name); \
    CHECK(name)
#define GFN(name) \
    auto name = (PFN_##name)gdpa(dev, #name); \
    CHECK(name)
    IFN(vkCreateHeadlessSurfaceEXT);
    IFN(vkDestroySurfaceKHR);
    IFN(vkGetPhysicalDeviceSurfaceSupportKHR);
    IFN(vkGetPhysicalDeviceSurfaceCapabilitiesKHR);
    IFN(vkGetPhysicalDeviceSurfaceFormatsKHR);
    IFN(vkGetPhysicalDeviceMemoryProperties);
    GFN(vkCreateSwapchainKHR);
    GFN(vkDestroySwapchainKHR);
    GFN(vkGetSwapchainImagesKHR);
    GFN(vkAcquireNextImageKHR);
    GFN(vkQueuePresentKHR);
    GFN(vkGetDeviceQueue);
    GFN(vkCreateFence);
    GFN(vkWaitForFences);
    GFN(vkResetFences);
    GFN(vkDestroyFence);
    GFN(vkCreateSemaphore);
    GFN(vkDestroySemaphore);
    GFN(vkCreateCommandPool);
    GFN(vkDestroyCommandPool);
    GFN(vkAllocateCommandBuffers);
    GFN(vkBeginCommandBuffer);
    GFN(vkEndCommandBuffer);
    GFN(vkCmdPipelineBarrier);
    GFN(vkCmdClearColorImage);
    GFN(vkCmdCopyImageToBuffer);
    GFN(vkQueueSubmit);
    GFN(vkCreateBuffer);
    GFN(vkDestroyBuffer);
    GFN(vkGetBufferMemoryRequirements);
    GFN(vkAllocateMemory);
    GFN(vkFreeMemory);
    GFN(vkBindBufferMemory);
    GFN(vkMapMemory);
    GFN(vkDeviceWaitIdle);
    const char* pixels = getenv("SCSK_VK_PIXELS");
    VkHeadlessSurfaceCreateInfoEXT hi{VK_STRUCTURE_TYPE_HEADLESS_SURFACE_CREATE_INFO_EXT};
    VkSurfaceKHR surf;
    CHECK(vkCreateHeadlessSurfaceEXT(inst, &hi, nullptr, &surf) == VK_SUCCESS);
    VkBool32 can = VK_FALSE;
    CHECK(vkGetPhysicalDeviceSurfaceSupportKHR(gpu, family, surf, &can) == VK_SUCCESS && can);
    VkSurfaceCapabilitiesKHR caps;
    CHECK(vkGetPhysicalDeviceSurfaceCapabilitiesKHR(gpu, surf, &caps) == VK_SUCCESS);
    CHECK((caps.supportedUsageFlags & VK_IMAGE_USAGE_TRANSFER_SRC_BIT) && (caps.supportedUsageFlags & VK_IMAGE_USAGE_TRANSFER_DST_BIT));
    uint32_t nf = 1;
    VkSurfaceFormatKHR fmt;
    CHECK(vkGetPhysicalDeviceSurfaceFormatsKHR(gpu, surf, &nf, &fmt) >= 0 && nf == 1);
    VkQueue q;
    vkGetDeviceQueue(dev, family, 0, &q);
    VkFenceCreateInfo fci{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
    VkFence fence;
    CHECK(vkCreateFence(dev, &fci, nullptr, &fence) == VK_SUCCESS);
    VkCommandPoolCreateInfo pci{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO, nullptr, VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT, family};
    VkCommandPool pool;
    CHECK(vkCreateCommandPool(dev, &pci, nullptr, &pool) == VK_SUCCESS);
    constexpr uint32_t kMax = 8;
    VkCommandBuffer cbs[kMax];
    VkCommandBufferAllocateInfo cai{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO, nullptr, pool, VK_COMMAND_BUFFER_LEVEL_PRIMARY, kMax};
    CHECK(vkAllocateCommandBuffers(dev, &cai, cbs) == VK_SUCCESS);
    VkFence done[kMax];
    VkSemaphore ready[kMax];
    bool busy[kMax] = {};
    for (uint32_t i = 0; i < kMax; ++i) {
        VkSemaphoreCreateInfo si{VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO};
        CHECK(vkCreateFence(dev, &fci, nullptr, &done[i]) == VK_SUCCESS && vkCreateSemaphore(dev, &si, nullptr, &ready[i]) == VK_SUCCESS);
    }
    const VkImageSubresourceRange all{VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
    VkSwapchainKHR sc = VK_NULL_HANDLE;
    bool suboptimal = false;
    for (uint32_t round = 0; round < 2; ++round) {
        VkSwapchainCreateInfoKHR ci{VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR};
        ci.surface = surf;
        ci.minImageCount = std::max(caps.minImageCount, 2u);
        if (caps.maxImageCount) ci.minImageCount = std::min(ci.minImageCount, caps.maxImageCount);
        ci.imageFormat = fmt.format, ci.imageColorSpace = fmt.colorSpace;
        ci.imageExtent = caps.currentExtent.width == 0xFFFFFFFF ? VkExtent2D{640 + 160 * round, 360 + 90 * round} : caps.currentExtent;
        ci.imageArrayLayers = 1, ci.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE;
        ci.imageUsage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT | (round ? 0 : VK_IMAGE_USAGE_TRANSFER_DST_BIT);
        ci.preTransform = caps.currentTransform;
        ci.compositeAlpha = (VkCompositeAlphaFlagBitsKHR)(caps.supportedCompositeAlpha & -caps.supportedCompositeAlpha);
        ci.presentMode = VK_PRESENT_MODE_FIFO_KHR, ci.clipped = VK_TRUE, ci.oldSwapchain = sc;
        VkSwapchainKHR next;
        CHECK(vkCreateSwapchainKHR(dev, &ci, nullptr, &next) == VK_SUCCESS);
        CHECK(vkDeviceWaitIdle(dev) == VK_SUCCESS);
        if (sc) vkDestroySwapchainKHR(dev, sc, nullptr);
        sc = next;
        uint32_t ni = kMax;
        VkImage imgs[kMax];
        CHECK(vkGetSwapchainImagesKHR(dev, sc, &ni, imgs) == VK_SUCCESS);
        bool fresh[kMax];
        std::fill(fresh, fresh + kMax, true);
        auto acquire = [&](uint32_t& idx) {
            if (vkAcquireNextImageKHR(dev, sc, UINT64_MAX, VK_NULL_HANDLE, fence, &idx) < 0) return false;
            return vkWaitForFences(dev, 1, &fence, VK_TRUE, UINT64_MAX) == VK_SUCCESS && vkResetFences(dev, 1, &fence) == VK_SUCCESS && idx < ni;
        };
        auto barrier = [&](VkCommandBuffer cb, VkImage img, VkImageLayout from, VkImageLayout to, VkAccessFlags dst) {
            VkImageMemoryBarrier b{VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER, nullptr, 0, dst, from, to, VK_QUEUE_FAMILY_IGNORED, VK_QUEUE_FAMILY_IGNORED, img, all};
            vkCmdPipelineBarrier(cb, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, 0, 0, nullptr, 0, nullptr, 1, &b);
        };
        for (int i = 0; i < 20; ++i) {
            uint32_t idx;
            CHECK(acquire(idx));
            if (busy[idx]) CHECK(vkWaitForFences(dev, 1, &done[idx], VK_TRUE, UINT64_MAX) == VK_SUCCESS && vkResetFences(dev, 1, &done[idx]) == VK_SUCCESS);
            VkCommandBuffer cb = cbs[idx];
            VkCommandBufferBeginInfo bi{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
            CHECK(vkBeginCommandBuffer(cb, &bi) == VK_SUCCESS);
            if (!round) {
                barrier(cb, imgs[idx], VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_ACCESS_TRANSFER_WRITE_BIT);
                const VkClearColorValue magenta{{1, 0, 1, 1}};
                vkCmdClearColorImage(cb, imgs[idx], VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, &magenta, 1, &all);
                barrier(cb, imgs[idx], VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_IMAGE_LAYOUT_PRESENT_SRC_KHR, 0);
            } else if (fresh[idx]) barrier(cb, imgs[idx], VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_PRESENT_SRC_KHR, 0);
            fresh[idx] = false;
            CHECK(vkEndCommandBuffer(cb) == VK_SUCCESS);
            VkSubmitInfo si{VK_STRUCTURE_TYPE_SUBMIT_INFO, nullptr, 0, nullptr, nullptr, 1, &cb, 1, &ready[idx]};
            CHECK(vkQueueSubmit(q, 1, &si, done[idx]) == VK_SUCCESS);
            busy[idx] = true;
            VkResult each = VK_RESULT_MAX_ENUM;
            VkPresentInfoKHR pi{VK_STRUCTURE_TYPE_PRESENT_INFO_KHR, nullptr, 1, &ready[idx], 1, &sc, &idx, i % 2 ? &each : nullptr};
            const VkResult r = vkQueuePresentKHR(q, &pi);
            CHECK(r == VK_SUCCESS || r == VK_SUBOPTIMAL_KHR);
            CHECK(!(i % 2) || each == r);
            suboptimal |= r == VK_SUBOPTIMAL_KHR;
            usleep(2000);
        }
        if (!pixels) continue;
        CHECK(vkDeviceWaitIdle(dev) == VK_SUCCESS);
        uint32_t idx;
        CHECK(acquire(idx));
        const VkDeviceSize size = (VkDeviceSize)ci.imageExtent.width * ci.imageExtent.height * 4;
        VkBufferCreateInfo bci{VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO, nullptr, 0, size, VK_BUFFER_USAGE_TRANSFER_DST_BIT, VK_SHARING_MODE_EXCLUSIVE};
        VkBuffer buf;
        CHECK(vkCreateBuffer(dev, &bci, nullptr, &buf) == VK_SUCCESS);
        VkMemoryRequirements req;
        vkGetBufferMemoryRequirements(dev, buf, &req);
        VkPhysicalDeviceMemoryProperties mp;
        vkGetPhysicalDeviceMemoryProperties(gpu, &mp);
        uint32_t type = 0;
        const VkMemoryPropertyFlags want = VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT;
        while (type < mp.memoryTypeCount && !((req.memoryTypeBits >> type & 1) && (mp.memoryTypes[type].propertyFlags & want) == want)) ++type;
        VkMemoryAllocateInfo ai{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO, nullptr, req.size, type};
        VkDeviceMemory mem;
        CHECK(type < mp.memoryTypeCount && vkAllocateMemory(dev, &ai, nullptr, &mem) == VK_SUCCESS && vkBindBufferMemory(dev, buf, mem, 0) == VK_SUCCESS);
        VkCommandBuffer cb = cbs[idx];
        VkCommandBufferBeginInfo bi{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
        CHECK(vkResetFences(dev, 1, &done[idx]) == VK_SUCCESS && vkBeginCommandBuffer(cb, &bi) == VK_SUCCESS);
        barrier(cb, imgs[idx], VK_IMAGE_LAYOUT_PRESENT_SRC_KHR, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, VK_ACCESS_TRANSFER_READ_BIT);
        VkBufferImageCopy region{0, 0, 0, {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1}, {0, 0, 0}, {ci.imageExtent.width, ci.imageExtent.height, 1}};
        vkCmdCopyImageToBuffer(cb, imgs[idx], VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, buf, 1, &region);
        CHECK(vkEndCommandBuffer(cb) == VK_SUCCESS);
        VkSubmitInfo si{VK_STRUCTURE_TYPE_SUBMIT_INFO, nullptr, 0, nullptr, nullptr, 1, &cb};
        CHECK(vkQueueSubmit(q, 1, &si, done[idx]) == VK_SUCCESS && vkWaitForFences(dev, 1, &done[idx], VK_TRUE, UINT64_MAX) == VK_SUCCESS);
        void* p;
        CHECK(vkMapMemory(dev, mem, 0, size, 0, &p) == VK_SUCCESS);
        const uint32_t head[3] = {ci.imageExtent.width, ci.imageExtent.height, (uint32_t)fmt.format};
        FILE* f = fopen((std::string(pixels) + "." + std::to_string(round)).c_str(), "wb");
        CHECK(f && fwrite(head, 4, 3, f) == 3 && fwrite(p, 1, size, f) == size && !fclose(f));
        vkFreeMemory(dev, mem, nullptr), vkDestroyBuffer(dev, buf, nullptr), busy[idx] = true;
    }
    vkDeviceWaitIdle(dev);
    for (uint32_t i = 0; i < kMax; ++i) vkDestroyFence(dev, done[i], nullptr), vkDestroySemaphore(dev, ready[i], nullptr);
    vkDestroyCommandPool(dev, pool, nullptr);
    vkDestroySwapchainKHR(dev, sc, nullptr), vkDestroyFence(dev, fence, nullptr), vkDestroySurfaceKHR(inst, surf, nullptr);
    usleep(1500000);
    return suboptimal ? 4 : 0;
}

static int vkchild() {
    // SCSK_VK_PRELOAD: a library mapped, then deleted, before the instance
    if (const char* pre = getenv("SCSK_VK_PRELOAD")) CHECK(dlopen(pre, RTLD_NOW | RTLD_LOCAL) && !unlink(pre));
    void* lib = dlopen("libvulkan.so.1", RTLD_NOW | RTLD_LOCAL);
    CHECK(lib);
    auto gipa = (PFN_vkGetInstanceProcAddr)dlsym(lib, "vkGetInstanceProcAddr");
    CHECK(gipa);
    auto create_instance = (PFN_vkCreateInstance)gipa(nullptr, "vkCreateInstance");
    const char* app = getenv("SCSK_VK_APP");
    const char* engine = getenv("SCSK_VK_ENGINE");
    VkApplicationInfo ai{VK_STRUCTURE_TYPE_APPLICATION_INFO, nullptr, app, 0, engine, 0, VK_API_VERSION_1_3};
    const bool present = getenv("SCSK_VK_PRESENT");
    const char* const iext[] = {"VK_KHR_surface", "VK_EXT_headless_surface"};
    const char* const dext[] = {"VK_KHR_swapchain"};
    VkInstanceCreateInfo ici{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO, nullptr, 0, &ai, 0, nullptr, present ? 2u : 0u, iext};
    VkInstance inst;
    CHECK(create_instance(&ici, nullptr, &inst) == VK_SUCCESS);
#define FN(name) auto name = (PFN_##name)gipa(inst, #name)
    FN(vkEnumeratePhysicalDevices);
    FN(vkGetPhysicalDeviceQueueFamilyProperties);
    FN(vkCreateDevice);
    FN(vkGetDeviceProcAddr);
    uint32_t n = 1;
    VkPhysicalDevice gpu;
    CHECK(vkEnumeratePhysicalDevices(inst, &n, &gpu) >= 0 && n == 1);
    uint32_t qn = 0;
    vkGetPhysicalDeviceQueueFamilyProperties(gpu, &qn, nullptr);
    std::vector<VkQueueFamilyProperties> qf(qn);
    vkGetPhysicalDeviceQueueFamilyProperties(gpu, &qn, qf.data());
    uint32_t family = 0;
    while (family < qn && !(qf[family].queueFlags & VK_QUEUE_COMPUTE_BIT)) ++family;
    CHECK(family < qn);
    const float priority = 1;
    VkDeviceQueueCreateInfo q{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO, nullptr, 0, family, 1, &priority};
    VkPhysicalDeviceVulkan13Features f13{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES};
    f13.pipelineCreationCacheControl = f13.dynamicRendering = VK_TRUE;
    VkDeviceCreateInfo dci{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO, &f13, 0, 1, &q, 0, nullptr, present ? 1u : 0u, dext};
    VkDevice dev;
    CHECK(vkCreateDevice(gpu, &dci, nullptr, &dev) == VK_SUCCESS);
#define DFN(name) auto name = (PFN_##name)vkGetDeviceProcAddr(dev, #name)
    DFN(vkCreateDescriptorSetLayout);
    DFN(vkCreatePipelineLayout);
    DFN(vkCreateShaderModule);
    DFN(vkCreateComputePipelines);
    DFN(vkDestroyPipeline);
    DFN(vkDestroyShaderModule);
    DFN(vkDestroyPipelineLayout);
    DFN(vkDestroyDescriptorSetLayout);
    DFN(vkDestroyDevice);
    auto vkDestroyInstance = (PFN_vkDestroyInstance)gipa(inst, "vkDestroyInstance");
    VkDescriptorSetLayoutBinding b{0, VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, 1, VK_SHADER_STAGE_COMPUTE_BIT};
    VkDescriptorSetLayoutCreateInfo sci{VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO, nullptr, 0, 1, &b};
    VkDescriptorSetLayout sl;
    CHECK(vkCreateDescriptorSetLayout(dev, &sci, nullptr, &sl) == VK_SUCCESS);
    VkPipelineLayoutCreateInfo pci{VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO, nullptr, 0, 1, &sl};
    VkPipelineLayout pl;
    CHECK(vkCreatePipelineLayout(dev, &pci, nullptr, &pl) == VK_SUCCESS);
    std::vector<uint32_t> w(kSpv_cs, kSpv_cs + sizeof kSpv_cs / 4);
    const size_t at = std::find(w.begin(), w.end(), kSpv_cs_marker) - w.begin();
    CHECK(at < w.size());
    std::random_device rd;
    for (int i = 0; i < 2; ++i) {  // new to the run: a seeded constant in each
        const float v = (float)(rd() % 8000000 + 1000);
        memcpy(&w[at], &v, 4);
        VkShaderModuleCreateInfo mi{VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO, nullptr, 0, w.size() * 4, w.data()};
        VkShaderModule sm;
        CHECK(vkCreateShaderModule(dev, &mi, nullptr, &sm) == VK_SUCCESS);
        VkComputePipelineCreateInfo ci{VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO, nullptr, 0,
                                       {VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO, nullptr, 0, VK_SHADER_STAGE_COMPUTE_BIT, sm, "main"}, pl};
        VkPipeline p;
        CHECK(vkCreateComputePipelines(dev, VK_NULL_HANDLE, 1, &ci, nullptr, &p) == VK_SUCCESS);
        vkDestroyPipeline(dev, p, nullptr), vkDestroyShaderModule(dev, sm, nullptr);
    }
    vkDestroyPipelineLayout(dev, pl, nullptr), vkDestroyDescriptorSetLayout(dev, sl, nullptr);
    const int presented = present ? presents(gipa, inst, gpu, dev, family, vkGetDeviceProcAddr) : 0;
    vkDestroyDevice(dev, nullptr), vkDestroyInstance(inst, nullptr);
    return presented;
}

static bool write_file(const std::string& path, const std::string& text) {
    FILE* f = fopen(path.c_str(), "wb");
    return f && fwrite(text.data(), 1, text.size(), f) == text.size() && fclose(f) == 0;
}

// The exe's scskiller.ini as the app signs it (ScsKiller.Signed): exe_size=, exe_time=, root=., sig=.
// pid= and pid_time= when bound: the process's start time as ProcessTree.Created reads it (/proc/<pid>/stat field 22).
// overlay: the countdown's lines of a 5 minute before run
static bool arm(const std::string& game, const std::string& exe, const std::string& rec, pid_t pid = 0, long long pid_time = 0, bool overlay = false) {
    struct stat st;
    if (stat(exe.c_str(), &st)) return false;
    const std::string key = read_all(rec + "/recorder.key", 256);
    const std::wstring size = std::to_wstring((uint64_t)st.st_size),
                       time = std::to_wstring(((uint64_t)st.st_mtim.tv_sec + 11644473600ull) * 10000000ull + (uint64_t)st.st_mtim.tv_nsec / 100);
    const std::string here = final_dir(game);
    const std::wstring p = pid ? std::to_wstring(pid) : L"", pt = pid ? std::to_wstring(pid_time) : L"";
    const std::string sig = recorder_sig(key, file_key(exe), file_key(no_slash(game)), signed_stamp(size, time, p, pt, L"", L"", overlay ? L"1" : L"", overlay ? L"300" : L"", overlay ? L"before" : L""), L".", file_key(no_slash(here)));
    const std::string bound = pid ? "pid=" + to_utf8(p) + "\r\npid_time=" + to_utf8(pt) + "\r\n" : "";
    return !sig.empty() && write_file(game + "scskiller.ini", "[scskiller]\r\nexe_size=" + to_utf8(size) + "\r\nexe_time=" + to_utf8(time) + "\r\n" + bound + (overlay ? "overlay=1\r\nab_play_s=300\r\nab_tag=before\r\n" : "") + "root=.\r\nsig=" + sig + "\r\n");
}

// The process's start time as Core's ProcessTree.Stat parses it: the 20th field after the name's closing parenthesis.
static long long start_time(pid_t pid) {
    const std::string stat = read_all("/proc/" + std::to_string(pid) + "/stat", 4096);
    const size_t close = stat.rfind(')');
    if (close == std::string::npos) return -1;
    std::vector<std::string> fields;
    for (size_t at = close + 1; at < stat.size();) {
        const size_t end = std::min(stat.find(' ', at), stat.size());
        if (end > at) fields.push_back(stat.substr(at, end - at));
        at = end + 1;
    }
    return fields.size() > 19 ? atoll(fields[19].c_str()) : -1;
}

// exe with args; before(pid) runs while the child waits, before it execs (the pid and start time it keeps).
static int run(const std::string& exe, const std::vector<std::string>& args, const std::function<void(pid_t)>& before = nullptr) {
    fflush(stdout);
    int go[2];
    if (pipe(go)) return -1;
    const pid_t pid = fork();
    if (pid < 0) return -1;
    if (!pid) {
        char c;
        close(go[1]);
        if (read(go[0], &c, 1) != 1) _exit(126);
        std::vector<char*> argv{(char*)exe.c_str()};
        for (auto& a : args) argv.push_back((char*)a.c_str());
        argv.push_back(nullptr);
        execv(exe.c_str(), argv.data());
        _exit(127);
    }
    close(go[0]);
    if (before) before(pid);
    if (write(go[1], "g", 1) != 1) {}
    close(go[1]);
    int status = 0;
    waitpid(pid, &status, 0);
    return WIFEXITED(status) ? WEXITSTATUS(status) : 128;
}

// A read back image (presents' SCSK_VK_PIXELS): 1 when the panel's top left, through "SCSKiller before", is as overlay.h
// paints it, 0 when it is the game's magenta, -1 else or when round0's top left pixel, outside the panel, isn't magenta.
static int panel(const std::string& path, bool round0) {
    const std::string f = read_all(path, 64 << 20);
    uint32_t head[3];
    if (f.size() < 12) return -2;
    memcpy(head, f.data(), 12);
    const uint32_t w = head[0], h = head[1];
    if (f.size() != 12 + (size_t)w * h * 4) return -2;
    const bool bgra = head[2] == VK_FORMAT_B8G8R8A8_UNORM || head[2] == VK_FORMAT_B8G8R8A8_SRGB;
    auto px = [&](long x, long y) {
        const uint8_t* q = (const uint8_t*)f.data() + 12 + ((size_t)y * w + x) * 4;
        return (uint32_t)(bgra ? q[2] : q[0]) | (uint32_t)q[1] << 8 | (uint32_t)(bgra ? q[0] : q[2]) << 16 | 0xFF000000u;
    };
    const uint32_t magenta = 0xFFFF00FFu;
    if (round0 && px(0, 0) != magenta) return -1;
    char widest[48];
    ov::countdown(false, 300, widest);
    ov::Layout L;
    ov::layout(w, h, widest, true, L);
    std::vector<uint32_t> c((size_t)L.w * L.h, ov::kPanel);
    const char* prefix = "SCSKiller before";
    ov::text(c.data(), L.w, L.pad, L.text_y, prefix, L.s, ov::kText, ov::kPanel);
    bool drawn = L.sx == 0 && L.sy == 0, untouched = true;
    for (long y = 0; y < L.text_y + ov::font(L.s).height; ++y)
        for (long x = 0; x < L.pad + ov::text_width(prefix, L.s); ++x) {
            const uint32_t v = px(L.at.left + x, L.at.top + y);
            drawn &= v == c[y * L.w + x], untouched &= v == magenta;
        }
    printf("  %s: %ux%u format %u, panel at %ld,%ld: %s\n", path.substr(path.find_last_of('/') + 1).c_str(), w, h, head[2], L.at.left, L.at.top,
           drawn ? "drawn" : untouched ? "untouched" : "neither");
    return drawn ? 1 : untouched ? 0 : -1;
}

static int vkrec(const std::string& bin) {
    namespace fs = std::filesystem;
    int bad = 0;
    // the keys and the HMAC as Python's hashlib makes them of the same UTF-16LE
    bad += sha1_hex("abc") != "a9993e364706816aba3e25717850c26c9cd0d89d";
    bad += path_key("/Home/\xc3\x89t\xc3\xa9/G\xf0\x9f\x98\x80/A.exe") != "abc08883149eb06d69a87a07c569df9a0c09454b";
    bad += hmac_sha1_hex(std::string(70, 'k'), utf16le(L"mé\U0001F600")) != "2c4df6250947320722b27954e866158fefa8b3c9";
    bad += hmac_sha1_hex("0123456789abcdef0123456789abcdef", utf16le(L"x")) != "8dbcda1b3ef36efe7fcdc3934793e8d9dbba477d";
    printf("keys: %s\n", bad ? "FAILED" : "as hashlib");

    char tmpl[] = "/tmp/scsk-vkrec-XXXXXX";
    CHECK(mkdtemp(tmpl));
    const std::string dir = std::string(tmpl) + "/", steam = dir + "steam", game = steam + "/steamapps/common/scskgame/", layer = dir + "layer/", data = dir + "data/",
                      rec = dir + "rec";
    const std::string implicit = data + "vulkan/implicit_layer.d/";
    std::random_device rd;
    const std::string exe = game + "scskvr" + std::to_string(rd() % 1000000);
    std::error_code ec;
    fs::create_directories(game, ec), fs::create_directories(layer, ec), fs::create_directories(implicit, ec), fs::create_directories(rec, ec);
    CHECK(fs::copy_file(bin + "selftest_linux", exe, ec));
    // Steam's files as admission reads them: app 4242 is the game's folder, app 4343 a folder linked elsewhere that Steam
    // marks VAC (appinfo.vdf v28: magic, universe, then each app's id, size, info state 2, 56 bytes, KeyValues; app 0 ends it)
    fs::create_directories(dir + "elsewhere", ec), fs::create_directories(steam + "/appcache", ec);
    fs::create_directory_symlink(dir + "elsewhere", steam + "/steamapps/common/scsklinked", ec);
    CHECK(write_file(steam + "/steamapps/libraryfolders.vdf", "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + steam + "\"\n\t}\n}\n"));
    CHECK(write_file(steam + "/steamapps/appmanifest_4242.acf", "\"AppState\"\n{\n\t\"appid\"\t\t\"4242\"\n\t\"installdir\"\t\t\"scskgame\"\n}\n"));
    CHECK(write_file(steam + "/steamapps/appmanifest_4343.acf", "\"AppState\"\n{\n\t\"appid\"\t\t\"4343\"\n\t\"installdir\"\t\t\"scsklinked\"\n}\n"));
    auto u32 = [](uint32_t v) { return std::string((const char*)&v, 4); };
    auto app = [&](uint32_t id, bool vac) {
        const std::string z(1, '\0'), end(1, '\x08');
        auto obj = [&](const char* n) { return z + n + z; };
        auto str = [&](const char* k, const char* v) { return std::string(1, '\1') + k + z + v + z; };
        const std::string kv = obj("appinfo") + obj("common") + str("name", "x") + (vac ? obj("category") + str("category_8", "1") + end : "") + end + end + end;
        return u32(id) + u32(60 + (uint32_t)kv.size()) + u32(2) + std::string(56, '\0') + kv;
    };
    CHECK(write_file(steam + "/appcache/appinfo.vdf", u32(0x07564428) + u32(1) + app(4242, false) + app(4343, true) + u32(0)));
    setenv("SCSKILLER_TEST_STEAM_ROOT", steam.c_str(), 1);
    std::string key;
    for (int i = 0; i < 64; ++i) key += "0123456789abcdef"[rd() & 15];
    CHECK(write_file(rec + "/recorder.key", key));
    // nothing of this run's loader reads the user's: home, data and config are the temp folder's
    setenv("HOME", dir.c_str(), 1), setenv("XDG_DATA_HOME", data.c_str(), 1), setenv("XDG_CONFIG_HOME", (dir + "config").c_str(), 1);
    setenv("SCSKILLER_TEST_RECORDER_DIR", rec.c_str(), 1);
    if (!getenv("VK_ICD_FILENAMES") && !access("/usr/share/vulkan/icd.d/lvp_icd.json", R_OK)) setenv("VK_ICD_FILENAMES", "/usr/share/vulkan/icd.d/lvp_icd.json", 1);
    const std::string so = real_path(bin + "libVkLayer_scskiller.so");
    CHECK(!so.empty());
    auto manifest = [&](const std::string& to, const char* name, const char* gate) {
        return write_file(to, std::string("{\"file_format_version\": \"1.0.0\", \"layer\": {\"name\": \"") + name + "\", \"type\": \"GLOBAL\", \"library_path\": \"" + so +
                                  "\",\n \"api_version\": \"1.4.303\", \"implementation_version\": \"1\", \"description\": \"selftest\",\n"
                                  " \"functions\": {\"vkGetInstanceProcAddr\": \"VK_LAYER_fossilize_GetInstanceProcAddr\", \"vkGetDeviceProcAddr\": \"VK_LAYER_fossilize_GetDeviceProcAddr\"}" +
                                  gate + "}}\n");
    };
    const std::string out = rec + "/vulkan/" + file_key(exe) + "/", refusal = rec + "/refused/" + file_key(exe);
    auto files = [&](const std::string& d) {
        std::vector<std::string> r;
        std::error_code e;
        for (fs::directory_iterator it(d, e), end; !e && it != end; it.increment(e)) r.push_back(it->path().filename().string());
        std::sort(r.begin(), r.end());
        return r;
    };
    auto pipelines = [&](const std::string& foz) {  // tags 6, 7, 9
        std::string f = read_all(out + foz, 64 << 20);
        int n = 0;
        for (size_t o = 16; o + 56 <= f.size();) {
            uint32_t stored;
            memcpy(&stored, f.data() + o + 40, 4);
            const int tag = (int)strtoul(f.substr(o, 24).c_str(), nullptr, 16);
            n += tag == 6 || tag == 7 || tag == 9;
            o += 56 + stored;
        }
        return n;
    };
    // frames: scskiller_frames.bin's frames on swap chain index 0, -1 without the file, -2 when it doesn't hold one launch
    // stamped as the csv's #session with every frame on index 0
    struct Seen { int code = -1, foz = 0, pipes = 0, rows = 0, frames = -1; bool api = false, cache = false, refused = false, inst = false, dev = false; std::vector<std::string> game; };
    auto play = [&](const char* tag, const std::function<void(pid_t)>& before = nullptr) {
        Seen s;
        std::error_code e;
        fs::remove_all(out, e), unlink(refusal.c_str());
        s.code = run(exe, {"vkchild"}, before);
        for (auto& n : files(out))
            if (!n.compare(0, 10, "scskiller.") && n.size() > 4 && !n.compare(n.size() - 4, 4, ".foz")) ++s.foz, s.pipes += pipelines(n);
        const std::string csv = read_all(out + "scskiller_creates.csv");
        s.api = csv.find("\n#api,vulkan\n") != std::string::npos;
        s.cache = csv.find("\n#cache,__GL_SHADER_DISK_CACHE_PATH=/tmp/scsk vkrec cache\n") != std::string::npos;
        s.inst = csv.find("\n#vkinstance,4206592,0,0,=a%2C b%25c,=scsk engine,\n") != std::string::npos;
        if (size_t d = csv.find("\n#vkdevice,"); d != std::string::npos) {
            const std::string l = csv.substr(d + 1, csv.find('\n', d + 1) - d - 1);
            s.dev = l.find(":1:0:1,53:000100000000100") != std::string::npos;
        }
        for (size_t at = 0; (at = csv.find('\n', at)) != std::string::npos;)
            if (++at < csv.size() && csv[at] != '#') s.rows += csv.find(",C,", at) < csv.find('\n', at);
        if (const std::string f = read_all(out + "scskiller_frames.bin"); !f.empty()) {
            uint32_t r;
            uint64_t stamp = 0;
            memcpy(&r, f.data(), 4);
            if (f.size() >= 36) memcpy(&stamp, f.data() + 4, 8);
            s.frames = f.size() >= 36 && r == 0xFFFFFFFF && csv.find("#session," + std::to_string(stamp) + ",") != std::string::npos ? 0 : -2;
            for (size_t o = 36; s.frames >= 0 && o + 4 <= f.size(); o += 4) {
                memcpy(&r, f.data() + o, 4);
                if (r == 0xFFFFFFFF || (r >> 28 && r >> 28 != 15)) s.frames = -2;
                else if (!(r >> 28)) ++s.frames;
            }
        }
        s.refused = !access(refusal.c_str(), F_OK);
        s.game = files(game);
        printf("%s: exit %d, %d archive(s), %d pipelines, %d rows, frames %d%s%s%s%s%s; game folder:", tag, s.code, s.foz, s.pipes, s.rows, s.frames, s.api ? ", #api,vulkan" : "",
               s.cache ? ", #cache" : "", s.inst ? ", #vkinstance" : "", s.dev ? ", #vkdevice" : "", s.refused ? ", refused" : "");
        for (auto& n : s.game) printf(" %s", n.c_str());
        if (s.refused) printf(" (%s)", read_all(refusal).c_str());
        printf("\n");
        return s;
    };
    auto only_ini = [](const Seen& s) { return s.game.size() == 2; };  // the exe and its scskiller.ini
    auto nothing = [](const Seen& s) { return s.code == 0 && !s.foz && !s.rows && s.frames == -1; };
    auto clean = [] {
        for (auto n : {"VK_LAYER_PATH", "VK_ADD_LAYER_PATH", "VK_INSTANCE_LAYERS", "VK_LOADER_LAYERS_ALLOW", "FOSSILIZE_DUMP_PATH", "__GL_SHADER_DISK_CACHE_PATH", "SteamAppId",
                       "SCSK_VK_APP", "SCSK_VK_ENGINE"})
            unsetenv(n);
        setenv("VK_LOADER_LAYERS_DISABLE", "~implicit~", 1);  // the system's implicit layers stay out
    };

    clean();
    CHECK(arm(game, exe, rec));
    CHECK(manifest(layer + "VkLayer_scskiller.json", "VK_LAYER_SCSKILLER_selftest", ""));
    setenv("VK_LAYER_PATH", layer.c_str(), 1), setenv("VK_INSTANCE_LAYERS", "VK_LAYER_SCSKILLER_selftest", 1);
    setenv("FOSSILIZE_DUMP_PATH", (game + "fossilize").c_str(), 1), setenv("__GL_SHADER_DISK_CACHE_PATH", "/tmp/scsk vkrec cache", 1);
    setenv("SCSK_VK_APP", "a, b%c", 1), setenv("SCSK_VK_ENGINE", "scsk engine", 1);
    Seen x = play("X");
    bad += x.code != 0 || x.foz < 1 || x.pipes < 2 || x.rows < 2 || !x.api || !x.cache || !x.inst || !x.dev || x.refused || !only_ini(x);

    setenv("SCSK_VK_PRESENT", "1", 1);
    const std::string px = dir + "pixels";
    setenv("SCSK_VK_PIXELS", px.c_str(), 1);
    Seen pr = play("P");
    const int pr0 = panel(px + ".0", true);
    unsetenv("VK_INSTANCE_LAYERS");
    Seen p0 = play("P0");
    const int p00 = panel(px + ".0", true);
    setenv("VK_INSTANCE_LAYERS", "VK_LAYER_SCSKILLER_selftest", 1);
    bad += (pr.code != 0 && pr.code != 4) || pr.code != p0.code || pr.frames != 40 || pr.refused || !only_ini(pr) || p0.rows || p0.frames != -1 || pr0 != 0 || p00 != 0;
    CHECK(arm(game, exe, rec, 0, 0, true));
    Seen po = play("PO");
    const int po0 = panel(px + ".0", true), po1 = panel(px + ".1", false);
    const std::string polog = read_all(out + "scskiller.log"), pocsv = read_all(out + "scskiller_creates.csv");
    const bool added = polog.find("transfer usage added: drawn on") != std::string::npos, drew = pocsv.find("\n#overlay,1\n") != std::string::npos;
    printf("  PO log: %s%s%s\n", polog.find("overlay: on (before, 300 s of play)") != std::string::npos ? "on" : "not on", added ? ", usage added" : "", drew ? ", #overlay,1" : "");
    bad += po.code != p0.code || po.frames != 40 || po.refused || !only_ini(po) || po0 != 1 || po1 != 1 || !added || !drew || polog.find("off for this launch") != std::string::npos;
    CHECK(arm(game, exe, rec));
    unsetenv("SCSK_VK_PRESENT"), unsetenv("SCSK_VK_PIXELS");

    struct stat st;
    CHECK(!stat(exe.c_str(), &st));
    struct timespec later[2] = {st.st_atim, {st.st_mtim.tv_sec + 2, st.st_mtim.tv_nsec}};
    CHECK(!utimensat(AT_FDCWD, exe.c_str(), later, 0));
    Seen t = play("T");
    bad += !nothing(t) || !t.refused || !only_ini(t) || read_all(refusal).find(" exe not as checked") == std::string::npos;

    CHECK(arm(game, exe, rec));
    fs::create_directory(game + "EasyAntiCheat", ec);
    Seen a = play("A");
    bad += !nothing(a) || !a.refused || read_all(refusal).find(" anti-cheat next to the exe") == std::string::npos;
    fs::remove(game + "EasyAntiCheat", ec);

    // an anti-cheat client mapped and deleted since: still loaded (a copy of the loader's library under its name)
    const std::string lib = dir + "lib/easyanticheat_x64.so";
    fs::create_directories(dir + "lib", ec);
    void* vk = dlopen("libvulkan.so.1", RTLD_LAZY | RTLD_LOCAL);
    Dl_info vi{};
    CHECK(vk && dladdr(dlsym(vk, "vkGetInstanceProcAddr"), &vi) && vi.dli_fname);
    CHECK(fs::copy_file(vi.dli_fname, lib, fs::copy_options::overwrite_existing, ec));
    setenv("SCSK_VK_PRELOAD", lib.c_str(), 1);
    Seen d = play("D");
    unsetenv("SCSK_VK_PRELOAD");
    bad += !nothing(d) || !d.refused || read_all(refusal).find(" anti-cheat client loaded") == std::string::npos;

    // bound to the process: its pid and start time, signed as the app signs an offline or risk session's
    Seen b = play("B", [&](pid_t pid) { arm(game, exe, rec, pid, start_time(pid)); });
    bad += b.code != 0 || b.foz < 1 || b.pipes < 2 || b.refused || !only_ini(b);
    Seen b2 = play("B2", [&](pid_t pid) { arm(game, exe, rec, pid, start_time(pid) + 1); });
    bad += !nothing(b2) || !b2.refused || read_all(refusal).find(" bound to another process") == std::string::npos;
    CHECK(arm(game, exe, rec));

    // the exe through a link Steam's folder names: the app is found, and Steam marks it VAC
    const std::string linked = dir + "elsewhere/" + base_name(exe);
    CHECK(fs::copy_file(exe, linked, ec));
    const std::string linked_refusal = rec + "/refused/" + file_key(linked);  // a file's key is its inode's: after the copy
    CHECK(arm(dir + "elsewhere/", linked, rec));
    const int vcode = run(linked, {"vkchild"});
    const std::string vwhy = read_all(linked_refusal);
    printf("V: exit %d, refused (%s)\n", vcode, vwhy.c_str());
    bad += vcode != 0 || vwhy.find(" VAC (Steam marks the game)") == std::string::npos || fs::exists(rec + "/vulkan/" + file_key(linked));

    // a file name that isn't UTF-8: its key can't be the app's, it refuses
    const std::string odd = dir + "odd/scskvr\xff";
    fs::create_directories(dir + "odd", ec);
    CHECK(fs::copy_file(exe, odd, ec) && arm(dir + "odd/", odd, rec));
    const std::string odd_refusal = rec + "/refused/" + file_key(odd);
    const int ncode = run(odd, {"vkchild"});
    printf("N: exit %d, refused (%s)\n", ncode, read_all(odd_refusal).c_str());
    bad += ncode != 0 || read_all(odd_refusal).find(" a folder or file name isn't UTF-8") == std::string::npos;

    CHECK(!unlink((game + "scskiller.ini").c_str()));
    Seen u = play("U");
    bad += !nothing(u) || !u.refused || u.game.size() != 1;
    setenv("SCSK_VK_PRESENT", "1", 1);
    Seen pu = play("PU");
    unsetenv("SCSK_VK_PRESENT");
    bad += pu.code != p0.code || pu.foz || pu.rows || pu.frames != -1 || !pu.refused;

    clean();
    CHECK(arm(game, exe, rec));
    CHECK(manifest(implicit + "VkLayer_scskiller_4242.json", "VK_LAYER_SCSKILLER_4242",
                   ",\n \"enable_environment\": {\"SteamAppId\": \"4242\"}, \"disable_environment\": {\"DISABLE_VK_LAYER_SCSKILLER_4242\": \"1\"}"));
    setenv("VK_LOADER_LAYERS_ALLOW", "VK_LAYER_SCSKILLER_4242", 1);
    Seen i0 = play("I0");
    bad += !nothing(i0) || i0.refused || !only_ini(i0);
    setenv("SteamAppId", "4242", 1);
    Seen i1 = play("I1");
    bad += i1.code != 0 || i1.foz < 1 || i1.pipes < 2 || i1.refused || !only_ini(i1);
    // another app's folder that is the game's through a link: which app runs can't be told
    fs::create_directory_symlink(game, steam + "/steamapps/common/scskalias", ec);
    CHECK(write_file(steam + "/steamapps/appmanifest_4545.acf", "\"AppState\"\n{\n\t\"appid\"\t\t\"4545\"\n\t\"installdir\"\t\t\"scskalias\"\n}\n"));
    Seen w = play("W");
    bad += !nothing(w) || !w.refused || read_all(refusal).find(" VAC: two Steam manifests name the game's folder") == std::string::npos;
    clean();
    unsetenv("VK_LOADER_LAYERS_DISABLE");
    if (!bad) fs::remove_all(dir, ec);
    else printf("left for a look: %s\n", dir.c_str());
    printf("vkrec: %s\n", bad ? "FAILED" : "passed");
    return bad ? 1 : 0;
}

int main(int argc, char** argv) {
    const std::string self = module_path();
    if (argc > 1 && !strcmp(argv[1], "vkchild")) return vkchild();
    if (argc > 1 && !strcmp(argv[1], "vkrec")) return vkrec(self.substr(0, self.find_last_of('/') + 1));
    printf("usage: selftest_linux vkrec\n");
    return 2;
}
