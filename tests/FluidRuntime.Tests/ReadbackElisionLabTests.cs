using System.Text.Json;
using FluidRuntime.Cli;
using FluidRuntime.Runtime;

namespace FluidRuntime.Tests;

public sealed class ReadbackElisionLabTests
{
    [Fact]
    public void Options_use_the_fixed_bounded_readback_contract()
    {
        var options = ReadbackElisionLabOptions.Parse(
        [
            "readback-elision-lab",
            "--target", "target.exe",
            "--hook", "hook.dll",
            "--out", "report.json"
        ]);

        Assert.Equal(64, ReadbackElisionLabOptions.RedundantCopyCount);
        Assert.Equal(4 * 1024 * 1024, ReadbackElisionLabOptions.ReadbackBufferBytes);
        Assert.Equal(10, options.TrialPairs);
        Assert.Equal(1, options.WarmupPairs);
        Assert.Equal(50, options.HoldMs);
        Assert.Equal(5000, options.GpuTimeoutMs);
        Assert.False(options.UseHardware);
    }

    [Fact]
    public void Options_reject_unknown_or_unpaired_values()
    {
        Assert.Throws<ArgumentException>(() => ReadbackElisionLabOptions.Parse(
        [
            "readback-elision-lab",
            "--target", "target.exe",
            "--hook", "hook.dll",
            "--out", "report.json",
            "--copy-count", "65"
        ]));
        Assert.Throws<ArgumentException>(() => ReadbackElisionLabOptions.Parse(
        [
            "readback-elision-lab",
            "--target", "target.exe",
            "--hook", "hook.dll",
            "--out"
        ]));
    }

    [Fact]
    public void Report_requires_consistent_cpu_and_gpu_hardware_improvement()
    {
        var options = Options(useHardware: true);
        var trials = Trials(
            baselineCpu: 1000,
            optimizedCpu: 100,
            baselineGpu: 1000,
            optimizedGpu: 100,
            driver: "hardware");

        var report = ReadbackElisionLabRunner.BuildReport(trials, options);

        Assert.True(report.PerformanceClaimAllowed);
        Assert.Empty(report.PerformanceClaimBlockers);
        Assert.Equal(268_435_456UL, report.AvoidedReadbackBytesPerOptimizedRun);
        Assert.Equal(10, report.CpuImprovedPairCount);
        Assert.Equal(10, report.GpuWorkload!.OptimizedLowerCount);
        Assert.Equal(
            "owned-d3d11-default-to-staging-readback-workload-only",
            report.ClaimScope);
    }

    [Fact]
    public void Report_blocks_cpu_regression_and_software_measurement()
    {
        var options = Options(useHardware: false);
        var trials = Trials(
            baselineCpu: 100,
            optimizedCpu: 110,
            baselineGpu: 1000,
            optimizedGpu: 100,
            driver: "warp");

        var report = ReadbackElisionLabRunner.BuildReport(trials, options);

        Assert.False(report.PerformanceClaimAllowed);
        Assert.Contains("cpu-improvement-not-consistent", report.PerformanceClaimBlockers);
        Assert.Contains("software-adapter-not-hardware", report.PerformanceClaimBlockers);
    }

    private static ReadbackElisionLabOptions Options(bool useHardware) => new(
        "target.exe",
        "hook.dll",
        "report.json",
        TrialPairs: 10,
        WarmupPairs: 0,
        HoldMs: 50,
        GpuTimeoutMs: 5000,
        UseHardware: useHardware);

    private static IReadOnlyList<ReadbackElisionTrialReport> Trials(
        double baselineCpu,
        double optimizedCpu,
        double baselineGpu,
        double optimizedGpu,
        string driver) =>
        Enumerable.Range(0, 10).Select(index =>
        {
            var baseline = Run(false, baselineCpu, baselineGpu, driver);
            var optimized = Run(true, optimizedCpu, optimizedGpu, driver);
            return new ReadbackElisionTrialReport(
                index,
                "measured",
                true,
                index % 2 == 0
                    ? "baseline-then-optimized"
                    : "optimized-then-baseline",
                ContentEquivalent: true,
                RollbackRestoredInBothRuns: true,
                AdapterIdentityMatched: true,
                baselineCpu,
                optimizedCpu,
                baselineGpu,
                optimizedGpu,
                baseline,
                optimized);
        }).ToArray();

    private static ReadbackElisionRunReport Run(
        bool optimized,
        double cpu,
        double gpu,
        string driver)
    {
        using var document = JsonDocument.Parse("{}");
        return new ReadbackElisionRunReport(
            Optimized: optimized,
            ProcessId: 42,
            RingAbiVersion: 9,
            RingCapacity: 2048,
            RenderDriver: driver,
            AdapterDescription: "Test GPU",
            AdapterVendorId: 0x1002,
            AdapterDeviceId: 0x6FDF,
            AdapterLuid: "0000000000000001",
            EventCount: 1043,
            LostSequenceCount: 0,
            NativeOverrunCount: 0,
            ReadbackCopyCount: 65,
            ReadbackCopyBytes: 272_629_760,
            ReadMapCount: 65,
            ReadMapBytes: 272_629_760,
            ForwardedCopyCount: optimized ? 7 : 71,
            ForwardedCopyBytes: optimized ? 4_243_456UL : 272_678_912UL,
            SkippedReadbackCopyCount: optimized ? 64 : 0,
            SkippedReadbackCopyBytes: optimized ? 268_435_456UL : 0,
            PublishedPolicyEpoch: optimized ? 1 : 0,
            AcknowledgedPolicyEpoch: optimized ? 1 : 0,
            AppliedPolicyActions: optimized ? 64 : 0,
            PolicyStatus: optimized ? "exhausted" : "none",
            ContentEquivalent: true,
            RollbackRestored: true,
            ExpectedHash: "0123456789abcdef",
            FirstMapHash: "0123456789abcdef",
            FinalMapHash: "0123456789abcdef",
            PostDetachSourceHash: "0123456789abcdef",
            PostDetachDestinationHash: "0123456789abcdef",
            CpuWorkloadMicroseconds: cpu,
            GpuWorkloadMicroseconds: gpu,
            TargetReport: document.RootElement.Clone());
    }
}
