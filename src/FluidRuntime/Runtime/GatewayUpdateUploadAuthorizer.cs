using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluidLink;
using FluidRuntime.Native;

namespace FluidRuntime.Runtime;

public enum GatewayUploadBackend
{
    D3D11UpdateSubresource = 0,
    D3D12CopyBufferRegion = 1
}

public sealed record GatewayUpdateUploadAuthorizationRequest(
    int PairIndex,
    string Phase,
    ulong ResourceBytes,
    ulong CandidateActionCount,
    string TargetSha256,
    string HookSha256,
    GatewayUploadBackend Backend = GatewayUploadBackend.D3D11UpdateSubresource,
    NativeTransferTopology? Topology = null);

public sealed record GatewayUpdateUploadAuthorization(
    string Protocol,
    string ContractSha256,
    string WireSessionId,
    string RuntimeSessionId,
    int PairIndex,
    string Phase,
    string AdvertisedServerName,
    string AdvertisedServerVersion,
    bool PeerProcessBindingVerified,
    bool PeerCryptographicallyAuthenticated,
    int PeerProcessId,
    string PeerExecutablePath,
    string PeerExecutableSha256,
    DateTimeOffset PeerProcessStartedAtUtc,
    string AuthorizationNonce,
    string AuthorizationContextSha256,
    int AuthorizationDeadlineMilliseconds,
    string TargetSha256,
    string HookSha256,
    ulong NegotiatedCapabilities,
    bool HeartbeatVerified,
    bool SeedUploadExecuted,
    bool AllCandidateDecisionsAccepted,
    bool AllCandidateExecutionsDeferredToNative,
    int CandidateDecisionOpcode,
    string CandidatePolicy,
    ulong CandidateDecisionCount,
    ulong AuthorizedLogicalBytes,
    ulong NativeActionMask,
    ulong NativeActionBudget,
    int RuntimeEventCount,
    int RoundTripCount,
    long BytesSent,
    long BytesReceived,
    long AuthorizationLatencyMicroseconds,
    bool Authorized,
    string AuthorizationScope,
    IReadOnlyList<string> NativeSafetyGuards)
{
    public GatewayUploadBackend Backend { get; init; } =
        GatewayUploadBackend.D3D11UpdateSubresource;

    public NativeTransferDescriptor TransferDescriptor =>
        GatewayUploadAuthorizationProfiles.For(Backend).TransferDescriptor;

    public NativeTransferTopology? TransferTopology { get; init; }

    public void EnsureMatchesNativePolicy(
        ulong expectedResourceBytes,
        ulong expectedActionCount,
        int expectedPairIndex,
        string expectedPhase,
        string expectedTargetSha256,
        string expectedHookSha256,
        GatewayUploadBackend expectedBackend =
            GatewayUploadBackend.D3D11UpdateSubresource,
        NativeTransferTopology? expectedTopology = null)
    {
        var profile = GatewayUploadAuthorizationProfiles.For(expectedBackend);
        var topology = expectedTopology ??
            profile.CreateDefaultTopology(expectedActionCount);
        topology.Validate(expectedActionCount);
        var requiredCapabilities = FluidLinkV2Protocol.RequiredCapabilities |
            FluidLinkV2Capability.Heartbeat |
            FluidLinkV2Capability.MemoryTransit |
            FluidLinkV2Capability.SessionLifecycle |
            FluidLinkV2Capability.BatchedRuntimeEvents;
        var expectedLogicalBytes = checked(expectedResourceBytes * expectedActionCount);
        var contextTopology = expectedBackend ==
                GatewayUploadBackend.D3D12CopyBufferRegion
            ? topology
            : expectedTopology;
        var topologyEvidenceMatches = expectedBackend ==
                GatewayUploadBackend.D3D12CopyBufferRegion
            ? TransferTopology == topology
            : TransferTopology is null || TransferTopology == topology;
        var request = new GatewayUpdateUploadAuthorizationRequest(
            expectedPairIndex,
            expectedPhase,
            expectedResourceBytes,
            expectedActionCount,
            expectedTargetSha256,
            expectedHookSha256,
            expectedBackend,
            contextTopology);
        var expectedContext = FluidLinkGatewayUpdateUploadAuthorizer
            .ComputeAuthorizationContextSha256(
                AuthorizationNonce,
                PeerProcessId,
                PeerExecutableSha256,
                PeerProcessStartedAtUtc,
                request,
                profile.NativeActionMask,
                expectedActionCount);
        if (!Authorized ||
            Protocol != FluidLinkV2Protocol.Version ||
            ContractSha256 != FluidLinkV2BatchProtocol.ContractSha256 ||
            WireSessionId.Length != 32 ||
            WireSessionId.Any(character => !Uri.IsHexDigit(character)) ||
            RuntimeSessionId != $"{profile.SessionPrefix}-{expectedContext}" ||
            Backend != expectedBackend ||
            PairIndex != expectedPairIndex ||
            !string.Equals(Phase, expectedPhase, StringComparison.Ordinal) ||
            AdvertisedServerName != "fluidgateway" ||
            !PeerProcessBindingVerified ||
            PeerCryptographicallyAuthenticated ||
            PeerProcessId <= 0 ||
            string.IsNullOrWhiteSpace(PeerExecutablePath) ||
            !Path.IsPathFullyQualified(PeerExecutablePath) ||
            !IsSha256(PeerExecutableSha256) ||
            PeerProcessStartedAtUtc == default ||
            AuthorizationNonce.Length == 0 ||
            AuthorizationContextSha256 != expectedContext ||
            AuthorizationDeadlineMilliseconds <= 0 ||
            TargetSha256 != expectedTargetSha256 ||
            HookSha256 != expectedHookSha256 ||
            !IsSha256(TargetSha256) ||
            !IsSha256(HookSha256) ||
            (requiredCapabilities & ~(FluidLinkV2Capability)NegotiatedCapabilities) != 0 ||
            !HeartbeatVerified ||
            !SeedUploadExecuted ||
            !AllCandidateDecisionsAccepted ||
            !AllCandidateExecutionsDeferredToNative ||
            CandidateDecisionOpcode !=
                (int)FluidLinkV2DecisionOpcode.DeduplicateIdenticalTransfer ||
            CandidatePolicy != "deduplicate-identical-transfer" ||
            CandidateDecisionCount != expectedActionCount ||
            AuthorizedLogicalBytes != expectedLogicalBytes ||
            NativeActionMask != profile.NativeActionMask ||
            NativeActionBudget != expectedActionCount ||
            RoundTripCount != 10 ||
            BytesSent <= 0 ||
            BytesReceived <= 0 ||
            AuthorizationLatencyMicroseconds <= 0 ||
            AuthorizationLatencyMicroseconds >
                checked((long)AuthorizationDeadlineMilliseconds * 1000) ||
            AuthorizationScope != profile.AuthorizationScope ||
            RuntimeEventCount != topology.RuntimeEventCount ||
            !topologyEvidenceMatches)
        {
            throw new InvalidDataException(
                "FluidGateway authorization does not match the bounded native policy.");
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);
}

internal sealed record GatewayUploadAuthorizationProfile(
    ulong NativeActionMask,
    string NoncePrefix,
    string SessionPrefix,
    string ContextVersion,
    string AuthorizationScope,
    IReadOnlyList<string> NativeSafetyGuards,
    NativeTransferDescriptor TransferDescriptor,
    Func<ulong, NativeTransferTopology> CreateDefaultTopology);

internal static class GatewayUploadAuthorizationProfiles
{
    public static GatewayUploadAuthorizationProfile For(
        GatewayUploadBackend backend) => backend switch
        {
            GatewayUploadBackend.D3D11UpdateSubresource => new(
                HookRingReader.SkipRedundantUpdateSubresourceAction,
                "gateway-update",
                "gateway-update",
                "fluidruntime-gateway-update-upload-authorization-context-v2",
                "owned-d3d11-process-bound-candidates-native-exact-content-final-gate",
                [
                    "expected loopback peer PID and executable SHA matched through the OS TCP owner table",
                "owned target and hook binaries frozen before authorization",
                "owned cooperative target only",
                "exact full-buffer content comparison before every skipped call",
                "mutation and external-write generation invalidation",
                "one resource and four MiB retained-content bound",
                "one short-lived native policy epoch with a fixed action budget",
                "post-detach content equivalence and rollback verification"
                ],
                NativeTransferDescriptors.D3D11UpdateBuffer,
                NativeTransferTopology.D3D11SingleLane),
            GatewayUploadBackend.D3D12CopyBufferRegion => new(
                HookRingReader.SkipRedundantTransferBufferCopyAction,
                "gateway-transfer-d3d12",
                "gateway-transfer-d3d12",
                "fluidruntime-gateway-transfer-authorization-context-v1",
                "owned-d3d12-process-bound-multi-lane-copy-buffer-final-gate",
                [
                    "expected loopback peer PID and executable SHA matched through the OS TCP owner table",
                "owned target and D3D12 hook binaries frozen before authorization",
                "bounded command-list, resource, lane, queue, and fence topology",
                "exact full-buffer comparison against registration-frozen CPU shadows",
                "retained content isolated by execution scope and destination resource",
                "unmodeled and owner-declared writes invalidate the affected lane",
                "registered upload ranges remain unmapped through the completion fence",
                "Close, Reset, CopyResource, and CopyTextureRegion clear retained state",
                "queue submission, fence signal, readback equivalence, and vtable rollback verification"
                ],
                NativeTransferDescriptors.D3D12CopyBuffer,
                NativeTransferTopology.D3D12MultiLane),
            _ => throw new ArgumentOutOfRangeException(nameof(backend))
        };
}

public interface IGatewayUpdateUploadAuthorizer
{
    Task<GatewayUpdateUploadAuthorization> AuthorizeAsync(
        GatewayUpdateUploadAuthorizationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class FluidLinkGatewayUpdateUploadAuthorizer :
    IGatewayUpdateUploadAuthorizer
{
    private const string ClientName = "fluidruntime-gateway-manager";
    private const string ClientVersion = "0.21.0";
    private const string ExpectedAdvertisedServerName = "fluidgateway";
    private const int AuthorizationRoundTrips = 10;
    private readonly string host;
    private readonly int port;
    private readonly TimeSpan deadline;
    private readonly int deadlineMilliseconds;
    private readonly int expectedGatewayProcessId;
    private readonly string expectedGatewayExecutableSha256;

    public FluidLinkGatewayUpdateUploadAuthorizer(
        string host,
        int port,
        TimeSpan deadline,
        int expectedGatewayProcessId,
        string expectedGatewayExecutableSha256)
    {
        if (!string.Equals(host, "127.0.0.1", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Gateway authorization requires exact IPv4 loopback.",
                nameof(host));
        }
        if (port is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
        if (deadline <= TimeSpan.Zero ||
            deadline.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(deadline));
        }
        if (expectedGatewayProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedGatewayProcessId));
        }

        this.host = host;
        this.port = port;
        this.deadline = deadline;
        deadlineMilliseconds = checked((int)Math.Ceiling(deadline.TotalMilliseconds));
        this.expectedGatewayProcessId = expectedGatewayProcessId;
        this.expectedGatewayExecutableSha256 = RequireSha256(
            expectedGatewayExecutableSha256,
            nameof(expectedGatewayExecutableSha256));
    }

    public async Task<GatewayUpdateUploadAuthorization> AuthorizeAsync(
        GatewayUpdateUploadAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var completedRoundTrips = 0;
        var startedAt = Stopwatch.GetTimestamp();
        using var deadlineSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        deadlineSource.CancelAfter(deadline);
        try
        {
            var profile = GatewayUploadAuthorizationProfiles.For(request.Backend);
            var token = Guid.NewGuid().ToString("N");
            var authorizationNonce = $"{profile.NoncePrefix}-{token}";
            var ramResourceId = $"ram-source-{token}";
            var vramResourceId = $"vram-target-{token}";

            await using var client = new FluidLinkV2Client(host, port, deadline);
            await client.ConnectAsync(deadlineSource.Token);
            var localEndPoint = client.LocalEndPoint ?? throw new InvalidDataException(
                "FluidLink did not expose its connected local endpoint.");
            var remoteEndPoint = client.RemoteEndPoint ?? throw new InvalidDataException(
                "FluidLink did not expose its connected remote endpoint.");
            var peer = WindowsLoopbackPeerVerifier.Verify(
                localEndPoint,
                remoteEndPoint,
                expectedGatewayProcessId,
                expectedGatewayExecutableSha256);
            deadlineSource.Token.ThrowIfCancellationRequested();

            var contextSha256 = ComputeAuthorizationContextSha256(
                authorizationNonce,
                peer.ProcessId,
                peer.ExecutableSha256,
                peer.ProcessStartedAtUtc,
                request,
                profile.NativeActionMask,
                request.CandidateActionCount);
            var runtimeSessionId = $"{profile.SessionPrefix}-{contextSha256}";
            var requiredCapabilities = FluidLinkV2Protocol.RequiredCapabilities |
                FluidLinkV2Capability.Heartbeat |
                FluidLinkV2Capability.MemoryTransit |
                FluidLinkV2Capability.SessionLifecycle |
                FluidLinkV2Capability.BatchedRuntimeEvents;

            var welcome = await client.HandshakeBatchAsync(
                ClientName,
                ClientVersion,
                requiredCapabilities: requiredCapabilities,
                cancellationToken: deadlineSource.Token);
            completedRoundTrips++;
            var heartbeat = await client.PingAsync(
                authorizationNonce,
                deadlineSource.Token);
            completedRoundTrips++;

            await client.SendSessionEventAsync(
                new FluidLinkV2SessionEvent(
                    FluidLinkV2LifecycleAction.Begin,
                    runtimeSessionId,
                    FrameBudgetMicroseconds: 16_667,
                    RamBudgetBytes: request.ResourceBytes * 4,
                    VramBudgetBytes: request.ResourceBytes * 4),
                deadlineSource.Token);
            completedRoundTrips++;
            await client.SendFrameEventAsync(
                new FluidLinkV2FrameEvent(
                    FluidLinkV2LifecycleAction.Begin,
                    Frame: 0,
                    TargetFrameMicroseconds: 16_667),
                deadlineSource.Token);
            completedRoundTrips++;
            await client.SendResourceEventAsync(
                FluidLinkV2ResourceEvent.Register(
                    ramResourceId,
                    FluidLinkV2ResourceKind.Buffer,
                    FluidLinkV2MemoryLayer.Ram,
                    FluidLinkV2Lifetime.Session,
                    request.ResourceBytes),
                deadlineSource.Token);
            completedRoundTrips++;
            await client.SendResourceEventAsync(
                FluidLinkV2ResourceEvent.Register(
                    vramResourceId,
                    FluidLinkV2ResourceKind.Buffer,
                    FluidLinkV2MemoryLayer.Vram,
                    FluidLinkV2Lifetime.Session,
                    request.ResourceBytes),
                deadlineSource.Token);
            completedRoundTrips++;

            var batchDecision = await client.SendOperationBatchAsync(
                new FluidLinkV2OperationBatchEvent(
                    token,
                    checked((int)request.CandidateActionCount + 1),
                    FluidLinkV2OperationType.Upload,
                    FluidLinkV2Queue.Copy,
                    CostMicroseconds: 0,
                    SizeBytes: request.ResourceBytes,
                    Source: ramResourceId,
                    Target: vramResourceId,
                    Reason:
                        $"authorization-context-sha256:{contextSha256}",
                    Frame: 0),
                deadlineSource.Token);
            completedRoundTrips++;
            var seedDecision = batchDecision.Decisions[0];
            var candidateDecisions = batchDecision.Decisions.Skip(1).ToArray();

            await client.SendFrameEventAsync(
                new FluidLinkV2FrameEvent(
                    FluidLinkV2LifecycleAction.End,
                    Frame: 0),
                deadlineSource.Token);
            completedRoundTrips++;
            await client.SendSessionEventAsync(
                new FluidLinkV2SessionEvent(
                    FluidLinkV2LifecycleAction.End,
                    SessionId: string.Empty),
                deadlineSource.Token);
            completedRoundTrips++;
            await client.GoodbyeAsync(deadlineSource.Token);
            completedRoundTrips++;

            var elapsed = ElapsedMicroseconds(startedAt);
            if (elapsed > checked((long)deadlineMilliseconds * 1000))
            {
                throw new TimeoutException(
                    "FluidGateway authorization exceeded its total deadline.");
            }
            return BuildAuthorization(
                request,
                welcome,
                heartbeat,
                authorizationNonce,
                runtimeSessionId,
                contextSha256,
                peer,
                deadlineMilliseconds,
                seedDecision,
                candidateDecisions,
                completedRoundTrips,
                client.BytesSent,
                client.BytesReceived,
                elapsed);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw GatewayUpdateUploadAuthorizationFailureException.Create(
                new TimeoutException(
                    "FluidGateway authorization exceeded its total deadline.",
                    exception),
                completedRoundTrips,
                ElapsedMicroseconds(startedAt),
                deadlineMilliseconds);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException &&
                  exception is not GatewayUpdateUploadAuthorizationFailureException)
        {
            throw GatewayUpdateUploadAuthorizationFailureException.Create(
                exception,
                completedRoundTrips,
                ElapsedMicroseconds(startedAt),
                deadlineMilliseconds);
        }
    }

    internal static GatewayUpdateUploadAuthorization BuildAuthorization(
        GatewayUpdateUploadAuthorizationRequest request,
        FluidLinkV2Welcome welcome,
        string heartbeat,
        string expectedHeartbeat,
        string runtimeSessionId,
        string authorizationContextSha256,
        WindowsLoopbackPeerIdentity peer,
        int authorizationDeadlineMilliseconds,
        FluidLinkV2RuntimeDecision seedDecision,
        IReadOnlyList<FluidLinkV2RuntimeDecision> candidateDecisions,
        int roundTripCount,
        long bytesSent,
        long bytesReceived,
        long authorizationLatencyMicroseconds)
    {
        ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(peer);
        var profile = GatewayUploadAuthorizationProfiles.For(request.Backend);
        var topology = request.Topology ??
            profile.CreateDefaultTopology(request.CandidateActionCount);
        var requiredCapabilities = FluidLinkV2Protocol.RequiredCapabilities |
            FluidLinkV2Capability.Heartbeat |
            FluidLinkV2Capability.MemoryTransit |
            FluidLinkV2Capability.SessionLifecycle |
            FluidLinkV2Capability.BatchedRuntimeEvents;
        var expectedLogicalBytes = checked(
            request.ResourceBytes * request.CandidateActionCount);
        var expectedContext = ComputeAuthorizationContextSha256(
            expectedHeartbeat,
            peer.ProcessId,
            peer.ExecutableSha256,
            peer.ProcessStartedAtUtc,
            request,
            profile.NativeActionMask,
            request.CandidateActionCount);
        var decisionsMatch = candidateDecisions.Count ==
                checked((int)request.CandidateActionCount) &&
            candidateDecisions.All(decision =>
                decision.Accepted &&
                decision.EventOpcode == FluidLinkV2EventOpcode.Operation &&
                decision.DecisionOpcode ==
                    FluidLinkV2DecisionOpcode.DeduplicateIdenticalTransfer &&
                decision.Executed is false &&
                decision.SavedMicroseconds == 0 &&
                decision.SavedBytes == request.ResourceBytes);
        var authorized =
            welcome.ContractSha256 == FluidLinkV2BatchProtocol.ContractSha256 &&
            welcome.ServerName == ExpectedAdvertisedServerName &&
            welcome.MaxPayloadBytes == FluidLinkV2Protocol.MaxPayloadBytes &&
            (requiredCapabilities & ~welcome.AcceptedCapabilities) == 0 &&
            string.Equals(heartbeat, expectedHeartbeat, StringComparison.Ordinal) &&
            runtimeSessionId == $"{profile.SessionPrefix}-{expectedContext}" &&
            authorizationContextSha256 == expectedContext &&
            peer.ProcessId > 0 &&
            !string.IsNullOrWhiteSpace(peer.ExecutablePath) &&
            Path.IsPathFullyQualified(peer.ExecutablePath) &&
            RequireSha256(peer.ExecutableSha256, nameof(peer.ExecutableSha256)) ==
                peer.ExecutableSha256 &&
            peer.ProcessStartedAtUtc != default &&
            seedDecision.Accepted &&
            seedDecision.EventOpcode == FluidLinkV2EventOpcode.Operation &&
            seedDecision.DecisionOpcode == FluidLinkV2DecisionOpcode.Execute &&
            seedDecision.Executed is true &&
            seedDecision.SavedMicroseconds == 0 &&
            seedDecision.SavedBytes == 0 &&
            decisionsMatch &&
            roundTripCount == AuthorizationRoundTrips &&
            bytesSent > 0 &&
            bytesReceived > 0 &&
            authorizationDeadlineMilliseconds > 0 &&
            authorizationLatencyMicroseconds > 0 &&
            authorizationLatencyMicroseconds <=
                checked((long)authorizationDeadlineMilliseconds * 1000);
        if (!authorized)
        {
            throw new InvalidDataException(
                "FluidGateway did not return an exact fail-closed upload authorization.");
        }

        var evidence = new GatewayUpdateUploadAuthorization(
            FluidLinkV2Protocol.Version,
            welcome.ContractSha256,
            welcome.SessionId,
            runtimeSessionId,
            request.PairIndex,
            request.Phase,
            welcome.ServerName,
            welcome.ServerVersion,
            PeerProcessBindingVerified: true,
            PeerCryptographicallyAuthenticated: false,
            peer.ProcessId,
            peer.ExecutablePath,
            peer.ExecutableSha256,
            peer.ProcessStartedAtUtc,
            expectedHeartbeat,
            authorizationContextSha256,
            authorizationDeadlineMilliseconds,
            request.TargetSha256,
            request.HookSha256,
            (ulong)welcome.AcceptedCapabilities,
            HeartbeatVerified: true,
            SeedUploadExecuted: true,
            AllCandidateDecisionsAccepted: true,
            AllCandidateExecutionsDeferredToNative: true,
            (int)FluidLinkV2DecisionOpcode.DeduplicateIdenticalTransfer,
            FluidLinkV2Protocol.DecisionPolicyName(
                FluidLinkV2DecisionOpcode.DeduplicateIdenticalTransfer),
            request.CandidateActionCount,
            expectedLogicalBytes,
            profile.NativeActionMask,
            request.CandidateActionCount,
            RuntimeEventCount: topology.RuntimeEventCount,
            roundTripCount,
            bytesSent,
            bytesReceived,
            authorizationLatencyMicroseconds,
            Authorized: true,
            profile.AuthorizationScope,
            profile.NativeSafetyGuards)
        {
            Backend = request.Backend,
            TransferTopology = topology
        };
        evidence.EnsureMatchesNativePolicy(
            request.ResourceBytes,
            request.CandidateActionCount,
            request.PairIndex,
            request.Phase,
            request.TargetSha256,
            request.HookSha256,
            request.Backend,
            request.Topology);
        return evidence;
    }

    internal static string ComputeAuthorizationContextSha256(
        string nonce,
        int peerProcessId,
        string peerExecutableSha256,
        DateTimeOffset peerProcessStartedAtUtc,
        GatewayUpdateUploadAuthorizationRequest request,
        ulong nativeActionMask,
        ulong nativeActionBudget)
    {
        ValidateRequest(request);
        if (string.IsNullOrWhiteSpace(nonce) || peerProcessId <= 0 ||
            peerProcessStartedAtUtc == default)
        {
            throw new ArgumentException("Authorization context identity is incomplete.");
        }
        var peerSha256 = RequireSha256(
            peerExecutableSha256,
            nameof(peerExecutableSha256));
        var profile = GatewayUploadAuthorizationProfiles.For(request.Backend);
        var canonicalLines = new List<string>
        {
            $"context_version={profile.ContextVersion}",
            $"protocol={FluidLinkV2Protocol.Version}",
            $"contract_sha256={FluidLinkV2BatchProtocol.ContractSha256}",
            $"nonce={nonce}",
            $"peer_process_id={peerProcessId}",
            $"peer_executable_sha256={peerSha256}",
            $"peer_started_utc_ticks={peerProcessStartedAtUtc.UtcDateTime.Ticks}",
            $"target_sha256={request.TargetSha256}",
            $"hook_sha256={request.HookSha256}",
            $"pair_index={request.PairIndex}",
            $"phase={request.Phase}",
            $"resource_bytes={request.ResourceBytes}",
            $"candidate_action_count={request.CandidateActionCount}",
            $"native_action_mask={nativeActionMask}",
            $"native_action_budget={nativeActionBudget}"
        };
        if (request.Topology is not null)
        {
            canonicalLines.InsertRange(1,
            [
                $"transfer_contract={profile.TransferDescriptor.ContractVersion}",
                $"transfer_backend={(int)profile.TransferDescriptor.Backend}",
                $"transfer_operation={(int)profile.TransferDescriptor.Operation}",
                $"transfer_scope={profile.TransferDescriptor.Scope}",
                $"queue_count={request.Topology.QueueCount}",
                $"execution_scope_count={request.Topology.ExecutionScopeCount}",
                $"source_resource_count={request.Topology.SourceResourceCount}",
                $"destination_resource_count={request.Topology.DestinationResourceCount}",
                $"lane_count={request.Topology.LaneCount}",
                $"fence_count={request.Topology.FenceCount}",
                $"runtime_event_count={request.Topology.RuntimeEventCount}"
            ]);
        }
        var canonical = string.Join('\n', canonicalLines);
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static void ValidateRequest(
        GatewayUpdateUploadAuthorizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PairIndex < 0 ||
            request.Phase is not ("warmup" or "measured") ||
            request.ResourceBytes == 0 ||
            request.CandidateActionCount is 0 or
                > HookRingReader.MaxControlActionBudget ||
            !Enum.IsDefined(request.Backend))
        {
            throw new ArgumentException(
                "Gateway update authorization request is outside the native policy bounds.",
                nameof(request));
        }
        if (request.Backend == GatewayUploadBackend.D3D12CopyBufferRegion &&
            request.Topology is null)
        {
            throw new ArgumentException(
                "D3D12 authorization requires an explicit transfer topology.",
                nameof(request));
        }
        RequireSha256(request.TargetSha256, nameof(request.TargetSha256));
        RequireSha256(request.HookSha256, nameof(request.HookSha256));
        var profile = GatewayUploadAuthorizationProfiles.For(request.Backend);
        (request.Topology ?? profile.CreateDefaultTopology(
            request.CandidateActionCount)).Validate(request.CandidateActionCount);
    }

    private static string RequireSha256(string value, string name)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = value.ToLowerInvariant();
        if (normalized.Length != 64 || !normalized.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A 64-character SHA-256 is required.", name);
        }
        return normalized;
    }

    private static long ElapsedMicroseconds(long startedAt) =>
        Math.Max(
            1,
            checked((long)Math.Ceiling(
                Stopwatch.GetElapsedTime(startedAt).TotalMicroseconds)));
}

public sealed class GatewayUpdateUploadAuthorizationFailureException : Exception
{
    private GatewayUpdateUploadAuthorizationFailureException(
        string failureType,
        Exception failure,
        int completedRoundTrips,
        long elapsedMicroseconds,
        int deadlineMilliseconds)
        : base(failure.Message, failure)
    {
        FailureType = failureType;
        CompletedRoundTrips = completedRoundTrips;
        ElapsedMicroseconds = elapsedMicroseconds;
        DeadlineMilliseconds = deadlineMilliseconds;
    }

    public string FailureType { get; }

    public int CompletedRoundTrips { get; }

    public long ElapsedMicroseconds { get; }

    public int DeadlineMilliseconds { get; }

    public static GatewayUpdateUploadAuthorizationFailureException Create(
        Exception failure,
        int completedRoundTrips,
        long elapsedMicroseconds,
        int deadlineMilliseconds) =>
        new(
            failure is TimeoutException ? nameof(TimeoutException) :
                failure.GetType().Name,
            failure,
            completedRoundTrips,
            elapsedMicroseconds,
            deadlineMilliseconds);
}
