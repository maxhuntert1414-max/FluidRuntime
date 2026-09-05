#define VK_NO_PROTOTYPES
#include <vulkan/vulkan.h>
#include "fluidruntime_vulkan_api.h"
#include "fluidruntime_hook_api.h"
#include "fluidruntime_transfer_api.h"
#include "vulkan_transfer_policy.h"

#include <array>
#include <atomic>
#include <cmath>
#include <cstring>
#include <cstdio>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

namespace {
thread_local std::array<char, 512> error_text{};
void record_error(const char* text) noexcept {
    std::snprintf(error_text.data(), error_text.size(), "%s", text);
}
_Post_satisfies_(valid) void require(bool valid, const char* message) {
    if (!valid) throw std::runtime_error(message);
}
void vk_check(VkResult result, const char* operation) {
    if (result != VK_SUCCESS) {
        throw std::runtime_error(std::string(operation) + ": VkResult=" +
            std::to_string(result));
    }
}
LONG64 ticks() {
    LARGE_INTEGER value{};
    QueryPerformanceCounter(&value);
    return value.QuadPart;
}
LONG64 atomic_read(volatile LONG64& value) {
    return InterlockedCompareExchange64(&value, 0, 0);
}

#define INSTANCE_FUNCTIONS(X) \
    X(DestroyInstance) X(EnumeratePhysicalDevices) \
    X(GetPhysicalDeviceProperties) X(GetPhysicalDeviceMemoryProperties) \
    X(GetPhysicalDeviceQueueFamilyProperties) X(CreateDevice) X(GetDeviceProcAddr)
#define DEVICE_FUNCTIONS(X) \
    X(DestroyDevice) X(GetDeviceQueue) X(CreateBuffer) X(DestroyBuffer) \
    X(GetBufferMemoryRequirements) X(AllocateMemory) X(FreeMemory) \
    X(BindBufferMemory) X(MapMemory) X(UnmapMemory) X(FlushMappedMemoryRanges) \
    X(InvalidateMappedMemoryRanges) X(CreateCommandPool) X(DestroyCommandPool) \
    X(AllocateCommandBuffers) X(ResetCommandPool) X(BeginCommandBuffer) \
    X(EndCommandBuffer) X(CmdCopyBuffer) X(CmdFillBuffer) X(CmdPipelineBarrier) \
    X(CreateFence) X(DestroyFence) X(ResetFences) X(WaitForFences) X(QueueSubmit) \
    X(CreateQueryPool) X(DestroyQueryPool) X(CmdResetQueryPool) \
    X(CmdWriteTimestamp) X(GetQueryPoolResults)
struct Dispatch {
    PFN_vkGetInstanceProcAddr GetInstanceProcAddr{};
    PFN_vkCreateInstance CreateInstance{};
    PFN_vkEnumerateInstanceLayerProperties EnumerateInstanceLayerProperties{};
    PFN_vkCreateDebugUtilsMessengerEXT CreateDebugUtilsMessengerEXT{};
    PFN_vkDestroyDebugUtilsMessengerEXT DestroyDebugUtilsMessengerEXT{};
#define FIELD(name) PFN_vk##name name{};
    INSTANCE_FUNCTIONS(FIELD)
    DEVICE_FUNCTIONS(FIELD)
#undef FIELD
};

struct Ring {
    HANDLE mapping{};
    FluidHookRingHeaderV1* header{};
    FluidHookControlBlockV1* control{};
    FluidHookEventV1* events{};
    ~Ring() {
        if (header) UnmapViewOfFile(header);
        if (mapping) CloseHandle(mapping);
    }
    void open() {
        const auto name = std::wstring(fluid_transfer_ring_name_prefix) +
            L"3-" + std::to_wstring(GetCurrentProcessId());
        mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
            0, static_cast<DWORD>(fluid_hook_ring_mapping_size), name.c_str());
        require(mapping != nullptr, "Cannot create Vulkan IPC mapping");
        require(GetLastError() != ERROR_ALREADY_EXISTS, "Vulkan IPC name already exists");
        header = static_cast<FluidHookRingHeaderV1*>(MapViewOfFile(mapping,
            FILE_MAP_ALL_ACCESS, 0, 0, fluid_hook_ring_mapping_size));
        require(header != nullptr, "Cannot map Vulkan IPC ring");
        ZeroMemory(header, fluid_hook_ring_mapping_size);
        control = reinterpret_cast<FluidHookControlBlockV1*>(header + 1);
        events = reinterpret_cast<FluidHookEventV1*>(control + 1);
        LARGE_INTEGER frequency{};
        QueryPerformanceFrequency(&frequency);
        header->abi_version = fluid_hook_ring_abi_version;
        header->capacity = fluid_hook_ring_capacity;
        header->event_size = sizeof(FluidHookEventV1);
        header->qpc_frequency = static_cast<std::uint64_t>(frequency.QuadPart);
        header->process_id = GetCurrentProcessId();
        header->reserved = static_cast<std::uint64_t>(FluidTransferBackendV1::vulkan);
        control->magic = fluid_hook_control_magic;
        control->abi_version = fluid_hook_control_abi_version;
        for (std::uint32_t i = 0; i < fluid_hook_ring_capacity; ++i) events[i].sequence = -1;
        MemoryBarrier();
        InterlockedExchange(reinterpret_cast<volatile LONG*>(&header->magic),
            static_cast<LONG>(fluid_hook_ring_magic));
    }
    void emit(FluidHookEventTypeV1 type, std::uint64_t a = 0,
        std::uint64_t b = 0, std::uint64_t bytes = 0,
        std::uint64_t generation = 0, std::uint32_t flags = 0,
        std::uint64_t scope = 0, std::uint32_t source_slot = 0) {
        const auto sequence = atomic_read(header->next_sequence);
        if (sequence - atomic_read(header->reader_sequence) >= fluid_hook_ring_capacity) {
            InterlockedIncrement64(&header->overrun_count);
        }
        auto& event = events[static_cast<std::uint64_t>(sequence) % fluid_hook_ring_capacity];
        InterlockedExchange64(&event.sequence, -1);
        event.qpc_ticks = ticks();
        event.type = static_cast<std::uint32_t>(type);
        event.thread_id = GetCurrentThreadId();
        event.resource_a = a;
        event.resource_b = b;
        event.size_bytes = bytes;
        event.generation = generation;
        event.flags = flags | fluid_hook_event_flag_generalized_transfer;
        event.subresource_a = source_slot;
        event.subresource_b = 0;
        event.reserved = 0;
        event.region_key = scope;
        MemoryBarrier();
        InterlockedExchange64(&event.sequence, sequence);
        InterlockedIncrement64(&header->next_sequence);
    }
    void status(FluidHookControlStatusV1 value) {
        InterlockedExchange64(&control->status, static_cast<LONG64>(value));
    }
};
struct Buffer {
    VkBuffer buffer{};
    VkDeviceMemory memory{};
    VkMemoryPropertyFlags flags{};
};
struct Lane {
    Buffer source;
    Buffer destination;
    Buffer readback;
    std::array<std::vector<unsigned char>, 2> shadow;
    VkCommandBuffer command{};
    int retained_slot{-1};
    bool initialized{};
    std::uint64_t generation{};
};
enum class State { idle, recording, pending, completed, failed };
}

struct FluidVulkanContext {
    DWORD owner{GetCurrentThreadId()};
    FluidVulkanOptionsV1 options{};
    HMODULE loader{};
    Dispatch vk;
    VkInstance instance{};
    VkDebugUtilsMessengerEXT messenger{};
    VkPhysicalDevice physical{};
    VkPhysicalDeviceProperties properties{};
    VkPhysicalDeviceMemoryProperties memory_properties{};
    VkDevice device{};
    VkQueue queue{};
    std::uint32_t family{};
    std::uint32_t timestamp_bits{};
    VkCommandPool pool{};
    VkFence fence{};
    VkQueryPool queries{};
    std::array<Lane, fluid_vulkan_lanes> lanes;
    Ring ring;
    fluid::vulkan::Policy policy;
    State state{State::idle};
    bool revoked{};
    bool control_processed{};
    std::atomic<std::uint64_t> validation_errors{};
    std::atomic<std::uint64_t> validation_warnings{};
    std::atomic<std::uint64_t> loader_messages{};
    FluidVulkanSnapshotV1 stats{};
    LONG64 submitted_at{};

    ~FluidVulkanContext() { cleanup(); }

    void cleanup() noexcept {
        // Never destroy in-flight objects: the exported destroy operation
        // refuses pending work; callers may retry submit to wait again.
        if (device && vk.DestroyDevice) {
            if (queries && vk.DestroyQueryPool) vk.DestroyQueryPool(device, queries, nullptr);
            if (fence && vk.DestroyFence) vk.DestroyFence(device, fence, nullptr);
            if (pool && vk.DestroyCommandPool) vk.DestroyCommandPool(device, pool, nullptr);
            for (auto& lane : lanes) {
                for (auto* buffer : {&lane.source, &lane.destination, &lane.readback}) {
                    if (buffer->buffer && vk.DestroyBuffer) vk.DestroyBuffer(device, buffer->buffer, nullptr);
                    if (buffer->memory && vk.FreeMemory) vk.FreeMemory(device, buffer->memory, nullptr);
                }
            }
            vk.DestroyDevice(device, nullptr);
            device = nullptr;
        }
        if (messenger && vk.DestroyDebugUtilsMessengerEXT) {
            vk.DestroyDebugUtilsMessengerEXT(instance, messenger, nullptr);
            messenger = VK_NULL_HANDLE;
        }
        if (instance && vk.DestroyInstance) vk.DestroyInstance(instance, nullptr);
        instance = nullptr;
        if (loader) FreeLibrary(loader);
        loader = nullptr;
    }

    bool registered() const {
        for (const auto& lane : lanes) {
            for (const auto& source : lane.shadow) {
                if (source.size() != options.buffer_bytes) return false;
            }
        }
        return true;
    }
    void barrier(VkCommandBuffer command, VkAccessFlags from, VkAccessFlags to,
        VkPipelineStageFlags source_stage = VK_PIPELINE_STAGE_TRANSFER_BIT,
        VkPipelineStageFlags target_stage = VK_PIPELINE_STAGE_TRANSFER_BIT) {
        VkMemoryBarrier barrier{};
        barrier.sType = VK_STRUCTURE_TYPE_MEMORY_BARRIER;
        barrier.srcAccessMask = from;
        barrier.dstAccessMask = to;
        vk.CmdPipelineBarrier(command, source_stage, target_stage, 0,
            1, &barrier, 0, nullptr, 0, nullptr);
    }
    Lane& recording_lane(std::uint32_t index) {
        require(state == State::recording && index < lanes.size(),
            "Operation requires a recording context and a valid lane");
        return lanes[index];
    }
    void invalidate_lane(std::uint32_t index, bool explicit_write) {
        auto& lane = recording_lane(index);
        lane.retained_slot = -1;
        ++lane.generation;
        ++stats.invalidations;
        ring.emit(FluidHookEventTypeV1::transfer_resource_invalidate,
            201 + index, 0, options.buffer_bytes, lane.generation,
            explicit_write ? fluid_hook_event_flag_explicit_invalidation : 0,
            1 + index);
    }
    bool reserve() {
        const auto now = ticks();
        const auto published = atomic_read(ring.control->published_epoch);
        if (revoked || !policy.reserve(now, published)) {
            if (policy.epoch && !revoked) {
                ring.status(published != policy.epoch ? FluidHookControlStatusV1::rejected :
                    now >= policy.deadline ? FluidHookControlStatusV1::expired :
                    FluidHookControlStatusV1::exhausted);
            }
            return false;
        }
        InterlockedExchange64(&ring.control->applied_action_count,
            static_cast<LONG64>(policy.applied));
        if (policy.applied == policy.budget) ring.status(FluidHookControlStatusV1::exhausted);
        return true;
    }
    void make_buffer(Buffer& result, VkDeviceSize bytes, VkBufferUsageFlags usage,
        VkMemoryPropertyFlags required) {
        VkBufferCreateInfo info{};
        info.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO;
        info.size = bytes;
        info.usage = usage;
        info.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
        vk_check(vk.CreateBuffer(device, &info, nullptr, &result.buffer), "vkCreateBuffer");
        VkMemoryRequirements requirements{};
        vk.GetBufferMemoryRequirements(device, result.buffer, &requirements);
        std::uint32_t type = UINT32_MAX;
        for (std::uint32_t i = 0; i < memory_properties.memoryTypeCount; ++i) {
            const auto flags = memory_properties.memoryTypes[i].propertyFlags;
            if ((requirements.memoryTypeBits & (1U << i)) && (flags & required) == required) {
                type = i;
                result.flags = flags;
                break;
            }
        }
        require(type != UINT32_MAX, "Required Vulkan memory type unavailable");
        VkMemoryAllocateInfo allocation{};
        allocation.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
        allocation.allocationSize = requirements.size;
        allocation.memoryTypeIndex = type;
        vk_check(vk.AllocateMemory(device, &allocation, nullptr, &result.memory), "vkAllocateMemory");
        vk_check(vk.BindBufferMemory(device, result.buffer, result.memory, 0), "vkBindBufferMemory");
    }
};

namespace {
VKAPI_ATTR VkBool32 VKAPI_CALL debug_callback(VkDebugUtilsMessageSeverityFlagBitsEXT severity,
    VkDebugUtilsMessageTypeFlagsEXT type, const VkDebugUtilsMessengerCallbackDataEXT* data, void* user) {
    auto& context = *static_cast<FluidVulkanContext*>(user);
    // The loader reports intentional overlay disabling as GENERAL warnings.
    // Preserve those separately; API/synchronization warnings and all errors
    // still fail the validation gate.
    if (!(type & (VK_DEBUG_UTILS_MESSAGE_TYPE_VALIDATION_BIT_EXT |
            VK_DEBUG_UTILS_MESSAGE_TYPE_PERFORMANCE_BIT_EXT)) &&
        !(severity & VK_DEBUG_UTILS_MESSAGE_SEVERITY_ERROR_BIT_EXT)) {
        ++context.loader_messages;
        std::fprintf(stderr, "Vulkan loader: %s\n", data->pMessage);
        return VK_FALSE;
    }
    if (severity & VK_DEBUG_UTILS_MESSAGE_SEVERITY_ERROR_BIT_EXT) ++context.validation_errors;
    if (severity & VK_DEBUG_UTILS_MESSAGE_SEVERITY_WARNING_BIT_EXT) ++context.validation_warnings;
    std::fprintf(stderr, "Vulkan validation: %s\n", data->pMessage);
    return VK_FALSE;
}

template<class Action> HRESULT protect(FluidVulkanContext* context, Action&& action) {
    try {
        error_text[0] = '\0';
        require(context != nullptr, "Null Vulkan context");
        require(context->owner == GetCurrentThreadId(), "Vulkan context called from a non-owner thread");
        action(*context);
        return S_OK;
    } catch (const std::exception& error) {
        record_error(error.what());
        return E_FAIL;
    } catch (...) {
        record_error("Unknown native Vulkan failure");
        return E_FAIL;
    }
}

void initialize(FluidVulkanContext& c) {
    c.loader = LoadLibraryExW(L"vulkan-1.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    require(c.loader != nullptr, "System Vulkan loader unavailable; install the GPU vendor driver");
    c.vk.GetInstanceProcAddr = reinterpret_cast<PFN_vkGetInstanceProcAddr>(
        GetProcAddress(c.loader, "vkGetInstanceProcAddr"));
    require(c.vk.GetInstanceProcAddr != nullptr, "Vulkan loader missing vkGetInstanceProcAddr");
    c.vk.CreateInstance = reinterpret_cast<PFN_vkCreateInstance>(
        c.vk.GetInstanceProcAddr(nullptr, "vkCreateInstance"));
    c.vk.EnumerateInstanceLayerProperties = reinterpret_cast<PFN_vkEnumerateInstanceLayerProperties>(
        c.vk.GetInstanceProcAddr(nullptr, "vkEnumerateInstanceLayerProperties"));
    require(c.vk.CreateInstance && c.vk.EnumerateInstanceLayerProperties, "Vulkan global dispatch incomplete");

    const char* validation_layer = "VK_LAYER_KHRONOS_validation";
    const std::array<const char*, 2> extensions{
        VK_EXT_DEBUG_UTILS_EXTENSION_NAME, VK_EXT_VALIDATION_FEATURES_EXTENSION_NAME};
    VkDebugUtilsMessengerCreateInfoEXT debug{};
    debug.sType = VK_STRUCTURE_TYPE_DEBUG_UTILS_MESSENGER_CREATE_INFO_EXT;
    debug.messageSeverity = VK_DEBUG_UTILS_MESSAGE_SEVERITY_ERROR_BIT_EXT |
        VK_DEBUG_UTILS_MESSAGE_SEVERITY_WARNING_BIT_EXT;
    debug.messageType = VK_DEBUG_UTILS_MESSAGE_TYPE_GENERAL_BIT_EXT |
        VK_DEBUG_UTILS_MESSAGE_TYPE_VALIDATION_BIT_EXT | VK_DEBUG_UTILS_MESSAGE_TYPE_PERFORMANCE_BIT_EXT;
    debug.pfnUserCallback = debug_callback;
    debug.pUserData = &c;
    VkValidationFeatureEnableEXT sync = VK_VALIDATION_FEATURE_ENABLE_SYNCHRONIZATION_VALIDATION_EXT;
    VkValidationFeaturesEXT validation{};
    validation.sType = VK_STRUCTURE_TYPE_VALIDATION_FEATURES_EXT;
    validation.enabledValidationFeatureCount = 1;
    validation.pEnabledValidationFeatures = &sync;
    validation.pNext = &debug;
    VkApplicationInfo application{};
    application.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO;
    application.pApplicationName = "FluidRuntime owned Vulkan transfers";
    application.apiVersion = VK_API_VERSION_1_0;
    VkInstanceCreateInfo instance{};
    instance.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
    instance.pApplicationInfo = &application;
    if (c.options.require_validation) {
        std::uint32_t count{};
        vk_check(c.vk.EnumerateInstanceLayerProperties(&count, nullptr), "Enumerate validation layers");
        std::vector<VkLayerProperties> layers(count);
        vk_check(c.vk.EnumerateInstanceLayerProperties(&count, layers.data()), "Read validation layers");
        bool available = false;
        for (const auto& layer : layers) available |= std::strcmp(layer.layerName, validation_layer) == 0;
        require(available, "Requested Khronos validation layer is not available");
        instance.enabledLayerCount = 1;
        instance.ppEnabledLayerNames = &validation_layer;
        instance.enabledExtensionCount = static_cast<std::uint32_t>(extensions.size());
        instance.ppEnabledExtensionNames = extensions.data();
        instance.pNext = &validation;
    }
    vk_check(c.vk.CreateInstance(&instance, nullptr, &c.instance), "vkCreateInstance");
#define LOAD_INSTANCE(name) c.vk.name = reinterpret_cast<PFN_vk##name>( \
    c.vk.GetInstanceProcAddr(c.instance, "vk" #name)); \
    require(c.vk.name != nullptr, "Missing vk" #name);
    INSTANCE_FUNCTIONS(LOAD_INSTANCE)
#undef LOAD_INSTANCE
    if (c.options.require_validation) {
        c.vk.CreateDebugUtilsMessengerEXT = reinterpret_cast<PFN_vkCreateDebugUtilsMessengerEXT>(
            c.vk.GetInstanceProcAddr(c.instance, "vkCreateDebugUtilsMessengerEXT"));
        c.vk.DestroyDebugUtilsMessengerEXT = reinterpret_cast<PFN_vkDestroyDebugUtilsMessengerEXT>(
            c.vk.GetInstanceProcAddr(c.instance, "vkDestroyDebugUtilsMessengerEXT"));
        require(c.vk.CreateDebugUtilsMessengerEXT && c.vk.DestroyDebugUtilsMessengerEXT,
            "Validation debug dispatch missing");
        vk_check(c.vk.CreateDebugUtilsMessengerEXT(c.instance, &debug, nullptr, &c.messenger), "Create debug messenger");
    }
    std::uint32_t count{};
    vk_check(c.vk.EnumeratePhysicalDevices(c.instance, &count, nullptr), "Enumerate physical devices");
    std::vector<VkPhysicalDevice> devices(count);
    vk_check(c.vk.EnumeratePhysicalDevices(c.instance, &count, devices.data()), "Read physical devices");
    for (auto physical : devices) {
        VkPhysicalDeviceProperties properties{};
        c.vk.GetPhysicalDeviceProperties(physical, &properties);
        const bool hardware = properties.deviceType == VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU ||
            properties.deviceType == VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU;
        if (c.options.use_hardware ? !hardware : properties.deviceType != VK_PHYSICAL_DEVICE_TYPE_CPU) continue;
        c.vk.GetPhysicalDeviceQueueFamilyProperties(physical, &count, nullptr);
        std::vector<VkQueueFamilyProperties> families(count);
        c.vk.GetPhysicalDeviceQueueFamilyProperties(physical, &count, families.data());
        for (std::uint32_t i = 0; i < count; ++i) {
            // A graphics queue guarantees transfer operations and permits both
            // TOP/BOTTOM timestamp stages when timestamps are supported.
            if (families[i].queueCount && (families[i].queueFlags & VK_QUEUE_GRAPHICS_BIT)) {
                c.physical = physical;
                c.properties = properties;
                c.family = i;
                c.timestamp_bits = families[i].timestampValidBits;
                break;
            }
        }
        if (c.physical) break;
    }
    require(c.physical != nullptr, "No Vulkan device matching requested hardware/software mode");
    c.vk.GetPhysicalDeviceMemoryProperties(c.physical, &c.memory_properties);
    const float priority = 1.0F;
    VkDeviceQueueCreateInfo queue{};
    queue.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO;
    queue.queueFamilyIndex = c.family;
    queue.queueCount = 1;
    queue.pQueuePriorities = &priority;
    VkDeviceCreateInfo device{};
    device.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
    device.queueCreateInfoCount = 1;
    device.pQueueCreateInfos = &queue;
    vk_check(c.vk.CreateDevice(c.physical, &device, nullptr, &c.device), "vkCreateDevice");
#define LOAD_DEVICE(name) c.vk.name = reinterpret_cast<PFN_vk##name>( \
    c.vk.GetDeviceProcAddr(c.device, "vk" #name)); \
    require(c.vk.name != nullptr, "Missing vk" #name);
    DEVICE_FUNCTIONS(LOAD_DEVICE)
#undef LOAD_DEVICE
    c.vk.GetDeviceQueue(c.device, c.family, 0, &c.queue);
    VkCommandPoolCreateInfo pool{};
    pool.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO;
    pool.queueFamilyIndex = c.family;
    vk_check(c.vk.CreateCommandPool(c.device, &pool, nullptr, &c.pool), "vkCreateCommandPool");
    std::array<VkCommandBuffer, fluid_vulkan_lanes> commands{};
    VkCommandBufferAllocateInfo allocate{};
    allocate.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
    allocate.commandPool = c.pool;
    allocate.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    allocate.commandBufferCount = static_cast<std::uint32_t>(commands.size());
    vk_check(c.vk.AllocateCommandBuffers(c.device, &allocate, commands.data()), "vkAllocateCommandBuffers");
    VkFenceCreateInfo fence{};
    fence.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO;
    vk_check(c.vk.CreateFence(c.device, &fence, nullptr, &c.fence), "vkCreateFence");
    if (c.timestamp_bits) {
        VkQueryPoolCreateInfo query{};
        query.sType = VK_STRUCTURE_TYPE_QUERY_POOL_CREATE_INFO;
        query.queryType = VK_QUERY_TYPE_TIMESTAMP;
        query.queryCount = 2 * fluid_vulkan_lanes;
        vk_check(c.vk.CreateQueryPool(c.device, &query, nullptr, &c.queries), "vkCreateQueryPool");
    }
    for (std::uint32_t i = 0; i < fluid_vulkan_lanes; ++i) {
        auto& lane = c.lanes[i];
        lane.command = commands[i];
        c.make_buffer(lane.source, 2 * c.options.buffer_bytes,
            VK_BUFFER_USAGE_TRANSFER_SRC_BIT, VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT);
        c.make_buffer(lane.destination, c.options.buffer_bytes,
            VK_BUFFER_USAGE_TRANSFER_SRC_BIT | VK_BUFFER_USAGE_TRANSFER_DST_BIT,
            VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
        c.make_buffer(lane.readback, c.options.buffer_bytes,
            VK_BUFFER_USAGE_TRANSFER_DST_BIT, VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT);
    }
    c.ring.open();
}

HRESULT WINAPI create(const FluidVulkanOptionsV1* options, FluidVulkanContext** output) {
    if (output) *output = nullptr;
    try {
        error_text[0] = '\0';
        require(options && output, "Missing Vulkan options or output");
        require(options->struct_size == sizeof(*options) && options->abi_version == fluid_vulkan_abi &&
            options->buffer_bytes >= 4 && options->buffer_bytes <= fluid_vulkan_max_bytes &&
            options->buffer_bytes % 4 == 0 && options->max_actions >= 1 && options->max_actions <= 128 &&
            options->allow_control <= 1 && options->require_validation <= 1 && options->use_hardware <= 1,
            "Vulkan options violate the bounded ABI");
        auto context = std::make_unique<FluidVulkanContext>();
        context->options = *options;
        initialize(*context);
        *output = context.release();
        return S_OK;
    } catch (const std::exception& error) {
        record_error(error.what());
        return E_FAIL;
    } catch (...) {
        record_error("Unknown Vulkan initialization failure");
        return E_FAIL;
    }
}

HRESULT WINAPI set_source(FluidVulkanContext* context, std::uint32_t index,
    std::uint32_t slot, const void* data, std::uint64_t bytes) {
    return protect(context, [&](auto& c) {
        require((c.state == State::idle || c.state == State::completed) && index < fluid_vulkan_lanes &&
            slot < 2 && data && bytes == c.options.buffer_bytes && !c.control_processed,
            "Sources must be frozen before authority/recording; invalid source range or state");
        auto& lane = c.lanes[index];
        std::vector<unsigned char> shadow(static_cast<const unsigned char*>(data),
            static_cast<const unsigned char*>(data) + bytes);
        void* mapped{};
        vk_check(c.vk.MapMemory(c.device, lane.source.memory, 0, VK_WHOLE_SIZE, 0, &mapped), "Map source");
        std::memcpy(static_cast<unsigned char*>(mapped) + slot * bytes, shadow.data(), static_cast<size_t>(bytes));
        VkMappedMemoryRange range{};
        range.sType = VK_STRUCTURE_TYPE_MAPPED_MEMORY_RANGE;
        range.memory = lane.source.memory;
        range.size = VK_WHOLE_SIZE;
        // Mapping and flushing the whole dedicated allocation avoids atom-size
        // alignment bugs, including when HOST_COHERENT is unavailable.
        const auto result = c.vk.FlushMappedMemoryRanges(c.device, 1, &range);
        c.vk.UnmapMemory(c.device, lane.source.memory);
        vk_check(result, "Flush source");
        lane.shadow[slot] = std::move(shadow);
        lane.retained_slot = -1;
    });
}

HRESULT WINAPI wait_control(FluidVulkanContext* context, std::uint32_t timeout_ms) {
    return protect(context, [&](auto& c) {
        require(c.options.allow_control && !c.revoked && !c.control_processed && c.registered() &&
            c.state == State::idle && timeout_ms >= 1 && timeout_ms <= 5000,
            "Control requires complete frozen registration, opt-in, and one bounded epoch");
        const auto end = GetTickCount64() + timeout_ms;
        auto* control = c.ring.control;
        while (GetTickCount64() < end) {
            const auto epoch = atomic_read(control->published_epoch);
            if (epoch <= 0) { Sleep(1); continue; }
            const auto expires = atomic_read(control->expires_at_qpc);
            const auto mask = atomic_read(control->action_mask);
            const auto budget = atomic_read(control->action_budget);
            MemoryBarrier();
            if (atomic_read(control->published_epoch) != epoch) continue;
            c.control_processed = true;
            const bool accepted = control->magic == fluid_hook_control_magic &&
                control->abi_version == fluid_hook_control_abi_version &&
                c.policy.accept(epoch, expires, mask, budget, c.options.max_actions, ticks(),
                    static_cast<LONG64>(c.ring.header->qpc_frequency), c.registered());
            c.ring.status(accepted ? FluidHookControlStatusV1::accepted : FluidHookControlStatusV1::rejected);
            if (accepted) c.ring.emit(FluidHookEventTypeV1::control_policy_accepted,
                static_cast<std::uint64_t>(epoch), static_cast<std::uint64_t>(mask),
                static_cast<std::uint64_t>(budget), static_cast<std::uint64_t>(expires));
            InterlockedExchange64(&control->acknowledged_epoch, epoch);
            require(accepted, "Rejected Vulkan control policy");
            return;
        }
        c.control_processed = true;
        c.revoked = true;
        throw std::runtime_error("Vulkan control wait timed out; no action authorized");
    });
}

HRESULT WINAPI begin(FluidVulkanContext* context) {
    return protect(context, [](auto& c) {
        require((c.state == State::idle || c.state == State::completed) && c.registered(),
            "Begin requires frozen sources and no pending submission");
        const bool reset = c.state == State::completed;
        c.state = State::failed;
        vk_check(c.vk.ResetCommandPool(c.device, c.pool, 0), "vkResetCommandPool");
        vk_check(c.vk.ResetFences(c.device, 1, &c.fence), "vkResetFences");
        for (std::uint32_t i = 0; i < fluid_vulkan_lanes; ++i) {
            auto& lane = c.lanes[i];
            lane.retained_slot = -1;
            lane.initialized = false;
            ++lane.generation;
            if (reset) c.ring.emit(FluidHookEventTypeV1::transfer_scope_reset,
                i + 1, 0, 0, lane.generation, 0, i + 1);
            VkCommandBufferBeginInfo begin{};
            begin.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
            begin.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
            vk_check(c.vk.BeginCommandBuffer(lane.command, &begin), "vkBeginCommandBuffer");
            if (c.queries) {
                c.vk.CmdResetQueryPool(lane.command, c.queries, 2 * i, 2);
                c.vk.CmdWriteTimestamp(lane.command, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, c.queries, 2 * i);
            }
            c.barrier(lane.command, VK_ACCESS_HOST_WRITE_BIT, VK_ACCESS_TRANSFER_READ_BIT,
                VK_PIPELINE_STAGE_HOST_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
        }
        c.state = State::recording;
    });
}

HRESULT WINAPI copy(FluidVulkanContext* context, std::uint32_t index, std::uint32_t slot) {
    return protect(context, [&](auto& c) {
        auto& lane = c.recording_lane(index);
        require(slot < 2, "Invalid Vulkan source slot");
        std::uint32_t flags = fluid_hook_event_flag_upload_transfer |
            fluid_hook_event_flag_immutable_upload_source;
        bool candidate = false;
        if (lane.retained_slot >= 0) {
            ++c.stats.comparisons;
            flags |= fluid_hook_event_flag_content_compared;
            candidate = std::memcmp(lane.shadow[slot].data(),
                lane.shadow[static_cast<size_t>(lane.retained_slot)].data(),
                static_cast<size_t>(c.options.buffer_bytes)) == 0;
        }
        if (candidate) { ++c.stats.candidates; flags |= fluid_hook_event_flag_redundant_candidate; }
        const bool skipped = candidate && c.reserve();
        if (skipped) { ++c.stats.skipped_copies; flags |= fluid_hook_event_flag_copy_skipped; }
        else {
            c.barrier(lane.command, VK_ACCESS_TRANSFER_WRITE_BIT | VK_ACCESS_TRANSFER_READ_BIT,
                VK_ACCESS_TRANSFER_WRITE_BIT | VK_ACCESS_TRANSFER_READ_BIT);
            const VkBufferCopy region{slot * c.options.buffer_bytes, 0, c.options.buffer_bytes};
            c.vk.CmdCopyBuffer(lane.command, lane.source.buffer, lane.destination.buffer, 1, &region);
        }
        lane.retained_slot = static_cast<int>(slot);
        lane.initialized = true;
        ++c.stats.tracked_copies;
        c.ring.emit(FluidHookEventTypeV1::transfer_buffer_copy, 101 + index, 201 + index,
            c.options.buffer_bytes, lane.generation, flags, 1 + index, slot);
    });
}

HRESULT WINAPI fill(FluidVulkanContext* context, std::uint32_t index, std::uint32_t value) {
    return protect(context, [&](auto& c) {
        auto& lane = c.recording_lane(index);
        c.barrier(lane.command, VK_ACCESS_TRANSFER_WRITE_BIT | VK_ACCESS_TRANSFER_READ_BIT,
            VK_ACCESS_TRANSFER_WRITE_BIT);
        c.vk.CmdFillBuffer(lane.command, lane.destination.buffer, 0, c.options.buffer_bytes, value);
        c.invalidate_lane(index, false);
        lane.initialized = true;
    });
}
HRESULT WINAPI invalidate(FluidVulkanContext* context, std::uint32_t index) {
    return protect(context, [&](auto& c) { c.invalidate_lane(index, true); });
}

HRESULT WINAPI submit(FluidVulkanContext* context, std::uint32_t timeout_ms) {
    return protect(context, [&](auto& c) {
        require(timeout_ms >= 1 && timeout_ms <= 30000 &&
            (c.state == State::recording || c.state == State::pending), "Invalid submit state or timeout");
        if (c.state == State::recording) {
            for (const auto& lane : c.lanes) require(lane.initialized, "All lanes need initialized content before readback");
            c.state = State::failed;
            std::array<VkCommandBuffer, fluid_vulkan_lanes> commands{};
            for (std::uint32_t i = 0; i < fluid_vulkan_lanes; ++i) {
                auto& lane = c.lanes[i];
                c.barrier(lane.command, VK_ACCESS_TRANSFER_WRITE_BIT,
                    VK_ACCESS_TRANSFER_READ_BIT | VK_ACCESS_TRANSFER_WRITE_BIT);
                const VkBufferCopy region{0, 0, c.options.buffer_bytes};
                c.vk.CmdCopyBuffer(lane.command, lane.destination.buffer, lane.readback.buffer, 1, &region);
                c.barrier(lane.command, VK_ACCESS_TRANSFER_WRITE_BIT, VK_ACCESS_HOST_READ_BIT,
                    VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_HOST_BIT);
                if (c.queries) c.vk.CmdWriteTimestamp(lane.command,
                    VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT, c.queries, 2 * i + 1);
                vk_check(c.vk.EndCommandBuffer(lane.command), "vkEndCommandBuffer");
                lane.retained_slot = -1;
                c.ring.emit(FluidHookEventTypeV1::transfer_scope_close,
                    i + 1, 0, 0, lane.generation, 0, i + 1);
                commands[i] = lane.command;
            }
            VkSubmitInfo submission{};
            submission.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
            submission.commandBufferCount = static_cast<std::uint32_t>(commands.size());
            submission.pCommandBuffers = commands.data();
            c.submitted_at = ticks();
            vk_check(c.vk.QueueSubmit(c.queue, 1, &submission, c.fence), "vkQueueSubmit");
            c.state = State::pending;
            ++c.stats.submissions;
            c.ring.emit(FluidHookEventTypeV1::transfer_queue_submit, 1, 0,
                fluid_vulkan_lanes, c.stats.submissions);
        }
        const auto result = c.vk.WaitForFences(c.device, 1, &c.fence, VK_TRUE,
            static_cast<std::uint64_t>(timeout_ms) * 1000000);
        if (result == VK_ERROR_DEVICE_LOST) c.state = State::failed;
        vk_check(result, "vkWaitForFences (pending contexts may only wait again, never reset/destroy)");
        c.state = State::completed;
        ++c.stats.completed_submissions;
        c.stats.submit_to_fence_us = static_cast<double>(ticks() - c.submitted_at) * 1e6 /
            static_cast<double>(c.ring.header->qpc_frequency);
        c.ring.emit(FluidHookEventTypeV1::transfer_sync_signal, 1, 1, c.stats.completed_submissions,
            c.stats.submissions);
        c.stats.gpu_us = 0;
        c.stats.gpu_timestamp_valid = 0;
        if (c.queries) {
            std::array<std::uint64_t, 2 * fluid_vulkan_lanes> timestamps{};
            vk_check(c.vk.GetQueryPoolResults(c.device, c.queries, 0,
                static_cast<std::uint32_t>(timestamps.size()), sizeof(timestamps),
                timestamps.data(), sizeof(std::uint64_t), VK_QUERY_RESULT_64_BIT), "Read GPU timestamps");
            const auto mask = c.timestamp_bits >= 64 ? UINT64_MAX : (1ULL << c.timestamp_bits) - 1;
            for (std::uint32_t i = 0; i < fluid_vulkan_lanes; ++i) {
                c.stats.gpu_us += static_cast<double>((timestamps[2 * i + 1] - timestamps[2 * i]) & mask) *
                    static_cast<double>(c.properties.limits.timestampPeriod) / 1000.0;
            }
            const auto wrap_us = std::ldexp(1.0, static_cast<int>(c.timestamp_bits)) *
                static_cast<double>(c.properties.limits.timestampPeriod) / 1000.0;
            // Multiple wraps cannot be reconstructed from two timestamps.
            // A host interval longer than one counter period is not evidence.
            c.stats.gpu_timestamp_valid = c.stats.submit_to_fence_us < wrap_us ? 1U : 0U;
            if (!c.stats.gpu_timestamp_valid) c.stats.gpu_us = 0;
        }
    });
}

HRESULT WINAPI readback(FluidVulkanContext* context, std::uint32_t index,
    void* output, std::uint64_t bytes) {
    return protect(context, [&](auto& c) {
        require(c.state == State::completed && index < fluid_vulkan_lanes && output &&
            bytes == c.options.buffer_bytes, "Readback requires a completed fence and exact output size");
        auto& lane = c.lanes[index];
        void* mapped{};
        vk_check(c.vk.MapMemory(c.device, lane.readback.memory, 0, VK_WHOLE_SIZE, 0, &mapped), "Map readback");
        VkMappedMemoryRange range{};
        range.sType = VK_STRUCTURE_TYPE_MAPPED_MEMORY_RANGE;
        range.memory = lane.readback.memory;
        range.size = VK_WHOLE_SIZE;
        const auto result = c.vk.InvalidateMappedMemoryRanges(c.device, 1, &range);
        if (result == VK_SUCCESS) std::memcpy(output, mapped, static_cast<size_t>(bytes));
        c.vk.UnmapMemory(c.device, lane.readback.memory);
        vk_check(result, "Invalidate readback");
    });
}
HRESULT WINAPI revoke(FluidVulkanContext* context) {
    return protect(context, [](auto& c) {
        c.revoked = true;
        c.policy.enabled = false;
        for (auto& lane : c.lanes) lane.retained_slot = -1;
    });
}
HRESULT WINAPI snapshot(FluidVulkanContext* context, FluidVulkanSnapshotV1* output) {
    return protect(context, [&](auto& c) {
        require(output && output->struct_size == sizeof(*output) && output->abi_version == fluid_vulkan_abi,
            "Invalid Vulkan snapshot ABI");
        auto result = c.stats;
        result.struct_size = sizeof(result);
        result.abi_version = fluid_vulkan_abi;
        result.events = static_cast<std::uint64_t>(atomic_read(c.ring.header->next_sequence));
        result.overruns = static_cast<std::uint64_t>(atomic_read(c.ring.header->overrun_count));
        result.policy_epoch = static_cast<std::uint64_t>(c.policy.epoch);
        result.policy_status = static_cast<std::uint64_t>(atomic_read(c.ring.control->status));
        result.applied_actions = c.policy.applied;
        result.validation_errors = c.validation_errors.load();
        result.validation_warnings = c.validation_warnings.load();
        result.loader_messages = c.loader_messages.load();
        result.timestamp_valid_bits = c.timestamp_bits;
        result.api_version = c.properties.apiVersion;
        result.device_type = c.properties.deviceType;
        result.validation_enabled = c.options.require_validation;
        result.upload_memory_flags = c.lanes[0].source.flags;
        result.device_memory_flags = c.lanes[0].destination.flags;
        result.readback_memory_flags = c.lanes[0].readback.flags;
        std::memcpy(result.device_name, c.properties.deviceName, sizeof(result.device_name));
        *output = result;
    });
}
HRESULT WINAPI destroy(FluidVulkanContext* context, FluidVulkanSnapshotV1* final_snapshot) {
    const auto result = protect(context, [&](auto& c) {
        require(c.state != State::pending, "Cannot destroy pending Vulkan work; wait for its fence first");
        require(!final_snapshot || (final_snapshot->struct_size == sizeof(*final_snapshot) &&
            final_snapshot->abi_version == fluid_vulkan_abi), "Invalid final snapshot ABI");
        c.cleanup();
        if (final_snapshot) snapshot(&c, final_snapshot);
    });
    if (SUCCEEDED(result)) delete context;
    return result;
}
const char* WINAPI last_error() { return error_text.data(); }
}

extern "C" __declspec(dllexport) HRESULT WINAPI FluidVulkanGetApi(FluidVulkanApiV1* api) {
    if (!api || api->struct_size != sizeof(*api) || api->abi_version != fluid_vulkan_abi) return E_INVALIDARG;
    *api = {sizeof(*api), fluid_vulkan_abi, create, set_source, wait_control, begin,
        copy, fill, invalidate, submit, readback, revoke, snapshot, destroy, last_error};
    return S_OK;
}
