using System.Diagnostics;
using System.Text.Json;
using FluidRuntime.Cli;
using FluidRuntime.Native;

namespace FluidRuntime.Runtime;

public sealed class UpdateUploadElisionLabRunner
{
    private const int MinimumPairsForPerformanceClaim = 10;
    private const double CpuComparisonOverheadBudgetMicroseconds = 1000;
    private const double CpuComparisonOverheadBudgetPercent = 10;
    private const long LegacyUpdateCount = 3;
    private const ulong LegacyUpdateBytes = 9_216;

    public Task<UpdateUploadElisionLabReport> RunAsync(
        UpdateUploadElisionLabOptions options,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(
            options,
            authorizer: null,
            binaryBinding: null,
            cancellationToken);

    public async Task<GatewayUpdateUploadLabReport> RunGatewayManagedAsync(
        UpdateUploadElisionLabOptions options,
        IGatewayUpdateUploadAuthorizer authorizer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(options);
        using var binaryBinding = OwnedBinaryBinding.Open(
            options.TargetPath,
            options.HookPath);
        var boundOptions = options with
        {
            TargetPath = binaryBinding.TargetPath,
            HookPath = binaryBinding.HookPath
        };
        var nativeEvidence = await RunCoreAsync(
            boundOptions,
            authorizer,
            binaryBinding,
            cancellationToken);
        return GatewayUpdateUploadLabReport.Build(
            nativeEvidence,
            binaryBinding.TargetSha256,
            binaryBinding.HookSha256);
    }

    private async Task<UpdateUploadElisionLabReport> RunCoreAsync(
        UpdateUploadElisionLabOptions options,
        IGatewayUpdateUploadAuthorizer? authorizer,
        OwnedBinaryBinding? binaryBinding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.CandidateActionCount is < 1 or
            > UpdateUploadElisionLabOptions.MaximumCandidateActionCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.CandidateActionCount),
                "Candidate action count must be between 1 and 128.");
        }
        var targetPath = RequireFile(options.TargetPath, "Hook target executable");
        var hookPath = RequireFile(options.HookPath, "Hook DLL");
        var trials = new List<UpdateUploadElisionTrialReport>();
        for (var pair = 0; pair < options.WarmupPairs; ++pair)
        {
            trials.Add(await RunPairAsync(
                options,
                targetPath,
                hookPath,
                pair,
                "warmup",
                includedInStatistics: false,
                authorizer,
                binaryBinding,
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
                authorizer,
                binaryBinding,
                cancellationToken));
        }
        return BuildReport(trials, options);
    }

    private static async Task<UpdateUploadElisionTrialReport> RunPairAsync(
        UpdateUploadElisionLabOptions options,
        string targetPath,
        string hookPath,
        int pairIndex,
        string phase,
        bool includedInStatistics,
        IGatewayUpdateUploadAuthorizer? authorizer,
        OwnedBinaryBinding? binaryBinding,
        CancellationToken cancellationToken)
    {
        UpdateUploadElisionRunReport baseline;
        UpdateUploadElisionRunReport optimized;
        var baselineFirst = pairIndex % 2 == 0;
        if (baselineFirst)
        {
            baseline = await RunOneAsync(
                options,
                targetPath,
                hookPath,
                optimized: false,
                pairIndex,
                phase,
                authorizer,
                binaryBinding,
                cancellationToken);
            optimized = await RunOneAsync(
                options,
                targetPath,
                hookPath,
                optimized: true,
                pairIndex,
                phase,
                authorizer,
                binaryBinding,
                cancellationToken);
        }
        else
        {
            optimized = await RunOneAsync(
                options,
                targetPath,
                hookPath,
                optimized: true,
                pairIndex,
                phase,
                authorizer,
                binaryBinding,
                cancellationToken);
            baseline = await RunOneAsync(
                options,
                targetPath,
                hookPath,
                optimized: false,
                pairIndex,
                phase,
                authorizer,
                binaryBinding,
                cancellationToken);
        }

        return new UpdateUploadElisionTrialReport(
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

    private static async Task<UpdateUploadElisionRunReport> RunOneAsync(
        UpdateUploadElisionLabOptions options,
        string targetPath,
        string hookPath,
        bool optimized,
        int pairIndex,
        string phase,
        IGatewayUpdateUploadAuthorizer? authorizer,
        OwnedBinaryBinding? binaryBinding,
        CancellationToken cancellationToken)
    {
        var managedStartedAt = Stopwatch.GetTimestamp();
        GatewayUpdateUploadAuthorization? gatewayAuthorization = null;
        if (optimized && authorizer is not null)
        {
            if (binaryBinding is null)
            {
                throw new InvalidOperationException(
                    "Gateway-managed actuation requires frozen owned binaries.");
            }
            try
            {
                gatewayAuthorization = await authorizer.AuthorizeAsync(
                    new GatewayUpdateUploadAuthorizationRequest(
                        pairIndex,
                        phase,
                        UpdateUploadElisionLabOptions.BufferBytes,
                        (ulong)options.CandidateActionCount,
                        binaryBinding.TargetSha256,
                        binaryBinding.HookSha256),
                    cancellationToken);
                gatewayAuthorization.EnsureMatchesNativePolicy(
                    UpdateUploadElisionLabOptions.BufferBytes,
                    (ulong)options.CandidateActionCount,
                    pairIndex,
                    phase,
                    binaryBinding.TargetSha256,
                    binaryBinding.HookSha256);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException ||
                !cancellationToken.IsCancellationRequested)
            {
                var authorizationFailure = exception is OperationCanceledException
                    ? new TimeoutException(
                        "FluidGateway authorization timed out before target launch.",
                        exception)
                    : exception;
                var baselineFallback = await RunOneAsync(
                    options,
                    targetPath,
                    hookPath,
                    optimized: false,
                    pairIndex,
                    phase,
                    authorizer: null,
                    binaryBinding,
                    cancellationToken);
                var failClosedReport = GatewayUpdateUploadFailClosedReport.Build(
                    authorizationFailure,
                    baselineFallback,
                    binaryBinding.TargetSha256,
                    binaryBinding.HookSha256,
                    ElapsedMicroseconds(managedStartedAt));
                throw new GatewayUpdateUploadAuthorizationDeniedException(
                    authorizationFailure,
                    failClosedReport);
            }
        }

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
        startInfo.ArgumentList.Add("--update-upload-count");
        startInfo.ArgumentList.Add(options.CandidateActionCount.ToString());
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
            ?? throw new InvalidOperationException("Unable to start update-upload target.");
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var reader = await HookLabRunner.OpenRingAsync(process, cancellationToken);
            if (reader.ProcessId != (ulong)process.Id)
            {
                throw new InvalidDataException(
                    "Hook ring process identity did not match the owned target.");
            }
            binaryBinding?.ValidateLaunchedProcess(process);
            HookControlPolicy? policy = null;
            if (optimized)
            {
                policy = reader.PublishUpdateSubresourceElisionPolicy(
                    TimeSpan.FromSeconds(4),
                    gatewayAuthorization?.NativeActionBudget ??
                        (ulong)options.CandidateActionCount);
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
                    $"Update-upload target exited with code {process.ExitCode}: " +
                    $"{stderr.Trim()} {stdout.Trim()}");
            }

            using var document = JsonDocument.Parse(stdout);
            var runReport = BuildRunReport(
                options,
                optimized,
                process.Id,
                policy,
                reader.ControlSnapshot,
                reader,
                events,
                document.RootElement.Clone(),
                gatewayAuthorization);
            return runReport with
            {
                ManagedEndToEndElapsedMicroseconds =
                    ElapsedMicroseconds(managedStartedAt)
            };
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

    private static UpdateUploadElisionRunReport BuildRunReport(
        UpdateUploadElisionLabOptions options,
        bool optimized,
        int processId,
        HookControlPolicy? policy,
        HookControlSnapshot control,
        HookRingReader reader,
        IReadOnlyList<HookIpcEvent> events,
        JsonElement report,
        GatewayUpdateUploadAuthorization? gatewayAuthorization)
    {
        var resources = report.GetProperty("resources");
        var timing = report.GetProperty("timing");
        var adapter = report.GetProperty("adapter");
        var directEvents = events.Where(item =>
            item.Type == HookEventType.UpdateSubresource &&
            item.IsUploadTransfer &&
            item.IsContentCompared).ToArray();
        var candidateEvents = directEvents.Where(item =>
            item.IsRedundantUpdateSubresourceCandidate).ToArray();
        var skippedEvents = directEvents.Where(item =>
            item.WasUpdateSubresourceSkipped).ToArray();
        var acceptedEvents = events.Where(item =>
            item.Type == HookEventType.ControlPolicyAccepted).ToArray();
        var initialHash = report.GetProperty("update_upload_initial_hash").GetString() ?? "";
        var finalHash = report.GetProperty("update_upload_final_hash").GetString() ?? "";
        var guardHash = report.GetProperty("update_upload_guard_hash").GetString() ?? "";
        var destinationHash = report
            .GetProperty("update_upload_destination_buffer_hash")
            .GetString() ?? "";
        var expectedSkipped = optimized
            ? options.CandidateActionCount
            : 0L;
        var expectedForwarded = LegacyUpdateCount +
            options.TotalUpdateCount - expectedSkipped;
        var expectedForwardedBytes = LegacyUpdateBytes +
            (ulong)(options.TotalUpdateCount - expectedSkipped) *
                UpdateUploadElisionLabOptions.BufferBytes;
        var expectedStatus = optimized
            ? HookControlPolicyStatus.Exhausted
            : HookControlPolicyStatus.None;
        var expectedEpoch = optimized ? 1L : 0L;
        var sequencesMatch = events.Select((item, index) =>
            item.Sequence == index).All(value => value);
        var eventPatternMatches = DirectEventPatternMatches(
            directEvents,
            optimized,
            initialHash,
            finalHash,
            options.CandidateActionCount);
        var acceptedEventMatches = acceptedEvents.Length == (optimized ? 1 : 0) &&
            (!optimized ||
                (acceptedEvents[0].Sequence == 0 &&
                 acceptedEvents[0].ResourceA == 1 &&
                 acceptedEvents[0].ResourceB ==
                    HookRingReader.SkipRedundantUpdateSubresourceAction &&
                 acceptedEvents[0].SizeBytes ==
                    (ulong)options.CandidateActionCount));
        var contentEquivalent =
            report.GetProperty("update_upload_mutation_applied").GetBoolean() &&
            report.GetProperty("update_upload_generation_guard_applied").GetBoolean() &&
            report.GetProperty("update_upload_contents_equal").GetBoolean() &&
            initialHash.Length == 16 &&
            finalHash.Length == 16 &&
            guardHash.Length == 16 &&
            initialHash != finalHash &&
            guardHash != finalHash &&
            finalHash == destinationHash;

        var totalDirectBytes = (ulong)options.TotalUpdateCount *
            UpdateUploadElisionLabOptions.BufferBytes;
        var valid =
            report.GetProperty("mode").GetString() ==
                "fluidruntime-resource-hook-lab-v0.12.0" &&
            report.GetProperty("target_owned").GetBoolean() &&
            !report.GetProperty("remote_injection").GetBoolean() &&
            report.GetProperty("render_driver").GetString() ==
                (options.UseHardware ? "hardware" : "warp") &&
            report.GetProperty("update_upload_scope").GetString() ==
                "owned-d3d11-default-buffer-full-update-subresource-exact-content" &&
            report.GetProperty("update_upload_count").GetInt32() ==
                options.CandidateActionCount &&
            report.GetProperty("update_upload_call_count").GetInt32() ==
                options.TotalUpdateCount &&
            report.GetProperty("update_upload_buffer_bytes").GetInt32() ==
                UpdateUploadElisionLabOptions.BufferBytes &&
            report.GetProperty("update_upload_logical_bytes").GetUInt64() ==
                totalDirectBytes &&
            report.GetProperty("optimization_requested").GetBoolean() == optimized &&
            report.GetProperty("would_skip_updates").GetBoolean() == optimized &&
            !report.GetProperty("would_skip_copies").GetBoolean() &&
            report.GetProperty("optimization_kind").GetString() ==
                (optimized
                    ? "managed-policy-skip-redundant-update-subresource"
                    : "none") &&
            report.GetProperty("max_skipped_update_count").GetInt64() ==
                expectedSkipped &&
            report.GetProperty("original_pointer_restored").GetBoolean() &&
            report.GetProperty("detach_hresult").GetString() == "0x00000000" &&
            report.GetProperty("resource_metrics_matched").GetBoolean() &&
            resources.GetProperty("update_subresource_count").GetInt64() ==
                LegacyUpdateCount + options.TotalUpdateCount &&
            resources.GetProperty("tracked_update_subresource_count").GetInt64() ==
                options.TotalUpdateCount &&
            resources.GetProperty(
                "redundant_update_subresource_candidate_count").GetInt64() ==
                options.CandidateActionCount &&
            resources.GetProperty("forwarded_update_subresource_count").GetInt64() ==
                expectedForwarded &&
            resources.GetProperty(
                "forwarded_update_subresource_bytes_estimated").GetUInt64() ==
                expectedForwardedBytes &&
            resources.GetProperty("skipped_update_subresource_count").GetInt64() ==
                expectedSkipped &&
            resources.GetProperty(
                "skipped_update_subresource_bytes_estimated").GetUInt64() ==
                (ulong)expectedSkipped * UpdateUploadElisionLabOptions.BufferBytes &&
            resources.GetProperty("update_content_cache_resource_count").GetInt64() == 1 &&
            resources.GetProperty("update_content_cache_bytes").GetUInt64() ==
                UpdateUploadElisionLabOptions.BufferBytes &&
            resources.GetProperty("control_policy_applied_action_count").GetInt64() ==
                expectedSkipped &&
            resources.GetProperty("provenance_failure_count").GetInt64() == 0 &&
            resources.GetProperty("ipc_overrun_count").GetInt64() == 0 &&
            directEvents.Length == options.TotalUpdateCount &&
            candidateEvents.Length == options.CandidateActionCount &&
            skippedEvents.LongLength == expectedSkipped &&
            acceptedEventMatches &&
            eventPatternMatches &&
            sequencesMatch &&
            reader.LostSequenceCount == 0 &&
            reader.NativeOverrunCount == 0 &&
            control.PublishedEpoch == expectedEpoch &&
            control.AcknowledgedEpoch == expectedEpoch &&
            control.AppliedActionCount == expectedSkipped &&
            control.Status == expectedStatus &&
            (!optimized ||
                (policy is not null &&
                 policy.ActionMask ==
                    HookRingReader.SkipRedundantUpdateSubresourceAction &&
                 policy.ActionBudget ==
                    (ulong)options.CandidateActionCount)) &&
            contentEquivalent;
        if (!valid)
        {
            throw new InvalidDataException(
                $"Update-upload {(optimized ? "optimized" : "baseline")} run " +
                "violated the native/managed evidence contract.");
        }

        var qpcFrequency = timing.GetProperty("qpc_frequency").GetUInt64();
        var cpuMicroseconds = Math.Round(
            timing.GetProperty("workload_qpc_ticks").GetUInt64() *
                1_000_000d / qpcFrequency,
            3);
        double? gpuMicroseconds = null;
        if (timing.GetProperty("gpu_timing_valid").GetBoolean() &&
            !timing.GetProperty("gpu_timing_disjoint").GetBoolean() &&
            !timing.GetProperty("gpu_query_timed_out").GetBoolean() &&
            timing.GetProperty("gpu_frequency").GetUInt64() is var gpuFrequency &&
            gpuFrequency != 0)
        {
            gpuMicroseconds = Math.Round(
                timing.GetProperty("gpu_workload_ticks").GetUInt64() *
                    1_000_000d / gpuFrequency,
                3);
        }

        return new UpdateUploadElisionRunReport(
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
            options.TotalUpdateCount,
            totalDirectBytes,
            options.CandidateActionCount,
            (ulong)options.CandidateActionCount *
                UpdateUploadElisionLabOptions.BufferBytes,
            expectedForwarded,
            expectedForwardedBytes,
            expectedSkipped,
            (ulong)expectedSkipped * UpdateUploadElisionLabOptions.BufferBytes,
            1,
            UpdateUploadElisionLabOptions.BufferBytes,
            control.PublishedEpoch,
            control.AcknowledgedEpoch,
            control.AppliedActionCount,
            control.Status.ToString().ToLowerInvariant(),
            MutationApplied: true,
            GenerationGuardApplied: true,
            contentEquivalent,
            RollbackRestored: true,
            initialHash,
            finalHash,
            guardHash,
            destinationHash,
            cpuMicroseconds,
            gpuMicroseconds,
            report)
        {
            GatewayAuthorization = gatewayAuthorization,
            PublishedPolicyExpiresAtQpc = policy?.ExpiresAtQpc ?? 0,
            PublishedPolicyActionMask = policy?.ActionMask ?? 0,
            PublishedPolicyActionBudget = policy?.ActionBudget ?? 0
        };
    }

    private static bool DirectEventPatternMatches(
        IReadOnlyList<HookIpcEvent> events,
        bool optimized,
        string initialHash,
        string finalHash,
        int candidateActionCount)
    {
        if (!ulong.TryParse(initialHash, System.Globalization.NumberStyles.HexNumber, null,
                out var initialKey) ||
            !ulong.TryParse(finalHash, System.Globalization.NumberStyles.HexNumber, null,
                out var finalKey) ||
            events.Count != candidateActionCount +
                UpdateUploadElisionLabOptions.RequiredUpdateCount)
        {
            return false;
        }

        var initialRepeatCount = candidateActionCount / 2;
        var finalRepeatCount = candidateActionCount - initialRepeatCount;
        var split = 1 + initialRepeatCount;
        var generationGuardIndex = split + 1 + finalRepeatCount / 2;
        for (var index = 0; index < events.Count; ++index)
        {
            var item = events[index];
            var required = index == 0 || index == split ||
                index == generationGuardIndex;
            var expectedKey = index < split ? initialKey : finalKey;
            var expectedGeneration = optimized
                ? (index < split
                    ? 1UL
                    : (index < generationGuardIndex ? 2UL : 4UL))
                : (ulong)index + (index < generationGuardIndex ? 1UL : 2UL);
            if (item.ResourceA != events[0].ResourceA ||
                item.ResourceA == 0 ||
                item.ResourceB != 0 ||
                item.SubresourceA != 0 ||
                item.SubresourceB != 0 ||
                item.SizeBytes != UpdateUploadElisionLabOptions.BufferBytes ||
                item.RegionKey != expectedKey ||
                item.Generation != expectedGeneration ||
                item.IsRedundantUpdateSubresourceCandidate == required ||
                item.WasUpdateSubresourceSkipped != (optimized && !required))
            {
                return false;
            }
        }
        return true;
    }

    internal static UpdateUploadElisionLabReport BuildReport(
        IReadOnlyList<UpdateUploadElisionTrialReport> trials,
        UpdateUploadElisionLabOptions options)
    {
        if (options.CandidateActionCount is < 1 or
            > UpdateUploadElisionLabOptions.MaximumCandidateActionCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.CandidateActionCount),
                "Candidate action count must be between 1 and 128.");
        }
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
                !item.Baseline.MutationApplied ||
                !item.Optimized.MutationApplied ||
                !item.Baseline.GenerationGuardApplied ||
                !item.Optimized.GenerationGuardApplied ||
                item.Baseline.SkippedUpdateSubresourceCount != 0 ||
                item.Optimized.SkippedUpdateSubresourceCount !=
                    options.CandidateActionCount ||
                item.Baseline.RingAbiVersion != HookRingReader.ExpectedAbiVersion ||
                item.Optimized.RingAbiVersion != HookRingReader.ExpectedAbiVersion ||
                item.Baseline.RingCapacity != HookRingReader.ExpectedCapacity ||
                item.Optimized.RingCapacity != HookRingReader.ExpectedCapacity))
        {
            throw new InvalidDataException(
                "Update-upload trial trace is incomplete or unsafe.");
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
        var cpuWithinBudgetPairCount = included.Count(item =>
        {
            var delta = item.OptimizedCpuMicroseconds - item.BaselineCpuMicroseconds;
            var deltaPercent = item.BaselineCpuMicroseconds == 0
                ? (delta <= 0 ? 0 : double.PositiveInfinity)
                : delta * 100d / item.BaselineCpuMicroseconds;
            return delta <= CpuComparisonOverheadBudgetMicroseconds &&
                deltaPercent <= CpuComparisonOverheadBudgetPercent;
        });
        var requiredWins = (int)Math.Ceiling(included.Length * 0.8);
        var blockers = new List<string>();
        if (included.Length < MinimumPairsForPerformanceClaim)
        {
            blockers.Add("insufficient-trial-pairs");
        }
        if (cpuWithinBudgetPairCount != included.Length ||
            cpuSummary.Delta.P50 > CpuComparisonOverheadBudgetMicroseconds ||
            cpuSummary.Delta.P95 > CpuComparisonOverheadBudgetMicroseconds ||
            cpuSummary.DeltaPercent.P50 > CpuComparisonOverheadBudgetPercent ||
            cpuSummary.DeltaPercent.P95 > CpuComparisonOverheadBudgetPercent)
        {
            blockers.Add("cpu-content-comparison-overhead-budget-exceeded");
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

        return new UpdateUploadElisionLabReport(
            "fluidruntime-update-upload-elision-trace-v0.12.0",
            TargetOwned: true,
            CooperativeLoad: true,
            RemoteInjection: false,
            UpdateUploadElisionLabOptions.BufferBytes,
            UpdateUploadElisionLabOptions.RequiredUpdateCount,
            options.CandidateActionCount,
            (ulong)options.CandidateActionCount *
                UpdateUploadElisionLabOptions.BufferBytes,
            ExactContentCacheResourceLimit: 1,
            ExactContentCacheByteLimit: UpdateUploadElisionLabOptions.BufferBytes,
            options.TrialPairs,
            options.WarmupPairs,
            included.Length,
            "alternating-within-pair",
            included[0].Baseline.AdapterDescription,
            included[0].Baseline.AdapterVendorId,
            included[0].Baseline.AdapterDeviceId,
            included[0].Baseline.AdapterLuid,
            MutationGuardPassed: true,
            GenerationGuardPassed: true,
            ContentEquivalent: true,
            RollbackRestoredInAllRuns: true,
            "owned-d3d11-default-buffer-full-update-subresource-exact-content-workload-only",
            "gpu-interval-improvement-with-bounded-cpu-content-comparison-overhead",
            PerformanceClaimAllowed: blockers.Count == 0,
            blockers,
            cpuSummary.OptimizedLowerCount,
            cpuWithinBudgetPairCount,
            CpuComparisonOverheadBudgetMicroseconds,
            CpuComparisonOverheadBudgetPercent,
            gpuPairs.Length,
            cpuSummary,
            gpuSummary,
            trials);
    }

    private static bool SameAdapter(
        UpdateUploadElisionRunReport first,
        UpdateUploadElisionRunReport second) =>
        !string.IsNullOrWhiteSpace(first.AdapterLuid) &&
        first.AdapterLuid == second.AdapterLuid &&
        first.AdapterVendorId == second.AdapterVendorId &&
        first.AdapterDeviceId == second.AdapterDeviceId &&
        first.AdapterDescription == second.AdapterDescription;

    private static long ElapsedMicroseconds(long startedAt) =>
        Math.Max(
            1,
            checked((long)Math.Ceiling(
                Stopwatch.GetElapsedTime(startedAt).TotalMicroseconds)));

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
