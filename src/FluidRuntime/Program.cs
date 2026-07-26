using System.Text.Json;
using FluidRuntime.Cli;
using FluidRuntime.Contracts;
using FluidRuntime.Native;
using FluidRuntime.Runtime;
using FluidRuntime.Telemetry;

return await RuntimeApplication.RunAsync(args);

public static class RuntimeApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 &&
            string.Equals(args[0], "sustained-copy-lab", StringComparison.OrdinalIgnoreCase))
        {
            return await SustainedCopyLabCommand.RunAsync(args);
        }

        if (args.Length > 0 &&
            string.Equals(
                args[0],
                "control-policy-matrix",
                StringComparison.OrdinalIgnoreCase))
        {
            return await ControlPolicyMatrixCommand.RunAsync(args);
        }

        if (args.Length > 0 &&
            string.Equals(args[0], "manager-lab", StringComparison.OrdinalIgnoreCase))
        {
            return await CopyElisionLabCommand.RunManagerAsync(args);
        }

        if (args.Length > 0 &&
            string.Equals(args[0], "copy-elision-lab", StringComparison.OrdinalIgnoreCase))
        {
            return await CopyElisionLabCommand.RunAsync(args);
        }

        if (args.Length > 0 &&
            string.Equals(args[0], "hook-lab", StringComparison.OrdinalIgnoreCase))
        {
            return await HookLabCommand.RunAsync(args);
        }

        if (args.Any(argument => argument is "--help" or "-h"))
        {
            Console.WriteLine(RuntimeOptions.Usage);
            Console.WriteLine(HookLabOptions.Usage);
            Console.WriteLine(HookLabOptions.CopyElisionUsage);
            Console.WriteLine(HookLabOptions.ManagerUsage);
            Console.WriteLine(ControlPolicyMatrixOptions.Usage);
            Console.WriteLine(SustainedCopyLabOptions.Usage);
            return 0;
        }

        try
        {
            var options = RuntimeOptions.Parse(args);
            var ledger = FluidGatewayLedgerLoader.Load(options.LedgerPath);
            NativeProbeReport? nativeProbe = null;
            if (!string.IsNullOrWhiteSpace(options.NativeProbePath))
            {
                nativeProbe = await new NativeProbeClient().ProbeAsync(
                    options.NativeProbePath,
                    options.ProcessId,
                    options.IntervalMs);
            }

            var inspector = new RuntimeInspector(
                new WindowsProcessTelemetrySampler(),
                new RuntimeDecisionEngine());

            var report = await inspector.InspectAsync(
                ledger,
                options.LedgerPath,
                options.ProcessId,
                options.SampleCount,
                TimeSpan.FromMilliseconds(options.IntervalMs),
                nativeProbe,
                options.AllowLedgerTargetMismatch);

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
            if (!report.LedgerTargetMatched)
            {
                Console.WriteLine("Ledger target mismatch: control recommendations are held.");
            }
            if (report.NativeProbe?.Capabilities.GpuProcessMemory == true &&
                report.NativeProbe.Gpu.LocalUsageBytes is double localUsageBytes)
            {
                var localMb = localUsageBytes / (1024d * 1024d);
                Console.WriteLine(
                    $"Native GPU probe: local={localMb:0.##} MB; " +
                    $"engines={report.NativeProbe.Gpu.EngineInstanceCount}; " +
                    $"utilization-sum={report.NativeProbe.Gpu.EngineUtilizationSumPercent:0.###}%.");
            }
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
