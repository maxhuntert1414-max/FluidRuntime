#pragma once

#include <array>
#include <cstdint>
#include <limits>
#include <optional>

namespace fluid::observation {

// Fixed storage and reusable slots: neither lookup nor churn allocates on the hook path.
// The layer owns synchronization; no driver call may run while its state lock is held.
template <class Value, std::size_t Capacity> class ResourceTable {
    static_assert(Capacity > 0 && Capacity < UINT32_MAX / 2);
    struct Slot {
        void *device{};
        std::uint64_t handle{};
        std::uint32_t next{};
        Value value{};
    };
    std::array<Slot, Capacity> slots_{};
    std::array<std::uint32_t, Capacity * 2> buckets_{};
    std::uint32_t free_{1};

    static std::size_t bucket(void *device, std::uint64_t handle) noexcept {
        auto hash = handle ^ static_cast<std::uint64_t>(reinterpret_cast<std::uintptr_t>(device));
        hash ^= hash >> 30;
        hash *= UINT64_C(0xbf58476d1ce4e5b9);
        hash ^= hash >> 27;
        return static_cast<std::size_t>(hash % (Capacity * 2));
    }

  public:
    ResourceTable() noexcept {
        for (std::uint32_t i = 0; i < Capacity; ++i) {
            slots_[i].next = i + 1 < Capacity ? i + 2 : 0;
        }
    }

    Value *find(void *device, std::uint64_t handle) noexcept {
        if (!handle)
            return nullptr;
        for (auto index = buckets_[bucket(device, handle)]; index; index = slots_[index - 1].next) {
            auto &slot = slots_[index - 1];
            if (slot.device == device && slot.handle == handle)
                return &slot.value;
        }
        return nullptr;
    }

    bool insert(void *device, std::uint64_t handle, const Value &value) noexcept {
        if (!handle || !free_ || find(device, handle))
            return false;
        auto &head = buckets_[bucket(device, handle)];
        const auto index = free_;
        free_ = slots_[index - 1].next;
        slots_[index - 1] = {device, handle, head, value};
        head = index;
        return true;
    }

    std::optional<Value> erase(void *device, std::uint64_t handle) noexcept {
        if (!handle)
            return std::nullopt;
        auto *link = &buckets_[bucket(device, handle)];
        while (*link) {
            const auto index = *link;
            auto &slot = slots_[index - 1];
            if (slot.device == device && slot.handle == handle) {
                const auto value = slot.value;
                *link = slot.next;
                slot = {};
                slot.next = free_;
                free_ = index;
                return value;
            }
            link = &slot.next;
        }
        return std::nullopt;
    }

    template <class Callback> void erase_device(void *device, Callback callback) noexcept {
        for (auto &slot : slots_) {
            if (slot.handle && slot.device == device) {
                const auto value = erase(device, slot.handle);
                callback(*value);
            }
        }
    }
};

enum class CopyMemoryClass {
    unknown,
    host_to_device,
    device_to_host,
    device_to_device,
    host_to_host,
    shared
};

struct MemoryRecord {
    std::uint64_t bytes{};
    std::uint64_t generation{};
    std::uint32_t flags{};
    bool known{};
};

struct BufferRecord {
    std::uint64_t bytes{};
    std::uint64_t memory{};
    std::uint64_t memory_generation{};
    std::uint64_t offset{};
    bool known{};
};

struct CopyAttribution {
    CopyMemoryClass memory_class{CopyMemoryClass::unknown};
    bool same_allocation{};
};

template <std::size_t Capacity = 8192> class BufferTracker {
    ResourceTable<MemoryRecord, Capacity> memories_;
    ResourceTable<BufferRecord, Capacity> buffers_;
    std::uint64_t generation_{};

    MemoryRecord *bound_memory(void *device, const BufferRecord *buffer) noexcept {
        if (!buffer || !buffer->known || !buffer->memory_generation)
            return nullptr;
        auto *memory = memories_.find(device, buffer->memory);
        return memory && memory->known && memory->generation == buffer->memory_generation ? memory : nullptr;
    }

  public:
    bool add_memory(void *device, std::uint64_t handle, std::uint64_t bytes, std::uint32_t flags,
                    bool known) noexcept {
        if (generation_ == std::numeric_limits<std::uint64_t>::max())
            return false;
        return memories_.insert(device, handle, {bytes, ++generation_, flags, known});
    }

    std::optional<MemoryRecord> remove_memory(void *device, std::uint64_t handle) noexcept {
        // Bindings keep the old generation, so recycled driver handles cannot revive them.
        return memories_.erase(device, handle);
    }

    bool add_buffer(void *device, std::uint64_t handle, std::uint64_t bytes, bool known) noexcept {
        return buffers_.insert(device, handle, {bytes, 0, 0, 0, known});
    }

    bool remove_buffer(void *device, std::uint64_t handle) noexcept {
        return buffers_.erase(device, handle).has_value();
    }

    bool bind(void *device, std::uint64_t buffer_handle, std::uint64_t memory_handle, std::uint64_t offset,
              bool known) noexcept {
        auto *buffer = buffers_.find(device, buffer_handle);
        if (!buffer)
            return false;
        buffer->memory = 0;
        buffer->memory_generation = 0;
        const auto *memory = memories_.find(device, memory_handle);
        if (!known || !buffer->known || !memory || !memory->known || offset > memory->bytes ||
            buffer->bytes > memory->bytes - offset)
            return false;
        buffer->memory = memory_handle;
        buffer->memory_generation = memory->generation;
        buffer->offset = offset;
        return true;
    }

    CopyAttribution attribute(void *device, std::uint64_t source, std::uint64_t destination,
                              std::uint64_t source_offset, std::uint64_t destination_offset,
                              std::uint64_t bytes) noexcept {
        const auto *src = buffers_.find(device, source);
        const auto *dst = buffers_.find(device, destination);
        const auto *source_memory = bound_memory(device, src);
        const auto *destination_memory = bound_memory(device, dst);
        if (!source_memory || !destination_memory || !bytes || source_offset > src->bytes ||
            bytes > src->bytes - source_offset || destination_offset > dst->bytes ||
            bytes > dst->bytes - destination_offset)
            return {};

        // Vulkan property bits 0/1 are DEVICE_LOCAL/HOST_VISIBLE, not physical locations.
        const auto source_flags = source_memory->flags & 3U;
        const auto destination_flags = destination_memory->flags & 3U;
        CopyMemoryClass category = CopyMemoryClass::unknown;
        if (source_flags && destination_flags) {
            if (source_flags == 3 || destination_flags == 3)
                category = CopyMemoryClass::shared;
            else if (source_flags == 2 && destination_flags == 1)
                category = CopyMemoryClass::host_to_device;
            else if (source_flags == 1 && destination_flags == 2)
                category = CopyMemoryClass::device_to_host;
            else if (source_flags == 1)
                category = CopyMemoryClass::device_to_device;
            else
                category = CopyMemoryClass::host_to_host;
        }
        return {category, src->memory == dst->memory};
    }

    template <class MemoryCallback, class BufferCallback>
    void remove_device(void *device, MemoryCallback memory_callback,
                       BufferCallback buffer_callback) noexcept {
        buffers_.erase_device(device, buffer_callback);
        memories_.erase_device(device, memory_callback);
    }
};

} // namespace fluid::observation
