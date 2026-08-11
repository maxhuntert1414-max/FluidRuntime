using System.Text.Json;
using FluidRuntime.Cli;
using FluidRuntime.Native;
using FluidRuntime.Runtime;

namespace FluidRuntime.Tests;

public sealed class UpdateUploadElisionLabTests
{
    [Fact]
    public void Options_use_the_fixed_exact_content_contract()
    {
        var options = UpdateUploadElisionLabOptions.Parse(
        [
            "update-upload-elision-lab",
            "--target", "target.exe",
            "--hook", "hook.dll",
            "--out", "report.json"
        ]);

        Assert.Equal(4 * 1024 * 1024, UpdateUploadElisionLabOptions.BufferBytes);
        Assert.Equal(3, UpdateUploadElisionLabOptions.RequiredUpdateCount);
        Assert.Equal(128, options.CandidateActionCount);
        Assert.Equal(131, options.TotalUpdateCount);
        Assert.Equal(10, options.TrialPairs);
        Assert.Equal(1, options.WarmupPairs);
        Assert.False(options.UseHardware);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(127)]
    [InlineData(128)]
    public void Options_accept_the_bounded_candidate_profiles(int candidateActionCount)
    {
        var options = UpdateUploadElisionLabOptions.Parse(
        [
            "update-upload-elision-lab",
            "--target", "target.exe",
            "--hook", "hook.dll",
            "--out", "report.json",
            "--candidate-action-count", candidateActionCount.ToString()
        ]);

        Assert.Equal(candidateActionCount, options.CandidateActionCount);
        Assert.Equal(candidateActionCount + 3, options.TotalUpdateCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(129)]
    public void Options_reject_candidate_profiles_outside_the_native_bound(
        int candidateActionCount)
    {
        Assert.Throws<ArgumentException>(() => UpdateUploadElisionLabOptions.Parse(
        [
            "update-upload-elision-lab",
            "--target", "target.exe",
            "--hook", "hook.dll",
            "--out", "report.json",
            "--candidate-action-count", candidateActionCount.ToString()
        ]));
    }

    [Fact]
    public void Options_reject_unknown_or_unpaired_values()
    {
        Assert.Throws<ArgumentException>(() => UpdateUploadElisionLabOptions.Parse(
        [
            "update-upload-elision-lab",
            "--target", "target.exe",
            "--hook", "hook.dll",
            "--out", "report.json",
            "--update-count", "64"
        ]));
        Assert.Throws<ArgumentException>(() => UpdateUploadElisionLabOptions.Parse(
        [
            "update-upload-elision-lab",
            "--target", "target.exe",
            "--hook", "hook.dll",
            "--out"
        ]));
    }

    [Fact]
    public void Event_flags_are_direction_and_operation_specific()
    {
        var item = new HookIpcEvent(
            0, 1, HookEventType.UpdateSubresource, 2, 3, 0,
            UpdateUploadElisionLabOptions.BufferBytes, 1,
            Flags: 1 | 2 | 32 | 64,
            RegionKey: 42);

        Assert.True(item.IsUploadTransfer);
        Assert.True(item.IsContentCompared);
        Assert.True(item.IsRedundantUpdateSubresourceCandidate);
        Assert.True(item.WasUpdateSubresourceSkipped);
        Assert.False(item.IsRedundantCopyCandidate);
        Assert.False(item.WasCopySkipped);
    }

    [Fact]
    public void Report_allows_gpu_improvement_with_bounded_comparison_overhead()
    {
        var options = Options(useHardware: true);
        var trials = Trials(
            baselineCpu: 1000,
            optimizedCpu: 1050,
            baselineGpu: 1000,
            optimizedGpu: 100,
            driver: "hardware");

        var report = UpdateUploadElisionLabRunner.BuildReport(trials, options);

        Assert.True(report.PerformanceClaimAllowed);
        Assert.Empty(report.PerformanceClaimBlockers);
        Assert.Equal(268_435_456UL, report.AvoidedUpdateBytesPerOptimizedRun);
        Assert.True(report.MutationGuardPassed);
        Assert.True(report.GenerationGuardPassed);
        Assert.Equal(10, report.CpuWithinBudgetPairCount);
        Assert.Equal(10, report.GpuWorkload!.OptimizedLowerCount);
        Assert.Equal(
            "gpu-interval-improvement-with-bounded-cpu-content-comparison-overhead",
            report.PerformanceClaimBasis);
        Assert.Equal(
            "owned-d3d11-default-buffer-full-update-subresource-exact-content-workload-only",
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

        var report = UpdateUploadElisionLabRunner.BuildReport(trials, options);

        Assert.False(report.PerformanceClaimAllowed);
        Assert.Contains(
            "cpu-content-comparison-overhead-budget-exceeded",
            report.PerformanceClaimBlockers);
        Assert.Contains("software-adapter-not-hardware", report.PerformanceClaimBlockers);
    }

    [Fact]
    public void Report_supports_the_128_candidate_profile_with_the_same_cache_bound()
    {
        const int candidateActionCount = 128;
        var options = Options(
            useHardware: false,
            candidateActionCount: candidateActionCount);
        var trials = Trials(
            baselineCpu: 1000,
            optimizedCpu: 900,
            baselineGpu: 1000,
            optimizedGpu: 100,
            driver: "warp",
            candidateActionCount);

        var report = UpdateUploadElisionLabRunner.BuildReport(trials, options);

        Assert.Equal(candidateActionCount, report.RedundantUpdateCountPerOptimizedRun);
        Assert.Equal(536_870_912UL, report.AvoidedUpdateBytesPerOptimizedRun);
        Assert.Equal(1, report.ExactContentCacheResourceLimit);
        Assert.Equal(4UL * 1024 * 1024, report.ExactContentCacheByteLimit);
    }

    private static UpdateUploadElisionLabOptions Options(
        bool useHardware,
        int candidateActionCount = 64) => new(
        "target.exe",
        "hook.dll",
        "report.json",
        TrialPairs: 10,
        WarmupPairs: 0,
        HoldMs: 50,
        GpuTimeoutMs: 5000,
        CandidateActionCount: candidateActionCount,
        UseHardware: useHardware);

    private static IReadOnlyList<UpdateUploadElisionTrialReport> Trials(
        double baselineCpu,
        double optimizedCpu,
        double baselineGpu,
        double optimizedGpu,
        string driver,
        int candidateActionCount = 64) =>
        Enumerable.Range(0, 10).Select(index =>
        {
            var baseline = Run(
                false,
                baselineCpu,
                baselineGpu,
                driver,
                candidateActionCount);
            var optimized = Run(
                true,
                optimizedCpu,
                optimizedGpu,
                driver,
                candidateActionCount);
            return new UpdateUploadElisionTrialReport(
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

    private static UpdateUploadElisionRunReport Run(
        bool optimized,
        double cpu,
        double gpu,
        string driver,
        int candidateActionCount)
    {
        using var document = JsonDocument.Parse("{}");
        var directUpdateCount = candidateActionCount + 3;
        var directUpdateBytes = checked(
            (ulong)directUpdateCount * UpdateUploadElisionLabOptions.BufferBytes);
        var candidateBytes = checked(
            (ulong)candidateActionCount * UpdateUploadElisionLabOptions.BufferBytes);
        var forwardedCount = optimized ? 6 : candidateActionCount + 6;
        var forwardedBytes = checked(
            9_216UL +
            (ulong)(optimized ? 3 : directUpdateCount) *
                UpdateUploadElisionLabOptions.BufferBytes);
        return new UpdateUploadElisionRunReport(
            Optimized: optimized,
            ProcessId: 42,
            RingAbiVersion: HookRingReader.ExpectedAbiVersion,
            RingCapacity: HookRingReader.ExpectedCapacity,
            RenderDriver: driver,
            AdapterDescription: "Test GPU",
            AdapterVendorId: 0x1002,
            AdapterDeviceId: 0x6FDF,
            AdapterLuid: "0000000000000001",
            EventCount: 330,
            LostSequenceCount: 0,
            NativeOverrunCount: 0,
            DirectUploadUpdateCount: directUpdateCount,
            DirectUploadBytes: directUpdateBytes,
            RedundantUpdateCandidateCount: candidateActionCount,
            RedundantUpdateCandidateBytes: candidateBytes,
            ForwardedUpdateSubresourceCount: forwardedCount,
            ForwardedUpdateSubresourceBytes: forwardedBytes,
            SkippedUpdateSubresourceCount: optimized ? candidateActionCount : 0,
            SkippedUpdateSubresourceBytes: optimized ? candidateBytes : 0,
            ContentCacheResourceCount: 1,
            ContentCacheBytes: 4_194_304,
            PublishedPolicyEpoch: optimized ? 1 : 0,
            AcknowledgedPolicyEpoch: optimized ? 1 : 0,
            AppliedPolicyActions: optimized ? candidateActionCount : 0,
            PolicyStatus: optimized ? "exhausted" : "none",
            MutationApplied: true,
            GenerationGuardApplied: true,
            ContentEquivalent: true,
            RollbackRestored: true,
            InitialHash: "0123456789abcdef",
            FinalHash: "fedcba9876543210",
            GuardHash: "aaaaaaaaaaaaaaaa",
            PostDetachDestinationHash: "fedcba9876543210",
            CpuWorkloadMicroseconds: cpu,
            GpuWorkloadMicroseconds: gpu,
            TargetReport: document.RootElement.Clone());
    }
}
