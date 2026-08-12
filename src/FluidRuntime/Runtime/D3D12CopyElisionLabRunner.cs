using System.Diagnostics;
using System.Text.Json;
using FluidRuntime.Cli;
using FluidRuntime.Native;

namespace FluidRuntime.Runtime;

public sealed class D3D12CopyElisionLabRunner
{
    private const int MinimumPairsForPerformanceClaim = 10;

    public async Task<GatewayD3D12CopyLabReport> RunAsync(
        GatewayD3D12CopyLabOptions options,
        IGatewayUpdateUploadAuthorizer authorizer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authorizer);
        using var binding = OwnedBinaryBinding.Open(
            options.TargetPath,
            options.HookPath);
        var trials = new List<D3D12CopyElisionTrialReport>();
        for (var pair = 0; pair < options.WarmupPairs; ++pair)
        {
            trials.Add(await RunPairAsync(
                options,
                binding,
                authorizer,
                pair,
                "warmup",
                includedInStatistics: false,
                cancellationToken));
        }
        for (var pair = 0; pair < options.TrialPairs; ++pair)
        {
            trials.Add(await RunPairAsync(
                options,
                binding,
                authorizer,
                pair,
                "measured",
                includedInStatistics: true,
                cancellationToken));
        }
        return BuildReport(options, binding, trials);
    }

    private static async Task<D3D12CopyElisionTrialReport> RunPairAsync(
        GatewayD3D12CopyLabOptions options,
        OwnedBinaryBinding binding,
        IGatewayUpdateUploadAuthorizer authorizer,
        int pairIndex,
        string phase,
        bool includedInStatistics,
        CancellationToken cancellationToken)
    {
        D3D12CopyElisionRunReport baseline;
        D3D12CopyElisionRunReport optimized;
        var baselineFirst = pairIndex % 2 == 0;
        if (baselineFirst)
        {
            baseline = await RunOneAsync(
                options,
                binding,
                optimized: false,
                pairIndex,
                phase,
                authorizer: null,
                cancellationToken);
            optimized = await RunOneAsync(
                options,
                binding,
                optimized: true,
                pairIndex,
                phase,
                authorizer,
                cancellationToken);
        }
        else
        {
            optimized = await RunOneAsync(
                options,
                binding,
                optimized: true,
                pairIndex,
                phase,
                authorizer,
                cancellationToken);
            baseline = await RunOneAsync(
                options,
                binding,
                optimized: false,
                pairIndex,
                phase,
                authorizer: null,
                cancellationToken);
        }

        return new D3D12CopyElisionTrialReport(
            pairIndex,
            phase,
            includedInStatistics,
            baselineFirst ? "baseline-then-optimized" : "optimized-then-baseline",
            baseline.ContentEquivalent && optimized.ContentEquivalent,
            baseline.FenceCompleted && optimized.FenceCompleted,
            baseline.RollbackRestored && optimized.RollbackRestored,
            SameAdapter(baseline, optimized),
            baseline,
            optimized);
    }

    private static async Task<D3D12CopyElisionRunReport> RunOneAsync(
        GatewayD3D12CopyLabOptions options,
        OwnedBinaryBinding binding,
        bool optimized,
        int pairIndex,
        string phase,
        IGatewayUpdateUploadAuthorizer? authorizer,
        CancellationToken cancellationToken)
    {
        var managedStartedAt = Stopwatch.GetTimestamp();
        GatewayUpdateUploadAuthorization? authorization = null;
        if (optimized)
        {
            ArgumentNullException.ThrowIfNull(authorizer);
            try
            {
                authorization = await authorizer.AuthorizeAsync(
                    new GatewayUpdateUploadAuthorizationRequest(
                        pairIndex,
                        phase,
                        GatewayD3D12CopyLabOptions.BufferBytes,
                        (ulong)options.CandidateActionCount,
                        binding.TargetSha256,
                        binding.HookSha256,
                        GatewayUploadBackend.D3D12CopyBufferRegion),
                    cancellationToken);
                authorization.EnsureMatchesNativePolicy(
                    GatewayD3D12CopyLabOptions.BufferBytes,
                    (ulong)options.CandidateActionCount,
                    pairIndex,
                    phase,
                    binding.TargetSha256,
                    binding.HookSha256,
                    GatewayUploadBackend.D3D12CopyBufferRegion);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException ||
                !cancellationToken.IsCancellationRequested)
            {
                var failure = exception is OperationCanceledException
                    ? new TimeoutException(
                        "FluidGateway D3D12 authorization timed out.",
                        exception)
                    : exception;
                var fallback = await RunOneAsync(
                    options,
                    binding,
                    optimized: false,
                    pairIndex,
                    phase,
                    authorizer: null,
                    cancellationToken);
                var typed = failure as GatewayUpdateUploadAuthorizationFailureException;
                var report = new GatewayD3D12CopyFailClosedReport(
                    "fluidruntime-gateway-d3d12-copy-fail-closed-v0.20.0",
                    FailClosed: true,
                    NativePolicyPublished: false,
                    typed?.FailureType ?? failure.GetType().Name,
                    failure.Message,
                    typed?.CompletedRoundTrips ?? 0,
                    typed?.ElapsedMicroseconds ??
                        Math.Max(1, ElapsedMicroseconds(managedStartedAt) -
                            fallback.ManagedEndToEndElapsedMicroseconds),
                    typed?.DeadlineMilliseconds ?? options.TimeoutMs,
                    CompleteFallbackElapsedMicroseconds:
                        ElapsedMicroseconds(managedStartedAt),
                    binding.TargetSha256,
                    binding.HookSha256,
                    AllTrackedCopiesForwarded:
                        fallback.ForwardedCopyCount == fallback.TrackedCopyCount,
                    NoCopiesSkipped: fallback.SkippedCopyCount == 0,
                    fallback.ContentEquivalent,
                    fallback.FenceCompleted,
                    fallback.RollbackRestored,
                    fallback);
                throw new GatewayD3D12CopyAuthorizationDeniedException(
                    failure,
                    report);
            }
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = binding.TargetPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--hook");
        startInfo.ArgumentList.Add(binding.HookPath);
        startInfo.ArgumentList.Add("--hardware");
        startInfo.ArgumentList.Add(options.UseHardware ? "true" : "false");
        startInfo.ArgumentList.Add("--candidate-count");
        startInfo.ArgumentList.Add(options.CandidateActionCount.ToString());
        startInfo.ArgumentList.Add("--hold-ms");
        startInfo.ArgumentList.Add(options.HoldMs.ToString());
        startInfo.ArgumentList.Add("--gpu-timeout-ms");
        startInfo.ArgumentList.Add(options.GpuTimeoutMs.ToString());
        if (optimized)
        {
            startInfo.ArgumentList.Add("--managed-control");
            startInfo.ArgumentList.Add("--control-timeout-ms");
            startInfo.ArgumentList.Add("5000");
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Unable to start the owned D3D12 hook target.");
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(checked(
                options.HoldMs + options.GpuTimeoutMs + 15_000));
            using var reader = await HookLabRunner.OpenRingAsync(
                process,
                timeout.Token,
                d3d12: true);
            if (reader.ProcessId != (ulong)process.Id)
            {
                throw new InvalidDataException(
                    "D3D12 hook ring PID did not match the owned target.");
            }
            binding.ValidateLaunchedProcess(process);

            HookControlPolicy? policy = null;
            if (optimized)
            {
                policy = reader.PublishD3D12CopyBufferRegionElisionPolicy(
                    TimeSpan.FromSeconds(4),
                    authorization!.NativeActionBudget);
                await reader.WaitForControlAcknowledgmentAsync(
                    policy.Epoch,
                    TimeSpan.FromSeconds(5),
                    timeout.Token);
            }

            var events = new List<HookIpcEvent>();
            while (!process.HasExited)
            {
                timeout.Token.ThrowIfCancellationRequested();
                events.AddRange(reader.ReadAvailable());
                await Task.Delay(5, timeout.Token);
            }
            await process.WaitForExitAsync(timeout.Token);
            events.AddRange(reader.ReadAvailable());
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"D3D12 hook target exited with code {process.ExitCode}: " +
                    $"{stderr.Trim()} {stdout.Trim()}");
            }

            using var document = JsonDocument.Parse(stdout);
            var run = BuildRunReport(
                options,
                optimized,
                process.Id,
                reader,
                reader.ControlSnapshot,
                events,
                document.RootElement.Clone(),
                policy,
                authorization);
            return run with
            {
                ManagedEndToEndElapsedMicroseconds =
                    ElapsedMicroseconds(managedStartedAt)
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The owned D3D12 hook target timed out.");
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

    internal static D3D12CopyElisionRunReport BuildRunReport(
        GatewayD3D12CopyLabOptions options,
        bool optimized,
        int processId,
        HookRingReader reader,
        HookControlSnapshot control,
        IReadOnlyList<HookIpcEvent> events,
        JsonElement report,
        HookControlPolicy? policy,
        GatewayUpdateUploadAuthorization? authorization)
    {
        var adapter = report.GetProperty("adapter");
        var workload = report.GetProperty("workload");
        var hook = report.GetProperty("hook");
        var nativeControl = report.GetProperty("control");
        var timing = report.GetProperty("timing");
        const string sourceSnapshotMode =
            "registration-copy-cpu-shadow-upload-unmapped-until-fence";
        var directEvents = events.Where(item =>
            item.Type == HookEventType.D3D12CopyBufferRegion).ToArray();
        var candidateEvents = directEvents.Where(item =>
            item.IsD3D12RedundantCandidate).ToArray();
        var skippedEvents = directEvents.Where(item =>
            item.WasD3D12CopySkipped).ToArray();
        var invalidations = events.Where(item =>
            item.Type == HookEventType.D3D12ResourceInvalidate).ToArray();
        var closes = events.Where(item =>
            item.Type == HookEventType.D3D12CommandListClose).ToArray();
        var accepted = events.Where(item =>
            item.Type == HookEventType.ControlPolicyAccepted).ToArray();
        var expectedTracked = options.CandidateActionCount + 4L;
        var expectedSkipped = optimized ? options.CandidateActionCount : 0L;
        var expectedForwarded = expectedTracked - expectedSkipped;
        var expectedStatus = optimized
            ? HookControlPolicyStatus.Exhausted
            : HookControlPolicyStatus.None;
        var sequencesMatch = events.Select((item, index) =>
            item.Sequence == index).All(value => value);
        var eventPatternMatches = D3D12EventPatternMatches(
            directEvents,
            options.CandidateActionCount,
            optimized);
        var acceptedMatches = accepted.Length == (optimized ? 1 : 0) &&
            (!optimized ||
                (accepted[0].ResourceA == 1 &&
                 accepted[0].ResourceB ==
                    HookRingReader.SkipRedundantD3D12CopyBufferRegionAction &&
                 accepted[0].SizeBytes == (ulong)options.CandidateActionCount));
        var automaticInvalidations = invalidations.Where(item =>
            !item.IsD3D12ExplicitInvalidation).ToArray();
        var explicitInvalidations = invalidations.Where(item =>
            item.IsD3D12ExplicitInvalidation).ToArray();
        var guardEventsMatch =
            automaticInvalidations.Length == 1 &&
            automaticInvalidations[0].SizeBytes == 1 &&
            automaticInvalidations[0].Generation == 3 &&
            explicitInvalidations.Length == 1 &&
            explicitInvalidations[0].SizeBytes ==
                GatewayD3D12CopyLabOptions.BufferBytes &&
            explicitInvalidations[0].Generation == 5 &&
            closes.Length == 1 &&
            closes[0].Generation == 7;
        var patternAHash = workload.GetProperty("pattern_a_hash").GetString() ?? "";
        var patternBHash = workload.GetProperty("pattern_b_hash").GetString() ?? "";
        var finalHash = workload.GetProperty("final_hash").GetString() ?? "";
        var fenceCompleted =
            timing.GetProperty("fence_completed_value").GetUInt64() >=
            timing.GetProperty("fence_signaled_value").GetUInt64();
        var debugValid =
            report.GetProperty("debug_warning_count").GetUInt64() == 0 &&
            report.GetProperty("debug_error_count").GetUInt64() == 0;
        var contentEquivalent =
            workload.GetProperty("source_transition_applied").GetBoolean() &&
            workload.GetProperty("automatic_invalidation_guard_applied").GetBoolean() &&
            workload.GetProperty("explicit_invalidation_guard_applied").GetBoolean() &&
            workload.GetProperty("immutable_sources_verified").GetBoolean() &&
            workload.GetProperty("content_equivalent").GetBoolean() &&
            patternAHash.Length == 16 &&
            patternBHash.Length == 16 &&
            finalHash.Length == 16 &&
            patternAHash != patternBHash &&
            finalHash == patternBHash;
        var expectedPolicyEpoch = optimized ? 1L : 0L;
        var nativeValid =
            report.GetProperty("mode").GetString() ==
                "fluidruntime-owned-d3d12-copy-elision-v0.20.0" &&
            report.GetProperty("target_owned").GetBoolean() &&
            report.GetProperty("cooperative_load").GetBoolean() &&
            !report.GetProperty("remote_injection").GetBoolean() &&
            report.GetProperty("actuation_enabled").GetBoolean() == optimized &&
            !report.GetProperty("self_published_control").GetBoolean() &&
            !report.GetProperty("physical_transfer_bytes_measured").GetBoolean() &&
            report.GetProperty("render_driver").GetString() ==
                (options.UseHardware ? "hardware" : "warp") &&
            report.GetProperty("process_id").GetInt32() == processId &&
            workload.GetProperty("scope").GetString() ==
                "owned-d3d12-copy-queue-full-buffer-copy-buffer-region" &&
            workload.GetProperty("buffer_bytes").GetUInt64() ==
                GatewayD3D12CopyLabOptions.BufferBytes &&
            workload.GetProperty("source_snapshot_mode").GetString() ==
                sourceSnapshotMode &&
            workload.GetProperty("source_snapshot_bytes").GetUInt64() ==
                GatewayD3D12CopyLabOptions.SourceSnapshotBytes &&
            workload.GetProperty("upload_unmapped_after_registration").GetBoolean() &&
            workload.GetProperty("candidate_count").GetInt32() ==
                options.CandidateActionCount &&
            workload.GetProperty("tracked_copy_count").GetInt64() == expectedTracked &&
            workload.GetProperty("expected_forwarded_count").GetInt64() ==
                expectedForwarded &&
            workload.GetProperty("expected_skipped_count").GetInt64() ==
                expectedSkipped &&
            hook.GetProperty("snapshot_abi_version").GetUInt32() == 1 &&
            hook.GetProperty("source_snapshot_bytes").GetUInt64() ==
                GatewayD3D12CopyLabOptions.SourceSnapshotBytes &&
            hook.GetProperty("attach_hresult").GetString() == "0x00000000" &&
            hook.GetProperty("register_upload_hresult").GetString() == "0x00000000" &&
            hook.GetProperty("register_destination_hresult").GetString() ==
                "0x00000000" &&
            hook.GetProperty("invalidation_hresult").GetString() == "0x00000000" &&
            hook.GetProperty("detach_hresult").GetString() == "0x00000000" &&
            hook.GetProperty("original_pointer_restored").GetBoolean() &&
            hook.GetProperty("tracked_copy_count").GetInt64() == expectedTracked &&
            hook.GetProperty("redundant_candidate_count").GetInt64() ==
                options.CandidateActionCount &&
            hook.GetProperty("forwarded_copy_count").GetInt64() == expectedForwarded &&
            hook.GetProperty("skipped_copy_count").GetInt64() == expectedSkipped &&
            hook.GetProperty("exact_comparison_count").GetInt64() ==
                options.CandidateActionCount + 1L &&
            hook.GetProperty("automatic_invalidation_count").GetInt64() == 1 &&
            hook.GetProperty("explicit_invalidation_count").GetInt64() == 1 &&
            hook.GetProperty("command_list_close_count").GetInt64() == 1 &&
            hook.GetProperty("cache_generation").GetInt64() == 7 &&
            hook.GetProperty("ipc_event_count").GetInt64() == events.Count &&
            hook.GetProperty("ipc_overrun_count").GetInt64() == 0 &&
            nativeControl.GetProperty("requested").GetBoolean() == optimized &&
            nativeControl.GetProperty("wait_hresult").GetString() ==
                (optimized ? "0x00000000" : "0x00000001") &&
            nativeControl.GetProperty("enabled").GetInt64() == expectedPolicyEpoch &&
            nativeControl.GetProperty("epoch").GetInt64() == expectedPolicyEpoch &&
            nativeControl.GetProperty("acknowledged_epoch").GetInt64() ==
                expectedPolicyEpoch &&
            nativeControl.GetProperty("applied_action_count").GetInt64() ==
                expectedSkipped &&
            nativeControl.GetProperty("rejected_count").GetInt64() == 0 &&
            nativeControl.GetProperty("status").GetInt64() == (long)expectedStatus &&
            contentEquivalent &&
            fenceCompleted &&
            debugValid &&
            directEvents.LongLength == expectedTracked &&
            candidateEvents.Length == options.CandidateActionCount &&
            skippedEvents.LongLength == expectedSkipped &&
            acceptedMatches &&
            guardEventsMatch &&
            eventPatternMatches &&
            sequencesMatch &&
            reader.LostSequenceCount == 0 &&
            reader.NativeOverrunCount == 0 &&
            control.PublishedEpoch == expectedPolicyEpoch &&
            control.AcknowledgedEpoch == expectedPolicyEpoch &&
            control.AppliedActionCount == expectedSkipped &&
            control.Status == expectedStatus &&
            (!optimized ||
                (policy is not null &&
                 policy.ActionMask ==
                    HookRingReader.SkipRedundantD3D12CopyBufferRegionAction &&
                 policy.ActionBudget == (ulong)options.CandidateActionCount &&
                 authorization?.Backend == GatewayUploadBackend.D3D12CopyBufferRegion));
        if (!nativeValid)
        {
            throw new InvalidDataException(
                $"D3D12 {(optimized ? "optimized" : "baseline")} run " +
                "violated the native/managed evidence contract.");
        }

        var gpuValid = timing.GetProperty("gpu_timestamp_valid").GetBoolean();
        return new D3D12CopyElisionRunReport(
            optimized,
            processId,
            reader.AbiVersion,
            reader.Capacity,
            report.GetProperty("render_driver").GetString() ?? "",
            adapter.GetProperty("description").GetString() ?? "",
            adapter.GetProperty("vendor_id").GetUInt32(),
            adapter.GetProperty("device_id").GetUInt32(),
            adapter.GetProperty("luid").GetString() ?? "",
            adapter.GetProperty("uma").GetBoolean(),
            adapter.GetProperty("cache_coherent_uma").GetBoolean(),
            adapter.GetProperty("resource_heap_tier").GetUInt32(),
            events.Count,
            reader.LostSequenceCount,
            reader.NativeOverrunCount,
            options.CandidateActionCount,
            (ulong)options.CandidateActionCount *
                GatewayD3D12CopyLabOptions.BufferBytes,
            sourceSnapshotMode,
            GatewayD3D12CopyLabOptions.SourceSnapshotBytes,
            UploadUnmappedAfterRegistration: true,
            expectedTracked,
            (ulong)expectedTracked * GatewayD3D12CopyLabOptions.BufferBytes,
            expectedForwarded,
            (ulong)expectedForwarded * GatewayD3D12CopyLabOptions.BufferBytes,
            expectedSkipped,
            (ulong)expectedSkipped * GatewayD3D12CopyLabOptions.BufferBytes,
            options.CandidateActionCount + 1L,
            (ulong)(options.CandidateActionCount + 1L) *
                GatewayD3D12CopyLabOptions.BufferBytes,
            1,
            1,
            1,
            SourceTransitionApplied: true,
            AutomaticInvalidationGuardApplied: true,
            ExplicitInvalidationGuardApplied: true,
            ImmutableSourcesVerified: true,
            contentEquivalent,
            fenceCompleted,
            debugValid,
            RollbackRestored: true,
            patternAHash,
            patternBHash,
            finalHash,
            timing.GetProperty("cpu_record_microseconds").GetDouble(),
            timing.GetProperty("submit_to_fence_microseconds").GetDouble(),
            timing.GetProperty("total_workload_microseconds").GetDouble(),
            gpuValid
                ? timing.GetProperty("gpu_workload_microseconds").GetDouble()
                : null,
            report)
        {
            GatewayAuthorization = authorization,
            PublishedPolicyExpiresAtQpc = policy?.ExpiresAtQpc ?? 0,
            PublishedPolicyActionMask = policy?.ActionMask ?? 0,
            PublishedPolicyActionBudget = policy?.ActionBudget ?? 0
        };
    }

    private static bool D3D12EventPatternMatches(
        IReadOnlyList<HookIpcEvent> events,
        int candidateCount,
        bool optimized)
    {
        if (events.Count != candidateCount + 4)
        {
            return false;
        }
        var firstGroup = candidateCount / 2;
        var secondTotal = candidateCount - firstGroup;
        var secondBeforeAutomaticInvalidation = secondTotal / 2;
        var transitionIndex = firstGroup + 1;
        var automaticGuardIndex = transitionIndex +
            secondBeforeAutomaticInvalidation + 1;
        var explicitGuardIndex = automaticGuardIndex + 1;
        var destination = events[0].ResourceA;
        var source = events[0].ResourceB;
        for (var index = 0; index < events.Count; ++index)
        {
            var item = events[index];
            var required = index is 0 || index == transitionIndex ||
                index == automaticGuardIndex || index == explicitGuardIndex;
            var beforeTransition = index < transitionIndex;
            var beforeAutomaticInvalidation = index < automaticGuardIndex;
            var beforeExplicitInvalidation = index < explicitGuardIndex;
            var expectedGeneration = beforeTransition
                ? 1UL
                : (beforeAutomaticInvalidation
                    ? 2UL
                    : (beforeExplicitInvalidation ? 4UL : 6UL));
            var expectedSourceOffset = beforeTransition
                ? 0U
                : (uint)GatewayD3D12CopyLabOptions.BufferBytes;
            var compared = index != 0 &&
                index != automaticGuardIndex &&
                index != explicitGuardIndex;
            if (destination == 0 || source == 0 ||
                item.ResourceA != destination ||
                item.ResourceB != source ||
                item.SizeBytes != GatewayD3D12CopyLabOptions.BufferBytes ||
                item.Generation != expectedGeneration ||
                item.SubresourceA != 0 ||
                item.SubresourceB != expectedSourceOffset ||
                item.RegionKey != expectedSourceOffset ||
                !item.IsD3D12ImmutableUploadSource ||
                item.IsD3D12ExactContentCompared != compared ||
                item.IsD3D12RedundantCandidate == required ||
                item.WasD3D12CopySkipped != (optimized && !required))
            {
                return false;
            }
        }
        return true;
    }

    internal static GatewayD3D12CopyLabReport BuildReport(
        GatewayD3D12CopyLabOptions options,
        OwnedBinaryBinding binding,
        IReadOnlyList<D3D12CopyElisionTrialReport> trials)
    {
        var measured = trials.Where(item => item.IncludedInStatistics).ToArray();
        var warmups = trials.Where(item => !item.IncludedInStatistics).ToArray();
        if (measured.Length != options.TrialPairs ||
            warmups.Length != options.WarmupPairs ||
            measured.Length == 0 ||
            !OrderMatches(measured, "measured") ||
            !OrderMatches(warmups, "warmup") ||
            trials.Any(item =>
                !item.ContentEquivalent ||
                !item.FenceCompletedInBothRuns ||
                !item.RollbackRestoredInBothRuns ||
                !item.AdapterIdentityMatched))
        {
            throw new InvalidDataException(
                "D3D12 paired trials violated ordering or correctness gates.");
        }

        var authorizations = trials.Select(item =>
            item.Optimized.GatewayAuthorization).ToArray();
        if (authorizations.Any(item => item is null) ||
            trials.Any(item =>
                item.Baseline.GatewayAuthorization is not null ||
                item.Baseline.PublishedPolicyActionMask != 0 ||
                item.Baseline.PublishedPolicyActionBudget != 0))
        {
            throw new InvalidDataException(
                "D3D12 trials omitted or leaked Gateway policy evidence.");
        }
        var verified = authorizations.Select(item => item!).ToArray();
        for (var index = 0; index < verified.Length; ++index)
        {
            var trial = trials[index];
            var authorization = verified[index];
            authorization.EnsureMatchesNativePolicy(
                GatewayD3D12CopyLabOptions.BufferBytes,
                (ulong)options.CandidateActionCount,
                trial.PairIndex,
                trial.Phase,
                binding.TargetSha256,
                binding.HookSha256,
                GatewayUploadBackend.D3D12CopyBufferRegion);
            if (trial.Optimized.PublishedPolicyActionMask !=
                    authorization.NativeActionMask ||
                trial.Optimized.PublishedPolicyActionBudget !=
                    authorization.NativeActionBudget ||
                trial.Optimized.PublishedPolicyExpiresAtQpc <= 0)
            {
                throw new InvalidDataException(
                    "D3D12 native policy did not match Gateway authorization.");
            }
        }
        if (verified.Select(item => item.AuthorizationContextSha256)
                .Distinct(StringComparer.Ordinal).Count() != verified.Length ||
            verified.Select(item => item.PeerProcessId).Distinct().Count() != 1 ||
            verified.Select(item => item.PeerExecutableSha256).Distinct().Count() != 1 ||
            verified.Select(item => item.PeerProcessStartedAtUtc).Distinct().Count() != 1 ||
            verified.Select(item => item.AdvertisedServerVersion).Distinct().Count() != 1 ||
            verified.Any(item =>
                item.Backend != GatewayUploadBackend.D3D12CopyBufferRegion ||
                !item.PeerProcessBindingVerified ||
                item.PeerCryptographicallyAuthenticated))
        {
            throw new InvalidDataException(
                "D3D12 Gateway peer or authorization identity drifted across runs.");
        }
        if (trials.Any(item =>
            item.Baseline.ManagedEndToEndElapsedMicroseconds <= 0 ||
            item.Optimized.ManagedEndToEndElapsedMicroseconds <
                item.Optimized.GatewayAuthorization!.AuthorizationLatencyMicroseconds))
        {
            throw new InvalidDataException(
                "D3D12 end-to-end timing does not contain Gateway authorization.");
        }

        var first = trials[0].Baseline;
        var managed = Summarize(
            measured.Select(item =>
                (double)item.Baseline.ManagedEndToEndElapsedMicroseconds),
            measured.Select(item =>
                (double)item.Optimized.ManagedEndToEndElapsedMicroseconds));
        var cpu = Summarize(
            measured.Select(item => item.Baseline.CpuRecordMicroseconds),
            measured.Select(item => item.Optimized.CpuRecordMicroseconds));
        var submit = Summarize(
            measured.Select(item => item.Baseline.SubmitToFenceMicroseconds),
            measured.Select(item => item.Optimized.SubmitToFenceMicroseconds));
        var total = Summarize(
            measured.Select(item => item.Baseline.TotalWorkloadMicroseconds),
            measured.Select(item => item.Optimized.TotalWorkloadMicroseconds));
        PairedTailLatencySummary? gpu = null;
        if (measured.All(item =>
            item.Baseline.GpuWorkloadMicroseconds.HasValue &&
            item.Optimized.GpuWorkloadMicroseconds.HasValue))
        {
            gpu = Summarize(
                measured.Select(item => item.Baseline.GpuWorkloadMicroseconds!.Value),
                measured.Select(item => item.Optimized.GpuWorkloadMicroseconds!.Value));
        }
        var winsRequired = (int)Math.Ceiling(measured.Length * 0.8);
        var blockers = new List<string>();
        if (!options.UseHardware)
        {
            blockers.Add("software-adapter-not-hardware");
        }
        if (measured.Length < MinimumPairsForPerformanceClaim)
        {
            blockers.Add("insufficient-paired-samples");
        }
        AddTailBlocker(
            blockers,
            managed,
            winsRequired,
            "managed-end-to-end-improvement-not-consistent");
        AddTailBlocker(
            blockers,
            submit,
            winsRequired,
            "submit-to-fence-improvement-not-consistent");
        if (gpu is null)
        {
            blockers.Add("gpu-timestamp-evidence-incomplete");
        }
        else
        {
            AddTailBlocker(
                blockers,
                gpu,
                winsRequired,
                "gpu-timestamp-improvement-not-consistent");
        }

        var authorizationLatency = GatewayLatencyStatistics.Distribution(
            verified.Select(item => item.AuthorizationLatencyMicroseconds));
        var distinctBlockers = blockers.Distinct(StringComparer.Ordinal).ToArray();
        return new GatewayD3D12CopyLabReport(
            "fluidruntime-gateway-d3d12-copy-control-trace-v0.20.0",
            TargetOwned: true,
            CooperativeLoad: true,
            RemoteInjection: false,
            FailClosed: true,
            PhysicalTransferBytesMeasured: false,
            PolicyOrigin: "fluidgateway-live-fluidlink-v2-decisions",
            verified[0].Protocol,
            verified[0].ContractSha256,
            verified[0].AdvertisedServerName,
            verified[0].AdvertisedServerVersion,
            PeerProcessBindingVerified: true,
            PeerCryptographicallyAuthenticated: false,
            verified[0].PeerProcessId,
            verified[0].PeerExecutablePath,
            verified[0].PeerExecutableSha256,
            binding.TargetSha256,
            binding.HookSha256,
            options.TrialPairs,
            options.WarmupPairs,
            measured.Length,
            "alternating-baseline-first-by-pair-index",
            GatewayD3D12CopyLabOptions.BufferBytes,
            first.SourceSnapshotMode,
            first.SourceSnapshotBytes,
            first.UploadUnmappedAfterRegistration,
            options.CandidateActionCount,
            (ulong)options.CandidateActionCount *
                GatewayD3D12CopyLabOptions.BufferBytes,
            first.AdapterDescription,
            first.AdapterVendorId,
            first.AdapterDeviceId,
            first.AdapterLuid,
            first.Uma,
            first.CacheCoherentUma,
            first.ResourceHeapTier,
            ContentEquivalent: true,
            ImmutableSourceGuardPassed: true,
            AutomaticInvalidationGuardPassed: true,
            ExplicitInvalidationGuardPassed: true,
            FenceCompletedInAllRuns: true,
            RollbackRestoredInAllRuns: true,
            AuthorizationRunCount: verified.Length,
            GatewayRoundTripCount: verified.Sum(item => (long)item.RoundTripCount),
            FluidLinkBytesSent: verified.Sum(item => item.BytesSent),
            FluidLinkBytesReceived: verified.Sum(item => item.BytesReceived),
            authorizationLatency,
            managed,
            cpu,
            submit,
            total,
            gpu,
            winsRequired,
            "owned-d3d12-copy-buffer-region-fluidgateway-authorized-exact-content-elision",
            "paired-managed-end-to-end-submit-fence-and-gpu-timestamp-tails",
            PerformanceClaimAllowed: distinctBlockers.Length == 0,
            distinctBlockers,
            verified,
            trials);
    }

    private static bool OrderMatches(
        IReadOnlyList<D3D12CopyElisionTrialReport> trials,
        string phase) => trials.Select((item, index) =>
        item.PairIndex == index &&
        item.Phase == phase &&
        item.ExecutionOrder == (index % 2 == 0
            ? "baseline-then-optimized"
            : "optimized-then-baseline")).All(value => value);

    private static PairedTailLatencySummary Summarize(
        IEnumerable<double> baseline,
        IEnumerable<double> optimized) =>
        GatewayLatencyStatistics.SummarizePairs(
            baseline.Select(value => checked((long)Math.Ceiling(value))),
            optimized.Select(value => checked((long)Math.Ceiling(value))));

    private static void AddTailBlocker(
        ICollection<string> blockers,
        PairedTailLatencySummary summary,
        int winsRequired,
        string blocker)
    {
        if (summary.Delta.P50 >= 0 ||
            summary.Delta.P95 >= 0 ||
            summary.Delta.P99 >= 0 ||
            summary.OptimizedLowerCount < winsRequired)
        {
            blockers.Add(blocker);
        }
    }

    private static bool SameAdapter(
        D3D12CopyElisionRunReport left,
        D3D12CopyElisionRunReport right) =>
        left.RenderDriver == right.RenderDriver &&
        left.AdapterDescription == right.AdapterDescription &&
        left.AdapterVendorId == right.AdapterVendorId &&
        left.AdapterDeviceId == right.AdapterDeviceId &&
        left.AdapterLuid == right.AdapterLuid &&
        left.Uma == right.Uma &&
        left.CacheCoherentUma == right.CacheCoherentUma &&
        left.ResourceHeapTier == right.ResourceHeapTier;

    private static long ElapsedMicroseconds(long startedAt) =>
        Math.Max(
            1,
            checked((long)Math.Ceiling(
                Stopwatch.GetElapsedTime(startedAt).TotalMicroseconds)));
}
