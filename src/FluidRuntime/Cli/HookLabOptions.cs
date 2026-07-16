namespace FluidRuntime.Cli;

public sealed record HookLabOptions(
    string TargetPath,
    string HookPath,
    string OutputPath,
    int FrameCount,
    int HoldMs,
    int GpuTimeoutMs,
    int TrialPairs,
    int WarmupPairs,
    bool UseHardware,
    bool SkipFirstRedundantCopy,
    bool UseManagedControlPolicy = false)
{
    public const string Usage =
        "Usage: fluidruntime hook-lab --target <hook-target.exe> --hook <hook.dll> " +
        "--out <report.json> [--frames <count>] [--hold-ms <milliseconds>] " +
        "[--gpu-timeout-ms <milliseconds>] [--hardware <true|false>]";

    public const string CopyElisionUsage =
        "Usage: fluidruntime copy-elision-lab --target <hook-target.exe> " +
        "--hook <hook.dll> --out <report.json> [--frames <count>] " +
        "[--hold-ms <milliseconds>] [--gpu-timeout-ms <milliseconds>] " +
        "[--trial-pairs <count>] [--warmup-pairs <count>] " +
        "[--hardware <true|false>]";

    public const string ManagerUsage =
        "Usage: fluidruntime manager-lab --target <hook-target.exe> " +
        "--hook <hook.dll> --out <report.json> [--frames <count>] " +
        "[--hold-ms <milliseconds>] [--gpu-timeout-ms <milliseconds>] " +
        "[--trial-pairs <count>] [--warmup-pairs <count>] " +
        "[--hardware <true|false>]";

    public static HookLabOptions Parse(string[] args)
    {
        return ParseCore(args, "hook-lab", Usage, allowExperimentOptions: false);
    }

    public static HookLabOptions ParseCopyElision(string[] args)
    {
        return ParseCore(
            args,
            "copy-elision-lab",
            CopyElisionUsage,
            allowExperimentOptions: true);
    }

    public static HookLabOptions ParseManager(string[] args)
    {
        return ParseCore(
            args,
            "manager-lab",
            ManagerUsage,
            allowExperimentOptions: true);
    }

    private static HookLabOptions ParseCore(
        string[] args,
        string command,
        string usage,
        bool allowExperimentOptions)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 ||
            !string.Equals(args[0], command, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(usage);
        }

        string? targetPath = null;
        string? hookPath = null;
        string? outputPath = null;
        var frameCount = 120;
        var holdMs = 1000;
        var gpuTimeoutMs = 1000;
        var trialPairs = allowExperimentOptions ? 5 : 1;
        var warmupPairs = allowExperimentOptions ? 1 : 0;
        var useHardware = false;
        var skipFirstRedundantCopy = false;

        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for '{args[index]}'.");
            }

            var value = args[index + 1];
            switch (args[index])
            {
                case "--target":
                    targetPath = value;
                    break;
                case "--hook":
                    hookPath = value;
                    break;
                case "--out":
                    outputPath = value;
                    break;
                case "--frames":
                    frameCount = ParsePositiveInt(value, "--frames", maximum: 10000);
                    break;
                case "--hold-ms":
                    holdMs = ParsePositiveInt(value, "--hold-ms", maximum: 60000);
                    break;
                case "--gpu-timeout-ms":
                    gpuTimeoutMs = ParsePositiveInt(
                        value,
                        "--gpu-timeout-ms",
                        maximum: 10000);
                    break;
                case "--trial-pairs" when allowExperimentOptions:
                    trialPairs = ParsePositiveInt(value, "--trial-pairs", maximum: 30);
                    break;
                case "--warmup-pairs" when allowExperimentOptions:
                    warmupPairs = ParseNonNegativeInt(value, "--warmup-pairs", maximum: 5);
                    break;
                case "--hardware":
                    if (!bool.TryParse(value, out useHardware))
                    {
                        throw new ArgumentException("--hardware must be true or false.");
                    }
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{args[index]}'. {usage}");
            }
        }

        if (string.IsNullOrWhiteSpace(targetPath) ||
            string.IsNullOrWhiteSpace(hookPath) ||
            string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException($"--target, --hook, and --out are required. {usage}");
        }

        return new HookLabOptions(
            targetPath,
            hookPath,
            outputPath,
            frameCount,
            holdMs,
            gpuTimeoutMs,
            trialPairs,
            warmupPairs,
            useHardware,
            skipFirstRedundantCopy,
            UseManagedControlPolicy: false);
    }

    private static int ParsePositiveInt(string value, string option, int maximum)
    {
        if (!int.TryParse(value, out var result) || result <= 0 || result > maximum)
        {
            throw new ArgumentException(
                $"{option} must be a positive integer no greater than {maximum}.");
        }
        return result;
    }

    private static int ParseNonNegativeInt(string value, string option, int maximum)
    {
        if (!int.TryParse(value, out var result) || result < 0 || result > maximum)
        {
            throw new ArgumentException(
                $"{option} must be between 0 and {maximum}.");
        }
        return result;
    }
}
