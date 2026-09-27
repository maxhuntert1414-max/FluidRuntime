#include "fluid_memory_arena.h"
#include "memory_arena_windows.h"
#include "memory_coherence.h"
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <algorithm>
#include <array>
#include <cstring>
#include <memory>
#include <mutex>
#include <vector>

namespace {
using namespace fluid::memory;
using Microsoft::WRL::ComPtr;
using windows::Handle;
static_assert(sizeof(void *) == 8, "The arena ABI targets Windows x64");
static_assert(sizeof(fma_options) == 24 && sizeof(fma_span) == 24 && sizeof(fma_peer) == 56);
static_assert(sizeof(fma_reader_hello) == 16 && sizeof(fma_shared_read) == 64 && sizeof(fma_metrics) == 128);

struct Failure {
    fma_status status;
};
void require(bool valid, fma_status status) {
    if (!valid) {
        throw Failure{status};
    }
}
void checked(HRESULT result) { require(SUCCEEDED(result), FMA_DEVICE_ERROR); }
void add(std::uint64_t &counter, std::uint64_t value) {
    require(value <= UINT64_MAX - counter, FMA_LIMIT);
    counter += value;
}
fma_status status(Status value) {
    switch (value) {
    case Status::ok:
        return FMA_OK;
    case Status::busy:
        return FMA_BUSY;
    case Status::host_stale:
    case Status::device_stale:
        return FMA_STALE;
    case Status::closed:
        return FMA_CLOSED;
    case Status::capacity:
    case Status::exhausted:
        return FMA_LIMIT;
    default:
        return FMA_INVALID_ARGUMENT;
    }
}
std::uint64_t opaque_counter{};
std::uint64_t token() {
    if (!opaque_counter) {
        require(BCryptGenRandom(nullptr, reinterpret_cast<PUCHAR>(&opaque_counter), sizeof(opaque_counter),
                                BCRYPT_USE_SYSTEM_PREFERRED_RNG) >= 0,
                FMA_INTERNAL_ERROR);
        opaque_counter &= UINT64_MAX >> 2;
    }
    require(opaque_counter != UINT64_MAX, FMA_LIMIT);
    return ++opaque_counter;
}

struct Allocation {
    fma_buffer id{};
    std::uint64_t bytes{}, charge{};
    Buffer logical{};
    Handle section, peer, image;
    ComPtr<ID3D12Resource> upload, device, readback;
    bool source{};
    Ticket ticket{};
    fma_lease lease{};
    std::uint64_t deadline{};
    void *view{};
    ~Allocation() {
        if (view) {
            UnmapViewOfFile(view);
        }
    }
};

ComPtr<IDXGIAdapter1> choose_adapter(IDXGIFactory6 *factory, bool hardware) {
    ComPtr<IDXGIAdapter1> adapter;
    if (!hardware) {
        checked(factory->EnumWarpAdapter(IID_PPV_ARGS(&adapter)));
        return adapter;
    }
    for (UINT index = 0;; ++index) {
        ComPtr<IDXGIAdapter1> candidate;
        const auto result = factory->EnumAdapterByGpuPreference(index, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE,
                                                                IID_PPV_ARGS(&candidate));
        if (result == DXGI_ERROR_NOT_FOUND) {
            break;
        }
        checked(result);
        DXGI_ADAPTER_DESC1 description{};
        checked(candidate->GetDesc1(&description));
        if (!(description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) &&
            SUCCEEDED(D3D12CreateDevice(candidate.Get(), D3D_FEATURE_LEVEL_11_0, __uuidof(ID3D12Device),
                                        nullptr))) {
            return candidate;
        }
    }
    throw Failure{FMA_DEVICE_ERROR};
}

struct Arena {
    fma_arena id;
    Coherence<> coherence;
    fma_metrics metrics{};
    DWORD timeout;
    std::array<std::unique_ptr<Allocation>, FMA_MAX_BUFFERS> buffers{};
    ComPtr<ID3D12Device> device;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12CommandAllocator> allocator;
    ComPtr<ID3D12GraphicsCommandList> list;
    ComPtr<ID3D12Fence> fence;
    ComPtr<ID3D12InfoQueue> debug;
    Handle event;
    std::uint64_t sequence{}, in_flight{};
    bool signaled{};

    Arena(fma_arena identity, const fma_options &options)
        : id(identity), coherence(identity, 1), timeout(options.timeout_ms) {
        metrics.size = sizeof(metrics);
        metrics.abi_version = FMA_ABI_VERSION;
        metrics.hardware = options.adapter;
        metrics.budget_bytes = options.budget_bytes;
        ComPtr<IDXGIFactory6> factory;
        checked(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)));
        auto adapter = choose_adapter(factory.Get(), options.adapter == FMA_HARDWARE);
        checked(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device)));
        D3D12_FEATURE_DATA_ARCHITECTURE1 architecture{};
        checked(
            device->CheckFeatureSupport(D3D12_FEATURE_ARCHITECTURE1, &architecture, sizeof(architecture)));
        metrics.uma = architecture.UMA != FALSE;
        metrics.cache_coherent_uma = architecture.CacheCoherentUMA != FALSE;
        // Never enable the process-wide debug layer from a library: the host may
        // already have live D3D12 devices. Owned tests enable it before creating us.
        if (SUCCEEDED(device.As(&debug))) {
            metrics.debug_layer_enabled = 1;
            checked(debug->SetMessageCountLimit(128));
        }
        D3D12_COMMAND_QUEUE_DESC desc{};
        desc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        checked(device->CreateCommandQueue(&desc, IID_PPV_ARGS(&queue)));
        checked(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator)));
        checked(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.Get(), nullptr,
                                          IID_PPV_ARGS(&list)));
        checked(list->Close());
        checked(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence)));
        event = Handle(CreateEventW(nullptr, FALSE, FALSE, nullptr));
        require(event.get() != nullptr, FMA_OUT_OF_MEMORY);
    }
    void close() {
        metrics.closed = 1;
        coherence.close();
    }
    void health() {
        for (const auto &entry : buffers) {
            if (!entry || !entry->lease) {
                continue;
            }
            if (!metrics.closed && GetTickCount64() >= entry->deadline) {
                add(metrics.timeouts, 1);
                close();
            }
            if (entry->peer.get() && WaitForSingleObject(entry->peer.get(), 0) == WAIT_OBJECT_0) {
                DWORD code{};
                if (!GetExitCodeProcess(entry->peer.get(), &code) || code != 0) {
                    close();
                }
            }
        }
    }
    Allocation *find(fma_buffer value) {
        for (auto &entry : buffers) {
            if (entry && entry->id == value) {
                return entry.get();
            }
        }
        return nullptr;
    }
    Allocation *find_lease(fma_lease value) {
        if (!value) {
            return nullptr;
        }
        for (auto &entry : buffers) {
            if (entry && entry->lease == value) {
                return entry.get();
            }
        }
        return nullptr;
    }
    bool drained() {
        if (!in_flight) {
            return true;
        }
        const auto completed = fence->GetCompletedValue();
        if (completed == UINT64_MAX || FAILED(device->GetDeviceRemovedReason()) ||
            (signaled && completed >= in_flight)) {
            in_flight = 0;
            return true;
        }
        return false;
    }
    fma_status allocate(std::uint64_t bytes, fma_buffer &output) {
        if (!bytes || bytes > FMA_MAX_BYTES) {
            return FMA_INVALID_ARGUMENT;
        }
        auto slot = std::find_if(buffers.begin(), buffers.end(), [](const auto &entry) { return !entry; });
        if (slot == buffers.end()) {
            return FMA_LIMIT;
        }
        D3D12_RESOURCE_DESC desc{};
        desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        desc.Width = bytes;
        desc.Height = desc.DepthOrArraySize = desc.MipLevels = 1;
        desc.SampleDesc.Count = 1;
        desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        const auto info = device->GetResourceAllocationInfo(0, 1, &desc);
        require(info.SizeInBytes && info.SizeInBytes != UINT64_MAX && info.SizeInBytes <= FMA_MAX_BYTES,
                FMA_LIMIT);
        SYSTEM_INFO system{};
        GetSystemInfo(&system);
        const auto host_charge = ((bytes + system.dwPageSize - 1) / system.dwPageSize) * system.dwPageSize;
        const auto charge = host_charge + 3 * info.SizeInBytes;
        if (charge > metrics.budget_bytes - metrics.committed_bytes) {
            return FMA_LIMIT;
        }
        auto entry = std::make_unique<Allocation>();
        entry->id = token();
        entry->bytes = bytes;
        entry->charge = charge;
        entry->section = Handle(CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
                                                   static_cast<DWORD>(bytes), nullptr));
        require(entry->section.get() != nullptr, FMA_OUT_OF_MEMORY);
        auto resource = [&](D3D12_HEAP_TYPE type, ComPtr<ID3D12Resource> &result) {
            D3D12_HEAP_PROPERTIES heap{};
            heap.Type = type;
            heap.CreationNodeMask = heap.VisibleNodeMask = 1;
            checked(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc,
                                                    type == D3D12_HEAP_TYPE_UPLOAD
                                                        ? D3D12_RESOURCE_STATE_GENERIC_READ
                                                        : D3D12_RESOURCE_STATE_COPY_DEST,
                                                    nullptr, IID_PPV_ARGS(&result)));
        };
        resource(D3D12_HEAP_TYPE_UPLOAD, entry->upload);
        resource(D3D12_HEAP_TYPE_DEFAULT, entry->device);
        resource(D3D12_HEAP_TYPE_READBACK, entry->readback);
        require(coherence.allocate(bytes, entry->logical) == Status::ok, FMA_INTERNAL_ERROR);
        add(metrics.backing_allocations, 4);
        metrics.committed_bytes += charge;
        metrics.peak_bytes = (std::max)(metrics.peak_bytes, metrics.committed_bytes);
        ++metrics.active_buffers;
        output = entry->id;
        *slot = std::move(entry);
        return FMA_OK;
    }
    void barrier(Allocation &entry, bool source) {
        if (entry.source == source) {
            return;
        }
        D3D12_RESOURCE_BARRIER barrier{};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barrier.Transition.pResource = entry.device.Get();
        barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        barrier.Transition.StateBefore =
            entry.source ? D3D12_RESOURCE_STATE_COPY_SOURCE : D3D12_RESOURCE_STATE_COPY_DEST;
        barrier.Transition.StateAfter =
            source ? D3D12_RESOURCE_STATE_COPY_SOURCE : D3D12_RESOURCE_STATE_COPY_DEST;
        list->ResourceBarrier(1, &barrier);
        entry.source = source;
    }
    fma_status transfer(Allocation &entry, bool readback) {
        require(sequence < UINT64_MAX - 1, FMA_LIMIT);
        const auto admission = coherence.begin({entry.logical, 0, entry.bytes},
                                               readback ? Access::readback : Access::upload, {1, ++sequence});
        if (admission.status != Status::ok) {
            return status(admission.status);
        }
        // The host mapping and staging remain owned and immutable through GPU completion.
        void *host = MapViewOfFile(entry.section.get(), readback ? FILE_MAP_WRITE : FILE_MAP_READ, 0, 0,
                                   static_cast<SIZE_T>(entry.bytes));
        if (!host) {
            throw Failure{FMA_OUT_OF_MEMORY};
        }
        struct Unmap {
            void *view;
            ~Unmap() { UnmapViewOfFile(view); }
        } unmap{host};
        const D3D12_RANGE none{0, 0}, whole{0, static_cast<SIZE_T>(entry.bytes)};
        if (!readback) {
            void *mapped{};
            checked(entry.upload->Map(0, &none, &mapped));
            std::memcpy(mapped, host, static_cast<SIZE_T>(entry.bytes));
            entry.upload->Unmap(0, &whole);
            add(metrics.cpu_copy_bytes, entry.bytes);
        }
        checked(allocator->Reset());
        checked(list->Reset(allocator.Get(), nullptr));
        barrier(entry, readback);
        if (readback) {
            list->CopyBufferRegion(entry.readback.Get(), 0, entry.device.Get(), 0, entry.bytes);
        } else {
            list->CopyBufferRegion(entry.device.Get(), 0, entry.upload.Get(), 0, entry.bytes);
        }
        checked(list->Close());
        add(metrics.gpu_copy_bytes, entry.bytes);
        ID3D12CommandList *commands[]{list.Get()};
        in_flight = sequence;
        signaled = false;
        queue->ExecuteCommandLists(1, commands);
        checked(queue->Signal(fence.Get(), sequence));
        signaled = true;
        checked(fence->SetEventOnCompletion(sequence, event.get()));
        const auto wait = WaitForSingleObject(event.get(), timeout);
        if (wait == WAIT_TIMEOUT) {
            add(metrics.timeouts, 1);
            close();
            return FMA_TIMEOUT;
        }
        require(wait == WAIT_OBJECT_0 && fence->GetCompletedValue() != UINT64_MAX &&
                    fence->GetCompletedValue() >= sequence,
                FMA_DEVICE_ERROR);
        in_flight = 0;
        if (readback) {
            void *mapped{};
            checked(entry.readback->Map(0, &whole, &mapped));
            std::memcpy(host, mapped, static_cast<SIZE_T>(entry.bytes));
            entry.readback->Unmap(0, &none);
            add(metrics.cpu_copy_bytes, entry.bytes);
        }
        require(coherence.finish(admission.ticket, {1, sequence}) == Status::ok, FMA_INTERNAL_ERROR);
        add(metrics.transfers, 1);
        return FMA_OK;
    }
    void debug_metrics() {
        if (!debug) {
            return;
        }
        require(debug->GetNumMessagesDiscardedByMessageCountLimit() == 0, FMA_DEVICE_ERROR);
        for (UINT64 index = 0; index < debug->GetNumStoredMessages(); ++index) {
            SIZE_T size{};
            checked(debug->GetMessage(index, nullptr, &size));
            require(size && size <= 1024 * 1024, FMA_INTERNAL_ERROR);
            std::vector<unsigned char> storage(size);
            auto *message = reinterpret_cast<D3D12_MESSAGE *>(storage.data());
            checked(debug->GetMessage(index, message, &size));
            if (message->Severity <= D3D12_MESSAGE_SEVERITY_ERROR) {
                add(metrics.debug_errors, 1);
            }
        }
        debug->ClearStoredMessages();
        if (metrics.debug_errors) {
            close();
        }
    }
};

// One process-wide registry bounds both backing and token/lifetime bookkeeping.
std::mutex registry_mutex;
std::unique_ptr<Arena> current;
template <class Function> fma_status call(fma_arena handle, bool allow_closed, Function &&function) noexcept {
    try {
        std::unique_lock lock(registry_mutex, std::try_to_lock);
        if (!lock) {
            return FMA_BUSY;
        }
        if (!current || current->id != handle) {
            return FMA_INVALID_HANDLE;
        }
        auto &arena = *current;
        try {
            arena.health();
            if (arena.metrics.closed && !allow_closed) {
                return FMA_CLOSED;
            }
            return function(arena);
        } catch (const Failure &error) {
            arena.close();
            if (error.status == FMA_DEVICE_ERROR) {
                add(arena.metrics.device_errors, 1);
            }
            return error.status;
        } catch (const std::bad_alloc &) {
            arena.close();
            return FMA_OUT_OF_MEMORY;
        } catch (...) {
            arena.close();
            return FMA_INTERNAL_ERROR;
        }
    } catch (...) {
        return FMA_INTERNAL_ERROR;
    }
}
} // namespace

fma_status FMA_CALL fma_create(const fma_options *options, fma_arena *output) noexcept {
    if (!output) {
        return FMA_INVALID_ARGUMENT;
    }
    if (!options) {
        *output = 0;
        return FMA_INVALID_ARGUMENT;
    }
    const auto config = *options;
    *output = 0;
    if (config.size != sizeof(config) || config.abi_version != FMA_ABI_VERSION) {
        return FMA_ABI_MISMATCH;
    }
    if (config.adapter > FMA_HARDWARE || !config.budget_bytes || config.budget_bytes > FMA_MAX_BYTES ||
        config.timeout_ms < 100 || config.timeout_ms > 10000) {
        return FMA_INVALID_ARGUMENT;
    }
    try {
        std::unique_lock lock(registry_mutex, std::try_to_lock);
        if (!lock) {
            return FMA_BUSY;
        }
        if (current) {
            return FMA_LIMIT;
        }
        auto arena = std::make_unique<Arena>(token(), config);
        *output = arena->id;
        current = std::move(arena);
        return FMA_OK;
    } catch (const Failure &error) {
        return error.status;
    } catch (const std::bad_alloc &) {
        return FMA_OUT_OF_MEMORY;
    } catch (...) {
        return FMA_INTERNAL_ERROR;
    }
}
fma_status FMA_CALL fma_close(fma_arena handle) noexcept {
    return call(handle, true, [](Arena &arena) -> fma_status {
        arena.close();
        return FMA_OK;
    });
}
fma_status FMA_CALL fma_destroy(fma_arena handle) noexcept {
    return call(handle, true, [](Arena &arena) -> fma_status {
        if (arena.metrics.active_leases || !arena.drained()) {
            return FMA_BUSY;
        }
        current.reset();
        return FMA_OK;
    });
}
fma_status FMA_CALL fma_allocate(fma_arena handle, uint64_t bytes, fma_buffer *output) noexcept {
    if (!output) {
        return FMA_INVALID_ARGUMENT;
    }
    *output = 0;
    return call(handle, false, [&](Arena &arena) -> fma_status { return arena.allocate(bytes, *output); });
}
fma_status FMA_CALL fma_release(fma_arena handle, fma_buffer buffer) noexcept {
    return call(handle, true, [&](Arena &arena) -> fma_status {
        auto *entry = arena.find(buffer);
        if (!entry) {
            return FMA_INVALID_HANDLE;
        }
        if (entry->lease || !arena.drained()) {
            return FMA_BUSY;
        }
        if (!arena.metrics.closed) {
            require(arena.coherence.release(entry->logical) == Status::ok, FMA_INTERNAL_ERROR);
        }
        arena.metrics.committed_bytes -= entry->charge;
        --arena.metrics.active_buffers;
        for (auto &slot : arena.buffers) {
            if (slot.get() == entry) {
                slot.reset();
                break;
            }
        }
        return FMA_OK;
    });
}
fma_status FMA_CALL fma_acquire(fma_arena handle, fma_buffer buffer, uint64_t offset, uint64_t bytes,
                                uint32_t access, fma_span *output) noexcept {
    if (!output) {
        return FMA_INVALID_ARGUMENT;
    }
    *output = {};
    if (access != FMA_HOST_READ && access != FMA_HOST_WRITE) {
        return FMA_INVALID_ARGUMENT;
    }
    return call(handle, false, [&](Arena &arena) -> fma_status {
        auto *entry = arena.find(buffer);
        if (!entry) {
            return FMA_INVALID_HANDLE;
        }
        if (!bytes || offset > entry->bytes || bytes > entry->bytes - offset) {
            arena.close();
            return FMA_INVALID_ARGUMENT;
        }
        const auto admission =
            arena.coherence.begin({entry->logical, offset, bytes},
                                  access == FMA_HOST_READ ? Access::host_read : Access::host_write);
        if (admission.status != Status::ok) {
            return status(admission.status);
        }
        const auto lease = token();
        entry->view =
            MapViewOfFile(entry->section.get(), access == FMA_HOST_READ ? FILE_MAP_READ : FILE_MAP_WRITE, 0,
                          0, static_cast<SIZE_T>(entry->bytes));
        require(entry->view != nullptr, FMA_OUT_OF_MEMORY);
        entry->ticket = admission.ticket;
        entry->lease = lease;
        entry->deadline = GetTickCount64() + arena.timeout;
        ++arena.metrics.active_leases;
        *output = {lease, static_cast<unsigned char *>(entry->view) + offset, bytes};
        return FMA_OK;
    });
}
fma_status FMA_CALL fma_end(fma_arena handle, fma_lease lease, uint32_t publish) noexcept {
    if (publish > 1) {
        return FMA_INVALID_ARGUMENT;
    }
    return call(handle, true, [&](Arena &arena) -> fma_status {
        auto *entry = arena.find_lease(lease);
        if (!entry || !entry->view || entry->peer.get()) {
            return FMA_INVALID_HANDLE;
        }
        UnmapViewOfFile(entry->view);
        entry->view = nullptr;
        entry->lease = 0;
        --arena.metrics.active_leases;
        if (arena.metrics.closed) {
            return FMA_CLOSED;
        }
        return status(publish ? arena.coherence.finish(entry->ticket) : arena.coherence.abort(entry->ticket));
    });
}
fma_status FMA_CALL fma_transfer(fma_arena handle, fma_buffer buffer, uint32_t direction) noexcept {
    if (direction != FMA_UPLOAD && direction != FMA_READBACK) {
        return FMA_INVALID_ARGUMENT;
    }
    return call(handle, false, [&](Arena &arena) -> fma_status {
        auto *entry = arena.find(buffer);
        return entry ? arena.transfer(*entry, direction == FMA_READBACK) : FMA_INVALID_HANDLE;
    });
}
fma_status FMA_CALL fma_export_read(fma_arena handle, fma_buffer buffer, const fma_peer *peer,
                                    const fma_reader_hello *hello, fma_shared_read *output) noexcept {
    if (!output) {
        return FMA_INVALID_ARGUMENT;
    }
    if (!peer || !hello) {
        *output = {};
        return FMA_INVALID_ARGUMENT;
    }
    const auto identity = *peer;
    const auto negotiation = *hello;
    *output = {};
    if (negotiation.magic != FMA_LOCAL_MAGIC || negotiation.version != FMA_ABI_VERSION ||
        negotiation.opcode != 0 || negotiation.capabilities != FMA_READ_CAPABILITY) {
        return FMA_ABI_MISMATCH;
    }
    if (!identity.process_handle || identity.process_handle >= UINTPTR_MAX || !identity.pid ||
        identity.reserved) {
        return FMA_INVALID_ARGUMENT;
    }
    return call(handle, false, [&](Arena &arena) -> fma_status {
        auto *entry = arena.find(buffer);
        if (!entry) {
            return FMA_INVALID_HANDLE;
        }
        if (entry->lease) {
            return FMA_BUSY;
        }
        HANDLE duplicate{};
        if (!DuplicateHandle(
                GetCurrentProcess(), reinterpret_cast<HANDLE>(identity.process_handle), GetCurrentProcess(),
                &duplicate, PROCESS_DUP_HANDLE | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, 0)) {
            return FMA_IDENTITY_MISMATCH;
        }
        Handle process(duplicate);
        if (GetProcessId(process.get()) != identity.pid || identity.pid == GetCurrentProcessId() ||
            WaitForSingleObject(process.get(), 0) != WAIT_TIMEOUT ||
            windows::creation_time(process.get()) != identity.creation_time) {
            return FMA_IDENTITY_MISMATCH;
        }
        Handle image(CreateFileW(windows::image_path(process.get()).c_str(), GENERIC_READ, FILE_SHARE_READ,
                                 nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
        if (image.get() == INVALID_HANDLE_VALUE) {
            return FMA_IDENTITY_MISMATCH;
        }
        const auto digest = windows::file_hash(image.get());
        if (std::memcmp(digest.data(), identity.image_sha256, digest.size()) != 0) {
            return FMA_IDENTITY_MISMATCH;
        }
        const auto admission = arena.coherence.begin({entry->logical, 0, entry->bytes}, Access::host_read);
        if (admission.status != Status::ok) {
            return status(admission.status);
        }
        const auto lease = token();
        const auto version = arena.coherence.inspect({entry->logical, 0, entry->bytes}).version;
        HANDLE remote{};
        if (!DuplicateHandle(GetCurrentProcess(), entry->section.get(), process.get(), &remote, FILE_MAP_READ,
                             FALSE, 0)) {
            arena.coherence.finish(admission.ticket);
            return FMA_IDENTITY_MISMATCH;
        }
        entry->ticket = admission.ticket;
        entry->lease = lease;
        entry->deadline = GetTickCount64() + arena.timeout;
        entry->peer = std::move(process);
        entry->image = std::move(image);
        ++arena.metrics.active_leases;
        *output = {FMA_LOCAL_MAGIC, FMA_ABI_VERSION, 1,     sizeof(*output),
                   arena.id,        buffer,          lease, reinterpret_cast<std::uintptr_t>(remote),
                   entry->bytes,    version};
        return FMA_OK;
    });
}
fma_status FMA_CALL fma_end_read(fma_arena handle, fma_lease lease) noexcept {
    return call(handle, true, [&](Arena &arena) -> fma_status {
        auto *entry = arena.find_lease(lease);
        if (!entry || !entry->peer.get()) {
            return FMA_INVALID_HANDLE;
        }
        if (WaitForSingleObject(entry->peer.get(), 0) != WAIT_OBJECT_0) {
            return FMA_BUSY;
        }
        entry->peer.reset();
        entry->image.reset();
        entry->lease = 0;
        --arena.metrics.active_leases;
        if (arena.metrics.closed) {
            return FMA_CLOSED;
        }
        return status(arena.coherence.finish(entry->ticket));
    });
}
fma_status FMA_CALL fma_get_metrics(fma_arena handle, fma_metrics *output, uint32_t size) noexcept {
    if (!output || size != sizeof(*output)) {
        return FMA_INVALID_ARGUMENT;
    }
    *output = {};
    return call(handle, true, [&](Arena &arena) -> fma_status {
        arena.debug_metrics();
        *output = arena.metrics;
        return FMA_OK;
    });
}
