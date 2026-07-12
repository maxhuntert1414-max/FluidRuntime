namespace FluidRuntime.Runtime;

public sealed record MetricDistribution(
    int Count,
    double Minimum,
    double P50,
    double P95,
    double Maximum,
    double Mean);

public sealed record PairedMetricSummary(
    MetricDistribution Baseline,
    MetricDistribution Optimized,
    MetricDistribution Delta,
    MetricDistribution DeltaPercent,
    int OptimizedLowerCount,
    int BaselineLowerCount,
    int TieCount);

public sealed record CopyElisionTrialReport(
    int PairIndex,
    string Phase,
    bool IncludedInStatistics,
    string ExecutionOrder,
    bool ContentEquivalent,
    bool RollbackRestoredInBothRuns,
    bool AdapterIdentityMatched,
    long ObservedCopyCount,
    long AvoidedCopyCount,
    ulong AvoidedCopyBytes,
    double BaselineCpuMicroseconds,
    double OptimizedCpuMicroseconds,
    double CpuDeltaMicroseconds,
    double CpuDeltaPercent,
    double? BaselineGpuMicroseconds,
    double? OptimizedGpuMicroseconds,
    double? GpuDeltaMicroseconds,
    double? GpuDeltaPercent,
    HookLabReport Baseline,
    HookLabReport Optimized);

public sealed record CopyElisionLabReport(
    string Mode,
    bool TargetOwned,
    bool CooperativeLoad,
    bool RemoteInjection,
    bool ContentEquivalent,
    bool RollbackRestoredInAllRuns,
    int TrialPairsRequested,
    int WarmupPairs,
    int IncludedTrialPairs,
    string OrderingPolicy,
    string AdapterDescription,
    uint AdapterVendorId,
    uint AdapterDeviceId,
    string AdapterLuid,
    long ObservedCopyCountPerRun,
    long AvoidedCopyCountPerOptimizedRun,
    ulong AvoidedCopyBytesPerOptimizedRun,
    string ClaimScope,
    bool PerformanceClaimAllowed,
    IReadOnlyList<string> PerformanceClaimBlockers,
    int GpuValidPairCount,
    PairedMetricSummary CpuWorkload,
    PairedMetricSummary? GpuWorkload,
    IReadOnlyList<CopyElisionTrialReport> Trials);
