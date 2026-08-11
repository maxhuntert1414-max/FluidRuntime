namespace FluidRuntime.Runtime;

public sealed record GatewayUpdateUploadLabReport(
    string Mode,
    bool TargetOwned,
    bool CooperativeLoad,
    bool RemoteInjection,
    bool FailClosed,
    string PolicyOrigin,
    string Protocol,
    string ContractSha256,
    string AdvertisedServerName,
    string AdvertisedServerVersion,
    bool PeerProcessBindingVerified,
    bool PeerCryptographicallyAuthenticated,
    int PeerProcessId,
    string PeerExecutablePath,
    string PeerExecutableSha256,
    DateTimeOffset PeerProcessStartedAtUtc,
    int AuthorizationDeadlineMilliseconds,
    string TargetSha256,
    string HookSha256,
    int AuthorizationRunCount,
    int MeasuredAuthorizationRunCount,
    long GatewayRoundTripCount,
    long GatewayCandidateDecisionCount,
    ulong AuthorizedLogicalBytesPerOptimizedRun,
    long FluidLinkBytesSent,
    long FluidLinkBytesReceived,
    MetricDistribution AuthorizationLatencyMicroseconds,
    PairedTailLatencySummary ManagedEndToEndLatencyMicroseconds,
    ulong NativeActionMask,
    ulong NativeActionBudgetPerOptimizedRun,
    bool NativeExactContentFinalGate,
    bool MutationGuardPassed,
    bool GenerationGuardPassed,
    bool ContentEquivalent,
    bool RollbackRestoredInAllRuns,
    string ClaimScope,
    string PerformanceClaimBasis,
    bool PerformanceClaimAllowed,
    IReadOnlyList<string> PerformanceClaimBlockers,
    GatewayAuthorizationConcurrencyBenchmarkReport? AuthorizationConcurrencyBenchmark,
    UpdateUploadElisionLabReport NativeEvidence,
    IReadOnlyList<GatewayUpdateUploadAuthorization> Authorizations)
{
    private const int RequiredAuthorizationConcurrency = 8;
    private const int RequiredAuthorizationSamplesPerLevel = 32;
    private const int MaximumAuthorizationP99BudgetMilliseconds = 250;

    public static GatewayUpdateUploadLabReport Build(
        UpdateUploadElisionLabReport nativeEvidence,
        string targetSha256,
        string hookSha256)
    {
        ArgumentNullException.ThrowIfNull(nativeEvidence);
        RequireSha256(targetSha256, nameof(targetSha256));
        RequireSha256(hookSha256, nameof(hookSha256));
        var authorizations = nativeEvidence.Trials
            .Select(item => item.Optimized.GatewayAuthorization)
            .ToArray();
        var expectedCount = nativeEvidence.TrialPairsRequested +
            nativeEvidence.WarmupPairs;
        if (authorizations.Length != expectedCount ||
            authorizations.Any(item => item is null) ||
            nativeEvidence.Trials.Any(item =>
                item.Baseline.GatewayAuthorization is not null ||
                item.Baseline.PublishedPolicyExpiresAtQpc != 0 ||
                item.Baseline.PublishedPolicyActionMask != 0 ||
                item.Baseline.PublishedPolicyActionBudget != 0))
        {
            throw new InvalidDataException(
                "Gateway-managed trace is missing exact per-run authorization evidence.");
        }

        var verified = authorizations.Select(item => item!).ToArray();
        for (var index = 0; index < verified.Length; ++index)
        {
            var authorization = verified[index];
            var trial = nativeEvidence.Trials[index];
            authorization.EnsureMatchesNativePolicy(
                (ulong)nativeEvidence.BufferBytes,
                (ulong)nativeEvidence.RedundantUpdateCountPerOptimizedRun,
                trial.PairIndex,
                trial.Phase,
                targetSha256,
                hookSha256);
        }
        if (nativeEvidence.Trials.Any(item =>
            item.Optimized.PublishedPolicyExpiresAtQpc <= 0 ||
            item.Optimized.PublishedPolicyActionMask !=
                item.Optimized.GatewayAuthorization!.NativeActionMask ||
            item.Optimized.PublishedPolicyActionBudget !=
                item.Optimized.GatewayAuthorization.NativeActionBudget))
        {
            throw new InvalidDataException(
                "Published native policy does not match its Gateway authorization.");
        }
        if (verified.Any(item =>
                !item.PeerProcessBindingVerified ||
                item.PeerCryptographicallyAuthenticated ||
                item.TargetSha256 != targetSha256 ||
                item.HookSha256 != hookSha256) ||
            verified.Select(item => item.AuthorizationContextSha256)
                .Distinct(StringComparer.Ordinal).Count() != verified.Length ||
            verified.Select(item => item.ContractSha256).Distinct().Count() != 1 ||
            verified.Select(item => item.AdvertisedServerName).Distinct().Count() != 1 ||
            verified.Select(item => item.AdvertisedServerVersion).Distinct().Count() != 1 ||
            verified.Select(item => item.PeerProcessId).Distinct().Count() != 1 ||
            verified.Select(item => item.PeerExecutablePath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1 ||
            verified.Select(item => item.PeerExecutableSha256).Distinct().Count() != 1 ||
            verified.Select(item => item.PeerProcessStartedAtUtc).Distinct().Count() != 1 ||
            verified.Select(item => item.AuthorizationDeadlineMilliseconds)
                .Distinct().Count() != 1 ||
            verified.Select(item => item.NativeActionMask).Distinct().Count() != 1 ||
            verified.Select(item => item.NativeActionBudget).Distinct().Count() != 1)
        {
            throw new InvalidDataException(
                "Gateway authorization identity or native policy drifted between runs.");
        }

        var measuredAuthorizationCount = nativeEvidence.Trials.Count(item =>
            item.IncludedInStatistics &&
            item.Optimized.GatewayAuthorization is not null);
        if (measuredAuthorizationCount != nativeEvidence.IncludedTrialPairs)
        {
            throw new InvalidDataException(
                "Measured native runs and Gateway authorization runs do not match.");
        }
        if (nativeEvidence.Trials.Any(item =>
                item.Baseline.ManagedEndToEndElapsedMicroseconds <= 0 ||
                item.Optimized.ManagedEndToEndElapsedMicroseconds <= 0 ||
                item.Optimized.ManagedEndToEndElapsedMicroseconds <
                    item.Optimized.GatewayAuthorization!
                        .AuthorizationLatencyMicroseconds))
        {
            throw new InvalidDataException(
                "Managed end-to-end timing does not contain Gateway authorization.");
        }

        var latency = Distribution(
            verified.Select(item => (double)item.AuthorizationLatencyMicroseconds));
        var measuredTrials = nativeEvidence.Trials
            .Where(item => item.IncludedInStatistics)
            .ToArray();
        var managedEndToEnd = GatewayLatencyStatistics.SummarizePairs(
            measuredTrials.Select(item =>
                item.Baseline.ManagedEndToEndElapsedMicroseconds),
            measuredTrials.Select(item =>
                item.Optimized.ManagedEndToEndElapsedMicroseconds));
        var requiredWins = (int)Math.Ceiling(measuredTrials.Length * 0.8);
        var performanceClaimBlockers = nativeEvidence.PerformanceClaimBlockers
            .ToList();
        if (managedEndToEnd.Delta.P50 >= 0 ||
            managedEndToEnd.Delta.P95 >= 0 ||
            managedEndToEnd.Delta.P99 >= 0 ||
            managedEndToEnd.OptimizedLowerCount < requiredWins)
        {
            performanceClaimBlockers.Add(
                "managed-end-to-end-improvement-not-consistent");
        }
        performanceClaimBlockers.Add(
            "concurrent-authorization-benchmark-missing");
        var distinctBlockers = performanceClaimBlockers
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new GatewayUpdateUploadLabReport(
            Mode: "fluidruntime-gateway-update-upload-control-trace-v0.19.0",
            nativeEvidence.TargetOwned,
            nativeEvidence.CooperativeLoad,
            nativeEvidence.RemoteInjection,
            FailClosed: true,
            PolicyOrigin: "fluidgateway-live-fluidlink-v2-decisions",
            verified[0].Protocol,
            verified[0].ContractSha256,
            verified[0].AdvertisedServerName,
            verified[0].AdvertisedServerVersion,
            PeerProcessBindingVerified: true,
            PeerCryptographicallyAuthenticated: false,
            verified[0].PeerProcessId,
            verified[0].PeerExecutablePath,
            verified[0].PeerExecutableSha256,
            verified[0].PeerProcessStartedAtUtc,
            verified[0].AuthorizationDeadlineMilliseconds,
            targetSha256,
            hookSha256,
            AuthorizationRunCount: verified.Length,
            MeasuredAuthorizationRunCount: measuredAuthorizationCount,
            GatewayRoundTripCount: verified.Sum(item => (long)item.RoundTripCount),
            GatewayCandidateDecisionCount: verified.Sum(
                item => checked((long)item.CandidateDecisionCount)),
            AuthorizedLogicalBytesPerOptimizedRun:
                verified[0].AuthorizedLogicalBytes,
            FluidLinkBytesSent: verified.Sum(item => item.BytesSent),
            FluidLinkBytesReceived: verified.Sum(item => item.BytesReceived),
            AuthorizationLatencyMicroseconds: latency,
            ManagedEndToEndLatencyMicroseconds: managedEndToEnd,
            NativeActionMask: verified[0].NativeActionMask,
            NativeActionBudgetPerOptimizedRun: verified[0].NativeActionBudget,
            NativeExactContentFinalGate: true,
            nativeEvidence.MutationGuardPassed,
            nativeEvidence.GenerationGuardPassed,
            nativeEvidence.ContentEquivalent,
            nativeEvidence.RollbackRestoredInAllRuns,
            ClaimScope:
                "owned-d3d11-process-bound-fluidgateway-authorized-full-buffer-update-subresource-only",
            PerformanceClaimBasis:
                "paired-managed-end-to-end-tail-awaiting-concurrent-authorization",
            PerformanceClaimAllowed: false,
            PerformanceClaimBlockers: distinctBlockers,
            AuthorizationConcurrencyBenchmark: null,
            nativeEvidence,
            verified);
    }

    public GatewayUpdateUploadLabReport AttachAuthorizationConcurrencyBenchmark(
        GatewayAuthorizationConcurrencyBenchmarkReport benchmark)
    {
        ArgumentNullException.ThrowIfNull(benchmark);
        if (AuthorizationConcurrencyBenchmark is not null)
        {
            throw new InvalidOperationException(
                "An authorization concurrency benchmark is already attached.");
        }
        if (benchmark.TargetSha256 != TargetSha256 ||
            benchmark.HookSha256 != HookSha256 ||
            benchmark.CandidateActionCount !=
                NativeEvidence.RedundantUpdateCountPerOptimizedRun)
        {
            throw new InvalidDataException(
                "Authorization concurrency evidence does not match the native run.");
        }
        if ((benchmark.ReliabilityGatePassed && benchmark.PeerProcessId == 0) ||
            (benchmark.PeerProcessId != 0 &&
                (benchmark.Protocol != Protocol ||
                 benchmark.ContractSha256 != ContractSha256 ||
                 benchmark.AdvertisedServerName != AdvertisedServerName ||
                 benchmark.AdvertisedServerVersion != AdvertisedServerVersion ||
                 benchmark.PeerProcessId != PeerProcessId ||
                 !string.Equals(
                     benchmark.PeerExecutablePath,
                     PeerExecutablePath,
                     StringComparison.OrdinalIgnoreCase) ||
                 benchmark.PeerExecutableSha256 != PeerExecutableSha256 ||
                 benchmark.PeerProcessStartedAtUtc != PeerProcessStartedAtUtc)))
        {
            throw new InvalidDataException(
                "Authorization concurrency peer does not match the native run.");
        }

        var blockers = PerformanceClaimBlockers
            .Where(item => item != "concurrent-authorization-benchmark-missing")
            .ToList();
        if (benchmark.MaxConcurrency < RequiredAuthorizationConcurrency)
        {
            blockers.Add("authorization-concurrency-below-required-level");
        }
        if (benchmark.SamplesPerLevel < RequiredAuthorizationSamplesPerLevel)
        {
            blockers.Add("authorization-sample-count-below-required");
        }
        if (benchmark.P99BudgetMilliseconds >
            MaximumAuthorizationP99BudgetMilliseconds)
        {
            blockers.Add("authorization-p99-budget-too-permissive");
        }
        if (!benchmark.ReliabilityGatePassed)
        {
            blockers.AddRange(benchmark.ReliabilityBlockers.Select(item =>
                $"concurrent-authorization-{item}"));
        }
        var distinctBlockers = blockers
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return this with
        {
            PerformanceClaimBasis =
                "paired-managed-end-to-end-and-concurrent-authorization-tail",
            PerformanceClaimAllowed = distinctBlockers.Length == 0,
            PerformanceClaimBlockers = distinctBlockers,
            AuthorizationConcurrencyBenchmark = benchmark
        };
    }

    private static void RequireSha256(string value, string name)
    {
        if (value.Length != 64 || value.Any(character =>
            !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A lowercase or uppercase SHA-256 is required.", name);
        }
    }

    private static MetricDistribution Distribution(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
        {
            throw new ArgumentException("Authorization latency values are required.");
        }
        return new MetricDistribution(
            ordered.Length,
            Math.Round(ordered[0], 3),
            Percentile(ordered, 0.50),
            Percentile(ordered, 0.95),
            Math.Round(ordered[^1], 3),
            Math.Round(ordered.Average(), 3));
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        var rank = percentile * (ordered.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return Math.Round(
            ordered[lower] + (ordered[upper] - ordered[lower]) * (rank - lower),
            3);
    }
}
