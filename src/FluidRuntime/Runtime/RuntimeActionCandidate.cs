namespace FluidRuntime.Runtime;

public sealed record RuntimeActionCandidate(
    string Action,
    string ControlSurface,
    string Hypothesis,
    string Disposition,
    bool RequiresNativeBackend,
    bool RequiresPrivilege,
    bool Blocked,
    IReadOnlyDictionary<string, double> Evidence);
