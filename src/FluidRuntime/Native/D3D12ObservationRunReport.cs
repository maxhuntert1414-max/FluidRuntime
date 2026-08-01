namespace FluidRuntime.Native;

public sealed record D3D12AdapterSnapshot
{
    public required string Description { get; init; }
    public required uint VendorId { get; init; }
    public required uint DeviceId { get; init; }
    public required uint SubsystemId { get; init; }
    public required uint Revision { get; init; }
    public required string Luid { get; init; }
    public required ulong DedicatedVideoMemoryBytes { get; init; }
    public required ulong DedicatedSystemMemoryBytes { get; init; }
    public required ulong SharedSystemMemoryBytes { get; init; }
}

public sealed record D3D12ArchitectureSnapshot
{
    public required bool Available { get; init; }
    public required uint NodeCount { get; init; }
    public required bool TileBasedRenderer { get; init; }
    public required bool Uma { get; init; }
    public required bool CacheCoherentUma { get; init; }
    public required uint ResourceHeapTier { get; init; }
}

public sealed record D3D12QueueSnapshot
{
    public required string Type { get; init; }
    public required string Priority { get; init; }
    public required bool TimestampFrequencySupported { get; init; }
    public required ulong TimestampFrequencyHz { get; init; }
}

public sealed record D3D12TransferSnapshot
{
    public required ulong BufferBytes { get; init; }
    public required ulong LogicalUploadBytes { get; init; }
    public required ulong LogicalReadbackBytes { get; init; }
    public required ulong LogicalTotalCopyBytes { get; init; }
    public required string UploadHeapType { get; init; }
    public required string DefaultHeapType { get; init; }
    public required string ReadbackHeapType { get; init; }
    public required string UploadInitialState { get; init; }
    public required string DefaultInitialState { get; init; }
    public required string DefaultFirstAccessPromotion { get; init; }
    public required string DefaultStateBeforeReadbackCopy { get; init; }
    public required string ExpectedDefaultPostExecuteState { get; init; }
    public required string ReadbackInitialState { get; init; }
    public required string CommandListType { get; init; }
    public required uint CommandListCount { get; init; }
    public required uint CopyCommandCount { get; init; }
    public required uint ResourceBarrierCount { get; init; }
    public required uint SubmittedCommandListCount { get; init; }
    public required ulong FenceSignaledValue { get; init; }
    public required ulong FenceCompletedValue { get; init; }
    public required bool WaitCompleted { get; init; }
    public required string HashAlgorithm { get; init; }
    public required string SourceHash { get; init; }
    public required string ReadbackHash { get; init; }
    public required bool ContentEquivalent { get; init; }
    public required double CpuRecordMicroseconds { get; init; }
    public required double SubmitToFenceMicroseconds { get; init; }
    public required double TotalWorkloadMicroseconds { get; init; }
}

public sealed record D3D12VideoMemorySnapshot
{
    public required bool Available { get; init; }
    public required ulong BudgetBytes { get; init; }
    public required ulong CurrentUsageBytes { get; init; }
    public required ulong CurrentReservationBytes { get; init; }
    public required ulong AvailableForReservationBytes { get; init; }
}

public sealed record D3D12MemorySnapshot
{
    public required string Source { get; init; }
    public required D3D12VideoMemorySnapshot LocalBefore { get; init; }
    public required D3D12VideoMemorySnapshot LocalAfter { get; init; }
    public required D3D12VideoMemorySnapshot NonLocalBefore { get; init; }
    public required D3D12VideoMemorySnapshot NonLocalAfter { get; init; }
}

public sealed record D3D12ObservationRunReport
{
    public required string Mode { get; init; }
    public required bool TargetOwned { get; init; }
    public required bool CooperativeLoad { get; init; }
    public required bool RemoteInjection { get; init; }
    public required bool ReadOnlyObservation { get; init; }
    public required bool ActuationEnabled { get; init; }
    public required bool PhysicalTransferBytesMeasured { get; init; }
    public required bool DebugLayerEnabled { get; init; }
    public required bool DebugMessageValidationAvailable { get; init; }
    public required ulong DebugWarningCount { get; init; }
    public required ulong DebugErrorCount { get; init; }
    public required string RenderDriver { get; init; }
    public required int ProcessId { get; init; }
    public required long CapturedAtUnixMs { get; init; }
    public required D3D12AdapterSnapshot Adapter { get; init; }
    public required D3D12ArchitectureSnapshot Architecture { get; init; }
    public required D3D12QueueSnapshot Queue { get; init; }
    public required D3D12TransferSnapshot Transfer { get; init; }
    public required D3D12MemorySnapshot Memory { get; init; }
    public required string ClaimScope { get; init; }
    public required List<string> Limitations { get; init; }
}
