using System.Text.Json;

namespace FluidRuntime.Runtime;

public sealed record D3D12CopyElisionRunReport(
    bool Optimized,
    int ProcessId,
    uint RingAbiVersion,
    uint RingCapacity,
    string RenderDriver,
    string AdapterDescription,
    uint AdapterVendorId,
    uint AdapterDeviceId,
    string AdapterLuid,
    bool Uma,
    bool CacheCoherentUma,
    uint ResourceHeapTier,
    long EventCount,
    long LostSequenceCount,
    long NativeOverrunCount,
    long CandidateCount,
    ulong CandidateBytes,
    string SourceSnapshotMode,
    ulong SourceSnapshotBytes,
    bool UploadUnmappedAfterRegistration,
    long TrackedCopyCount,
    ulong TrackedCopyBytes,
    long ForwardedCopyCount,
    ulong ForwardedCopyBytes,
    long SkippedCopyCount,
    ulong SkippedCopyBytes,
    long ExactComparisonCount,
    ulong ExactComparisonBytes,
    long AutomaticInvalidationCount,
    long ExplicitInvalidationCount,
    long CommandListCloseCount,
    bool SourceTransitionApplied,
    bool AutomaticInvalidationGuardApplied,
    bool ExplicitInvalidationGuardApplied,
    bool ImmutableSourcesVerified,
    bool ContentEquivalent,
    bool FenceCompleted,
    bool DebugValidationPassed,
    bool RollbackRestored,
    string PatternAHash,
    string PatternBHash,
    string FinalHash,
    double CpuRecordMicroseconds,
    double SubmitToFenceMicroseconds,
    double TotalWorkloadMicroseconds,
    double? GpuWorkloadMicroseconds,
    JsonElement TargetReport)
{
    public GatewayUpdateUploadAuthorization? GatewayAuthorization { get; init; }

    public long ManagedEndToEndElapsedMicroseconds { get; init; }

    public long PublishedPolicyExpiresAtQpc { get; init; }

    public ulong PublishedPolicyActionMask { get; init; }

    public ulong PublishedPolicyActionBudget { get; init; }
}

public sealed record D3D12CopyElisionTrialReport(
    int PairIndex,
    string Phase,
    bool IncludedInStatistics,
    string ExecutionOrder,
    bool ContentEquivalent,
    bool FenceCompletedInBothRuns,
    bool RollbackRestoredInBothRuns,
    bool AdapterIdentityMatched,
    D3D12CopyElisionRunReport Baseline,
    D3D12CopyElisionRunReport Optimized);

public sealed record GatewayD3D12CopyLabReport(
    string Mode,
    bool TargetOwned,
    bool CooperativeLoad,
    bool RemoteInjection,
    bool FailClosed,
    bool PhysicalTransferBytesMeasured,
    string PolicyOrigin,
    string Protocol,
    string ContractSha256,
    string AdvertisedServerName,
    string AdvertisedServerVersion,
    bool PeerProcessBindingVerified,
    bool PeerCryptographicallyAuthenticated,
    int PeerProcessId,
    string PeerExecutablePath,
    string PeerExecutableSha256,
    string TargetSha256,
    string HookSha256,
    int TrialPairsRequested,
    int WarmupPairs,
    int IncludedTrialPairs,
    string OrderingPolicy,
    ulong BufferBytes,
    string SourceSnapshotMode,
    ulong SourceSnapshotBytes,
    bool UploadUnmappedAfterRegistration,
    int CandidateActionCount,
    ulong AvoidedLogicalBytesPerOptimizedRun,
    string AdapterDescription,
    uint AdapterVendorId,
    uint AdapterDeviceId,
    string AdapterLuid,
    bool Uma,
    bool CacheCoherentUma,
    uint ResourceHeapTier,
    bool ContentEquivalent,
    bool ImmutableSourceGuardPassed,
    bool AutomaticInvalidationGuardPassed,
    bool ExplicitInvalidationGuardPassed,
    bool FenceCompletedInAllRuns,
    bool RollbackRestoredInAllRuns,
    int AuthorizationRunCount,
    long GatewayRoundTripCount,
    long FluidLinkBytesSent,
    long FluidLinkBytesReceived,
    TailLatencyDistribution AuthorizationLatencyMicroseconds,
    PairedTailLatencySummary ManagedEndToEndMicroseconds,
    PairedTailLatencySummary CpuRecordMicroseconds,
    PairedTailLatencySummary SubmitToFenceMicroseconds,
    PairedTailLatencySummary TotalWorkloadMicroseconds,
    PairedTailLatencySummary? GpuWorkloadMicroseconds,
    int NativeOptimizedWinRequirement,
    string ClaimScope,
    string PerformanceClaimBasis,
    bool PerformanceClaimAllowed,
    IReadOnlyList<string> PerformanceClaimBlockers,
    IReadOnlyList<GatewayUpdateUploadAuthorization> Authorizations,
    IReadOnlyList<D3D12CopyElisionTrialReport> Trials);

public sealed record GatewayD3D12CopyFailClosedReport(
    string Mode,
    bool FailClosed,
    bool NativePolicyPublished,
    string FailureType,
    string FailureMessage,
    int CompletedAuthorizationRoundTrips,
    long AuthorizationElapsedMicroseconds,
    int AuthorizationDeadlineMilliseconds,
    long CompleteFallbackElapsedMicroseconds,
    string TargetSha256,
    string HookSha256,
    bool AllTrackedCopiesForwarded,
    bool NoCopiesSkipped,
    bool ContentEquivalent,
    bool FenceCompleted,
    bool RollbackRestored,
    D3D12CopyElisionRunReport BaselineFallback);

public sealed class GatewayD3D12CopyAuthorizationDeniedException : Exception
{
    public GatewayD3D12CopyAuthorizationDeniedException(
        Exception failure,
        GatewayD3D12CopyFailClosedReport report)
        : base("FluidGateway denied D3D12 copy actuation; baseline completed.", failure)
    {
        FailClosedReport = report;
    }

    public GatewayD3D12CopyFailClosedReport FailClosedReport { get; }
}
