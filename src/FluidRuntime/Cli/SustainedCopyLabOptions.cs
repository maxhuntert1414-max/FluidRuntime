using FluidRuntime.Native;

namespace FluidRuntime.Cli;

public sealed record SustainedCopyLabOptions(
    string TargetPath,
    string HookPath,
    string OutputPath,
    int CopyCount,
    int TrialPairs,
    int WarmupPairs,
    int HoldMs,
    int GpuTimeoutMs,
    bool UseHardware)
{
    public const int SustainedBufferBytes = 4 * 1024 * 1024;

    public const string Usage =
        "Usage: fluidruntime sustained-copy-lab --target <hook-target.exe> " +
        "--hook <hook.dll> --out <report.json> [--copy-count <1..128>] " +
        "[--trial-pairs <count>] [--warmup-pairs <count>] " +
        "[--hold-ms <milliseconds>] [--gpu-timeout-ms <milliseconds>] " +
        "[--hardware <true|false>]";

    public static SustainedCopyLabOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 ||
            !string.Equals(args[0], "sustained-copy-lab", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(Usage);
        }

        string? target = null;
        string? hook = null;
        string? output = null;
        var copyCount = (int)HookRingReader.MaxControlActionBudget;
        var trialPairs = 10;
        var warmupPairs = 1;
        var holdMs = 50;
        var gpuTimeoutMs = 5000;
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
                case "--copy-count":
                    copyCount = ParseInt(
                        value,
                        "--copy-count",
                        1,
                        (int)HookRingReader.MaxControlActionBudget);
                    break;
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
            throw new ArgumentException($"--target, --hook, and --out are required. {Usage}");
        }

        return new(
            target,
            hook,
            output,
            copyCount,
            trialPairs,
            warmupPairs,
            holdMs,
            gpuTimeoutMs,
            useHardware);
    }

    private static int ParseInt(string value, string option, int minimum, int maximum)
    {
        if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
        {
            throw new ArgumentException(
                $"{option} must be between {minimum} and {maximum}.");
        }
        return parsed;
    }
}
