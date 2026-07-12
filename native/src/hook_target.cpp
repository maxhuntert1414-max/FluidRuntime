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

namespace {

using Microsoft::WRL::ComPtr;

constexpr UINT kBufferBytes = 4096;
constexpr UINT kTextureWidth = 64;
constexpr UINT kTextureHeight = 64;
constexpr std::uint64_t kExpectedCopyCount = 6;
constexpr std::uint64_t kExpectedCopyBytes = 49152;
constexpr std::uint64_t kExpectedRedundantCopyCount = 3;
constexpr std::uint64_t kExpectedRedundantCopyBytes = 24576;

struct Options {
    std::wstring hook_path;
    std::wstring output_path;
    unsigned long frames{60};
    unsigned long hold_ms{};
    bool use_hardware{};
};

struct WorkloadResources {
    ComPtr<ID3D11Buffer> source_buffer;
    ComPtr<ID3D11Buffer> destination_buffer;
    ComPtr<ID3D11Buffer> dynamic_buffer;
    ComPtr<ID3D11Texture2D> source_texture;
    ComPtr<ID3D11Texture2D> destination_texture;
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
        } else if (argument == L"--hardware") {
            options.use_hardware = true;
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

bool snapshot_matches_workload(const FluidHookSnapshotV1& snapshot) {
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
        snapshot.tracked_resource_count == 5 &&
        snapshot.hook_refresh_failure_count == 0 &&
        snapshot.ipc_event_count >= snapshot.present_count + 14 &&
        snapshot.ipc_overrun_count == 0;
}

std::string build_report(
    const Options& options,
    const FluidHookSnapshotV1& snapshot,
    HRESULT attach_result,
    HRESULT snapshot_result,
    HRESULT detach_result,
    bool original_pointer_restored,
    bool render_succeeded,
    bool resource_workload_succeeded,
    bool resource_metrics_matched,
    bool context_vtable_pointer_stable,
    bool context_copy_entry_stable) {
    std::ostringstream output;
    output << "{\n"
           << "  \"mode\": \"fluidruntime-resource-hook-lab-v0.4\",\n"
           << "  \"target_owned\": true,\n"
           << "  \"cooperative_load\": true,\n"
           << "  \"remote_injection\": false,\n"
           << "  \"read_only_hook\": true,\n"
           << "  \"would_modify_frame_data\": false,\n"
           << "  \"would_skip_copies\": false,\n"
           << "  \"render_driver\": \""
           << (options.use_hardware ? "hardware" : "warp") << "\",\n"
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
           << "    \"tracked_resource_count\": " << snapshot.tracked_resource_count << ",\n"
           << "    \"hook_refresh_count\": " << snapshot.hook_refresh_count << ",\n"
           << "    \"hook_refresh_failure_count\": "
           << snapshot.hook_refresh_failure_count << ",\n"
           << "    \"ipc_event_count\": " << snapshot.ipc_event_count << ",\n"
           << "    \"ipc_overrun_count\": " << snapshot.ipc_overrun_count << "\n"
           << "  },\n"
           << "  \"attach_hresult\": \"" << hresult_hex(attach_result) << "\",\n"
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
                      L"[--out <report.json>] [--hardware]\n";
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
    const auto detach = reinterpret_cast<FluidHookDetachFunction>(
        GetProcAddress(hook_module, "FluidHookDetach"));
    const auto is_attached = reinterpret_cast<FluidHookIsAttachedFunction>(
        GetProcAddress(hook_module, "FluidHookIsAttached"));
    const auto read_snapshot = reinterpret_cast<FluidHookReadSnapshotFunction>(
        GetProcAddress(hook_module, "FluidHookReadSnapshot"));
    if (attach == nullptr || detach == nullptr ||
        is_attached == nullptr || read_snapshot == nullptr) {
        FreeLibrary(hook_module);
        DestroyWindow(window);
        UnregisterClassW(window_class_name, instance);
        std::cerr << "Hook DLL contract is incomplete.\n";
        return 5;
    }

    const auto attach_result = attach(swap_chain.Get());
    WorkloadResources workload_resources;
    bool context_vtable_pointer_stable = false;
    bool context_copy_entry_stable = false;
    const auto resource_workload_succeeded =
        SUCCEEDED(attach_result) &&
        is_attached() != FALSE &&
        run_resource_workload(
            device.Get(),
            context.Get(),
            workload_resources,
            context_vtable_pointer_stable,
            context_copy_entry_stable);

    bool render_succeeded = resource_workload_succeeded;
    for (unsigned long frame = 0; render_succeeded && frame < options->frames; ++frame) {
        const float red = static_cast<float>(frame % 60) / 60.0F;
        const float color[]{red, 0.2F, 1.0F - red, 1.0F};
        context->ClearRenderTargetView(render_target.Get(), color);
        render_succeeded = SUCCEEDED(swap_chain->Present(0, 0));
    }

    if (options->hold_ms != 0) {
        Sleep(options->hold_ms);
    }

    FluidHookSnapshotV1 snapshot{};
    snapshot.struct_size = sizeof(snapshot);
    const auto snapshot_result = read_snapshot(&snapshot);
    const auto resource_metrics_matched =
        SUCCEEDED(snapshot_result) && snapshot_matches_workload(snapshot);
    const auto detach_result = detach();
    const auto original_pointer_restored =
        SUCCEEDED(detach_result) && is_attached() == FALSE;

    const auto report = build_report(
        *options,
        snapshot,
        attach_result,
        snapshot_result,
        detach_result,
        original_pointer_restored,
        render_succeeded,
        resource_workload_succeeded,
        resource_metrics_matched,
        context_vtable_pointer_stable,
        context_copy_entry_stable);
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
        snapshot.present_count == options->frames;
    return passed ? 0 : 6;
}
