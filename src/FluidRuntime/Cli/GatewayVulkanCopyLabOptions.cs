using FluidRuntime.Runtime;

namespace FluidRuntime.Cli;

public sealed record GatewayVulkanCopyLabOptions(
    string TargetPath, string LibraryPath, string OutputPath, int Port,
    int TimeoutMs, int GatewayProcessId, string GatewayExecutableSha256,
    int TrialPairs, int WarmupPairs, int CandidateActionCount,
    int GpuTimeoutMs, bool UseHardware, bool RequireValidation)
{
    public GatewayBackendOptions GatewayConnection { get; init; } = GatewayBackendOptions.Server;
    public const ulong BufferBytes = 4UL * 1024 * 1024;
    public const string Usage = "Usage: fluidruntime gateway-vulkan-copy-lab " +
        "--target <vulkan-transfer-target.exe> --library <vulkan-transfer.dll> " +
        "--out <report.json> --gateway-pid <pid> --gateway-executable-sha256 <sha256> " +
        "[--port <1-65535>] [--timeout-ms <100-30000>] [--trial-pairs <1-30>] " +
        "[--warmup-pairs <0-5>] [--candidate-action-count <1-128>] " +
        "[--gpu-timeout-ms <1-30000>] [--hardware <true|false>] [--validation <true|false>] " + GatewayBackendOptions.Usage;

    public static GatewayVulkanCopyLabOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var connection = GatewayBackendOptions.Extract(ref args);
        if (args.Length == 0 || args[0] != "gateway-vulkan-copy-lab" || args.Length % 2 != 1)
            throw new ArgumentException(Usage);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] allowed = ["--target", "--library", "--out", "--gateway-pid",
            "--gateway-executable-sha256", "--port", "--timeout-ms", "--trial-pairs",
            "--warmup-pairs", "--candidate-action-count", "--gpu-timeout-ms", "--hardware", "--validation"];
        for (var i = 1; i < args.Length; i += 2)
        {
            if (!allowed.Contains(args[i]) || !values.TryAdd(args[i], args[i + 1]) ||
                string.IsNullOrWhiteSpace(args[i + 1]))
                throw new ArgumentException($"Invalid, duplicate or empty option: {args[i]}");
        }
        string Required(string key) => values.TryGetValue(key, out var value)
            ? value : throw new ArgumentException($"Missing {key}. {Usage}");
        int Number(string key, int fallback, int minimum, int maximum)
        {
            if (!values.TryGetValue(key, out var text)) return fallback;
            if (!int.TryParse(text, out var number) || number < minimum || number > maximum)
                throw new ArgumentException($"{key} must be between {minimum} and {maximum}.");
            return number;
        }
        bool Boolean(string key, bool fallback)
        {
            if (!values.TryGetValue(key, out var text)) return fallback;
            return bool.TryParse(text, out var value) ? value :
                throw new ArgumentException($"{key} must be true or false.");
        }
        var sha = connection.Mode == "server" ? Required("--gateway-executable-sha256").ToLowerInvariant() : string.Empty;
        if (connection.Mode == "server" && (sha.Length != 64 || !sha.All(Uri.IsHexDigit)))
            throw new ArgumentException("Gateway SHA-256 must have 64 hexadecimal characters.");
        if (connection.Mode == "server") _ = Required("--gateway-pid");
        return new(Required("--target"), Required("--library"), Required("--out"),
            Number("--port", 8765, 1, 65535), Number("--timeout-ms", 5000, 100, 30000),
            Number("--gateway-pid", 0, 1, int.MaxValue), sha,
            Number("--trial-pairs", 10, 1, 30), Number("--warmup-pairs", 1, 0, 5),
            Number("--candidate-action-count", 128, 1, 128),
            Number("--gpu-timeout-ms", 10000, 1, 30000),
            Boolean("--hardware", true), Boolean("--validation", false))
        { GatewayConnection = connection };
    }

    public NativeTransferTopology CreateTransferTopology() =>
        NativeTransferTopology.VulkanMultiLane((ulong)CandidateActionCount);

    public FluidLinkGatewayUpdateUploadAuthorizer CreateAuthorizer() =>
        GatewayConnection.Create("127.0.0.1", Port, TimeoutMs, GatewayProcessId, GatewayExecutableSha256);
}
