#include "fluidruntime_d3d12_hook_api.h"

#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

#include <array>
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
constexpr UINT64 kUploadBytes = 2ULL * kBufferBytes;
constexpr UINT64 kFenceValue = 1;
constexpr std::uint64_t kQueueId = 401;
constexpr std::array<std::uint64_t, 2> kScopeIds{301, 302};
constexpr std::array<std::uint64_t, 2> kSourceIds{101, 102};
constexpr std::array<std::uint64_t, 2> kDestinationIds{201, 202};
constexpr std::uint64_t kFenceId = 501;
constexpr size_t kCloseVtableIndex = 9;
constexpr size_t kResetVtableIndex = 10;
constexpr size_t kCopyBufferRegionVtableIndex = 15;
constexpr size_t kCopyTextureRegionVtableIndex = 16;
constexpr size_t kCopyResourceVtableIndex = 17;
constexpr size_t kExecuteCommandListsVtableIndex = 10;
constexpr size_t kSignalVtableIndex = 14;
constexpr std::uint64_t kFnvOffsetBasis = 14695981039346656037ULL;
constexpr std::uint64_t kFnvPrime = 1099511628211ULL;

struct Options {
    std::wstring hook_path;
    bool use_hardware{};
    bool managed_control{};
    bool self_publish_control{};
    bool invalid_topology{};
    bool incomplete_registration{};
    bool aliased_destination{};
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
        default: output << character; break;
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

std::uint64_t append_scope_hash(std::uint64_t hash, std::uint64_t scope_id) {
    for (unsigned int shift = 0; shift < 64; shift += 8) {
        hash ^= (scope_id >> shift) & 0xff;
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
        throw std::invalid_argument(wide_to_utf8(option) + " must be an integer.");
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
        if (argument == L"--invalid-topology") {
            options.invalid_topology = true;
            continue;
        }
        if (argument == L"--incomplete-registration") {
            options.incomplete_registration = true;
            continue;
        }
        if (argument == L"--aliased-destination") {
            options.aliased_destination = true;
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
            options.control_timeout_ms = parse_unsigned(value, argument, 1, 5000);
        } else if (argument == L"--gpu-timeout-ms") {
            options.gpu_timeout_ms = parse_unsigned(value, argument, 1, 30000);
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
    if ((options.invalid_topology || options.incomplete_registration ||
         options.aliased_destination) &&
        options.managed_control) {
        throw std::invalid_argument(
            "Negative contract checks cannot be combined with managed control.");
    }
    const auto negative_check_count =
        static_cast<unsigned int>(options.invalid_topology) +
        static_cast<unsigned int>(options.incomplete_registration) +
        static_cast<unsigned int>(options.aliased_destination);
    if (negative_check_count > 1) {
        throw std::invalid_argument(
            "Only one negative contract check may be requested.");
    }
    return options;
}

void publish_self_test_policy(unsigned long action_budget) {
    const auto mapping_name = std::wstring(fluid_transfer_ring_name_prefix) +
        std::to_wstring(static_cast<std::uint32_t>(FluidTransferBackendV1::d3d12)) +
        L"-" + std::to_wstring(GetCurrentProcessId());
    const auto mapping = OpenFileMappingW(
        FILE_MAP_ALL_ACCESS,
        FALSE,
        mapping_name.c_str());
    if (mapping == nullptr) {
        throw std::runtime_error("Unable to open the D3D12 transfer mapping.");
    }
    auto* view = static_cast<std::uint8_t*>(MapViewOfFile(
        mapping,
        FILE_MAP_ALL_ACCESS,
        0,
        0,
        static_cast<SIZE_T>(fluid_hook_ring_mapping_size)));
    if (view == nullptr) {
        CloseHandle(mapping);
        throw std::runtime_error("Unable to map the D3D12 transfer control block.");
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
        throw std::runtime_error("The D3D12 transfer mapping has the wrong ABI.");
    }
    LARGE_INTEGER now{};
    QueryPerformanceCounter(&now);
    InterlockedExchange64(
        &control->expires_at_qpc,
        now.QuadPart + static_cast<LONG64>(header->qpc_frequency * 3));
    InterlockedExchange64(
        &control->action_mask,
        static_cast<LONG64>(
            fluid_hook_control_action_skip_redundant_transfer_buffer_copy));
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
        } else if (message->Severity == D3D12_MESSAGE_SEVERITY_ERROR ||
                   message->Severity == D3D12_MESSAGE_SEVERITY_CORRUPTION) {
            ++counts.errors;
            std::cerr << "D3D12 debug error: " << message->pDescription << '\n';
        }
    }
    return counts;
}

ComPtr<IDXGIAdapter1> select_adapter(IDXGIFactory6* factory, bool use_hardware) {
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

FluidTransferTopologyV1 make_topology(unsigned long candidate_count) {
    return {
        .struct_size = sizeof(FluidTransferTopologyV1),
        .abi_version = fluid_transfer_contract_abi_version,
        .backend = static_cast<std::uint32_t>(FluidTransferBackendV1::d3d12),
        .operation = static_cast<std::uint32_t>(FluidTransferOperationV1::copy_buffer),
        .queue_count = 1,
        .execution_scope_count = 2,
        .source_resource_count = 2,
        .destination_resource_count = 2,
        .lane_count = 2,
        .fence_count = 1,
        .max_action_count = candidate_count,
        .expected_runtime_event_count = candidate_count + 17,
        .max_resource_bytes = kBufferBytes,
        .max_total_retained_bytes = 2 * kBufferBytes,
    };
}

int run(const Options& options) {
    LARGE_INTEGER qpc_frequency{};
    if (!QueryPerformanceFrequency(&qpc_frequency)) {
        throw std::runtime_error("QueryPerformanceFrequency failed.");
    }
    const auto debug_layer_enabled = enable_debug_layer_if_available();
    ComPtr<IDXGIFactory6> factory;
    check_hresult(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)), "CreateDXGIFactory2");
    auto adapter = select_adapter(factory.Get(), options.use_hardware);
    const auto adapter_info = query_adapter(adapter.Get());
    ComPtr<ID3D12Device> device;
    check_hresult(
        D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device)),
        "D3D12CreateDevice");
    const auto architecture = query_architecture(device.Get());
    ComPtr<ID3D12InfoQueue> debug_info_queue;
    if (debug_layer_enabled) {
        check_hresult(device.As(&debug_info_queue), "ID3D12InfoQueue");
    }

    D3D12_COMMAND_QUEUE_DESC queue_description{};
    queue_description.Type = D3D12_COMMAND_LIST_TYPE_COPY;
    queue_description.Priority = D3D12_COMMAND_QUEUE_PRIORITY_NORMAL;
    ComPtr<ID3D12CommandQueue> queue;
    check_hresult(
        device->CreateCommandQueue(&queue_description, IID_PPV_ARGS(&queue)),
        "ID3D12Device::CreateCommandQueue");

    std::array<ComPtr<ID3D12CommandAllocator>, 2> allocators;
    std::array<ComPtr<ID3D12GraphicsCommandList>, 2> command_lists;
    for (size_t lane = 0; lane < command_lists.size(); ++lane) {
        check_hresult(
            device->CreateCommandAllocator(
                D3D12_COMMAND_LIST_TYPE_COPY,
                IID_PPV_ARGS(&allocators[lane])),
            "ID3D12Device::CreateCommandAllocator");
        check_hresult(
            device->CreateCommandList(
                0,
                D3D12_COMMAND_LIST_TYPE_COPY,
                allocators[lane].Get(),
                nullptr,
                IID_PPV_ARGS(&command_lists[lane])),
            "ID3D12Device::CreateCommandList");
    }

    std::array<ComPtr<ID3D12Resource>, 2> uploads;
    std::array<ComPtr<ID3D12Resource>, 2> destinations;
    std::array<ComPtr<ID3D12Resource>, 2> readbacks;
    for (size_t lane = 0; lane < uploads.size(); ++lane) {
        uploads[lane] = create_buffer(
            device.Get(),
            kUploadBytes,
            D3D12_HEAP_TYPE_UPLOAD,
            D3D12_RESOURCE_STATE_GENERIC_READ);
        destinations[lane] = create_buffer(
            device.Get(),
            kBufferBytes,
            D3D12_HEAP_TYPE_DEFAULT,
            D3D12_RESOURCE_STATE_COMMON);
        readbacks[lane] = create_buffer(
            device.Get(),
            kBufferBytes,
            D3D12_HEAP_TYPE_READBACK,
            D3D12_RESOURCE_STATE_COPY_DEST);
    }

    ComPtr<ID3D12Fence> fence;
    check_hresult(
        device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence)),
        "ID3D12Device::CreateFence");

    UINT64 timestamp_frequency = 0;
    const auto timestamp_supported =
        SUCCEEDED(queue->GetTimestampFrequency(&timestamp_frequency)) &&
        timestamp_frequency != 0;
    ComPtr<ID3D12QueryHeap> timestamp_heap;
    ComPtr<ID3D12Resource> timestamp_readback;
    if (timestamp_supported) {
        D3D12_QUERY_HEAP_DESC query_description{};
        query_description.Type = D3D12_QUERY_HEAP_TYPE_COPY_QUEUE_TIMESTAMP;
        query_description.Count = 4;
        check_hresult(
            device->CreateQueryHeap(&query_description, IID_PPV_ARGS(&timestamp_heap)),
            "ID3D12Device::CreateQueryHeap");
        timestamp_readback = create_buffer(
            device.Get(),
            4 * sizeof(UINT64),
            D3D12_HEAP_TYPE_READBACK,
            D3D12_RESOURCE_STATE_COPY_DEST);
    }

    std::array<std::vector<std::uint8_t>, 4> patterns;
    constexpr std::array<std::uint64_t, 4> seeds{17, 91, 43, 157};
    std::array<std::uint64_t, 4> pattern_hashes{};
    for (size_t index = 0; index < patterns.size(); ++index) {
        patterns[index].resize(static_cast<size_t>(kBufferBytes));
        fill_pattern(patterns[index], seeds[index]);
        pattern_hashes[index] = fnv1a64(patterns[index].data(), patterns[index].size());
    }
    if (patterns[0] == patterns[1] || patterns[2] == patterns[3]) {
        throw std::runtime_error("D3D12 transfer patterns are not distinct.");
    }
    std::array<std::vector<std::uint8_t>, 2> upload_shadows;
    const D3D12_RANGE no_read{0, 0};
    for (size_t lane = 0; lane < uploads.size(); ++lane) {
        upload_shadows[lane].resize(static_cast<size_t>(kUploadBytes));
        std::memcpy(
            upload_shadows[lane].data(),
            patterns[lane * 2].data(),
            static_cast<size_t>(kBufferBytes));
        std::memcpy(
            upload_shadows[lane].data() + kBufferBytes,
            patterns[lane * 2 + 1].data(),
            static_cast<size_t>(kBufferBytes));
        void* mapping = nullptr;
        check_hresult(uploads[lane]->Map(0, &no_read, &mapping), "upload Map");
        std::memcpy(mapping, upload_shadows[lane].data(), upload_shadows[lane].size());
    }

    auto** command_vtable = *reinterpret_cast<void***>(command_lists[0].Get());
    auto** second_command_vtable = *reinterpret_cast<void***>(command_lists[1].Get());
    auto** queue_vtable = *reinterpret_cast<void***>(queue.Get());
    if (command_vtable != second_command_vtable) {
        throw std::runtime_error("Owned command lists do not share a hookable vtable.");
    }
    const std::array<void*, 7> original_pointers{
        command_vtable[kCloseVtableIndex],
        command_vtable[kResetVtableIndex],
        command_vtable[kCopyBufferRegionVtableIndex],
        command_vtable[kCopyTextureRegionVtableIndex],
        command_vtable[kCopyResourceVtableIndex],
        queue_vtable[kExecuteCommandListsVtableIndex],
        queue_vtable[kSignalVtableIndex],
    };

    const auto hook_module = LoadLibraryW(options.hook_path.c_str());
    if (hook_module == nullptr) {
        throw std::runtime_error("Unable to load the D3D12 transfer hook DLL.");
    }
    const auto attach = reinterpret_cast<FluidD3D12HookAttachV2Function>(
        GetProcAddress(hook_module, "FluidD3D12HookAttachV2"));
    const auto register_upload =
        reinterpret_cast<FluidD3D12HookRegisterUploadBufferV2Function>(
            GetProcAddress(hook_module, "FluidD3D12HookRegisterUploadBufferV2"));
    const auto register_destination =
        reinterpret_cast<FluidD3D12HookRegisterCopyOnlyBufferV2Function>(
            GetProcAddress(hook_module, "FluidD3D12HookRegisterCopyOnlyBufferV2"));
    const auto register_lane =
        reinterpret_cast<FluidD3D12HookRegisterCopyLaneV2Function>(
            GetProcAddress(hook_module, "FluidD3D12HookRegisterCopyLaneV2"));
    const auto register_fence =
        reinterpret_cast<FluidD3D12HookRegisterFenceV2Function>(
            GetProcAddress(hook_module, "FluidD3D12HookRegisterFenceV2"));
    const auto invalidate =
        reinterpret_cast<FluidD3D12HookInvalidateResourceV2Function>(
            GetProcAddress(hook_module, "FluidD3D12HookInvalidateResourceV2"));
    const auto wait_for_policy =
        reinterpret_cast<FluidD3D12HookWaitForControlPolicyFunction>(
            GetProcAddress(hook_module, "FluidD3D12HookWaitForControlPolicy"));
    const auto read_snapshot =
        reinterpret_cast<FluidD3D12HookReadSnapshotV2Function>(
            GetProcAddress(hook_module, "FluidD3D12HookReadSnapshotV2"));
    const auto detach = reinterpret_cast<FluidD3D12HookDetachFunction>(
        GetProcAddress(hook_module, "FluidD3D12HookDetach"));
    if (attach == nullptr || register_upload == nullptr ||
        register_destination == nullptr || register_lane == nullptr ||
        register_fence == nullptr || invalidate == nullptr ||
        wait_for_policy == nullptr || read_snapshot == nullptr || detach == nullptr) {
        throw std::runtime_error("D3D12 transfer hook DLL omitted a required export.");
    }

    const std::array<FluidD3D12CommandScopeV2, 2> scopes{
        FluidD3D12CommandScopeV2{command_lists[0].Get(), kScopeIds[0]},
        FluidD3D12CommandScopeV2{command_lists[1].Get(), kScopeIds[1]},
    };
    auto topology = make_topology(options.candidate_count);
    if (options.invalid_topology) {
        topology.queue_count = 2;
    }
    FluidD3D12HookAttachOptionsV2 attach_options{
        .struct_size = sizeof(FluidD3D12HookAttachOptionsV2),
        .abi_version = fluid_d3d12_hook_attach_options_v2_abi_version,
        .flags = (options.managed_control || options.incomplete_registration)
            ? fluid_d3d12_hook_attach_flag_allow_control_policy
            : 0,
        .topology = topology,
        .queue_id = kQueueId,
    };
    const auto attach_result = attach(
        queue.Get(),
        scopes.data(),
        static_cast<std::uint32_t>(scopes.size()),
        &attach_options);
    if (options.invalid_topology) {
        const auto untouched =
            command_vtable[kCloseVtableIndex] == original_pointers[0] &&
            command_vtable[kResetVtableIndex] == original_pointers[1] &&
            command_vtable[kCopyBufferRegionVtableIndex] == original_pointers[2] &&
            command_vtable[kCopyTextureRegionVtableIndex] == original_pointers[3] &&
            command_vtable[kCopyResourceVtableIndex] == original_pointers[4] &&
            queue_vtable[kExecuteCommandListsVtableIndex] == original_pointers[5] &&
            queue_vtable[kSignalVtableIndex] == original_pointers[6];
        if (attach_result != E_INVALIDARG || !untouched) {
            throw std::runtime_error("Invalid topology was not rejected before patching.");
        }
        std::cout << "{\n"
                  << "  \"mode\": \"fluidruntime-owned-d3d12-transfer-invalid-topology-v0.21.0\",\n"
                  << "  \"attach_hresult\": \"" << hresult_hex(attach_result) << "\",\n"
                  << "  \"fail_closed\": true,\n"
                  << "  \"vtable_untouched\": true\n"
                  << "}\n";
        return 0;
    }
    check_hresult(attach_result, "FluidD3D12HookAttachV2");

    for (size_t lane = 0; lane < uploads.size(); ++lane) {
        check_hresult(
            register_upload(
                uploads[lane].Get(),
                kSourceIds[lane],
                upload_shadows[lane].data(),
                kUploadBytes),
            "FluidD3D12HookRegisterUploadBufferV2");
        const D3D12_RANGE written{0, static_cast<SIZE_T>(kUploadBytes)};
        uploads[lane]->Unmap(0, &written);
        check_hresult(
            register_destination(
                destinations[lane].Get(),
                kDestinationIds[lane],
                kBufferBytes),
            "FluidD3D12HookRegisterCopyOnlyBufferV2");
    }

    if (options.aliased_destination) {
        check_hresult(
            register_lane(kScopeIds[0], kDestinationIds[0]),
            "FluidD3D12HookRegisterCopyLaneV2(first owner)");
        const auto rejected_alias = register_lane(kScopeIds[1], kDestinationIds[0]);
        FluidD3D12HookSnapshotV2 rejected_snapshot{
            .struct_size = sizeof(FluidD3D12HookSnapshotV2),
            .abi_version = fluid_d3d12_hook_snapshot_v2_abi_version,
        };
        check_hresult(
            read_snapshot(&rejected_snapshot),
            "FluidD3D12HookReadSnapshotV2(aliased destination)");
        const auto detach_result = detach();
        check_hresult(detach_result, "FluidD3D12HookDetach(aliased destination)");
        const auto pointers_restored =
            command_vtable[kCloseVtableIndex] == original_pointers[0] &&
            command_vtable[kResetVtableIndex] == original_pointers[1] &&
            command_vtable[kCopyBufferRegionVtableIndex] == original_pointers[2] &&
            command_vtable[kCopyTextureRegionVtableIndex] == original_pointers[3] &&
            command_vtable[kCopyResourceVtableIndex] == original_pointers[4] &&
            queue_vtable[kExecuteCommandListsVtableIndex] == original_pointers[5] &&
            queue_vtable[kSignalVtableIndex] == original_pointers[6];
        if (rejected_alias != HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS) ||
            rejected_snapshot.lane_count != 1 ||
            rejected_snapshot.skipped_copy_count != 0 ||
            !pointers_restored) {
            throw std::runtime_error(
                "Aliased destination ownership was not rejected safely.");
        }
        std::cout << "{\n"
                  << "  \"mode\": \"fluidruntime-owned-d3d12-transfer-aliased-destination-v0.21.0\",\n"
                  << "  \"register_hresult\": \""
                  << hresult_hex(rejected_alias) << "\",\n"
                  << "  \"fail_closed\": true,\n"
                  << "  \"registered_lane_count\": 1,\n"
                  << "  \"skipped_copy_count\": 0,\n"
                  << "  \"vtable_restored\": true\n"
                  << "}\n";
        return 0;
    }

    for (size_t lane = 0; lane < destinations.size(); ++lane) {
        check_hresult(
            register_lane(kScopeIds[lane], kDestinationIds[lane]),
            "FluidD3D12HookRegisterCopyLaneV2");
    }
    if (!options.incomplete_registration) {
        check_hresult(
            register_fence(fence.Get(), kFenceId),
            "FluidD3D12HookRegisterFenceV2");
    }

    if (options.incomplete_registration) {
        publish_self_test_policy(options.candidate_count);
        const auto rejected = wait_for_policy(options.control_timeout_ms);
        FluidD3D12HookSnapshotV2 rejected_snapshot{
            .struct_size = sizeof(FluidD3D12HookSnapshotV2),
            .abi_version = fluid_d3d12_hook_snapshot_v2_abi_version,
        };
        check_hresult(
            read_snapshot(&rejected_snapshot),
            "FluidD3D12HookReadSnapshotV2(rejected)");
        const auto detach_result = detach();
        check_hresult(detach_result, "FluidD3D12HookDetach(rejected)");
        const auto pointers_restored =
            command_vtable[kCloseVtableIndex] == original_pointers[0] &&
            command_vtable[kResetVtableIndex] == original_pointers[1] &&
            command_vtable[kCopyBufferRegionVtableIndex] == original_pointers[2] &&
            command_vtable[kCopyTextureRegionVtableIndex] == original_pointers[3] &&
            command_vtable[kCopyResourceVtableIndex] == original_pointers[4] &&
            queue_vtable[kExecuteCommandListsVtableIndex] == original_pointers[5] &&
            queue_vtable[kSignalVtableIndex] == original_pointers[6];
        if (rejected != E_INVALIDARG ||
            rejected_snapshot.control_policy_rejected_count != 1 ||
            rejected_snapshot.control_policy_status !=
                static_cast<std::uint64_t>(FluidHookControlStatusV1::rejected) ||
            rejected_snapshot.fence_count != 0 ||
            rejected_snapshot.skipped_copy_count != 0 ||
            !pointers_restored) {
            throw std::runtime_error(
                "Incomplete registration did not fail closed before actuation.");
        }
        std::cout << "{\n"
                  << "  \"mode\": \"fluidruntime-owned-d3d12-transfer-incomplete-registration-v0.21.0\",\n"
                  << "  \"wait_hresult\": \"" << hresult_hex(rejected) << "\",\n"
                  << "  \"fail_closed\": true,\n"
                  << "  \"rejected_policy_count\": 1,\n"
                  << "  \"skipped_copy_count\": 0,\n"
                  << "  \"vtable_restored\": true\n"
                  << "}\n";
        return 0;
    }

    Sleep(options.hold_ms);
    if (options.self_publish_control) {
        publish_self_test_policy(options.candidate_count);
    }
    const auto control_wait_result = options.managed_control
        ? wait_for_policy(options.control_timeout_ms)
        : S_FALSE;
    if (options.managed_control) {
        check_hresult(control_wait_result, "FluidD3D12HookWaitForControlPolicy");
    }

    const std::array<unsigned long, 2> lane_candidate_counts{
        options.candidate_count / 2,
        options.candidate_count - options.candidate_count / 2,
    };
    LARGE_INTEGER record_start{};
    LARGE_INTEGER record_end{};
    LARGE_INTEGER submit_start{};
    LARGE_INTEGER completion{};
    QueryPerformanceCounter(&record_start);
    for (size_t lane = 0; lane < command_lists.size(); ++lane) {
        auto* command_list = command_lists[lane].Get();
        if (timestamp_supported) {
            command_list->EndQuery(
                timestamp_heap.Get(),
                D3D12_QUERY_TYPE_TIMESTAMP,
                static_cast<UINT>(lane * 2));
        }
        command_list->CopyBufferRegion(
            destinations[lane].Get(), 0, uploads[lane].Get(), 0, kBufferBytes);
        const auto first_group = lane_candidate_counts[lane] / 2;
        const auto second_total = lane_candidate_counts[lane] - first_group;
        const auto before_automatic_invalidation = second_total / 2;
        const auto after_explicit_invalidation =
            second_total - before_automatic_invalidation;
        for (unsigned long index = 0; index < first_group; ++index) {
            command_list->CopyBufferRegion(
                destinations[lane].Get(), 0, uploads[lane].Get(), 0, kBufferBytes);
        }
        command_list->CopyBufferRegion(
            destinations[lane].Get(),
            0,
            uploads[lane].Get(),
            kBufferBytes,
            kBufferBytes);
        for (unsigned long index = 0;
             index < before_automatic_invalidation;
             ++index) {
            command_list->CopyBufferRegion(
                destinations[lane].Get(),
                0,
                uploads[lane].Get(),
                kBufferBytes,
                kBufferBytes);
        }
        command_list->CopyBufferRegion(
            destinations[lane].Get(), 0, uploads[lane].Get(), kBufferBytes, 1);
        command_list->CopyBufferRegion(
            destinations[lane].Get(),
            0,
            uploads[lane].Get(),
            kBufferBytes,
            kBufferBytes);
        check_hresult(
            invalidate(kDestinationIds[lane]),
            "FluidD3D12HookInvalidateResourceV2");
        command_list->CopyBufferRegion(
            destinations[lane].Get(),
            0,
            uploads[lane].Get(),
            kBufferBytes,
            kBufferBytes);
        for (unsigned long index = 0; index < after_explicit_invalidation; ++index) {
            command_list->CopyBufferRegion(
                destinations[lane].Get(),
                0,
                uploads[lane].Get(),
                kBufferBytes,
                kBufferBytes);
        }
        if (timestamp_supported) {
            command_list->EndQuery(
                timestamp_heap.Get(),
                D3D12_QUERY_TYPE_TIMESTAMP,
                static_cast<UINT>(lane * 2 + 1));
            command_list->ResolveQueryData(
                timestamp_heap.Get(),
                D3D12_QUERY_TYPE_TIMESTAMP,
                static_cast<UINT>(lane * 2),
                2,
                timestamp_readback.Get(),
                lane * 2 * sizeof(UINT64));
        }
        D3D12_RESOURCE_BARRIER barrier{};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barrier.Transition.pResource = destinations[lane].Get();
        barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
        barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
        barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        command_list->ResourceBarrier(1, &barrier);
        command_list->CopyBufferRegion(
            readbacks[lane].Get(), 0, destinations[lane].Get(), 0, kBufferBytes);
        check_hresult(command_list->Close(), "ID3D12GraphicsCommandList::Close");
    }
    QueryPerformanceCounter(&record_end);

    UniqueHandle fence_event(CreateEventW(nullptr, FALSE, FALSE, nullptr));
    if (fence_event.get() == nullptr) {
        throw std::runtime_error("CreateEventW failed.");
    }
    QueryPerformanceCounter(&submit_start);
    std::array<ID3D12CommandList*, 2> submitted{
        command_lists[0].Get(),
        command_lists[1].Get(),
    };
    queue->ExecuteCommandLists(
        static_cast<UINT>(submitted.size()),
        submitted.data());
    check_hresult(queue->Signal(fence.Get(), kFenceValue), "ID3D12CommandQueue::Signal");
    if (fence->GetCompletedValue() < kFenceValue) {
        check_hresult(
            fence->SetEventOnCompletion(kFenceValue, fence_event.get()),
            "ID3D12Fence::SetEventOnCompletion");
        const auto wait_result = WaitForSingleObject(fence_event.get(), options.gpu_timeout_ms);
        if (wait_result == WAIT_TIMEOUT) {
            throw std::runtime_error("D3D12 fence wait timed out.");
        }
        if (wait_result != WAIT_OBJECT_0) {
            throw std::runtime_error("D3D12 fence wait failed.");
        }
    }
    QueryPerformanceCounter(&completion);

    std::array<std::uint64_t, 2> final_hashes{};
    bool content_equivalent = true;
    const D3D12_RANGE full_read{0, static_cast<SIZE_T>(kBufferBytes)};
    for (size_t lane = 0; lane < readbacks.size(); ++lane) {
        void* mapping = nullptr;
        check_hresult(readbacks[lane]->Map(0, &full_read, &mapping), "readback Map");
        final_hashes[lane] = fnv1a64(
            static_cast<const std::uint8_t*>(mapping),
            static_cast<size_t>(kBufferBytes));
        content_equivalent = content_equivalent &&
            std::memcmp(
                mapping,
                patterns[lane * 2 + 1].data(),
                static_cast<size_t>(kBufferBytes)) == 0 &&
            final_hashes[lane] == pattern_hashes[lane * 2 + 1];
        readbacks[lane]->Unmap(0, &no_read);
    }
    if (!content_equivalent) {
        throw std::runtime_error("D3D12 multi-lane content verification failed.");
    }

    std::array<UINT64, 4> timestamps{};
    bool gpu_timing_valid = false;
    UINT64 gpu_ticks = 0;
    if (timestamp_supported) {
        void* mapping = nullptr;
        const D3D12_RANGE timestamp_read{0, sizeof(timestamps)};
        check_hresult(timestamp_readback->Map(0, &timestamp_read, &mapping), "timestamp Map");
        std::memcpy(timestamps.data(), mapping, sizeof(timestamps));
        timestamp_readback->Unmap(0, &no_read);
        gpu_timing_valid =
            timestamps[1] >= timestamps[0] && timestamps[3] >= timestamps[2];
        if (gpu_timing_valid) {
            gpu_ticks = (timestamps[1] - timestamps[0]) +
                (timestamps[3] - timestamps[2]);
        }
    }

    const auto immutable_sources_verified =
        fnv1a64(upload_shadows[0].data(), kBufferBytes) == pattern_hashes[0] &&
        fnv1a64(upload_shadows[0].data() + kBufferBytes, kBufferBytes) ==
            pattern_hashes[1] &&
        fnv1a64(upload_shadows[1].data(), kBufferBytes) == pattern_hashes[2] &&
        fnv1a64(upload_shadows[1].data() + kBufferBytes, kBufferBytes) ==
            pattern_hashes[3];
    if (!immutable_sources_verified) {
        throw std::runtime_error("A registered immutable upload shadow changed.");
    }

    FluidD3D12HookSnapshotV2 snapshot{
        .struct_size = sizeof(FluidD3D12HookSnapshotV2),
        .abi_version = fluid_d3d12_hook_snapshot_v2_abi_version,
    };
    check_hresult(read_snapshot(&snapshot), "FluidD3D12HookReadSnapshotV2");
    const auto expected_tracked =
        static_cast<std::uint64_t>(options.candidate_count) + 8;
    const auto expected_skipped = options.managed_control
        ? static_cast<std::uint64_t>(options.candidate_count)
        : 0;
    const auto expected_forwarded = expected_tracked - expected_skipped;
    const auto expected_status = options.managed_control
        ? static_cast<std::uint64_t>(FluidHookControlStatusV1::exhausted)
        : static_cast<std::uint64_t>(FluidHookControlStatusV1::none);
    auto expected_scope_hash = kFnvOffsetBasis;
    expected_scope_hash = append_scope_hash(expected_scope_hash, kScopeIds[0]);
    expected_scope_hash = append_scope_hash(expected_scope_hash, kScopeIds[1]);
    const auto expected_event_count =
        static_cast<std::uint64_t>(options.candidate_count) +
        (options.managed_control ? 17 : 16);
    const auto metrics_valid =
        snapshot.attached == 1 && snapshot.queue_id == kQueueId &&
        snapshot.execution_scope_count == 2 &&
        snapshot.source_resource_count == 2 &&
        snapshot.destination_resource_count == 2 && snapshot.lane_count == 2 &&
        snapshot.fence_count == 1 && snapshot.source_snapshot_bytes == 2 * kUploadBytes &&
        snapshot.retained_capacity_bytes == 2 * kBufferBytes &&
        snapshot.valid_lane_count == 0 && snapshot.maximum_lane_generation == 7 &&
        snapshot.copy_buffer_region_count == options.candidate_count + 12 &&
        snapshot.tracked_copy_count == expected_tracked &&
        snapshot.tracked_copy_bytes == expected_tracked * kBufferBytes &&
        snapshot.redundant_candidate_count == options.candidate_count &&
        snapshot.redundant_candidate_bytes ==
            static_cast<std::uint64_t>(options.candidate_count) * kBufferBytes &&
        snapshot.forwarded_copy_count == expected_forwarded &&
        snapshot.forwarded_copy_bytes == expected_forwarded * kBufferBytes &&
        snapshot.skipped_copy_count == expected_skipped &&
        snapshot.skipped_copy_bytes == expected_skipped * kBufferBytes &&
        snapshot.exact_comparison_count == options.candidate_count + 2 &&
        snapshot.exact_comparison_bytes ==
            static_cast<std::uint64_t>(options.candidate_count + 2) * kBufferBytes &&
        snapshot.source_registration_count == 2 &&
        snapshot.destination_registration_count == 2 &&
        snapshot.lane_registration_count == 2 &&
        snapshot.fence_registration_count == 1 &&
        snapshot.automatic_invalidation_count == 2 &&
        snapshot.explicit_invalidation_count == 2 &&
        snapshot.command_list_close_count == 2 &&
        snapshot.command_list_reset_count == 0 &&
        snapshot.queue_execute_count == 1 && snapshot.queue_signal_count == 1 &&
        snapshot.submitted_scope_count == 2 &&
        snapshot.unregistered_submitted_scope_count == 0 &&
        snapshot.last_submission_scope_hash == expected_scope_hash &&
        snapshot.last_signaled_fence_id == kFenceId &&
        snapshot.last_signaled_fence_value == kFenceValue &&
        snapshot.control_policy_enabled == (options.managed_control ? 1ULL : 0ULL) &&
        snapshot.control_policy_epoch == (options.managed_control ? 1ULL : 0ULL) &&
        snapshot.control_policy_acknowledged_epoch ==
            (options.managed_control ? 1ULL : 0ULL) &&
        snapshot.control_policy_applied_action_count == expected_skipped &&
        snapshot.control_policy_rejected_count == 0 &&
        snapshot.control_policy_status == expected_status &&
        snapshot.ipc_event_count == expected_event_count &&
        snapshot.ipc_overrun_count == 0;
    if (!metrics_valid) {
        throw std::runtime_error("D3D12 transfer metrics violated the v2 contract.");
    }

    const auto detach_result = detach();
    check_hresult(detach_result, "FluidD3D12HookDetach");
    const auto pointers_restored =
        command_vtable[kCloseVtableIndex] == original_pointers[0] &&
        command_vtable[kResetVtableIndex] == original_pointers[1] &&
        command_vtable[kCopyBufferRegionVtableIndex] == original_pointers[2] &&
        command_vtable[kCopyTextureRegionVtableIndex] == original_pointers[3] &&
        command_vtable[kCopyResourceVtableIndex] == original_pointers[4] &&
        queue_vtable[kExecuteCommandListsVtableIndex] == original_pointers[5] &&
        queue_vtable[kSignalVtableIndex] == original_pointers[6];
    if (!pointers_restored) {
        throw std::runtime_error("A D3D12 transfer vtable pointer was not restored.");
    }
    const auto debug_messages = inspect_debug_messages(debug_info_queue.Get());
    if (debug_messages.warnings != 0 || debug_messages.errors != 0) {
        throw std::runtime_error("The D3D12 debug layer reported a contract violation.");
    }

    const auto cpu_record_microseconds = elapsed_microseconds(
        record_start, record_end, qpc_frequency);
    const auto submit_to_fence_microseconds = elapsed_microseconds(
        submit_start, completion, qpc_frequency);
    const auto total_workload_microseconds = elapsed_microseconds(
        record_start, completion, qpc_frequency);
    const auto gpu_workload_microseconds = gpu_timing_valid
        ? static_cast<double>(gpu_ticks) * 1'000'000.0 /
            static_cast<double>(timestamp_frequency)
        : 0.0;

    std::cout << std::fixed << std::setprecision(3)
              << "{\n"
              << "  \"mode\": \"fluidruntime-owned-d3d12-transfer-v0.21.0\",\n"
              << "  \"target_owned\": true,\n"
              << "  \"cooperative_load\": true,\n"
              << "  \"remote_injection\": false,\n"
              << "  \"actuation_enabled\": " << json_bool(options.managed_control) << ",\n"
              << "  \"self_published_control\": "
              << json_bool(options.self_publish_control) << ",\n"
              << "  \"physical_transfer_bytes_measured\": false,\n"
              << "  \"render_driver\": \""
              << (options.use_hardware ? "hardware" : "warp") << "\",\n"
              << "  \"debug_layer_enabled\": " << json_bool(debug_layer_enabled) << ",\n"
              << "  \"debug_warning_count\": " << debug_messages.warnings << ",\n"
              << "  \"debug_error_count\": " << debug_messages.errors << ",\n"
              << "  \"process_id\": " << GetCurrentProcessId() << ",\n"
              << "  \"adapter\": {\n"
              << "    \"description\": \"" << json_escape(adapter_info.description) << "\",\n"
              << "    \"vendor_id\": " << adapter_info.vendor_id << ",\n"
              << "    \"device_id\": " << adapter_info.device_id << ",\n"
              << "    \"luid\": \"" << adapter_info.luid << "\",\n"
              << "    \"uma\": " << json_bool(architecture.uma) << ",\n"
              << "    \"cache_coherent_uma\": "
              << json_bool(architecture.cache_coherent_uma) << ",\n"
              << "    \"resource_heap_tier\": "
              << architecture.resource_heap_tier << "\n"
              << "  },\n"
              << "  \"transfer_contract\": {\n"
              << "    \"abi_version\": " << fluid_transfer_contract_abi_version << ",\n"
              << "    \"backend\": " << topology.backend << ",\n"
              << "    \"operation\": " << topology.operation << ",\n"
              << "    \"queue_count\": " << topology.queue_count << ",\n"
              << "    \"execution_scope_count\": " << topology.execution_scope_count << ",\n"
              << "    \"source_resource_count\": " << topology.source_resource_count << ",\n"
              << "    \"destination_resource_count\": " << topology.destination_resource_count << ",\n"
              << "    \"lane_count\": " << topology.lane_count << ",\n"
              << "    \"fence_count\": " << topology.fence_count << ",\n"
              << "    \"max_action_count\": " << topology.max_action_count << ",\n"
              << "    \"runtime_event_count\": "
              << topology.expected_runtime_event_count << ",\n"
              << "    \"max_resource_bytes\": " << topology.max_resource_bytes << ",\n"
              << "    \"max_total_retained_bytes\": "
              << topology.max_total_retained_bytes << "\n"
              << "  },\n"
              << "  \"workload\": {\n"
              << "    \"scope\": \"owned-d3d12-copy-queue-multi-lane-full-buffer\",\n"
              << "    \"candidate_count\": " << options.candidate_count << ",\n"
              << "    \"buffer_bytes\": " << kBufferBytes << ",\n"
              << "    \"source_snapshot_mode\": "
              << "\"registration-copy-cpu-shadow-upload-unmapped-until-fence\",\n"
              << "    \"source_snapshot_bytes\": " << 2 * kUploadBytes << ",\n"
              << "    \"upload_unmapped_after_registration\": true,\n"
              << "    \"logical_candidate_bytes\": "
              << static_cast<std::uint64_t>(options.candidate_count) * kBufferBytes << ",\n"
              << "    \"tracked_copy_count\": " << expected_tracked << ",\n"
              << "    \"expected_forwarded_count\": " << expected_forwarded << ",\n"
              << "    \"expected_skipped_count\": " << expected_skipped << ",\n"
              << "    \"lane_candidate_counts\": ["
              << lane_candidate_counts[0] << ", " << lane_candidate_counts[1] << "],\n"
              << "    \"scope_ids\": [" << kScopeIds[0] << ", "
              << kScopeIds[1] << "],\n"
              << "    \"source_resource_ids\": [" << kSourceIds[0] << ", "
              << kSourceIds[1] << "],\n"
              << "    \"destination_resource_ids\": [" << kDestinationIds[0]
              << ", " << kDestinationIds[1] << "],\n"
              << "    \"source_transition_applied\": true,\n"
              << "    \"automatic_invalidation_guard_applied\": true,\n"
              << "    \"explicit_invalidation_guard_applied\": true,\n"
              << "    \"immutable_sources_verified\": "
              << json_bool(immutable_sources_verified) << ",\n"
              << "    \"content_equivalent\": " << json_bool(content_equivalent) << ",\n"
              << "    \"pattern_hashes\": [\"" << uint64_hex(pattern_hashes[0])
              << "\", \"" << uint64_hex(pattern_hashes[1]) << "\", \""
              << uint64_hex(pattern_hashes[2]) << "\", \""
              << uint64_hex(pattern_hashes[3]) << "\"],\n"
              << "    \"final_hashes\": [\"" << uint64_hex(final_hashes[0])
              << "\", \"" << uint64_hex(final_hashes[1]) << "\"]\n"
              << "  },\n"
              << "  \"hook\": {\n"
              << "    \"snapshot_abi_version\": " << snapshot.abi_version << ",\n"
              << "    \"attach_hresult\": \"" << hresult_hex(attach_result) << "\",\n"
              << "    \"detach_hresult\": \"" << hresult_hex(detach_result) << "\",\n"
              << "    \"original_pointers_restored\": " << json_bool(pointers_restored) << ",\n"
              << "    \"source_snapshot_bytes\": " << snapshot.source_snapshot_bytes << ",\n"
              << "    \"retained_capacity_bytes\": " << snapshot.retained_capacity_bytes << ",\n"
              << "    \"maximum_lane_generation\": " << snapshot.maximum_lane_generation << ",\n"
              << "    \"copy_buffer_region_count\": " << snapshot.copy_buffer_region_count << ",\n"
              << "    \"tracked_copy_count\": " << snapshot.tracked_copy_count << ",\n"
              << "    \"redundant_candidate_count\": " << snapshot.redundant_candidate_count << ",\n"
              << "    \"forwarded_copy_count\": " << snapshot.forwarded_copy_count << ",\n"
              << "    \"skipped_copy_count\": " << snapshot.skipped_copy_count << ",\n"
              << "    \"exact_comparison_count\": " << snapshot.exact_comparison_count << ",\n"
              << "    \"automatic_invalidation_count\": " << snapshot.automatic_invalidation_count << ",\n"
              << "    \"explicit_invalidation_count\": " << snapshot.explicit_invalidation_count << ",\n"
              << "    \"command_list_close_count\": " << snapshot.command_list_close_count << ",\n"
              << "    \"ipc_event_count\": " << snapshot.ipc_event_count << ",\n"
              << "    \"ipc_overrun_count\": " << snapshot.ipc_overrun_count << "\n"
              << "  },\n"
              << "  \"submission\": {\n"
              << "    \"queue_id\": " << snapshot.queue_id << ",\n"
              << "    \"execute_count\": " << snapshot.queue_execute_count << ",\n"
              << "    \"submitted_scope_count\": " << snapshot.submitted_scope_count << ",\n"
              << "    \"unregistered_scope_count\": "
              << snapshot.unregistered_submitted_scope_count << ",\n"
              << "    \"scope_order_hash\": \""
              << uint64_hex(snapshot.last_submission_scope_hash) << "\",\n"
              << "    \"fence_id\": " << snapshot.last_signaled_fence_id << ",\n"
              << "    \"fence_value\": " << snapshot.last_signaled_fence_value << ",\n"
              << "    \"fence_completed_value\": " << fence->GetCompletedValue() << "\n"
              << "  },\n"
              << "  \"control\": {\n"
              << "    \"requested\": " << json_bool(options.managed_control) << ",\n"
              << "    \"self_published\": " << json_bool(options.self_publish_control) << ",\n"
              << "    \"wait_hresult\": \"" << hresult_hex(control_wait_result) << "\",\n"
              << "    \"enabled\": " << snapshot.control_policy_enabled << ",\n"
              << "    \"epoch\": " << snapshot.control_policy_epoch << ",\n"
              << "    \"acknowledged_epoch\": "
              << snapshot.control_policy_acknowledged_epoch << ",\n"
              << "    \"applied_action_count\": "
              << snapshot.control_policy_applied_action_count << ",\n"
              << "    \"rejected_count\": " << snapshot.control_policy_rejected_count << ",\n"
              << "    \"status\": " << snapshot.control_policy_status << "\n"
              << "  },\n"
              << "  \"timing\": {\n"
              << "    \"cpu_record_microseconds\": " << cpu_record_microseconds << ",\n"
              << "    \"submit_to_fence_microseconds\": "
              << submit_to_fence_microseconds << ",\n"
              << "    \"total_workload_microseconds\": "
              << total_workload_microseconds << ",\n"
              << "    \"gpu_timestamp_valid\": " << json_bool(gpu_timing_valid) << ",\n"
              << "    \"gpu_workload_microseconds\": "
              << gpu_workload_microseconds << "\n"
              << "  },\n"
              << "  \"claim_scope\": \"owned-d3d12-copy-buffer-multi-lane-exact-content-elision\",\n"
              << "  \"limitations\": [\n"
              << "    \"Skipped bytes are logical API commands, not measured PCIe or physical RAM-to-VRAM traffic.\",\n"
              << "    \"This milestone covers buffer copies, not textures, aliasing, or residency management.\",\n"
              << "    \"The backend-neutral contract is Vulkan-ready, but this binary executes D3D12 only.\",\n"
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
        std::cerr << "D3D12 transfer target input error: " << exception.what() << '\n';
        return 2;
    } catch (const std::exception& exception) {
        std::cerr << "D3D12 transfer target failed: " << exception.what() << '\n';
        return 1;
    }
}
