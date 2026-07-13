#include "fluidruntime_hook_api.h"

#include <d3d11.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <deque>
#include <functional>
#include <mutex>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace {

using PresentFunction = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
using CreateBufferFunction = HRESULT(STDMETHODCALLTYPE*)(
    ID3D11Device*,
    const D3D11_BUFFER_DESC*,
    const D3D11_SUBRESOURCE_DATA*,
    ID3D11Buffer**);
using CreateTexture2DFunction = HRESULT(STDMETHODCALLTYPE*)(
    ID3D11Device*,
    const D3D11_TEXTURE2D_DESC*,
    const D3D11_SUBRESOURCE_DATA*,
    ID3D11Texture2D**);
using MapFunction = HRESULT(STDMETHODCALLTYPE*)(
    ID3D11DeviceContext*,
    ID3D11Resource*,
    UINT,
    D3D11_MAP,
    UINT,
    D3D11_MAPPED_SUBRESOURCE*);
using UnmapFunction = void(STDMETHODCALLTYPE*)(
    ID3D11DeviceContext*,
    ID3D11Resource*,
    UINT);
using CopyResourceFunction = void(STDMETHODCALLTYPE*)(
    ID3D11DeviceContext*,
    ID3D11Resource*,
    ID3D11Resource*);
using CopySubresourceRegionFunction = void(STDMETHODCALLTYPE*)(
    ID3D11DeviceContext*,
    ID3D11Resource*,
    UINT,
    UINT,
    UINT,
    UINT,
    ID3D11Resource*,
    UINT,
    const D3D11_BOX*);
using UpdateSubresourceFunction = void(STDMETHODCALLTYPE*)(
    ID3D11DeviceContext*,
    ID3D11Resource*,
    UINT,
    const D3D11_BOX*,
    const void*,
    UINT,
    UINT);
using ReleaseFunction = ULONG(STDMETHODCALLTYPE*)(IUnknown*);

constexpr size_t kReleaseVtableIndex = 2;
constexpr size_t kPresentVtableIndex = 8;
constexpr size_t kCreateBufferVtableIndex = 3;
constexpr size_t kCreateTexture2DVtableIndex = 5;
constexpr size_t kMapVtableIndex = 14;
constexpr size_t kUnmapVtableIndex = 15;
constexpr size_t kCopySubresourceRegionVtableIndex = 46;
constexpr size_t kCopyResourceVtableIndex = 47;
constexpr size_t kUpdateSubresourceVtableIndex = 48;
constexpr size_t kHookSlotCount = 8;
constexpr size_t kRetiredResourceIdentityCapacity = 4096;

struct HookSlot {
    void** slot{};
    void* original{};
    void* hook{};
};

struct ResourceState {
    std::uint64_t resource_id{};
    std::uint64_t size_bytes{};
    std::uint64_t generation{};
    std::vector<std::uint64_t> subresource_generations;
    bool provenance_trusted{};
};

struct LastCopy {
    std::uint64_t source_resource_id{};
    std::uint64_t source_generation{};
    std::uint64_t destination_generation{};
};

struct SubresourceKey {
    ID3D11Resource* resource{};
    UINT subresource{};

    bool operator==(const SubresourceKey&) const = default;
};

struct SubresourceKeyHash {
    size_t operator()(const SubresourceKey& key) const noexcept {
        const auto pointer_hash = std::hash<ID3D11Resource*>{}(key.resource);
        const auto subresource_hash = std::hash<UINT>{}(key.subresource);
        return pointer_hash ^ (subresource_hash + 0x9E3779B9U +
            (pointer_hash << 6) + (pointer_hash >> 2));
    }
};

struct CopyRegionIdentity {
    UINT destination_subresource{};
    UINT destination_x{};
    UINT destination_y{};
    UINT destination_z{};
    UINT source_subresource{};
    bool has_source_box{};
    D3D11_BOX source_box{};

    bool operator==(const CopyRegionIdentity& other) const {
        return destination_subresource == other.destination_subresource &&
            destination_x == other.destination_x &&
            destination_y == other.destination_y &&
            destination_z == other.destination_z &&
            source_subresource == other.source_subresource &&
            has_source_box == other.has_source_box &&
            (!has_source_box ||
                (source_box.left == other.source_box.left &&
                 source_box.top == other.source_box.top &&
                 source_box.front == other.source_box.front &&
                 source_box.right == other.source_box.right &&
                 source_box.bottom == other.source_box.bottom &&
                 source_box.back == other.source_box.back));
    }
};

std::uint64_t copy_region_key(const CopyRegionIdentity& region) {
    constexpr std::uint64_t offset_basis = 14695981039346656037ULL;
    constexpr std::uint64_t prime = 1099511628211ULL;
    auto hash = offset_basis;
    const auto mix = [&hash](std::uint32_t value) {
        constexpr std::uint64_t local_prime = 1099511628211ULL;
        for (int shift = 0; shift < 32; shift += 8) {
            hash ^= static_cast<unsigned char>(value >> shift);
            hash *= local_prime;
        }
    };
    mix(region.destination_subresource);
    mix(region.destination_x);
    mix(region.destination_y);
    mix(region.destination_z);
    mix(region.source_subresource);
    mix(region.has_source_box ? 1U : 0U);
    if (region.has_source_box) {
        mix(region.source_box.left);
        mix(region.source_box.top);
        mix(region.source_box.front);
        mix(region.source_box.right);
        mix(region.source_box.bottom);
        mix(region.source_box.back);
    }
    return hash == 0 ? prime : hash;
}

struct LastSubresourceCopy {
    std::uint64_t source_resource_id{};
    std::uint64_t source_generation{};
    std::uint64_t destination_generation{};
    CopyRegionIdentity region;
};

struct ResourceRegistration {
    std::uint64_t resource_id{};
    std::uint64_t previous_resource_id{};
    bool reused{};
    bool reuse_without_retire{};
};

std::mutex g_hook_mutex;
std::mutex g_patch_mutex;
std::mutex g_resource_mutex;
std::array<HookSlot, kHookSlotCount> g_hook_slots{};
std::vector<HookSlot> g_release_hook_slots;
size_t g_installed_hook_count{};
std::atomic<bool> g_detaching{false};
std::atomic<bool> g_track_resource_lifetime{false};

std::atomic<unsigned long> g_active_hook_calls{0};
std::atomic<PresentFunction> g_original_present{nullptr};
std::atomic<CreateBufferFunction> g_original_create_buffer{nullptr};
std::atomic<CreateTexture2DFunction> g_original_create_texture2d{nullptr};
std::atomic<MapFunction> g_original_map{nullptr};
std::atomic<UnmapFunction> g_original_unmap{nullptr};
std::atomic<CopySubresourceRegionFunction> g_original_copy_subresource_region{nullptr};
std::atomic<CopyResourceFunction> g_original_copy_resource{nullptr};
std::atomic<UpdateSubresourceFunction> g_original_update_subresource{nullptr};

std::atomic<std::uint64_t> g_present_count{0};
std::atomic<std::uint64_t> g_create_buffer_count{0};
std::atomic<std::uint64_t> g_buffer_bytes_requested{0};
std::atomic<std::uint64_t> g_create_texture2d_count{0};
std::atomic<std::uint64_t> g_texture_bytes_estimated{0};
std::atomic<std::uint64_t> g_map_write_count{0};
std::atomic<std::uint64_t> g_unmap_write_count{0};
std::atomic<std::uint64_t> g_update_subresource_count{0};
std::atomic<std::uint64_t> g_copy_resource_count{0};
std::atomic<std::uint64_t> g_copy_resource_bytes_estimated{0};
std::atomic<std::uint64_t> g_copy_subresource_region_count{0};
std::atomic<std::uint64_t> g_copy_subresource_region_bytes_estimated{0};
std::atomic<std::uint64_t> g_redundant_subresource_copy_candidate_count{0};
std::atomic<std::uint64_t> g_redundant_subresource_copy_bytes_estimated{0};
std::atomic<std::uint64_t> g_redundant_copy_candidate_count{0};
std::atomic<std::uint64_t> g_redundant_copy_bytes_estimated{0};
std::atomic<std::uint64_t> g_forwarded_copy_count{0};
std::atomic<std::uint64_t> g_forwarded_copy_bytes_estimated{0};
std::atomic<std::uint64_t> g_skipped_copy_count{0};
std::atomic<std::uint64_t> g_skipped_copy_bytes_estimated{0};
std::atomic<std::uint64_t> g_hook_refresh_count{0};
std::atomic<std::uint64_t> g_hook_refresh_failure_count{0};
std::atomic<std::uint64_t> g_resource_retire_count{0};
std::atomic<std::uint64_t> g_resource_reuse_count{0};
std::atomic<std::uint64_t> g_provenance_failure_count{0};
std::atomic<std::uint64_t> g_resource_destroy_count{0};
std::atomic<std::uint64_t> g_release_hook_failure_count{0};
std::atomic<std::uint32_t> g_max_skipped_copy_count{0};

std::unordered_map<ID3D11Resource*, ResourceState> g_resources;
std::unordered_map<ID3D11Resource*, LastCopy> g_last_copies;
std::unordered_map<SubresourceKey, LastSubresourceCopy, SubresourceKeyHash>
    g_last_subresource_copies;
std::unordered_set<SubresourceKey, SubresourceKeyHash> g_pending_write_maps;
std::unordered_map<ID3D11Resource*, std::uint64_t> g_retired_resources;
std::deque<std::pair<ID3D11Resource*, std::uint64_t>> g_retired_resource_order;
std::uint64_t g_next_resource_id{};

HANDLE g_ring_mapping{};
FluidHookRingHeaderV1* g_ring_header{};
FluidHookEventV1* g_ring_events{};

ULONG STDMETHODCALLTYPE hooked_release(IUnknown* object);

class ActiveHookCall {
public:
    ActiveHookCall() {
        g_active_hook_calls.fetch_add(1, std::memory_order_acquire);
    }
    ~ActiveHookCall() {
        g_active_hook_calls.fetch_sub(1, std::memory_order_release);
    }

    ActiveHookCall(const ActiveHookCall&) = delete;
    ActiveHookCall& operator=(const ActiveHookCall&) = delete;
};

bool write_pointer(void** slot, void* value, void* rollback_value) {
    DWORD old_protection{};
    if (!VirtualProtect(
            slot,
            sizeof(void*),
            PAGE_EXECUTE_READWRITE,
            &old_protection)) {
        return false;
    }

    *slot = value;
    FlushInstructionCache(GetCurrentProcess(), slot, sizeof(void*));

    DWORD ignored{};
    if (VirtualProtect(slot, sizeof(void*), old_protection, &ignored) != FALSE) {
        return true;
    }

    const auto restore_error = GetLastError();
    *slot = rollback_value;
    FlushInstructionCache(GetCurrentProcess(), slot, sizeof(void*));
    VirtualProtect(slot, sizeof(void*), old_protection, &ignored);
    SetLastError(restore_error);
    return false;
}

bool install_release_hook(IUnknown* object) {
    if (!g_track_resource_lifetime.load(std::memory_order_acquire)) {
        return true;
    }
    if (object == nullptr) {
        g_release_hook_failure_count.fetch_add(1, std::memory_order_relaxed);
        return false;
    }

    auto** vtable = *reinterpret_cast<void***>(object);
    auto** slot = &vtable[kReleaseVtableIndex];
    const auto hook = reinterpret_cast<void*>(hooked_release);
    const std::lock_guard patch_lock(g_patch_mutex);
    if (g_detaching.load(std::memory_order_acquire)) {
        g_release_hook_failure_count.fetch_add(1, std::memory_order_relaxed);
        return false;
    }

    const auto existing = std::find_if(
        g_release_hook_slots.begin(),
        g_release_hook_slots.end(),
        [slot](const HookSlot& candidate) { return candidate.slot == slot; });
    if (existing != g_release_hook_slots.end()) {
        if (*slot == hook) {
            return true;
        }
        g_release_hook_failure_count.fetch_add(1, std::memory_order_relaxed);
        return false;
    }

    const auto original = *slot;
    if (original == nullptr || original == hook) {
        g_release_hook_failure_count.fetch_add(1, std::memory_order_relaxed);
        return false;
    }
    g_release_hook_slots.push_back(HookSlot{
        .slot = slot,
        .original = original,
        .hook = hook,
    });
    if (!write_pointer(slot, hook, original)) {
        g_release_hook_slots.pop_back();
        g_release_hook_failure_count.fetch_add(1, std::memory_order_relaxed);
        return false;
    }
    return true;
}

bool initialize_event_ring() {
    const auto mapping_name = std::wstring(fluid_hook_ring_name_prefix)
        + std::to_wstring(GetCurrentProcessId());
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
    auto* events = reinterpret_cast<FluidHookEventV1*>(
        view + sizeof(FluidHookRingHeaderV1));
    LARGE_INTEGER frequency{};
    QueryPerformanceFrequency(&frequency);
    header->magic = 0;
    header->abi_version = fluid_hook_ring_abi_version;
    header->capacity = fluid_hook_ring_capacity;
    header->event_size = sizeof(FluidHookEventV1);
    header->next_sequence = 0;
    header->reader_sequence = 0;
    header->overrun_count = 0;
    header->qpc_frequency = static_cast<std::uint64_t>(frequency.QuadPart);
    header->process_id = GetCurrentProcessId();
    for (std::uint32_t index = 0; index < fluid_hook_ring_capacity; ++index) {
        events[index].sequence = -1;
    }

    MemoryBarrier();
    InterlockedExchange(
        reinterpret_cast<volatile LONG*>(&header->magic),
        static_cast<LONG>(fluid_hook_ring_magic));

    g_ring_mapping = mapping;
    g_ring_header = header;
    g_ring_events = events;
    return true;
}

void close_event_ring() {
    auto* header = g_ring_header;
    const auto mapping = g_ring_mapping;
    g_ring_header = nullptr;
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

void update_context_original(size_t slot_index, void* original) {
    switch (slot_index) {
    case 3:
        g_original_map.store(
            reinterpret_cast<MapFunction>(original),
            std::memory_order_release);
        break;
    case 4:
        g_original_unmap.store(
            reinterpret_cast<UnmapFunction>(original),
            std::memory_order_release);
        break;
    case 5:
        g_original_copy_subresource_region.store(
            reinterpret_cast<CopySubresourceRegionFunction>(original),
            std::memory_order_release);
        break;
    case 6:
        g_original_copy_resource.store(
            reinterpret_cast<CopyResourceFunction>(original),
            std::memory_order_release);
        break;
    case 7:
        g_original_update_subresource.store(
            reinterpret_cast<UpdateSubresourceFunction>(original),
            std::memory_order_release);
        break;
    default:
        break;
    }
}

void refresh_context_hook_slots() {
    constexpr size_t first_context_slot = 3;
    const std::lock_guard patch_lock(g_patch_mutex);
    if (g_detaching.load(std::memory_order_acquire) ||
        g_installed_hook_count < kHookSlotCount) {
        return;
    }

    for (size_t index = first_context_slot; index < kHookSlotCount; ++index) {
        auto& slot = g_hook_slots[index];
        if (*slot.slot == slot.hook) {
            continue;
        }
        const auto transitioned_original = *slot.slot;
        const auto points_to_another_hook = std::any_of(
            g_hook_slots.begin(),
            g_hook_slots.begin() + static_cast<std::ptrdiff_t>(g_installed_hook_count),
            [transitioned_original](const HookSlot& candidate) {
                return candidate.hook == transitioned_original;
            });
        if (transitioned_original == nullptr || points_to_another_hook) {
            g_hook_refresh_failure_count.fetch_add(1, std::memory_order_relaxed);
            continue;
        }
        const auto previous_original = slot.original;
        slot.original = transitioned_original;
        update_context_original(index, transitioned_original);
        if (!write_pointer(slot.slot, slot.hook, transitioned_original)) {
            slot.original = previous_original;
            update_context_original(index, previous_original);
            g_hook_refresh_failure_count.fetch_add(1, std::memory_order_relaxed);
            continue;
        }
        const auto refresh_count =
            g_hook_refresh_count.fetch_add(1, std::memory_order_relaxed) + 1;
        emit_hook_event(
            FluidHookEventTypeV1::hook_refresh,
            index,
            0,
            0,
            refresh_count);
    }
}

std::uint64_t bytes_per_pixel(DXGI_FORMAT format) {
    switch (format) {
    case DXGI_FORMAT_R32G32B32A32_FLOAT:
    case DXGI_FORMAT_R32G32B32A32_UINT:
    case DXGI_FORMAT_R32G32B32A32_SINT:
        return 16;
    case DXGI_FORMAT_R16G16B16A16_FLOAT:
    case DXGI_FORMAT_R16G16B16A16_UNORM:
    case DXGI_FORMAT_R16G16B16A16_UINT:
    case DXGI_FORMAT_R32G32_FLOAT:
    case DXGI_FORMAT_R32G32_UINT:
        return 8;
    case DXGI_FORMAT_R8G8B8A8_UNORM:
    case DXGI_FORMAT_R8G8B8A8_UINT:
    case DXGI_FORMAT_B8G8R8A8_UNORM:
    case DXGI_FORMAT_R16G16_FLOAT:
    case DXGI_FORMAT_R32_FLOAT:
    case DXGI_FORMAT_R32_UINT:
    case DXGI_FORMAT_D32_FLOAT:
        return 4;
    case DXGI_FORMAT_R8G8_UNORM:
    case DXGI_FORMAT_R16_FLOAT:
    case DXGI_FORMAT_R16_UINT:
        return 2;
    case DXGI_FORMAT_R8_UNORM:
    case DXGI_FORMAT_A8_UNORM:
        return 1;
    default:
        return 0;
    }
}

UINT texture_mip_levels(const D3D11_TEXTURE2D_DESC& description) {
    if (description.MipLevels != 0) {
        return description.MipLevels;
    }
    UINT mip_levels = 1;
    for (auto dimension = std::max(description.Width, description.Height);
         dimension > 1;
         dimension >>= 1) {
        ++mip_levels;
    }
    return mip_levels;
}

std::uint64_t estimate_texture_bytes(const D3D11_TEXTURE2D_DESC& description) {
    const auto pixel_bytes = bytes_per_pixel(description.Format);
    if (pixel_bytes == 0) {
        return 0;
    }

    auto width = std::max(1U, description.Width);
    auto height = std::max(1U, description.Height);
    const auto mip_levels = texture_mip_levels(description);

    std::uint64_t total = 0;
    for (UINT mip = 0; mip < mip_levels; ++mip) {
        total += static_cast<std::uint64_t>(width) * height * pixel_bytes;
        width = std::max(1U, width >> 1);
        height = std::max(1U, height >> 1);
    }
    return total
        * std::max(1U, description.ArraySize)
        * std::max(1U, description.SampleDesc.Count);
}

UINT query_subresource_count(ID3D11Resource* resource) {
    if (resource == nullptr) {
        return 0;
    }
    D3D11_RESOURCE_DIMENSION dimension{};
    resource->GetType(&dimension);
    if (dimension == D3D11_RESOURCE_DIMENSION_BUFFER) {
        return 1;
    }
    if (dimension == D3D11_RESOURCE_DIMENSION_TEXTURE2D) {
        D3D11_TEXTURE2D_DESC description{};
        static_cast<ID3D11Texture2D*>(resource)->GetDesc(&description);
        return texture_mip_levels(description) * std::max(1U, description.ArraySize);
    }
    return 0;
}

std::uint64_t query_subresource_size(
    ID3D11Resource* resource,
    UINT subresource) {
    if (resource == nullptr) {
        return 0;
    }
    D3D11_RESOURCE_DIMENSION dimension{};
    resource->GetType(&dimension);
    if (dimension == D3D11_RESOURCE_DIMENSION_BUFFER) {
        if (subresource != 0) {
            return 0;
        }
        D3D11_BUFFER_DESC description{};
        static_cast<ID3D11Buffer*>(resource)->GetDesc(&description);
        return description.ByteWidth;
    }
    if (dimension == D3D11_RESOURCE_DIMENSION_TEXTURE2D) {
        D3D11_TEXTURE2D_DESC description{};
        static_cast<ID3D11Texture2D*>(resource)->GetDesc(&description);
        const auto mip_levels = texture_mip_levels(description);
        if (subresource >= mip_levels * std::max(1U, description.ArraySize)) {
            return 0;
        }
        const auto mip = subresource % mip_levels;
        const auto width = std::max(1U, description.Width >> mip);
        const auto height = std::max(1U, description.Height >> mip);
        return static_cast<std::uint64_t>(width) * height *
            bytes_per_pixel(description.Format) *
            std::max(1U, description.SampleDesc.Count);
    }
    return 0;
}

bool source_box_is_empty(const D3D11_BOX* source_box) {
    return source_box != nullptr &&
        (source_box->left >= source_box->right ||
         source_box->top >= source_box->bottom ||
         source_box->front >= source_box->back);
}

std::uint64_t estimate_copy_region_bytes(
    ID3D11Resource* source,
    UINT source_subresource,
    const D3D11_BOX* source_box) {
    if (source == nullptr || source_box_is_empty(source_box)) {
        return 0;
    }
    if (source_box == nullptr) {
        return query_subresource_size(source, source_subresource);
    }

    D3D11_RESOURCE_DIMENSION dimension{};
    source->GetType(&dimension);
    if (dimension == D3D11_RESOURCE_DIMENSION_BUFFER) {
        return source_subresource == 0
            ? static_cast<std::uint64_t>(source_box->right - source_box->left)
            : 0;
    }
    if (dimension == D3D11_RESOURCE_DIMENSION_TEXTURE2D) {
        D3D11_TEXTURE2D_DESC description{};
        static_cast<ID3D11Texture2D*>(source)->GetDesc(&description);
        const auto pixel_bytes = bytes_per_pixel(description.Format);
        if (pixel_bytes == 0) {
            return 0;
        }
        return static_cast<std::uint64_t>(source_box->right - source_box->left) *
            (source_box->bottom - source_box->top) *
            (source_box->back - source_box->front) * pixel_bytes *
            std::max(1U, description.SampleDesc.Count);
    }
    return 0;
}

std::uint64_t query_resource_size(ID3D11Resource* resource) {
    if (resource == nullptr) {
        return 0;
    }

    D3D11_RESOURCE_DIMENSION dimension{};
    resource->GetType(&dimension);
    if (dimension == D3D11_RESOURCE_DIMENSION_BUFFER) {
        D3D11_BUFFER_DESC description{};
        static_cast<ID3D11Buffer*>(resource)->GetDesc(&description);
        return description.ByteWidth;
    }
    if (dimension == D3D11_RESOURCE_DIMENSION_TEXTURE2D) {
        D3D11_TEXTURE2D_DESC description{};
        static_cast<ID3D11Texture2D*>(resource)->GetDesc(&description);
        return estimate_texture_bytes(description);
    }
    return 0;
}

void erase_resource_provenance_locked(
    ID3D11Resource* resource,
    std::uint64_t resource_id) {
    std::erase_if(g_pending_write_maps, [resource](const SubresourceKey& key) {
        return key.resource == resource;
    });
    g_last_copies.erase(resource);
    std::erase_if(g_last_copies, [resource_id](const auto& item) {
        return item.second.source_resource_id == resource_id;
    });
    std::erase_if(g_last_subresource_copies, [resource, resource_id](const auto& item) {
        return item.first.resource == resource ||
            item.second.source_resource_id == resource_id;
    });
}

void remember_retired_resource_locked(
    ID3D11Resource* resource,
    std::uint64_t resource_id) {
    g_retired_resources[resource] = resource_id;
    g_retired_resource_order.emplace_back(resource, resource_id);
    while (g_retired_resource_order.size() > kRetiredResourceIdentityCapacity) {
        const auto [old_resource, old_id] = g_retired_resource_order.front();
        g_retired_resource_order.pop_front();
        const auto current = g_retired_resources.find(old_resource);
        if (current != g_retired_resources.end() && current->second == old_id) {
            g_retired_resources.erase(current);
        }
    }
}

bool remove_resource_locked(
    ID3D11Resource* resource,
    std::uint64_t expected_resource_id,
    ResourceState& removed) {
    const auto current = g_resources.find(resource);
    if (current == g_resources.end() ||
        current->second.resource_id != expected_resource_id) {
        return false;
    }
    removed = current->second;
    erase_resource_provenance_locked(resource, removed.resource_id);
    g_resources.erase(current);
    remember_retired_resource_locked(resource, removed.resource_id);
    return true;
}

ResourceRegistration register_resource_locked(
    ID3D11Resource* resource,
    std::uint64_t size_bytes,
    std::uint64_t generation,
    UINT subresource_count,
    bool provenance_trusted) {
    ResourceRegistration registration;
    const auto active = g_resources.find(resource);
    if (active != g_resources.end()) {
        registration.previous_resource_id = active->second.resource_id;
        registration.reused = true;
        registration.reuse_without_retire = true;
        erase_resource_provenance_locked(resource, active->second.resource_id);
        g_resources.erase(active);
        g_provenance_failure_count.fetch_add(1, std::memory_order_relaxed);
    } else {
        const auto retired = g_retired_resources.find(resource);
        if (retired != g_retired_resources.end()) {
            registration.previous_resource_id = retired->second;
            registration.reused = true;
            g_retired_resources.erase(retired);
        }
    }

    ResourceState state{
        .resource_id = ++g_next_resource_id,
        .size_bytes = size_bytes,
        .generation = generation,
        .subresource_generations = std::vector<std::uint64_t>(
            subresource_count,
            generation),
        .provenance_trusted = provenance_trusted && !registration.reuse_without_retire,
    };
    registration.resource_id = state.resource_id;
    g_resources.emplace(resource, state);
    if (registration.reused) {
        g_resource_reuse_count.fetch_add(1, std::memory_order_relaxed);
    }
    return registration;
}

ResourceState& ensure_resource_locked(ID3D11Resource* resource) {
    const auto [iterator, inserted] = g_resources.try_emplace(resource);
    if (inserted) {
        iterator->second.resource_id = ++g_next_resource_id;
        iterator->second.size_bytes = query_resource_size(resource);
        iterator->second.subresource_generations.resize(
            query_subresource_count(resource));
        iterator->second.provenance_trusted = false;
        g_provenance_failure_count.fetch_add(1, std::memory_order_relaxed);
    }
    return iterator->second;
}

void mark_resource_written_locked(ID3D11Resource* resource) {
    auto& state = ensure_resource_locked(resource);
    ++state.generation;
    std::fill(
        state.subresource_generations.begin(),
        state.subresource_generations.end(),
        state.generation);
    g_last_copies.erase(resource);
    std::erase_if(g_last_subresource_copies, [resource](const auto& item) {
        return item.first.resource == resource;
    });
}

bool get_subresource_generation_locked(
    ID3D11Resource* resource,
    UINT subresource,
    ResourceState*& state,
    std::uint64_t& generation) {
    state = &ensure_resource_locked(resource);
    if (subresource >= state->subresource_generations.size()) {
        state->provenance_trusted = false;
        erase_resource_provenance_locked(resource, state->resource_id);
        g_provenance_failure_count.fetch_add(1, std::memory_order_relaxed);
        generation = state->generation;
        return false;
    }
    generation = state->subresource_generations[subresource];
    return true;
}

bool mark_subresource_written_locked(
    ID3D11Resource* resource,
    UINT subresource,
    std::uint64_t& generation) {
    ResourceState* state = nullptr;
    if (!get_subresource_generation_locked(
            resource,
            subresource,
            state,
            generation)) {
        ++state->generation;
        generation = state->generation;
        return false;
    }
    ++state->generation;
    generation = state->generation;
    state->subresource_generations[subresource] = generation;
    g_last_copies.erase(resource);
    g_last_subresource_copies.erase(SubresourceKey{resource, subresource});
    return true;
}

ULONG STDMETHODCALLTYPE hooked_release(IUnknown* object) {
    const ActiveHookCall active_call;
    if (object == nullptr) {
        g_release_hook_failure_count.fetch_add(1, std::memory_order_relaxed);
        return 1;
    }

    ReleaseFunction original = nullptr;
    {
        auto** vtable = *reinterpret_cast<void***>(object);
        auto** slot = &vtable[kReleaseVtableIndex];
        const std::lock_guard patch_lock(g_patch_mutex);
        const auto installed = std::find_if(
            g_release_hook_slots.begin(),
            g_release_hook_slots.end(),
            [slot](const HookSlot& candidate) { return candidate.slot == slot; });
        if (installed != g_release_hook_slots.end()) {
            original = reinterpret_cast<ReleaseFunction>(installed->original);
        }
    }
    if (original == nullptr ||
        original == reinterpret_cast<ReleaseFunction>(hooked_release)) {
        g_release_hook_failure_count.fetch_add(1, std::memory_order_relaxed);
        return 1;
    }

    auto* resource = reinterpret_cast<ID3D11Resource*>(object);
    std::uint64_t observed_resource_id = 0;
    {
        const std::lock_guard resource_lock(g_resource_mutex);
        const auto current = g_resources.find(resource);
        if (current != g_resources.end()) {
            observed_resource_id = current->second.resource_id;
        }
    }

    const auto remaining_references = original(object);
    if (remaining_references != 0 || observed_resource_id == 0) {
        return remaining_references;
    }

    ResourceState destroyed;
    bool removed = false;
    {
        const std::lock_guard resource_lock(g_resource_mutex);
        removed = remove_resource_locked(resource, observed_resource_id, destroyed);
    }
    if (!removed) {
        g_provenance_failure_count.fetch_add(1, std::memory_order_relaxed);
        return remaining_references;
    }

    g_resource_destroy_count.fetch_add(1, std::memory_order_relaxed);
    emit_hook_event(
        FluidHookEventTypeV1::resource_destroy,
        destroyed.resource_id,
        0,
        destroyed.size_bytes,
        destroyed.generation);
    return remaining_references;
}

bool map_type_writes(D3D11_MAP map_type) {
    return map_type == D3D11_MAP_WRITE ||
        map_type == D3D11_MAP_READ_WRITE ||
        map_type == D3D11_MAP_WRITE_DISCARD ||
        map_type == D3D11_MAP_WRITE_NO_OVERWRITE;
}

void reset_metrics_and_resources() {
    g_present_count.store(0, std::memory_order_relaxed);
    g_create_buffer_count.store(0, std::memory_order_relaxed);
    g_buffer_bytes_requested.store(0, std::memory_order_relaxed);
    g_create_texture2d_count.store(0, std::memory_order_relaxed);
    g_texture_bytes_estimated.store(0, std::memory_order_relaxed);
    g_map_write_count.store(0, std::memory_order_relaxed);
    g_unmap_write_count.store(0, std::memory_order_relaxed);
    g_update_subresource_count.store(0, std::memory_order_relaxed);
    g_copy_resource_count.store(0, std::memory_order_relaxed);
    g_copy_resource_bytes_estimated.store(0, std::memory_order_relaxed);
    g_copy_subresource_region_count.store(0, std::memory_order_relaxed);
    g_copy_subresource_region_bytes_estimated.store(0, std::memory_order_relaxed);
    g_redundant_subresource_copy_candidate_count.store(0, std::memory_order_relaxed);
    g_redundant_subresource_copy_bytes_estimated.store(0, std::memory_order_relaxed);
    g_redundant_copy_candidate_count.store(0, std::memory_order_relaxed);
    g_redundant_copy_bytes_estimated.store(0, std::memory_order_relaxed);
    g_forwarded_copy_count.store(0, std::memory_order_relaxed);
    g_forwarded_copy_bytes_estimated.store(0, std::memory_order_relaxed);
    g_skipped_copy_count.store(0, std::memory_order_relaxed);
    g_skipped_copy_bytes_estimated.store(0, std::memory_order_relaxed);
    g_hook_refresh_count.store(0, std::memory_order_relaxed);
    g_hook_refresh_failure_count.store(0, std::memory_order_relaxed);
    g_resource_retire_count.store(0, std::memory_order_relaxed);
    g_resource_reuse_count.store(0, std::memory_order_relaxed);
    g_provenance_failure_count.store(0, std::memory_order_relaxed);
    g_resource_destroy_count.store(0, std::memory_order_relaxed);
    g_release_hook_failure_count.store(0, std::memory_order_relaxed);

    const std::lock_guard resource_lock(g_resource_mutex);
    g_resources.clear();
    g_last_copies.clear();
    g_last_subresource_copies.clear();
    g_pending_write_maps.clear();
    g_retired_resources.clear();
    g_retired_resource_order.clear();
    g_next_resource_id = 0;
}

HRESULT STDMETHODCALLTYPE hooked_present(
    IDXGISwapChain* swap_chain,
    UINT sync_interval,
    UINT flags) {
    const ActiveHookCall active_call;
    const auto present_count =
        g_present_count.fetch_add(1, std::memory_order_relaxed) + 1;
    emit_hook_event(
        FluidHookEventTypeV1::present,
        0,
        0,
        0,
        present_count);
    const auto original = g_original_present.load(std::memory_order_acquire);
    return original != nullptr
        ? original(swap_chain, sync_interval, flags)
        : E_UNEXPECTED;
}

HRESULT STDMETHODCALLTYPE hooked_create_buffer(
    ID3D11Device* device,
    const D3D11_BUFFER_DESC* description,
    const D3D11_SUBRESOURCE_DATA* initial_data,
    ID3D11Buffer** buffer) {
    const ActiveHookCall active_call;
    const auto original = g_original_create_buffer.load(std::memory_order_acquire);
    if (original == nullptr) {
        return E_UNEXPECTED;
    }

    const auto result = original(device, description, initial_data, buffer);
    if (SUCCEEDED(result) && description != nullptr && buffer != nullptr && *buffer != nullptr) {
        g_create_buffer_count.fetch_add(1, std::memory_order_relaxed);
        g_buffer_bytes_requested.fetch_add(description->ByteWidth, std::memory_order_relaxed);
        const auto release_hook_ready = install_release_hook(*buffer);
        if (!release_hook_ready) {
            g_provenance_failure_count.fetch_add(1, std::memory_order_relaxed);
        }
        ResourceRegistration registration;
        const auto generation = initial_data != nullptr ? 1ULL : 0ULL;
        {
            const std::lock_guard resource_lock(g_resource_mutex);
            registration = register_resource_locked(
                *buffer,
                description->ByteWidth,
                generation,
                1,
                release_hook_ready);
        }
        emit_hook_event(
            FluidHookEventTypeV1::create_buffer,
            registration.resource_id,
            0,
            description->ByteWidth,
            generation);
        if (registration.reused) {
            emit_hook_event(
                FluidHookEventTypeV1::resource_reuse,
                registration.previous_resource_id,
                registration.resource_id,
                description->ByteWidth,
                generation,
                registration.reuse_without_retire
                    ? fluid_hook_event_flag_reuse_without_retire
                    : 0);
        }
    }
    return result;
}

HRESULT STDMETHODCALLTYPE hooked_create_texture2d(
    ID3D11Device* device,
    const D3D11_TEXTURE2D_DESC* description,
    const D3D11_SUBRESOURCE_DATA* initial_data,
    ID3D11Texture2D** texture) {
    const ActiveHookCall active_call;
    const auto original = g_original_create_texture2d.load(std::memory_order_acquire);
    if (original == nullptr) {
        return E_UNEXPECTED;
    }

    const auto result = original(device, description, initial_data, texture);
    if (SUCCEEDED(result) && description != nullptr && texture != nullptr && *texture != nullptr) {
        const auto estimated_bytes = estimate_texture_bytes(*description);
        g_create_texture2d_count.fetch_add(1, std::memory_order_relaxed);
        g_texture_bytes_estimated.fetch_add(estimated_bytes, std::memory_order_relaxed);
        const auto release_hook_ready = install_release_hook(*texture);
        if (!release_hook_ready) {
            g_provenance_failure_count.fetch_add(1, std::memory_order_relaxed);
        }
        ResourceRegistration registration;
        const auto generation = initial_data != nullptr ? 1ULL : 0ULL;
        {
            const std::lock_guard resource_lock(g_resource_mutex);
            registration = register_resource_locked(
                *texture,
                estimated_bytes,
                generation,
                texture_mip_levels(*description) *
                    std::max(1U, description->ArraySize),
                release_hook_ready);
        }
        emit_hook_event(
            FluidHookEventTypeV1::create_texture2d,
            registration.resource_id,
            0,
            estimated_bytes,
            generation);
        if (registration.reused) {
            emit_hook_event(
                FluidHookEventTypeV1::resource_reuse,
                registration.previous_resource_id,
                registration.resource_id,
                estimated_bytes,
                generation,
                registration.reuse_without_retire
                    ? fluid_hook_event_flag_reuse_without_retire
                    : 0);
        }
    }
    return result;
}

HRESULT STDMETHODCALLTYPE hooked_map(
    ID3D11DeviceContext* context,
    ID3D11Resource* resource,
    UINT subresource,
    D3D11_MAP map_type,
    UINT map_flags,
    D3D11_MAPPED_SUBRESOURCE* mapped_resource) {
    const ActiveHookCall active_call;
    const auto original = g_original_map.load(std::memory_order_acquire);
    if (original == nullptr) {
        return E_UNEXPECTED;
    }

    const auto result = original(
        context,
        resource,
        subresource,
        map_type,
        map_flags,
        mapped_resource);
    refresh_context_hook_slots();
    if (SUCCEEDED(result) && resource != nullptr && map_type_writes(map_type)) {
        g_map_write_count.fetch_add(1, std::memory_order_relaxed);
        std::uint64_t resource_id = 0;
        std::uint64_t size_bytes = 0;
        std::uint64_t generation = 0;
        {
            const std::lock_guard resource_lock(g_resource_mutex);
            g_pending_write_maps.insert(SubresourceKey{resource, subresource});
            ResourceState* state = nullptr;
            get_subresource_generation_locked(
                resource,
                subresource,
                state,
                generation);
            resource_id = state->resource_id;
            size_bytes = query_subresource_size(resource, subresource);
        }
        emit_hook_event(
            FluidHookEventTypeV1::map_write,
            resource_id,
            0,
            size_bytes,
            generation,
            static_cast<std::uint32_t>(map_type),
            subresource);
    }
    return result;
}

void STDMETHODCALLTYPE hooked_unmap(
    ID3D11DeviceContext* context,
    ID3D11Resource* resource,
    UINT subresource) {
    const ActiveHookCall active_call;
    const auto original = g_original_unmap.load(std::memory_order_acquire);
    if (original == nullptr) {
        return;
    }

    original(context, resource, subresource);
    refresh_context_hook_slots();
    if (resource == nullptr) {
        return;
    }

    std::uint64_t resource_id = 0;
    std::uint64_t size_bytes = 0;
    std::uint64_t generation = 0;
    bool wrote_resource = false;
    {
        const std::lock_guard resource_lock(g_resource_mutex);
        wrote_resource = g_pending_write_maps.erase(
            SubresourceKey{resource, subresource}) != 0;
        if (wrote_resource) {
            mark_subresource_written_locked(resource, subresource, generation);
            const auto& state = ensure_resource_locked(resource);
            resource_id = state.resource_id;
            size_bytes = query_subresource_size(resource, subresource);
        }
    }
    if (wrote_resource) {
        g_unmap_write_count.fetch_add(1, std::memory_order_relaxed);
        emit_hook_event(
            FluidHookEventTypeV1::unmap_write,
            resource_id,
            0,
            size_bytes,
            generation,
            0,
            subresource);
    }
}

void STDMETHODCALLTYPE hooked_update_subresource(
    ID3D11DeviceContext* context,
    ID3D11Resource* destination,
    UINT destination_subresource,
    const D3D11_BOX* destination_box,
    const void* source_data,
    UINT source_row_pitch,
    UINT source_depth_pitch) {
    const ActiveHookCall active_call;
    const auto original = g_original_update_subresource.load(std::memory_order_acquire);
    if (original == nullptr) {
        return;
    }

    original(
        context,
        destination,
        destination_subresource,
        destination_box,
        source_data,
        source_row_pitch,
        source_depth_pitch);
    refresh_context_hook_slots();
    if (destination != nullptr) {
        g_update_subresource_count.fetch_add(1, std::memory_order_relaxed);
        std::uint64_t resource_id = 0;
        std::uint64_t size_bytes = 0;
        std::uint64_t generation = 0;
        {
            const std::lock_guard resource_lock(g_resource_mutex);
            mark_subresource_written_locked(
                destination,
                destination_subresource,
                generation);
            const auto& state = ensure_resource_locked(destination);
            resource_id = state.resource_id;
            size_bytes = estimate_copy_region_bytes(
                destination,
                destination_subresource,
                destination_box);
        }
        emit_hook_event(
            FluidHookEventTypeV1::update_subresource,
            resource_id,
            0,
            size_bytes,
            generation,
            0,
            destination_subresource);
    }
}

void STDMETHODCALLTYPE hooked_copy_subresource_region(
    ID3D11DeviceContext* context,
    ID3D11Resource* destination,
    UINT destination_subresource,
    UINT destination_x,
    UINT destination_y,
    UINT destination_z,
    ID3D11Resource* source,
    UINT source_subresource,
    const D3D11_BOX* source_box) {
    const ActiveHookCall active_call;
    const auto original =
        g_original_copy_subresource_region.load(std::memory_order_acquire);
    if (original == nullptr) {
        return;
    }
    if (destination == nullptr || source == nullptr) {
        original(
            context,
            destination,
            destination_subresource,
            destination_x,
            destination_y,
            destination_z,
            source,
            source_subresource,
            source_box);
        refresh_context_hook_slots();
        return;
    }

    CopyRegionIdentity region{
        .destination_subresource = destination_subresource,
        .destination_x = destination_x,
        .destination_y = destination_y,
        .destination_z = destination_z,
        .source_subresource = source_subresource,
        .has_source_box = source_box != nullptr,
        .source_box = source_box != nullptr ? *source_box : D3D11_BOX{},
    };
    const auto empty_copy = source_box_is_empty(source_box);
    const auto copy_bytes = estimate_copy_region_bytes(
        source,
        source_subresource,
        source_box);
    bool redundant_candidate = false;
    bool source_subresource_valid = false;
    bool destination_subresource_valid = false;
    std::uint64_t source_id = 0;
    std::uint64_t destination_id = 0;
    std::uint64_t source_generation = 0;
    std::uint64_t destination_generation = 0;
    {
        const std::lock_guard resource_lock(g_resource_mutex);
        ResourceState* source_state = nullptr;
        ResourceState* destination_state = nullptr;
        source_subresource_valid = get_subresource_generation_locked(
            source,
            source_subresource,
            source_state,
            source_generation);
        destination_subresource_valid = get_subresource_generation_locked(
            destination,
            destination_subresource,
            destination_state,
            destination_generation);
        source_id = source_state->resource_id;
        destination_id = destination_state->resource_id;

        const auto previous_copy = g_last_subresource_copies.find(
            SubresourceKey{destination, destination_subresource});
        redundant_candidate =
            (source != destination ||
                source_subresource != destination_subresource) &&
            !empty_copy && copy_bytes != 0 &&
            source_subresource_valid && destination_subresource_valid &&
            source_state->provenance_trusted &&
            destination_state->provenance_trusted &&
            previous_copy != g_last_subresource_copies.end() &&
            previous_copy->second.source_resource_id == source_id &&
            previous_copy->second.source_generation == source_generation &&
            previous_copy->second.destination_generation == destination_generation &&
            previous_copy->second.region == region;
    }

    original(
        context,
        destination,
        destination_subresource,
        destination_x,
        destination_y,
        destination_z,
        source,
        source_subresource,
        source_box);
    refresh_context_hook_slots();

    if (!empty_copy) {
        const std::lock_guard resource_lock(g_resource_mutex);
        const auto current_source = g_resources.find(source);
        const auto current_destination = g_resources.find(destination);
        const auto state_unchanged =
            current_source != g_resources.end() &&
            current_destination != g_resources.end() &&
            current_source->second.resource_id == source_id &&
            current_destination->second.resource_id == destination_id &&
            source_subresource <
                current_source->second.subresource_generations.size() &&
            destination_subresource <
                current_destination->second.subresource_generations.size() &&
            current_source->second.subresource_generations[source_subresource] ==
                source_generation &&
            current_destination->second
                    .subresource_generations[destination_subresource] ==
                destination_generation;
        redundant_candidate = redundant_candidate && state_unchanged;
        const auto destination_marked = mark_subresource_written_locked(
            destination,
            destination_subresource,
            destination_generation);
        if (state_unchanged &&
            source_subresource_valid && destination_subresource_valid &&
            destination_marked && copy_bytes != 0) {
            g_last_subresource_copies[
                SubresourceKey{destination, destination_subresource}] =
                LastSubresourceCopy{
                    .source_resource_id = source_id,
                    .source_generation = source_generation,
                    .destination_generation = destination_generation,
                    .region = region,
                };
        }
    }

    g_copy_subresource_region_count.fetch_add(1, std::memory_order_relaxed);
    g_copy_subresource_region_bytes_estimated.fetch_add(
        copy_bytes,
        std::memory_order_relaxed);
    if (redundant_candidate) {
        g_redundant_subresource_copy_candidate_count.fetch_add(
            1,
            std::memory_order_relaxed);
        g_redundant_subresource_copy_bytes_estimated.fetch_add(
            copy_bytes,
            std::memory_order_relaxed);
    }
    emit_hook_event(
        FluidHookEventTypeV1::copy_subresource_region,
        destination_id,
        source_id,
        copy_bytes,
        destination_generation,
        redundant_candidate ? fluid_hook_event_flag_redundant_candidate : 0,
        destination_subresource,
        source_subresource,
        copy_region_key(region));
}

void STDMETHODCALLTYPE hooked_copy_resource(
    ID3D11DeviceContext* context,
    ID3D11Resource* destination,
    ID3D11Resource* source) {
    const ActiveHookCall active_call;
    const auto original = g_original_copy_resource.load(std::memory_order_acquire);
    if (original == nullptr) {
        return;
    }
    if (destination == nullptr || source == nullptr) {
        original(context, destination, source);
        refresh_context_hook_slots();
        return;
    }

    bool redundant_candidate = false;
    std::uint64_t copy_bytes = 0;
    std::uint64_t source_id = 0;
    std::uint64_t destination_id = 0;
    std::uint64_t source_generation = 0;
    {
        const std::lock_guard resource_lock(g_resource_mutex);
        auto& source_state = ensure_resource_locked(source);
        auto& destination_state = ensure_resource_locked(destination);
        source_id = source_state.resource_id;
        destination_id = destination_state.resource_id;
        source_generation = source_state.generation;
        copy_bytes = source_state.size_bytes != 0 && destination_state.size_bytes != 0
            ? std::min(source_state.size_bytes, destination_state.size_bytes)
            : std::max(source_state.size_bytes, destination_state.size_bytes);

        const auto previous_copy = g_last_copies.find(destination);
        redundant_candidate = source_state.provenance_trusted &&
            destination_state.provenance_trusted &&
            previous_copy != g_last_copies.end() &&
            previous_copy->second.source_resource_id == source_id &&
            previous_copy->second.source_generation == source_generation &&
            previous_copy->second.destination_generation == destination_state.generation;
    }

    bool skipped_copy = false;
    const auto skip_limit = g_max_skipped_copy_count.load(std::memory_order_acquire);
    if (redundant_candidate && copy_bytes != 0 && skip_limit != 0) {
        auto skipped_count = g_skipped_copy_count.load(std::memory_order_relaxed);
        while (skipped_count < skip_limit &&
               !g_skipped_copy_count.compare_exchange_weak(
                   skipped_count,
                   skipped_count + 1,
                   std::memory_order_acq_rel,
                   std::memory_order_relaxed)) {
        }
        skipped_copy = skipped_count < skip_limit;
    }

    if (skipped_copy) {
        g_skipped_copy_bytes_estimated.fetch_add(copy_bytes, std::memory_order_relaxed);
    } else {
        original(context, destination, source);
        refresh_context_hook_slots();
        g_forwarded_copy_count.fetch_add(1, std::memory_order_relaxed);
        g_forwarded_copy_bytes_estimated.fetch_add(copy_bytes, std::memory_order_relaxed);
    }
    g_copy_resource_count.fetch_add(1, std::memory_order_relaxed);
    g_copy_resource_bytes_estimated.fetch_add(copy_bytes, std::memory_order_relaxed);
    if (redundant_candidate) {
        g_redundant_copy_candidate_count.fetch_add(1, std::memory_order_relaxed);
        g_redundant_copy_bytes_estimated.fetch_add(copy_bytes, std::memory_order_relaxed);
    }

    std::uint64_t destination_generation = 0;
    {
        const std::lock_guard resource_lock(g_resource_mutex);
        mark_resource_written_locked(destination);
        auto& destination_state = ensure_resource_locked(destination);
        destination_generation = destination_state.generation;
        g_last_copies[destination] = LastCopy{
            .source_resource_id = source_id,
            .source_generation = source_generation,
            .destination_generation = destination_generation,
        };
    }
    std::uint32_t event_flags = 0;
    if (redundant_candidate) {
        event_flags |= fluid_hook_event_flag_redundant_candidate;
    }
    if (skipped_copy) {
        event_flags |= fluid_hook_event_flag_copy_skipped;
    }
    emit_hook_event(
        FluidHookEventTypeV1::copy_resource,
        destination_id,
        source_id,
        copy_bytes,
        destination_generation,
        event_flags);
}

void clear_original_functions() {
    g_original_present.store(nullptr, std::memory_order_release);
    g_original_create_buffer.store(nullptr, std::memory_order_release);
    g_original_create_texture2d.store(nullptr, std::memory_order_release);
    g_original_map.store(nullptr, std::memory_order_release);
    g_original_unmap.store(nullptr, std::memory_order_release);
    g_original_copy_subresource_region.store(nullptr, std::memory_order_release);
    g_original_copy_resource.store(nullptr, std::memory_order_release);
    g_original_update_subresource.store(nullptr, std::memory_order_release);
}

} // namespace

HRESULT WINAPI FluidHookAttach(IDXGISwapChain* swap_chain) {
    return FluidHookAttachEx(swap_chain, nullptr);
}

HRESULT WINAPI FluidHookAttachEx(
    IDXGISwapChain* swap_chain,
    const FluidHookAttachOptionsV1* options) {
    if (swap_chain == nullptr) {
        return E_POINTER;
    }

    std::uint32_t max_skipped_copy_count = 0;
    bool track_resource_lifetime = false;
    if (options != nullptr) {
        if (options->struct_size < sizeof(FluidHookAttachOptionsV1) ||
            options->abi_version != fluid_hook_attach_options_abi_version ||
            (options->flags & ~(
                fluid_hook_attach_flag_skip_first_redundant_copy |
                fluid_hook_attach_flag_track_resource_lifetime)) != 0) {
            return E_INVALIDARG;
        }
        const auto skip_enabled =
            (options->flags & fluid_hook_attach_flag_skip_first_redundant_copy) != 0;
        if ((skip_enabled && options->max_skipped_copy_count != 1) ||
            (!skip_enabled && options->max_skipped_copy_count != 0)) {
            return E_INVALIDARG;
        }
        max_skipped_copy_count = options->max_skipped_copy_count;
        track_resource_lifetime =
            (options->flags & fluid_hook_attach_flag_track_resource_lifetime) != 0;
    }

    const std::lock_guard hook_lock(g_hook_mutex);
    if (g_installed_hook_count != 0 || !g_release_hook_slots.empty()) {
        return HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS);
    }

    ID3D11Device* device{};
    auto result = swap_chain->GetDevice(IID_PPV_ARGS(&device));
    if (FAILED(result) || device == nullptr) {
        return FAILED(result) ? result : E_NOINTERFACE;
    }

    ID3D11DeviceContext* context{};
    device->GetImmediateContext(&context);
    if (context == nullptr) {
        device->Release();
        return E_NOINTERFACE;
    }

    auto** swap_chain_vtable = *reinterpret_cast<void***>(swap_chain);
    auto** device_vtable = *reinterpret_cast<void***>(device);
    auto** context_vtable = *reinterpret_cast<void***>(context);
    std::array<HookSlot, kHookSlotCount> slots{
        HookSlot{
            &swap_chain_vtable[kPresentVtableIndex],
            swap_chain_vtable[kPresentVtableIndex],
            reinterpret_cast<void*>(hooked_present)},
        HookSlot{
            &device_vtable[kCreateBufferVtableIndex],
            device_vtable[kCreateBufferVtableIndex],
            reinterpret_cast<void*>(hooked_create_buffer)},
        HookSlot{
            &device_vtable[kCreateTexture2DVtableIndex],
            device_vtable[kCreateTexture2DVtableIndex],
            reinterpret_cast<void*>(hooked_create_texture2d)},
        HookSlot{
            &context_vtable[kMapVtableIndex],
            context_vtable[kMapVtableIndex],
            reinterpret_cast<void*>(hooked_map)},
        HookSlot{
            &context_vtable[kUnmapVtableIndex],
            context_vtable[kUnmapVtableIndex],
            reinterpret_cast<void*>(hooked_unmap)},
        HookSlot{
            &context_vtable[kCopySubresourceRegionVtableIndex],
            context_vtable[kCopySubresourceRegionVtableIndex],
            reinterpret_cast<void*>(hooked_copy_subresource_region)},
        HookSlot{
            &context_vtable[kCopyResourceVtableIndex],
            context_vtable[kCopyResourceVtableIndex],
            reinterpret_cast<void*>(hooked_copy_resource)},
        HookSlot{
            &context_vtable[kUpdateSubresourceVtableIndex],
            context_vtable[kUpdateSubresourceVtableIndex],
            reinterpret_cast<void*>(hooked_update_subresource)},
    };

    context->Release();
    device->Release();

    for (const auto& slot : slots) {
        if (slot.original == nullptr || slot.original == slot.hook) {
            return E_UNEXPECTED;
        }
    }

    g_original_present.store(
        reinterpret_cast<PresentFunction>(slots[0].original),
        std::memory_order_release);
    g_original_create_buffer.store(
        reinterpret_cast<CreateBufferFunction>(slots[1].original),
        std::memory_order_release);
    g_original_create_texture2d.store(
        reinterpret_cast<CreateTexture2DFunction>(slots[2].original),
        std::memory_order_release);
    g_original_map.store(
        reinterpret_cast<MapFunction>(slots[3].original),
        std::memory_order_release);
    g_original_unmap.store(
        reinterpret_cast<UnmapFunction>(slots[4].original),
        std::memory_order_release);
    g_original_copy_subresource_region.store(
        reinterpret_cast<CopySubresourceRegionFunction>(slots[5].original),
        std::memory_order_release);
    g_original_copy_resource.store(
        reinterpret_cast<CopyResourceFunction>(slots[6].original),
        std::memory_order_release);
    g_original_update_subresource.store(
        reinterpret_cast<UpdateSubresourceFunction>(slots[7].original),
        std::memory_order_release);
    g_max_skipped_copy_count.store(max_skipped_copy_count, std::memory_order_release);
    g_track_resource_lifetime.store(track_resource_lifetime, std::memory_order_release);
    reset_metrics_and_resources();
    if (!initialize_event_ring()) {
        const auto ring_error = GetLastError();
        clear_original_functions();
        g_max_skipped_copy_count.store(0, std::memory_order_release);
        g_track_resource_lifetime.store(false, std::memory_order_release);
        return HRESULT_FROM_WIN32(ring_error);
    }

    const std::lock_guard patch_lock(g_patch_mutex);
    g_detaching.store(false, std::memory_order_release);
    size_t patched_count = 0;
    for (; patched_count < slots.size(); ++patched_count) {
        const auto& slot = slots[patched_count];
        if (!write_pointer(slot.slot, slot.hook, slot.original)) {
            const auto patch_error = GetLastError();
            while (patched_count != 0) {
                --patched_count;
                const auto& patched = slots[patched_count];
                write_pointer(patched.slot, patched.original, patched.hook);
            }
            clear_original_functions();
            close_event_ring();
            g_max_skipped_copy_count.store(0, std::memory_order_release);
            g_track_resource_lifetime.store(false, std::memory_order_release);
            return HRESULT_FROM_WIN32(patch_error);
        }
    }

    g_hook_slots = slots;
    g_installed_hook_count = slots.size();
    return S_OK;
}

HRESULT WINAPI FluidHookDetach() {
    const std::lock_guard hook_lock(g_hook_mutex);
    if (g_installed_hook_count == 0) {
        return S_FALSE;
    }

    g_detaching.store(true, std::memory_order_release);
    std::vector<HookSlot*> restored_slots;
    {
        const std::lock_guard patch_lock(g_patch_mutex);
        std::vector<HookSlot*> installed_slots;
        installed_slots.reserve(g_installed_hook_count + g_release_hook_slots.size());
        for (size_t index = 0; index < g_installed_hook_count; ++index) {
            installed_slots.push_back(&g_hook_slots[index]);
        }
        for (auto& slot : g_release_hook_slots) {
            installed_slots.push_back(&slot);
        }

        for (const auto* slot : installed_slots) {
            if (*slot->slot != slot->hook && *slot->slot != slot->original) {
                g_detaching.store(false, std::memory_order_release);
                return E_UNEXPECTED;
            }
        }

        restored_slots.reserve(installed_slots.size());
        for (auto* slot : installed_slots) {
            if (*slot->slot == slot->original) {
                continue;
            }
            if (!write_pointer(slot->slot, slot->original, slot->hook)) {
                const auto restore_error = GetLastError();
                auto rollback_succeeded = true;
                for (auto restored = restored_slots.rbegin();
                     restored != restored_slots.rend();
                     ++restored) {
                    if (*(*restored)->slot != (*restored)->original ||
                        !write_pointer(
                            (*restored)->slot,
                            (*restored)->hook,
                            (*restored)->original)) {
                        rollback_succeeded = false;
                        break;
                    }
                }
                if (rollback_succeeded) {
                    g_detaching.store(false, std::memory_order_release);
                }
                return HRESULT_FROM_WIN32(restore_error);
            }
            restored_slots.push_back(slot);
        }
    }

    constexpr unsigned long detach_wait_limit_ms = 5000;
    unsigned long waited_ms = 0;
    while (g_active_hook_calls.load(std::memory_order_acquire) != 0 &&
           waited_ms < detach_wait_limit_ms) {
        Sleep(1);
        ++waited_ms;
    }
    if (g_active_hook_calls.load(std::memory_order_acquire) != 0) {
        auto rollback_succeeded = true;
        {
            const std::lock_guard patch_lock(g_patch_mutex);
            for (auto restored = restored_slots.rbegin();
                 restored != restored_slots.rend();
                 ++restored) {
                if (*(*restored)->slot == (*restored)->hook) {
                    continue;
                }
                if (*(*restored)->slot != (*restored)->original ||
                    !write_pointer(
                        (*restored)->slot,
                        (*restored)->hook,
                        (*restored)->original)) {
                    rollback_succeeded = false;
                    break;
                }
            }
        }
        if (rollback_succeeded) {
            g_detaching.store(false, std::memory_order_release);
        }
        return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
    }

    g_installed_hook_count = 0;
    g_hook_slots = {};
    g_release_hook_slots.clear();
    clear_original_functions();
    g_max_skipped_copy_count.store(0, std::memory_order_release);
    g_track_resource_lifetime.store(false, std::memory_order_release);
    g_detaching.store(false, std::memory_order_release);
    {
        const std::lock_guard resource_lock(g_resource_mutex);
        g_resources.clear();
        g_last_copies.clear();
        g_last_subresource_copies.clear();
        g_pending_write_maps.clear();
        g_retired_resources.clear();
        g_retired_resource_order.clear();
    }
    close_event_ring();
    return S_OK;
}

HRESULT WINAPI FluidHookRefresh() {
    const std::lock_guard hook_lock(g_hook_mutex);
    if (g_installed_hook_count == 0) {
        return S_FALSE;
    }

    const auto failures_before =
        g_hook_refresh_failure_count.load(std::memory_order_relaxed);
    refresh_context_hook_slots();
    return g_hook_refresh_failure_count.load(std::memory_order_relaxed) == failures_before
        ? S_OK
        : E_FAIL;
}

HRESULT WINAPI FluidHookRetireResource(ID3D11Resource* resource) {
    if (resource == nullptr) {
        return E_POINTER;
    }

    const std::lock_guard hook_lock(g_hook_mutex);
    if (g_installed_hook_count == 0 || g_detaching.load(std::memory_order_acquire)) {
        return S_FALSE;
    }

    ResourceState retired;
    {
        const std::lock_guard resource_lock(g_resource_mutex);
        const auto current = g_resources.find(resource);
        if (current == g_resources.end()) {
            return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        }
        const auto resource_id = current->second.resource_id;
        if (!remove_resource_locked(resource, resource_id, retired)) {
            g_provenance_failure_count.fetch_add(1, std::memory_order_relaxed);
            return E_UNEXPECTED;
        }
    }

    g_resource_retire_count.fetch_add(1, std::memory_order_relaxed);
    emit_hook_event(
        FluidHookEventTypeV1::resource_retire,
        retired.resource_id,
        0,
        retired.size_bytes,
        retired.generation);
    return S_OK;
}

std::uint64_t WINAPI FluidHookPresentCount() {
    return g_present_count.load(std::memory_order_relaxed);
}

BOOL WINAPI FluidHookIsAttached() {
    const std::lock_guard hook_lock(g_hook_mutex);
    return g_installed_hook_count != 0 ? TRUE : FALSE;
}

HRESULT WINAPI FluidHookReadSnapshot(FluidHookSnapshotV1* snapshot) {
    if (snapshot == nullptr) {
        return E_POINTER;
    }
    if (snapshot->struct_size < sizeof(FluidHookSnapshotV1)) {
        return E_INVALIDARG;
    }

    const std::lock_guard hook_lock(g_hook_mutex);

    FluidHookSnapshotV1 result{};
    result.struct_size = sizeof(result);
    result.abi_version = fluid_hook_snapshot_abi_version;
    result.present_count = g_present_count.load(std::memory_order_relaxed);
    result.create_buffer_count = g_create_buffer_count.load(std::memory_order_relaxed);
    result.buffer_bytes_requested = g_buffer_bytes_requested.load(std::memory_order_relaxed);
    result.create_texture2d_count = g_create_texture2d_count.load(std::memory_order_relaxed);
    result.texture_bytes_estimated = g_texture_bytes_estimated.load(std::memory_order_relaxed);
    result.map_write_count = g_map_write_count.load(std::memory_order_relaxed);
    result.unmap_write_count = g_unmap_write_count.load(std::memory_order_relaxed);
    result.update_subresource_count = g_update_subresource_count.load(std::memory_order_relaxed);
    result.copy_resource_count = g_copy_resource_count.load(std::memory_order_relaxed);
    result.copy_resource_bytes_estimated =
        g_copy_resource_bytes_estimated.load(std::memory_order_relaxed);
    result.redundant_copy_candidate_count =
        g_redundant_copy_candidate_count.load(std::memory_order_relaxed);
    result.redundant_copy_bytes_estimated =
        g_redundant_copy_bytes_estimated.load(std::memory_order_relaxed);
    result.forwarded_copy_count = g_forwarded_copy_count.load(std::memory_order_relaxed);
    result.forwarded_copy_bytes_estimated =
        g_forwarded_copy_bytes_estimated.load(std::memory_order_relaxed);
    result.skipped_copy_count = g_skipped_copy_count.load(std::memory_order_relaxed);
    result.skipped_copy_bytes_estimated =
        g_skipped_copy_bytes_estimated.load(std::memory_order_relaxed);
    {
        const std::lock_guard resource_lock(g_resource_mutex);
        result.tracked_resource_count = g_resources.size();
        result.retired_resource_identity_count = g_retired_resources.size();
    }
    result.hook_refresh_count = g_hook_refresh_count.load(std::memory_order_relaxed);
    result.hook_refresh_failure_count =
        g_hook_refresh_failure_count.load(std::memory_order_relaxed);
    result.resource_retire_count =
        g_resource_retire_count.load(std::memory_order_relaxed);
    result.resource_reuse_count =
        g_resource_reuse_count.load(std::memory_order_relaxed);
    result.provenance_failure_count =
        g_provenance_failure_count.load(std::memory_order_relaxed);
    result.resource_destroy_count =
        g_resource_destroy_count.load(std::memory_order_relaxed);
    result.release_hook_failure_count =
        g_release_hook_failure_count.load(std::memory_order_relaxed);
    result.automatic_lifetime_tracking =
        g_track_resource_lifetime.load(std::memory_order_acquire) ? 1 : 0;
    result.copy_subresource_region_count =
        g_copy_subresource_region_count.load(std::memory_order_relaxed);
    result.copy_subresource_region_bytes_estimated =
        g_copy_subresource_region_bytes_estimated.load(std::memory_order_relaxed);
    result.redundant_subresource_copy_candidate_count =
        g_redundant_subresource_copy_candidate_count.load(std::memory_order_relaxed);
    result.redundant_subresource_copy_bytes_estimated =
        g_redundant_subresource_copy_bytes_estimated.load(std::memory_order_relaxed);
    {
        const std::lock_guard patch_lock(g_patch_mutex);
        result.release_hook_slot_count = g_release_hook_slots.size();
    }
    if (g_ring_header != nullptr) {
        result.ipc_event_count = static_cast<std::uint64_t>(
            InterlockedCompareExchange64(&g_ring_header->next_sequence, 0, 0));
        result.ipc_overrun_count = static_cast<std::uint64_t>(
            InterlockedCompareExchange64(&g_ring_header->overrun_count, 0, 0));
    }

    *snapshot = result;
    return S_OK;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}
