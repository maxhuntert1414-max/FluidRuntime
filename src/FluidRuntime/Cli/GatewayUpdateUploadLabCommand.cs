using System.Text.Json;
using FluidLink;
using FluidRuntime.Runtime;

namespace FluidRuntime.Cli;

public static class GatewayUpdateUploadLabCommand
{
    public static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        if (args.Any(argument => argument is "--help" or "-h"))
        {
            Console.WriteLine(GatewayUpdateUploadLabOptions.Usage);
            return 0;
        }

        try
        {
            var options = GatewayUpdateUploadLabOptions.Parse(args);
            var authorizer = options.CreateAuthorizer();
            var report = await new UpdateUploadElisionLabRunner()
                .RunGatewayManagedAsync(
                    options.ToNativeOptions(),
                    authorizer,
                    cancellationToken);
            var concurrencyBenchmark =
                await new GatewayAuthorizationConcurrencyBenchmarkRunner()
                    .RunAsync(
                        options.ToAuthorizationBenchmarkConfiguration(),
                        authorizer,
                        report.TargetSha256,
                        report.HookSha256,
                        cancellationToken);
            report = report.AttachAuthorizationConcurrencyBenchmark(
                concurrencyBenchmark);
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
                $"FluidGateway authorized {report.GatewayCandidateDecisionCount} " +
                $"bounded upload candidates across {report.AuthorizationRunCount} runs.");
            Console.WriteLine(
                $"Native hook avoided " +
                $"{report.NativeEvidence.AvoidedUpdateBytesPerOptimizedRun} " +
                "logical bytes per optimized run with exact-content guards.");
            Console.WriteLine(
                $"Authorization latency p50={report.AuthorizationLatencyMicroseconds.P50:0.###} us; " +
                $"p95={report.AuthorizationLatencyMicroseconds.P95:0.###} us.");
            Console.WriteLine(
                "Managed end-to-end delta " +
                $"p50={report.ManagedEndToEndLatencyMicroseconds.Delta.P50:0.###} us; " +
                $"p95={report.ManagedEndToEndLatencyMicroseconds.Delta.P95:0.###} us; " +
                $"p99={report.ManagedEndToEndLatencyMicroseconds.Delta.P99:0.###} us.");
            var peakConcurrency = concurrencyBenchmark.Levels[^1];
            Console.WriteLine(
                $"Concurrent authorization x{peakConcurrency.Concurrency}: " +
                $"p99={peakConcurrency.LatencyMicroseconds.P99:0.###} us; " +
                $"failures={concurrencyBenchmark.TotalFailureCount}; " +
                $"transport={concurrencyBenchmark.TransportDecision}.");
            Console.WriteLine(report.PerformanceClaimAllowed
                ? $"Performance evidence gate: passed for {report.ClaimScope}."
                : "Performance evidence gate: blocked by " +
                    string.Join(", ", report.PerformanceClaimBlockers) + ".");
            Console.WriteLine($"Report: {outputPath}");
            return concurrencyBenchmark.ReliabilityGatePassed ? 0 : 4;
        }
        catch (GatewayUpdateUploadAuthorizationDeniedException exception)
        {
            var options = GatewayUpdateUploadLabOptions.Parse(args);
            var outputPath = Path.GetFullPath(options.OutputPath);
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var json = JsonSerializer.Serialize(
                exception.FailClosedReport,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    WriteIndented = true
                });
            await AtomicJsonFile.WriteTextAsync(
                outputPath,
                json + Environment.NewLine,
                cancellationToken);
            Console.Error.WriteLine(
                "FluidGateway authorization failed; all UpdateSubresource calls " +
                "were forwarded through the verified baseline path.");
            Console.Error.WriteLine($"Fail-closed report: {outputPath}");
            return 3;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            FileNotFoundException or
            InvalidDataException or
            FluidLinkV2ProtocolException)
        {
            Console.Error.WriteLine(
                $"Gateway-managed update-upload input/control error: {exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Gateway-managed update-upload lab failed: {exception.Message}");
            return 1;
        }
    }
}
