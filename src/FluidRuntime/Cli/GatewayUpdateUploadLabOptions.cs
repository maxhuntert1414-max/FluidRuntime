using FluidRuntime.Runtime;

namespace FluidRuntime.Cli;

public sealed record GatewayUpdateUploadLabOptions(
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
    int AuthorizationMaxConcurrency,
    int AuthorizationSamplesPerLevel,
    int AuthorizationP99BudgetMs,
    bool UseHardware)
{
    public const string Usage =
        "Usage: fluidruntime gateway-update-upload-lab " +
        "--target <hook-target.exe> --hook <hook.dll> --out <report.json> " +
        "--gateway-pid <pid> --gateway-executable-sha256 <sha256> " +
        "[--host <loopback-host>] [--port <port>] [--timeout-ms <milliseconds>] " +
        "[--trial-pairs <count>] [--warmup-pairs <count>] " +
        "[--hold-ms <milliseconds>] [--gpu-timeout-ms <milliseconds>] " +
        "[--candidate-action-count <1-128>] " +
        "[--authorization-max-concurrency <1|2|4|8>] " +
        "[--authorization-samples-per-level <1-256>] " +
        "[--authorization-p99-budget-ms <milliseconds>] " +
        "[--hardware <true|false>]";

    public static GatewayUpdateUploadLabOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 ||
            !string.Equals(
                args[0],
                "gateway-update-upload-lab",
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
        var gpuTimeoutMs = 5000;
        var candidateActionCount =
            UpdateUploadElisionLabOptions.DefaultCandidateActionCount;
        var authorizationMaxConcurrency = 8;
        var authorizationSamplesPerLevel = 32;
        var authorizationP99BudgetMs = 250;
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
                        value,
                        "--gateway-pid",
                        1,
                        int.MaxValue);
                    break;
                case "--gateway-executable-sha256":
                    gatewayExecutableSha256 = RequireSha256(
                        value,
                        "--gateway-executable-sha256");
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
                    gpuTimeoutMs = ParseInt(value, "--gpu-timeout-ms", 1, 10_000);
                    break;
                case "--candidate-action-count":
                    candidateActionCount = ParseInt(
                        value,
                        "--candidate-action-count",
                        1,
                        UpdateUploadElisionLabOptions.MaximumCandidateActionCount);
                    break;
                case "--authorization-max-concurrency":
                    authorizationMaxConcurrency = ParseInt(
                        value,
                        "--authorization-max-concurrency",
                        1,
                        8);
                    break;
                case "--authorization-samples-per-level":
                    authorizationSamplesPerLevel = ParseInt(
                        value,
                        "--authorization-samples-per-level",
                        1,
                        256);
                    break;
                case "--authorization-p99-budget-ms":
                    authorizationP99BudgetMs = ParseInt(
                        value,
                        "--authorization-p99-budget-ms",
                        1,
                        30_000);
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
            string.IsNullOrWhiteSpace(host) ||
            gatewayProcessId == 0 ||
            gatewayExecutableSha256 is null)
        {
            throw new ArgumentException(
                "Target, hook, output, Gateway PID, Gateway executable SHA-256, " +
                $"and host are required. {Usage}");
        }
        if (!string.Equals(host, "127.0.0.1", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The owned gateway lab requires the exact IPv4 loopback host " +
                "127.0.0.1.");
        }
        if (authorizationMaxConcurrency is not (1 or 2 or 4 or 8))
        {
            throw new ArgumentException(
                "--authorization-max-concurrency must be 1, 2, 4, or 8.");
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
            authorizationMaxConcurrency,
            authorizationSamplesPerLevel,
            authorizationP99BudgetMs,
            useHardware);
    }

    public UpdateUploadElisionLabOptions ToNativeOptions() =>
        new(
            TargetPath,
            HookPath,
            OutputPath,
            TrialPairs,
            WarmupPairs,
            HoldMs,
            GpuTimeoutMs,
            CandidateActionCount,
            UseHardware);

    public IGatewayUpdateUploadAuthorizer CreateAuthorizer() =>
        new FluidLinkGatewayUpdateUploadAuthorizer(
            Host,
            Port,
            TimeSpan.FromMilliseconds(TimeoutMs),
            GatewayProcessId,
            GatewayExecutableSha256);

    public GatewayAuthorizationBenchmarkConfiguration
        ToAuthorizationBenchmarkConfiguration() =>
        new(
            CandidateActionCount,
            AuthorizationMaxConcurrency,
            AuthorizationSamplesPerLevel,
            AuthorizationP99BudgetMs);

    private static string RequireSha256(string value, string name)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 ||
            normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException($"{name} must be a 64-character SHA-256.");
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
