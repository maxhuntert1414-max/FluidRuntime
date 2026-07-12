using System.Text.Json;
using FluidRuntime.Cli;
using FluidRuntime.Contracts;
using FluidRuntime.Runtime;
using FluidRuntime.Telemetry;

return await RuntimeApplication.RunAsync(args);

public static class RuntimeApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(argument => argument is "--help" or "-h"))
        {
            Console.WriteLine(RuntimeOptions.Usage);
            return 0;
        }

        try
        {
            var options = RuntimeOptions.Parse(args);
            var ledger = FluidGatewayLedgerLoader.Load(options.LedgerPath);
            var inspector = new RuntimeInspector(
                new WindowsProcessTelemetrySampler(),
                new RuntimeDecisionEngine());

            var report = await inspector.InspectAsync(
                ledger,
                options.LedgerPath,
                options.ProcessId,
                options.SampleCount,
                TimeSpan.FromMilliseconds(options.IntervalMs));

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
                $"FluidRuntime inspected PID {report.Telemetry.ProcessId} " +
                $"({report.Telemetry.ProcessName}) with {report.Telemetry.SampleCount} samples.");
            Console.WriteLine(
                $"Decision: {report.DecisionPlan.Policy}; " +
                $"pressure={report.DecisionPlan.CombinedPressureScore:0.##}; " +
                $"actions={report.DecisionPlan.Actions.Count}.");
            Console.WriteLine($"Report: {outputPath}");
            return 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or FileNotFoundException)
        {
            Console.Error.WriteLine($"Input error: {exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Runtime inspection failed: {exception.Message}");
            return 1;
        }
    }
}
