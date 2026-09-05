using System.Text.Json;
using FluidRuntime.Native;

namespace FluidRuntime.Runtime;

public sealed record VulkanCopyRunReport(
    bool Optimized, int ProcessId, long ManagedEndToEndMicroseconds,
    GatewayUpdateUploadAuthorization? Authorization, HookControlPolicy? Policy,
    JsonElement NativeEvidence, IReadOnlyList<HookIpcEvent> Events, string NativeDiagnostics);

public sealed record VulkanCopyPairReport(
    int PairIndex, string Phase, string Order,
    VulkanCopyRunReport Baseline, VulkanCopyRunReport Optimized);

public sealed record VulkanAuthorizationFailure(
    int PairIndex, string Phase, string FailureType, string Message,
    bool NativePolicyPublished, VulkanCopyRunReport BaselineFallback);

public sealed record VulkanCopyLabReport(
    string Mode, DateTimeOffset CreatedAtUtc, NativeTransferDescriptor TransferDescriptor,
    NativeTransferTopology TransferTopology, string TargetSha256, string LibrarySha256,
    int CandidateActionCount, ulong AvoidedLogicalBytesPerOptimizedRun,
    IReadOnlyList<VulkanCopyPairReport> Pairs, VulkanAuthorizationFailure? FailClosed,
    PairedTailLatencySummary? ManagedEndToEndMicroseconds,
    PairedTailLatencySummary? NativeWorkloadMicroseconds,
    PairedTailLatencySummary? SubmitToFenceMicroseconds,
    PairedTailLatencySummary? GpuMicroseconds,
    bool NativeExecutionGatePassed, bool PerformanceClaimAllowed,
    IReadOnlyList<string> Limitations);
