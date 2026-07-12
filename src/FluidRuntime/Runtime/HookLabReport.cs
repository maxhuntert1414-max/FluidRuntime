using System.Text.Json;

namespace FluidRuntime.Runtime;

public sealed record HookLabReport(
    string Mode,
    bool ReadOnly,
    bool WouldModifySystem,
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
    JsonElement TargetReport);
