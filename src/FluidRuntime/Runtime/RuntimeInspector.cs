using FluidRuntime.Contracts;
using FluidRuntime.Telemetry;

namespace FluidRuntime.Runtime;

public sealed class RuntimeInspector(
    IProcessTelemetrySampler sampler,
    RuntimeDecisionEngine decisionEngine)
{
    public async Task<RuntimeInspectionReport> InspectAsync(
        FluidGatewayLedger ledger,
        string sourceLedger,
        int processId,
        int sampleCount,
        TimeSpan interval,
        CancellationToken cancellationToken = default)
    {
        var samples = await sampler.SampleAsync(
            processId,
            sampleCount,
            interval,
            cancellationToken);
        var telemetry = TelemetrySummary.From(samples);
        var decisionPlan = decisionEngine.Build(ledger, telemetry);

        return new RuntimeInspectionReport(
            "fluidruntime-inspection-v0.1",
            DateTimeOffset.UtcNow,
            Path.GetFullPath(sourceLedger),
            ledger.Application,
            telemetry,
            samples,
            decisionPlan,
            "Inferred diagnostic and advisory plan; not proof of internal cause and not an executed optimization.");
    }
}
