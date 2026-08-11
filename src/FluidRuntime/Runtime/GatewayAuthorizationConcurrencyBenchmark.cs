using System.Collections.Concurrent;
using System.Diagnostics;
using FluidRuntime.Cli;
using FluidRuntime.Native;

namespace FluidRuntime.Runtime;

public sealed record GatewayAuthorizationBenchmarkConfiguration(
    int CandidateActionCount,
    int MaxConcurrency,
    int SamplesPerLevel,
    int P99BudgetMilliseconds);

public sealed record GatewayAuthorizationConcurrencyLevelReport(
    int Concurrency,
    int WarmupRequestCount,
    int MeasuredRequestCount,
    int FailureCount,
    double WallTimeMilliseconds,
    double ThroughputRequestsPerSecond,
    TailLatencyDistribution LatencyMicroseconds,
    long RoundTripCount,
    long CandidateDecisionCount,
    long BytesSent,
    long BytesReceived,
    bool ExactDecisionsVerified,
    bool ContextsUnique,
    bool PeerIdentityStable,
    IReadOnlyDictionary<string, int> FailureCountsByType,
    IReadOnlyDictionary<string, int> FailureCountsByReason,
    IReadOnlyDictionary<string, int> FailureCountsByCompletedRoundTrips);

public sealed record GatewayAuthorizationConcurrencyBenchmarkReport(
    string Mode,
    int CandidateActionCount,
    int MaxConcurrency,
    int SamplesPerLevel,
    int P99BudgetMilliseconds,
    string TargetSha256,
    string HookSha256,
    string Protocol,
    string ContractSha256,
    string AdvertisedServerName,
    string AdvertisedServerVersion,
    int PeerProcessId,
    string PeerExecutablePath,
    string PeerExecutableSha256,
    DateTimeOffset PeerProcessStartedAtUtc,
    int TotalWarmupRequestCount,
    int TotalMeasuredRequestCount,
    int TotalFailureCount,
    long TotalRoundTripCount,
    long TotalCandidateDecisionCount,
    long TotalBytesSent,
    long TotalBytesReceived,
    bool ExactDecisionsVerified,
    bool ContextsUnique,
    bool PeerIdentityStable,
    bool ReliabilityGatePassed,
    bool SharedMemoryPrototypeJustified,
    string TransportDecision,
    IReadOnlyList<string> ReliabilityBlockers,
    IReadOnlyList<GatewayAuthorizationConcurrencyLevelReport> Levels);

public sealed class GatewayAuthorizationConcurrencyBenchmarkRunner
{
    private static readonly int[] SupportedConcurrencyLevels = [1, 2, 4, 8];

    public async Task<GatewayAuthorizationConcurrencyBenchmarkReport> RunAsync(
        GatewayAuthorizationBenchmarkConfiguration configuration,
        IGatewayUpdateUploadAuthorizer authorizer,
        string targetSha256,
        string hookSha256,
        CancellationToken cancellationToken = default)
    {
        Validate(configuration, targetSha256, hookSha256);
        ArgumentNullException.ThrowIfNull(authorizer);

        var nextPairIndex = -1;
        var levels = new List<GatewayAuthorizationConcurrencyLevelReport>();
        var observedAuthorizations = new List<GatewayUpdateUploadAuthorization>();
        foreach (var concurrency in SupportedConcurrencyLevels.Where(
            value => value <= configuration.MaxConcurrency))
        {
            var warmup = await RunWaveAsync(
                concurrency,
                requestCount: concurrency,
                phase: "warmup",
                cancellationToken);
            var measuredStartedAt = Stopwatch.GetTimestamp();
            var measured = await RunWaveAsync(
                concurrency,
                configuration.SamplesPerLevel,
                phase: "measured",
                cancellationToken);
            var wallTime = Stopwatch.GetElapsedTime(measuredStartedAt);
            var allSamples = warmup.Concat(measured).ToArray();
            var successfulMeasured = measured
                .Where(item => item.Authorization is not null)
                .ToArray();
            observedAuthorizations.AddRange(allSamples
                .Select(item => item.Authorization)
                .Where(item => item is not null)
                .Cast<GatewayUpdateUploadAuthorization>());
            var failureCount = allSamples.Count(item => item.Authorization is null);
            levels.Add(new GatewayAuthorizationConcurrencyLevelReport(
                concurrency,
                warmup.Length,
                measured.Length,
                failureCount,
                WallTimeMilliseconds: Math.Round(wallTime.TotalMilliseconds, 3),
                ThroughputRequestsPerSecond: Math.Round(
                    measured.Length / Math.Max(wallTime.TotalSeconds, 0.000_001),
                    3),
                GatewayLatencyStatistics.Distribution(
                    successfulMeasured.Select(item => item.ElapsedMicroseconds)),
                RoundTripCount: successfulMeasured.Sum(item =>
                    (long)item.Authorization!.RoundTripCount),
                CandidateDecisionCount: successfulMeasured.Sum(item =>
                    checked((long)item.Authorization!.CandidateDecisionCount)),
                BytesSent: successfulMeasured.Sum(item => item.Authorization!.BytesSent),
                BytesReceived: successfulMeasured.Sum(item =>
                    item.Authorization!.BytesReceived),
                ExactDecisionsVerified: failureCount == 0,
                ContextsUnique: AllContextsUnique(allSamples),
                PeerIdentityStable: IsPeerIdentityStable(allSamples),
                FailureCountsByType: allSamples
                    .Where(item => item.FailureType is not null)
                    .GroupBy(item => item.FailureType!, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Count(),
                        StringComparer.Ordinal),
                FailureCountsByReason: allSamples
                    .Where(item => item.FailureReason is not null)
                    .GroupBy(item => item.FailureReason!, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Count(),
                        StringComparer.Ordinal),
                FailureCountsByCompletedRoundTrips: allSamples
                    .Where(item => item.FailureType is not null)
                    .GroupBy(
                        item => item.CompletedRoundTrips.ToString(),
                        StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Count(),
                        StringComparer.Ordinal)));
        }

        var contextsUnique = AllContextsUnique(observedAuthorizations);
        var peerIdentityStable = IsPeerIdentityStable(observedAuthorizations);
        var blockers = new List<string>();
        if (levels.Any(item => item.FailureCount != 0))
        {
            blockers.Add("authorization-request-failures");
        }
        if (levels.Any(item => !item.ExactDecisionsVerified))
        {
            blockers.Add("authorization-decisions-not-exact");
        }
        if (levels.Any(item => !item.ContextsUnique) || !contextsUnique)
        {
            blockers.Add("authorization-context-reuse");
        }
        if (levels.Any(item => !item.PeerIdentityStable) || !peerIdentityStable)
        {
            blockers.Add("authorization-peer-identity-drift");
        }
        if (levels.Any(item => item.LatencyMicroseconds.P99 >
            configuration.P99BudgetMilliseconds * 1000d))
        {
            blockers.Add("tcp-p99-budget-exceeded");
        }

        var nonTailBlocker = blockers.Any(item =>
            item != "tcp-p99-budget-exceeded");
        var sharedMemoryPrototypeJustified = !nonTailBlocker &&
            blockers.Contains("tcp-p99-budget-exceeded", StringComparer.Ordinal);
        var transportDecision = blockers.Count == 0
            ? "retain-loopback-tcp-for-current-session-level-control"
            : sharedMemoryPrototypeJustified
                ? "investigate-shared-memory-transport-prototype"
                : "repair-authorization-reliability-before-transport-decision";
        var peer = observedAuthorizations.FirstOrDefault();
        return new GatewayAuthorizationConcurrencyBenchmarkReport(
            Mode: "fluidruntime-gateway-authorization-concurrency-v0.19.0",
            configuration.CandidateActionCount,
            configuration.MaxConcurrency,
            configuration.SamplesPerLevel,
            configuration.P99BudgetMilliseconds,
            targetSha256,
            hookSha256,
            Protocol: peer?.Protocol ?? string.Empty,
            ContractSha256: peer?.ContractSha256 ?? string.Empty,
            AdvertisedServerName: peer?.AdvertisedServerName ?? string.Empty,
            AdvertisedServerVersion: peer?.AdvertisedServerVersion ?? string.Empty,
            PeerProcessId: peer?.PeerProcessId ?? 0,
            PeerExecutablePath: peer?.PeerExecutablePath ?? string.Empty,
            PeerExecutableSha256: peer?.PeerExecutableSha256 ?? string.Empty,
            PeerProcessStartedAtUtc: peer?.PeerProcessStartedAtUtc ?? default,
            TotalWarmupRequestCount: levels.Sum(item => item.WarmupRequestCount),
            TotalMeasuredRequestCount: levels.Sum(item => item.MeasuredRequestCount),
            TotalFailureCount: levels.Sum(item => item.FailureCount),
            TotalRoundTripCount: levels.Sum(item => item.RoundTripCount),
            TotalCandidateDecisionCount: levels.Sum(item =>
                item.CandidateDecisionCount),
            TotalBytesSent: levels.Sum(item => item.BytesSent),
            TotalBytesReceived: levels.Sum(item => item.BytesReceived),
            ExactDecisionsVerified: levels.All(item => item.ExactDecisionsVerified),
            ContextsUnique: contextsUnique,
            PeerIdentityStable: peerIdentityStable,
            ReliabilityGatePassed: blockers.Count == 0,
            sharedMemoryPrototypeJustified,
            transportDecision,
            blockers,
            levels);

        async Task<AuthorizationSample[]> RunWaveAsync(
            int concurrency,
            int requestCount,
            string phase,
            CancellationToken token)
        {
            var samples = new ConcurrentBag<AuthorizationSample>();
            var nextSample = -1;
            var workers = Enumerable.Range(0, Math.Min(concurrency, requestCount))
                .Select(async _ =>
                {
                    while (true)
                    {
                        var sampleIndex = Interlocked.Increment(ref nextSample);
                        if (sampleIndex >= requestCount)
                        {
                            return;
                        }

                        var pairIndex = Interlocked.Increment(ref nextPairIndex);
                        var request = new GatewayUpdateUploadAuthorizationRequest(
                            pairIndex,
                            phase,
                            UpdateUploadElisionLabOptions.BufferBytes,
                            checked((ulong)configuration.CandidateActionCount),
                            targetSha256,
                            hookSha256);
                        var startedAt = Stopwatch.GetTimestamp();
                        try
                        {
                            var authorization = await authorizer.AuthorizeAsync(
                                request,
                                token);
                            authorization.EnsureMatchesNativePolicy(
                                UpdateUploadElisionLabOptions.BufferBytes,
                                checked((ulong)configuration.CandidateActionCount),
                                pairIndex,
                                phase,
                                targetSha256,
                                hookSha256);
                            samples.Add(new AuthorizationSample(
                                ElapsedMicroseconds(startedAt),
                                authorization,
                                FailureType: null,
                                FailureReason: null,
                                CompletedRoundTrips: authorization.RoundTripCount));
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            samples.Add(new AuthorizationSample(
                                ElapsedMicroseconds(startedAt),
                                Authorization: null,
                                FailureType: exception is
                                    GatewayUpdateUploadAuthorizationFailureException details
                                        ? details.FailureType
                                        : exception.GetType().Name,
                                FailureReason: $"{exception.GetType().Name}: " +
                                    exception.Message,
                                CompletedRoundTrips: exception is
                                    GatewayUpdateUploadAuthorizationFailureException failure
                                        ? failure.CompletedRoundTrips
                                        : 0));
                        }
                    }
                });
            await Task.WhenAll(workers);
            return samples.ToArray();
        }
    }

    private static bool AllContextsUnique(
        IReadOnlyList<AuthorizationSample> samples)
    {
        var authorizations = samples
            .Select(item => item.Authorization)
            .Where(item => item is not null)
            .Cast<GatewayUpdateUploadAuthorization>()
            .ToArray();
        return AllContextsUnique(authorizations);
    }

    private static bool AllContextsUnique(
        IReadOnlyList<GatewayUpdateUploadAuthorization> authorizations) =>
        authorizations
            .Select(item => item.AuthorizationContextSha256)
            .Distinct(StringComparer.Ordinal)
            .Count() == authorizations.Count;

    private static bool IsPeerIdentityStable(
        IReadOnlyList<AuthorizationSample> samples)
    {
        var authorizations = samples
            .Select(item => item.Authorization)
            .Where(item => item is not null)
            .Cast<GatewayUpdateUploadAuthorization>()
            .ToArray();
        return IsPeerIdentityStable(authorizations);
    }

    private static bool IsPeerIdentityStable(
        IReadOnlyList<GatewayUpdateUploadAuthorization> authorizations)
    {
        return authorizations.Count == 0 ||
            authorizations.Select(item => new
            {
                item.Protocol,
                item.ContractSha256,
                item.AdvertisedServerName,
                item.AdvertisedServerVersion,
                item.PeerProcessId,
                PeerExecutablePath = item.PeerExecutablePath.ToUpperInvariant(),
                item.PeerExecutableSha256,
                item.PeerProcessStartedAtUtc
            }).Distinct().Count() == 1;
    }

    private static void Validate(
        GatewayAuthorizationBenchmarkConfiguration configuration,
        string targetSha256,
        string hookSha256)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.CandidateActionCount is < 1 or
                > UpdateUploadElisionLabOptions.MaximumCandidateActionCount ||
            !SupportedConcurrencyLevels.Contains(configuration.MaxConcurrency) ||
            configuration.SamplesPerLevel is < 1 or > 256 ||
            configuration.P99BudgetMilliseconds is < 1 or > 30_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "Authorization benchmark configuration is outside its bounds.");
        }
        RequireSha256(targetSha256, nameof(targetSha256));
        RequireSha256(hookSha256, nameof(hookSha256));
    }

    private static void RequireSha256(string value, string name)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A 64-character SHA-256 is required.", name);
        }
    }

    private static long ElapsedMicroseconds(long startedAt) =>
        Math.Max(
            1,
            checked((long)Math.Ceiling(
                Stopwatch.GetElapsedTime(startedAt).TotalMicroseconds)));

    private sealed record AuthorizationSample(
        long ElapsedMicroseconds,
        GatewayUpdateUploadAuthorization? Authorization,
        string? FailureType,
        string? FailureReason,
        int CompletedRoundTrips);
}
