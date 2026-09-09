using System.Text.Json;
using FluidRuntime.Runtime;

namespace FluidRuntime.Cli;

public static class GatewayReadbackLabCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Any(argument => argument is "--help" or "-h"))
        {
            Console.WriteLine(GatewayReadbackLabOptions.Usage);
            return 0;
        }
        try
        {
            var options = GatewayReadbackLabOptions.Parse(args);
            using var authorizer = options.CreateAuthorizer();
            try
            {
                var report = await new GatewayReadbackLabRunner().RunAsync(options.Native, authorizer, cancellationToken);
                await WriteReport(options.Native.OutputPath, report, cancellationToken);
                Console.WriteLine($"Gateway {report.GatewayBackend}: {report.NativeEvidence.IncludedTrialPairs} verified readback pairs; " +
                    $"{report.NativeEvidence.AvoidedReadbackBytesPerOptimizedRun} logical bytes omitted per controlled run.");
                Console.WriteLine("Read maps, content equivalence and rollback verified. No general performance claim.");
                Console.WriteLine($"Report: {Path.GetFullPath(options.Native.OutputPath)}");
                return 0;
            }
            catch (GatewayReadbackDeniedException error)
            {
                await WriteReport(options.Native.OutputPath, error.Report, cancellationToken);
                Console.Error.WriteLine(error.Message);
                return 3;
            }
        }
        catch (Exception error) when (error is ArgumentException or FileNotFoundException or InvalidDataException)
        {
            Console.Error.WriteLine($"Gateway-readback input error: {error.Message}");
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Gateway-readback failed: {error.Message}");
            return 1;
        }
    }

    private static Task WriteReport<T>(string path, T report, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        return AtomicJsonFile.WriteTextAsync(fullPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true
        }) + Environment.NewLine, cancellationToken);
    }
}
