#include "memory_coherence.h"
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <cerrno>
#include <cstdlib>
#include <cstring>
#include <iomanip>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace {
using Microsoft::WRL::ComPtr;
using namespace fluid::memory;
constexpr std::size_t bytes = 16 * 1024;
constexpr std::uint64_t queue_id = 1;

void require(bool condition, const char *message) {
    if (!condition)
        throw std::runtime_error(message);
}
void check(HRESULT result, const char *operation) {
    if (FAILED(result)) {
        std::ostringstream message;
        message << operation << " failed: 0x" << std::hex << static_cast<unsigned long>(result);
        throw std::runtime_error(message.str());
    }
}
class Handle {
    HANDLE handle_{};

  public:
    explicit Handle(HANDLE handle) : handle_(handle) {}
    ~Handle() {
        if (handle_)
            CloseHandle(handle_);
    }
    Handle(const Handle &) = delete;
    Handle &operator=(const Handle &) = delete;
    HANDLE get() const { return handle_; }
};
class MappedView {
    void *data_{};

  public:
    MappedView(HANDLE handle, DWORD access) : data_(MapViewOfFile(handle, access, 0, 0, bytes)) {
        require(data_ != nullptr, "MapViewOfFile failed");
    }
    ~MappedView() { UnmapViewOfFile(data_); }
    MappedView(const MappedView &) = delete;
    MappedView &operator=(const MappedView &) = delete;
    unsigned char *data() const { return static_cast<unsigned char *>(data_); }
};
class HandleList {
    std::vector<unsigned char> storage_;

  public:
    explicit HandleList(HANDLE *handle) {
        SIZE_T size{};
        InitializeProcThreadAttributeList(nullptr, 1, 0, &size);
        require(size != 0 && size <= 65536, "Invalid process attribute size");
        storage_.resize(size);
        require(InitializeProcThreadAttributeList(get(), 1, 0, &size) != FALSE,
                "InitializeProcThreadAttributeList failed");
        if (!UpdateProcThreadAttribute(get(), 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handle, sizeof(HANDLE),
                                       nullptr, nullptr)) {
            DeleteProcThreadAttributeList(get());
            throw std::runtime_error("Handle allowlist failed");
        }
    }
    ~HandleList() { DeleteProcThreadAttributeList(get()); }
    HandleList(const HandleList &) = delete;
    HandleList &operator=(const HandleList &) = delete;
    LPPROC_THREAD_ATTRIBUTE_LIST get() {
        return reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage_.data());
    }
};

unsigned char pattern(std::size_t index, unsigned seed) {
    return static_cast<unsigned char>((index * 131 + (index >> 7) + seed) & 255);
}
void fill(unsigned char *data, unsigned seed) {
    for (std::size_t i = 0; i < bytes; ++i)
        data[i] = pattern(i, seed);
}
int mapping_child(const wchar_t *argument) {
    wchar_t *end{};
    errno = 0;
    const auto value = wcstoull(argument, &end, 10);
    if (*argument < L'0' || *argument > L'9' || *end || !value || value >= UINTPTR_MAX || errno)
        return 2;
    Handle mapping(reinterpret_cast<HANDLE>(static_cast<std::uintptr_t>(value)));
    auto *writable = MapViewOfFile(mapping.get(), FILE_MAP_WRITE, 0, 0, bytes);
    if (writable) {
        UnmapViewOfFile(writable);
        return 3;
    }
    require(GetLastError() == ERROR_ACCESS_DENIED, "Child did not receive a read-only mapping handle");
    MappedView view(mapping.get(), FILE_MAP_READ);
    for (std::size_t i = 0; i < bytes; ++i)
        require(view.data()[i] == pattern(i, 3), "Cross-process payload mismatch");
    return 0;
}
void prove_mapping(HANDLE mapping) {
    HANDLE duplicate{};
    require(DuplicateHandle(GetCurrentProcess(), mapping, GetCurrentProcess(), &duplicate, FILE_MAP_READ,
                            TRUE, 0) != FALSE,
            "Duplicate read-only mapping failed");
    Handle child_mapping(duplicate);
    HandleList handles(&duplicate);
    std::wstring executable(32768, L'\0');
    const auto length = GetModuleFileNameW(nullptr, executable.data(), static_cast<DWORD>(executable.size()));
    require(length && length < executable.size(), "Cannot locate owned child executable");
    executable.resize(length);
    auto command = L"\"" + executable + L"\" --mapping-child " +
                   std::to_wstring(reinterpret_cast<std::uintptr_t>(duplicate));
    STARTUPINFOEXW startup{};
    startup.StartupInfo.cb = sizeof(startup);
    startup.lpAttributeList = handles.get();
    PROCESS_INFORMATION information{};
    require(CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, TRUE,
                           CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT, nullptr, nullptr,
                           &startup.StartupInfo, &information) != FALSE,
            "Owned mapping child launch failed");
    Handle child(information.hProcess), thread(information.hThread);
    if (WaitForSingleObject(child.get(), 15000) != WAIT_OBJECT_0) {
        TerminateProcess(child.get(), 4);
        WaitForSingleObject(child.get(), 5000);
        throw std::runtime_error("Owned mapping child exceeded deadline");
    }
    DWORD result{};
    require(GetExitCodeProcess(child.get(), &result) && result == 0, "Cross-process mapping check failed");
}

std::string json_string(const wchar_t *text) {
    const auto length = WideCharToMultiByte(CP_UTF8, 0, text, -1, nullptr, 0, nullptr, nullptr);
    require(length > 0, "Adapter description encoding failed");
    std::string value(static_cast<std::size_t>(length), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text, -1, value.data(), length, nullptr, nullptr);
    value.pop_back();
    std::ostringstream output;
    output << '"';
    for (const auto character : value) {
        const auto c = static_cast<unsigned char>(character);
        if (c == '"' || c == '\\')
            output << '\\' << character;
        else if (c < 32)
            output << "\\u" << std::hex << std::setw(4) << std::setfill('0') << unsigned(c);
        else
            output << character;
    }
    output << '"';
    return output.str();
}
ComPtr<IDXGIAdapter1> select_adapter(IDXGIFactory6 *factory, bool hardware) {
    ComPtr<IDXGIAdapter1> adapter;
    if (!hardware) {
        check(factory->EnumWarpAdapter(IID_PPV_ARGS(&adapter)), "EnumWarpAdapter");
        return adapter;
    }
    for (UINT i = 0;; ++i) {
        ComPtr<IDXGIAdapter1> candidate;
        const auto result = factory->EnumAdapterByGpuPreference(i, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE,
                                                                IID_PPV_ARGS(&candidate));
        if (result == DXGI_ERROR_NOT_FOUND)
            break;
        check(result, "EnumAdapterByGpuPreference");
        DXGI_ADAPTER_DESC1 description{};
        check(candidate->GetDesc1(&description), "GetDesc1");
        if (!(description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) &&
            SUCCEEDED(
                D3D12CreateDevice(candidate.Get(), D3D_FEATURE_LEVEL_11_0, __uuidof(ID3D12Device), nullptr)))
            return candidate;
    }
    throw std::runtime_error("No D3D12 hardware adapter; no silent WARP fallback");
}
ComPtr<ID3D12Resource> buffer(ID3D12Device *device, D3D12_HEAP_TYPE type) {
    D3D12_HEAP_PROPERTIES heap{};
    heap.Type = type;
    heap.CreationNodeMask = heap.VisibleNodeMask = 1;
    D3D12_RESOURCE_DESC description{};
    description.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    description.Width = bytes;
    description.Height = description.DepthOrArraySize = description.MipLevels = 1;
    description.SampleDesc.Count = 1;
    description.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    ComPtr<ID3D12Resource> result;
    check(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &description,
                                          type == D3D12_HEAP_TYPE_UPLOAD ? D3D12_RESOURCE_STATE_GENERIC_READ
                                                                         : D3D12_RESOURCE_STATE_COPY_DEST,
                                          nullptr, IID_PPV_ARGS(&result)),
          "CreateCommittedResource");
    return result;
}
void transition(ID3D12GraphicsCommandList *list, ID3D12Resource *resource, D3D12_RESOURCE_STATES before,
                D3D12_RESOURCE_STATES after) {
    D3D12_RESOURCE_BARRIER barrier{};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition = {resource, D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, before, after};
    list->ResourceBarrier(1, &barrier);
}

int run(bool hardware) {
    ComPtr<ID3D12Debug> debug;
    const bool debug_enabled = SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&debug)));
    if (debug_enabled)
        debug->EnableDebugLayer();
    ComPtr<IDXGIFactory6> factory;
    check(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)), "CreateDXGIFactory2");
    const auto adapter = select_adapter(factory.Get(), hardware);
    DXGI_ADAPTER_DESC1 adapter_info{};
    check(adapter->GetDesc1(&adapter_info), "GetDesc1");
    ComPtr<ID3D12Device> device;
    check(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device)),
          "D3D12CreateDevice");
    D3D12_FEATURE_DATA_ARCHITECTURE1 architecture{};
    check(device->CheckFeatureSupport(D3D12_FEATURE_ARCHITECTURE1, &architecture, sizeof(architecture)),
          "CheckFeatureSupport(ARCHITECTURE1)");
    bool gpu_upload_known = false, gpu_upload = false;
#if defined(__ID3D12Device10_INTERFACE_DEFINED__)
    D3D12_FEATURE_DATA_D3D12_OPTIONS16 options16{};
    gpu_upload_known =
        SUCCEEDED(device->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS16, &options16, sizeof(options16)));
    gpu_upload = gpu_upload_known && options16.GPUUploadHeapSupported;
#endif
    ComPtr<IDXGIAdapter3> budget_adapter;
    DXGI_QUERY_VIDEO_MEMORY_INFO local{}, nonlocal{};
    const bool has_budgets =
        SUCCEEDED(adapter.As(&budget_adapter)) &&
        SUCCEEDED(budget_adapter->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &local)) &&
        SUCCEEDED(budget_adapter->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL, &nonlocal));
    ComPtr<ID3D12InfoQueue> debug_queue;
    if (debug_enabled)
        check(device.As(&debug_queue), "ID3D12InfoQueue");

    // The CPU arena is an unnamed OS section, not a GPU heap or authority mailbox.
    Handle mapping(CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
                                      static_cast<DWORD>(bytes), nullptr));
    require(mapping.get() != nullptr, "CreateFileMapping failed");
    MappedView host(mapping.get(), FILE_MAP_READ | FILE_MAP_WRITE);
    Coherence<> coherence(1, queue_id); // Owned one-shot lab; production broker supplies a fresh identity.
    Buffer logical{};
    require(coherence.allocate(bytes, logical) == Status::ok, "Logical allocation failed");
    const View full{logical, 0, bytes}, alias{logical, 256, 64};
    std::vector<unsigned char> expected(bytes);
    auto host_write = coherence.begin(full, Access::host_write);
    require(host_write.status == Status::ok, "Host write admission failed");
    fill(host.data(), 3);
    fill(expected.data(), 3);
    require(coherence.finish(host_write.ticket) == Status::ok, "Host publish failed");
    const auto shared_read = coherence.begin(full, Access::host_read);
    require(shared_read.status == Status::ok, "Shared reader admission failed");
    prove_mapping(mapping.get());
    require(coherence.finish(shared_read.ticket) == Status::ok, "Shared reader release failed");

    const auto upload = buffer(device.Get(), D3D12_HEAP_TYPE_UPLOAD);
    const auto gpu = buffer(device.Get(), D3D12_HEAP_TYPE_DEFAULT);
    const auto readback = buffer(device.Get(), D3D12_HEAP_TYPE_READBACK);
    ComPtr<ID3D12CommandQueue> queue;
    D3D12_COMMAND_QUEUE_DESC queue_desc{};
    queue_desc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    check(device->CreateCommandQueue(&queue_desc, IID_PPV_ARGS(&queue)), "CreateCommandQueue");
    ComPtr<ID3D12CommandAllocator> allocator;
    check(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator)),
          "CreateCommandAllocator");
    ComPtr<ID3D12GraphicsCommandList> list;
    check(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.Get(), nullptr,
                                    IID_PPV_ARGS(&list)),
          "CreateCommandList");
    check(list->Close(), "Close initial list");
    ComPtr<ID3D12Fence> fence;
    check(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence)), "CreateFence");
    Handle event(CreateEventW(nullptr, FALSE, FALSE, nullptr));
    require(event.get() != nullptr, "CreateEvent failed");
    std::uint64_t sequence{};
    bool gpu_is_source{};

    auto submit = [&](Ticket ticket) {
        check(list->Close(), "Close");
        ID3D12CommandList *commands[]{list.Get()};
        queue->ExecuteCommandLists(1, commands);
        // Do not unwind/recycle COM resources while GPU progress is unknown.
        // This lab owns its process, so OS process teardown is the timeout fallback.
        if (FAILED(queue->Signal(fence.Get(), ticket.fence.value)) ||
            FAILED(fence->SetEventOnCompletion(ticket.fence.value, event.get())) ||
            WaitForSingleObject(event.get(), 10000) != WAIT_OBJECT_0 ||
            fence->GetCompletedValue() == UINT64_MAX || fence->GetCompletedValue() < ticket.fence.value) {
            coherence.abort(ticket);
            std::cerr << "GPU completion failed; quarantining and terminating owned lab\n" << std::flush;
            std::_Exit(4);
        }
    };
    auto reset = [&] {
        check(allocator->Reset(), "Allocator reset");
        check(list->Reset(allocator.Get(), nullptr), "List reset");
    };
    auto to_gpu = [&](Access access) {
        const auto admission = coherence.begin(full, access, {queue_id, ++sequence});
        require(admission.status == Status::ok, "Upload/device-write admission failed");
        void *mapped{};
        const D3D12_RANGE no_read{0, 0};
        check(upload->Map(0, &no_read, &mapped), "Upload map");
        std::memcpy(mapped, access == Access::device_write ? expected.data() : host.data(), bytes);
        const D3D12_RANGE written{0, bytes};
        upload->Unmap(0, &written);
        reset();
        if (gpu_is_source)
            transition(list.Get(), gpu.Get(), D3D12_RESOURCE_STATE_COPY_SOURCE,
                       D3D12_RESOURCE_STATE_COPY_DEST);
        list->CopyBufferRegion(gpu.Get(), 0, upload.Get(), 0, bytes);
        transition(list.Get(), gpu.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_COPY_SOURCE);
        gpu_is_source = true;
        require(coherence.begin(alias, Access::host_write).status == Status::busy,
                "In-flight write not blocked");
        submit(admission.ticket);
        require(coherence.finish(admission.ticket, {queue_id, fence->GetCompletedValue()}) == Status::ok,
                "GPU publication failed");
    };
    auto to_host = [&] {
        const auto admission = coherence.begin(full, Access::readback, {queue_id, ++sequence});
        require(admission.status == Status::ok, "Readback admission failed");
        reset();
        list->CopyBufferRegion(readback.Get(), 0, gpu.Get(), 0, bytes);
        submit(admission.ticket);
        void *mapped{};
        const D3D12_RANGE read{0, bytes}, no_write{0, 0};
        check(readback->Map(0, &read, &mapped), "Readback map");
        std::memcpy(host.data(), mapped, bytes);
        readback->Unmap(0, &no_write);
        require(coherence.finish(admission.ticket, {queue_id, fence->GetCompletedValue()}) == Status::ok,
                "Host replication failed");
        const auto lease = coherence.begin(full, Access::host_read);
        require(lease.status == Status::ok, "Host read admission failed");
        const bool equal = std::memcmp(host.data(), expected.data(), bytes) == 0;
        require(coherence.finish(lease.ticket) == Status::ok && equal, "Full readback content mismatch");
    };
    to_gpu(Access::upload);
    to_host();
    host_write = coherence.begin(alias, Access::host_write);
    require(host_write.status == Status::ok, "Alias write admission failed");
    for (std::size_t i = 256; i < 320; ++i)
        host.data()[i] = expected[i] = pattern(i, 7);
    require(coherence.finish(host_write.ticket) == Status::ok, "Alias write publication failed");
    require(!coherence.inspect(full).device_current, "Alias write did not invalidate GPU replica");
    to_gpu(Access::upload);
    to_host();
    fill(expected.data(), 11);
    to_gpu(Access::device_write);
    require(coherence.begin(full, Access::host_read).status == Status::host_stale,
            "Stale host read accepted");
    to_host();
    host_write = coherence.begin(alias, Access::host_write);
    require(host_write.status == Status::ok, "Aborted write admission failed");
    host.data()[256] ^= 1;
    require(coherence.abort(host_write.ticket) == Status::ok, "CPU abort failed");
    require(coherence.begin(alias, Access::host_write).status == Status::host_stale, "Unknown bytes trusted");
    host_write = coherence.begin(full, Access::host_write);
    require(host_write.status == Status::ok, "Full recovery admission failed");
    fill(host.data(), 13);
    fill(expected.data(), 13);
    require(coherence.finish(host_write.ticket) == Status::ok, "Recovery publication failed");
    to_gpu(Access::upload);
    to_host();
    require(coherence.release(logical) == Status::ok, "Allocation still busy after readback");

    UINT64 debug_errors{};
    if (debug_queue) {
        const auto count = debug_queue->GetNumStoredMessages();
        for (UINT64 i = 0; i < count; ++i) {
            SIZE_T size{};
            check(debug_queue->GetMessage(i, nullptr, &size), "Debug message size");
            require(size && size <= 1024 * 1024, "Debug message too large");
            std::vector<unsigned char> storage(size);
            auto *message = reinterpret_cast<D3D12_MESSAGE *>(storage.data());
            check(debug_queue->GetMessage(i, message, &size), "Debug message");
            if (message->Severity <= D3D12_MESSAGE_SEVERITY_ERROR)
                ++debug_errors;
        }
    }
    require(debug_errors == 0, "D3D12 debug layer reported errors");
    std::cout << std::boolalpha << "{\n  \"mode\": \"fluidruntime-memory-foundation-v0.1\",\n"
              << "  \"foundation_passed\": true,\n  \"unified_memory_active\": false,\n"
              << "  \"third_party_authority\": false,\n  \"adapter\": "
              << json_string(adapter_info.Description) << ",\n  \"hardware\": " << hardware
              << ",\n  \"uma\": " << bool(architecture.UMA)
              << ",\n  \"cache_coherent_uma\": " << bool(architecture.CacheCoherentUMA)
              << ",\n  \"gpu_upload_heap_supported\": "
              << (gpu_upload_known ? (gpu_upload ? "true" : "false") : "null")
              << ",\n  \"local_budget_bytes\": " << (has_budgets ? std::to_string(local.Budget) : "null")
              << ",\n  \"nonlocal_budget_bytes\": "
              << (has_budgets ? std::to_string(nonlocal.Budget) : "null")
              << ",\n  \"cross_process_read_only_mapping\": true,\n  \"payload_bytes\": " << bytes
              << ",\n  \"verified_roundtrips\": 4,\n  \"logical_gpu_copy_bytes\": " << bytes * 8
              << ",\n  \"logical_gpu_bytes_omitted\": 0,\n  \"coherence_state_bytes\": "
              << sizeof(Coherence<>) << ",\n  \"debug_layer_enabled\": " << debug_enabled
              << ",\n  \"debug_errors\": " << debug_errors << "\n}\n";
    return 0;
}
} // namespace

int wmain(int argc, wchar_t *argv[]) {
    try {
        if (argc == 3 && std::wstring_view(argv[1]) == L"--mapping-child")
            return mapping_child(argv[2]);
        if (argc != 2 ||
            (std::wstring_view(argv[1]) != L"--warp" && std::wstring_view(argv[1]) != L"--hardware")) {
            std::cerr << "Usage: fluidruntime-memory-readiness --warp | --hardware\n";
            return 2;
        }
        return run(std::wstring_view(argv[1]) == L"--hardware");
    } catch (const std::exception &error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
