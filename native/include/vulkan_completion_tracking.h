#pragma once

#include "vulkan_buffer_tracking.h"

namespace fluid::observation {

struct CompletionTotals {
    std::uint64_t submits{};
    std::uint64_t copies{};
    std::uint64_t bytes{};
};

struct QueueMarker {
    std::uint64_t handle{};
    std::uint64_t generation{};
    CompletionTotals totals{};
};

struct FenceMarker {
    std::uint64_t handle{};
    std::uint64_t generation{};
    QueueMarker queue{};
};

// Cumulative queue prefixes replace a pending-submission history. Only work
// accepted by the driver and attributed by CommandTracker enters these totals.
// The layer serializes access; markers cross driver calls without holding locks.
template <std::size_t QueueCapacity = 256, std::size_t FenceCapacity = 2048> class CompletionTracker {
    struct Queue {
        std::uint64_t generation{};
        CompletionTotals submitted{};
        CompletionTotals completed{};
    };
    struct Fence {
        std::uint64_t generation{};
        QueueMarker queue{};
    };
    ResourceTable<Queue, QueueCapacity> queues_;
    ResourceTable<Fence, FenceCapacity> fences_;
    std::uint64_t generation_{};

    std::uint64_t next_generation() noexcept { return generation_ == UINT64_MAX ? 0 : ++generation_; }

  public:
    using DeviceMarkers = std::array<QueueMarker, QueueCapacity>;

    bool create_fence(void *device, std::uint64_t handle) noexcept {
        const auto generation = next_generation();
        return generation && fences_.insert(device, handle, {generation, {}});
    }

    bool destroy_fence(void *device, std::uint64_t handle) noexcept {
        return fences_.erase(device, handle).has_value();
    }

    // Called BEFORE reset or submission. Even failed calls must not retain an
    // old payload association. A generation of zero is permanently untrusted.
    FenceMarker invalidate_fence(void *device, std::uint64_t handle) noexcept {
        auto *fence = fences_.find(device, handle);
        if (!fence)
            return {};
        fence->generation = next_generation();
        fence->queue = {};
        return {handle, fence->generation, {}};
    }

    FenceMarker fence_marker(void *device, std::uint64_t handle) noexcept {
        const auto *fence = fences_.find(device, handle);
        return fence ? FenceMarker{handle, fence->generation, fence->queue} : FenceMarker{};
    }

    QueueMarker queue_marker(void *device, std::uint64_t handle) noexcept {
        const auto *queue = queues_.find(device, handle);
        return queue ? QueueMarker{handle, queue->generation, queue->submitted} : QueueMarker{};
    }

    void device_markers(void *device, DeviceMarkers &markers) noexcept {
        std::size_t count{};
        queues_.visit_device(device, [&](std::uint64_t handle, const Queue &queue) {
            if (count < markers.size())
                markers[count++] = {handle, queue.generation, queue.submitted};
        });
    }

    // Null totals mean the submission was accepted but its recording could not
    // be attributed. Its fence can still prove completion of the known prefix.
    bool submit(void *device, std::uint64_t handle, const FenceMarker &fence_token,
                const CompletionTotals *totals) noexcept {
        auto *queue = queues_.find(device, handle);
        if (!queue) {
            const auto generation = next_generation();
            if (!generation || !queues_.insert(device, handle, {generation, {}, {}}))
                return false;
            queue = queues_.find(device, handle);
        }
        if (totals) {
            auto &target = queue->submitted;
            if (totals->submits > UINT64_MAX - target.submits ||
                totals->copies > UINT64_MAX - target.copies || totals->bytes > UINT64_MAX - target.bytes)
                return false;
            target.submits += totals->submits;
            target.copies += totals->copies;
            target.bytes += totals->bytes;
        }
        auto *fence = fences_.find(device, fence_token.handle);
        if (fence && fence_token.generation && fence->generation == fence_token.generation)
            fence->queue = {handle, queue->generation, queue->submitted};
        return true;
    }

    CompletionTotals complete_queue(void *device, const QueueMarker &marker) noexcept {
        auto *queue = queues_.find(device, marker.handle);
        if (!queue || !marker.generation || queue->generation != marker.generation)
            return {};
        const auto &target = marker.totals;
        auto &previous = queue->completed;
        // Reject stale, fabricated or out-of-order snapshots without subtraction
        // underflow or double-counting. A newer fence includes earlier submits.
        if (target.submits <= previous.submits || target.submits > queue->submitted.submits ||
            target.copies < previous.copies || target.copies > queue->submitted.copies ||
            target.bytes < previous.bytes || target.bytes > queue->submitted.bytes)
            return {};
        const CompletionTotals delta{target.submits - previous.submits, target.copies - previous.copies,
                                     target.bytes - previous.bytes};
        previous = target;
        return delta;
    }

    CompletionTotals complete_fence(void *device, const FenceMarker &marker) noexcept {
        const auto *fence = fences_.find(device, marker.handle);
        if (!fence || !marker.generation || fence->generation != marker.generation)
            return {};
        return complete_queue(device, marker.queue);
    }

    template <class QueueCallback, class FenceCallback>
    void destroy_device(void *device, QueueCallback queue_callback, FenceCallback fence_callback) noexcept {
        queues_.erase_device(device, [&](const Queue &queue) {
            queue_callback(queue.submitted.submits - queue.completed.submits);
        });
        fences_.erase_device(device, [&](const Fence &) { fence_callback(); });
    }
};

} // namespace fluid::observation
