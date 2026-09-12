#include "vulkan_completion_tracking.h"
#include "vulkan_command_tracking.h"
#include <cstdio>
#include <cstdlib>

using namespace fluid::observation;
namespace {
unsigned checks{};
void check(bool condition) {
    ++checks;
    if (!condition) {
        std::fprintf(stderr, "Completion check %u failed\n", checks);
        std::exit(1);
    }
}
} // namespace

int main() {
    CompletionTracker<2, 2> tracker;
    int a{}, b{};
    void *device = &a;
    const CompletionTotals copy{1, 2, 64};
    check(tracker.create_fence(device, 10));
    check(tracker.create_fence(device, 11));
    check(!tracker.create_fence(device, 12));
    check(!tracker.create_fence(device, 10));
    check(!tracker.complete_fence(device, tracker.fence_marker(device, 10)).submits);
    check(tracker.submit(device, 1, {}, &copy));
    auto token = tracker.invalidate_fence(device, 10);
    check(tracker.submit(device, 1, token, &copy));
    auto first = tracker.fence_marker(device, 10);
    token = tracker.invalidate_fence(device, 11);
    check(tracker.submit(device, 1, token, &copy));
    const auto latest = tracker.fence_marker(device, 11);
    auto delta = tracker.complete_fence(device, latest);
    check(delta.submits == 3 && delta.copies == 6 && delta.bytes == 192);
    check(!tracker.complete_fence(device, first).submits);
    check(!tracker.complete_fence(device, latest).submits);
    check(!tracker.complete_fence(&b, latest).submits);

    token = tracker.invalidate_fence(device, 10);
    check(tracker.submit(device, 1, token, &copy));
    first = tracker.fence_marker(device, 10);
    tracker.invalidate_fence(device, 10);
    check(!tracker.complete_fence(device, first).submits);
    // A reset while the downstream submit runs cannot attach an old token.
    check(tracker.submit(device, 1, token, &copy));
    check(!tracker.fence_marker(device, 10).queue.handle);
    token = tracker.invalidate_fence(device, 10);
    // An unresolved or empty submission can fence earlier attributed work.
    check(tracker.submit(device, 1, token, nullptr));
    delta = tracker.complete_fence(device, tracker.fence_marker(device, 10));
    check(delta.submits == 2 && delta.bytes == 128);
    check(tracker.submit(device, 2, {}, &copy));
    check(!tracker.submit(device, 3, {}, &copy));
    CompletionTracker<2, 2>::DeviceMarkers markers{};
    tracker.device_markers(device, markers);
    check(tracker.submit(device, 2, {}, &copy));
    std::uint64_t completed{};
    for (const auto &marker : markers)
        completed += tracker.complete_queue(device, marker).submits;
    check(completed == 1); // Idle snapshots never include later work.
    auto invalid = tracker.queue_marker(device, 2);
    invalid.totals.bytes = UINT64_MAX;
    check(!tracker.complete_queue(device, invalid).submits);
    first = tracker.fence_marker(device, 10);
    check(tracker.destroy_fence(device, 10));
    check(tracker.create_fence(device, 10));
    check(!tracker.complete_fence(device, first).submits);
    std::uint64_t abandoned{}, retired{};
    const auto stale = tracker.queue_marker(device, 2);
    tracker.destroy_device(device, [&](auto pending) { abandoned += pending; }, [&] { ++retired; });
    check(abandoned == 1 && retired == 2);
    check(tracker.submit(device, 2, {}, &copy));
    check(!tracker.complete_queue(device, stale).submits);
    tracker.destroy_device(device, [](auto) {}, [] {});

    const CompletionTotals extreme{1, UINT64_MAX, UINT64_MAX};
    check(tracker.submit(device, 1, {}, &extreme));
    check(!tracker.submit(device, 1, {}, &copy));
    delta = tracker.complete_queue(device, tracker.queue_marker(device, 1));
    check(delta.submits == 1 && delta.bytes == UINT64_MAX && delta.copies == UINT64_MAX);
    tracker.destroy_device(device, [](auto) {}, [] {});
    // Churn proves slots are reclaimed while generations never revive markers.
    for (unsigned i = 0; i < 10000; ++i) {
        check(tracker.create_fence(device, 10));
        check(tracker.submit(device, 1, tracker.invalidate_fence(device, 10), &copy));
        check(tracker.complete_fence(device, tracker.fence_marker(device, 10)).submits == 1);
        tracker.destroy_device(device, [](auto) {}, [] {});
    }
    std::printf("%u completion checks; combined fixed state %zu bytes; idle snapshot %zu bytes\n", checks,
                sizeof(BufferTracker<>) + sizeof(CommandTracker<>) + sizeof(CompletionTracker<>),
                sizeof(CompletionTracker<>::DeviceMarkers));
}
