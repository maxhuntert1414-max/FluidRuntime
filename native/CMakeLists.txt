cmake_minimum_required(VERSION 3.25)

project(FluidRuntimeNativeProbe VERSION 0.23.0 LANGUAGES CXX)

if(NOT WIN32)
    message(FATAL_ERROR "FluidRuntime native probe currently requires Windows.")
endif()

option(
    FLUIDRUNTIME_ENABLE_ASAN
    "Build owned native targets with Microsoft AddressSanitizer"
    OFF
)

function(configure_fluidruntime_native_target target)
    target_compile_features(${target} PRIVATE cxx_std_20)
    target_compile_definitions(
        ${target}
        PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX
    )
    target_compile_options(
        ${target}
        PRIVATE /W4 /WX /permissive- /EHsc /GS /sdl /guard:cf
    )
    target_link_options(
        ${target}
        PRIVATE /guard:cf /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA
    )
    if(FLUIDRUNTIME_ENABLE_ASAN)
        target_compile_options(${target} PRIVATE /fsanitize=address /Zi)
        target_link_options(${target} PRIVATE /INCREMENTAL:NO /DEBUG)
    endif()
endfunction()

add_executable(fluidruntime-native-probe src/main.cpp)
configure_fluidruntime_native_target(fluidruntime-native-probe)
target_link_libraries(fluidruntime-native-probe PRIVATE pdh psapi)

add_executable(fluidruntime-d3d12-observation src/d3d12_observation.cpp)
configure_fluidruntime_native_target(fluidruntime-d3d12-observation)
target_link_libraries(fluidruntime-d3d12-observation PRIVATE d3d12 dxgi)

add_library(fluidruntime-d3d12-hook SHARED src/d3d12_hook.cpp)
configure_fluidruntime_native_target(fluidruntime-d3d12-hook)
target_compile_definitions(
    fluidruntime-d3d12-hook
    PRIVATE FLUIDRUNTIME_D3D12_HOOK_EXPORTS
)
target_include_directories(fluidruntime-d3d12-hook PRIVATE include)
target_link_libraries(fluidruntime-d3d12-hook PRIVATE d3d12)

add_executable(fluidruntime-d3d12-hook-target src/d3d12_hook_target.cpp)
configure_fluidruntime_native_target(fluidruntime-d3d12-hook-target)
target_include_directories(fluidruntime-d3d12-hook-target PRIVATE include)
target_link_libraries(fluidruntime-d3d12-hook-target PRIVATE d3d12 dxgi)

add_library(fluidruntime-d3d12-transfer-hook SHARED src/d3d12_transfer_hook.cpp)
configure_fluidruntime_native_target(fluidruntime-d3d12-transfer-hook)
target_compile_definitions(
    fluidruntime-d3d12-transfer-hook
    PRIVATE FLUIDRUNTIME_D3D12_HOOK_EXPORTS
)
target_include_directories(fluidruntime-d3d12-transfer-hook PRIVATE include)
target_link_libraries(fluidruntime-d3d12-transfer-hook PRIVATE d3d12)

add_executable(fluidruntime-d3d12-transfer-target src/d3d12_transfer_target.cpp)
configure_fluidruntime_native_target(fluidruntime-d3d12-transfer-target)
target_include_directories(fluidruntime-d3d12-transfer-target PRIVATE include)
target_link_libraries(fluidruntime-d3d12-transfer-target PRIVATE d3d12 dxgi)

add_library(fluidruntime-present-hook SHARED src/present_hook.cpp)
configure_fluidruntime_native_target(fluidruntime-present-hook)
target_compile_definitions(fluidruntime-present-hook PRIVATE FLUIDRUNTIME_HOOK_EXPORTS)
target_include_directories(fluidruntime-present-hook PRIVATE include)
target_link_libraries(fluidruntime-present-hook PRIVATE dxgi)

add_executable(fluidruntime-hook-target src/hook_target.cpp)
configure_fluidruntime_native_target(fluidruntime-hook-target)
target_include_directories(fluidruntime-hook-target PRIVATE include)
target_link_libraries(fluidruntime-hook-target PRIVATE d3d11 dxgi)

option(FLUIDRUNTIME_ENABLE_VULKAN "Build the cooperative native Vulkan backend" ON)
option(FLUIDRUNTIME_TEST_VULKAN_GPU "Run Vulkan tests requiring a Vulkan ICD" OFF)
if(FLUIDRUNTIME_ENABLE_VULKAN)
    include(FetchContent)
    set(VULKAN_HEADERS_ENABLE_MODULE OFF CACHE BOOL "FluidRuntime uses the Vulkan C API, not C++ modules")
    FetchContent_Declare(VulkanHeaders
        URL https://github.com/KhronosGroup/Vulkan-Headers/archive/refs/tags/v1.3.290.tar.gz
        URL_HASH SHA256=f38a653bf93cab7a2a229a53d2d53b1cba9a2819e4c0a7de13c54085bde9bcf5
        DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
    FetchContent_MakeAvailable(VulkanHeaders)
    add_library(fluidruntime-vulkan-observe SHARED src/vulkan_observation_layer.cpp)
    configure_fluidruntime_native_target(fluidruntime-vulkan-observe)
    target_include_directories(fluidruntime-vulkan-observe PRIVATE include)
    target_link_libraries(fluidruntime-vulkan-observe PRIVATE Vulkan::Headers)
    add_executable(fluidruntime-vulkan-observation-tests src/vulkan_observation_tests.cpp)
    configure_fluidruntime_native_target(fluidruntime-vulkan-observation-tests)
    target_include_directories(fluidruntime-vulkan-observation-tests PRIVATE include)
    target_link_libraries(fluidruntime-vulkan-observation-tests PRIVATE Vulkan::Headers)
    add_executable(fluidruntime-vulkan-buffer-tracking-tests src/vulkan_buffer_tracking_tests.cpp)
    configure_fluidruntime_native_target(fluidruntime-vulkan-buffer-tracking-tests)
    target_include_directories(fluidruntime-vulkan-buffer-tracking-tests PRIVATE include)
    add_executable(fluidruntime-vulkan-command-tracking-tests src/vulkan_command_tracking_tests.cpp)
    configure_fluidruntime_native_target(fluidruntime-vulkan-command-tracking-tests)
    target_include_directories(fluidruntime-vulkan-command-tracking-tests PRIVATE include)
    configure_file(vulkan-observe.json.in vulkan-observe.json COPYONLY)
    add_custom_command(TARGET fluidruntime-vulkan-observe POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            ${CMAKE_CURRENT_BINARY_DIR}/vulkan-observe.json
            $<TARGET_FILE_DIR:fluidruntime-vulkan-observe>/vulkan-observe.json)
    add_library(fluidruntime-vulkan-transfer SHARED src/vulkan_transfer.cpp)
    configure_fluidruntime_native_target(fluidruntime-vulkan-transfer)
    target_include_directories(fluidruntime-vulkan-transfer PRIVATE include)
    target_link_libraries(fluidruntime-vulkan-transfer PRIVATE Vulkan::Headers)
    add_executable(fluidruntime-vulkan-transfer-target src/vulkan_transfer_target.cpp)
    configure_fluidruntime_native_target(fluidruntime-vulkan-transfer-target)
    target_include_directories(fluidruntime-vulkan-transfer-target PRIVATE include)
    add_executable(fluidruntime-vulkan-policy-tests src/vulkan_policy_tests.cpp)
    configure_fluidruntime_native_target(fluidruntime-vulkan-policy-tests)
    target_include_directories(fluidruntime-vulkan-policy-tests PRIVATE include)
endif()

enable_testing()
if(FLUIDRUNTIME_ENABLE_VULKAN)
    add_test(NAME fluidruntime-vulkan-observation-dispatch
        COMMAND fluidruntime-vulkan-observation-tests $<TARGET_FILE:fluidruntime-vulkan-observe>)
    set_tests_properties(fluidruntime-vulkan-observation-dispatch PROPERTIES TIMEOUT 30)
    add_test(NAME fluidruntime-vulkan-buffer-tracking COMMAND fluidruntime-vulkan-buffer-tracking-tests)
    set_tests_properties(fluidruntime-vulkan-buffer-tracking PROPERTIES TIMEOUT 30)
    add_test(NAME fluidruntime-vulkan-command-tracking COMMAND fluidruntime-vulkan-command-tracking-tests)
    set_tests_properties(fluidruntime-vulkan-command-tracking PROPERTIES TIMEOUT 30)
    add_test(NAME fluidruntime-vulkan-policy-tests COMMAND fluidruntime-vulkan-policy-tests)
    add_test(NAME fluidruntime-vulkan-rejects-invalid-count
        COMMAND fluidruntime-vulkan-transfer-target --candidate-count 129)
    set_tests_properties(fluidruntime-vulkan-rejects-invalid-count PROPERTIES WILL_FAIL TRUE)
    if(FLUIDRUNTIME_TEST_VULKAN_GPU)
        foreach(mode baseline controlled guards)
            add_test(NAME fluidruntime-vulkan-${mode}
                COMMAND fluidruntime-vulkan-transfer-target
                    --library $<TARGET_FILE:fluidruntime-vulkan-transfer>
                    --candidate-count 16 --mode ${mode})
            set_tests_properties(fluidruntime-vulkan-${mode} PROPERTIES TIMEOUT 30
                ENVIRONMENT "VK_LOADER_LAYERS_DISABLE=~implicit~")
        endforeach()
        foreach(count 1 127 128)
            add_test(NAME fluidruntime-vulkan-controlled-${count}
                COMMAND fluidruntime-vulkan-transfer-target
                    --library $<TARGET_FILE:fluidruntime-vulkan-transfer>
                    --candidate-count ${count} --mode controlled)
            set_tests_properties(fluidruntime-vulkan-controlled-${count} PROPERTIES TIMEOUT 30
                ENVIRONMENT "VK_LOADER_LAYERS_DISABLE=~implicit~")
        endforeach()
    endif()
endif()
add_test(
    NAME fluidruntime-native-probe-self-test
    COMMAND fluidruntime-native-probe --self-test
)
add_test(
    NAME fluidruntime-native-probe-series-self-test
    COMMAND fluidruntime-native-probe --self-test --samples 3
)
add_test(
    NAME fluidruntime-native-probe-rejects-oversized-series
    COMMAND fluidruntime-native-probe --self-test --samples 101
)
set_tests_properties(
    fluidruntime-native-probe-rejects-oversized-series
    PROPERTIES WILL_FAIL TRUE
)
add_test(
    NAME fluidruntime-d3d12-observation-warp
    COMMAND fluidruntime-d3d12-observation
        --hardware false
        --gpu-timeout-ms 10000
)
add_test(
    NAME fluidruntime-d3d12-observation-rejects-invalid-driver
    COMMAND fluidruntime-d3d12-observation --hardware maybe
)
set_tests_properties(
    fluidruntime-d3d12-observation-rejects-invalid-driver
    PROPERTIES WILL_FAIL TRUE
)
add_test(
    NAME fluidruntime-d3d12-observation-rejects-invalid-timeout
    COMMAND fluidruntime-d3d12-observation --gpu-timeout-ms 0
)
set_tests_properties(
    fluidruntime-d3d12-observation-rejects-invalid-timeout
    PROPERTIES WILL_FAIL TRUE
)
add_test(
    NAME fluidruntime-d3d12-copy-elision-baseline
    COMMAND fluidruntime-d3d12-hook-target
        --hook $<TARGET_FILE:fluidruntime-d3d12-hook>
        --hardware false
        --candidate-count 16
        --hold-ms 1
)
add_test(
    NAME fluidruntime-d3d12-copy-elision-controlled
    COMMAND fluidruntime-d3d12-hook-target
        --hook $<TARGET_FILE:fluidruntime-d3d12-hook>
        --hardware false
        --candidate-count 16
        --hold-ms 1
        --self-publish-control
)
add_test(
    NAME fluidruntime-d3d12-copy-elision-rejects-invalid-count
    COMMAND fluidruntime-d3d12-hook-target
        --hook $<TARGET_FILE:fluidruntime-d3d12-hook>
        --candidate-count 129
)
set_tests_properties(
    fluidruntime-d3d12-copy-elision-rejects-invalid-count
    PROPERTIES WILL_FAIL TRUE
)
add_test(
    NAME fluidruntime-d3d12-transfer-baseline
    COMMAND fluidruntime-d3d12-transfer-target
        --hook $<TARGET_FILE:fluidruntime-d3d12-transfer-hook>
        --hardware false
        --candidate-count 16
        --hold-ms 1
)
add_test(
    NAME fluidruntime-d3d12-transfer-controlled
    COMMAND fluidruntime-d3d12-transfer-target
        --hook $<TARGET_FILE:fluidruntime-d3d12-transfer-hook>
        --hardware false
        --candidate-count 16
        --hold-ms 1
        --self-publish-control
)
add_test(
    NAME fluidruntime-d3d12-transfer-rejects-invalid-topology
    COMMAND fluidruntime-d3d12-transfer-target
        --hook $<TARGET_FILE:fluidruntime-d3d12-transfer-hook>
        --hardware false
        --candidate-count 16
        --hold-ms 1
        --invalid-topology
)
add_test(
    NAME fluidruntime-d3d12-transfer-rejects-incomplete-registration
    COMMAND fluidruntime-d3d12-transfer-target
        --hook $<TARGET_FILE:fluidruntime-d3d12-transfer-hook>
        --hardware false
        --candidate-count 16
        --hold-ms 1
        --incomplete-registration
)
add_test(
    NAME fluidruntime-d3d12-transfer-rejects-aliased-destination
    COMMAND fluidruntime-d3d12-transfer-target
        --hook $<TARGET_FILE:fluidruntime-d3d12-transfer-hook>
        --hardware false
        --candidate-count 16
        --hold-ms 1
        --aliased-destination
)
add_test(
    NAME fluidruntime-present-hook-lab
    COMMAND fluidruntime-hook-target
        --hook $<TARGET_FILE:fluidruntime-present-hook>
        --frames 30
)
add_test(
    NAME fluidruntime-copy-elision-lab
    COMMAND fluidruntime-hook-target
        --hook $<TARGET_FILE:fluidruntime-present-hook>
        --frames 30
        --skip-first-redundant-copy
)
add_test(
    NAME fluidruntime-cooperative-lifetime-fallback
    COMMAND fluidruntime-hook-target
        --hook $<TARGET_FILE:fluidruntime-present-hook>
        --frames 30
        --cooperative-lifetime
)
add_test(
    NAME fluidruntime-concurrent-lifetime-detach
    COMMAND fluidruntime-hook-target
        --hook $<TARGET_FILE:fluidruntime-present-hook>
        --concurrent-lifetime-stress
)
add_test(
    NAME fluidruntime-sustained-copy-baseline
    COMMAND fluidruntime-hook-target
        --hook $<TARGET_FILE:fluidruntime-present-hook>
        --frames 1
        --sustained-copy-count 128
)
add_test(
    NAME fluidruntime-readback-elision-baseline
    COMMAND fluidruntime-hook-target
        --hook $<TARGET_FILE:fluidruntime-present-hook>
        --frames 1
        --readback-copy-count 64
)
add_test(
    NAME fluidruntime-upload-elision-baseline
    COMMAND fluidruntime-hook-target
        --hook $<TARGET_FILE:fluidruntime-present-hook>
        --frames 1
        --upload-copy-count 64
)
add_test(
    NAME fluidruntime-update-upload-elision-baseline
    COMMAND fluidruntime-hook-target
        --hook $<TARGET_FILE:fluidruntime-present-hook>
        --frames 1
        --update-upload-count 64
)
add_test(
    NAME fluidruntime-update-upload-elision-max-baseline
    COMMAND fluidruntime-hook-target
        --hook $<TARGET_FILE:fluidruntime-present-hook>
        --frames 1
        --update-upload-count 128
)
