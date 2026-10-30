// What the patched Fossilize layer (third_party/fossilize/scskiller.patch) calls in SCSKiller's Vulkan recorder
// (vklayer.cpp): the admission, the dump path, the per-create timings and the session's instance and devices.
#pragma once
#include <cstdint>

struct VkInstanceCreateInfo;
struct VkDeviceCreateInfo;
struct VkPresentInfoKHR;
struct VkSwapchainCreateInfoKHR;
struct VkInstance_T;
struct VkPhysicalDevice_T;
struct VkDevice_T;
struct VkQueue_T;

// Every environment read of the layer: none steers it (a launcher or the user can set them).
inline const char* scsk_layer_getenv(const char*) { return nullptr; }
// Decides once, at the first instance: whether this process records.
void scsk_layer_admit();
bool scsk_layer_recording();
// The archive's path prefix (the code page's bytes, in the recorder folder's vulkan\<exe file_key>\), set by admission;
// never asked for unless it admitted.
const char* scsk_layer_dump_path();
double scsk_layer_now();
// A vkCreate*Pipelines call of count pipelines from t0 to t1 (scsk_layer_now): kind 'G' graphics, 'C' compute, 'R' ray tracing.
void scsk_layer_created(char kind, uint32_t count, double t0, double t1);
// The game's own create infos, as it passed them, after a successful create: one line each in scskiller_creates.csv, as an
// AMD warm needs them (AMD's redirected cache keys on the enabled device extensions, measured):
//   #vkinstance,<apiVersion>,<applicationVersion>,<engineVersion>,<applicationName>,<engineName>,<instance extensions>
//   #vkdevice,<device extensions>,<queues>,<features>
// Versions in decimal, all three empty without VkApplicationInfo. A name is empty for null, else "=" and its bytes with %,
// comma and control bytes as %XX. Extensions space-separated. Queues space-separated family:count:flags:priority/priority...
// Features space-separated <sType>:<one 0 or 1 per VkBool32 in member order>, pEnabledFeatures as sType 1000059000
// (VkPhysicalDeviceFeatures2), a pNext struct not in vk_features.h as <sType>:?.
void scsk_layer_instance(const VkInstanceCreateInfo* ci);
void scsk_layer_device(const VkDeviceCreateInfo* ci);
// A vkCreateSwapchainKHR that made sc for surface, and a vkQueuePresentKHR that returned res: the frames file
// (scskiller_frames.bin). Handles as their 64 bits.
void scsk_layer_swapchain(uint64_t sc, uint64_t surface);
void scsk_layer_presented(const VkPresentInfoKHR* pi, int32_t res);
// The overlay of a before and after run (vklayer.cpp): a device made, with the next layer's vkGetInstanceProcAddr and
// vkGetDeviceProcAddr and the loader's vkSetDeviceLoaderData (null when the loader gave none), and destroyed.
void scsk_layer_device_made(VkPhysicalDevice_T* gpu, VkDevice_T* dev, VkInstance_T* inst, void* gipa, void* gdpa, void* set_loader_data);
void scsk_layer_device_gone(VkDevice_T* dev);
void scsk_layer_queue(VkDevice_T* dev, uint32_t family, VkQueue_T* q);
// The create info to pass down: ci, or copy with the usage the overlay needs. made: the swapchain created with it (added:
// the usage was the overlay's), gone: before its destroy.
const VkSwapchainCreateInfoKHR* scsk_layer_swapchain_info(VkDevice_T* dev, const VkSwapchainCreateInfoKHR* ci, VkSwapchainCreateInfoKHR* copy);
void scsk_layer_swapchain_made(VkDevice_T* dev, uint64_t sc, const VkSwapchainCreateInfoKHR* ci, bool added);
void scsk_layer_swapchain_gone(VkDevice_T* dev, uint64_t sc);
// The present info to pass down, before the present: pi, or copy waiting on the overlay's semaphore instead of pi's.
const VkPresentInfoKHR* scsk_layer_present(VkQueue_T* q, const VkPresentInfoKHR* pi, VkPresentInfoKHR* copy);
