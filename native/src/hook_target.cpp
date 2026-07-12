#include "fluidruntime_hook_api.h"

#include <d3d11.h>
#include <wrl/client.h>

#include <array>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <optional>
#include <sstream>
#include <string>
#include <string_view>
#include <vector>

namespace {

using Microsoft::WRL::ComPtr;

constexpr UINT kBufferBytes = 4096;
constexpr UINT kTextureWidth = 64;
constexpr UINT kTextureHeight = 64;
constexpr std::uint64_t kExpectedCopyCount = 6;
constexpr std::uint64_t kExpectedCopyBytes = 49152;
constexpr std::uint64_t kExpectedRedundantCopyCount = 3;
constexpr std::uint64_t kExpectedRedundantCopyBytes = 24576;
constexpr std::uint64_t kExpectedSkippedCopyCount = 1;
constexpr std::uint64_t kExpectedSkippedCopyBytes = kBufferBytes;
constexpr std::uint64_t kFnvOffsetBasis = 14695981039346656037ULL;
constexpr std::uint64_t kFnvPrime = 1099511628211ULL;

struct Options {
    std::wstring hook_path;
    std::wstring output_path;
    unsigned long frames{60};
    unsigned long hold_ms{};
    unsigned long gpu_timeout_ms{1000};
    bool use_hardware{};
    bool skip_first_redundant_copy{};
};

struct WorkloadResources {
    ComPtr<ID3D11Buffer> source_buffer;
    ComPtr<ID3D11Buffer> destination_buffer;
    ComPtr<ID3D11Buffer> dynamic_buffer;
    ComPtr<ID3D11Texture2D> source_texture;
    ComPtr<ID3D11Texture2D> destination_texture;
};

struct ContentVerification {
    bool readback_succeeded{};
    bool buffer_contents_equal{};
    bool texture_contents_equal{};
    std::uint64_t source_buffer_hash{};
    std::uint64_t destination_buffer_hash{};
    std::uint64_t source_texture_hash{};
    std::uint64_t destination_texture_hash{};
};

struct TimingMetrics {
    std::uint64_t qpc_frequency{};
    std::uint64_t workload_qpc_ticks{};
    std::uint64_t present_qpc_ticks{};
    std::uint64_t readback_qpc_ticks{};
    bool gpu_timing_supported{};
    bool gpu_timing_valid{};
    bool gpu_timing_disjoint{};
    bool gpu_query_timed_out{};
    std::uint64_t gpu_frequency{};
    std::uint64_t gpu_workload_ticks{};
};

struct GpuTimingQueries {
    ComPtr<ID3D11Query> disjoint;
    ComPtr<ID3D11Query> start;
    ComPtr<ID3D11Query> end;
};

struct AdapterIdentity {
    bool available{};
    std::string description;
    std::uint32_t vendor_id{};
    std::uint32_t device_id{};
    std::uint32_t subsystem_id{};
    std::uint32_t revision{};
    std::uint64_t dedicated_video_memory{};
    std::uint64_t dedicated_system_memory{};
    std::uint64_t shared_system_memory{};
    std::uint64_t luid{};
};

LRESULT CALLBACK window_procedure(
    HWND window,
    UINT message,
    WPARAM word_parameter,
    LPARAM long_parameter) {
    return DefWindowProcW(window, message, word_parameter, long_parameter);
}

std::optional<unsigned long> parse_positive(const wchar_t* value) {
    wchar_t* end{};
    const auto parsed = wcstoul(value, &end, 10);
    if (end == value || *end != L'\0' || parsed == 0) {
        return std::nullopt;
    }
    return parsed;
}

std::optional<Options> parse_options(int argc, wchar_t* argv[]) {
    Options options;
    for (int index = 1; index < argc; ++index) {
        const std::wstring_view argument(argv[index]);
        if (argument == L"--hook" && index + 1 < argc) {
            options.hook_path = argv[++index];
        } else if (argument == L"--out" && index + 1 < argc) {
            options.output_path = argv[++index];
        } else if (argument == L"--frames" && index + 1 < argc) {
            const auto frames = parse_positive(argv[++index]);
            if (!frames.has_value() || *frames > 10000) {
                return std::nullopt;
            }
            options.frames = *frames;
        } else if (argument == L"--hold-ms" && index + 1 < argc) {
            const auto hold_ms = parse_positive(argv[++index]);
            if (!hold_ms.has_value() || *hold_ms > 60000) {
                return std::nullopt;
            }
            options.hold_ms = *hold_ms;
        } else if (argument == L"--gpu-timeout-ms" && index + 1 < argc) {
            const auto gpu_timeout_ms = parse_positive(argv[++index]);
            if (!gpu_timeout_ms.has_value() || *gpu_timeout_ms > 10000) {
                return std::nullopt;
            }
            options.gpu_timeout_ms = *gpu_timeout_ms;
        } else if (argument == L"--hardware") {
            options.use_hardware = true;
        } else if (argument == L"--skip-first-redundant-copy") {
            options.skip_first_redundant_copy = true;
        } else {
            return std::nullopt;
        }
    }

    if (options.hook_path.empty()) {
        return std::nullopt;
    }
    return options;
}

std::string hresult_hex(HRESULT result) {
    std::ostringstream output;
    output << "0x" << std::uppercase << std::hex << std::setw(8)
           << std::setfill('0') << static_cast<unsigned long>(result);
    return output.str();
}

std::string uint64_hex(std::uint64_t value) {
    std::ostringstream output;
    output << std::hex << std::setw(16) << std::setfill('0') << value;
    return output.str();
}

std::string json_escape(std::string_view value) {
    std::string output;
    output.reserve(value.size());
    for (const auto character : value) {
        switch (character) {
        case '"':
            output += "\\\"";
            break;
        case '\\':
            output += "\\\\";
            break;
        case '\n':
            output += "\\n";
            break;
        case '\r':
            output += "\\r";
            break;
        case '\t':
            output += "\\t";
            break;
        default:
            output += character;
            break;
        }
    }
    return output;
}

std::string wide_to_utf8(const wchar_t* value) {
    if (value == nullptr || *value == L'\0') {
        return {};
    }
    const auto required = WideCharToMultiByte(
        CP_UTF8,
        0,
        value,
        -1,
        nullptr,
        0,
        nullptr,
        nullptr);
    if (required <= 1) {
        return {};
    }
    std::string output(static_cast<size_t>(required), '\0');
    WideCharToMultiByte(
        CP_UTF8,
        0,
        value,
        -1,
        output.data(),
        required,
        nullptr,
        nullptr);
    output.resize(static_cast<size_t>(required - 1));
    return output;
}

AdapterIdentity query_adapter_identity(ID3D11Device* device) {
    AdapterIdentity result;
    if (device == nullptr) {
        return result;
    }
    ComPtr<IDXGIDevice> dxgi_device;
    if (FAILED(device->QueryInterface(IID_PPV_ARGS(&dxgi_device)))) {
        return result;
    }
    ComPtr<IDXGIAdapter> adapter;
    if (FAILED(dxgi_device->GetAdapter(&adapter))) {
        return result;
    }
    DXGI_ADAPTER_DESC description{};
    if (FAILED(adapter->GetDesc(&description))) {
        return result;
    }

    result.available = true;
    result.description = wide_to_utf8(description.Description);
    result.vendor_id = description.VendorId;
    result.device_id = description.DeviceId;
    result.subsystem_id = description.SubSysId;
    result.revision = description.Revision;
    result.dedicated_video_memory = description.DedicatedVideoMemory;
    result.dedicated_system_memory = description.DedicatedSystemMemory;
    result.shared_system_memory = description.SharedSystemMemory;
    result.luid =
        static_cast<std::uint64_t>(static_cast<std::uint32_t>(description.AdapterLuid.HighPart))
            << 32 |
        description.AdapterLuid.LowPart;
    return result;
}

std::uint64_t hash_bytes(
    std::uint64_t hash,
    const void* data,
    size_t size) {
    const auto* bytes = static_cast<const unsigned char*>(data);
    for (size_t index = 0; index < size; ++index) {
        hash ^= bytes[index];
        hash *= kFnvPrime;
    }
    return hash;
}

std::optional<std::vector<unsigned char>> readback_buffer(
    ID3D11Device* device,
    ID3D11DeviceContext* context,
    ID3D11Buffer* buffer) {
    if (device == nullptr || context == nullptr || buffer == nullptr) {
        return std::nullopt;
    }

    D3D11_BUFFER_DESC description{};
    buffer->GetDesc(&description);
    description.Usage = D3D11_USAGE_STAGING;
    description.BindFlags = 0;
    description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    description.MiscFlags = 0;
    description.StructureByteStride = 0;
    ComPtr<ID3D11Buffer> staging;
    if (FAILED(device->CreateBuffer(&description, nullptr, &staging))) {
        return std::nullopt;
    }

    context->CopyResource(staging.Get(), buffer);
    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (FAILED(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped))) {
        return std::nullopt;
    }
    std::vector<unsigned char> data(description.ByteWidth);
    std::memcpy(data.data(), mapped.pData, data.size());
    context->Unmap(staging.Get(), 0);
    return data;
}

std::optional<std::vector<unsigned char>> readback_texture(
    ID3D11Device* device,
    ID3D11DeviceContext* context,
    ID3D11Texture2D* texture) {
    if (device == nullptr || context == nullptr || texture == nullptr) {
        return std::nullopt;
    }

    D3D11_TEXTURE2D_DESC description{};
    texture->GetDesc(&description);
    if (description.Format != DXGI_FORMAT_R8G8B8A8_UNORM ||
        description.MipLevels != 1 ||
        description.ArraySize != 1) {
        return std::nullopt;
    }
    description.Usage = D3D11_USAGE_STAGING;
    description.BindFlags = 0;
    description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    description.MiscFlags = 0;
    ComPtr<ID3D11Texture2D> staging;
    if (FAILED(device->CreateTexture2D(&description, nullptr, &staging))) {
        return std::nullopt;
    }

    context->CopyResource(staging.Get(), texture);
    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (FAILED(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped))) {
        return std::nullopt;
    }
    const auto logical_row_bytes = static_cast<size_t>(description.Width) * 4;
    std::vector<unsigned char> data(logical_row_bytes * description.Height);
    const auto* row = static_cast<const unsigned char*>(mapped.pData);
    for (UINT y = 0; y < description.Height; ++y) {
        std::memcpy(data.data() + y * logical_row_bytes, row, logical_row_bytes);
        row += mapped.RowPitch;
    }
    context->Unmap(staging.Get(), 0);
    return data;
}

ContentVerification verify_workload_content(
    ID3D11Device* device,
    ID3D11DeviceContext* context,
    const WorkloadResources& resources) {
    ContentVerification result;
    const auto source_buffer = readback_buffer(
        device,
        context,
        resources.source_buffer.Get());
    const auto destination_buffer = readback_buffer(
        device,
        context,
        resources.destination_buffer.Get());
    const auto source_texture = readback_texture(
        device,
        context,
        resources.source_texture.Get());
    const auto destination_texture = readback_texture(
        device,
        context,
        resources.destination_texture.Get());
    result.readback_succeeded = source_buffer.has_value() &&
        destination_buffer.has_value() &&
        source_texture.has_value() &&
        destination_texture.has_value();
    if (!result.readback_succeeded) {
        return result;
    }

    result.source_buffer_hash = hash_bytes(
        kFnvOffsetBasis,
        source_buffer->data(),
        source_buffer->size());
    result.destination_buffer_hash = hash_bytes(
        kFnvOffsetBasis,
        destination_buffer->data(),
        destination_buffer->size());
    result.source_texture_hash = hash_bytes(
        kFnvOffsetBasis,
        source_texture->data(),
        source_texture->size());
    result.destination_texture_hash = hash_bytes(
        kFnvOffsetBasis,
        destination_texture->data(),
        destination_texture->size());
    result.buffer_contents_equal = *source_buffer == *destination_buffer;
    result.texture_contents_equal = *source_texture == *destination_texture;
    return result;
}

GpuTimingQueries create_gpu_timing_queries(ID3D11Device* device) {
    GpuTimingQueries result;
    if (device == nullptr) {
        return result;
    }

    D3D11_QUERY_DESC description{};
    description.Query = D3D11_QUERY_TIMESTAMP_DISJOINT;
    if (FAILED(device->CreateQuery(&description, &result.disjoint))) {
        return {};
    }
    description.Query = D3D11_QUERY_TIMESTAMP;
    if (FAILED(device->CreateQuery(&description, &result.start)) ||
        FAILED(device->CreateQuery(&description, &result.end))) {
        return {};
    }
    return result;
}

template <typename T>
bool wait_for_query_data(
    ID3D11DeviceContext* context,
    ID3D11Query* query,
    T& data,
    bool& timed_out,
    ULONGLONG timeout_ms) {
    const auto deadline = GetTickCount64() + timeout_ms;
    while (GetTickCount64() <= deadline) {
        const auto result = context->GetData(
            query,
            &data,
            sizeof(data),
            D3D11_ASYNC_GETDATA_DONOTFLUSH);
        if (result == S_OK) {
            return true;
        }
        if (FAILED(result)) {
            return false;
        }
        Sleep(1);
    }
    timed_out = true;
    return false;
}

void collect_gpu_timing(
    ID3D11DeviceContext* context,
    const GpuTimingQueries& queries,
    TimingMetrics& timing,
    unsigned long timeout_ms) {
    timing.gpu_timing_supported =
        queries.disjoint != nullptr && queries.start != nullptr && queries.end != nullptr;
    if (!timing.gpu_timing_supported || context == nullptr) {
        return;
    }

    context->Flush();
    D3D11_QUERY_DATA_TIMESTAMP_DISJOINT disjoint_data{};
    std::uint64_t start_timestamp = 0;
    std::uint64_t end_timestamp = 0;
    if (!wait_for_query_data(
            context,
            queries.disjoint.Get(),
            disjoint_data,
            timing.gpu_query_timed_out,
            timeout_ms) ||
        !wait_for_query_data(
            context,
            queries.start.Get(),
            start_timestamp,
            timing.gpu_query_timed_out,
            timeout_ms) ||
        !wait_for_query_data(
            context,
            queries.end.Get(),
            end_timestamp,
            timing.gpu_query_timed_out,
            timeout_ms)) {
        return;
    }

    timing.gpu_timing_disjoint = disjoint_data.Disjoint != FALSE;
    timing.gpu_frequency = disjoint_data.Frequency;
    if (!timing.gpu_timing_disjoint &&
        timing.gpu_frequency != 0 &&
        end_timestamp >= start_timestamp) {
        timing.gpu_workload_ticks = end_timestamp - start_timestamp;
        timing.gpu_timing_valid = true;
    }
}

bool warm_up_context(ID3D11Device* device, ID3D11DeviceContext* context) {
    D3D11_BUFFER_DESC description{};
    description.ByteWidth = 16;
    description.Usage = D3D11_USAGE_DEFAULT;
    ComPtr<ID3D11Buffer> buffer;
    if (FAILED(device->CreateBuffer(&description, nullptr, &buffer))) {
        return false;
    }

    const std::array<std::uint32_t, 4> data{1, 2, 3, 4};
    context->UpdateSubresource(buffer.Get(), 0, nullptr, data.data(), 0, 0);
    return true;
}

bool run_resource_workload(
    ID3D11Device* device,
    ID3D11DeviceContext* context,
    WorkloadResources& resources,
    bool& context_vtable_pointer_stable,
    bool& context_copy_entry_stable) {
    std::array<std::uint32_t, kBufferBytes / sizeof(std::uint32_t)> buffer_data{};
    for (size_t index = 0; index < buffer_data.size(); ++index) {
        buffer_data[index] = static_cast<std::uint32_t>(index);
    }

    D3D11_BUFFER_DESC default_buffer_description{};
    default_buffer_description.ByteWidth = kBufferBytes;
    default_buffer_description.Usage = D3D11_USAGE_DEFAULT;
    D3D11_SUBRESOURCE_DATA buffer_initial_data{};
    buffer_initial_data.pSysMem = buffer_data.data();
    auto result = device->CreateBuffer(
        &default_buffer_description,
        &buffer_initial_data,
        &resources.source_buffer);
    if (FAILED(result)) {
        return false;
    }
    result = device->CreateBuffer(
        &default_buffer_description,
        nullptr,
        &resources.destination_buffer);
    if (FAILED(result)) {
        return false;
    }

    D3D11_BUFFER_DESC dynamic_buffer_description{};
    dynamic_buffer_description.ByteWidth = kBufferBytes;
    dynamic_buffer_description.Usage = D3D11_USAGE_DYNAMIC;
    dynamic_buffer_description.BindFlags = D3D11_BIND_VERTEX_BUFFER;
    dynamic_buffer_description.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    result = device->CreateBuffer(
        &dynamic_buffer_description,
        nullptr,
        &resources.dynamic_buffer);
    if (FAILED(result)) {
        return false;
    }

    D3D11_MAPPED_SUBRESOURCE mapped{};
    result = context->Map(
        resources.dynamic_buffer.Get(),
        0,
        D3D11_MAP_WRITE_DISCARD,
        0,
        &mapped);
    if (FAILED(result)) {
        return false;
    }
    std::memset(mapped.pData, 0x5A, kBufferBytes);
    context->Unmap(resources.dynamic_buffer.Get(), 0);

    auto** context_vtable_before_update = *reinterpret_cast<void***>(context);
    const auto copy_entry_before_update = context_vtable_before_update[47];
    context->CopyResource(
        resources.destination_buffer.Get(),
        resources.source_buffer.Get());
    context->CopyResource(
        resources.destination_buffer.Get(),
        resources.source_buffer.Get());

    for (auto& value : buffer_data) {
        value ^= 0xA5A5A5A5U;
    }
    context->UpdateSubresource(
        resources.source_buffer.Get(),
        0,
        nullptr,
        buffer_data.data(),
        0,
        0);
    auto** context_vtable_after_update = *reinterpret_cast<void***>(context);
    context_vtable_pointer_stable =
        context_vtable_after_update == context_vtable_before_update;
    context_copy_entry_stable =
        context_vtable_after_update[47] == copy_entry_before_update;
    context->CopyResource(
        resources.destination_buffer.Get(),
        resources.source_buffer.Get());
    context->CopyResource(
        resources.destination_buffer.Get(),
        resources.source_buffer.Get());

    std::array<std::uint32_t, kTextureWidth * kTextureHeight> texture_data{};
    for (size_t index = 0; index < texture_data.size(); ++index) {
        texture_data[index] = 0xFF000000U | static_cast<std::uint32_t>(index);
    }

    D3D11_TEXTURE2D_DESC texture_description{};
    texture_description.Width = kTextureWidth;
    texture_description.Height = kTextureHeight;
    texture_description.MipLevels = 1;
    texture_description.ArraySize = 1;
    texture_description.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    texture_description.SampleDesc.Count = 1;
    texture_description.Usage = D3D11_USAGE_DEFAULT;
    D3D11_SUBRESOURCE_DATA texture_initial_data{};
    texture_initial_data.pSysMem = texture_data.data();
    texture_initial_data.SysMemPitch = kTextureWidth * sizeof(std::uint32_t);
    result = device->CreateTexture2D(
        &texture_description,
        &texture_initial_data,
        &resources.source_texture);
    if (FAILED(result)) {
        return false;
    }
    result = device->CreateTexture2D(
        &texture_description,
        nullptr,
        &resources.destination_texture);
    if (FAILED(result)) {
        return false;
    }

    context->CopyResource(
        resources.destination_texture.Get(),
        resources.source_texture.Get());
    context->CopyResource(
        resources.destination_texture.Get(),
        resources.source_texture.Get());
    return true;
}

bool snapshot_matches_workload(
    const FluidHookSnapshotV1& snapshot,
    const Options& options) {
    const auto expected_skipped_count = options.skip_first_redundant_copy
        ? kExpectedSkippedCopyCount
        : 0;
    const auto expected_skipped_bytes = options.skip_first_redundant_copy
        ? kExpectedSkippedCopyBytes
        : 0;
    return snapshot.abi_version == fluid_hook_snapshot_abi_version &&
        snapshot.create_buffer_count == 3 &&
        snapshot.buffer_bytes_requested == 3 * kBufferBytes &&
        snapshot.create_texture2d_count == 2 &&
        snapshot.texture_bytes_estimated == 2 * kTextureWidth * kTextureHeight * 4 &&
        snapshot.map_write_count == 1 &&
        snapshot.unmap_write_count == 1 &&
        snapshot.update_subresource_count == 1 &&
        snapshot.copy_resource_count == kExpectedCopyCount &&
        snapshot.copy_resource_bytes_estimated == kExpectedCopyBytes &&
        snapshot.redundant_copy_candidate_count == kExpectedRedundantCopyCount &&
        snapshot.redundant_copy_bytes_estimated == kExpectedRedundantCopyBytes &&
        snapshot.forwarded_copy_count == kExpectedCopyCount - expected_skipped_count &&
        snapshot.forwarded_copy_bytes_estimated ==
            kExpectedCopyBytes - expected_skipped_bytes &&
        snapshot.skipped_copy_count == expected_skipped_count &&
        snapshot.skipped_copy_bytes_estimated == expected_skipped_bytes &&
        snapshot.tracked_resource_count == 5 &&
        snapshot.hook_refresh_failure_count == 0 &&
        snapshot.ipc_event_count >= snapshot.present_count + 14 &&
        snapshot.ipc_overrun_count == 0;
}

std::string build_report(
    const Options& options,
    const FluidHookSnapshotV1& snapshot,
    HRESULT attach_result,
    HRESULT refresh_result,
    HRESULT snapshot_result,
    HRESULT detach_result,
    bool original_pointer_restored,
    bool render_succeeded,
    bool resource_workload_succeeded,
    bool resource_metrics_matched,
    bool context_vtable_pointer_stable,
    bool context_copy_entry_stable,
    const ContentVerification& content,
    const TimingMetrics& timing,
    const AdapterIdentity& adapter) {
    std::ostringstream output;
    output << "{\n"
           << "  \"mode\": \"fluidruntime-resource-hook-lab-v0.6\",\n"
           << "  \"target_owned\": true,\n"
           << "  \"cooperative_load\": true,\n"
           << "  \"remote_injection\": false,\n"
           << "  \"read_only_hook\": "
           << (options.skip_first_redundant_copy ? "false" : "true") << ",\n"
           << "  \"would_modify_frame_data\": false,\n"
           << "  \"would_skip_copies\": "
           << (options.skip_first_redundant_copy ? "true" : "false") << ",\n"
           << "  \"optimization_requested\": "
           << (options.skip_first_redundant_copy ? "true" : "false") << ",\n"
           << "  \"optimization_kind\": \"skip-first-redundant-copy-resource\",\n"
           << "  \"max_skipped_copy_count\": "
           << (options.skip_first_redundant_copy ? 1 : 0) << ",\n"
           << "  \"render_driver\": \""
           << (options.use_hardware ? "hardware" : "warp") << "\",\n"
           << "  \"adapter\": {\n"
           << "    \"available\": " << (adapter.available ? "true" : "false") << ",\n"
           << "    \"description\": \"" << json_escape(adapter.description) << "\",\n"
           << "    \"vendor_id\": " << adapter.vendor_id << ",\n"
           << "    \"device_id\": " << adapter.device_id << ",\n"
           << "    \"subsystem_id\": " << adapter.subsystem_id << ",\n"
           << "    \"revision\": " << adapter.revision << ",\n"
           << "    \"dedicated_video_memory\": "
           << adapter.dedicated_video_memory << ",\n"
           << "    \"dedicated_system_memory\": "
           << adapter.dedicated_system_memory << ",\n"
           << "    \"shared_system_memory\": " << adapter.shared_system_memory << ",\n"
           << "    \"luid\": \"" << uint64_hex(adapter.luid) << "\"\n"
           << "  },\n"
           << "  \"requested_presents\": " << options.frames << ",\n"
           << "  \"hold_ms\": " << options.hold_ms << ",\n"
           << "  \"observed_presents\": " << snapshot.present_count << ",\n"
           << "  \"render_succeeded\": "
           << (render_succeeded ? "true" : "false") << ",\n"
           << "  \"resource_workload_succeeded\": "
           << (resource_workload_succeeded ? "true" : "false") << ",\n"
           << "  \"resource_metrics_matched\": "
           << (resource_metrics_matched ? "true" : "false") << ",\n"
           << "  \"context_vtable_pointer_stable\": "
           << (context_vtable_pointer_stable ? "true" : "false") << ",\n"
           << "  \"context_copy_entry_stable\": "
           << (context_copy_entry_stable ? "true" : "false") << ",\n"
           << "  \"original_pointer_restored\": "
           << (original_pointer_restored ? "true" : "false") << ",\n"
           << "  \"content_readback_succeeded\": "
           << (content.readback_succeeded ? "true" : "false") << ",\n"
           << "  \"buffer_contents_equal\": "
           << (content.buffer_contents_equal ? "true" : "false") << ",\n"
           << "  \"texture_contents_equal\": "
           << (content.texture_contents_equal ? "true" : "false") << ",\n"
           << "  \"hash_algorithm\": \"fnv1a64\",\n"
           << "  \"source_buffer_hash\": \""
           << uint64_hex(content.source_buffer_hash) << "\",\n"
           << "  \"destination_buffer_hash\": \""
           << uint64_hex(content.destination_buffer_hash) << "\",\n"
           << "  \"source_texture_hash\": \""
           << uint64_hex(content.source_texture_hash) << "\",\n"
           << "  \"destination_texture_hash\": \""
           << uint64_hex(content.destination_texture_hash) << "\",\n"
           << "  \"timing\": {\n"
           << "    \"qpc_frequency\": " << timing.qpc_frequency << ",\n"
           << "    \"workload_qpc_ticks\": " << timing.workload_qpc_ticks << ",\n"
           << "    \"present_qpc_ticks\": " << timing.present_qpc_ticks << ",\n"
           << "    \"readback_qpc_ticks\": " << timing.readback_qpc_ticks << ",\n"
           << "    \"gpu_timing_supported\": "
           << (timing.gpu_timing_supported ? "true" : "false") << ",\n"
           << "    \"gpu_timing_valid\": "
           << (timing.gpu_timing_valid ? "true" : "false") << ",\n"
           << "    \"gpu_timing_disjoint\": "
           << (timing.gpu_timing_disjoint ? "true" : "false") << ",\n"
           << "    \"gpu_query_timed_out\": "
           << (timing.gpu_query_timed_out ? "true" : "false") << ",\n"
           << "    \"gpu_timeout_ms\": " << options.gpu_timeout_ms << ",\n"
           << "    \"gpu_frequency\": " << timing.gpu_frequency << ",\n"
           << "    \"gpu_workload_ticks\": " << timing.gpu_workload_ticks << "\n"
           << "  },\n"
           << "  \"resources\": {\n"
           << "    \"create_buffer_count\": " << snapshot.create_buffer_count << ",\n"
           << "    \"buffer_bytes_requested\": " << snapshot.buffer_bytes_requested << ",\n"
           << "    \"create_texture2d_count\": " << snapshot.create_texture2d_count << ",\n"
           << "    \"texture_bytes_estimated\": " << snapshot.texture_bytes_estimated << ",\n"
           << "    \"map_write_count\": " << snapshot.map_write_count << ",\n"
           << "    \"unmap_write_count\": " << snapshot.unmap_write_count << ",\n"
           << "    \"update_subresource_count\": "
           << snapshot.update_subresource_count << ",\n"
           << "    \"copy_resource_count\": " << snapshot.copy_resource_count << ",\n"
           << "    \"copy_resource_bytes_estimated\": "
           << snapshot.copy_resource_bytes_estimated << ",\n"
           << "    \"redundant_copy_candidate_count\": "
           << snapshot.redundant_copy_candidate_count << ",\n"
           << "    \"redundant_copy_bytes_estimated\": "
           << snapshot.redundant_copy_bytes_estimated << ",\n"
           << "    \"forwarded_copy_count\": " << snapshot.forwarded_copy_count << ",\n"
           << "    \"forwarded_copy_bytes_estimated\": "
           << snapshot.forwarded_copy_bytes_estimated << ",\n"
           << "    \"skipped_copy_count\": " << snapshot.skipped_copy_count << ",\n"
           << "    \"skipped_copy_bytes_estimated\": "
           << snapshot.skipped_copy_bytes_estimated << ",\n"
           << "    \"tracked_resource_count\": " << snapshot.tracked_resource_count << ",\n"
           << "    \"hook_refresh_count\": " << snapshot.hook_refresh_count << ",\n"
           << "    \"hook_refresh_failure_count\": "
           << snapshot.hook_refresh_failure_count << ",\n"
           << "    \"ipc_event_count\": " << snapshot.ipc_event_count << ",\n"
           << "    \"ipc_overrun_count\": " << snapshot.ipc_overrun_count << "\n"
           << "  },\n"
           << "  \"attach_hresult\": \"" << hresult_hex(attach_result) << "\",\n"
           << "  \"refresh_hresult\": \"" << hresult_hex(refresh_result) << "\",\n"
           << "  \"snapshot_hresult\": \"" << hresult_hex(snapshot_result) << "\",\n"
           << "  \"detach_hresult\": \"" << hresult_hex(detach_result) << "\"\n"
           << "}\n";
    return output.str();
}

} // namespace

int wmain(int argc, wchar_t* argv[]) {
    const auto options = parse_options(argc, argv);
    if (!options.has_value()) {
        std::wcerr << L"Usage: fluidruntime-hook-target --hook <dll> "
                      L"[--frames <count>] [--hold-ms <milliseconds>] "
                      L"[--gpu-timeout-ms <milliseconds>] "
                      L"[--out <report.json>] [--hardware] "
                      L"[--skip-first-redundant-copy]\n";
        return 2;
    }

    const auto instance = GetModuleHandleW(nullptr);
    const wchar_t window_class_name[] = L"FluidRuntimeHookLabWindow";
    WNDCLASSW window_class{};
    window_class.lpfnWndProc = window_procedure;
    window_class.hInstance = instance;
    window_class.lpszClassName = window_class_name;
    if (RegisterClassW(&window_class) == 0) {
        std::cerr << "Unable to register hook lab window class.\n";
        return 3;
    }

    const auto window = CreateWindowExW(
        0,
        window_class_name,
        L"FluidRuntime Hook Lab",
        WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT,
        CW_USEDEFAULT,
        320,
        180,
        nullptr,
        nullptr,
        instance,
        nullptr);
    if (window == nullptr) {
        UnregisterClassW(window_class_name, instance);
        std::cerr << "Unable to create hook lab window.\n";
        return 3;
    }

    DXGI_SWAP_CHAIN_DESC swap_chain_description{};
    swap_chain_description.BufferDesc.Width = 320;
    swap_chain_description.BufferDesc.Height = 180;
    swap_chain_description.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    swap_chain_description.SampleDesc.Count = 1;
    swap_chain_description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    swap_chain_description.BufferCount = 1;
    swap_chain_description.OutputWindow = window;
    swap_chain_description.Windowed = TRUE;
    swap_chain_description.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

    ComPtr<IDXGISwapChain> swap_chain;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    D3D_FEATURE_LEVEL feature_level{};
    const auto driver_type = options->use_hardware
        ? D3D_DRIVER_TYPE_HARDWARE
        : D3D_DRIVER_TYPE_WARP;
    const auto create_result = D3D11CreateDeviceAndSwapChain(
        nullptr,
        driver_type,
        nullptr,
        0,
        nullptr,
        0,
        D3D11_SDK_VERSION,
        &swap_chain_description,
        &swap_chain,
        &device,
        &feature_level,
        &context);
    if (FAILED(create_result)) {
        DestroyWindow(window);
        UnregisterClassW(window_class_name, instance);
        std::cerr << "Unable to create D3D11 swap chain: "
                  << hresult_hex(create_result) << "\n";
        return 4;
    }

    ComPtr<ID3D11Texture2D> back_buffer;
    auto result = swap_chain->GetBuffer(0, IID_PPV_ARGS(&back_buffer));
    ComPtr<ID3D11RenderTargetView> render_target;
    if (SUCCEEDED(result)) {
        result = device->CreateRenderTargetView(
            back_buffer.Get(),
            nullptr,
            &render_target);
    }
    if (FAILED(result)) {
        DestroyWindow(window);
        UnregisterClassW(window_class_name, instance);
        std::cerr << "Unable to create D3D11 render target.\n";
        return 4;
    }
    const auto adapter_identity = query_adapter_identity(device.Get());

    if (!warm_up_context(device.Get(), context.Get())) {
        DestroyWindow(window);
        UnregisterClassW(window_class_name, instance);
        std::cerr << "Unable to warm up D3D11 context.\n";
        return 4;
    }

    const auto hook_module = LoadLibraryW(options->hook_path.c_str());
    if (hook_module == nullptr) {
        DestroyWindow(window);
        UnregisterClassW(window_class_name, instance);
        std::cerr << "Unable to load hook DLL.\n";
        return 5;
    }

    const auto attach = reinterpret_cast<FluidHookAttachFunction>(
        GetProcAddress(hook_module, "FluidHookAttach"));
    const auto attach_ex = reinterpret_cast<FluidHookAttachExFunction>(
        GetProcAddress(hook_module, "FluidHookAttachEx"));
    const auto detach = reinterpret_cast<FluidHookDetachFunction>(
        GetProcAddress(hook_module, "FluidHookDetach"));
    const auto refresh = reinterpret_cast<FluidHookRefreshFunction>(
        GetProcAddress(hook_module, "FluidHookRefresh"));
    const auto is_attached = reinterpret_cast<FluidHookIsAttachedFunction>(
        GetProcAddress(hook_module, "FluidHookIsAttached"));
    const auto read_snapshot = reinterpret_cast<FluidHookReadSnapshotFunction>(
        GetProcAddress(hook_module, "FluidHookReadSnapshot"));
    if (attach == nullptr || attach_ex == nullptr || detach == nullptr || refresh == nullptr ||
        is_attached == nullptr || read_snapshot == nullptr) {
        FreeLibrary(hook_module);
        DestroyWindow(window);
        UnregisterClassW(window_class_name, instance);
        std::cerr << "Hook DLL contract is incomplete.\n";
        return 5;
    }

    FluidHookAttachOptionsV1 attach_options{};
    attach_options.struct_size = sizeof(attach_options);
    attach_options.abi_version = fluid_hook_attach_options_abi_version;
    attach_options.flags = fluid_hook_attach_flag_skip_first_redundant_copy;
    attach_options.max_skipped_copy_count = 1;
    const auto attach_result = options->skip_first_redundant_copy
        ? attach_ex(swap_chain.Get(), &attach_options)
        : attach(swap_chain.Get());
    LARGE_INTEGER qpc_frequency{};
    QueryPerformanceFrequency(&qpc_frequency);
    TimingMetrics timing{
        .qpc_frequency = static_cast<std::uint64_t>(qpc_frequency.QuadPart),
    };
    const auto gpu_timing_queries = create_gpu_timing_queries(device.Get());
    WorkloadResources workload_resources;
    bool context_vtable_pointer_stable = false;
    bool context_copy_entry_stable = false;
    LARGE_INTEGER workload_start{};
    LARGE_INTEGER workload_end{};
    if (gpu_timing_queries.disjoint != nullptr) {
        context->Begin(gpu_timing_queries.disjoint.Get());
        context->End(gpu_timing_queries.start.Get());
    }
    QueryPerformanceCounter(&workload_start);
    const auto resource_workload_succeeded =
        SUCCEEDED(attach_result) &&
        is_attached() != FALSE &&
        run_resource_workload(
            device.Get(),
            context.Get(),
            workload_resources,
            context_vtable_pointer_stable,
            context_copy_entry_stable);
    QueryPerformanceCounter(&workload_end);
    if (gpu_timing_queries.disjoint != nullptr) {
        context->End(gpu_timing_queries.end.Get());
        context->End(gpu_timing_queries.disjoint.Get());
    }
    timing.workload_qpc_ticks = static_cast<std::uint64_t>(
        workload_end.QuadPart - workload_start.QuadPart);

    bool render_succeeded = resource_workload_succeeded;
    LARGE_INTEGER present_start{};
    LARGE_INTEGER present_end{};
    QueryPerformanceCounter(&present_start);
    for (unsigned long frame = 0; render_succeeded && frame < options->frames; ++frame) {
        const float red = static_cast<float>(frame % 60) / 60.0F;
        const float color[]{red, 0.2F, 1.0F - red, 1.0F};
        context->ClearRenderTargetView(render_target.Get(), color);
        render_succeeded = SUCCEEDED(swap_chain->Present(0, 0));
    }
    QueryPerformanceCounter(&present_end);
    timing.present_qpc_ticks = static_cast<std::uint64_t>(
        present_end.QuadPart - present_start.QuadPart);

    if (options->hold_ms != 0) {
        Sleep(options->hold_ms);
    }
    collect_gpu_timing(
        context.Get(),
        gpu_timing_queries,
        timing,
        options->gpu_timeout_ms);
    const auto refresh_result = refresh();

    FluidHookSnapshotV1 snapshot{};
    snapshot.struct_size = sizeof(snapshot);
    const auto snapshot_result = read_snapshot(&snapshot);
    const auto resource_metrics_matched =
        SUCCEEDED(refresh_result) &&
        SUCCEEDED(snapshot_result) &&
        snapshot_matches_workload(snapshot, *options);
    const auto detach_result = detach();
    const auto original_pointer_restored =
        SUCCEEDED(detach_result) && is_attached() == FALSE;
    LARGE_INTEGER readback_start{};
    LARGE_INTEGER readback_end{};
    QueryPerformanceCounter(&readback_start);
    const auto content = original_pointer_restored
        ? verify_workload_content(device.Get(), context.Get(), workload_resources)
        : ContentVerification{};
    QueryPerformanceCounter(&readback_end);
    timing.readback_qpc_ticks = static_cast<std::uint64_t>(
        readback_end.QuadPart - readback_start.QuadPart);

    const auto report = build_report(
        *options,
        snapshot,
        attach_result,
        refresh_result,
        snapshot_result,
        detach_result,
        original_pointer_restored,
        render_succeeded,
        resource_workload_succeeded,
        resource_metrics_matched,
        context_vtable_pointer_stable,
        context_copy_entry_stable,
        content,
        timing,
        adapter_identity);
    std::cout << report;
    if (!options->output_path.empty()) {
        std::ofstream output(options->output_path, std::ios::binary);
        output << report;
        if (!output) {
            render_succeeded = false;
        }
    }

    if (original_pointer_restored) {
        FreeLibrary(hook_module);
    }
    DestroyWindow(window);
    UnregisterClassW(window_class_name, instance);

    const auto passed = render_succeeded &&
        resource_workload_succeeded &&
        resource_metrics_matched &&
        original_pointer_restored &&
        content.readback_succeeded &&
        content.buffer_contents_equal &&
        content.texture_contents_equal &&
        snapshot.present_count == options->frames;
    return passed ? 0 : 6;
}
