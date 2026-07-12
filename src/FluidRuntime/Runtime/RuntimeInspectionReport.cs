using FluidRuntime.Telemetry;

namespace FluidRuntime.Runtime;

public sealed record RuntimeInspectionReport(
    string Mode,
    DateTimeOffset GeneratedAtUtc,
    string SourceLedger,
    string Application,
    TelemetrySummary Telemetry,
    IReadOnlyList<TelemetrySnapshot> Samples,
    RuntimeDecisionPlan DecisionPlan,
    string Disclaimer);
