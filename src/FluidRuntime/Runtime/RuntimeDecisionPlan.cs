namespace FluidRuntime.Runtime;

public sealed record RuntimeDecisionPlan(
    string Mode,
    bool DryRun,
    bool WouldModifySystem,
    string ExecutionGuard,
    string Policy,
    double CombinedPressureScore,
    bool NativePromotionAllowed,
    IReadOnlyList<RuntimeActionCandidate> Actions);
