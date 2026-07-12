#pragma once

#include <windows.h>
#include <dxgi.h>

#include <cstdint>

constexpr std::uint32_t fluid_hook_snapshot_abi_version = 4;
constexpr std::uint32_t fluid_hook_attach_options_abi_version = 1;
constexpr std::uint32_t fluid_hook_ring_magic = 0x47524C46;
constexpr std::uint32_t fluid_hook_ring_abi_version = 1;
constexpr std::uint32_t fluid_hook_ring_capacity = 1024;
constexpr wchar_t fluid_hook_ring_name_prefix[] = L"Local\\FluidRuntimeHook-";

enum class FluidHookEventTypeV1 : std::uint32_t {
    present = 1,
    create_buffer = 2,
    create_texture2d = 3,
    map_write = 4,
    unmap_write = 5,
    update_subresource = 6,
    copy_resource = 7,
    hook_refresh = 8,
};

constexpr std::uint32_t fluid_hook_event_flag_redundant_candidate = 1;
constexpr std::uint32_t fluid_hook_event_flag_copy_skipped = 2;
constexpr std::uint32_t fluid_hook_attach_flag_skip_first_redundant_copy = 1;

struct FluidHookAttachOptionsV1 {
    std::uint32_t struct_size;
    std::uint32_t abi_version;
    std::uint32_t flags;
    std::uint32_t max_skipped_copy_count;
};

struct alignas(64) FluidHookRingHeaderV1 {
    std::uint32_t magic;
    std::uint32_t abi_version;
    std::uint32_t capacity;
    std::uint32_t event_size;
    volatile LONG64 next_sequence;
    volatile LONG64 reader_sequence;
    volatile LONG64 overrun_count;
    std::uint64_t qpc_frequency;
    std::uint64_t process_id;
    std::uint64_t reserved;
};

struct alignas(8) FluidHookEventV1 {
    volatile LONG64 sequence;
    LONG64 qpc_ticks;
    std::uint32_t type;
    std::uint32_t thread_id;
    std::uint64_t resource_a;
    std::uint64_t resource_b;
    std::uint64_t size_bytes;
    std::uint64_t generation;
    std::uint32_t flags;
    std::uint32_t reserved;
};

static_assert(sizeof(FluidHookRingHeaderV1) == 64);
static_assert(sizeof(FluidHookEventV1) == 64);

constexpr std::uint64_t fluid_hook_ring_mapping_size =
    sizeof(FluidHookRingHeaderV1) +
    static_cast<std::uint64_t>(fluid_hook_ring_capacity) * sizeof(FluidHookEventV1);

struct FluidHookSnapshotV1 {
    std::uint32_t struct_size;
    std::uint32_t abi_version;
    std::uint64_t present_count;
    std::uint64_t create_buffer_count;
    std::uint64_t buffer_bytes_requested;
    std::uint64_t create_texture2d_count;
    std::uint64_t texture_bytes_estimated;
    std::uint64_t map_write_count;
    std::uint64_t unmap_write_count;
    std::uint64_t update_subresource_count;
    std::uint64_t copy_resource_count;
    std::uint64_t copy_resource_bytes_estimated;
    std::uint64_t redundant_copy_candidate_count;
    std::uint64_t redundant_copy_bytes_estimated;
    std::uint64_t forwarded_copy_count;
    std::uint64_t forwarded_copy_bytes_estimated;
    std::uint64_t skipped_copy_count;
    std::uint64_t skipped_copy_bytes_estimated;
    std::uint64_t tracked_resource_count;
    std::uint64_t hook_refresh_count;
    std::uint64_t hook_refresh_failure_count;
    std::uint64_t ipc_event_count;
    std::uint64_t ipc_overrun_count;
};

#ifdef FLUIDRUNTIME_HOOK_EXPORTS
#define FLUID_HOOK_API extern "C" __declspec(dllexport)
#else
#define FLUID_HOOK_API extern "C"
#endif

FLUID_HOOK_API HRESULT WINAPI FluidHookAttach(IDXGISwapChain* swap_chain);
FLUID_HOOK_API HRESULT WINAPI FluidHookAttachEx(
    IDXGISwapChain* swap_chain,
    const FluidHookAttachOptionsV1* options);
FLUID_HOOK_API HRESULT WINAPI FluidHookDetach();
FLUID_HOOK_API HRESULT WINAPI FluidHookRefresh();
FLUID_HOOK_API std::uint64_t WINAPI FluidHookPresentCount();
FLUID_HOOK_API BOOL WINAPI FluidHookIsAttached();
FLUID_HOOK_API HRESULT WINAPI FluidHookReadSnapshot(FluidHookSnapshotV1* snapshot);

using FluidHookAttachFunction = HRESULT(WINAPI*)(IDXGISwapChain*);
using FluidHookAttachExFunction = HRESULT(WINAPI*)(
    IDXGISwapChain*,
    const FluidHookAttachOptionsV1*);
using FluidHookDetachFunction = HRESULT(WINAPI*)();
using FluidHookRefreshFunction = HRESULT(WINAPI*)();
using FluidHookPresentCountFunction = std::uint64_t(WINAPI*)();
using FluidHookIsAttachedFunction = BOOL(WINAPI*)();
using FluidHookReadSnapshotFunction = HRESULT(WINAPI*)(FluidHookSnapshotV1*);

#undef FLUID_HOOK_API
