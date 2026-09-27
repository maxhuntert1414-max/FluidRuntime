// White-box executable uses the exact production implementation. No fault-injection
// switches or extra entry points are compiled into the shipping DLL.
#include "memory_arena.cpp"
#include <cstdio>

int main() {
    try {
        const fma_options options{sizeof(fma_options), FMA_ABI_VERSION, FMA_WARP, 100, FMA_MAX_BYTES};
        fma_arena arena{};
        require(fma_create(&options, &arena) == FMA_OK, FMA_INTERNAL_ERROR);
        fma_buffer buffer{};
        require(fma_allocate(arena, 4096, &buffer) == FMA_OK, FMA_INTERNAL_ERROR);
        fma_span span{};
        require(fma_acquire(arena, buffer, 0, 4096, FMA_HOST_WRITE, &span) == FMA_OK, FMA_INTERNAL_ERROR);
        if (!span.data) {
            throw Failure{FMA_INTERNAL_ERROR};
        }
        std::memset(span.data, 19, 4096);
        require(fma_end(arena, span.lease, 1) == FMA_OK, FMA_INTERNAL_ERROR);

        ComPtr<ID3D12Fence> gate;
        checked(current->device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&gate)));
        // Release the owned queue even if a test assertion throws.
        struct Unblock {
            ID3D12Fence *gate;
            ~Unblock() { gate->Signal(1); }
        } unblock{gate.Get()};
        checked(current->queue->Wait(gate.Get(), 1));
        require(fma_transfer(arena, buffer, FMA_UPLOAD) == FMA_TIMEOUT, FMA_INTERNAL_ERROR);
        require(fma_destroy(arena) == FMA_BUSY, FMA_INTERNAL_ERROR);
        require(fma_release(arena, buffer) == FMA_BUSY, FMA_INTERNAL_ERROR);
        require(fma_transfer(arena, buffer, FMA_UPLOAD) == FMA_CLOSED, FMA_INTERNAL_ERROR);
        require(current->metrics.committed_bytes != 0 && current->metrics.timeouts == 1, FMA_INTERNAL_ERROR);
        checked(gate->Signal(1));
        require(WaitForSingleObject(current->event.get(), 5000) == WAIT_OBJECT_0, FMA_TIMEOUT);
        require(fma_transfer(arena, buffer, FMA_UPLOAD) == FMA_CLOSED, FMA_INTERNAL_ERROR);
        require(fma_destroy(arena) == FMA_OK, FMA_INTERNAL_ERROR);
        require(fma_create(&options, &arena) == FMA_OK, FMA_INTERNAL_ERROR);
        opaque_counter = UINT64_MAX;
        require(fma_allocate(arena, 4096, &buffer) == FMA_LIMIT && buffer == 0, FMA_INTERNAL_ERROR);
        require(current->metrics.closed && current->metrics.committed_bytes == 0, FMA_INTERNAL_ERROR);
        require(fma_destroy(arena) == FMA_OK, FMA_INTERNAL_ERROR);
        require(fma_create(&options, &arena) == FMA_LIMIT && arena == 0, FMA_INTERNAL_ERROR);
        std::puts("Arena GPU timeout: retained backing, no revival, drained cleanup passed");
        return 0;
    } catch (...) {
        std::fputs("Arena GPU timeout regression failed\n", stderr);
        // A failed test never unwinds global resources with unproven GPU progress.
        std::_Exit(1);
    }
}
