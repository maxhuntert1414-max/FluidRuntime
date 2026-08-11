namespace FluidRuntime.Cli;

public sealed record UpdateUploadElisionLabOptions(
    string TargetPath,
    string HookPath,
    string OutputPath,
    int TrialPairs,
    int WarmupPairs,
    int HoldMs,
    int GpuTimeoutMs,
    int CandidateActionCount,
    bool UseHardware)
{
    public const int BufferBytes = 4 * 1024 * 1024;
    public const int DefaultCandidateActionCount = 128;
    public const int MaximumCandidateActionCount = 128;
    public const int RequiredUpdateCount = 3;

    public int TotalUpdateCount => checked(CandidateActionCount + RequiredUpdateCount);

    public const string Usage =
        "Usage: fluidruntime update-upload-elision-lab " +
        "--target <hook-target.exe> --hook <hook.dll> --out <report.json> " +
        "[--trial-pairs <count>] [--warmup-pairs <count>] " +
        "[--hold-ms <milliseconds>] [--gpu-timeout-ms <milliseconds>] " +
        "[--candidate-action-count <1-128>] " +
        "[--hardware <true|false>]";

    public static UpdateUploadElisionLabOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 ||
            !string.Equals(
                args[0],
                "update-upload-elision-lab",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(Usage);
        }

        string? target = null;
        string? hook = null;
        string? output = null;
        var trialPairs = 10;
        var warmupPairs = 1;
        var holdMs = 50;
        var gpuTimeoutMs = 5000;
        var candidateActionCount = DefaultCandidateActionCount;
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
                case "--hook": hook = value; break;
                case "--out": output = value; break;
                case "--trial-pairs":
                    trialPairs = ParseInt(value, "--trial-pairs", 1, 30);
                    break;
                case "--warmup-pairs":
                    warmupPairs = ParseInt(value, "--warmup-pairs", 0, 5);
                    break;
                case "--hold-ms":
                    holdMs = ParseInt(value, "--hold-ms", 1, 5000);
                    break;
                case "--gpu-timeout-ms":
                    gpuTimeoutMs = ParseInt(value, "--gpu-timeout-ms", 1, 10000);
                    break;
                case "--candidate-action-count":
                    candidateActionCount = ParseInt(
                        value,
                        "--candidate-action-count",
                        1,
                        MaximumCandidateActionCount);
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

        if (string.IsNullOrWhiteSpace(target) ||
            string.IsNullOrWhiteSpace(hook) ||
            string.IsNullOrWhiteSpace(output))
        {
            throw new ArgumentException($"Target, hook, and output are required. {Usage}");
        }

        return new(
            target,
            hook,
            output,
            trialPairs,
            warmupPairs,
            holdMs,
            gpuTimeoutMs,
            candidateActionCount,
            useHardware);
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
