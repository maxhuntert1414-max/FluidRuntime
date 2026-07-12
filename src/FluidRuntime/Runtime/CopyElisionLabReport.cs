namespace FluidRuntime.Runtime;

public sealed record CopyElisionLabReport(
    string Mode,
    bool TargetOwned,
    bool CooperativeLoad,
    bool RemoteInjection,
    bool ContentEquivalent,
    bool RollbackRestoredInBothRuns,
    long ObservedCopyCount,
    long AvoidedCopyCount,
    ulong AvoidedCopyBytes,
    double BaselineWorkloadMicroseconds,
    double OptimizedWorkloadMicroseconds,
    double WorkloadDeltaPercent,
    HookLabReport Baseline,
    HookLabReport Optimized);
