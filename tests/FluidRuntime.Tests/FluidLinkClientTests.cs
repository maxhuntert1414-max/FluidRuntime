using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluidLink;
using FluidRuntime.Cli;

namespace FluidRuntime.Tests;

public sealed class FluidLinkClientTests
{
    private static readonly byte[] TestSession = Enumerable
        .Repeat((byte)0x22, 16)
        .ToArray();

    [Fact]
    public void Client_rejects_non_loopback_hosts()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new FluidLinkClient("192.0.2.10", 8765));

        Assert.Contains("loopback", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Probe_options_parse_bounded_endpoint_and_output()
    {
        var options = FluidLinkProbeOptions.Parse(
        [
            "link-probe",
            "--host", "localhost",
            "--port", "9123",
            "--timeout-ms", "2500",
            "--out", "probe.json"
        ]);

        Assert.Equal("localhost", options.Host);
        Assert.Equal(9123, options.Port);
        Assert.Equal(2500, options.TimeoutMs);
        Assert.Equal("probe.json", options.OutputPath);
    }

    [Fact]
    public void Bundled_contract_matches_the_declared_fingerprint_and_layout()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "contracts",
            "fluidlink-v1.contract.json");
        var content = File.ReadAllBytes(path);
        var digest = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        using var contract = JsonDocument.Parse(content);

        Assert.Equal(FluidLinkProtocol.ContractSha256, digest);
        Assert.Equal(
            FluidLinkProtocol.HeaderSize,
            contract.RootElement.GetProperty("wire").GetProperty("header_size").GetInt32());
        Assert.Equal(
            10,
            contract.RootElement.GetProperty("opcodes").GetProperty("runtime_event")
                .GetInt32());
        Assert.Equal(
            FluidLinkProtocol.MaxJsonDepth,
            contract.RootElement.GetProperty("limits").GetProperty("max_json_depth")
                .GetInt32());
        Assert.Equal(
            "exact-sha256-match",
            contract.RootElement.GetProperty("handshake")
                .GetProperty("contract_rule").GetString());
    }

    [Fact]
    public void Protocol_uses_single_byte_numeric_opcodes()
    {
        Assert.Equal(10, (byte)FluidLinkOpcode.RuntimeEvent);
        Assert.Equal(103, (byte)FluidLinkEventOpcode.Operation);
        Assert.Equal(2, (byte)FluidLinkDecisionOpcode.DeduplicateIdenticalTransfer);
        Assert.Equal(
            "deduplicate-identical-transfer",
            FluidLinkProtocol.DecisionPolicyName(
                FluidLinkDecisionOpcode.DeduplicateIdenticalTransfer));
    }

    [Fact]
    public void Codec_round_trips_the_fixed_binary_header()
    {
        var frame = RequestFrame(
            FluidLinkOpcode.RuntimeEvent,
            sequence: 2,
            payload: new { id = "upload-1" },
            sessionId: TestSession,
            subjectOpcode: (byte)FluidLinkEventOpcode.Operation,
            messageId: Enumerable.Repeat((byte)0x11, 16).ToArray());

        var wire = FluidLinkFrameCodec.Encode(frame);
        var decoded = FluidLinkFrameCodec.Decode(wire);
        var equivalentJsonBytes =
            FluidLinkFrameCodec.EstimateEquivalentJsonEnvelopeSize(frame);

        Assert.Equal("FLNK", Encoding.ASCII.GetString(wire, 0, 4));
        Assert.Equal(FluidLinkProtocol.WireVersion, wire[4]);
        Assert.Equal((byte)FluidLinkFrameKind.Request, wire[5]);
        Assert.Equal((byte)FluidLinkOpcode.RuntimeEvent, wire[6]);
        Assert.Equal((byte)FluidLinkEventOpcode.Operation, wire[7]);
        Assert.Equal((byte)FluidLinkDecisionOpcode.Execute, wire[8]);
        Assert.Equal(frame.Sequence, decoded.Sequence);
        Assert.Equal(frame.Payload.GetRawText(), decoded.Payload.GetRawText());
        Assert.True(decoded.MessageId.Span.SequenceEqual(frame.MessageId.Span));
        Assert.True(decoded.SessionId.Span.SequenceEqual(frame.SessionId.Span));
        Assert.True(equivalentJsonBytes > wire.Length);
        Assert.DoesNotContain(
            "runtime.event",
            Encoding.UTF8.GetString(wire),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Codec_reassembles_fragmented_reads()
    {
        var frame = RequestFrame(
            FluidLinkOpcode.Hello,
            sequence: 1,
            payload: new { client = new { name = "test", version = "1" } });
        var wire = FluidLinkFrameCodec.Encode(frame);
        await using var stream = new FragmentedReadStream(wire, chunkSize: 3);

        var decoded = await FluidLinkFrameCodec.ReadAsync(stream);

        Assert.Equal(FluidLinkOpcode.Hello, decoded.Opcode);
        Assert.Equal(wire.Length, decoded.WireSize);
    }

    [Fact]
    public void Codec_rejects_truncated_frames()
    {
        var frame = RequestFrame(
            FluidLinkOpcode.Hello,
            sequence: 1,
            payload: new { client = new { name = "test", version = "1" } });
        var wire = FluidLinkFrameCodec.Encode(frame);

        var exception = Assert.Throws<FluidLinkProtocolException>(
            () => FluidLinkFrameCodec.Decode(wire[..^1]));

        Assert.Equal("frame_size_mismatch", exception.Code);
    }

    [Fact]
    public async Task Codec_read_rejects_a_truncated_stream_with_a_protocol_error()
    {
        var frame = RequestFrame(
            FluidLinkOpcode.Hello,
            sequence: 1,
            payload: new { client = new { name = "test", version = "1" } });
        var wire = FluidLinkFrameCodec.Encode(frame);
        await using var stream = new MemoryStream(wire[..^1], writable: false);

        var exception = await Assert.ThrowsAsync<FluidLinkProtocolException>(
            async () => await FluidLinkFrameCodec.ReadAsync(stream));

        Assert.Equal("truncated_frame", exception.Code);
    }

    [Fact]
    public void Codec_rejects_unknown_flags_reserved_bits_and_oversized_payloads()
    {
        var frame = RequestFrame(
            FluidLinkOpcode.Hello,
            sequence: 1,
            payload: new { client = new { name = "test", version = "1" } });

        var unknownFlags = FluidLinkFrameCodec.Encode(frame);
        unknownFlags[9] |= 0x80;
        var flagsException = Assert.Throws<FluidLinkProtocolException>(
            () => FluidLinkFrameCodec.Decode(unknownFlags));
        Assert.Equal("invalid_flags", flagsException.Code);

        var reserved = FluidLinkFrameCodec.Encode(frame);
        reserved[10] = 1;
        var reservedException = Assert.Throws<FluidLinkProtocolException>(
            () => FluidLinkFrameCodec.Decode(reserved));
        Assert.Equal("invalid_reserved_bits", reservedException.Code);

        var oversized = FluidLinkFrameCodec.Encode(frame);
        BinaryPrimitives.WriteUInt32LittleEndian(
            oversized.AsSpan(52, 4),
            FluidLinkProtocol.MaxPayloadBytes + 1u);
        var sizeException = Assert.Throws<FluidLinkProtocolException>(
            () => FluidLinkFrameCodec.Decode(oversized));
        Assert.Equal("payload_too_large", sizeException.Code);
    }

    [Fact]
    public async Task Client_negotiates_event_heartbeat_and_clean_shutdown()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeHappyPathAsync(listener);

        await using var client = new FluidLinkClient(
            "127.0.0.1",
            port,
            TimeSpan.FromSeconds(5));
        var welcome = await client.HandshakeAsync("test-runtime", "0.1");
        var nonce = await client.PingAsync("nonce-1");
        var decision = await client.SendRuntimeEventAsync(
            FluidLinkEventOpcode.Resource,
            new
            {
                Id = "buffer-1",
                Kind = "buffer",
                Memory = "ram",
                SizeMb = 4
            });
        await client.GoodbyeAsync();
        await serverTask;

        Assert.Equal("fluidgateway", welcome.ServerName);
        Assert.Equal(FluidLinkProtocol.ContractSha256, welcome.ContractSha256);
        Assert.Equal("0.63.0", welcome.ServerVersion);
        Assert.Contains("binary.framing.v1", welcome.AcceptedCapabilities);
        Assert.Equal("nonce-1", nonce);
        Assert.True(decision.Accepted);
        Assert.Equal(FluidLinkEventOpcode.Resource, decision.EventOpcode);
        Assert.Equal(FluidLinkDecisionOpcode.Execute, decision.DecisionOpcode);
        Assert.Null(decision.Executed);
        Assert.True(client.BytesSent > 0);
        Assert.True(client.BytesReceived > 0);
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
            var hello = await FluidLinkFrameCodec.ReadAsync(stream);
            await FluidLinkFrameCodec.WriteAsync(
                stream,
                ResponseFrame(hello, FluidLinkOpcode.Welcome, WelcomePayload()));

            for (var index = 0; index < 2; index += 1)
            {
                var request = await FluidLinkFrameCodec.ReadAsync(stream);
                sequences.Add(request.Sequence);
                await Task.Delay(25);
                await FluidLinkFrameCodec.WriteAsync(
                    stream,
                    ResponseFrame(
                        request,
                        FluidLinkOpcode.RuntimeDecision,
                        new { accepted = true },
                        subjectOpcode: request.SubjectOpcode));
            }
        });

        await using var client = new FluidLinkClient(
            "127.0.0.1",
            port,
            TimeSpan.FromSeconds(5));
        await client.HandshakeAsync("test-runtime", "0.1");
        var first = client.SendRuntimeEventAsync(
            FluidLinkEventOpcode.Resource,
            new { Id = "buffer-1" });
        var second = client.SendRuntimeEventAsync(
            FluidLinkEventOpcode.Resource,
            new { Id = "buffer-2" });

        await Task.WhenAll(first, second);
        await serverTask;

        Assert.Equal([2UL, 3UL], sequences);
    }

    [Fact]
    public async Task Client_rejects_contract_drift_during_handshake()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await using var stream = socket.GetStream();
            var hello = await FluidLinkFrameCodec.ReadAsync(stream);
            await FluidLinkFrameCodec.WriteAsync(
                stream,
                ResponseFrame(
                    hello,
                    FluidLinkOpcode.Welcome,
                    WelcomePayload(contractSha256: new string('0', 64))));
        });

        await using var client = new FluidLinkClient(
            "127.0.0.1",
            port,
            TimeSpan.FromSeconds(5));
        var exception = await Assert.ThrowsAsync<FluidLinkProtocolException>(
            () => client.HandshakeAsync("test-runtime", "0.1"));
        await serverTask;

        Assert.Equal("contract_mismatch", exception.Code);
        Assert.Null(client.SessionId);
    }

    [Fact]
    public async Task Client_rejects_a_mismatched_heartbeat_and_invalidates_session()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await using var stream = socket.GetStream();
            var hello = await FluidLinkFrameCodec.ReadAsync(stream);
            await FluidLinkFrameCodec.WriteAsync(
                stream,
                ResponseFrame(hello, FluidLinkOpcode.Welcome, WelcomePayload()));
            var ping = await FluidLinkFrameCodec.ReadAsync(stream);
            await FluidLinkFrameCodec.WriteAsync(
                stream,
                ResponseFrame(
                    ping,
                    FluidLinkOpcode.Pong,
                    new { nonce = "wrong" }));
        });

        await using var client = new FluidLinkClient(
            "127.0.0.1",
            port,
            TimeSpan.FromSeconds(5));
        await client.HandshakeAsync("test-runtime", "0.1");
        var exception = await Assert.ThrowsAsync<FluidLinkProtocolException>(
            () => client.PingAsync("expected"));
        await serverTask;

        Assert.Equal("heartbeat_mismatch", exception.Code);
        Assert.Null(client.SessionId);
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
            var request = await FluidLinkFrameCodec.ReadAsync(stream);
            var response = ResponseFrame(
                request,
                FluidLinkOpcode.Welcome,
                WelcomePayload(),
                messageId: Enumerable.Repeat((byte)0xFF, 16).ToArray());
            await FluidLinkFrameCodec.WriteAsync(stream, response);
        });

        await using var client = new FluidLinkClient(
            "127.0.0.1",
            port,
            TimeSpan.FromSeconds(5));
        var exception = await Assert.ThrowsAsync<FluidLinkProtocolException>(
            () => client.HandshakeAsync("test-runtime", "0.1"));
        await serverTask;

        Assert.Equal("correlation_mismatch", exception.Code);
    }

    private static async Task ServeHappyPathAsync(TcpListener listener)
    {
        using var socket = await listener.AcceptTcpClientAsync();
        await using var stream = socket.GetStream();

        var hello = await FluidLinkFrameCodec.ReadAsync(stream);
        Assert.Equal(FluidLinkOpcode.Hello, hello.Opcode);
        Assert.False(hello.HasSession);
        Assert.Equal(
            FluidLinkProtocol.ContractSha256,
            hello.Payload.GetProperty("contract_sha256").GetString());
        await FluidLinkFrameCodec.WriteAsync(
            stream,
            ResponseFrame(hello, FluidLinkOpcode.Welcome, WelcomePayload()));

        var ping = await FluidLinkFrameCodec.ReadAsync(stream);
        Assert.Equal(FluidLinkOpcode.Ping, ping.Opcode);
        await FluidLinkFrameCodec.WriteAsync(
            stream,
            ResponseFrame(
                ping,
                FluidLinkOpcode.Pong,
                new { nonce = "nonce-1" }));

        var runtimeEvent = await FluidLinkFrameCodec.ReadAsync(stream);
        Assert.Equal(FluidLinkOpcode.RuntimeEvent, runtimeEvent.Opcode);
        Assert.Equal(
            (byte)FluidLinkEventOpcode.Resource,
            runtimeEvent.SubjectOpcode);
        Assert.False(runtimeEvent.Payload.TryGetProperty("event", out _));
        Assert.Equal(
            "buffer-1",
            runtimeEvent.Payload.GetProperty("id").GetString());
        await FluidLinkFrameCodec.WriteAsync(
            stream,
            ResponseFrame(
                runtimeEvent,
                FluidLinkOpcode.RuntimeDecision,
                new { accepted = true },
                subjectOpcode: (byte)FluidLinkEventOpcode.Resource,
                decisionOpcode: (byte)FluidLinkDecisionOpcode.Execute));

        var goodbye = await FluidLinkFrameCodec.ReadAsync(stream);
        Assert.Equal(FluidLinkOpcode.Goodbye, goodbye.Opcode);
        await FluidLinkFrameCodec.WriteAsync(
            stream,
            ResponseFrame(
                goodbye,
                FluidLinkOpcode.Goodbye,
                new { closed = true }));
    }

    private static FluidLinkFrame RequestFrame(
        FluidLinkOpcode opcode,
        ulong sequence,
        object payload,
        byte[]? sessionId = null,
        byte subjectOpcode = 0,
        byte[]? messageId = null)
    {
        var flags = FluidLinkFrameFlags.JsonPayload;
        if (sessionId is not null)
        {
            flags |= FluidLinkFrameFlags.HasSession;
        }
        return new FluidLinkFrame(
            FluidLinkFrameKind.Request,
            opcode,
            subjectOpcode,
            0,
            flags,
            sequence,
            messageId ?? Guid.NewGuid().ToByteArray(),
            sessionId is null ? ReadOnlyMemory<byte>.Empty : sessionId,
            JsonSerializer.SerializeToElement(payload));
    }

    private static FluidLinkFrame ResponseFrame(
        FluidLinkFrame request,
        FluidLinkOpcode opcode,
        object payload,
        byte subjectOpcode = 0,
        byte decisionOpcode = 0,
        byte[]? messageId = null)
    {
        return new FluidLinkFrame(
            FluidLinkFrameKind.Response,
            opcode,
            subjectOpcode,
            decisionOpcode,
            FluidLinkFrameFlags.JsonPayload |
                FluidLinkFrameFlags.HasSession |
                FluidLinkFrameFlags.Ok,
            request.Sequence,
            messageId is null ? request.MessageId : messageId,
            TestSession,
            JsonSerializer.SerializeToElement(payload));
    }

    private static object WelcomePayload(string? contractSha256 = null) => new
    {
        contract_sha256 = contractSha256 ?? FluidLinkProtocol.ContractSha256,
        server = new { name = "fluidgateway", version = "0.63.0" },
        available_capabilities = FluidLinkProtocol.RuntimeCapabilities,
        accepted_capabilities = FluidLinkProtocol.RuntimeCapabilities,
        limits = new
        {
            max_payload_bytes = FluidLinkProtocol.MaxPayloadBytes,
            max_json_depth = FluidLinkProtocol.MaxJsonDepth
        }
    };

    private sealed class FragmentedReadStream : Stream
    {
        private readonly MemoryStream inner;
        private readonly int chunkSize;

        public FragmentedReadStream(byte[] data, int chunkSize)
        {
            inner = new MemoryStream(data, writable: false);
            this.chunkSize = chunkSize;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, Math.Min(count, chunkSize));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
