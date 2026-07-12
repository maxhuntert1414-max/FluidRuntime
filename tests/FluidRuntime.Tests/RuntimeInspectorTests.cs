using FluidRuntime.Contracts;
using FluidRuntime.Runtime;
using FluidRuntime.Telemetry;

namespace FluidRuntime.Tests;

public sealed class RuntimeInspectorTests
{
    [Fact]
    public async Task InspectAsync_aggregates_samples_and_builds_advisory_report()
    {
        var samples = new TelemetrySnapshot[]
        {
            new(DateTimeOffset.UtcNow, 42, "TestGame", 20, 500, 400, 12, 40, 9000),
            new(DateTimeOffset.UtcNow, 42, "TestGame", 40, 550, 430, 14, 50, 8000)
        };
        var inspector = new RuntimeInspector(
            new FakeSampler(samples),
            new RuntimeDecisionEngine());
        var ledger = new FluidGatewayLedger
        {
            Mode = "presentmon-operational-ledger-v0.61",
            DryRun = true,
            WouldModifySystem = false,
            Application = "TestGame.exe",
            WastePressureScore = 35,
            NativePromotionAllowed = false
        };

        var report = await inspector.InspectAsync(
            ledger,
            "ledger.json",
            processId: 42,
            sampleCount: 2,
            interval: TimeSpan.FromMilliseconds(1));

        Assert.Equal(30, report.Telemetry.AverageCpuPercent);
        Assert.Equal(550, report.Telemetry.MaximumWorkingSetMb);
        Assert.Equal(2, report.Samples.Count);
        Assert.True(report.DecisionPlan.DryRun);
        Assert.False(report.DecisionPlan.WouldModifySystem);
    }

    private sealed class FakeSampler(IReadOnlyList<TelemetrySnapshot> samples)
        : IProcessTelemetrySampler
    {
        public Task<IReadOnlyList<TelemetrySnapshot>> SampleAsync(
            int processId,
            int sampleCount,
            TimeSpan interval,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(samples);
    }
}
