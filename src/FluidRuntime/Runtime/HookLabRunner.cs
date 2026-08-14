using System.Diagnostics;
using System.Text.Json;
using FluidRuntime.Cli;
using FluidRuntime.Native;

namespace FluidRuntime.Runtime;

public sealed class HookLabRunner
{
    public async Task<HookLabReport> RunAsync(
        HookLabOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SkipFirstRedundantCopy && options.UseManagedControlPolicy)
        {
            throw new ArgumentException(
                "Attach-option and managed-policy copy elision cannot be combined.");
        }
        var targetPath = RequireFile(options.TargetPath, "Hook target executable");
        var hookPath = RequireFile(options.HookPath, "Hook DLL");

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
        startInfo.ArgumentList.Add(options.FrameCount.ToString());
        startInfo.ArgumentList.Add("--hold-ms");
        startInfo.ArgumentList.Add(options.HoldMs.ToString());
        startInfo.ArgumentList.Add("--gpu-timeout-ms");
        startInfo.ArgumentList.Add(options.GpuTimeoutMs.ToString());
        if (options.UseHardware)
        {
            startInfo.ArgumentList.Add("--hardware");
        }
        if (options.SkipFirstRedundantCopy)
        {
            startInfo.ArgumentList.Add("--skip-first-redundant-copy");
        }
        if (options.UseManagedControlPolicy)
        {
            startInfo.ArgumentList.Add("--managed-control");
            startInfo.ArgumentList.Add("--control-timeout-ms");
            startInfo.ArgumentList.Add("5000");
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the hook lab target.");
        await using var processGuard = new OwnedProcessGuard(process);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var reader = await OpenRingAsync(process, cancellationToken);
        if (reader.ProcessId != (ulong)process.Id)
        {
            throw new InvalidDataException("Hook ring process identity did not match the target.");
        }
        HookControlPolicy? publishedControlPolicy = null;
        if (options.UseManagedControlPolicy)
        {
            publishedControlPolicy = reader.PublishCopyElisionPolicy(
                TimeSpan.FromSeconds(3));
            await reader.WaitForControlAcknowledgmentAsync(
                publishedControlPolicy.Epoch,
                TimeSpan.FromSeconds(5),
                cancellationToken);
        }
        var events = new List<HookIpcEvent>();
        while (!process.HasExited)
        {
            events.AddRange(reader.ReadAvailable());
            await Task.Delay(10, cancellationToken);
        }

        await process.WaitForExitAsync(cancellationToken);
        events.AddRange(reader.ReadAvailable());
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Hook lab target exited with code {process.ExitCode}: " +
                $"{stderr.Trim()} {stdout.Trim()}");
        }

        using var targetDocument = JsonDocument.Parse(stdout);
        var targetReport = targetDocument.RootElement.Clone();
        ValidateTargetReport(targetReport, options);
        var controlSnapshot = reader.ControlSnapshot;

        var eventTypeCounts = events
            .GroupBy(item => item.Type)
            .ToDictionary(group => group.Key.ToString(), group => group.LongCount());
        var lifecycle = ResourceLifecycleValidator.Validate(events);
        var copyEvents = events
            .Where(item => item.Type == HookEventType.CopyResource)
            .ToArray();
        var redundantCopyEvents = copyEvents
            .Where(item => item.IsRedundantCopyCandidate)
            .ToArray();
        var skippedCopyEvents = copyEvents
            .Where(item => item.WasCopySkipped)
            .ToArray();
        var subresourceCopyEvents = events
            .Where(item => item.Type == HookEventType.CopySubresourceRegion)
            .ToArray();
        var redundantSubresourceCopyEvents = subresourceCopyEvents
            .Where(item => item.IsRedundantSubresourceCopyCandidate)
            .ToArray();
        var gpuViewWriteEvents = events
            .Where(item => item.Type is HookEventType.ClearRenderTargetView or
                HookEventType.ClearUnorderedAccessViewFloat)
            .ToArray();
        var controlPolicyEvents = events
            .Where(item => item.Type == HookEventType.ControlPolicyAccepted)
            .ToArray();
        var copyBytes = copyEvents.Aggregate(0UL, (total, item) => total + item.SizeBytes);
        var redundantBytes = redundantCopyEvents.Aggregate(
            0UL,
            (total, item) => total + item.SizeBytes);
        var subresourceCopyBytes = subresourceCopyEvents.Aggregate(
            0UL,
            (total, item) => total + item.SizeBytes);
        var redundantSubresourceCopyBytes = redundantSubresourceCopyEvents.Aggregate(
            0UL,
            (total, item) => total + item.SizeBytes);
        var gpuViewWriteBytes = gpuViewWriteEvents.Aggregate(
            0UL,
            (total, item) => total + item.SizeBytes);

        ValidateEventAgreement(
            targetReport,
            events,
            copyEvents,
            redundantCopyEvents,
            skippedCopyEvents,
            subresourceCopyEvents,
            redundantSubresourceCopyEvents,
            gpuViewWriteEvents,
            controlPolicyEvents,
            copyBytes,
            redundantBytes,
            subresourceCopyBytes,
            redundantSubresourceCopyBytes,
            gpuViewWriteBytes,
            reader,
            lifecycle,
            options);

        var resources = targetReport.GetProperty("resources");
        var timing = targetReport.GetProperty("timing");
        var adapter = targetReport.GetProperty("adapter");
        var gpuTimingValid = timing.GetProperty("gpu_timing_valid").GetBoolean();
        var gpuFrequency = timing.GetProperty("gpu_frequency").GetUInt64();
        var gpuWorkloadTicks = timing.GetProperty("gpu_workload_ticks").GetUInt64();

        return new HookLabReport(
            "fluidruntime-hook-ipc-lab-v0.12.0",
            ReadOnly: !options.SkipFirstRedundantCopy && !options.UseManagedControlPolicy,
            WouldModifySystem: false,
            CopyElisionEnabled:
                options.SkipFirstRedundantCopy || options.UseManagedControlPolicy,
            AutomaticLifetimeTracking:
                targetReport.GetProperty("automatic_lifetime_tracking").GetBoolean(),
            ReleaseObservationScope:
                targetReport.GetProperty("release_observation_scope").GetString() ?? string.Empty,
            RenderDriver: targetReport.GetProperty("render_driver").GetString() ?? string.Empty,
            AdapterIdentityAvailable: adapter.GetProperty("available").GetBoolean(),
            AdapterDescription: adapter.GetProperty("description").GetString() ?? string.Empty,
            AdapterVendorId: adapter.GetProperty("vendor_id").GetUInt32(),
            AdapterDeviceId: adapter.GetProperty("device_id").GetUInt32(),
            AdapterDedicatedVideoMemory:
                adapter.GetProperty("dedicated_video_memory").GetUInt64(),
            AdapterSharedSystemMemory:
                adapter.GetProperty("shared_system_memory").GetUInt64(),
            AdapterLuid: adapter.GetProperty("luid").GetString() ?? string.Empty,
            TargetProcessId: process.Id,
            RingName: reader.MappingName,
            RingAbiVersion: reader.AbiVersion,
            QpcFrequency: reader.QpcFrequency,
            EventCount: events.Count,
            LostSequenceCount: reader.LostSequenceCount,
            NativeOverrunCount: reader.NativeOverrunCount,
            EventTypeCounts: eventTypeCounts,
            ResourceRetireCount: resources.GetProperty("resource_retire_count").GetInt64(),
            ResourceDestroyCount: resources.GetProperty("resource_destroy_count").GetInt64(),
            ResourceReuseCount: resources.GetProperty("resource_reuse_count").GetInt64(),
            ActiveResourceCount: lifecycle.ActiveResourceIds.Count,
            RetiredResourceIdCount: lifecycle.RetiredResourceIds.Count,
            RetiredResourceIdentityCount:
                resources.GetProperty("retired_resource_identity_count").GetInt64(),
            ProvenanceFailureCount:
                resources.GetProperty("provenance_failure_count").GetInt64(),
            ReleaseHookSlotCount:
                resources.GetProperty("release_hook_slot_count").GetInt64(),
            ReleaseHookFailureCount:
                resources.GetProperty("release_hook_failure_count").GetInt64(),
            CopyResourceBytes: copyBytes,
            RedundantCopyCandidateCount: redundantCopyEvents.LongLength,
            RedundantCopyBytes: redundantBytes,
            AvoidableCopySharePercent: copyBytes == 0
                ? 0
                : Math.Round(redundantBytes * 100d / copyBytes, 2),
            ForwardedCopyCount: resources.GetProperty("forwarded_copy_count").GetInt64(),
            ForwardedCopyBytes:
                resources.GetProperty("forwarded_copy_bytes_estimated").GetUInt64(),
            SkippedCopyCount: resources.GetProperty("skipped_copy_count").GetInt64(),
            SkippedCopyBytes:
                resources.GetProperty("skipped_copy_bytes_estimated").GetUInt64(),
            ContentEquivalent:
                targetReport.GetProperty("buffer_contents_equal").GetBoolean() &&
                targetReport.GetProperty("texture_contents_equal").GetBoolean() &&
                targetReport.GetProperty("subresource_contents_equal").GetBoolean(),
            RollbackRestored:
                targetReport.GetProperty("original_pointer_restored").GetBoolean(),
            DestinationBufferHash:
                targetReport.GetProperty("destination_buffer_hash").GetString() ?? string.Empty,
            DestinationTextureHash:
                targetReport.GetProperty("destination_texture_hash").GetString() ?? string.Empty,
            QpcFrequencyFromTarget: timing.GetProperty("qpc_frequency").GetUInt64(),
            WorkloadQpcTicks: timing.GetProperty("workload_qpc_ticks").GetUInt64(),
            GpuTimingSupported: timing.GetProperty("gpu_timing_supported").GetBoolean(),
            GpuTimingValid: gpuTimingValid,
            GpuTimingStatus: GpuTimingStatus(timing),
            GpuTimingDisjoint: timing.GetProperty("gpu_timing_disjoint").GetBoolean(),
            GpuQueryTimedOut: timing.GetProperty("gpu_query_timed_out").GetBoolean(),
            GpuFrequency: gpuFrequency,
            GpuWorkloadTicks: gpuWorkloadTicks,
            GpuWorkloadMicroseconds: gpuTimingValid
                ? Math.Round(gpuWorkloadTicks * 1_000_000d / gpuFrequency, 3)
                : null,
            TargetReport: targetReport,
            SubresourceProvenanceScope:
                targetReport.GetProperty("subresource_provenance_scope").GetString() ??
                    string.Empty,
            CopySubresourceRegionCount: subresourceCopyEvents.LongLength,
            CopySubresourceRegionBytes: subresourceCopyBytes,
            RedundantSubresourceCopyCandidateCount:
                redundantSubresourceCopyEvents.LongLength,
            RedundantSubresourceCopyBytes: redundantSubresourceCopyBytes,
            SubresourceContentEquivalent:
                targetReport.GetProperty("subresource_contents_equal").GetBoolean(),
            SourceSubresourceHash:
                targetReport.GetProperty("source_subresource_hash").GetString() ?? string.Empty,
            DestinationSubresourceHash:
                targetReport.GetProperty("destination_subresource_hash").GetString() ??
                    string.Empty,
            GpuViewWriteScope:
                targetReport.GetProperty("gpu_view_write_scope").GetString() ?? string.Empty,
            ClearRenderTargetViewCount:
                gpuViewWriteEvents.LongCount(item =>
                    item.Type == HookEventType.ClearRenderTargetView),
            ClearUnorderedAccessViewFloatCount:
                gpuViewWriteEvents.LongCount(item =>
                    item.Type == HookEventType.ClearUnorderedAccessViewFloat),
            GpuViewWriteBytes: gpuViewWriteBytes,
            ManagedControlPolicyEnabled: options.UseManagedControlPolicy,
            ControlPlane:
                targetReport.GetProperty("control_plane").GetString() ?? string.Empty,
            ControlPolicyPublishedEpoch:
                publishedControlPolicy?.Epoch ?? controlSnapshot.PublishedEpoch,
            ControlPolicyAcknowledgedEpoch: controlSnapshot.AcknowledgedEpoch,
            ControlPolicyAppliedActionCount: controlSnapshot.AppliedActionCount,
            ControlPolicyRejectedCount:
                resources.GetProperty("control_policy_rejected_count").GetInt64(),
            ControlPolicyStatus: controlSnapshot.Status.ToString().ToLowerInvariant(),
            ModulePinnedUntilProcessExit:
                targetReport.GetProperty("module_pinned_until_process_exit").GetBoolean());
    }

    private static string RequireFile(string path, string label)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"{label} was not found.", fullPath);
        }
        return fullPath;
    }

    internal static async Task<HookRingReader> OpenRingAsync(
        Process process,
        CancellationToken cancellationToken,
        int? transferBackendId = null)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        while (!process.HasExited && Stopwatch.GetTimestamp() < deadline)
        {
            try
            {
                return transferBackendId is not null
                    ? HookRingReader.OpenTransferForProcess(
                        process.Id,
                        transferBackendId.Value)
                    : HookRingReader.OpenForProcess(process.Id);
            }
            catch (Exception exception)
                when (exception is FileNotFoundException or InvalidDataException)
            {
                await Task.Delay(10, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"Native {(transferBackendId is not null ? "transfer " : string.Empty)}hook ring for PID " +
            $"{process.Id} did not become available.");
    }

    private sealed class OwnedProcessGuard(Process process) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await OwnedProcessLifetime.TerminateAsync(process);
        }
    }

    private static void ValidateTargetReport(JsonElement report, HookLabOptions options)
    {
        var copyElisionEnabled =
            options.SkipFirstRedundantCopy || options.UseManagedControlPolicy;
        var timing = report.GetProperty("timing");
        var resources = report.GetProperty("resources");
        var gpuTimingSupported = timing.GetProperty("gpu_timing_supported").GetBoolean();
        var gpuTimingValid = timing.GetProperty("gpu_timing_valid").GetBoolean();
        var gpuTimingDisjoint = timing.GetProperty("gpu_timing_disjoint").GetBoolean();
        var gpuQueryTimedOut = timing.GetProperty("gpu_query_timed_out").GetBoolean();
        var gpuFrequency = timing.GetProperty("gpu_frequency").GetUInt64();
        if (report.GetProperty("mode").GetString() !=
                "fluidruntime-resource-hook-lab-v0.12.0" ||
            !report.GetProperty("automatic_lifetime_tracking").GetBoolean() ||
            report.GetProperty("release_observation_scope").GetString() !=
                "owned-returned-buffer-texture-interface" ||
            report.GetProperty("subresource_provenance_scope").GetString() !=
                "owned-buffer-texture2d-map-update-copy-region" ||
            report.GetProperty("gpu_view_write_scope").GetString() !=
                "owned-texture2d-single-subresource-rtv-uav-clear" ||
            report.GetProperty("read_only_hook").GetBoolean() == copyElisionEnabled ||
            report.GetProperty("would_modify_frame_data").GetBoolean() ||
            report.GetProperty("would_skip_copies").GetBoolean() != copyElisionEnabled ||
            report.GetProperty("optimization_requested").GetBoolean() != copyElisionEnabled ||
            report.GetProperty("control_policy_requested").GetBoolean() !=
                options.UseManagedControlPolicy ||
            !report.GetProperty("module_pinned_until_process_exit").GetBoolean() ||
            report.GetProperty("control_plane").GetString() !=
                (options.UseManagedControlPolicy
                    ? "managed-shared-memory-policy-v1"
                    : (options.SkipFirstRedundantCopy
                        ? "immutable-attach-options"
                        : "observe-only")) ||
            report.GetProperty("control_policy_wait_hresult").GetString() !=
                (options.UseManagedControlPolicy ? "0x00000000" : "0x00000001") ||
            resources.GetProperty("control_policy_enabled").GetInt64() !=
                (options.UseManagedControlPolicy ? 1 : 0) ||
            resources.GetProperty("control_policy_epoch").GetInt64() !=
                (options.UseManagedControlPolicy ? 1 : 0) ||
            resources.GetProperty("control_policy_acknowledged_epoch").GetInt64() !=
                (options.UseManagedControlPolicy ? 1 : 0) ||
            resources.GetProperty("control_policy_applied_action_count").GetInt64() !=
                (options.UseManagedControlPolicy ? 1 : 0) ||
            resources.GetProperty("control_policy_rejected_count").GetInt64() != 0 ||
            resources.GetProperty("control_policy_status").GetInt64() !=
                (options.UseManagedControlPolicy
                    ? (long)HookControlPolicyStatus.Exhausted
                    : (long)HookControlPolicyStatus.None) ||
            !report.GetProperty("resource_metrics_matched").GetBoolean() ||
            !report.GetProperty("original_pointer_restored").GetBoolean() ||
            !report.GetProperty("content_readback_succeeded").GetBoolean() ||
            !report.GetProperty("buffer_contents_equal").GetBoolean() ||
            !report.GetProperty("texture_contents_equal").GetBoolean() ||
            !report.GetProperty("subresource_contents_equal").GetBoolean() ||
            !report.GetProperty("context_subresource_copy_entry_stable").GetBoolean() ||
            !report.GetProperty("context_gpu_view_write_entries_stable").GetBoolean() ||
            report.GetProperty("refresh_hresult").GetString() != "0x00000000" ||
            (gpuTimingValid &&
                (!gpuTimingSupported || gpuTimingDisjoint || gpuQueryTimedOut || gpuFrequency == 0)))
        {
            throw new InvalidDataException("Hook target report violated the read-only lab contract.");
        }
    }

    private static void ValidateEventAgreement(
        JsonElement targetReport,
        IReadOnlyList<HookIpcEvent> events,
        IReadOnlyCollection<HookIpcEvent> copyEvents,
        IReadOnlyCollection<HookIpcEvent> redundantCopyEvents,
        IReadOnlyCollection<HookIpcEvent> skippedCopyEvents,
        IReadOnlyCollection<HookIpcEvent> subresourceCopyEvents,
        IReadOnlyCollection<HookIpcEvent> redundantSubresourceCopyEvents,
        IReadOnlyCollection<HookIpcEvent> gpuViewWriteEvents,
        IReadOnlyCollection<HookIpcEvent> controlPolicyEvents,
        ulong copyBytes,
        ulong redundantBytes,
        ulong subresourceCopyBytes,
        ulong redundantSubresourceCopyBytes,
        ulong gpuViewWriteBytes,
        HookRingReader reader,
        ResourceLifecycleValidationResult lifecycle,
        HookLabOptions options)
    {
        var copyElisionEnabled =
            options.SkipFirstRedundantCopy || options.UseManagedControlPolicy;
        var resources = targetReport.GetProperty("resources");
        var expectedEventCount = resources.GetProperty("ipc_event_count").GetInt64();
        var sequencesMatch = events.Select((item, index) => item.Sequence == index).All(value => value);
        var knownEventTypes = events.All(item => Enum.IsDefined(item.Type));
        var workloadMatches = MatchesDeterministicWorkload(
            events,
            targetReport.GetProperty("observed_presents").GetInt64(),
            copyElisionEnabled,
            options.UseManagedControlPolicy);
        var expectedSkippedCount = copyElisionEnabled ? 1 : 0;
        var expectedSkippedBytes = copyElisionEnabled ? 4096UL : 0UL;
        var eventTypesMatch =
            CountEvents(events, HookEventType.Present) ==
                targetReport.GetProperty("observed_presents").GetInt64() &&
            CountEvents(events, HookEventType.CreateBuffer) ==
                resources.GetProperty("create_buffer_count").GetInt64() &&
            CountEvents(events, HookEventType.CreateTexture2D) ==
                resources.GetProperty("create_texture2d_count").GetInt64() &&
            CountEvents(events, HookEventType.MapWrite) ==
                resources.GetProperty("map_write_count").GetInt64() &&
            CountEvents(events, HookEventType.UnmapWrite) ==
                resources.GetProperty("unmap_write_count").GetInt64() &&
            CountEvents(events, HookEventType.UpdateSubresource) ==
                resources.GetProperty("update_subresource_count").GetInt64() &&
            CountEvents(events, HookEventType.CopyResource) ==
                resources.GetProperty("copy_resource_count").GetInt64() &&
            CountEvents(events, HookEventType.CopySubresourceRegion) ==
                resources.GetProperty("copy_subresource_region_count").GetInt64() &&
            CountEvents(events, HookEventType.ClearRenderTargetView) ==
                resources.GetProperty("clear_render_target_view_count").GetInt64() &&
            CountEvents(events, HookEventType.ClearUnorderedAccessViewFloat) ==
                resources.GetProperty("clear_unordered_access_view_float_count").GetInt64() &&
            CountEvents(events, HookEventType.ControlPolicyAccepted) ==
                (options.UseManagedControlPolicy ? 1 : 0) &&
            CountEvents(events, HookEventType.HookRefresh) ==
                resources.GetProperty("hook_refresh_count").GetInt64() &&
            CountEvents(events, HookEventType.ResourceRetire) ==
                resources.GetProperty("resource_retire_count").GetInt64() &&
            CountEvents(events, HookEventType.ResourceDestroy) ==
                resources.GetProperty("resource_destroy_count").GetInt64() &&
            CountEvents(events, HookEventType.ResourceReuse) ==
                resources.GetProperty("resource_reuse_count").GetInt64();
        var completedLifecycleCount =
            resources.GetProperty("resource_retire_count").GetInt64() +
            resources.GetProperty("resource_destroy_count").GetInt64();
        var controlSnapshot = reader.ControlSnapshot;
        if (events.Count != expectedEventCount ||
            !sequencesMatch ||
            !knownEventTypes ||
            !workloadMatches ||
            !eventTypesMatch ||
            !lifecycle.IsValid ||
            lifecycle.ActiveResourceIds.Count !=
                resources.GetProperty("tracked_resource_count").GetInt64() ||
            lifecycle.RetiredResourceIds.Count != completedLifecycleCount ||
            resources.GetProperty("provenance_failure_count").GetInt64() != 0 ||
            resources.GetProperty("release_hook_slot_count").GetInt64() < 2 ||
            resources.GetProperty("release_hook_failure_count").GetInt64() != 0 ||
            resources.GetProperty("retired_resource_identity_count").GetInt64() +
                resources.GetProperty("resource_reuse_count").GetInt64() !=
                completedLifecycleCount ||
            events.Any(item =>
                item.Type == HookEventType.ResourceReuse && item.Flags != 0) ||
            copyEvents.Count != resources.GetProperty("copy_resource_count").GetInt64() ||
            copyBytes != resources.GetProperty("copy_resource_bytes_estimated").GetUInt64() ||
            redundantCopyEvents.Count !=
                resources.GetProperty("redundant_copy_candidate_count").GetInt64() ||
            redundantBytes !=
                resources.GetProperty("redundant_copy_bytes_estimated").GetUInt64() ||
            subresourceCopyEvents.Count !=
                resources.GetProperty("copy_subresource_region_count").GetInt64() ||
            subresourceCopyBytes !=
                resources.GetProperty("copy_subresource_region_bytes_estimated").GetUInt64() ||
            redundantSubresourceCopyEvents.Count !=
                resources.GetProperty("redundant_subresource_copy_candidate_count").GetInt64() ||
            redundantSubresourceCopyBytes !=
                resources.GetProperty("redundant_subresource_copy_bytes_estimated").GetUInt64() ||
            gpuViewWriteEvents.Any(item => !item.IsPreciseSubresourceWrite) ||
            gpuViewWriteBytes !=
                resources.GetProperty("gpu_view_write_bytes_estimated").GetUInt64() ||
            controlPolicyEvents.Count != (options.UseManagedControlPolicy ? 1 : 0) ||
            controlSnapshot.PublishedEpoch != (options.UseManagedControlPolicy ? 1 : 0) ||
            controlSnapshot.AcknowledgedEpoch != (options.UseManagedControlPolicy ? 1 : 0) ||
            controlSnapshot.AppliedActionCount != (options.UseManagedControlPolicy ? 1 : 0) ||
            controlSnapshot.Status != (options.UseManagedControlPolicy
                ? HookControlPolicyStatus.Exhausted
                : HookControlPolicyStatus.None) ||
            skippedCopyEvents.Count != expectedSkippedCount ||
            skippedCopyEvents.Aggregate(0UL, (total, item) => total + item.SizeBytes) !=
                expectedSkippedBytes ||
            resources.GetProperty("skipped_copy_count").GetInt64() != expectedSkippedCount ||
            resources.GetProperty("skipped_copy_bytes_estimated").GetUInt64() !=
                expectedSkippedBytes ||
            resources.GetProperty("forwarded_copy_count").GetInt64() !=
                copyEvents.Count - expectedSkippedCount ||
            resources.GetProperty("forwarded_copy_bytes_estimated").GetUInt64() !=
                copyBytes - expectedSkippedBytes ||
            reader.LostSequenceCount != 0 ||
            reader.NativeOverrunCount != 0)
        {
            throw new InvalidDataException(
                "Managed hook events did not match the native snapshot without loss.");
        }
    }

    private static long CountEvents(
        IEnumerable<HookIpcEvent> events,
        HookEventType eventType) =>
        events.LongCount(item => item.Type == eventType);

    private static string GpuTimingStatus(JsonElement timing)
    {
        if (timing.GetProperty("gpu_timing_valid").GetBoolean())
        {
            return "valid";
        }
        if (!timing.GetProperty("gpu_timing_supported").GetBoolean())
        {
            return "unsupported";
        }
        if (timing.GetProperty("gpu_query_timed_out").GetBoolean())
        {
            return "timeout";
        }
        if (timing.GetProperty("gpu_timing_disjoint").GetBoolean())
        {
            return "disjoint";
        }
        return timing.GetProperty("gpu_frequency").GetUInt64() == 0
            ? "frequency-zero"
            : "unavailable";
    }

    internal static bool MatchesDeterministicWorkload(
        IReadOnlyList<HookIpcEvent> events,
        long expectedPresentCount,
        bool copyElisionEnabled = false,
        bool managedControlPolicy = false)
    {
        if (expectedPresentCount < 0 ||
            events.Any(item => item.QpcTicks <= 0 || item.ThreadId == 0))
        {
            return false;
        }

        var expectedCoreEvents = new[]
        {
            (HookEventType.CreateBuffer, 1UL, 0UL, 4096UL, 1UL, 0U, 0U, 0U),
            (HookEventType.CreateBuffer, 2UL, 0UL, 4096UL, 0UL, 0U, 0U, 0U),
            (HookEventType.CreateBuffer, 3UL, 0UL, 4096UL, 0UL, 0U, 0U, 0U),
            (HookEventType.MapWrite, 3UL, 0UL, 4096UL, 0UL, 4U, 0U, 0U),
            (HookEventType.UnmapWrite, 3UL, 0UL, 4096UL, 1UL, 0U, 0U, 0U),
            (HookEventType.CopyResource, 2UL, 1UL, 4096UL, 1UL, 0U, 0U, 0U),
            (HookEventType.CopyResource, 2UL, 1UL, 4096UL,
                copyElisionEnabled ? 1UL : 2UL,
                copyElisionEnabled ? 3U : 1U, 0U, 0U),
            (HookEventType.UpdateSubresource, 1UL, 0UL, 4096UL, 2UL, 0U, 0U, 0U),
            (HookEventType.CopyResource, 2UL, 1UL, 4096UL,
                copyElisionEnabled ? 2UL : 3UL, 0U, 0U, 0U),
            (HookEventType.CopyResource, 2UL, 1UL, 4096UL,
                copyElisionEnabled ? 3UL : 4UL, 1U, 0U, 0U),
            (HookEventType.CreateTexture2D, 4UL, 0UL, 16384UL, 1UL, 0U, 0U, 0U),
            (HookEventType.CreateTexture2D, 5UL, 0UL, 16384UL, 0UL, 0U, 0U, 0U),
            (HookEventType.CopyResource, 5UL, 4UL, 16384UL, 1UL, 0U, 0U, 0U),
            (HookEventType.CopyResource, 5UL, 4UL, 16384UL, 2UL, 1U, 0U, 0U),
            (HookEventType.CreateTexture2D, 6UL, 0UL, 5120UL, 1UL, 0U, 0U, 0U),
            (HookEventType.CreateTexture2D, 7UL, 0UL, 5120UL, 0UL, 0U, 0U, 0U),
            (HookEventType.CopySubresourceRegion, 7UL, 6UL, 1024UL, 1UL, 0U, 1U, 1U),
            (HookEventType.CopySubresourceRegion, 7UL, 6UL, 0UL, 1UL, 0U, 1U, 1U),
            (HookEventType.CopySubresourceRegion, 7UL, 6UL, 1024UL, 2UL, 1U, 1U, 1U),
            (HookEventType.UpdateSubresource, 6UL, 0UL, 4096UL, 2UL, 0U, 0U, 0U),
            (HookEventType.CopySubresourceRegion, 7UL, 6UL, 1024UL, 3UL, 1U, 1U, 1U),
            (HookEventType.ClearRenderTargetView, 6UL, 0UL, 4096UL, 3UL, 8U, 0U, 0U),
            (HookEventType.CopySubresourceRegion, 7UL, 6UL, 1024UL, 4UL, 1U, 1U, 1U),
            (HookEventType.UpdateSubresource, 6UL, 0UL, 1024UL, 4UL, 0U, 1U, 0U),
            (HookEventType.CopySubresourceRegion, 7UL, 6UL, 256UL, 5UL, 0U, 1U, 1U),
            (HookEventType.CopySubresourceRegion, 7UL, 6UL, 256UL, 6UL, 0U, 1U, 1U),
            (HookEventType.CopySubresourceRegion, 7UL, 6UL, 1024UL, 7UL, 0U, 1U, 1U),
            (HookEventType.CopySubresourceRegion, 7UL, 6UL, 1024UL, 8UL, 1U, 1U, 1U),
            (HookEventType.ClearUnorderedAccessViewFloat, 6UL, 0UL, 1024UL, 5UL, 8U, 1U, 0U),
            (HookEventType.CopySubresourceRegion, 7UL, 6UL, 1024UL, 9UL, 0U, 1U, 1U),
            (HookEventType.CopySubresourceRegion, 7UL, 6UL, 1024UL, 10UL, 1U, 1U, 1U)
        };
        var nonRefreshEvents = events
            .Where(item => item.Type != HookEventType.HookRefresh)
            .ToArray();
        var coreStart = 0;
        if (managedControlPolicy)
        {
            if (nonRefreshEvents.Length == 0)
            {
                return false;
            }
            var control = nonRefreshEvents[0];
            if (control.Type != HookEventType.ControlPolicyAccepted ||
                control.ResourceA != 1 ||
                control.ResourceB != HookRingReader.SkipRedundantCopyResourceAction ||
                control.SizeBytes != 1 ||
                control.Generation <= (ulong)control.QpcTicks ||
                control.Flags != 0 ||
                control.SubresourceA != 0 ||
                control.SubresourceB != 0 ||
                control.RegionKey != 0)
            {
                return false;
            }
            coreStart = 1;
        }
        else if (nonRefreshEvents.Any(item =>
            item.Type == HookEventType.ControlPolicyAccepted))
        {
            return false;
        }
        const int automaticLifetimeCycles = 64;
        if (nonRefreshEvents.LongLength <
            coreStart + expectedCoreEvents.LongLength + 2 +
                automaticLifetimeCycles * 2 + expectedPresentCount)
        {
            return false;
        }

        for (var index = 0; index < expectedCoreEvents.Length; ++index)
        {
            var actual = nonRefreshEvents[coreStart + index];
            var expected = expectedCoreEvents[index];
            if (actual.Type != expected.Item1 ||
                actual.ResourceA != expected.Item2 ||
                actual.ResourceB != expected.Item3 ||
                actual.SizeBytes != expected.Item4 ||
                actual.Generation != expected.Item5 ||
                actual.Flags != expected.Item6 ||
                actual.SubresourceA != expected.Item7 ||
                actual.SubresourceB != expected.Item8 ||
                (actual.Type != HookEventType.CopySubresourceRegion &&
                    actual.RegionKey != 0))
            {
                return false;
            }
        }

        var fullRegionKey = nonRefreshEvents[coreStart + 16].RegionKey;
        var emptyRegionKey = nonRefreshEvents[coreStart + 17].RegionKey;
        var firstPartialRegionKey = nonRefreshEvents[coreStart + 24].RegionKey;
        var secondPartialRegionKey = nonRefreshEvents[coreStart + 25].RegionKey;
        if (fullRegionKey == 0 ||
            emptyRegionKey == 0 || emptyRegionKey == fullRegionKey ||
            nonRefreshEvents[coreStart + 18].RegionKey != fullRegionKey ||
            nonRefreshEvents[coreStart + 20].RegionKey != fullRegionKey ||
            nonRefreshEvents[coreStart + 22].RegionKey != fullRegionKey ||
            firstPartialRegionKey == 0 || firstPartialRegionKey == fullRegionKey ||
            secondPartialRegionKey == 0 ||
            secondPartialRegionKey == fullRegionKey ||
            secondPartialRegionKey == firstPartialRegionKey ||
            nonRefreshEvents[coreStart + 26].RegionKey != fullRegionKey ||
            nonRefreshEvents[coreStart + 27].RegionKey != fullRegionKey ||
            nonRefreshEvents[coreStart + 29].RegionKey != fullRegionKey ||
            nonRefreshEvents[coreStart + 30].RegionKey != fullRegionKey)
        {
            return false;
        }

        var cursor = coreStart + expectedCoreEvents.Length;
        var cooperativeCreate = nonRefreshEvents[cursor++];
        var cooperativeRetire = nonRefreshEvents[cursor++];
        if (cooperativeCreate.Type != HookEventType.CreateBuffer ||
            cooperativeCreate.ResourceA != 8 ||
            cooperativeCreate.ResourceB != 0 ||
            cooperativeCreate.SizeBytes != 256 ||
            cooperativeCreate.Generation != 0 ||
            cooperativeCreate.Flags != 0 ||
            cooperativeCreate.SubresourceA != 0 ||
            cooperativeCreate.SubresourceB != 0 ||
            cooperativeCreate.RegionKey != 0 ||
            cooperativeRetire.Type != HookEventType.ResourceRetire ||
            cooperativeRetire.ResourceA != 8 ||
            cooperativeRetire.ResourceB != 0 ||
            cooperativeRetire.SizeBytes != 256 ||
            cooperativeRetire.Generation != 0 ||
            cooperativeRetire.Flags != 0 ||
            cooperativeRetire.SubresourceA != 0 ||
            cooperativeRetire.SubresourceB != 0 ||
            cooperativeRetire.RegionKey != 0)
        {
            return false;
        }

        var retiredIds = new HashSet<ulong> { 8 };
        for (var cycle = 0; cycle < automaticLifetimeCycles; ++cycle)
        {
            var expectedResourceId = (ulong)(9 + cycle);
            if (cursor >= nonRefreshEvents.Length)
            {
                return false;
            }
            var create = nonRefreshEvents[cursor++];
            if (create.Type != HookEventType.CreateBuffer ||
                create.ResourceA != expectedResourceId ||
                create.ResourceB != 0 ||
                create.SizeBytes != 512 ||
                create.Generation != 0 ||
                create.Flags != 0 ||
                create.SubresourceA != 0 ||
                create.SubresourceB != 0 ||
                create.RegionKey != 0)
            {
                return false;
            }

            if (cursor < nonRefreshEvents.Length &&
                nonRefreshEvents[cursor].Type == HookEventType.ResourceReuse)
            {
                var reuse = nonRefreshEvents[cursor++];
                if (!retiredIds.Contains(reuse.ResourceA) ||
                    reuse.ResourceB != expectedResourceId ||
                    reuse.SizeBytes != 512 ||
                    reuse.Generation != 0 ||
                    reuse.Flags != 0 ||
                    reuse.SubresourceA != 0 ||
                    reuse.SubresourceB != 0 ||
                    reuse.RegionKey != 0)
                {
                    return false;
                }
            }

            if (cursor >= nonRefreshEvents.Length)
            {
                return false;
            }
            var destroy = nonRefreshEvents[cursor++];
            if (destroy.Type != HookEventType.ResourceDestroy ||
                destroy.ResourceA != expectedResourceId ||
                destroy.ResourceB != 0 ||
                destroy.SizeBytes != 512 ||
                destroy.Generation != 0 ||
                destroy.Flags != 0 ||
                destroy.SubresourceA != 0 ||
                destroy.SubresourceB != 0 ||
                destroy.RegionKey != 0)
            {
                return false;
            }
            retiredIds.Add(expectedResourceId);
        }

        if (
            nonRefreshEvents.LongLength != cursor + expectedPresentCount)
        {
            return false;
        }

        for (var index = 0L; index < expectedPresentCount; ++index)
        {
            var actual = nonRefreshEvents[cursor + (int)index];
            if (actual.Type != HookEventType.Present ||
                actual.ResourceA != 0 ||
                actual.ResourceB != 0 ||
                actual.SizeBytes != 0 ||
                actual.Generation != (ulong)index + 1 ||
                actual.Flags != 0 ||
                actual.SubresourceA != 0 ||
                actual.SubresourceB != 0 ||
                actual.RegionKey != 0)
            {
                return false;
            }
        }

        var refreshEvents = events
            .Where(item => item.Type == HookEventType.HookRefresh)
            .ToArray();
        for (var index = 0; index < refreshEvents.Length; ++index)
        {
            var actual = refreshEvents[index];
            if (actual.ResourceA is < 3 or > 9 ||
                actual.ResourceB != 0 ||
                actual.SizeBytes != 0 ||
                actual.Generation != (ulong)index + 1 ||
                actual.Flags != 0 ||
                actual.SubresourceA != 0 ||
                actual.SubresourceB != 0 ||
                actual.RegionKey != 0)
            {
                return false;
            }
        }
        return true;
    }
}
