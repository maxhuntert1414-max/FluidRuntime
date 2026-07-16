namespace FluidRuntime.Cli;

public sealed record ControlPolicyMatrixOptions(
    string ReleaseTargetPath,
    string ReleaseHookPath,
    string DebugTargetPath,
    string DebugHookPath,
    string OutputPath)
{
    public const int RepetitionsPerCase = 20;

    public const string Usage =
        "Usage: fluidruntime control-policy-matrix " +
        "--release-target <hook-target.exe> --release-hook <hook.dll> " +
        "--debug-target <hook-target.exe> --debug-hook <hook.dll> " +
        "--out <trace.json>";

    public static ControlPolicyMatrixOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 ||
            !string.Equals(args[0], "control-policy-matrix", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(Usage);
        }

        string? releaseTarget = null;
        string? releaseHook = null;
        string? debugTarget = null;
        string? debugHook = null;
        string? output = null;
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for '{args[index]}'.");
            }
            var value = args[index + 1];
            switch (args[index])
            {
                case "--release-target": releaseTarget = value; break;
                case "--release-hook": releaseHook = value; break;
                case "--debug-target": debugTarget = value; break;
                case "--debug-hook": debugHook = value; break;
                case "--out": output = value; break;
                default: throw new ArgumentException($"Unknown option '{args[index]}'. {Usage}");
            }
        }

        if (string.IsNullOrWhiteSpace(releaseTarget) ||
            string.IsNullOrWhiteSpace(releaseHook) ||
            string.IsNullOrWhiteSpace(debugTarget) ||
            string.IsNullOrWhiteSpace(debugHook) ||
            string.IsNullOrWhiteSpace(output))
        {
            throw new ArgumentException($"All paths are required. {Usage}");
        }

        return new(releaseTarget, releaseHook, debugTarget, debugHook, output);
    }
}
