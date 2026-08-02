using FluidLink;
using FluidRuntime.Cli;
using FluidRuntime.Native;
using FluidRuntime.Runtime;
using System.Text.Json;

namespace FluidRuntime.Tests;

public sealed class GatewayUpdateUploadAuthorizerTests
{
    private const ulong ResourceBytes = 4UL * 1024 * 1024;
    private const ulong CandidateCount = 64;
    private static readonly string BinarySha256 = new('a', 64);
    private static readonly string PeerSha256 = new('b', 64);
    private static readonly DateTimeOffset PeerStartedAt =
        new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Options_preserve_loopback_and_native_lab_bounds()
    {
        var options = GatewayUpdateUploadLabOptions.Parse(
        [
            "gateway-update-upload-lab",
            "--target", "target.exe",
            "--hook", "hook.dll",
            "--out", "report.json",
            "--port", "9123",
            "--timeout-ms", "8000",
            "--gateway-pid", "4242",
            "--gateway-executable-sha256", PeerSha256,
            "--trial-pairs", "3",
            "--warmup-pairs", "0",
            "--hardware", "true"
        ]);

        Assert.Equal("127.0.0.1", options.Host);
        Assert.Equal(9123, options.Port);
        Assert.Equal(8000, options.TimeoutMs);
        Assert.Equal(4242, options.GatewayProcessId);
        Assert.Equal(PeerSha256, options.GatewayExecutableSha256);
        Assert.Equal(3, options.TrialPairs);
        Assert.Equal(0, options.WarmupPairs);
        Assert.True(options.UseHardware);
        var native = options.ToNativeOptions();
        Assert.Equal(3, native.TrialPairs);
        Assert.True(native.UseHardware);
    }

    [Fact]
    public void Options_reject_unknown_or_unpaired_values()
    {
        Assert.Throws<ArgumentException>(() => GatewayUpdateUploadLabOptions.Parse(
        [
            "gateway-update-upload-lab",
            "--target", "target.exe",
            "--hook", "hook.dll",
            "--out", "report.json",
            "--actions", "64"
        ]));
        Assert.Throws<ArgumentException>(() => GatewayUpdateUploadLabOptions.Parse(
        [
            "gateway-update-upload-lab",
            "--target", "target.exe",
            "--hook", "hook.dll",
            "--out"
        ]));
    }

    [Fact]
    public void Exact_gateway_decisions_authorize_the_bounded_native_policy()
    {
        var evidence = Build();

        Assert.True(evidence.Authorized);
        Assert.Equal(FluidLinkV2Protocol.Version, evidence.Protocol);
        Assert.Equal(
            FluidLinkV2BatchProtocol.ContractSha256,
            evidence.ContractSha256);
        Assert.Equal(64UL, evidence.CandidateDecisionCount);
        Assert.Equal(268_435_456UL, evidence.AuthorizedLogicalBytes);
        Assert.Equal(
            HookRingReader.SkipRedundantUpdateSubresourceAction,
            evidence.NativeActionMask);
        Assert.Equal(64UL, evidence.NativeActionBudget);
        Assert.Equal(71, evidence.RuntimeEventCount);
        Assert.Equal(10, evidence.RoundTripCount);
        evidence.EnsureMatchesNativePolicy(
            ResourceBytes,
            CandidateCount,
            expectedPairIndex: 0,
            expectedPhase: "measured",
            BinarySha256,
            BinarySha256);
    }

    [Fact]
    public void Authorization_rejects_any_non_deduplicate_candidate()
    {
        var candidates = Candidates().ToArray();
        candidates[31] = candidates[31] with
        {
            DecisionOpcode = FluidLinkV2DecisionOpcode.Execute,
            Status = FluidLinkV2DecisionStatus.Accepted |
                FluidLinkV2DecisionStatus.HasExecutionState |
                FluidLinkV2DecisionStatus.Executed,
            SavedBytes = 0
        };

        Assert.Throws<InvalidDataException>(() => Build(candidates: candidates));
    }

    [Fact]
    public void Authorization_rejects_wrong_gateway_identity_or_byte_evidence()
    {
        Assert.Throws<InvalidDataException>(() => Build(
            welcome: Welcome() with { ServerName = "not-fluidgateway" }));

        var candidates = Candidates().ToArray();
        candidates[0] = candidates[0] with { SavedBytes = ResourceBytes - 1 };
        Assert.Throws<InvalidDataException>(() => Build(candidates: candidates));
    }

    [Fact]
    public void Native_policy_matcher_rejects_a_tampered_budget()
    {
        var evidence = Build() with { NativeActionBudget = CandidateCount - 1 };

        Assert.Throws<InvalidDataException>(() =>
            evidence.EnsureMatchesNativePolicy(
                ResourceBytes,
                CandidateCount,
                expectedPairIndex: 0,
                expectedPhase: "measured",
                BinarySha256,
                BinarySha256));
    }

    [Fact]
    public void Authorization_context_changes_with_every_actuation_binding()
    {
        var request = Request();
        var baseline = Context(request: request);

        Assert.NotEqual(baseline, Context(peerProcessId: 43));
        Assert.NotEqual(baseline, Context(
            request: request with { TargetSha256 = new string('c', 64) }));
        Assert.NotEqual(baseline, Context(
            request: request with { HookSha256 = new string('d', 64) }));
        Assert.NotEqual(baseline, Context(
            request: request with { PairIndex = 1 }));
        Assert.NotEqual(baseline, Context(nativeActionMask: 4));
        Assert.NotEqual(baseline, Context(nativeActionBudget: CandidateCount - 1));
    }

    [Fact]
    public void Authorization_failure_report_proves_unmodified_baseline_fallback()
    {
        var report = GatewayUpdateUploadFailClosedReport.Build(
            GatewayUpdateUploadAuthorizationFailureException.Create(
                new TimeoutException("gateway timeout"),
                completedRoundTrips: 4,
                elapsedMicroseconds: 500_000,
                deadlineMilliseconds: 500),
            BaselineFallback(),
            BinarySha256,
            BinarySha256);

        Assert.False(report.AuthorizationAccepted);
        Assert.False(report.NativePolicyPublished);
        Assert.True(report.BaselineFallbackCompleted);
        Assert.Equal(70, report.ForwardedUpdateSubresourceCount);
        Assert.Equal(0, report.SkippedUpdateSubresourceCount);
        Assert.True(report.ContentEquivalent);
        Assert.True(report.RollbackRestored);
        Assert.Equal("TimeoutException", report.AuthorizationFailureType);
        Assert.Equal(500, report.AuthorizationDeadlineMilliseconds);
        Assert.Equal(500_000, report.AuthorizationElapsedMicroseconds);
        Assert.Equal(4, report.CompletedRoundTripCount);
    }

    [Fact]
    public void Authorization_failure_report_rejects_any_published_policy()
    {
        var invalid = BaselineFallback() with
        {
            PublishedPolicyEpoch = 1,
            AcknowledgedPolicyEpoch = 1
        };

        Assert.Throws<InvalidDataException>(() =>
            GatewayUpdateUploadFailClosedReport.Build(
                new TimeoutException("gateway timeout"),
                invalid,
                BinarySha256,
                BinarySha256));
    }

    private static GatewayUpdateUploadAuthorization Build(
        FluidLinkV2Welcome? welcome = null,
        IReadOnlyList<FluidLinkV2RuntimeDecision>? candidates = null)
    {
        var request = Request();
        var peer = Peer();
        var context = Context(request: request);
        return FluidLinkGatewayUpdateUploadAuthorizer.BuildAuthorization(
            request,
            welcome ?? Welcome(),
            heartbeat: "nonce",
            expectedHeartbeat: "nonce",
            runtimeSessionId: $"gateway-update-{context}",
            authorizationContextSha256: context,
            peer,
            authorizationDeadlineMilliseconds: 5000,
            seedDecision: new FluidLinkV2RuntimeDecision(
                FluidLinkV2EventOpcode.Operation,
                FluidLinkV2DecisionOpcode.Execute,
                FluidLinkV2DecisionStatus.Accepted |
                    FluidLinkV2DecisionStatus.HasExecutionState |
                    FluidLinkV2DecisionStatus.Executed,
                SavedMicroseconds: 0,
                SavedBytes: 0),
            candidateDecisions: candidates ?? Candidates(),
            roundTripCount: 10,
            bytesSent: 4096,
            bytesReceived: 4096,
            authorizationLatencyMicroseconds: 1000);
    }

    private static GatewayUpdateUploadAuthorizationRequest Request() =>
        new(
            PairIndex: 0,
            Phase: "measured",
            ResourceBytes,
            CandidateCount,
            BinarySha256,
            BinarySha256);

    private static WindowsLoopbackPeerIdentity Peer() =>
        new(
            ProcessId: 42,
            ExecutablePath: Path.GetFullPath("gateway.exe"),
            ExecutableSha256: PeerSha256,
            ProcessStartedAtUtc: PeerStartedAt);

    private static string Context(
        GatewayUpdateUploadAuthorizationRequest? request = null,
        int peerProcessId = 42,
        ulong nativeActionMask = HookRingReader.SkipRedundantUpdateSubresourceAction,
        ulong nativeActionBudget = CandidateCount) =>
        FluidLinkGatewayUpdateUploadAuthorizer.ComputeAuthorizationContextSha256(
            "nonce",
            peerProcessId,
            PeerSha256,
            PeerStartedAt,
            request ?? Request(),
            nativeActionMask,
            nativeActionBudget);

    private static FluidLinkV2Welcome Welcome() =>
        new(
            FluidLinkV2BatchProtocol.ContractSha256,
            SessionId: "00112233445566778899aabbccddeeff",
            ServerName: "fluidgateway",
            ServerVersion: "0.64.0",
            FluidLinkV2BatchProtocol.AllCapabilities,
            FluidLinkV2BatchProtocol.AllCapabilities,
            FluidLinkV2Protocol.MaxPayloadBytes);

    private static IReadOnlyList<FluidLinkV2RuntimeDecision> Candidates() =>
        Enumerable.Range(0, checked((int)CandidateCount))
            .Select(_ => new FluidLinkV2RuntimeDecision(
                FluidLinkV2EventOpcode.Operation,
                FluidLinkV2DecisionOpcode.DeduplicateIdenticalTransfer,
                FluidLinkV2DecisionStatus.Accepted |
                    FluidLinkV2DecisionStatus.HasExecutionState,
                SavedMicroseconds: 0,
                SavedBytes: ResourceBytes))
            .ToArray();

    private static UpdateUploadElisionRunReport BaselineFallback()
    {
        using var document = JsonDocument.Parse("{}");
        return new UpdateUploadElisionRunReport(
            Optimized: false,
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
            ForwardedUpdateSubresourceCount: 70,
            ForwardedUpdateSubresourceBytes: 281_027_584,
            SkippedUpdateSubresourceCount: 0,
            SkippedUpdateSubresourceBytes: 0,
            ContentCacheResourceCount: 1,
            ContentCacheBytes: ResourceBytes,
            PublishedPolicyEpoch: 0,
            AcknowledgedPolicyEpoch: 0,
            AppliedPolicyActions: 0,
            PolicyStatus: "none",
            MutationApplied: true,
            GenerationGuardApplied: true,
            ContentEquivalent: true,
            RollbackRestored: true,
            InitialHash: "0123456789abcdef",
            FinalHash: "fedcba9876543210",
            GuardHash: "aaaaaaaaaaaaaaaa",
            PostDetachDestinationHash: "fedcba9876543210",
            CpuWorkloadMicroseconds: 1000,
            GpuWorkloadMicroseconds: 900,
            TargetReport: document.RootElement.Clone());
    }
}
