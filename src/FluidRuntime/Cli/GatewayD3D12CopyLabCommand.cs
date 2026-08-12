using System.Text.Json;
using FluidLink;
using FluidRuntime.Runtime;

namespace FluidRuntime.Cli;

public static class GatewayD3D12CopyLabCommand
{
    public static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        if (args.Any(argument => argument is "--help" or "-h"))
        {
            Console.WriteLine(GatewayD3D12CopyLabOptions.Usage);
            return 0;
        }

        try
        {
            var options = GatewayD3D12CopyLabOptions.Parse(args);
            var report = await new D3D12CopyElisionLabRunner().RunAsync(
                options,
                options.CreateAuthorizer(),
                cancellationToken);
            await WriteReportAsync(options.OutputPath, report, cancellationToken);
            Console.WriteLine(
                $"FluidGateway authorized {report.AuthorizationRunCount} D3D12 " +
                $"runs with {report.CandidateActionCount} candidates each.");
            Console.WriteLine(
                $"Native D3D12 hook omitted {report.CandidateActionCount} " +
                $"CopyBufferRegion calls and {report.AvoidedLogicalBytesPerOptimizedRun} " +
                "logical API bytes per optimized run.");
            Console.WriteLine(
                "Managed end-to-end delta " +
                $"p50={report.ManagedEndToEndMicroseconds.Delta.P50:0.###} us; " +
                $"p95={report.ManagedEndToEndMicroseconds.Delta.P95:0.###} us; " +
                $"p99={report.ManagedEndToEndMicroseconds.Delta.P99:0.###} us.");
            Console.WriteLine(
                "Submit-to-fence delta " +
                $"p50={report.SubmitToFenceMicroseconds.Delta.P50:0.###} us; " +
                $"p95={report.SubmitToFenceMicroseconds.Delta.P95:0.###} us; " +
                $"p99={report.SubmitToFenceMicroseconds.Delta.P99:0.###} us.");
            Console.WriteLine(report.PerformanceClaimAllowed
                ? $"Performance evidence gate: passed for {report.ClaimScope}."
                : "Performance evidence gate: blocked by " +
                    string.Join(", ", report.PerformanceClaimBlockers) + ".");
            Console.WriteLine($"Report: {Path.GetFullPath(options.OutputPath)}");
            return 0;
        }
        catch (GatewayD3D12CopyAuthorizationDeniedException exception)
        {
            var options = GatewayD3D12CopyLabOptions.Parse(args);
            await WriteReportAsync(
                options.OutputPath,
                exception.FailClosedReport,
                cancellationToken);
            Console.Error.WriteLine(
                "FluidGateway authorization failed; the D3D12 workload completed " +
                "through the verified all-forwarded baseline path.");
            Console.Error.WriteLine(
                $"Fail-closed report: {Path.GetFullPath(options.OutputPath)}");
            return 3;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            FileNotFoundException or
            InvalidDataException or
            FluidLinkV2ProtocolException)
        {
            Console.Error.WriteLine(
                $"Gateway-managed D3D12 input/control error: {exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Gateway-managed D3D12 lab failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task WriteReportAsync<T>(
        string path,
        T report,
        CancellationToken cancellationToken)
    {
        var outputPath = Path.GetFullPath(path);
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
    }
}
