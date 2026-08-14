namespace FluidRuntime.Cli;

public sealed record RuntimeOptions(
    string LedgerPath,
    int ProcessId,
    int SampleCount,
    int IntervalMs,
    string OutputPath,
    string? NativeProbePath,
    int NativeProbeTimeoutMs,
    bool AllowLedgerTargetMismatch)
{
    public const string Usage =
        "Usage: fluidruntime inspect --ledger <ledger.json> --out <report.json> " +
        "[--pid <id>] [--samples <count>] [--interval-ms <milliseconds>] " +
        "[--native-probe <fluidruntime-native-probe.exe>] " +
        "[--native-probe-timeout-ms <milliseconds>] " +
        "[--allow-ledger-target-mismatch <true|false>]";

    public static RuntimeOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0 || !string.Equals(args[0], "inspect", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(Usage);
        }

        string? ledgerPath = null;
        string? outputPath = null;
        var processId = Environment.ProcessId;
        var sampleCount = 3;
        var intervalMs = 250;
        string? nativeProbePath = null;
        var nativeProbeTimeoutMs = 10000;
        var nativeProbeTimeoutExplicit = false;
        var allowLedgerTargetMismatch = false;

        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for '{args[index]}'.");
            }

            var value = args[index + 1];
            switch (args[index])
            {
                case "--ledger":
                    ledgerPath = value;
                    break;
                case "--out":
                    outputPath = value;
                    break;
                case "--pid":
                    processId = ParsePositiveInt(value, "--pid");
                    break;
                case "--samples":
                    sampleCount = ParsePositiveInt(value, "--samples");
                    break;
                case "--interval-ms":
                    intervalMs = ParsePositiveInt(value, "--interval-ms");
                    break;
                case "--native-probe":
                    nativeProbePath = value;
                    break;
                case "--native-probe-timeout-ms":
                    nativeProbeTimeoutMs = ParsePositiveInt(
                        value,
                        "--native-probe-timeout-ms");
                    nativeProbeTimeoutExplicit = true;
                    break;
                case "--allow-ledger-target-mismatch":
                    if (!bool.TryParse(value, out allowLedgerTargetMismatch))
                    {
                        throw new ArgumentException(
                            "--allow-ledger-target-mismatch must be true or false.");
                    }
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{args[index]}'. {Usage}");
            }
        }

        if (string.IsNullOrWhiteSpace(ledgerPath) || string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException($"Both --ledger and --out are required. {Usage}");
        }

        if (sampleCount > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "--samples must be 100 or less.");
        }
        if (intervalMs > 60000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                "--interval-ms must be 60000 or less.");
        }
        if (nativeProbeTimeoutMs > 120000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                "--native-probe-timeout-ms must be 120000 or less.");
        }
        if (!nativeProbeTimeoutExplicit)
        {
            nativeProbeTimeoutMs = Math.Clamp(intervalMs + 5000, 10000, 65000);
        }

        return new RuntimeOptions(
            ledgerPath,
            processId,
            sampleCount,
            intervalMs,
            outputPath,
            nativeProbePath,
            nativeProbeTimeoutMs,
            allowLedgerTargetMismatch);
    }

    private static int ParsePositiveInt(string value, string option)
    {
        if (!int.TryParse(value, out var result) || result <= 0)
        {
            throw new ArgumentException($"{option} must be a positive integer.");
        }

        return result;
    }
}
