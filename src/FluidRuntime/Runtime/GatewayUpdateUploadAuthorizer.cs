using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluidLink;
using FluidRuntime.Native;

namespace FluidRuntime.Runtime;

public sealed record GatewayUpdateUploadAuthorizationRequest(
    int PairIndex,
    string Phase,
    ulong ResourceBytes,
    ulong CandidateActionCount,
    string TargetSha256,
    string HookSha256);

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
    public void EnsureMatchesNativePolicy(
        ulong expectedResourceBytes,
        ulong expectedActionCount,
        int expectedPairIndex,
        string expectedPhase,
        string expectedTargetSha256,
        string expectedHookSha256)
    {
        var requiredCapabilities = FluidLinkV2Protocol.RequiredCapabilities |
            FluidLinkV2Capability.Heartbeat |
            FluidLinkV2Capability.MemoryTransit |
            FluidLinkV2Capability.SessionLifecycle;
        var expectedLogicalBytes = checked(expectedResourceBytes * expectedActionCount);
        var request = new GatewayUpdateUploadAuthorizationRequest(
            expectedPairIndex,
            expectedPhase,
            expectedResourceBytes,
            expectedActionCount,
            expectedTargetSha256,
            expectedHookSha256);
        var expectedContext = FluidLinkGatewayUpdateUploadAuthorizer
            .ComputeAuthorizationContextSha256(
                AuthorizationNonce,
                PeerProcessId,
                PeerExecutableSha256,
                PeerProcessStartedAtUtc,
                request,
                HookRingReader.SkipRedundantUpdateSubresourceAction,
                expectedActionCount);
        if (!Authorized ||
            Protocol != FluidLinkV2Protocol.Version ||
            ContractSha256 != FluidLinkV2Protocol.ContractSha256 ||
            WireSessionId.Length != 32 ||
            WireSessionId.Any(character => !Uri.IsHexDigit(character)) ||
            RuntimeSessionId != $"gateway-update-{expectedContext}" ||
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
            NativeActionMask != HookRingReader.SkipRedundantUpdateSubresourceAction ||
            NativeActionBudget != expectedActionCount ||
            RuntimeEventCount != checked((int)expectedActionCount + 7) ||
            RoundTripCount != checked((int)expectedActionCount + 10) ||
            BytesSent <= 0 ||
            BytesReceived <= 0 ||
            AuthorizationLatencyMicroseconds <= 0 ||
            AuthorizationLatencyMicroseconds >
                checked((long)AuthorizationDeadlineMilliseconds * 1000))
        {
            throw new InvalidDataException(
                "FluidGateway authorization does not match the bounded native policy.");
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);
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
    private const string ClientVersion = "0.15.0";
    private const string ExpectedAdvertisedServerName = "fluidgateway";
    private const int FixedControlRoundTrips = 10;
    private const string ContextVersion =
        "fluidruntime-gateway-update-upload-authorization-context-v1";

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
            var token = Guid.NewGuid().ToString("N");
            var authorizationNonce = $"gateway-update-{token}";
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
                HookRingReader.SkipRedundantUpdateSubresourceAction,
                request.CandidateActionCount);
            var runtimeSessionId = $"gateway-update-{contextSha256}";
            var requiredCapabilities = FluidLinkV2Protocol.RequiredCapabilities |
                FluidLinkV2Capability.Heartbeat |
                FluidLinkV2Capability.MemoryTransit |
                FluidLinkV2Capability.SessionLifecycle;

            var welcome = await client.HandshakeAsync(
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

            var seedDecision = await client.SendOperationEventAsync(
                UploadEvent(
                    $"seed-{token}",
                    ramResourceId,
                    vramResourceId,
                    request.ResourceBytes,
                    contextSha256),
                deadlineSource.Token);
            completedRoundTrips++;
            var candidateDecisions = new List<FluidLinkV2RuntimeDecision>(
                checked((int)request.CandidateActionCount));
            for (ulong index = 0; index < request.CandidateActionCount; ++index)
            {
                candidateDecisions.Add(await client.SendOperationEventAsync(
                    UploadEvent(
                        $"candidate-{index:D3}-{token}",
                        ramResourceId,
                        vramResourceId,
                        request.ResourceBytes,
                        contextSha256),
                    deadlineSource.Token));
                completedRoundTrips++;
            }

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
        var requiredCapabilities = FluidLinkV2Protocol.RequiredCapabilities |
            FluidLinkV2Capability.Heartbeat |
            FluidLinkV2Capability.MemoryTransit |
            FluidLinkV2Capability.SessionLifecycle;
        var expectedLogicalBytes = checked(
            request.ResourceBytes * request.CandidateActionCount);
        var expectedContext = ComputeAuthorizationContextSha256(
            expectedHeartbeat,
            peer.ProcessId,
            peer.ExecutableSha256,
            peer.ProcessStartedAtUtc,
            request,
            HookRingReader.SkipRedundantUpdateSubresourceAction,
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
            welcome.ContractSha256 == FluidLinkV2Protocol.ContractSha256 &&
            welcome.ServerName == ExpectedAdvertisedServerName &&
            welcome.MaxPayloadBytes == FluidLinkV2Protocol.MaxPayloadBytes &&
            (requiredCapabilities & ~welcome.AcceptedCapabilities) == 0 &&
            string.Equals(heartbeat, expectedHeartbeat, StringComparison.Ordinal) &&
            runtimeSessionId == $"gateway-update-{expectedContext}" &&
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
            roundTripCount == checked((int)request.CandidateActionCount +
                FixedControlRoundTrips) &&
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
            HookRingReader.SkipRedundantUpdateSubresourceAction,
            request.CandidateActionCount,
            RuntimeEventCount: checked((int)request.CandidateActionCount + 7),
            roundTripCount,
            bytesSent,
            bytesReceived,
            authorizationLatencyMicroseconds,
            Authorized: true,
            AuthorizationScope:
                "owned-d3d11-process-bound-candidates-native-exact-content-final-gate",
            NativeSafetyGuards:
            [
                "expected loopback peer PID and executable SHA matched through the OS TCP owner table",
                "owned target and hook binaries frozen before authorization",
                "owned cooperative target only",
                "exact full-buffer content comparison before every skipped call",
                "mutation and external-write generation invalidation",
                "one resource and four MiB retained-content bound",
                "one short-lived native policy epoch with a fixed action budget",
                "post-detach content equivalence and rollback verification"
            ]);
        evidence.EnsureMatchesNativePolicy(
            request.ResourceBytes,
            request.CandidateActionCount,
            request.PairIndex,
            request.Phase,
            request.TargetSha256,
            request.HookSha256);
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
        var canonical = string.Join('\n',
        [
            $"context_version={ContextVersion}",
            $"protocol={FluidLinkV2Protocol.Version}",
            $"contract_sha256={FluidLinkV2Protocol.ContractSha256}",
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
        ]);
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static FluidLinkV2OperationEvent UploadEvent(
        string operationId,
        string source,
        string target,
        ulong resourceBytes,
        string authorizationContextSha256) =>
        new(
            FluidLinkV2OperationType.Upload,
            FluidLinkV2Queue.Copy,
            operationId,
            CostMicroseconds: 0,
            SizeBytes: resourceBytes,
            Source: source,
            Target: target,
            Reason: $"authorization-context-sha256:{authorizationContextSha256}",
            Frame: 0);

    private static void ValidateRequest(
        GatewayUpdateUploadAuthorizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PairIndex < 0 ||
            request.Phase is not ("warmup" or "measured") ||
            request.ResourceBytes == 0 ||
            request.CandidateActionCount is 0 or
                > HookRingReader.MaxControlActionBudget)
        {
            throw new ArgumentException(
                "Gateway update authorization request is outside the native policy bounds.",
                nameof(request));
        }
        RequireSha256(request.TargetSha256, nameof(request.TargetSha256));
        RequireSha256(request.HookSha256, nameof(request.HookSha256));
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
