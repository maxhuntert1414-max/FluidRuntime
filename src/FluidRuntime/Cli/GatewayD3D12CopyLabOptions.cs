using FluidRuntime.Runtime;

namespace FluidRuntime.Cli;

public sealed record GatewayD3D12CopyLabOptions(
    string TargetPath,
    string HookPath,
    string OutputPath,
    string Host,
    int Port,
    int TimeoutMs,
    int GatewayProcessId,
    string GatewayExecutableSha256,
    int TrialPairs,
    int WarmupPairs,
    int HoldMs,
    int GpuTimeoutMs,
    int CandidateActionCount,
    bool UseHardware)
{
    public const ulong BufferBytes = 4UL * 1024UL * 1024UL;
    public const ulong UploadResourceBytes = 2 * BufferBytes;
    public const ulong SourceSnapshotBytes = 2 * UploadResourceBytes;
    public const ulong RetainedCapacityBytes = 2 * BufferBytes;
    public const int MaximumCandidateActionCount = 128;

    public const string Usage =
        "Usage: fluidruntime gateway-d3d12-copy-lab " +
        "--target <d3d12-transfer-target.exe> " +
        "--hook <d3d12-transfer-hook.dll> " +
        "--out <report.json> --gateway-pid <pid> " +
        "--gateway-executable-sha256 <sha256> " +
        "[--host 127.0.0.1] [--port <port>] [--timeout-ms <milliseconds>] " +
        "[--trial-pairs <1-30>] [--warmup-pairs <0-5>] " +
        "[--hold-ms <1-5000>] [--gpu-timeout-ms <1-30000>] " +
        "[--candidate-action-count <1-128>] [--hardware <true|false>]";

    public static GatewayD3D12CopyLabOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 ||
            !string.Equals(
                args[0],
                "gateway-d3d12-copy-lab",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(Usage);
        }

        string? target = null;
        string? hook = null;
        string? output = null;
        var host = "127.0.0.1";
        var port = 8765;
        var timeoutMs = 5000;
        var gatewayProcessId = 0;
        string? gatewayExecutableSha256 = null;
        var trialPairs = 10;
        var warmupPairs = 1;
        var holdMs = 50;
        var gpuTimeoutMs = 10000;
        var candidateActionCount = MaximumCandidateActionCount;
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
                case "--host": host = value; break;
                case "--port": port = ParseInt(value, "--port", 1, 65_535); break;
                case "--timeout-ms":
                    timeoutMs = ParseInt(value, "--timeout-ms", 100, 30_000);
                    break;
                case "--gateway-pid":
                    gatewayProcessId = ParseInt(
                        value, "--gateway-pid", 1, int.MaxValue);
                    break;
                case "--gateway-executable-sha256":
                    gatewayExecutableSha256 = RequireSha256(value);
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
                    gpuTimeoutMs = ParseInt(value, "--gpu-timeout-ms", 1, 30_000);
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
            string.IsNullOrWhiteSpace(output) ||
            gatewayProcessId == 0 ||
            gatewayExecutableSha256 is null)
        {
            throw new ArgumentException(
                "Target, hook, output, Gateway PID, and Gateway executable " +
                $"SHA-256 are required. {Usage}");
        }
        if (!string.Equals(host, "127.0.0.1", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The owned D3D12 lab requires exact IPv4 loopback.");
        }

        return new(
            target,
            hook,
            output,
            host,
            port,
            timeoutMs,
            gatewayProcessId,
            gatewayExecutableSha256,
            trialPairs,
            warmupPairs,
            holdMs,
            gpuTimeoutMs,
            candidateActionCount,
            useHardware);
    }

    public IGatewayUpdateUploadAuthorizer CreateAuthorizer() =>
        new FluidLinkGatewayUpdateUploadAuthorizer(
            Host,
            Port,
            TimeSpan.FromMilliseconds(TimeoutMs),
            GatewayProcessId,
            GatewayExecutableSha256);

    public NativeTransferTopology CreateTransferTopology() =>
        NativeTransferTopology.D3D12MultiLane((ulong)CandidateActionCount);

    private static string RequireSha256(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "--gateway-executable-sha256 must be a 64-character SHA-256.");
        }
        return normalized;
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
