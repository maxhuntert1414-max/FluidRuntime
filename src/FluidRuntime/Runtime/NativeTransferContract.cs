namespace FluidRuntime.Runtime;

public enum NativeTransferBackend
{
    D3D11 = 1,
    D3D12 = 2,
    Vulkan = 3
}

public enum NativeTransferOperation
{
    UpdateBuffer = 1,
    CopyBuffer = 2
}

public enum NativeTransferResourceRole
{
    HostSource = 1,
    DeviceDestination = 2,
    Readback = 3,
    Synchronization = 4
}

public sealed record NativeTransferDescriptor(
    string ContractVersion,
    NativeTransferBackend Backend,
    NativeTransferOperation Operation,
    string Scope);

public sealed record NativeTransferTopology(
    int QueueCount,
    int ExecutionScopeCount,
    int SourceResourceCount,
    int DestinationResourceCount,
    int LaneCount,
    int FenceCount,
    int RuntimeEventCount)
{
    public const string ContractVersion = "fluidruntime-native-transfer-v1";
    public const int MaximumQueueCount = 4;
    public const int MaximumExecutionScopeCount = 4;
    public const int MaximumResourceCount = 8;
    public const int MaximumLaneCount = 8;
    public const int MaximumFenceCount = 4;
    public const int MaximumRuntimeEventCount = 2048;

    public static NativeTransferTopology D3D11SingleLane(ulong candidateCount) =>
        new(
            QueueCount: 0,
            ExecutionScopeCount: 1,
            SourceResourceCount: 1,
            DestinationResourceCount: 1,
            LaneCount: 1,
            FenceCount: 0,
            RuntimeEventCount: checked((int)candidateCount + 7));

    public static NativeTransferTopology D3D12MultiLane(ulong candidateCount) =>
        new(
            QueueCount: 1,
            ExecutionScopeCount: 2,
            SourceResourceCount: 2,
            DestinationResourceCount: 2,
            LaneCount: 2,
            FenceCount: 1,
            RuntimeEventCount: checked((int)candidateCount + 17));

    public void Validate(ulong candidateCount)
    {
        if (candidateCount == 0 ||
            QueueCount is < 0 or > MaximumQueueCount ||
            ExecutionScopeCount is < 1 or > MaximumExecutionScopeCount ||
            SourceResourceCount is < 1 or > MaximumResourceCount ||
            DestinationResourceCount is < 1 or > MaximumResourceCount ||
            LaneCount is < 1 or > MaximumLaneCount ||
            FenceCount is < 0 or > MaximumFenceCount ||
            LaneCount != DestinationResourceCount ||
            ExecutionScopeCount > LaneCount ||
            RuntimeEventCount < checked((int)candidateCount) ||
            RuntimeEventCount > MaximumRuntimeEventCount)
        {
            throw new ArgumentException(
                "Native transfer topology is outside the bounded v1 contract.");
        }
    }
}

public static class NativeTransferDescriptors
{
    public static readonly NativeTransferDescriptor D3D11UpdateBuffer = new(
        NativeTransferTopology.ContractVersion,
        NativeTransferBackend.D3D11,
        NativeTransferOperation.UpdateBuffer,
        "owned-d3d11-single-context-buffer-update");

    public static readonly NativeTransferDescriptor D3D12CopyBuffer = new(
        NativeTransferTopology.ContractVersion,
        NativeTransferBackend.D3D12,
        NativeTransferOperation.CopyBuffer,
        "owned-d3d12-multi-command-list-buffer-copy");
}
