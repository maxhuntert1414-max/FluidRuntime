using System.Text.Json;
using FluidRuntime.Runtime;

namespace FluidRuntime.Cli;

public static class ReadbackElisionLabCommand
{
    public static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        if (args.Any(argument => argument is "--help" or "-h"))
        {
            Console.WriteLine(ReadbackElisionLabOptions.Usage);
            return 0;
        }

        try
        {
            var options = ReadbackElisionLabOptions.Parse(args);
            var report = await new ReadbackElisionLabRunner().RunAsync(
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
            await File.WriteAllTextAsync(
                outputPath,
                json + Environment.NewLine,
                cancellationToken);

            Console.WriteLine(
                $"Readback elision validated {report.IncludedTrialPairs} measured pairs " +
                $"and {report.WarmupPairs} warmup pairs.");
            Console.WriteLine(
                $"Avoided per optimized run: " +
                $"{report.RedundantReadbackCopyCountPerOptimizedRun} copies, " +
                $"{report.AvoidedReadbackBytesPerOptimizedRun} logical bytes.");
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
            Console.WriteLine(report.PerformanceClaimAllowed
                ? "Performance evidence gate: passed for the owned readback workload."
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
            Console.Error.WriteLine($"Readback-elision input error: {exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Readback-elision lab failed: {exception.Message}");
            return 1;
        }
    }
}
