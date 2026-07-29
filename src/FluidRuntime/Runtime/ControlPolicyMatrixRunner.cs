using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluidRuntime.Cli;
using FluidRuntime.Native;

namespace FluidRuntime.Runtime;

public sealed class ControlPolicyMatrixRunner
{
    public async Task<ControlPolicyMatrixReport> RunAsync(
        ControlPolicyMatrixOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configurations = new[]
        {
            new Configuration(
                "Release",
                RequireFile(options.ReleaseTargetPath, "Release hook target"),
                RequireFile(options.ReleaseHookPath, "Release hook DLL")),
            new Configuration(
                "Debug",
                RequireFile(options.DebugTargetPath, "Debug hook target"),
                RequireFile(options.DebugHookPath, "Debug hook DLL"))
        };
        if (Path.GetFullPath(configurations[0].TargetPath).Equals(
                Path.GetFullPath(configurations[1].TargetPath),
                StringComparison.OrdinalIgnoreCase) ||
            Path.GetFullPath(configurations[0].HookPath).Equals(
                Path.GetFullPath(configurations[1].HookPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Release and Debug must use distinct binaries.");
        }

        var caseReports = new List<ControlPolicyCaseReport>();
        foreach (var configuration in configurations)
        {
            foreach (var policyCase in HookControlPolicyCases.Matrix)
            {
                var runs = new List<ControlPolicyRunEvidence>();
                var projections = new List<ControlPolicyProjection>();
                for (var repetition = 1;
                     repetition <= ControlPolicyMatrixOptions.RepetitionsPerCase;
                     ++repetition)
                {
                    var (evidence, projection) = await RunOneAsync(
                        configuration,
                        policyCase,
                        repetition,
                        cancellationToken);
                    if (!evidence.Passed)
                    {
                        throw new InvalidDataException(
                            $"{configuration.Name}/{policyCase.ToCliValue()} " +
                            $"repetition {repetition} violated the policy contract: " +
                            $"exit={evidence.ExitCode}, published={evidence.PublishedEpoch}, " +
                            $"ack={evidence.AcknowledgedEpoch}, " +
                            $"applied={evidence.AppliedActionCount}, " +
                            $"status={evidence.Status}, " +
                            $"accepted-events={evidence.AcceptedEventCount}, " +
                            $"forwarded={evidence.ForwardedCopyCount}, " +
                            $"skipped={evidence.SkippedCopyCount}, " +
                            $"events={evidence.EventCount}, lost={evidence.LostSequenceCount}, " +
                            $"overruns={evidence.NativeOverrunCount}, " +
                            $"content={evidence.ContentEquivalent}, " +
                            $"rollback={evidence.RollbackRestored}.");
                    }
                    runs.Add(evidence);
                    projections.Add(projection);
                }

                var deterministic = projections.All(item => item == projections[0]);
                var projectionHash = ProjectionHash(projections[0]);
                if (!deterministic)
                {
                    throw new InvalidDataException(
                        $"{configuration.Name}/{policyCase.ToCliValue()} was not deterministic.");
                }
                caseReports.Add(new ControlPolicyCaseReport(
                    configuration.Name,
                    policyCase.ToCliValue(),
                    ControlPolicyMatrixOptions.RepetitionsPerCase,
                    runs.Count,
                    deterministic,
                    Passed: runs.All(item => item.Passed),
                    projectionHash,
                    runs));
            }
        }

        var crossConfigurationDeterministic = HookControlPolicyCases.Matrix.All(policyCase =>
        {
            var matching = caseReports
                .Where(item => item.PolicyCase == policyCase.ToCliValue())
                .Select(item => item.ProjectionSha256)
                .Distinct(StringComparer.Ordinal)
                .Count();
            return matching == 1;
        });
        if (!crossConfigurationDeterministic)
        {
            throw new InvalidDataException(
                "Release and Debug produced different normalized policy evidence.");
        }

        var expectedRunCount =
            configurations.Length *
            HookControlPolicyCases.Matrix.Count *
            ControlPolicyMatrixOptions.RepetitionsPerCase;
        var completedRunCount = caseReports.Sum(item => item.CompletedRunCount);
        return new ControlPolicyMatrixReport(
            "fluidruntime-control-policy-matrix-trace-v0.12.0",
            TargetOwned: true,
            WarpOnly: true,
            PerformanceClaim: false,
            ControlPolicyMatrixOptions.RepetitionsPerCase,
            expectedRunCount,
            completedRunCount,
            DeterministicAcrossRepetitions: caseReports.All(item => item.Deterministic),
            DeterministicAcrossConfigurations: crossConfigurationDeterministic,
            Passed: completedRunCount == expectedRunCount &&
                caseReports.All(item => item.Passed),
            caseReports);
    }

    private static async Task<(ControlPolicyRunEvidence Evidence, ControlPolicyProjection Projection)>
        RunOneAsync(
            Configuration configuration,
            HookControlPolicyCase policyCase,
            int repetition,
            CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = configuration.TargetPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--hook");
        startInfo.ArgumentList.Add(configuration.HookPath);
        startInfo.ArgumentList.Add("--frames");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("--gpu-timeout-ms");
        startInfo.ArgumentList.Add("1000");
        startInfo.ArgumentList.Add("--control-timeout-ms");
        startInfo.ArgumentList.Add("5000");
        startInfo.ArgumentList.Add("--control-policy-case");
        startInfo.ArgumentList.Add(policyCase.ToCliValue());

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the policy matrix target.");
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var reader = await HookLabRunner.OpenRingAsync(process, cancellationToken);
            if (reader.ProcessId != (ulong)process.Id)
            {
                throw new InvalidDataException("Hook ring process identity did not match target.");
            }

            var policy = reader.PublishControlPolicyForLab(policyCase);
            await process.StandardInput.WriteLineAsync("published".AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);

            if (policyCase is HookControlPolicyCase.AcceptedThenExpired)
            {
                var accepted = await reader.WaitForControlStatusAsync(
                    policy.Epoch,
                    TimeSpan.FromSeconds(5),
                    cancellationToken);
                if (accepted.Status is not HookControlPolicyStatus.Accepted ||
                    accepted.AppliedActionCount != 0)
                {
                    throw new InvalidDataException(
                        "The expiry case was not held at the accepted pre-consume gate.");
                }
                WaitUntilQpc(policy.ExpiresAtQpc, cancellationToken);
                await process.StandardInput.WriteLineAsync("expired".AsMemory(), cancellationToken);
                await process.StandardInput.FlushAsync(cancellationToken);
            }
            else if (policyCase is not HookControlPolicyCase.NoOptIn)
            {
                await reader.WaitForControlStatusAsync(
                    policy.Epoch,
                    TimeSpan.FromSeconds(5),
                    cancellationToken);
            }

            var events = new List<HookIpcEvent>();
            while (!process.HasExited)
            {
                events.AddRange(reader.ReadAvailable());
                await Task.Delay(1, cancellationToken);
            }
            await process.WaitForExitAsync(cancellationToken);
            events.AddRange(reader.ReadAvailable());
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (string.IsNullOrWhiteSpace(stdout))
            {
                throw new InvalidDataException(
                    $"Target produced no report. exit={process.ExitCode}; {stderr.Trim()}");
            }

            using var document = JsonDocument.Parse(stdout);
            var targetReport = document.RootElement.Clone();
            var snapshot = reader.ControlSnapshot;
            var evidence = BuildEvidence(
                configuration.Name,
                policyCase,
                repetition,
                process.Id,
                process.ExitCode,
                policy,
                snapshot,
                reader,
                events,
                targetReport);
            return (evidence, Projection(evidence));
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

    private static ControlPolicyRunEvidence BuildEvidence(
        string configuration,
        HookControlPolicyCase policyCase,
        int repetition,
        int processId,
        int exitCode,
        HookControlPolicy policy,
        HookControlSnapshot snapshot,
        HookRingReader reader,
        IReadOnlyList<HookIpcEvent> events,
        JsonElement report)
    {
        var resources = report.GetProperty("resources");
        var acceptedEvents = events
            .Where(item => item.Type == HookEventType.ControlPolicyAccepted)
            .ToArray();
        var expectedAccepted = IsAccepted(policyCase) ? 1 : 0;
        var expectedApplied = policyCase is HookControlPolicyCase.Valid ? 1 : 0;
        var expectedRejected = IsRejected(policyCase) ? 1 : 0;
        var expectedAck = ExpectedAcknowledgedEpoch(policyCase);
        var expectedStatus = ExpectedStatus(policyCase);
        var expectedSkipped = policyCase is HookControlPolicyCase.Valid ? 1 : 0;
        var expectedSkippedBytes = expectedSkipped == 1 ? 4096UL : 0UL;
        var expectedForwarded = 6 - expectedSkipped;
        var expectedForwardedBytes = 49152UL - expectedSkippedBytes;
        var waitHresult = report.GetProperty("control_policy_wait_hresult").GetString() ?? "";
        var expiryWaitHresult =
            report.GetProperty("control_policy_expiry_wait_hresult").GetString() ?? "";
        var destinationBufferHash =
            report.GetProperty("destination_buffer_hash").GetString() ?? "";
        var destinationTextureHash =
            report.GetProperty("destination_texture_hash").GetString() ?? "";
        var destinationSubresourceHash =
            report.GetProperty("destination_subresource_hash").GetString() ?? "";
        var contentEquivalent =
            report.GetProperty("content_readback_succeeded").GetBoolean() &&
            report.GetProperty("buffer_contents_equal").GetBoolean() &&
            report.GetProperty("texture_contents_equal").GetBoolean() &&
            report.GetProperty("subresource_contents_equal").GetBoolean() &&
            report.GetProperty("source_buffer_hash").GetString() == destinationBufferHash &&
            report.GetProperty("source_texture_hash").GetString() == destinationTextureHash &&
            report.GetProperty("source_subresource_hash").GetString() ==
                destinationSubresourceHash;
        var rollback = report.GetProperty("original_pointer_restored").GetBoolean();
        var sequencesMatch = events
            .Select((item, index) => item.Sequence == index)
            .All(value => value);
        var acceptedEventMatches = acceptedEvents.Length == expectedAccepted &&
            (expectedAccepted == 0 ||
                (acceptedEvents[0].ResourceA == 1 &&
                 acceptedEvents[0].ResourceB == HookRingReader.SkipRedundantCopyResourceAction &&
                 acceptedEvents[0].SizeBytes == 1 &&
                 acceptedEvents[0].Generation == (ulong)policy.ExpiresAtQpc &&
                 acceptedEvents[0].Sequence == 0));
        var expectedControlEnabled =
            policyCase is HookControlPolicyCase.NoOptIn ? 0 : 1;
        var expectedActiveEpoch = IsAccepted(policyCase) ? 1 : 0;
        var passed =
            exitCode == 0 &&
            report.GetProperty("mode").GetString() ==
                "fluidruntime-resource-hook-lab-v0.12.0" &&
            report.GetProperty("render_driver").GetString() == "warp" &&
            report.GetProperty("control_policy_case").GetString() ==
                policyCase.ToCliValue() &&
            report.GetProperty("control_policy_requested").GetBoolean() &&
            waitHresult == ExpectedWaitHresult(policyCase) &&
            expiryWaitHresult ==
                (policyCase is HookControlPolicyCase.AcceptedThenExpired
                    ? "0x00000000"
                    : "0x00000001") &&
            snapshot.PublishedEpoch == policy.Epoch &&
            snapshot.AcknowledgedEpoch == expectedAck &&
            snapshot.AppliedActionCount == expectedApplied &&
            snapshot.Status == expectedStatus &&
            resources.GetProperty("control_policy_enabled").GetInt64() ==
                expectedControlEnabled &&
            resources.GetProperty("control_policy_epoch").GetInt64() ==
                expectedActiveEpoch &&
            resources.GetProperty("control_policy_acknowledged_epoch").GetInt64() ==
                expectedAck &&
            resources.GetProperty("control_policy_applied_action_count").GetInt64() ==
                expectedApplied &&
            resources.GetProperty("control_policy_rejected_count").GetInt64() ==
                expectedRejected &&
            resources.GetProperty("control_policy_status").GetInt64() ==
                (long)expectedStatus &&
            resources.GetProperty("copy_resource_count").GetInt64() == 6 &&
            resources.GetProperty("forwarded_copy_count").GetInt64() == expectedForwarded &&
            resources.GetProperty("forwarded_copy_bytes_estimated").GetUInt64() ==
                expectedForwardedBytes &&
            resources.GetProperty("skipped_copy_count").GetInt64() == expectedSkipped &&
            resources.GetProperty("skipped_copy_bytes_estimated").GetUInt64() ==
                expectedSkippedBytes &&
            resources.GetProperty("provenance_failure_count").GetInt64() == 0 &&
            resources.GetProperty("release_hook_failure_count").GetInt64() == 0 &&
            resources.GetProperty("ipc_overrun_count").GetInt64() == 0 &&
            report.GetProperty("resource_metrics_matched").GetBoolean() &&
            contentEquivalent && rollback && acceptedEventMatches && sequencesMatch &&
            events.Count == resources.GetProperty("ipc_event_count").GetInt64() &&
            reader.LostSequenceCount == 0 && reader.NativeOverrunCount == 0;

        return new ControlPolicyRunEvidence(
            configuration,
            policyCase.ToCliValue(),
            repetition,
            processId,
            exitCode,
            waitHresult,
            expiryWaitHresult,
            snapshot.PublishedEpoch,
            snapshot.AcknowledgedEpoch,
            snapshot.AppliedActionCount,
            resources.GetProperty("control_policy_rejected_count").GetInt64(),
            snapshot.Status.ToString().ToLowerInvariant(),
            acceptedEvents.LongLength,
            resources.GetProperty("forwarded_copy_count").GetInt64(),
            resources.GetProperty("forwarded_copy_bytes_estimated").GetUInt64(),
            resources.GetProperty("skipped_copy_count").GetInt64(),
            resources.GetProperty("skipped_copy_bytes_estimated").GetUInt64(),
            events.Count,
            reader.LostSequenceCount,
            reader.NativeOverrunCount,
            destinationBufferHash,
            destinationTextureHash,
            destinationSubresourceHash,
            contentEquivalent,
            rollback,
            passed,
            report);
    }

    private static void WaitUntilQpc(long deadline, CancellationToken cancellationToken)
    {
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Yield();
        }
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

    private static bool IsAccepted(HookControlPolicyCase value) =>
        value is HookControlPolicyCase.Valid or HookControlPolicyCase.AcceptedThenExpired;

    private static bool IsRejected(HookControlPolicyCase value) => value is
        HookControlPolicyCase.WrongEpoch or
        HookControlPolicyCase.UnknownAction or
        HookControlPolicyCase.WrongBudget or
        HookControlPolicyCase.TooLongExpiry or
        HookControlPolicyCase.AlreadyExpired;

    private static long ExpectedAcknowledgedEpoch(HookControlPolicyCase value) => value switch
    {
        HookControlPolicyCase.NoOptIn => 0,
        HookControlPolicyCase.WrongEpoch => 2,
        _ => 1
    };

    private static HookControlPolicyStatus ExpectedStatus(HookControlPolicyCase value) =>
        value switch
        {
            HookControlPolicyCase.Valid => HookControlPolicyStatus.Exhausted,
            HookControlPolicyCase.AcceptedThenExpired => HookControlPolicyStatus.Expired,
            _ when IsRejected(value) => HookControlPolicyStatus.Rejected,
            _ => HookControlPolicyStatus.None
        };

    private static string ExpectedWaitHresult(HookControlPolicyCase value) => value switch
    {
        HookControlPolicyCase.NoOptIn => "0x80070005",
        _ when IsRejected(value) => "0x80070057",
        _ => "0x00000000"
    };

    private static ControlPolicyProjection Projection(ControlPolicyRunEvidence evidence) => new(
        evidence.PolicyCase,
        evidence.ControlPolicyWaitHresult,
        evidence.ControlPolicyExpiryWaitHresult,
        evidence.PublishedEpoch,
        evidence.AcknowledgedEpoch,
        evidence.AppliedActionCount,
        evidence.RejectedCount,
        evidence.Status,
        evidence.AcceptedEventCount,
        evidence.ForwardedCopyCount,
        evidence.ForwardedCopyBytes,
        evidence.SkippedCopyCount,
        evidence.SkippedCopyBytes,
        evidence.DestinationBufferHash,
        evidence.DestinationTextureHash,
        evidence.DestinationSubresourceHash,
        evidence.ContentEquivalent,
        evidence.RollbackRestored);

    private static string ProjectionHash(ControlPolicyProjection projection)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(projection));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private sealed record Configuration(string Name, string TargetPath, string HookPath);

    private sealed record ControlPolicyProjection(
        string PolicyCase,
        string ControlPolicyWaitHresult,
        string ControlPolicyExpiryWaitHresult,
        long PublishedEpoch,
        long AcknowledgedEpoch,
        long AppliedActionCount,
        long RejectedCount,
        string Status,
        long AcceptedEventCount,
        long ForwardedCopyCount,
        ulong ForwardedCopyBytes,
        long SkippedCopyCount,
        ulong SkippedCopyBytes,
        string DestinationBufferHash,
        string DestinationTextureHash,
        string DestinationSubresourceHash,
        bool ContentEquivalent,
        bool RollbackRestored);
}
