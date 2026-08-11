cmake_minimum_required(VERSION 3.25)

project(FluidRuntimeNativeProbe VERSION 0.16.0 LANGUAGES CXX)

if(NOT WIN32)
    message(FATAL_ERROR "FluidRuntime native probe currently requires Windows.")
endif()

function(configure_fluidruntime_native_target target)
    target_compile_features(${target} PRIVATE cxx_std_20)
    target_compile_definitions(
        ${target}
        PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX
    )
    target_compile_options(${target} PRIVATE /W4 /permissive- /EHsc)
endfunction()

add_executable(fluidruntime-native-probe src/main.cpp)
configure_fluidruntime_native_target(fluidruntime-native-probe)
target_link_libraries(fluidruntime-native-probe PRIVATE pdh psapi)

add_executable(fluidruntime-d3d12-observation src/d3d12_observation.cpp)
configure_fluidruntime_native_target(fluidruntime-d3d12-observation)
target_link_libraries(fluidruntime-d3d12-observation PRIVATE d3d12 dxgi)

add_library(fluidruntime-present-hook SHARED src/present_hook.cpp)
configure_fluidruntime_native_target(fluidruntime-present-hook)
target_compile_definitions(fluidruntime-present-hook PRIVATE FLUIDRUNTIME_HOOK_EXPORTS)
target_include_directories(fluidruntime-present-hook PRIVATE include)
target_link_libraries(fluidruntime-present-hook PRIVATE dxgi)

add_executable(fluidruntime-hook-target src/hook_target.cpp)
configure_fluidruntime_native_target(fluidruntime-hook-target)
target_include_directories(fluidruntime-hook-target PRIVATE include)
target_link_libraries(fluidruntime-hook-target PRIVATE d3d11 dxgi)

enable_testing()
add_test(
    NAME fluidruntime-native-probe-self-test
    COMMAND fluidruntime-native-probe --self-test
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
