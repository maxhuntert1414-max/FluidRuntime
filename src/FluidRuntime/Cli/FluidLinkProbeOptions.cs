namespace FluidRuntime.Cli;

public sealed record FluidLinkProbeOptions(
    string Host,
    int Port,
    int TimeoutMs,
    string OutputPath)
{
    public const string Usage =
        "Usage: fluidruntime link-probe --out <report.json> " +
        "[--host <loopback-host>] [--port <port>] [--timeout-ms <milliseconds>]";

    public static FluidLinkProbeOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 ||
            !string.Equals(args[0], "link-probe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(Usage);
        }

        var host = "127.0.0.1";
        var port = 8765;
        var timeoutMs = 5000;
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
                case "--host":
                    host = value;
                    break;
                case "--port":
                    port = ParseInt(value, "--port", 1, 65535);
                    break;
                case "--timeout-ms":
                    timeoutMs = ParseInt(value, "--timeout-ms", 100, 30000);
                    break;
                case "--out":
                    output = value;
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{args[index]}'. {Usage}");
            }
        }
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(output))
        {
            throw new ArgumentException($"Host and output are required. {Usage}");
        }
        return new(host, port, timeoutMs, output);
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
