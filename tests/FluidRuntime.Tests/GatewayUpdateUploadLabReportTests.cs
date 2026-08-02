using FluidLink;
using FluidRuntime.Native;
using FluidRuntime.Runtime;
using System.Text.Json;

namespace FluidRuntime.Tests;

public sealed class GatewayUpdateUploadLabReportTests
{
    private const int BufferBytes = 4 * 1024 * 1024;
    private const ulong CandidateCount = 64;
    private static readonly string BinarySha256 = new('a', 64);
    private static readonly string PeerSha256 = new('b', 64);
    private static readonly DateTimeOffset PeerStartedAt =
        new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_blocks_performance_when_native_evidence_allows_it()
    {
        var nativeEvidence = NativeEvidence();

        Assert.True(nativeEvidence.PerformanceClaimAllowed);

        var report = GatewayUpdateUploadLabReport.Build(
            nativeEvidence,
            BinarySha256,
            BinarySha256);

        Assert.False(report.PerformanceClaimAllowed);
        Assert.Contains(
            "gateway-authorization-outside-native-timing-window",
            report.PerformanceClaimBlockers);
    }

    [Fact]
    public void Build_rejects_action_mask_or_budget_drift_from_authorization()
    {
        var nativeEvidence = NativeEvidence();
        var trial = Assert.Single(nativeEvidence.Trials);
        UpdateUploadElisionRunReport[] mismatchedRuns =
        [
            trial.Optimized with { PublishedPolicyActionMask = 0 },
            trial.Optimized with { PublishedPolicyActionBudget = CandidateCount - 1 }
        ];

        foreach (var optimized in mismatchedRuns)
        {
            var drifted = nativeEvidence with
            {
                Trials = [trial with { Optimized = optimized }]
            };

            Assert.Throws<InvalidDataException>(() =>
                GatewayUpdateUploadLabReport.Build(
                    drifted,
                    BinarySha256,
                    BinarySha256));
        }
    }

    [Fact]
    public void Build_rejects_baseline_with_any_published_policy_marker()
    {
        var nativeEvidence = NativeEvidence();
        var trial = Assert.Single(nativeEvidence.Trials);
        UpdateUploadElisionRunReport[] invalidBaselines =
        [
            trial.Baseline with { PublishedPolicyExpiresAtQpc = 1 },
            trial.Baseline with
            {
                PublishedPolicyActionMask =
                    HookRingReader.SkipRedundantUpdateSubresourceAction
            },
            trial.Baseline with { PublishedPolicyActionBudget = CandidateCount }
        ];

        foreach (var baseline in invalidBaselines)
        {
            var invalid = nativeEvidence with
            {
                Trials = [trial with { Baseline = baseline }]
            };

            Assert.Throws<InvalidDataException>(() =>
                GatewayUpdateUploadLabReport.Build(
                    invalid,
                    BinarySha256,
                    BinarySha256));
        }
    }

    private static UpdateUploadElisionLabReport NativeEvidence()
    {
        var authorization = Authorization();
        var baseline = Run(optimized: false);
        var optimized = Run(optimized: true) with
        {
            GatewayAuthorization = authorization,
            PublishedPolicyExpiresAtQpc = 123_456,
            PublishedPolicyActionMask = authorization.NativeActionMask,
            PublishedPolicyActionBudget = authorization.NativeActionBudget
        };
        var trial = new UpdateUploadElisionTrialReport(
            PairIndex: 0,
            Phase: "measured",
            IncludedInStatistics: true,
            ExecutionOrder: "baseline-then-optimized",
            ContentEquivalent: true,
            RollbackRestoredInBothRuns: true,
            AdapterIdentityMatched: true,
            BaselineCpuMicroseconds: 1_000,
            OptimizedCpuMicroseconds: 500,
            BaselineGpuMicroseconds: 900,
            OptimizedGpuMicroseconds: 450,
            baseline,
            optimized);

        return new UpdateUploadElisionLabReport(
            Mode: "fluidruntime-update-upload-elision-control-trace-v0.12.0",
            TargetOwned: true,
            CooperativeLoad: true,
            RemoteInjection: false,
            BufferBytes,
            RequiredUpdateCountPerRun: 3,
            RedundantUpdateCountPerOptimizedRun: checked((int)CandidateCount),
            AvoidedUpdateBytesPerOptimizedRun:
                checked((ulong)BufferBytes * CandidateCount),
            ExactContentCacheResourceLimit: 1,
            ExactContentCacheByteLimit: BufferBytes,
            TrialPairsRequested: 1,
            WarmupPairs: 0,
            IncludedTrialPairs: 1,
            OrderingPolicy: "alternating",
            AdapterDescription: "Microsoft Basic Render Driver",
            AdapterVendorId: 0x1414,
            AdapterDeviceId: 0x008C,
            AdapterLuid: "0000000000000001",
            MutationGuardPassed: true,
            GenerationGuardPassed: true,
            ContentEquivalent: true,
            RollbackRestoredInAllRuns: true,
            ClaimScope: "owned-d3d11-update-subresource-only",
            PerformanceClaimBasis: "native-window",
            PerformanceClaimAllowed: true,
            PerformanceClaimBlockers: [],
            CpuImprovedPairCount: 1,
            CpuWithinBudgetPairCount: 1,
            CpuComparisonOverheadBudgetMicroseconds: 100,
            CpuComparisonOverheadBudgetPercent: 10,
            GpuValidPairCount: 1,
            CpuWorkload: Metrics(1_000, 500),
            GpuWorkload: Metrics(900, 450),
            Trials: [trial]);
    }

    private static GatewayUpdateUploadAuthorization Authorization()
    {
        var request = new GatewayUpdateUploadAuthorizationRequest(
            PairIndex: 0,
            Phase: "measured",
            ResourceBytes: BufferBytes,
            CandidateActionCount: CandidateCount,
            BinarySha256,
            BinarySha256);
        var context = FluidLinkGatewayUpdateUploadAuthorizer
            .ComputeAuthorizationContextSha256(
                "nonce",
                peerProcessId: 42,
                PeerSha256,
                PeerStartedAt,
                request,
                HookRingReader.SkipRedundantUpdateSubresourceAction,
                CandidateCount);
        return new GatewayUpdateUploadAuthorization(
            Protocol: FluidLinkV2Protocol.Version,
            ContractSha256: FluidLinkV2BatchProtocol.ContractSha256,
            WireSessionId: "00112233445566778899aabbccddeeff",
            RuntimeSessionId: $"gateway-update-{context}",
            PairIndex: 0,
            Phase: "measured",
            AdvertisedServerName: "fluidgateway",
            AdvertisedServerVersion: "0.64.0",
            PeerProcessBindingVerified: true,
            PeerCryptographicallyAuthenticated: false,
            PeerProcessId: 42,
            PeerExecutablePath: Path.GetFullPath("gateway.exe"),
            PeerExecutableSha256: PeerSha256,
            PeerProcessStartedAtUtc: PeerStartedAt,
            AuthorizationNonce: "nonce",
            AuthorizationContextSha256: context,
            AuthorizationDeadlineMilliseconds: 5000,
            TargetSha256: BinarySha256,
            HookSha256: BinarySha256,
            NegotiatedCapabilities: (ulong)FluidLinkV2BatchProtocol.AllCapabilities,
            HeartbeatVerified: true,
            SeedUploadExecuted: true,
            AllCandidateDecisionsAccepted: true,
            AllCandidateExecutionsDeferredToNative: true,
            CandidateDecisionOpcode:
                (int)FluidLinkV2DecisionOpcode.DeduplicateIdenticalTransfer,
            CandidatePolicy: "deduplicate-identical-transfer",
            CandidateDecisionCount: CandidateCount,
            AuthorizedLogicalBytes: checked((ulong)BufferBytes * CandidateCount),
            NativeActionMask: HookRingReader.SkipRedundantUpdateSubresourceAction,
            NativeActionBudget: CandidateCount,
            RuntimeEventCount: 71,
            RoundTripCount: 10,
            BytesSent: 4_096,
            BytesReceived: 4_096,
            AuthorizationLatencyMicroseconds: 1_000,
            Authorized: true,
            AuthorizationScope:
                "owned-d3d11-process-bound-candidates-native-exact-content-final-gate",
            NativeSafetyGuards: []);
    }

    private static UpdateUploadElisionRunReport Run(bool optimized)
    {
        var skippedCount = optimized ? checked((long)CandidateCount) : 0;
        var skippedBytes = optimized
            ? checked((ulong)BufferBytes * CandidateCount)
            : 0;
        return new UpdateUploadElisionRunReport(
            Optimized: optimized,
            ProcessId: 42,
            RingAbiVersion: HookRingReader.ExpectedAbiVersion,
            RingCapacity: HookRingReader.ExpectedCapacity,
            RenderDriver: "warp",
            AdapterDescription: "Microsoft Basic Render Driver",
            AdapterVendorId: 0x1414,
            AdapterDeviceId: 0x008C,
            AdapterLuid: "0000000000000001",
            EventCount: 330,
            LostSequenceCount: 0,
            NativeOverrunCount: 0,
            DirectUploadUpdateCount: 67,
            DirectUploadBytes: 281_018_368,
            RedundantUpdateCandidateCount: 64,
            RedundantUpdateCandidateBytes: 268_435_456,
            ForwardedUpdateSubresourceCount: optimized ? 6 : 70,
            ForwardedUpdateSubresourceBytes: optimized ? 12_592_128UL : 281_027_584UL,
            SkippedUpdateSubresourceCount: skippedCount,
            SkippedUpdateSubresourceBytes: skippedBytes,
            ContentCacheResourceCount: 1,
            ContentCacheBytes: BufferBytes,
            PublishedPolicyEpoch: optimized ? 1 : 0,
            AcknowledgedPolicyEpoch: optimized ? 1 : 0,
            AppliedPolicyActions: optimized ? skippedCount : 0,
            PolicyStatus: optimized ? "acknowledged" : "none",
            MutationApplied: true,
            GenerationGuardApplied: true,
            ContentEquivalent: true,
            RollbackRestored: true,
            InitialHash: "0123456789abcdef",
            FinalHash: "fedcba9876543210",
            GuardHash: "aaaaaaaaaaaaaaaa",
            PostDetachDestinationHash: "fedcba9876543210",
            CpuWorkloadMicroseconds: optimized ? 500 : 1_000,
            GpuWorkloadMicroseconds: optimized ? 450 : 900,
            TargetReport: EmptyTargetReport());
    }

    private static PairedMetricSummary Metrics(double baseline, double optimized) =>
        new(
            Distribution(baseline),
            Distribution(optimized),
            Distribution(optimized - baseline),
            Distribution((optimized - baseline) / baseline * 100),
            OptimizedLowerCount: 1,
            BaselineLowerCount: 0,
            TieCount: 0);

    private static MetricDistribution Distribution(double value) =>
        new(1, value, value, value, value, value);

    private static JsonElement EmptyTargetReport()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
