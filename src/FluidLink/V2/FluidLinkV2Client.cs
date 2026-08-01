using System.Net;
using System.Net.Sockets;

namespace FluidLink;

public sealed class FluidLinkV2Client : IAsyncDisposable
{
    private readonly string host;
    private readonly int port;
    private readonly TimeSpan timeout;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private TcpClient? tcpClient;
    private NetworkStream? stream;
    private IPEndPoint? localEndPoint;
    private IPEndPoint? remoteEndPoint;
    private ulong nextSequence = 1;
    private bool negotiated;
    private bool disposed;
    private uint negotiatedMaxPayloadBytes = FluidLinkV2Protocol.MaxPayloadBytes;
    private ReadOnlyMemory<byte> sessionId = ReadOnlyMemory<byte>.Empty;
    private FluidLinkV2Capability acceptedCapabilities;

    public FluidLinkV2Client(
        string host = "127.0.0.1",
        int port = 8765,
        TimeSpan? timeout = null)
    {
        if (!IsLoopbackHost(host))
        {
            throw new ArgumentException(
                "FluidLink v2 only permits loopback hosts.",
                nameof(host));
        }
        if (port is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
        this.timeout = timeout ?? TimeSpan.FromSeconds(5);
        if (this.timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        this.host = host;
        this.port = port;
    }

    public string? SessionId => sessionId.IsEmpty
        ? null
        : Convert.ToHexString(sessionId.Span).ToLowerInvariant();

    public FluidLinkV2Capability AcceptedCapabilities => acceptedCapabilities;

    public bool IsNegotiated => negotiated;

    public IPEndPoint? LocalEndPoint => localEndPoint;

    public IPEndPoint? RemoteEndPoint => remoteEndPoint;

    public long BytesSent { get; private set; }

    public long BytesReceived { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await RunSerializedAsync(ConnectCoreAsync, cancellationToken);
    }

    public async Task<FluidLinkV2Welcome> HandshakeAsync(
        string clientName,
        string clientVersion,
        FluidLinkV2Capability requestedCapabilities =
            FluidLinkV2Protocol.AllCapabilities,
        FluidLinkV2Capability requiredCapabilities =
            FluidLinkV2Protocol.RequiredCapabilities,
        CancellationToken cancellationToken = default)
    {
        requiredCapabilities |= FluidLinkV2Protocol.RequiredCapabilities;
        requestedCapabilities |= requiredCapabilities;
        ValidateCapabilities(requestedCapabilities, nameof(requestedCapabilities));
        ValidateCapabilities(requiredCapabilities, nameof(requiredCapabilities));
        var helloPayload = FluidLinkV2PayloadCodec.EncodeHello(
            new FluidLinkV2HelloPayload(
                FluidLinkV2Protocol.ContractHash,
                requestedCapabilities,
                requiredCapabilities,
                clientName,
                clientVersion));

        return await RunSerializedAsync(
            async token =>
            {
                try
                {
                    return await HandshakeCoreAsync(
                        helloPayload,
                        requestedCapabilities,
                        requiredCapabilities,
                        token);
                }
                catch
                {
                    InvalidateConnection();
                    throw;
                }
            },
            cancellationToken);
    }

    public Task<FluidLinkV2RuntimeDecision> SendSessionEventAsync(
        FluidLinkV2SessionEvent runtimeEvent,
        CancellationToken cancellationToken = default) =>
        SendRuntimeEventAsync(runtimeEvent, cancellationToken);

    public Task<FluidLinkV2RuntimeDecision> SendFrameEventAsync(
        FluidLinkV2FrameEvent runtimeEvent,
        CancellationToken cancellationToken = default) =>
        SendRuntimeEventAsync(runtimeEvent, cancellationToken);

    public Task<FluidLinkV2RuntimeDecision> SendResourceEventAsync(
        FluidLinkV2ResourceEvent runtimeEvent,
        CancellationToken cancellationToken = default) =>
        SendRuntimeEventAsync(runtimeEvent, cancellationToken);

    public Task<FluidLinkV2RuntimeDecision> SendOperationEventAsync(
        FluidLinkV2OperationEvent runtimeEvent,
        CancellationToken cancellationToken = default) =>
        SendRuntimeEventAsync(runtimeEvent, cancellationToken);

    public Task<FluidLinkV2RuntimeDecision> SendStateEventAsync(
        FluidLinkV2StateEvent runtimeEvent,
        CancellationToken cancellationToken = default) =>
        SendRuntimeEventAsync(runtimeEvent, cancellationToken);

    public async Task<FluidLinkV2RuntimeDecision> SendRuntimeEventAsync(
        IFluidLinkV2RuntimeEvent runtimeEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        var payload = FluidLinkV2PayloadCodec.EncodeRuntimeEvent(runtimeEvent);
        return await RunSerializedAsync(
            async token =>
            {
                try
                {
                    return await SendRuntimeEventCoreAsync(
                        runtimeEvent.EventOpcode,
                        payload,
                        token);
                }
                catch (FluidLinkV2ProtocolException exception)
                    when (exception.Code == "runtime_event_rejected" ||
                          exception.PeerErrorCode ==
                              FluidLinkV2ErrorCode.RuntimeEventRejected)
                {
                    throw;
                }
                catch
                {
                    InvalidateConnection();
                    throw;
                }
            },
            cancellationToken);
    }

    public async Task<string> PingAsync(
        string nonce,
        CancellationToken cancellationToken = default)
    {
        var payload = FluidLinkV2PayloadCodec.EncodePingPong(nonce);
        return await RunSerializedAsync(
            async token =>
            {
                try
                {
                    RequireCapabilities(FluidLinkV2Capability.Heartbeat);
                    var response = await SendRequestCoreAsync(
                        FluidLinkV2Opcode.Ping,
                        payload,
                        subjectOpcode: 0,
                        expectedOpcode: FluidLinkV2Opcode.Pong,
                        expectedSubjectOpcode: 0,
                        includeSession: true,
                        token);
                    var returnedNonce = FluidLinkV2PayloadCodec.DecodePingPong(
                        response.Payload.Span);
                    if (!string.Equals(returnedNonce, nonce, StringComparison.Ordinal))
                    {
                        throw new FluidLinkV2ProtocolException(
                            "heartbeat_mismatch",
                            "FluidLink v2 heartbeat nonce does not match the request.");
                    }
                    return returnedNonce;
                }
                catch
                {
                    InvalidateConnection();
                    throw;
                }
            },
            cancellationToken);
    }

    public async Task GoodbyeAsync(CancellationToken cancellationToken = default)
    {
        await RunSerializedAsync(
            async token =>
            {
                try
                {
                    EnsureNegotiated();
                    var response = await SendRequestCoreAsync(
                        FluidLinkV2Opcode.Goodbye,
                        FluidLinkV2PayloadCodec.EncodeGoodbye(),
                        subjectOpcode: 0,
                        expectedOpcode: FluidLinkV2Opcode.Goodbye,
                        expectedSubjectOpcode: 0,
                        includeSession: true,
                        token);
                    FluidLinkV2PayloadCodec.DecodeGoodbye(response.Payload.Span);
                }
                finally
                {
                    InvalidateConnection();
                }
            },
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await operationGate.WaitAsync();
        try
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            InvalidateConnection();
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        if (tcpClient is not null && stream is not null)
        {
            return;
        }

        var client = IPAddress.TryParse(host, out var literalAddress)
            ? new TcpClient(literalAddress.AddressFamily)
            : new TcpClient();
        client.NoDelay = true;
        using var timeoutSource = CreateTimeoutSource(cancellationToken);
        try
        {
            await client.ConnectAsync(host, port, timeoutSource.Token);
            var connectedLocalEndPoint = client.Client.LocalEndPoint as IPEndPoint ??
                throw new InvalidDataException(
                    "FluidLink v2 connection has no local IP endpoint.");
            var connectedRemoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint ??
                throw new InvalidDataException(
                    "FluidLink v2 connection has no remote IP endpoint.");
            tcpClient = client;
            stream = client.GetStream();
            localEndPoint = connectedLocalEndPoint;
            remoteEndPoint = connectedRemoteEndPoint;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<FluidLinkV2Welcome> HandshakeCoreAsync(
        byte[] helloPayload,
        FluidLinkV2Capability requestedCapabilities,
        FluidLinkV2Capability requiredCapabilities,
        CancellationToken cancellationToken)
    {
        if (negotiated)
        {
            throw new InvalidOperationException(
                "FluidLink v2 is already negotiated.");
        }

        await ConnectCoreAsync(cancellationToken);
        var response = await SendRequestCoreAsync(
            FluidLinkV2Opcode.Hello,
            helloPayload,
            subjectOpcode: 0,
            expectedOpcode: FluidLinkV2Opcode.Welcome,
            expectedSubjectOpcode: 0,
            includeSession: false,
            cancellationToken);
        if (!response.HasSession || response.SessionId.Length != 16)
        {
            throw new FluidLinkV2ProtocolException(
                "missing_session_id",
                "FluidLink v2 welcome did not include a 16-byte session_id.");
        }

        var welcome = FluidLinkV2PayloadCodec.DecodeWelcome(response.Payload.Span);
        if (!welcome.ContractHash.Span.SequenceEqual(
            FluidLinkV2Protocol.ContractHash.Span))
        {
            throw new FluidLinkV2ProtocolException(
                "contract_mismatch",
                "FluidLink peers do not share the exact v2 contract.");
        }
        var expectedAccepted =
            (requestedCapabilities | requiredCapabilities) &
            welcome.AvailableCapabilities;
        if (welcome.AcceptedCapabilities != expectedAccepted)
        {
            throw new FluidLinkV2ProtocolException(
                "invalid_capability_negotiation",
                "FluidLink v2 accepted capabilities do not equal the " +
                "requested/available intersection.");
        }
        if ((requiredCapabilities & ~welcome.AcceptedCapabilities) != 0)
        {
            throw new FluidLinkV2ProtocolException(
                "required_capability_unavailable",
                "FluidLink v2 welcome omitted required capability bits.");
        }

        sessionId = response.SessionId.ToArray();
        negotiatedMaxPayloadBytes = welcome.MaxPayloadBytes;
        acceptedCapabilities = welcome.AcceptedCapabilities;
        negotiated = true;
        return new FluidLinkV2Welcome(
            FluidLinkV2Protocol.ContractSha256,
            SessionId!,
            welcome.ServerName,
            welcome.ServerVersion,
            welcome.AvailableCapabilities,
            welcome.AcceptedCapabilities,
            welcome.MaxPayloadBytes);
    }

    private async Task<FluidLinkV2RuntimeDecision> SendRuntimeEventCoreAsync(
        FluidLinkV2EventOpcode eventOpcode,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        RequireCapabilities(FluidLinkV2Protocol.RequiredCapabilities);
        if (eventOpcode == FluidLinkV2EventOpcode.Session)
        {
            RequireCapabilities(FluidLinkV2Capability.SessionLifecycle);
        }

        var response = await SendRequestCoreAsync(
            FluidLinkV2Opcode.RuntimeEvent,
            payload,
            subjectOpcode: (byte)eventOpcode,
            expectedOpcode: FluidLinkV2Opcode.RuntimeDecision,
            expectedSubjectOpcode: (byte)eventOpcode,
            includeSession: true,
            cancellationToken);
        var decisionOpcode = (FluidLinkV2DecisionOpcode)response.DecisionOpcode;
        if (!Enum.IsDefined(decisionOpcode) ||
            decisionOpcode == FluidLinkV2DecisionOpcode.Unknown)
        {
            throw new FluidLinkV2ProtocolException(
                "invalid_runtime_decision",
                "FluidLink v2 returned an unknown decision opcode.");
        }

        var payloadDecision = FluidLinkV2PayloadCodec.DecodeRuntimeDecision(
            response.Payload.Span);
        if (!payloadDecision.Accepted)
        {
            throw new FluidLinkV2ProtocolException(
                "runtime_event_rejected",
                "FluidLink v2 decision did not accept the runtime event.");
        }
        if (eventOpcode == FluidLinkV2EventOpcode.Operation)
        {
            if (!payloadDecision.Executed.HasValue ||
                (payloadDecision.Executed.Value &&
                 decisionOpcode != FluidLinkV2DecisionOpcode.Execute) ||
                (!payloadDecision.Executed.Value &&
                 decisionOpcode == FluidLinkV2DecisionOpcode.Execute))
            {
                throw new FluidLinkV2ProtocolException(
                    "invalid_runtime_decision",
                    "FluidLink v2 operation execution state and decision " +
                    "opcode disagree.");
            }
        }
        else if (payloadDecision.Executed.HasValue ||
                 decisionOpcode != FluidLinkV2DecisionOpcode.Execute)
        {
            throw new FluidLinkV2ProtocolException(
                "invalid_runtime_decision",
                "FluidLink v2 non-operation events require an execute " +
                "acknowledgement without execution state.");
        }

        return new FluidLinkV2RuntimeDecision(
            eventOpcode,
            decisionOpcode,
            payloadDecision.Status,
            payloadDecision.SavedMicroseconds,
            payloadDecision.SavedBytes);
    }

    private async Task<FluidLinkV2Frame> SendRequestCoreAsync(
        FluidLinkV2Opcode opcode,
        ReadOnlyMemory<byte> payload,
        byte subjectOpcode,
        FluidLinkV2Opcode expectedOpcode,
        byte expectedSubjectOpcode,
        bool includeSession,
        CancellationToken cancellationToken)
    {
        if (tcpClient is null || stream is null)
        {
            throw new InvalidOperationException("FluidLink v2 is not connected.");
        }
        if (nextSequence == ulong.MaxValue)
        {
            InvalidateConnection();
            throw new FluidLinkV2ProtocolException(
                "sequence_exhausted",
                "FluidLink v2 sequence space is exhausted; reconnect is required.");
        }
        if (includeSession)
        {
            EnsureNegotiated();
        }
        var maximumPayload = negotiated
            ? negotiatedMaxPayloadBytes
            : FluidLinkV2Protocol.MaxPayloadBytes;
        if (payload.Length > maximumPayload)
        {
            throw new FluidLinkV2ProtocolException(
                "payload_too_large",
                "FluidLink v2 request exceeds the negotiated payload limit.");
        }

        var flags = includeSession
            ? FluidLinkV2FrameFlags.HasSession
            : FluidLinkV2FrameFlags.None;
        var messageId = Guid.NewGuid().ToByteArray();
        var sequence = nextSequence;
        var request = new FluidLinkV2Frame(
            Kind: FluidLinkV2FrameKind.Request,
            Opcode: opcode,
            SubjectOpcode: subjectOpcode,
            DecisionOpcode: 0,
            Flags: flags,
            Sequence: sequence,
            MessageId: messageId,
            SessionId: includeSession
                ? sessionId
                : ReadOnlyMemory<byte>.Empty,
            Payload: payload);
        var encoded = FluidLinkV2FrameCodec.Encode(request);

        FluidLinkV2Frame response;
        try
        {
            using var timeoutSource = CreateTimeoutSource(cancellationToken);
            await stream.WriteAsync(encoded, timeoutSource.Token);
            BytesSent += encoded.Length;
            response = await FluidLinkV2FrameCodec.ReadAsync(
                stream,
                timeoutSource.Token);
            BytesReceived += response.WireSize;
            ValidateCorrelation(response, messageId, sequence, includeSession);
        }
        catch
        {
            InvalidateConnection();
            throw;
        }
        nextSequence += 1;

        if (response.Payload.Length > maximumPayload)
        {
            throw new FluidLinkV2ProtocolException(
                "payload_too_large",
                "FluidLink v2 response exceeds the negotiated payload limit.");
        }
        if (response.SubjectOpcode != expectedSubjectOpcode)
        {
            throw new FluidLinkV2ProtocolException(
                "subject_correlation_mismatch",
                "FluidLink v2 response subject opcode does not match the request.");
        }
        if (!response.Ok)
        {
            if (response.Opcode != FluidLinkV2Opcode.Error ||
                response.DecisionOpcode is not (0 or
                    (byte)FluidLinkV2DecisionOpcode.Unknown))
            {
                throw new FluidLinkV2ProtocolException(
                    "invalid_error_response",
                    "FluidLink v2 rejected a request with an invalid error envelope.");
            }
            var error = FluidLinkV2PayloadCodec.DecodeError(response.Payload.Span);
            throw new FluidLinkV2ProtocolException(
                "peer_error",
                error.Message,
                peerErrorCode: error.ErrorCode);
        }
        if (response.Opcode != expectedOpcode)
        {
            throw new FluidLinkV2ProtocolException(
                "unexpected_response_opcode",
                $"Expected FluidLink v2 opcode {(byte)expectedOpcode}, " +
                $"received {(byte)response.Opcode}.");
        }
        if (expectedOpcode != FluidLinkV2Opcode.RuntimeDecision &&
            response.DecisionOpcode != 0)
        {
            throw new FluidLinkV2ProtocolException(
                "unexpected_decision_opcode",
                "FluidLink v2 control response contains a decision opcode.");
        }
        return response;
    }

    private void ValidateCorrelation(
        FluidLinkV2Frame response,
        ReadOnlySpan<byte> messageId,
        ulong sequence,
        bool includeSession)
    {
        if (response.Kind != FluidLinkV2FrameKind.Response)
        {
            throw new FluidLinkV2ProtocolException(
                "invalid_response",
                "FluidLink v2 peer returned a non-response frame.");
        }
        if (!response.MessageId.Span.SequenceEqual(messageId) ||
            response.Sequence != sequence)
        {
            throw new FluidLinkV2ProtocolException(
                "correlation_mismatch",
                "FluidLink v2 response correlation does not match the request.");
        }
        if (includeSession &&
            (!response.HasSession ||
             !response.SessionId.Span.SequenceEqual(sessionId.Span)))
        {
            throw new FluidLinkV2ProtocolException(
                "session_mismatch",
                "FluidLink v2 response session does not match the negotiated session.");
        }
    }

    private void RequireCapabilities(FluidLinkV2Capability capabilities)
    {
        EnsureNegotiated();
        if ((capabilities & ~acceptedCapabilities) != 0)
        {
            throw new FluidLinkV2ProtocolException(
                "capability_not_negotiated",
                "FluidLink v2 operation requires capability bits that were " +
                "not negotiated.");
        }
    }

    private void EnsureNegotiated()
    {
        if (!negotiated || sessionId.IsEmpty)
        {
            throw new InvalidOperationException(
                "FluidLink v2 handshake is required.");
        }
    }

    private async Task RunSerializedAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await action(cancellationToken);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<T> RunSerializedAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            return await action(cancellationToken);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private void InvalidateConnection()
    {
        localEndPoint = null;
        remoteEndPoint = null;
        stream?.Dispose();
        tcpClient?.Dispose();
        stream = null;
        tcpClient = null;
        negotiated = false;
        sessionId = ReadOnlyMemory<byte>.Empty;
        negotiatedMaxPayloadBytes = FluidLinkV2Protocol.MaxPayloadBytes;
        acceptedCapabilities = FluidLinkV2Capability.None;
        nextSequence = 1;
    }

    private CancellationTokenSource CreateTimeoutSource(
        CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return IPAddress.TryParse(host, out var address) &&
            IPAddress.IsLoopback(address);
    }

    private static void ValidateCapabilities(
        FluidLinkV2Capability capabilities,
        string parameterName)
    {
        if ((capabilities & ~FluidLinkV2Protocol.AllCapabilities) != 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "FluidLink v2 capabilities contain unknown bits.");
        }
    }
}
