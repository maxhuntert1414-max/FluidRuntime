using System.Buffers.Binary;
using System.Text.Json;

namespace FluidLink;

public sealed record FluidLinkFrame(
    FluidLinkFrameKind Kind,
    FluidLinkOpcode Opcode,
    byte SubjectOpcode,
    byte DecisionOpcode,
    FluidLinkFrameFlags Flags,
    ulong Sequence,
    ReadOnlyMemory<byte> MessageId,
    ReadOnlyMemory<byte> SessionId,
    JsonElement Payload)
{
    public bool Ok => Flags.HasFlag(FluidLinkFrameFlags.Ok);

    public bool HasSession => Flags.HasFlag(FluidLinkFrameFlags.HasSession);

    public int WireSize { get; init; }
}

public static class FluidLinkFrameCodec
{
    private static ReadOnlySpan<byte> Magic => "FLNK"u8;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = FluidLinkProtocol.MaxJsonDepth
    };

    private const FluidLinkFrameFlags AllowedFlags =
        FluidLinkFrameFlags.Ok |
        FluidLinkFrameFlags.HasSession |
        FluidLinkFrameFlags.JsonPayload;

    public static byte[] Encode(FluidLinkFrame frame)
    {
        ValidateFrame(frame);
        byte[] payload;
        try
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(frame.Payload, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new FluidLinkProtocolException(
                "invalid_payload",
                $"FluidLink payload must contain bounded standard JSON: " +
                exception.Message);
        }
        if (payload.Length > FluidLinkProtocol.MaxPayloadBytes)
        {
            throw new FluidLinkProtocolException(
                "payload_too_large",
                "FluidLink payload exceeds the 1 MiB limit.");
        }

        var result = new byte[FluidLinkProtocol.HeaderSize + payload.Length];
        var header = result.AsSpan(0, FluidLinkProtocol.HeaderSize);
        Magic.CopyTo(header);
        header[4] = FluidLinkProtocol.WireVersion;
        header[5] = (byte)frame.Kind;
        header[6] = (byte)frame.Opcode;
        header[7] = frame.SubjectOpcode;
        header[8] = frame.DecisionOpcode;
        header[9] = (byte)frame.Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..12], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(header[12..20], frame.Sequence);
        frame.MessageId.Span.CopyTo(header[20..36]);
        if (frame.HasSession)
        {
            frame.SessionId.Span.CopyTo(header[36..52]);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[52..56],
            checked((uint)payload.Length));
        payload.CopyTo(result.AsSpan(FluidLinkProtocol.HeaderSize));
        return result;
    }

    public static int EstimateEquivalentJsonEnvelopeSize(FluidLinkFrame frame)
    {
        ValidateFrame(frame);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("protocol", FluidLinkProtocol.Version);
            writer.WriteString(
                "kind",
                frame.Kind == FluidLinkFrameKind.Request ? "request" : "response");
            writer.WriteString(
                "message_id",
                Convert.ToHexString(frame.MessageId.Span).ToLowerInvariant());
            writer.WriteNumber("sequence", frame.Sequence);
            writer.WriteNumber("op", (byte)frame.Opcode);
            writer.WriteNumber("subject_op", frame.SubjectOpcode);
            writer.WriteNumber("decision_op", frame.DecisionOpcode);
            if (frame.HasSession)
            {
                writer.WriteString(
                    "session_id",
                    Convert.ToHexString(frame.SessionId.Span).ToLowerInvariant());
            }
            else
            {
                writer.WriteNull("session_id");
            }
            if (frame.Kind == FluidLinkFrameKind.Response)
            {
                writer.WriteBoolean("ok", frame.Ok);
            }
            writer.WritePropertyName("payload");
            frame.Payload.WriteTo(writer);
            writer.WriteEndObject();
        }
        return checked((int)buffer.Length + 1);
    }

    public static FluidLinkFrame Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < FluidLinkProtocol.HeaderSize)
        {
            throw new FluidLinkProtocolException(
                "truncated_frame",
                "FluidLink frame is shorter than its 56-byte header.");
        }

        var header = data[..FluidLinkProtocol.HeaderSize];
        ValidateHeader(header);
        var payloadSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            header[52..56]));
        var expectedSize = FluidLinkProtocol.HeaderSize + payloadSize;
        if (data.Length != expectedSize)
        {
            throw new FluidLinkProtocolException(
                "frame_size_mismatch",
                $"FluidLink frame declares {expectedSize} bytes, " +
                $"received {data.Length}.");
        }

        JsonElement payload;
        try
        {
            using var document = JsonDocument.Parse(
                data[FluidLinkProtocol.HeaderSize..].ToArray(),
                new JsonDocumentOptions
                {
                    MaxDepth = FluidLinkProtocol.MaxJsonDepth
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new FluidLinkProtocolException(
                    "invalid_payload",
                    "FluidLink payload must be a JSON object.");
            }
            payload = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new FluidLinkProtocolException(
                "invalid_payload",
                $"FluidLink payload must be valid UTF-8 JSON: {exception.Message}");
        }

        var flags = (FluidLinkFrameFlags)header[9];
        var session = flags.HasFlag(FluidLinkFrameFlags.HasSession)
            ? data[36..52].ToArray()
            : ReadOnlyMemory<byte>.Empty;
        return new FluidLinkFrame(
            Kind: (FluidLinkFrameKind)header[5],
            Opcode: (FluidLinkOpcode)header[6],
            SubjectOpcode: header[7],
            DecisionOpcode: header[8],
            Flags: flags,
            Sequence: BinaryPrimitives.ReadUInt64LittleEndian(header[12..20]),
            MessageId: data[20..36].ToArray(),
            SessionId: session,
            Payload: payload)
        {
            WireSize = data.Length
        };
    }

    public static async ValueTask<FluidLinkFrame> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[FluidLinkProtocol.HeaderSize];
        await ReadExactlyAsync(stream, header, cancellationToken);
        ValidateHeader(header);
        var payloadSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            header.AsSpan(52, 4)));
        var frame = new byte[FluidLinkProtocol.HeaderSize + payloadSize];
        header.CopyTo(frame, 0);
        if (payloadSize > 0)
        {
            await ReadExactlyAsync(
                stream,
                frame.AsMemory(FluidLinkProtocol.HeaderSize, payloadSize),
                cancellationToken);
        }
        return Decode(frame);
    }

    public static async ValueTask<int> WriteAsync(
        Stream stream,
        FluidLinkFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var encoded = Encode(frame);
        await stream.WriteAsync(encoded, cancellationToken);
        return encoded.Length;
    }

    private static void ValidateHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length != FluidLinkProtocol.HeaderSize)
        {
            throw new FluidLinkProtocolException(
                "truncated_frame",
                "FluidLink header must contain exactly 56 bytes.");
        }
        if (!header[..4].SequenceEqual(Magic))
        {
            throw new FluidLinkProtocolException(
                "invalid_magic",
                "Invalid FluidLink magic.");
        }
        if (header[4] != FluidLinkProtocol.WireVersion)
        {
            throw new FluidLinkProtocolException(
                "unsupported_wire_version",
                $"Unsupported FluidLink wire version {header[4]}.");
        }
        if (!Enum.IsDefined((FluidLinkFrameKind)header[5]))
        {
            throw new FluidLinkProtocolException(
                "invalid_kind",
                $"Unsupported FluidLink frame kind {header[5]}.");
        }
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[10..12]) != 0)
        {
            throw new FluidLinkProtocolException(
                "invalid_reserved_bits",
                "FluidLink reserved header bits must be zero.");
        }
        var payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(header[52..56]);
        if (payloadSize > FluidLinkProtocol.MaxPayloadBytes)
        {
            throw new FluidLinkProtocolException(
                "payload_too_large",
                "FluidLink payload exceeds the 1 MiB limit.");
        }

        var flags = (FluidLinkFrameFlags)header[9];
        if ((flags & ~AllowedFlags) != 0)
        {
            throw new FluidLinkProtocolException(
                "invalid_flags",
                "FluidLink frame contains unknown flags.");
        }
        if (!flags.HasFlag(FluidLinkFrameFlags.JsonPayload))
        {
            throw new FluidLinkProtocolException(
                "unsupported_payload_encoding",
                "FluidLink v1 requires the JSON payload flag.");
        }
        if ((FluidLinkFrameKind)header[5] == FluidLinkFrameKind.Request &&
            flags.HasFlag(FluidLinkFrameFlags.Ok))
        {
            throw new FluidLinkProtocolException(
                "invalid_flags",
                "FluidLink requests cannot carry the OK flag.");
        }
        if (BinaryPrimitives.ReadUInt64LittleEndian(header[12..20]) == 0)
        {
            throw new FluidLinkProtocolException(
                "invalid_sequence",
                "FluidLink sequence must be nonzero.");
        }
        if (AllZero(header[20..36]))
        {
            throw new FluidLinkProtocolException(
                "invalid_message_id",
                "FluidLink message_id must contain 16 nonzero bytes.");
        }
        var hasSession = flags.HasFlag(FluidLinkFrameFlags.HasSession);
        var sessionIsZero = AllZero(header[36..52]);
        if (hasSession == sessionIsZero)
        {
            throw new FluidLinkProtocolException(
                "invalid_session_flag",
                "FluidLink session flag and session bytes disagree.");
        }
    }

    private static void ValidateFrame(FluidLinkFrame frame)
    {
        if (!Enum.IsDefined(frame.Kind))
        {
            throw new FluidLinkProtocolException(
                "invalid_kind",
                "FluidLink frame kind is invalid.");
        }
        if (frame.Sequence == 0)
        {
            throw new FluidLinkProtocolException(
                "invalid_sequence",
                "FluidLink sequence must be nonzero.");
        }
        if (frame.MessageId.Length != 16 || AllZero(frame.MessageId.Span))
        {
            throw new FluidLinkProtocolException(
                "invalid_message_id",
                "FluidLink message_id must contain 16 nonzero bytes.");
        }
        if ((frame.Flags & ~AllowedFlags) != 0 ||
            !frame.Flags.HasFlag(FluidLinkFrameFlags.JsonPayload))
        {
            throw new FluidLinkProtocolException(
                "invalid_flags",
                "FluidLink frame flags are invalid.");
        }
        if (frame.Kind == FluidLinkFrameKind.Request && frame.Ok)
        {
            throw new FluidLinkProtocolException(
                "invalid_flags",
                "FluidLink requests cannot carry the OK flag.");
        }
        if (frame.HasSession != !frame.SessionId.IsEmpty)
        {
            throw new FluidLinkProtocolException(
                "invalid_session_flag",
                "FluidLink session flag and session_id disagree.");
        }
        if (frame.HasSession &&
            (frame.SessionId.Length != 16 || AllZero(frame.SessionId.Span)))
        {
            throw new FluidLinkProtocolException(
                "invalid_session_id",
                "FluidLink session_id must contain 16 nonzero bytes.");
        }
        if (frame.Payload.ValueKind != JsonValueKind.Object)
        {
            throw new FluidLinkProtocolException(
                "invalid_payload",
                "FluidLink payload must be a JSON object.");
        }
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var readTotal = 0;
        while (readTotal < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[readTotal..], cancellationToken);
            if (read == 0)
            {
                throw new FluidLinkProtocolException(
                    "truncated_frame",
                    "FluidLink peer closed before the complete frame arrived.");
            }
            readTotal += read;
        }
    }

    private static bool AllZero(ReadOnlySpan<byte> value)
    {
        foreach (var item in value)
        {
            if (item != 0)
            {
                return false;
            }
        }
        return true;
    }
}
