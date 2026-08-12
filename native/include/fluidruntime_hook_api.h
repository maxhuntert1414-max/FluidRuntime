#pragma once

#include <windows.h>
#include <dxgi.h>

#include <cstdint>

struct ID3D11Resource;

constexpr std::uint32_t fluid_hook_snapshot_abi_version = 12;
constexpr std::uint32_t fluid_hook_attach_options_abi_version = 3;
constexpr std::uint32_t fluid_hook_ring_magic = 0x47524C46;
constexpr std::uint32_t fluid_hook_ring_abi_version = 9;
constexpr std::uint32_t fluid_hook_control_magic = 0x4C544346;
constexpr std::uint32_t fluid_hook_control_abi_version = 1;
constexpr std::uint32_t fluid_hook_ring_capacity = 2048;
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
    resource_retire = 9,
    resource_reuse = 10,
    resource_destroy = 11,
    copy_subresource_region = 12,
    clear_render_target_view = 13,
    clear_unordered_access_view_float = 14,
    control_policy_accepted = 15,
    map_read = 16,
    transfer_buffer_copy = 17,
    transfer_resource_invalidate = 18,
    transfer_scope_close = 19,
    transfer_scope_reset = 20,
    transfer_queue_submit = 21,
    transfer_sync_signal = 22,
    d3d12_copy_buffer_region = 17,
    d3d12_resource_invalidate = 18,
    d3d12_command_list_close = 19,
    d3d12_command_list_reset = 20,
    d3d12_queue_execute = 21,
    d3d12_queue_signal = 22,
};

constexpr std::uint32_t fluid_hook_event_flag_redundant_candidate = 1;
constexpr std::uint32_t fluid_hook_event_flag_copy_skipped = 2;
constexpr std::uint32_t fluid_hook_event_flag_reuse_without_retire = 4;
constexpr std::uint32_t fluid_hook_event_flag_precise_subresource_write = 8;
constexpr std::uint32_t fluid_hook_event_flag_readback_transfer = 16;
constexpr std::uint32_t fluid_hook_event_flag_upload_transfer = 32;
constexpr std::uint32_t fluid_hook_event_flag_content_compared = 64;
constexpr std::uint32_t fluid_hook_event_flag_immutable_upload_source = 128;
constexpr std::uint32_t fluid_hook_event_flag_explicit_invalidation = 256;
constexpr std::uint32_t fluid_hook_event_flag_generalized_transfer = 512;
constexpr std::uint32_t fluid_hook_attach_flag_skip_first_redundant_copy = 1;
constexpr std::uint32_t fluid_hook_attach_flag_track_resource_lifetime = 2;
constexpr std::uint32_t fluid_hook_attach_flag_allow_control_policy = 4;
constexpr std::uint32_t fluid_hook_attach_flag_track_update_subresource_content = 8;
constexpr std::uint64_t fluid_hook_control_action_skip_redundant_copy_resource = 1;
constexpr std::uint64_t fluid_hook_control_action_skip_redundant_readback_copy = 2;
constexpr std::uint64_t fluid_hook_control_action_skip_redundant_upload_copy = 4;
constexpr std::uint64_t
    fluid_hook_control_action_skip_redundant_update_subresource = 8;
constexpr std::uint64_t
    fluid_hook_control_action_skip_redundant_transfer_buffer_copy = 16;
constexpr std::uint64_t
    fluid_hook_control_action_skip_redundant_d3d12_copy_buffer_region =
        fluid_hook_control_action_skip_redundant_transfer_buffer_copy;
constexpr std::uint64_t fluid_hook_control_max_action_budget = 128;

enum class FluidHookControlStatusV1 : std::uint64_t {
    none = 0,
    accepted = 1,
    rejected = 2,
    expired = 3,
    exhausted = 4,
};

struct FluidHookAttachOptionsV1 {
    std::uint32_t struct_size;
    std::uint32_t abi_version;
    std::uint32_t flags;
    std::uint32_t max_skipped_copy_count;
    std::uint64_t max_tracked_update_subresource_bytes;
    std::uint32_t max_tracked_update_subresource_resources;
    std::uint32_t reserved;
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
    std::uint32_t subresource_a;
    std::uint32_t subresource_b;
    std::uint32_t reserved;
    std::uint64_t region_key;
};

struct alignas(64) FluidHookControlBlockV1 {
    std::uint32_t magic;
    std::uint32_t abi_version;
    volatile LONG64 published_epoch;
    volatile LONG64 acknowledged_epoch;
    volatile LONG64 expires_at_qpc;
    volatile LONG64 action_mask;
    volatile LONG64 action_budget;
    volatile LONG64 applied_action_count;
    volatile LONG64 status;
};

static_assert(sizeof(FluidHookRingHeaderV1) == 64);
static_assert(sizeof(FluidHookControlBlockV1) == 64);
static_assert(sizeof(FluidHookEventV1) == 80);
static_assert(sizeof(FluidHookAttachOptionsV1) == 32);

constexpr std::uint64_t fluid_hook_ring_mapping_size =
    sizeof(FluidHookRingHeaderV1) + sizeof(FluidHookControlBlockV1) +
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
    std::uint64_t resource_retire_count;
    std::uint64_t resource_reuse_count;
    std::uint64_t retired_resource_identity_count;
    std::uint64_t provenance_failure_count;
    std::uint64_t resource_destroy_count;
    std::uint64_t release_hook_slot_count;
    std::uint64_t release_hook_failure_count;
    std::uint64_t automatic_lifetime_tracking;
    std::uint64_t copy_subresource_region_count;
    std::uint64_t copy_subresource_region_bytes_estimated;
    std::uint64_t redundant_subresource_copy_candidate_count;
    std::uint64_t redundant_subresource_copy_bytes_estimated;
    std::uint64_t clear_render_target_view_count;
    std::uint64_t clear_unordered_access_view_float_count;
    std::uint64_t gpu_view_write_bytes_estimated;
    std::uint64_t control_policy_enabled;
    std::uint64_t control_policy_epoch;
    std::uint64_t control_policy_acknowledged_epoch;
    std::uint64_t control_policy_applied_action_count;
    std::uint64_t control_policy_rejected_count;
    std::uint64_t control_policy_status;
    std::uint64_t map_read_count;
    std::uint64_t map_read_bytes_estimated;
    std::uint64_t readback_copy_count;
    std::uint64_t readback_copy_bytes_estimated;
    std::uint64_t skipped_readback_copy_count;
    std::uint64_t skipped_readback_copy_bytes_estimated;
    std::uint64_t upload_copy_count;
    std::uint64_t upload_copy_bytes_estimated;
    std::uint64_t skipped_upload_copy_count;
    std::uint64_t skipped_upload_copy_bytes_estimated;
    std::uint64_t update_subresource_bytes_estimated;
    std::uint64_t tracked_update_subresource_count;
    std::uint64_t tracked_update_subresource_bytes_estimated;
    std::uint64_t redundant_update_subresource_candidate_count;
    std::uint64_t redundant_update_subresource_bytes_estimated;
    std::uint64_t forwarded_update_subresource_count;
    std::uint64_t forwarded_update_subresource_bytes_estimated;
    std::uint64_t skipped_update_subresource_count;
    std::uint64_t skipped_update_subresource_bytes_estimated;
    std::uint64_t update_content_cache_resource_count;
    std::uint64_t update_content_cache_bytes;
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
FLUID_HOOK_API HRESULT WINAPI FluidHookWaitForControlPolicy(DWORD timeout_ms);
FLUID_HOOK_API HRESULT WINAPI FluidHookRetireResource(ID3D11Resource* resource);
FLUID_HOOK_API std::uint64_t WINAPI FluidHookPresentCount();
FLUID_HOOK_API BOOL WINAPI FluidHookIsAttached();
FLUID_HOOK_API HRESULT WINAPI FluidHookReadSnapshot(FluidHookSnapshotV1* snapshot);

using FluidHookAttachFunction = HRESULT(WINAPI*)(IDXGISwapChain*);
using FluidHookAttachExFunction = HRESULT(WINAPI*)(
    IDXGISwapChain*,
    const FluidHookAttachOptionsV1*);
using FluidHookDetachFunction = HRESULT(WINAPI*)();
using FluidHookRefreshFunction = HRESULT(WINAPI*)();
using FluidHookWaitForControlPolicyFunction = HRESULT(WINAPI*)(DWORD);
using FluidHookRetireResourceFunction = HRESULT(WINAPI*)(ID3D11Resource*);
using FluidHookPresentCountFunction = std::uint64_t(WINAPI*)();
using FluidHookIsAttachedFunction = BOOL(WINAPI*)();
using FluidHookReadSnapshotFunction = HRESULT(WINAPI*)(FluidHookSnapshotV1*);

#undef FLUID_HOOK_API
