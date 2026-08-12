#include "fluidruntime_d3d12_hook_api.h"

#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

#include <algorithm>
#include <cstdint>
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

constexpr UINT64 kBufferBytes = 4ULL * 1024ULL * 1024ULL;
constexpr UINT64 kUploadBytes = 2 * kBufferBytes;
constexpr UINT64 kFenceValue = 1;
constexpr size_t kCopyBufferRegionVtableIndex = 15;
constexpr std::uint64_t kFnvOffsetBasis = 14695981039346656037ULL;
constexpr std::uint64_t kFnvPrime = 1099511628211ULL;

struct Options {
    std::wstring hook_path;
    bool use_hardware{};
    bool managed_control{};
    bool self_publish_control{};
    unsigned long candidate_count{128};
    DWORD control_timeout_ms{5000};
    DWORD gpu_timeout_ms{10000};
    DWORD hold_ms{100};
};

struct AdapterInfo {
    std::string description;
    UINT vendor_id{};
    UINT device_id{};
    std::string luid;
};

struct ArchitectureInfo {
    bool uma{};
    bool cache_coherent_uma{};
    UINT resource_heap_tier{};
};

struct MemoryInfo {
    bool available{};
    UINT64 budget_bytes{};
    UINT64 current_usage_bytes{};
};

struct DebugMessageCounts {
    UINT64 warnings{};
    UINT64 errors{};
};

class UniqueHandle {
public:
    explicit UniqueHandle(HANDLE handle) : handle_(handle) {}
    ~UniqueHandle() {
        if (handle_ != nullptr) {
            CloseHandle(handle_);
        }
    }

    UniqueHandle(const UniqueHandle&) = delete;
    UniqueHandle& operator=(const UniqueHandle&) = delete;

    [[nodiscard]] HANDLE get() const { return handle_; }

private:
    HANDLE handle_{};
};

std::string hresult_hex(HRESULT result) {
    std::ostringstream output;
    output << "0x" << std::uppercase << std::hex << std::setw(8)
           << std::setfill('0') << static_cast<unsigned long>(result);
    return output.str();
}

void check_hresult(HRESULT result, std::string_view operation) {
    if (FAILED(result)) {
        throw std::runtime_error(
            std::string(operation) + " failed with " + hresult_hex(result));
    }
}

std::string wide_to_utf8(std::wstring_view value) {
    if (value.empty()) {
        return {};
    }
    const auto required = WideCharToMultiByte(
        CP_UTF8,
        0,
        value.data(),
        static_cast<int>(value.size()),
        nullptr,
        0,
        nullptr,
        nullptr);
    if (required <= 0) {
        throw std::runtime_error("UTF-8 conversion failed.");
    }
    std::string result(static_cast<size_t>(required), '\0');
    WideCharToMultiByte(
        CP_UTF8,
        0,
        value.data(),
        static_cast<int>(value.size()),
        result.data(),
        required,
        nullptr,
        nullptr);
    return result;
}

std::string json_escape(std::string_view value) {
    std::ostringstream output;
    for (const auto character : value) {
        switch (character) {
        case '"': output << "\\\""; break;
        case '\\': output << "\\\\"; break;
        case '\n': output << "\\n"; break;
        case '\r': output << "\\r"; break;
        case '\t': output << "\\t"; break;
        default:
            if (static_cast<unsigned char>(character) < 0x20) {
                output << "\\u" << std::hex << std::setw(4)
                       << std::setfill('0')
                       << static_cast<int>(static_cast<unsigned char>(character))
                       << std::dec;
            } else {
                output << character;
            }
        }
    }
    return output.str();
}

const char* json_bool(bool value) {
    return value ? "true" : "false";
}

std::string uint64_hex(std::uint64_t value) {
    std::ostringstream output;
    output << std::hex << std::setw(16) << std::setfill('0') << value;
    return output.str();
}

std::string luid_hex(const LUID& luid) {
    const auto high = static_cast<std::uint64_t>(
        static_cast<std::uint32_t>(luid.HighPart));
    return uint64_hex((high << 32) | luid.LowPart);
}

std::uint64_t fnv1a64(const std::uint8_t* data, size_t size) {
    auto hash = kFnvOffsetBasis;
    for (size_t index = 0; index < size; ++index) {
        hash ^= data[index];
        hash *= kFnvPrime;
    }
    return hash;
}

double elapsed_microseconds(
    const LARGE_INTEGER& start,
    const LARGE_INTEGER& end,
    const LARGE_INTEGER& frequency) {
    return static_cast<double>(end.QuadPart - start.QuadPart) * 1'000'000.0 /
        static_cast<double>(frequency.QuadPart);
}

unsigned long parse_unsigned(
    std::wstring_view value,
    std::wstring_view option,
    unsigned long minimum,
    unsigned long maximum) {
    size_t consumed = 0;
    unsigned long parsed = 0;
    try {
        parsed = std::stoul(std::wstring(value), &consumed, 10);
    } catch (const std::exception&) {
        throw std::invalid_argument(
            wide_to_utf8(option) + " must be an integer.");
    }
    if (consumed != value.size() || parsed < minimum || parsed > maximum) {
        throw std::invalid_argument(
            wide_to_utf8(option) + " is outside its allowed range.");
    }
    return parsed;
}

bool parse_bool(std::wstring_view value) {
    if (value == L"true") return true;
    if (value == L"false") return false;
    throw std::invalid_argument("--hardware must be true or false.");
}

Options parse_options(int argc, wchar_t* argv[]) {
    Options options;
    for (int index = 1; index < argc; ++index) {
        const std::wstring_view argument(argv[index]);
        if (argument == L"--managed-control") {
            options.managed_control = true;
            continue;
        }
        if (argument == L"--self-publish-control") {
            options.managed_control = true;
            options.self_publish_control = true;
            continue;
        }
        if (index + 1 >= argc) {
            throw std::invalid_argument("A command option is missing its value.");
        }
        const std::wstring_view value(argv[++index]);
        if (argument == L"--hook") {
            options.hook_path = value;
        } else if (argument == L"--hardware") {
            options.use_hardware = parse_bool(value);
        } else if (argument == L"--candidate-count") {
            options.candidate_count = parse_unsigned(
                value,
                argument,
                1,
                static_cast<unsigned long>(fluid_hook_control_max_action_budget));
        } else if (argument == L"--control-timeout-ms") {
            options.control_timeout_ms = parse_unsigned(
                value, argument, 1, 5000);
        } else if (argument == L"--gpu-timeout-ms") {
            options.gpu_timeout_ms = parse_unsigned(
                value, argument, 1, 30000);
        } else if (argument == L"--hold-ms") {
            options.hold_ms = parse_unsigned(value, argument, 1, 5000);
        } else {
            throw std::invalid_argument(
                "Unknown option '" + wide_to_utf8(argument) + "'.");
        }
    }
    if (options.hook_path.empty()) {
        throw std::invalid_argument("--hook is required.");
    }
    return options;
}

void publish_self_test_policy(unsigned long action_budget) {
    const auto mapping_name = std::wstring(fluid_d3d12_hook_ring_name_prefix) +
        std::to_wstring(GetCurrentProcessId());
    const auto mapping = OpenFileMappingW(
        FILE_MAP_ALL_ACCESS,
        FALSE,
        mapping_name.c_str());
    if (mapping == nullptr) {
        throw std::runtime_error("Unable to open the D3D12 hook mapping for self-test.");
    }
    auto* view = static_cast<std::uint8_t*>(MapViewOfFile(
        mapping,
        FILE_MAP_ALL_ACCESS,
        0,
        0,
        static_cast<SIZE_T>(fluid_hook_ring_mapping_size)));
    if (view == nullptr) {
        const auto error = GetLastError();
        CloseHandle(mapping);
        SetLastError(error);
        throw std::runtime_error("Unable to map the D3D12 hook self-test control block.");
    }

    auto* header = reinterpret_cast<FluidHookRingHeaderV1*>(view);
    auto* control = reinterpret_cast<FluidHookControlBlockV1*>(
        view + sizeof(FluidHookRingHeaderV1));
    if (header->magic != fluid_hook_ring_magic ||
        header->abi_version != fluid_hook_ring_abi_version ||
        control->magic != fluid_hook_control_magic ||
        control->abi_version != fluid_hook_control_abi_version) {
        UnmapViewOfFile(view);
        CloseHandle(mapping);
        throw std::runtime_error("The D3D12 hook self-test mapping has the wrong ABI.");
    }

    LARGE_INTEGER now{};
    QueryPerformanceCounter(&now);
    InterlockedExchange64(&control->expires_at_qpc, now.QuadPart +
        static_cast<LONG64>(header->qpc_frequency * 3));
    InterlockedExchange64(
        &control->action_mask,
        static_cast<LONG64>(
            fluid_hook_control_action_skip_redundant_d3d12_copy_buffer_region));
    InterlockedExchange64(
        &control->action_budget,
        static_cast<LONG64>(action_budget));
    InterlockedExchange64(&control->applied_action_count, 0);
    InterlockedExchange64(&control->status, 0);
    MemoryBarrier();
    InterlockedExchange64(&control->published_epoch, 1);
    UnmapViewOfFile(view);
    CloseHandle(mapping);
}

bool enable_debug_layer_if_available() {
#ifdef _DEBUG
    ComPtr<ID3D12Debug> debug;
    if (SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&debug)))) {
        debug->EnableDebugLayer();
        return true;
    }
#endif
    return false;
}

DebugMessageCounts inspect_debug_messages(ID3D12InfoQueue* info_queue) {
    DebugMessageCounts counts;
    if (info_queue == nullptr) {
        return counts;
    }
    const auto count = info_queue->GetNumStoredMessages();
    for (UINT64 index = 0; index < count; ++index) {
        SIZE_T bytes = 0;
        check_hresult(
            info_queue->GetMessage(index, nullptr, &bytes),
            "ID3D12InfoQueue::GetMessage(size)");
        std::vector<std::uint8_t> storage(bytes);
        auto* message = reinterpret_cast<D3D12_MESSAGE*>(storage.data());
        check_hresult(
            info_queue->GetMessage(index, message, &bytes),
            "ID3D12InfoQueue::GetMessage(data)");
        if (message->Severity == D3D12_MESSAGE_SEVERITY_WARNING) {
            ++counts.warnings;
            std::cerr << "D3D12 debug warning: " << message->pDescription << '\n';
        } else if (
            message->Severity == D3D12_MESSAGE_SEVERITY_ERROR ||
            message->Severity == D3D12_MESSAGE_SEVERITY_CORRUPTION) {
            ++counts.errors;
            std::cerr << "D3D12 debug error: " << message->pDescription << '\n';
        }
    }
    return counts;
}

ComPtr<IDXGIAdapter1> select_adapter(
    IDXGIFactory6* factory,
    bool use_hardware) {
    ComPtr<IDXGIAdapter1> adapter;
    if (!use_hardware) {
        check_hresult(
            factory->EnumWarpAdapter(IID_PPV_ARGS(&adapter)),
            "IDXGIFactory4::EnumWarpAdapter");
        return adapter;
    }

    for (UINT index = 0;; ++index) {
        ComPtr<IDXGIAdapter1> candidate;
        const auto result = factory->EnumAdapterByGpuPreference(
            index,
            DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE,
            IID_PPV_ARGS(&candidate));
        if (result == DXGI_ERROR_NOT_FOUND) {
            break;
        }
        check_hresult(result, "IDXGIFactory6::EnumAdapterByGpuPreference");
        DXGI_ADAPTER_DESC1 description{};
        check_hresult(candidate->GetDesc1(&description), "IDXGIAdapter1::GetDesc1");
        if ((description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0 &&
            SUCCEEDED(D3D12CreateDevice(
                candidate.Get(),
                D3D_FEATURE_LEVEL_11_0,
                __uuidof(ID3D12Device),
                nullptr))) {
            return candidate;
        }
    }
    throw std::runtime_error("No D3D12 hardware adapter was found.");
}

AdapterInfo query_adapter(IDXGIAdapter1* adapter) {
    DXGI_ADAPTER_DESC1 description{};
    check_hresult(adapter->GetDesc1(&description), "IDXGIAdapter1::GetDesc1");
    return {
        .description = wide_to_utf8(description.Description),
        .vendor_id = description.VendorId,
        .device_id = description.DeviceId,
        .luid = luid_hex(description.AdapterLuid),
    };
}

ArchitectureInfo query_architecture(ID3D12Device* device) {
    ArchitectureInfo result;
    D3D12_FEATURE_DATA_ARCHITECTURE1 architecture{.NodeIndex = 0};
    if (SUCCEEDED(device->CheckFeatureSupport(
            D3D12_FEATURE_ARCHITECTURE1,
            &architecture,
            sizeof(architecture)))) {
        result.uma = architecture.UMA != FALSE;
        result.cache_coherent_uma = architecture.CacheCoherentUMA != FALSE;
    } else {
        D3D12_FEATURE_DATA_ARCHITECTURE fallback{.NodeIndex = 0};
        check_hresult(
            device->CheckFeatureSupport(
                D3D12_FEATURE_ARCHITECTURE,
                &fallback,
                sizeof(fallback)),
            "ID3D12Device::CheckFeatureSupport(ARCHITECTURE)");
        result.uma = fallback.UMA != FALSE;
        result.cache_coherent_uma = fallback.CacheCoherentUMA != FALSE;
    }
    D3D12_FEATURE_DATA_D3D12_OPTIONS options{};
    check_hresult(
        device->CheckFeatureSupport(
            D3D12_FEATURE_D3D12_OPTIONS,
            &options,
            sizeof(options)),
        "ID3D12Device::CheckFeatureSupport(D3D12_OPTIONS)");
    result.resource_heap_tier = static_cast<UINT>(options.ResourceHeapTier);
    return result;
}

MemoryInfo query_memory(
    IDXGIAdapter3* adapter,
    DXGI_MEMORY_SEGMENT_GROUP group) {
    if (adapter == nullptr) {
        return {};
    }
    DXGI_QUERY_VIDEO_MEMORY_INFO info{};
    if (FAILED(adapter->QueryVideoMemoryInfo(0, group, &info))) {
        return {};
    }
    return {
        .available = true,
        .budget_bytes = info.Budget,
        .current_usage_bytes = info.CurrentUsage,
    };
}

ComPtr<ID3D12Resource> create_buffer(
    ID3D12Device* device,
    UINT64 bytes,
    D3D12_HEAP_TYPE heap_type,
    D3D12_RESOURCE_STATES initial_state) {
    D3D12_HEAP_PROPERTIES heap{};
    heap.Type = heap_type;
    heap.CreationNodeMask = 1;
    heap.VisibleNodeMask = 1;
    D3D12_RESOURCE_DESC description{};
    description.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    description.Width = bytes;
    description.Height = 1;
    description.DepthOrArraySize = 1;
    description.MipLevels = 1;
    description.Format = DXGI_FORMAT_UNKNOWN;
    description.SampleDesc.Count = 1;
    description.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;

    ComPtr<ID3D12Resource> resource;
    check_hresult(
        device->CreateCommittedResource(
            &heap,
            D3D12_HEAP_FLAG_NONE,
            &description,
            initial_state,
            nullptr,
            IID_PPV_ARGS(&resource)),
        "ID3D12Device::CreateCommittedResource");
    return resource;
}

void fill_pattern(std::vector<std::uint8_t>& bytes, std::uint64_t seed) {
    for (size_t index = 0; index < bytes.size(); ++index) {
        bytes[index] = static_cast<std::uint8_t>(
            (index * 131ULL + (index >> 7U) + seed) & 0xffULL);
    }
}

int run(const Options& options) {
    LARGE_INTEGER timer_frequency{};
    if (!QueryPerformanceFrequency(&timer_frequency)) {
        throw std::runtime_error("QueryPerformanceFrequency failed.");
    }

    const auto debug_layer_enabled = enable_debug_layer_if_available();
    ComPtr<IDXGIFactory6> factory;
    check_hresult(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)), "CreateDXGIFactory2");
    auto adapter = select_adapter(factory.Get(), options.use_hardware);
    const auto adapter_info = query_adapter(adapter.Get());
    ComPtr<IDXGIAdapter3> adapter3;
    adapter.As(&adapter3);

    ComPtr<ID3D12Device> device;
    check_hresult(
        D3D12CreateDevice(
            adapter.Get(),
            D3D_FEATURE_LEVEL_11_0,
            IID_PPV_ARGS(&device)),
        "D3D12CreateDevice");
    ComPtr<ID3D12InfoQueue> debug_info_queue;
    if (debug_layer_enabled) {
        check_hresult(
            device.As(&debug_info_queue),
            "ID3D12Device::QueryInterface(ID3D12InfoQueue)");
    }
    const auto architecture = query_architecture(device.Get());
    const auto local_before = query_memory(
        adapter3.Get(), DXGI_MEMORY_SEGMENT_GROUP_LOCAL);
    const auto non_local_before = query_memory(
        adapter3.Get(), DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL);

    D3D12_COMMAND_QUEUE_DESC queue_description{};
    queue_description.Type = D3D12_COMMAND_LIST_TYPE_COPY;
    queue_description.Priority = D3D12_COMMAND_QUEUE_PRIORITY_NORMAL;
    ComPtr<ID3D12CommandQueue> queue;
    check_hresult(
        device->CreateCommandQueue(&queue_description, IID_PPV_ARGS(&queue)),
        "ID3D12Device::CreateCommandQueue");
    UINT64 timestamp_frequency = 0;
    const auto timestamp_supported =
        SUCCEEDED(queue->GetTimestampFrequency(&timestamp_frequency)) &&
        timestamp_frequency != 0;

    auto upload = create_buffer(
        device.Get(),
        kUploadBytes,
        D3D12_HEAP_TYPE_UPLOAD,
        D3D12_RESOURCE_STATE_GENERIC_READ);
    auto destination = create_buffer(
        device.Get(),
        kBufferBytes,
        D3D12_HEAP_TYPE_DEFAULT,
        D3D12_RESOURCE_STATE_COMMON);
    auto readback = create_buffer(
        device.Get(),
        kBufferBytes,
        D3D12_HEAP_TYPE_READBACK,
        D3D12_RESOURCE_STATE_COPY_DEST);
    ComPtr<ID3D12Resource> timestamp_readback;
    ComPtr<ID3D12QueryHeap> timestamp_heap;
    if (timestamp_supported) {
        timestamp_readback = create_buffer(
            device.Get(),
            2 * sizeof(UINT64),
            D3D12_HEAP_TYPE_READBACK,
            D3D12_RESOURCE_STATE_COPY_DEST);
        D3D12_QUERY_HEAP_DESC query_description{};
        query_description.Type = D3D12_QUERY_HEAP_TYPE_COPY_QUEUE_TIMESTAMP;
        query_description.Count = 2;
        check_hresult(
            device->CreateQueryHeap(&query_description, IID_PPV_ARGS(&timestamp_heap)),
            "ID3D12Device::CreateQueryHeap");
    }

    std::vector<std::uint8_t> pattern_a(static_cast<size_t>(kBufferBytes));
    std::vector<std::uint8_t> pattern_b(static_cast<size_t>(kBufferBytes));
    fill_pattern(pattern_a, 17);
    fill_pattern(pattern_b, 91);
    const auto pattern_a_hash = fnv1a64(pattern_a.data(), pattern_a.size());
    const auto pattern_b_hash = fnv1a64(pattern_b.data(), pattern_b.size());
    if (pattern_a_hash == pattern_b_hash || pattern_a == pattern_b) {
        throw std::runtime_error("D3D12 source patterns are not distinct.");
    }
    std::vector<std::uint8_t> upload_shadow(static_cast<size_t>(kUploadBytes));
    std::memcpy(upload_shadow.data(), pattern_a.data(), pattern_a.size());
    std::memcpy(
        upload_shadow.data() + kBufferBytes,
        pattern_b.data(),
        pattern_b.size());

    void* upload_mapping = nullptr;
    const D3D12_RANGE no_read{0, 0};
    check_hresult(
        upload->Map(0, &no_read, &upload_mapping),
        "ID3D12Resource::Map(upload)");
    std::memcpy(upload_mapping, upload_shadow.data(), upload_shadow.size());

    ComPtr<ID3D12CommandAllocator> allocator;
    check_hresult(
        device->CreateCommandAllocator(
            D3D12_COMMAND_LIST_TYPE_COPY,
            IID_PPV_ARGS(&allocator)),
        "ID3D12Device::CreateCommandAllocator");
    ComPtr<ID3D12GraphicsCommandList> command_list;
    check_hresult(
        device->CreateCommandList(
            0,
            D3D12_COMMAND_LIST_TYPE_COPY,
            allocator.Get(),
            nullptr,
            IID_PPV_ARGS(&command_list)),
        "ID3D12Device::CreateCommandList");
    auto** command_list_vtable = *reinterpret_cast<void***>(command_list.Get());
    const auto original_copy_buffer_region =
        command_list_vtable[kCopyBufferRegionVtableIndex];

    const auto hook_module = LoadLibraryW(options.hook_path.c_str());
    if (hook_module == nullptr) {
        throw std::runtime_error("Unable to load D3D12 hook DLL.");
    }
    const auto attach = reinterpret_cast<FluidD3D12HookAttachExFunction>(
        GetProcAddress(hook_module, "FluidD3D12HookAttachEx"));
    const auto register_upload =
        reinterpret_cast<FluidD3D12HookRegisterUploadBufferFunction>(
            GetProcAddress(hook_module, "FluidD3D12HookRegisterUploadBuffer"));
    const auto register_destination =
        reinterpret_cast<FluidD3D12HookRegisterCopyOnlyBufferFunction>(
            GetProcAddress(hook_module, "FluidD3D12HookRegisterCopyOnlyBuffer"));
    const auto invalidate =
        reinterpret_cast<FluidD3D12HookInvalidateResourceFunction>(
            GetProcAddress(hook_module, "FluidD3D12HookInvalidateResource"));
    const auto wait_for_policy =
        reinterpret_cast<FluidD3D12HookWaitForControlPolicyFunction>(
            GetProcAddress(hook_module, "FluidD3D12HookWaitForControlPolicy"));
    const auto detach = reinterpret_cast<FluidD3D12HookDetachFunction>(
        GetProcAddress(hook_module, "FluidD3D12HookDetach"));
    const auto read_snapshot = reinterpret_cast<FluidD3D12HookReadSnapshotFunction>(
        GetProcAddress(hook_module, "FluidD3D12HookReadSnapshot"));
    if (attach == nullptr || register_upload == nullptr ||
        register_destination == nullptr || invalidate == nullptr ||
        wait_for_policy == nullptr || detach == nullptr || read_snapshot == nullptr) {
        throw std::runtime_error("D3D12 hook DLL omitted a required export.");
    }

    FluidD3D12HookAttachOptionsV1 attach_options{
        .struct_size = sizeof(FluidD3D12HookAttachOptionsV1),
        .abi_version = fluid_d3d12_hook_attach_options_abi_version,
        .flags = options.managed_control
            ? fluid_d3d12_hook_attach_flag_allow_control_policy
            : 0,
        .max_tracked_copy_bytes = kBufferBytes,
        .max_tracked_resources = 1,
    };
    const auto attach_result = attach(command_list.Get(), &attach_options);
    check_hresult(attach_result, "FluidD3D12HookAttachEx");
    const auto register_upload_result = register_upload(
        upload.Get(), upload_shadow.data(), kUploadBytes);
    check_hresult(
        register_upload_result,
        "FluidD3D12HookRegisterUploadBuffer");
    const D3D12_RANGE upload_written{
        0,
        static_cast<SIZE_T>(kUploadBytes),
    };
    upload->Unmap(0, &upload_written);
    upload_mapping = nullptr;
    const bool upload_unmapped_after_registration = true;
    const auto register_destination_result = register_destination(
        destination.Get(), kBufferBytes);
    check_hresult(
        register_destination_result,
        "FluidD3D12HookRegisterCopyOnlyBuffer");

    Sleep(options.hold_ms);
    if (options.self_publish_control) {
        publish_self_test_policy(options.candidate_count);
    }
    const auto control_wait_result = options.managed_control
        ? wait_for_policy(options.control_timeout_ms)
        : S_FALSE;
    if (options.managed_control) {
        check_hresult(
            control_wait_result,
            "FluidD3D12HookWaitForControlPolicy");
    }

    LARGE_INTEGER record_start{};
    LARGE_INTEGER record_end{};
    LARGE_INTEGER submit_start{};
    LARGE_INTEGER completion{};
    QueryPerformanceCounter(&record_start);
    if (timestamp_supported) {
        command_list->EndQuery(
            timestamp_heap.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 0);
    }

    command_list->CopyBufferRegion(
        destination.Get(), 0, upload.Get(), 0, kBufferBytes);
    const auto first_group = options.candidate_count / 2;
    const auto second_total = options.candidate_count - first_group;
    const auto second_before_automatic_invalidation = second_total / 2;
    const auto second_after_explicit_invalidation =
        second_total - second_before_automatic_invalidation;
    for (unsigned long index = 0; index < first_group; ++index) {
        command_list->CopyBufferRegion(
            destination.Get(), 0, upload.Get(), 0, kBufferBytes);
    }
    command_list->CopyBufferRegion(
        destination.Get(), 0, upload.Get(), kBufferBytes, kBufferBytes);
    for (unsigned long index = 0;
         index < second_before_automatic_invalidation;
         ++index) {
        command_list->CopyBufferRegion(
            destination.Get(), 0, upload.Get(), kBufferBytes, kBufferBytes);
    }
    command_list->CopyBufferRegion(
        destination.Get(), 0, upload.Get(), kBufferBytes, 1);
    command_list->CopyBufferRegion(
        destination.Get(), 0, upload.Get(), kBufferBytes, kBufferBytes);
    const auto invalidation_result = invalidate(destination.Get());
    check_hresult(invalidation_result, "FluidD3D12HookInvalidateResource");
    command_list->CopyBufferRegion(
        destination.Get(), 0, upload.Get(), kBufferBytes, kBufferBytes);
    for (unsigned long index = 0;
         index < second_after_explicit_invalidation;
         ++index) {
        command_list->CopyBufferRegion(
            destination.Get(), 0, upload.Get(), kBufferBytes, kBufferBytes);
    }

    if (timestamp_supported) {
        command_list->EndQuery(
            timestamp_heap.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 1);
        command_list->ResolveQueryData(
            timestamp_heap.Get(),
            D3D12_QUERY_TYPE_TIMESTAMP,
            0,
            2,
            timestamp_readback.Get(),
            0);
    }
    D3D12_RESOURCE_BARRIER barrier{};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = destination.Get();
    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    command_list->ResourceBarrier(1, &barrier);
    command_list->CopyBufferRegion(
        readback.Get(), 0, destination.Get(), 0, kBufferBytes);
    const auto close_result = command_list->Close();
    if (FAILED(close_result)) {
        inspect_debug_messages(debug_info_queue.Get());
    }
    check_hresult(close_result, "ID3D12GraphicsCommandList::Close");
    QueryPerformanceCounter(&record_end);

    ComPtr<ID3D12Fence> fence;
    check_hresult(
        device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence)),
        "ID3D12Device::CreateFence");
    UniqueHandle fence_event(CreateEventW(nullptr, FALSE, FALSE, nullptr));
    if (fence_event.get() == nullptr) {
        throw std::runtime_error("CreateEventW failed.");
    }

    QueryPerformanceCounter(&submit_start);
    ID3D12CommandList* command_lists[] = {command_list.Get()};
    queue->ExecuteCommandLists(1, command_lists);
    check_hresult(queue->Signal(fence.Get(), kFenceValue), "ID3D12CommandQueue::Signal");
    if (fence->GetCompletedValue() < kFenceValue) {
        check_hresult(
            fence->SetEventOnCompletion(kFenceValue, fence_event.get()),
            "ID3D12Fence::SetEventOnCompletion");
        const auto wait_result = WaitForSingleObject(
            fence_event.get(), options.gpu_timeout_ms);
        if (wait_result == WAIT_TIMEOUT) {
            throw std::runtime_error("D3D12 fence wait timed out.");
        }
        if (wait_result != WAIT_OBJECT_0) {
            throw std::runtime_error("D3D12 fence wait failed.");
        }
    }
    QueryPerformanceCounter(&completion);

    void* readback_mapping = nullptr;
    const D3D12_RANGE full_read{0, static_cast<SIZE_T>(kBufferBytes)};
    check_hresult(
        readback->Map(0, &full_read, &readback_mapping),
        "ID3D12Resource::Map(readback)");
    const auto final_hash = fnv1a64(
        static_cast<const std::uint8_t*>(readback_mapping),
        static_cast<size_t>(kBufferBytes));
    const auto content_equivalent =
        std::memcmp(readback_mapping, pattern_b.data(), pattern_b.size()) == 0 &&
        final_hash == pattern_b_hash;
    readback->Unmap(0, &no_read);
    if (!content_equivalent) {
        throw std::runtime_error("D3D12 final content verification failed.");
    }

    UINT64 gpu_ticks = 0;
    bool gpu_timing_valid = false;
    if (timestamp_supported) {
        void* timestamp_mapping = nullptr;
        const D3D12_RANGE timestamp_read{0, 2 * sizeof(UINT64)};
        check_hresult(
            timestamp_readback->Map(0, &timestamp_read, &timestamp_mapping),
            "ID3D12Resource::Map(timestamp)");
        const auto* timestamps = static_cast<const UINT64*>(timestamp_mapping);
        gpu_timing_valid = timestamps[1] >= timestamps[0];
        gpu_ticks = gpu_timing_valid ? timestamps[1] - timestamps[0] : 0;
        timestamp_readback->Unmap(0, &no_read);
    }

    const auto upload_a_hash_after = fnv1a64(
        upload_shadow.data(),
        static_cast<size_t>(kBufferBytes));
    const auto upload_b_hash_after = fnv1a64(
        upload_shadow.data() + kBufferBytes,
        static_cast<size_t>(kBufferBytes));
    const auto immutable_sources_verified =
        upload_unmapped_after_registration &&
        upload_a_hash_after == pattern_a_hash &&
        upload_b_hash_after == pattern_b_hash;
    if (!immutable_sources_verified) {
        throw std::runtime_error("A registered immutable upload source changed.");
    }

    FluidD3D12HookSnapshotV1 snapshot{
        .struct_size = sizeof(FluidD3D12HookSnapshotV1),
        .abi_version = fluid_d3d12_hook_snapshot_abi_version,
    };
    check_hresult(
        read_snapshot(&snapshot),
        "FluidD3D12HookReadSnapshot");
    const auto expected_tracked =
        static_cast<std::uint64_t>(options.candidate_count) + 4;
    const auto expected_skipped = options.managed_control
        ? static_cast<std::uint64_t>(options.candidate_count)
        : 0;
    const auto expected_forwarded = expected_tracked - expected_skipped;
    const auto expected_status = options.managed_control
        ? static_cast<std::uint64_t>(FluidHookControlStatusV1::exhausted)
        : static_cast<std::uint64_t>(FluidHookControlStatusV1::none);
    const auto metrics_valid =
        snapshot.attached == 1 &&
        snapshot.source_snapshot_bytes == kUploadBytes &&
        snapshot.tracked_resource_bytes == kBufferBytes &&
        snapshot.copy_buffer_region_count == expected_tracked + 2 &&
        snapshot.tracked_copy_count == expected_tracked &&
        snapshot.tracked_copy_bytes == expected_tracked * kBufferBytes &&
        snapshot.redundant_candidate_count == options.candidate_count &&
        snapshot.redundant_candidate_bytes ==
            static_cast<std::uint64_t>(options.candidate_count) * kBufferBytes &&
        snapshot.forwarded_copy_count == expected_forwarded &&
        snapshot.forwarded_copy_bytes == expected_forwarded * kBufferBytes &&
        snapshot.skipped_copy_count == expected_skipped &&
        snapshot.skipped_copy_bytes == expected_skipped * kBufferBytes &&
        snapshot.exact_comparison_count == options.candidate_count + 1 &&
        snapshot.exact_comparison_bytes ==
            static_cast<std::uint64_t>(options.candidate_count + 1) * kBufferBytes &&
        snapshot.source_registration_count == 1 &&
        snapshot.destination_registration_count == 1 &&
        snapshot.automatic_invalidation_count == 1 &&
        snapshot.explicit_invalidation_count == 1 &&
        snapshot.command_list_close_count == 1 &&
        snapshot.command_list_reset_count == 0 &&
        snapshot.cache_bytes == 0 &&
        snapshot.control_policy_enabled == (options.managed_control ? 1ULL : 0ULL) &&
        snapshot.control_policy_epoch == (options.managed_control ? 1ULL : 0ULL) &&
        snapshot.control_policy_acknowledged_epoch ==
            (options.managed_control ? 1ULL : 0ULL) &&
        snapshot.control_policy_applied_action_count == expected_skipped &&
        snapshot.control_policy_rejected_count == 0 &&
        snapshot.control_policy_status == expected_status &&
        snapshot.ipc_overrun_count == 0;
    if (!metrics_valid) {
        throw std::runtime_error("D3D12 hook metrics violated the native contract.");
    }

    const auto detach_result = detach();
    check_hresult(detach_result, "FluidD3D12HookDetach");
    const auto original_pointer_restored =
        command_list_vtable[kCopyBufferRegionVtableIndex] ==
        original_copy_buffer_region;
    if (!original_pointer_restored) {
        throw std::runtime_error("D3D12 command-list vtable was not restored.");
    }
    const auto local_after = query_memory(
        adapter3.Get(), DXGI_MEMORY_SEGMENT_GROUP_LOCAL);
    const auto non_local_after = query_memory(
        adapter3.Get(), DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL);
    const auto debug_messages = inspect_debug_messages(debug_info_queue.Get());
    if (debug_messages.warnings != 0 || debug_messages.errors != 0) {
        throw std::runtime_error(
            "The D3D12 debug layer reported a warning, error, or corruption message.");
    }

    const auto cpu_record_microseconds = elapsed_microseconds(
        record_start, record_end, timer_frequency);
    const auto submit_to_fence_microseconds = elapsed_microseconds(
        submit_start, completion, timer_frequency);
    const auto total_workload_microseconds = elapsed_microseconds(
        record_start, completion, timer_frequency);
    const auto gpu_workload_microseconds = gpu_timing_valid
        ? static_cast<double>(gpu_ticks) * 1'000'000.0 /
            static_cast<double>(timestamp_frequency)
        : 0.0;

    std::cout << std::fixed << std::setprecision(3)
              << "{\n"
              << "  \"mode\": \"fluidruntime-owned-d3d12-copy-elision-v0.20.0\",\n"
              << "  \"target_owned\": true,\n"
              << "  \"cooperative_load\": true,\n"
              << "  \"remote_injection\": false,\n"
              << "  \"actuation_enabled\": "
              << json_bool(options.managed_control) << ",\n"
              << "  \"self_published_control\": "
              << json_bool(options.self_publish_control) << ",\n"
              << "  \"physical_transfer_bytes_measured\": false,\n"
              << "  \"debug_layer_enabled\": "
              << json_bool(debug_layer_enabled) << ",\n"
              << "  \"debug_warning_count\": "
              << debug_messages.warnings << ",\n"
              << "  \"debug_error_count\": "
              << debug_messages.errors << ",\n"
              << "  \"render_driver\": \""
              << (options.use_hardware ? "hardware" : "warp") << "\",\n"
              << "  \"process_id\": " << GetCurrentProcessId() << ",\n"
              << "  \"adapter\": {\n"
              << "    \"description\": \""
              << json_escape(adapter_info.description) << "\",\n"
              << "    \"vendor_id\": " << adapter_info.vendor_id << ",\n"
              << "    \"device_id\": " << adapter_info.device_id << ",\n"
              << "    \"luid\": \"" << adapter_info.luid << "\",\n"
              << "    \"uma\": " << json_bool(architecture.uma) << ",\n"
              << "    \"cache_coherent_uma\": "
              << json_bool(architecture.cache_coherent_uma) << ",\n"
              << "    \"resource_heap_tier\": "
              << architecture.resource_heap_tier << "\n"
              << "  },\n"
              << "  \"workload\": {\n"
              << "    \"scope\": \"owned-d3d12-copy-queue-full-buffer-copy-buffer-region\",\n"
              << "    \"buffer_bytes\": " << kBufferBytes << ",\n"
              << "    \"source_snapshot_mode\": "
              << "\"registration-copy-cpu-shadow-upload-unmapped-until-fence\",\n"
              << "    \"source_snapshot_bytes\": " << kUploadBytes << ",\n"
              << "    \"upload_unmapped_after_registration\": "
              << json_bool(upload_unmapped_after_registration) << ",\n"
              << "    \"candidate_count\": " << options.candidate_count << ",\n"
              << "    \"tracked_copy_count\": " << expected_tracked << ",\n"
              << "    \"logical_candidate_bytes\": "
              << static_cast<std::uint64_t>(options.candidate_count) * kBufferBytes
              << ",\n"
              << "    \"expected_forwarded_count\": " << expected_forwarded << ",\n"
              << "    \"expected_skipped_count\": " << expected_skipped << ",\n"
              << "    \"source_transition_applied\": true,\n"
              << "    \"automatic_invalidation_guard_applied\": true,\n"
              << "    \"explicit_invalidation_guard_applied\": true,\n"
              << "    \"immutable_sources_verified\": "
              << json_bool(immutable_sources_verified) << ",\n"
              << "    \"pattern_a_hash\": \""
              << uint64_hex(pattern_a_hash) << "\",\n"
              << "    \"pattern_b_hash\": \""
              << uint64_hex(pattern_b_hash) << "\",\n"
              << "    \"final_hash\": \"" << uint64_hex(final_hash) << "\",\n"
              << "    \"content_equivalent\": "
              << json_bool(content_equivalent) << "\n"
              << "  },\n"
              << "  \"hook\": {\n"
              << "    \"snapshot_abi_version\": " << snapshot.abi_version << ",\n"
              << "    \"source_snapshot_bytes\": "
              << snapshot.source_snapshot_bytes << ",\n"
              << "    \"attach_hresult\": \"" << hresult_hex(attach_result) << "\",\n"
              << "    \"register_upload_hresult\": \""
              << hresult_hex(register_upload_result) << "\",\n"
              << "    \"register_destination_hresult\": \""
              << hresult_hex(register_destination_result) << "\",\n"
              << "    \"invalidation_hresult\": \""
              << hresult_hex(invalidation_result) << "\",\n"
              << "    \"detach_hresult\": \"" << hresult_hex(detach_result) << "\",\n"
              << "    \"original_pointer_restored\": "
              << json_bool(original_pointer_restored) << ",\n"
              << "    \"copy_buffer_region_count\": "
              << snapshot.copy_buffer_region_count << ",\n"
              << "    \"tracked_copy_count\": "
              << snapshot.tracked_copy_count << ",\n"
              << "    \"tracked_copy_bytes\": "
              << snapshot.tracked_copy_bytes << ",\n"
              << "    \"redundant_candidate_count\": "
              << snapshot.redundant_candidate_count << ",\n"
              << "    \"redundant_candidate_bytes\": "
              << snapshot.redundant_candidate_bytes << ",\n"
              << "    \"forwarded_copy_count\": "
              << snapshot.forwarded_copy_count << ",\n"
              << "    \"forwarded_copy_bytes\": "
              << snapshot.forwarded_copy_bytes << ",\n"
              << "    \"skipped_copy_count\": "
              << snapshot.skipped_copy_count << ",\n"
              << "    \"skipped_copy_bytes\": "
              << snapshot.skipped_copy_bytes << ",\n"
              << "    \"exact_comparison_count\": "
              << snapshot.exact_comparison_count << ",\n"
              << "    \"exact_comparison_bytes\": "
              << snapshot.exact_comparison_bytes << ",\n"
              << "    \"automatic_invalidation_count\": "
              << snapshot.automatic_invalidation_count << ",\n"
              << "    \"explicit_invalidation_count\": "
              << snapshot.explicit_invalidation_count << ",\n"
              << "    \"command_list_close_count\": "
              << snapshot.command_list_close_count << ",\n"
              << "    \"cache_generation\": "
              << snapshot.cache_generation << ",\n"
              << "    \"ipc_event_count\": "
              << snapshot.ipc_event_count << ",\n"
              << "    \"ipc_overrun_count\": "
              << snapshot.ipc_overrun_count << "\n"
              << "  },\n"
              << "  \"control\": {\n"
              << "    \"requested\": "
              << json_bool(options.managed_control) << ",\n"
              << "    \"wait_hresult\": \""
              << hresult_hex(control_wait_result) << "\",\n"
              << "    \"enabled\": "
              << snapshot.control_policy_enabled << ",\n"
              << "    \"epoch\": " << snapshot.control_policy_epoch << ",\n"
              << "    \"acknowledged_epoch\": "
              << snapshot.control_policy_acknowledged_epoch << ",\n"
              << "    \"applied_action_count\": "
              << snapshot.control_policy_applied_action_count << ",\n"
              << "    \"rejected_count\": "
              << snapshot.control_policy_rejected_count << ",\n"
              << "    \"status\": " << snapshot.control_policy_status << "\n"
              << "  },\n"
              << "  \"timing\": {\n"
              << "    \"qpc_frequency\": " << timer_frequency.QuadPart << ",\n"
              << "    \"cpu_record_microseconds\": "
              << cpu_record_microseconds << ",\n"
              << "    \"submit_to_fence_microseconds\": "
              << submit_to_fence_microseconds << ",\n"
              << "    \"total_workload_microseconds\": "
              << total_workload_microseconds << ",\n"
              << "    \"gpu_timestamp_valid\": "
              << json_bool(gpu_timing_valid) << ",\n"
              << "    \"gpu_timestamp_frequency_hz\": "
              << timestamp_frequency << ",\n"
              << "    \"gpu_workload_ticks\": " << gpu_ticks << ",\n"
              << "    \"gpu_workload_microseconds\": "
              << gpu_workload_microseconds << ",\n"
              << "    \"fence_signaled_value\": " << kFenceValue << ",\n"
              << "    \"fence_completed_value\": "
              << fence->GetCompletedValue() << "\n"
              << "  },\n"
              << "  \"memory\": {\n"
              << "    \"source\": \"idxgiadapter3-query-video-memory-info\",\n"
              << "    \"local_available\": "
              << json_bool(local_before.available && local_after.available) << ",\n"
              << "    \"local_budget_before\": "
              << local_before.budget_bytes << ",\n"
              << "    \"local_usage_before\": "
              << local_before.current_usage_bytes << ",\n"
              << "    \"local_usage_after\": "
              << local_after.current_usage_bytes << ",\n"
              << "    \"non_local_available\": "
              << json_bool(non_local_before.available && non_local_after.available)
              << ",\n"
              << "    \"non_local_budget_before\": "
              << non_local_before.budget_bytes << ",\n"
              << "    \"non_local_usage_before\": "
              << non_local_before.current_usage_bytes << ",\n"
              << "    \"non_local_usage_after\": "
              << non_local_after.current_usage_bytes << "\n"
              << "  },\n"
              << "  \"claim_scope\": "
              << "\"owned-d3d12-copy-buffer-region-exact-content-elision\",\n"
              << "  \"limitations\": [\n"
              << "    \"Logical skipped bytes are API-level commands, not measured PCIe or physical RAM-to-VRAM traffic.\",\n"
              << "    \"Actuation is restricted to one registered copy-only buffer in one owned copy command list.\",\n"
              << "    \"The registered CPU shadow must match the upload range, which remains unmapped until the completion fence.\",\n"
              << "    \"This target does not inject into or alter external applications.\"\n"
              << "  ]\n"
              << "}\n";
    return 0;
}

} // namespace

int wmain(int argc, wchar_t* argv[]) {
    try {
        return run(parse_options(argc, argv));
    } catch (const std::invalid_argument& exception) {
        std::cerr << "D3D12 hook target input error: " << exception.what() << '\n';
        return 2;
    } catch (const std::exception& exception) {
        std::cerr << "D3D12 hook target failed: " << exception.what() << '\n';
        return 1;
    }
}
