using System.Text.Json;
using FluidRuntime.Runtime;

namespace FluidRuntime.Cli;

public static class SustainedCopyLabCommand
{
    public static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var options = SustainedCopyLabOptions.Parse(args);
            var report = await new SustainedCopyLabRunner().RunAsync(
                options,
                cancellationToken);
            var outputPath = Path.GetFullPath(options.OutputPath);
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = true
            });
            await AtomicJsonFile.WriteTextAsync(
                outputPath,
                json + Environment.NewLine,
                cancellationToken);

            Console.WriteLine(
                $"Sustained copy elision validated {report.IncludedTrialPairs} measured " +
                $"pairs and {report.WarmupPairs} warmup pairs.");
            Console.WriteLine(
                $"Avoided per optimized run: {report.RedundantCopyCountPerOptimizedRun} " +
                $"CopyResource calls, {report.AvoidedCopyBytesPerOptimizedRun} bytes.");
            Console.WriteLine(
                $"CPU p95: baseline={report.CpuWorkload.Baseline.P95:0.###} us; " +
                $"optimized={report.CpuWorkload.Optimized.P95:0.###} us; " +
                $"delta={report.CpuWorkload.Delta.P95:+0.###;-0.###;0} us.");
            if (report.GpuWorkload is not null)
            {
                Console.WriteLine(
                    $"GPU p95: baseline={report.GpuWorkload.Baseline.P95:0.###} us; " +
                    $"optimized={report.GpuWorkload.Optimized.P95:0.###} us; " +
                    $"delta={report.GpuWorkload.Delta.P95:+0.###;-0.###;0} us.");
            }
            if (report.CpuRegressionObserved)
            {
                Console.WriteLine(
                    "CPU caveat: a paired CPU median or p95 regression was observed; " +
                    "the claim remains GPU-workload-only.");
            }
            Console.WriteLine(report.PerformanceClaimAllowed
                ? "Performance evidence gate: passed for the owned sustained workload."
                : "Performance evidence gate: blocked by " +
                    string.Join(", ", report.PerformanceClaimBlockers) + ".");
            Console.WriteLine($"Report: {outputPath}");
            return 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            FileNotFoundException or
            InvalidDataException)
        {
            Console.Error.WriteLine($"Sustained-copy input error: {exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Sustained-copy lab failed: {exception.Message}");
            return 1;
        }
    }
}
