using System.Text.Json;
using FluidRuntime.Cli;
using FluidRuntime.Runtime;

namespace FluidRuntime.Tests;

public sealed class CopyElisionLabCommandTests
{
    [Fact]
    public void BuildTrial_requires_matching_content_and_one_skipped_copy()
    {
        var baseline = CreateRun(copyElisionEnabled: false, forwardedCopies: 6, skippedCopies: 0);
        var optimized = CreateRun(copyElisionEnabled: true, forwardedCopies: 5, skippedCopies: 1);

        var trial = CopyElisionLabCommand.BuildTrial(baseline, optimized);

        Assert.True(trial.ContentEquivalent);
        Assert.Equal(1, trial.AvoidedCopyCount);
        Assert.Equal(4096UL, trial.AvoidedCopyBytes);

        var mismatched = optimized with { DestinationBufferHash = "different" };
        Assert.Throws<InvalidDataException>(() =>
            CopyElisionLabCommand.BuildTrial(baseline, mismatched));

        var rollbackFailed = optimized with { RollbackRestored = false };
        Assert.Throws<InvalidDataException>(() =>
            CopyElisionLabCommand.BuildTrial(baseline, rollbackFailed));

        var provenanceFailed = optimized with { ProvenanceFailureCount = 1 };
        Assert.Throws<InvalidDataException>(() =>
            CopyElisionLabCommand.BuildTrial(baseline, provenanceFailed));

        var gpuWriteDrifted = optimized with { GpuViewWriteBytes = 0 };
        Assert.Throws<InvalidDataException>(() =>
            CopyElisionLabCommand.BuildTrial(baseline, gpuWriteDrifted));
    }

    [Fact]
    public void BuildReport_excludes_warmup_and_gates_small_samples()
    {
        var baseline = CreateRun(false, 6, 0);
        var optimized = CreateRun(true, 5, 1);
        var warmup = CopyElisionLabCommand.BuildTrial(
            baseline,
            optimized,
            pairIndex: 0,
            phase: "warmup",
            includedInStatistics: false);
        var measured = CopyElisionLabCommand.BuildTrial(baseline, optimized);

        var report = CopyElisionLabCommand.BuildReport([warmup, measured], 1, 1);

        Assert.Equal("fluidruntime-copy-elision-trace-v0.7.3", report.Mode);
        Assert.Equal(1, report.CpuWorkload.Baseline.Count);
        Assert.Equal(1, report.GpuValidPairCount);
        Assert.False(report.PerformanceClaimAllowed);
        Assert.Contains("insufficient-trial-pairs", report.PerformanceClaimBlockers);
    }

    [Fact]
    public void BuildReport_requires_alternating_order_and_valid_gpu_for_claim()
    {
        var baseline = CreateRun(false, 6, 0);
        var optimized = CreateRun(true, 5, 1);
        var trials = Enumerable.Range(0, 10)
            .Select(index => CopyElisionLabCommand.BuildTrial(
                baseline,
                optimized,
                pairIndex: index,
                executionOrder: index % 2 == 0
                    ? "baseline-then-optimized"
                    : "optimized-then-baseline"))
            .ToArray();

        var report = CopyElisionLabCommand.BuildReport(trials, 10, 0);

        Assert.Equal("owned-d3d11-copy-elision-gpu-workload-only", report.ClaimScope);
        Assert.True(report.PerformanceClaimAllowed);
        Assert.Empty(report.PerformanceClaimBlockers);
        Assert.Equal(10, report.GpuValidPairCount);

        var invalidOrder = trials.ToArray();
        invalidOrder[1] = invalidOrder[1] with { ExecutionOrder = "baseline-then-optimized" };
        Assert.Throws<InvalidDataException>(() =>
            CopyElisionLabCommand.BuildReport(invalidOrder, 10, 0));

        var invalidGpu = optimized with
        {
            GpuTimingValid = false,
            GpuWorkloadMicroseconds = null
        };
        var gpuTrials = trials.ToArray();
        gpuTrials[0] = CopyElisionLabCommand.BuildTrial(
            baseline,
            invalidGpu,
            pairIndex: 0);
        var blocked = CopyElisionLabCommand.BuildReport(gpuTrials, 10, 0);
        Assert.False(blocked.PerformanceClaimAllowed);
        Assert.Contains("invalid-or-missing-gpu-timing", blocked.PerformanceClaimBlockers);

        var slowerGpu = optimized with
        {
            GpuWorkloadTicks = 600,
            GpuWorkloadMicroseconds = 60
        };
        var inconsistentTrials = Enumerable.Range(0, 10)
            .Select(index => CopyElisionLabCommand.BuildTrial(
                baseline,
                slowerGpu,
                pairIndex: index,
                executionOrder: index % 2 == 0
                    ? "baseline-then-optimized"
                    : "optimized-then-baseline"))
            .ToArray();
        var inconsistent = CopyElisionLabCommand.BuildReport(inconsistentTrials, 10, 0);
        Assert.False(inconsistent.PerformanceClaimAllowed);
        Assert.Contains(
            "gpu-improvement-not-consistent",
            inconsistent.PerformanceClaimBlockers);
    }

    [Fact]
    public void SummarizePairs_calculates_interpolated_percentiles()
    {
        var summary = CopyElisionLabCommand.SummarizePairs(
            [10d, 20d, 30d],
            [8d, 25d, 27d]);

        Assert.Equal(20, summary.Baseline.P50);
        Assert.Equal(25, summary.Optimized.P50);
        Assert.Equal(-2, summary.Delta.P50);
        Assert.Equal(2, summary.OptimizedLowerCount);
        Assert.Equal(1, summary.BaselineLowerCount);
    }

    private static HookLabReport CreateRun(
        bool copyElisionEnabled,
        long forwardedCopies,
        long skippedCopies)
    {
        using var document = JsonDocument.Parse("{}");
        return new HookLabReport(
            Mode: "fluidruntime-hook-ipc-lab-v0.7.3",
            ReadOnly: !copyElisionEnabled,
            WouldModifySystem: false,
            CopyElisionEnabled: copyElisionEnabled,
            AutomaticLifetimeTracking: true,
            ReleaseObservationScope: "owned-returned-buffer-texture-interface",
            RenderDriver: "hardware",
            AdapterIdentityAvailable: true,
            AdapterDescription: "Test GPU",
            AdapterVendorId: 0x1002,
            AdapterDeviceId: 0x1234,
            AdapterDedicatedVideoMemory: 8UL * 1024 * 1024 * 1024,
            AdapterSharedSystemMemory: 16UL * 1024 * 1024 * 1024,
            AdapterLuid: "0000000000000042",
            TargetProcessId: 42,
            RingName: "test",
            RingAbiVersion: 5,
            QpcFrequency: 10_000_000,
            EventCount: 20,
            LostSequenceCount: 0,
            NativeOverrunCount: 0,
            EventTypeCounts: new Dictionary<string, long> { ["CopyResource"] = 6 },
            ResourceRetireCount: 1,
            ResourceDestroyCount: 64,
            ResourceReuseCount: 63,
            ActiveResourceCount: 7,
            RetiredResourceIdCount: 65,
            RetiredResourceIdentityCount: 2,
            ProvenanceFailureCount: 0,
            ReleaseHookSlotCount: 2,
            ReleaseHookFailureCount: 0,
            CopyResourceBytes: 49152,
            RedundantCopyCandidateCount: 3,
            RedundantCopyBytes: 24576,
            AvoidableCopySharePercent: 50,
            ForwardedCopyCount: forwardedCopies,
            ForwardedCopyBytes: 49152UL - (ulong)skippedCopies * 4096UL,
            SkippedCopyCount: skippedCopies,
            SkippedCopyBytes: (ulong)skippedCopies * 4096UL,
            ContentEquivalent: true,
            RollbackRestored: true,
            DestinationBufferHash: "buffer",
            DestinationTextureHash: "texture",
            QpcFrequencyFromTarget: 10_000_000,
            WorkloadQpcTicks: 1000,
            GpuTimingSupported: true,
            GpuTimingValid: true,
            GpuTimingStatus: "valid",
            GpuTimingDisjoint: false,
            GpuQueryTimedOut: false,
            GpuFrequency: 10_000_000,
            GpuWorkloadTicks: copyElisionEnabled ? 400UL : 500UL,
            GpuWorkloadMicroseconds: copyElisionEnabled ? 40 : 50,
            TargetReport: document.RootElement.Clone(),
            SubresourceProvenanceScope:
                "owned-buffer-texture2d-map-update-copy-region",
            CopySubresourceRegionCount: 11,
            CopySubresourceRegionBytes: 8704,
            RedundantSubresourceCopyCandidateCount: 5,
            RedundantSubresourceCopyBytes: 5120,
            SubresourceContentEquivalent: true,
            SourceSubresourceHash: "subresource",
            DestinationSubresourceHash: "subresource",
            GpuViewWriteScope:
                "owned-texture2d-single-subresource-rtv-uav-clear",
            ClearRenderTargetViewCount: 1,
            ClearUnorderedAccessViewFloatCount: 1,
            GpuViewWriteBytes: 5120);
    }
}
