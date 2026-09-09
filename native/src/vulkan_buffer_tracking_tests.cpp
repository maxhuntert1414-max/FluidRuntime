#include "vulkan_buffer_tracking.h"
#include <iostream>
#include <stdexcept>
#include <string>

namespace {
using namespace fluid::observation;
unsigned checks{};
void check(bool condition) {
    ++checks;
    if (!condition)
        throw std::runtime_error("Buffer tracking check " + std::to_string(checks) + " failed");
}

void table_churn() {
    ResourceTable<unsigned, 8> table;
    int first_device{}, second_device{};
    check(!table.insert(&first_device, 0, 1));
    for (unsigned cycle = 0; cycle < 1000; ++cycle) {
        for (unsigned i = 1; i <= 8; ++i)
            check(table.insert(&first_device, i, i + cycle));
        check(!table.insert(&first_device, 9, 99));
        check(!table.insert(&second_device, 1, 99));
        for (unsigned i = 1; i <= 8; ++i)
            check(*table.find(&first_device, i) == i + cycle);
        for (unsigned i = 8; i; --i) {
            check(table.erase(&first_device, i) == i + cycle);
            check(!table.find(&first_device, i));
            check(!table.erase(&first_device, i));
        }
    }
    check(table.insert(&first_device, 1, 11));
    check(!table.insert(&first_device, 1, 12));
    check(table.insert(&second_device, 1, 22));
    table.erase_device(&first_device, [](unsigned value) { check(value == 11); });
    check(!table.find(&first_device, 1));
    check(*table.find(&second_device, 1) == 22);
}

void attribution_and_lifetime() {
    BufferTracker<8> tracker;
    int device{}, other{};
    auto direction = [&](std::uint64_t source, std::uint64_t destination) {
        return tracker.attribute(&device, source, destination, 0, 0, 32).memory_class;
    };
    for (unsigned i = 1; i <= 4; ++i) {
        check(tracker.add_memory(&device, i, 1024, i & 3U, true));
        check(tracker.add_buffer(&device, i, 256, true));
        check(tracker.bind(&device, i, i, 128, true));
    }
    check(direction(2, 1) == CopyMemoryClass::host_to_device);
    check(direction(1, 2) == CopyMemoryClass::device_to_host);
    check(direction(1, 1) == CopyMemoryClass::device_to_device);
    check(direction(2, 2) == CopyMemoryClass::host_to_host);
    check(direction(3, 1) == CopyMemoryClass::shared);
    check(direction(2, 3) == CopyMemoryClass::shared);
    check(direction(4, 1) == CopyMemoryClass::unknown);
    check(direction(1, 99) == CopyMemoryClass::unknown);
    check(tracker.attribute(&other, 1, 2, 0, 0, 32).memory_class == CopyMemoryClass::unknown);

    check(tracker.add_buffer(&device, 5, 256, true));
    check(tracker.bind(&device, 5, 1, 512, true));
    check(tracker.attribute(&device, 1, 5, 0, 0, 32).same_allocation);
    check(!tracker.attribute(&device, 1, 2, 0, 0, 32).same_allocation);
    const auto old = tracker.remove_memory(&device, 1);
    check(old.has_value());
    check(tracker.add_memory(&device, 1, 1024, 2, true));
    check(direction(1, 2) == CopyMemoryClass::unknown);
    check(direction(5, 2) == CopyMemoryClass::unknown);
    check(tracker.bind(&device, 1, 1, 0, true));
    check(direction(1, 2) == CopyMemoryClass::host_to_host);
    check(tracker.remove_buffer(&device, 1));
    check(tracker.add_buffer(&device, 1, 256, true));
    check(direction(1, 2) == CopyMemoryClass::unknown);
    check(tracker.remove_memory(&device, 1)->generation != old->generation);

    // Unknown extension chains, sparse/protected resources, and failed binds
    // are represented as unknown; this tracker never authorizes execution changes.
    check(tracker.add_memory(&device, 6, 1024, 1, false));
    check(!tracker.bind(&device, 1, 6, 0, true));
    check(!tracker.bind(&device, 2, 2, 0, false));
    check(direction(2, 3) == CopyMemoryClass::unknown);
    check(tracker.add_buffer(&device, 6, 128, false));
    check(!tracker.bind(&device, 6, 2, 0, true));
    unsigned memories{}, buffers{};
    tracker.remove_device(
        &device, [&](const MemoryRecord &) { ++memories; }, [&](const BufferRecord &) { ++buffers; });
    check(memories == 4 && buffers == 6);
    check(direction(3, 4) == CopyMemoryClass::unknown);
    for (unsigned i = 1; i <= 8; ++i) {
        check(tracker.add_memory(&device, i, 1024, 1, true));
        check(tracker.add_buffer(&device, i, 256, true));
    }
    check(!tracker.add_memory(&device, 9, 1024, 1, true));
    check(!tracker.add_buffer(&device, 9, 256, true));
    check(!tracker.bind(&device, 9, 1, 0, true));
    check(direction(9, 1) == CopyMemoryClass::unknown);
}

void ranges_do_not_wrap() {
    BufferTracker<2> tracker;
    int device{};
    constexpr auto maximum = std::numeric_limits<std::uint64_t>::max();
    check(tracker.add_memory(&device, 1, maximum, 1, true));
    check(tracker.add_buffer(&device, 1, 256, true));
    check(tracker.add_buffer(&device, 2, 256, true));
    check(!tracker.bind(&device, 1, 1, maximum - 255, true));
    check(tracker.bind(&device, 1, 1, maximum - 256, true));
    check(tracker.bind(&device, 2, 1, 0, true));
    check(tracker.attribute(&device, 1, 2, 255, 255, 1).memory_class == CopyMemoryClass::device_to_device);
    for (const auto size : {std::uint64_t{0}, std::uint64_t{2}, maximum}) {
        check(tracker.attribute(&device, 1, 2, 255, 255, size).memory_class == CopyMemoryClass::unknown);
    }
    check(tracker.attribute(&device, 1, 2, maximum, 0, 1).memory_class == CopyMemoryClass::unknown);
    check(tracker.attribute(&device, 1, 2, 0, maximum, 1).memory_class == CopyMemoryClass::unknown);
}
} // namespace

int main() {
    try {
        table_churn();
        attribution_and_lifetime();
        ranges_do_not_wrap();
        std::cout << "Vulkan buffer tracking: " << checks << " checks passed\n";
        return 0;
    } catch (const std::exception &error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
