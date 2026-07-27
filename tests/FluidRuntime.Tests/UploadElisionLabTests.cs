using System.Text.Json;
using FluidRuntime.Cli;
using FluidRuntime.Runtime;

namespace FluidRuntime.Tests;

public sealed class UploadElisionLabTests
{
    [Fact]
    public void Options_use_the_fixed_bounded_upload_contract()
    {
        var options = UploadElisionLabOptions.Parse(
        [
            "upload-elision-lab",
            "--target", "target.exe",
            "--hook", "hook.dll",
            "--out", "report.json"
        ]);

        Assert.Equal(64, UploadElisionLabOptions.RedundantCopyCount);
        Assert.Equal(4 * 1024 * 1024, UploadElisionLabOptions.UploadBufferBytes);
        Assert.Equal(10, options.TrialPairs);
        Assert.Equal(1, options.WarmupPairs);
        Assert.Equal(50, options.HoldMs);
        Assert.Equal(5000, options.GpuTimeoutMs);
        Assert.False(options.UseHardware);
    }

    [Fact]
    public void Options_reject_unknown_or_unpaired_values()
    {
        Assert.Throws<ArgumentException>(() => UploadElisionLabOptions.Parse(
        [
            "upload-elision-lab",
            "--target", "target.exe",
            "--hook", "hook.dll",
            "--out", "report.json",
            "--copy-count", "65"
        ]));
        Assert.Throws<ArgumentException>(() => UploadElisionLabOptions.Parse(
        [
            "upload-elision-lab",
            "--target", "target.exe",
            "--hook", "hook.dll",
            "--out"
        ]));
    }

    [Fact]
    public void Report_allows_gpu_improvement_with_bounded_cpu_submission_overhead()
    {
        var options = Options(useHardware: true);
        var trials = Trials(
            baselineCpu: 1000,
            optimizedCpu: 1050,
            baselineGpu: 1000,
            optimizedGpu: 100,
            driver: "hardware");

        var report = UploadElisionLabRunner.BuildReport(trials, options);

        Assert.True(report.PerformanceClaimAllowed);
        Assert.Empty(report.PerformanceClaimBlockers);
        Assert.Equal(268_435_456UL, report.AvoidedUploadBytesPerOptimizedRun);
        Assert.Equal(0, report.CpuImprovedPairCount);
        Assert.Equal(10, report.GpuWorkload!.OptimizedLowerCount);
        Assert.Equal(10, report.CpuWithinBudgetPairCount);
        Assert.Equal(
            "gpu-interval-improvement-with-bounded-cpu-submission-overhead",
            report.PerformanceClaimBasis);
        Assert.Equal(
            "owned-d3d11-writable-staging-to-default-upload-copy-workload-only",
            report.ClaimScope);
    }

    [Fact]
    public void Report_blocks_excessive_cpu_overhead_and_software_measurement()
    {
        var options = Options(useHardware: false);
        var trials = Trials(
            baselineCpu: 100,
            optimizedCpu: 120,
            baselineGpu: 1000,
            optimizedGpu: 100,
            driver: "warp");

        var report = UploadElisionLabRunner.BuildReport(trials, options);

        Assert.False(report.PerformanceClaimAllowed);
        Assert.Contains(
            "cpu-submission-overhead-budget-exceeded",
            report.PerformanceClaimBlockers);
        Assert.Contains("software-adapter-not-hardware", report.PerformanceClaimBlockers);
    }

    private static UploadElisionLabOptions Options(bool useHardware) => new(
        "target.exe",
        "hook.dll",
        "report.json",
        TrialPairs: 10,
        WarmupPairs: 0,
        HoldMs: 50,
        GpuTimeoutMs: 5000,
        UseHardware: useHardware);

    private static IReadOnlyList<UploadElisionTrialReport> Trials(
        double baselineCpu,
        double optimizedCpu,
        double baselineGpu,
        double optimizedGpu,
        string driver) =>
        Enumerable.Range(0, 10).Select(index =>
        {
            var baseline = Run(false, baselineCpu, baselineGpu, driver);
            var optimized = Run(true, optimizedCpu, optimizedGpu, driver);
            return new UploadElisionTrialReport(
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

    private static UploadElisionRunReport Run(
        bool optimized,
        double cpu,
        double gpu,
        string driver)
    {
        using var document = JsonDocument.Parse("{}");
        return new UploadElisionRunReport(
            Optimized: optimized,
            ProcessId: 42,
            RingAbiVersion: 8,
            RingCapacity: 2048,
            RenderDriver: driver,
            AdapterDescription: "Test GPU",
            AdapterVendorId: 0x1002,
            AdapterDeviceId: 0x6FDF,
            AdapterLuid: "0000000000000001",
            EventCount: 329,
            LostSequenceCount: 0,
            NativeOverrunCount: 0,
            UploadCopyCount: 65,
            UploadCopyBytes: 272_629_760,
            UploadWriteMapCount: 1,
            UploadWriteMapBytes: 4_194_304,
            ForwardedCopyCount: optimized ? 7 : 71,
            ForwardedCopyBytes: optimized ? 4_243_456UL : 272_678_912UL,
            SkippedUploadCopyCount: optimized ? 64 : 0,
            SkippedUploadCopyBytes: optimized ? 268_435_456UL : 0,
            PublishedPolicyEpoch: optimized ? 1 : 0,
            AcknowledgedPolicyEpoch: optimized ? 1 : 0,
            AppliedPolicyActions: optimized ? 64 : 0,
            PolicyStatus: optimized ? "exhausted" : "none",
            ContentEquivalent: true,
            RollbackRestored: true,
            ExpectedHash: "0123456789abcdef",
            PostDetachSourceHash: "0123456789abcdef",
            PostDetachDestinationHash: "0123456789abcdef",
            CpuWorkloadMicroseconds: cpu,
            GpuWorkloadMicroseconds: gpu,
            TargetReport: document.RootElement.Clone());
    }
}
