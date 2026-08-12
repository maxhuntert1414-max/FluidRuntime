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

constexpr size_t kCloseVtableIndex = 9;
constexpr size_t kResetVtableIndex = 10;
constexpr size_t kCopyBufferRegionVtableIndex = 15;
constexpr size_t kCopyTextureRegionVtableIndex = 16;
constexpr size_t kCopyResourceVtableIndex = 17;
constexpr size_t kHookSlotCount = 5;
constexpr std::uint64_t kMaximumUploadRegistrationBytes =
    2 * fluid_d3d12_hook_max_tracked_bytes;

struct HookSlot {
    void** slot{};
    void* original{};
    void* hook{};
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

ID3D12GraphicsCommandList* g_command_list{};
ID3D12Resource* g_upload_resource{};
ID3D12Resource* g_destination_resource{};
std::vector<std::uint8_t> g_upload_snapshot;
std::uint64_t g_destination_bytes{};
std::uint64_t g_max_tracked_copy_bytes{};
std::uint32_t g_max_tracked_resources{};
std::vector<std::uint8_t> g_retained_content;
bool g_cache_valid{};
std::uint64_t g_cache_generation{};
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
std::atomic<std::uint64_t> g_automatic_invalidation_count{0};
std::atomic<std::uint64_t> g_explicit_invalidation_count{0};
std::atomic<std::uint64_t> g_command_list_close_count{0};
std::atomic<std::uint64_t> g_command_list_reset_count{0};

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
    const auto mapping_name = std::wstring(fluid_d3d12_hook_ring_name_prefix) +
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
        const auto map_error = GetLastError();
        CloseHandle(mapping);
        SetLastError(map_error);
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
    const auto valid =
        published_epoch == 1 &&
        processed_epoch == 0 &&
        action_mask == static_cast<LONG64>(
            fluid_hook_control_action_skip_redundant_d3d12_copy_buffer_region) &&
        action_budget >= 1 &&
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
        static_cast<std::uint64_t>(expires_at_qpc));
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
            fluid_hook_control_action_skip_redundant_d3d12_copy_buffer_region ||
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

void invalidate_cache_locked() {
    g_cache_valid = false;
    ++g_cache_generation;
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
    g_automatic_invalidation_count.store(0, std::memory_order_relaxed);
    g_explicit_invalidation_count.store(0, std::memory_order_relaxed);
    g_command_list_close_count.store(0, std::memory_order_relaxed);
    g_command_list_reset_count.store(0, std::memory_order_relaxed);
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
    g_cache_valid = false;
    g_cache_generation = 0;
    g_upload_snapshot.clear();
    g_retained_content.clear();
}

void clear_original_functions() {
    g_original_close.store(nullptr, std::memory_order_release);
    g_original_reset.store(nullptr, std::memory_order_release);
    g_original_copy_buffer_region.store(nullptr, std::memory_order_release);
    g_original_copy_texture_region.store(nullptr, std::memory_order_release);
    g_original_copy_resource.store(nullptr, std::memory_order_release);
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

HRESULT STDMETHODCALLTYPE hooked_close(ID3D12GraphicsCommandList* command_list) {
    const auto original = g_original_close.load(std::memory_order_acquire);
    if (original == nullptr) {
        return E_UNEXPECTED;
    }
    const ActiveHookCall call;
    const auto result = original(command_list);
    if (call.active() && command_list == g_command_list && SUCCEEDED(result)) {
        std::uint64_t generation = 0;
        {
            const std::lock_guard state_lock(g_state_mutex);
            invalidate_cache_locked();
            generation = g_cache_generation;
        }
        g_command_list_close_count.fetch_add(1, std::memory_order_relaxed);
        emit_hook_event(
            FluidHookEventTypeV1::d3d12_command_list_close,
            identity(command_list),
            0,
            0,
            generation);
    }
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
    if (call.active() && command_list == g_command_list && SUCCEEDED(result)) {
        std::uint64_t generation = 0;
        {
            const std::lock_guard state_lock(g_state_mutex);
            invalidate_cache_locked();
            generation = g_cache_generation;
        }
        g_command_list_reset_count.fetch_add(1, std::memory_order_relaxed);
        emit_hook_event(
            FluidHookEventTypeV1::d3d12_command_list_reset,
            identity(command_list),
            0,
            0,
            generation);
    }
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
    if (!call.active() || command_list != g_command_list) {
        original(
            command_list,
            destination,
            destination_offset,
            source,
            source_offset,
            bytes);
        return;
    }

    g_copy_buffer_region_count.fetch_add(1, std::memory_order_relaxed);
    bool tracked = false;
    bool compared = false;
    bool candidate = false;
    bool skipped = false;
    bool automatically_invalidated = false;
    std::uint64_t generation = 0;
    {
        const std::lock_guard state_lock(g_state_mutex);
        const auto source_snapshot_bytes = static_cast<std::uint64_t>(
            g_upload_snapshot.size());
        tracked = destination == g_destination_resource &&
            source == g_upload_resource &&
            !g_upload_snapshot.empty() &&
            destination_offset == 0 &&
            bytes == g_destination_bytes &&
            bytes != 0 &&
            bytes <= g_max_tracked_copy_bytes &&
            g_retained_content.size() == bytes &&
            source_offset <= source_snapshot_bytes &&
            bytes <= source_snapshot_bytes - source_offset;
        if (tracked) {
            const auto* source_bytes = g_upload_snapshot.data() + source_offset;
            if (g_cache_valid && g_retained_content.size() == bytes) {
                compared = true;
                g_exact_comparison_count.fetch_add(1, std::memory_order_relaxed);
                g_exact_comparison_bytes.fetch_add(bytes, std::memory_order_relaxed);
                candidate = std::memcmp(
                    g_retained_content.data(),
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
                        g_retained_content.data(),
                        source_bytes,
                        static_cast<size_t>(bytes));
                    g_cache_valid = true;
                    ++g_cache_generation;
                }
            }
            generation = g_cache_generation;
        } else if (destination == g_destination_resource) {
            invalidate_cache_locked();
            generation = g_cache_generation;
            automatically_invalidated = true;
        }
    }

    if (!tracked) {
        original(
            command_list,
            destination,
            destination_offset,
            source,
            source_offset,
            bytes);
        if (automatically_invalidated) {
            g_automatic_invalidation_count.fetch_add(
                1,
                std::memory_order_relaxed);
            emit_hook_event(
                FluidHookEventTypeV1::d3d12_resource_invalidate,
                identity(destination),
                identity(source),
                bytes,
                generation,
                0,
                static_cast<std::uint32_t>(destination_offset),
                static_cast<std::uint32_t>(source_offset),
                source_offset);
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

    std::uint32_t flags = fluid_hook_event_flag_immutable_upload_source;
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
        FluidHookEventTypeV1::d3d12_copy_buffer_region,
        identity(destination),
        identity(source),
        bytes,
        generation,
        flags,
        static_cast<std::uint32_t>(destination_offset),
        static_cast<std::uint32_t>(source_offset),
        source_offset);
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
    std::uint64_t generation = 0;
    ID3D12Resource* destination_resource =
        destination != nullptr ? destination->pResource : nullptr;
    if (call.active() && command_list == g_command_list &&
        destination_resource == g_destination_resource) {
        {
            const std::lock_guard state_lock(g_state_mutex);
            invalidate_cache_locked();
            generation = g_cache_generation;
        }
        g_automatic_invalidation_count.fetch_add(1, std::memory_order_relaxed);
        invalidated = true;
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
        emit_hook_event(
            FluidHookEventTypeV1::d3d12_resource_invalidate,
            identity(destination_resource),
            source != nullptr ? identity(source->pResource) : 0,
            0,
            generation);
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
    std::uint64_t generation = 0;
    if (call.active() && command_list == g_command_list &&
        destination == g_destination_resource) {
        {
            const std::lock_guard state_lock(g_state_mutex);
            invalidate_cache_locked();
            generation = g_cache_generation;
        }
        g_automatic_invalidation_count.fetch_add(1, std::memory_order_relaxed);
        invalidated = true;
    }
    original(command_list, destination, source);
    if (invalidated) {
        emit_hook_event(
            FluidHookEventTypeV1::d3d12_resource_invalidate,
            identity(destination),
            identity(source),
            g_destination_bytes,
            generation);
    }
}

} // namespace

HRESULT WINAPI FluidD3D12HookAttachEx(
    ID3D12GraphicsCommandList* command_list,
    const FluidD3D12HookAttachOptionsV1* options) {
    if (command_list == nullptr || options == nullptr) {
        return E_POINTER;
    }
    if (options->struct_size < sizeof(FluidD3D12HookAttachOptionsV1) ||
        options->abi_version != fluid_d3d12_hook_attach_options_abi_version ||
        (options->flags & ~fluid_d3d12_hook_attach_flag_allow_control_policy) != 0 ||
        options->reserved0 != 0 ||
        options->reserved1 != 0 ||
        options->max_tracked_copy_bytes == 0 ||
        options->max_tracked_copy_bytes > fluid_d3d12_hook_max_tracked_bytes ||
        options->max_tracked_resources == 0 ||
        options->max_tracked_resources > fluid_d3d12_hook_max_tracked_resources ||
        command_list->GetType() != D3D12_COMMAND_LIST_TYPE_COPY) {
        return E_INVALIDARG;
    }

    const std::lock_guard hook_lock(g_hook_mutex);
    if (g_installed_hook_count.load(std::memory_order_acquire) != 0 ||
        g_attach_completed_once) {
        return HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS);
    }
    if (!pin_hook_module()) {
        return HRESULT_FROM_WIN32(GetLastError());
    }

    auto** vtable = *reinterpret_cast<void***>(command_list);
    std::array<HookSlot, kHookSlotCount> slots{
        HookSlot{
            &vtable[kCloseVtableIndex],
            vtable[kCloseVtableIndex],
            reinterpret_cast<void*>(hooked_close)},
        HookSlot{
            &vtable[kResetVtableIndex],
            vtable[kResetVtableIndex],
            reinterpret_cast<void*>(hooked_reset)},
        HookSlot{
            &vtable[kCopyBufferRegionVtableIndex],
            vtable[kCopyBufferRegionVtableIndex],
            reinterpret_cast<void*>(hooked_copy_buffer_region)},
        HookSlot{
            &vtable[kCopyTextureRegionVtableIndex],
            vtable[kCopyTextureRegionVtableIndex],
            reinterpret_cast<void*>(hooked_copy_texture_region)},
        HookSlot{
            &vtable[kCopyResourceVtableIndex],
            vtable[kCopyResourceVtableIndex],
            reinterpret_cast<void*>(hooked_copy_resource)},
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
    g_allow_control_policy.store(
        (options->flags & fluid_d3d12_hook_attach_flag_allow_control_policy) != 0,
        std::memory_order_release);
    g_max_tracked_copy_bytes = options->max_tracked_copy_bytes;
    g_max_tracked_resources = options->max_tracked_resources;
    reset_metrics_and_control();
    if (!initialize_event_ring()) {
        const auto error = GetLastError();
        clear_original_functions();
        g_allow_control_policy.store(false, std::memory_order_release);
        return HRESULT_FROM_WIN32(error);
    }

    command_list->AddRef();
    g_command_list = command_list;
    const std::lock_guard patch_lock(g_patch_mutex);
    g_detaching.store(false, std::memory_order_release);
    size_t patched_count = 0;
    for (; patched_count < slots.size(); ++patched_count) {
        const auto& slot = slots[patched_count];
        if (!write_pointer(slot.slot, slot.hook, slot.original)) {
            const auto error = GetLastError();
            while (patched_count != 0) {
                --patched_count;
                const auto& patched = slots[patched_count];
                write_pointer(patched.slot, patched.original, patched.hook);
            }
            g_command_list->Release();
            g_command_list = nullptr;
            close_event_ring();
            clear_original_functions();
            g_allow_control_policy.store(false, std::memory_order_release);
            return HRESULT_FROM_WIN32(error);
        }
    }

    g_hook_slots = slots;
    g_installed_hook_count.store(slots.size(), std::memory_order_release);
    g_attach_completed_once = true;
    return S_OK;
}

HRESULT WINAPI FluidD3D12HookRegisterUploadBuffer(
    ID3D12Resource* resource,
    const void* immutable_cpu_shadow,
    std::uint64_t shadow_bytes) {
    if (resource == nullptr || immutable_cpu_shadow == nullptr) {
        return E_POINTER;
    }
    if (shadow_bytes == 0 || shadow_bytes > kMaximumUploadRegistrationBytes ||
        !validates_buffer(resource, D3D12_HEAP_TYPE_UPLOAD, shadow_bytes, false)) {
        return E_INVALIDARG;
    }

    const std::lock_guard hook_lock(g_hook_mutex);
    if (g_installed_hook_count.load(std::memory_order_acquire) == 0 ||
        g_detaching.load(std::memory_order_acquire)) {
        return S_FALSE;
    }
    const std::lock_guard state_lock(g_state_mutex);
    if (g_upload_resource != nullptr) {
        return HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS);
    }
    std::vector<std::uint8_t> snapshot;
    try {
        snapshot.resize(static_cast<size_t>(shadow_bytes));
    } catch (const std::bad_alloc&) {
        return E_OUTOFMEMORY;
    }
    std::memcpy(snapshot.data(), immutable_cpu_shadow, snapshot.size());
    resource->AddRef();
    g_upload_resource = resource;
    g_upload_snapshot = std::move(snapshot);
    g_source_registration_count.fetch_add(1, std::memory_order_relaxed);
    return S_OK;
}

HRESULT WINAPI FluidD3D12HookRegisterCopyOnlyBuffer(
    ID3D12Resource* resource,
    std::uint64_t resource_bytes) {
    if (resource == nullptr) {
        return E_POINTER;
    }
    const std::lock_guard hook_lock(g_hook_mutex);
    if (g_installed_hook_count.load(std::memory_order_acquire) == 0 ||
        g_detaching.load(std::memory_order_acquire)) {
        return S_FALSE;
    }
    if (resource_bytes == 0 || resource_bytes > g_max_tracked_copy_bytes ||
        !validates_buffer(
            resource,
            D3D12_HEAP_TYPE_DEFAULT,
            resource_bytes,
            true)) {
        return E_INVALIDARG;
    }
    const std::lock_guard state_lock(g_state_mutex);
    if (g_destination_resource != nullptr || g_max_tracked_resources != 1) {
        return HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS);
    }
    std::vector<std::uint8_t> retained_content;
    try {
        retained_content.resize(static_cast<size_t>(resource_bytes));
    } catch (const std::bad_alloc&) {
        return E_OUTOFMEMORY;
    }
    resource->AddRef();
    g_destination_resource = resource;
    g_destination_bytes = resource_bytes;
    g_retained_content = std::move(retained_content);
    g_destination_registration_count.fetch_add(1, std::memory_order_relaxed);
    return S_OK;
}

HRESULT WINAPI FluidD3D12HookInvalidateResource(ID3D12Resource* resource) {
    if (resource == nullptr) {
        return E_POINTER;
    }
    const std::lock_guard hook_lock(g_hook_mutex);
    if (g_installed_hook_count.load(std::memory_order_acquire) == 0 ||
        g_detaching.load(std::memory_order_acquire)) {
        return S_FALSE;
    }

    std::uint64_t generation = 0;
    {
        const std::lock_guard state_lock(g_state_mutex);
        if (resource != g_destination_resource) {
            return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        }
        invalidate_cache_locked();
        generation = g_cache_generation;
    }
    g_explicit_invalidation_count.fetch_add(1, std::memory_order_relaxed);
    emit_hook_event(
        FluidHookEventTypeV1::d3d12_resource_invalidate,
        identity(resource),
        0,
        g_destination_bytes,
        generation,
        fluid_hook_event_flag_explicit_invalidation);
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
                for (auto item = restored.rbegin(); item != restored.rend(); ++item) {
                    write_pointer((*item)->slot, (*item)->hook, (*item)->original);
                }
                g_detaching.store(false, std::memory_order_release);
                return HRESULT_FROM_WIN32(error);
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
        for (auto item = restored.rbegin(); item != restored.rend(); ++item) {
            write_pointer((*item)->slot, (*item)->hook, (*item)->original);
        }
        g_detaching.store(false, std::memory_order_release);
        return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
    }

    g_installed_hook_count.store(0, std::memory_order_release);
    g_hook_slots = {};
    {
        const std::lock_guard state_lock(g_state_mutex);
        if (g_upload_resource != nullptr) {
            g_upload_resource->Release();
        }
        if (g_destination_resource != nullptr) {
            g_destination_resource->Release();
        }
        if (g_command_list != nullptr) {
            g_command_list->Release();
        }
        g_upload_resource = nullptr;
        g_destination_resource = nullptr;
        g_command_list = nullptr;
        g_upload_snapshot.clear();
        g_destination_bytes = 0;
        g_cache_valid = false;
        g_retained_content.clear();
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

HRESULT WINAPI FluidD3D12HookReadSnapshot(FluidD3D12HookSnapshotV1* snapshot) {
    if (snapshot == nullptr) {
        return E_POINTER;
    }
    if (snapshot->struct_size < sizeof(FluidD3D12HookSnapshotV1)) {
        return E_INVALIDARG;
    }

    const std::lock_guard hook_lock(g_hook_mutex);
    const std::lock_guard state_lock(g_state_mutex);
    FluidD3D12HookSnapshotV1 result{};
    result.struct_size = sizeof(result);
    result.abi_version = fluid_d3d12_hook_snapshot_abi_version;
    result.attached = g_installed_hook_count.load(std::memory_order_acquire) != 0;
    result.command_list_identity = identity(g_command_list);
    result.upload_resource_identity = identity(g_upload_resource);
    result.source_snapshot_bytes = g_upload_snapshot.size();
    result.destination_resource_identity = identity(g_destination_resource);
    result.tracked_resource_bytes = g_destination_bytes;
    result.copy_buffer_region_count =
        g_copy_buffer_region_count.load(std::memory_order_relaxed);
    result.tracked_copy_count = g_tracked_copy_count.load(std::memory_order_relaxed);
    result.tracked_copy_bytes = g_tracked_copy_bytes.load(std::memory_order_relaxed);
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
    result.automatic_invalidation_count =
        g_automatic_invalidation_count.load(std::memory_order_relaxed);
    result.explicit_invalidation_count =
        g_explicit_invalidation_count.load(std::memory_order_relaxed);
    result.command_list_close_count =
        g_command_list_close_count.load(std::memory_order_relaxed);
    result.command_list_reset_count =
        g_command_list_reset_count.load(std::memory_order_relaxed);
    result.cache_generation = g_cache_generation;
    result.cache_bytes = g_cache_valid ? g_retained_content.size() : 0;
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

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) {
    return TRUE;
}
