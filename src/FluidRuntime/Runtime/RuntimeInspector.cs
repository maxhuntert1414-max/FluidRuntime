using FluidRuntime.Contracts;
using FluidRuntime.Native;
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
        NativeProbeReport? nativeProbe = null,
        bool allowLedgerTargetMismatch = false,
        CancellationToken cancellationToken = default,
        Task<IReadOnlyList<NativeProbeReport>>? nativeProbeSamplesTask = null)
    {
        var samplesTask = sampler.SampleAsync(
            processId,
            sampleCount,
            interval,
            cancellationToken);
        if (nativeProbeSamplesTask is not null)
        {
            await Task.WhenAll(samplesTask, nativeProbeSamplesTask);
        }
        var samples = await samplesTask;
        var nativeProbeSamples = nativeProbeSamplesTask is not null
            ? await nativeProbeSamplesTask
            : nativeProbe is null
                ? []
                : [nativeProbe];
        var decisionProbe = nativeProbeSamples.LastOrDefault() ?? nativeProbe;
        var telemetry = TelemetrySummary.From(samples);
        var targetMatched = TargetMatches(ledger.Application, telemetry.ProcessName);
        if (!targetMatched && !allowLedgerTargetMismatch)
        {
            throw new InvalidDataException(
                $"Ledger application '{ledger.Application}' does not match " +
                $"observed process '{telemetry.ProcessName}'.");
        }

        var decisionPlan = targetMatched
            ? decisionEngine.Build(ledger, telemetry, decisionProbe)
            : BuildTargetMismatchPlan(ledger, telemetry);

        return new RuntimeInspectionReport(
            "fluidruntime-inspection-v0.2",
            DateTimeOffset.UtcNow,
            Path.GetFullPath(sourceLedger),
            ledger.Application,
            targetMatched,
            telemetry,
            samples,
            decisionProbe,
            decisionPlan,
            "Inferred diagnostic and advisory plan; not proof of internal cause and not an executed optimization.")
        {
            NativeProbeSamples = nativeProbeSamples
        };
    }

    private static bool TargetMatches(string application, string processName)
    {
        var expectedName = Path.GetFileNameWithoutExtension(application?.Trim());
        return !string.IsNullOrWhiteSpace(expectedName) &&
            string.Equals(expectedName, processName, StringComparison.OrdinalIgnoreCase);
    }

    private static RuntimeDecisionPlan BuildTargetMismatchPlan(
        FluidGatewayLedger ledger,
        TelemetrySummary telemetry) =>
        new(
            "fluidruntime-decision-plan-v0.2",
            DryRun: true,
            WouldModifySystem: false,
            ExecutionGuard: "advisory-only",
            Policy: "hold-ledger-target-mismatch",
            CombinedPressureScore: Math.Round(Math.Max(
                telemetry.AverageCpuPercent,
                telemetry.MaximumHostMemoryPressurePercent), 2),
            NativePromotionAllowed: false,
            Actions:
            [
                new RuntimeActionCandidate(
                    "capture-matching-trace",
                    "trace-identity",
                    $"Ledger '{ledger.Application}' cannot guide process '{telemetry.ProcessName}'.",
                    "blocked-target-mismatch",
                    RequiresNativeBackend: false,
                    RequiresPrivilege: false,
                    Blocked: true,
                    Evidence: new Dictionary<string, double>
                    {
                        ["sample_count"] = telemetry.SampleCount
                    })
            ]);
}
