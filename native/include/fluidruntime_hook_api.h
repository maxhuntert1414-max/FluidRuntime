#pragma once

#include <windows.h>
#include <dxgi.h>

#include <cstdint>

constexpr std::uint32_t fluid_hook_snapshot_abi_version = 2;

struct FluidHookSnapshotV1 {
    std::uint32_t struct_size;
    std::uint32_t abi_version;
    std::uint64_t present_count;
    std::uint64_t create_buffer_count;
    std::uint64_t buffer_bytes_requested;
    std::uint64_t create_texture2d_count;
    std::uint64_t texture_bytes_estimated;
    std::uint64_t map_write_count;
    std::uint64_t unmap_write_count;
    std::uint64_t update_subresource_count;
    std::uint64_t copy_resource_count;
    std::uint64_t copy_resource_bytes_estimated;
    std::uint64_t redundant_copy_candidate_count;
    std::uint64_t redundant_copy_bytes_estimated;
    std::uint64_t tracked_resource_count;
    std::uint64_t hook_refresh_count;
    std::uint64_t hook_refresh_failure_count;
};

#ifdef FLUIDRUNTIME_HOOK_EXPORTS
#define FLUID_HOOK_API extern "C" __declspec(dllexport)
#else
#define FLUID_HOOK_API extern "C"
#endif

FLUID_HOOK_API HRESULT WINAPI FluidHookAttach(IDXGISwapChain* swap_chain);
FLUID_HOOK_API HRESULT WINAPI FluidHookDetach();
FLUID_HOOK_API std::uint64_t WINAPI FluidHookPresentCount();
FLUID_HOOK_API BOOL WINAPI FluidHookIsAttached();
FLUID_HOOK_API HRESULT WINAPI FluidHookReadSnapshot(FluidHookSnapshotV1* snapshot);

using FluidHookAttachFunction = HRESULT(WINAPI*)(IDXGISwapChain*);
using FluidHookDetachFunction = HRESULT(WINAPI*)();
using FluidHookPresentCountFunction = std::uint64_t(WINAPI*)();
using FluidHookIsAttachedFunction = BOOL(WINAPI*)();
using FluidHookReadSnapshotFunction = HRESULT(WINAPI*)(FluidHookSnapshotV1*);

#undef FLUID_HOOK_API
