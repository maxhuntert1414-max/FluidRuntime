#pragma once

#include <cstdint>

namespace fluid::vulkan {
// Epoch and limits are captured once, never reloaded from writable IPC when
// reserving an action. A changed epoch revokes the captured authority.
struct Policy {
    std::int64_t epoch{};
    std::int64_t deadline{};
    std::uint64_t budget{};
    std::uint64_t applied{};
    bool enabled{};

    bool accept(std::int64_t published, std::int64_t expires,
        std::int64_t mask, std::int64_t count, std::uint32_t maximum,
        std::int64_t now, std::int64_t frequency, bool registered) {
        if (epoch != 0 || published != 1 || !registered || mask != 16 ||
            count < 1 || count > maximum || count > 128 ||
            frequency <= 0 || expires <= now ||
            expires - now > frequency * 4) {
            enabled = false;
            return false;
        }
        epoch = published;
        deadline = expires;
        budget = static_cast<std::uint64_t>(count);
        enabled = true;
        return true;
    }

    bool reserve(std::int64_t now, std::int64_t published) {
        if (!enabled || published != epoch || now >= deadline || applied >= budget) {
            enabled = false;
            return false;
        }
        ++applied;
        return true;
    }
};
}
