using System.Security.Cryptography;
using FluidRuntime.Cli;
using FluidRuntime.Native;
using FluidRuntime.Runtime;

namespace FluidRuntime.Tests;

public sealed class GatewayReadbackTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static string[] Base => ["gateway-readback-lab", "--target", "owned.exe", "--hook", "hook.dll", "--out", "report.json"];
    private static string[] Server => [.. Base, "--gateway-pid", "42", "--gateway-executable-sha256", Hash];

    [Fact]
    public void Options_preserve_server_default_and_require_explicit_pinned_dll()
    {
        var server = GatewayReadbackLabOptions.Parse(Server);
        Assert.Equal("server", server.Connection.Mode);
        Assert.Equal(42, server.GatewayProcessId);
        var embedded = GatewayReadbackLabOptions.Parse([.. Base, "--gateway-backend", "inprocess",
            "--gateway-library", Path.GetFullPath("Gateway.dll"), "--gateway-library-sha256", Hash]);
        Assert.Equal("inprocess", embedded.Connection.Mode);
        Assert.Equal(64, ReadbackElisionLabOptions.RedundantCopyCount);
        Assert.Throws<ArgumentException>(() => GatewayReadbackLabOptions.Parse(Base));
        Assert.Throws<ArgumentException>(() => GatewayReadbackLabOptions.Parse([.. Server,
            "--gateway-backend", "inprocess", "--gateway-library", Path.GetFullPath("Gateway.dll"),
            "--gateway-library-sha256", Hash]));
    }

    [Theory]
    [InlineData("--host", "0.0.0.0")]
    [InlineData("--port", "65536")]
    [InlineData("--timeout-ms", "0")]
    [InlineData("--trial-pairs", "31")]
    [InlineData("--warmup-pairs", "6")]
    [InlineData("--gateway-pid", "42")]
    [InlineData("--candidate-action-count", "128")]
    [InlineData("--gateway-backend", "automatic")]
    public void Options_reject_unbounded_ambiguous_or_unsupported_values(string option, string value) =>
        Assert.Throws<ArgumentException>(() => GatewayReadbackLabOptions.Parse([.. Server, option, value]));

    [GatewayReadbackFact]
    public async Task Wrong_action_mask_runs_original_readback_without_publishing_a_policy()
    {
        using var module = Load();
        using var real = new FluidLinkGatewayUpdateUploadAuthorizer(module, TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<GatewayReadbackDeniedException>(() =>
            new GatewayReadbackLabRunner().RunAsync(Options(), new AlteredAuthorizer(real)));
        Assert.Equal(0, error.Report.CompletedTrialPairs);
        Assert.False(error.Report.RejectedRunPolicyPublished);
        Assert.True(error.Report.FailClosed);
        AssertOriginal(error.Report.BaselineFallback);
    }

    [GatewayReadbackFact]
    public async Task Disposed_dll_authorizer_cannot_fall_back_to_local_authority()
    {
        using var module = Load();
        using var authorizer = new FluidLinkGatewayUpdateUploadAuthorizer(module, TimeSpan.FromSeconds(5));
        authorizer.Dispose();
        var error = await Assert.ThrowsAsync<GatewayReadbackDeniedException>(() =>
            new GatewayReadbackLabRunner().RunAsync(Options(), authorizer));
        Assert.False(error.Report.RejectedRunPolicyPublished);
        AssertOriginal(error.Report.BaselineFallback);
    }

    [GatewayDllFact]
    public async Task Direction_and_backend_identity_are_bound_to_authorization()
    {
        using var module = Load();
        using var authorizer = new FluidLinkGatewayUpdateUploadAuthorizer(module, TimeSpan.FromSeconds(5));
        var request = new GatewayUpdateUploadAuthorizationRequest(0, "measured", 4194304, 64,
            Hash, Hash, GatewayUploadBackend.D3D11ReadbackCopy, NativeTransferTopology.D3D11SingleLane(64));
        var first = await authorizer.AuthorizeAsync(request);
        var next = await authorizer.AuthorizeAsync(request with { PairIndex = 1 });
        GatewayReadbackLabRunner.ValidateIdentity(first, next);
        foreach (var changed in new[]
        {
            next with { GatewayBackend = "server" },
            next with { GatewayLibrary = null },
            next with { PeerProcessId = next.PeerProcessId + 1 },
            next with { PeerExecutableSha256 = Hash },
            next with { AdvertisedServerVersion = "different" }
        }) Assert.Throws<InvalidDataException>(() => GatewayReadbackLabRunner.ValidateIdentity(first, changed));
        Assert.Throws<InvalidDataException>(() => first.EnsureMatchesNativePolicy(
            4194304, 64, 0, "measured", Hash, Hash, GatewayUploadBackend.D3D11UpdateSubresource));
        await Assert.ThrowsAsync<ArgumentException>(() => authorizer.AuthorizeAsync(request with { ResourceBytes = ulong.MaxValue }));
    }

    private static void AssertOriginal(ReadbackElisionRunReport run)
    {
        Assert.False(run.Optimized);
        Assert.Null(run.GatewayAuthorization);
        Assert.Equal(0, run.PublishedPolicyEpoch);
        Assert.Equal(0, run.AppliedPolicyActions);
        Assert.Equal(0, run.SkippedReadbackCopyCount);
        Assert.Equal(65, run.ReadMapCount);
        Assert.True(run.ContentEquivalent);
        Assert.True(run.RollbackRestored);
    }

    private static NativeGatewayLibrary Load()
    {
        var path = Environment.GetEnvironmentVariable("FLUIDGATEWAY_DLL")!;
        return new(path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    private static ReadbackElisionLabOptions Options()
    {
        var directory = Environment.GetEnvironmentVariable("FLUIDRUNTIME_NATIVE")!;
        return new(Path.Combine(directory, "fluidruntime-hook-target.exe"),
            Path.Combine(directory, "fluidruntime-present-hook.dll"), "unused.json", 1, 0, 50, 5000, false);
    }

    private sealed class AlteredAuthorizer(IGatewayUpdateUploadAuthorizer original) : IGatewayUpdateUploadAuthorizer
    {
        public async Task<GatewayUpdateUploadAuthorization> AuthorizeAsync(
            GatewayUpdateUploadAuthorizationRequest request, CancellationToken cancellationToken = default) =>
            (await original.AuthorizeAsync(request, cancellationToken)) with
            { NativeActionMask = HookRingReader.SkipRedundantUpdateSubresourceAction };
    }
}

public sealed class GatewayReadbackFactAttribute : FactAttribute
{
    public GatewayReadbackFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Environment.GetEnvironmentVariable("FLUIDGATEWAY_DLL")) ||
            !File.Exists(Path.Combine(Environment.GetEnvironmentVariable("FLUIDRUNTIME_NATIVE") ?? "", "fluidruntime-hook-target.exe")))
            Skip = "Set FLUIDGATEWAY_DLL and FLUIDRUNTIME_NATIVE for real owned readback integration.";
    }
}
