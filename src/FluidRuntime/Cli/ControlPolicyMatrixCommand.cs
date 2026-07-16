using System.Text.Json;
using FluidRuntime.Runtime;

namespace FluidRuntime.Cli;

public static class ControlPolicyMatrixCommand
{
    public static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var options = ControlPolicyMatrixOptions.Parse(args);
            var report = await new ControlPolicyMatrixRunner().RunAsync(
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
                $"Control-policy matrix: {report.CompletedRunCount}/{report.ExpectedRunCount} " +
                $"runs; passed={report.Passed.ToString().ToLowerInvariant()}.");
            Console.WriteLine($"Trace: {outputPath}");
            return report.Passed ? 0 : 1;
        }
        catch (Exception exception) when (
            exception is ArgumentException or FileNotFoundException or InvalidDataException)
        {
            Console.Error.WriteLine($"Matrix input error: {exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Control-policy matrix failed: {exception.Message}");
            return 1;
        }
    }
}
