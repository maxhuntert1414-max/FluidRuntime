#pragma once
#include <windows.h>
#include <cstdint>

namespace fluid::observation {
constexpr std::uint32_t magic = 0x4f564746;
constexpr std::uint32_t version = 1;
enum Counter : unsigned {
    instances, devices, allocations, frees, allocation_bytes, live_bytes, peak_bytes,
    host_visible_bytes, device_local_bytes, maps, flushes, invalidates, buffer_binds,
    buffer_copies, buffer_copy_bytes, buffer_image_copies, fills, barriers, submits,
    presents, api_errors, untracked_allocations, active_instances, active_devices,
    intercepted_calls, unmaps, image_binds, queue_waits, fence_waits, copy2_calls,
    submit2_calls, telemetry_failures, counter_count
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
static_assert(sizeof(Shared) == 288);
}
