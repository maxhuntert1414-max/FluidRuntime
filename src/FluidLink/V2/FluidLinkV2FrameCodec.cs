using System.Buffers.Binary;

namespace FluidLink;

public sealed record FluidLinkV2Frame(
    FluidLinkV2FrameKind Kind,
    FluidLinkV2Opcode Opcode,
    byte SubjectOpcode,
    byte DecisionOpcode,
    FluidLinkV2FrameFlags Flags,
    ulong Sequence,
    ReadOnlyMemory<byte> MessageId,
    ReadOnlyMemory<byte> SessionId,
    ReadOnlyMemory<byte> Payload)
{
    public bool Ok => Flags.HasFlag(FluidLinkV2FrameFlags.Ok);

    public bool HasSession => Flags.HasFlag(FluidLinkV2FrameFlags.HasSession);

    public int WireSize { get; init; }
}

public static class FluidLinkV2FrameCodec
{
    private static ReadOnlySpan<byte> Magic => "FLNK"u8;

    private const FluidLinkV2FrameFlags AllowedFlags =
        FluidLinkV2FrameFlags.Ok |
        FluidLinkV2FrameFlags.HasSession;

    public static byte[] Encode(FluidLinkV2Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateFrame(frame);
        var result = new byte[
            FluidLinkV2Protocol.HeaderSize + frame.Payload.Length];
        var header = result.AsSpan(0, FluidLinkV2Protocol.HeaderSize);
        Magic.CopyTo(header);
        header[4] = FluidLinkV2Protocol.WireVersion;
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
            checked((uint)frame.Payload.Length));
        frame.Payload.Span.CopyTo(result.AsSpan(FluidLinkV2Protocol.HeaderSize));
        return result;
    }

    public static FluidLinkV2Frame Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < FluidLinkV2Protocol.HeaderSize)
        {
            throw new FluidLinkV2ProtocolException(
                "truncated_frame",
                "FluidLink v2 frame is shorter than its 56-byte header.");
        }

        var header = data[..FluidLinkV2Protocol.HeaderSize];
        ValidateHeader(header);
        var payloadSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            header[52..56]));
        var expectedSize = FluidLinkV2Protocol.HeaderSize + payloadSize;
        if (data.Length != expectedSize)
        {
            throw new FluidLinkV2ProtocolException(
                "frame_size_mismatch",
                $"FluidLink v2 frame declares {expectedSize} bytes, " +
                $"received {data.Length}.");
        }

        var flags = (FluidLinkV2FrameFlags)header[9];
        var sessionId = flags.HasFlag(FluidLinkV2FrameFlags.HasSession)
            ? header[36..52].ToArray()
            : ReadOnlyMemory<byte>.Empty;
        return new FluidLinkV2Frame(
            Kind: (FluidLinkV2FrameKind)header[5],
            Opcode: (FluidLinkV2Opcode)header[6],
            SubjectOpcode: header[7],
            DecisionOpcode: header[8],
            Flags: flags,
            Sequence: BinaryPrimitives.ReadUInt64LittleEndian(header[12..20]),
            MessageId: header[20..36].ToArray(),
            SessionId: sessionId,
            Payload: data[FluidLinkV2Protocol.HeaderSize..].ToArray())
        {
            WireSize = data.Length
        };
    }

    public static async ValueTask<FluidLinkV2Frame> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[FluidLinkV2Protocol.HeaderSize];
        await ReadExactlyAsync(stream, header, cancellationToken);
        ValidateHeader(header);
        var payloadSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            header.AsSpan(52, 4)));
        var frame = new byte[FluidLinkV2Protocol.HeaderSize + payloadSize];
        header.CopyTo(frame, 0);
        if (payloadSize > 0)
        {
            await ReadExactlyAsync(
                stream,
                frame.AsMemory(FluidLinkV2Protocol.HeaderSize, payloadSize),
                cancellationToken);
        }
        return Decode(frame);
    }

    public static async ValueTask<int> WriteAsync(
        Stream stream,
        FluidLinkV2Frame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var encoded = Encode(frame);
        await stream.WriteAsync(encoded, cancellationToken);
        return encoded.Length;
    }

    private static void ValidateHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length != FluidLinkV2Protocol.HeaderSize)
        {
            throw new FluidLinkV2ProtocolException(
                "truncated_frame",
                "FluidLink v2 header must contain exactly 56 bytes.");
        }
        if (!header[..4].SequenceEqual(Magic))
        {
            throw new FluidLinkV2ProtocolException(
                "invalid_magic",
                "Invalid FluidLink v2 magic.");
        }
        if (header[4] != FluidLinkV2Protocol.WireVersion)
        {
            throw new FluidLinkV2ProtocolException(
                "unsupported_wire_version",
                $"Unsupported FluidLink wire version {header[4]}.");
        }
        if (!Enum.IsDefined((FluidLinkV2FrameKind)header[5]))
        {
            throw new FluidLinkV2ProtocolException(
                "invalid_kind",
                $"Unsupported FluidLink v2 frame kind {header[5]}.");
        }
        if (!Enum.IsDefined((FluidLinkV2Opcode)header[6]))
        {
            throw new FluidLinkV2ProtocolException(
                "invalid_opcode",
                $"Unsupported FluidLink v2 opcode {header[6]}.");
        }
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[10..12]) != 0)
        {
            throw new FluidLinkV2ProtocolException(
                "invalid_reserved_bits",
                "FluidLink v2 reserved header bits must be zero.");
        }
        var payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(header[52..56]);
        if (payloadSize > FluidLinkV2Protocol.MaxPayloadBytes)
        {
            throw new FluidLinkV2ProtocolException(
                "payload_too_large",
                $"FluidLink v2 payload exceeds " +
                $"{FluidLinkV2Protocol.MaxPayloadBytes} bytes.");
        }

        var flags = (FluidLinkV2FrameFlags)header[9];
        if ((flags & ~AllowedFlags) != 0)
        {
            throw new FluidLinkV2ProtocolException(
                "invalid_flags",
                "FluidLink v2 frame contains unknown flags.");
        }
        if ((FluidLinkV2FrameKind)header[5] == FluidLinkV2FrameKind.Request &&
            flags.HasFlag(FluidLinkV2FrameFlags.Ok))
        {
            throw new FluidLinkV2ProtocolException(
                "invalid_flags",
                "FluidLink v2 requests cannot carry the OK flag.");
        }
        if (BinaryPrimitives.ReadUInt64LittleEndian(header[12..20]) == 0)
        {
            throw new FluidLinkV2ProtocolException(
                "invalid_sequence",
                "FluidLink v2 sequence must be nonzero.");
        }
        if (AllZero(header[20..36]))
        {
            throw new FluidLinkV2ProtocolException(
                "invalid_message_id",
                "FluidLink v2 message_id must contain 16 nonzero bytes.");
        }
        var hasSession = flags.HasFlag(FluidLinkV2FrameFlags.HasSession);
        var sessionIsZero = AllZero(header[36..52]);
        if (hasSession == sessionIsZero)
        {
            throw new FluidLinkV2ProtocolException(
                "invalid_session_flag",
                "FluidLink v2 session flag and session bytes disagree.");
        }
        ValidateSubOpcodes(
            (FluidLinkV2Opcode)header[6],
            header[7],
            header[8]);
    }

    private static void ValidateFrame(FluidLinkV2Frame frame)
    {
        if (!Enum.IsDefined(frame.Kind))
        {
            throw new FluidLinkV2ProtocolException(
                "invalid_kind",
                "FluidLink v2 frame kind is invalid.");
        }
        if (!Enum.IsDefined(frame.Opcode))
        {
            throw new FluidLinkV2ProtocolException(
                "invalid_opcode",
                "FluidLink v2 frame opcode is invalid.");
        }
        if (frame.Sequence == 0)
        {
            throw new FluidLinkV2ProtocolException(
                "invalid_sequence",
                "FluidLink v2 sequence must be nonzero.");
        }
        if (frame.MessageId.Length != 16 || AllZero(frame.MessageId.Span))
        {
            throw new FluidLinkV2ProtocolException(
                "invalid_message_id",
                "FluidLink v2 message_id must contain 16 nonzero bytes.");
        }
        if ((frame.Flags & ~AllowedFlags) != 0 ||
            (frame.Kind == FluidLinkV2FrameKind.Request && frame.Ok))
        {
            throw new FluidLinkV2ProtocolException(
                "invalid_flags",
                "FluidLink v2 frame flags are invalid.");
        }
        if (frame.HasSession != !frame.SessionId.IsEmpty)
        {
            throw new FluidLinkV2ProtocolException(
                "invalid_session_flag",
                "FluidLink v2 session flag and session_id disagree.");
        }
        if (frame.HasSession &&
            (frame.SessionId.Length != 16 || AllZero(frame.SessionId.Span)))
        {
            throw new FluidLinkV2ProtocolException(
                "invalid_session_id",
                "FluidLink v2 session_id must contain 16 nonzero bytes.");
        }
        if (frame.Payload.Length > FluidLinkV2Protocol.MaxPayloadBytes)
        {
            throw new FluidLinkV2ProtocolException(
                "payload_too_large",
                $"FluidLink v2 payload exceeds " +
                $"{FluidLinkV2Protocol.MaxPayloadBytes} bytes.");
        }
        ValidateSubOpcodes(
            frame.Opcode,
            frame.SubjectOpcode,
            frame.DecisionOpcode);
    }

    private static void ValidateSubOpcodes(
        FluidLinkV2Opcode opcode,
        byte subjectOpcode,
        byte decisionOpcode)
    {
        if (opcode is FluidLinkV2Opcode.RuntimeEvent or
            FluidLinkV2Opcode.RuntimeDecision)
        {
            if (!Enum.IsDefined((FluidLinkV2EventOpcode)subjectOpcode))
            {
                throw new FluidLinkV2ProtocolException(
                    "invalid_subject_opcode",
                    $"Unsupported FluidLink v2 subject opcode {subjectOpcode}.");
            }
        }
        else if (opcode == FluidLinkV2Opcode.Error)
        {
            if (subjectOpcode != 0 &&
                !Enum.IsDefined((FluidLinkV2EventOpcode)subjectOpcode))
            {
                throw new FluidLinkV2ProtocolException(
                    "invalid_subject_opcode",
                    $"Unsupported FluidLink v2 error subject opcode " +
                    $"{subjectOpcode}.");
            }
        }
        else if (subjectOpcode != 0)
        {
            throw new FluidLinkV2ProtocolException(
                "invalid_subject_opcode",
                "FluidLink v2 control frames cannot carry a subject opcode.");
        }

        if (opcode == FluidLinkV2Opcode.RuntimeDecision)
        {
            if (!Enum.IsDefined((FluidLinkV2DecisionOpcode)decisionOpcode))
            {
                throw new FluidLinkV2ProtocolException(
                    "invalid_decision_opcode",
                    $"Unsupported FluidLink v2 decision opcode {decisionOpcode}.");
            }
        }
        else if (opcode == FluidLinkV2Opcode.Error)
        {
            if (decisionOpcode is not (0 or
                (byte)FluidLinkV2DecisionOpcode.Unknown))
            {
                throw new FluidLinkV2ProtocolException(
                    "invalid_decision_opcode",
                    "FluidLink v2 error frame has an invalid decision opcode.");
            }
        }
        else if (decisionOpcode != 0)
        {
            throw new FluidLinkV2ProtocolException(
                "invalid_decision_opcode",
                "FluidLink v2 control frame cannot carry a decision opcode.");
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
                throw new FluidLinkV2ProtocolException(
                    "truncated_frame",
                    "FluidLink v2 peer closed before the complete frame arrived.");
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
