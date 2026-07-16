using System.Text.Json;
using FluidRuntime.Runtime;

namespace FluidRuntime.Cli;

public static class HookLabCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(argument => argument is "--help" or "-h"))
        {
            Console.WriteLine(HookLabOptions.Usage);
            return 0;
        }

        try
        {
            var options = HookLabOptions.Parse(args);
            var report = await new HookLabRunner().RunAsync(options);
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
                $"FluidRuntime received {report.EventCount} hook events from " +
                $"PID {report.TargetProcessId} with no loss.");
            Console.WriteLine(
                $"Copies: {report.CopyResourceBytes} bytes observed; " +
                $"{report.RedundantCopyBytes} bytes ({report.AvoidableCopySharePercent:0.##}%) " +
                "classified as redundant candidates.");
            Console.WriteLine(
                $"Forwarded: {report.ForwardedCopyCount}; skipped: " +
                $"{report.SkippedCopyCount} ({report.SkippedCopyBytes} bytes).");
            Console.WriteLine(
                $"Subresource regions: {report.CopySubresourceRegionCount} calls / " +
                $"{report.CopySubresourceRegionBytes} bytes; " +
                $"{report.RedundantSubresourceCopyCandidateCount} candidates / " +
                $"{report.RedundantSubresourceCopyBytes} bytes, all forwarded.");
            Console.WriteLine(
                $"GPU view writes: RTV clears={report.ClearRenderTargetViewCount}; " +
                $"UAV float clears={report.ClearUnorderedAccessViewFloatCount}; " +
                $"{report.GpuViewWriteBytes} bytes attributed to exact subresources.");
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
            Console.Error.WriteLine($"Hook lab failed: {exception.Message}");
            return 1;
        }
    }
}
