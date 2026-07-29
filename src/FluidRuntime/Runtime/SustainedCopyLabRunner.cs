using System.Diagnostics;
using System.Text.Json;
using FluidRuntime.Cli;
using FluidRuntime.Native;

namespace FluidRuntime.Runtime;

public sealed class SustainedCopyLabRunner
{
    private const int MinimumPairsForPerformanceClaim = 10;
    private const ulong LegacyCopyBytes = 49_152;
    private const ulong LegacyRedundantCopyBytes = 24_576;

    public async Task<SustainedCopyLabReport> RunAsync(
        SustainedCopyLabOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var targetPath = RequireFile(options.TargetPath, "Hook target executable");
        var hookPath = RequireFile(options.HookPath, "Hook DLL");
        var trials = new List<SustainedCopyTrialReport>();
        for (var pair = 0; pair < options.WarmupPairs; ++pair)
        {
            trials.Add(await RunPairAsync(
                options,
                targetPath,
                hookPath,
                pair,
                phase: "warmup",
                includedInStatistics: false,
                cancellationToken));
        }
        for (var pair = 0; pair < options.TrialPairs; ++pair)
        {
            trials.Add(await RunPairAsync(
                options,
                targetPath,
                hookPath,
                pair,
                phase: "measured",
                includedInStatistics: true,
                cancellationToken));
        }
        return BuildReport(trials, options);
    }

    private static async Task<SustainedCopyTrialReport> RunPairAsync(
        SustainedCopyLabOptions options,
        string targetPath,
        string hookPath,
        int pairIndex,
        string phase,
        bool includedInStatistics,
        CancellationToken cancellationToken)
    {
        SustainedCopyRunReport baseline;
        SustainedCopyRunReport optimized;
        var baselineFirst = pairIndex % 2 == 0;
        if (baselineFirst)
        {
            baseline = await RunOneAsync(
                options,
                targetPath,
                hookPath,
                optimized: false,
                cancellationToken);
            optimized = await RunOneAsync(
                options,
                targetPath,
                hookPath,
                optimized: true,
                cancellationToken);
        }
        else
        {
            optimized = await RunOneAsync(
                options,
                targetPath,
                hookPath,
                optimized: true,
                cancellationToken);
            baseline = await RunOneAsync(
                options,
                targetPath,
                hookPath,
                optimized: false,
                cancellationToken);
        }

        var adapterMatched = SameAdapter(baseline, optimized);
        return new SustainedCopyTrialReport(
            pairIndex,
            phase,
            includedInStatistics,
            baselineFirst ? "baseline-then-optimized" : "optimized-then-baseline",
            ContentEquivalent: baseline.ContentEquivalent && optimized.ContentEquivalent,
            RollbackRestoredInBothRuns:
                baseline.RollbackRestored && optimized.RollbackRestored,
            AdapterIdentityMatched: adapterMatched,
            baseline.CpuWorkloadMicroseconds,
            optimized.CpuWorkloadMicroseconds,
            baseline.GpuWorkloadMicroseconds,
            optimized.GpuWorkloadMicroseconds,
            baseline,
            optimized);
    }

    private static async Task<SustainedCopyRunReport> RunOneAsync(
        SustainedCopyLabOptions options,
        string targetPath,
        string hookPath,
        bool optimized,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = targetPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--hook");
        startInfo.ArgumentList.Add(hookPath);
        startInfo.ArgumentList.Add("--frames");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("--hold-ms");
        startInfo.ArgumentList.Add(options.HoldMs.ToString());
        startInfo.ArgumentList.Add("--gpu-timeout-ms");
        startInfo.ArgumentList.Add(options.GpuTimeoutMs.ToString());
        startInfo.ArgumentList.Add("--sustained-copy-count");
        startInfo.ArgumentList.Add(options.CopyCount.ToString());
        if (options.UseHardware)
        {
            startInfo.ArgumentList.Add("--hardware");
        }
        if (optimized)
        {
            startInfo.ArgumentList.Add("--managed-control");
            startInfo.ArgumentList.Add("--control-timeout-ms");
            startInfo.ArgumentList.Add("5000");
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start sustained-copy target.");
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var reader = await HookLabRunner.OpenRingAsync(process, cancellationToken);
            HookControlPolicy? policy = null;
            if (optimized)
            {
                policy = reader.PublishCopyElisionPolicy(
                    TimeSpan.FromSeconds(3),
                    (ulong)options.CopyCount);
                await reader.WaitForControlAcknowledgmentAsync(
                    policy.Epoch,
                    TimeSpan.FromSeconds(5),
                    cancellationToken);
            }

            var events = new List<HookIpcEvent>();
            while (!process.HasExited)
            {
                events.AddRange(reader.ReadAvailable());
                await Task.Delay(5, cancellationToken);
            }
            await process.WaitForExitAsync(cancellationToken);
            events.AddRange(reader.ReadAvailable());
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Sustained-copy target exited with code {process.ExitCode}: " +
                    $"{stderr.Trim()} {stdout.Trim()}");
            }

            using var document = JsonDocument.Parse(stdout);
            var targetReport = document.RootElement.Clone();
            return BuildRunReport(
                options,
                optimized,
                process.Id,
                policy,
                reader.ControlSnapshot,
                reader,
                events,
                targetReport);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static SustainedCopyRunReport BuildRunReport(
        SustainedCopyLabOptions options,
        bool optimized,
        int processId,
        HookControlPolicy? policy,
        HookControlSnapshot control,
        HookRingReader reader,
        IReadOnlyList<HookIpcEvent> events,
        JsonElement report)
    {
        var resources = report.GetProperty("resources");
        var timing = report.GetProperty("timing");
        var adapter = report.GetProperty("adapter");
        var copyEvents = events.Where(item => item.Type == HookEventType.CopyResource).ToArray();
        var redundantEvents = copyEvents
            .Where(item => item.IsRedundantCopyCandidate)
            .ToArray();
        var skippedEvents = copyEvents.Where(item => item.WasCopySkipped).ToArray();
        var acceptedEvents = events
            .Where(item => item.Type == HookEventType.ControlPolicyAccepted)
            .ToArray();
        var copyCount = options.CopyCount;
        var expectedObservedCopies = copyCount + 7L;
        var expectedObservedBytes =
            LegacyCopyBytes + (ulong)(copyCount + 1) * SustainedCopyLabOptions.SustainedBufferBytes;
        var expectedRedundantCopies = copyCount + 3L;
        var expectedRedundantBytes =
            LegacyRedundantCopyBytes +
            (ulong)copyCount * SustainedCopyLabOptions.SustainedBufferBytes;
        var expectedSkippedCopies = optimized ? copyCount : 0L;
        var expectedSkippedBytes = optimized
            ? (ulong)copyCount * SustainedCopyLabOptions.SustainedBufferBytes
            : 0UL;
        var expectedForwardedCopies = expectedObservedCopies - expectedSkippedCopies;
        var expectedForwardedBytes = expectedObservedBytes - expectedSkippedBytes;
        var sequencesMatch = events
            .Select((item, index) => item.Sequence == index)
            .All(value => value);
        var contentEquivalent =
            report.GetProperty("content_readback_succeeded").GetBoolean() &&
            report.GetProperty("sustained_buffer_contents_equal").GetBoolean() &&
            report.GetProperty("buffer_contents_equal").GetBoolean() &&
            report.GetProperty("texture_contents_equal").GetBoolean() &&
            report.GetProperty("subresource_contents_equal").GetBoolean();
        var sustainedSourceHash =
            report.GetProperty("sustained_source_buffer_hash").GetString() ?? "";
        var sustainedDestinationHash =
            report.GetProperty("sustained_destination_buffer_hash").GetString() ?? "";
        var rollbackRestored = report.GetProperty("original_pointer_restored").GetBoolean();
        var expectedStatus = optimized
            ? HookControlPolicyStatus.Exhausted
            : HookControlPolicyStatus.None;
        var acceptedEventMatches = acceptedEvents.Length == (optimized ? 1 : 0) &&
            (!optimized ||
                (acceptedEvents[0].Sequence == 0 &&
                 acceptedEvents[0].ResourceA == 1 &&
                 acceptedEvents[0].ResourceB ==
                    HookRingReader.SkipRedundantCopyResourceAction &&
                 acceptedEvents[0].SizeBytes == (ulong)copyCount));
        var eventCopyBytes = copyEvents.Aggregate(0UL, (sum, item) => sum + item.SizeBytes);
        var eventRedundantBytes =
            redundantEvents.Aggregate(0UL, (sum, item) => sum + item.SizeBytes);
        var eventSkippedBytes =
            skippedEvents.Aggregate(0UL, (sum, item) => sum + item.SizeBytes);
        var expectedPolicyEpoch = optimized ? 1L : 0L;
        var valid =
            report.GetProperty("mode").GetString() ==
                "fluidruntime-resource-hook-lab-v0.12.0" &&
            report.GetProperty("target_owned").GetBoolean() &&
            !report.GetProperty("remote_injection").GetBoolean() &&
            report.GetProperty("render_driver").GetString() ==
                (options.UseHardware ? "hardware" : "warp") &&
            report.GetProperty("sustained_copy_count").GetInt32() == copyCount &&
            report.GetProperty("sustained_buffer_bytes").GetInt32() ==
                SustainedCopyLabOptions.SustainedBufferBytes &&
            report.GetProperty("sustained_logical_copy_bytes").GetUInt64() ==
                (ulong)(copyCount + 1) * SustainedCopyLabOptions.SustainedBufferBytes &&
            report.GetProperty("max_skipped_copy_count").GetInt64() ==
                expectedSkippedCopies &&
            report.GetProperty("optimization_requested").GetBoolean() == optimized &&
            report.GetProperty("would_skip_copies").GetBoolean() == optimized &&
            report.GetProperty("control_policy_requested").GetBoolean() == optimized &&
            report.GetProperty("control_policy_wait_hresult").GetString() ==
                (optimized ? "0x00000000" : "0x00000001") &&
            report.GetProperty("resource_metrics_matched").GetBoolean() &&
            resources.GetProperty("copy_resource_count").GetInt64() ==
                expectedObservedCopies &&
            resources.GetProperty("copy_resource_bytes_estimated").GetUInt64() ==
                expectedObservedBytes &&
            resources.GetProperty("redundant_copy_candidate_count").GetInt64() ==
                expectedRedundantCopies &&
            resources.GetProperty("redundant_copy_bytes_estimated").GetUInt64() ==
                expectedRedundantBytes &&
            resources.GetProperty("forwarded_copy_count").GetInt64() ==
                expectedForwardedCopies &&
            resources.GetProperty("forwarded_copy_bytes_estimated").GetUInt64() ==
                expectedForwardedBytes &&
            resources.GetProperty("skipped_copy_count").GetInt64() ==
                expectedSkippedCopies &&
            resources.GetProperty("skipped_copy_bytes_estimated").GetUInt64() ==
                expectedSkippedBytes &&
            resources.GetProperty("control_policy_enabled").GetInt64() ==
                (optimized ? 1 : 0) &&
            resources.GetProperty("control_policy_epoch").GetInt64() ==
                expectedPolicyEpoch &&
            resources.GetProperty("control_policy_acknowledged_epoch").GetInt64() ==
                expectedPolicyEpoch &&
            resources.GetProperty("control_policy_applied_action_count").GetInt64() ==
                expectedSkippedCopies &&
            resources.GetProperty("control_policy_rejected_count").GetInt64() == 0 &&
            resources.GetProperty("control_policy_status").GetInt64() ==
                (long)expectedStatus &&
            resources.GetProperty("provenance_failure_count").GetInt64() == 0 &&
            resources.GetProperty("release_hook_failure_count").GetInt64() == 0 &&
            resources.GetProperty("ipc_overrun_count").GetInt64() == 0 &&
            copyEvents.LongLength == expectedObservedCopies &&
            eventCopyBytes == expectedObservedBytes &&
            redundantEvents.LongLength == expectedRedundantCopies &&
            eventRedundantBytes == expectedRedundantBytes &&
            skippedEvents.LongLength == expectedSkippedCopies &&
            eventSkippedBytes == expectedSkippedBytes &&
            acceptedEventMatches && sequencesMatch &&
            events.Count == resources.GetProperty("ipc_event_count").GetInt64() &&
            reader.LostSequenceCount == 0 && reader.NativeOverrunCount == 0 &&
            control.PublishedEpoch == expectedPolicyEpoch &&
            control.AcknowledgedEpoch == expectedPolicyEpoch &&
            control.AppliedActionCount == expectedSkippedCopies &&
            control.Status == expectedStatus &&
            (!optimized ||
                (policy is not null && policy.ActionBudget == (ulong)copyCount)) &&
            contentEquivalent && rollbackRestored &&
            sustainedSourceHash == sustainedDestinationHash &&
            sustainedSourceHash != "0000000000000000";
        if (!valid)
        {
            throw new InvalidDataException(
                $"Sustained-copy {(optimized ? "optimized" : "baseline")} run " +
                "violated the native/managed evidence contract.");
        }

        var qpcFrequency = timing.GetProperty("qpc_frequency").GetUInt64();
        var workloadTicks = timing.GetProperty("workload_qpc_ticks").GetUInt64();
        if (qpcFrequency == 0)
        {
            throw new InvalidDataException("Target reported a zero QPC frequency.");
        }
        var cpuMicroseconds = Math.Round(
            workloadTicks * 1_000_000d / qpcFrequency,
            3);
        double? gpuMicroseconds = null;
        if (timing.GetProperty("gpu_timing_valid").GetBoolean())
        {
            var gpuFrequency = timing.GetProperty("gpu_frequency").GetUInt64();
            if (gpuFrequency == 0)
            {
                throw new InvalidDataException("Valid GPU timing had zero frequency.");
            }
            gpuMicroseconds = Math.Round(
                timing.GetProperty("gpu_workload_ticks").GetUInt64() *
                    1_000_000d / gpuFrequency,
                3);
        }

        return new SustainedCopyRunReport(
            optimized,
            processId,
            report.GetProperty("render_driver").GetString() ?? "",
            adapter.GetProperty("description").GetString() ?? "",
            adapter.GetProperty("vendor_id").GetUInt32(),
            adapter.GetProperty("device_id").GetUInt32(),
            adapter.GetProperty("luid").GetString() ?? "",
            events.Count,
            reader.LostSequenceCount,
            reader.NativeOverrunCount,
            expectedObservedCopies,
            expectedObservedBytes,
            expectedRedundantCopies,
            expectedRedundantBytes,
            expectedForwardedCopies,
            expectedForwardedBytes,
            expectedSkippedCopies,
            expectedSkippedBytes,
            control.PublishedEpoch,
            control.AcknowledgedEpoch,
            control.AppliedActionCount,
            control.Status.ToString().ToLowerInvariant(),
            contentEquivalent,
            rollbackRestored,
            sustainedSourceHash,
            sustainedDestinationHash,
            cpuMicroseconds,
            gpuMicroseconds,
            report);
    }

    internal static SustainedCopyLabReport BuildReport(
        IReadOnlyList<SustainedCopyTrialReport> trials,
        SustainedCopyLabOptions options)
    {
        var included = trials.Where(item => item.IncludedInStatistics).ToArray();
        var warmups = trials.Where(item => !item.IncludedInStatistics).ToArray();
        var measuredOrderMatches = included.Select((item, index) =>
            item.PairIndex == index &&
            item.Phase == "measured" &&
            item.ExecutionOrder == (index % 2 == 0
                ? "baseline-then-optimized"
                : "optimized-then-baseline")).All(value => value);
        var warmupOrderMatches = warmups.Select((item, index) =>
            item.PairIndex == index &&
            item.Phase == "warmup" &&
            item.ExecutionOrder == (index % 2 == 0
                ? "baseline-then-optimized"
                : "optimized-then-baseline")).All(value => value);
        if (included.Length != options.TrialPairs ||
            warmups.Length != options.WarmupPairs ||
            included.Length == 0 ||
            !measuredOrderMatches ||
            !warmupOrderMatches ||
            trials.Any(item =>
                !item.ContentEquivalent ||
                !item.RollbackRestoredInBothRuns ||
                item.Optimized.SkippedCopyCount != options.CopyCount ||
                item.Baseline.SkippedCopyCount != 0))
        {
            throw new InvalidDataException("Sustained-copy trial trace is incomplete or unsafe.");
        }

        var gpuPairs = included.Where(item =>
            item.BaselineGpuMicroseconds.HasValue &&
            item.OptimizedGpuMicroseconds.HasValue).ToArray();
        var cpuSummary = CopyElisionLabCommand.SummarizePairs(
            included.Select(item => item.BaselineCpuMicroseconds),
            included.Select(item => item.OptimizedCpuMicroseconds));
        var gpuSummary = gpuPairs.Length == 0
            ? null
            : CopyElisionLabCommand.SummarizePairs(
                gpuPairs.Select(item => item.BaselineGpuMicroseconds!.Value),
                gpuPairs.Select(item => item.OptimizedGpuMicroseconds!.Value));
        var blockers = new List<string>();
        if (included.Length < MinimumPairsForPerformanceClaim)
        {
            blockers.Add("insufficient-trial-pairs");
        }
        if (gpuPairs.Length != included.Length)
        {
            blockers.Add("invalid-or-missing-gpu-timing");
        }
        if (gpuSummary is not null &&
            (gpuSummary.Delta.P50 >= 0 ||
             gpuSummary.Delta.P95 >= 0 ||
             gpuSummary.OptimizedLowerCount < Math.Ceiling(included.Length * 0.8)))
        {
            blockers.Add("gpu-improvement-not-consistent");
        }
        var cpuRegressionObserved =
            cpuSummary.Delta.P50 > 0 || cpuSummary.Delta.P95 > 0;
        if (included.Any(item => !item.AdapterIdentityMatched) ||
            included.Select(item => item.Baseline.AdapterLuid).Distinct().Count() != 1)
        {
            blockers.Add("missing-or-mismatched-adapter-identity");
        }
        if (included[0].Baseline.RenderDriver != "hardware")
        {
            blockers.Add("software-adapter-not-hardware");
        }

        return new SustainedCopyLabReport(
            "fluidruntime-sustained-copy-elision-trace-v0.12.0",
            TargetOwned: true,
            CooperativeLoad: true,
            RemoteInjection: false,
            SustainedCopyLabOptions.SustainedBufferBytes,
            options.CopyCount,
            (ulong)options.CopyCount * SustainedCopyLabOptions.SustainedBufferBytes,
            options.TrialPairs,
            options.WarmupPairs,
            included.Length,
            "alternating-within-pair",
            included[0].Baseline.AdapterDescription,
            included[0].Baseline.AdapterVendorId,
            included[0].Baseline.AdapterDeviceId,
            included[0].Baseline.AdapterLuid,
            ContentEquivalent: true,
            RollbackRestoredInAllRuns: true,
            "owned-d3d11-sustained-copy-elision-gpu-workload-only",
            PerformanceClaimAllowed: blockers.Count == 0,
            blockers,
            CpuRegressionObserved: cpuRegressionObserved,
            gpuPairs.Length,
            cpuSummary,
            gpuSummary,
            trials);
    }

    private static bool SameAdapter(
        SustainedCopyRunReport first,
        SustainedCopyRunReport second) =>
        !string.IsNullOrWhiteSpace(first.AdapterLuid) &&
        first.AdapterLuid == second.AdapterLuid &&
        first.AdapterVendorId == second.AdapterVendorId &&
        first.AdapterDeviceId == second.AdapterDeviceId &&
        first.AdapterDescription == second.AdapterDescription;

    private static string RequireFile(string path, string label)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"{label} was not found.", fullPath);
        }
        return fullPath;
    }
}
