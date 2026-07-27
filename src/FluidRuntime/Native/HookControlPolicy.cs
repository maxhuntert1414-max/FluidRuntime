namespace FluidRuntime.Native;

public enum HookControlPolicyStatus : long
{
    None = 0,
    Accepted = 1,
    Rejected = 2,
    Expired = 3,
    Exhausted = 4
}

public enum HookControlPolicyCase
{
    None = 0,
    Valid,
    NoOptIn,
    WrongEpoch,
    UnknownAction,
    WrongBudget,
    TooLongExpiry,
    AlreadyExpired,
    AcceptedThenExpired
}

public static class HookControlPolicyCases
{
    public static IReadOnlyList<HookControlPolicyCase> Matrix { get; } =
    [
        HookControlPolicyCase.Valid,
        HookControlPolicyCase.NoOptIn,
        HookControlPolicyCase.WrongEpoch,
        HookControlPolicyCase.UnknownAction,
        HookControlPolicyCase.WrongBudget,
        HookControlPolicyCase.TooLongExpiry,
        HookControlPolicyCase.AlreadyExpired,
        HookControlPolicyCase.AcceptedThenExpired
    ];

    public static string ToCliValue(this HookControlPolicyCase policyCase) => policyCase switch
    {
        HookControlPolicyCase.Valid => "valid",
        HookControlPolicyCase.NoOptIn => "no-opt-in",
        HookControlPolicyCase.WrongEpoch => "wrong-epoch",
        HookControlPolicyCase.UnknownAction => "unknown-action",
        HookControlPolicyCase.WrongBudget => "wrong-budget",
        HookControlPolicyCase.TooLongExpiry => "too-long-expiry",
        HookControlPolicyCase.AlreadyExpired => "already-expired",
        HookControlPolicyCase.AcceptedThenExpired => "accepted-then-expired",
        _ => "none"
    };

    internal static HookControlPolicy CreateLabPolicy(
        this HookControlPolicyCase policyCase,
        ulong qpcFrequency,
        long now)
    {
        if (policyCase is HookControlPolicyCase.None)
        {
            throw new ArgumentOutOfRangeException(nameof(policyCase));
        }

        var normalExpiry = checked(now + (long)qpcFrequency * 3);
        return policyCase switch
        {
            HookControlPolicyCase.WrongEpoch => new(2, normalExpiry, 1, 1),
            HookControlPolicyCase.UnknownAction => new(1, normalExpiry, 8, 1),
            HookControlPolicyCase.WrongBudget =>
                new(1, normalExpiry, 1, HookRingReader.MaxControlActionBudget + 1),
            HookControlPolicyCase.TooLongExpiry =>
                new(1, checked(now + (long)qpcFrequency * 5), 1, 1),
            HookControlPolicyCase.AlreadyExpired => new(1, now - 1, 1, 1),
            HookControlPolicyCase.AcceptedThenExpired =>
                new(1, checked(now + Math.Max(1, (long)qpcFrequency / 10)), 1, 1),
            _ => new(1, normalExpiry, 1, 1)
        };
    }
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
