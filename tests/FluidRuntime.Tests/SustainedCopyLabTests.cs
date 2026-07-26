using System.Text.Json;
using FluidRuntime.Cli;
using FluidRuntime.Runtime;

namespace FluidRuntime.Tests;

public sealed class SustainedCopyLabTests
{
    [Fact]
    public void Options_use_bounded_measurement_defaults()
    {
        var options = SustainedCopyLabOptions.Parse(
        [
            "sustained-copy-lab",
            "--target", "target.exe",
            "--hook", "hook.dll",
            "--out", "report.json"
        ]);

        Assert.Equal(128, options.CopyCount);
        Assert.Equal(10, options.TrialPairs);
        Assert.Equal(1, options.WarmupPairs);
        Assert.Equal(50, options.HoldMs);
        Assert.Equal(5000, options.GpuTimeoutMs);
        Assert.False(options.UseHardware);
    }

    [Fact]
    public void Options_reject_unbounded_copy_counts()
    {
        Assert.Throws<ArgumentException>(() => SustainedCopyLabOptions.Parse(
        [
            "sustained-copy-lab",
            "--target", "target.exe",
            "--hook", "hook.dll",
            "--copy-count", "129",
            "--out", "report.json"
        ]));
    }

    [Fact]
    public void Report_allows_only_consistent_hardware_improvement()
    {
        var options = new SustainedCopyLabOptions(
            "target.exe",
            "hook.dll",
            "report.json",
            CopyCount: 128,
            TrialPairs: 10,
            WarmupPairs: 0,
            HoldMs: 50,
            GpuTimeoutMs: 5000,
            UseHardware: true);
        var trials = Enumerable.Range(0, 10).Select(index => new SustainedCopyTrialReport(
            PairIndex: index,
            Phase: "measured",
            IncludedInStatistics: true,
            ExecutionOrder: index % 2 == 0
                ? "baseline-then-optimized"
                : "optimized-then-baseline",
            ContentEquivalent: true,
            RollbackRestoredInBothRuns: true,
            AdapterIdentityMatched: true,
            BaselineCpuMicroseconds: 100,
            OptimizedCpuMicroseconds: 110,
            BaselineGpuMicroseconds: 1000,
            OptimizedGpuMicroseconds: 100,
            Baseline: Run(optimized: false, cpu: 100, gpu: 1000),
            Optimized: Run(optimized: true, cpu: 110, gpu: 100))).ToArray();

        var report = SustainedCopyLabRunner.BuildReport(trials, options);

        Assert.True(report.PerformanceClaimAllowed);
        Assert.Empty(report.PerformanceClaimBlockers);
        Assert.True(report.CpuRegressionObserved);
        Assert.Equal(536_870_912UL, report.AvoidedCopyBytesPerOptimizedRun);
        Assert.Equal(10, report.GpuWorkload!.OptimizedLowerCount);
    }

    [Fact]
    public void Report_blocks_software_adapter_claims()
    {
        var options = new SustainedCopyLabOptions(
            "target.exe",
            "hook.dll",
            "report.json",
            128,
            10,
            0,
            50,
            5000,
            UseHardware: false);
        var trials = Enumerable.Range(0, 10).Select(index =>
        {
            var baseline = Run(false, 100, 1000) with { RenderDriver = "warp" };
            var optimized = Run(true, 90, 100) with { RenderDriver = "warp" };
            return new SustainedCopyTrialReport(
                index,
                "measured",
                true,
                index % 2 == 0
                    ? "baseline-then-optimized"
                    : "optimized-then-baseline",
                true,
                true,
                true,
                100,
                90,
                1000,
                100,
                baseline,
                optimized);
        }).ToArray();

        var report = SustainedCopyLabRunner.BuildReport(trials, options);

        Assert.False(report.PerformanceClaimAllowed);
        Assert.Contains("software-adapter-not-hardware", report.PerformanceClaimBlockers);
    }

    private static SustainedCopyRunReport Run(bool optimized, double cpu, double gpu)
    {
        using var document = JsonDocument.Parse("{}");
        return new SustainedCopyRunReport(
            Optimized: optimized,
            ProcessId: 42,
            RenderDriver: "hardware",
            AdapterDescription: "Test GPU",
            AdapterVendorId: 0x1002,
            AdapterDeviceId: 0x6FDF,
            AdapterLuid: "0000000000000001",
            EventCount: 391,
            LostSequenceCount: 0,
            NativeOverrunCount: 0,
            ObservedCopyCount: 135,
            ObservedCopyBytes: 541_114_368,
            RedundantCopyCount: 131,
            RedundantCopyBytes: 536_895_488,
            ForwardedCopyCount: optimized ? 7 : 135,
            ForwardedCopyBytes: optimized ? 4_243_456UL : 541_114_368UL,
            SkippedCopyCount: optimized ? 128 : 0,
            SkippedCopyBytes: optimized ? 536_870_912UL : 0,
            PublishedPolicyEpoch: optimized ? 1 : 0,
            AcknowledgedPolicyEpoch: optimized ? 1 : 0,
            AppliedPolicyActions: optimized ? 128 : 0,
            PolicyStatus: optimized ? "exhausted" : "none",
            ContentEquivalent: true,
            RollbackRestored: true,
            SustainedSourceHash: "0123456789abcdef",
            SustainedDestinationHash: "0123456789abcdef",
            CpuWorkloadMicroseconds: cpu,
            GpuWorkloadMicroseconds: gpu,
            TargetReport: document.RootElement.Clone());
    }
}
