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

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the hook lab target.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var reader = await OpenRingAsync(process, cancellationToken);
        if (reader.ProcessId != (ulong)process.Id)
        {
            throw new InvalidDataException("Hook ring process identity did not match the target.");
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
        ValidateTargetReport(targetReport, options.SkipFirstRedundantCopy);

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
        var copyBytes = copyEvents.Aggregate(0UL, (total, item) => total + item.SizeBytes);
        var redundantBytes = redundantCopyEvents.Aggregate(
            0UL,
            (total, item) => total + item.SizeBytes);

        ValidateEventAgreement(
            targetReport,
            events,
            copyEvents,
            redundantCopyEvents,
            skippedCopyEvents,
            copyBytes,
            redundantBytes,
            reader,
            lifecycle,
            options.SkipFirstRedundantCopy);

        var resources = targetReport.GetProperty("resources");
        var timing = targetReport.GetProperty("timing");
        var adapter = targetReport.GetProperty("adapter");
        var gpuTimingValid = timing.GetProperty("gpu_timing_valid").GetBoolean();
        var gpuFrequency = timing.GetProperty("gpu_frequency").GetUInt64();
        var gpuWorkloadTicks = timing.GetProperty("gpu_workload_ticks").GetUInt64();

        return new HookLabReport(
            "fluidruntime-hook-ipc-lab-v0.7",
            ReadOnly: !options.SkipFirstRedundantCopy,
            WouldModifySystem: false,
            CopyElisionEnabled: options.SkipFirstRedundantCopy,
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
            ResourceReuseCount: resources.GetProperty("resource_reuse_count").GetInt64(),
            ActiveResourceCount: lifecycle.ActiveResourceIds.Count,
            RetiredResourceIdCount: lifecycle.RetiredResourceIds.Count,
            RetiredResourceIdentityCount:
                resources.GetProperty("retired_resource_identity_count").GetInt64(),
            ProvenanceFailureCount:
                resources.GetProperty("provenance_failure_count").GetInt64(),
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
                targetReport.GetProperty("texture_contents_equal").GetBoolean(),
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
            TargetReport: targetReport);
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
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!process.HasExited && DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                return HookRingReader.OpenForProcess(process.Id);
            }
            catch (Exception exception)
                when (exception is FileNotFoundException or InvalidDataException)
            {
                await Task.Delay(10, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"Native hook ring for PID {process.Id} did not become available.");
    }

    private static void ValidateTargetReport(JsonElement report, bool copyElisionEnabled)
    {
        var timing = report.GetProperty("timing");
        var gpuTimingSupported = timing.GetProperty("gpu_timing_supported").GetBoolean();
        var gpuTimingValid = timing.GetProperty("gpu_timing_valid").GetBoolean();
        var gpuTimingDisjoint = timing.GetProperty("gpu_timing_disjoint").GetBoolean();
        var gpuQueryTimedOut = timing.GetProperty("gpu_query_timed_out").GetBoolean();
        var gpuFrequency = timing.GetProperty("gpu_frequency").GetUInt64();
        if (report.GetProperty("mode").GetString() != "fluidruntime-resource-hook-lab-v0.7" ||
            report.GetProperty("read_only_hook").GetBoolean() == copyElisionEnabled ||
            report.GetProperty("would_modify_frame_data").GetBoolean() ||
            report.GetProperty("would_skip_copies").GetBoolean() != copyElisionEnabled ||
            report.GetProperty("optimization_requested").GetBoolean() != copyElisionEnabled ||
            !report.GetProperty("resource_metrics_matched").GetBoolean() ||
            !report.GetProperty("original_pointer_restored").GetBoolean() ||
            !report.GetProperty("content_readback_succeeded").GetBoolean() ||
            !report.GetProperty("buffer_contents_equal").GetBoolean() ||
            !report.GetProperty("texture_contents_equal").GetBoolean() ||
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
        ulong copyBytes,
        ulong redundantBytes,
        HookRingReader reader,
        ResourceLifecycleValidationResult lifecycle,
        bool copyElisionEnabled)
    {
        var resources = targetReport.GetProperty("resources");
        var expectedEventCount = resources.GetProperty("ipc_event_count").GetInt64();
        var sequencesMatch = events.Select((item, index) => item.Sequence == index).All(value => value);
        var knownEventTypes = events.All(item => Enum.IsDefined(item.Type));
        var workloadMatches = MatchesDeterministicWorkload(
            events,
            targetReport.GetProperty("observed_presents").GetInt64(),
            copyElisionEnabled);
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
            CountEvents(events, HookEventType.HookRefresh) ==
                resources.GetProperty("hook_refresh_count").GetInt64() &&
            CountEvents(events, HookEventType.ResourceRetire) ==
                resources.GetProperty("resource_retire_count").GetInt64() &&
            CountEvents(events, HookEventType.ResourceReuse) ==
                resources.GetProperty("resource_reuse_count").GetInt64();
        if (events.Count != expectedEventCount ||
            !sequencesMatch ||
            !knownEventTypes ||
            !workloadMatches ||
            !eventTypesMatch ||
            !lifecycle.IsValid ||
            lifecycle.ActiveResourceIds.Count !=
                resources.GetProperty("tracked_resource_count").GetInt64() ||
            lifecycle.RetiredResourceIds.Count !=
                resources.GetProperty("resource_retire_count").GetInt64() ||
            resources.GetProperty("provenance_failure_count").GetInt64() != 0 ||
            resources.GetProperty("retired_resource_identity_count").GetInt64() +
                resources.GetProperty("resource_reuse_count").GetInt64() !=
                resources.GetProperty("resource_retire_count").GetInt64() ||
            events.Any(item =>
                item.Type == HookEventType.ResourceReuse && item.Flags != 0) ||
            copyEvents.Count != resources.GetProperty("copy_resource_count").GetInt64() ||
            copyBytes != resources.GetProperty("copy_resource_bytes_estimated").GetUInt64() ||
            redundantCopyEvents.Count !=
                resources.GetProperty("redundant_copy_candidate_count").GetInt64() ||
            redundantBytes !=
                resources.GetProperty("redundant_copy_bytes_estimated").GetUInt64() ||
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
        bool copyElisionEnabled = false)
    {
        if (expectedPresentCount < 0 ||
            events.Any(item => item.QpcTicks <= 0 || item.ThreadId == 0))
        {
            return false;
        }

        var expectedCoreEvents = new[]
        {
            (HookEventType.CreateBuffer, 1UL, 0UL, 4096UL, 1UL, 0U),
            (HookEventType.CreateBuffer, 2UL, 0UL, 4096UL, 0UL, 0U),
            (HookEventType.CreateBuffer, 3UL, 0UL, 4096UL, 0UL, 0U),
            (HookEventType.MapWrite, 3UL, 0UL, 4096UL, 0UL, 4U),
            (HookEventType.UnmapWrite, 3UL, 0UL, 4096UL, 1UL, 0U),
            (HookEventType.CopyResource, 2UL, 1UL, 4096UL, 1UL, 0U),
            (HookEventType.CopyResource, 2UL, 1UL, 4096UL, 2UL,
                copyElisionEnabled ? 3U : 1U),
            (HookEventType.UpdateSubresource, 1UL, 0UL, 4096UL, 2UL, 0U),
            (HookEventType.CopyResource, 2UL, 1UL, 4096UL, 3UL, 0U),
            (HookEventType.CopyResource, 2UL, 1UL, 4096UL, 4UL, 1U),
            (HookEventType.CreateTexture2D, 4UL, 0UL, 16384UL, 1UL, 0U),
            (HookEventType.CreateTexture2D, 5UL, 0UL, 16384UL, 0UL, 0U),
            (HookEventType.CopyResource, 5UL, 4UL, 16384UL, 1UL, 0U),
            (HookEventType.CopyResource, 5UL, 4UL, 16384UL, 2UL, 1U)
        };
        var nonRefreshEvents = events
            .Where(item => item.Type != HookEventType.HookRefresh)
            .ToArray();
        if (nonRefreshEvents.LongLength <
            expectedCoreEvents.LongLength + 4 + expectedPresentCount)
        {
            return false;
        }

        for (var index = 0; index < expectedCoreEvents.Length; ++index)
        {
            var actual = nonRefreshEvents[index];
            var expected = expectedCoreEvents[index];
            if (actual.Type != expected.Item1 ||
                actual.ResourceA != expected.Item2 ||
                actual.ResourceB != expected.Item3 ||
                actual.SizeBytes != expected.Item4 ||
                actual.Generation != expected.Item5 ||
                actual.Flags != expected.Item6)
            {
                return false;
            }
        }

        var cursor = expectedCoreEvents.Length;
        var firstCreate = nonRefreshEvents[cursor++];
        var firstRetire = nonRefreshEvents[cursor++];
        var secondCreate = nonRefreshEvents[cursor++];
        if (firstCreate.Type != HookEventType.CreateBuffer ||
            firstCreate.ResourceA != 6 ||
            firstCreate.ResourceB != 0 ||
            firstCreate.SizeBytes != 256 ||
            firstCreate.Generation != 0 ||
            firstCreate.Flags != 0 ||
            firstRetire.Type != HookEventType.ResourceRetire ||
            firstRetire.ResourceA != 6 ||
            firstRetire.ResourceB != 0 ||
            firstRetire.SizeBytes != 256 ||
            firstRetire.Generation != 0 ||
            firstRetire.Flags != 0 ||
            secondCreate.Type != HookEventType.CreateBuffer ||
            secondCreate.ResourceA != 7 ||
            secondCreate.ResourceB != 0 ||
            secondCreate.SizeBytes != 256 ||
            secondCreate.Generation != 0 ||
            secondCreate.Flags != 0)
        {
            return false;
        }

        if (nonRefreshEvents[cursor].Type == HookEventType.ResourceReuse)
        {
            var reuse = nonRefreshEvents[cursor++];
            if (reuse.ResourceA != 6 ||
                reuse.ResourceB != 7 ||
                reuse.SizeBytes != 256 ||
                reuse.Generation != 0 ||
                reuse.Flags != 0)
            {
                return false;
            }
        }

        var secondRetire = nonRefreshEvents[cursor++];
        if (secondRetire.Type != HookEventType.ResourceRetire ||
            secondRetire.ResourceA != 7 ||
            secondRetire.ResourceB != 0 ||
            secondRetire.SizeBytes != 256 ||
            secondRetire.Generation != 0 ||
            secondRetire.Flags != 0 ||
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
                actual.Flags != 0)
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
            if (actual.ResourceA is < 3 or > 6 ||
                actual.ResourceB != 0 ||
                actual.SizeBytes != 0 ||
                actual.Generation != (ulong)index + 1 ||
                actual.Flags != 0)
            {
                return false;
            }
        }
        return true;
    }
}
