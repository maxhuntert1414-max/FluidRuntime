#define VK_NO_PROTOTYPES
#include <vulkan/vulkan.h>
#include <vulkan/vk_layer.h>
#include <windows.h>
#include "vulkan_observation.h"
#include <cstring>
#include <iostream>
#include <stdexcept>
#include <string>

namespace {
struct Handle { void* dispatch; } instance_handle{}, device_handle{};
int instance_key{}, device_key{};
unsigned checks{}, destroyed_instances{}, destroyed_devices{}, submits{};
bool late_lookup{};
uintptr_t allocation_id{};
bool reuse_on_free{};
PFN_vkAllocateMemory top_allocate{};
PFN_vkVoidFunction top_destroy_instance{}, top_destroy_device{};
_Post_satisfies_(value) void check(bool value) { ++checks; if (!value) throw std::runtime_error("Observation dispatch regression"); }
VKAPI_ATTR VkResult VKAPI_CALL create_instance(const VkInstanceCreateInfo*, const VkAllocationCallbacks*, VkInstance* out) {
    instance_handle.dispatch = &instance_key;
    *out = reinterpret_cast<VkInstance>(&instance_handle); return VK_SUCCESS;
}
VKAPI_ATTR void VKAPI_CALL destroy_instance(VkInstance, const VkAllocationCallbacks*) { ++destroyed_instances; }
VKAPI_ATTR VkResult VKAPI_CALL create_device(VkPhysicalDevice, const VkDeviceCreateInfo*, const VkAllocationCallbacks*, VkDevice* out) {
    device_handle.dispatch = &device_key;
    *out = reinterpret_cast<VkDevice>(&device_handle); return VK_SUCCESS;
}
VKAPI_ATTR void VKAPI_CALL destroy_device(VkDevice, const VkAllocationCallbacks*) { ++destroyed_devices; }
VKAPI_ATTR void VKAPI_CALL memory_properties(VkPhysicalDevice, VkPhysicalDeviceMemoryProperties* out) { *out = {}; }
VKAPI_ATTR VkResult VKAPI_CALL submit(VkQueue q, uint32_t n, const VkSubmitInfo* info, VkFence fence) {
    check(q == reinterpret_cast<VkQueue>(&device_handle) && n == 0 && !info && !fence);
    ++submits; return VK_ERROR_DEVICE_LOST;
}
VKAPI_ATTR void VKAPI_CALL extension() {}
VKAPI_ATTR VkResult VKAPI_CALL allocate_memory(VkDevice, const VkMemoryAllocateInfo*, const VkAllocationCallbacks*, VkDeviceMemory* out) {
    *out = reinterpret_cast<VkDeviceMemory>(++allocation_id); return VK_SUCCESS;
}
VKAPI_ATTR void VKAPI_CALL free_memory(VkDevice d, VkDeviceMemory m, const VkAllocationCallbacks*) {
    if (reuse_on_free) {
        reuse_on_free = false;
        allocation_id = reinterpret_cast<uintptr_t>(m) - 1;
        VkMemoryAllocateInfo info{};
        info.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
        info.allocationSize = 256;
        VkDeviceMemory replacement{};
        check(top_allocate(d, &info, nullptr, &replacement) == VK_SUCCESS && replacement == m);
    }
}
VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL gipa(VkInstance, const char* name) {
    if (!std::strcmp(name, "vkCreateInstance")) return reinterpret_cast<PFN_vkVoidFunction>(create_instance);
    if (!std::strcmp(name, "vkDestroyInstance")) return late_lookup ? top_destroy_instance : reinterpret_cast<PFN_vkVoidFunction>(destroy_instance);
    if (!std::strcmp(name, "vkCreateDevice")) return reinterpret_cast<PFN_vkVoidFunction>(create_device);
    if (!std::strcmp(name, "vkGetPhysicalDeviceMemoryProperties")) return reinterpret_cast<PFN_vkVoidFunction>(memory_properties);
    if (!std::strcmp(name, "vkUnknownPhysicalExtensionTEST")) return extension;
    return nullptr;
}
VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL gdpa(VkDevice, const char* name) {
    if (!std::strcmp(name, "vkDestroyDevice")) return late_lookup ? top_destroy_device : reinterpret_cast<PFN_vkVoidFunction>(destroy_device);
    if (!std::strcmp(name, "vkQueueSubmit")) return reinterpret_cast<PFN_vkVoidFunction>(submit);
    if (!std::strcmp(name, "vkAllocateMemory")) return reinterpret_cast<PFN_vkVoidFunction>(allocate_memory);
    if (!std::strcmp(name, "vkFreeMemory")) return reinterpret_cast<PFN_vkVoidFunction>(free_memory);
    if (!std::strcmp(name, "vkUnknownDeviceExtensionTEST")) return extension;
    return nullptr;
}
}

int main(int argc, char** argv) {
    try {
        check(argc == 2);
        const auto mapping_name = L"Local\\FluidRuntimeObserve-test-" + std::to_wstring(GetCurrentProcessId());
        const auto mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
            sizeof(fluid::observation::Shared), mapping_name.c_str());
        check(mapping != nullptr && GetLastError() != ERROR_ALREADY_EXISTS);
        auto* shared = static_cast<fluid::observation::Shared*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, 0));
        check(shared != nullptr);
        shared->signature = fluid::observation::magic;
        shared->abi = fluid::observation::version;
        shared->size = sizeof(*shared);
        shared->count = fluid::observation::counter_count;
        shared->enabled = 1;
        wchar_t executable[1024]{};
        const auto executable_length = GetModuleFileNameW(nullptr, executable, 1024);
        check(executable_length > 0 && executable_length < 1024);
        check(SetEnvironmentVariableW(L"FLUIDRUNTIME_OBSERVE_MAPPING", mapping_name.c_str()) != 0);
        check(SetEnvironmentVariableW(L"FLUIDRUNTIME_OBSERVE_EXE", executable) != 0);
        const auto module = LoadLibraryExA(argv[1], nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        check(module != nullptr);
        const auto negotiate = reinterpret_cast<PFN_vkNegotiateLoaderLayerInterfaceVersion>(GetProcAddress(module, "fgNegotiate"));
        check(negotiate != nullptr);
        VkNegotiateLayerInterface api{};
        api.sType = LAYER_NEGOTIATE_INTERFACE_STRUCT;
        api.loaderLayerInterfaceVersion = 1;
        check(negotiate(&api) == VK_ERROR_INITIALIZATION_FAILED);
        check(negotiate(nullptr) == VK_ERROR_INITIALIZATION_FAILED);
        api.loaderLayerInterfaceVersion = 999;
        check(negotiate(&api) == VK_SUCCESS && api.loaderLayerInterfaceVersion == 2);
        check(api.pfnGetInstanceProcAddr(nullptr, "vkUnknownGlobalTEST") == nullptr);
        check(api.pfnGetDeviceProcAddr(nullptr, "vkQueueSubmit") == nullptr);
        for (unsigned cycle = 0; cycle < 100; ++cycle) {
            late_lookup = false;
            VkLayerInstanceLink ilink{nullptr, gipa, gipa};
            VkLayerInstanceCreateInfo ichain{};
            ichain.sType = VK_STRUCTURE_TYPE_LOADER_INSTANCE_CREATE_INFO;
            ichain.function = VK_LAYER_LINK_INFO;
            ichain.u.pLayerInfo = &ilink;
            VkInstanceCreateInfo icreate{};
            icreate.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
            icreate.pNext = &ichain;
            VkInstance instance{};
            const auto ci = reinterpret_cast<PFN_vkCreateInstance>(api.pfnGetInstanceProcAddr(nullptr, "vkCreateInstance"));
            check(ci(&icreate, nullptr, &instance) == VK_SUCCESS);
            check(ichain.u.pLayerInfo == nullptr);
            top_destroy_instance = api.pfnGetInstanceProcAddr(instance, "vkDestroyInstance");
            check(api.pfnGetPhysicalDeviceProcAddr(instance, "vkUnknownPhysicalExtensionTEST") == extension);
            VkLayerDeviceLink dlink{nullptr, gipa, gdpa};
            VkLayerDeviceCreateInfo dchain{};
            dchain.sType = VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO;
            dchain.function = VK_LAYER_LINK_INFO;
            dchain.u.pLayerInfo = &dlink;
            VkDeviceCreateInfo dcreate{};
            dcreate.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
            dcreate.pNext = &dchain;
            VkDevice device{};
            const auto cd = reinterpret_cast<PFN_vkCreateDevice>(api.pfnGetInstanceProcAddr(instance, "vkCreateDevice"));
            check(cd(reinterpret_cast<VkPhysicalDevice>(instance), &dcreate, nullptr, &device) == VK_SUCCESS);
            top_destroy_device = api.pfnGetDeviceProcAddr(device, "vkDestroyDevice");
            check(api.pfnGetDeviceProcAddr(device, "vkUnknownDeviceExtensionTEST") == extension);
            check(api.pfnGetDeviceProcAddr(device, "vkQueueSubmit2KHR") == nullptr);
            if (cycle == 0) {
                top_allocate = reinterpret_cast<PFN_vkAllocateMemory>(api.pfnGetDeviceProcAddr(device, "vkAllocateMemory"));
                const auto release = reinterpret_cast<PFN_vkFreeMemory>(api.pfnGetDeviceProcAddr(device, "vkFreeMemory"));
                VkMemoryAllocateInfo info{};
                info.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
                info.allocationSize = 64;
                VkDeviceMemory first{}, second{};
                check(top_allocate(device, &info, nullptr, &first) == VK_SUCCESS);
                info.allocationSize = 128;
                check(top_allocate(device, &info, nullptr, &second) == VK_SUCCESS);
                release(device, first, nullptr);
                reuse_on_free = true;
                release(device, second, nullptr);
                check(shared->counters[fluid::observation::live_bytes] == 256);
                release(device, second, nullptr);
                check(shared->counters[fluid::observation::live_bytes] == 0);
            }
            const auto queue_submit = reinterpret_cast<PFN_vkQueueSubmit>(api.pfnGetDeviceProcAddr(device, "vkQueueSubmit"));
            check(queue_submit(reinterpret_cast<VkQueue>(device), 0, nullptr, VK_NULL_HANDLE) == VK_ERROR_DEVICE_LOST);
            // Simulate a loader whose late lookup routes through the top table.
            // Destruction must still invoke the captured downstream functions.
            late_lookup = true;
            reinterpret_cast<PFN_vkDestroyDevice>(top_destroy_device)(device, nullptr);
            reinterpret_cast<PFN_vkDestroyInstance>(top_destroy_instance)(instance, nullptr);
            check(destroyed_instances == cycle + 1 && destroyed_devices == cycle + 1);
        }
        check(submits == 100);
        check(shared->counters[fluid::observation::api_errors] == 100);
        check(shared->counters[fluid::observation::active_devices] == 0);
        check(shared->counters[fluid::observation::allocations] == 3);
        FreeLibrary(module);
        UnmapViewOfFile(shared);
        CloseHandle(mapping);
        std::cout << "Observation dispatch: " << checks << " checks passed\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
