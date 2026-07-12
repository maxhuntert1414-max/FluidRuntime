namespace FluidRuntime.Cli;

public sealed record HookLabOptions(
    string TargetPath,
    string HookPath,
    string OutputPath,
    int FrameCount,
    int HoldMs,
    bool UseHardware)
{
    public const string Usage =
        "Usage: fluidruntime hook-lab --target <hook-target.exe> --hook <hook.dll> " +
        "--out <report.json> [--frames <count>] [--hold-ms <milliseconds>] " +
        "[--hardware <true|false>]";

    public static HookLabOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 ||
            !string.Equals(args[0], "hook-lab", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(Usage);
        }

        string? targetPath = null;
        string? hookPath = null;
        string? outputPath = null;
        var frameCount = 120;
        var holdMs = 1000;
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

        if (string.IsNullOrWhiteSpace(targetPath) ||
            string.IsNullOrWhiteSpace(hookPath) ||
            string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException($"--target, --hook, and --out are required. {Usage}");
        }

        return new HookLabOptions(
            targetPath,
            hookPath,
            outputPath,
            frameCount,
            holdMs,
            useHardware);
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
}
