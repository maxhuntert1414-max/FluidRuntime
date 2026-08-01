namespace FluidRuntime.Cli;

public sealed record D3D12ObservationLabOptions(
    string TargetPath,
    string OutputPath,
    int Runs,
    int GpuTimeoutMs,
    int ProcessTimeoutMs,
    bool UseHardware)
{
    public const string Usage =
        "Usage: fluidruntime d3d12-observe-lab " +
        "--target <fluidruntime-d3d12-observation.exe> --out <report.json> " +
        "[--runs <1..30>] [--gpu-timeout-ms <1..30000>] " +
        "[--process-timeout-ms <2..60000>] [--hardware <true|false>]";

    public static D3D12ObservationLabOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 ||
            !string.Equals(args[0], "d3d12-observe-lab", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(Usage);
        }

        string? target = null;
        string? output = null;
        var runs = 3;
        var gpuTimeoutMs = 10000;
        var processTimeoutMs = 20000;
        var useHardware = false;
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for '{args[index]}'.");
            }

            var value = args[index + 1];
            switch (args[index])
            {
                case "--target": target = value; break;
                case "--out": output = value; break;
                case "--runs": runs = ParseInt(value, "--runs", 1, 30); break;
                case "--gpu-timeout-ms":
                    gpuTimeoutMs = ParseInt(value, "--gpu-timeout-ms", 1, 30000);
                    break;
                case "--process-timeout-ms":
                    processTimeoutMs = ParseInt(
                        value,
                        "--process-timeout-ms",
                        2,
                        60000);
                    break;
                case "--hardware":
                    if (!bool.TryParse(value, out useHardware))
                    {
                        throw new ArgumentException("--hardware must be true or false.");
                    }
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{args[index]}'. {Usage}");
            }
        }

        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(output))
        {
            throw new ArgumentException($"Target and output are required. {Usage}");
        }
        if (processTimeoutMs <= gpuTimeoutMs)
        {
            throw new ArgumentException(
                "--process-timeout-ms must be greater than --gpu-timeout-ms.");
        }
        return new(target, output, runs, gpuTimeoutMs, processTimeoutMs, useHardware);
    }

    private static int ParseInt(string value, string name, int minimum, int maximum)
    {
        if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
        {
            throw new ArgumentException(
                $"{name} must be between {minimum} and {maximum}.");
        }
        return parsed;
    }
}
