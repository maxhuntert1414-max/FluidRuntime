#define VK_NO_PROTOTYPES
#include <vulkan/vulkan.h>
#include <vulkan/vk_layer.h>
#include <windows.h>
#include "vulkan_observation.h"
#include <cstring>
#include <iostream>
#include <stdexcept>
#include <string>
#include <atomic>
#include <array>
#include <thread>
#include <vector>
#include <limits>

namespace {
struct Handle { void* dispatch; } instance_handle{}, device_handle{};
int instance_key{}, device_key{};
unsigned checks{}, destroyed_instances{}, destroyed_devices{}, submits{};
bool late_lookup{};
uintptr_t allocation_id{};
bool reuse_on_free{};
PFN_vkAllocateMemory top_allocate{};
PFN_vkVoidFunction top_destroy_instance{}, top_destroy_device{};
uintptr_t buffer_id{};
bool fail_buffer{}, reuse_on_destroy{};
VkResult bind_result = VK_SUCCESS;
PFN_vkCreateBuffer top_create_buffer{};
std::atomic<unsigned> copies{}, copy2s{};
std::atomic<const void*> last_regions{};
std::atomic<VkBuffer> last_source{}, last_destination{};
const VkBindBufferMemoryInfo* last_bind_infos{};
unsigned destroyed_buffers{};
bool recreate_on_destroy{};
PFN_vkCreateDevice top_create_device{};
VkPhysicalDevice reuse_physical{};
bool testing_commands{};
VkResult command_result = VK_SUCCESS;
VkResult command_submit_result = VK_SUCCESS;
std::array<Handle, 8> command_handles{};
std::atomic<const void*> last_command_info{};
std::atomic<unsigned> command_submits{};
unsigned next_command_handle{};
const auto command_pool_handle = reinterpret_cast<VkCommandPool>(uintptr_t{701});
PFN_vkResetCommandBuffer top_reset_command{};
VkCommandBuffer reset_during_submit{};
void recreate_device();
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
VKAPI_ATTR void VKAPI_CALL destroy_device(VkDevice, const VkAllocationCallbacks*) {
    ++destroyed_devices;
    if (recreate_on_destroy) {
        recreate_on_destroy = false;
        recreate_device();
    }
}
VKAPI_ATTR void VKAPI_CALL memory_properties(VkPhysicalDevice, VkPhysicalDeviceMemoryProperties* out) {
    *out = {};
    out->memoryTypeCount = 3;
    out->memoryTypes[0].propertyFlags = VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT;
    out->memoryTypes[1].propertyFlags = VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT;
    out->memoryTypes[2].propertyFlags = VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT;
}
VKAPI_ATTR VkResult VKAPI_CALL submit(VkQueue q, uint32_t n, const VkSubmitInfo* info, VkFence fence) {
    if (testing_commands) {
        last_command_info = info;
        ++command_submits;
        if (reset_during_submit) {
            const auto command = reset_during_submit;
            reset_during_submit = nullptr;
            check(top_reset_command(command, 0) == VK_SUCCESS);
        }
        return command_submit_result;
    }
    check(q == reinterpret_cast<VkQueue>(&device_handle) && n == 0 && !info && !fence);
    ++submits; return VK_ERROR_DEVICE_LOST;
}
VKAPI_ATTR VkResult VKAPI_CALL submit2(VkQueue, uint32_t, const VkSubmitInfo2* info, VkFence) {
    last_command_info = info;
    ++command_submits;
    return command_submit_result;
}
VKAPI_ATTR VkResult VKAPI_CALL create_command_pool(VkDevice, const VkCommandPoolCreateInfo* info,
    const VkAllocationCallbacks*, VkCommandPool* output) {
    last_command_info = info;
    if (command_result == VK_SUCCESS) *output = command_pool_handle;
    return command_result;
}
VKAPI_ATTR void VKAPI_CALL destroy_command_pool(VkDevice, VkCommandPool, const VkAllocationCallbacks*) {}
VKAPI_ATTR VkResult VKAPI_CALL reset_command_pool(VkDevice, VkCommandPool, VkCommandPoolResetFlags) {
    return command_result;
}
VKAPI_ATTR VkResult VKAPI_CALL allocate_commands(VkDevice, const VkCommandBufferAllocateInfo* info, VkCommandBuffer* output) {
    last_command_info = info;
    if (command_result != VK_SUCCESS) return command_result;
    for (uint32_t i = 0; i < info->commandBufferCount; ++i) {
        check(next_command_handle < command_handles.size());
        auto& handle = command_handles[next_command_handle++];
        handle.dispatch = &device_key;
        output[i] = reinterpret_cast<VkCommandBuffer>(&handle);
    }
    return VK_SUCCESS;
}
VKAPI_ATTR void VKAPI_CALL free_commands(VkDevice, VkCommandPool, uint32_t, const VkCommandBuffer* buffers) {
    last_command_info = buffers;
}
VKAPI_ATTR VkResult VKAPI_CALL begin_command(VkCommandBuffer, const VkCommandBufferBeginInfo* info) {
    last_command_info = info;
    return command_result;
}
VKAPI_ATTR VkResult VKAPI_CALL end_command(VkCommandBuffer) { return command_result; }
VKAPI_ATTR VkResult VKAPI_CALL reset_command(VkCommandBuffer, VkCommandBufferResetFlags) { return command_result; }
VKAPI_ATTR void VKAPI_CALL execute_commands(VkCommandBuffer, uint32_t, const VkCommandBuffer* buffers) {
    last_command_info = buffers;
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
VKAPI_ATTR VkResult VKAPI_CALL create_buffer(VkDevice, const VkBufferCreateInfo*, const VkAllocationCallbacks*, VkBuffer* out) {
    if (fail_buffer) return VK_ERROR_OUT_OF_DEVICE_MEMORY;
    *out = reinterpret_cast<VkBuffer>(++buffer_id);
    return VK_SUCCESS;
}
VKAPI_ATTR void VKAPI_CALL destroy_buffer(VkDevice device, VkBuffer buffer, const VkAllocationCallbacks*) {
    ++destroyed_buffers;
    if (reuse_on_destroy) {
        reuse_on_destroy = false;
        buffer_id = reinterpret_cast<uintptr_t>(buffer) - 1;
        VkBufferCreateInfo info{};
        info.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO;
        info.size = 1024;
        VkBuffer replacement{};
        check(top_create_buffer(device, &info, nullptr, &replacement) == VK_SUCCESS && replacement == buffer);
    }
}
VKAPI_ATTR VkResult VKAPI_CALL bind_buffer(VkDevice, VkBuffer, VkDeviceMemory, VkDeviceSize) { return bind_result; }
VKAPI_ATTR VkResult VKAPI_CALL bind_buffer2(VkDevice, uint32_t, const VkBindBufferMemoryInfo* info) {
    last_bind_infos = info;
    return bind_result;
}
VKAPI_ATTR void VKAPI_CALL copy_buffer(VkCommandBuffer, VkBuffer source, VkBuffer destination,
    uint32_t, const VkBufferCopy* regions) {
    last_source = source;
    last_destination = destination;
    last_regions = regions;
    ++copies;
}
VKAPI_ATTR void VKAPI_CALL copy_buffer2(VkCommandBuffer, const VkCopyBufferInfo2* info) {
    last_source = info->srcBuffer;
    last_destination = info->dstBuffer;
    last_regions = info;
    ++copy2s;
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
    if (!std::strcmp(name, "vkQueueSubmit2") || !std::strcmp(name, "vkQueueSubmit2KHR"))
        return reinterpret_cast<PFN_vkVoidFunction>(submit2);
    if (!std::strcmp(name, "vkCreateCommandPool")) return reinterpret_cast<PFN_vkVoidFunction>(create_command_pool);
    if (!std::strcmp(name, "vkDestroyCommandPool")) return reinterpret_cast<PFN_vkVoidFunction>(destroy_command_pool);
    if (!std::strcmp(name, "vkResetCommandPool")) return reinterpret_cast<PFN_vkVoidFunction>(reset_command_pool);
    if (!std::strcmp(name, "vkAllocateCommandBuffers")) return reinterpret_cast<PFN_vkVoidFunction>(allocate_commands);
    if (!std::strcmp(name, "vkFreeCommandBuffers")) return reinterpret_cast<PFN_vkVoidFunction>(free_commands);
    if (!std::strcmp(name, "vkBeginCommandBuffer")) return reinterpret_cast<PFN_vkVoidFunction>(begin_command);
    if (!std::strcmp(name, "vkEndCommandBuffer")) return reinterpret_cast<PFN_vkVoidFunction>(end_command);
    if (!std::strcmp(name, "vkResetCommandBuffer")) return reinterpret_cast<PFN_vkVoidFunction>(reset_command);
    if (!std::strcmp(name, "vkCmdExecuteCommands")) return reinterpret_cast<PFN_vkVoidFunction>(execute_commands);
    if (!std::strcmp(name, "vkAllocateMemory")) return reinterpret_cast<PFN_vkVoidFunction>(allocate_memory);
    if (!std::strcmp(name, "vkFreeMemory")) return reinterpret_cast<PFN_vkVoidFunction>(free_memory);
    if (!std::strcmp(name, "vkCreateBuffer")) return reinterpret_cast<PFN_vkVoidFunction>(create_buffer);
    if (!std::strcmp(name, "vkDestroyBuffer")) return reinterpret_cast<PFN_vkVoidFunction>(destroy_buffer);
    if (!std::strcmp(name, "vkBindBufferMemory")) return reinterpret_cast<PFN_vkVoidFunction>(bind_buffer);
    if (!std::strcmp(name, "vkBindBufferMemory2") || !std::strcmp(name, "vkBindBufferMemory2KHR"))
        return reinterpret_cast<PFN_vkVoidFunction>(bind_buffer2);
    if (!std::strcmp(name, "vkCmdCopyBuffer")) return reinterpret_cast<PFN_vkVoidFunction>(copy_buffer);
    if (!std::strcmp(name, "vkCmdCopyBuffer2") || !std::strcmp(name, "vkCmdCopyBuffer2KHR"))
        return reinterpret_cast<PFN_vkVoidFunction>(copy_buffer2);
    if (!std::strcmp(name, "vkUnknownDeviceExtensionTEST")) return extension;
    return nullptr;
}

void recreate_device() {
    VkLayerDeviceLink link{nullptr, gipa, gdpa};
    VkLayerDeviceCreateInfo chain{};
    chain.sType = VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO;
    chain.function = VK_LAYER_LINK_INFO;
    chain.u.pLayerInfo = &link;
    VkDeviceCreateInfo info{};
    info.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
    info.pNext = &chain;
    VkDevice replacement{};
    late_lookup = false;
    check(top_create_device(reuse_physical, &info, nullptr, &replacement) == VK_SUCCESS);
    late_lookup = true;
}

void buffer_hooks(const VkNegotiateLayerInterface& api, VkDevice device, fluid::observation::Shared* shared) {
    using namespace fluid::observation;
    const auto get = [&](const char* name) { return api.pfnGetDeviceProcAddr(device, name); };
    top_create_buffer = reinterpret_cast<PFN_vkCreateBuffer>(get("vkCreateBuffer"));
    const auto destroy = reinterpret_cast<PFN_vkDestroyBuffer>(get("vkDestroyBuffer"));
    const auto bind = reinterpret_cast<PFN_vkBindBufferMemory>(get("vkBindBufferMemory"));
    const auto copy = reinterpret_cast<PFN_vkCmdCopyBuffer>(get("vkCmdCopyBuffer"));
    const auto release = reinterpret_cast<PFN_vkFreeMemory>(get("vkFreeMemory"));
    VkBuffer buffers[3]{};
    VkDeviceMemory memories[3]{};
    VkBufferCreateInfo buffer_info{};
    buffer_info.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO;
    buffer_info.size = 1024;
    buffer_info.usage = VK_BUFFER_USAGE_TRANSFER_SRC_BIT | VK_BUFFER_USAGE_TRANSFER_DST_BIT;
    fail_buffer = true;
    check(top_create_buffer(device, &buffer_info, nullptr, &buffers[0]) == VK_ERROR_OUT_OF_DEVICE_MEMORY);
    check(!buffers[0] && shared->counters[buffers_created] == 0);
    fail_buffer = false;
    for (uint32_t i = 0; i < 3; ++i) {
        check(top_create_buffer(device, &buffer_info, nullptr, &buffers[i]) == VK_SUCCESS);
        VkMemoryAllocateInfo memory_info{};
        memory_info.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
        memory_info.allocationSize = 1024;
        memory_info.memoryTypeIndex = i;
        check(top_allocate(device, &memory_info, nullptr, &memories[i]) == VK_SUCCESS);
        check(bind(device, buffers[i], memories[i], 0) == VK_SUCCESS);
    }
    const auto command = reinterpret_cast<VkCommandBuffer>(device);
    VkBufferCopy region{0, 0, 1024};
    copy(command, buffers[0], buffers[1], 1, &region);
    check(last_source == buffers[0] && last_destination == buffers[1] && last_regions == &region);
    copy(command, buffers[1], buffers[0], 1, &region);
    copy(command, buffers[0], buffers[2], 1, &region);
    check(shared->counters[host_to_device_copy_bytes] == 1024);
    check(shared->counters[device_to_host_copy_bytes] == 1024);
    check(shared->counters[shared_memory_copy_bytes] == 1024);
    for (const auto* name : {"vkBindBufferMemory2", "vkBindBufferMemory2KHR"}) {
        const auto bind2 = reinterpret_cast<PFN_vkBindBufferMemory2>(get(name));
        VkBindBufferMemoryInfo info{VK_STRUCTURE_TYPE_BIND_BUFFER_MEMORY_INFO, nullptr, buffers[1], memories[1], 0};
        bind_result = VK_ERROR_OUT_OF_DEVICE_MEMORY;
        check(bind2(device, 1, &info) == bind_result);
        check(last_bind_infos == &info);
        copy(command, buffers[0], buffers[1], 1, &region);
        bind_result = VK_SUCCESS;
        check(bind2(device, 1, &info) == VK_SUCCESS);
    }
    check(shared->counters[unknown_buffer_copy_bytes] == 2048);
    check(shared->counters[buffer_binding_failures] == 2);
    for (const auto* name : {"vkCmdCopyBuffer2", "vkCmdCopyBuffer2KHR"}) {
        const auto copy2 = reinterpret_cast<PFN_vkCmdCopyBuffer2>(get(name));
        VkBufferCopy2 region2{VK_STRUCTURE_TYPE_BUFFER_COPY_2, nullptr, 0, 0, 1024};
        VkCopyBufferInfo2 info{VK_STRUCTURE_TYPE_COPY_BUFFER_INFO_2, nullptr, buffers[0], buffers[1], 1, &region2};
        copy2(command, &info);
        check(last_regions == &info && last_source == buffers[0] && last_destination == buffers[1]);
    }
    check(shared->counters[host_to_device_copy_bytes] == 3072);

    std::vector<std::thread> workers;
    for (unsigned i = 0; i < 4; ++i) {
        workers.emplace_back([&] {
            Handle command_handle{&device_key};
            for (unsigned j = 0; j < 1000; ++j) {
                copy(reinterpret_cast<VkCommandBuffer>(&command_handle), buffers[0], buffers[1], 1, &region);
            }
        });
    }
    for (auto& worker : workers) worker.join();
    check(shared->counters[host_to_device_copy_bytes] == 4003 * 1024);
    const auto recorded = shared->counters[buffer_copy_bytes];
    InterlockedExchange64(&shared->enabled, 0);
    copy(command, buffers[0], buffers[1], 1, &region);
    check(shared->counters[buffer_copy_bytes] == recorded);
    InterlockedExchange64(&shared->enabled, 1);

    reuse_on_destroy = true;
    destroy(device, buffers[0], nullptr);
    copy(command, buffers[0], buffers[1], 1, &region);
    check(shared->counters[unknown_buffer_copy_bytes] == 3072);
    check(shared->counters[live_buffers] == 3);
    for (unsigned i = 0; i < 3; ++i) {
        destroy(device, buffers[i], nullptr);
        release(device, memories[i], nullptr);
    }
    check(shared->counters[live_buffers] == 0 && shared->counters[live_bytes] == 0);
    check(destroyed_buffers == 4);
    check(copies == 4007 && copy2s == 2);

    // A synthetic mock-only oversized region must still be forwarded without
    // allowing unsigned sizes to wrap the telemetry into negative counters.
    region.size = std::numeric_limits<uint64_t>::max();
    copy(command, buffers[0], buffers[1], 1, &region);
    check(shared->counters[buffer_copy_bytes] == std::numeric_limits<LONG64>::max());
    check(shared->counters[counter_overflows] > 0 && copies == 4008);
}

void command_hooks(const VkNegotiateLayerInterface &api, VkDevice device,
                   fluid::observation::Shared *shared) {
    using namespace fluid::observation;
    testing_commands = true;
    const auto get = [&](const char *name) { return api.pfnGetDeviceProcAddr(device, name); };
    const auto create = reinterpret_cast<PFN_vkCreateCommandPool>(get("vkCreateCommandPool"));
    const auto destroy = reinterpret_cast<PFN_vkDestroyCommandPool>(get("vkDestroyCommandPool"));
    const auto allocate = reinterpret_cast<PFN_vkAllocateCommandBuffers>(get("vkAllocateCommandBuffers"));
    const auto free = reinterpret_cast<PFN_vkFreeCommandBuffers>(get("vkFreeCommandBuffers"));
    const auto begin = reinterpret_cast<PFN_vkBeginCommandBuffer>(get("vkBeginCommandBuffer"));
    const auto end = reinterpret_cast<PFN_vkEndCommandBuffer>(get("vkEndCommandBuffer"));
    const auto reset_pool = reinterpret_cast<PFN_vkResetCommandPool>(get("vkResetCommandPool"));
    const auto execute = reinterpret_cast<PFN_vkCmdExecuteCommands>(get("vkCmdExecuteCommands"));
    const auto copy = reinterpret_cast<PFN_vkCmdCopyBuffer>(get("vkCmdCopyBuffer"));
    const auto queue_submit = reinterpret_cast<PFN_vkQueueSubmit>(get("vkQueueSubmit"));
    top_reset_command = reinterpret_cast<PFN_vkResetCommandBuffer>(get("vkResetCommandBuffer"));
    VkCommandPoolCreateInfo pool_info{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};
    VkCommandPool pool{};
    check(create(device, &pool_info, nullptr, &pool) == VK_SUCCESS && last_command_info == &pool_info);
    VkCommandBufferAllocateInfo allocation{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
    allocation.commandPool = pool;
    allocation.commandBufferCount = 1;
    VkCommandBuffer primary{}, secondary{};
    check(allocate(device, &allocation, &primary) == VK_SUCCESS && last_command_info == &allocation);
    allocation.level = VK_COMMAND_BUFFER_LEVEL_SECONDARY;
    check(allocate(device, &allocation, &secondary) == VK_SUCCESS);
    VkCommandBufferInheritanceInfo inheritance{VK_STRUCTURE_TYPE_COMMAND_BUFFER_INHERITANCE_INFO};
    VkCommandBufferBeginInfo begin_info{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
    begin_info.flags = VK_COMMAND_BUFFER_USAGE_SIMULTANEOUS_USE_BIT;
    begin_info.pInheritanceInfo = &inheritance;
    check(begin(secondary, &begin_info) == VK_SUCCESS && last_command_info == &begin_info);
    VkBufferCopy region{0, 0, 128};
    // Mocks isolate command accounting from memory attribution; no ICD sees these
    // handles.
    const auto source = reinterpret_cast<VkBuffer>(uintptr_t{901});
    const auto destination = reinterpret_cast<VkBuffer>(uintptr_t{902});
    copy(secondary, source, destination, 1, &region);
    check(end(secondary) == VK_SUCCESS);
    begin_info.pInheritanceInfo = reinterpret_cast<const VkCommandBufferInheritanceInfo *>(uintptr_t{1});
    check(begin(primary, &begin_info) == VK_SUCCESS);
    execute(primary, 1, &secondary);
    check(last_command_info == &secondary);
    region.size = 32;
    copy(primary, source, destination, 1, &region);
    check(end(primary) == VK_SUCCESS);
    const auto queue = reinterpret_cast<VkQueue>(device);
    VkSubmitInfo submission{VK_STRUCTURE_TYPE_SUBMIT_INFO};
    submission.commandBufferCount = 1;
    submission.pCommandBuffers = &primary;
    command_submit_result = VK_ERROR_DEVICE_LOST;
    check(queue_submit(queue, 1, &submission, nullptr) == VK_ERROR_DEVICE_LOST);
    check(last_command_info == &submission && shared->counters[submitted_buffer_copy_bytes] == 0);
    command_submit_result = VK_SUCCESS;
    check(queue_submit(queue, 1, &submission, nullptr) == VK_SUCCESS);
    check(shared->counters[submitted_buffer_copy_bytes] == 160 &&
          shared->counters[resubmitted_command_buffers] == 0);
    check(queue_submit(queue, 1, &submission, nullptr) == VK_SUCCESS);
    check(shared->counters[submitted_buffer_copy_bytes] == 320 &&
          shared->counters[resubmitted_command_buffers] == 2);
    for (const auto *name : {"vkQueueSubmit2", "vkQueueSubmit2KHR"}) {
        const auto submit_new = reinterpret_cast<PFN_vkQueueSubmit2>(get(name));
        VkCommandBufferSubmitInfo command_info{VK_STRUCTURE_TYPE_COMMAND_BUFFER_SUBMIT_INFO};
        command_info.commandBuffer = primary;
        VkSubmitInfo2 info{VK_STRUCTURE_TYPE_SUBMIT_INFO_2};
        info.commandBufferInfoCount = 1;
        info.pCommandBufferInfos = &command_info;
        check(submit_new(queue, 1, &info, nullptr) == VK_SUCCESS && last_command_info == &info);
        command_info.deviceMask = 2;
        check(submit_new(queue, 1, &info, nullptr) == VK_SUCCESS);
    }
    check(shared->counters[submitted_buffer_copy_bytes] == 640);
    check(shared->counters[unresolved_submit_calls] == 2);
    // Concurrent accepted submissions must not lose replay increments.
    std::vector<std::thread> workers;
    for (unsigned i = 0; i < 4; ++i) {
        workers.emplace_back([&] {
            Handle queue_handle{&device_key};
            for (unsigned j = 0; j < 100; ++j)
                queue_submit(reinterpret_cast<VkQueue>(&queue_handle), 1, &submission, nullptr);
        });
    }
    for (auto &worker : workers)
        worker.join();
    check(shared->counters[submitted_buffer_copy_bytes] == 64640);
    check(shared->counters[resubmitted_command_buffers] == 806);
    check(top_reset_command(secondary, 0) == VK_SUCCESS);
    check(queue_submit(queue, 1, &submission, nullptr) == VK_SUCCESS);
    check(shared->counters[submitted_buffer_copy_bytes] == 64640);
    check(shared->counters[unresolved_submit_calls] == 3);
    check(begin(primary, &begin_info) == VK_SUCCESS);
    copy(primary, source, destination, 1, &region);
    check(end(primary) == VK_SUCCESS);
    reset_during_submit = primary;
    check(queue_submit(queue, 1, &submission, nullptr) == VK_SUCCESS);
    check(shared->counters[submitted_buffer_copy_bytes] == 64640);
    check(shared->counters[unresolved_submit_calls] == 4);
    check(begin(primary, &begin_info) == VK_SUCCESS);
    copy(primary, source, destination, 1, &region);
    check(end(primary) == VK_SUCCESS);
    VkBaseInStructure unknown{VK_STRUCTURE_TYPE_APPLICATION_INFO, nullptr};
    submission.pNext = &unknown;
    check(queue_submit(queue, 1, &submission, nullptr) == VK_SUCCESS);
    check(shared->counters[unresolved_submit_calls] == 5);
    submission.pNext = nullptr;
    check(reset_pool(device, pool, 0) == VK_SUCCESS);
    check(queue_submit(queue, 1, &submission, nullptr) == VK_SUCCESS);
    check(shared->counters[unresolved_submit_calls] == 6);
    free(device, pool, 1, &secondary);
    check(last_command_info == &secondary && shared->counters[live_command_buffers] == 1);
    destroy(device, pool, nullptr);
    check(shared->counters[live_command_buffers] == 0);
    check(shared->counters[command_buffers_allocated] == 2 && shared->counters[command_buffers_freed] == 2);
    check(shared->counters[submitted_primary_command_buffers] == 404);
    check(shared->counters[submitted_secondary_command_buffers] == 404);
    check(shared->counters[submitted_buffer_copies] == 808);
    check(command_submits == 411);
    testing_commands = false;
}
} // namespace

int main(int argc, char **argv) {
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
            check(api.pfnGetDeviceProcAddr(device, "vkQueueBindSparse") == nullptr);
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
                buffer_hooks(api, device, shared);
                command_hooks(api, device, shared);
            }
            const auto queue_submit = reinterpret_cast<PFN_vkQueueSubmit>(api.pfnGetDeviceProcAddr(device, "vkQueueSubmit"));
            check(queue_submit(reinterpret_cast<VkQueue>(device), 0, nullptr, VK_NULL_HANDLE) == VK_ERROR_DEVICE_LOST);
            // Simulate a loader whose late lookup routes through the top table.
            // Destruction must still invoke the captured downstream functions.
            late_lookup = true;
            if (cycle == 99) {
                top_create_device = cd;
                reuse_physical = reinterpret_cast<VkPhysicalDevice>(instance);
                recreate_on_destroy = true;
            }
            reinterpret_cast<PFN_vkDestroyDevice>(top_destroy_device)(device, nullptr);
            if (cycle == 99) {
                check(shared->counters[fluid::observation::active_devices] == 1);
                check(api.pfnGetDeviceProcAddr(device, "vkCreateBuffer") != nullptr);
                reinterpret_cast<PFN_vkDestroyDevice>(top_destroy_device)(device, nullptr);
            }
            reinterpret_cast<PFN_vkDestroyInstance>(top_destroy_instance)(instance, nullptr);
            check(destroyed_instances == cycle + 1 && destroyed_devices == cycle + 1 + (cycle == 99 ? 1 : 0));
        }
        check(submits == 100);
        check(shared->counters[fluid::observation::api_errors] == 104);
        check(shared->counters[fluid::observation::active_devices] == 0);
        check(shared->counters[fluid::observation::allocations] == 6);
        FreeLibrary(module);
        UnmapViewOfFile(shared);
        CloseHandle(mapping);
        std::cout << "Observation dispatch: " << checks << " checks passed\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
