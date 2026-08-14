using System.Text.Json;
using FluidRuntime.Runtime;

namespace FluidRuntime.Cli;

public static class UpdateUploadElisionLabCommand
{
    public static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        if (args.Any(argument => argument is "--help" or "-h"))
        {
            Console.WriteLine(UpdateUploadElisionLabOptions.Usage);
            return 0;
        }

        try
        {
            var options = UpdateUploadElisionLabOptions.Parse(args);
            var report = await new UpdateUploadElisionLabRunner().RunAsync(
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
                $"Update-upload elision validated {report.IncludedTrialPairs} measured " +
                $"pairs and {report.WarmupPairs} warmup pairs.");
            Console.WriteLine(
                $"Avoided per optimized run: {report.RedundantUpdateCountPerOptimizedRun} " +
                $"updates, {report.AvoidedUpdateBytesPerOptimizedRun} logical bytes.");
            Console.WriteLine(
                $"CPU path p95: baseline={report.CpuWorkload.Baseline.P95:0.###} us; " +
                $"optimized={report.CpuWorkload.Optimized.P95:0.###} us; " +
                $"delta={report.CpuWorkload.Delta.P95:+0.###;-0.###;0} us.");
            if (report.GpuWorkload is not null)
            {
                Console.WriteLine(
                    $"GPU p95: baseline={report.GpuWorkload.Baseline.P95:0.###} us; " +
                    $"optimized={report.GpuWorkload.Optimized.P95:0.###} us; " +
                    $"delta={report.GpuWorkload.Delta.P95:+0.###;-0.###;0} us.");
            }
            Console.WriteLine(report.PerformanceClaimAllowed
                ? "Performance evidence gate: passed for the owned UpdateSubresource workload."
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
            Console.Error.WriteLine($"Update-upload input error: {exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Update-upload lab failed: {exception.Message}");
            return 1;
        }
    }
}
