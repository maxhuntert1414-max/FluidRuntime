using System.Text.Json;
using System.Text.Json.Nodes;
using FluidRuntime.Cli;
using FluidRuntime.Native;
using FluidRuntime.Runtime;

namespace FluidRuntime.Tests;

public sealed class VulkanCopyLabTests
{
    private static readonly string Sha = new('a', 64);
    private static string[] Arguments => ["gateway-vulkan-copy-lab", "--target", "target.exe",
        "--library", "library.dll", "--out", "report.json", "--gateway-pid", "42",
        "--gateway-executable-sha256", Sha];

    [Fact]
    public void Options_and_numeric_profile_are_explicitly_Vulkan()
    {
        var options = GatewayVulkanCopyLabOptions.Parse(Arguments);
        Assert.True(options.UseHardware);
        Assert.False(options.RequireValidation);
        Assert.Equal(128, options.CandidateActionCount);
        Assert.Equal(10, options.TrialPairs);
        Assert.Equal(1, options.WarmupPairs);
        Assert.Equal(3, (int)NativeTransferDescriptors.VulkanCopyBuffer.Backend);
        Assert.Equal(2, (int)NativeTransferDescriptors.VulkanCopyBuffer.Operation);
        Assert.Equal(new NativeTransferTopology(1, 2, 2, 2, 2, 1, 145), options.CreateTransferTopology());
        var profile = GatewayUploadAuthorizationProfiles.For(GatewayUploadBackend.VulkanCopyBuffer);
        Assert.Equal(16UL, profile.NativeActionMask);
        Assert.Contains("owned-vulkan", profile.AuthorizationScope);
    }

    [Theory]
    [InlineData("--candidate-action-count", "0")]
    [InlineData("--candidate-action-count", "129")]
    [InlineData("--trial-pairs", "31")]
    [InlineData("--warmup-pairs", "6")]
    [InlineData("--port", "0")]
    [InlineData("--timeout-ms", "99")]
    [InlineData("--gpu-timeout-ms", "30001")]
    [InlineData("--hardware", "maybe")]
    [InlineData("--validation", "yes")]
    [InlineData("--target", "duplicate.exe")]
    [InlineData("--host", "0.0.0.0")]
    public void Options_reject_ambiguous_or_unbounded_authority(string key, string value) =>
        Assert.Throws<ArgumentException>(() => GatewayVulkanCopyLabOptions.Parse([.. Arguments, key, value]));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(127)]
    [InlineData(128)]
    public void Events_prove_each_lane_and_all_forwarded_rollback(int count)
    {
        foreach (var optimized in new[] { false, true })
        {
            var events = Events(count, optimized);
            VulkanCopyLabRunner.ValidateEvents(events, count, optimized, 10000);
            Assert.Equal(count + (optimized ? 27 : 26), events.Length);
            Assert.Equal(optimized ? count : 0, events.Count(item => item.WasTransferSkipped));
            Assert.Equal(count + 2, events.Count(item => item.IsTransferRedundantCandidate));
            Assert.Equal(2, events.Count(item => item.Type == HookEventType.TransferScopeReset));
            Assert.Equal(2, events.Count(item => item.Type == HookEventType.TransferSyncSignal));
        }
    }

    [Fact]
    public void Events_reject_tampering_at_every_position()
    {
        var events = Events(16, true);
        for (var index = 0; index < events.Length; ++index)
        {
            var changed = events.ToArray();
            changed[index] = changed[index] with { ResourceB = 9999 };
            Assert.Throws<InvalidDataException>(() => VulkanCopyLabRunner.ValidateEvents(changed, 16, true, 10000));
        }
        Assert.Throws<InvalidDataException>(() => VulkanCopyLabRunner.ValidateEvents(events[..^1], 16, true, 10000));
        Assert.Throws<InvalidDataException>(() => VulkanCopyLabRunner.ValidateEvents(events, 16, false, 10000));
        Assert.Throws<InvalidDataException>(() => VulkanCopyLabRunner.ValidateEvents(events, 16, true, 9999));
    }

    [Fact]
    public void Vulkan_authorization_is_domain_separated_and_topology_bound()
    {
        var request = new GatewayUpdateUploadAuthorizationRequest(0, "measured", 4194304, 128, Sha, Sha,
            GatewayUploadBackend.VulkanCopyBuffer, NativeTransferTopology.VulkanMultiLane(128));
        string Context(GatewayUpdateUploadAuthorizationRequest input) =>
            FluidLinkGatewayUpdateUploadAuthorizer.ComputeAuthorizationContextSha256("nonce", 42, Sha,
                DateTimeOffset.Parse("2026-09-04T00:00:00Z"), input, 16, 128);
        Assert.NotEqual(Context(request), Context(request with { Backend = GatewayUploadBackend.D3D12CopyBufferRegion }));
        Assert.NotEqual(Context(request), Context(request with { Topology = request.Topology! with { QueueCount = 2 } }));
        Assert.NotEqual(Context(request), Context(request with { HookSha256 = new string('b', 64) }));
        Assert.Throws<ArgumentException>(() => Context(request with { Topology = null }));
    }

    private static HookIpcEvent[] Events(int count, bool optimized) =>
        VulkanCopyLabRunner.ExpectedEvents(count, optimized, 10000)
            .Select((item, index) => item with { QpcTicks = index + 1, ThreadId = 42 }).ToArray();

    private const string Evidence = """
    {
        "schema":"fluidruntime-vulkan-transfer-v1", "mode":"managed", "process_id":42,
        "backend":3, "operation":2, "target_owned":true, "cooperative_library":true,
        "remote_injection":false, "self_published_control":false, "actuation_enabled":true,
        "device_name":"Test GPU", "api_version":4194304, "device_type":2,
        "validation_enabled":false, "validation_errors":0, "validation_warnings":0,
        "loader_messages":0,
        "upload_memory_flags":2, "device_memory_flags":1, "readback_memory_flags":2,
        "buffer_bytes":4194304, "lane_count":2, "queue_count":1, "fence_count":1,
        "source_snapshot_bytes":16777216, "candidate_count":128, "tracked_copies":136,
        "forwarded_copies":8, "skipped_copies":128, "avoided_logical_bytes":536870912,
        "physical_transfer_bytes_measured":false, "content_equivalent":true, "rollback_verified":true,
        "source_mutation_guard_verified":true, "invalidation_guards_verified":true,
        "comparisons":130, "invalidations":4, "policy_epoch":1, "policy_status":4,
        "applied_actions":128, "events":155, "overruns":0, "completed_submissions":2,
        "rollback_forwarded_copies":4, "native_workload_us":1000, "submit_to_fence_us":100,
        "gpu_timestamp_valid":true, "gpu_timestamp_valid_bits":64, "gpu_us":90,
        "pattern_hashes":["1111111111111111","2222222222222222","3333333333333333","4444444444444444"],
        "final_hashes":["2222222222222222","4444444444444444"]
    }
    """;

    [Fact]
    public void Native_evidence_requires_complete_readback_control_and_timing_proof()
    {
        var options = GatewayVulkanCopyLabOptions.Parse(Arguments);
        var evidence = JsonNode.Parse(Evidence)!.AsObject();
        VulkanCopyLabRunner.ValidateNativeEvidence(JsonSerializer.SerializeToElement(evidence), options, true, 42);
        foreach (var key in evidence.Select(pair => pair.Key).ToArray())
        {
            var changed = evidence.DeepClone().AsObject();
            changed.Remove(key);
            Assert.ThrowsAny<Exception>(() => VulkanCopyLabRunner.ValidateNativeEvidence(
                JsonSerializer.SerializeToElement(changed), options, true, 42));
        }
        Assert.Throws<InvalidDataException>(() => VulkanCopyLabRunner.ValidateNativeEvidence(
            JsonSerializer.SerializeToElement(evidence), options, false, 42));
        Assert.Throws<InvalidDataException>(() => VulkanCopyLabRunner.ValidateNativeEvidence(
            JsonSerializer.SerializeToElement(evidence), options, true, 43));
    }

    [Theory]
    [InlineData("backend", "2")]
    [InlineData("skipped_copies", "127")]
    [InlineData("validation_errors", "1")]
    [InlineData("content_equivalent", "false")]
    [InlineData("rollback_verified", "false")]
    [InlineData("physical_transfer_bytes_measured", "true")]
    [InlineData("self_published_control", "true")]
    [InlineData("native_workload_us", "-1")]
    [InlineData("gpu_timestamp_valid_bits", "65")]
    public void Native_evidence_rejects_invalid_claims(string key, string value)
    {
        var evidence = JsonNode.Parse(Evidence)!.AsObject();
        evidence[key] = JsonNode.Parse(value);
        Assert.Throws<InvalidDataException>(() => VulkanCopyLabRunner.ValidateNativeEvidence(
            JsonSerializer.SerializeToElement(evidence), GatewayVulkanCopyLabOptions.Parse(Arguments), true, 42));
    }
}
