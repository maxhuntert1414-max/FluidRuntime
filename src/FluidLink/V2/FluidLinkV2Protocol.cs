namespace FluidLink;

public enum FluidLinkV2FrameKind : byte
{
    Request = 1,
    Response = 2
}

[Flags]
public enum FluidLinkV2FrameFlags : byte
{
    None = 0,
    Ok = 1,
    HasSession = 2
}

public enum FluidLinkV2Opcode : byte
{
    Hello = 1,
    Welcome = 2,
    RuntimeEvent = 10,
    RuntimeDecision = 11,
    Ping = 20,
    Pong = 21,
    Goodbye = 30,
    Error = 255
}

public enum FluidLinkV2EventOpcode : byte
{
    Session = 100,
    Frame = 101,
    Resource = 102,
    Operation = 103,
    State = 104,
    OperationBatch = 105
}

public enum FluidLinkV2DecisionOpcode : byte
{
    Execute = 0,
    EliminateSelfCopy = 1,
    DeduplicateIdenticalTransfer = 2,
    CollapseAliasedResourceCopy = 3,
    RemoveOrphanSync = 4,
    RemoveEmptySync = 5,
    ReuseTransientBuffer = 6,
    BatchVector = 7,
    Unknown = 255
}

[Flags]
public enum FluidLinkV2Capability : ulong
{
    None = 0,
    BinaryPayloads = 1UL << 0,
    FixedPointUnits = 1UL << 1,
    Heartbeat = 1UL << 2,
    RuntimeEvents = 1UL << 3,
    RuntimeDecisions = 1UL << 4,
    MemoryTransit = 1UL << 5,
    SessionLifecycle = 1UL << 6,
    BatchedRuntimeEvents = 1UL << 7
}

public enum FluidLinkV2LifecycleAction : byte
{
    Begin = 1,
    End = 2
}

public enum FluidLinkV2ResourceAction : byte
{
    Register = 1,
    Release = 2
}

public enum FluidLinkV2StateAction : byte
{
    Snapshot = 1
}

public enum FluidLinkV2ResourceKind : byte
{
    Unknown = 0,
    Buffer = 1,
    Texture = 2,
    Framebuffer = 3,
    Command = 4
}

public enum FluidLinkV2MemoryLayer : byte
{
    Ram = 1,
    Vram = 2,
    Shared = 3,
    Staging = 4,
    Swapchain = 5,
    Display = 6
}

public enum FluidLinkV2Lifetime : byte
{
    Unknown = 0,
    Asset = 1,
    Frame = 2,
    Transient = 3,
    Session = 4
}

public enum FluidLinkV2OperationType : byte
{
    Copy = 1,
    Sync = 2,
    Allocate = 3,
    Upload = 4,
    Present = 5,
    Compute = 6,
    Draw = 7
}

public enum FluidLinkV2Queue : byte
{
    Unknown = 0,
    Cpu = 1,
    Copy = 2,
    Graphics = 3,
    Compute = 4,
    Present = 5
}

public enum FluidLinkV2ErrorCode : ushort
{
    InvalidFrame = 1,
    HandshakeRequired = 2,
    ContractMismatch = 3,
    RequiredCapabilityUnavailable = 4,
    CapabilityNotNegotiated = 5,
    SequenceMismatch = 6,
    SessionMismatch = 7,
    UnsupportedOpcode = 8,
    UnsupportedEventOpcode = 9,
    InvalidPayload = 10,
    RuntimeEventRejected = 11,
    SessionClosed = 12
}

[Flags]
public enum FluidLinkV2DecisionStatus : byte
{
    None = 0,
    Accepted = 1,
    HasExecutionState = 2,
    Executed = 4
}

public static class FluidLinkV2Protocol
{
    public const string Version = "fluidlink-v2";
    public const string Magic = "FLNK";
    public const byte WireVersion = 2;
    public const int HeaderSize = 56;
    public const int MaxPayloadBytes = 65_535;
    public const int MaxPeerNameUtf8Bytes = 128;
    public const int MaxPeerVersionUtf8Bytes = 64;
    public const int MaxNonceUtf8Bytes = 128;
    public const int MaxIdentifierUtf8Bytes = 256;
    public const int MaxReasonUtf8Bytes = 512;
    public const int MaxAliases = 32;
    public const int MaxDependencies = 32;
    public const string ContractSha256 =
        "0d24d96aec32d74e123f9e198e51adde74ddf190e8c40b0ac18bddf5c4108b2f";

    public const FluidLinkV2Capability AllCapabilities =
        FluidLinkV2Capability.BinaryPayloads |
        FluidLinkV2Capability.FixedPointUnits |
        FluidLinkV2Capability.Heartbeat |
        FluidLinkV2Capability.RuntimeEvents |
        FluidLinkV2Capability.RuntimeDecisions |
        FluidLinkV2Capability.MemoryTransit |
        FluidLinkV2Capability.SessionLifecycle;

    public const FluidLinkV2Capability RequiredCapabilities =
        FluidLinkV2Capability.BinaryPayloads |
        FluidLinkV2Capability.FixedPointUnits |
        FluidLinkV2Capability.RuntimeEvents |
        FluidLinkV2Capability.RuntimeDecisions;

    public const FluidLinkV2Capability SupportedCapabilities =
        AllCapabilities |
        FluidLinkV2Capability.BatchedRuntimeEvents;

    private static readonly byte[] ContractHashBytes =
        Convert.FromHexString(ContractSha256);

    public static ReadOnlyMemory<byte> ContractHash => ContractHashBytes;

    public static string DecisionPolicyName(FluidLinkV2DecisionOpcode opcode) =>
        opcode switch
        {
            FluidLinkV2DecisionOpcode.Execute => "execute",
            FluidLinkV2DecisionOpcode.EliminateSelfCopy => "eliminate-self-copy",
            FluidLinkV2DecisionOpcode.DeduplicateIdenticalTransfer =>
                "deduplicate-identical-transfer",
            FluidLinkV2DecisionOpcode.CollapseAliasedResourceCopy =>
                "collapse-aliased-resource-copy",
            FluidLinkV2DecisionOpcode.RemoveOrphanSync => "remove-orphan-sync",
            FluidLinkV2DecisionOpcode.RemoveEmptySync => "remove-empty-sync",
            FluidLinkV2DecisionOpcode.ReuseTransientBuffer => "reuse-transient-buffer",
            FluidLinkV2DecisionOpcode.BatchVector => "batch-vector",
            _ => "unknown"
        };
}

public static class FluidLinkV2BatchProtocol
{
    public const string Profile = "fluidlink-v2-batched-runtime-events-v1";
    public const int MaxOperations = 256;
    public const string ContractSha256 =
        "bf8727c22ac878ceff6dd0f462d6db5e81174737e839ecdf2e263a6f55268542";

    public const FluidLinkV2Capability AllCapabilities =
        FluidLinkV2Protocol.AllCapabilities |
        FluidLinkV2Capability.BatchedRuntimeEvents;

    public const FluidLinkV2Capability RequiredCapabilities =
        FluidLinkV2Protocol.RequiredCapabilities |
        FluidLinkV2Capability.BatchedRuntimeEvents;

    private static readonly byte[] ContractHashBytes =
        Convert.FromHexString(ContractSha256);

    public static ReadOnlyMemory<byte> ContractHash => ContractHashBytes;
}

public sealed record FluidLinkV2HelloPayload(
    ReadOnlyMemory<byte> ContractHash,
    FluidLinkV2Capability RequestedCapabilities,
    FluidLinkV2Capability RequiredCapabilities,
    string ClientName,
    string ClientVersion);

public sealed record FluidLinkV2WelcomePayload(
    ReadOnlyMemory<byte> ContractHash,
    FluidLinkV2Capability AvailableCapabilities,
    FluidLinkV2Capability AcceptedCapabilities,
    uint MaxPayloadBytes,
    string ServerName,
    string ServerVersion);

public sealed record FluidLinkV2Welcome(
    string ContractSha256,
    string SessionId,
    string ServerName,
    string ServerVersion,
    FluidLinkV2Capability AvailableCapabilities,
    FluidLinkV2Capability AcceptedCapabilities,
    uint MaxPayloadBytes);

public interface IFluidLinkV2RuntimeEvent
{
    FluidLinkV2EventOpcode EventOpcode { get; }
}

public sealed record FluidLinkV2SessionEvent(
    FluidLinkV2LifecycleAction Action,
    string SessionId,
    uint? FrameBudgetMicroseconds = null,
    ulong? RamBudgetBytes = null,
    ulong? VramBudgetBytes = null,
    ulong? SharedBudgetBytes = null,
    ulong? StagingBudgetBytes = null,
    ulong? SwapchainBudgetBytes = null) : IFluidLinkV2RuntimeEvent
{
    public FluidLinkV2EventOpcode EventOpcode => FluidLinkV2EventOpcode.Session;
}

public sealed record FluidLinkV2FrameEvent(
    FluidLinkV2LifecycleAction Action,
    ulong Frame,
    uint? TargetFrameMicroseconds = null) : IFluidLinkV2RuntimeEvent
{
    public FluidLinkV2EventOpcode EventOpcode => FluidLinkV2EventOpcode.Frame;
}

public sealed record FluidLinkV2ResourceEvent(
    FluidLinkV2ResourceAction Action,
    string ResourceId,
    FluidLinkV2ResourceKind Kind = FluidLinkV2ResourceKind.Unknown,
    FluidLinkV2MemoryLayer Memory = FluidLinkV2MemoryLayer.Ram,
    FluidLinkV2Lifetime Lifetime = FluidLinkV2Lifetime.Unknown,
    ulong SizeBytes = 0,
    IReadOnlyList<string>? Aliases = null) : IFluidLinkV2RuntimeEvent
{
    public FluidLinkV2EventOpcode EventOpcode => FluidLinkV2EventOpcode.Resource;

    public static FluidLinkV2ResourceEvent Register(
        string resourceId,
        FluidLinkV2ResourceKind kind,
        FluidLinkV2MemoryLayer memory,
        FluidLinkV2Lifetime lifetime,
        ulong sizeBytes,
        IReadOnlyList<string>? aliases = null) =>
        new(
            FluidLinkV2ResourceAction.Register,
            resourceId,
            kind,
            memory,
            lifetime,
            sizeBytes,
            aliases);

    public static FluidLinkV2ResourceEvent Release(string resourceId) =>
        new(FluidLinkV2ResourceAction.Release, resourceId);
}

public sealed record FluidLinkV2OperationEvent(
    FluidLinkV2OperationType OperationType,
    FluidLinkV2Queue Queue,
    string OperationId,
    uint CostMicroseconds,
    ulong SizeBytes,
    string? Source = null,
    string? Target = null,
    string? Reason = null,
    ulong? Frame = null,
    IReadOnlyList<string>? Dependencies = null) : IFluidLinkV2RuntimeEvent
{
    public FluidLinkV2EventOpcode EventOpcode => FluidLinkV2EventOpcode.Operation;
}

public sealed record FluidLinkV2OperationBatchEvent(
    string BatchId,
    int OperationCount,
    FluidLinkV2OperationType OperationType,
    FluidLinkV2Queue Queue,
    uint CostMicroseconds,
    ulong SizeBytes,
    string? Source = null,
    string? Target = null,
    string? Reason = null,
    ulong? Frame = null,
    IReadOnlyList<string>? Dependencies = null);

public sealed record FluidLinkV2OperationBatchDecision(
    string BatchId,
    IReadOnlyList<FluidLinkV2RuntimeDecision> Decisions);

public sealed record FluidLinkV2StateEvent(
    FluidLinkV2StateAction Action = FluidLinkV2StateAction.Snapshot)
    : IFluidLinkV2RuntimeEvent
{
    public FluidLinkV2EventOpcode EventOpcode => FluidLinkV2EventOpcode.State;
}

public sealed record FluidLinkV2RuntimeDecisionPayload(
    FluidLinkV2DecisionStatus Status,
    ulong SavedMicroseconds,
    ulong SavedBytes)
{
    public bool Accepted => Status.HasFlag(FluidLinkV2DecisionStatus.Accepted);

    public bool? Executed => Status.HasFlag(
        FluidLinkV2DecisionStatus.HasExecutionState)
        ? Status.HasFlag(FluidLinkV2DecisionStatus.Executed)
        : null;
}

public sealed record FluidLinkV2RuntimeDecision(
    FluidLinkV2EventOpcode EventOpcode,
    FluidLinkV2DecisionOpcode DecisionOpcode,
    FluidLinkV2DecisionStatus Status,
    ulong SavedMicroseconds,
    ulong SavedBytes)
{
    public bool Accepted => Status.HasFlag(FluidLinkV2DecisionStatus.Accepted);

    public bool? Executed => Status.HasFlag(
        FluidLinkV2DecisionStatus.HasExecutionState)
        ? Status.HasFlag(FluidLinkV2DecisionStatus.Executed)
        : null;
}

public sealed record FluidLinkV2ErrorPayload(
    FluidLinkV2ErrorCode ErrorCode,
    string Message);

public sealed class FluidLinkV2ProtocolException : Exception
{
    public FluidLinkV2ProtocolException(
        string code,
        string message,
        FluidLinkV2ErrorCode? peerErrorCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        PeerErrorCode = peerErrorCode;
    }

    public string Code { get; }

    public FluidLinkV2ErrorCode? PeerErrorCode { get; }
}
