using System.Text.Json;

namespace FluidRuntime.Runtime;

public sealed record HookLabReport(
    string Mode,
    bool ReadOnly,
    bool WouldModifySystem,
    bool CopyElisionEnabled,
    int TargetProcessId,
    string RingName,
    uint RingAbiVersion,
    ulong QpcFrequency,
    long EventCount,
    long LostSequenceCount,
    long NativeOverrunCount,
    IReadOnlyDictionary<string, long> EventTypeCounts,
    ulong CopyResourceBytes,
    long RedundantCopyCandidateCount,
    ulong RedundantCopyBytes,
    double AvoidableCopySharePercent,
    long ForwardedCopyCount,
    ulong ForwardedCopyBytes,
    long SkippedCopyCount,
    ulong SkippedCopyBytes,
    bool ContentEquivalent,
    bool RollbackRestored,
    string DestinationBufferHash,
    string DestinationTextureHash,
    ulong QpcFrequencyFromTarget,
    ulong WorkloadQpcTicks,
    JsonElement TargetReport);
