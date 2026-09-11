#include "vulkan_command_tracking.h"
#include <iostream>
#include <stdexcept>
#include <string>

namespace {
using namespace fluid::observation;
unsigned checks{};
void check(bool condition) {
    ++checks;
    if (!condition)
        throw std::runtime_error("Command tracking check " + std::to_string(checks) + " failed");
}
using Tracker = CommandTracker<8, 2, 2>;
int test_device{}, other{};

void record(Tracker &tracker, std::uint64_t command, std::uint64_t bytes, bool one_time = false) {
    check(tracker.begin(&test_device, command, true, true, one_time));
    check(tracker.copy(&test_device, command, bytes, true));
    check(tracker.end(&test_device, command, true));
}

std::optional<SubmittedCopies> submit(Tracker &tracker, std::uint64_t command) {
    SubmissionSnapshot<8> snapshot;
    tracker.prepare(&test_device, command, snapshot);
    return tracker.commit(&test_device, snapshot);
}

void replay_and_reset() {
    Tracker tracker;
    check(tracker.add_pool(&test_device, 1, true));
    check(tracker.allocate(&test_device, 10, 1, false, true));
    check(!submit(tracker, 10));
    record(tracker, 10, 1024);
    // Merely preparing a failed downstream submit cannot mark a recording used.
    SubmissionSnapshot<8> failed;
    tracker.prepare(&test_device, 10, failed);
    check(failed.known && failed.totals.bytes == 1024);
    const auto first = submit(tracker, 10);
    check(first && first->bytes == 1024 && first->copies == 1 && first->replays == 0);
    const auto second = submit(tracker, 10);
    check(second && second->bytes == 1024 && second->replays == 1);
    record(tracker, 10, 64);
    check(!tracker.commit(&test_device, failed));
    const auto renewed = submit(tracker, 10);
    check(renewed && renewed->bytes == 64 && renewed->replays == 0);
    check(tracker.reset(&test_device, 10, true));
    check(!submit(tracker, 10));
    record(tracker, 10, 32, true);
    check(submit(tracker, 10).has_value());
    check(!submit(tracker, 10));
    record(tracker, 10, 32, true);
    SubmissionSnapshot<8> duplicate;
    tracker.prepare(&test_device, 10, duplicate);
    tracker.prepare(&test_device, 10, duplicate);
    check(!tracker.commit(&test_device, duplicate));
    check(submit(tracker, 10)->replays == 0);
}

void secondary_generations() {
    Tracker tracker;
    check(tracker.add_pool(&test_device, 1, true));
    check(tracker.add_pool(&test_device, 2, true));
    check(tracker.allocate(&test_device, 10, 1, false, true));
    check(tracker.allocate(&test_device, 20, 2, true, true));
    record(tracker, 20, 128);
    check(tracker.begin(&test_device, 10, true, true, false));
    check(tracker.copy(&test_device, 10, 16, true));
    check(tracker.execute(&test_device, 10, 20));
    check(tracker.execute(&test_device, 10, 20));
    check(tracker.end(&test_device, 10, true));
    const auto first = submit(tracker, 10);
    check(first && first->bytes == 272 && first->copies == 3 && first->primary == 1 &&
          first->secondary == 2 && first->replays == 1);
    check(submit(tracker, 10)->replays == 3);
    check(!submit(tracker, 20));
    // Re-recording or recycling a secondary never revives an old primary link.
    record(tracker, 20, 256);
    check(!submit(tracker, 10));
    check(tracker.begin(&test_device, 10, true, true, false));
    check(tracker.execute(&test_device, 10, 20));
    check(tracker.end(&test_device, 10, true));
    check(tracker.free(&test_device, 20));
    check(tracker.allocate(&test_device, 20, 2, true, true));
    record(tracker, 20, 512);
    check(!submit(tracker, 10));
    check(tracker.begin(&test_device, 10, true, true, false));
    check(tracker.execute(&test_device, 10, 20));
    check(tracker.end(&test_device, 10, true));
    check(tracker.reset_pool(&test_device, 2, true));
    record(tracker, 20, 512);
    check(!submit(tracker, 10));
    // Nested command-buffer extensions are outside this observer's model.
    check(tracker.begin(&test_device, 20, true, true, false));
    check(!tracker.execute(&test_device, 20, 20));
    check(!tracker.end(&test_device, 20, true));
}

void limits_and_failure_atomicity() {
    Tracker tracker;
    check(tracker.add_pool(&test_device, 1, true));
    check(tracker.add_pool(&other, 1, true));
    check(!tracker.add_pool(&test_device, 2, true));
    for (unsigned i = 1; i <= 8; ++i)
        check(tracker.allocate(&test_device, i, 1, false, true));
    check(!tracker.allocate(&test_device, 9, 1, false, true));
    check(!tracker.allocate(&other, 1, 1, false, true));
    record(tracker, 1, 8);
    record(tracker, 2, 16);
    SubmissionSnapshot<1> limited;
    tracker.prepare(&test_device, 1, limited);
    tracker.prepare(&test_device, 2, limited);
    check(limited.overflow && !tracker.commit(&test_device, limited));
    SubmissionSnapshot<1> oversized;
    oversized.count = 2;
    check(!tracker.commit(&test_device, oversized));
    check(submit(tracker, 1)->replays == 0);
    SubmissionSnapshot<8> mixed;
    tracker.prepare(&test_device, 2, mixed);
    tracker.prepare(&test_device, 999, mixed);
    check(!tracker.commit(&test_device, mixed));
    check(submit(tracker, 2)->replays == 0);
    record(tracker, 1, 8);
    record(tracker, 2, 16);
    SubmissionSnapshot<8> stale;
    tracker.prepare(&test_device, 1, stale);
    tracker.prepare(&test_device, 2, stale);
    check(tracker.free(&test_device, 2));
    check(!tracker.commit(&test_device, stale));
    check(submit(tracker, 1)->replays == 0);
    check(tracker.destroy_pool(&test_device, 1) == 7);
    check(tracker.allocate(&other, 1, 1, false, true));
    check(tracker.destroy_device(&other) == 1);
    check(tracker.destroy_device(&test_device) == 0);
    // Fixed slots must survive churn without consuming historical capacity.
    for (unsigned i = 0; i < 1000; ++i) {
        check(tracker.add_pool(&test_device, 1, true));
        check(tracker.allocate(&test_device, 1, 1, false, true));
        check(tracker.destroy_pool(&test_device, 1) == 1);
    }
}

void incomplete_and_overflow() {
    Tracker tracker;
    check(tracker.add_pool(&test_device, 1, true));
    check(tracker.allocate(&test_device, 1, 1, false, true));
    check(tracker.allocate(&test_device, 2, 1, false, true));
    record(tracker, 1, UINT64_MAX);
    record(tracker, 2, 1);
    SubmissionSnapshot<8> overflow;
    tracker.prepare(&test_device, 1, overflow);
    tracker.prepare(&test_device, 2, overflow);
    check(overflow.overflow && !tracker.commit(&test_device, overflow));
    check(tracker.begin(&test_device, 1, true, true, false));
    check(tracker.copy(&test_device, 1, UINT64_MAX, true));
    check(!tracker.copy(&test_device, 1, 1, true));
    check(!tracker.end(&test_device, 1, true));
    check(!submit(tracker, 1));
    record(tracker, 1, 8);
    check(!tracker.begin(&test_device, 1, false, true, false));
    check(!submit(tracker, 1));
    record(tracker, 1, 8);
    check(!tracker.reset_pool(&test_device, 1, false));
    check(!submit(tracker, 1));
    check(!tracker.end(&test_device, 1, true));
    check(!tracker.begin(&test_device, 1, true, false, false));
    check(!tracker.copy(&test_device, 1, 8, true));
    check(!tracker.end(&test_device, 1, true));
    record(tracker, 1, 8);
    check(!tracker.end(&test_device, 1, false));
    check(!submit(tracker, 1));
    check(tracker.free(&test_device, 2));
    check(tracker.allocate(&test_device, 2, 1, true, true));
    record(tracker, 2, 4);
    check(tracker.begin(&test_device, 1, true, true, false));
    check(tracker.execute(&test_device, 1, 2));
    check(tracker.execute(&test_device, 1, 2));
    check(!tracker.execute(&test_device, 1, 2));
    check(!tracker.end(&test_device, 1, true));
    check(!submit(tracker, 1));
}
} // namespace

int main() {
    try {
        replay_and_reset();
        secondary_generations();
        limits_and_failure_atomicity();
        incomplete_and_overflow();
        std::cout << "Vulkan command tracking: " << checks << " checks passed\n";
        std::cout << "Fixed resource/command state: " << sizeof(BufferTracker<>) + sizeof(CommandTracker<>)
                  << " bytes; submit snapshot: " << sizeof(SubmissionSnapshot<>) << " bytes\n";
        return 0;
    } catch (const std::exception &error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
