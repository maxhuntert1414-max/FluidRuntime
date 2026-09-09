using System.Diagnostics;
using FluidRuntime.Cli;

namespace FluidRuntime.Runtime;

public sealed class GatewayReadbackLabRunner
{
    public async Task<GatewayReadbackLabReport> RunAsync(
        ReadbackElisionLabOptions options,
        IGatewayUpdateUploadAuthorizer authorizer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authorizer);
        if (options.TrialPairs is < 1 or > 30 || options.WarmupPairs is < 0 or > 5)
            throw new ArgumentException("Readback requires 1-30 measured and 0-5 warmup pairs.");
        using var binding = OwnedBinaryBinding.Open(options.TargetPath, options.HookPath);
        var trials = new List<ReadbackElisionTrialReport>();
        var authorizations = new List<GatewayUpdateUploadAuthorization>();
        var contexts = new HashSet<string>(StringComparer.Ordinal);
        var topology = NativeTransferTopology.D3D11SingleLane(ReadbackElisionLabOptions.RedundantCopyCount);
        for (var index = 0; index < options.WarmupPairs + options.TrialPairs; ++index)
        {
            var measured = index >= options.WarmupPairs;
            var pair = measured ? index - options.WarmupPairs : index;
            var phase = measured ? "measured" : "warmup";
            var baselineFirst = pair % 2 == 0;
            ReadbackElisionRunReport? baseline = baselineFirst ? await Run(false) : null;
            var startedAt = Stopwatch.GetTimestamp();
            GatewayUpdateUploadAuthorization authorization;
            try
            {
                authorization = await authorizer.AuthorizeAsync(new(
                    pair, phase, ReadbackElisionLabOptions.ReadbackBufferBytes,
                    ReadbackElisionLabOptions.RedundantCopyCount, binding.TargetSha256,
                    binding.HookSha256, GatewayUploadBackend.D3D11ReadbackCopy, topology), cancellationToken);
                authorization.EnsureMatchesNativePolicy(
                    ReadbackElisionLabOptions.ReadbackBufferBytes, ReadbackElisionLabOptions.RedundantCopyCount,
                    pair, phase, binding.TargetSha256, binding.HookSha256,
                    GatewayUploadBackend.D3D11ReadbackCopy, topology);
                if (!contexts.Add(authorization.AuthorizationContextSha256))
                    throw new InvalidDataException("A readback authorization context was reused.");
                if (authorizations.Count > 0) ValidateIdentity(authorizations[0], authorization);
            }
            catch (Exception error) when (
                error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                baseline ??= await Run(false);
                throw new GatewayReadbackDeniedException(error, new(
                    "gateway-readback-fail-closed-v1", true, false, trials.Count,
                    error.GetType().Name, error.Message, binding.TargetSha256, binding.HookSha256, baseline));
            }
            var optimized = await Run(true, authorization);
            optimized = optimized with
            {
                ManagedEndToEndMicroseconds = checked((long)Math.Ceiling(
                    Stopwatch.GetElapsedTime(startedAt).TotalMicroseconds))
            };
            baseline ??= await Run(false);
            authorizations.Add(authorization);
            trials.Add(new(pair, phase, measured,
                baselineFirst ? "baseline-then-optimized" : "optimized-then-baseline",
                baseline.ContentEquivalent && optimized.ContentEquivalent,
                baseline.RollbackRestored && optimized.RollbackRestored,
                ReadbackElisionLabRunner.SameAdapter(baseline, optimized),
                baseline.CpuWorkloadMicroseconds, optimized.CpuWorkloadMicroseconds,
                baseline.GpuWorkloadMicroseconds, optimized.GpuWorkloadMicroseconds, baseline, optimized));

            Task<ReadbackElisionRunReport> Run(bool optimized, GatewayUpdateUploadAuthorization? authorization = null) =>
                ReadbackElisionLabRunner.RunOneAsync(options, binding.TargetPath, binding.HookPath,
                    optimized, cancellationToken, binding, authorization);
        }
        var native = ReadbackElisionLabRunner.BuildReport(trials, options);
        var measuredTrials = trials.Where(item => item.IncludedInStatistics).ToArray();
        return new("gateway-readback-lab-v1", true, binding.TargetSha256, binding.HookSha256,
            native, authorizations.AsReadOnly(), GatewayLatencyStatistics.SummarizePairs(
                measuredTrials.Select(item => item.Baseline.ManagedEndToEndMicroseconds),
                measuredTrials.Select(item => item.Optimized.ManagedEndToEndMicroseconds)));
    }

    internal static void ValidateIdentity(GatewayUpdateUploadAuthorization first, GatewayUpdateUploadAuthorization next)
    {
        if (first.GatewayBackend != next.GatewayBackend || first.GatewayLibrary != next.GatewayLibrary ||
            first.PeerProcessId != next.PeerProcessId || first.PeerExecutableSha256 != next.PeerExecutableSha256 ||
            first.PeerProcessStartedAtUtc != next.PeerProcessStartedAtUtc ||
            first.AdvertisedServerVersion != next.AdvertisedServerVersion)
            throw new InvalidDataException("Gateway process, backend or DLL identity changed between readback pairs.");
    }
}
