using System.Net;
using System.Net.Sockets;
using FluidLink;

namespace FluidRuntime.Tests;

public sealed class FluidLinkV2ClientTests
{
    private static readonly byte[] TestSession =
        Enumerable.Range(101, 16).Select(value => (byte)value).ToArray();

    [Fact]
    public async Task Client_exposes_endpoints_only_while_connected()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var listenerEndPoint = (IPEndPoint)listener.LocalEndpoint;
        var acceptTask = listener.AcceptTcpClientAsync();

        await using var client = new FluidLinkV2Client(
            "127.0.0.1",
            listenerEndPoint.Port,
            TimeSpan.FromSeconds(5));
        Assert.Null(client.LocalEndPoint);
        Assert.Null(client.RemoteEndPoint);

        await client.ConnectAsync();
        using var server = await acceptTask;
        var localEndPoint = Assert.IsType<IPEndPoint>(client.LocalEndPoint);
        var remoteEndPoint = Assert.IsType<IPEndPoint>(client.RemoteEndPoint);
        var serverRemoteEndPoint = Assert.IsType<IPEndPoint>(
            server.Client.RemoteEndPoint);

        Assert.Equal(serverRemoteEndPoint, localEndPoint);
        Assert.Equal(listenerEndPoint, remoteEndPoint);

        await client.DisposeAsync();
        Assert.Null(client.LocalEndPoint);
        Assert.Null(client.RemoteEndPoint);
    }

    [Theory]
    [InlineData("127.0.0.1", AddressFamily.InterNetwork)]
    [InlineData("::1", AddressFamily.InterNetworkV6)]
    public async Task Client_preserves_literal_loopback_address_family(
        string host,
        AddressFamily expectedAddressFamily)
    {
        if (expectedAddressFamily == AddressFamily.InterNetworkV6 &&
            !Socket.OSSupportsIPv6)
        {
            return;
        }

        var address = IPAddress.Parse(host);
        using var listener = new TcpListener(address, 0);
        listener.Start();
        var listenerEndPoint = (IPEndPoint)listener.LocalEndpoint;
        var acceptTask = listener.AcceptTcpClientAsync();
        await using var client = new FluidLinkV2Client(
            host,
            listenerEndPoint.Port,
            TimeSpan.FromSeconds(5));

        await client.ConnectAsync();
        using var server = await acceptTask;

        Assert.Equal(
            expectedAddressFamily,
            client.LocalEndPoint?.AddressFamily);
        Assert.Equal(
            expectedAddressFamily,
            client.RemoteEndPoint?.AddressFamily);
    }

    [Fact]
    public async Task Client_rejects_non_loopback_hosts_and_invalid_capabilities()
    {
        Assert.Throws<ArgumentException>(
            () => new FluidLinkV2Client("192.0.2.1"));

        await using var client = new FluidLinkV2Client();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.HandshakeAsync(
                "runtime",
                "0.2",
                (FluidLinkV2Capability)(1UL << 63)));
    }

    [Fact]
    public async Task Client_negotiates_all_typed_events_heartbeat_and_goodbye()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeHappyPathAsync(listener);

        await using var client = new FluidLinkV2Client(
            "127.0.0.1",
            port,
            TimeSpan.FromSeconds(5));
        var welcome = await client.HandshakeAsync("runtime-test", "0.2");
        var sessionDecision = await client.SendSessionEventAsync(
            new FluidLinkV2SessionEvent(
                FluidLinkV2LifecycleAction.Begin,
                "session-typed",
                8_333,
                4UL * 1024 * 1024 * 1024,
                8UL * 1024 * 1024 * 1024));
        var frameDecision = await client.SendFrameEventAsync(
            new FluidLinkV2FrameEvent(
                FluidLinkV2LifecycleAction.Begin,
                7,
                8_333));
        var resourceDecision = await client.SendResourceEventAsync(
            FluidLinkV2ResourceEvent.Register(
                "upload-buffer",
                FluidLinkV2ResourceKind.Buffer,
                FluidLinkV2MemoryLayer.Staging,
                FluidLinkV2Lifetime.Frame,
                4UL * 1024 * 1024,
                ["upload"]));
        var operationDecision = await client.SendOperationEventAsync(
            new FluidLinkV2OperationEvent(
                FluidLinkV2OperationType.Upload,
                FluidLinkV2Queue.Copy,
                "upload-7",
                350,
                4UL * 1024 * 1024,
                Source: "upload-buffer",
                Target: "texture-vram",
                Reason: "duplicate upload",
                Frame: 7,
                Dependencies: ["allocate-7"]));
        var stateDecision = await client.SendStateEventAsync(new());
        var nonce = await client.PingAsync("nonce-v2");
        await client.GoodbyeAsync();
        await serverTask;

        Assert.Equal(FluidLinkV2Protocol.ContractSha256, welcome.ContractSha256);
        Assert.Equal("fluidgateway", welcome.ServerName);
        Assert.Equal("0.64.0", welcome.ServerVersion);
        Assert.Equal(FluidLinkV2Protocol.AllCapabilities, welcome.AcceptedCapabilities);
        Assert.True(sessionDecision.Accepted);
        Assert.True(frameDecision.Accepted);
        Assert.True(resourceDecision.Accepted);
        Assert.True(stateDecision.Accepted);
        Assert.False(operationDecision.Executed);
        Assert.Equal(
            FluidLinkV2DecisionOpcode.DeduplicateIdenticalTransfer,
            operationDecision.DecisionOpcode);
        Assert.Equal(350UL, operationDecision.SavedMicroseconds);
        Assert.Equal(4UL * 1024 * 1024, operationDecision.SavedBytes);
        Assert.Equal("nonce-v2", nonce);
        Assert.True(client.BytesSent > 0);
        Assert.True(client.BytesReceived > 0);
        Assert.False(client.IsNegotiated);
        Assert.Null(client.SessionId);
    }

    [Fact]
    public async Task Client_negotiates_batch_profile_and_validates_decision_vector()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        const string batchId = "0102030405060708090a0b0c0d0e0f10";
        var serverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await using var stream = socket.GetStream();
            var helloFrame = await FluidLinkV2FrameCodec.ReadAsync(stream);
            var hello = FluidLinkV2PayloadCodec.DecodeHello(helloFrame.Payload.Span);
            Assert.Equal(
                FluidLinkV2BatchProtocol.ContractHash.ToArray(),
                hello.ContractHash.ToArray());
            Assert.Equal(
                FluidLinkV2BatchProtocol.AllCapabilities,
                hello.RequestedCapabilities);
            Assert.Equal(
                FluidLinkV2BatchProtocol.RequiredCapabilities,
                hello.RequiredCapabilities);
            await FluidLinkV2FrameCodec.WriteAsync(
                stream,
                ResponseFrame(
                    helloFrame,
                    FluidLinkV2Opcode.Welcome,
                    WelcomePayload(
                        contractHash:
                            FluidLinkV2BatchProtocol.ContractHash.ToArray(),
                        availableCapabilities:
                            FluidLinkV2BatchProtocol.AllCapabilities)));

            var batchFrame = await FluidLinkV2FrameCodec.ReadAsync(stream);
            Assert.Equal(
                (byte)FluidLinkV2EventOpcode.OperationBatch,
                batchFrame.SubjectOpcode);
            var batch = FluidLinkV2PayloadCodec.DecodeOperationBatchEvent(
                batchFrame.Payload.Span);
            Assert.Equal(batchId, batch.BatchId);
            Assert.Equal(2, batch.OperationCount);
            Assert.Equal(800U, batch.CostMicroseconds);
            var decisions = new FluidLinkV2OperationBatchDecision(
                batchId,
                [
                    new FluidLinkV2RuntimeDecision(
                        FluidLinkV2EventOpcode.Operation,
                        FluidLinkV2DecisionOpcode.Execute,
                        FluidLinkV2DecisionStatus.Accepted |
                        FluidLinkV2DecisionStatus.HasExecutionState |
                        FluidLinkV2DecisionStatus.Executed,
                        0,
                        0),
                    new FluidLinkV2RuntimeDecision(
                        FluidLinkV2EventOpcode.Operation,
                        FluidLinkV2DecisionOpcode.DeduplicateIdenticalTransfer,
                        FluidLinkV2DecisionStatus.Accepted |
                        FluidLinkV2DecisionStatus.HasExecutionState,
                        800,
                        64UL * 1024 * 1024)
                ]);
            await FluidLinkV2FrameCodec.WriteAsync(
                stream,
                ResponseFrame(
                    batchFrame,
                    FluidLinkV2Opcode.RuntimeDecision,
                    FluidLinkV2PayloadCodec.EncodeOperationBatchDecision(decisions),
                    decisionOpcode:
                        (byte)FluidLinkV2DecisionOpcode.BatchVector));
        });

        await using var client = new FluidLinkV2Client(
            "127.0.0.1",
            port,
            TimeSpan.FromSeconds(5));
        var welcome = await client.HandshakeBatchAsync("runtime-test", "0.2");
        var decision = await client.SendOperationBatchAsync(
            new FluidLinkV2OperationBatchEvent(
                batchId,
                2,
                FluidLinkV2OperationType.Upload,
                FluidLinkV2Queue.Copy,
                800,
                64UL * 1024 * 1024,
                Source: "ram-buffer",
                Target: "vram-texture",
                Frame: 42));
        await serverTask;

        Assert.Equal(
            FluidLinkV2BatchProtocol.ContractSha256,
            welcome.ContractSha256);
        Assert.Equal(
            FluidLinkV2BatchProtocol.AllCapabilities,
            welcome.AcceptedCapabilities);
        Assert.Equal(batchId, decision.BatchId);
        Assert.Equal(2, decision.Decisions.Count);
        Assert.True(decision.Decisions[0].Executed);
        Assert.False(decision.Decisions[1].Executed);
        Assert.Equal(
            FluidLinkV2DecisionOpcode.DeduplicateIdenticalTransfer,
            decision.Decisions[1].DecisionOpcode);
    }

    [Fact]
    public async Task Client_serializes_concurrent_round_trips_and_sequences()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var sequences = new List<ulong>();
        var serverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await using var stream = socket.GetStream();
            var hello = await FluidLinkV2FrameCodec.ReadAsync(stream);
            await FluidLinkV2FrameCodec.WriteAsync(
                stream,
                ResponseFrame(
                    hello,
                    FluidLinkV2Opcode.Welcome,
                    WelcomePayload()));

            for (var index = 0; index < 2; index += 1)
            {
                var request = await FluidLinkV2FrameCodec.ReadAsync(stream);
                sequences.Add(request.Sequence);
                await Task.Delay(20);
                await FluidLinkV2FrameCodec.WriteAsync(
                    stream,
                    RuntimeDecisionFrame(request));
            }
        });

        await using var client = new FluidLinkV2Client(
            "127.0.0.1",
            port,
            TimeSpan.FromSeconds(5));
        await client.HandshakeAsync("runtime-test", "0.2");
        var first = client.SendFrameEventAsync(
            new FluidLinkV2FrameEvent(FluidLinkV2LifecycleAction.Begin, 1));
        var second = client.SendFrameEventAsync(
            new FluidLinkV2FrameEvent(FluidLinkV2LifecycleAction.Begin, 2));
        await Task.WhenAll(first, second);
        await serverTask;

        Assert.Equal([2UL, 3UL], sequences);
    }

    [Fact]
    public async Task Client_rejects_contract_drift_and_missing_required_capabilities()
    {
        var contractError = await RunHandshakeFailureAsync(
            hello => ResponseFrame(
                hello,
                FluidLinkV2Opcode.Welcome,
                WelcomePayload(contractHash: new byte[32])));
        Assert.Equal("contract_mismatch", contractError.Code);

        var available = FluidLinkV2Protocol.AllCapabilities &
            ~FluidLinkV2Capability.FixedPointUnits;
        var capabilityError = await RunHandshakeFailureAsync(
            hello => ResponseFrame(
                hello,
                FluidLinkV2Opcode.Welcome,
                WelcomePayload(
                    availableCapabilities: available,
                    acceptedCapabilities: available)));
        Assert.Equal("required_capability_unavailable", capabilityError.Code);
    }

    [Fact]
    public async Task Client_fails_closed_on_response_correlation_drift()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await using var stream = socket.GetStream();
            var hello = await FluidLinkV2FrameCodec.ReadAsync(stream);
            var response = ResponseFrame(
                hello,
                FluidLinkV2Opcode.Welcome,
                WelcomePayload(),
                messageId: Enumerable.Repeat((byte)0xFF, 16).ToArray());
            await FluidLinkV2FrameCodec.WriteAsync(stream, response);
        });

        await using var client = new FluidLinkV2Client(
            "127.0.0.1",
            port,
            TimeSpan.FromSeconds(5));
        var exception = await Assert.ThrowsAsync<FluidLinkV2ProtocolException>(
            () => client.HandshakeAsync("runtime-test", "0.2"));
        await serverTask;

        Assert.Equal("correlation_mismatch", exception.Code);
        Assert.False(client.IsNegotiated);
        Assert.Null(client.SessionId);
    }

    [Fact]
    public async Task Client_fails_closed_on_inconsistent_operation_decision()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await using var stream = socket.GetStream();
            var hello = await FluidLinkV2FrameCodec.ReadAsync(stream);
            await FluidLinkV2FrameCodec.WriteAsync(
                stream,
                ResponseFrame(
                    hello,
                    FluidLinkV2Opcode.Welcome,
                    WelcomePayload()));
            var operation = await FluidLinkV2FrameCodec.ReadAsync(stream);
            await FluidLinkV2FrameCodec.WriteAsync(
                stream,
                RuntimeDecisionFrame(
                    operation,
                    FluidLinkV2DecisionOpcode.DeduplicateIdenticalTransfer,
                    FluidLinkV2DecisionStatus.Accepted |
                    FluidLinkV2DecisionStatus.HasExecutionState |
                    FluidLinkV2DecisionStatus.Executed));
        });

        await using var client = new FluidLinkV2Client(
            "127.0.0.1",
            port,
            TimeSpan.FromSeconds(5));
        await client.HandshakeAsync("runtime-test", "0.2");
        var exception = await Assert.ThrowsAsync<FluidLinkV2ProtocolException>(
            () => client.SendOperationEventAsync(
                new FluidLinkV2OperationEvent(
                    FluidLinkV2OperationType.Copy,
                    FluidLinkV2Queue.Copy,
                    "copy-1",
                    10,
                    64,
                    Source: "a",
                    Target: "b")));
        await serverTask;

        Assert.Equal("invalid_runtime_decision", exception.Code);
        Assert.False(client.IsNegotiated);
        Assert.Null(client.SessionId);
    }

    [Fact]
    public async Task Client_surfaces_typed_peer_errors_without_losing_a_valid_session()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await using var stream = socket.GetStream();
            var hello = await FluidLinkV2FrameCodec.ReadAsync(stream);
            await FluidLinkV2FrameCodec.WriteAsync(
                stream,
                ResponseFrame(
                    hello,
                    FluidLinkV2Opcode.Welcome,
                    WelcomePayload()));
            var runtimeEvent = await FluidLinkV2FrameCodec.ReadAsync(stream);
            await FluidLinkV2FrameCodec.WriteAsync(
                stream,
                ErrorFrame(
                    runtimeEvent,
                    FluidLinkV2ErrorCode.RuntimeEventRejected,
                    "resource refused"));
            var goodbye = await FluidLinkV2FrameCodec.ReadAsync(stream);
            await FluidLinkV2FrameCodec.WriteAsync(
                stream,
                ResponseFrame(
                    goodbye,
                    FluidLinkV2Opcode.Goodbye,
                    FluidLinkV2PayloadCodec.EncodeGoodbye()));
        });

        await using var client = new FluidLinkV2Client(
            "127.0.0.1",
            port,
            TimeSpan.FromSeconds(5));
        await client.HandshakeAsync("runtime-test", "0.2");
        var exception = await Assert.ThrowsAsync<FluidLinkV2ProtocolException>(
            () => client.SendResourceEventAsync(
                FluidLinkV2ResourceEvent.Release("missing")));

        Assert.Equal("peer_error", exception.Code);
        Assert.Equal(
            FluidLinkV2ErrorCode.RuntimeEventRejected,
            exception.PeerErrorCode);
        Assert.True(client.IsNegotiated);
        await client.GoodbyeAsync();
        await serverTask;
    }

    [Theory]
    [InlineData(FluidLinkV2ErrorCode.SequenceMismatch)]
    [InlineData(FluidLinkV2ErrorCode.SessionMismatch)]
    public async Task Client_invalidates_the_session_on_fatal_typed_peer_errors(
        FluidLinkV2ErrorCode errorCode)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await using var stream = socket.GetStream();
            var hello = await FluidLinkV2FrameCodec.ReadAsync(stream);
            await FluidLinkV2FrameCodec.WriteAsync(
                stream,
                ResponseFrame(
                    hello,
                    FluidLinkV2Opcode.Welcome,
                    WelcomePayload()));
            var runtimeEvent = await FluidLinkV2FrameCodec.ReadAsync(stream);
            await FluidLinkV2FrameCodec.WriteAsync(
                stream,
                ErrorFrame(runtimeEvent, errorCode, "session cannot continue"));
        });

        await using var client = new FluidLinkV2Client(
            "127.0.0.1",
            port,
            TimeSpan.FromSeconds(5));
        await client.HandshakeAsync("runtime-test", "0.2");
        var exception = await Assert.ThrowsAsync<FluidLinkV2ProtocolException>(
            () => client.SendFrameEventAsync(
                new FluidLinkV2FrameEvent(FluidLinkV2LifecycleAction.Begin, 1)));
        await serverTask;

        Assert.Equal("peer_error", exception.Code);
        Assert.Equal(errorCode, exception.PeerErrorCode);
        Assert.False(client.IsNegotiated);
        Assert.Null(client.SessionId);
    }

    private static async Task ServeHappyPathAsync(TcpListener listener)
    {
        using var socket = await listener.AcceptTcpClientAsync();
        await using var stream = socket.GetStream();

        var helloFrame = await FluidLinkV2FrameCodec.ReadAsync(stream);
        Assert.Equal(FluidLinkV2Opcode.Hello, helloFrame.Opcode);
        Assert.False(helloFrame.HasSession);
        var hello = FluidLinkV2PayloadCodec.DecodeHello(helloFrame.Payload.Span);
        Assert.Equal("runtime-test", hello.ClientName);
        Assert.Equal(FluidLinkV2Protocol.AllCapabilities, hello.RequestedCapabilities);
        Assert.Equal(FluidLinkV2Protocol.RequiredCapabilities, hello.RequiredCapabilities);
        Assert.Equal(
            FluidLinkV2Protocol.ContractHash.ToArray(),
            hello.ContractHash.ToArray());
        await FluidLinkV2FrameCodec.WriteAsync(
            stream,
            ResponseFrame(
                helloFrame,
                FluidLinkV2Opcode.Welcome,
                WelcomePayload()));

        var session = await ReadEventAsync<FluidLinkV2SessionEvent>(stream);
        Assert.Equal("session-typed", session.Event.SessionId);
        Assert.Equal(8_333U, session.Event.FrameBudgetMicroseconds);
        await FluidLinkV2FrameCodec.WriteAsync(
            stream,
            RuntimeDecisionFrame(session.Frame));

        var frame = await ReadEventAsync<FluidLinkV2FrameEvent>(stream);
        Assert.Equal(7UL, frame.Event.Frame);
        Assert.Equal(8_333U, frame.Event.TargetFrameMicroseconds);
        await FluidLinkV2FrameCodec.WriteAsync(
            stream,
            RuntimeDecisionFrame(frame.Frame));

        var resource = await ReadEventAsync<FluidLinkV2ResourceEvent>(stream);
        Assert.Equal("upload-buffer", resource.Event.ResourceId);
        Assert.Equal(4UL * 1024 * 1024, resource.Event.SizeBytes);
        await FluidLinkV2FrameCodec.WriteAsync(
            stream,
            RuntimeDecisionFrame(resource.Frame));

        var operation = await ReadEventAsync<FluidLinkV2OperationEvent>(stream);
        Assert.Equal("upload-7", operation.Event.OperationId);
        Assert.Equal(350U, operation.Event.CostMicroseconds);
        Assert.Equal(4UL * 1024 * 1024, operation.Event.SizeBytes);
        await FluidLinkV2FrameCodec.WriteAsync(
            stream,
            RuntimeDecisionFrame(
                operation.Frame,
                FluidLinkV2DecisionOpcode.DeduplicateIdenticalTransfer,
                FluidLinkV2DecisionStatus.Accepted |
                FluidLinkV2DecisionStatus.HasExecutionState,
                savedMicroseconds: 350,
                savedBytes: 4UL * 1024 * 1024));

        var state = await ReadEventAsync<FluidLinkV2StateEvent>(stream);
        Assert.Equal(FluidLinkV2StateAction.Snapshot, state.Event.Action);
        await FluidLinkV2FrameCodec.WriteAsync(
            stream,
            RuntimeDecisionFrame(state.Frame));

        var ping = await FluidLinkV2FrameCodec.ReadAsync(stream);
        Assert.Equal(FluidLinkV2Opcode.Ping, ping.Opcode);
        Assert.Equal(
            "nonce-v2",
            FluidLinkV2PayloadCodec.DecodePingPong(ping.Payload.Span));
        await FluidLinkV2FrameCodec.WriteAsync(
            stream,
            ResponseFrame(
                ping,
                FluidLinkV2Opcode.Pong,
                FluidLinkV2PayloadCodec.EncodePingPong("nonce-v2")));

        var goodbye = await FluidLinkV2FrameCodec.ReadAsync(stream);
        Assert.Equal(FluidLinkV2Opcode.Goodbye, goodbye.Opcode);
        FluidLinkV2PayloadCodec.DecodeGoodbye(goodbye.Payload.Span);
        await FluidLinkV2FrameCodec.WriteAsync(
            stream,
            ResponseFrame(
                goodbye,
                FluidLinkV2Opcode.Goodbye,
                FluidLinkV2PayloadCodec.EncodeGoodbye()));
    }

    private static async Task<FluidLinkV2ProtocolException> RunHandshakeFailureAsync(
        Func<FluidLinkV2Frame, FluidLinkV2Frame> responseFactory)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await using var stream = socket.GetStream();
            var hello = await FluidLinkV2FrameCodec.ReadAsync(stream);
            await FluidLinkV2FrameCodec.WriteAsync(
                stream,
                responseFactory(hello));
        });
        await using var client = new FluidLinkV2Client(
            "127.0.0.1",
            port,
            TimeSpan.FromSeconds(5));
        var exception = await Assert.ThrowsAsync<FluidLinkV2ProtocolException>(
            () => client.HandshakeAsync("runtime-test", "0.2"));
        await serverTask;
        Assert.False(client.IsNegotiated);
        Assert.Null(client.SessionId);
        return exception;
    }

    private static async Task<(FluidLinkV2Frame Frame, T Event)> ReadEventAsync<T>(
        Stream stream)
        where T : class, IFluidLinkV2RuntimeEvent
    {
        var frame = await FluidLinkV2FrameCodec.ReadAsync(stream);
        Assert.Equal(FluidLinkV2Opcode.RuntimeEvent, frame.Opcode);
        var runtimeEvent = FluidLinkV2PayloadCodec.DecodeRuntimeEvent(
            (FluidLinkV2EventOpcode)frame.SubjectOpcode,
            frame.Payload.Span);
        return (frame, Assert.IsType<T>(runtimeEvent));
    }

    private static byte[] WelcomePayload(
        byte[]? contractHash = null,
        FluidLinkV2Capability availableCapabilities =
            FluidLinkV2Protocol.AllCapabilities,
        FluidLinkV2Capability? acceptedCapabilities = null) =>
        FluidLinkV2PayloadCodec.EncodeWelcome(
            new FluidLinkV2WelcomePayload(
                contractHash is null
                    ? FluidLinkV2Protocol.ContractHash
                    : contractHash,
                availableCapabilities,
                acceptedCapabilities ?? availableCapabilities,
                FluidLinkV2Protocol.MaxPayloadBytes,
                "fluidgateway",
                "0.64.0"));

    private static FluidLinkV2Frame ResponseFrame(
        FluidLinkV2Frame request,
        FluidLinkV2Opcode opcode,
        byte[] payload,
        byte decisionOpcode = 0,
        byte[]? messageId = null) =>
        new(
            FluidLinkV2FrameKind.Response,
            opcode,
            request.SubjectOpcode,
            decisionOpcode,
            FluidLinkV2FrameFlags.Ok | FluidLinkV2FrameFlags.HasSession,
            request.Sequence,
            messageId is null ? request.MessageId : messageId,
            TestSession,
            payload);

    private static FluidLinkV2Frame RuntimeDecisionFrame(
        FluidLinkV2Frame request,
        FluidLinkV2DecisionOpcode decisionOpcode = FluidLinkV2DecisionOpcode.Execute,
        FluidLinkV2DecisionStatus status = FluidLinkV2DecisionStatus.Accepted,
        ulong savedMicroseconds = 0,
        ulong savedBytes = 0) =>
        ResponseFrame(
            request,
            FluidLinkV2Opcode.RuntimeDecision,
            FluidLinkV2PayloadCodec.EncodeRuntimeDecision(
                new FluidLinkV2RuntimeDecisionPayload(
                    status,
                    savedMicroseconds,
                    savedBytes)),
            (byte)decisionOpcode);

    private static FluidLinkV2Frame ErrorFrame(
        FluidLinkV2Frame request,
        FluidLinkV2ErrorCode errorCode,
        string message) =>
        new(
            FluidLinkV2FrameKind.Response,
            FluidLinkV2Opcode.Error,
            request.SubjectOpcode,
            (byte)FluidLinkV2DecisionOpcode.Unknown,
            FluidLinkV2FrameFlags.HasSession,
            request.Sequence,
            request.MessageId,
            TestSession,
            FluidLinkV2PayloadCodec.EncodeError(
                new FluidLinkV2ErrorPayload(errorCode, message)));

}
