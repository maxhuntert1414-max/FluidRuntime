#include "fluidruntime_d3d12_hook_api.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <new>
#include <string>
#include <vector>

namespace {

using CloseFunction = HRESULT(STDMETHODCALLTYPE*)(ID3D12GraphicsCommandList*);
using ResetFunction = HRESULT(STDMETHODCALLTYPE*)(
    ID3D12GraphicsCommandList*,
    ID3D12CommandAllocator*,
    ID3D12PipelineState*);
using CopyBufferRegionFunction = void(STDMETHODCALLTYPE*)(
    ID3D12GraphicsCommandList*,
    ID3D12Resource*,
    UINT64,
    ID3D12Resource*,
    UINT64,
    UINT64);
using CopyTextureRegionFunction = void(STDMETHODCALLTYPE*)(
    ID3D12GraphicsCommandList*,
    const D3D12_TEXTURE_COPY_LOCATION*,
    UINT,
    UINT,
    UINT,
    const D3D12_TEXTURE_COPY_LOCATION*,
    const D3D12_BOX*);
using CopyResourceFunction = void(STDMETHODCALLTYPE*)(
    ID3D12GraphicsCommandList*,
    ID3D12Resource*,
    ID3D12Resource*);
using ExecuteCommandListsFunction = void(STDMETHODCALLTYPE*)(
    ID3D12CommandQueue*,
    UINT,
    ID3D12CommandList* const*);
using SignalFunction = HRESULT(STDMETHODCALLTYPE*)(
    ID3D12CommandQueue*,
    ID3D12Fence*,
    UINT64);

constexpr size_t kCloseVtableIndex = 9;
constexpr size_t kResetVtableIndex = 10;
constexpr size_t kCopyBufferRegionVtableIndex = 15;
constexpr size_t kCopyTextureRegionVtableIndex = 16;
constexpr size_t kCopyResourceVtableIndex = 17;
constexpr size_t kExecuteCommandListsVtableIndex = 10;
constexpr size_t kSignalVtableIndex = 14;
constexpr size_t kHookSlotCount = 7;
constexpr std::uint64_t kMaximumSourceSnapshotBytes =
    4 * fluid_transfer_max_resource_bytes;
constexpr std::uint32_t kGeneralizedFlag =
    fluid_hook_event_flag_generalized_transfer;

struct HookSlot {
    void** slot{};
    void* original{};
    void* hook{};
};

struct ScopeState {
    ID3D12GraphicsCommandList* command_list{};
    std::uint64_t scope_id{};
};

struct SourceState {
    ID3D12Resource* resource{};
    std::uint64_t resource_id{};
    std::vector<std::uint8_t> snapshot;
};

struct DestinationState {
    ID3D12Resource* resource{};
    std::uint64_t resource_id{};
    std::uint64_t bytes{};
};

struct LaneState {
    std::uint64_t scope_id{};
    std::uint64_t destination_resource_id{};
    std::vector<std::uint8_t> retained_content;
    bool valid{};
    std::uint64_t generation{};
};

struct FenceState {
    ID3D12Fence* fence{};
    std::uint64_t fence_id{};
};

std::mutex g_hook_mutex;
std::mutex g_patch_mutex;
std::mutex g_state_mutex;
std::array<HookSlot, kHookSlotCount> g_hook_slots{};
std::atomic<size_t> g_installed_hook_count{0};
std::atomic<bool> g_detaching{false};
std::atomic<unsigned long> g_active_hook_calls{0};
std::atomic<CloseFunction> g_original_close{nullptr};
std::atomic<ResetFunction> g_original_reset{nullptr};
std::atomic<CopyBufferRegionFunction> g_original_copy_buffer_region{nullptr};
std::atomic<CopyTextureRegionFunction> g_original_copy_texture_region{nullptr};
std::atomic<CopyResourceFunction> g_original_copy_resource{nullptr};
std::atomic<ExecuteCommandListsFunction> g_original_execute_command_lists{nullptr};
std::atomic<SignalFunction> g_original_signal{nullptr};

ID3D12CommandQueue* g_queue{};
std::uint64_t g_queue_id{};
FluidTransferTopologyV1 g_topology{};
std::vector<ScopeState> g_scopes;
std::vector<SourceState> g_sources;
std::vector<DestinationState> g_destinations;
std::vector<LaneState> g_lanes;
std::vector<FenceState> g_fences;
bool g_module_pinned{};
bool g_attach_completed_once{};

std::atomic<std::uint64_t> g_copy_buffer_region_count{0};
std::atomic<std::uint64_t> g_tracked_copy_count{0};
std::atomic<std::uint64_t> g_tracked_copy_bytes{0};
std::atomic<std::uint64_t> g_redundant_candidate_count{0};
std::atomic<std::uint64_t> g_redundant_candidate_bytes{0};
std::atomic<std::uint64_t> g_forwarded_copy_count{0};
std::atomic<std::uint64_t> g_forwarded_copy_bytes{0};
std::atomic<std::uint64_t> g_skipped_copy_count{0};
std::atomic<std::uint64_t> g_skipped_copy_bytes{0};
std::atomic<std::uint64_t> g_exact_comparison_count{0};
std::atomic<std::uint64_t> g_exact_comparison_bytes{0};
std::atomic<std::uint64_t> g_source_registration_count{0};
std::atomic<std::uint64_t> g_destination_registration_count{0};
std::atomic<std::uint64_t> g_lane_registration_count{0};
std::atomic<std::uint64_t> g_fence_registration_count{0};
std::atomic<std::uint64_t> g_automatic_invalidation_count{0};
std::atomic<std::uint64_t> g_explicit_invalidation_count{0};
std::atomic<std::uint64_t> g_command_list_close_count{0};
std::atomic<std::uint64_t> g_command_list_reset_count{0};
std::atomic<std::uint64_t> g_queue_execute_count{0};
std::atomic<std::uint64_t> g_queue_signal_count{0};
std::atomic<std::uint64_t> g_submitted_scope_count{0};
std::atomic<std::uint64_t> g_unregistered_submitted_scope_count{0};
std::atomic<std::uint64_t> g_last_submission_scope_hash{0};
std::atomic<std::uint64_t> g_last_signaled_fence_id{0};
std::atomic<std::uint64_t> g_last_signaled_fence_value{0};

std::atomic<bool> g_allow_control_policy{false};
std::atomic<std::uint64_t> g_processed_control_policy_epoch{0};
std::atomic<std::uint64_t> g_active_control_policy_epoch{0};
std::atomic<std::uint64_t> g_control_policy_action_mask{0};
std::atomic<std::uint64_t> g_control_policy_action_budget{0};
std::atomic<std::uint64_t> g_control_policy_expires_at_qpc{0};
std::atomic<std::uint64_t> g_control_policy_applied_action_count{0};
std::atomic<std::uint64_t> g_control_policy_rejected_count{0};
std::atomic<std::uint64_t> g_control_policy_status{0};

HANDLE g_ring_mapping{};
FluidHookRingHeaderV1* g_ring_header{};
FluidHookControlBlockV1* g_control_block{};
FluidHookEventV1* g_ring_events{};

HRESULT STDMETHODCALLTYPE hooked_close(ID3D12GraphicsCommandList* command_list);
HRESULT STDMETHODCALLTYPE hooked_reset(
    ID3D12GraphicsCommandList* command_list,
    ID3D12CommandAllocator* allocator,
    ID3D12PipelineState* initial_state);
void STDMETHODCALLTYPE hooked_copy_buffer_region(
    ID3D12GraphicsCommandList* command_list,
    ID3D12Resource* destination,
    UINT64 destination_offset,
    ID3D12Resource* source,
    UINT64 source_offset,
    UINT64 bytes);
void STDMETHODCALLTYPE hooked_copy_texture_region(
    ID3D12GraphicsCommandList* command_list,
    const D3D12_TEXTURE_COPY_LOCATION* destination,
    UINT destination_x,
    UINT destination_y,
    UINT destination_z,
    const D3D12_TEXTURE_COPY_LOCATION* source,
    const D3D12_BOX* source_box);
void STDMETHODCALLTYPE hooked_copy_resource(
    ID3D12GraphicsCommandList* command_list,
    ID3D12Resource* destination,
    ID3D12Resource* source);
void STDMETHODCALLTYPE hooked_execute_command_lists(
    ID3D12CommandQueue* queue,
    UINT command_list_count,
    ID3D12CommandList* const* command_lists);
HRESULT STDMETHODCALLTYPE hooked_signal(
    ID3D12CommandQueue* queue,
    ID3D12Fence* fence,
    UINT64 value);

class ActiveHookCall {
public:
    ActiveHookCall() {
        g_active_hook_calls.fetch_add(1, std::memory_order_acquire);
        active_ = !g_detaching.load(std::memory_order_acquire) &&
            g_installed_hook_count.load(std::memory_order_acquire) != 0;
    }

    ~ActiveHookCall() {
        g_active_hook_calls.fetch_sub(1, std::memory_order_release);
    }

    ActiveHookCall(const ActiveHookCall&) = delete;
    ActiveHookCall& operator=(const ActiveHookCall&) = delete;

    [[nodiscard]] bool active() const { return active_; }

private:
    bool active_{};
};

std::uint64_t identity(const void* value) {
    return static_cast<std::uint64_t>(reinterpret_cast<std::uintptr_t>(value));
}

bool pin_hook_module() {
    if (g_module_pinned) {
        return true;
    }
    HMODULE module{};
    if (!GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                GET_MODULE_HANDLE_EX_FLAG_PIN,
            reinterpret_cast<LPCWSTR>(&g_hook_mutex),
            &module)) {
        return false;
    }
    g_module_pinned = true;
    return true;
}

bool write_pointer(void** slot, void* value, void* rollback_value) {
    DWORD old_protection{};
    if (!VirtualProtect(
            slot,
            sizeof(void*),
            PAGE_EXECUTE_READWRITE,
            &old_protection)) {
        return false;
    }
    InterlockedExchangePointer(
        reinterpret_cast<PVOID volatile*>(slot),
        value);
    FlushInstructionCache(GetCurrentProcess(), slot, sizeof(void*));
    DWORD ignored{};
    if (VirtualProtect(slot, sizeof(void*), old_protection, &ignored)) {
        return true;
    }
    const auto restore_error = GetLastError();
    InterlockedExchangePointer(
        reinterpret_cast<PVOID volatile*>(slot),
        rollback_value);
    FlushInstructionCache(GetCurrentProcess(), slot, sizeof(void*));
    VirtualProtect(slot, sizeof(void*), old_protection, &ignored);
    SetLastError(restore_error);
    return false;
}

bool initialize_event_ring() {
    const auto mapping_name = std::wstring(fluid_transfer_ring_name_prefix) +
        std::to_wstring(g_topology.backend) + L"-" +
        std::to_wstring(GetCurrentProcessId());
    const auto mapping = CreateFileMappingW(
        INVALID_HANDLE_VALUE,
        nullptr,
        PAGE_READWRITE,
        0,
        static_cast<DWORD>(fluid_hook_ring_mapping_size),
        mapping_name.c_str());
    if (mapping == nullptr) {
        return false;
    }
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        CloseHandle(mapping);
        SetLastError(ERROR_ALREADY_EXISTS);
        return false;
    }
    auto* view = static_cast<unsigned char*>(MapViewOfFile(
        mapping,
        FILE_MAP_ALL_ACCESS,
        0,
        0,
        static_cast<SIZE_T>(fluid_hook_ring_mapping_size)));
    if (view == nullptr) {
        const auto error = GetLastError();
        CloseHandle(mapping);
        SetLastError(error);
        return false;
    }
    ZeroMemory(view, static_cast<SIZE_T>(fluid_hook_ring_mapping_size));
    auto* header = reinterpret_cast<FluidHookRingHeaderV1*>(view);
    auto* control = reinterpret_cast<FluidHookControlBlockV1*>(
        view + sizeof(FluidHookRingHeaderV1));
    auto* events = reinterpret_cast<FluidHookEventV1*>(
        view + sizeof(FluidHookRingHeaderV1) + sizeof(FluidHookControlBlockV1));
    LARGE_INTEGER frequency{};
    QueryPerformanceFrequency(&frequency);
    header->abi_version = fluid_hook_ring_abi_version;
    header->capacity = fluid_hook_ring_capacity;
    header->event_size = sizeof(FluidHookEventV1);
    header->qpc_frequency = static_cast<std::uint64_t>(frequency.QuadPart);
    header->process_id = GetCurrentProcessId();
    header->reserved = g_topology.backend;
    control->magic = fluid_hook_control_magic;
    control->abi_version = fluid_hook_control_abi_version;
    for (std::uint32_t index = 0; index < fluid_hook_ring_capacity; ++index) {
        events[index].sequence = -1;
    }
    MemoryBarrier();
    InterlockedExchange(
        reinterpret_cast<volatile LONG*>(&header->magic),
        static_cast<LONG>(fluid_hook_ring_magic));
    g_ring_mapping = mapping;
    g_ring_header = header;
    g_control_block = control;
    g_ring_events = events;
    return true;
}

void close_event_ring() {
    auto* header = g_ring_header;
    const auto mapping = g_ring_mapping;
    g_ring_header = nullptr;
    g_control_block = nullptr;
    g_ring_events = nullptr;
    g_ring_mapping = nullptr;
    if (header != nullptr) {
        UnmapViewOfFile(header);
    }
    if (mapping != nullptr) {
        CloseHandle(mapping);
    }
}

void emit_hook_event(
    FluidHookEventTypeV1 type,
    std::uint64_t resource_a = 0,
    std::uint64_t resource_b = 0,
    std::uint64_t size_bytes = 0,
    std::uint64_t generation = 0,
    std::uint32_t flags = 0,
    std::uint32_t subresource_a = 0,
    std::uint32_t subresource_b = 0,
    std::uint64_t region_key = 0) {
    auto* header = g_ring_header;
    auto* events = g_ring_events;
    if (header == nullptr || events == nullptr) {
        return;
    }
    const auto sequence = InterlockedIncrement64(&header->next_sequence) - 1;
    const auto reader_sequence = InterlockedCompareExchange64(
        &header->reader_sequence,
        0,
        0);
    if (sequence - reader_sequence >= fluid_hook_ring_capacity) {
        InterlockedIncrement64(&header->overrun_count);
    }
    auto& event = events[static_cast<std::uint64_t>(sequence) % header->capacity];
    InterlockedExchange64(&event.sequence, -1);
    LARGE_INTEGER timestamp{};
    QueryPerformanceCounter(&timestamp);
    event.qpc_ticks = timestamp.QuadPart;
    event.type = static_cast<std::uint32_t>(type);
    event.thread_id = GetCurrentThreadId();
    event.resource_a = resource_a;
    event.resource_b = resource_b;
    event.size_bytes = size_bytes;
    event.generation = generation;
    event.flags = flags;
    event.subresource_a = subresource_a;
    event.subresource_b = subresource_b;
    event.reserved = 0;
    event.region_key = region_key;
    MemoryBarrier();
    InterlockedExchange64(&event.sequence, sequence);
}

void publish_control_status(FluidHookControlStatusV1 status) {
    const auto value = static_cast<std::uint64_t>(status);
    g_control_policy_status.store(value, std::memory_order_release);
    if (g_control_block != nullptr) {
        InterlockedExchange64(
            &g_control_block->status,
            static_cast<LONG64>(value));
    }
}

bool registration_topology_complete_locked() {
    if (g_queue == nullptr ||
        g_scopes.size() != g_topology.execution_scope_count ||
        g_sources.size() != g_topology.source_resource_count ||
        g_destinations.size() != g_topology.destination_resource_count ||
        g_lanes.size() != g_topology.lane_count ||
        g_fences.size() != g_topology.fence_count) {
        return false;
    }
    const auto every_scope_covered = std::all_of(
        g_scopes.begin(),
        g_scopes.end(),
        [](const ScopeState& scope) {
            return std::any_of(
                g_lanes.begin(),
                g_lanes.end(),
                [&scope](const LaneState& lane) {
                    return lane.scope_id == scope.scope_id;
                });
        });
    const auto every_destination_covered = std::all_of(
        g_destinations.begin(),
        g_destinations.end(),
        [](const DestinationState& destination) {
            return std::any_of(
                g_lanes.begin(),
                g_lanes.end(),
                [&destination](const LaneState& lane) {
                    return lane.destination_resource_id ==
                        destination.resource_id;
                });
        });
    return every_scope_covered && every_destination_covered;
}

HRESULT process_published_control_policy() {
    auto* control = g_control_block;
    auto* header = g_ring_header;
    if (control == nullptr || header == nullptr) {
        return E_UNEXPECTED;
    }
    if (!g_allow_control_policy.load(std::memory_order_acquire)) {
        return E_ACCESSDENIED;
    }
    const auto published_epoch = InterlockedCompareExchange64(
        &control->published_epoch,
        0,
        0);
    if (published_epoch <= 0) {
        return S_FALSE;
    }
    const auto processed_epoch = g_processed_control_policy_epoch.load(
        std::memory_order_acquire);
    if (static_cast<std::uint64_t>(published_epoch) <= processed_epoch) {
        const auto acknowledged_epoch = InterlockedCompareExchange64(
            &control->acknowledged_epoch,
            0,
            0);
        const auto status = static_cast<FluidHookControlStatusV1>(
            g_control_policy_status.load(std::memory_order_acquire));
        if (acknowledged_epoch != published_epoch) {
            return S_FALSE;
        }
        if (status == FluidHookControlStatusV1::rejected) {
            return E_INVALIDARG;
        }
        if (status == FluidHookControlStatusV1::expired) {
            return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
        }
        return S_OK;
    }
    const auto expires_at_qpc = InterlockedCompareExchange64(
        &control->expires_at_qpc,
        0,
        0);
    const auto action_mask = InterlockedCompareExchange64(
        &control->action_mask,
        0,
        0);
    const auto action_budget = InterlockedCompareExchange64(
        &control->action_budget,
        0,
        0);
    MemoryBarrier();
    if (InterlockedCompareExchange64(&control->published_epoch, 0, 0) !=
        published_epoch) {
        return S_FALSE;
    }
    LARGE_INTEGER now{};
    QueryPerformanceCounter(&now);
    const auto maximum_lifetime = static_cast<LONG64>(header->qpc_frequency * 4);
    bool registration_topology_complete = false;
    {
        const std::lock_guard state_lock(g_state_mutex);
        registration_topology_complete = registration_topology_complete_locked();
    }
    const auto valid =
        registration_topology_complete &&
        published_epoch == 1 &&
        processed_epoch == 0 &&
        action_mask == static_cast<LONG64>(
            fluid_hook_control_action_skip_redundant_transfer_buffer_copy) &&
        action_budget >= 1 &&
        action_budget <= static_cast<LONG64>(g_topology.max_action_count) &&
        action_budget <= static_cast<LONG64>(fluid_hook_control_max_action_budget) &&
        expires_at_qpc > now.QuadPart &&
        expires_at_qpc - now.QuadPart <= maximum_lifetime;
    g_processed_control_policy_epoch.store(
        static_cast<std::uint64_t>(published_epoch),
        std::memory_order_release);
    if (!valid) {
        g_control_policy_rejected_count.fetch_add(1, std::memory_order_relaxed);
        publish_control_status(FluidHookControlStatusV1::rejected);
        MemoryBarrier();
        InterlockedExchange64(&control->acknowledged_epoch, published_epoch);
        return E_INVALIDARG;
    }
    g_active_control_policy_epoch.store(
        static_cast<std::uint64_t>(published_epoch),
        std::memory_order_release);
    g_control_policy_action_mask.store(
        static_cast<std::uint64_t>(action_mask),
        std::memory_order_release);
    g_control_policy_action_budget.store(
        static_cast<std::uint64_t>(action_budget),
        std::memory_order_release);
    g_control_policy_expires_at_qpc.store(
        static_cast<std::uint64_t>(expires_at_qpc),
        std::memory_order_release);
    g_control_policy_applied_action_count.store(0, std::memory_order_release);
    InterlockedExchange64(&control->applied_action_count, 0);
    publish_control_status(FluidHookControlStatusV1::accepted);
    emit_hook_event(
        FluidHookEventTypeV1::control_policy_accepted,
        static_cast<std::uint64_t>(published_epoch),
        static_cast<std::uint64_t>(action_mask),
        static_cast<std::uint64_t>(action_budget),
        static_cast<std::uint64_t>(expires_at_qpc),
        kGeneralizedFlag);
    MemoryBarrier();
    InterlockedExchange64(&control->acknowledged_epoch, published_epoch);
    return S_OK;
}

bool reserve_control_policy_action() {
    auto* control = g_control_block;
    if (control == nullptr ||
        !g_allow_control_policy.load(std::memory_order_acquire) ||
        g_active_control_policy_epoch.load(std::memory_order_acquire) == 0 ||
        g_control_policy_action_mask.load(std::memory_order_acquire) !=
            fluid_hook_control_action_skip_redundant_transfer_buffer_copy ||
        static_cast<FluidHookControlStatusV1>(
            g_control_policy_status.load(std::memory_order_acquire)) !=
            FluidHookControlStatusV1::accepted) {
        return false;
    }
    LARGE_INTEGER now{};
    QueryPerformanceCounter(&now);
    if (static_cast<std::uint64_t>(now.QuadPart) >=
        g_control_policy_expires_at_qpc.load(std::memory_order_acquire)) {
        publish_control_status(FluidHookControlStatusV1::expired);
        return false;
    }
    const auto budget = g_control_policy_action_budget.load(
        std::memory_order_acquire);
    auto applied = g_control_policy_applied_action_count.load(
        std::memory_order_relaxed);
    while (applied < budget &&
           !g_control_policy_applied_action_count.compare_exchange_weak(
               applied,
               applied + 1,
               std::memory_order_acq_rel,
               std::memory_order_relaxed)) {
    }
    if (applied >= budget) {
        publish_control_status(FluidHookControlStatusV1::exhausted);
        return false;
    }
    const auto new_count = applied + 1;
    InterlockedExchange64(
        &control->applied_action_count,
        static_cast<LONG64>(new_count));
    if (new_count >= budget) {
        publish_control_status(FluidHookControlStatusV1::exhausted);
    }
    return true;
}

ScopeState* find_scope_locked(ID3D12GraphicsCommandList* command_list) {
    const auto item = std::find_if(
        g_scopes.begin(),
        g_scopes.end(),
        [command_list](const ScopeState& scope) {
            return scope.command_list == command_list;
        });
    return item != g_scopes.end() ? &*item : nullptr;
}

ScopeState* find_scope_locked(ID3D12CommandList* command_list) {
    const auto item = std::find_if(
        g_scopes.begin(),
        g_scopes.end(),
        [command_list](const ScopeState& scope) {
            return static_cast<ID3D12CommandList*>(scope.command_list) == command_list;
        });
    return item != g_scopes.end() ? &*item : nullptr;
}

ScopeState* find_scope_id_locked(std::uint64_t scope_id) {
    const auto item = std::find_if(
        g_scopes.begin(),
        g_scopes.end(),
        [scope_id](const ScopeState& scope) {
            return scope.scope_id == scope_id;
        });
    return item != g_scopes.end() ? &*item : nullptr;
}

SourceState* find_source_locked(ID3D12Resource* resource) {
    const auto item = std::find_if(
        g_sources.begin(),
        g_sources.end(),
        [resource](const SourceState& source) {
            return source.resource == resource;
        });
    return item != g_sources.end() ? &*item : nullptr;
}

DestinationState* find_destination_locked(ID3D12Resource* resource) {
    const auto item = std::find_if(
        g_destinations.begin(),
        g_destinations.end(),
        [resource](const DestinationState& destination) {
            return destination.resource == resource;
        });
    return item != g_destinations.end() ? &*item : nullptr;
}

DestinationState* find_destination_id_locked(std::uint64_t resource_id) {
    const auto item = std::find_if(
        g_destinations.begin(),
        g_destinations.end(),
        [resource_id](const DestinationState& destination) {
            return destination.resource_id == resource_id;
        });
    return item != g_destinations.end() ? &*item : nullptr;
}

LaneState* find_lane_locked(
    std::uint64_t scope_id,
    std::uint64_t destination_resource_id) {
    const auto item = std::find_if(
        g_lanes.begin(),
        g_lanes.end(),
        [scope_id, destination_resource_id](const LaneState& lane) {
            return lane.scope_id == scope_id &&
                lane.destination_resource_id == destination_resource_id;
        });
    return item != g_lanes.end() ? &*item : nullptr;
}

FenceState* find_fence_locked(ID3D12Fence* fence) {
    const auto item = std::find_if(
        g_fences.begin(),
        g_fences.end(),
        [fence](const FenceState& state) {
            return state.fence == fence;
        });
    return item != g_fences.end() ? &*item : nullptr;
}

std::uint64_t invalidate_lane_locked(LaneState& lane) {
    lane.valid = false;
    return ++lane.generation;
}

std::uint64_t invalidate_scope_locked(std::uint64_t scope_id) {
    std::uint64_t maximum = 0;
    for (auto& lane : g_lanes) {
        if (lane.scope_id == scope_id) {
            maximum = std::max(maximum, invalidate_lane_locked(lane));
        }
    }
    return maximum;
}

std::uint64_t invalidate_destination_locked(std::uint64_t resource_id) {
    std::uint64_t maximum = 0;
    for (auto& lane : g_lanes) {
        if (lane.destination_resource_id == resource_id) {
            maximum = std::max(maximum, invalidate_lane_locked(lane));
        }
    }
    return maximum;
}

std::uint64_t retained_capacity_locked() {
    std::uint64_t total = 0;
    for (const auto& lane : g_lanes) {
        total += lane.retained_content.size();
    }
    return total;
}

bool validates_buffer(
    ID3D12Resource* resource,
    D3D12_HEAP_TYPE expected_heap,
    std::uint64_t required_bytes,
    bool require_exact_width) {
    if (resource == nullptr || required_bytes == 0) {
        return false;
    }
    const auto description = resource->GetDesc();
    if (description.Dimension != D3D12_RESOURCE_DIMENSION_BUFFER ||
        description.Height != 1 ||
        description.DepthOrArraySize != 1 ||
        description.MipLevels != 1 ||
        description.Width < required_bytes ||
        (require_exact_width && description.Width != required_bytes)) {
        return false;
    }
    D3D12_HEAP_PROPERTIES heap{};
    D3D12_HEAP_FLAGS flags{};
    return SUCCEEDED(resource->GetHeapProperties(&heap, &flags)) &&
        heap.Type == expected_heap;
}

bool topology_valid(
    const FluidD3D12HookAttachOptionsV2& options,
    std::uint32_t scope_count) {
    const auto& topology = options.topology;
    return options.struct_size >= sizeof(FluidD3D12HookAttachOptionsV2) &&
        options.abi_version == fluid_d3d12_hook_attach_options_v2_abi_version &&
        (options.flags & ~fluid_d3d12_hook_attach_flag_allow_control_policy) == 0 &&
        options.reserved0 == 0 &&
        options.reserved1 == 0 &&
        options.queue_id != 0 &&
        topology.struct_size >= sizeof(FluidTransferTopologyV1) &&
        topology.abi_version == fluid_transfer_contract_abi_version &&
        topology.backend == static_cast<std::uint32_t>(FluidTransferBackendV1::d3d12) &&
        topology.operation ==
            static_cast<std::uint32_t>(FluidTransferOperationV1::copy_buffer) &&
        topology.queue_count == 1 &&
        topology.execution_scope_count == scope_count &&
        scope_count >= 1 &&
        scope_count <= fluid_transfer_max_execution_scope_count &&
        topology.source_resource_count >= 1 &&
        topology.source_resource_count <= fluid_transfer_max_resource_count &&
        topology.destination_resource_count >= 1 &&
        topology.destination_resource_count <= fluid_transfer_max_resource_count &&
        topology.lane_count >= 1 &&
        topology.lane_count <= fluid_transfer_max_lane_count &&
        topology.lane_count == topology.destination_resource_count &&
        topology.execution_scope_count <= topology.lane_count &&
        topology.fence_count >= 1 &&
        topology.fence_count <= fluid_transfer_max_fence_count &&
        topology.max_action_count >= 1 &&
        topology.max_action_count <= fluid_hook_control_max_action_budget &&
        topology.expected_runtime_event_count >= topology.max_action_count &&
        topology.expected_runtime_event_count <=
            fluid_transfer_max_runtime_event_count &&
        topology.max_resource_bytes >= 1 &&
        topology.max_resource_bytes <= fluid_transfer_max_resource_bytes &&
        topology.max_total_retained_bytes >= topology.max_resource_bytes &&
        topology.max_total_retained_bytes <=
            fluid_transfer_max_total_retained_bytes;
}

void reset_metrics_and_control() {
    g_copy_buffer_region_count.store(0, std::memory_order_relaxed);
    g_tracked_copy_count.store(0, std::memory_order_relaxed);
    g_tracked_copy_bytes.store(0, std::memory_order_relaxed);
    g_redundant_candidate_count.store(0, std::memory_order_relaxed);
    g_redundant_candidate_bytes.store(0, std::memory_order_relaxed);
    g_forwarded_copy_count.store(0, std::memory_order_relaxed);
    g_forwarded_copy_bytes.store(0, std::memory_order_relaxed);
    g_skipped_copy_count.store(0, std::memory_order_relaxed);
    g_skipped_copy_bytes.store(0, std::memory_order_relaxed);
    g_exact_comparison_count.store(0, std::memory_order_relaxed);
    g_exact_comparison_bytes.store(0, std::memory_order_relaxed);
    g_source_registration_count.store(0, std::memory_order_relaxed);
    g_destination_registration_count.store(0, std::memory_order_relaxed);
    g_lane_registration_count.store(0, std::memory_order_relaxed);
    g_fence_registration_count.store(0, std::memory_order_relaxed);
    g_automatic_invalidation_count.store(0, std::memory_order_relaxed);
    g_explicit_invalidation_count.store(0, std::memory_order_relaxed);
    g_command_list_close_count.store(0, std::memory_order_relaxed);
    g_command_list_reset_count.store(0, std::memory_order_relaxed);
    g_queue_execute_count.store(0, std::memory_order_relaxed);
    g_queue_signal_count.store(0, std::memory_order_relaxed);
    g_submitted_scope_count.store(0, std::memory_order_relaxed);
    g_unregistered_submitted_scope_count.store(0, std::memory_order_relaxed);
    g_last_submission_scope_hash.store(0, std::memory_order_relaxed);
    g_last_signaled_fence_id.store(0, std::memory_order_relaxed);
    g_last_signaled_fence_value.store(0, std::memory_order_relaxed);
    g_processed_control_policy_epoch.store(0, std::memory_order_relaxed);
    g_active_control_policy_epoch.store(0, std::memory_order_relaxed);
    g_control_policy_action_mask.store(0, std::memory_order_relaxed);
    g_control_policy_action_budget.store(0, std::memory_order_relaxed);
    g_control_policy_expires_at_qpc.store(0, std::memory_order_relaxed);
    g_control_policy_applied_action_count.store(0, std::memory_order_relaxed);
    g_control_policy_rejected_count.store(0, std::memory_order_relaxed);
    g_control_policy_status.store(
        static_cast<std::uint64_t>(FluidHookControlStatusV1::none),
        std::memory_order_relaxed);
}

void clear_original_functions() {
    g_original_close.store(nullptr, std::memory_order_release);
    g_original_reset.store(nullptr, std::memory_order_release);
    g_original_copy_buffer_region.store(nullptr, std::memory_order_release);
    g_original_copy_texture_region.store(nullptr, std::memory_order_release);
    g_original_copy_resource.store(nullptr, std::memory_order_release);
    g_original_execute_command_lists.store(nullptr, std::memory_order_release);
    g_original_signal.store(nullptr, std::memory_order_release);
}

HRESULT STDMETHODCALLTYPE hooked_close(ID3D12GraphicsCommandList* command_list) {
    const auto original = g_original_close.load(std::memory_order_acquire);
    if (original == nullptr) {
        return E_UNEXPECTED;
    }
    const ActiveHookCall call;
    const auto result = original(command_list);
    if (!call.active() || FAILED(result)) {
        return result;
    }
    std::uint64_t scope_id = 0;
    std::uint64_t generation = 0;
    {
        const std::lock_guard state_lock(g_state_mutex);
        const auto* scope = find_scope_locked(command_list);
        if (scope == nullptr) {
            return result;
        }
        scope_id = scope->scope_id;
        generation = invalidate_scope_locked(scope_id);
    }
    g_command_list_close_count.fetch_add(1, std::memory_order_relaxed);
    emit_hook_event(
        FluidHookEventTypeV1::transfer_scope_close,
        scope_id,
        0,
        0,
        generation,
        kGeneralizedFlag,
        0,
        0,
        scope_id);
    return result;
}

HRESULT STDMETHODCALLTYPE hooked_reset(
    ID3D12GraphicsCommandList* command_list,
    ID3D12CommandAllocator* allocator,
    ID3D12PipelineState* initial_state) {
    const auto original = g_original_reset.load(std::memory_order_acquire);
    if (original == nullptr) {
        return E_UNEXPECTED;
    }
    const ActiveHookCall call;
    const auto result = original(command_list, allocator, initial_state);
    if (!call.active() || FAILED(result)) {
        return result;
    }
    std::uint64_t scope_id = 0;
    std::uint64_t generation = 0;
    {
        const std::lock_guard state_lock(g_state_mutex);
        const auto* scope = find_scope_locked(command_list);
        if (scope == nullptr) {
            return result;
        }
        scope_id = scope->scope_id;
        generation = invalidate_scope_locked(scope_id);
    }
    g_command_list_reset_count.fetch_add(1, std::memory_order_relaxed);
    emit_hook_event(
        FluidHookEventTypeV1::transfer_scope_reset,
        scope_id,
        0,
        0,
        generation,
        kGeneralizedFlag,
        0,
        0,
        scope_id);
    return result;
}

void STDMETHODCALLTYPE hooked_copy_buffer_region(
    ID3D12GraphicsCommandList* command_list,
    ID3D12Resource* destination,
    UINT64 destination_offset,
    ID3D12Resource* source,
    UINT64 source_offset,
    UINT64 bytes) {
    const auto original = g_original_copy_buffer_region.load(
        std::memory_order_acquire);
    if (original == nullptr) {
        return;
    }
    const ActiveHookCall call;
    if (!call.active()) {
        original(
            command_list,
            destination,
            destination_offset,
            source,
            source_offset,
            bytes);
        return;
    }

    bool registered_scope = false;
    bool tracked = false;
    bool compared = false;
    bool candidate = false;
    bool skipped = false;
    bool automatically_invalidated = false;
    std::uint64_t scope_id = 0;
    std::uint64_t source_id = 0;
    std::uint64_t destination_id = 0;
    std::uint64_t generation = 0;
    {
        const std::lock_guard state_lock(g_state_mutex);
        const auto* scope_state = find_scope_locked(command_list);
        if (scope_state == nullptr) {
            original(
                command_list,
                destination,
                destination_offset,
                source,
                source_offset,
                bytes);
            return;
        }
        registered_scope = true;
        scope_id = scope_state->scope_id;
        g_copy_buffer_region_count.fetch_add(1, std::memory_order_relaxed);
        auto* source_state = find_source_locked(source);
        auto* destination_state = find_destination_locked(destination);
        if (source_state != nullptr) {
            source_id = source_state->resource_id;
        }
        if (destination_state != nullptr) {
            destination_id = destination_state->resource_id;
        }
        auto* lane = destination_state != nullptr
            ? find_lane_locked(scope_id, destination_state->resource_id)
            : nullptr;
        const auto source_snapshot_bytes = source_state != nullptr
            ? static_cast<std::uint64_t>(source_state->snapshot.size())
            : 0;
        tracked = source_state != nullptr &&
            destination_state != nullptr &&
            lane != nullptr &&
            destination_offset == 0 &&
            bytes == destination_state->bytes &&
            bytes != 0 &&
            bytes <= g_topology.max_resource_bytes &&
            lane->retained_content.size() == bytes &&
            source_offset <= source_snapshot_bytes &&
            bytes <= source_snapshot_bytes - source_offset;
        if (tracked) {
            const auto* source_bytes = source_state->snapshot.data() + source_offset;
            if (lane->valid) {
                compared = true;
                g_exact_comparison_count.fetch_add(1, std::memory_order_relaxed);
                g_exact_comparison_bytes.fetch_add(bytes, std::memory_order_relaxed);
                candidate = std::memcmp(
                    lane->retained_content.data(),
                    source_bytes,
                    static_cast<size_t>(bytes)) == 0;
            }
            if (candidate) {
                skipped = reserve_control_policy_action();
            }
            if (!skipped) {
                original(
                    command_list,
                    destination,
                    destination_offset,
                    source,
                    source_offset,
                    bytes);
                if (!candidate) {
                    std::memcpy(
                        lane->retained_content.data(),
                        source_bytes,
                        static_cast<size_t>(bytes));
                    lane->valid = true;
                    ++lane->generation;
                }
            }
            generation = lane->generation;
        } else {
            original(
                command_list,
                destination,
                destination_offset,
                source,
                source_offset,
                bytes);
            if (destination_state != nullptr) {
                generation = invalidate_destination_locked(
                    destination_state->resource_id);
                automatically_invalidated = true;
            }
        }
    }
    if (!registered_scope) {
        return;
    }
    if (!tracked) {
        if (automatically_invalidated) {
            g_automatic_invalidation_count.fetch_add(1, std::memory_order_relaxed);
            emit_hook_event(
                FluidHookEventTypeV1::transfer_resource_invalidate,
                destination_id,
                source_id,
                bytes,
                generation,
                kGeneralizedFlag,
                static_cast<std::uint32_t>(destination_offset),
                static_cast<std::uint32_t>(source_offset),
                scope_id);
        }
        return;
    }
    g_tracked_copy_count.fetch_add(1, std::memory_order_relaxed);
    g_tracked_copy_bytes.fetch_add(bytes, std::memory_order_relaxed);
    if (candidate) {
        g_redundant_candidate_count.fetch_add(1, std::memory_order_relaxed);
        g_redundant_candidate_bytes.fetch_add(bytes, std::memory_order_relaxed);
    }
    if (skipped) {
        g_skipped_copy_count.fetch_add(1, std::memory_order_relaxed);
        g_skipped_copy_bytes.fetch_add(bytes, std::memory_order_relaxed);
    } else {
        g_forwarded_copy_count.fetch_add(1, std::memory_order_relaxed);
        g_forwarded_copy_bytes.fetch_add(bytes, std::memory_order_relaxed);
    }
    std::uint32_t flags = kGeneralizedFlag |
        fluid_hook_event_flag_immutable_upload_source;
    if (compared) {
        flags |= fluid_hook_event_flag_content_compared;
    }
    if (candidate) {
        flags |= fluid_hook_event_flag_redundant_candidate;
    }
    if (skipped) {
        flags |= fluid_hook_event_flag_copy_skipped;
    }
    emit_hook_event(
        FluidHookEventTypeV1::transfer_buffer_copy,
        destination_id,
        source_id,
        bytes,
        generation,
        flags,
        static_cast<std::uint32_t>(destination_offset),
        static_cast<std::uint32_t>(source_offset),
        scope_id);
}

void STDMETHODCALLTYPE hooked_copy_texture_region(
    ID3D12GraphicsCommandList* command_list,
    const D3D12_TEXTURE_COPY_LOCATION* destination,
    UINT destination_x,
    UINT destination_y,
    UINT destination_z,
    const D3D12_TEXTURE_COPY_LOCATION* source,
    const D3D12_BOX* source_box) {
    const auto original = g_original_copy_texture_region.load(
        std::memory_order_acquire);
    if (original == nullptr) {
        return;
    }
    const ActiveHookCall call;
    bool invalidated = false;
    std::uint64_t scope_id = 0;
    std::uint64_t destination_id = 0;
    std::uint64_t source_id = 0;
    std::uint64_t generation = 0;
    if (call.active()) {
        const std::lock_guard state_lock(g_state_mutex);
        const auto* scope = find_scope_locked(command_list);
        auto* destination_state = destination != nullptr
            ? find_destination_locked(destination->pResource)
            : nullptr;
        if (scope != nullptr && destination_state != nullptr) {
            scope_id = scope->scope_id;
            destination_id = destination_state->resource_id;
            const auto* source_state = source != nullptr
                ? find_source_locked(source->pResource)
                : nullptr;
            source_id = source_state != nullptr ? source_state->resource_id : 0;
            generation = invalidate_destination_locked(destination_id);
            invalidated = true;
        }
    }
    original(
        command_list,
        destination,
        destination_x,
        destination_y,
        destination_z,
        source,
        source_box);
    if (invalidated) {
        g_automatic_invalidation_count.fetch_add(1, std::memory_order_relaxed);
        emit_hook_event(
            FluidHookEventTypeV1::transfer_resource_invalidate,
            destination_id,
            source_id,
            0,
            generation,
            kGeneralizedFlag,
            0,
            0,
            scope_id);
    }
}

void STDMETHODCALLTYPE hooked_copy_resource(
    ID3D12GraphicsCommandList* command_list,
    ID3D12Resource* destination,
    ID3D12Resource* source) {
    const auto original = g_original_copy_resource.load(std::memory_order_acquire);
    if (original == nullptr) {
        return;
    }
    const ActiveHookCall call;
    bool invalidated = false;
    std::uint64_t scope_id = 0;
    std::uint64_t destination_id = 0;
    std::uint64_t source_id = 0;
    std::uint64_t generation = 0;
    if (call.active()) {
        const std::lock_guard state_lock(g_state_mutex);
        const auto* scope = find_scope_locked(command_list);
        auto* destination_state = find_destination_locked(destination);
        if (scope != nullptr && destination_state != nullptr) {
            scope_id = scope->scope_id;
            destination_id = destination_state->resource_id;
            const auto* source_state = find_source_locked(source);
            source_id = source_state != nullptr ? source_state->resource_id : 0;
            generation = invalidate_destination_locked(destination_id);
            invalidated = true;
        }
    }
    original(command_list, destination, source);
    if (invalidated) {
        g_automatic_invalidation_count.fetch_add(1, std::memory_order_relaxed);
        emit_hook_event(
            FluidHookEventTypeV1::transfer_resource_invalidate,
            destination_id,
            source_id,
            0,
            generation,
            kGeneralizedFlag,
            0,
            0,
            scope_id);
    }
}

std::uint64_t append_scope_hash(std::uint64_t hash, std::uint64_t scope_id) {
    constexpr std::uint64_t prime = 1099511628211ULL;
    for (unsigned int shift = 0; shift < 64; shift += 8) {
        hash ^= (scope_id >> shift) & 0xff;
        hash *= prime;
    }
    return hash;
}

void STDMETHODCALLTYPE hooked_execute_command_lists(
    ID3D12CommandQueue* queue,
    UINT command_list_count,
    ID3D12CommandList* const* command_lists) {
    const auto original = g_original_execute_command_lists.load(
        std::memory_order_acquire);
    if (original == nullptr) {
        return;
    }
    const ActiveHookCall call;
    original(queue, command_list_count, command_lists);
    if (!call.active() || queue != g_queue) {
        return;
    }
    std::uint32_t registered_count = 0;
    std::uint32_t unregistered_count = 0;
    std::uint64_t scope_hash = 14695981039346656037ULL;
    {
        const std::lock_guard state_lock(g_state_mutex);
        for (UINT index = 0; index < command_list_count; ++index) {
            const auto* scope = find_scope_locked(command_lists[index]);
            if (scope == nullptr) {
                ++unregistered_count;
            } else {
                ++registered_count;
                scope_hash = append_scope_hash(scope_hash, scope->scope_id);
            }
        }
    }
    const auto execute_count =
        g_queue_execute_count.fetch_add(1, std::memory_order_relaxed) + 1;
    g_submitted_scope_count.fetch_add(registered_count, std::memory_order_relaxed);
    g_unregistered_submitted_scope_count.fetch_add(
        unregistered_count,
        std::memory_order_relaxed);
    g_last_submission_scope_hash.store(scope_hash, std::memory_order_relaxed);
    emit_hook_event(
        FluidHookEventTypeV1::transfer_queue_submit,
        g_queue_id,
        0,
        command_list_count,
        execute_count,
        kGeneralizedFlag,
        registered_count,
        unregistered_count,
        scope_hash);
}

HRESULT STDMETHODCALLTYPE hooked_signal(
    ID3D12CommandQueue* queue,
    ID3D12Fence* fence,
    UINT64 value) {
    const auto original = g_original_signal.load(std::memory_order_acquire);
    if (original == nullptr) {
        return E_UNEXPECTED;
    }
    const ActiveHookCall call;
    const auto result = original(queue, fence, value);
    if (!call.active() || queue != g_queue || FAILED(result)) {
        return result;
    }
    std::uint64_t fence_id = 0;
    {
        const std::lock_guard state_lock(g_state_mutex);
        const auto* state = find_fence_locked(fence);
        fence_id = state != nullptr ? state->fence_id : 0;
    }
    const auto signal_count =
        g_queue_signal_count.fetch_add(1, std::memory_order_relaxed) + 1;
    g_last_signaled_fence_id.store(fence_id, std::memory_order_relaxed);
    g_last_signaled_fence_value.store(value, std::memory_order_relaxed);
    emit_hook_event(
        FluidHookEventTypeV1::transfer_sync_signal,
        g_queue_id,
        fence_id,
        value,
        signal_count,
        kGeneralizedFlag);
    return result;
}

} // namespace

HRESULT WINAPI FluidD3D12HookAttachV2(
    ID3D12CommandQueue* queue,
    const FluidD3D12CommandScopeV2* scopes,
    std::uint32_t scope_count,
    const FluidD3D12HookAttachOptionsV2* options) {
    if (queue == nullptr || scopes == nullptr || options == nullptr) {
        return E_POINTER;
    }
    if (!topology_valid(*options, scope_count) ||
        queue->GetDesc().Type != D3D12_COMMAND_LIST_TYPE_COPY) {
        return E_INVALIDARG;
    }
    std::vector<ScopeState> scope_states;
    try {
        scope_states.reserve(scope_count);
    } catch (const std::bad_alloc&) {
        return E_OUTOFMEMORY;
    }
    void** command_list_vtable = nullptr;
    for (std::uint32_t index = 0; index < scope_count; ++index) {
        const auto& item = scopes[index];
        if (item.command_list == nullptr || item.scope_id == 0 ||
            item.command_list->GetType() != D3D12_COMMAND_LIST_TYPE_COPY ||
            std::any_of(
                scope_states.begin(),
                scope_states.end(),
                [&item](const ScopeState& existing) {
                    return existing.command_list == item.command_list ||
                        existing.scope_id == item.scope_id;
                })) {
            return E_INVALIDARG;
        }
        auto** current_vtable = *reinterpret_cast<void***>(item.command_list);
        if (command_list_vtable == nullptr) {
            command_list_vtable = current_vtable;
        } else if (command_list_vtable != current_vtable) {
            return E_NOTIMPL;
        }
        scope_states.push_back({item.command_list, item.scope_id});
    }

    const std::lock_guard hook_lock(g_hook_mutex);
    if (g_installed_hook_count.load(std::memory_order_acquire) != 0 ||
        g_attach_completed_once) {
        return HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS);
    }
    if (!pin_hook_module()) {
        return HRESULT_FROM_WIN32(GetLastError());
    }
    auto** queue_vtable = *reinterpret_cast<void***>(queue);
    std::array<HookSlot, kHookSlotCount> slots{
        HookSlot{&command_list_vtable[kCloseVtableIndex],
            command_list_vtable[kCloseVtableIndex],
            reinterpret_cast<void*>(hooked_close)},
        HookSlot{&command_list_vtable[kResetVtableIndex],
            command_list_vtable[kResetVtableIndex],
            reinterpret_cast<void*>(hooked_reset)},
        HookSlot{&command_list_vtable[kCopyBufferRegionVtableIndex],
            command_list_vtable[kCopyBufferRegionVtableIndex],
            reinterpret_cast<void*>(hooked_copy_buffer_region)},
        HookSlot{&command_list_vtable[kCopyTextureRegionVtableIndex],
            command_list_vtable[kCopyTextureRegionVtableIndex],
            reinterpret_cast<void*>(hooked_copy_texture_region)},
        HookSlot{&command_list_vtable[kCopyResourceVtableIndex],
            command_list_vtable[kCopyResourceVtableIndex],
            reinterpret_cast<void*>(hooked_copy_resource)},
        HookSlot{&queue_vtable[kExecuteCommandListsVtableIndex],
            queue_vtable[kExecuteCommandListsVtableIndex],
            reinterpret_cast<void*>(hooked_execute_command_lists)},
        HookSlot{&queue_vtable[kSignalVtableIndex],
            queue_vtable[kSignalVtableIndex],
            reinterpret_cast<void*>(hooked_signal)},
    };
    for (const auto& slot : slots) {
        if (slot.original == nullptr || slot.original == slot.hook) {
            return E_UNEXPECTED;
        }
    }

    g_original_close.store(
        reinterpret_cast<CloseFunction>(slots[0].original),
        std::memory_order_release);
    g_original_reset.store(
        reinterpret_cast<ResetFunction>(slots[1].original),
        std::memory_order_release);
    g_original_copy_buffer_region.store(
        reinterpret_cast<CopyBufferRegionFunction>(slots[2].original),
        std::memory_order_release);
    g_original_copy_texture_region.store(
        reinterpret_cast<CopyTextureRegionFunction>(slots[3].original),
        std::memory_order_release);
    g_original_copy_resource.store(
        reinterpret_cast<CopyResourceFunction>(slots[4].original),
        std::memory_order_release);
    g_original_execute_command_lists.store(
        reinterpret_cast<ExecuteCommandListsFunction>(slots[5].original),
        std::memory_order_release);
    g_original_signal.store(
        reinterpret_cast<SignalFunction>(slots[6].original),
        std::memory_order_release);
    g_allow_control_policy.store(
        (options->flags & fluid_d3d12_hook_attach_flag_allow_control_policy) != 0,
        std::memory_order_release);
    g_topology = options->topology;
    g_queue_id = options->queue_id;
    reset_metrics_and_control();
    if (!initialize_event_ring()) {
        const auto error = GetLastError();
        clear_original_functions();
        g_allow_control_policy.store(false, std::memory_order_release);
        return HRESULT_FROM_WIN32(error);
    }

    queue->AddRef();
    g_queue = queue;
    for (auto& scope : scope_states) {
        scope.command_list->AddRef();
    }
    g_scopes = std::move(scope_states);
    const std::lock_guard patch_lock(g_patch_mutex);
    g_detaching.store(false, std::memory_order_release);
    size_t patched_count = 0;
    for (; patched_count < slots.size(); ++patched_count) {
        const auto& slot = slots[patched_count];
        if (!write_pointer(slot.slot, slot.hook, slot.original)) {
            const auto error = GetLastError();
            bool rollback_ok = true;
            while (patched_count != 0) {
                --patched_count;
                const auto& patched = slots[patched_count];
                rollback_ok = write_pointer(
                    patched.slot,
                    patched.original,
                    patched.hook) && rollback_ok;
            }
            for (auto& scope : g_scopes) {
                scope.command_list->Release();
            }
            g_scopes.clear();
            g_queue->Release();
            g_queue = nullptr;
            close_event_ring();
            clear_original_functions();
            g_allow_control_policy.store(false, std::memory_order_release);
            return rollback_ok ? HRESULT_FROM_WIN32(error) : E_UNEXPECTED;
        }
    }
    g_hook_slots = slots;
    g_installed_hook_count.store(slots.size(), std::memory_order_release);
    g_attach_completed_once = true;
    return S_OK;
}

HRESULT WINAPI FluidD3D12HookRegisterUploadBufferV2(
    ID3D12Resource* resource,
    std::uint64_t resource_id,
    const void* immutable_cpu_shadow,
    std::uint64_t shadow_bytes) {
    if (resource == nullptr || immutable_cpu_shadow == nullptr) {
        return E_POINTER;
    }
    if (resource_id == 0 || shadow_bytes == 0 ||
        shadow_bytes > kMaximumSourceSnapshotBytes ||
        !validates_buffer(resource, D3D12_HEAP_TYPE_UPLOAD, shadow_bytes, false)) {
        return E_INVALIDARG;
    }
    const std::lock_guard hook_lock(g_hook_mutex);
    if (g_installed_hook_count.load(std::memory_order_acquire) == 0 ||
        g_detaching.load(std::memory_order_acquire)) {
        return S_FALSE;
    }
    const std::lock_guard state_lock(g_state_mutex);
    if (g_sources.size() >= g_topology.source_resource_count ||
        std::any_of(g_sources.begin(), g_sources.end(),
            [resource, resource_id](const SourceState& item) {
                return item.resource == resource || item.resource_id == resource_id;
            })) {
        return HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS);
    }
    std::uint64_t total_snapshot_bytes = shadow_bytes;
    for (const auto& item : g_sources) {
        total_snapshot_bytes += item.snapshot.size();
    }
    if (total_snapshot_bytes > kMaximumSourceSnapshotBytes) {
        return E_INVALIDARG;
    }
    SourceState state;
    try {
        state.snapshot.resize(static_cast<size_t>(shadow_bytes));
    } catch (const std::bad_alloc&) {
        return E_OUTOFMEMORY;
    }
    std::memcpy(state.snapshot.data(), immutable_cpu_shadow, state.snapshot.size());
    resource->AddRef();
    state.resource = resource;
    state.resource_id = resource_id;
    g_sources.push_back(std::move(state));
    g_source_registration_count.fetch_add(1, std::memory_order_relaxed);
    return S_OK;
}

HRESULT WINAPI FluidD3D12HookRegisterCopyOnlyBufferV2(
    ID3D12Resource* resource,
    std::uint64_t resource_id,
    std::uint64_t resource_bytes) {
    if (resource == nullptr) {
        return E_POINTER;
    }
    if (resource_id == 0 || resource_bytes == 0 ||
        resource_bytes > g_topology.max_resource_bytes ||
        !validates_buffer(
            resource,
            D3D12_HEAP_TYPE_DEFAULT,
            resource_bytes,
            true)) {
        return E_INVALIDARG;
    }
    const std::lock_guard hook_lock(g_hook_mutex);
    if (g_installed_hook_count.load(std::memory_order_acquire) == 0 ||
        g_detaching.load(std::memory_order_acquire)) {
        return S_FALSE;
    }
    const std::lock_guard state_lock(g_state_mutex);
    if (g_destinations.size() >= g_topology.destination_resource_count ||
        std::any_of(g_destinations.begin(), g_destinations.end(),
            [resource, resource_id](const DestinationState& item) {
                return item.resource == resource || item.resource_id == resource_id;
            })) {
        return HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS);
    }
    resource->AddRef();
    g_destinations.push_back({resource, resource_id, resource_bytes});
    g_destination_registration_count.fetch_add(1, std::memory_order_relaxed);
    return S_OK;
}

HRESULT WINAPI FluidD3D12HookRegisterCopyLaneV2(
    std::uint64_t scope_id,
    std::uint64_t destination_resource_id) {
    if (scope_id == 0 || destination_resource_id == 0) {
        return E_INVALIDARG;
    }
    const std::lock_guard hook_lock(g_hook_mutex);
    if (g_installed_hook_count.load(std::memory_order_acquire) == 0 ||
        g_detaching.load(std::memory_order_acquire)) {
        return S_FALSE;
    }
    const std::lock_guard state_lock(g_state_mutex);
    auto* scope = find_scope_id_locked(scope_id);
    auto* destination = find_destination_id_locked(destination_resource_id);
    if (scope == nullptr || destination == nullptr) {
        return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    }
    if (g_lanes.size() >= g_topology.lane_count ||
        find_lane_locked(scope_id, destination_resource_id) != nullptr ||
        std::any_of(g_lanes.begin(), g_lanes.end(),
            [destination_resource_id](const LaneState& lane) {
                return lane.destination_resource_id == destination_resource_id;
            })) {
        return HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS);
    }
    const auto retained_after = retained_capacity_locked() + destination->bytes;
    if (retained_after > g_topology.max_total_retained_bytes) {
        return E_INVALIDARG;
    }
    LaneState lane;
    try {
        lane.retained_content.resize(static_cast<size_t>(destination->bytes));
    } catch (const std::bad_alloc&) {
        return E_OUTOFMEMORY;
    }
    lane.scope_id = scope_id;
    lane.destination_resource_id = destination_resource_id;
    g_lanes.push_back(std::move(lane));
    g_lane_registration_count.fetch_add(1, std::memory_order_relaxed);
    return S_OK;
}

HRESULT WINAPI FluidD3D12HookRegisterFenceV2(
    ID3D12Fence* fence,
    std::uint64_t fence_id) {
    if (fence == nullptr) {
        return E_POINTER;
    }
    if (fence_id == 0) {
        return E_INVALIDARG;
    }
    const std::lock_guard hook_lock(g_hook_mutex);
    if (g_installed_hook_count.load(std::memory_order_acquire) == 0 ||
        g_detaching.load(std::memory_order_acquire)) {
        return S_FALSE;
    }
    const std::lock_guard state_lock(g_state_mutex);
    if (g_fences.size() >= g_topology.fence_count ||
        std::any_of(g_fences.begin(), g_fences.end(),
            [fence, fence_id](const FenceState& item) {
                return item.fence == fence || item.fence_id == fence_id;
            })) {
        return HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS);
    }
    fence->AddRef();
    g_fences.push_back({fence, fence_id});
    g_fence_registration_count.fetch_add(1, std::memory_order_relaxed);
    return S_OK;
}

HRESULT WINAPI FluidD3D12HookInvalidateResourceV2(
    std::uint64_t destination_resource_id) {
    if (destination_resource_id == 0) {
        return E_INVALIDARG;
    }
    const std::lock_guard hook_lock(g_hook_mutex);
    if (g_installed_hook_count.load(std::memory_order_acquire) == 0 ||
        g_detaching.load(std::memory_order_acquire)) {
        return S_FALSE;
    }
    std::uint64_t bytes = 0;
    std::uint64_t generation = 0;
    {
        const std::lock_guard state_lock(g_state_mutex);
        const auto* destination = find_destination_id_locked(
            destination_resource_id);
        if (destination == nullptr) {
            return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        }
        bytes = destination->bytes;
        generation = invalidate_destination_locked(destination_resource_id);
    }
    g_explicit_invalidation_count.fetch_add(1, std::memory_order_relaxed);
    emit_hook_event(
        FluidHookEventTypeV1::transfer_resource_invalidate,
        destination_resource_id,
        0,
        bytes,
        generation,
        kGeneralizedFlag | fluid_hook_event_flag_explicit_invalidation);
    return S_OK;
}

HRESULT WINAPI FluidD3D12HookWaitForControlPolicy(DWORD timeout_ms) {
    if (timeout_ms == 0 || timeout_ms > 5000) {
        return E_INVALIDARG;
    }
    if (!g_allow_control_policy.load(std::memory_order_acquire)) {
        return E_ACCESSDENIED;
    }
    for (DWORD waited_ms = 0; waited_ms < timeout_ms; ++waited_ms) {
        HRESULT result = S_FALSE;
        {
            const std::lock_guard hook_lock(g_hook_mutex);
            if (g_detaching.load(std::memory_order_acquire) ||
                g_installed_hook_count.load(std::memory_order_acquire) == 0) {
                return E_ABORT;
            }
            result = process_published_control_policy();
        }
        if (result != S_FALSE) {
            return result;
        }
        Sleep(1);
    }
    return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
}

HRESULT WINAPI FluidD3D12HookReadSnapshotV2(
    FluidD3D12HookSnapshotV2* snapshot) {
    if (snapshot == nullptr) {
        return E_POINTER;
    }
    if (snapshot->struct_size < sizeof(FluidD3D12HookSnapshotV2)) {
        return E_INVALIDARG;
    }
    const std::lock_guard hook_lock(g_hook_mutex);
    const std::lock_guard state_lock(g_state_mutex);
    FluidD3D12HookSnapshotV2 result{};
    result.struct_size = sizeof(result);
    result.abi_version = fluid_d3d12_hook_snapshot_v2_abi_version;
    result.attached = g_installed_hook_count.load(std::memory_order_acquire) != 0;
    result.queue_identity = identity(g_queue);
    result.queue_id = g_queue_id;
    result.execution_scope_count = g_scopes.size();
    result.source_resource_count = g_sources.size();
    result.destination_resource_count = g_destinations.size();
    result.lane_count = g_lanes.size();
    result.fence_count = g_fences.size();
    for (const auto& source : g_sources) {
        result.source_snapshot_bytes += source.snapshot.size();
    }
    result.retained_capacity_bytes = retained_capacity_locked();
    for (const auto& lane : g_lanes) {
        result.valid_lane_count += lane.valid ? 1 : 0;
        result.maximum_lane_generation = std::max(
            result.maximum_lane_generation,
            lane.generation);
    }
    result.copy_buffer_region_count =
        g_copy_buffer_region_count.load(std::memory_order_relaxed);
    result.tracked_copy_count =
        g_tracked_copy_count.load(std::memory_order_relaxed);
    result.tracked_copy_bytes =
        g_tracked_copy_bytes.load(std::memory_order_relaxed);
    result.redundant_candidate_count =
        g_redundant_candidate_count.load(std::memory_order_relaxed);
    result.redundant_candidate_bytes =
        g_redundant_candidate_bytes.load(std::memory_order_relaxed);
    result.forwarded_copy_count =
        g_forwarded_copy_count.load(std::memory_order_relaxed);
    result.forwarded_copy_bytes =
        g_forwarded_copy_bytes.load(std::memory_order_relaxed);
    result.skipped_copy_count =
        g_skipped_copy_count.load(std::memory_order_relaxed);
    result.skipped_copy_bytes =
        g_skipped_copy_bytes.load(std::memory_order_relaxed);
    result.exact_comparison_count =
        g_exact_comparison_count.load(std::memory_order_relaxed);
    result.exact_comparison_bytes =
        g_exact_comparison_bytes.load(std::memory_order_relaxed);
    result.source_registration_count =
        g_source_registration_count.load(std::memory_order_relaxed);
    result.destination_registration_count =
        g_destination_registration_count.load(std::memory_order_relaxed);
    result.lane_registration_count =
        g_lane_registration_count.load(std::memory_order_relaxed);
    result.fence_registration_count =
        g_fence_registration_count.load(std::memory_order_relaxed);
    result.automatic_invalidation_count =
        g_automatic_invalidation_count.load(std::memory_order_relaxed);
    result.explicit_invalidation_count =
        g_explicit_invalidation_count.load(std::memory_order_relaxed);
    result.command_list_close_count =
        g_command_list_close_count.load(std::memory_order_relaxed);
    result.command_list_reset_count =
        g_command_list_reset_count.load(std::memory_order_relaxed);
    result.queue_execute_count =
        g_queue_execute_count.load(std::memory_order_relaxed);
    result.queue_signal_count =
        g_queue_signal_count.load(std::memory_order_relaxed);
    result.submitted_scope_count =
        g_submitted_scope_count.load(std::memory_order_relaxed);
    result.unregistered_submitted_scope_count =
        g_unregistered_submitted_scope_count.load(std::memory_order_relaxed);
    result.last_submission_scope_hash =
        g_last_submission_scope_hash.load(std::memory_order_relaxed);
    result.last_signaled_fence_id =
        g_last_signaled_fence_id.load(std::memory_order_relaxed);
    result.last_signaled_fence_value =
        g_last_signaled_fence_value.load(std::memory_order_relaxed);
    result.control_policy_enabled =
        g_allow_control_policy.load(std::memory_order_acquire) ? 1 : 0;
    result.control_policy_epoch =
        g_active_control_policy_epoch.load(std::memory_order_acquire);
    result.control_policy_acknowledged_epoch = g_control_block != nullptr
        ? static_cast<std::uint64_t>(InterlockedCompareExchange64(
            &g_control_block->acknowledged_epoch,
            0,
            0))
        : 0;
    result.control_policy_applied_action_count =
        g_control_policy_applied_action_count.load(std::memory_order_acquire);
    result.control_policy_rejected_count =
        g_control_policy_rejected_count.load(std::memory_order_relaxed);
    result.control_policy_status =
        g_control_policy_status.load(std::memory_order_acquire);
    result.ipc_event_count = g_ring_header != nullptr
        ? static_cast<std::uint64_t>(InterlockedCompareExchange64(
            &g_ring_header->next_sequence,
            0,
            0))
        : 0;
    result.ipc_overrun_count = g_ring_header != nullptr
        ? static_cast<std::uint64_t>(InterlockedCompareExchange64(
            &g_ring_header->overrun_count,
            0,
            0))
        : 0;
    result.active_hook_call_count =
        g_active_hook_calls.load(std::memory_order_acquire);
    *snapshot = result;
    return S_OK;
}

HRESULT WINAPI FluidD3D12HookDetach() {
    const std::lock_guard hook_lock(g_hook_mutex);
    const auto installed = g_installed_hook_count.load(std::memory_order_acquire);
    if (installed == 0) {
        return S_FALSE;
    }
    g_detaching.store(true, std::memory_order_release);
    std::vector<HookSlot*> restored;
    {
        const std::lock_guard patch_lock(g_patch_mutex);
        for (size_t index = 0; index < installed; ++index) {
            const auto& slot = g_hook_slots[index];
            if (*slot.slot != slot.hook && *slot.slot != slot.original) {
                g_detaching.store(false, std::memory_order_release);
                return E_UNEXPECTED;
            }
        }
        for (size_t index = 0; index < installed; ++index) {
            auto& slot = g_hook_slots[index];
            if (*slot.slot == slot.original) {
                continue;
            }
            if (!write_pointer(slot.slot, slot.original, slot.hook)) {
                const auto error = GetLastError();
                bool rollback_ok = true;
                for (auto item = restored.rbegin(); item != restored.rend(); ++item) {
                    rollback_ok = write_pointer(
                        (*item)->slot,
                        (*item)->hook,
                        (*item)->original) && rollback_ok;
                }
                g_detaching.store(false, std::memory_order_release);
                return rollback_ok ? HRESULT_FROM_WIN32(error) : E_UNEXPECTED;
            }
            restored.push_back(&slot);
        }
    }
    DWORD waited_ms = 0;
    while (g_active_hook_calls.load(std::memory_order_acquire) != 0 &&
           waited_ms < 5000) {
        Sleep(1);
        ++waited_ms;
    }
    if (g_active_hook_calls.load(std::memory_order_acquire) != 0) {
        const std::lock_guard patch_lock(g_patch_mutex);
        bool rollback_ok = true;
        for (auto item = restored.rbegin(); item != restored.rend(); ++item) {
            rollback_ok = write_pointer(
                (*item)->slot,
                (*item)->hook,
                (*item)->original) && rollback_ok;
        }
        g_detaching.store(false, std::memory_order_release);
        return rollback_ok ? HRESULT_FROM_WIN32(WAIT_TIMEOUT) : E_UNEXPECTED;
    }
    g_installed_hook_count.store(0, std::memory_order_release);
    g_hook_slots = {};
    {
        const std::lock_guard state_lock(g_state_mutex);
        for (auto& source : g_sources) {
            source.resource->Release();
        }
        for (auto& destination : g_destinations) {
            destination.resource->Release();
        }
        for (auto& fence : g_fences) {
            fence.fence->Release();
        }
        for (auto& scope : g_scopes) {
            scope.command_list->Release();
        }
        if (g_queue != nullptr) {
            g_queue->Release();
        }
        g_sources.clear();
        g_destinations.clear();
        g_lanes.clear();
        g_fences.clear();
        g_scopes.clear();
        g_queue = nullptr;
        g_queue_id = 0;
        g_topology = {};
    }
    g_allow_control_policy.store(false, std::memory_order_release);
    clear_original_functions();
    close_event_ring();
    g_detaching.store(false, std::memory_order_release);
    return S_OK;
}

BOOL WINAPI FluidD3D12HookIsAttached() {
    return g_installed_hook_count.load(std::memory_order_acquire) != 0
        ? TRUE
        : FALSE;
}

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) {
    return TRUE;
}
