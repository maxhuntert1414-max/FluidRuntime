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
    ResourceDestroy = 11
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
    uint Flags)
{
    public bool IsRedundantCopyCandidate =>
        Type == HookEventType.CopyResource && (Flags & 1) != 0;

    public bool WasCopySkipped =>
        Type == HookEventType.CopyResource && (Flags & 2) != 0;
}
