namespace FluidRuntime.Native;

public enum HookControlPolicyStatus : long
{
    None = 0,
    Accepted = 1,
    Rejected = 2,
    Expired = 3,
    Exhausted = 4
}

public sealed record HookControlPolicy(
    long Epoch,
    long ExpiresAtQpc,
    ulong ActionMask,
    ulong ActionBudget);

public sealed record HookControlSnapshot(
    long PublishedEpoch,
    long AcknowledgedEpoch,
    long AppliedActionCount,
    HookControlPolicyStatus Status);
