#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

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
constexpr UINT64 kFenceValue = 1;
constexpr std::uint64_t kFnvOffsetBasis = 14695981039346656037ULL;
constexpr std::uint64_t kFnvPrime = 1099511628211ULL;

struct Options {
    bool use_hardware{};
    DWORD gpu_timeout_ms{10000};
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

struct AdapterInfo {
    std::string description;
    UINT vendor_id{};
    UINT device_id{};
    UINT subsystem_id{};
    UINT revision{};
    std::string luid;
    UINT64 dedicated_video_memory_bytes{};
    UINT64 dedicated_system_memory_bytes{};
    UINT64 shared_system_memory_bytes{};
};

struct ArchitectureInfo {
    bool available{};
    UINT node_count{};
    bool tile_based_renderer{};
    bool uma{};
    bool cache_coherent_uma{};
    UINT resource_heap_tier{};
};

struct VideoMemorySnapshot {
    bool available{};
    UINT64 budget_bytes{};
    UINT64 current_usage_bytes{};
    UINT64 current_reservation_bytes{};
    UINT64 available_for_reservation_bytes{};
};

struct DebugMessageCounts {
    UINT64 warnings{};
    UINT64 errors{};
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

    const auto message_count = info_queue->GetNumStoredMessages();
    for (UINT64 index = 0; index < message_count; ++index) {
        SIZE_T message_bytes = 0;
        check_hresult(
            info_queue->GetMessage(index, nullptr, &message_bytes),
            "ID3D12InfoQueue::GetMessage(size)");
        std::vector<std::uint8_t> storage(message_bytes);
        auto* message = reinterpret_cast<D3D12_MESSAGE*>(storage.data());
        check_hresult(
            info_queue->GetMessage(index, message, &message_bytes),
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
        throw std::runtime_error("Unable to encode adapter description as UTF-8.");
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
        case '\b': output << "\\b"; break;
        case '\f': output << "\\f"; break;
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

std::uint64_t unix_time_ms() {
    FILETIME file_time{};
    GetSystemTimePreciseAsFileTime(&file_time);
    ULARGE_INTEGER ticks{};
    ticks.LowPart = file_time.dwLowDateTime;
    ticks.HighPart = file_time.dwHighDateTime;
    constexpr std::uint64_t windows_to_unix_epoch_ticks =
        116444736000000000ULL;
    return (ticks.QuadPart - windows_to_unix_epoch_ticks) / 10000ULL;
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

bool parse_bool(std::wstring_view value) {
    if (value == L"true") return true;
    if (value == L"false") return false;
    throw std::invalid_argument("--hardware must be true or false.");
}

DWORD parse_timeout(std::wstring_view value) {
    size_t consumed = 0;
    unsigned long parsed = 0;
    try {
        parsed = std::stoul(std::wstring(value), &consumed, 10);
    } catch (const std::exception&) {
        throw std::invalid_argument("--gpu-timeout-ms must be an integer.");
    }
    if (consumed != value.size() || parsed < 1 || parsed > 30000) {
        throw std::invalid_argument(
            "--gpu-timeout-ms must be between 1 and 30000.");
    }
    return static_cast<DWORD>(parsed);
}

Options parse_options(int argc, wchar_t* argv[]) {
    Options options;
    for (int index = 1; index < argc; index += 2) {
        if (index + 1 >= argc) {
            throw std::invalid_argument(
                "Usage: fluidruntime-d3d12-observation "
                "[--hardware <true|false>] [--gpu-timeout-ms <1..30000>]");
        }

        const std::wstring_view name(argv[index]);
        const std::wstring_view value(argv[index + 1]);
        if (name == L"--hardware") {
            options.use_hardware = parse_bool(value);
        } else if (name == L"--gpu-timeout-ms") {
            options.gpu_timeout_ms = parse_timeout(value);
        } else {
            throw std::invalid_argument(
                "Unknown option '" + wide_to_utf8(name) + "'.");
        }
    }
    return options;
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
        if ((description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) {
            continue;
        }
        if (SUCCEEDED(D3D12CreateDevice(
                candidate.Get(),
                D3D_FEATURE_LEVEL_11_0,
                __uuidof(ID3D12Device),
                nullptr))) {
            return candidate;
        }
    }

    throw std::runtime_error("No hardware adapter with D3D12 feature level 11_0 was found.");
}

AdapterInfo query_adapter(IDXGIAdapter1* adapter) {
    DXGI_ADAPTER_DESC1 description{};
    check_hresult(adapter->GetDesc1(&description), "IDXGIAdapter1::GetDesc1");
    return {
        .description = wide_to_utf8(description.Description),
        .vendor_id = description.VendorId,
        .device_id = description.DeviceId,
        .subsystem_id = description.SubSysId,
        .revision = description.Revision,
        .luid = luid_hex(description.AdapterLuid),
        .dedicated_video_memory_bytes = description.DedicatedVideoMemory,
        .dedicated_system_memory_bytes = description.DedicatedSystemMemory,
        .shared_system_memory_bytes = description.SharedSystemMemory,
    };
}

ArchitectureInfo query_architecture(ID3D12Device* device) {
    ArchitectureInfo result{.node_count = device->GetNodeCount()};
    D3D12_FEATURE_DATA_ARCHITECTURE1 architecture{.NodeIndex = 0};
    if (SUCCEEDED(device->CheckFeatureSupport(
            D3D12_FEATURE_ARCHITECTURE1,
            &architecture,
            sizeof(architecture)))) {
        result.available = true;
        result.tile_based_renderer = architecture.TileBasedRenderer != FALSE;
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
        result.available = true;
        result.tile_based_renderer = fallback.TileBasedRenderer != FALSE;
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

VideoMemorySnapshot query_video_memory(
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
        .current_reservation_bytes = info.CurrentReservation,
        .available_for_reservation_bytes = info.AvailableForReservation,
    };
}

ComPtr<ID3D12Resource> create_buffer(
    ID3D12Device* device,
    D3D12_HEAP_TYPE heap_type,
    D3D12_RESOURCE_STATES initial_state) {
    D3D12_HEAP_PROPERTIES heap{};
    heap.Type = heap_type;
    heap.CPUPageProperty = D3D12_CPU_PAGE_PROPERTY_UNKNOWN;
    heap.MemoryPoolPreference = D3D12_MEMORY_POOL_UNKNOWN;
    heap.CreationNodeMask = 1;
    heap.VisibleNodeMask = 1;

    D3D12_RESOURCE_DESC description{};
    description.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    description.Width = kBufferBytes;
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

void write_memory_snapshot(
    std::ostream& output,
    std::string_view name,
    const VideoMemorySnapshot& snapshot,
    bool trailing_comma) {
    output << "    \"" << name << "\": {\n"
           << "      \"available\": " << json_bool(snapshot.available) << ",\n"
           << "      \"budget_bytes\": " << snapshot.budget_bytes << ",\n"
           << "      \"current_usage_bytes\": " << snapshot.current_usage_bytes << ",\n"
           << "      \"current_reservation_bytes\": "
           << snapshot.current_reservation_bytes << ",\n"
           << "      \"available_for_reservation_bytes\": "
           << snapshot.available_for_reservation_bytes << "\n"
           << "    }" << (trailing_comma ? "," : "") << "\n";
}

int run(const Options& options) {
    LARGE_INTEGER timer_frequency{};
    if (!QueryPerformanceFrequency(&timer_frequency)) {
        throw std::runtime_error("QueryPerformanceFrequency failed.");
    }

    const auto debug_layer_enabled = enable_debug_layer_if_available();
    ComPtr<IDXGIFactory6> factory;
    check_hresult(
        CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)),
        "CreateDXGIFactory2");
    auto adapter = select_adapter(factory.Get(), options.use_hardware);
    const auto adapter_info = query_adapter(adapter.Get());

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

    ComPtr<IDXGIAdapter3> adapter3;
    adapter.As(&adapter3);
    const auto local_before = query_video_memory(
        adapter3.Get(), DXGI_MEMORY_SEGMENT_GROUP_LOCAL);
    const auto non_local_before = query_video_memory(
        adapter3.Get(), DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL);

    D3D12_COMMAND_QUEUE_DESC queue_description{};
    queue_description.Type = D3D12_COMMAND_LIST_TYPE_COPY;
    queue_description.Priority = D3D12_COMMAND_QUEUE_PRIORITY_NORMAL;
    ComPtr<ID3D12CommandQueue> queue;
    check_hresult(
        device->CreateCommandQueue(&queue_description, IID_PPV_ARGS(&queue)),
        "ID3D12Device::CreateCommandQueue");

    UINT64 timestamp_frequency = 0;
    const auto timestamp_frequency_supported =
        SUCCEEDED(queue->GetTimestampFrequency(&timestamp_frequency));
    if (!timestamp_frequency_supported) {
        timestamp_frequency = 0;
    }

    auto upload = create_buffer(
        device.Get(),
        D3D12_HEAP_TYPE_UPLOAD,
        D3D12_RESOURCE_STATE_GENERIC_READ);
    auto gpu_default = create_buffer(
        device.Get(),
        D3D12_HEAP_TYPE_DEFAULT,
        D3D12_RESOURCE_STATE_COMMON);
    auto readback = create_buffer(
        device.Get(),
        D3D12_HEAP_TYPE_READBACK,
        D3D12_RESOURCE_STATE_COPY_DEST);

    std::vector<std::uint8_t> source(static_cast<size_t>(kBufferBytes));
    for (size_t index = 0; index < source.size(); ++index) {
        source[index] = static_cast<std::uint8_t>(
            (index * 131ULL + (index >> 7U) + 17ULL) & 0xffULL);
    }

    void* upload_data = nullptr;
    const D3D12_RANGE no_read{0, 0};
    check_hresult(upload->Map(0, &no_read, &upload_data), "ID3D12Resource::Map(upload)");
    std::memcpy(upload_data, source.data(), source.size());
    const D3D12_RANGE upload_written{0, static_cast<SIZE_T>(kBufferBytes)};
    upload->Unmap(0, &upload_written);

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

    LARGE_INTEGER record_start{};
    LARGE_INTEGER record_end{};
    LARGE_INTEGER submit_start{};
    LARGE_INTEGER completion{};
    QueryPerformanceCounter(&record_start);
    command_list->CopyResource(gpu_default.Get(), upload.Get());
    D3D12_RESOURCE_BARRIER barrier{};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = gpu_default.Get();
    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    command_list->ResourceBarrier(1, &barrier);
    command_list->CopyResource(readback.Get(), gpu_default.Get());
    check_hresult(command_list->Close(), "ID3D12GraphicsCommandList::Close");
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
    const auto completed_fence_value = fence->GetCompletedValue();

    void* readback_data = nullptr;
    const D3D12_RANGE read_range{0, static_cast<SIZE_T>(kBufferBytes)};
    check_hresult(
        readback->Map(0, &read_range, &readback_data),
        "ID3D12Resource::Map(readback)");
    const auto content_equivalent =
        std::memcmp(source.data(), readback_data, source.size()) == 0;
    const auto source_hash = fnv1a64(source.data(), source.size());
    const auto readback_hash = fnv1a64(
        static_cast<const std::uint8_t*>(readback_data), source.size());
    readback->Unmap(0, &no_read);
    if (!content_equivalent || source_hash != readback_hash) {
        throw std::runtime_error("D3D12 round-trip content verification failed.");
    }

    const auto local_after = query_video_memory(
        adapter3.Get(), DXGI_MEMORY_SEGMENT_GROUP_LOCAL);
    const auto non_local_after = query_video_memory(
        adapter3.Get(), DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL);
    const auto debug_messages = inspect_debug_messages(debug_info_queue.Get());
    if (debug_messages.warnings != 0 || debug_messages.errors != 0) {
        throw std::runtime_error(
            "The D3D12 debug layer reported a warning, error, or corruption message.");
    }

    std::cout << std::fixed << std::setprecision(3)
              << "{\n"
              << "  \"mode\": \"fluidruntime-owned-d3d12-observation-v0.1.0\",\n"
              << "  \"target_owned\": true,\n"
              << "  \"cooperative_load\": true,\n"
              << "  \"remote_injection\": false,\n"
              << "  \"read_only_observation\": true,\n"
              << "  \"actuation_enabled\": false,\n"
              << "  \"physical_transfer_bytes_measured\": false,\n"
              << "  \"debug_layer_enabled\": "
              << json_bool(debug_layer_enabled) << ",\n"
              << "  \"debug_message_validation_available\": "
              << json_bool(debug_info_queue != nullptr) << ",\n"
              << "  \"debug_warning_count\": " << debug_messages.warnings << ",\n"
              << "  \"debug_error_count\": " << debug_messages.errors << ",\n"
              << "  \"render_driver\": \""
              << (options.use_hardware ? "hardware" : "warp") << "\",\n"
              << "  \"process_id\": " << GetCurrentProcessId() << ",\n"
              << "  \"captured_at_unix_ms\": " << unix_time_ms() << ",\n"
              << "  \"adapter\": {\n"
              << "    \"description\": \""
              << json_escape(adapter_info.description) << "\",\n"
              << "    \"vendor_id\": " << adapter_info.vendor_id << ",\n"
              << "    \"device_id\": " << adapter_info.device_id << ",\n"
              << "    \"subsystem_id\": " << adapter_info.subsystem_id << ",\n"
              << "    \"revision\": " << adapter_info.revision << ",\n"
              << "    \"luid\": \"" << adapter_info.luid << "\",\n"
              << "    \"dedicated_video_memory_bytes\": "
              << adapter_info.dedicated_video_memory_bytes << ",\n"
              << "    \"dedicated_system_memory_bytes\": "
              << adapter_info.dedicated_system_memory_bytes << ",\n"
              << "    \"shared_system_memory_bytes\": "
              << adapter_info.shared_system_memory_bytes << "\n"
              << "  },\n"
              << "  \"architecture\": {\n"
              << "    \"available\": " << json_bool(architecture.available) << ",\n"
              << "    \"node_count\": " << architecture.node_count << ",\n"
              << "    \"tile_based_renderer\": "
              << json_bool(architecture.tile_based_renderer) << ",\n"
              << "    \"uma\": " << json_bool(architecture.uma) << ",\n"
              << "    \"cache_coherent_uma\": "
              << json_bool(architecture.cache_coherent_uma) << ",\n"
              << "    \"resource_heap_tier\": "
              << architecture.resource_heap_tier << "\n"
              << "  },\n"
              << "  \"queue\": {\n"
              << "    \"type\": \"copy\",\n"
              << "    \"priority\": \"normal\",\n"
              << "    \"timestamp_frequency_supported\": "
              << json_bool(timestamp_frequency_supported) << ",\n"
              << "    \"timestamp_frequency_hz\": " << timestamp_frequency << "\n"
              << "  },\n"
              << "  \"transfer\": {\n"
              << "    \"buffer_bytes\": " << kBufferBytes << ",\n"
              << "    \"logical_upload_bytes\": " << kBufferBytes << ",\n"
              << "    \"logical_readback_bytes\": " << kBufferBytes << ",\n"
              << "    \"logical_total_copy_bytes\": " << (2 * kBufferBytes) << ",\n"
              << "    \"upload_heap_type\": \"upload\",\n"
              << "    \"default_heap_type\": \"default\",\n"
              << "    \"readback_heap_type\": \"readback\",\n"
              << "    \"upload_initial_state\": \"generic-read\",\n"
              << "    \"default_initial_state\": \"common\",\n"
              << "    \"default_first_access_promotion\": \"copy-dest\",\n"
              << "    \"default_state_before_readback_copy\": \"copy-source\",\n"
              << "    \"expected_default_post_execute_state\": "
              << "\"common-via-buffer-decay\",\n"
              << "    \"readback_initial_state\": \"copy-dest\",\n"
              << "    \"command_list_type\": \"copy\",\n"
              << "    \"command_list_count\": 1,\n"
              << "    \"copy_command_count\": 2,\n"
              << "    \"resource_barrier_count\": 1,\n"
              << "    \"submitted_command_list_count\": 1,\n"
              << "    \"fence_signaled_value\": " << kFenceValue << ",\n"
              << "    \"fence_completed_value\": " << completed_fence_value << ",\n"
              << "    \"wait_completed\": true,\n"
              << "    \"hash_algorithm\": \"fnv1a64\",\n"
              << "    \"source_hash\": \"" << uint64_hex(source_hash) << "\",\n"
              << "    \"readback_hash\": \"" << uint64_hex(readback_hash) << "\",\n"
              << "    \"content_equivalent\": true,\n"
              << "    \"cpu_record_microseconds\": "
              << elapsed_microseconds(record_start, record_end, timer_frequency) << ",\n"
              << "    \"submit_to_fence_microseconds\": "
              << elapsed_microseconds(submit_start, completion, timer_frequency) << ",\n"
              << "    \"total_workload_microseconds\": "
              << elapsed_microseconds(record_start, completion, timer_frequency) << "\n"
              << "  },\n"
              << "  \"memory\": {\n"
              << "    \"source\": \"idxgiadapter3-query-video-memory-info\",\n";
    write_memory_snapshot(std::cout, "local_before", local_before, true);
    write_memory_snapshot(std::cout, "local_after", local_after, true);
    write_memory_snapshot(std::cout, "non_local_before", non_local_before, true);
    write_memory_snapshot(std::cout, "non_local_after", non_local_after, false);
    std::cout << "  },\n"
              << "  \"claim_scope\": "
              << "\"owned-d3d12-upload-default-readback-observation-only\",\n"
              << "  \"limitations\": [\n"
              << "    \"DXGI budgets and usage are snapshots, not physical transfer counters.\",\n"
              << "    \"Logical bytes describe commands issued by this owned workload only.\",\n"
              << "    \"This probe does not hook, inject, schedule, or alter external applications.\"\n"
              << "  ]\n"
              << "}\n";
    return 0;
}

} // namespace

int wmain(int argc, wchar_t* argv[]) {
    try {
        return run(parse_options(argc, argv));
    } catch (const std::invalid_argument& exception) {
        std::cerr << "D3D12 observation input error: " << exception.what() << '\n';
        return 2;
    } catch (const std::exception& exception) {
        std::cerr << "D3D12 observation failed: " << exception.what() << '\n';
        return 1;
    }
}
