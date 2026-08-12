namespace FluidRuntime.Native;

public enum HookEventType : uint
{
    Present = 1,
    CreateBuffer = 2,
    CreateTexture2D = 3,
    MapWrite = 4,
    UnmapWrite = 5,
    UpdateSubresource = 6,
    CopyResource = 7,
    HookRefresh = 8,
    ResourceRetire = 9,
    ResourceReuse = 10,
    ResourceDestroy = 11,
    CopySubresourceRegion = 12,
    ClearRenderTargetView = 13,
    ClearUnorderedAccessViewFloat = 14,
    ControlPolicyAccepted = 15,
    MapRead = 16,
    TransferBufferCopy = 17,
    TransferResourceInvalidate = 18,
    TransferScopeClose = 19,
    TransferScopeReset = 20,
    TransferQueueSubmit = 21,
    TransferSyncSignal = 22,
    D3D12CopyBufferRegion = TransferBufferCopy,
    D3D12ResourceInvalidate = TransferResourceInvalidate,
    D3D12CommandListClose = TransferScopeClose,
    D3D12CommandListReset = TransferScopeReset,
    D3D12QueueExecute = TransferQueueSubmit,
    D3D12QueueSignal = TransferSyncSignal
}

public sealed record HookIpcEvent(
    long Sequence,
    long QpcTicks,
    HookEventType Type,
    uint ThreadId,
    ulong ResourceA,
    ulong ResourceB,
    ulong SizeBytes,
    ulong Generation,
    uint Flags,
    uint SubresourceA = 0,
    uint SubresourceB = 0,
    ulong RegionKey = 0)
{
    public bool IsRedundantCopyCandidate =>
        Type == HookEventType.CopyResource && (Flags & 1) != 0;

    public bool WasCopySkipped =>
        Type == HookEventType.CopyResource && (Flags & 2) != 0;

    public bool IsRedundantSubresourceCopyCandidate =>
        Type == HookEventType.CopySubresourceRegion && (Flags & 1) != 0;

    public bool IsPreciseSubresourceWrite =>
        (Type is HookEventType.ClearRenderTargetView or
            HookEventType.ClearUnorderedAccessViewFloat) &&
        (Flags & 8) != 0;

    public bool IsReadbackTransfer =>
        Type is HookEventType.CopyResource or HookEventType.MapRead &&
        (Flags & 16) != 0;

    public bool IsUploadTransfer =>
        (Type is HookEventType.CopyResource or HookEventType.MapWrite or
            HookEventType.UnmapWrite or HookEventType.UpdateSubresource) &&
        (Flags & 32) != 0;

    public bool IsContentCompared =>
        Type == HookEventType.UpdateSubresource && (Flags & 64) != 0;

    public bool IsRedundantUpdateSubresourceCandidate =>
        Type == HookEventType.UpdateSubresource && (Flags & 1) != 0;

    public bool WasUpdateSubresourceSkipped =>
        Type == HookEventType.UpdateSubresource && (Flags & 2) != 0;

    public bool IsTransferRedundantCandidate =>
        Type == HookEventType.TransferBufferCopy && (Flags & 1) != 0;

    public bool WasTransferSkipped =>
        Type == HookEventType.TransferBufferCopy && (Flags & 2) != 0;

    public bool IsTransferExactContentCompared =>
        Type == HookEventType.TransferBufferCopy && (Flags & 64) != 0;

    public bool IsTransferImmutableHostSource =>
        Type == HookEventType.TransferBufferCopy && (Flags & 128) != 0;

    public bool IsTransferExplicitInvalidation =>
        Type == HookEventType.TransferResourceInvalidate && (Flags & 256) != 0;

    public bool IsD3D12RedundantCandidate => IsTransferRedundantCandidate;

    public bool WasD3D12CopySkipped => WasTransferSkipped;

    public bool IsD3D12ExactContentCompared => IsTransferExactContentCompared;

    public bool IsD3D12ImmutableUploadSource => IsTransferImmutableHostSource;

    public bool IsD3D12ExplicitInvalidation => IsTransferExplicitInvalidation;

    public bool IsGeneralizedTransfer => (Flags & 512) != 0;
}
