#include "fluidruntime_hook_api.h"

#include <d3d11.h>
#include <wrl/client.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <optional>
#include <sstream>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

namespace {

using Microsoft::WRL::ComPtr;
using DelayedCreateBufferFunction = HRESULT(STDMETHODCALLTYPE*)(
    ID3D11Device*,
    const D3D11_BUFFER_DESC*,
    const D3D11_SUBRESOURCE_DATA*,
    ID3D11Buffer**);
using DelayedReleaseFunction = ULONG(STDMETHODCALLTYPE*)(IUnknown*);

constexpr UINT kBufferBytes = 4096;
constexpr UINT kSustainedBufferBytes = 4 * 1024 * 1024;
constexpr UINT kReadbackBufferBytes = 4 * 1024 * 1024;
constexpr UINT kUploadBufferBytes = 4 * 1024 * 1024;
constexpr UINT kUpdateUploadBufferBytes = 4 * 1024 * 1024;
constexpr unsigned long kMaximumSustainedCopyCount =
    static_cast<unsigned long>(fluid_hook_control_max_action_budget);
constexpr unsigned long kMaximumReadbackCopyCount =
    static_cast<unsigned long>(fluid_hook_control_max_action_budget);
constexpr unsigned long kMaximumUploadCopyCount =
    static_cast<unsigned long>(fluid_hook_control_max_action_budget);
constexpr unsigned long kMaximumUpdateUploadCount =
    static_cast<unsigned long>(fluid_hook_control_max_action_budget);
constexpr UINT kCooperativeBufferBytes = 256;
constexpr UINT kAutomaticBufferBytes = 512;
constexpr int kAutomaticLifetimeCycles = 64;
constexpr UINT kTextureWidth = 64;
constexpr UINT kTextureHeight = 64;
constexpr UINT kSubresourceTextureWidth = 32;
constexpr UINT kSubresourceTextureHeight = 32;
constexpr UINT kSubresourceTextureMipLevels = 2;
constexpr std::uint64_t kExpectedCopyCount = 6;
constexpr std::uint64_t kExpectedCopyBytes = 49152;
constexpr std::uint64_t kExpectedRedundantCopyCount = 3;
constexpr std::uint64_t kExpectedRedundantCopyBytes = 24576;
constexpr std::uint64_t kExpectedSkippedCopyCount = 1;
constexpr std::uint64_t kExpectedSkippedCopyBytes = kBufferBytes;
constexpr std::uint64_t kExpectedCopySubresourceCount = 11;
constexpr std::uint64_t kExpectedCopySubresourceBytes =
    8 * 16 * 16 * 4 + 2 * 8 * 8 * 4;
constexpr std::uint64_t kExpectedRedundantSubresourceCopyCount = 5;
constexpr std::uint64_t kExpectedRedundantSubresourceCopyBytes = 5 * 16 * 16 * 4;
constexpr std::uint64_t kExpectedGpuViewWriteBytes =
    (kSubresourceTextureWidth * kSubresourceTextureHeight +
        (kSubresourceTextureWidth / 2) * (kSubresourceTextureHeight / 2)) * 4;
constexpr std::uint64_t kExpectedUpdateSubresourceBytes =
    kBufferBytes +
    kSubresourceTextureWidth * kSubresourceTextureHeight * 4 +
    (kSubresourceTextureWidth / 2) * (kSubresourceTextureHeight / 2) * 4;
constexpr std::uint64_t kExpectedResourceRetireCount = 1;
constexpr std::uint64_t kExpectedResourceDestroyCount = kAutomaticLifetimeCycles;
constexpr std::uint64_t kFnvOffsetBasis = 14695981039346656037ULL;
constexpr std::uint64_t kFnvPrime = 1099511628211ULL;

enum class ControlPolicyCase {
    none,
    valid,
    no_opt_in,
    wrong_epoch,
    unknown_action,
    wrong_budget,
    too_long_expiry,
    already_expired,
    accepted_then_expired,
};

std::optional<ControlPolicyCase> parse_control_policy_case(std::wstring_view value) {
    if (value == L"valid") return ControlPolicyCase::valid;
    if (value == L"no-opt-in") return ControlPolicyCase::no_opt_in;
    if (value == L"wrong-epoch") return ControlPolicyCase::wrong_epoch;
    if (value == L"unknown-action") return ControlPolicyCase::unknown_action;
    if (value == L"wrong-budget") return ControlPolicyCase::wrong_budget;
    if (value == L"too-long-expiry") return ControlPolicyCase::too_long_expiry;
    if (value == L"already-expired") return ControlPolicyCase::already_expired;
    if (value == L"accepted-then-expired") {
        return ControlPolicyCase::accepted_then_expired;
    }
    return std::nullopt;
}

const char* control_policy_case_name(ControlPolicyCase value) {
    switch (value) {
    case ControlPolicyCase::valid: return "valid";
    case ControlPolicyCase::no_opt_in: return "no-opt-in";
    case ControlPolicyCase::wrong_epoch: return "wrong-epoch";
    case ControlPolicyCase::unknown_action: return "unknown-action";
    case ControlPolicyCase::wrong_budget: return "wrong-budget";
    case ControlPolicyCase::too_long_expiry: return "too-long-expiry";
    case ControlPolicyCase::already_expired: return "already-expired";
    case ControlPolicyCase::accepted_then_expired: return "accepted-then-expired";
    default: return "none";
    }
}

bool control_policy_opt_in(ControlPolicyCase value) {
    return value != ControlPolicyCase::none && value != ControlPolicyCase::no_opt_in;
}

bool control_policy_accepted(ControlPolicyCase value) {
    return value == ControlPolicyCase::valid ||
        value == ControlPolicyCase::accepted_then_expired;
}

bool control_policy_applies_action(ControlPolicyCase value) {
    return value == ControlPolicyCase::valid;
}

bool control_policy_rejected(ControlPolicyCase value) {
    return value == ControlPolicyCase::wrong_epoch ||
        value == ControlPolicyCase::unknown_action ||
        value == ControlPolicyCase::wrong_budget ||
        value == ControlPolicyCase::too_long_expiry ||
        value == ControlPolicyCase::already_expired;
}

HRESULT expected_control_wait_result(ControlPolicyCase value) {
    if (control_policy_accepted(value)) return S_OK;
    if (value == ControlPolicyCase::no_opt_in) return E_ACCESSDENIED;
    if (control_policy_rejected(value)) return E_INVALIDARG;
    return S_FALSE;
}

std::uint64_t expected_control_acknowledged_epoch(ControlPolicyCase value) {
    if (value == ControlPolicyCase::none || value == ControlPolicyCase::no_opt_in) return 0;
    return value == ControlPolicyCase::wrong_epoch ? 2 : 1;
}

FluidHookControlStatusV1 expected_control_status(ControlPolicyCase value) {
    if (value == ControlPolicyCase::valid) return FluidHookControlStatusV1::exhausted;
    if (value == ControlPolicyCase::accepted_then_expired) {
        return FluidHookControlStatusV1::expired;
    }
    if (control_policy_rejected(value)) return FluidHookControlStatusV1::rejected;
    return FluidHookControlStatusV1::none;
}

struct Options {
    std::wstring hook_path;
    std::wstring output_path;
    unsigned long frames{60};
    unsigned long hold_ms{};
    unsigned long gpu_timeout_ms{1000};
    unsigned long control_timeout_ms{5000};
    unsigned long sustained_copy_count{};
    unsigned long readback_copy_count{};
    unsigned long upload_copy_count{};
    unsigned long update_upload_count{};
    bool use_hardware{};
    bool skip_first_redundant_copy{};
    bool managed_control{};
    ControlPolicyCase control_policy_case{ControlPolicyCase::none};
    bool control_policy_matrix_case{};
    bool automatic_lifetime_tracking{true};
    bool concurrent_lifetime_stress{};
};

struct WorkloadResources {
    ComPtr<ID3D11Buffer> sustained_source_buffer;
    ComPtr<ID3D11Buffer> sustained_destination_buffer;
    ComPtr<ID3D11Buffer> readback_source_buffer;
    ComPtr<ID3D11Buffer> readback_destination_buffer;
    ComPtr<ID3D11Buffer> upload_source_buffer;
    ComPtr<ID3D11Buffer> upload_destination_buffer;
    ComPtr<ID3D11Buffer> update_upload_destination_buffer;
    ComPtr<ID3D11Buffer> update_upload_guard_source_buffer;
    ComPtr<ID3D11Buffer> source_buffer;
    ComPtr<ID3D11Buffer> destination_buffer;
    ComPtr<ID3D11Buffer> dynamic_buffer;
    ComPtr<ID3D11Texture2D> source_texture;
    ComPtr<ID3D11Texture2D> destination_texture;
    ComPtr<ID3D11Texture2D> source_subresource_texture;
    ComPtr<ID3D11Texture2D> destination_subresource_texture;
    ComPtr<ID3D11RenderTargetView> source_mip_zero_render_target_view;
    ComPtr<ID3D11UnorderedAccessView> source_mip_one_unordered_access_view;
};

struct ContentVerification {
    bool readback_succeeded{};
    bool sustained_buffer_contents_equal{true};
    bool readback_buffer_contents_equal{true};
    bool upload_buffer_contents_equal{true};
    bool update_upload_contents_equal{true};
    bool buffer_contents_equal{};
    bool texture_contents_equal{};
    bool subresource_contents_equal{};
    std::uint64_t source_buffer_hash{};
    std::uint64_t destination_buffer_hash{};
    std::uint64_t sustained_source_buffer_hash{};
    std::uint64_t sustained_destination_buffer_hash{};
    std::uint64_t readback_source_buffer_hash{};
    std::uint64_t readback_destination_buffer_hash{};
    std::uint64_t upload_source_buffer_hash{};
    std::uint64_t upload_destination_buffer_hash{};
    std::uint64_t update_upload_destination_buffer_hash{};
    std::uint64_t source_texture_hash{};
    std::uint64_t destination_texture_hash{};
    std::uint64_t source_subresource_hash{};
    std::uint64_t destination_subresource_hash{};
};

struct ReadbackWorkloadVerification {
    bool all_maps_succeeded{true};
    bool all_maps_equal{true};
    std::uint64_t successful_map_count{};
    std::uint64_t expected_hash{};
    std::uint64_t first_map_hash{};
    std::uint64_t final_map_hash{};
};

struct UploadWorkloadVerification {
    bool write_map_succeeded{};
    std::uint64_t expected_hash{};
};

struct UpdateUploadWorkloadVerification {
    bool mutation_applied{};
    bool generation_guard_applied{};
    std::uint64_t initial_hash{};
    std::uint64_t final_hash{};
    std::uint64_t guard_hash{};
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

bool wait_for_control_gate(std::string_view expected) {
    std::string value;
    return std::getline(std::cin, value) && value == expected;
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
        } else if (argument == L"--control-timeout-ms" && index + 1 < argc) {
            const auto control_timeout_ms = parse_positive(argv[++index]);
            if (!control_timeout_ms.has_value() || *control_timeout_ms > 5000) {
                return std::nullopt;
            }
            options.control_timeout_ms = *control_timeout_ms;
        } else if (argument == L"--sustained-copy-count" && index + 1 < argc) {
            const auto sustained_copy_count = parse_positive(argv[++index]);
            if (!sustained_copy_count.has_value() ||
                *sustained_copy_count > kMaximumSustainedCopyCount) {
                return std::nullopt;
            }
            options.sustained_copy_count = *sustained_copy_count;
        } else if (argument == L"--readback-copy-count" && index + 1 < argc) {
            const auto readback_copy_count = parse_positive(argv[++index]);
            if (!readback_copy_count.has_value() ||
                *readback_copy_count > kMaximumReadbackCopyCount) {
                return std::nullopt;
            }
            options.readback_copy_count = *readback_copy_count;
        } else if (argument == L"--upload-copy-count" && index + 1 < argc) {
            const auto upload_copy_count = parse_positive(argv[++index]);
            if (!upload_copy_count.has_value() ||
                *upload_copy_count > kMaximumUploadCopyCount) {
                return std::nullopt;
            }
            options.upload_copy_count = *upload_copy_count;
        } else if (argument == L"--update-upload-count" && index + 1 < argc) {
            const auto update_upload_count = parse_positive(argv[++index]);
            if (!update_upload_count.has_value() ||
                *update_upload_count > kMaximumUpdateUploadCount) {
                return std::nullopt;
            }
            options.update_upload_count = *update_upload_count;
        } else if (argument == L"--hardware") {
            options.use_hardware = true;
        } else if (argument == L"--skip-first-redundant-copy") {
            options.skip_first_redundant_copy = true;
        } else if (argument == L"--managed-control") {
            options.managed_control = true;
            options.control_policy_case = ControlPolicyCase::valid;
        } else if (argument == L"--control-policy-case" && index + 1 < argc) {
            const auto policy_case = parse_control_policy_case(argv[++index]);
            if (!policy_case.has_value()) {
                return std::nullopt;
            }
            options.managed_control = true;
            options.control_policy_case = *policy_case;
            options.control_policy_matrix_case = true;
        } else if (argument == L"--cooperative-lifetime") {
            options.automatic_lifetime_tracking = false;
        } else if (argument == L"--concurrent-lifetime-stress") {
            options.concurrent_lifetime_stress = true;
        } else {
            return std::nullopt;
        }
    }

    const auto specialized_workload_count =
        (options.sustained_copy_count != 0 ? 1 : 0) +
        (options.readback_copy_count != 0 ? 1 : 0) +
        (options.upload_copy_count != 0 ? 1 : 0) +
        (options.update_upload_count != 0 ? 1 : 0);
    if (options.hook_path.empty() ||
        specialized_workload_count > 1 ||
        (specialized_workload_count != 0 &&
            options.skip_first_redundant_copy) ||
        (options.managed_control &&
            (options.skip_first_redundant_copy ||
             !options.automatic_lifetime_tracking ||
             options.concurrent_lifetime_stress))) {
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

bool map_and_verify_readback(
    ID3D11DeviceContext* context,
    ID3D11Buffer* staging_buffer,
    const std::vector<std::uint32_t>& expected,
    ReadbackWorkloadVerification& verification) {
    if (context == nullptr || staging_buffer == nullptr || expected.empty()) {
        verification.all_maps_succeeded = false;
        return false;
    }

    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (FAILED(context->Map(
            staging_buffer,
            0,
            D3D11_MAP_READ,
            0,
            &mapped)) ||
        mapped.pData == nullptr) {
        verification.all_maps_succeeded = false;
        return false;
    }

    const auto byte_count = expected.size() * sizeof(expected.front());
    const auto hash = hash_bytes(kFnvOffsetBasis, mapped.pData, byte_count);
    const auto equal = std::memcmp(mapped.pData, expected.data(), byte_count) == 0;
    context->Unmap(staging_buffer, 0);

    ++verification.successful_map_count;
    if (verification.successful_map_count == 1) {
        verification.first_map_hash = hash;
    }
    verification.final_map_hash = hash;
    verification.all_maps_equal = verification.all_maps_equal && equal;
    return equal;
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

std::optional<std::vector<unsigned char>> map_readable_buffer(
    ID3D11DeviceContext* context,
    ID3D11Buffer* buffer) {
    if (context == nullptr || buffer == nullptr) {
        return std::nullopt;
    }

    D3D11_BUFFER_DESC description{};
    buffer->GetDesc(&description);
    if (description.Usage != D3D11_USAGE_STAGING ||
        (description.CPUAccessFlags & D3D11_CPU_ACCESS_READ) == 0) {
        return std::nullopt;
    }

    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (FAILED(context->Map(buffer, 0, D3D11_MAP_READ, 0, &mapped)) ||
        mapped.pData == nullptr) {
        return std::nullopt;
    }
    std::vector<unsigned char> data(description.ByteWidth);
    std::memcpy(data.data(), mapped.pData, data.size());
    context->Unmap(buffer, 0);
    return data;
}

std::optional<std::vector<unsigned char>> readback_texture(
    ID3D11Device* device,
    ID3D11DeviceContext* context,
    ID3D11Texture2D* texture,
    UINT subresource = 0) {
    if (device == nullptr || context == nullptr || texture == nullptr) {
        return std::nullopt;
    }

    D3D11_TEXTURE2D_DESC description{};
    texture->GetDesc(&description);
    if (description.Format != DXGI_FORMAT_R8G8B8A8_UNORM ||
        description.MipLevels == 0 ||
        subresource >= description.MipLevels * description.ArraySize) {
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

    context->CopySubresourceRegion(
        staging.Get(),
        subresource,
        0,
        0,
        0,
        texture,
        subresource,
        nullptr);
    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (FAILED(context->Map(
            staging.Get(),
            subresource,
            D3D11_MAP_READ,
            0,
            &mapped))) {
        return std::nullopt;
    }
    const auto mip = subresource % description.MipLevels;
    const auto width = std::max(1U, description.Width >> mip);
    const auto height = std::max(1U, description.Height >> mip);
    const auto logical_row_bytes = static_cast<size_t>(width) * 4;
    std::vector<unsigned char> data(logical_row_bytes * height);
    const auto* row = static_cast<const unsigned char*>(mapped.pData);
    for (UINT y = 0; y < height; ++y) {
        std::memcpy(data.data() + y * logical_row_bytes, row, logical_row_bytes);
        row += mapped.RowPitch;
    }
    context->Unmap(staging.Get(), subresource);
    return data;
}

ContentVerification verify_workload_content(
    ID3D11Device* device,
    ID3D11DeviceContext* context,
    const WorkloadResources& resources) {
    ContentVerification result;
    const auto has_sustained_buffers =
        resources.sustained_source_buffer.Get() != nullptr ||
        resources.sustained_destination_buffer.Get() != nullptr;
    const auto has_readback_buffers =
        resources.readback_source_buffer.Get() != nullptr ||
        resources.readback_destination_buffer.Get() != nullptr;
    const auto has_upload_buffers =
        resources.upload_source_buffer.Get() != nullptr ||
        resources.upload_destination_buffer.Get() != nullptr;
    const auto has_update_upload_buffer =
        resources.update_upload_destination_buffer.Get() != nullptr;
    std::optional<std::vector<unsigned char>> sustained_source_buffer;
    std::optional<std::vector<unsigned char>> sustained_destination_buffer;
    if (has_sustained_buffers) {
        sustained_source_buffer = readback_buffer(
            device,
            context,
            resources.sustained_source_buffer.Get());
        sustained_destination_buffer = readback_buffer(
            device,
            context,
            resources.sustained_destination_buffer.Get());
    }
    std::optional<std::vector<unsigned char>> readback_source_buffer;
    std::optional<std::vector<unsigned char>> readback_destination_buffer;
    if (has_readback_buffers) {
        readback_source_buffer = readback_buffer(
            device,
            context,
            resources.readback_source_buffer.Get());
        readback_destination_buffer = map_readable_buffer(
            context,
            resources.readback_destination_buffer.Get());
    }
    std::optional<std::vector<unsigned char>> upload_source_buffer;
    std::optional<std::vector<unsigned char>> upload_destination_buffer;
    if (has_upload_buffers) {
        upload_source_buffer = map_readable_buffer(
            context,
            resources.upload_source_buffer.Get());
        upload_destination_buffer = readback_buffer(
            device,
            context,
            resources.upload_destination_buffer.Get());
    }
    std::optional<std::vector<unsigned char>> update_upload_destination_buffer;
    if (has_update_upload_buffer) {
        update_upload_destination_buffer = readback_buffer(
            device,
            context,
            resources.update_upload_destination_buffer.Get());
    }
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
    const auto source_subresource = readback_texture(
        device,
        context,
        resources.source_subresource_texture.Get(),
        1);
    const auto destination_subresource = readback_texture(
        device,
        context,
        resources.destination_subresource_texture.Get(),
        1);
    result.readback_succeeded =
        (!has_sustained_buffers ||
            (sustained_source_buffer.has_value() &&
             sustained_destination_buffer.has_value())) &&
        (!has_readback_buffers ||
            (readback_source_buffer.has_value() &&
             readback_destination_buffer.has_value())) &&
        (!has_upload_buffers ||
            (upload_source_buffer.has_value() &&
             upload_destination_buffer.has_value())) &&
        (!has_update_upload_buffer ||
            update_upload_destination_buffer.has_value()) &&
        source_buffer.has_value() &&
        destination_buffer.has_value() &&
        source_texture.has_value() &&
        destination_texture.has_value() &&
        source_subresource.has_value() &&
        destination_subresource.has_value();
    if (!result.readback_succeeded) {
        return result;
    }

    if (has_sustained_buffers) {
        result.sustained_source_buffer_hash = hash_bytes(
            kFnvOffsetBasis,
            sustained_source_buffer->data(),
            sustained_source_buffer->size());
        result.sustained_destination_buffer_hash = hash_bytes(
            kFnvOffsetBasis,
            sustained_destination_buffer->data(),
            sustained_destination_buffer->size());
        result.sustained_buffer_contents_equal =
            *sustained_source_buffer == *sustained_destination_buffer;
    }
    if (has_readback_buffers) {
        result.readback_source_buffer_hash = hash_bytes(
            kFnvOffsetBasis,
            readback_source_buffer->data(),
            readback_source_buffer->size());
        result.readback_destination_buffer_hash = hash_bytes(
            kFnvOffsetBasis,
            readback_destination_buffer->data(),
            readback_destination_buffer->size());
        result.readback_buffer_contents_equal =
            *readback_source_buffer == *readback_destination_buffer;
    }
    if (has_upload_buffers) {
        result.upload_source_buffer_hash = hash_bytes(
            kFnvOffsetBasis,
            upload_source_buffer->data(),
            upload_source_buffer->size());
        result.upload_destination_buffer_hash = hash_bytes(
            kFnvOffsetBasis,
            upload_destination_buffer->data(),
            upload_destination_buffer->size());
        result.upload_buffer_contents_equal =
            *upload_source_buffer == *upload_destination_buffer;
    }
    if (has_update_upload_buffer) {
        result.update_upload_destination_buffer_hash = hash_bytes(
            kFnvOffsetBasis,
            update_upload_destination_buffer->data(),
            update_upload_destination_buffer->size());
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
    result.source_subresource_hash = hash_bytes(
        kFnvOffsetBasis,
        source_subresource->data(),
        source_subresource->size());
    result.destination_subresource_hash = hash_bytes(
        kFnvOffsetBasis,
        destination_subresource->data(),
        destination_subresource->size());
    result.buffer_contents_equal = *source_buffer == *destination_buffer;
    result.texture_contents_equal = *source_texture == *destination_texture;
    result.subresource_contents_equal =
        *source_subresource == *destination_subresource;
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
    unsigned long sustained_copy_count,
    unsigned long readback_copy_count,
    unsigned long upload_copy_count,
    unsigned long update_upload_count,
    ReadbackWorkloadVerification& readback_verification,
    UploadWorkloadVerification& upload_verification,
    UpdateUploadWorkloadVerification& update_upload_verification,
    bool& context_vtable_pointer_stable,
    bool& context_copy_entry_stable,
    bool& context_subresource_copy_entry_stable,
    bool& context_gpu_view_write_entries_stable) {
    HRESULT result = S_OK;
    if (sustained_copy_count != 0) {
        std::vector<std::uint32_t> sustained_data(
            kSustainedBufferBytes / sizeof(std::uint32_t));
        for (size_t index = 0; index < sustained_data.size(); ++index) {
            sustained_data[index] =
                0xC0010000U ^ static_cast<std::uint32_t>(index);
        }

        D3D11_BUFFER_DESC sustained_description{};
        sustained_description.ByteWidth = kSustainedBufferBytes;
        sustained_description.Usage = D3D11_USAGE_DEFAULT;
        D3D11_SUBRESOURCE_DATA sustained_initial_data{};
        sustained_initial_data.pSysMem = sustained_data.data();
        result = device->CreateBuffer(
            &sustained_description,
            &sustained_initial_data,
            &resources.sustained_source_buffer);
        if (FAILED(result)) {
            return false;
        }
        result = device->CreateBuffer(
            &sustained_description,
            nullptr,
            &resources.sustained_destination_buffer);
        if (FAILED(result)) {
            return false;
        }

        context->CopyResource(
            resources.sustained_destination_buffer.Get(),
            resources.sustained_source_buffer.Get());
        for (unsigned long copy = 0; copy < sustained_copy_count; ++copy) {
            context->CopyResource(
                resources.sustained_destination_buffer.Get(),
                resources.sustained_source_buffer.Get());
        }
    }

    if (readback_copy_count != 0) {
        std::vector<std::uint32_t> readback_data(
            kReadbackBufferBytes / sizeof(std::uint32_t));
        for (size_t index = 0; index < readback_data.size(); ++index) {
            readback_data[index] =
                0xF10D0000U ^ static_cast<std::uint32_t>(index * 2654435761U);
        }
        readback_verification.expected_hash = hash_bytes(
            kFnvOffsetBasis,
            readback_data.data(),
            readback_data.size() * sizeof(readback_data.front()));

        D3D11_BUFFER_DESC source_description{};
        source_description.ByteWidth = kReadbackBufferBytes;
        source_description.Usage = D3D11_USAGE_DEFAULT;
        D3D11_SUBRESOURCE_DATA source_initial_data{};
        source_initial_data.pSysMem = readback_data.data();
        result = device->CreateBuffer(
            &source_description,
            &source_initial_data,
            &resources.readback_source_buffer);
        if (FAILED(result)) {
            return false;
        }

        D3D11_BUFFER_DESC staging_description{};
        staging_description.ByteWidth = kReadbackBufferBytes;
        staging_description.Usage = D3D11_USAGE_STAGING;
        staging_description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        result = device->CreateBuffer(
            &staging_description,
            nullptr,
            &resources.readback_destination_buffer);
        if (FAILED(result)) {
            return false;
        }

        const auto copy_and_read = [&]() {
            context->CopyResource(
                resources.readback_destination_buffer.Get(),
                resources.readback_source_buffer.Get());
            return map_and_verify_readback(
                context,
                resources.readback_destination_buffer.Get(),
                readback_data,
                readback_verification);
        };
        if (!copy_and_read()) {
            return false;
        }
        for (unsigned long copy = 0; copy < readback_copy_count; ++copy) {
            if (!copy_and_read()) {
                return false;
            }
        }
    }

    if (upload_copy_count != 0) {
        std::vector<std::uint32_t> upload_data(
            kUploadBufferBytes / sizeof(std::uint32_t));
        for (size_t index = 0; index < upload_data.size(); ++index) {
            upload_data[index] =
                0xA1100000U ^ static_cast<std::uint32_t>(index * 2246822519U);
        }
        upload_verification.expected_hash = hash_bytes(
            kFnvOffsetBasis,
            upload_data.data(),
            upload_data.size() * sizeof(upload_data.front()));

        D3D11_BUFFER_DESC staging_description{};
        staging_description.ByteWidth = kUploadBufferBytes;
        staging_description.Usage = D3D11_USAGE_STAGING;
        staging_description.CPUAccessFlags =
            D3D11_CPU_ACCESS_READ | D3D11_CPU_ACCESS_WRITE;
        result = device->CreateBuffer(
            &staging_description,
            nullptr,
            &resources.upload_source_buffer);
        if (FAILED(result)) {
            return false;
        }

        D3D11_BUFFER_DESC destination_description{};
        destination_description.ByteWidth = kUploadBufferBytes;
        destination_description.Usage = D3D11_USAGE_DEFAULT;
        result = device->CreateBuffer(
            &destination_description,
            nullptr,
            &resources.upload_destination_buffer);
        if (FAILED(result)) {
            return false;
        }

        D3D11_MAPPED_SUBRESOURCE upload_mapping{};
        result = context->Map(
            resources.upload_source_buffer.Get(),
            0,
            D3D11_MAP_WRITE,
            0,
            &upload_mapping);
        if (FAILED(result) || upload_mapping.pData == nullptr) {
            return false;
        }
        std::memcpy(
            upload_mapping.pData,
            upload_data.data(),
            upload_data.size() * sizeof(upload_data.front()));
        context->Unmap(resources.upload_source_buffer.Get(), 0);
        upload_verification.write_map_succeeded = true;

        context->CopyResource(
            resources.upload_destination_buffer.Get(),
            resources.upload_source_buffer.Get());
        for (unsigned long copy = 0; copy < upload_copy_count; ++copy) {
            context->CopyResource(
                resources.upload_destination_buffer.Get(),
                resources.upload_source_buffer.Get());
        }
    }

    if (update_upload_count != 0) {
        std::vector<std::uint32_t> initial_data(
            kUpdateUploadBufferBytes / sizeof(std::uint32_t));
        for (size_t index = 0; index < initial_data.size(); ++index) {
            initial_data[index] =
                0xD1200000U ^ static_cast<std::uint32_t>(index * 3266489917U);
        }
        auto final_data = initial_data;
        final_data[final_data.size() / 2] ^= 0x00000001U;
        auto guard_data = final_data;
        for (size_t index = 0; index < guard_data.size(); ++index) {
            guard_data[index] ^= 0x5A5A0000U;
        }
        update_upload_verification.mutation_applied =
            final_data[final_data.size() / 2] !=
                initial_data[initial_data.size() / 2];
        update_upload_verification.initial_hash = hash_bytes(
            kFnvOffsetBasis,
            initial_data.data(),
            initial_data.size() * sizeof(initial_data.front()));
        update_upload_verification.final_hash = hash_bytes(
            kFnvOffsetBasis,
            final_data.data(),
            final_data.size() * sizeof(final_data.front()));
        update_upload_verification.guard_hash = hash_bytes(
            kFnvOffsetBasis,
            guard_data.data(),
            guard_data.size() * sizeof(guard_data.front()));

        D3D11_BUFFER_DESC description{};
        description.ByteWidth = kUpdateUploadBufferBytes;
        description.Usage = D3D11_USAGE_DEFAULT;
        result = device->CreateBuffer(
            &description,
            nullptr,
            &resources.update_upload_destination_buffer);
        if (FAILED(result)) {
            return false;
        }
        D3D11_SUBRESOURCE_DATA guard_initial_data{};
        guard_initial_data.pSysMem = guard_data.data();
        result = device->CreateBuffer(
            &description,
            &guard_initial_data,
            &resources.update_upload_guard_source_buffer);
        if (FAILED(result)) {
            return false;
        }

        context->UpdateSubresource(
            resources.update_upload_destination_buffer.Get(),
            0,
            nullptr,
            initial_data.data(),
            0,
            0);
        const auto initial_repeat_count = update_upload_count / 2;
        for (unsigned long update = 0;
             update < initial_repeat_count;
             ++update) {
            context->UpdateSubresource(
                resources.update_upload_destination_buffer.Get(),
                0,
                nullptr,
                initial_data.data(),
                0,
                0);
        }

        context->UpdateSubresource(
            resources.update_upload_destination_buffer.Get(),
            0,
            nullptr,
            final_data.data(),
            0,
            0);
        const auto final_repeat_count = update_upload_count - initial_repeat_count;
        const auto final_repeats_before_guard = final_repeat_count / 2;
        for (unsigned long update = 0;
             update < final_repeats_before_guard;
             ++update) {
            context->UpdateSubresource(
                resources.update_upload_destination_buffer.Get(),
                0,
                nullptr,
                final_data.data(),
                0,
                0);
        }

        context->CopyResource(
            resources.update_upload_destination_buffer.Get(),
            resources.update_upload_guard_source_buffer.Get());
        update_upload_verification.generation_guard_applied = true;
        context->UpdateSubresource(
            resources.update_upload_destination_buffer.Get(),
            0,
            nullptr,
            final_data.data(),
            0,
            0);
        for (unsigned long update = final_repeats_before_guard;
             update < final_repeat_count;
             ++update) {
            context->UpdateSubresource(
                resources.update_upload_destination_buffer.Get(),
                0,
                nullptr,
                final_data.data(),
                0,
                0);
        }
    }

    std::array<std::uint32_t, kBufferBytes / sizeof(std::uint32_t)> buffer_data{};
    for (size_t index = 0; index < buffer_data.size(); ++index) {
        buffer_data[index] = static_cast<std::uint32_t>(index);
    }

    D3D11_BUFFER_DESC default_buffer_description{};
    default_buffer_description.ByteWidth = kBufferBytes;
    default_buffer_description.Usage = D3D11_USAGE_DEFAULT;
    D3D11_SUBRESOURCE_DATA buffer_initial_data{};
    buffer_initial_data.pSysMem = buffer_data.data();
    result = device->CreateBuffer(
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
    const auto subresource_copy_entry_before_update = context_vtable_before_update[46];
    const auto copy_entry_before_update = context_vtable_before_update[47];
    const auto clear_render_target_entry_before_update = context_vtable_before_update[50];
    const auto clear_uav_float_entry_before_update = context_vtable_before_update[52];
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
    context_subresource_copy_entry_stable =
        context_vtable_after_update[46] == subresource_copy_entry_before_update;
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

    std::array<std::uint32_t,
        kSubresourceTextureWidth * kSubresourceTextureHeight> mip_zero_data{};
    std::array<std::uint32_t,
        (kSubresourceTextureWidth / 2) * (kSubresourceTextureHeight / 2)>
        mip_one_data{};
    for (size_t index = 0; index < mip_zero_data.size(); ++index) {
        mip_zero_data[index] = 0xFF100000U | static_cast<std::uint32_t>(index);
    }
    for (size_t index = 0; index < mip_one_data.size(); ++index) {
        mip_one_data[index] = 0xFF001000U | static_cast<std::uint32_t>(index);
    }

    D3D11_TEXTURE2D_DESC subresource_texture_description{};
    subresource_texture_description.Width = kSubresourceTextureWidth;
    subresource_texture_description.Height = kSubresourceTextureHeight;
    subresource_texture_description.MipLevels = kSubresourceTextureMipLevels;
    subresource_texture_description.ArraySize = 1;
    subresource_texture_description.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    subresource_texture_description.SampleDesc.Count = 1;
    subresource_texture_description.Usage = D3D11_USAGE_DEFAULT;
    subresource_texture_description.BindFlags =
        D3D11_BIND_RENDER_TARGET | D3D11_BIND_UNORDERED_ACCESS;
    std::array<D3D11_SUBRESOURCE_DATA, kSubresourceTextureMipLevels>
        subresource_initial_data{};
    subresource_initial_data[0].pSysMem = mip_zero_data.data();
    subresource_initial_data[0].SysMemPitch =
        kSubresourceTextureWidth * sizeof(std::uint32_t);
    subresource_initial_data[1].pSysMem = mip_one_data.data();
    subresource_initial_data[1].SysMemPitch =
        (kSubresourceTextureWidth / 2) * sizeof(std::uint32_t);
    result = device->CreateTexture2D(
        &subresource_texture_description,
        subresource_initial_data.data(),
        &resources.source_subresource_texture);
    if (FAILED(result)) {
        return false;
    }

    D3D11_RENDER_TARGET_VIEW_DESC render_target_view_description{};
    render_target_view_description.Format = subresource_texture_description.Format;
    render_target_view_description.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
    render_target_view_description.Texture2D.MipSlice = 0;
    result = device->CreateRenderTargetView(
        resources.source_subresource_texture.Get(),
        &render_target_view_description,
        &resources.source_mip_zero_render_target_view);
    if (FAILED(result)) {
        return false;
    }

    D3D11_UNORDERED_ACCESS_VIEW_DESC unordered_access_view_description{};
    unordered_access_view_description.Format = subresource_texture_description.Format;
    unordered_access_view_description.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;
    unordered_access_view_description.Texture2D.MipSlice = 1;
    result = device->CreateUnorderedAccessView(
        resources.source_subresource_texture.Get(),
        &unordered_access_view_description,
        &resources.source_mip_one_unordered_access_view);
    if (FAILED(result)) {
        return false;
    }
    result = device->CreateTexture2D(
        &subresource_texture_description,
        nullptr,
        &resources.destination_subresource_texture);
    if (FAILED(result)) {
        return false;
    }

    const auto copy_mip_one = [&]() {
        context->CopySubresourceRegion(
            resources.destination_subresource_texture.Get(),
            1,
            0,
            0,
            0,
            resources.source_subresource_texture.Get(),
            1,
            nullptr);
    };
    copy_mip_one();
    const D3D11_BOX empty_box{0, 0, 0, 0, 1, 1};
    context->CopySubresourceRegion(
        resources.destination_subresource_texture.Get(),
        1,
        0,
        0,
        0,
        resources.source_subresource_texture.Get(),
        1,
        &empty_box);
    copy_mip_one();

    for (auto& value : mip_zero_data) {
        value ^= 0x0000FF00U;
    }
    context->UpdateSubresource(
        resources.source_subresource_texture.Get(),
        0,
        nullptr,
        mip_zero_data.data(),
        kSubresourceTextureWidth * sizeof(std::uint32_t),
        0);
    copy_mip_one();

    const FLOAT render_target_clear[4]{0.25F, 0.5F, 0.75F, 1.0F};
    context->ClearRenderTargetView(
        resources.source_mip_zero_render_target_view.Get(),
        render_target_clear);
    copy_mip_one();

    for (auto& value : mip_one_data) {
        value ^= 0x00FF0000U;
    }
    context->UpdateSubresource(
        resources.source_subresource_texture.Get(),
        1,
        nullptr,
        mip_one_data.data(),
        (kSubresourceTextureWidth / 2) * sizeof(std::uint32_t),
        0);
    const D3D11_BOX partial_box{0, 0, 0, 8, 8, 1};
    context->CopySubresourceRegion(
        resources.destination_subresource_texture.Get(),
        1,
        0,
        0,
        0,
        resources.source_subresource_texture.Get(),
        1,
        &partial_box);
    context->CopySubresourceRegion(
        resources.destination_subresource_texture.Get(),
        1,
        8,
        0,
        0,
        resources.source_subresource_texture.Get(),
        1,
        &partial_box);
    copy_mip_one();
    copy_mip_one();
    const FLOAT unordered_access_clear[4]{0.125F, 0.375F, 0.625F, 1.0F};
    context->ClearUnorderedAccessViewFloat(
        resources.source_mip_one_unordered_access_view.Get(),
        unordered_access_clear);
    copy_mip_one();
    copy_mip_one();

    auto** context_vtable_after_gpu_writes = *reinterpret_cast<void***>(context);
    context_gpu_view_write_entries_stable =
        context_vtable_after_gpu_writes[50] == clear_render_target_entry_before_update &&
        context_vtable_after_gpu_writes[52] == clear_uav_float_entry_before_update;
    return true;
}

bool run_resource_lifetime_workload(
    ID3D11Device* device,
    FluidHookRetireResourceFunction retire_resource,
    bool automatic_lifetime_tracking) {
    if (device == nullptr || retire_resource == nullptr) {
        return false;
    }
    if (retire_resource(nullptr) != E_POINTER) {
        return false;
    }

    D3D11_BUFFER_DESC cooperative_description{};
    cooperative_description.ByteWidth = kCooperativeBufferBytes;
    cooperative_description.Usage = D3D11_USAGE_DEFAULT;
    ComPtr<ID3D11Buffer> cooperative_buffer;
    if (FAILED(device->CreateBuffer(
            &cooperative_description,
            nullptr,
            &cooperative_buffer)) ||
        retire_resource(cooperative_buffer.Get()) != S_OK ||
        retire_resource(cooperative_buffer.Get()) != HRESULT_FROM_WIN32(ERROR_NOT_FOUND)) {
        return false;
    }
    cooperative_buffer.Reset();

    D3D11_BUFFER_DESC automatic_description{};
    automatic_description.ByteWidth = kAutomaticBufferBytes;
    automatic_description.Usage = D3D11_USAGE_DEFAULT;
    for (int index = 0; index < kAutomaticLifetimeCycles; ++index) {
        ComPtr<ID3D11Buffer> buffer;
        if (FAILED(device->CreateBuffer(&automatic_description, nullptr, &buffer))) {
            return false;
        }
        if (!automatic_lifetime_tracking && retire_resource(buffer.Get()) != S_OK) {
            return false;
        }
        buffer.Reset();
    }
    return true;
}

bool run_concurrent_lifetime_detach_stress(
    ID3D11Device* device,
    IDXGISwapChain* swap_chain,
    FluidHookAttachExFunction attach_ex,
    FluidHookDetachFunction detach,
    FluidHookIsAttachedFunction is_attached,
    FluidHookReadSnapshotFunction read_snapshot,
    unsigned long& completed_cycles,
    bool& stale_create_forwarded,
    bool& stale_release_forwarded,
    bool& stale_calls_observation_neutral,
    bool& reattach_rejected,
    FluidHookSnapshotV1& pre_detach_snapshot,
    HRESULT& snapshot_result,
    HRESULT& detach_result,
    HRESULT& reattach_result,
    HRESULT& final_detach_result) {
    if (device == nullptr || swap_chain == nullptr || attach_ex == nullptr ||
        detach == nullptr || is_attached == nullptr || read_snapshot == nullptr) {
        return false;
    }

    D3D11_BUFFER_DESC delayed_description{};
    delayed_description.ByteWidth = kAutomaticBufferBytes;
    delayed_description.Usage = D3D11_USAGE_DEFAULT;
    ComPtr<ID3D11Buffer> delayed_buffer;
    if (FAILED(device->CreateBuffer(
            &delayed_description,
            nullptr,
            &delayed_buffer))) {
        return false;
    }
    auto** device_vtable = *reinterpret_cast<void***>(device);
    auto** buffer_vtable = *reinterpret_cast<void***>(delayed_buffer.Get());
    const auto delayed_create = reinterpret_cast<DelayedCreateBufferFunction>(
        device_vtable[3]);
    const auto delayed_release = reinterpret_cast<DelayedReleaseFunction>(
        buffer_vtable[2]);
    auto* delayed_buffer_raw = delayed_buffer.Detach();

    std::atomic<bool> start{false};
    std::atomic<bool> stop{false};
    std::atomic<bool> worker_succeeded{true};
    std::atomic<unsigned long> completed{0};
    std::thread worker([&] {
        while (!start.load(std::memory_order_acquire)) {
            std::this_thread::yield();
        }
        D3D11_BUFFER_DESC description{};
        description.ByteWidth = kAutomaticBufferBytes;
        description.Usage = D3D11_USAGE_DEFAULT;
        while (!stop.load(std::memory_order_acquire) &&
               completed.load(std::memory_order_relaxed) < 100000) {
            ComPtr<ID3D11Buffer> buffer;
            if (FAILED(device->CreateBuffer(&description, nullptr, &buffer))) {
                worker_succeeded.store(false, std::memory_order_release);
                break;
            }
            buffer.Reset();
            completed.fetch_add(1, std::memory_order_release);
        }
    });

    start.store(true, std::memory_order_release);
    for (int waited_ms = 0;
         completed.load(std::memory_order_acquire) < 64 &&
             worker_succeeded.load(std::memory_order_acquire) &&
             waited_ms < 5000;
         ++waited_ms) {
        Sleep(1);
    }
    pre_detach_snapshot.struct_size = sizeof(pre_detach_snapshot);
    snapshot_result = read_snapshot(&pre_detach_snapshot);
    detach_result = detach();
    stop.store(true, std::memory_order_release);
    worker.join();

    FluidHookSnapshotV1 before_stale_snapshot{};
    before_stale_snapshot.struct_size = sizeof(before_stale_snapshot);
    const auto before_stale_snapshot_result =
        read_snapshot(&before_stale_snapshot);
    ID3D11Buffer* post_detach_buffer{};
    const auto delayed_create_result = delayed_create(
        device,
        &delayed_description,
        nullptr,
        &post_detach_buffer);
    stale_create_forwarded = SUCCEEDED(delayed_create_result) &&
        post_detach_buffer != nullptr;
    if (post_detach_buffer != nullptr) {
        stale_create_forwarded = stale_create_forwarded &&
            delayed_release(post_detach_buffer) == 0;
    }
    stale_release_forwarded = delayed_release(delayed_buffer_raw) == 0;
    FluidHookSnapshotV1 after_stale_snapshot{};
    after_stale_snapshot.struct_size = sizeof(after_stale_snapshot);
    const auto after_stale_snapshot_result = read_snapshot(&after_stale_snapshot);
    stale_calls_observation_neutral =
        SUCCEEDED(before_stale_snapshot_result) &&
        SUCCEEDED(after_stale_snapshot_result) &&
        std::memcmp(
            &before_stale_snapshot,
            &after_stale_snapshot,
            sizeof(before_stale_snapshot)) == 0;

    FluidHookAttachOptionsV1 reattach_options{};
    reattach_options.struct_size = sizeof(reattach_options);
    reattach_options.abi_version = fluid_hook_attach_options_abi_version;
    reattach_options.flags = fluid_hook_attach_flag_track_resource_lifetime;
    reattach_result = attach_ex(swap_chain, &reattach_options);
    final_detach_result = detach();
    FluidHookSnapshotV1 after_rejected_reattach_snapshot{};
    after_rejected_reattach_snapshot.struct_size =
        sizeof(after_rejected_reattach_snapshot);
    const auto after_rejected_reattach_snapshot_result =
        read_snapshot(&after_rejected_reattach_snapshot);
    reattach_rejected =
        reattach_result == HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS) &&
        final_detach_result == S_FALSE &&
        is_attached() == FALSE &&
        SUCCEEDED(after_rejected_reattach_snapshot_result) &&
        std::memcmp(
            &after_stale_snapshot,
            &after_rejected_reattach_snapshot,
            sizeof(after_stale_snapshot)) == 0;
    completed_cycles = completed.load(std::memory_order_acquire);
    return worker_succeeded.load(std::memory_order_acquire) &&
        completed_cycles >= 64 &&
        SUCCEEDED(snapshot_result) &&
        pre_detach_snapshot.automatic_lifetime_tracking == 1 &&
        pre_detach_snapshot.release_hook_slot_count >= 1 &&
        pre_detach_snapshot.release_hook_failure_count == 0 &&
        pre_detach_snapshot.provenance_failure_count == 0 &&
        SUCCEEDED(detach_result) &&
        stale_create_forwarded &&
        stale_release_forwarded &&
        stale_calls_observation_neutral &&
        reattach_rejected;
}

bool snapshot_matches_workload(
    const FluidHookSnapshotV1& snapshot,
    const Options& options) {
    const auto sustained_copy_count =
        static_cast<std::uint64_t>(options.sustained_copy_count);
    const auto readback_copy_count =
        static_cast<std::uint64_t>(options.readback_copy_count);
    const auto upload_copy_count =
        static_cast<std::uint64_t>(options.upload_copy_count);
    const auto update_upload_count =
        static_cast<std::uint64_t>(options.update_upload_count);
    const auto has_sustained_workload = sustained_copy_count != 0;
    const auto has_readback_workload = readback_copy_count != 0;
    const auto has_upload_workload = upload_copy_count != 0;
    const auto has_update_upload_workload = update_upload_count != 0;
    const auto sustained_copy_call_count = has_sustained_workload
        ? sustained_copy_count + 1
        : 0;
    const auto readback_copy_call_count = has_readback_workload
        ? readback_copy_count + 1
        : 0;
    const auto upload_copy_call_count = has_upload_workload
        ? upload_copy_count + 1
        : 0;
    const auto update_upload_call_count = has_update_upload_workload
        ? update_upload_count + 3
        : 0;
    const auto policy_accepted = control_policy_accepted(options.control_policy_case);
    const auto policy_rejected = control_policy_rejected(options.control_policy_case);
    const auto expected_control_applied_action_count =
        control_policy_applies_action(options.control_policy_case)
        ? (has_sustained_workload
            ? sustained_copy_count
            : (has_readback_workload
                ? readback_copy_count
                : (has_upload_workload
                    ? upload_copy_count
                    : (has_update_upload_workload
                        ? update_upload_count
                        : 1ULL))))
        : 0ULL;
    const auto expected_skipped_copy_count = has_update_upload_workload
        ? 0ULL
        : (options.skip_first_redundant_copy
        ? kExpectedSkippedCopyCount
        : expected_control_applied_action_count);
    const auto expected_skipped_copy_bytes = has_update_upload_workload
        ? 0ULL
        : (options.skip_first_redundant_copy
        ? kExpectedSkippedCopyBytes
        : (has_sustained_workload
            ? expected_control_applied_action_count * kSustainedBufferBytes
            : (has_readback_workload
                ? expected_control_applied_action_count * kReadbackBufferBytes
                : (has_upload_workload
                    ? expected_control_applied_action_count * kUploadBufferBytes
                    : expected_control_applied_action_count *
                        kExpectedSkippedCopyBytes))));
    const auto expected_skipped_update_count = has_update_upload_workload
        ? expected_control_applied_action_count
        : 0ULL;
    const auto expected_skipped_update_bytes =
        expected_skipped_update_count * kUpdateUploadBufferBytes;
    const auto expected_copy_count =
        kExpectedCopyCount + sustained_copy_call_count + readback_copy_call_count +
            upload_copy_call_count + (has_update_upload_workload ? 1ULL : 0ULL);
    const auto expected_copy_bytes =
        kExpectedCopyBytes + sustained_copy_call_count * kSustainedBufferBytes +
            readback_copy_call_count * kReadbackBufferBytes +
            upload_copy_call_count * kUploadBufferBytes +
            (has_update_upload_workload ? kUpdateUploadBufferBytes : 0ULL);
    const auto expected_redundant_copy_count =
        kExpectedRedundantCopyCount + sustained_copy_count + readback_copy_count +
            upload_copy_count;
    const auto expected_redundant_copy_bytes =
        kExpectedRedundantCopyBytes + sustained_copy_count * kSustainedBufferBytes +
            readback_copy_count * kReadbackBufferBytes +
            upload_copy_count * kUploadBufferBytes;
    const auto expected_update_subresource_count =
        3ULL + update_upload_call_count;
    const auto expected_update_subresource_bytes =
        kExpectedUpdateSubresourceBytes +
            update_upload_call_count * kUpdateUploadBufferBytes;
    const auto expected_retire_count = options.automatic_lifetime_tracking
        ? kExpectedResourceRetireCount
        : kExpectedResourceRetireCount + kExpectedResourceDestroyCount;
    const auto expected_destroy_count = options.automatic_lifetime_tracking
        ? kExpectedResourceDestroyCount
        : 0;
    return snapshot.abi_version == fluid_hook_snapshot_abi_version &&
        snapshot.create_buffer_count ==
            4 + kAutomaticLifetimeCycles +
                (has_sustained_workload || has_readback_workload ||
                    has_upload_workload || has_update_upload_workload
                        ? 2
                        : 0) &&
        snapshot.buffer_bytes_requested ==
            3 * kBufferBytes + kCooperativeBufferBytes +
                kAutomaticLifetimeCycles * kAutomaticBufferBytes +
                (has_sustained_workload ? 2ULL * kSustainedBufferBytes : 0ULL) +
                (has_readback_workload ? 2ULL * kReadbackBufferBytes : 0ULL) +
                (has_upload_workload ? 2ULL * kUploadBufferBytes : 0ULL) +
                (has_update_upload_workload ? 2ULL * kUpdateUploadBufferBytes : 0ULL) &&
        snapshot.create_texture2d_count == 4 &&
        snapshot.texture_bytes_estimated ==
            2 * kTextureWidth * kTextureHeight * 4 +
            2 * (kSubresourceTextureWidth * kSubresourceTextureHeight +
                (kSubresourceTextureWidth / 2) *
                    (kSubresourceTextureHeight / 2)) * 4 &&
        snapshot.map_read_count == readback_copy_call_count &&
        snapshot.map_read_bytes_estimated ==
            readback_copy_call_count * kReadbackBufferBytes &&
        snapshot.map_write_count == (has_upload_workload ? 2ULL : 1ULL) &&
        snapshot.unmap_write_count == (has_upload_workload ? 2ULL : 1ULL) &&
        snapshot.update_subresource_count == expected_update_subresource_count &&
        snapshot.update_subresource_bytes_estimated ==
            expected_update_subresource_bytes &&
        snapshot.tracked_update_subresource_count == update_upload_call_count &&
        snapshot.tracked_update_subresource_bytes_estimated ==
            update_upload_call_count * kUpdateUploadBufferBytes &&
        snapshot.redundant_update_subresource_candidate_count ==
            update_upload_count &&
        snapshot.redundant_update_subresource_bytes_estimated ==
            update_upload_count * kUpdateUploadBufferBytes &&
        snapshot.forwarded_update_subresource_count ==
            expected_update_subresource_count - expected_skipped_update_count &&
        snapshot.forwarded_update_subresource_bytes_estimated ==
            expected_update_subresource_bytes - expected_skipped_update_bytes &&
        snapshot.skipped_update_subresource_count ==
            expected_skipped_update_count &&
        snapshot.skipped_update_subresource_bytes_estimated ==
            expected_skipped_update_bytes &&
        snapshot.update_content_cache_resource_count ==
            (has_update_upload_workload ? 1ULL : 0ULL) &&
        snapshot.update_content_cache_bytes ==
            (has_update_upload_workload ? kUpdateUploadBufferBytes : 0ULL) &&
        snapshot.copy_resource_count == expected_copy_count &&
        snapshot.copy_resource_bytes_estimated == expected_copy_bytes &&
        snapshot.redundant_copy_candidate_count == expected_redundant_copy_count &&
        snapshot.redundant_copy_bytes_estimated == expected_redundant_copy_bytes &&
        snapshot.copy_subresource_region_count == kExpectedCopySubresourceCount &&
        snapshot.copy_subresource_region_bytes_estimated ==
            kExpectedCopySubresourceBytes &&
        snapshot.redundant_subresource_copy_candidate_count ==
            kExpectedRedundantSubresourceCopyCount &&
        snapshot.redundant_subresource_copy_bytes_estimated ==
            kExpectedRedundantSubresourceCopyBytes &&
        snapshot.clear_render_target_view_count == 1 &&
        snapshot.clear_unordered_access_view_float_count == 1 &&
        snapshot.gpu_view_write_bytes_estimated == kExpectedGpuViewWriteBytes &&
        snapshot.control_policy_enabled ==
            (control_policy_opt_in(options.control_policy_case) ? 1ULL : 0ULL) &&
        snapshot.control_policy_epoch == (policy_accepted ? 1ULL : 0ULL) &&
        snapshot.control_policy_acknowledged_epoch ==
            expected_control_acknowledged_epoch(options.control_policy_case) &&
        snapshot.control_policy_applied_action_count ==
            expected_control_applied_action_count &&
        snapshot.control_policy_rejected_count == (policy_rejected ? 1ULL : 0ULL) &&
        snapshot.control_policy_status == static_cast<std::uint64_t>(
            expected_control_status(options.control_policy_case)) &&
        snapshot.forwarded_copy_count ==
            expected_copy_count - expected_skipped_copy_count &&
        snapshot.forwarded_copy_bytes_estimated ==
            expected_copy_bytes - expected_skipped_copy_bytes &&
        snapshot.skipped_copy_count == expected_skipped_copy_count &&
        snapshot.skipped_copy_bytes_estimated == expected_skipped_copy_bytes &&
        snapshot.readback_copy_count == readback_copy_call_count &&
        snapshot.readback_copy_bytes_estimated ==
            readback_copy_call_count * kReadbackBufferBytes &&
        snapshot.skipped_readback_copy_count ==
            (has_readback_workload ? expected_skipped_copy_count : 0ULL) &&
        snapshot.skipped_readback_copy_bytes_estimated ==
            (has_readback_workload ? expected_skipped_copy_bytes : 0ULL) &&
        snapshot.upload_copy_count == upload_copy_call_count &&
        snapshot.upload_copy_bytes_estimated ==
            upload_copy_call_count * kUploadBufferBytes &&
        snapshot.skipped_upload_copy_count ==
            (has_upload_workload ? expected_skipped_copy_count : 0ULL) &&
        snapshot.skipped_upload_copy_bytes_estimated ==
            (has_upload_workload ? expected_skipped_copy_bytes : 0ULL) &&
        snapshot.tracked_resource_count ==
            (has_sustained_workload || has_readback_workload ||
                has_upload_workload || has_update_upload_workload
                    ? 9ULL
                    : 7ULL) &&
        snapshot.resource_retire_count == expected_retire_count &&
        snapshot.resource_destroy_count == expected_destroy_count &&
        snapshot.resource_reuse_count <= kAutomaticLifetimeCycles &&
        snapshot.retired_resource_identity_count + snapshot.resource_reuse_count ==
            expected_retire_count + expected_destroy_count &&
        snapshot.provenance_failure_count == 0 &&
        (options.automatic_lifetime_tracking
            ? snapshot.release_hook_slot_count >= 2
            : snapshot.release_hook_slot_count == 0) &&
        snapshot.release_hook_failure_count == 0 &&
        snapshot.automatic_lifetime_tracking ==
            (options.automatic_lifetime_tracking ? 1ULL : 0ULL) &&
        snapshot.hook_refresh_failure_count == 0 &&
        snapshot.ipc_event_count >=
            snapshot.present_count + 161 + snapshot.resource_reuse_count +
                (policy_accepted ? 1 : 0) +
                (has_sustained_workload ? sustained_copy_count + 3 : 0) +
                (has_readback_workload ? 2 * readback_copy_count + 4 : 0) +
                (has_upload_workload ? upload_copy_count + 5 : 0) +
                (has_update_upload_workload ? update_upload_count + 6 : 0) &&
        snapshot.ipc_overrun_count == 0;
}

std::string build_report(
    const Options& options,
    const FluidHookSnapshotV1& snapshot,
    HRESULT attach_result,
    HRESULT control_policy_wait_result,
    HRESULT control_policy_expiry_wait_result,
    HRESULT refresh_result,
    HRESULT snapshot_result,
    HRESULT detach_result,
    bool original_pointer_restored,
    bool render_succeeded,
    bool resource_workload_succeeded,
    bool resource_metrics_matched,
    bool context_vtable_pointer_stable,
    bool context_copy_entry_stable,
    bool context_subresource_copy_entry_stable,
    bool context_gpu_view_write_entries_stable,
    const ReadbackWorkloadVerification& readback_verification,
    const UploadWorkloadVerification& upload_verification,
    const UpdateUploadWorkloadVerification& update_upload_verification,
    const ContentVerification& content,
    const TimingMetrics& timing,
    const AdapterIdentity& adapter) {
    const auto managed_action_budget =
        control_policy_applies_action(options.control_policy_case)
        ? (options.sustained_copy_count != 0
            ? static_cast<std::uint64_t>(options.sustained_copy_count)
            : (options.readback_copy_count != 0
                ? static_cast<std::uint64_t>(options.readback_copy_count)
                : (options.upload_copy_count != 0
                    ? static_cast<std::uint64_t>(options.upload_copy_count)
                    : (options.update_upload_count != 0
                        ? static_cast<std::uint64_t>(options.update_upload_count)
                        : 1ULL))))
        : 0ULL;
    const auto optimization_enabled =
        options.skip_first_redundant_copy ||
        managed_action_budget != 0;
    const auto expected_skipped_copy_count = options.update_upload_count != 0
        ? 0ULL
        : (options.skip_first_redundant_copy
        ? 1ULL
        : managed_action_budget);
    const auto expected_skipped_update_count = options.update_upload_count != 0
        ? managed_action_budget
        : 0ULL;
    const auto optimization_requested =
        options.skip_first_redundant_copy || options.managed_control;
    const auto optimization_kind = options.managed_control
        ? (options.readback_copy_count != 0
            ? "managed-policy-skip-redundant-readback-copy"
            : (options.upload_copy_count != 0
                ? "managed-policy-skip-redundant-upload-copy"
                : (options.update_upload_count != 0
                    ? "managed-policy-skip-redundant-update-subresource"
                    : "managed-policy-skip-redundant-copy-resource")))
        : (options.skip_first_redundant_copy
            ? "attach-option-skip-redundant-copy-resource"
            : "none");
    std::ostringstream output;
    output << "{\n"
           << "  \"mode\": \"fluidruntime-resource-hook-lab-v0.12.0\",\n"
           << "  \"target_owned\": true,\n"
           << "  \"cooperative_load\": true,\n"
           << "  \"remote_injection\": false,\n"
           << "  \"read_only_hook\": "
           << (optimization_enabled ? "false" : "true") << ",\n"
           << "  \"would_modify_frame_data\": false,\n"
           << "  \"would_skip_copies\": "
           << (optimization_enabled && options.update_upload_count == 0
                ? "true"
                : "false")
           << ",\n"
           << "  \"would_skip_updates\": "
           << (optimization_enabled && options.update_upload_count != 0
                ? "true"
                : "false")
           << ",\n"
           << "  \"optimization_requested\": "
           << (optimization_requested ? "true" : "false") << ",\n"
           << "  \"optimization_kind\": \"" << optimization_kind << "\",\n"
           << "  \"module_pinned_until_process_exit\": "
           << (SUCCEEDED(attach_result) ? "true" : "false") << ",\n"
           << "  \"max_skipped_copy_count\": "
           << expected_skipped_copy_count << ",\n"
           << "  \"max_skipped_update_count\": "
           << expected_skipped_update_count << ",\n"
           << "  \"control_plane\": "
           << (options.managed_control
               ? "\"managed-shared-memory-policy-v1\""
               : (options.skip_first_redundant_copy
                    ? "\"immutable-attach-options\""
                    : "\"observe-only\""))
           << ",\n"
           << "  \"control_policy_requested\": "
           << (options.managed_control ? "true" : "false") << ",\n"
           << "  \"control_policy_case\": \""
           << control_policy_case_name(options.control_policy_case) << "\",\n"
           << "  \"control_policy_timeout_ms\": "
           << options.control_timeout_ms << ",\n"
           << "  \"automatic_lifetime_tracking\": "
           << (snapshot.automatic_lifetime_tracking != 0 ? "true" : "false") << ",\n"
           << "  \"release_observation_scope\": "
           << (snapshot.automatic_lifetime_tracking != 0
               ? "\"owned-returned-buffer-texture-interface\""
               : "\"cooperative-retire-only\"")
           << ",\n"
           << "  \"subresource_provenance_scope\": "
              "\"owned-buffer-texture2d-map-update-copy-region\",\n"
           << "  \"gpu_view_write_scope\": "
              "\"owned-texture2d-single-subresource-rtv-uav-clear\",\n"
           << "  \"readback_scope\": "
              "\"owned-d3d11-default-to-readable-staging-buffer\",\n"
           << "  \"upload_scope\": "
              "\"owned-d3d11-readable-writable-staging-to-default-buffer\",\n"
           << "  \"update_upload_scope\": "
              "\"owned-d3d11-default-buffer-full-update-subresource-exact-content\",\n"
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
           << "  \"sustained_copy_count\": "
           << options.sustained_copy_count << ",\n"
           << "  \"sustained_buffer_bytes\": "
           << (options.sustained_copy_count != 0 ? kSustainedBufferBytes : 0)
           << ",\n"
           << "  \"sustained_logical_copy_bytes\": "
           << (options.sustained_copy_count != 0
                ? (static_cast<std::uint64_t>(options.sustained_copy_count) + 1) *
                    kSustainedBufferBytes
                : 0ULL)
           << ",\n"
           << "  \"readback_copy_count\": "
           << options.readback_copy_count << ",\n"
           << "  \"readback_buffer_bytes\": "
           << (options.readback_copy_count != 0 ? kReadbackBufferBytes : 0)
           << ",\n"
           << "  \"readback_logical_copy_bytes\": "
           << (options.readback_copy_count != 0
                ? (static_cast<std::uint64_t>(options.readback_copy_count) + 1) *
                    kReadbackBufferBytes
                : 0ULL)
           << ",\n"
           << "  \"upload_copy_count\": "
           << options.upload_copy_count << ",\n"
           << "  \"upload_buffer_bytes\": "
           << (options.upload_copy_count != 0 ? kUploadBufferBytes : 0)
           << ",\n"
           << "  \"upload_logical_copy_bytes\": "
           << (options.upload_copy_count != 0
                ? (static_cast<std::uint64_t>(options.upload_copy_count) + 1) *
                    kUploadBufferBytes
                : 0ULL)
           << ",\n"
           << "  \"update_upload_count\": "
           << options.update_upload_count << ",\n"
           << "  \"update_upload_call_count\": "
           << (options.update_upload_count != 0
                ? static_cast<std::uint64_t>(options.update_upload_count) + 3
                : 0ULL)
           << ",\n"
           << "  \"update_upload_buffer_bytes\": "
           << (options.update_upload_count != 0 ? kUpdateUploadBufferBytes : 0)
           << ",\n"
           << "  \"update_upload_logical_bytes\": "
           << (options.update_upload_count != 0
                ? (static_cast<std::uint64_t>(options.update_upload_count) + 3) *
                    kUpdateUploadBufferBytes
                : 0ULL)
           << ",\n"
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
           << "  \"context_subresource_copy_entry_stable\": "
           << (context_subresource_copy_entry_stable ? "true" : "false") << ",\n"
           << "  \"context_gpu_view_write_entries_stable\": "
           << (context_gpu_view_write_entries_stable ? "true" : "false") << ",\n"
           << "  \"original_pointer_restored\": "
           << (original_pointer_restored ? "true" : "false") << ",\n"
           << "  \"content_readback_succeeded\": "
           << (content.readback_succeeded ? "true" : "false") << ",\n"
           << "  \"readback_all_maps_succeeded\": "
           << (readback_verification.all_maps_succeeded ? "true" : "false")
           << ",\n"
           << "  \"readback_all_maps_equal\": "
           << (readback_verification.all_maps_equal ? "true" : "false") << ",\n"
           << "  \"readback_successful_map_count\": "
           << readback_verification.successful_map_count << ",\n"
           << "  \"sustained_buffer_contents_equal\": "
           << (content.sustained_buffer_contents_equal ? "true" : "false")
           << ",\n"
           << "  \"readback_buffer_contents_equal\": "
           << (content.readback_buffer_contents_equal ? "true" : "false")
           << ",\n"
           << "  \"upload_write_map_succeeded\": "
           << (upload_verification.write_map_succeeded ? "true" : "false")
           << ",\n"
           << "  \"upload_buffer_contents_equal\": "
           << (content.upload_buffer_contents_equal ? "true" : "false")
           << ",\n"
           << "  \"update_upload_mutation_applied\": "
           << (update_upload_verification.mutation_applied ? "true" : "false")
           << ",\n"
           << "  \"update_upload_generation_guard_applied\": "
           << (update_upload_verification.generation_guard_applied
                ? "true"
                : "false")
           << ",\n"
           << "  \"update_upload_contents_equal\": "
           << (options.update_upload_count == 0 ||
                    content.update_upload_destination_buffer_hash ==
                        update_upload_verification.final_hash
                ? "true"
                : "false")
           << ",\n"
           << "  \"buffer_contents_equal\": "
           << (content.buffer_contents_equal ? "true" : "false") << ",\n"
           << "  \"texture_contents_equal\": "
           << (content.texture_contents_equal ? "true" : "false") << ",\n"
           << "  \"subresource_contents_equal\": "
           << (content.subresource_contents_equal ? "true" : "false") << ",\n"
           << "  \"hash_algorithm\": \"fnv1a64\",\n"
           << "  \"sustained_source_buffer_hash\": \""
           << uint64_hex(content.sustained_source_buffer_hash) << "\",\n"
           << "  \"sustained_destination_buffer_hash\": \""
           << uint64_hex(content.sustained_destination_buffer_hash) << "\",\n"
           << "  \"readback_expected_hash\": \""
           << uint64_hex(readback_verification.expected_hash) << "\",\n"
           << "  \"readback_first_map_hash\": \""
           << uint64_hex(readback_verification.first_map_hash) << "\",\n"
           << "  \"readback_final_map_hash\": \""
           << uint64_hex(readback_verification.final_map_hash) << "\",\n"
           << "  \"readback_source_buffer_hash\": \""
           << uint64_hex(content.readback_source_buffer_hash) << "\",\n"
           << "  \"readback_destination_buffer_hash\": \""
           << uint64_hex(content.readback_destination_buffer_hash) << "\",\n"
           << "  \"upload_expected_hash\": \""
           << uint64_hex(upload_verification.expected_hash) << "\",\n"
           << "  \"upload_source_buffer_hash\": \""
           << uint64_hex(content.upload_source_buffer_hash) << "\",\n"
           << "  \"upload_destination_buffer_hash\": \""
           << uint64_hex(content.upload_destination_buffer_hash) << "\",\n"
           << "  \"update_upload_initial_hash\": \""
           << uint64_hex(update_upload_verification.initial_hash) << "\",\n"
           << "  \"update_upload_final_hash\": \""
           << uint64_hex(update_upload_verification.final_hash) << "\",\n"
           << "  \"update_upload_guard_hash\": \""
           << uint64_hex(update_upload_verification.guard_hash) << "\",\n"
           << "  \"update_upload_destination_buffer_hash\": \""
           << uint64_hex(content.update_upload_destination_buffer_hash) << "\",\n"
           << "  \"source_buffer_hash\": \""
           << uint64_hex(content.source_buffer_hash) << "\",\n"
           << "  \"destination_buffer_hash\": \""
           << uint64_hex(content.destination_buffer_hash) << "\",\n"
           << "  \"source_texture_hash\": \""
           << uint64_hex(content.source_texture_hash) << "\",\n"
           << "  \"destination_texture_hash\": \""
           << uint64_hex(content.destination_texture_hash) << "\",\n"
           << "  \"source_subresource_hash\": \""
           << uint64_hex(content.source_subresource_hash) << "\",\n"
           << "  \"destination_subresource_hash\": \""
           << uint64_hex(content.destination_subresource_hash) << "\",\n"
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
           << "    \"map_read_count\": " << snapshot.map_read_count << ",\n"
           << "    \"map_read_bytes_estimated\": "
           << snapshot.map_read_bytes_estimated << ",\n"
           << "    \"map_write_count\": " << snapshot.map_write_count << ",\n"
           << "    \"unmap_write_count\": " << snapshot.unmap_write_count << ",\n"
           << "    \"update_subresource_count\": "
           << snapshot.update_subresource_count << ",\n"
           << "    \"update_subresource_bytes_estimated\": "
           << snapshot.update_subresource_bytes_estimated << ",\n"
           << "    \"tracked_update_subresource_count\": "
           << snapshot.tracked_update_subresource_count << ",\n"
           << "    \"tracked_update_subresource_bytes_estimated\": "
           << snapshot.tracked_update_subresource_bytes_estimated << ",\n"
           << "    \"redundant_update_subresource_candidate_count\": "
           << snapshot.redundant_update_subresource_candidate_count << ",\n"
           << "    \"redundant_update_subresource_bytes_estimated\": "
           << snapshot.redundant_update_subresource_bytes_estimated << ",\n"
           << "    \"forwarded_update_subresource_count\": "
           << snapshot.forwarded_update_subresource_count << ",\n"
           << "    \"forwarded_update_subresource_bytes_estimated\": "
           << snapshot.forwarded_update_subresource_bytes_estimated << ",\n"
           << "    \"skipped_update_subresource_count\": "
           << snapshot.skipped_update_subresource_count << ",\n"
           << "    \"skipped_update_subresource_bytes_estimated\": "
           << snapshot.skipped_update_subresource_bytes_estimated << ",\n"
           << "    \"update_content_cache_resource_count\": "
           << snapshot.update_content_cache_resource_count << ",\n"
           << "    \"update_content_cache_bytes\": "
           << snapshot.update_content_cache_bytes << ",\n"
           << "    \"copy_resource_count\": " << snapshot.copy_resource_count << ",\n"
           << "    \"copy_resource_bytes_estimated\": "
           << snapshot.copy_resource_bytes_estimated << ",\n"
           << "    \"copy_subresource_region_count\": "
           << snapshot.copy_subresource_region_count << ",\n"
           << "    \"copy_subresource_region_bytes_estimated\": "
           << snapshot.copy_subresource_region_bytes_estimated << ",\n"
           << "    \"redundant_subresource_copy_candidate_count\": "
           << snapshot.redundant_subresource_copy_candidate_count << ",\n"
           << "    \"redundant_subresource_copy_bytes_estimated\": "
           << snapshot.redundant_subresource_copy_bytes_estimated << ",\n"
           << "    \"clear_render_target_view_count\": "
           << snapshot.clear_render_target_view_count << ",\n"
           << "    \"clear_unordered_access_view_float_count\": "
           << snapshot.clear_unordered_access_view_float_count << ",\n"
           << "    \"gpu_view_write_bytes_estimated\": "
           << snapshot.gpu_view_write_bytes_estimated << ",\n"
           << "    \"control_policy_enabled\": "
           << snapshot.control_policy_enabled << ",\n"
           << "    \"control_policy_epoch\": "
           << snapshot.control_policy_epoch << ",\n"
           << "    \"control_policy_acknowledged_epoch\": "
           << snapshot.control_policy_acknowledged_epoch << ",\n"
           << "    \"control_policy_applied_action_count\": "
           << snapshot.control_policy_applied_action_count << ",\n"
           << "    \"control_policy_rejected_count\": "
           << snapshot.control_policy_rejected_count << ",\n"
           << "    \"control_policy_status\": "
           << snapshot.control_policy_status << ",\n"
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
           << "    \"readback_copy_count\": "
           << snapshot.readback_copy_count << ",\n"
           << "    \"readback_copy_bytes_estimated\": "
           << snapshot.readback_copy_bytes_estimated << ",\n"
           << "    \"skipped_readback_copy_count\": "
           << snapshot.skipped_readback_copy_count << ",\n"
           << "    \"skipped_readback_copy_bytes_estimated\": "
           << snapshot.skipped_readback_copy_bytes_estimated << ",\n"
           << "    \"upload_copy_count\": "
           << snapshot.upload_copy_count << ",\n"
           << "    \"upload_copy_bytes_estimated\": "
           << snapshot.upload_copy_bytes_estimated << ",\n"
           << "    \"skipped_upload_copy_count\": "
           << snapshot.skipped_upload_copy_count << ",\n"
           << "    \"skipped_upload_copy_bytes_estimated\": "
           << snapshot.skipped_upload_copy_bytes_estimated << ",\n"
           << "    \"tracked_resource_count\": " << snapshot.tracked_resource_count << ",\n"
           << "    \"resource_retire_count\": " << snapshot.resource_retire_count << ",\n"
           << "    \"resource_reuse_count\": " << snapshot.resource_reuse_count << ",\n"
           << "    \"retired_resource_identity_count\": "
           << snapshot.retired_resource_identity_count << ",\n"
           << "    \"provenance_failure_count\": "
           << snapshot.provenance_failure_count << ",\n"
           << "    \"resource_destroy_count\": " << snapshot.resource_destroy_count << ",\n"
           << "    \"release_hook_slot_count\": "
           << snapshot.release_hook_slot_count << ",\n"
           << "    \"release_hook_failure_count\": "
           << snapshot.release_hook_failure_count << ",\n"
           << "    \"hook_refresh_count\": " << snapshot.hook_refresh_count << ",\n"
           << "    \"hook_refresh_failure_count\": "
           << snapshot.hook_refresh_failure_count << ",\n"
           << "    \"ipc_event_count\": " << snapshot.ipc_event_count << ",\n"
           << "    \"ipc_overrun_count\": " << snapshot.ipc_overrun_count << "\n"
           << "  },\n"
           << "  \"attach_hresult\": \"" << hresult_hex(attach_result) << "\",\n"
           << "  \"control_policy_wait_hresult\": \""
           << hresult_hex(control_policy_wait_result) << "\",\n"
           << "  \"control_policy_expiry_wait_hresult\": \""
           << hresult_hex(control_policy_expiry_wait_result) << "\",\n"
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
                      L"[--control-timeout-ms <milliseconds>] "
                      L"[--sustained-copy-count <count>] "
                       L"[--readback-copy-count <count>] "
                       L"[--upload-copy-count <count>] "
                       L"[--update-upload-count <count>] "
                      L"[--out <report.json>] [--hardware] "
                      L"[--skip-first-redundant-copy] [--managed-control] "
                      L"[--control-policy-case <case>] "
                      L"[--cooperative-lifetime] "
                      L"[--concurrent-lifetime-stress]\n";
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
    const auto wait_for_control_policy =
        reinterpret_cast<FluidHookWaitForControlPolicyFunction>(
            GetProcAddress(hook_module, "FluidHookWaitForControlPolicy"));
    const auto retire_resource = reinterpret_cast<FluidHookRetireResourceFunction>(
        GetProcAddress(hook_module, "FluidHookRetireResource"));
    const auto is_attached = reinterpret_cast<FluidHookIsAttachedFunction>(
        GetProcAddress(hook_module, "FluidHookIsAttached"));
    const auto read_snapshot = reinterpret_cast<FluidHookReadSnapshotFunction>(
        GetProcAddress(hook_module, "FluidHookReadSnapshot"));
    if (attach == nullptr || attach_ex == nullptr || detach == nullptr || refresh == nullptr ||
        wait_for_control_policy == nullptr ||
        retire_resource == nullptr || is_attached == nullptr || read_snapshot == nullptr) {
        FreeLibrary(hook_module);
        DestroyWindow(window);
        UnregisterClassW(window_class_name, instance);
        std::cerr << "Hook DLL contract is incomplete.\n";
        return 5;
    }

    FluidHookAttachOptionsV1 attach_options{};
    attach_options.struct_size = sizeof(attach_options);
    attach_options.abi_version = fluid_hook_attach_options_abi_version;
    attach_options.flags = options->automatic_lifetime_tracking
        ? fluid_hook_attach_flag_track_resource_lifetime
        : 0;
    attach_options.max_skipped_copy_count = 0;
    if (options->update_upload_count != 0) {
        attach_options.flags |=
            fluid_hook_attach_flag_track_update_subresource_content;
        attach_options.max_tracked_update_subresource_bytes =
            kUpdateUploadBufferBytes;
        attach_options.max_tracked_update_subresource_resources = 1;
    }
    if (options->skip_first_redundant_copy) {
        attach_options.flags |= fluid_hook_attach_flag_skip_first_redundant_copy;
        attach_options.max_skipped_copy_count = 1;
    }
    if (control_policy_opt_in(options->control_policy_case)) {
        attach_options.flags |= fluid_hook_attach_flag_allow_control_policy;
    }
    const auto attach_result =
        !options->automatic_lifetime_tracking &&
            !options->skip_first_redundant_copy &&
            !options->managed_control
        ? attach(swap_chain.Get())
        : attach_ex(swap_chain.Get(), &attach_options);
    const auto published_gate_opened =
        !options->control_policy_matrix_case || wait_for_control_gate("published");
    const auto control_policy_wait_result = options->managed_control
        ? (SUCCEEDED(attach_result) && published_gate_opened
            ? wait_for_control_policy(options->control_timeout_ms)
            : (FAILED(attach_result) ? attach_result : E_ABORT))
        : S_FALSE;
    const auto control_policy_expiry_wait_result =
        options->control_policy_case == ControlPolicyCase::accepted_then_expired
        ? (SUCCEEDED(control_policy_wait_result)
            ? (wait_for_control_gate("expired") ? S_OK : E_ABORT)
            : control_policy_wait_result)
        : S_FALSE;
    if (options->concurrent_lifetime_stress) {
        unsigned long completed_cycles = 0;
        bool stale_create_forwarded = false;
        bool stale_release_forwarded = false;
        bool stale_calls_observation_neutral = false;
        bool reattach_rejected = false;
        FluidHookSnapshotV1 stress_snapshot{};
        HRESULT stress_snapshot_result = E_UNEXPECTED;
        HRESULT stress_detach_result = E_UNEXPECTED;
        HRESULT stress_reattach_result = E_UNEXPECTED;
        HRESULT stress_final_detach_result = E_UNEXPECTED;
        const auto stress_succeeded =
            SUCCEEDED(attach_result) &&
            options->automatic_lifetime_tracking &&
            run_concurrent_lifetime_detach_stress(
                device.Get(),
                swap_chain.Get(),
                attach_ex,
                detach,
                is_attached,
                read_snapshot,
                completed_cycles,
                stale_create_forwarded,
                stale_release_forwarded,
                stale_calls_observation_neutral,
                reattach_rejected,
                stress_snapshot,
                stress_snapshot_result,
                stress_detach_result,
                stress_reattach_result,
                stress_final_detach_result);
        std::ostringstream stress_output;
        stress_output << "{\n"
                      << "  \"mode\": "
                         "\"fluidruntime-concurrent-lifetime-detach-v0.12.0\",\n"
                      << "  \"target_owned\": true,\n"
                      << "  \"automatic_lifetime_tracking\": true,\n"
                      << "  \"module_pinned_until_process_exit\": true,\n"
                      << "  \"stale_create_forwarded\": "
                      << (stale_create_forwarded ? "true" : "false") << ",\n"
                      << "  \"stale_release_forwarded\": "
                      << (stale_release_forwarded ? "true" : "false") << ",\n"
                      << "  \"stale_calls_observation_neutral\": "
                      << (stale_calls_observation_neutral ? "true" : "false")
                      << ",\n"
                      << "  \"reattach_rejected\": "
                      << (reattach_rejected ? "true" : "false") << ",\n"
                      << "  \"completed_cycles\": " << completed_cycles << ",\n"
                      << "  \"release_hook_slot_count\": "
                      << stress_snapshot.release_hook_slot_count << ",\n"
                      << "  \"release_hook_failure_count\": "
                      << stress_snapshot.release_hook_failure_count << ",\n"
                      << "  \"provenance_failure_count\": "
                      << stress_snapshot.provenance_failure_count << ",\n"
                      << "  \"attach_hresult\": \""
                      << hresult_hex(attach_result) << "\",\n"
                      << "  \"snapshot_hresult\": \""
                      << hresult_hex(stress_snapshot_result) << "\",\n"
                      << "  \"detach_hresult\": \""
                      << hresult_hex(stress_detach_result) << "\",\n"
                      << "  \"reattach_hresult\": \""
                      << hresult_hex(stress_reattach_result) << "\",\n"
                      << "  \"final_detach_hresult\": \""
                      << hresult_hex(stress_final_detach_result) << "\",\n"
                      << "  \"rollback_restored\": "
                      << (stress_succeeded ? "true" : "false") << "\n"
                      << "}\n";
        const auto stress_report = stress_output.str();
        std::cout << stress_report;
        auto report_written = true;
        if (!options->output_path.empty()) {
            std::ofstream output(options->output_path, std::ios::binary);
            output << stress_report;
            report_written = output.good();
        }
        if (stress_succeeded) {
            FreeLibrary(hook_module);
        }
        DestroyWindow(window);
        UnregisterClassW(window_class_name, instance);
        return stress_succeeded && report_written ? 0 : 6;
    }
    LARGE_INTEGER qpc_frequency{};
    QueryPerformanceFrequency(&qpc_frequency);
    TimingMetrics timing{
        .qpc_frequency = static_cast<std::uint64_t>(qpc_frequency.QuadPart),
    };
    const auto gpu_timing_queries = create_gpu_timing_queries(device.Get());
    WorkloadResources workload_resources;
    ReadbackWorkloadVerification readback_verification;
    UploadWorkloadVerification upload_verification;
    UpdateUploadWorkloadVerification update_upload_verification;
    bool context_vtable_pointer_stable = false;
    bool context_copy_entry_stable = false;
    bool context_subresource_copy_entry_stable = false;
    bool context_gpu_view_write_entries_stable = false;
    LARGE_INTEGER workload_start{};
    LARGE_INTEGER workload_end{};
    if (gpu_timing_queries.disjoint != nullptr) {
        context->Begin(gpu_timing_queries.disjoint.Get());
        context->End(gpu_timing_queries.start.Get());
    }
    QueryPerformanceCounter(&workload_start);
    const auto control_policy_wait_matched =
        control_policy_wait_result ==
            expected_control_wait_result(options->control_policy_case);
    const auto control_policy_expiry_wait_matched =
        options->control_policy_case != ControlPolicyCase::accepted_then_expired ||
        control_policy_expiry_wait_result == S_OK;
    auto resource_workload_succeeded =
        SUCCEEDED(attach_result) &&
        control_policy_wait_matched &&
        control_policy_expiry_wait_matched &&
        is_attached() != FALSE &&
        run_resource_workload(
            device.Get(),
            context.Get(),
            workload_resources,
            options->sustained_copy_count,
            options->readback_copy_count,
            options->upload_copy_count,
            options->update_upload_count,
            readback_verification,
            upload_verification,
            update_upload_verification,
            context_vtable_pointer_stable,
            context_copy_entry_stable,
            context_subresource_copy_entry_stable,
            context_gpu_view_write_entries_stable);
    QueryPerformanceCounter(&workload_end);
    if (gpu_timing_queries.disjoint != nullptr) {
        context->End(gpu_timing_queries.end.Get());
        context->End(gpu_timing_queries.disjoint.Get());
    }
    timing.workload_qpc_ticks = static_cast<std::uint64_t>(
        workload_end.QuadPart - workload_start.QuadPart);
    if (resource_workload_succeeded) {
        resource_workload_succeeded =
            run_resource_lifetime_workload(
                device.Get(),
                retire_resource,
                options->automatic_lifetime_tracking);
    }

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
        control_policy_wait_result,
        control_policy_expiry_wait_result,
        refresh_result,
        snapshot_result,
        detach_result,
        original_pointer_restored,
        render_succeeded,
        resource_workload_succeeded,
        resource_metrics_matched,
        context_vtable_pointer_stable,
        context_copy_entry_stable,
        context_subresource_copy_entry_stable,
        context_gpu_view_write_entries_stable,
        readback_verification,
        upload_verification,
        update_upload_verification,
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

    const auto readback_workload_verified = options->readback_copy_count == 0 ||
        (readback_verification.all_maps_succeeded &&
         readback_verification.all_maps_equal &&
         readback_verification.successful_map_count ==
            static_cast<std::uint64_t>(options->readback_copy_count) + 1 &&
         readback_verification.expected_hash != 0 &&
         readback_verification.first_map_hash == readback_verification.expected_hash &&
         readback_verification.final_map_hash == readback_verification.expected_hash);
    const auto upload_workload_verified = options->upload_copy_count == 0 ||
        (upload_verification.write_map_succeeded &&
         upload_verification.expected_hash != 0 &&
         content.upload_source_buffer_hash == upload_verification.expected_hash &&
         content.upload_destination_buffer_hash == upload_verification.expected_hash);
    const auto update_upload_workload_verified =
        options->update_upload_count == 0 ||
        (update_upload_verification.mutation_applied &&
         update_upload_verification.generation_guard_applied &&
         update_upload_verification.initial_hash != 0 &&
         update_upload_verification.final_hash != 0 &&
         update_upload_verification.guard_hash != 0 &&
         update_upload_verification.initial_hash !=
            update_upload_verification.final_hash &&
         update_upload_verification.guard_hash !=
            update_upload_verification.final_hash &&
         content.update_upload_destination_buffer_hash ==
            update_upload_verification.final_hash);
    const auto passed = render_succeeded &&
        resource_workload_succeeded &&
        resource_metrics_matched &&
        control_policy_wait_matched &&
        control_policy_expiry_wait_matched &&
        original_pointer_restored &&
        context_vtable_pointer_stable &&
        context_copy_entry_stable &&
        context_subresource_copy_entry_stable &&
        context_gpu_view_write_entries_stable &&
        readback_workload_verified &&
        upload_workload_verified &&
        update_upload_workload_verified &&
        content.readback_succeeded &&
        content.sustained_buffer_contents_equal &&
        content.readback_buffer_contents_equal &&
        content.upload_buffer_contents_equal &&
        content.buffer_contents_equal &&
        content.texture_contents_equal &&
        content.subresource_contents_equal &&
        snapshot.present_count == options->frames;
    return passed ? 0 : 6;
}
