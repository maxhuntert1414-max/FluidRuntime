using FluidLink;
using FluidRuntime.Cli;
using FluidRuntime.Native;
using FluidRuntime.Runtime;

namespace FluidRuntime.Tests;

public sealed class D3D12CopyElisionTests
{
    private static readonly string BinarySha256 = new('a', 64);
    private static readonly string PeerSha256 = new('b', 64);
    private static readonly DateTimeOffset PeerStartedAt =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Options_preserve_the_bounded_D3D12_profile()
    {
        var options = GatewayD3D12CopyLabOptions.Parse(
        [
            "gateway-d3d12-copy-lab",
            "--target", "target.exe",
            "--hook", "hook.dll",
            "--out", "report.json",
            "--gateway-pid", "42",
            "--gateway-executable-sha256", PeerSha256,
            "--trial-pairs", "12",
            "--warmup-pairs", "2",
            "--candidate-action-count", "127",
            "--hardware", "true"
        ]);

        Assert.Equal("127.0.0.1", options.Host);
        Assert.Equal(12, options.TrialPairs);
        Assert.Equal(2, options.WarmupPairs);
        Assert.Equal(127, options.CandidateActionCount);
        Assert.True(options.UseHardware);
        Assert.Equal(4UL * 1024 * 1024, GatewayD3D12CopyLabOptions.BufferBytes);
        Assert.Equal(
            8UL * 1024 * 1024,
            GatewayD3D12CopyLabOptions.SourceSnapshotBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(129)]
    public void Options_reject_D3D12_action_counts_outside_the_native_bound(int count)
    {
        Assert.Throws<ArgumentException>(() => GatewayD3D12CopyLabOptions.Parse(
        [
            "gateway-d3d12-copy-lab",
            "--target", "target.exe",
            "--hook", "hook.dll",
            "--out", "report.json",
            "--gateway-pid", "42",
            "--gateway-executable-sha256", PeerSha256,
            "--candidate-action-count", count.ToString()
        ]));
    }

    [Fact]
    public void D3D12_authorization_binds_the_dedicated_native_action_and_scope()
    {
        var evidence = BuildAuthorization();

        Assert.Equal(GatewayUploadBackend.D3D12CopyBufferRegion, evidence.Backend);
        Assert.Equal(
            HookRingReader.SkipRedundantD3D12CopyBufferRegionAction,
            evidence.NativeActionMask);
        Assert.Contains("owned-d3d12", evidence.AuthorizationScope);
        Assert.Contains(
            evidence.NativeSafetyGuards,
            item => item.Contains("completion fence", StringComparison.Ordinal));
        Assert.Contains(
            evidence.NativeSafetyGuards,
            item => item.Contains("unmodeled writes", StringComparison.Ordinal));
        evidence.EnsureMatchesNativePolicy(
            GatewayD3D12CopyLabOptions.BufferBytes,
            128,
            0,
            "measured",
            BinarySha256,
            BinarySha256,
            GatewayUploadBackend.D3D12CopyBufferRegion);
    }

    [Fact]
    public void D3D12_authorization_cannot_be_replayed_as_D3D11_policy()
    {
        var evidence = BuildAuthorization();

        Assert.Throws<InvalidDataException>(() => evidence.EnsureMatchesNativePolicy(
            GatewayD3D12CopyLabOptions.BufferBytes,
            128,
            0,
            "measured",
            BinarySha256,
            BinarySha256));
    }

    [Fact]
    public void D3D12_context_is_domain_separated_from_D3D11()
    {
        var d3d12 = Request();
        var d3d11 = d3d12 with
        {
            Backend = GatewayUploadBackend.D3D11UpdateSubresource
        };

        Assert.NotEqual(
            Context(d3d12, HookRingReader.SkipRedundantD3D12CopyBufferRegionAction),
            Context(d3d11, HookRingReader.SkipRedundantUpdateSubresourceAction));
    }

    [Fact]
    public void D3D12_event_flags_expose_exact_skip_and_invalidation_semantics()
    {
        var copy = new HookIpcEvent(
            0,
            1,
            HookEventType.D3D12CopyBufferRegion,
            7,
            10,
            20,
            GatewayD3D12CopyLabOptions.BufferBytes,
            1,
            Flags: 1 | 2 | 64 | 128);
        var invalidation = copy with
        {
            Type = HookEventType.D3D12ResourceInvalidate,
            Flags = 256
        };
        var automaticInvalidation = invalidation with { Flags = 0 };

        Assert.True(copy.IsD3D12RedundantCandidate);
        Assert.True(copy.WasD3D12CopySkipped);
        Assert.True(copy.IsD3D12ExactContentCompared);
        Assert.True(copy.IsD3D12ImmutableUploadSource);
        Assert.True(invalidation.IsD3D12ExplicitInvalidation);
        Assert.False(automaticInvalidation.IsD3D12ExplicitInvalidation);
    }

    [Fact]
    public async Task D3D12_command_exposes_help_without_starting_native_work()
    {
        Assert.Equal(
            0,
            await GatewayD3D12CopyLabCommand.RunAsync(
                ["gateway-d3d12-copy-lab", "--help"]));
    }

    private static GatewayUpdateUploadAuthorization BuildAuthorization()
    {
        var request = Request();
        var context = Context(
            request,
            HookRingReader.SkipRedundantD3D12CopyBufferRegionAction);
        return FluidLinkGatewayUpdateUploadAuthorizer.BuildAuthorization(
            request,
            new FluidLinkV2Welcome(
                FluidLinkV2BatchProtocol.ContractSha256,
                "00112233445566778899aabbccddeeff",
                "fluidgateway",
                "0.67.0",
                FluidLinkV2BatchProtocol.AllCapabilities,
                FluidLinkV2BatchProtocol.AllCapabilities,
                FluidLinkV2Protocol.MaxPayloadBytes),
            heartbeat: "nonce",
            expectedHeartbeat: "nonce",
            runtimeSessionId: $"gateway-d3d12-copy-{context}",
            authorizationContextSha256: context,
            new WindowsLoopbackPeerIdentity(
                42,
                Path.GetFullPath("gateway.exe"),
                PeerSha256,
                PeerStartedAt),
            authorizationDeadlineMilliseconds: 5000,
            new FluidLinkV2RuntimeDecision(
                FluidLinkV2EventOpcode.Operation,
                FluidLinkV2DecisionOpcode.Execute,
                FluidLinkV2DecisionStatus.Accepted |
                    FluidLinkV2DecisionStatus.HasExecutionState |
                    FluidLinkV2DecisionStatus.Executed,
                0,
                0),
            Enumerable.Range(0, 128).Select(_ =>
                new FluidLinkV2RuntimeDecision(
                    FluidLinkV2EventOpcode.Operation,
                    FluidLinkV2DecisionOpcode.DeduplicateIdenticalTransfer,
                    FluidLinkV2DecisionStatus.Accepted |
                        FluidLinkV2DecisionStatus.HasExecutionState,
                    0,
                    GatewayD3D12CopyLabOptions.BufferBytes)).ToArray(),
            roundTripCount: 10,
            bytesSent: 4096,
            bytesReceived: 4096,
            authorizationLatencyMicroseconds: 1000);
    }

    private static GatewayUpdateUploadAuthorizationRequest Request() =>
        new(
            0,
            "measured",
            GatewayD3D12CopyLabOptions.BufferBytes,
            128,
            BinarySha256,
            BinarySha256,
            GatewayUploadBackend.D3D12CopyBufferRegion);

    private static string Context(
        GatewayUpdateUploadAuthorizationRequest request,
        ulong actionMask) =>
        FluidLinkGatewayUpdateUploadAuthorizer.ComputeAuthorizationContextSha256(
            "nonce",
            42,
            PeerSha256,
            PeerStartedAt,
            request,
            actionMask,
            request.CandidateActionCount);
}
