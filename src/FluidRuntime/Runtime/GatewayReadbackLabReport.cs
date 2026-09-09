using FluidRuntime.Native;

namespace FluidRuntime.Runtime;

public sealed record GatewayReadbackLabReport(
    string Mode,
    bool FailClosed,
    string TargetSha256,
    string HookSha256,
    ReadbackElisionLabReport NativeEvidence,
    IReadOnlyList<GatewayUpdateUploadAuthorization> Authorizations,
    PairedTailLatencySummary ManagedEndToEndMicroseconds)
{
    public string GatewayBackend => Authorizations[0].GatewayBackend;
    public GatewayLibraryIdentity? GatewayLibrary => Authorizations[0].GatewayLibrary;
    public long WireExchangeCount => Authorizations.Sum(item => (long)item.WireExchangeCount);
    public long TransportRoundTripCount => Authorizations.Sum(item => (long)item.TransportRoundTripCount);
    public bool PhysicalTransferBytesMeasured => false;
    public bool PerformanceClaimAllowed => false;
    public string ClaimScope => "owned-d3d11-readback-correctness-not-general-game-performance";
}

public sealed record GatewayReadbackFailureReport(
    string Mode,
    bool FailClosed,
    bool RejectedRunPolicyPublished,
    int CompletedTrialPairs,
    string FailureType,
    string FailureMessage,
    string TargetSha256,
    string HookSha256,
    ReadbackElisionRunReport BaselineFallback);

public sealed class GatewayReadbackDeniedException(Exception error, GatewayReadbackFailureReport report)
    : Exception("Gateway readback authorization denied; original readback completed.", error)
{
    public GatewayReadbackFailureReport Report { get; } = report;
}
