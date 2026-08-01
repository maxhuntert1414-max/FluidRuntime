using FluidRuntime.Cli;
using FluidRuntime.Native;
using FluidRuntime.Runtime;

namespace FluidRuntime.Tests;

public sealed class D3D12ObservationLabTests
{
    [Fact]
    public void Options_use_bounded_observation_defaults()
    {
        var options = D3D12ObservationLabOptions.Parse(
        [
            "d3d12-observe-lab",
            "--target", "observe.exe",
            "--out", "report.json"
        ]);

        Assert.Equal(3, options.Runs);
        Assert.Equal(10000, options.GpuTimeoutMs);
        Assert.Equal(20000, options.ProcessTimeoutMs);
        Assert.False(options.UseHardware);
    }

    [Fact]
    public void Options_reject_unknown_values_and_an_unbounded_process_deadline()
    {
        Assert.Throws<ArgumentException>(() => D3D12ObservationLabOptions.Parse(
        [
            "d3d12-observe-lab",
            "--target", "observe.exe",
            "--out", "report.json",
            "--copies", "2"
        ]));
        Assert.Throws<ArgumentException>(() => D3D12ObservationLabOptions.Parse(
        [
            "d3d12-observe-lab",
            "--target", "observe.exe",
            "--out", "report.json",
            "--gpu-timeout-ms", "5000",
            "--process-timeout-ms", "5000"
        ]));
    }

    [Fact]
    public void Parser_accepts_the_fixed_owned_round_trip_contract()
    {
        var report = D3D12ObservationRunParser.Parse(ValidJson);

        Assert.True(report.TargetOwned);
        Assert.True(report.ReadOnlyObservation);
        Assert.False(report.ActuationEnabled);
        Assert.Equal(4UL * 1024UL * 1024UL, report.Transfer.BufferBytes);
        Assert.Equal(report.Transfer.SourceHash, report.Transfer.ReadbackHash);
        Assert.True(report.Transfer.ContentEquivalent);
    }

    [Theory]
    [InlineData("\"target_owned\": true", "\"target_owned\": false")]
    [InlineData(
        "\"physical_transfer_bytes_measured\": false",
        "\"physical_transfer_bytes_measured\": true")]
    [InlineData("\"debug_error_count\": 0", "\"debug_error_count\": 1")]
    [InlineData("\"debug_warning_count\": 0", "\"debug_warning_count\": 1")]
    [InlineData(
        "\"captured_at_unix_ms\": 1785628800000",
        "\"captured_at_unix_ms\": 0")]
    [InlineData("\"buffer_bytes\": 4194304", "\"buffer_bytes\": 4096")]
    [InlineData(
        "\"default_initial_state\": \"common\"",
        "\"default_initial_state\": \"copy-dest\"")]
    [InlineData("\"node_count\": 1,", "")]
    [InlineData("\"resource_barrier_count\": 1", "\"resource_barrier_count\": 0")]
    [InlineData(
        "\"readback_hash\": \"6b35601f05442325\"",
        "\"readback_hash\": \"0000000000000000\"")]
    public void Parser_rejects_contract_drift(string oldValue, string newValue)
    {
        var json = ValidJson.Replace(oldValue, newValue, StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() =>
            D3D12ObservationRunParser.Parse(json));
    }

    [Fact]
    public void Parser_rejects_unknown_fields()
    {
        var json = "{ \"unexpected\": true," + ValidJson[1..];

        Assert.Throws<InvalidDataException>(() =>
            D3D12ObservationRunParser.Parse(json));
    }

    [Fact]
    public void BuildReport_aggregates_observation_without_allowing_a_performance_claim()
    {
        var options = new D3D12ObservationLabOptions(
            "observe.exe",
            "report.json",
            Runs: 2,
            GpuTimeoutMs: 10000,
            ProcessTimeoutMs: 20000,
            UseHardware: false);
        var first = D3D12ObservationRunParser.Parse(ValidJson);
        var second = D3D12ObservationRunParser.Parse(
            ValidJson.Replace(
                "\"process_id\": 42",
                "\"process_id\": 43",
                StringComparison.Ordinal));

        var report = D3D12ObservationLabRunner.BuildReport(
            options,
            "observe.exe",
            new string('a', 64),
            [first, second]);

        Assert.Equal(2, report.CompletedRuns);
        Assert.True(report.AdapterIdentityStable);
        Assert.True(report.ContentEquivalentInAllRuns);
        Assert.True(report.FenceCompletedInAllRuns);
        Assert.False(report.PerformanceClaimAllowed);
        Assert.Contains(
            "logical-copy-bytes-not-physical-traffic",
            report.PerformanceClaimBlockers);
        Assert.Equal(2, report.TotalWorkloadMicroseconds.Count);
    }

    [Fact]
    public void BuildReport_rejects_adapter_identity_drift()
    {
        var options = new D3D12ObservationLabOptions(
            "observe.exe",
            "report.json",
            Runs: 2,
            GpuTimeoutMs: 10000,
            ProcessTimeoutMs: 20000,
            UseHardware: false);
        var first = D3D12ObservationRunParser.Parse(ValidJson);
        var second = D3D12ObservationRunParser.Parse(
            ValidJson.Replace(
                "\"luid\": \"00000000000105fa\"",
                "\"luid\": \"00000000000105fb\"",
                StringComparison.Ordinal));

        Assert.Throws<InvalidDataException>(() =>
            D3D12ObservationLabRunner.BuildReport(
                options,
                "observe.exe",
                new string('a', 64),
                [first, second]));
    }

    [Theory]
    [InlineData("\"node_count\": 1", "\"node_count\": 2")]
    [InlineData(
        "\"tile_based_renderer\": false",
        "\"tile_based_renderer\": true")]
    public void BuildReport_rejects_architecture_drift(
        string oldValue,
        string newValue)
    {
        var options = new D3D12ObservationLabOptions(
            "observe.exe",
            "report.json",
            Runs: 2,
            GpuTimeoutMs: 10000,
            ProcessTimeoutMs: 20000,
            UseHardware: false);
        var first = D3D12ObservationRunParser.Parse(ValidJson);
        var second = D3D12ObservationRunParser.Parse(
            ValidJson.Replace(oldValue, newValue, StringComparison.Ordinal));

        Assert.Throws<InvalidDataException>(() =>
            D3D12ObservationLabRunner.BuildReport(
                options,
                "observe.exe",
                new string('a', 64),
                [first, second]));
    }

    [Fact]
    public void BuildReport_rejects_backwards_timestamps()
    {
        var options = new D3D12ObservationLabOptions(
            "observe.exe",
            "report.json",
            Runs: 2,
            GpuTimeoutMs: 10000,
            ProcessTimeoutMs: 20000,
            UseHardware: false);
        var first = D3D12ObservationRunParser.Parse(ValidJson);
        var second = D3D12ObservationRunParser.Parse(
            ValidJson.Replace(
                "\"captured_at_unix_ms\": 1785628800000",
                "\"captured_at_unix_ms\": 1785628799999",
                StringComparison.Ordinal));

        Assert.Throws<InvalidDataException>(() =>
            D3D12ObservationLabRunner.BuildReport(
                options,
                "observe.exe",
                new string('a', 64),
                [first, second]));
    }

    private const string ValidJson = """
        {
          "mode": "fluidruntime-owned-d3d12-observation-v0.1.0",
          "target_owned": true,
          "cooperative_load": true,
          "remote_injection": false,
          "read_only_observation": true,
          "actuation_enabled": false,
          "physical_transfer_bytes_measured": false,
          "debug_layer_enabled": false,
          "debug_message_validation_available": false,
          "debug_warning_count": 0,
          "debug_error_count": 0,
          "render_driver": "warp",
          "process_id": 42,
          "captured_at_unix_ms": 1785628800000,
          "adapter": {
            "description": "Microsoft Basic Render Driver",
            "vendor_id": 5140,
            "device_id": 140,
            "subsystem_id": 0,
            "revision": 0,
            "luid": "00000000000105fa",
            "dedicated_video_memory_bytes": 0,
            "dedicated_system_memory_bytes": 0,
            "shared_system_memory_bytes": 8589934592
          },
          "architecture": {
            "available": true,
            "node_count": 1,
            "tile_based_renderer": false,
            "uma": true,
            "cache_coherent_uma": true,
            "resource_heap_tier": 2
          },
          "queue": {
            "type": "copy",
            "priority": "normal",
            "timestamp_frequency_supported": true,
            "timestamp_frequency_hz": 10000000
          },
          "transfer": {
            "buffer_bytes": 4194304,
            "logical_upload_bytes": 4194304,
            "logical_readback_bytes": 4194304,
            "logical_total_copy_bytes": 8388608,
            "upload_heap_type": "upload",
            "default_heap_type": "default",
            "readback_heap_type": "readback",
            "upload_initial_state": "generic-read",
            "default_initial_state": "common",
            "default_first_access_promotion": "copy-dest",
            "default_state_before_readback_copy": "copy-source",
            "expected_default_post_execute_state": "common-via-buffer-decay",
            "readback_initial_state": "copy-dest",
            "command_list_type": "copy",
            "command_list_count": 1,
            "copy_command_count": 2,
            "resource_barrier_count": 1,
            "submitted_command_list_count": 1,
            "fence_signaled_value": 1,
            "fence_completed_value": 1,
            "wait_completed": true,
            "hash_algorithm": "fnv1a64",
            "source_hash": "6b35601f05442325",
            "readback_hash": "6b35601f05442325",
            "content_equivalent": true,
            "cpu_record_microseconds": 80.0,
            "submit_to_fence_microseconds": 12000.0,
            "total_workload_microseconds": 12100.0
          },
          "memory": {
            "source": "idxgiadapter3-query-video-memory-info",
            "local_before": {
              "available": true,
              "budget_bytes": 7861376717,
              "current_usage_bytes": 0,
              "current_reservation_bytes": 0,
              "available_for_reservation_bytes": 4064906086
            },
            "local_after": {
              "available": true,
              "budget_bytes": 7861376717,
              "current_usage_bytes": 12582912,
              "current_reservation_bytes": 0,
              "available_for_reservation_bytes": 4064906086
            },
            "non_local_before": {
              "available": true,
              "budget_bytes": 0,
              "current_usage_bytes": 0,
              "current_reservation_bytes": 0,
              "available_for_reservation_bytes": 0
            },
            "non_local_after": {
              "available": true,
              "budget_bytes": 0,
              "current_usage_bytes": 0,
              "current_reservation_bytes": 0,
              "available_for_reservation_bytes": 0
            }
          },
          "claim_scope": "owned-d3d12-upload-default-readback-observation-only",
          "limitations": [
            "DXGI budgets and usage are snapshots, not physical transfer counters.",
            "Logical bytes describe commands issued by this owned workload only.",
            "This probe does not hook, inject, schedule, or alter external applications."
          ]
        }
        """;
}
