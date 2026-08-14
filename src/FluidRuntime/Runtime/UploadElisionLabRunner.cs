using System.Diagnostics;
using System.Text.Json;
using FluidRuntime.Cli;
using FluidRuntime.Native;

namespace FluidRuntime.Runtime;

public sealed class UploadElisionLabRunner
{
    private const int MinimumPairsForPerformanceClaim = 10;
    private const double CpuSubmissionOverheadBudgetMicroseconds = 1000;
    private const double CpuSubmissionOverheadBudgetPercent = 10;
    private const long TotalUploadCopies =
        UploadElisionLabOptions.RedundantCopyCount + 1L;
    private const ulong LegacyCopyBytes = 49_152;
    private const long LegacyCopyCount = 6;
    private const long LegacyRedundantCopyCount = 3;

    public async Task<UploadElisionLabReport> RunAsync(
        UploadElisionLabOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var targetPath = RequireFile(options.TargetPath, "Hook target executable");
        var hookPath = RequireFile(options.HookPath, "Hook DLL");
        var trials = new List<UploadElisionTrialReport>();
        for (var pair = 0; pair < options.WarmupPairs; ++pair)
        {
            trials.Add(await RunPairAsync(
                options,
                targetPath,
                hookPath,
                pair,
                "warmup",
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
                "measured",
                includedInStatistics: true,
                cancellationToken));
        }
        return BuildReport(trials, options);
    }

    private static async Task<UploadElisionTrialReport> RunPairAsync(
        UploadElisionLabOptions options,
        string targetPath,
        string hookPath,
        int pairIndex,
        string phase,
        bool includedInStatistics,
        CancellationToken cancellationToken)
    {
        UploadElisionRunReport baseline;
        UploadElisionRunReport optimized;
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

        return new UploadElisionTrialReport(
            pairIndex,
            phase,
            includedInStatistics,
            baselineFirst ? "baseline-then-optimized" : "optimized-then-baseline",
            ContentEquivalent: baseline.ContentEquivalent && optimized.ContentEquivalent,
            RollbackRestoredInBothRuns:
                baseline.RollbackRestored && optimized.RollbackRestored,
            AdapterIdentityMatched: SameAdapter(baseline, optimized),
            baseline.CpuWorkloadMicroseconds,
            optimized.CpuWorkloadMicroseconds,
            baseline.GpuWorkloadMicroseconds,
            optimized.GpuWorkloadMicroseconds,
            baseline,
            optimized);
    }

    private static async Task<UploadElisionRunReport> RunOneAsync(
        UploadElisionLabOptions options,
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
        startInfo.ArgumentList.Add("--upload-copy-count");
        startInfo.ArgumentList.Add(
            UploadElisionLabOptions.RedundantCopyCount.ToString());
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
            ?? throw new InvalidOperationException("Unable to start upload target.");
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var reader = await HookLabRunner.OpenRingAsync(process, cancellationToken);
            HookControlPolicy? policy = null;
            if (optimized)
            {
                policy = reader.PublishUploadElisionPolicy(
                    TimeSpan.FromSeconds(4),
                    UploadElisionLabOptions.RedundantCopyCount);
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
                    $"Upload target exited with code {process.ExitCode}: " +
                    $"{stderr.Trim()} {stdout.Trim()}");
            }

            using var document = JsonDocument.Parse(stdout);
            return BuildRunReport(
                options,
                optimized,
                process.Id,
                policy,
                reader.ControlSnapshot,
                reader,
                events,
                document.RootElement.Clone());
        }
        finally
        {
            await OwnedProcessLifetime.TerminateAsync(process);
        }
    }

    private static UploadElisionRunReport BuildRunReport(
        UploadElisionLabOptions options,
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
        var copyEvents = events
            .Where(item => item.Type == HookEventType.CopyResource)
            .ToArray();
        var uploadCopyEvents = copyEvents
            .Where(item => item.IsUploadTransfer)
            .ToArray();
        var redundantUploadEvents = uploadCopyEvents
            .Where(item => item.IsRedundantCopyCandidate)
            .ToArray();
        var uploadMapWriteEvents = events
            .Where(item => item.Type == HookEventType.MapWrite && item.IsUploadTransfer)
            .ToArray();
        var uploadUnmapWriteEvents = events
            .Where(item => item.Type == HookEventType.UnmapWrite && item.IsUploadTransfer)
            .ToArray();
        var skippedEvents = uploadCopyEvents
            .Where(item => item.WasCopySkipped)
            .ToArray();
        var acceptedEvents = events
            .Where(item => item.Type == HookEventType.ControlPolicyAccepted)
            .ToArray();
        var expectedSkippedCopies = optimized
            ? UploadElisionLabOptions.RedundantCopyCount
            : 0L;
        var expectedSkippedBytes = (ulong)expectedSkippedCopies *
            UploadElisionLabOptions.UploadBufferBytes;
        var totalUploadBytes = (ulong)TotalUploadCopies *
            UploadElisionLabOptions.UploadBufferBytes;
        var totalCopyCount = LegacyCopyCount + TotalUploadCopies;
        var totalCopyBytes = LegacyCopyBytes + totalUploadBytes;
        var expectedForwardedCopies = totalCopyCount - expectedSkippedCopies;
        var expectedForwardedBytes = totalCopyBytes - expectedSkippedBytes;
        var expectedStatus = optimized
            ? HookControlPolicyStatus.Exhausted
            : HookControlPolicyStatus.None;
        var expectedPolicyEpoch = optimized ? 1L : 0L;
        var sequencesMatch = events
            .Select((item, index) => item.Sequence == index)
            .All(value => value);
        var expectedHash = report.GetProperty("upload_expected_hash").GetString() ?? "";
        var sourceHash = report.GetProperty("upload_source_buffer_hash").GetString() ?? "";
        var destinationHash =
            report.GetProperty("upload_destination_buffer_hash").GetString() ?? "";
        var contentEquivalent =
            report.GetProperty("content_readback_succeeded").GetBoolean() &&
            report.GetProperty("upload_write_map_succeeded").GetBoolean() &&
            report.GetProperty("upload_buffer_contents_equal").GetBoolean() &&
            report.GetProperty("buffer_contents_equal").GetBoolean() &&
            report.GetProperty("texture_contents_equal").GetBoolean() &&
            report.GetProperty("subresource_contents_equal").GetBoolean() &&
            expectedHash != "0000000000000000" &&
            expectedHash == sourceHash &&
            expectedHash == destinationHash;
        var acceptedEventMatches = acceptedEvents.Length == (optimized ? 1 : 0) &&
            (!optimized ||
                (acceptedEvents[0].Sequence == 0 &&
                 acceptedEvents[0].ResourceA == 1 &&
                 acceptedEvents[0].ResourceB ==
                    HookRingReader.SkipRedundantUploadCopyAction &&
                 acceptedEvents[0].SizeBytes ==
                    UploadElisionLabOptions.RedundantCopyCount));
        var valid =
            report.GetProperty("mode").GetString() ==
                "fluidruntime-resource-hook-lab-v0.12.0" &&
            report.GetProperty("target_owned").GetBoolean() &&
            !report.GetProperty("remote_injection").GetBoolean() &&
            report.GetProperty("render_driver").GetString() ==
                (options.UseHardware ? "hardware" : "warp") &&
            report.GetProperty("upload_scope").GetString() ==
                "owned-d3d11-readable-writable-staging-to-default-buffer" &&
            report.GetProperty("upload_copy_count").GetInt32() ==
                UploadElisionLabOptions.RedundantCopyCount &&
            report.GetProperty("upload_buffer_bytes").GetInt32() ==
                UploadElisionLabOptions.UploadBufferBytes &&
            report.GetProperty("upload_logical_copy_bytes").GetUInt64() ==
                totalUploadBytes &&
            report.GetProperty("optimization_requested").GetBoolean() == optimized &&
            report.GetProperty("would_skip_copies").GetBoolean() == optimized &&
            report.GetProperty("optimization_kind").GetString() ==
                (optimized
                    ? "managed-policy-skip-redundant-upload-copy"
                    : "none") &&
            report.GetProperty("control_policy_requested").GetBoolean() == optimized &&
            report.GetProperty("control_policy_wait_hresult").GetString() ==
                (optimized ? "0x00000000" : "0x00000001") &&
            report.GetProperty("resource_metrics_matched").GetBoolean() &&
            report.GetProperty("original_pointer_restored").GetBoolean() &&
            resources.GetProperty("map_write_count").GetInt64() == 2 &&
            resources.GetProperty("unmap_write_count").GetInt64() == 2 &&
            resources.GetProperty("copy_resource_count").GetInt64() == totalCopyCount &&
            resources.GetProperty("copy_resource_bytes_estimated").GetUInt64() ==
                totalCopyBytes &&
            resources.GetProperty("redundant_copy_candidate_count").GetInt64() ==
                LegacyRedundantCopyCount +
                    UploadElisionLabOptions.RedundantCopyCount &&
            resources.GetProperty("readback_copy_count").GetInt64() == 0 &&
            resources.GetProperty("upload_copy_count").GetInt64() ==
                TotalUploadCopies &&
            resources.GetProperty("upload_copy_bytes_estimated").GetUInt64() ==
                totalUploadBytes &&
            resources.GetProperty("skipped_upload_copy_count").GetInt64() ==
                expectedSkippedCopies &&
            resources.GetProperty("skipped_upload_copy_bytes_estimated").GetUInt64() ==
                expectedSkippedBytes &&
            resources.GetProperty("skipped_copy_count").GetInt64() ==
                expectedSkippedCopies &&
            resources.GetProperty("skipped_copy_bytes_estimated").GetUInt64() ==
                expectedSkippedBytes &&
            resources.GetProperty("forwarded_copy_count").GetInt64() ==
                expectedForwardedCopies &&
            resources.GetProperty("forwarded_copy_bytes_estimated").GetUInt64() ==
                expectedForwardedBytes &&
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
            copyEvents.LongLength == totalCopyCount &&
            copyEvents.Aggregate(0UL, (sum, item) => sum + item.SizeBytes) ==
                totalCopyBytes &&
            uploadCopyEvents.LongLength == TotalUploadCopies &&
            uploadCopyEvents.All(item =>
                item.SizeBytes == UploadElisionLabOptions.UploadBufferBytes) &&
            redundantUploadEvents.LongLength ==
                UploadElisionLabOptions.RedundantCopyCount &&
            uploadMapWriteEvents.Length == 1 &&
            uploadMapWriteEvents[0].SizeBytes ==
                UploadElisionLabOptions.UploadBufferBytes &&
            uploadUnmapWriteEvents.Length == 1 &&
            uploadUnmapWriteEvents[0].SizeBytes ==
                UploadElisionLabOptions.UploadBufferBytes &&
            skippedEvents.LongLength == expectedSkippedCopies &&
            skippedEvents.Aggregate(0UL, (sum, item) => sum + item.SizeBytes) ==
                expectedSkippedBytes &&
            acceptedEventMatches &&
            sequencesMatch &&
            events.Count == resources.GetProperty("ipc_event_count").GetInt64() &&
            reader.LostSequenceCount == 0 &&
            reader.NativeOverrunCount == 0 &&
            control.PublishedEpoch == expectedPolicyEpoch &&
            control.AcknowledgedEpoch == expectedPolicyEpoch &&
            control.AppliedActionCount == expectedSkippedCopies &&
            control.Status == expectedStatus &&
            (!optimized ||
                (policy is not null &&
                 policy.ActionMask == HookRingReader.SkipRedundantUploadCopyAction &&
                 policy.ActionBudget ==
                    UploadElisionLabOptions.RedundantCopyCount)) &&
            contentEquivalent;
        if (!valid)
        {
            throw new InvalidDataException(
                $"Upload {(optimized ? "optimized" : "baseline")} run " +
                "violated the native/managed evidence contract.");
        }

        var qpcFrequency = timing.GetProperty("qpc_frequency").GetUInt64();
        if (qpcFrequency == 0)
        {
            throw new InvalidDataException("Target reported a zero QPC frequency.");
        }
        var cpuMicroseconds = Math.Round(
            timing.GetProperty("workload_qpc_ticks").GetUInt64() *
                1_000_000d / qpcFrequency,
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

        return new UploadElisionRunReport(
            optimized,
            processId,
            reader.AbiVersion,
            reader.Capacity,
            report.GetProperty("render_driver").GetString() ?? "",
            adapter.GetProperty("description").GetString() ?? "",
            adapter.GetProperty("vendor_id").GetUInt32(),
            adapter.GetProperty("device_id").GetUInt32(),
            adapter.GetProperty("luid").GetString() ?? "",
            events.Count,
            reader.LostSequenceCount,
            reader.NativeOverrunCount,
            TotalUploadCopies,
            totalUploadBytes,
            UploadWriteMapCount: 1,
            UploadWriteMapBytes: UploadElisionLabOptions.UploadBufferBytes,
            expectedForwardedCopies,
            expectedForwardedBytes,
            expectedSkippedCopies,
            expectedSkippedBytes,
            control.PublishedEpoch,
            control.AcknowledgedEpoch,
            control.AppliedActionCount,
            control.Status.ToString().ToLowerInvariant(),
            contentEquivalent,
            RollbackRestored: true,
            expectedHash,
            sourceHash,
            destinationHash,
            cpuMicroseconds,
            gpuMicroseconds,
            report);
    }

    internal static UploadElisionLabReport BuildReport(
        IReadOnlyList<UploadElisionTrialReport> trials,
        UploadElisionLabOptions options)
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
                item.Optimized.SkippedUploadCopyCount !=
                    UploadElisionLabOptions.RedundantCopyCount ||
                item.Baseline.SkippedUploadCopyCount != 0 ||
                item.Optimized.RingAbiVersion != HookRingReader.ExpectedAbiVersion ||
                item.Baseline.RingAbiVersion != HookRingReader.ExpectedAbiVersion ||
                item.Optimized.RingCapacity != HookRingReader.ExpectedCapacity ||
                item.Baseline.RingCapacity != HookRingReader.ExpectedCapacity ||
                item.Optimized.UploadWriteMapCount != 1 ||
                item.Baseline.UploadWriteMapCount != 1))
        {
            throw new InvalidDataException("Upload trial trace is incomplete or unsafe.");
        }

        var cpuSummary = CopyElisionLabCommand.SummarizePairs(
            included.Select(item => item.BaselineCpuMicroseconds),
            included.Select(item => item.OptimizedCpuMicroseconds));
        var gpuPairs = included.Where(item =>
            item.BaselineGpuMicroseconds.HasValue &&
            item.OptimizedGpuMicroseconds.HasValue).ToArray();
        var gpuSummary = gpuPairs.Length == 0
            ? null
            : CopyElisionLabCommand.SummarizePairs(
                gpuPairs.Select(item => item.BaselineGpuMicroseconds!.Value),
                gpuPairs.Select(item => item.OptimizedGpuMicroseconds!.Value));
        var requiredWins = (int)Math.Ceiling(included.Length * 0.8);
        var cpuWithinBudgetPairCount = included.Count(item =>
        {
            var delta = item.OptimizedCpuMicroseconds -
                item.BaselineCpuMicroseconds;
            var deltaPercent = item.BaselineCpuMicroseconds == 0
                ? (delta <= 0 ? 0 : double.PositiveInfinity)
                : delta * 100d / item.BaselineCpuMicroseconds;
            return delta <= CpuSubmissionOverheadBudgetMicroseconds &&
                deltaPercent <= CpuSubmissionOverheadBudgetPercent;
        });
        var blockers = new List<string>();
        if (included.Length < MinimumPairsForPerformanceClaim)
        {
            blockers.Add("insufficient-trial-pairs");
        }
        if (cpuWithinBudgetPairCount != included.Length ||
            cpuSummary.Delta.P50 > CpuSubmissionOverheadBudgetMicroseconds ||
            cpuSummary.Delta.P95 > CpuSubmissionOverheadBudgetMicroseconds ||
            cpuSummary.DeltaPercent.P50 > CpuSubmissionOverheadBudgetPercent ||
            cpuSummary.DeltaPercent.P95 > CpuSubmissionOverheadBudgetPercent)
        {
            blockers.Add("cpu-submission-overhead-budget-exceeded");
        }
        if (gpuPairs.Length != included.Length)
        {
            blockers.Add("invalid-or-missing-gpu-timing");
        }
        if (gpuSummary is not null &&
            (gpuSummary.Delta.P50 >= 0 ||
             gpuSummary.Delta.P95 >= 0 ||
             gpuSummary.OptimizedLowerCount < requiredWins))
        {
            blockers.Add("gpu-improvement-not-consistent");
        }
        if (included.Any(item => !item.AdapterIdentityMatched) ||
            included.Select(item => item.Baseline.AdapterLuid).Distinct().Count() != 1 ||
            string.IsNullOrWhiteSpace(included[0].Baseline.AdapterLuid))
        {
            blockers.Add("missing-or-mismatched-adapter-identity");
        }
        if (included[0].Baseline.RenderDriver != "hardware")
        {
            blockers.Add("software-adapter-not-hardware");
        }

        return new UploadElisionLabReport(
            "fluidruntime-upload-elision-trace-v0.12.0",
            TargetOwned: true,
            CooperativeLoad: true,
            RemoteInjection: false,
            UploadElisionLabOptions.UploadBufferBytes,
            UploadElisionLabOptions.RedundantCopyCount,
            (ulong)UploadElisionLabOptions.RedundantCopyCount *
                UploadElisionLabOptions.UploadBufferBytes,
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
            "owned-d3d11-writable-staging-to-default-upload-copy-workload-only",
            "gpu-interval-improvement-with-bounded-cpu-submission-overhead",
            PerformanceClaimAllowed: blockers.Count == 0,
            blockers,
            cpuSummary.OptimizedLowerCount,
            cpuWithinBudgetPairCount,
            CpuSubmissionOverheadBudgetMicroseconds,
            CpuSubmissionOverheadBudgetPercent,
            gpuPairs.Length,
            cpuSummary,
            gpuSummary,
            trials);
    }

    private static bool SameAdapter(
        UploadElisionRunReport first,
        UploadElisionRunReport second) =>
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
