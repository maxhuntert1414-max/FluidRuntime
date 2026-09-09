using FluidRuntime.Runtime;

namespace FluidRuntime.Cli;

public sealed record GatewayReadbackLabOptions(
    ReadbackElisionLabOptions Native,
    GatewayBackendOptions Connection,
    string Host,
    int Port,
    int TimeoutMs,
    int GatewayProcessId,
    string GatewayExecutableSha256)
{
    public const string Usage =
        "Usage: fluidruntime gateway-readback-lab --target <hook-target.exe> --hook <hook.dll> " +
        "--out <report.json> [--trial-pairs <1-30>] [--warmup-pairs <0-5>] " +
        "[--hold-ms <1-5000>] [--gpu-timeout-ms <1-10000>] [--hardware <true|false>] " +
        "[--host 127.0.0.1] [--port <port>] [--timeout-ms <100-30000>] " +
        "[--gateway-pid <pid> --gateway-executable-sha256 <sha256>] " + GatewayBackendOptions.Usage;

    public static GatewayReadbackLabOptions Parse(string[] args)
    {
        var connection = GatewayBackendOptions.Extract(ref args);
        if (!string.Equals(args[0], "gateway-readback-lab", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(Usage);
        var native = new List<string> { "readback-elision-lab" };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var host = "127.0.0.1";
        var port = 8765;
        var timeout = 5000;
        var pid = 0;
        var sha = string.Empty;
        for (var i = 1; i < args.Length; i += 2)
        {
            if (!seen.Add(args[i])) throw new ArgumentException($"Duplicate option: {args[i]}");
            var value = args[i + 1];
            switch (args[i])
            {
                case "--host": host = value; break;
                case "--port": port = Number(value, args[i], 1, 65535); break;
                case "--timeout-ms": timeout = Number(value, args[i], 100, 30000); break;
                case "--gateway-pid": pid = Number(value, args[i], 1, int.MaxValue); break;
                case "--gateway-executable-sha256":
                    if (value.Length != 64 || !value.All(Uri.IsHexDigit))
                        throw new ArgumentException("Gateway executable SHA-256 must have 64 hexadecimal characters.");
                    sha = value.ToLowerInvariant();
                    break;
                default: native.AddRange([args[i], value]); break;
            }
        }
        if (host != "127.0.0.1") throw new ArgumentException("Readback requires exact IPv4 loopback.");
        if (connection.Mode == "server" && (pid == 0 || sha.Length == 0))
            throw new ArgumentException("Server mode requires Gateway PID and executable SHA-256.");
        return new(ReadbackElisionLabOptions.Parse(native.ToArray()), connection, host, port, timeout, pid, sha);
    }

    public FluidLinkGatewayUpdateUploadAuthorizer CreateAuthorizer() =>
        Connection.Create(Host, Port, TimeoutMs, GatewayProcessId, GatewayExecutableSha256);

    private static int Number(string value, string option, int minimum, int maximum)
    {
        if (!int.TryParse(value, out var number) || number < minimum || number > maximum)
            throw new ArgumentException($"{option} must be between {minimum} and {maximum}.");
        return number;
    }
}
