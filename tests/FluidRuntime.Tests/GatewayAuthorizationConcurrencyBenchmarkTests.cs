using System.Diagnostics;
using FluidLink;
using FluidRuntime.Native;
using FluidRuntime.Runtime;

namespace FluidRuntime.Tests;

public sealed class GatewayAuthorizationConcurrencyBenchmarkTests
{
    private const int BufferBytes = 4 * 1024 * 1024;
    private const ulong CandidateCount = 64;
    private static readonly string BinarySha256 = new('a', 64);
    private static readonly string PeerSha256 = new('b', 64);
    private static readonly DateTimeOffset PeerStartedAt =
        new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Runner_proves_exact_authorization_at_each_concurrency_level()
    {
        var authorizer = new TrackingAuthorizer(delayMilliseconds: 5);
        var configuration = new GatewayAuthorizationBenchmarkConfiguration(
            CandidateActionCount: checked((int)CandidateCount),
            MaxConcurrency: 8,
            SamplesPerLevel: 32,
            P99BudgetMilliseconds: 250);

        var report = await new GatewayAuthorizationConcurrencyBenchmarkRunner()
            .RunAsync(
                configuration,
                authorizer,
                BinarySha256,
                BinarySha256);

        Assert.Equal(
            new[] { 1, 2, 4, 8 },
            report.Levels.Select(item => item.Concurrency));
        Assert.Equal(128, report.TotalMeasuredRequestCount);
        Assert.Equal(0, report.TotalFailureCount);
        Assert.True(report.ExactDecisionsVerified);
        Assert.True(report.ContextsUnique);
        Assert.True(report.PeerIdentityStable);
        Assert.True(report.ReliabilityGatePassed);
        Assert.False(report.SharedMemoryPrototypeJustified);
        Assert.Equal(
            "retain-loopback-tcp-for-current-session-level-control",
            report.TransportDecision);
        Assert.InRange(authorizer.PeakActiveCount, 8, 8);

        var combined = GatewayUpdateUploadLabReport.Build(
                GatewayUpdateUploadLabReportTests.CreateNativeEvidence(),
                BinarySha256,
                BinarySha256)
            .AttachAuthorizationConcurrencyBenchmark(report);

        Assert.True(combined.PerformanceClaimAllowed);
        Assert.Empty(combined.PerformanceClaimBlockers);

        Assert.Throws<InvalidDataException>(() =>
            GatewayUpdateUploadLabReport.Build(
                    GatewayUpdateUploadLabReportTests.CreateNativeEvidence(),
                    BinarySha256,
                    BinarySha256)
                .AttachAuthorizationConcurrencyBenchmark(
                    report with { PeerProcessId = 43 }));
    }

    [Fact]
    public async Task Report_blocks_a_weakened_public_benchmark_gate()
    {
        var report = await new GatewayAuthorizationConcurrencyBenchmarkRunner()
            .RunAsync(
                new GatewayAuthorizationBenchmarkConfiguration(
                    CandidateActionCount: checked((int)CandidateCount),
                    MaxConcurrency: 8,
                    SamplesPerLevel: 2,
                    P99BudgetMilliseconds: 1_000),
                new TrackingAuthorizer(delayMilliseconds: 1),
                BinarySha256,
                BinarySha256);

        var combined = GatewayUpdateUploadLabReport.Build(
                GatewayUpdateUploadLabReportTests.CreateNativeEvidence(),
                BinarySha256,
                BinarySha256)
            .AttachAuthorizationConcurrencyBenchmark(report);

        Assert.False(combined.PerformanceClaimAllowed);
        Assert.Contains(
            "authorization-sample-count-below-required",
            combined.PerformanceClaimBlockers);
        Assert.Contains(
            "authorization-p99-budget-too-permissive",
            combined.PerformanceClaimBlockers);
    }

    [Fact]
    public async Task Runner_justifies_transport_investigation_when_p99_exceeds_budget()
    {
        var authorizer = new TrackingAuthorizer(delayMilliseconds: 25);
        var configuration = new GatewayAuthorizationBenchmarkConfiguration(
            CandidateActionCount: checked((int)CandidateCount),
            MaxConcurrency: 2,
            SamplesPerLevel: 4,
            P99BudgetMilliseconds: 10);

        var report = await new GatewayAuthorizationConcurrencyBenchmarkRunner()
            .RunAsync(
                configuration,
                authorizer,
                BinarySha256,
                BinarySha256);

        Assert.False(report.ReliabilityGatePassed);
        Assert.True(report.SharedMemoryPrototypeJustified);
        Assert.Equal(
            "investigate-shared-memory-transport-prototype",
            report.TransportDecision);
        Assert.Contains(
            "tcp-p99-budget-exceeded",
            report.ReliabilityBlockers);
    }

    [Fact]
    public async Task Runner_does_not_mislabel_failed_requests_as_identity_drift()
    {
        var configuration = new GatewayAuthorizationBenchmarkConfiguration(
            CandidateActionCount: checked((int)CandidateCount),
            MaxConcurrency: 1,
            SamplesPerLevel: 2,
            P99BudgetMilliseconds: 250);

        var report = await new GatewayAuthorizationConcurrencyBenchmarkRunner()
            .RunAsync(
                configuration,
                new RejectingAuthorizer(),
                BinarySha256,
                BinarySha256);

        Assert.Equal(3, report.TotalFailureCount);
        Assert.Contains("authorization-request-failures", report.ReliabilityBlockers);
        Assert.Contains("authorization-decisions-not-exact", report.ReliabilityBlockers);
        Assert.DoesNotContain("authorization-context-reuse", report.ReliabilityBlockers);
        Assert.DoesNotContain(
            "authorization-peer-identity-drift",
            report.ReliabilityBlockers);
        Assert.Equal(3, report.Levels[0].FailureCountsByType["IOException"]);
    }

    [Fact]
    public async Task Runner_rejects_peer_restart_between_concurrency_levels()
    {
        var report = await new GatewayAuthorizationConcurrencyBenchmarkRunner()
            .RunAsync(
                new GatewayAuthorizationBenchmarkConfiguration(
                    CandidateActionCount: checked((int)CandidateCount),
                    MaxConcurrency: 2,
                    SamplesPerLevel: 4,
                    P99BudgetMilliseconds: 250),
                new TrackingAuthorizer(
                    delayMilliseconds: 1,
                    driftAfterRequest: 5),
                BinarySha256,
                BinarySha256);

        Assert.All(report.Levels, level => Assert.True(level.PeerIdentityStable));
        Assert.False(report.PeerIdentityStable);
        Assert.False(report.ReliabilityGatePassed);
        Assert.Contains(
            "authorization-peer-identity-drift",
            report.ReliabilityBlockers);
    }

    private sealed class RejectingAuthorizer : IGatewayUpdateUploadAuthorizer
    {
        public Task<GatewayUpdateUploadAuthorization> AuthorizeAsync(
            GatewayUpdateUploadAuthorizationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<GatewayUpdateUploadAuthorization>(
                new IOException("synthetic connection rejection"));
    }

    private sealed class TrackingAuthorizer(
        int delayMilliseconds,
        int? driftAfterRequest = null) :
        IGatewayUpdateUploadAuthorizer
    {
        private int activeCount;
        private int nonce;
        private int peakActiveCount;

        public int PeakActiveCount => Volatile.Read(ref peakActiveCount);

        public async Task<GatewayUpdateUploadAuthorization> AuthorizeAsync(
            GatewayUpdateUploadAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref activeCount);
            UpdateMaximum(ref peakActiveCount, active);
            var started = Stopwatch.GetTimestamp();
            try
            {
                await Task.Delay(delayMilliseconds, cancellationToken);
                var requestNumber = Interlocked.Increment(ref nonce);
                var authorizationNonce = $"benchmark-{requestNumber}";
                var peerProcessId = driftAfterRequest.HasValue &&
                    requestNumber > driftAfterRequest.Value
                        ? 43
                        : 42;
                var context = FluidLinkGatewayUpdateUploadAuthorizer
                    .ComputeAuthorizationContextSha256(
                        authorizationNonce,
                        peerProcessId,
                        PeerSha256,
                        PeerStartedAt,
                        request,
                        HookRingReader.SkipRedundantUpdateSubresourceAction,
                        request.CandidateActionCount);
                var elapsed = Math.Max(
                    1,
                    (long)Math.Ceiling(
                        Stopwatch.GetElapsedTime(started).TotalMicroseconds));
                return new GatewayUpdateUploadAuthorization(
                    Protocol: FluidLinkV2Protocol.Version,
                    ContractSha256: FluidLinkV2BatchProtocol.ContractSha256,
                    WireSessionId: Guid.NewGuid().ToString("N"),
                    RuntimeSessionId: $"gateway-update-{context}",
                    request.PairIndex,
                    request.Phase,
                    AdvertisedServerName: "fluidgateway",
                    AdvertisedServerVersion: "0.67.0",
                    PeerProcessBindingVerified: true,
                    PeerCryptographicallyAuthenticated: false,
                    peerProcessId,
                    PeerExecutablePath: Path.GetFullPath("gateway.exe"),
                    PeerExecutableSha256: PeerSha256,
                    PeerProcessStartedAtUtc: PeerStartedAt,
                    authorizationNonce,
                    AuthorizationContextSha256: context,
                    AuthorizationDeadlineMilliseconds: 5_000,
                    request.TargetSha256,
                    request.HookSha256,
                    NegotiatedCapabilities:
                        (ulong)FluidLinkV2BatchProtocol.AllCapabilities,
                    HeartbeatVerified: true,
                    SeedUploadExecuted: true,
                    AllCandidateDecisionsAccepted: true,
                    AllCandidateExecutionsDeferredToNative: true,
                    CandidateDecisionOpcode:
                        (int)FluidLinkV2DecisionOpcode.DeduplicateIdenticalTransfer,
                    CandidatePolicy: "deduplicate-identical-transfer",
                    CandidateDecisionCount: request.CandidateActionCount,
                    AuthorizedLogicalBytes: checked(
                        request.ResourceBytes * request.CandidateActionCount),
                    NativeActionMask:
                        HookRingReader.SkipRedundantUpdateSubresourceAction,
                    NativeActionBudget: request.CandidateActionCount,
                    RuntimeEventCount: checked(
                        (int)request.CandidateActionCount + 7),
                    RoundTripCount: 10,
                    BytesSent: 1_168,
                    BytesReceived: 3_122,
                    AuthorizationLatencyMicroseconds: elapsed,
                    Authorized: true,
                    AuthorizationScope:
                        "owned-d3d11-process-bound-candidates-native-exact-content-final-gate",
                    NativeSafetyGuards: []);
            }
            finally
            {
                Interlocked.Decrement(ref activeCount);
            }
        }

        private static void UpdateMaximum(ref int location, int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref location);
                if (value <= current ||
                    Interlocked.CompareExchange(ref location, value, current) == current)
                {
                    return;
                }
            }
        }
    }
}
