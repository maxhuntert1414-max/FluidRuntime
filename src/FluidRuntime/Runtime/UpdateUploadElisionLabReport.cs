using System.Text.Json;

namespace FluidRuntime.Runtime;

public sealed record UpdateUploadElisionRunReport(
    bool Optimized,
    int ProcessId,
    uint RingAbiVersion,
    uint RingCapacity,
    string RenderDriver,
    string AdapterDescription,
    uint AdapterVendorId,
    uint AdapterDeviceId,
    string AdapterLuid,
    long EventCount,
    long LostSequenceCount,
    long NativeOverrunCount,
    long DirectUploadUpdateCount,
    ulong DirectUploadBytes,
    long RedundantUpdateCandidateCount,
    ulong RedundantUpdateCandidateBytes,
    long ForwardedUpdateSubresourceCount,
    ulong ForwardedUpdateSubresourceBytes,
    long SkippedUpdateSubresourceCount,
    ulong SkippedUpdateSubresourceBytes,
    long ContentCacheResourceCount,
    ulong ContentCacheBytes,
    long PublishedPolicyEpoch,
    long AcknowledgedPolicyEpoch,
    long AppliedPolicyActions,
    string PolicyStatus,
    bool MutationApplied,
    bool GenerationGuardApplied,
    bool ContentEquivalent,
    bool RollbackRestored,
    string InitialHash,
    string FinalHash,
    string GuardHash,
    string PostDetachDestinationHash,
    double CpuWorkloadMicroseconds,
    double? GpuWorkloadMicroseconds,
    JsonElement TargetReport)
{
    public GatewayUpdateUploadAuthorization? GatewayAuthorization { get; init; }

    public long ManagedEndToEndElapsedMicroseconds { get; init; }

    public long PublishedPolicyExpiresAtQpc { get; init; }

    public ulong PublishedPolicyActionMask { get; init; }

    public ulong PublishedPolicyActionBudget { get; init; }
}

public sealed record UpdateUploadElisionTrialReport(
    int PairIndex,
    string Phase,
    bool IncludedInStatistics,
    string ExecutionOrder,
    bool ContentEquivalent,
    bool RollbackRestoredInBothRuns,
    bool AdapterIdentityMatched,
    double BaselineCpuMicroseconds,
    double OptimizedCpuMicroseconds,
    double? BaselineGpuMicroseconds,
    double? OptimizedGpuMicroseconds,
    UpdateUploadElisionRunReport Baseline,
    UpdateUploadElisionRunReport Optimized);

public sealed record UpdateUploadElisionLabReport(
    string Mode,
    bool TargetOwned,
    bool CooperativeLoad,
    bool RemoteInjection,
    int BufferBytes,
    int RequiredUpdateCountPerRun,
    int RedundantUpdateCountPerOptimizedRun,
    ulong AvoidedUpdateBytesPerOptimizedRun,
    int ExactContentCacheResourceLimit,
    ulong ExactContentCacheByteLimit,
    int TrialPairsRequested,
    int WarmupPairs,
    int IncludedTrialPairs,
    string OrderingPolicy,
    string AdapterDescription,
    uint AdapterVendorId,
    uint AdapterDeviceId,
    string AdapterLuid,
    bool MutationGuardPassed,
    bool GenerationGuardPassed,
    bool ContentEquivalent,
    bool RollbackRestoredInAllRuns,
    string ClaimScope,
    string PerformanceClaimBasis,
    bool PerformanceClaimAllowed,
    IReadOnlyList<string> PerformanceClaimBlockers,
    int CpuImprovedPairCount,
    int CpuWithinBudgetPairCount,
    double CpuComparisonOverheadBudgetMicroseconds,
    double CpuComparisonOverheadBudgetPercent,
    int GpuValidPairCount,
    PairedMetricSummary CpuWorkload,
    PairedMetricSummary? GpuWorkload,
    IReadOnlyList<UpdateUploadElisionTrialReport> Trials);
