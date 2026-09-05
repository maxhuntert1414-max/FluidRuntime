#include "vulkan_transfer_policy.h"
#include <iostream>
#include <stdexcept>

int main() {
    using fluid::vulkan::Policy;
    unsigned checks = 0;
    const auto check = [&](bool value) {
        ++checks;
        if (!value) throw std::runtime_error("Vulkan policy regression");
    };
    Policy p;
    check(!p.reserve(1, 0));
    check(p.accept(1, 4000, 16, 2, 128, 1, 1000, true));
    check(p.reserve(2, 1));
    check(p.reserve(3, 1));
    check(!p.reserve(4, 1));
    check(p.applied == 2);
    check(!p.accept(2, 4000, 16, 2, 128, 1, 1000, true));
    for (int test = 0; test < 8; ++test) {
        Policy invalid;
        check(!invalid.accept(test == 0 ? 2 : 1, test == 1 ? 1 :
            test == 2 ? 4002 : 4000, test == 3 ? 8 : 16,
            test == 4 ? 0 : test == 5 ? 129 : 2,
            test == 6 ? 1U : 128U, 1, 1000, test != 7));
        check(!invalid.reserve(2, 1));
    }
    Policy expired;
    check(expired.accept(1, 10, 16, 128, 128, 1, 1000, true));
    check(!expired.reserve(10, 1));
    Policy replay;
    check(replay.accept(1, 4000, 16, 128, 128, 1, 1000, true));
    check(!replay.reserve(2, 2));
    check(!replay.reserve(3, 1));
    std::cout << "Vulkan policy: " << checks << " checks passed\n";
}
