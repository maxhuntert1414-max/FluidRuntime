using System.Text.Json;
using FluidRuntime.Runtime;

namespace FluidRuntime.Cli;

public static class CopyElisionLabCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(argument => argument is "--help" or "-h"))
        {
            Console.WriteLine(HookLabOptions.CopyElisionUsage);
            return 0;
        }

        try
        {
            var options = HookLabOptions.ParseCopyElision(args);
            var runner = new HookLabRunner();
            var baseline = await runner.RunAsync(options with
            {
                SkipFirstRedundantCopy = false
            });
            var optimized = await runner.RunAsync(options with
            {
                SkipFirstRedundantCopy = true
            });
            var report = BuildReport(baseline, optimized);
            var outputPath = Path.GetFullPath(options.OutputPath);
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = true
            });
            await File.WriteAllTextAsync(outputPath, json + Environment.NewLine);

            Console.WriteLine(
                "FluidRuntime proved equivalent D3D11 output across baseline and optimized runs.");
            Console.WriteLine(
                $"Avoided: {report.AvoidedCopyCount} CopyResource call, " +
                $"{report.AvoidedCopyBytes} bytes in the owned workload.");
            Console.WriteLine(
                $"Workload timing: baseline={report.BaselineWorkloadMicroseconds:0.###} us; " +
                $"optimized={report.OptimizedWorkloadMicroseconds:0.###} us; " +
                $"delta={report.WorkloadDeltaPercent:+0.##;-0.##;0}%.");
            Console.WriteLine($"Report: {outputPath}");
            return 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or FileNotFoundException)
        {
            Console.Error.WriteLine($"Input error: {exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Copy elision lab failed: {exception.Message}");
            return 1;
        }
    }

    internal static CopyElisionLabReport BuildReport(
        HookLabReport baseline,
        HookLabReport optimized)
    {
        var contentEquivalent = baseline.ContentEquivalent &&
            optimized.ContentEquivalent &&
            baseline.DestinationBufferHash == optimized.DestinationBufferHash &&
            baseline.DestinationTextureHash == optimized.DestinationTextureHash;
        var baselineObservedCopies = baseline.EventTypeCounts.GetValueOrDefault("CopyResource");
        var optimizedObservedCopies = optimized.EventTypeCounts.GetValueOrDefault("CopyResource");
        if (baseline.CopyElisionEnabled ||
            !optimized.CopyElisionEnabled ||
            baseline.SkippedCopyCount != 0 ||
            baseline.SkippedCopyBytes != 0 ||
            optimized.SkippedCopyCount != 1 ||
            optimized.SkippedCopyBytes != 4096 ||
            baselineObservedCopies != 6 ||
            optimizedObservedCopies != baselineObservedCopies ||
            !baseline.RollbackRestored ||
            !optimized.RollbackRestored ||
            baseline.ForwardedCopyCount - optimized.ForwardedCopyCount != 1 ||
            baseline.ForwardedCopyBytes - optimized.ForwardedCopyBytes != 4096 ||
            !contentEquivalent)
        {
            throw new InvalidDataException(
                "Baseline and optimized copy-elision runs did not satisfy the safety contract.");
        }

        var baselineMicroseconds = ToMicroseconds(
            baseline.WorkloadQpcTicks,
            baseline.QpcFrequencyFromTarget);
        var optimizedMicroseconds = ToMicroseconds(
            optimized.WorkloadQpcTicks,
            optimized.QpcFrequencyFromTarget);
        var deltaPercent = baselineMicroseconds == 0
            ? 0
            : Math.Round(
                (optimizedMicroseconds - baselineMicroseconds) * 100d / baselineMicroseconds,
                2);
        return new CopyElisionLabReport(
            "fluidruntime-copy-elision-comparison-v0.5",
            TargetOwned: true,
            CooperativeLoad: true,
            RemoteInjection: false,
            ContentEquivalent: true,
            RollbackRestoredInBothRuns:
                baseline.RollbackRestored && optimized.RollbackRestored,
            ObservedCopyCount: baselineObservedCopies,
            AvoidedCopyCount: optimized.SkippedCopyCount,
            AvoidedCopyBytes: optimized.SkippedCopyBytes,
            BaselineWorkloadMicroseconds: baselineMicroseconds,
            OptimizedWorkloadMicroseconds: optimizedMicroseconds,
            WorkloadDeltaPercent: deltaPercent,
            Baseline: baseline,
            Optimized: optimized);
    }

    private static double ToMicroseconds(ulong ticks, ulong frequency) =>
        frequency == 0 ? 0 : Math.Round(ticks * 1_000_000d / frequency, 3);
}
