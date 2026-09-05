#pragma once

#include <windows.h>
#include <cstdint>

// Cooperative ABI: all Vulkan objects stay private to the context. Calls must
// come from its creating thread; destroy only after submit has completed.
struct FluidVulkanContext;
constexpr std::uint32_t fluid_vulkan_abi = 1;
constexpr std::uint32_t fluid_vulkan_lanes = 2;
constexpr std::uint64_t fluid_vulkan_max_bytes = 4ULL * 1024 * 1024;

struct FluidVulkanOptionsV1 {
    std::uint32_t struct_size;
    std::uint32_t abi_version;
    std::uint64_t buffer_bytes;
    std::uint32_t max_actions;
    std::uint32_t allow_control;
    std::uint32_t require_validation;
    std::uint32_t use_hardware;
};

struct FluidVulkanSnapshotV1 {
    std::uint32_t struct_size;
    std::uint32_t abi_version;
    std::uint64_t tracked_copies;
    std::uint64_t candidates;
    std::uint64_t skipped_copies;
    std::uint64_t comparisons;
    std::uint64_t invalidations;
    std::uint64_t submissions;
    std::uint64_t completed_submissions;
    std::uint64_t events;
    std::uint64_t overruns;
    std::uint64_t policy_epoch;
    std::uint64_t policy_status;
    std::uint64_t applied_actions;
    std::uint64_t validation_errors;
    std::uint64_t validation_warnings;
    std::uint64_t loader_messages;
    double submit_to_fence_us;
    double gpu_us;
    std::uint32_t gpu_timestamp_valid;
    std::uint32_t timestamp_valid_bits;
    std::uint32_t api_version;
    std::uint32_t device_type;
    std::uint32_t validation_enabled;
    std::uint32_t upload_memory_flags;
    std::uint32_t device_memory_flags;
    std::uint32_t readback_memory_flags;
    char device_name[256];
};

struct FluidVulkanApiV1 {
    std::uint32_t struct_size;
    std::uint32_t abi_version;
    HRESULT (WINAPI* create)(const FluidVulkanOptionsV1*, FluidVulkanContext**);
    HRESULT (WINAPI* set_source)(FluidVulkanContext*, std::uint32_t lane,
        std::uint32_t slot, const void*, std::uint64_t bytes);
    HRESULT (WINAPI* wait_control)(FluidVulkanContext*, std::uint32_t timeout_ms);
    HRESULT (WINAPI* begin)(FluidVulkanContext*);
    HRESULT (WINAPI* copy)(FluidVulkanContext*, std::uint32_t lane, std::uint32_t slot);
    HRESULT (WINAPI* fill)(FluidVulkanContext*, std::uint32_t lane, std::uint32_t value);
    HRESULT (WINAPI* invalidate)(FluidVulkanContext*, std::uint32_t lane);
    HRESULT (WINAPI* submit)(FluidVulkanContext*, std::uint32_t timeout_ms);
    HRESULT (WINAPI* readback)(FluidVulkanContext*, std::uint32_t lane,
        void*, std::uint64_t bytes);
    HRESULT (WINAPI* revoke)(FluidVulkanContext*);
    HRESULT (WINAPI* snapshot)(FluidVulkanContext*, FluidVulkanSnapshotV1*);
    HRESULT (WINAPI* destroy)(FluidVulkanContext*, FluidVulkanSnapshotV1* final_snapshot);
    const char* (WINAPI* last_error)();
};

static_assert(sizeof(FluidVulkanOptionsV1) == 32);
static_assert(sizeof(FluidVulkanSnapshotV1) == 432);
using FluidVulkanGetApiFunction = HRESULT (WINAPI*)(FluidVulkanApiV1*);
