using System.Diagnostics;
using System.Security.Cryptography;
using FluidRuntime.Cli;
using FluidRuntime.Native;

namespace FluidRuntime.Runtime;

public sealed class D3D12ObservationLabRunner
{
    public const string LabMode = "fluidruntime-d3d12-observation-trace-v0.16.0";

    public async Task<D3D12ObservationLabReport> RunAsync(
        D3D12ObservationLabOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var targetPath = Path.GetFullPath(options.TargetPath);
        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException(
                "D3D12 observation target was not found.",
                targetPath);
        }

        using var targetHandle = new FileStream(
            targetPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        var targetSha256 = Convert.ToHexStringLower(SHA256.HashData(targetHandle));
        targetHandle.Position = 0;

        var runs = new List<D3D12ObservationRunReport>(options.Runs);
        for (var index = 0; index < options.Runs; ++index)
        {
            runs.Add(await RunOneAsync(
                targetPath,
                options,
                index,
                cancellationToken));
        }
        targetHandle.Position = 0;
        var finalTargetSha256 = Convert.ToHexStringLower(SHA256.HashData(targetHandle));
        if (!string.Equals(targetSha256, finalTargetSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The D3D12 observation target changed while the lab was running.");
        }
        return BuildReport(options, targetPath, targetSha256, runs);
    }

    internal static D3D12ObservationLabReport BuildReport(
        D3D12ObservationLabOptions options,
        string targetPath,
        string targetSha256,
        IReadOnlyList<D3D12ObservationRunReport> runs)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        RequireSha256(targetSha256);
        if (runs.Count != options.Runs || runs.Count == 0)
        {
            throw new InvalidDataException(
                "The D3D12 observation lab did not complete every requested run.");
        }

        var first = runs[0];
        var expectedDriver = options.UseHardware ? "hardware" : "warp";
        if (runs.Any(run =>
            run.RenderDriver != expectedDriver ||
            !SameAdapter(first, run) ||
            run.Architecture.NodeCount != first.Architecture.NodeCount ||
            run.Architecture.TileBasedRenderer !=
                first.Architecture.TileBasedRenderer ||
            run.Architecture.Uma != first.Architecture.Uma ||
            run.Architecture.CacheCoherentUma != first.Architecture.CacheCoherentUma ||
            run.Architecture.ResourceHeapTier != first.Architecture.ResourceHeapTier))
        {
            throw new InvalidDataException(
                "D3D12 driver, adapter, or architecture identity changed across runs.");
        }
        for (var index = 1; index < runs.Count; ++index)
        {
            if (runs[index].CapturedAtUnixMs < runs[index - 1].CapturedAtUnixMs)
            {
                throw new InvalidDataException(
                    "D3D12 observation timestamps moved backwards across runs.");
            }
        }

        return new D3D12ObservationLabReport(
            LabMode,
            TargetOwned: true,
            CooperativeLoad: true,
            RemoteInjection: false,
            ReadOnlyObservation: true,
            ActuationEnabled: false,
            PhysicalTransferBytesMeasured: false,
            Path.GetFileName(targetPath),
            targetSha256,
            runs.Min(run => run.CapturedAtUnixMs),
            runs.Max(run => run.CapturedAtUnixMs),
            options.Runs,
            runs.Count,
            first.RenderDriver,
            first.Adapter.Description,
            first.Adapter.VendorId,
            first.Adapter.DeviceId,
            first.Adapter.Luid,
            AdapterIdentityStable: true,
            first.Architecture.Uma,
            first.Architecture.CacheCoherentUma,
            first.Architecture.ResourceHeapTier,
            first.Queue.Type,
            first.Transfer.BufferBytes,
            first.Transfer.LogicalUploadBytes,
            first.Transfer.LogicalReadbackBytes,
            ContentEquivalentInAllRuns:
                runs.All(run => run.Transfer.ContentEquivalent),
            FenceCompletedInAllRuns:
                runs.All(run =>
                    run.Transfer.WaitCompleted &&
                    run.Transfer.FenceCompletedValue >=
                        run.Transfer.FenceSignaledValue),
            first.Memory.Source,
            LocalMemoryInfoAvailableInAllRuns:
                runs.All(run =>
                    run.Memory.LocalBefore.Available &&
                    run.Memory.LocalAfter.Available),
            NonLocalMemoryInfoAvailableInAllRuns:
                runs.All(run =>
                    run.Memory.NonLocalBefore.Available &&
                    run.Memory.NonLocalAfter.Available),
            Distribution(runs.Select(run => run.Transfer.CpuRecordMicroseconds)),
            Distribution(runs.Select(run => run.Transfer.SubmitToFenceMicroseconds)),
            Distribution(runs.Select(run => run.Transfer.TotalWorkloadMicroseconds)),
            D3D12ObservationRunParser.ClaimScope,
            PerformanceClaimAllowed: false,
            PerformanceClaimBlockers:
            [
                "observation-only-no-comparable-baseline",
                "logical-copy-bytes-not-physical-traffic"
            ],
            runs);
    }

    private static async Task<D3D12ObservationRunReport> RunOneAsync(
        string targetPath,
        D3D12ObservationLabOptions options,
        int runIndex,
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
        startInfo.ArgumentList.Add("--hardware");
        startInfo.ArgumentList.Add(options.UseHardware ? "true" : "false");
        startInfo.ArgumentList.Add("--gpu-timeout-ms");
        startInfo.ArgumentList.Add(options.GpuTimeoutMs.ToString());

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Unable to start the D3D12 observation target.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(options.ProcessTimeoutMs);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillOwnedProcess(process);
            await Task.WhenAll(stdoutTask, stderrTask);
            throw new TimeoutException(
                $"D3D12 observation run {runIndex} exceeded " +
                $"{options.ProcessTimeoutMs} ms.");
        }
        catch (OperationCanceledException)
        {
            KillOwnedProcess(process);
            await Task.WhenAll(stdoutTask, stderrTask);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"D3D12 observation run {runIndex} exited with code " +
                $"{process.ExitCode}: {stderr.Trim()}");
        }
        var report = D3D12ObservationRunParser.Parse(stdout);
        if (report.ProcessId != process.Id)
        {
            throw new InvalidDataException(
                $"D3D12 report PID {report.ProcessId} did not match launched PID " +
                $"{process.Id}.");
        }
        return report;
    }

    private static void KillOwnedProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static bool SameAdapter(
        D3D12ObservationRunReport left,
        D3D12ObservationRunReport right) =>
        left.Adapter.Description == right.Adapter.Description &&
        left.Adapter.VendorId == right.Adapter.VendorId &&
        left.Adapter.DeviceId == right.Adapter.DeviceId &&
        left.Adapter.Luid == right.Adapter.Luid;

    private static MetricDistribution Distribution(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return new MetricDistribution(
            ordered.Length,
            Math.Round(ordered[0], 3),
            Percentile(ordered, 0.50),
            Percentile(ordered, 0.95),
            Math.Round(ordered[^1], 3),
            Math.Round(ordered.Average(), 3));
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        var rank = percentile * (ordered.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return Math.Round(
            ordered[lower] + (ordered[upper] - ordered[lower]) * (rank - lower),
            3);
    }

    private static void RequireSha256(string value)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A SHA-256 target identity is required.");
        }
    }
}
