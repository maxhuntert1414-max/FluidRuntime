#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <mutex>

namespace fluid::memory {

// Trusted Runtime bookkeeping, not a wire ABI or a source of Gateway authority.
// All views of an allocation serialize through one record. No payload is retained.
enum class Access : std::uint32_t { host_read = 1, host_write, device_read, device_write, upload, readback };
enum class Status : std::uint32_t {
    ok,
    invalid,
    closed,
    capacity,
    exhausted,
    busy,
    host_stale,
    device_stale,
    quarantined,
    fence_pending
};
struct Buffer {
    std::uint64_t session{}, generation{};
    bool operator==(const Buffer &) const = default;
};
struct View {
    Buffer buffer{};
    std::uint64_t offset{}, bytes{};
};
struct Fence {
    std::uint64_t queue{}, value{};
    bool operator==(const Fence &) const = default;
};
struct Ticket {
    Buffer buffer{};
    std::uint64_t serial{};
    Access access{};
    Fence fence{};
    bool operator==(const Ticket &) const = default;
};
struct Admission {
    Status status{Status::invalid};
    Ticket ticket{};
};
struct Snapshot {
    Status status{Status::invalid};
    std::uint64_t version{};
    bool host_current{}, device_current{}, busy{};
};

// Single device/queue and exclusive accesses deliberately keep v1 conservative.
// SerialLimit permits deterministic wraparound tests without special runtime modes.
template <std::size_t Capacity = 64, std::uint64_t SerialLimit = UINT64_MAX - 1> class Coherence {
    static_assert(Capacity > 0 && Capacity <= 4096);
    struct Record {
        Buffer buffer{};
        std::uint64_t bytes{}, version{}, host{}, device{};
        Ticket pending{};
        bool quarantined{};
    };
    std::array<Record, Capacity> records_{};
    mutable std::mutex mutex_;
    const std::uint64_t session_, queue_;
    std::uint64_t serial_{}, last_fence_{}, gpu_pending_{};
    bool closed_{};

    std::uint64_t next() {
        if (serial_ >= SerialLimit) {
            closed_ = true;
            return 0;
        }
        return ++serial_;
    }
    Record *find(Buffer buffer) {
        if (!buffer.generation || buffer.session != session_)
            return nullptr;
        for (auto &record : records_)
            if (record.buffer == buffer)
                return &record;
        return nullptr;
    }
    static bool gpu(Access access) {
        return access == Access::device_read || access == Access::device_write || access == Access::upload ||
               access == Access::readback;
    }
    static bool valid_range(const Record &record, View view) {
        return view.bytes && view.offset <= record.bytes && view.bytes <= record.bytes - view.offset;
    }

  public:
    // Session identity must be unique per owning broker lifetime, never a recycled PID.
    explicit Coherence(std::uint64_t session, std::uint64_t queue)
        : session_(session), queue_(queue), closed_(!session || !queue) {}

    Status allocate(std::uint64_t requested_bytes, Buffer &output) {
        const std::lock_guard lock(mutex_);
        output = {};
        if (closed_)
            return Status::closed;
        if (!requested_bytes || requested_bytes > 64ULL * 1024 * 1024)
            return Status::invalid;
        for (auto &record : records_) {
            if (record.buffer.generation)
                continue;
            const auto generation = next();
            if (!generation)
                return Status::exhausted;
            record = {};
            output = record.buffer = {session_, generation};
            record.bytes = requested_bytes;
            return Status::ok;
        }
        return Status::capacity;
    }

    Status release(Buffer buffer) {
        const std::lock_guard lock(mutex_);
        if (closed_)
            return Status::closed;
        auto *record = find(buffer);
        if (!record)
            return Status::invalid;
        if (record->quarantined)
            return Status::quarantined;
        if (record->pending.serial)
            return Status::busy;
        *record = {};
        return Status::ok;
    }

    Snapshot inspect(View view) {
        const std::lock_guard lock(mutex_);
        if (closed_)
            return {Status::closed};
        const auto *record = find(view.buffer);
        if (!record || !valid_range(*record, view))
            return {};
        return {record->quarantined ? Status::quarantined : Status::ok, record->version,
                record->version != 0 && record->host == record->version,
                record->version != 0 && record->device == record->version, record->pending.serial != 0};
    }

    Admission begin(View view, Access access, Fence fence = {}) {
        const std::lock_guard lock(mutex_);
        if (closed_)
            return {Status::closed};
        auto *record = find(view.buffer);
        if (!record || !valid_range(*record, view))
            return {};
        if (record->quarantined)
            return {Status::quarantined};
        if (record->pending.serial)
            return {Status::busy};
        if (access < Access::host_read || access > Access::readback)
            return {};
        const bool whole = view.offset == 0 && view.bytes == record->bytes;
        const bool host = record->version && record->host == record->version;
        const bool device = record->version && record->device == record->version;
        if ((access == Access::host_read || access == Access::upload ||
             (access == Access::host_write && !whole)) &&
            !host)
            return {Status::host_stale};
        if ((access == Access::device_read || access == Access::readback ||
             (access == Access::device_write && !whole)) &&
            !device)
            return {Status::device_stale};
        // Replication is whole-allocation only; a partial copy cannot certify untouched bytes.
        if ((access == Access::upload || access == Access::readback) && !whole)
            return {};
        if (gpu(access)) {
            // Reserving fences under a mutex does not order actual queue submissions.
            if (gpu_pending_)
                return {Status::busy};
            if (fence.queue != queue_ || !fence.value || fence.value == UINT64_MAX ||
                fence.value <= last_fence_)
                return {};
        } else if (fence.queue || fence.value)
            return {};
        const auto serial = next();
        if (!serial)
            return {Status::exhausted};
        if (gpu(access)) {
            last_fence_ = fence.value;
            gpu_pending_ = serial;
        }
        record->pending = {view.buffer, serial, access, fence};
        // Invalidate at write admission, not at completion. Aliases cannot read old evidence.
        if (access == Access::host_write || access == Access::device_write)
            record->host = record->device = 0;
        return {Status::ok, record->pending};
    }

    Status finish(Ticket ticket, Fence completed = {}) {
        const std::lock_guard lock(mutex_);
        if (closed_)
            return Status::closed;
        auto *record = find(ticket.buffer);
        if (!record || !ticket.serial || record->pending != ticket)
            return Status::invalid;
        if (gpu(ticket.access)) {
            // UINT64_MAX is D3D12's device-removal sentinel, not successful completion.
            if (completed.value == UINT64_MAX) {
                record->quarantined = true;
                record->host = record->device = 0;
                closed_ = true; // Device removal invalidates every replica in this session.
                return Status::quarantined;
            }
            if (completed.value > last_fence_)
                return Status::invalid;
            if (completed.queue != queue_ || completed.value < ticket.fence.value)
                return Status::fence_pending;
        } else if (completed.queue || completed.value)
            return Status::invalid;
        if (record->quarantined)
            return Status::quarantined;
        switch (ticket.access) {
        case Access::host_write:
            record->version = record->host = ticket.serial;
            break;
        case Access::device_write:
            record->version = record->device = ticket.serial;
            break;
        case Access::upload:
            record->device = record->version;
            break;
        case Access::readback:
            record->host = record->version;
            break;
        default:
            break;
        }
        if (gpu(ticket.access))
            gpu_pending_ = 0;
        record->pending = {};
        return Status::ok;
    }

    Status abort(Ticket ticket) {
        const std::lock_guard lock(mutex_);
        if (closed_)
            return Status::closed;
        auto *record = find(ticket.buffer);
        if (!record || !ticket.serial || record->pending != ticket)
            return Status::invalid;
        record->host = record->device = 0;
        // Unknown GPU progress cannot be recovered by a timer or a fresh CPU write.
        // Quarantined allocations require owning backend teardown; never recycle here.
        if (gpu(ticket.access)) {
            record->quarantined = true;
            closed_ = true; // Unknown queue progress revokes every admission in the session.
        } else
            record->pending = {};
        return record->quarantined ? Status::quarantined : Status::ok;
    }

    void close() {
        const std::lock_guard lock(mutex_);
        closed_ = true;
        // This releases no physical memory; the backend still must drain/destroy its device.
    }
};

} // namespace fluid::memory
