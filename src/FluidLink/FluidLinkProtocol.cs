namespace FluidLink;

public enum FluidLinkFrameKind : byte
{
    Request = 1,
    Response = 2
}

[Flags]
public enum FluidLinkFrameFlags : byte
{
    None = 0,
    Ok = 1,
    HasSession = 2,
    JsonPayload = 4
}

public enum FluidLinkOpcode : byte
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

public enum FluidLinkEventOpcode : byte
{
    Session = 100,
    Frame = 101,
    Resource = 102,
    Operation = 103,
    State = 104
}

public enum FluidLinkDecisionOpcode : byte
{
    Execute = 0,
    EliminateSelfCopy = 1,
    DeduplicateIdenticalTransfer = 2,
    CollapseAliasedResourceCopy = 3,
    RemoveOrphanSync = 4,
    RemoveEmptySync = 5,
    ReuseTransientBuffer = 6,
    Unknown = 255
}

public static class FluidLinkProtocol
{
    public const string Version = "fluidlink-v1";
    public const string Magic = "FLNK";
    public const byte WireVersion = 1;
    public const int HeaderSize = 56;
    public const int MaxPayloadBytes = 1024 * 1024;
    public const int MaxJsonDepth = 64;
    public const int MaxCapabilities = 64;
    public const int MaxCapabilityNameUtf8Bytes = 128;
    public const int MaxPeerNameUtf8Bytes = 128;
    public const int MaxPeerVersionUtf8Bytes = 64;
    public const int MaxNonceUtf8Bytes = 128;
    public const string ContractSha256 =
        "10b46685472d13d2d49cc81aa1f7df2d654c1ec53fdc666e086e0d062ad114fa";

    public static IReadOnlyList<string> RuntimeCapabilities { get; } =
    Array.AsReadOnly(new[]
    {
        "binary.framing.v1",
        "compact.decisions.v1",
        "heartbeat.v1",
        "memory.transit.v1",
        "runtime.decisions.v1",
        "runtime.events.v1",
        "session.lifecycle.v1"
    });

    public static IReadOnlyList<string> RequiredRuntimeCapabilities { get; } =
    Array.AsReadOnly(new[]
    {
        "binary.framing.v1",
        "compact.decisions.v1",
        "runtime.decisions.v1",
        "runtime.events.v1"
    });

    public static string DecisionPolicyName(FluidLinkDecisionOpcode opcode) => opcode switch
    {
        FluidLinkDecisionOpcode.Execute => "execute",
        FluidLinkDecisionOpcode.EliminateSelfCopy => "eliminate-self-copy",
        FluidLinkDecisionOpcode.DeduplicateIdenticalTransfer =>
            "deduplicate-identical-transfer",
        FluidLinkDecisionOpcode.CollapseAliasedResourceCopy =>
            "collapse-aliased-resource-copy",
        FluidLinkDecisionOpcode.RemoveOrphanSync => "remove-orphan-sync",
        FluidLinkDecisionOpcode.RemoveEmptySync => "remove-empty-sync",
        FluidLinkDecisionOpcode.ReuseTransientBuffer => "reuse-transient-buffer",
        _ => "unknown"
    };
}

public sealed record FluidLinkWelcome(
    string ContractSha256,
    string SessionId,
    string ServerName,
    string ServerVersion,
    IReadOnlyList<string> AvailableCapabilities,
    IReadOnlyList<string> AcceptedCapabilities,
    int MaxPayloadBytes,
    int MaxJsonDepth);

public sealed record FluidLinkRuntimeDecision(
    FluidLinkEventOpcode EventOpcode,
    FluidLinkDecisionOpcode DecisionOpcode,
    bool Accepted,
    bool? Executed,
    double SavedMilliseconds,
    double SavedMegabytes);

public sealed class FluidLinkProtocolException : Exception
{
    public FluidLinkProtocolException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
