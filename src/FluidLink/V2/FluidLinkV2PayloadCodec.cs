using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace FluidLink;

public static class FluidLinkV2PayloadCodec
{
    private const byte SessionFrameBudget = 1 << 0;
    private const byte SessionRamBudget = 1 << 1;
    private const byte SessionVramBudget = 1 << 2;
    private const byte SessionSharedBudget = 1 << 3;
    private const byte SessionStagingBudget = 1 << 4;
    private const byte SessionSwapchainBudget = 1 << 5;
    private const byte SessionAllowedFields =
        SessionFrameBudget |
        SessionRamBudget |
        SessionVramBudget |
        SessionSharedBudget |
        SessionStagingBudget |
        SessionSwapchainBudget;

    private const byte FrameTarget = 1 << 0;

    private const byte OperationSource = 1 << 0;
    private const byte OperationTarget = 1 << 1;
    private const byte OperationReason = 1 << 2;
    private const byte OperationFrame = 1 << 3;
    private const byte OperationAllowedFields =
        OperationSource |
        OperationTarget |
        OperationReason |
        OperationFrame;

    private const FluidLinkV2DecisionStatus AllowedDecisionStatus =
        FluidLinkV2DecisionStatus.Accepted |
        FluidLinkV2DecisionStatus.HasExecutionState |
        FluidLinkV2DecisionStatus.Executed;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static byte[] EncodeHello(FluidLinkV2HelloPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidateHash(payload.ContractHash.Span);
        ValidateCapabilities(payload.RequestedCapabilities, "requested_capabilities");
        ValidateCapabilities(payload.RequiredCapabilities, "required_capabilities");
        var writer = new PayloadWriter();
        writer.WriteBytes(payload.ContractHash.Span);
        writer.WriteUInt64((ulong)payload.RequestedCapabilities);
        writer.WriteUInt64((ulong)payload.RequiredCapabilities);
        writer.WriteText8(
            payload.ClientName,
            FluidLinkV2Protocol.MaxPeerNameUtf8Bytes,
            "client_name",
            requireNonEmpty: true);
        writer.WriteText8(
            payload.ClientVersion,
            FluidLinkV2Protocol.MaxPeerVersionUtf8Bytes,
            "client_version",
            requireNonEmpty: true);
        return writer.ToArray();
    }

    public static FluidLinkV2HelloPayload DecodeHello(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var contractHash = reader.ReadBytes(32, "contract_sha256").ToArray();
        var requested = (FluidLinkV2Capability)reader.ReadUInt64(
            "requested_capabilities");
        var required = (FluidLinkV2Capability)reader.ReadUInt64(
            "required_capabilities");
        ValidateCapabilities(requested, "requested_capabilities");
        ValidateCapabilities(required, "required_capabilities");
        var clientName = reader.ReadText8(
            FluidLinkV2Protocol.MaxPeerNameUtf8Bytes,
            "client_name",
            requireNonEmpty: true);
        var clientVersion = reader.ReadText8(
            FluidLinkV2Protocol.MaxPeerVersionUtf8Bytes,
            "client_version",
            requireNonEmpty: true);
        reader.EnsureComplete();
        return new FluidLinkV2HelloPayload(
            contractHash,
            requested,
            required,
            clientName,
            clientVersion);
    }

    public static byte[] EncodeWelcome(FluidLinkV2WelcomePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidateHash(payload.ContractHash.Span);
        ValidateCapabilities(payload.AvailableCapabilities, "available_capabilities");
        ValidateCapabilities(payload.AcceptedCapabilities, "accepted_capabilities");
        if ((payload.AcceptedCapabilities & ~payload.AvailableCapabilities) != 0)
        {
            throw InvalidPayload(
                "accepted_capabilities must be a subset of available_capabilities.");
        }
        ValidateMaximumPayload(payload.MaxPayloadBytes);

        var writer = new PayloadWriter();
        writer.WriteBytes(payload.ContractHash.Span);
        writer.WriteUInt64((ulong)payload.AvailableCapabilities);
        writer.WriteUInt64((ulong)payload.AcceptedCapabilities);
        writer.WriteUInt32(payload.MaxPayloadBytes);
        writer.WriteText8(
            payload.ServerName,
            FluidLinkV2Protocol.MaxPeerNameUtf8Bytes,
            "server_name",
            requireNonEmpty: true);
        writer.WriteText8(
            payload.ServerVersion,
            FluidLinkV2Protocol.MaxPeerVersionUtf8Bytes,
            "server_version",
            requireNonEmpty: true);
        return writer.ToArray();
    }

    public static FluidLinkV2WelcomePayload DecodeWelcome(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var contractHash = reader.ReadBytes(32, "contract_sha256").ToArray();
        var available = (FluidLinkV2Capability)reader.ReadUInt64(
            "available_capabilities");
        var accepted = (FluidLinkV2Capability)reader.ReadUInt64(
            "accepted_capabilities");
        ValidateCapabilities(available, "available_capabilities");
        ValidateCapabilities(accepted, "accepted_capabilities");
        if ((accepted & ~available) != 0)
        {
            throw InvalidPayload(
                "accepted_capabilities must be a subset of available_capabilities.");
        }
        var maxPayloadBytes = reader.ReadUInt32("max_payload_bytes");
        ValidateMaximumPayload(maxPayloadBytes);
        var serverName = reader.ReadText8(
            FluidLinkV2Protocol.MaxPeerNameUtf8Bytes,
            "server_name",
            requireNonEmpty: true);
        var serverVersion = reader.ReadText8(
            FluidLinkV2Protocol.MaxPeerVersionUtf8Bytes,
            "server_version",
            requireNonEmpty: true);
        reader.EnsureComplete();
        return new FluidLinkV2WelcomePayload(
            contractHash,
            available,
            accepted,
            maxPayloadBytes,
            serverName,
            serverVersion);
    }

    public static byte[] EncodePingPong(string nonce)
    {
        var writer = new PayloadWriter();
        writer.WriteText8(
            nonce,
            FluidLinkV2Protocol.MaxNonceUtf8Bytes,
            "nonce",
            requireNonEmpty: true);
        return writer.ToArray();
    }

    public static string DecodePingPong(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var nonce = reader.ReadText8(
            FluidLinkV2Protocol.MaxNonceUtf8Bytes,
            "nonce",
            requireNonEmpty: true);
        reader.EnsureComplete();
        return nonce;
    }

    public static byte[] EncodeGoodbye() => [];

    public static void DecodeGoodbye(ReadOnlySpan<byte> payload)
    {
        if (!payload.IsEmpty)
        {
            throw InvalidPayload("goodbye payload must be empty.");
        }
    }

    public static byte[] EncodeRuntimeDecision(
        FluidLinkV2RuntimeDecisionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidateDecisionStatus(payload.Status);
        var writer = new PayloadWriter();
        writer.WriteByte((byte)payload.Status);
        writer.WriteUInt64(payload.SavedMicroseconds);
        writer.WriteUInt64(payload.SavedBytes);
        return writer.ToArray();
    }

    public static FluidLinkV2RuntimeDecisionPayload DecodeRuntimeDecision(
        ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var status = (FluidLinkV2DecisionStatus)reader.ReadByte("status_flags");
        ValidateDecisionStatus(status);
        var savedMicroseconds = reader.ReadUInt64("saved_microseconds");
        var savedBytes = reader.ReadUInt64("saved_bytes");
        reader.EnsureComplete();
        return new FluidLinkV2RuntimeDecisionPayload(
            status,
            savedMicroseconds,
            savedBytes);
    }

    public static byte[] EncodeRuntimeEvent(IFluidLinkV2RuntimeEvent runtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        return runtimeEvent switch
        {
            FluidLinkV2SessionEvent session => EncodeSessionEvent(session),
            FluidLinkV2FrameEvent frame => EncodeFrameEvent(frame),
            FluidLinkV2ResourceEvent resource => EncodeResourceEvent(resource),
            FluidLinkV2OperationEvent operation => EncodeOperationEvent(operation),
            FluidLinkV2StateEvent state => EncodeStateEvent(state),
            _ => throw InvalidPayload(
                $"Unsupported typed runtime event {runtimeEvent.GetType().Name}.")
        };
    }

    public static IFluidLinkV2RuntimeEvent DecodeRuntimeEvent(
        FluidLinkV2EventOpcode eventOpcode,
        ReadOnlySpan<byte> payload) => eventOpcode switch
        {
            FluidLinkV2EventOpcode.Session => DecodeSessionEvent(payload),
            FluidLinkV2EventOpcode.Frame => DecodeFrameEvent(payload),
            FluidLinkV2EventOpcode.Resource => DecodeResourceEvent(payload),
            FluidLinkV2EventOpcode.Operation => DecodeOperationEvent(payload),
            FluidLinkV2EventOpcode.State => DecodeStateEvent(payload),
            _ => throw InvalidPayload(
                $"Unsupported runtime event opcode {(byte)eventOpcode}.")
        };

    public static byte[] EncodeSessionEvent(FluidLinkV2SessionEvent payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidateEnum(payload.Action, "action");
        byte presence = 0;
        presence |= payload.FrameBudgetMicroseconds.HasValue
            ? SessionFrameBudget
            : (byte)0;
        presence |= payload.RamBudgetBytes.HasValue
            ? SessionRamBudget
            : (byte)0;
        presence |= payload.VramBudgetBytes.HasValue
            ? SessionVramBudget
            : (byte)0;
        presence |= payload.SharedBudgetBytes.HasValue
            ? SessionSharedBudget
            : (byte)0;
        presence |= payload.StagingBudgetBytes.HasValue
            ? SessionStagingBudget
            : (byte)0;
        presence |= payload.SwapchainBudgetBytes.HasValue
            ? SessionSwapchainBudget
            : (byte)0;
        if (payload.Action == FluidLinkV2LifecycleAction.End && presence != 0)
        {
            throw InvalidPayload("session end cannot carry budget fields.");
        }

        var writer = new PayloadWriter();
        writer.WriteByte((byte)payload.Action);
        writer.WriteByte(presence);
        writer.WriteText16(
            payload.SessionId,
            FluidLinkV2Protocol.MaxIdentifierUtf8Bytes,
            "session_id",
            requireNonEmpty: payload.Action == FluidLinkV2LifecycleAction.Begin);
        if (payload.FrameBudgetMicroseconds is { } frameBudget)
        {
            writer.WriteUInt32(frameBudget);
        }
        if (payload.RamBudgetBytes is { } ramBudget)
        {
            writer.WriteUInt64(ramBudget);
        }
        if (payload.VramBudgetBytes is { } vramBudget)
        {
            writer.WriteUInt64(vramBudget);
        }
        if (payload.SharedBudgetBytes is { } sharedBudget)
        {
            writer.WriteUInt64(sharedBudget);
        }
        if (payload.StagingBudgetBytes is { } stagingBudget)
        {
            writer.WriteUInt64(stagingBudget);
        }
        if (payload.SwapchainBudgetBytes is { } swapchainBudget)
        {
            writer.WriteUInt64(swapchainBudget);
        }
        return writer.ToArray();
    }

    public static FluidLinkV2SessionEvent DecodeSessionEvent(
        ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var action = reader.ReadEnum<FluidLinkV2LifecycleAction>("action");
        var presence = reader.ReadByte("presence_mask");
        ValidatePresence(presence, SessionAllowedFields, "session");
        if (action == FluidLinkV2LifecycleAction.End && presence != 0)
        {
            throw InvalidPayload("session end cannot carry budget fields.");
        }
        var sessionId = reader.ReadText16(
            FluidLinkV2Protocol.MaxIdentifierUtf8Bytes,
            "session_id",
            requireNonEmpty: action == FluidLinkV2LifecycleAction.Begin);
        uint? frameBudget = Has(presence, SessionFrameBudget)
            ? reader.ReadUInt32("frame_budget_microseconds")
            : null;
        ulong? ramBudget = Has(presence, SessionRamBudget)
            ? reader.ReadUInt64("ram_budget_bytes")
            : null;
        ulong? vramBudget = Has(presence, SessionVramBudget)
            ? reader.ReadUInt64("vram_budget_bytes")
            : null;
        ulong? sharedBudget = Has(presence, SessionSharedBudget)
            ? reader.ReadUInt64("shared_budget_bytes")
            : null;
        ulong? stagingBudget = Has(presence, SessionStagingBudget)
            ? reader.ReadUInt64("staging_budget_bytes")
            : null;
        ulong? swapchainBudget = Has(presence, SessionSwapchainBudget)
            ? reader.ReadUInt64("swapchain_budget_bytes")
            : null;
        reader.EnsureComplete();
        return new FluidLinkV2SessionEvent(
            action,
            sessionId,
            frameBudget,
            ramBudget,
            vramBudget,
            sharedBudget,
            stagingBudget,
            swapchainBudget);
    }

    public static byte[] EncodeFrameEvent(FluidLinkV2FrameEvent payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidateEnum(payload.Action, "action");
        var presence = payload.TargetFrameMicroseconds.HasValue
            ? FrameTarget
            : (byte)0;
        if (payload.Action == FluidLinkV2LifecycleAction.End && presence != 0)
        {
            throw InvalidPayload("frame end cannot carry a target budget.");
        }
        var writer = new PayloadWriter();
        writer.WriteByte((byte)payload.Action);
        writer.WriteByte(presence);
        writer.WriteUInt64(payload.Frame);
        if (payload.TargetFrameMicroseconds is { } target)
        {
            writer.WriteUInt32(target);
        }
        return writer.ToArray();
    }

    public static FluidLinkV2FrameEvent DecodeFrameEvent(
        ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var action = reader.ReadEnum<FluidLinkV2LifecycleAction>("action");
        var presence = reader.ReadByte("presence_mask");
        ValidatePresence(presence, FrameTarget, "frame");
        if (action == FluidLinkV2LifecycleAction.End && presence != 0)
        {
            throw InvalidPayload("frame end cannot carry a target budget.");
        }
        var frame = reader.ReadUInt64("frame");
        uint? target = Has(presence, FrameTarget)
            ? reader.ReadUInt32("target_frame_microseconds")
            : null;
        reader.EnsureComplete();
        return new FluidLinkV2FrameEvent(action, frame, target);
    }

    public static byte[] EncodeResourceEvent(FluidLinkV2ResourceEvent payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidateEnum(payload.Action, "action");
        var writer = new PayloadWriter();
        writer.WriteByte((byte)payload.Action);
        writer.WriteText16(
            payload.ResourceId,
            FluidLinkV2Protocol.MaxIdentifierUtf8Bytes,
            "resource_id",
            requireNonEmpty: true);
        if (payload.Action == FluidLinkV2ResourceAction.Release)
        {
            if (payload.Kind != FluidLinkV2ResourceKind.Unknown ||
                payload.Memory != FluidLinkV2MemoryLayer.Ram ||
                payload.Lifetime != FluidLinkV2Lifetime.Unknown ||
                payload.SizeBytes != 0 ||
                payload.Aliases is { Count: > 0 })
            {
                throw InvalidPayload(
                    "resource release cannot carry registration fields.");
            }
            return writer.ToArray();
        }

        ValidateEnum(payload.Kind, "kind");
        ValidateEnum(payload.Memory, "memory");
        ValidateEnum(payload.Lifetime, "lifetime");
        var aliases = payload.Aliases ?? [];
        ValidateCount(aliases.Count, FluidLinkV2Protocol.MaxAliases, "aliases");
        writer.WriteByte((byte)payload.Kind);
        writer.WriteByte((byte)payload.Memory);
        writer.WriteByte((byte)payload.Lifetime);
        writer.WriteUInt64(payload.SizeBytes);
        writer.WriteByte(checked((byte)aliases.Count));
        foreach (var alias in aliases)
        {
            writer.WriteText16(
                alias,
                FluidLinkV2Protocol.MaxIdentifierUtf8Bytes,
                "alias",
                requireNonEmpty: true);
        }
        return writer.ToArray();
    }

    public static FluidLinkV2ResourceEvent DecodeResourceEvent(
        ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var action = reader.ReadEnum<FluidLinkV2ResourceAction>("action");
        var resourceId = reader.ReadText16(
            FluidLinkV2Protocol.MaxIdentifierUtf8Bytes,
            "resource_id",
            requireNonEmpty: true);
        if (action == FluidLinkV2ResourceAction.Release)
        {
            reader.EnsureComplete();
            return FluidLinkV2ResourceEvent.Release(resourceId);
        }

        var kind = reader.ReadEnum<FluidLinkV2ResourceKind>("kind");
        var memory = reader.ReadEnum<FluidLinkV2MemoryLayer>("memory");
        var lifetime = reader.ReadEnum<FluidLinkV2Lifetime>("lifetime");
        var sizeBytes = reader.ReadUInt64("size_bytes");
        var aliasCount = reader.ReadByte("alias_count");
        ValidateCount(aliasCount, FluidLinkV2Protocol.MaxAliases, "aliases");
        var aliases = new string[aliasCount];
        for (var index = 0; index < aliases.Length; index += 1)
        {
            aliases[index] = reader.ReadText16(
                FluidLinkV2Protocol.MaxIdentifierUtf8Bytes,
                "alias",
                requireNonEmpty: true);
        }
        reader.EnsureComplete();
        return FluidLinkV2ResourceEvent.Register(
            resourceId,
            kind,
            memory,
            lifetime,
            sizeBytes,
            Array.AsReadOnly(aliases));
    }

    public static byte[] EncodeOperationEvent(FluidLinkV2OperationEvent payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidateEnum(payload.OperationType, "operation_type");
        ValidateEnum(payload.Queue, "queue");

        byte presence = 0;
        presence |= payload.Source is not null ? OperationSource : (byte)0;
        presence |= payload.Target is not null ? OperationTarget : (byte)0;
        presence |= payload.Reason is not null ? OperationReason : (byte)0;
        presence |= payload.Frame.HasValue ? OperationFrame : (byte)0;

        var dependencies = payload.Dependencies ?? [];
        ValidateCount(
            dependencies.Count,
            FluidLinkV2Protocol.MaxDependencies,
            "dependencies");
        var writer = new PayloadWriter();
        writer.WriteByte((byte)payload.OperationType);
        writer.WriteByte((byte)payload.Queue);
        writer.WriteByte(presence);
        writer.WriteText16(
            payload.OperationId,
            FluidLinkV2Protocol.MaxIdentifierUtf8Bytes,
            "operation_id",
            requireNonEmpty: true);
        if (payload.Source is not null)
        {
            writer.WriteText16(
                payload.Source,
                FluidLinkV2Protocol.MaxIdentifierUtf8Bytes,
                "source",
                requireNonEmpty: true);
        }
        if (payload.Target is not null)
        {
            writer.WriteText16(
                payload.Target,
                FluidLinkV2Protocol.MaxIdentifierUtf8Bytes,
                "target",
                requireNonEmpty: true);
        }
        if (payload.Reason is not null)
        {
            writer.WriteText16(
                payload.Reason,
                FluidLinkV2Protocol.MaxReasonUtf8Bytes,
                "reason",
                requireNonEmpty: true);
        }
        writer.WriteUInt32(payload.CostMicroseconds);
        writer.WriteUInt64(payload.SizeBytes);
        if (payload.Frame is { } frame)
        {
            writer.WriteUInt64(frame);
        }
        writer.WriteByte(checked((byte)dependencies.Count));
        foreach (var dependency in dependencies)
        {
            writer.WriteText16(
                dependency,
                FluidLinkV2Protocol.MaxIdentifierUtf8Bytes,
                "dependency",
                requireNonEmpty: true);
        }
        return writer.ToArray();
    }

    public static FluidLinkV2OperationEvent DecodeOperationEvent(
        ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var operationType = reader.ReadEnum<FluidLinkV2OperationType>(
            "operation_type");
        var queue = reader.ReadEnum<FluidLinkV2Queue>("queue");
        var presence = reader.ReadByte("presence_mask");
        ValidatePresence(presence, OperationAllowedFields, "operation");
        var operationId = reader.ReadText16(
            FluidLinkV2Protocol.MaxIdentifierUtf8Bytes,
            "operation_id",
            requireNonEmpty: true);
        var source = Has(presence, OperationSource)
            ? reader.ReadText16(
                FluidLinkV2Protocol.MaxIdentifierUtf8Bytes,
                "source",
                requireNonEmpty: true)
            : null;
        var target = Has(presence, OperationTarget)
            ? reader.ReadText16(
                FluidLinkV2Protocol.MaxIdentifierUtf8Bytes,
                "target",
                requireNonEmpty: true)
            : null;
        var reason = Has(presence, OperationReason)
            ? reader.ReadText16(
                FluidLinkV2Protocol.MaxReasonUtf8Bytes,
                "reason",
                requireNonEmpty: true)
            : null;
        var costMicroseconds = reader.ReadUInt32("cost_microseconds");
        var sizeBytes = reader.ReadUInt64("size_bytes");
        ulong? frame = Has(presence, OperationFrame)
            ? reader.ReadUInt64("frame")
            : null;
        var dependencyCount = reader.ReadByte("dependency_count");
        ValidateCount(
            dependencyCount,
            FluidLinkV2Protocol.MaxDependencies,
            "dependencies");
        var dependencies = new string[dependencyCount];
        for (var index = 0; index < dependencies.Length; index += 1)
        {
            dependencies[index] = reader.ReadText16(
                FluidLinkV2Protocol.MaxIdentifierUtf8Bytes,
                "dependency",
                requireNonEmpty: true);
        }
        reader.EnsureComplete();
        return new FluidLinkV2OperationEvent(
            operationType,
            queue,
            operationId,
            costMicroseconds,
            sizeBytes,
            source,
            target,
            reason,
            frame,
            Array.AsReadOnly(dependencies));
    }

    public static byte[] EncodeStateEvent(FluidLinkV2StateEvent payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidateEnum(payload.Action, "action");
        return [(byte)payload.Action];
    }

    public static FluidLinkV2StateEvent DecodeStateEvent(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var action = reader.ReadEnum<FluidLinkV2StateAction>("action");
        reader.EnsureComplete();
        return new FluidLinkV2StateEvent(action);
    }

    public static byte[] EncodeError(FluidLinkV2ErrorPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var writer = new PayloadWriter();
        ValidateEnum(payload.ErrorCode, "error_code");
        writer.WriteUInt16((ushort)payload.ErrorCode);
        writer.WriteText16(
            payload.Message,
            FluidLinkV2Protocol.MaxReasonUtf8Bytes,
            "message",
            requireNonEmpty: true);
        return writer.ToArray();
    }

    public static FluidLinkV2ErrorPayload DecodeError(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var errorCode = (FluidLinkV2ErrorCode)reader.ReadUInt16("error_code");
        ValidateEnum(errorCode, "error_code");
        var message = reader.ReadText16(
            FluidLinkV2Protocol.MaxReasonUtf8Bytes,
            "message",
            requireNonEmpty: true);
        reader.EnsureComplete();
        return new FluidLinkV2ErrorPayload(errorCode, message);
    }

    private static bool Has(byte value, byte flag) => (value & flag) != 0;

    private static void ValidateHash(ReadOnlySpan<byte> value)
    {
        if (value.Length != 32)
        {
            throw InvalidPayload("contract_sha256 must contain exactly 32 bytes.");
        }
    }

    private static void ValidateCapabilities(
        FluidLinkV2Capability value,
        string field)
    {
        if ((value & ~FluidLinkV2Protocol.AllCapabilities) != 0)
        {
            throw InvalidPayload($"{field} contains unknown capability bits.");
        }
    }

    private static void ValidateMaximumPayload(uint value)
    {
        if (value is 0 or > FluidLinkV2Protocol.MaxPayloadBytes)
        {
            throw InvalidPayload(
                $"max_payload_bytes must be between 1 and " +
                $"{FluidLinkV2Protocol.MaxPayloadBytes}.");
        }
    }

    private static void ValidateDecisionStatus(FluidLinkV2DecisionStatus value)
    {
        if ((value & ~AllowedDecisionStatus) != 0 ||
            (value.HasFlag(FluidLinkV2DecisionStatus.Executed) &&
             !value.HasFlag(FluidLinkV2DecisionStatus.HasExecutionState)))
        {
            throw InvalidPayload("status_flags contains an invalid combination.");
        }
    }

    private static void ValidatePresence(byte value, byte allowed, string schema)
    {
        if ((value & ~allowed) != 0)
        {
            throw InvalidPayload(
                $"{schema} presence_mask contains unknown field bits.");
        }
    }

    private static void ValidateCount(int value, int maximum, string field)
    {
        if (value < 0 || value > maximum)
        {
            throw InvalidPayload($"{field} exceeds its {maximum}-item limit.");
        }
    }

    private static void ValidateEnum<T>(T value, string field)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw InvalidPayload($"{field} contains an unknown enum value.");
        }
    }

    private static FluidLinkV2ProtocolException InvalidPayload(
        string message,
        Exception? innerException = null) =>
        new("invalid_payload", message, innerException: innerException);

    private sealed class PayloadWriter
    {
        private readonly ArrayBufferWriter<byte> buffer = new();

        public void WriteByte(byte value)
        {
            var target = buffer.GetSpan(1);
            target[0] = value;
            buffer.Advance(1);
        }

        public void WriteUInt16(ushort value)
        {
            var target = buffer.GetSpan(sizeof(ushort));
            BinaryPrimitives.WriteUInt16LittleEndian(target, value);
            buffer.Advance(sizeof(ushort));
        }

        public void WriteUInt32(uint value)
        {
            var target = buffer.GetSpan(sizeof(uint));
            BinaryPrimitives.WriteUInt32LittleEndian(target, value);
            buffer.Advance(sizeof(uint));
        }

        public void WriteUInt64(ulong value)
        {
            var target = buffer.GetSpan(sizeof(ulong));
            BinaryPrimitives.WriteUInt64LittleEndian(target, value);
            buffer.Advance(sizeof(ulong));
        }

        public void WriteBytes(ReadOnlySpan<byte> value)
        {
            value.CopyTo(buffer.GetSpan(value.Length));
            buffer.Advance(value.Length);
        }

        public void WriteText8(
            string value,
            int maximumUtf8Bytes,
            string field,
            bool requireNonEmpty)
        {
            var encoded = EncodeText(
                value,
                maximumUtf8Bytes,
                field,
                requireNonEmpty);
            if (encoded.Length > byte.MaxValue)
            {
                throw InvalidPayload($"{field} cannot fit in a text8 field.");
            }
            WriteByte(checked((byte)encoded.Length));
            WriteBytes(encoded);
        }

        public void WriteText16(
            string value,
            int maximumUtf8Bytes,
            string field,
            bool requireNonEmpty)
        {
            var encoded = EncodeText(
                value,
                maximumUtf8Bytes,
                field,
                requireNonEmpty);
            WriteUInt16(checked((ushort)encoded.Length));
            WriteBytes(encoded);
        }

        public byte[] ToArray()
        {
            if (buffer.WrittenCount > FluidLinkV2Protocol.MaxPayloadBytes)
            {
                throw new FluidLinkV2ProtocolException(
                    "payload_too_large",
                    $"FluidLink v2 payload exceeds " +
                    $"{FluidLinkV2Protocol.MaxPayloadBytes} bytes.");
            }
            return buffer.WrittenSpan.ToArray();
        }

        private static byte[] EncodeText(
            string value,
            int maximumUtf8Bytes,
            string field,
            bool requireNonEmpty)
        {
            if (value is null || (requireNonEmpty && string.IsNullOrWhiteSpace(value)))
            {
                throw InvalidPayload($"{field} must not be empty.");
            }
            byte[] encoded;
            try
            {
                encoded = StrictUtf8.GetBytes(value);
            }
            catch (EncoderFallbackException exception)
            {
                throw InvalidPayload($"{field} is not valid UTF-8 text.", exception);
            }
            if (encoded.Length > maximumUtf8Bytes)
            {
                throw InvalidPayload(
                    $"{field} exceeds its {maximumUtf8Bytes}-byte UTF-8 limit.");
            }
            return encoded;
        }
    }

    private ref struct PayloadReader
    {
        private readonly ReadOnlySpan<byte> payload;
        private int offset;

        public PayloadReader(ReadOnlySpan<byte> payload)
        {
            this.payload = payload;
            offset = 0;
        }

        public byte ReadByte(string field) => ReadBytes(1, field)[0];

        public ushort ReadUInt16(string field) =>
            BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(sizeof(ushort), field));

        public uint ReadUInt32(string field) =>
            BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(sizeof(uint), field));

        public ulong ReadUInt64(string field) =>
            BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(sizeof(ulong), field));

        public ReadOnlySpan<byte> ReadBytes(int count, string field)
        {
            if (count < 0 || count > payload.Length - offset)
            {
                throw new FluidLinkV2ProtocolException(
                    "truncated_payload",
                    $"FluidLink v2 payload ended while reading {field}.");
            }
            var result = payload.Slice(offset, count);
            offset += count;
            return result;
        }

        public string ReadText8(
            int maximumUtf8Bytes,
            string field,
            bool requireNonEmpty)
        {
            var length = ReadByte($"{field}_length");
            return DecodeText(
                ReadBytes(length, field),
                maximumUtf8Bytes,
                field,
                requireNonEmpty);
        }

        public string ReadText16(
            int maximumUtf8Bytes,
            string field,
            bool requireNonEmpty)
        {
            var length = ReadUInt16($"{field}_length");
            return DecodeText(
                ReadBytes(length, field),
                maximumUtf8Bytes,
                field,
                requireNonEmpty);
        }

        public T ReadEnum<T>(string field)
            where T : struct, Enum
        {
            var raw = ReadByte(field);
            var value = (T)Enum.ToObject(typeof(T), raw);
            ValidateEnum(value, field);
            return value;
        }

        public void EnsureComplete()
        {
            if (offset != payload.Length)
            {
                throw InvalidPayload(
                    $"FluidLink v2 payload contains {payload.Length - offset} " +
                    "unexpected trailing bytes.");
            }
        }

        private static string DecodeText(
            ReadOnlySpan<byte> encoded,
            int maximumUtf8Bytes,
            string field,
            bool requireNonEmpty)
        {
            if (encoded.Length > maximumUtf8Bytes)
            {
                throw InvalidPayload(
                    $"{field} exceeds its {maximumUtf8Bytes}-byte UTF-8 limit.");
            }
            string value;
            try
            {
                value = StrictUtf8.GetString(encoded);
            }
            catch (DecoderFallbackException exception)
            {
                throw InvalidPayload($"{field} is not valid UTF-8 text.", exception);
            }
            if (requireNonEmpty && string.IsNullOrWhiteSpace(value))
            {
                throw InvalidPayload($"{field} must not be empty.");
            }
            return value;
        }
    }
}
