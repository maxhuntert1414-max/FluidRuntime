using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FluidRuntime.Cli;
using FluidRuntime.Native;

namespace FluidRuntime.Runtime;

public sealed class VulkanCopyLabRunner
{
    public async Task<VulkanCopyLabReport> RunAsync(GatewayVulkanCopyLabOptions options,
        IGatewayUpdateUploadAuthorizer authorizer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authorizer);
        using var binding = OwnedBinaryBinding.Open(options.TargetPath, options.LibraryPath);
        var pairs = new List<VulkanCopyPairReport>();
        VulkanAuthorizationFailure? failure = null;
        GatewayUpdateUploadAuthorization? firstAuthorization = null;
        for (var index = 0; index < options.TrialPairs + options.WarmupPairs; ++index)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var phase = index < options.WarmupPairs ? "warmup" : "measured";
            var pairIndex = index < options.WarmupPairs ? index : index - options.WarmupPairs;
            VulkanCopyRunReport? baseline = null;
            VulkanCopyRunReport? optimized = null;
            // Alternate AB/BA within each phase, so warmups cannot unbalance
            // the order of the measured pairs.
            var optimizedFirst = pairIndex % 2 != 0;
            foreach (var controlled in optimizedFirst ? new[] { true, false } : [false, true])
            {
                var started = Stopwatch.GetTimestamp();
                GatewayUpdateUploadAuthorization? authorization = null;
                if (controlled)
                {
                    try
                    {
                        var request = new GatewayUpdateUploadAuthorizationRequest(pairIndex, phase,
                            GatewayVulkanCopyLabOptions.BufferBytes, (ulong)options.CandidateActionCount,
                            binding.TargetSha256, binding.HookSha256, GatewayUploadBackend.VulkanCopyBuffer,
                            options.CreateTransferTopology());
                        authorization = await authorizer.AuthorizeAsync(request, cancellationToken);
                        authorization.EnsureMatchesNativePolicy(request.ResourceBytes, request.CandidateActionCount,
                            pairIndex, phase, binding.TargetSha256, binding.HookSha256,
                            request.Backend, request.Topology);
                        if (firstAuthorization is not null &&
                            (authorization.GatewayBackend != firstAuthorization.GatewayBackend ||
                             authorization.GatewayLibrary != firstAuthorization.GatewayLibrary))
                            throw new InvalidDataException("Gateway backend or DLL identity changed between Vulkan pairs.");
                        firstAuthorization ??= authorization;
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        var fallback = await RunNativeAsync(options, binding, null, cancellationToken);
                        failure = new(pairIndex, phase, error.GetType().Name, error.Message, false, fallback);
                        break;
                    }
                }
                var run = await RunNativeAsync(options, binding, authorization, cancellationToken);
                run = run with { ManagedEndToEndMicroseconds = Elapsed(started) };
                if (controlled) optimized = run; else baseline = run;
            }
            if (failure is not null) break;
            if (baseline!.NativeEvidence.GetProperty("device_name").GetString() !=
                optimized!.NativeEvidence.GetProperty("device_name").GetString() ||
                baseline.NativeEvidence.GetProperty("api_version").GetUInt32() !=
                optimized.NativeEvidence.GetProperty("api_version").GetUInt32())
                throw new InvalidDataException("Vulkan pair used different adapters or API versions.");
            pairs.Add(new(pairIndex, phase, optimizedFirst ? "BA" : "AB", baseline, optimized));
        }
        var measured = pairs.Where(pair => pair.Phase == "measured").ToArray();
        PairedTailLatencySummary? Summary(Func<VulkanCopyRunReport, long> value) => measured.Length == 0
            ? null : GatewayLatencyStatistics.SummarizePairs(
                measured.Select(pair => value(pair.Baseline)), measured.Select(pair => value(pair.Optimized)));
        long Metric(VulkanCopyRunReport run, string key) =>
            checked((long)Math.Round(run.NativeEvidence.GetProperty(key).GetDouble()));
        var gpuValid = measured.All(pair =>
            pair.Baseline.NativeEvidence.GetProperty("gpu_timestamp_valid").GetBoolean() &&
            pair.Optimized.NativeEvidence.GetProperty("gpu_timestamp_valid").GetBoolean());
        return new("fluidruntime-gateway-vulkan-copy-v1", DateTimeOffset.UtcNow,
            NativeTransferDescriptors.VulkanCopyBuffer, options.CreateTransferTopology(),
            binding.TargetSha256, binding.HookSha256, options.CandidateActionCount,
            checked((ulong)options.CandidateActionCount * GatewayVulkanCopyLabOptions.BufferBytes),
            pairs, failure, Summary(run => run.ManagedEndToEndMicroseconds),
            Summary(run => Metric(run, "native_workload_us")), Summary(run => Metric(run, "submit_to_fence_us")),
            gpuValid ? Summary(run => Metric(run, "gpu_us")) : null,
            failure is null && measured.Length == options.TrialPairs, false,
            ["Owned cooperative Vulkan buffers only; no external game hooks or global layer installation.",
             "Avoided bytes are logical vkCmdCopyBuffer bytes, not measured PCIe traffic or VRAM savings.",
             "Pair summaries are descriptive; no general FPS, input-latency or energy claim is authorized.",
             "Managed end-to-end timing includes authorization, process startup, verification and rollback.",
             "GPU timing sums two command-buffer intervals, not frame or presentation latency.",
             "No images, sparse/aliased/external memory, secondary command buffers or multi-queue ownership transfers."]);
    }

    private static async Task<VulkanCopyRunReport> RunNativeAsync(GatewayVulkanCopyLabOptions options,
        OwnedBinaryBinding binding, GatewayUpdateUploadAuthorization? authorization,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var optimized = authorization is not null;
        var start = new ProcessStartInfo(binding.TargetPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(binding.TargetPath)!
        };
        string[] arguments = ["--library", binding.HookPath, "--mode", optimized ? "managed" : "baseline",
            "--candidate-count", options.CandidateActionCount.ToString(CultureInfo.InvariantCulture),
            "--gpu-timeout-ms", options.GpuTimeoutMs.ToString(CultureInfo.InvariantCulture),
            "--hold-ms", "150", "--hardware", options.UseHardware ? "true" : "false",
            "--validation", options.RequireValidation ? "true" : "false"];
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["VK_LOADER_LAYERS_DISABLE"] = "~implicit~";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start owned Vulkan target.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(checked(2 * options.GpuTimeoutMs + 20000));
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            using var reader = await HookLabRunner.OpenRingAsync(process, timeout.Token,
                transferBackendId: (int)NativeTransferBackend.Vulkan);
            if (reader.ProcessId != (ulong)process.Id || reader.TransferBackendId != 3)
                throw new InvalidDataException("Vulkan ring identity mismatch.");
            binding.ValidateLaunchedProcess(process);
            HookControlPolicy? policy = null;
            if (optimized)
            {
                policy = reader.PublishTransferBufferCopyElisionPolicy(TimeSpan.FromSeconds(4),
                    authorization!.NativeActionBudget);
                await reader.WaitForControlAcknowledgmentAsync(policy.Epoch, TimeSpan.FromSeconds(5), timeout.Token);
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
            if (process.ExitCode != 0 || stderr.Contains("Vulkan validation:", StringComparison.Ordinal))
                throw new InvalidOperationException($"Owned Vulkan target failed ({process.ExitCode}): {stderr.Trim()} {stdout.Trim()}");
            using var document = JsonDocument.Parse(stdout);
            var evidence = document.RootElement.Clone();
            ValidateNativeEvidence(evidence, options, optimized, process.Id);
            ValidateEvents(events, options.CandidateActionCount, optimized, policy?.ExpiresAtQpc ?? 0);
            var control = reader.ControlSnapshot;
            if (reader.LostSequenceCount != 0 || reader.NativeOverrunCount != 0 ||
                control.PublishedEpoch != (optimized ? 1 : 0) ||
                control.AcknowledgedEpoch != (optimized ? 1 : 0) ||
                control.AppliedActionCount != (optimized ? options.CandidateActionCount : 0) ||
                control.Status != (optimized ? HookControlPolicyStatus.Exhausted : HookControlPolicyStatus.None))
                throw new InvalidDataException("Vulkan IPC loss or control evidence mismatch.");
            return new(optimized, process.Id, Elapsed(started), authorization, policy, evidence, events, stderr.Trim());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Owned Vulkan target timed out; terminated without changing other processes.");
        }
        finally
        {
            await OwnedProcessLifetime.TerminateAsync(process);
            // Drain redirected pipes even on cancellation or startup failure.
            await Task.WhenAll(stdoutTask, stderrTask);
        }
    }

    internal static void ValidateNativeEvidence(JsonElement e, GatewayVulkanCopyLabOptions options,
        bool optimized, int processId)
    {
        var count = options.CandidateActionCount;
        var skipped = optimized ? count : 0;
        bool Number(string name, long expected) => e.GetProperty(name).GetInt64() == expected;
        bool Flag(string name, bool expected = true) => e.GetProperty(name).GetBoolean() == expected;
        var patterns = e.GetProperty("pattern_hashes").EnumerateArray().Select(item => item.GetString()).ToArray();
        var hashes = e.GetProperty("final_hashes").EnumerateArray().Select(item => item.GetString()).ToArray();
        var valid = e.GetProperty("schema").GetString() == "fluidruntime-vulkan-transfer-v1" &&
            e.GetProperty("mode").GetString() == (optimized ? "managed" : "baseline") &&
            Number("process_id", processId) && Number("backend", 3) && Number("operation", 2) &&
            Flag("target_owned") && Flag("cooperative_library") && Flag("remote_injection", false) &&
            Flag("self_published_control", false) && Flag("actuation_enabled", optimized) &&
            Flag("physical_transfer_bytes_measured", false) && Flag("content_equivalent") &&
            Flag("rollback_verified") && Flag("source_mutation_guard_verified") && Flag("invalidation_guards_verified") &&
            Flag("validation_enabled", options.RequireValidation) && Number("validation_errors", 0) &&
            Number("validation_warnings", 0) && Number("lane_count", 2) && Number("queue_count", 1) &&
            e.GetProperty("loader_messages").GetInt64() >= 0 &&
            Number("fence_count", 1) && Number("buffer_bytes", (long)GatewayVulkanCopyLabOptions.BufferBytes) &&
            Number("source_snapshot_bytes", (long)GatewayVulkanCopyLabOptions.BufferBytes * 4) &&
            Number("candidate_count", count) && Number("tracked_copies", count + 8) &&
            Number("skipped_copies", skipped) && Number("forwarded_copies", count + 8 - skipped) &&
            Number("avoided_logical_bytes", skipped * (long)GatewayVulkanCopyLabOptions.BufferBytes) &&
            Number("comparisons", count + 2) && Number("invalidations", 4) &&
            Number("policy_epoch", optimized ? 1 : 0) && Number("policy_status", optimized ? 4 : 0) &&
            Number("applied_actions", skipped) && Number("events", count + (optimized ? 27 : 26)) &&
            Number("overruns", 0) && Number("completed_submissions", 2) && Number("rollback_forwarded_copies", 4) &&
            (e.GetProperty("upload_memory_flags").GetUInt32() & 2) != 0 &&
            (e.GetProperty("device_memory_flags").GetUInt32() & 1) != 0 &&
            (e.GetProperty("readback_memory_flags").GetUInt32() & 2) != 0 &&
            (options.UseHardware ? e.GetProperty("device_type").GetInt32() is 1 or 2 : Number("device_type", 4)) &&
            !string.IsNullOrWhiteSpace(e.GetProperty("device_name").GetString()) &&
            e.GetProperty("api_version").GetUInt32() >= (1U << 22) &&
            patterns.Length == 4 && hashes.Length == 2 &&
            patterns.All(item => item is { Length: 16 } && item.All(Uri.IsHexDigit)) &&
            patterns[0] != patterns[1] && patterns[2] != patterns[3] &&
            hashes[0] == patterns[1] && hashes[1] == patterns[3];
        foreach (var name in new[] { "native_workload_us", "submit_to_fence_us", "gpu_us" })
        {
            var value = e.GetProperty(name).GetDouble();
            valid &= double.IsFinite(value) && value >= 0 && value <= long.MaxValue / 2d;
        }
        var gpuValid = e.GetProperty("gpu_timestamp_valid").GetBoolean();
        var bits = e.GetProperty("gpu_timestamp_valid_bits").GetInt32();
        valid &= gpuValid ? bits is >= 1 and <= 64 :
            bits is >= 0 and <= 64 && e.GetProperty("gpu_us").GetDouble() == 0;
        if (!valid) throw new InvalidDataException("Vulkan native evidence violated the owned transfer contract.");
    }

    internal static IReadOnlyList<HookIpcEvent> ExpectedEvents(int count, bool optimized, long deadline)
    {
        var events = new List<HookIpcEvent>();
        void Add(HookEventType type, ulong a, ulong b, ulong bytes, ulong generation,
            uint flags = 0, ulong scope = 0, uint slot = 0) =>
            events.Add(new(events.Count, 0, type, 0, a, b, bytes, generation, flags | 512, slot, 0, scope));
        void Copy(ulong lane, ulong generation, uint slot, bool compared, bool candidate, bool skip) =>
            Add(HookEventType.TransferBufferCopy, 101 + lane, 201 + lane,
                GatewayVulkanCopyLabOptions.BufferBytes, generation,
                32U | 128U | (compared ? 64U : 0) | (candidate ? 1U : 0) | (skip ? 2U : 0), lane + 1, slot);
        void Submit(ulong generation, ulong submission)
        {
            for (ulong lane = 0; lane < 2; ++lane)
                Add(HookEventType.TransferScopeClose, lane + 1, 0, 0, generation, scope: lane + 1);
            Add(HookEventType.TransferQueueSubmit, 1, 0, 2, submission);
            Add(HookEventType.TransferSyncSignal, 1, 1, submission, submission);
        }
        if (optimized) Add(HookEventType.ControlPolicyAccepted, 1, 16, (ulong)count, (ulong)deadline);
        for (ulong lane = 0; lane < 2; ++lane)
        {
            Copy(lane, 1, 0, false, false, false);
            for (var i = 0; i < count / 2 + (lane == 0 ? count % 2 : 0); ++i)
                Copy(lane, 1, 0, true, true, optimized);
            Copy(lane, 1, 1, true, false, false);
            Add(HookEventType.TransferResourceInvalidate, 201 + lane, 0,
                GatewayVulkanCopyLabOptions.BufferBytes, 2, scope: lane + 1);
            Copy(lane, 2, 1, false, false, false);
            Add(HookEventType.TransferResourceInvalidate, 201 + lane, 0,
                GatewayVulkanCopyLabOptions.BufferBytes, 3, 256, lane + 1);
            Copy(lane, 3, 1, false, false, false);
        }
        Submit(3, 1);
        for (ulong lane = 0; lane < 2; ++lane)
            Add(HookEventType.TransferScopeReset, lane + 1, 0, 0, 4, scope: lane + 1);
        for (ulong lane = 0; lane < 2; ++lane)
        {
            Copy(lane, 4, 0, false, false, false);
            Copy(lane, 4, 0, true, true, false);
        }
        Submit(4, 2);
        return events;
    }

    internal static void ValidateEvents(IReadOnlyList<HookIpcEvent> events, int count, bool optimized, long deadline)
    {
        var expected = ExpectedEvents(count, optimized, deadline);
        if (events.Count != expected.Count) throw new InvalidDataException("Vulkan event count mismatch.");
        for (var i = 0; i < expected.Count; ++i)
        {
            var actual = events[i];
            if (actual.QpcTicks <= 0 || actual.ThreadId == 0 ||
                (i > 0 && (actual.QpcTicks < events[i - 1].QpcTicks || actual.ThreadId != events[0].ThreadId)) ||
                actual with { QpcTicks = 0, ThreadId = 0 } != expected[i])
                throw new InvalidDataException($"Vulkan event {i} violated copy, lane, source, reset or fence ordering.");
        }
    }

    private static long Elapsed(long started) => Math.Max(1,
        checked((long)Math.Ceiling(Stopwatch.GetElapsedTime(started).TotalMicroseconds)));
}
