#pragma once
#include <windows.h>
#include <cstdint>

namespace fluid::observation {
constexpr std::uint32_t magic = 0x4f564746;
constexpr std::uint32_t version = 4;
enum Counter : unsigned {
    instances, devices, allocations, frees, allocation_bytes, live_bytes, peak_bytes,
    host_visible_bytes, device_local_bytes, maps, flushes, invalidates, buffer_binds,
    buffer_copies, buffer_copy_bytes, buffer_image_copies, fills, barriers, submits,
    presents, api_errors, untracked_allocations, active_instances, active_devices,
    intercepted_calls, unmaps, image_binds, queue_waits, fence_waits, copy2_calls,
    submit2_calls, telemetry_failures,
    buffers_created, buffers_destroyed, live_buffers, untracked_buffers,
    host_to_device_copy_bytes, device_to_host_copy_bytes, device_to_device_copy_bytes,
    host_to_host_copy_bytes, shared_memory_copy_bytes, unknown_buffer_copy_bytes,
    same_allocation_copy_bytes, buffer_binding_failures, unclassified_buffer_bindings,
    counter_overflows,
    command_buffers_allocated, command_buffers_freed, live_command_buffers, untracked_command_buffers,
    command_buffer_begins, command_buffer_ends, command_buffer_resets, command_pool_resets,
    command_execute_calls, command_tracking_failures, successful_submit_calls, failed_submit_calls,
    submitted_primary_command_buffers, submitted_secondary_command_buffers, resubmitted_command_buffers,
    submitted_buffer_copies, submitted_buffer_copy_bytes, unresolved_submit_calls,
    command_tracking_overflows, untracked_command_pools,
    fences_created, fences_destroyed, live_fences, untracked_fences, fence_resets,
    fence_status_queries, device_waits, ambiguous_fence_waits, completion_tracking_failures,
    completed_submit_calls, completed_buffer_copies, completed_buffer_copy_bytes,
    pending_tracked_submits, abandoned_tracked_submits, counter_count
};
struct alignas(8) Shared {
    std::uint32_t signature;
    std::uint32_t abi;
    std::uint32_t size;
    std::uint32_t count;
    volatile LONG64 process_id;
    volatile LONG64 enabled;
    volatile LONG64 counters[counter_count];
};
static_assert(counter_count == 80);
static_assert(sizeof(Shared) == 672);
}
