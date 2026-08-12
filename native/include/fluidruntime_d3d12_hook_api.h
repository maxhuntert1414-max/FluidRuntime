#pragma once

#include "fluidruntime_hook_api.h"
#include "fluidruntime_transfer_api.h"

#include <d3d12.h>

constexpr std::uint32_t fluid_d3d12_hook_snapshot_abi_version = 1;
constexpr std::uint32_t fluid_d3d12_hook_attach_options_abi_version = 1;
constexpr std::uint32_t fluid_d3d12_hook_snapshot_v2_abi_version = 2;
constexpr std::uint32_t fluid_d3d12_hook_attach_options_v2_abi_version = 2;
constexpr wchar_t fluid_d3d12_hook_ring_name_prefix[] =
    L"Local\\FluidRuntimeD3D12Hook-";

constexpr std::uint32_t fluid_d3d12_hook_attach_flag_allow_control_policy = 1;
constexpr std::uint64_t fluid_d3d12_hook_max_tracked_bytes =
    4ULL * 1024ULL * 1024ULL;
constexpr std::uint32_t fluid_d3d12_hook_max_tracked_resources = 1;

struct FluidD3D12HookAttachOptionsV1 {
    std::uint32_t struct_size;
    std::uint32_t abi_version;
    std::uint32_t flags;
    std::uint32_t reserved0;
    std::uint64_t max_tracked_copy_bytes;
    std::uint32_t max_tracked_resources;
    std::uint32_t reserved1;
};

struct FluidD3D12HookSnapshotV1 {
    std::uint32_t struct_size;
    std::uint32_t abi_version;
    std::uint64_t attached;
    std::uint64_t command_list_identity;
    std::uint64_t upload_resource_identity;
    std::uint64_t source_snapshot_bytes;
    std::uint64_t destination_resource_identity;
    std::uint64_t tracked_resource_bytes;
    std::uint64_t copy_buffer_region_count;
    std::uint64_t tracked_copy_count;
    std::uint64_t tracked_copy_bytes;
    std::uint64_t redundant_candidate_count;
    std::uint64_t redundant_candidate_bytes;
    std::uint64_t forwarded_copy_count;
    std::uint64_t forwarded_copy_bytes;
    std::uint64_t skipped_copy_count;
    std::uint64_t skipped_copy_bytes;
    std::uint64_t exact_comparison_count;
    std::uint64_t exact_comparison_bytes;
    std::uint64_t source_registration_count;
    std::uint64_t destination_registration_count;
    std::uint64_t automatic_invalidation_count;
    std::uint64_t explicit_invalidation_count;
    std::uint64_t command_list_close_count;
    std::uint64_t command_list_reset_count;
    std::uint64_t cache_generation;
    std::uint64_t cache_bytes;
    std::uint64_t control_policy_enabled;
    std::uint64_t control_policy_epoch;
    std::uint64_t control_policy_acknowledged_epoch;
    std::uint64_t control_policy_applied_action_count;
    std::uint64_t control_policy_rejected_count;
    std::uint64_t control_policy_status;
    std::uint64_t ipc_event_count;
    std::uint64_t ipc_overrun_count;
    std::uint64_t active_hook_call_count;
};

struct FluidD3D12CommandScopeV2 {
    ID3D12GraphicsCommandList* command_list;
    std::uint64_t scope_id;
};

struct FluidD3D12HookAttachOptionsV2 {
    std::uint32_t struct_size;
    std::uint32_t abi_version;
    std::uint32_t flags;
    std::uint32_t reserved0;
    FluidTransferTopologyV1 topology;
    std::uint64_t queue_id;
    std::uint64_t reserved1;
};

struct FluidD3D12HookSnapshotV2 {
    std::uint32_t struct_size;
    std::uint32_t abi_version;
    std::uint64_t attached;
    std::uint64_t queue_identity;
    std::uint64_t queue_id;
    std::uint64_t execution_scope_count;
    std::uint64_t source_resource_count;
    std::uint64_t destination_resource_count;
    std::uint64_t lane_count;
    std::uint64_t fence_count;
    std::uint64_t source_snapshot_bytes;
    std::uint64_t retained_capacity_bytes;
    std::uint64_t valid_lane_count;
    std::uint64_t maximum_lane_generation;
    std::uint64_t copy_buffer_region_count;
    std::uint64_t tracked_copy_count;
    std::uint64_t tracked_copy_bytes;
    std::uint64_t redundant_candidate_count;
    std::uint64_t redundant_candidate_bytes;
    std::uint64_t forwarded_copy_count;
    std::uint64_t forwarded_copy_bytes;
    std::uint64_t skipped_copy_count;
    std::uint64_t skipped_copy_bytes;
    std::uint64_t exact_comparison_count;
    std::uint64_t exact_comparison_bytes;
    std::uint64_t source_registration_count;
    std::uint64_t destination_registration_count;
    std::uint64_t lane_registration_count;
    std::uint64_t fence_registration_count;
    std::uint64_t automatic_invalidation_count;
    std::uint64_t explicit_invalidation_count;
    std::uint64_t command_list_close_count;
    std::uint64_t command_list_reset_count;
    std::uint64_t queue_execute_count;
    std::uint64_t queue_signal_count;
    std::uint64_t submitted_scope_count;
    std::uint64_t unregistered_submitted_scope_count;
    std::uint64_t last_submission_scope_hash;
    std::uint64_t last_signaled_fence_id;
    std::uint64_t last_signaled_fence_value;
    std::uint64_t control_policy_enabled;
    std::uint64_t control_policy_epoch;
    std::uint64_t control_policy_acknowledged_epoch;
    std::uint64_t control_policy_applied_action_count;
    std::uint64_t control_policy_rejected_count;
    std::uint64_t control_policy_status;
    std::uint64_t ipc_event_count;
    std::uint64_t ipc_overrun_count;
    std::uint64_t active_hook_call_count;
};

static_assert(sizeof(FluidD3D12HookAttachOptionsV1) == 32);
static_assert(sizeof(FluidD3D12HookSnapshotV1) == 280);
static_assert(sizeof(FluidD3D12CommandScopeV2) == 16);
static_assert(sizeof(FluidD3D12HookAttachOptionsV2) == 96);
static_assert(sizeof(FluidD3D12HookSnapshotV2) == 384);

#ifdef FLUIDRUNTIME_D3D12_HOOK_EXPORTS
#define FLUID_D3D12_HOOK_API extern "C" __declspec(dllexport)
#else
#define FLUID_D3D12_HOOK_API extern "C"
#endif

FLUID_D3D12_HOOK_API HRESULT WINAPI FluidD3D12HookAttachEx(
    ID3D12GraphicsCommandList* command_list,
    const FluidD3D12HookAttachOptionsV1* options);
// The CPU shadow must byte-match the upload range and remain immutable until fence.
FLUID_D3D12_HOOK_API HRESULT WINAPI FluidD3D12HookRegisterUploadBuffer(
    ID3D12Resource* resource,
    const void* immutable_cpu_shadow,
    std::uint64_t shadow_bytes);
FLUID_D3D12_HOOK_API HRESULT WINAPI FluidD3D12HookRegisterCopyOnlyBuffer(
    ID3D12Resource* resource,
    std::uint64_t resource_bytes);
FLUID_D3D12_HOOK_API HRESULT WINAPI FluidD3D12HookInvalidateResource(
    ID3D12Resource* resource);
FLUID_D3D12_HOOK_API HRESULT WINAPI FluidD3D12HookWaitForControlPolicy(
    DWORD timeout_ms);
FLUID_D3D12_HOOK_API HRESULT WINAPI FluidD3D12HookDetach();
FLUID_D3D12_HOOK_API BOOL WINAPI FluidD3D12HookIsAttached();
FLUID_D3D12_HOOK_API HRESULT WINAPI FluidD3D12HookReadSnapshot(
    FluidD3D12HookSnapshotV1* snapshot);

FLUID_D3D12_HOOK_API HRESULT WINAPI FluidD3D12HookAttachV2(
    ID3D12CommandQueue* queue,
    const FluidD3D12CommandScopeV2* scopes,
    std::uint32_t scope_count,
    const FluidD3D12HookAttachOptionsV2* options);
FLUID_D3D12_HOOK_API HRESULT WINAPI FluidD3D12HookRegisterUploadBufferV2(
    ID3D12Resource* resource,
    std::uint64_t resource_id,
    const void* immutable_cpu_shadow,
    std::uint64_t shadow_bytes);
FLUID_D3D12_HOOK_API HRESULT WINAPI FluidD3D12HookRegisterCopyOnlyBufferV2(
    ID3D12Resource* resource,
    std::uint64_t resource_id,
    std::uint64_t resource_bytes);
FLUID_D3D12_HOOK_API HRESULT WINAPI FluidD3D12HookRegisterCopyLaneV2(
    std::uint64_t scope_id,
    std::uint64_t destination_resource_id);
FLUID_D3D12_HOOK_API HRESULT WINAPI FluidD3D12HookRegisterFenceV2(
    ID3D12Fence* fence,
    std::uint64_t fence_id);
FLUID_D3D12_HOOK_API HRESULT WINAPI FluidD3D12HookInvalidateResourceV2(
    std::uint64_t destination_resource_id);
FLUID_D3D12_HOOK_API HRESULT WINAPI FluidD3D12HookReadSnapshotV2(
    FluidD3D12HookSnapshotV2* snapshot);

using FluidD3D12HookAttachExFunction = HRESULT(WINAPI*)(
    ID3D12GraphicsCommandList*,
    const FluidD3D12HookAttachOptionsV1*);
using FluidD3D12HookRegisterUploadBufferFunction = HRESULT(WINAPI*)(
    ID3D12Resource*,
    const void*,
    std::uint64_t);
using FluidD3D12HookRegisterCopyOnlyBufferFunction = HRESULT(WINAPI*)(
    ID3D12Resource*,
    std::uint64_t);
using FluidD3D12HookInvalidateResourceFunction = HRESULT(WINAPI*)(
    ID3D12Resource*);
using FluidD3D12HookWaitForControlPolicyFunction = HRESULT(WINAPI*)(DWORD);
using FluidD3D12HookDetachFunction = HRESULT(WINAPI*)();
using FluidD3D12HookIsAttachedFunction = BOOL(WINAPI*)();
using FluidD3D12HookReadSnapshotFunction = HRESULT(WINAPI*)(
    FluidD3D12HookSnapshotV1*);
using FluidD3D12HookAttachV2Function = HRESULT(WINAPI*)(
    ID3D12CommandQueue*,
    const FluidD3D12CommandScopeV2*,
    std::uint32_t,
    const FluidD3D12HookAttachOptionsV2*);
using FluidD3D12HookRegisterUploadBufferV2Function = HRESULT(WINAPI*)(
    ID3D12Resource*,
    std::uint64_t,
    const void*,
    std::uint64_t);
using FluidD3D12HookRegisterCopyOnlyBufferV2Function = HRESULT(WINAPI*)(
    ID3D12Resource*,
    std::uint64_t,
    std::uint64_t);
using FluidD3D12HookRegisterCopyLaneV2Function = HRESULT(WINAPI*)(
    std::uint64_t,
    std::uint64_t);
using FluidD3D12HookRegisterFenceV2Function = HRESULT(WINAPI*)(
    ID3D12Fence*,
    std::uint64_t);
using FluidD3D12HookInvalidateResourceV2Function = HRESULT(WINAPI*)(
    std::uint64_t);
using FluidD3D12HookReadSnapshotV2Function = HRESULT(WINAPI*)(
    FluidD3D12HookSnapshotV2*);

#undef FLUID_D3D12_HOOK_API
