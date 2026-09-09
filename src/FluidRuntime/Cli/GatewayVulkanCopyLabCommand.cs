using System.Text.Json;
using FluidRuntime.Runtime;

namespace FluidRuntime.Cli;

public static class GatewayVulkanCopyLabCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Any(argument => argument is "--help" or "-h"))
        {
            Console.WriteLine(GatewayVulkanCopyLabOptions.Usage);
            return 0;
        }
        try
        {
            var options = GatewayVulkanCopyLabOptions.Parse(args);
            using var authorizer = options.CreateAuthorizer();
            var report = await new VulkanCopyLabRunner().RunAsync(options, authorizer, cancellationToken);
            var path = Path.GetFullPath(options.OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await AtomicJsonFile.WriteTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = true
            }) + Environment.NewLine, cancellationToken);
            if (report.FailClosed is not null)
            {
                Console.Error.WriteLine("Vulkan authorization failed; verified all-forwarded baseline completed. " +
                    report.FailClosed.Message);
                Console.Error.WriteLine($"Fail-closed report: {path}");
                return 3;
            }
            Console.WriteLine($"Vulkan: {options.TrialPairs} verified measured pairs; " +
                $"{options.CandidateActionCount} copies / {report.AvoidedLogicalBytesPerOptimizedRun} " +
                "logical bytes omitted per controlled run, with exact readback and rollback.");
            Console.WriteLine($"Managed end-to-end delta p50: {report.ManagedEndToEndMicroseconds!.Delta.P50:0.###} us. " +
                "No general game-performance claim.");
            Console.WriteLine($"Report: {path}");
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or FileNotFoundException or InvalidDataException)
        {
            Console.Error.WriteLine($"Vulkan input/evidence error: {error.Message}");
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Vulkan lab failed: {error.Message}");
            return 1;
        }
    }
}
