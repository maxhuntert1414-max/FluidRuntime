using FluidRuntime.Native;
using FluidRuntime.Telemetry;

namespace FluidRuntime.Runtime;

public sealed record RuntimeInspectionReport(
    string Mode,
    DateTimeOffset GeneratedAtUtc,
    string SourceLedger,
    string Application,
    bool LedgerTargetMatched,
    TelemetrySummary Telemetry,
    IReadOnlyList<TelemetrySnapshot> Samples,
    NativeProbeReport? NativeProbe,
    RuntimeDecisionPlan DecisionPlan,
    string Disclaimer)
{
    public IReadOnlyList<NativeProbeReport> NativeProbeSamples { get; init; } = [];
}
