using System.Text.Json;

namespace FluidRuntime.Runtime;

public sealed record ControlPolicyMatrixReport(
    string Mode,
    bool TargetOwned,
    bool WarpOnly,
    bool PerformanceClaim,
    int RepetitionsPerCase,
    int ExpectedRunCount,
    int CompletedRunCount,
    bool DeterministicAcrossRepetitions,
    bool DeterministicAcrossConfigurations,
    bool Passed,
    IReadOnlyList<ControlPolicyCaseReport> Cases);

public sealed record ControlPolicyCaseReport(
    string Configuration,
    string PolicyCase,
    int ExpectedRunCount,
    int CompletedRunCount,
    bool Deterministic,
    bool Passed,
    string ProjectionSha256,
    IReadOnlyList<ControlPolicyRunEvidence> Runs);

public sealed record ControlPolicyRunEvidence(
    string Configuration,
    string PolicyCase,
    int Repetition,
    int ProcessId,
    int ExitCode,
    string ControlPolicyWaitHresult,
    string ControlPolicyExpiryWaitHresult,
    long PublishedEpoch,
    long AcknowledgedEpoch,
    long AppliedActionCount,
    long RejectedCount,
    string Status,
    long AcceptedEventCount,
    long ForwardedCopyCount,
    ulong ForwardedCopyBytes,
    long SkippedCopyCount,
    ulong SkippedCopyBytes,
    long EventCount,
    long LostSequenceCount,
    long NativeOverrunCount,
    string DestinationBufferHash,
    string DestinationTextureHash,
    string DestinationSubresourceHash,
    bool ContentEquivalent,
    bool RollbackRestored,
    bool Passed,
    JsonElement TargetReport);
