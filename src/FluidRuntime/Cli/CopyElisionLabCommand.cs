using System.Text.Json;
using FluidRuntime.Runtime;

namespace FluidRuntime.Cli;

public static class CopyElisionLabCommand
{
    private const int MinimumPairsForPerformanceClaim = 10;
    private const string PerformanceClaimScope =
        "owned-d3d11-copy-elision-gpu-workload-only";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(argument => argument is "--help" or "-h"))
        {
            Console.WriteLine(HookLabOptions.CopyElisionUsage);
            return 0;
        }

        try
        {
            var options = HookLabOptions.ParseCopyElision(args);
            var runner = new HookLabRunner();
            var trials = new List<CopyElisionTrialReport>();
            for (var index = 0; index < options.WarmupPairs; ++index)
            {
                trials.Add(await RunPairAsync(
                    runner,
                    options,
                    pairIndex: index,
                    phase: "warmup",
                    includedInStatistics: false,
                    baselineFirst: index % 2 == 0));
            }
            for (var index = 0; index < options.TrialPairs; ++index)
            {
                trials.Add(await RunPairAsync(
                    runner,
                    options,
                    pairIndex: index,
                    phase: "measured",
                    includedInStatistics: true,
                    baselineFirst: index % 2 == 0));
            }

            var report = BuildReport(
                trials,
                options.TrialPairs,
                options.WarmupPairs);
            var outputPath = Path.GetFullPath(options.OutputPath);
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = true
            });
            await File.WriteAllTextAsync(outputPath, json + Environment.NewLine);

            Console.WriteLine(
                $"FluidRuntime validated {report.IncludedTrialPairs} measured copy-elision pairs " +
                $"and {report.WarmupPairs} warmup pairs.");
            Console.WriteLine(
                $"Avoided per optimized run: {report.AvoidedCopyCountPerOptimizedRun} " +
                $"CopyResource call, {report.AvoidedCopyBytesPerOptimizedRun} bytes.");
            Console.WriteLine(
                $"CPU p50: baseline={report.CpuWorkload.Baseline.P50:0.###} us; " +
                $"optimized={report.CpuWorkload.Optimized.P50:0.###} us; " +
                $"paired delta={report.CpuWorkload.Delta.P50:+0.###;-0.###;0} us.");
            if (report.GpuWorkload is not null)
            {
                Console.WriteLine(
                    $"GPU p50 ({report.GpuValidPairCount} valid pairs): " +
                    $"baseline={report.GpuWorkload.Baseline.P50:0.###} us; " +
                    $"optimized={report.GpuWorkload.Optimized.P50:0.###} us; " +
                    $"paired delta={report.GpuWorkload.Delta.P50:+0.###;-0.###;0} us.");
            }
            Console.WriteLine(
                report.PerformanceClaimAllowed
                    ? $"Performance evidence gate: allowed for {report.ClaimScope}."
                    : "Performance evidence gate: blocked by " +
                        string.Join(", ", report.PerformanceClaimBlockers) + ".");
            Console.WriteLine($"Report: {outputPath}");
            return 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or FileNotFoundException)
        {
            Console.Error.WriteLine($"Input error: {exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Copy elision lab failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<CopyElisionTrialReport> RunPairAsync(
        HookLabRunner runner,
        HookLabOptions options,
        int pairIndex,
        string phase,
        bool includedInStatistics,
        bool baselineFirst)
    {
        HookLabReport baseline;
        HookLabReport optimized;
        if (baselineFirst)
        {
            baseline = await runner.RunAsync(options with { SkipFirstRedundantCopy = false });
            optimized = await runner.RunAsync(options with { SkipFirstRedundantCopy = true });
        }
        else
        {
            optimized = await runner.RunAsync(options with { SkipFirstRedundantCopy = true });
            baseline = await runner.RunAsync(options with { SkipFirstRedundantCopy = false });
        }
        return BuildTrial(
            baseline,
            optimized,
            pairIndex,
            phase,
            includedInStatistics,
            baselineFirst ? "baseline-then-optimized" : "optimized-then-baseline");
    }

    internal static CopyElisionTrialReport BuildTrial(
        HookLabReport baseline,
        HookLabReport optimized,
        int pairIndex = 0,
        string phase = "measured",
        bool includedInStatistics = true,
        string executionOrder = "baseline-then-optimized")
    {
        var contentEquivalent = baseline.ContentEquivalent &&
            optimized.ContentEquivalent &&
            baseline.DestinationBufferHash == optimized.DestinationBufferHash &&
            baseline.DestinationTextureHash == optimized.DestinationTextureHash &&
            baseline.SubresourceContentEquivalent &&
            optimized.SubresourceContentEquivalent &&
            baseline.SourceSubresourceHash == baseline.DestinationSubresourceHash &&
            optimized.SourceSubresourceHash == optimized.DestinationSubresourceHash &&
            baseline.DestinationSubresourceHash == optimized.DestinationSubresourceHash;
        var baselineObservedCopies = baseline.EventTypeCounts.GetValueOrDefault("CopyResource");
        var optimizedObservedCopies = optimized.EventTypeCounts.GetValueOrDefault("CopyResource");
        var adapterIdentityMatched = baseline.AdapterIdentityAvailable &&
            optimized.AdapterIdentityAvailable &&
            baseline.RenderDriver == optimized.RenderDriver &&
            baseline.AdapterVendorId == optimized.AdapterVendorId &&
            baseline.AdapterDeviceId == optimized.AdapterDeviceId &&
            baseline.AdapterLuid == optimized.AdapterLuid;
        if (baseline.AdapterIdentityAvailable &&
            optimized.AdapterIdentityAvailable &&
            !adapterIdentityMatched)
        {
            throw new InvalidDataException(
                "Baseline and optimized runs used different graphics adapters.");
        }
        if (baseline.CopyElisionEnabled ||
            !optimized.CopyElisionEnabled ||
            baseline.SkippedCopyCount != 0 ||
            baseline.SkippedCopyBytes != 0 ||
            optimized.SkippedCopyCount != 1 ||
            optimized.SkippedCopyBytes != 4096 ||
            baselineObservedCopies != 6 ||
            optimizedObservedCopies != baselineObservedCopies ||
            !HasSafeLifecycle(baseline) ||
            !HasSafeLifecycle(optimized) ||
            !baseline.RollbackRestored ||
            !optimized.RollbackRestored ||
            baseline.ForwardedCopyCount - optimized.ForwardedCopyCount != 1 ||
            baseline.ForwardedCopyBytes - optimized.ForwardedCopyBytes != 4096 ||
            !contentEquivalent)
        {
            throw new InvalidDataException(
                "Baseline and optimized copy-elision runs did not satisfy the safety contract.");
        }

        var baselineCpu = ToMicroseconds(
            baseline.WorkloadQpcTicks,
            baseline.QpcFrequencyFromTarget);
        var optimizedCpu = ToMicroseconds(
            optimized.WorkloadQpcTicks,
            optimized.QpcFrequencyFromTarget);
        var baselineGpu = baseline.GpuTimingValid
            ? baseline.GpuWorkloadMicroseconds
            : null;
        var optimizedGpu = optimized.GpuTimingValid
            ? optimized.GpuWorkloadMicroseconds
            : null;
        return new CopyElisionTrialReport(
            PairIndex: pairIndex,
            Phase: phase,
            IncludedInStatistics: includedInStatistics,
            ExecutionOrder: executionOrder,
            ContentEquivalent: true,
            RollbackRestoredInBothRuns: true,
            AdapterIdentityMatched: adapterIdentityMatched,
            ObservedCopyCount: baselineObservedCopies,
            AvoidedCopyCount: optimized.SkippedCopyCount,
            AvoidedCopyBytes: optimized.SkippedCopyBytes,
            BaselineCpuMicroseconds: baselineCpu,
            OptimizedCpuMicroseconds: optimizedCpu,
            CpuDeltaMicroseconds: Math.Round(optimizedCpu - baselineCpu, 3),
            CpuDeltaPercent: DeltaPercent(baselineCpu, optimizedCpu),
            BaselineGpuMicroseconds: baselineGpu,
            OptimizedGpuMicroseconds: optimizedGpu,
            GpuDeltaMicroseconds: PairedDelta(baselineGpu, optimizedGpu),
            GpuDeltaPercent: PairedDeltaPercent(baselineGpu, optimizedGpu),
            Baseline: baseline,
            Optimized: optimized);
    }

    internal static CopyElisionLabReport BuildReport(
        IReadOnlyList<CopyElisionTrialReport> trials,
        int trialPairsRequested,
        int warmupPairs)
    {
        var included = trials.Where(trial => trial.IncludedInStatistics).ToArray();
        var warmups = trials.Where(trial => !trial.IncludedInStatistics).ToArray();
        var measuredOrderMatches = included.Select((trial, index) =>
            trial.PairIndex == index &&
            trial.Phase == "measured" &&
            trial.ExecutionOrder == (index % 2 == 0
                ? "baseline-then-optimized"
                : "optimized-then-baseline")).All(value => value);
        var warmupOrderMatches = warmups.Select((trial, index) =>
            trial.PairIndex == index &&
            trial.Phase == "warmup" &&
            trial.ExecutionOrder == (index % 2 == 0
                ? "baseline-then-optimized"
                : "optimized-then-baseline")).All(value => value);
        if (included.Length != trialPairsRequested ||
            warmups.Length != warmupPairs ||
            included.Length == 0 ||
            !measuredOrderMatches ||
            !warmupOrderMatches ||
            trials.Any(trial => !trial.ContentEquivalent || !trial.RollbackRestoredInBothRuns))
        {
            throw new InvalidDataException("Copy-elision trial trace is incomplete or unsafe.");
        }

        var gpuPairs = included
            .Where(trial => trial.BaselineGpuMicroseconds.HasValue &&
                trial.OptimizedGpuMicroseconds.HasValue)
            .ToArray();
        var cpuSummary = SummarizePairs(
            included.Select(trial => trial.BaselineCpuMicroseconds),
            included.Select(trial => trial.OptimizedCpuMicroseconds));
        var gpuSummary = gpuPairs.Length == 0
            ? null
            : SummarizePairs(
                gpuPairs.Select(trial => trial.BaselineGpuMicroseconds!.Value),
                gpuPairs.Select(trial => trial.OptimizedGpuMicroseconds!.Value));
        var blockers = new List<string>();
        if (included.Length < MinimumPairsForPerformanceClaim)
        {
            blockers.Add("insufficient-trial-pairs");
        }
        if (gpuPairs.Length != included.Length)
        {
            blockers.Add("invalid-or-missing-gpu-timing");
        }
        if (gpuPairs.Length == included.Length &&
            gpuSummary is not null &&
            (gpuSummary.Delta.P95 >= 0 ||
                gpuSummary.OptimizedLowerCount < Math.Ceiling(included.Length * 0.8)))
        {
            blockers.Add("gpu-improvement-not-consistent");
        }
        if (included.Any(trial => !trial.AdapterIdentityMatched) ||
            included.Select(trial => trial.Baseline.AdapterLuid).Distinct().Count() != 1)
        {
            blockers.Add("missing-or-mismatched-adapter-identity");
        }

        return new CopyElisionLabReport(
            Mode: "fluidruntime-copy-elision-trace-v0.7.3",
            TargetOwned: true,
            CooperativeLoad: true,
            RemoteInjection: false,
            ContentEquivalent: true,
            RollbackRestoredInAllRuns: true,
            TrialPairsRequested: trialPairsRequested,
            WarmupPairs: warmupPairs,
            IncludedTrialPairs: included.Length,
            OrderingPolicy: "alternating-within-pair",
            AdapterDescription: included[0].Baseline.AdapterDescription,
            AdapterVendorId: included[0].Baseline.AdapterVendorId,
            AdapterDeviceId: included[0].Baseline.AdapterDeviceId,
            AdapterLuid: included[0].Baseline.AdapterLuid,
            ObservedCopyCountPerRun: included[0].ObservedCopyCount,
            AvoidedCopyCountPerOptimizedRun: included[0].AvoidedCopyCount,
            AvoidedCopyBytesPerOptimizedRun: included[0].AvoidedCopyBytes,
            ClaimScope: PerformanceClaimScope,
            PerformanceClaimAllowed: blockers.Count == 0,
            PerformanceClaimBlockers: blockers,
            GpuValidPairCount: gpuPairs.Length,
            CpuWorkload: cpuSummary,
            GpuWorkload: gpuSummary,
            Trials: trials);
    }

    internal static PairedMetricSummary SummarizePairs(
        IEnumerable<double> baselineValues,
        IEnumerable<double> optimizedValues)
    {
        var baseline = baselineValues.ToArray();
        var optimized = optimizedValues.ToArray();
        if (baseline.Length == 0 || baseline.Length != optimized.Length)
        {
            throw new ArgumentException("Paired metric inputs must be non-empty and equal length.");
        }
        var deltas = baseline.Zip(optimized, (before, after) => after - before).ToArray();
        var deltaPercent = baseline.Zip(
            optimized,
            (before, after) => before == 0 ? 0 : (after - before) * 100d / before).ToArray();
        return new PairedMetricSummary(
            Baseline: Distribution(baseline),
            Optimized: Distribution(optimized),
            Delta: Distribution(deltas),
            DeltaPercent: Distribution(deltaPercent),
            OptimizedLowerCount: deltas.Count(value => value < 0),
            BaselineLowerCount: deltas.Count(value => value > 0),
            TieCount: deltas.Count(value => value == 0));
    }

    private static MetricDistribution Distribution(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return new MetricDistribution(
            Count: ordered.Length,
            Minimum: Math.Round(ordered[0], 3),
            P50: Percentile(ordered, 0.50),
            P95: Percentile(ordered, 0.95),
            Maximum: Math.Round(ordered[^1], 3),
            Mean: Math.Round(ordered.Average(), 3));
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        var rank = percentile * (ordered.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        var value = ordered[lower] + (ordered[upper] - ordered[lower]) * (rank - lower);
        return Math.Round(value, 3);
    }

    private static double ToMicroseconds(ulong ticks, ulong frequency) =>
        frequency == 0 ? 0 : Math.Round(ticks * 1_000_000d / frequency, 3);

    private static double DeltaPercent(double baseline, double optimized) =>
        baseline == 0 ? 0 : Math.Round((optimized - baseline) * 100d / baseline, 3);

    private static double? PairedDelta(double? baseline, double? optimized) =>
        baseline.HasValue && optimized.HasValue
            ? Math.Round(optimized.Value - baseline.Value, 3)
            : null;

    private static double? PairedDeltaPercent(double? baseline, double? optimized) =>
        baseline.HasValue && optimized.HasValue && baseline.Value != 0
            ? Math.Round((optimized.Value - baseline.Value) * 100d / baseline.Value, 3)
            : null;

    private static bool HasSafeLifecycle(HookLabReport report) =>
        report.RingAbiVersion == 5 &&
        report.AutomaticLifetimeTracking &&
        report.ReleaseObservationScope == "owned-returned-buffer-texture-interface" &&
        report.SubresourceProvenanceScope ==
            "owned-buffer-texture2d-map-update-copy-region" &&
        report.GpuViewWriteScope ==
            "owned-texture2d-single-subresource-rtv-uav-clear" &&
        report.ResourceRetireCount == 1 &&
        report.ResourceDestroyCount == 64 &&
        report.ActiveResourceCount == 7 &&
        report.RetiredResourceIdCount == 65 &&
        report.RetiredResourceIdentityCount + report.ResourceReuseCount == 65 &&
        report.ProvenanceFailureCount == 0 &&
        report.ReleaseHookSlotCount >= 2 &&
        report.ReleaseHookFailureCount == 0 &&
        report.CopySubresourceRegionCount == 11 &&
        report.CopySubresourceRegionBytes == 8704 &&
        report.RedundantSubresourceCopyCandidateCount == 5 &&
        report.RedundantSubresourceCopyBytes == 5120 &&
        report.ClearRenderTargetViewCount == 1 &&
        report.ClearUnorderedAccessViewFloatCount == 1 &&
        report.GpuViewWriteBytes == 5120 &&
        report.SubresourceContentEquivalent &&
        report.SourceSubresourceHash == report.DestinationSubresourceHash;
}
