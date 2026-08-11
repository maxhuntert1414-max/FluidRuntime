using FluidRuntime.Cli;
using FluidRuntime.Native;

namespace FluidRuntime.Runtime;

public sealed record GatewayUpdateUploadFailClosedReport(
    string Mode,
    bool TargetOwned,
    bool CooperativeLoad,
    bool RemoteInjection,
    bool AuthorizationAccepted,
    bool NativePolicyPublished,
    bool BaselineFallbackCompleted,
    string FailureStage,
    string AuthorizationFailureType,
    string AuthorizationFailureMessage,
    int AuthorizationDeadlineMilliseconds,
    long AuthorizationElapsedMicroseconds,
    int CompletedRoundTripCount,
    string TargetSha256,
    string HookSha256,
    long ForwardedUpdateSubresourceCount,
    long SkippedUpdateSubresourceCount,
    ulong ForwardedUpdateSubresourceBytes,
    bool ContentEquivalent,
    bool RollbackRestored,
    string FallbackAction,
    UpdateUploadElisionRunReport BaselineFallback)
{
    private const int LegacyUpdateCount = 3;

    public static GatewayUpdateUploadFailClosedReport Build(
        Exception authorizationFailure,
        UpdateUploadElisionRunReport baselineFallback,
        string targetSha256,
        string hookSha256)
    {
        ArgumentNullException.ThrowIfNull(authorizationFailure);
        ArgumentNullException.ThrowIfNull(baselineFallback);
        var candidateActionCount = baselineFallback.RedundantUpdateCandidateCount;
        var expectedDirectUpdateCount = checked(
            candidateActionCount + UpdateUploadElisionLabOptions.RequiredUpdateCount);
        var expectedForwardedUpdateCount = checked(
            expectedDirectUpdateCount + LegacyUpdateCount);
        if (baselineFallback.Optimized ||
            baselineFallback.GatewayAuthorization is not null ||
            baselineFallback.PublishedPolicyEpoch != 0 ||
            baselineFallback.AcknowledgedPolicyEpoch != 0 ||
            baselineFallback.AppliedPolicyActions != 0 ||
            baselineFallback.PublishedPolicyExpiresAtQpc != 0 ||
            baselineFallback.PublishedPolicyActionMask != 0 ||
            baselineFallback.PublishedPolicyActionBudget != 0 ||
            baselineFallback.PolicyStatus != "none" ||
            candidateActionCount is < 1 or
                > (long)HookRingReader.MaxControlActionBudget ||
            baselineFallback.DirectUploadUpdateCount !=
                expectedDirectUpdateCount ||
            baselineFallback.ForwardedUpdateSubresourceCount !=
                expectedForwardedUpdateCount ||
            baselineFallback.SkippedUpdateSubresourceCount != 0 ||
            baselineFallback.LostSequenceCount != 0 ||
            baselineFallback.NativeOverrunCount != 0 ||
            !baselineFallback.ContentEquivalent ||
            !baselineFallback.RollbackRestored)
        {
            throw new InvalidDataException(
                "Gateway authorization fallback did not complete the baseline contract.");
        }
        if (targetSha256.Length != 64 ||
            hookSha256.Length != 64 ||
            targetSha256.Any(character => !Uri.IsHexDigit(character)) ||
            hookSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Target and hook SHA-256 evidence is required.");
        }

        var details = authorizationFailure as
            GatewayUpdateUploadAuthorizationFailureException;
        return new GatewayUpdateUploadFailClosedReport(
            Mode: "fluidruntime-gateway-update-upload-fail-closed-v0.18.0",
            TargetOwned: true,
            CooperativeLoad: true,
            RemoteInjection: false,
            AuthorizationAccepted: false,
            NativePolicyPublished: false,
            BaselineFallbackCompleted: true,
            FailureStage: "gateway-authorization-before-target-launch",
            AuthorizationFailureType: details?.FailureType ??
                (authorizationFailure is OperationCanceledException
                    ? nameof(TimeoutException)
                    : authorizationFailure.GetType().Name),
            AuthorizationFailureMessage: authorizationFailure.Message,
            AuthorizationDeadlineMilliseconds: details?.DeadlineMilliseconds ?? 0,
            AuthorizationElapsedMicroseconds: details?.ElapsedMicroseconds ?? 0,
            CompletedRoundTripCount: details?.CompletedRoundTrips ?? 0,
            targetSha256,
            hookSha256,
            baselineFallback.ForwardedUpdateSubresourceCount,
            baselineFallback.SkippedUpdateSubresourceCount,
            baselineFallback.ForwardedUpdateSubresourceBytes,
            baselineFallback.ContentEquivalent,
            baselineFallback.RollbackRestored,
            FallbackAction: "forward-all-update-subresource-calls",
            baselineFallback);
    }
}

public sealed class GatewayUpdateUploadAuthorizationDeniedException(
    Exception authorizationFailure,
    GatewayUpdateUploadFailClosedReport failClosedReport)
    : Exception(
        "FluidGateway authorization failed; the owned workload completed through " +
        "the unmodified baseline path.",
        authorizationFailure)
{
    public GatewayUpdateUploadFailClosedReport FailClosedReport { get; } =
        failClosedReport;
}
