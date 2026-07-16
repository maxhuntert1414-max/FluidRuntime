cmake_minimum_required(VERSION 3.25)

project(FluidRuntimeNativeProbe VERSION 0.8.0 LANGUAGES CXX)

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
