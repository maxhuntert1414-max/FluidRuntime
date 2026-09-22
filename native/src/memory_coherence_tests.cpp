#include "memory_coherence.h"
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <thread>
#include <vector>

using namespace fluid::memory;
namespace {
unsigned checks{};
void check(bool condition) {
    ++checks;
    if (!condition) {
        std::fprintf(stderr, "Memory coherence check %u failed\n", checks);
        std::exit(1);
    }
}
template <class State> Ticket begin(State &state, View view, Access access, Fence fence = {}) {
    const auto result = state.begin(view, access, fence);
    check(result.status == Status::ok);
    return result.ticket;
}
} // namespace

int main() {
    Coherence<2> state(1, 7);
    Buffer buffer{}, other{};
    check(state.allocate(1024, buffer) == Status::ok);
    const View full{buffer, 0, 1024}, alias{buffer, 16, 32};
    check(state.begin(full, Access::host_read).status == Status::host_stale);
    check(state.begin(alias, Access::host_write).status == Status::host_stale);
    check(state.begin(alias, Access::device_write, {7, 1}).status == Status::device_stale);
    auto write = begin(state, full, Access::host_write);
    check(state.release(buffer) == Status::busy);
    check(state.begin(alias, Access::host_read).status == Status::busy);
    check(state.inspect(alias).busy);
    check(state.finish(write) == Status::ok);
    check(state.finish(write) == Status::invalid);
    check(state.inspect(alias).host_current && !state.inspect(alias).device_current);
    check(state.begin(full, Access::device_read, {7, 1}).status == Status::device_stale);
    check(state.begin(alias, Access::upload, {7, 1}).status == Status::invalid);
    check(state.begin(full, Access::upload, {8, 1}).status == Status::invalid);
    check(state.begin(full, Access::upload, {7, UINT64_MAX}).status == Status::invalid);
    auto upload = begin(state, full, Access::upload, {7, 1});
    auto forged = upload;
    forged.access = Access::device_write;
    check(state.finish(forged, {7, 1}) == Status::invalid);
    forged = upload;
    ++forged.serial;
    check(state.finish(forged, {7, 1}) == Status::invalid);
    check(state.finish(upload, {8, 1}) == Status::fence_pending);
    check(state.finish(upload, {7, 0}) == Status::fence_pending);
    check(state.finish(upload, {7, 2}) == Status::invalid);
    check(!state.inspect(full).device_current);
    check(state.begin(alias, Access::host_write).status == Status::busy);
    check(state.finish(upload, {7, 1}) == Status::ok);
    check(state.inspect(alias).device_current);
    const auto old_version = state.inspect(full).version;
    auto gpu_read = begin(state, alias, Access::device_read, {7, 2});
    check(state.begin(full, Access::host_write).status == Status::busy);
    check(state.finish(gpu_read, {7, 2}) == Status::ok);
    write = begin(state, alias, Access::host_write);
    check(!state.inspect(full).device_current && !state.inspect(full).host_current);
    check(state.finish(write) == Status::ok);
    check(state.inspect(full).version > old_version);
    check(!state.inspect(full).device_current);
    upload = begin(state, full, Access::upload, {7, 3});
    check(state.finish(upload, {7, 3}) == Status::ok);
    auto gpu_write = begin(state, alias, Access::device_write, {7, 4});
    check(state.finish(gpu_write, {7, 4}) == Status::ok);
    check(state.inspect(full).device_current && !state.inspect(full).host_current);
    check(state.begin(alias, Access::host_write).status == Status::host_stale);
    auto readback = begin(state, full, Access::readback, {7, 5});
    check(state.finish(readback, {7, 4}) == Status::fence_pending);
    check(state.finish(readback, {7, 5}) == Status::ok);
    auto read = begin(state, alias, Access::host_read);
    check(state.finish(read) == Status::ok);
    check(state.begin(full, Access::device_read, {7, 5}).status == Status::invalid);
    check(state.begin(full, Access::host_read, {7, 6}).status == Status::invalid);
    check(state.begin(full, static_cast<Access>(99)).status == Status::invalid);
    check(state.inspect({buffer, UINT64_MAX, 8}).status == Status::invalid);
    check(state.inspect({buffer, 1, UINT64_MAX}).status == Status::invalid);
    check(state.inspect({buffer, 0, 0}).status == Status::invalid);
    check(state.inspect({{2, buffer.generation}, 0, 1}).status == Status::invalid);
    check(state.allocate(64, other) == Status::ok);
    Buffer rejected{};
    check(state.allocate(64, rejected) == Status::capacity && !rejected.generation);
    check(state.release(other) == Status::ok);
    check(state.allocate(0, rejected) == Status::invalid);
    check(state.allocate(UINT64_MAX, rejected) == Status::invalid);

    // Abandoned CPU writes poison content but can be repaired by a complete overwrite.
    write = begin(state, full, Access::host_write);
    check(state.abort(write) == Status::ok);
    check(state.finish(write) == Status::invalid);
    check(state.begin(alias, Access::host_write).status == Status::host_stale);
    write = begin(state, full, Access::host_write);
    check(state.finish(write) == Status::ok);
    const auto retired = buffer;
    check(state.release(buffer) == Status::ok);
    check(state.allocate(1024, buffer) == Status::ok && buffer != retired);
    check(state.begin(full, Access::host_read).status == Status::invalid);
    check(state.finish(write) == Status::invalid);
    check(state.release(retired) == Status::invalid);

    // Unknown GPU progress/device loss must never unlock or recycle an allocation.
    const View fresh{buffer, 0, 1024};
    gpu_write = begin(state, fresh, Access::device_write, {7, 6});
    check(state.abort(gpu_write) == Status::quarantined);
    check(state.finish(gpu_write, {7, 6}) == Status::closed);
    check(state.release(buffer) == Status::closed);
    check(state.begin(fresh, Access::host_write).status == Status::closed);
    check(state.inspect(fresh).status == Status::closed);
    state.close();
    check(state.allocate(64, other) == Status::closed);
    check(state.begin(fresh, Access::host_write).status == Status::closed);
    check(state.finish(gpu_write, {7, 7}) == Status::closed);
    Coherence<> disconnected(100, 7);
    check(disconnected.finish(write) == Status::invalid);
    Coherence<> invalid(0, 7);
    check(invalid.allocate(64, other) == Status::closed);

    // One GPU ticket per session prevents out-of-order submission by independent callers.
    Coherence<2> lost(15, 7);
    check(lost.allocate(64, buffer) == Status::ok);
    check(lost.allocate(64, other) == Status::ok);
    gpu_write = begin(lost, {buffer, 0, 64}, Access::device_write, {7, 1});
    check(lost.begin({other, 0, 64}, Access::device_write, {7, 2}).status == Status::busy);
    write = begin(lost, {other, 0, 64}, Access::host_write);
    check(lost.finish(write) == Status::ok);
    check(lost.finish(gpu_write, {7, 0}) == Status::fence_pending);
    check(lost.begin({other, 0, 64}, Access::device_write, {7, 2}).status == Status::busy);
    check(lost.finish(gpu_write, {7, 1}) == Status::ok);
    gpu_write = begin(lost, {other, 0, 64}, Access::device_write, {7, 2});
    check(lost.finish(gpu_write, {7, UINT64_MAX}) == Status::quarantined);
    check(lost.inspect({buffer, 0, 64}).status == Status::closed);
    check(lost.finish(gpu_write, {7, 2}) == Status::closed);

    // No history growth and no stale IDs after slot reuse.
    Coherence<1> churn(10, 7);
    for (int i = 0; i < 10000; ++i) {
        check(churn.allocate(64, buffer) == Status::ok);
        write = begin(churn, {buffer, 0, 64}, Access::host_write);
        check(churn.finish(write) == Status::ok);
        check(churn.release(buffer) == Status::ok);
        check(churn.finish(write) == Status::invalid);
    }
    Coherence<1, 2> wrap(11, 7);
    check(wrap.allocate(64, buffer) == Status::ok);
    write = begin(wrap, {buffer, 0, 64}, Access::host_write);
    check(wrap.finish(write) == Status::ok);
    check(wrap.begin({buffer, 0, 64}, Access::host_read).status == Status::exhausted);
    check(wrap.begin({buffer, 0, 64}, Access::host_write).status == Status::closed);

    Coherence<1> concurrent(12, 7);
    check(concurrent.allocate(64, buffer) == Status::ok);
    std::atomic<unsigned> committed{}, errors{};
    std::vector<std::thread> workers;
    for (int thread = 0; thread < 4; ++thread) {
        workers.emplace_back([&] {
            for (int i = 0; i < 2000;) {
                auto result = concurrent.begin({buffer, 0, 64}, Access::host_write);
                if (result.status == Status::busy) {
                    std::this_thread::yield();
                    continue;
                }
                if (result.status != Status::ok || concurrent.finish(result.ticket) != Status::ok)
                    ++errors;
                else
                    ++committed;
                ++i;
            }
        });
    }
    for (auto &worker : workers)
        worker.join();
    check(!errors && committed == 8000);
    check(concurrent.inspect({buffer, 0, 64}).host_current);
    std::printf("Memory coherence: %u checks, 10000 churn cycles, 8000 concurrent commits, %zu fixed bytes\n",
                checks, sizeof(Coherence<>));
}
