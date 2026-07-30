using System.Collections.Frozen;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace FluidLink;

public sealed class FluidLinkClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        MaxDepth = FluidLinkProtocol.MaxJsonDepth
    };
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string host;
    private readonly int port;
    private readonly TimeSpan timeout;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private TcpClient? tcpClient;
    private NetworkStream? stream;
    private ulong nextSequence = 1;
    private bool negotiated;
    private bool disposed;
    private int negotiatedMaxPayloadBytes = FluidLinkProtocol.MaxPayloadBytes;
    private ReadOnlyMemory<byte> sessionId = ReadOnlyMemory<byte>.Empty;
    private FrozenSet<string> acceptedCapabilities =
        Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);

    public FluidLinkClient(
        string host = "127.0.0.1",
        int port = 8765,
        TimeSpan? timeout = null)
    {
        if (!IsLoopbackHost(host))
        {
            throw new ArgumentException(
                "FluidLink v1 only permits loopback hosts.",
                nameof(host));
        }
        if (port is < 1 or > 65535)
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

    public IReadOnlySet<string> AcceptedCapabilities => acceptedCapabilities;

    public long BytesSent { get; private set; }

    public long BytesReceived { get; private set; }

    public long EquivalentJsonEnvelopeBytes { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await RunSerializedAsync(ConnectCoreAsync, cancellationToken);
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        if (tcpClient is not null && stream is not null)
        {
            return;
        }

        var client = new TcpClient { NoDelay = true };
        using var timeoutSource = CreateTimeoutSource(cancellationToken);
        try
        {
            await client.ConnectAsync(host, port, timeoutSource.Token);
            tcpClient = client;
            stream = client.GetStream();
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<FluidLinkWelcome> HandshakeAsync(
        string clientName,
        string clientVersion,
        IEnumerable<string>? capabilities = null,
        IEnumerable<string>? requiredCapabilities = null,
        CancellationToken cancellationToken = default)
    {
        ValidateBoundedText(
            clientName,
            FluidLinkProtocol.MaxPeerNameUtf8Bytes,
            nameof(clientName));
        ValidateBoundedText(
            clientVersion,
            FluidLinkProtocol.MaxPeerVersionUtf8Bytes,
            nameof(clientVersion));
        return await RunSerializedAsync(
            async token =>
            {
                try
                {
                    return await HandshakeCoreAsync(
                        clientName.Trim(),
                        clientVersion.Trim(),
                        capabilities,
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

    private async Task<FluidLinkWelcome> HandshakeCoreAsync(
        string clientName,
        string clientVersion,
        IEnumerable<string>? capabilities,
        IEnumerable<string>? requiredCapabilities,
        CancellationToken cancellationToken)
    {
        if (negotiated)
        {
            throw new InvalidOperationException("FluidLink is already negotiated.");
        }

        await ConnectCoreAsync(cancellationToken);
        var required = NormalizeCapabilities(
            requiredCapabilities ?? FluidLinkProtocol.RequiredRuntimeCapabilities);
        var requested = NormalizeCapabilities(
            (capabilities ?? FluidLinkProtocol.RuntimeCapabilities).Concat(required));
        var payload = JsonSerializer.SerializeToElement(new
        {
            ContractSha256 = FluidLinkProtocol.ContractSha256,
            Client = new { Name = clientName, Version = clientVersion },
            Capabilities = requested,
            RequiredCapabilities = required
        }, JsonOptions);
        var response = await SendRequestCoreAsync(
            FluidLinkOpcode.Hello,
            payload,
            subjectOpcode: 0,
            expectedOpcode: FluidLinkOpcode.Welcome,
            expectedSubjectOpcode: 0,
            includeSession: false,
            cancellationToken);

        if (!response.HasSession || response.SessionId.Length != 16)
        {
            throw new FluidLinkProtocolException(
                "missing_session_id",
                "FluidLink welcome did not include a 16-byte session_id.");
        }

        var body = response.Payload;
        var contractSha256 = RequiredString(body, "contract_sha256");
        if (!string.Equals(
            contractSha256,
            FluidLinkProtocol.ContractSha256,
            StringComparison.Ordinal))
        {
            InvalidateConnection();
            throw new FluidLinkProtocolException(
                "contract_mismatch",
                "FluidLink peers do not share the same v1 contract.");
        }
        var server = RequiredObject(body, "server");
        var serverName = RequiredString(server, "name");
        var serverVersion = RequiredString(server, "version");
        ValidateBoundedPeerValue(
            serverName,
            FluidLinkProtocol.MaxPeerNameUtf8Bytes,
            "server.name");
        ValidateBoundedPeerValue(
            serverVersion,
            FluidLinkProtocol.MaxPeerVersionUtf8Bytes,
            "server.version");
        var available = RequiredStringArray(body, "available_capabilities");
        var accepted = RequiredStringArray(body, "accepted_capabilities");
        var limits = RequiredObject(body, "limits");
        var maxPayloadBytes = RequiredPositiveInt(limits, "max_payload_bytes");
        var maxJsonDepth = RequiredPositiveInt(limits, "max_json_depth");
        var missing = required.Except(accepted, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            throw new FluidLinkProtocolException(
                "required_capability_unavailable",
                "FluidLink welcome omitted required capabilities: " +
                string.Join(", ", missing));
        }
        if (accepted.Except(available, StringComparer.Ordinal).Any() ||
            accepted.Except(requested, StringComparer.Ordinal).Any())
        {
            InvalidateConnection();
            throw new FluidLinkProtocolException(
                "invalid_capability_negotiation",
                "FluidLink welcome accepted undeclared capabilities.");
        }
        if (maxJsonDepth != FluidLinkProtocol.MaxJsonDepth)
        {
            InvalidateConnection();
            throw new FluidLinkProtocolException(
                "contract_mismatch",
                "FluidLink peer reported an incompatible JSON depth limit.");
        }
        if (maxPayloadBytes > FluidLinkProtocol.MaxPayloadBytes)
        {
            maxPayloadBytes = FluidLinkProtocol.MaxPayloadBytes;
        }

        sessionId = response.SessionId.ToArray();
        negotiatedMaxPayloadBytes = maxPayloadBytes;
        acceptedCapabilities = accepted.ToFrozenSet(StringComparer.Ordinal);
        negotiated = true;
        return new FluidLinkWelcome(
            contractSha256,
            SessionId!,
            serverName,
            serverVersion,
            available,
            accepted,
            maxPayloadBytes,
            maxJsonDepth);
    }

    public Task<FluidLinkRuntimeDecision> SendRuntimeEventAsync(
        FluidLinkEventOpcode eventOpcode,
        object eventData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        return SendRuntimeEventElementAsync(
            eventOpcode,
            JsonSerializer.SerializeToElement(eventData, JsonOptions),
            cancellationToken);
    }

    public async Task<FluidLinkRuntimeDecision> SendRuntimeEventElementAsync(
        FluidLinkEventOpcode eventOpcode,
        JsonElement eventData,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(eventOpcode))
        {
            throw new ArgumentOutOfRangeException(nameof(eventOpcode));
        }
        if (eventData.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "FluidLink runtime event data must be a JSON object.",
                nameof(eventData));
        }
        var ownedEventData = eventData.Clone();
        return await RunSerializedAsync(
            token => SendRuntimeEventElementCoreAsync(
                eventOpcode,
                ownedEventData,
                token),
            cancellationToken);
    }

    private async Task<FluidLinkRuntimeDecision> SendRuntimeEventElementCoreAsync(
        FluidLinkEventOpcode eventOpcode,
        JsonElement eventData,
        CancellationToken cancellationToken)
    {
        RequireNegotiatedCapability("binary.framing.v1");
        RequireNegotiatedCapability("runtime.events.v1");
        RequireNegotiatedCapability("runtime.decisions.v1");
        RequireNegotiatedCapability("compact.decisions.v1");

        var response = await SendRequestCoreAsync(
            FluidLinkOpcode.RuntimeEvent,
            eventData,
            subjectOpcode: (byte)eventOpcode,
            expectedOpcode: FluidLinkOpcode.RuntimeDecision,
            expectedSubjectOpcode: (byte)eventOpcode,
            includeSession: true,
            cancellationToken);
        var accepted = ParsePeerValue(
            () => RequiredBool(response.Payload, "accepted"));
        if (!accepted)
        {
            throw new FluidLinkProtocolException(
                "runtime_event_rejected",
                "FluidLink decision did not accept the runtime event.");
        }

        var decisionOpcode = (FluidLinkDecisionOpcode)response.DecisionOpcode;
        if (!Enum.IsDefined(decisionOpcode) ||
            decisionOpcode == FluidLinkDecisionOpcode.Unknown)
        {
            InvalidateConnection();
            throw new FluidLinkProtocolException(
                "invalid_runtime_decision",
                "FluidLink returned an unknown decision opcode.");
        }
        var executed = ParsePeerValue(
            () => OptionalBool(response.Payload, "executed"));
        var savedMilliseconds = ParsePeerValue(
            () => OptionalNonNegativeDouble(response.Payload, "saved_ms")) ?? 0;
        var savedMegabytes = ParsePeerValue(
            () => OptionalNonNegativeDouble(response.Payload, "saved_mb")) ?? 0;
        if (eventOpcode == FluidLinkEventOpcode.Operation && executed is null)
        {
            InvalidateConnection();
            throw new FluidLinkProtocolException(
                "invalid_runtime_decision",
                "FluidLink operation decision requires an executed flag.");
        }
        if (eventOpcode == FluidLinkEventOpcode.Operation &&
            ((executed is true && decisionOpcode != FluidLinkDecisionOpcode.Execute) ||
             (executed is false && decisionOpcode == FluidLinkDecisionOpcode.Execute)))
        {
            InvalidateConnection();
            throw new FluidLinkProtocolException(
                "invalid_runtime_decision",
                "FluidLink execution state and decision opcode disagree.");
        }
        if (eventOpcode != FluidLinkEventOpcode.Operation &&
            (executed is not null || decisionOpcode != FluidLinkDecisionOpcode.Execute))
        {
            InvalidateConnection();
            throw new FluidLinkProtocolException(
                "invalid_runtime_decision",
                "FluidLink non-operation events must return an execute acknowledgement.");
        }
        return new FluidLinkRuntimeDecision(
            eventOpcode,
            decisionOpcode,
            accepted,
            executed,
            savedMilliseconds,
            savedMegabytes);
    }

    public async Task<string?> PingAsync(
        string nonce,
        CancellationToken cancellationToken = default)
    {
        ValidateBoundedText(
            nonce,
            FluidLinkProtocol.MaxNonceUtf8Bytes,
            nameof(nonce));
        return await RunSerializedAsync(
            token => PingCoreAsync(nonce, token),
            cancellationToken);
    }

    private async Task<string?> PingCoreAsync(
        string nonce,
        CancellationToken cancellationToken)
    {
        RequireNegotiatedCapability("heartbeat.v1");
        var response = await SendRequestCoreAsync(
            FluidLinkOpcode.Ping,
            JsonSerializer.SerializeToElement(new { Nonce = nonce }, JsonOptions),
            subjectOpcode: 0,
            expectedOpcode: FluidLinkOpcode.Pong,
            expectedSubjectOpcode: 0,
            includeSession: true,
            cancellationToken);
        if (!response.Payload.TryGetProperty("nonce", out var nonceElement) ||
            nonceElement.ValueKind != JsonValueKind.String ||
            !string.Equals(
                nonceElement.GetString(),
                nonce,
                StringComparison.Ordinal))
        {
            InvalidateConnection();
            throw new FluidLinkProtocolException(
                "heartbeat_mismatch",
                "FluidLink heartbeat nonce does not match the request.");
        }
        return nonceElement.GetString();
    }

    public async Task GoodbyeAsync(CancellationToken cancellationToken = default)
    {
        await RunSerializedAsync(GoodbyeCoreAsync, cancellationToken);
    }

    private async Task GoodbyeCoreAsync(CancellationToken cancellationToken)
    {
        EnsureNegotiated();
        var response = await SendRequestCoreAsync(
            FluidLinkOpcode.Goodbye,
            JsonSerializer.SerializeToElement(new { }, JsonOptions),
            subjectOpcode: 0,
            expectedOpcode: FluidLinkOpcode.Goodbye,
            expectedSubjectOpcode: 0,
            includeSession: true,
            cancellationToken);
        if (!response.Payload.TryGetProperty("closed", out var closed) ||
            closed.ValueKind != JsonValueKind.True)
        {
            InvalidateConnection();
            throw new FluidLinkProtocolException(
                "invalid_goodbye",
                "FluidLink goodbye response did not confirm closure.");
        }
        InvalidateConnection();
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

    private async Task<FluidLinkFrame> SendRequestCoreAsync(
        FluidLinkOpcode opcode,
        JsonElement payload,
        byte subjectOpcode,
        FluidLinkOpcode expectedOpcode,
        byte expectedSubjectOpcode,
        bool includeSession,
        CancellationToken cancellationToken)
    {
        if (tcpClient is null || stream is null)
        {
            throw new InvalidOperationException("FluidLink is not connected.");
        }
        if (nextSequence == ulong.MaxValue)
        {
            InvalidateConnection();
            throw new FluidLinkProtocolException(
                "sequence_exhausted",
                "FluidLink sequence space is exhausted; reconnect is required.");
        }
        if (includeSession)
        {
            EnsureNegotiated();
        }

        var flags = FluidLinkFrameFlags.JsonPayload;
        if (includeSession)
        {
            flags |= FluidLinkFrameFlags.HasSession;
        }
        var messageId = Guid.NewGuid().ToByteArray();
        var sequence = nextSequence;
        var request = new FluidLinkFrame(
            Kind: FluidLinkFrameKind.Request,
            Opcode: opcode,
            SubjectOpcode: subjectOpcode,
            DecisionOpcode: 0,
            Flags: flags,
            Sequence: sequence,
            MessageId: messageId,
            SessionId: includeSession ? sessionId : ReadOnlyMemory<byte>.Empty,
            Payload: payload);
        var encoded = FluidLinkFrameCodec.Encode(request);
        if (encoded.Length - FluidLinkProtocol.HeaderSize > negotiatedMaxPayloadBytes)
        {
            throw new FluidLinkProtocolException(
                "payload_too_large",
                "FluidLink request exceeds the negotiated payload limit.");
        }
        EquivalentJsonEnvelopeBytes +=
            FluidLinkFrameCodec.EstimateEquivalentJsonEnvelopeSize(request);

        FluidLinkFrame response;
        try
        {
            using var timeoutSource = CreateTimeoutSource(cancellationToken);
            await stream.WriteAsync(encoded, timeoutSource.Token);
            BytesSent += encoded.Length;
            response = await FluidLinkFrameCodec.ReadAsync(
                stream,
                timeoutSource.Token);
            BytesReceived += response.WireSize;
            EquivalentJsonEnvelopeBytes +=
                FluidLinkFrameCodec.EstimateEquivalentJsonEnvelopeSize(response);
            ValidateCorrelation(response, messageId, sequence, includeSession);
        }
        catch
        {
            InvalidateConnection();
            throw;
        }
        nextSequence += 1;

        if (response.SubjectOpcode != expectedSubjectOpcode)
        {
            InvalidateConnection();
            throw new FluidLinkProtocolException(
                "subject_correlation_mismatch",
                "FluidLink response subject opcode does not match the request.");
        }
        if (!response.Ok)
        {
            if (response.Opcode != FluidLinkOpcode.Error ||
                response.DecisionOpcode != (byte)FluidLinkDecisionOpcode.Unknown)
            {
                InvalidateConnection();
                throw new FluidLinkProtocolException(
                    "invalid_error_response",
                    "FluidLink rejected a request with an invalid error envelope.");
            }
            var code = OptionalString(response.Payload, "code") ?? "peer_error";
            var message = OptionalString(response.Payload, "message") ??
                "FluidLink peer rejected the request.";
            throw new FluidLinkProtocolException(code, message);
        }
        if (response.Opcode != expectedOpcode)
        {
            InvalidateConnection();
            throw new FluidLinkProtocolException(
                "unexpected_response_opcode",
                $"Expected FluidLink opcode {(byte)expectedOpcode}, " +
                $"received {(byte)response.Opcode}.");
        }
        if (expectedOpcode != FluidLinkOpcode.RuntimeDecision &&
            response.DecisionOpcode != 0)
        {
            InvalidateConnection();
            throw new FluidLinkProtocolException(
                "unexpected_decision_opcode",
                "FluidLink control response contains a decision opcode.");
        }
        return response;
    }

    private void ValidateCorrelation(
        FluidLinkFrame response,
        ReadOnlySpan<byte> messageId,
        ulong sequence,
        bool includeSession)
    {
        if (response.Kind != FluidLinkFrameKind.Response)
        {
            throw new FluidLinkProtocolException(
                "invalid_response",
                "FluidLink peer returned a non-response frame.");
        }
        if (!response.MessageId.Span.SequenceEqual(messageId) ||
            response.Sequence != sequence)
        {
            throw new FluidLinkProtocolException(
                "correlation_mismatch",
                "FluidLink response correlation does not match the request.");
        }
        if (includeSession &&
            !response.SessionId.Span.SequenceEqual(sessionId.Span))
        {
            throw new FluidLinkProtocolException(
                "session_mismatch",
                "FluidLink response session does not match the negotiated session.");
        }
    }

    private void RequireNegotiatedCapability(string capability)
    {
        EnsureNegotiated();
        if (!acceptedCapabilities.Contains(capability))
        {
            throw new FluidLinkProtocolException(
                "capability_not_negotiated",
                $"FluidLink capability '{capability}' was not negotiated.");
        }
    }

    private void EnsureNegotiated()
    {
        if (!negotiated || sessionId.IsEmpty)
        {
            throw new InvalidOperationException("FluidLink handshake is required.");
        }
    }

    private T ParsePeerValue<T>(Func<T> parser)
    {
        try
        {
            return parser();
        }
        catch (FluidLinkProtocolException)
        {
            InvalidateConnection();
            throw;
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
        stream?.Dispose();
        tcpClient?.Dispose();
        stream = null;
        tcpClient = null;
        negotiated = false;
        sessionId = ReadOnlyMemory<byte>.Empty;
        negotiatedMaxPayloadBytes = FluidLinkProtocol.MaxPayloadBytes;
        acceptedCapabilities = Array.Empty<string>()
            .ToFrozenSet(StringComparer.Ordinal);
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
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    private static string[] NormalizeCapabilities(IEnumerable<string> capabilities)
    {
        var values = capabilities
            .Select(item => item?.Trim() ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (values.Length > FluidLinkProtocol.MaxCapabilities ||
            values.Any(item =>
                item.Length < 1 ||
                !FitsUtf8Limit(
                    item,
                    FluidLinkProtocol.MaxCapabilityNameUtf8Bytes)))
        {
            throw new ArgumentException(
                "FluidLink capabilities must contain up to 64 names " +
                "of 1 through 128 UTF-8 bytes.",
                nameof(capabilities));
        }
        return values
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static JsonElement RequiredObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            throw new FluidLinkProtocolException(
                "invalid_response",
                $"FluidLink response requires object '{name}'.");
        }
        return value;
    }

    private static string RequiredString(JsonElement parent, string name) =>
        OptionalString(parent, name) ?? throw new FluidLinkProtocolException(
            "invalid_response",
            $"FluidLink response requires string '{name}'.");

    private static string? OptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static IReadOnlyList<string> RequiredStringArray(
        JsonElement parent,
        string name)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            throw new FluidLinkProtocolException(
                "invalid_response",
                $"FluidLink response requires array '{name}'.");
        }
        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new FluidLinkProtocolException(
                    "invalid_response",
                    $"FluidLink response array '{name}' contains an invalid value.");
            }
            result.Add(item.GetString()!);
        }
        if (result.Count > FluidLinkProtocol.MaxCapabilities ||
            result.Distinct(StringComparer.Ordinal).Count() != result.Count ||
            result.Any(item => !FitsUtf8Limit(
                item,
                FluidLinkProtocol.MaxCapabilityNameUtf8Bytes)))
        {
            throw new FluidLinkProtocolException(
                "invalid_response",
                $"FluidLink response array '{name}' violates capability limits.");
        }
        return result.AsReadOnly();
    }

    private static int RequiredPositiveInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            !value.TryGetInt32(out var result) || result < 1)
        {
            throw new FluidLinkProtocolException(
                "invalid_response",
                $"FluidLink response requires positive integer '{name}'.");
        }
        return result;
    }

    private static bool RequiredBool(JsonElement parent, string name)
    {
        var value = OptionalBool(parent, name);
        return value ?? throw new FluidLinkProtocolException(
            "invalid_response",
            $"FluidLink response requires boolean '{name}'.");
    }

    private static bool? OptionalBool(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return null;
        }
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new FluidLinkProtocolException(
                "invalid_response",
                $"FluidLink response value '{name}' must be boolean.");
        }
        return value.GetBoolean();
    }

    private static double? OptionalNonNegativeDouble(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return null;
        }
        if (!value.TryGetDouble(out var result))
        {
            throw new FluidLinkProtocolException(
                "invalid_response",
                $"FluidLink response value '{name}' must be numeric.");
        }
        if (!double.IsFinite(result) || result < 0)
        {
            throw new FluidLinkProtocolException(
                "invalid_response",
                $"FluidLink response number '{name}' must be finite and non-negative.");
        }
        return result;
    }

    private static void ValidateBoundedText(
        string value,
        int maximumUtf8Bytes,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !FitsUtf8Limit(value.Trim(), maximumUtf8Bytes))
        {
            throw new ArgumentException(
                $"FluidLink {parameterName} must contain 1 to " +
                $"{maximumUtf8Bytes} UTF-8 bytes.",
                parameterName);
        }
    }

    private static void ValidateBoundedPeerValue(
        string value,
        int maximumUtf8Bytes,
        string name)
    {
        if (!FitsUtf8Limit(value.Trim(), maximumUtf8Bytes))
        {
            throw new FluidLinkProtocolException(
                "invalid_response",
                $"FluidLink response value '{name}' exceeds its UTF-8 limit.");
        }
    }

    private static bool FitsUtf8Limit(string value, int maximumUtf8Bytes)
    {
        try
        {
            return StrictUtf8.GetByteCount(value) <= maximumUtf8Bytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}
