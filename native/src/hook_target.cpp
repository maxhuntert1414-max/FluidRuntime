#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <wrl/client.h>

#include <fstream>
#include <iomanip>
#include <iostream>
#include <optional>
#include <sstream>
#include <string>
#include <string_view>

namespace {

using Microsoft::WRL::ComPtr;
using HookAttach = HRESULT(WINAPI*)(IDXGISwapChain*);
using HookDetach = HRESULT(WINAPI*)();
using HookPresentCount = unsigned long long(WINAPI*)();
using HookIsAttached = BOOL(WINAPI*)();

struct Options {
    std::wstring hook_path;
    std::wstring output_path;
    unsigned long frames{60};
    bool use_hardware{};
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

std::string build_report(
    const Options& options,
    unsigned long long observed_presents,
    HRESULT attach_result,
    HRESULT detach_result,
    bool original_pointer_restored,
    bool render_succeeded) {
    std::ostringstream output;
    output << "{\n"
           << "  \"mode\": \"fluidruntime-present-hook-lab-v0.2\",\n"
           << "  \"target_owned\": true,\n"
           << "  \"cooperative_load\": true,\n"
           << "  \"remote_injection\": false,\n"
           << "  \"read_only_hook\": true,\n"
           << "  \"would_modify_frame_data\": false,\n"
           << "  \"render_driver\": \""
           << (options.use_hardware ? "hardware" : "warp") << "\",\n"
           << "  \"requested_presents\": " << options.frames << ",\n"
           << "  \"observed_presents\": " << observed_presents << ",\n"
           << "  \"render_succeeded\": "
           << (render_succeeded ? "true" : "false") << ",\n"
           << "  \"original_pointer_restored\": "
           << (original_pointer_restored ? "true" : "false") << ",\n"
           << "  \"attach_hresult\": \"" << hresult_hex(attach_result) << "\",\n"
           << "  \"detach_hresult\": \"" << hresult_hex(detach_result) << "\"\n"
           << "}\n";
    return output.str();
}

} // namespace

int wmain(int argc, wchar_t* argv[]) {
    const auto options = parse_options(argc, argv);
    if (!options.has_value()) {
        std::wcerr << L"Usage: fluidruntime-hook-target --hook <dll> "
                      L"[--frames <count>] [--out <report.json>] [--hardware]\n";
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

    const auto hook_module = LoadLibraryW(options->hook_path.c_str());
    if (hook_module == nullptr) {
        DestroyWindow(window);
        UnregisterClassW(window_class_name, instance);
        std::cerr << "Unable to load hook DLL.\n";
        return 5;
    }

    const auto attach = reinterpret_cast<HookAttach>(
        GetProcAddress(hook_module, "FluidHookAttach"));
    const auto detach = reinterpret_cast<HookDetach>(
        GetProcAddress(hook_module, "FluidHookDetach"));
    const auto present_count = reinterpret_cast<HookPresentCount>(
        GetProcAddress(hook_module, "FluidHookPresentCount"));
    const auto is_attached = reinterpret_cast<HookIsAttached>(
        GetProcAddress(hook_module, "FluidHookIsAttached"));
    if (attach == nullptr || detach == nullptr ||
        present_count == nullptr || is_attached == nullptr) {
        FreeLibrary(hook_module);
        DestroyWindow(window);
        UnregisterClassW(window_class_name, instance);
        std::cerr << "Hook DLL contract is incomplete.\n";
        return 5;
    }

    const auto attach_result = attach(swap_chain.Get());
    bool render_succeeded = SUCCEEDED(attach_result) && is_attached() != FALSE;
    for (unsigned long frame = 0; render_succeeded && frame < options->frames; ++frame) {
        const float red = static_cast<float>(frame % 60) / 60.0F;
        const float color[]{red, 0.2F, 1.0F - red, 1.0F};
        context->ClearRenderTargetView(render_target.Get(), color);
        render_succeeded = SUCCEEDED(swap_chain->Present(0, 0));
    }

    const auto observed_presents = present_count();
    const auto detach_result = detach();
    const auto original_pointer_restored =
        SUCCEEDED(detach_result) && is_attached() == FALSE;

    const auto report = build_report(
        *options,
        observed_presents,
        attach_result,
        detach_result,
        original_pointer_restored,
        render_succeeded);
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
        original_pointer_restored &&
        observed_presents == options->frames;
    return passed ? 0 : 6;
}
