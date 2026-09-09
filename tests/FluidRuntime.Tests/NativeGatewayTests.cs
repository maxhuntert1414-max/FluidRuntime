using System.Security.Cryptography;
using FluidLink;
using FluidRuntime.Cli;
using FluidRuntime.Native;
using FluidRuntime.Runtime;

namespace FluidRuntime.Tests;

public sealed class GatewayDllFactAttribute : FactAttribute
{
    public GatewayDllFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Environment.GetEnvironmentVariable("FLUIDGATEWAY_DLL")))
            Skip = "Set FLUIDGATEWAY_DLL to the built Release DLL for native integration tests.";
    }
}

[CollectionDefinition("Gateway DLL", DisableParallelization = true)]
public sealed class GatewayDllCollection { }

[Collection("Gateway DLL")]
public sealed class NativeGatewayTests
{
    private static string Dll => Environment.GetEnvironmentVariable("FLUIDGATEWAY_DLL")!;
    private static string Hash => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Dll)));
    private static NativeGatewayLibrary Load() => new(Dll, Hash);
    private static readonly string TestHash = new('a', 64);

    [Fact]
    public void Options_are_explicit_and_do_not_mix_identities()
    {
        string[] Base(string command, string hookFlag) => [command, "--target", "owned.exe", hookFlag, "hook.dll",
            "--out", "out.json", "--gateway-backend", "inprocess", "--gateway-library", @"C:\Gateway.dll",
            "--gateway-library-sha256", TestHash];
        var d11 = GatewayUpdateUploadLabOptions.Parse(Base("gateway-update-upload-lab", "--hook"));
        var d12 = GatewayD3D12CopyLabOptions.Parse(Base("gateway-d3d12-copy-lab", "--hook"));
        var vk = GatewayVulkanCopyLabOptions.Parse(Base("gateway-vulkan-copy-lab", "--library"));
        Assert.Equal("inprocess", d11.GatewayConnection.Mode);
        Assert.Equal(d11.GatewayConnection, d12.GatewayConnection);
        Assert.Equal(d11.GatewayConnection, vk.GatewayConnection);
        Assert.Throws<ArgumentException>(() => GatewayUpdateUploadLabOptions.Parse(
            [.. Base("gateway-update-upload-lab", "--hook"), "--gateway-pid", "42"]));
        Assert.Throws<ArgumentException>(() => GatewayVulkanCopyLabOptions.Parse(
            [.. Base("gateway-vulkan-copy-lab", "--library"), "--port", "9999"]));
        Assert.Throws<ArgumentException>(() => GatewayD3D12CopyLabOptions.Parse(
            [.. Base("gateway-d3d12-copy-lab", "--hook"), "--gateway-backend", "server"]));
    }

    [GatewayDllFact]
    public async Task Library_is_pinned_and_live_session_defers_unload()
    {
        Assert.Throws<InvalidDataException>(() => new NativeGatewayLibrary(Dll, TestHash));
        using var module = Load();
        var transport = module.CreateTransport();
        await using var client = new FluidLinkV2Client(transport);
        await client.HandshakeBatchAsync("integration", "1");
        var first = client.SessionId;
        Assert.Null(client.LocalEndPoint);
        Assert.Throws<IOException>(() => File.Open(Dll, FileMode.Open, FileAccess.Write, FileShare.Read));
        module.Dispose();
        Assert.Equal("alive", await client.PingAsync("alive"));
        Assert.Equal(2UL, transport.ReadMetrics().Exchanges);
        await client.GoodbyeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.HandshakeBatchAsync("integration", "1"));
        Assert.NotNull(first);
    }

    [GatewayDllFact]
    public async Task Sessions_are_bounded_and_reconnect_has_fresh_identity()
    {
        using var module = Load();
        var transports = Enumerable.Range(0, 8).Select(_ => module.CreateTransport()).ToArray();
        try
        {
            foreach (var transport in transports) await transport.ConnectAsync(default);
            using var overflow = module.CreateTransport();
            var error = await Assert.ThrowsAsync<NativeGatewayException>(async () => await overflow.ConnectAsync(default));
            Assert.Equal(4U, error.Status);
            transports[0].Abort();
            await overflow.ConnectAsync(default);
        }
        finally { foreach (var transport in transports) transport.Dispose(); }

        await using var client = new FluidLinkV2Client(module.CreateTransport());
        await client.HandshakeBatchAsync("integration", "1");
        var first = client.SessionId;
        await client.GoodbyeAsync();
        await client.HandshakeBatchAsync("integration", "1");
        Assert.NotEqual(first, client.SessionId);
    }

    [GatewayDllFact]
    public async Task Truncation_and_cancellation_never_leave_authority()
    {
        using var module = Load();
        using var transport = module.CreateTransport();
        await transport.ConnectAsync(default);
        var error = await Assert.ThrowsAsync<NativeGatewayException>(async () =>
            await transport.ExchangeAsync(new byte[] { 1, 2 }, default));
        Assert.Equal(7U, error.Status);
        Assert.False(transport.IsConnected);
        await transport.ConnectAsync(default);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await transport.ExchangeAsync(new byte[] { 1 }, cancelled.Token));
        Assert.False(transport.IsConnected);
    }

    [GatewayDllFact]
    public async Task Real_authorizations_preserve_all_native_gates_and_bind_dll_identity()
    {
        using var module = Load();
        using var authorizer = new FluidLinkGatewayUpdateUploadAuthorizer(module, TimeSpan.FromSeconds(5));
        foreach (var backend in Enum.GetValues<GatewayUploadBackend>())
        {
            var topology = backend switch
            {
                GatewayUploadBackend.D3D12CopyBufferRegion => NativeTransferTopology.D3D12MultiLane(128),
                GatewayUploadBackend.VulkanCopyBuffer => NativeTransferTopology.VulkanMultiLane(128),
                GatewayUploadBackend.D3D11ReadbackCopy => NativeTransferTopology.D3D11SingleLane(128),
                _ => null
            };
            var request = new GatewayUpdateUploadAuthorizationRequest(0, "measured", 4194304, 128,
                TestHash, TestHash, backend, topology);
            var result = await authorizer.AuthorizeAsync(request);
            void Validate(GatewayUpdateUploadAuthorization value) => value.EnsureMatchesNativePolicy(
                request.ResourceBytes, 128, 0, "measured", TestHash, TestHash, backend, topology);
            Validate(result);
            if (backend == GatewayUploadBackend.D3D11ReadbackCopy)
            {
                Assert.Equal(FluidLinkV2OperationType.Copy, result.OperationType);
                Assert.Equal(FluidLinkV2MemoryLayer.Vram, result.SourceMemoryLayer);
                Assert.Equal(FluidLinkV2MemoryLayer.Ram, result.DestinationMemoryLayer);
                Assert.Equal(HookRingReader.SkipRedundantReadbackCopyAction, result.NativeActionMask);
                Assert.True(result.SeedTransferExecuted);
                Assert.Throws<InvalidDataException>(() => Validate(result with
                {
                    NativeActionMask = HookRingReader.SkipRedundantUpdateSubresourceAction
                }));
            }
            Assert.Equal("inprocess", result.GatewayBackend);
            Assert.Equal(0, result.TransportRoundTripCount);
            Assert.Equal(10, result.WireExchangeCount);
            Assert.Equal(Environment.ProcessId, result.PeerProcessId);
            Assert.Equal(module.Identity, result.GatewayLibrary);
            Assert.DoesNotContain(result.NativeSafetyGuards, text => text.Contains("TCP owner"));
            Assert.Throws<InvalidDataException>(() => Validate(result with { GatewayBackend = "server" }));
            Assert.Throws<InvalidDataException>(() => Validate(result with { GatewayLibrary = null }));
            Assert.Throws<InvalidDataException>(() => Validate(result with
            {
                GatewayLibrary = module.Identity with { Sha256 = TestHash }
            }));
        }
    }

    [GatewayDllFact]
    public async Task Concurrent_clients_keep_contexts_and_budgets_independent()
    {
        using var module = Load();
        using var authorizer = new FluidLinkGatewayUpdateUploadAuthorizer(module, TimeSpan.FromSeconds(5));
        var report = await new GatewayAuthorizationConcurrencyBenchmarkRunner().RunAsync(
            new(128, 8, 32, 1000), authorizer, TestHash, TestHash);
        Assert.True(report.ReliabilityGatePassed);
        Assert.True(report.ContextsUnique);
        Assert.Equal(0, report.TotalRoundTripCount);
        Assert.False(report.SharedMemoryPrototypeJustified);
        Assert.Equal("inprocess", report.GatewayBackend);
    }
}
