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
    ControlPolicyAccepted = 15
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
}
