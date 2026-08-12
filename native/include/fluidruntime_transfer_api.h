#pragma once

#include <cstdint>

constexpr std::uint32_t fluid_transfer_contract_abi_version = 1;
constexpr std::uint32_t fluid_transfer_max_queue_count = 4;
constexpr std::uint32_t fluid_transfer_max_execution_scope_count = 4;
constexpr std::uint32_t fluid_transfer_max_resource_count = 8;
constexpr std::uint32_t fluid_transfer_max_lane_count = 8;
constexpr std::uint32_t fluid_transfer_max_fence_count = 4;
constexpr std::uint32_t fluid_transfer_max_runtime_event_count = 2048;
constexpr std::uint64_t fluid_transfer_max_resource_bytes =
    4ULL * 1024ULL * 1024ULL;
constexpr std::uint64_t fluid_transfer_max_total_retained_bytes =
    16ULL * 1024ULL * 1024ULL;
constexpr wchar_t fluid_transfer_ring_name_prefix[] =
    L"Local\\FluidRuntimeTransfer-";

enum class FluidTransferBackendV1 : std::uint32_t {
    d3d11 = 1,
    d3d12 = 2,
    vulkan = 3,
};

enum class FluidTransferOperationV1 : std::uint32_t {
    update_buffer = 1,
    copy_buffer = 2,
};

enum class FluidTransferResourceRoleV1 : std::uint32_t {
    host_source = 1,
    device_destination = 2,
    readback = 3,
    synchronization = 4,
};

struct FluidTransferTopologyV1 {
    std::uint32_t struct_size;
    std::uint32_t abi_version;
    std::uint32_t backend;
    std::uint32_t operation;
    std::uint32_t queue_count;
    std::uint32_t execution_scope_count;
    std::uint32_t source_resource_count;
    std::uint32_t destination_resource_count;
    std::uint32_t lane_count;
    std::uint32_t fence_count;
    std::uint32_t max_action_count;
    std::uint32_t expected_runtime_event_count;
    std::uint64_t max_resource_bytes;
    std::uint64_t max_total_retained_bytes;
};

static_assert(sizeof(FluidTransferTopologyV1) == 64);
static_assert(static_cast<std::uint32_t>(FluidTransferBackendV1::d3d11) == 1);
static_assert(static_cast<std::uint32_t>(FluidTransferBackendV1::d3d12) == 2);
static_assert(static_cast<std::uint32_t>(FluidTransferBackendV1::vulkan) == 3);
static_assert(static_cast<std::uint32_t>(FluidTransferOperationV1::update_buffer) == 1);
static_assert(static_cast<std::uint32_t>(FluidTransferOperationV1::copy_buffer) == 2);
static_assert(static_cast<std::uint32_t>(FluidTransferResourceRoleV1::host_source) == 1);
static_assert(static_cast<std::uint32_t>(FluidTransferResourceRoleV1::device_destination) == 2);
static_assert(static_cast<std::uint32_t>(FluidTransferResourceRoleV1::readback) == 3);
static_assert(static_cast<std::uint32_t>(FluidTransferResourceRoleV1::synchronization) == 4);
