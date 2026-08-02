using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using FluidLink;

namespace FluidRuntime.Tests;

public sealed class FluidLinkV2CodecTests
{
    private static readonly byte[] MessageId =
        Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
    private static readonly byte[] SessionId =
        Enumerable.Range(17, 16).Select(value => (byte)value).ToArray();

    [Fact]
    public void Bundled_batch_contract_has_the_exact_extension_fingerprint()
    {
        var contract = File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "contracts",
            "fluidlink-v2-batch.contract.json"));

        Assert.Equal(
            FluidLinkV2BatchProtocol.ContractSha256,
            Convert.ToHexString(SHA256.HashData(contract)).ToLowerInvariant());
        Assert.Equal(
            FluidLinkV2Protocol.AllCapabilities |
            FluidLinkV2Capability.BatchedRuntimeEvents,
            FluidLinkV2BatchProtocol.AllCapabilities);
        Assert.Equal(256, FluidLinkV2BatchProtocol.MaxOperations);
    }

    [Fact]
    public void Batch_golden_vectors_match_the_gateway_encoder_byte_for_byte()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "contracts",
            "fluidlink-v2-batch.golden.json")));
        var root = fixture.RootElement;
        var messageId = Convert.FromHexString(
            root.GetProperty("message_id_hex").GetString()!);
        var sessionId = Convert.FromHexString(
            root.GetProperty("session_id_hex").GetString()!);
        var batchId = root.GetProperty("batch_id_hex").GetString()!;
        var hello = new FluidLinkV2Frame(
            FluidLinkV2FrameKind.Request,
            FluidLinkV2Opcode.Hello,
            0,
            0,
            FluidLinkV2FrameFlags.None,
            1,
            messageId,
            ReadOnlyMemory<byte>.Empty,
            FluidLinkV2PayloadCodec.EncodeHello(
                new FluidLinkV2HelloPayload(
                    FluidLinkV2BatchProtocol.ContractHash,
                    FluidLinkV2BatchProtocol.AllCapabilities,
                    FluidLinkV2BatchProtocol.RequiredCapabilities,
                    "fluidruntime",
                    "0.17.0")));
        var batch = new FluidLinkV2Frame(
            FluidLinkV2FrameKind.Request,
            FluidLinkV2Opcode.RuntimeEvent,
            (byte)FluidLinkV2EventOpcode.OperationBatch,
            0,
            FluidLinkV2FrameFlags.HasSession,
            2,
            messageId,
            sessionId,
            FluidLinkV2PayloadCodec.EncodeOperationBatchEvent(
                new FluidLinkV2OperationBatchEvent(
                    batchId,
                    2,
                    FluidLinkV2OperationType.Upload,
                    FluidLinkV2Queue.Copy,
                    800,
                    64UL * 1024 * 1024,
                    Source: "ram-buffer",
                    Target: "vram-texture",
                    Reason: "duplicate upload",
                    Frame: 42,
                    Dependencies: ["allocate-1"])));
        var frames = new Dictionary<string, FluidLinkV2Frame>
        {
            ["batch_hello_request"] = hello,
            ["batch_welcome_response"] = new(
                FluidLinkV2FrameKind.Response,
                FluidLinkV2Opcode.Welcome,
                0,
                0,
                FluidLinkV2FrameFlags.Ok | FluidLinkV2FrameFlags.HasSession,
                1,
                messageId,
                sessionId,
                FluidLinkV2PayloadCodec.EncodeWelcome(
                    new FluidLinkV2WelcomePayload(
                        FluidLinkV2BatchProtocol.ContractHash,
                        FluidLinkV2BatchProtocol.AllCapabilities,
                        FluidLinkV2BatchProtocol.AllCapabilities,
                        FluidLinkV2Protocol.MaxPayloadBytes,
                        "fluidgateway",
                        "0.65.0"))),
            ["operation_batch_request"] = batch,
            ["operation_batch_decision_response"] = new(
                FluidLinkV2FrameKind.Response,
                FluidLinkV2Opcode.RuntimeDecision,
                (byte)FluidLinkV2EventOpcode.OperationBatch,
                (byte)FluidLinkV2DecisionOpcode.BatchVector,
                FluidLinkV2FrameFlags.Ok | FluidLinkV2FrameFlags.HasSession,
                2,
                messageId,
                sessionId,
                FluidLinkV2PayloadCodec.EncodeOperationBatchDecision(
                    new FluidLinkV2OperationBatchDecision(
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
                                FluidLinkV2DecisionOpcode
                                    .DeduplicateIdenticalTransfer,
                                FluidLinkV2DecisionStatus.Accepted |
                                FluidLinkV2DecisionStatus.HasExecutionState,
                                800,
                                64UL * 1024 * 1024)
                        ])))
        };

        Assert.Equal(
            FluidLinkV2BatchProtocol.ContractSha256,
            root.GetProperty("contract_sha256").GetString());
        foreach (var vector in root.GetProperty("vectors").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            var wire = FluidLinkV2FrameCodec.Encode(frames[name]);
            Assert.Equal(vector.GetProperty("wire_bytes").GetInt32(), wire.Length);
            Assert.Equal(
                vector.GetProperty("wire_hex").GetString(),
                Convert.ToHexString(wire).ToLowerInvariant());
        }
    }

    [Fact]
    public void Operation_batch_payloads_have_exact_layout_and_round_trip()
    {
        const string batchId = "0102030405060708090a0b0c0d0e0f10";
        var batch = new FluidLinkV2OperationBatchEvent(
            batchId,
            2,
            FluidLinkV2OperationType.Upload,
            FluidLinkV2Queue.Copy,
            0x01020304,
            0x0102030405060708,
            Source: "s",
            Target: "t",
            Reason: "r",
            Frame: 0x1112131415161718,
            Dependencies: ["d"]);
        var encoded = FluidLinkV2PayloadCodec.EncodeOperationBatchEvent(batch);

        Assert.Equal(
            Convert.FromHexString(
                "0102030405060708090a0b0c0d0e0f10" +
                "020004020f01007301007401007204030201" +
                "0807060504030201181716151413121101010064"),
            encoded);
        var decoded = FluidLinkV2PayloadCodec.DecodeOperationBatchEvent(encoded);
        Assert.Equal(batchId, decoded.BatchId);
        Assert.Equal(2, decoded.OperationCount);
        Assert.Equal(FluidLinkV2OperationType.Upload, decoded.OperationType);
        Assert.Equal(FluidLinkV2Queue.Copy, decoded.Queue);
        Assert.Equal(0x01020304U, decoded.CostMicroseconds);
        Assert.Equal(0x0102030405060708UL, decoded.SizeBytes);
        Assert.Equal(0x1112131415161718UL, decoded.Frame);
        Assert.Equal(["d"], decoded.Dependencies);

        var decision = new FluidLinkV2OperationBatchDecision(
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
        var decodedDecision = FluidLinkV2PayloadCodec.DecodeOperationBatchDecision(
            FluidLinkV2PayloadCodec.EncodeOperationBatchDecision(decision));
        Assert.Equal(batchId, decodedDecision.BatchId);
        Assert.Equal(2, decodedDecision.Decisions.Count);
        Assert.True(decodedDecision.Decisions[0].Executed);
        Assert.False(decodedDecision.Decisions[1].Executed);
        Assert.Equal(800UL, decodedDecision.Decisions[1].SavedMicroseconds);
    }

    [Fact]
    public void Operation_batch_codec_rejects_invalid_identity_count_and_decisions()
    {
        Assert.Throws<FluidLinkV2ProtocolException>(
            () => FluidLinkV2PayloadCodec.EncodeOperationBatchEvent(
                BatchEvent("00000000000000000000000000000000", 1)));
        Assert.Throws<FluidLinkV2ProtocolException>(
            () => FluidLinkV2PayloadCodec.EncodeOperationBatchEvent(
                BatchEvent("0102030405060708090a0b0c0d0e0f10", 0)));
        Assert.Throws<FluidLinkV2ProtocolException>(
            () => FluidLinkV2PayloadCodec.EncodeOperationBatchEvent(
                BatchEvent("0102030405060708090a0b0c0d0e0f10", 257)));
        Assert.Throws<FluidLinkV2ProtocolException>(
            () => FluidLinkV2PayloadCodec.EncodeOperationBatchDecision(
                new FluidLinkV2OperationBatchDecision(
                    "0102030405060708090a0b0c0d0e0f10",
                    [
                        new FluidLinkV2RuntimeDecision(
                            FluidLinkV2EventOpcode.Operation,
                            FluidLinkV2DecisionOpcode.Execute,
                            FluidLinkV2DecisionStatus.Accepted |
                            FluidLinkV2DecisionStatus.HasExecutionState,
                            0,
                            0)
                    ])));
    }

    [Fact]
    public void Operation_payload_has_exact_positional_little_endian_layout()
    {
        var payload = FluidLinkV2PayloadCodec.EncodeOperationEvent(
            new FluidLinkV2OperationEvent(
                FluidLinkV2OperationType.Copy,
                FluidLinkV2Queue.Cpu,
                "o",
                0x01020304,
                0x0102030405060708,
                Source: "s",
                Target: "t",
                Reason: "r",
                Frame: 0x1112131415161718,
                Dependencies: ["d"]));

        byte[] expected =
        [
            0x01, 0x01, 0x0F,
            0x01, 0x00, 0x6F,
            0x01, 0x00, 0x73,
            0x01, 0x00, 0x74,
            0x01, 0x00, 0x72,
            0x04, 0x03, 0x02, 0x01,
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
            0x18, 0x17, 0x16, 0x15, 0x14, 0x13, 0x12, 0x11,
            0x01,
            0x01, 0x00, 0x64
        ];
        Assert.Equal(expected, payload);

        var decoded = FluidLinkV2PayloadCodec.DecodeOperationEvent(payload);
        Assert.Equal(FluidLinkV2OperationType.Copy, decoded.OperationType);
        Assert.Equal(FluidLinkV2Queue.Cpu, decoded.Queue);
        Assert.Equal("o", decoded.OperationId);
        Assert.Equal("s", decoded.Source);
        Assert.Equal("t", decoded.Target);
        Assert.Equal("r", decoded.Reason);
        Assert.Equal(0x01020304U, decoded.CostMicroseconds);
        Assert.Equal(0x0102030405060708UL, decoded.SizeBytes);
        Assert.Equal(0x1112131415161718UL, decoded.Frame);
        Assert.Equal(["d"], decoded.Dependencies);
    }

    [Fact]
    public void Hello_and_welcome_use_hash_bytes_capability_u64_and_bounded_text()
    {
        var hello = FluidLinkV2PayloadCodec.EncodeHello(
            new FluidLinkV2HelloPayload(
                FluidLinkV2Protocol.ContractHash,
                FluidLinkV2Protocol.AllCapabilities,
                FluidLinkV2Protocol.RequiredCapabilities,
                "runtime",
                "0.2"));

        Assert.Equal(
            Convert.FromHexString(
                "0d24d96aec32d74e123f9e198e51adde" +
                "74ddf190e8c40b0ac18bddf5c4108b2f" +
                "7f000000000000001b00000000000000" +
                "0772756e74696d6503302e32"),
            hello);
        Assert.Equal(32 + 8 + 8 + 1 + 7 + 1 + 3, hello.Length);
        Assert.Equal(
            FluidLinkV2Protocol.ContractHash.ToArray(),
            hello.AsSpan(0, 32).ToArray());
        Assert.Equal(
            (ulong)FluidLinkV2Protocol.AllCapabilities,
            BinaryPrimitives.ReadUInt64LittleEndian(hello.AsSpan(32, 8)));
        Assert.Equal(
            (ulong)FluidLinkV2Protocol.RequiredCapabilities,
            BinaryPrimitives.ReadUInt64LittleEndian(hello.AsSpan(40, 8)));
        Assert.Equal(7, hello[48]);

        var decodedHello = FluidLinkV2PayloadCodec.DecodeHello(hello);
        Assert.Equal("runtime", decodedHello.ClientName);
        Assert.Equal("0.2", decodedHello.ClientVersion);

        var welcome = new FluidLinkV2WelcomePayload(
            FluidLinkV2Protocol.ContractHash,
            FluidLinkV2Protocol.AllCapabilities,
            FluidLinkV2Protocol.RequiredCapabilities,
            FluidLinkV2Protocol.MaxPayloadBytes,
            "gateway",
            "0.64");
        var decodedWelcome = FluidLinkV2PayloadCodec.DecodeWelcome(
            FluidLinkV2PayloadCodec.EncodeWelcome(welcome));
        Assert.Equal(welcome.AvailableCapabilities, decodedWelcome.AvailableCapabilities);
        Assert.Equal(welcome.AcceptedCapabilities, decodedWelcome.AcceptedCapabilities);
        Assert.Equal(welcome.MaxPayloadBytes, decodedWelcome.MaxPayloadBytes);
        Assert.Equal("gateway", decodedWelcome.ServerName);
        Assert.Equal("0.64", decodedWelcome.ServerVersion);
    }

    [Fact]
    public void Bundled_contract_and_all_canonical_golden_vectors_are_exact()
    {
        var contract = File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "contracts",
            "fluidlink-v2.contract.json"));
        Assert.Equal(
            FluidLinkV2Protocol.ContractSha256,
            Convert.ToHexString(SHA256.HashData(contract)).ToLowerInvariant());

        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "contracts",
            "fluidlink-v2.golden.json")));
        var root = fixture.RootElement;
        Assert.Equal(
            FluidLinkV2Protocol.ContractSha256,
            root.GetProperty("contract_sha256").GetString());
        var messageId = Convert.FromHexString(
            root.GetProperty("message_id_hex").GetString()!);
        var sessionId = Convert.FromHexString(
            root.GetProperty("session_id_hex").GetString()!);

        var hello = new FluidLinkV2Frame(
            FluidLinkV2FrameKind.Request,
            FluidLinkV2Opcode.Hello,
            0,
            0,
            FluidLinkV2FrameFlags.None,
            1,
            messageId,
            ReadOnlyMemory<byte>.Empty,
            FluidLinkV2PayloadCodec.EncodeHello(
                new FluidLinkV2HelloPayload(
                    FluidLinkV2Protocol.ContractHash,
                    FluidLinkV2Protocol.AllCapabilities,
                    FluidLinkV2Protocol.RequiredCapabilities,
                    "fluidruntime",
                    "0.14.0")));
        var sessionBegin = new FluidLinkV2Frame(
            FluidLinkV2FrameKind.Request,
            FluidLinkV2Opcode.RuntimeEvent,
            (byte)FluidLinkV2EventOpcode.Session,
            0,
            FluidLinkV2FrameFlags.HasSession,
            2,
            messageId,
            sessionId,
            FluidLinkV2PayloadCodec.EncodeSessionEvent(
                new FluidLinkV2SessionEvent(
                    FluidLinkV2LifecycleAction.Begin,
                    "golden",
                    FrameBudgetMicroseconds: 16_667,
                    RamBudgetBytes: 4UL * 1024 * 1024 * 1024,
                    VramBudgetBytes: 2UL * 1024 * 1024 * 1024,
                    SharedBudgetBytes: 1UL * 1024 * 1024 * 1024,
                    StagingBudgetBytes: 128UL * 1024 * 1024,
                    SwapchainBudgetBytes: 64UL * 1024 * 1024)));
        var frameBegin = new FluidLinkV2Frame(
            FluidLinkV2FrameKind.Request,
            FluidLinkV2Opcode.RuntimeEvent,
            (byte)FluidLinkV2EventOpcode.Frame,
            0,
            FluidLinkV2FrameFlags.HasSession,
            3,
            messageId,
            sessionId,
            FluidLinkV2PayloadCodec.EncodeFrameEvent(
                new FluidLinkV2FrameEvent(
                    FluidLinkV2LifecycleAction.Begin,
                    42,
                    16_667)));
        var resourceRegister = new FluidLinkV2Frame(
            FluidLinkV2FrameKind.Request,
            FluidLinkV2Opcode.RuntimeEvent,
            (byte)FluidLinkV2EventOpcode.Resource,
            0,
            FluidLinkV2FrameFlags.HasSession,
            4,
            messageId,
            sessionId,
            FluidLinkV2PayloadCodec.EncodeResourceEvent(
                FluidLinkV2ResourceEvent.Register(
                    "texture-1",
                    FluidLinkV2ResourceKind.Texture,
                    FluidLinkV2MemoryLayer.Vram,
                    FluidLinkV2Lifetime.Asset,
                    16UL * 1024 * 1024,
                    ["hero", "diffuse"])));
        var operation = new FluidLinkV2Frame(
            FluidLinkV2FrameKind.Request,
            FluidLinkV2Opcode.RuntimeEvent,
            (byte)FluidLinkV2EventOpcode.Operation,
            0,
            FluidLinkV2FrameFlags.HasSession,
            5,
            messageId,
            sessionId,
            FluidLinkV2PayloadCodec.EncodeOperationEvent(
                new FluidLinkV2OperationEvent(
                    FluidLinkV2OperationType.Upload,
                    FluidLinkV2Queue.Copy,
                    "upload-1",
                    800,
                    64UL * 1024 * 1024,
                    Source: "ram",
                    Target: "vram",
                    Reason: "duplicate upload",
                    Frame: 0,
                    Dependencies: ["allocate-1"])));
        var state = new FluidLinkV2Frame(
            FluidLinkV2FrameKind.Request,
            FluidLinkV2Opcode.RuntimeEvent,
            (byte)FluidLinkV2EventOpcode.State,
            0,
            FluidLinkV2FrameFlags.HasSession,
            6,
            messageId,
            sessionId,
            FluidLinkV2PayloadCodec.EncodeStateEvent(new()));
        var ping = new FluidLinkV2Frame(
            FluidLinkV2FrameKind.Request,
            FluidLinkV2Opcode.Ping,
            0,
            0,
            FluidLinkV2FrameFlags.HasSession,
            7,
            messageId,
            sessionId,
            FluidLinkV2PayloadCodec.EncodePingPong("nonce-v2"));
        var resourceRelease = new FluidLinkV2Frame(
            FluidLinkV2FrameKind.Request,
            FluidLinkV2Opcode.RuntimeEvent,
            (byte)FluidLinkV2EventOpcode.Resource,
            0,
            FluidLinkV2FrameFlags.HasSession,
            8,
            messageId,
            sessionId,
            FluidLinkV2PayloadCodec.EncodeResourceEvent(
                FluidLinkV2ResourceEvent.Release("texture-1")));
        var frameEnd = new FluidLinkV2Frame(
            FluidLinkV2FrameKind.Request,
            FluidLinkV2Opcode.RuntimeEvent,
            (byte)FluidLinkV2EventOpcode.Frame,
            0,
            FluidLinkV2FrameFlags.HasSession,
            9,
            messageId,
            sessionId,
            FluidLinkV2PayloadCodec.EncodeFrameEvent(
                new FluidLinkV2FrameEvent(
                    FluidLinkV2LifecycleAction.End,
                    42)));
        var sessionEnd = new FluidLinkV2Frame(
            FluidLinkV2FrameKind.Request,
            FluidLinkV2Opcode.RuntimeEvent,
            (byte)FluidLinkV2EventOpcode.Session,
            0,
            FluidLinkV2FrameFlags.HasSession,
            10,
            messageId,
            sessionId,
            FluidLinkV2PayloadCodec.EncodeSessionEvent(
                new FluidLinkV2SessionEvent(
                    FluidLinkV2LifecycleAction.End,
                    "")));
        var goodbye = new FluidLinkV2Frame(
            FluidLinkV2FrameKind.Request,
            FluidLinkV2Opcode.Goodbye,
            0,
            0,
            FluidLinkV2FrameFlags.HasSession,
            11,
            messageId,
            sessionId,
            FluidLinkV2PayloadCodec.EncodeGoodbye());
        var frames = new Dictionary<string, FluidLinkV2Frame>
        {
            ["hello_request"] = hello,
            ["welcome_response"] = new(
                FluidLinkV2FrameKind.Response,
                FluidLinkV2Opcode.Welcome,
                0,
                0,
                FluidLinkV2FrameFlags.Ok | FluidLinkV2FrameFlags.HasSession,
                1,
                messageId,
                sessionId,
                FluidLinkV2PayloadCodec.EncodeWelcome(
                    new FluidLinkV2WelcomePayload(
                        FluidLinkV2Protocol.ContractHash,
                        FluidLinkV2Protocol.AllCapabilities,
                        FluidLinkV2Protocol.AllCapabilities,
                        FluidLinkV2Protocol.MaxPayloadBytes,
                        "fluidgateway",
                        "0.64.0"))),
            ["session_begin_request"] = sessionBegin,
            ["frame_begin_request"] = frameBegin,
            ["resource_register_request"] = resourceRegister,
            ["operation_request"] = operation,
            ["operation_decision_response"] = new(
                FluidLinkV2FrameKind.Response,
                FluidLinkV2Opcode.RuntimeDecision,
                (byte)FluidLinkV2EventOpcode.Operation,
                (byte)FluidLinkV2DecisionOpcode.DeduplicateIdenticalTransfer,
                FluidLinkV2FrameFlags.Ok | FluidLinkV2FrameFlags.HasSession,
                5,
                messageId,
                sessionId,
                FluidLinkV2PayloadCodec.EncodeRuntimeDecision(
                    new FluidLinkV2RuntimeDecisionPayload(
                        FluidLinkV2DecisionStatus.Accepted |
                        FluidLinkV2DecisionStatus.HasExecutionState,
                        800,
                        64UL * 1024 * 1024))),
            ["state_request"] = state,
            ["state_decision_response"] = new(
                FluidLinkV2FrameKind.Response,
                FluidLinkV2Opcode.RuntimeDecision,
                (byte)FluidLinkV2EventOpcode.State,
                (byte)FluidLinkV2DecisionOpcode.Execute,
                FluidLinkV2FrameFlags.Ok | FluidLinkV2FrameFlags.HasSession,
                6,
                messageId,
                sessionId,
                FluidLinkV2PayloadCodec.EncodeRuntimeDecision(
                    new FluidLinkV2RuntimeDecisionPayload(
                        FluidLinkV2DecisionStatus.Accepted,
                        0,
                        0))),
            ["ping_request"] = ping,
            ["pong_response"] = new(
                FluidLinkV2FrameKind.Response,
                FluidLinkV2Opcode.Pong,
                0,
                0,
                FluidLinkV2FrameFlags.Ok | FluidLinkV2FrameFlags.HasSession,
                7,
                messageId,
                sessionId,
                FluidLinkV2PayloadCodec.EncodePingPong("nonce-v2")),
            ["resource_release_request"] = resourceRelease,
            ["frame_end_request"] = frameEnd,
            ["session_end_request"] = sessionEnd,
            ["invalid_payload_response"] = new(
                FluidLinkV2FrameKind.Response,
                FluidLinkV2Opcode.Error,
                (byte)FluidLinkV2EventOpcode.State,
                (byte)FluidLinkV2DecisionOpcode.Unknown,
                FluidLinkV2FrameFlags.HasSession,
                6,
                messageId,
                sessionId,
                FluidLinkV2PayloadCodec.EncodeError(
                    new FluidLinkV2ErrorPayload(
                        FluidLinkV2ErrorCode.InvalidPayload,
                        "state payload malformed"))),
            ["goodbye_request"] = goodbye,
            ["goodbye_response"] = new(
                FluidLinkV2FrameKind.Response,
                FluidLinkV2Opcode.Goodbye,
                0,
                0,
                FluidLinkV2FrameFlags.Ok | FluidLinkV2FrameFlags.HasSession,
                11,
                messageId,
                sessionId,
                FluidLinkV2PayloadCodec.EncodeGoodbye())
        };

        var vectors = root.GetProperty("vectors").EnumerateArray().ToArray();
        Assert.Equal(17, vectors.Length);
        Assert.Equal(frames.Count, vectors.Length);
        foreach (var vector in vectors)
        {
            var name = vector.GetProperty("name").GetString()!;
            var wire = FluidLinkV2FrameCodec.Encode(frames[name]);
            Assert.Equal(vector.GetProperty("wire_bytes").GetInt32(), wire.Length);
            Assert.Equal(
                vector.GetProperty("wire_hex").GetString(),
                Convert.ToHexString(wire).ToLowerInvariant());
        }
    }

    [Fact]
    public void Lifecycle_end_events_reject_optional_presence_fields()
    {
        var sessionEnd = FluidLinkV2PayloadCodec.EncodeSessionEvent(
            new FluidLinkV2SessionEvent(FluidLinkV2LifecycleAction.End, ""));
        Assert.Equal(Convert.FromHexString("02000000"), sessionEnd);
        Assert.Equal(
            "",
            FluidLinkV2PayloadCodec.DecodeSessionEvent(sessionEnd).SessionId);
        Assert.Throws<FluidLinkV2ProtocolException>(
            () => FluidLinkV2PayloadCodec.EncodeSessionEvent(
                new FluidLinkV2SessionEvent(
                    FluidLinkV2LifecycleAction.End,
                    "",
                    FrameBudgetMicroseconds: 16_667)));
        Assert.Throws<FluidLinkV2ProtocolException>(
            () => FluidLinkV2PayloadCodec.DecodeSessionEvent(
                Convert.FromHexString("020100001b410000")));
        Assert.Throws<FluidLinkV2ProtocolException>(
            () => FluidLinkV2PayloadCodec.EncodeSessionEvent(
                new FluidLinkV2SessionEvent(FluidLinkV2LifecycleAction.Begin, "")));

        var frameEnd = FluidLinkV2PayloadCodec.EncodeFrameEvent(
            new FluidLinkV2FrameEvent(FluidLinkV2LifecycleAction.End, 42));
        Assert.Equal(Convert.FromHexString("02002a00000000000000"), frameEnd);
        Assert.Throws<FluidLinkV2ProtocolException>(
            () => FluidLinkV2PayloadCodec.EncodeFrameEvent(
                new FluidLinkV2FrameEvent(
                    FluidLinkV2LifecycleAction.End,
                    42,
                    16_667)));
        Assert.Throws<FluidLinkV2ProtocolException>(
            () => FluidLinkV2PayloadCodec.DecodeFrameEvent(
                Convert.FromHexString("02012a000000000000001b410000")));
    }

    [Fact]
    public void Typed_payloads_match_the_gateway_python_golden_vectors()
    {
        var session = new FluidLinkV2SessionEvent(
            FluidLinkV2LifecycleAction.Begin,
            "game-1",
            FrameBudgetMicroseconds: 8_333,
            RamBudgetBytes: 4UL * 1024 * 1024 * 1024,
            VramBudgetBytes: 8UL * 1024 * 1024 * 1024,
            SharedBudgetBytes: 2UL * 1024 * 1024 * 1024,
            StagingBudgetBytes: 128UL * 1024 * 1024,
            SwapchainBudgetBytes: 64UL * 1024 * 1024);
        Assert.Equal(
            Convert.FromHexString(
                "013f060067616d652d318d200000000000" +
                "00010000000000000002000000000000" +
                "80000000000000000800000000000000" +
                "0400000000"),
            FluidLinkV2PayloadCodec.EncodeSessionEvent(session));

        Assert.Equal(
            Convert.FromHexString("01012a000000000000001b410000"),
            FluidLinkV2PayloadCodec.EncodeFrameEvent(
                new FluidLinkV2FrameEvent(
                    FluidLinkV2LifecycleAction.Begin,
                    42,
                    16_667)));

        Assert.Equal(
            Convert.FromHexString(
                "010900746578747572652d310202010000" +
                "0001000000000204006865726f070064" +
                "696666757365"),
            FluidLinkV2PayloadCodec.EncodeResourceEvent(
                FluidLinkV2ResourceEvent.Register(
                    "texture-1",
                    FluidLinkV2ResourceKind.Texture,
                    FluidLinkV2MemoryLayer.Vram,
                    FluidLinkV2Lifetime.Asset,
                    16UL * 1024 * 1024,
                    ["hero", "diffuse"])));

        Assert.Equal(
            Convert.FromHexString(
                "01010f01006f0100730100740100720403" +
                "0201080706050403020118171615141312" +
                "1101010064"),
            FluidLinkV2PayloadCodec.EncodeOperationEvent(
                new FluidLinkV2OperationEvent(
                    FluidLinkV2OperationType.Copy,
                    FluidLinkV2Queue.Cpu,
                    "o",
                    0x01020304,
                    0x0102030405060708,
                    Source: "s",
                    Target: "t",
                    Reason: "r",
                    Frame: 0x1112131415161718,
                    Dependencies: ["d"])));

        Assert.Equal(
            Convert.FromHexString("01"),
            FluidLinkV2PayloadCodec.EncodeStateEvent(new()));
        Assert.Equal(
            Convert.FromHexString("03fa000000000000000010000000000000"),
            FluidLinkV2PayloadCodec.EncodeRuntimeDecision(
                new FluidLinkV2RuntimeDecisionPayload(
                    FluidLinkV2DecisionStatus.Accepted |
                    FluidLinkV2DecisionStatus.HasExecutionState,
                    250,
                    4_096)));
        Assert.Equal(
            Convert.FromHexString("086e6f6e63652d7632"),
            FluidLinkV2PayloadCodec.EncodePingPong("nonce-v2"));
    }

    [Fact]
    public void Typed_event_payloads_round_trip_every_contract_schema()
    {
        var session = new FluidLinkV2SessionEvent(
            FluidLinkV2LifecycleAction.Begin,
            "game-1",
            FrameBudgetMicroseconds: 8_333,
            RamBudgetBytes: 4UL * 1024 * 1024 * 1024,
            VramBudgetBytes: 8UL * 1024 * 1024 * 1024,
            SharedBudgetBytes: 2UL * 1024 * 1024 * 1024,
            StagingBudgetBytes: 128UL * 1024 * 1024,
            SwapchainBudgetBytes: 64UL * 1024 * 1024);
        var decodedSession = Assert.IsType<FluidLinkV2SessionEvent>(
            FluidLinkV2PayloadCodec.DecodeRuntimeEvent(
                session.EventOpcode,
                FluidLinkV2PayloadCodec.EncodeRuntimeEvent(session)));
        Assert.Equal(session.SessionId, decodedSession.SessionId);
        Assert.Equal(session.FrameBudgetMicroseconds, decodedSession.FrameBudgetMicroseconds);
        Assert.Equal(session.RamBudgetBytes, decodedSession.RamBudgetBytes);
        Assert.Equal(session.VramBudgetBytes, decodedSession.VramBudgetBytes);
        Assert.Equal(session.SharedBudgetBytes, decodedSession.SharedBudgetBytes);
        Assert.Equal(session.StagingBudgetBytes, decodedSession.StagingBudgetBytes);
        Assert.Equal(session.SwapchainBudgetBytes, decodedSession.SwapchainBudgetBytes);

        var frame = new FluidLinkV2FrameEvent(
            FluidLinkV2LifecycleAction.Begin,
            42,
            16_667);
        var decodedFrame = Assert.IsType<FluidLinkV2FrameEvent>(
            FluidLinkV2PayloadCodec.DecodeRuntimeEvent(
                frame.EventOpcode,
                FluidLinkV2PayloadCodec.EncodeRuntimeEvent(frame)));
        Assert.Equal(frame, decodedFrame);

        var registration = FluidLinkV2ResourceEvent.Register(
            "texture-1",
            FluidLinkV2ResourceKind.Texture,
            FluidLinkV2MemoryLayer.Vram,
            FluidLinkV2Lifetime.Asset,
            16UL * 1024 * 1024,
            ["hero", "diffuse"]);
        var decodedRegistration = Assert.IsType<FluidLinkV2ResourceEvent>(
            FluidLinkV2PayloadCodec.DecodeRuntimeEvent(
                registration.EventOpcode,
                FluidLinkV2PayloadCodec.EncodeRuntimeEvent(registration)));
        Assert.Equal(FluidLinkV2ResourceAction.Register, decodedRegistration.Action);
        Assert.Equal("texture-1", decodedRegistration.ResourceId);
        Assert.Equal(FluidLinkV2MemoryLayer.Vram, decodedRegistration.Memory);
        Assert.Equal(["hero", "diffuse"], decodedRegistration.Aliases);

        var release = FluidLinkV2ResourceEvent.Release("texture-1");
        var decodedRelease = Assert.IsType<FluidLinkV2ResourceEvent>(
            FluidLinkV2PayloadCodec.DecodeRuntimeEvent(
                release.EventOpcode,
                FluidLinkV2PayloadCodec.EncodeRuntimeEvent(release)));
        Assert.Equal(FluidLinkV2ResourceAction.Release, decodedRelease.Action);
        Assert.Equal("texture-1", decodedRelease.ResourceId);

        var state = new FluidLinkV2StateEvent();
        var decodedState = Assert.IsType<FluidLinkV2StateEvent>(
            FluidLinkV2PayloadCodec.DecodeRuntimeEvent(
                state.EventOpcode,
                FluidLinkV2PayloadCodec.EncodeRuntimeEvent(state)));
        Assert.Equal(FluidLinkV2StateAction.Snapshot, decodedState.Action);
    }

    [Fact]
    public void Decision_ping_goodbye_and_error_payloads_round_trip()
    {
        var decision = new FluidLinkV2RuntimeDecisionPayload(
            FluidLinkV2DecisionStatus.Accepted |
            FluidLinkV2DecisionStatus.HasExecutionState,
            250,
            4_096);
        var decodedDecision = FluidLinkV2PayloadCodec.DecodeRuntimeDecision(
            FluidLinkV2PayloadCodec.EncodeRuntimeDecision(decision));
        Assert.True(decodedDecision.Accepted);
        Assert.False(decodedDecision.Executed);
        Assert.Equal(250UL, decodedDecision.SavedMicroseconds);
        Assert.Equal(4_096UL, decodedDecision.SavedBytes);

        const string nonce = "pulso-01";
        Assert.Equal(
            nonce,
            FluidLinkV2PayloadCodec.DecodePingPong(
                FluidLinkV2PayloadCodec.EncodePingPong(nonce)));
        FluidLinkV2PayloadCodec.DecodeGoodbye(
            FluidLinkV2PayloadCodec.EncodeGoodbye());

        var error = new FluidLinkV2ErrorPayload(
            FluidLinkV2ErrorCode.ContractMismatch,
            "contract rejected");
        Assert.Equal(
            error,
            FluidLinkV2PayloadCodec.DecodeError(
                FluidLinkV2PayloadCodec.EncodeError(error)));
    }

    [Fact]
    public void Payload_codec_rejects_invalid_utf8_limits_masks_and_trailing_bytes()
    {
        var invalidUtf8 = Assert.Throws<FluidLinkV2ProtocolException>(
            () => FluidLinkV2PayloadCodec.DecodePingPong([2, 0xC3, 0x28]));
        Assert.Equal("invalid_payload", invalidUtf8.Code);

        var longIdentifier = new string('a', 257);
        Assert.Throws<FluidLinkV2ProtocolException>(
            () => FluidLinkV2PayloadCodec.EncodeResourceEvent(
                FluidLinkV2ResourceEvent.Release(longIdentifier)));

        var aliases = Enumerable.Range(0, 33)
            .Select(index => $"alias-{index}")
            .ToArray();
        Assert.Throws<FluidLinkV2ProtocolException>(
            () => FluidLinkV2PayloadCodec.EncodeResourceEvent(
                FluidLinkV2ResourceEvent.Register(
                    "buffer",
                    FluidLinkV2ResourceKind.Buffer,
                    FluidLinkV2MemoryLayer.Ram,
                    FluidLinkV2Lifetime.Frame,
                    64,
                    aliases)));

        byte[] unknownFrameMask =
        [
            (byte)FluidLinkV2LifecycleAction.Begin,
            0x80,
            0, 0, 0, 0, 0, 0, 0, 0
        ];
        Assert.Throws<FluidLinkV2ProtocolException>(
            () => FluidLinkV2PayloadCodec.DecodeFrameEvent(unknownFrameMask));
        Assert.Throws<FluidLinkV2ProtocolException>(
            () => FluidLinkV2PayloadCodec.DecodeStateEvent([1, 0]));
        Assert.Throws<FluidLinkV2ProtocolException>(
            () => FluidLinkV2PayloadCodec.EncodePingPong(string.Empty));
        Assert.Throws<FluidLinkV2ProtocolException>(
            () => FluidLinkV2PayloadCodec.DecodeError(
                [0xFF, 0xFF, 0x01, 0x00, 0x78]));
        Assert.Throws<FluidLinkV2ProtocolException>(
            () => FluidLinkV2PayloadCodec.DecodeRuntimeDecision(
                [
                    (byte)FluidLinkV2DecisionStatus.Executed,
                    0, 0, 0, 0, 0, 0, 0, 0,
                    0, 0, 0, 0, 0, 0, 0, 0
                ]));
    }

    [Fact]
    public async Task Frame_codec_preserves_the_56_byte_header_and_fragmented_reads()
    {
        var payload = FluidLinkV2PayloadCodec.EncodeStateEvent(new());
        var frame = RequestFrame(
            FluidLinkV2Opcode.RuntimeEvent,
            payload,
            subjectOpcode: (byte)FluidLinkV2EventOpcode.State,
            sessionId: SessionId);
        var wire = FluidLinkV2FrameCodec.Encode(frame);

        Assert.Equal(FluidLinkV2Protocol.HeaderSize + payload.Length, wire.Length);
        Assert.Equal("FLNK", System.Text.Encoding.ASCII.GetString(wire, 0, 4));
        Assert.Equal(2, wire[4]);
        Assert.Equal(
            checked((uint)payload.Length),
            BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(52, 4)));
        await using var fragmented = new FragmentedReadStream(wire, chunkSize: 3);
        var decoded = await FluidLinkV2FrameCodec.ReadAsync(fragmented);
        Assert.Equal(frame.Kind, decoded.Kind);
        Assert.Equal(frame.Opcode, decoded.Opcode);
        Assert.Equal(frame.SubjectOpcode, decoded.SubjectOpcode);
        Assert.Equal(frame.Sequence, decoded.Sequence);
        Assert.Equal(frame.MessageId.ToArray(), decoded.MessageId.ToArray());
        Assert.Equal(frame.SessionId.ToArray(), decoded.SessionId.ToArray());
        Assert.Equal(payload, decoded.Payload.ToArray());
        Assert.Equal(wire.Length, decoded.WireSize);
    }

    [Fact]
    public void Frame_codec_fails_closed_on_v1_flags_reserved_bits_and_size_drift()
    {
        var valid = FluidLinkV2FrameCodec.Encode(RequestFrame(
            FluidLinkV2Opcode.Ping,
            FluidLinkV2PayloadCodec.EncodePingPong("n"),
            sessionId: SessionId));

        var v1 = valid.ToArray();
        v1[4] = 1;
        Assert.Equal(
            "unsupported_wire_version",
            Assert.Throws<FluidLinkV2ProtocolException>(
                () => FluidLinkV2FrameCodec.Decode(v1)).Code);

        var jsonFlag = valid.ToArray();
        jsonFlag[9] |= 4;
        Assert.Equal(
            "invalid_flags",
            Assert.Throws<FluidLinkV2ProtocolException>(
                () => FluidLinkV2FrameCodec.Decode(jsonFlag)).Code);

        var reserved = valid.ToArray();
        reserved[10] = 1;
        Assert.Equal(
            "invalid_reserved_bits",
            Assert.Throws<FluidLinkV2ProtocolException>(
                () => FluidLinkV2FrameCodec.Decode(reserved)).Code);

        var oversized = valid.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            oversized.AsSpan(52, 4),
            FluidLinkV2Protocol.MaxPayloadBytes + 1U);
        Assert.Equal(
            "payload_too_large",
            Assert.Throws<FluidLinkV2ProtocolException>(
                () => FluidLinkV2FrameCodec.Decode(oversized)).Code);

        Assert.Equal(
            "truncated_frame",
            Assert.Throws<FluidLinkV2ProtocolException>(
                () => FluidLinkV2FrameCodec.Decode(valid.AsSpan(0, 20))).Code);
    }

    [Fact]
    public async Task Frame_reader_rejects_a_peer_that_closes_mid_payload()
    {
        var wire = FluidLinkV2FrameCodec.Encode(RequestFrame(
            FluidLinkV2Opcode.Ping,
            FluidLinkV2PayloadCodec.EncodePingPong("nonce"),
            sessionId: SessionId));
        await using var truncated = new MemoryStream(
            wire.AsSpan(0, wire.Length - 2).ToArray(),
            writable: false);

        var exception = await Assert.ThrowsAsync<FluidLinkV2ProtocolException>(
            async () => await FluidLinkV2FrameCodec.ReadAsync(truncated));
        Assert.Equal("truncated_frame", exception.Code);
    }

    private static FluidLinkV2OperationBatchEvent BatchEvent(
        string batchId,
        int operationCount) =>
        new(
            batchId,
            operationCount,
            FluidLinkV2OperationType.Copy,
            FluidLinkV2Queue.Copy,
            10,
            64);

    private static FluidLinkV2Frame RequestFrame(
        FluidLinkV2Opcode opcode,
        byte[] payload,
        byte subjectOpcode = 0,
        byte[]? sessionId = null) =>
        new(
            FluidLinkV2FrameKind.Request,
            opcode,
            subjectOpcode,
            0,
            sessionId is null
                ? FluidLinkV2FrameFlags.None
                : FluidLinkV2FrameFlags.HasSession,
            1,
            MessageId,
            sessionId is null
                ? ReadOnlyMemory<byte>.Empty
                : sessionId,
            payload);

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
            inner.ReadAsync(
                buffer[..Math.Min(buffer.Length, chunkSize)],
                cancellationToken);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
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
