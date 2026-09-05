#define VK_NO_PROTOTYPES
#include <vulkan/vulkan.h>
#include <vulkan/vk_layer.h>
#include "vulkan_observation.h"
#include <array>
#include <cstring>
#include <cwchar>
#include <memory>
#include <new>

namespace {
using namespace fluid::observation;
SRWLOCK state_lock = SRWLOCK_INIT;
struct Lock {
    Lock() { AcquireSRWLockExclusive(&state_lock); }
    ~Lock() { ReleaseSRWLockExclusive(&state_lock); }
};
template<class T> void* key(T handle) { return handle ? *reinterpret_cast<void**>(handle) : nullptr; }
struct Instance {
    bool used{};
    void* dispatch{};
    VkInstance handle{};
    PFN_vkGetInstanceProcAddr next{};
    PFN_GetPhysicalDeviceProcAddr physical_next{};
    PFN_vkDestroyInstance destroy{};
    PFN_vkCreateDevice create_device{};
    PFN_vkGetPhysicalDeviceMemoryProperties memory_properties{};
};
constexpr auto device_names = std::to_array<const char*>({
    "vkDestroyDevice", "vkAllocateMemory", "vkFreeMemory", "vkMapMemory", "vkUnmapMemory",
    "vkFlushMappedMemoryRanges", "vkInvalidateMappedMemoryRanges", "vkBindBufferMemory",
    "vkBindBufferMemory2", "vkBindBufferMemory2KHR", "vkBindImageMemory", "vkBindImageMemory2",
    "vkBindImageMemory2KHR", "vkCmdCopyBuffer", "vkCmdCopyBuffer2", "vkCmdCopyBuffer2KHR",
    "vkCmdCopyBufferToImage", "vkCmdCopyImageToBuffer", "vkCmdFillBuffer", "vkCmdPipelineBarrier",
    "vkCmdPipelineBarrier2", "vkCmdPipelineBarrier2KHR", "vkQueueSubmit", "vkQueueSubmit2",
    "vkQueueSubmit2KHR", "vkQueuePresentKHR", "vkQueueWaitIdle", "vkWaitForFences"});
struct Device {
    bool used{};
    void* dispatch{};
    VkDevice handle{};
    PFN_vkGetDeviceProcAddr next{};
    VkPhysicalDeviceMemoryProperties memory{};
    std::array<PFN_vkVoidFunction, device_names.size()> functions{};
};
std::array<Instance, 16> instances_table{};
std::array<Device, 64> devices_table{};
struct Allocation { void* device{}; VkDeviceMemory handle{}; VkDeviceSize bytes{}; };
std::array<Allocation, 8192> allocation_table{};
INIT_ONCE sink_once = INIT_ONCE_STATIC_INIT;
HANDLE sink_mapping{};
Shared* sink{};

BOOL CALLBACK initialize_sink(PINIT_ONCE, PVOID, PVOID*) {
    wchar_t name[128]{};
    const auto paths = std::unique_ptr<wchar_t[]>(new (std::nothrow) wchar_t[65536]{});
    if (!paths) return TRUE;
    auto* expected = paths.get();
    auto* actual = expected + 32768;
    const auto n = GetEnvironmentVariableW(L"FLUIDRUNTIME_OBSERVE_MAPPING", name, 128);
    const auto e = GetEnvironmentVariableW(L"FLUIDRUNTIME_OBSERVE_EXE", expected, 32768);
    const auto a = GetModuleFileNameW(nullptr, actual, 32768);
    if (!n || n >= 128 || !e || e >= 32768 || !a || a >= 32768 ||
        _wcsicmp(expected, actual) != 0 || wcsncmp(name, L"Local\\FluidRuntimeObserve-", 26) != 0) return TRUE;
    sink_mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name);
    if (!sink_mapping) return TRUE;
    auto* view = static_cast<Shared*>(MapViewOfFile(sink_mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(Shared)));
    if (view && view->signature == magic && view->abi == version && view->size == sizeof(Shared) &&
        view->count == counter_count) {
        const auto pid = static_cast<LONG64>(GetCurrentProcessId());
        const auto owner = InterlockedCompareExchange64(&view->process_id, pid, 0);
        if (owner == 0 || owner == pid) { sink = view; return TRUE; }
    }
    if (view) UnmapViewOfFile(view);
    CloseHandle(sink_mapping);
    sink_mapping = nullptr;
    return TRUE;
}
void add(Counter counter, LONG64 value = 1) {
    if (sink && InterlockedCompareExchange64(&sink->enabled, 0, 0) == 1)
        InterlockedAdd64(&sink->counters[counter], value);
}
void seen(Counter counter) { add(counter); add(intercepted_calls); }
VkResult result(VkResult value) { if (value < 0) add(api_errors); return value; }
Instance instance_for(void* dispatch) {
    Lock lock;
    for (const auto& entry : instances_table) if (entry.used && entry.dispatch == dispatch) return entry;
    return {};
}
Device device_for(void* dispatch) {
    Lock lock;
    for (const auto& entry : devices_table) if (entry.used && entry.dispatch == dispatch) return entry;
    return {};
}
template<class F> F device_function(const Device& d, const char* name) {
    for (size_t i = 0; i < device_names.size(); ++i)
        if (std::strcmp(name, device_names[i]) == 0) return reinterpret_cast<F>(d.functions[i]);
    return nullptr;
}
void track(const Device& d, VkDeviceMemory memory, const VkMemoryAllocateInfo* info) {
    seen(allocations);
    add(allocation_bytes, static_cast<LONG64>(info->allocationSize));
    if (info->memoryTypeIndex < d.memory.memoryTypeCount) {
        const auto flags = d.memory.memoryTypes[info->memoryTypeIndex].propertyFlags;
        if (flags & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT) add(host_visible_bytes, static_cast<LONG64>(info->allocationSize));
        if (flags & VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT) add(device_local_bytes, static_cast<LONG64>(info->allocationSize));
    }
    Lock lock;
    for (auto& entry : allocation_table) if (!entry.handle) {
        entry = {d.dispatch, memory, info->allocationSize};
        add(live_bytes, static_cast<LONG64>(info->allocationSize));
        if (sink) {
            const auto live = InterlockedCompareExchange64(&sink->counters[live_bytes], 0, 0);
            if (live > InterlockedCompareExchange64(&sink->counters[peak_bytes], 0, 0))
                InterlockedExchange64(&sink->counters[peak_bytes], live);
        }
        return;
    }
    add(untracked_allocations);
}
void untrack(void* dispatch, VkDeviceMemory memory) {
    Lock lock;
    for (auto& entry : allocation_table) if (entry.device == dispatch && entry.handle == memory) {
        add(live_bytes, -static_cast<LONG64>(entry.bytes));
        entry = {};
        return;
    }
}
}

VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL fgGetInstanceProcAddr(VkInstance, const char*);
VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL fgGetDeviceProcAddr(VkDevice, const char*);
VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL fgGetPhysicalDeviceProcAddr(VkInstance, const char*);

VKAPI_ATTR VkResult VKAPI_CALL fgCreateInstance(const VkInstanceCreateInfo* info,
    const VkAllocationCallbacks* allocator, VkInstance* output) {
    InitOnceExecuteOnce(&sink_once, initialize_sink, nullptr, nullptr);
    auto* link = const_cast<VkLayerInstanceCreateInfo*>(static_cast<const VkLayerInstanceCreateInfo*>(info->pNext));
    while (link && (link->sType != VK_STRUCTURE_TYPE_LOADER_INSTANCE_CREATE_INFO || link->function != VK_LAYER_LINK_INFO))
        link = const_cast<VkLayerInstanceCreateInfo*>(static_cast<const VkLayerInstanceCreateInfo*>(link->pNext));
    if (!link || !link->u.pLayerInfo) return VK_ERROR_INITIALIZATION_FAILED;
    const auto next = link->u.pLayerInfo->pfnNextGetInstanceProcAddr;
    const auto physical = link->u.pLayerInfo->pfnNextGetPhysicalDeviceProcAddr;
    const auto create = reinterpret_cast<PFN_vkCreateInstance>(next(nullptr, "vkCreateInstance"));
    link->u.pLayerInfo = link->u.pLayerInfo->pNext;
    Instance* slot{};
    { Lock lock; for (auto& entry : instances_table) if (!entry.used) { entry.used = true; slot = &entry; break; } }
    if (!slot) return VK_ERROR_OUT_OF_HOST_MEMORY;
    const auto value = create(info, allocator, output);
    const auto destroy = value == VK_SUCCESS ? reinterpret_cast<PFN_vkDestroyInstance>(next(*output, "vkDestroyInstance")) : nullptr;
    const auto create_device = value == VK_SUCCESS ? reinterpret_cast<PFN_vkCreateDevice>(next(*output, "vkCreateDevice")) : nullptr;
    const auto memory_properties = value == VK_SUCCESS ? reinterpret_cast<PFN_vkGetPhysicalDeviceMemoryProperties>(
        next(*output, "vkGetPhysicalDeviceMemoryProperties")) : nullptr;
    { Lock lock; *slot = value == VK_SUCCESS ? Instance{true, key(*output), *output, next, physical,
        destroy, create_device, memory_properties} : Instance{}; }
    if (value == VK_SUCCESS) { seen(instances); add(active_instances); }
    return result(value);
}
VKAPI_ATTR void VKAPI_CALL fgDestroyInstance(VkInstance handle, const VkAllocationCallbacks* allocator) {
    if (!handle) return;
    const auto d = instance_for(key(handle));
    d.destroy(handle, allocator);
    { Lock lock; for (auto& entry : instances_table) if (entry.handle == handle) entry = {}; }
    add(active_instances, -1);
}
VKAPI_ATTR VkResult VKAPI_CALL fgCreateDevice(VkPhysicalDevice physical, const VkDeviceCreateInfo* info,
    const VkAllocationCallbacks* allocator, VkDevice* output) {
    const auto instance = instance_for(key(physical));
    auto* link = const_cast<VkLayerDeviceCreateInfo*>(static_cast<const VkLayerDeviceCreateInfo*>(info->pNext));
    while (link && (link->sType != VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO || link->function != VK_LAYER_LINK_INFO))
        link = const_cast<VkLayerDeviceCreateInfo*>(static_cast<const VkLayerDeviceCreateInfo*>(link->pNext));
    if (!link || !link->u.pLayerInfo || !instance.next) return VK_ERROR_INITIALIZATION_FAILED;
    const auto next = link->u.pLayerInfo->pfnNextGetDeviceProcAddr;
    link->u.pLayerInfo = link->u.pLayerInfo->pNext;
    Device* slot{};
    { Lock lock; for (auto& entry : devices_table) if (!entry.used) { entry.used = true; slot = &entry; break; } }
    if (!slot) return VK_ERROR_OUT_OF_HOST_MEMORY;
    const auto value = instance.create_device(physical, info, allocator, output);
    Device data{};
    if (value == VK_SUCCESS) {
        data = {true, key(*output), *output, next, {}};
        // Capture the downstream dispatch before the loader installs the top
        // table. Late lookup of DestroyInstance/Device can recurse into us.
        for (size_t j = 0; j < device_names.size(); ++j) data.functions[j] = next(*output, device_names[j]);
        instance.memory_properties(physical, &data.memory);
        seen(devices); add(active_devices);
    }
    { Lock lock; *slot = data; }
    return result(value);
}
VKAPI_ATTR void VKAPI_CALL fgDestroyDevice(VkDevice handle, const VkAllocationCallbacks* allocator) {
    if (!handle) return;
    const auto d = device_for(key(handle));
    const auto destroy = device_function<PFN_vkDestroyDevice>(d, "vkDestroyDevice");
    destroy(handle, allocator);
    {
        Lock lock;
        for (auto& entry : allocation_table) if (entry.device == d.dispatch) {
            add(live_bytes, -static_cast<LONG64>(entry.bytes)); entry = {};
        }
        for (auto& entry : devices_table) if (entry.handle == handle) entry = {};
    }
    add(active_devices, -1);
}

// Only these entry points are observed. Arguments, return values and ordering
// are forwarded; no barriers, copies, allocations or waits are removed.
#define DEVICE(name, handle) const auto d = device_for(key(handle)); const auto next = device_function<PFN_vk##name>(d, "vk" #name)
VKAPI_ATTR VkResult VKAPI_CALL fgAllocateMemory(VkDevice h, const VkMemoryAllocateInfo* i, const VkAllocationCallbacks* a, VkDeviceMemory* o) {
    DEVICE(AllocateMemory, h); const auto v = next(h, i, a, o); if (v == VK_SUCCESS) track(d, *o, i); return result(v);
}
VKAPI_ATTR void VKAPI_CALL fgFreeMemory(VkDevice h, VkDeviceMemory m, const VkAllocationCallbacks* a) {
    DEVICE(FreeMemory, h);
    // Retire before forwarding: another allocation can reuse the driver handle
    // as soon as FreeMemory returns, even before this wrapper regains control.
    if (m) { seen(frees); untrack(d.dispatch, m); }
    next(h, m, a);
}
VKAPI_ATTR VkResult VKAPI_CALL fgMapMemory(VkDevice h, VkDeviceMemory m, VkDeviceSize o, VkDeviceSize s, VkMemoryMapFlags f, void** p) {
    DEVICE(MapMemory, h); seen(maps); return result(next(h, m, o, s, f, p));
}
VKAPI_ATTR void VKAPI_CALL fgUnmapMemory(VkDevice h, VkDeviceMemory m) { DEVICE(UnmapMemory, h); seen(unmaps); next(h, m); }
VKAPI_ATTR VkResult VKAPI_CALL fgFlushMappedMemoryRanges(VkDevice h, uint32_t n, const VkMappedMemoryRange* r) {
    DEVICE(FlushMappedMemoryRanges, h); seen(flushes); return result(next(h, n, r));
}
VKAPI_ATTR VkResult VKAPI_CALL fgInvalidateMappedMemoryRanges(VkDevice h, uint32_t n, const VkMappedMemoryRange* r) {
    DEVICE(InvalidateMappedMemoryRanges, h); seen(invalidates); return result(next(h, n, r));
}
VKAPI_ATTR VkResult VKAPI_CALL fgBindBufferMemory(VkDevice h, VkBuffer b, VkDeviceMemory m, VkDeviceSize o) {
    DEVICE(BindBufferMemory, h); seen(buffer_binds); return result(next(h, b, m, o));
}
VKAPI_ATTR VkResult VKAPI_CALL fgBindImageMemory(VkDevice h, VkImage b, VkDeviceMemory m, VkDeviceSize o) {
    DEVICE(BindImageMemory, h); seen(image_binds); return result(next(h, b, m, o));
}
#define BIND2(Name, Type, counter) \
VKAPI_ATTR VkResult VKAPI_CALL fg##Name(VkDevice h, uint32_t n, const Type* i) { \
    DEVICE(Name, h); seen(counter); return result(next(h, n, i)); }
BIND2(BindBufferMemory2, VkBindBufferMemoryInfo, buffer_binds)
BIND2(BindBufferMemory2KHR, VkBindBufferMemoryInfo, buffer_binds)
BIND2(BindImageMemory2, VkBindImageMemoryInfo, image_binds)
BIND2(BindImageMemory2KHR, VkBindImageMemoryInfo, image_binds)
VKAPI_ATTR void VKAPI_CALL fgCmdCopyBuffer(VkCommandBuffer h, VkBuffer s, VkBuffer t, uint32_t n, const VkBufferCopy* r) {
    DEVICE(CmdCopyBuffer, h); seen(buffer_copies);
    for (uint32_t j = 0; j < n; ++j) add(buffer_copy_bytes, static_cast<LONG64>(r[j].size));
    next(h, s, t, n, r);
}
#define COPY2(Name) \
VKAPI_ATTR void VKAPI_CALL fg##Name(VkCommandBuffer h, const VkCopyBufferInfo2* i) { \
    DEVICE(Name, h); seen(buffer_copies); add(copy2_calls); \
    for (uint32_t j = 0; j < i->regionCount; ++j) add(buffer_copy_bytes, static_cast<LONG64>(i->pRegions[j].size)); \
    next(h, i); }
COPY2(CmdCopyBuffer2)
COPY2(CmdCopyBuffer2KHR)
VKAPI_ATTR void VKAPI_CALL fgCmdCopyBufferToImage(VkCommandBuffer h, VkBuffer b, VkImage i, VkImageLayout l, uint32_t n, const VkBufferImageCopy* r) {
    DEVICE(CmdCopyBufferToImage, h); seen(buffer_image_copies); next(h, b, i, l, n, r);
}
VKAPI_ATTR void VKAPI_CALL fgCmdCopyImageToBuffer(VkCommandBuffer h, VkImage i, VkImageLayout l, VkBuffer b, uint32_t n, const VkBufferImageCopy* r) {
    DEVICE(CmdCopyImageToBuffer, h); seen(buffer_image_copies); next(h, i, l, b, n, r);
}
VKAPI_ATTR void VKAPI_CALL fgCmdFillBuffer(VkCommandBuffer h, VkBuffer b, VkDeviceSize o, VkDeviceSize s, uint32_t v) {
    DEVICE(CmdFillBuffer, h); seen(fills); next(h, b, o, s, v);
}
VKAPI_ATTR void VKAPI_CALL fgCmdPipelineBarrier(VkCommandBuffer h, VkPipelineStageFlags s, VkPipelineStageFlags t, VkDependencyFlags f,
    uint32_t a, const VkMemoryBarrier* m, uint32_t b, const VkBufferMemoryBarrier* bm, uint32_t c, const VkImageMemoryBarrier* im) {
    DEVICE(CmdPipelineBarrier, h); seen(barriers); next(h, s, t, f, a, m, b, bm, c, im);
}
#define BARRIER2(Name) VKAPI_ATTR void VKAPI_CALL fg##Name(VkCommandBuffer h, const VkDependencyInfo* i) { \
    DEVICE(Name, h); seen(barriers); next(h, i); }
BARRIER2(CmdPipelineBarrier2)
BARRIER2(CmdPipelineBarrier2KHR)
VKAPI_ATTR VkResult VKAPI_CALL fgQueueSubmit(VkQueue h, uint32_t n, const VkSubmitInfo* i, VkFence f) {
    DEVICE(QueueSubmit, h); seen(submits); return result(next(h, n, i, f));
}
#define SUBMIT2(Name) VKAPI_ATTR VkResult VKAPI_CALL fg##Name(VkQueue h, uint32_t n, const VkSubmitInfo2* i, VkFence f) { \
    DEVICE(Name, h); seen(submits); add(submit2_calls); return result(next(h, n, i, f)); }
SUBMIT2(QueueSubmit2)
SUBMIT2(QueueSubmit2KHR)
VKAPI_ATTR VkResult VKAPI_CALL fgQueuePresentKHR(VkQueue h, const VkPresentInfoKHR* i) {
    DEVICE(QueuePresentKHR, h); seen(presents); return result(next(h, i));
}
VKAPI_ATTR VkResult VKAPI_CALL fgQueueWaitIdle(VkQueue h) { DEVICE(QueueWaitIdle, h); seen(queue_waits); return result(next(h)); }
VKAPI_ATTR VkResult VKAPI_CALL fgWaitForFences(VkDevice h, uint32_t n, const VkFence* f, VkBool32 all, uint64_t t) {
    DEVICE(WaitForFences, h); seen(fence_waits); return result(next(h, n, f, all, t));
}

namespace {
PFN_vkVoidFunction intercepted(const char* name) {
#define ENTRY(Name) if (std::strcmp(name, "vk" #Name) == 0) return reinterpret_cast<PFN_vkVoidFunction>(fg##Name)
    ENTRY(DestroyDevice); ENTRY(AllocateMemory); ENTRY(FreeMemory); ENTRY(MapMemory); ENTRY(UnmapMemory);
    ENTRY(FlushMappedMemoryRanges); ENTRY(InvalidateMappedMemoryRanges);
    ENTRY(BindBufferMemory); ENTRY(BindBufferMemory2); ENTRY(BindBufferMemory2KHR);
    ENTRY(BindImageMemory); ENTRY(BindImageMemory2); ENTRY(BindImageMemory2KHR);
    ENTRY(CmdCopyBuffer); ENTRY(CmdCopyBuffer2); ENTRY(CmdCopyBuffer2KHR);
    ENTRY(CmdCopyBufferToImage); ENTRY(CmdCopyImageToBuffer); ENTRY(CmdFillBuffer);
    ENTRY(CmdPipelineBarrier); ENTRY(CmdPipelineBarrier2); ENTRY(CmdPipelineBarrier2KHR);
    ENTRY(QueueSubmit); ENTRY(QueueSubmit2); ENTRY(QueueSubmit2KHR); ENTRY(QueuePresentKHR);
    ENTRY(QueueWaitIdle); ENTRY(WaitForFences);
    return nullptr;
}
}
VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL fgGetDeviceProcAddr(VkDevice device, const char* name) {
    if (!device || !name) return nullptr;
    const auto d = device_for(key(device));
    const auto next = d.next ? d.next(device, name) : nullptr;
    if (!next) return nullptr;
    if (std::strcmp(name, "vkGetDeviceProcAddr") == 0) return reinterpret_cast<PFN_vkVoidFunction>(fgGetDeviceProcAddr);
    const auto hook = intercepted(name);
    return hook ? hook : next;
}
VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL fgGetInstanceProcAddr(VkInstance instance, const char* name) {
    if (!name) return nullptr;
    if (std::strcmp(name, "vkGetInstanceProcAddr") == 0) return reinterpret_cast<PFN_vkVoidFunction>(fgGetInstanceProcAddr);
    if (std::strcmp(name, "vkCreateInstance") == 0) return reinterpret_cast<PFN_vkVoidFunction>(fgCreateInstance);
    if (!instance) return nullptr;
    const auto d = instance_for(key(instance));
    const auto next = d.next ? d.next(instance, name) : nullptr;
    if (!next) return nullptr;
    ENTRY(GetDeviceProcAddr); ENTRY(CreateDevice); ENTRY(DestroyInstance);
    const auto hook = intercepted(name);
    return hook ? hook : next;
}
VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL fgGetPhysicalDeviceProcAddr(VkInstance instance, const char* name) {
    if (!instance || !name) return nullptr;
    const auto d = instance_for(key(instance));
    return d.physical_next ? d.physical_next(instance, name) : (d.next ? d.next(instance, name) : nullptr);
}
extern "C" __declspec(dllexport) VKAPI_ATTR VkResult VKAPI_CALL fgNegotiate(VkNegotiateLayerInterface* i) {
    if (!i || i->sType != LAYER_NEGOTIATE_INTERFACE_STRUCT || i->loaderLayerInterfaceVersion < 2)
        return VK_ERROR_INITIALIZATION_FAILED;
    i->loaderLayerInterfaceVersion = 2;
    i->pfnGetInstanceProcAddr = fgGetInstanceProcAddr;
    i->pfnGetDeviceProcAddr = fgGetDeviceProcAddr;
    i->pfnGetPhysicalDeviceProcAddr = fgGetPhysicalDeviceProcAddr;
    return VK_SUCCESS;
}
BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_DETACH) {
        if (sink) UnmapViewOfFile(sink);
        if (sink_mapping) CloseHandle(sink_mapping);
    }
    return TRUE;
}
