#pragma once

#include "vulkan_buffer_tracking.h"

namespace fluid::observation {

struct RecordingReference {
    std::uint64_t handle{};
    std::uint64_t generation{};
};

struct SubmittedCopies {
    std::uint64_t copies{};
    std::uint64_t bytes{};
    std::uint64_t primary{};
    std::uint64_t secondary{};
    std::uint64_t replays{};
};

// A bounded snapshot crosses the downstream submit call without retaining
// pointers into the tracker. Failed calls never commit it. All access requires
// the layer lock.
template <std::size_t Capacity = 512> struct SubmissionSnapshot {
    std::array<RecordingReference, Capacity> recordings{};
    SubmittedCopies totals{};
    std::size_t count{};
    bool known{true};
    bool overflow{};
};

template <std::size_t BufferCapacity = 4096, std::size_t PoolCapacity = 1024,
          std::size_t ReferenceCapacity = 64>
class CommandTracker {
    struct Pool {
        bool known{};
    };
    enum class State { initial, recording, executable, invalid };
    struct Recording {
        std::uint64_t pool{};
        std::uint64_t generation{};
        std::uint64_t copies{};
        std::uint64_t bytes{};
        std::array<RecordingReference, ReferenceCapacity> secondaries{};
        std::size_t reference_count{};
        State state{State::initial};
        bool secondary{};
        bool allocation_known{};
        bool known{};
        bool one_time{};
        bool submitted{};
    };
    ResourceTable<Pool, PoolCapacity> pools_;
    ResourceTable<Recording, BufferCapacity> recordings_;
    std::uint64_t generation_{};

    bool renew(Recording &recording) noexcept {
        const auto pool = recording.pool;
        const auto secondary = recording.secondary;
        const auto allocation_known = recording.allocation_known;
        recording = {};
        recording.pool = pool;
        recording.secondary = secondary;
        recording.allocation_known = allocation_known;
        if (generation_ == UINT64_MAX) {
            recording.state = State::invalid;
            return false;
        }
        recording.generation = ++generation_;
        return true;
    }

    static bool accumulate(std::uint64_t &target, std::uint64_t value) noexcept {
        if (value > UINT64_MAX - target)
            return false;
        target += value;
        return true;
    }

    template <std::size_t Capacity>
    bool append(void *device, std::uint64_t handle, bool secondary,
                SubmissionSnapshot<Capacity> &snapshot) noexcept {
        const auto *recording = recordings_.find(device, handle);
        if (!recording || !recording->known || recording->state != State::executable ||
            recording->secondary != secondary || (recording->one_time && recording->submitted))
            return false;
        if (snapshot.count >= Capacity) {
            snapshot.overflow = true;
            return false;
        }
        if (recording->one_time) {
            for (std::size_t i = 0; i < Capacity && i < snapshot.count; ++i) {
                if (snapshot.recordings[i].handle == handle)
                    return false;
            }
        }
        if (!accumulate(snapshot.totals.copies, recording->copies) ||
            !accumulate(snapshot.totals.bytes, recording->bytes)) {
            snapshot.overflow = true;
            return false;
        }
        snapshot.recordings[snapshot.count++] = {handle, recording->generation};
        if (secondary)
            ++snapshot.totals.secondary;
        else
            ++snapshot.totals.primary;
        return true;
    }

  public:
    bool is_secondary(void *device, std::uint64_t handle) noexcept {
        const auto *recording = recordings_.find(device, handle);
        return recording && recording->secondary;
    }

    bool add_pool(void *device, std::uint64_t pool, bool known) noexcept {
        return pools_.insert(device, pool, {known});
    }

    bool allocate(void *device, std::uint64_t handle, std::uint64_t pool, bool secondary,
                  bool known) noexcept {
        const auto *owner = pools_.find(device, pool);
        if (!owner)
            return false;
        Recording recording{};
        recording.pool = pool;
        recording.secondary = secondary;
        recording.allocation_known = known && owner->known;
        return renew(recording) && recordings_.insert(device, handle, recording);
    }

    bool free(void *device, std::uint64_t handle) noexcept {
        return recordings_.erase(device, handle).has_value();
    }

    std::uint64_t destroy_pool(void *device, std::uint64_t pool) noexcept {
        std::uint64_t retired{};
        recordings_.visit_device(device, [&](std::uint64_t handle, const Recording &recording) {
            if (recording.pool == pool) {
                free(device, handle);
                ++retired;
            }
        });
        pools_.erase(device, pool);
        return retired;
    }

    std::uint64_t destroy_device(void *device) noexcept {
        std::uint64_t retired{};
        recordings_.erase_device(device, [&](const Recording &) { ++retired; });
        pools_.erase_device(device, [](const Pool &) {});
        return retired;
    }

    bool begin(void *device, std::uint64_t handle, bool success, bool understood, bool one_time) noexcept {
        auto *recording = recordings_.find(device, handle);
        if (!recording || !renew(*recording))
            return false;
        recording->state = success ? State::recording : State::invalid;
        recording->known = success && understood && recording->allocation_known;
        recording->one_time = one_time;
        return recording->known;
    }

    bool end(void *device, std::uint64_t handle, bool success) noexcept {
        auto *recording = recordings_.find(device, handle);
        if (!recording)
            return false;
        if (!success || recording->state != State::recording) {
            recording->state = State::invalid;
            recording->known = false;
            return false;
        }
        recording->state = State::executable;
        return recording->known;
    }

    bool reset(void *device, std::uint64_t handle, bool success) noexcept {
        auto *recording = recordings_.find(device, handle);
        if (!recording || !renew(*recording))
            return false;
        recording->state = success ? State::initial : State::invalid;
        return success;
    }

    bool reset_pool(void *device, std::uint64_t pool, bool success) noexcept {
        bool known = pools_.find(device, pool) != nullptr && success;
        recordings_.visit_device(device, [&](std::uint64_t handle, const Recording &recording) {
            if (recording.pool == pool && !reset(device, handle, success))
                known = false;
        });
        return known;
    }

    bool copy(void *device, std::uint64_t handle, std::uint64_t bytes, bool understood) noexcept {
        auto *recording = recordings_.find(device, handle);
        if (!recording)
            return false;
        recording->known = recording->known && recording->state == State::recording && understood;
        if (!accumulate(recording->copies, 1) || !accumulate(recording->bytes, bytes))
            recording->known = false;
        return recording->known;
    }

    bool execute(void *device, std::uint64_t primary, std::uint64_t secondary) noexcept {
        auto *parent = recordings_.find(device, primary);
        if (!parent)
            return false;
        const auto *child = recordings_.find(device, secondary);
        if (!parent->known || parent->state != State::recording || parent->secondary || !child ||
            !child->known || !child->secondary || child->state != State::executable ||
            parent->reference_count == ReferenceCapacity) {
            parent->known = false;
            return false;
        }
        parent->secondaries[parent->reference_count++] = {secondary, child->generation};
        return true;
    }

    template <std::size_t Capacity>
    void prepare(void *device, std::uint64_t primary, SubmissionSnapshot<Capacity> &snapshot) noexcept {
        if (!snapshot.known)
            return;
        if (!append(device, primary, false, snapshot)) {
            snapshot.known = false;
            return;
        }
        const auto *parent = recordings_.find(device, primary);
        for (std::size_t i = 0; i < parent->reference_count; ++i) {
            const auto &reference = parent->secondaries[i];
            const auto *child = recordings_.find(device, reference.handle);
            if (!child || child->generation != reference.generation ||
                !append(device, reference.handle, true, snapshot)) {
                snapshot.known = false;
                return;
            }
        }
    }

    template <std::size_t Capacity>
    std::optional<SubmittedCopies> commit(void *device,
                                          const SubmissionSnapshot<Capacity> &snapshot) noexcept {
        if (!snapshot.known || snapshot.count > Capacity)
            return std::nullopt;
        // A reset/free/reuse during the driver call must not attach old work to a
        // replacement recording. Preflight every token before updating any state.
        for (std::size_t i = 0; i < Capacity && i < snapshot.count; ++i) {
            const auto &reference = snapshot.recordings[i];
            const auto *recording = recordings_.find(device, reference.handle);
            if (!recording || recording->generation != reference.generation || !recording->known ||
                recording->state != State::executable || (recording->one_time && recording->submitted))
                return std::nullopt;
        }
        auto totals = snapshot.totals;
        for (std::size_t i = 0; i < Capacity && i < snapshot.count; ++i) {
            auto *recording = recordings_.find(device, snapshot.recordings[i].handle);
            if (recording->submitted)
                ++totals.replays;
            recording->submitted = true;
        }
        return totals;
    }
};

} // namespace fluid::observation
