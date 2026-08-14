using System.Text.Json;
using FluidRuntime.Runtime;

namespace FluidRuntime.Cli;

public static class D3D12ObservationLabCommand
{
    public static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        if (args.Any(argument => argument is "--help" or "-h"))
        {
            Console.WriteLine(D3D12ObservationLabOptions.Usage);
            return 0;
        }

        try
        {
            var options = D3D12ObservationLabOptions.Parse(args);
            var report = await new D3D12ObservationLabRunner().RunAsync(
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
                $"D3D12 observation completed {report.CompletedRuns}/" +
                $"{report.RunsRequested} owned runs on {report.AdapterDescription}.");
            Console.WriteLine(
                $"Path per run: {report.LogicalUploadBytesPerRun} upload + " +
                $"{report.LogicalReadbackBytesPerRun} readback logical bytes; " +
                $"exact-content={report.ContentEquivalentInAllRuns}.");
            Console.WriteLine(
                "Performance evidence gate: blocked; this step observes the path " +
                "and does not measure physical transfer bytes.");
            Console.WriteLine($"Report: {outputPath}");
            return 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            FileNotFoundException or
            InvalidDataException)
        {
            Console.Error.WriteLine($"D3D12 observation input error: {exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"D3D12 observation lab failed: {exception.Message}");
            return 1;
        }
    }
}
