using FluidRuntime.Native;
using System.Text.Json;

namespace FluidRuntime.Tests;

public sealed class NativeProbeReportParserTests
{
    [Fact]
    public void Parse_accepts_matching_read_only_probe_report()
    {
        var report = NativeProbeReportParser.Parse(ValidJson, expectedProcessId: 42);

        Assert.Equal(42, report.ProcessId);
        Assert.True(report.ReadOnly);
        Assert.False(report.WouldModifySystem);
        Assert.Equal(256 * 1024 * 1024, report.Gpu.LocalUsageBytes);
        Assert.True(report.Capabilities.GpuProcessMemory);
    }

    [Theory]
    [InlineData("\"read_only\": true", "\"read_only\": false")]
    [InlineData("\"would_modify_system\": false", "\"would_modify_system\": true")]
    [InlineData("\"captured_at_unix_ms\": 1783886400000", "\"captured_at_unix_ms\": 0")]
    [InlineData("\"process\": {", "\"process\": null, \"ignored_process\": {")]
    public void Parse_rejects_probe_that_breaks_read_only_contract(
        string oldValue,
        string newValue)
    {
        var json = ValidJson.Replace(oldValue, newValue, StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() =>
            NativeProbeReportParser.Parse(json, expectedProcessId: 42));
    }

    [Fact]
    public void Parse_rejects_report_for_another_process()
    {
        Assert.Throws<InvalidDataException>(() =>
            NativeProbeReportParser.Parse(ValidJson, expectedProcessId: 99));
    }

    [Fact]
    public void ParseSeries_accepts_ordered_read_only_samples()
    {
        var first = NativeProbeReportParser.Parse(ValidJson, expectedProcessId: 42);
        var series = new NativeProbeSeriesReport
        {
            Mode = "fluidruntime-native-probe-series-v0.1",
            ReadOnly = true,
            WouldModifySystem = false,
            ProcessId = 42,
            SampleIntervalMs = 250,
            SampleCount = 2,
            Samples =
            [
                first,
                first with { CapturedAtUnixMs = first.CapturedAtUnixMs + 250 }
            ]
        };

        var parsed = NativeProbeReportParser.ParseSeries(
            JsonSerializer.Serialize(series),
            expectedProcessId: 42,
            expectedSampleCount: 2);

        Assert.Equal(2, parsed.Samples.Count);
        Assert.True(parsed.Samples[1].CapturedAtUnixMs > parsed.Samples[0].CapturedAtUnixMs);
    }

    [Fact]
    public void ParseSeries_rejects_duplicate_timestamps()
    {
        var sample = NativeProbeReportParser.Parse(ValidJson, expectedProcessId: 42);
        var series = new NativeProbeSeriesReport
        {
            Mode = "fluidruntime-native-probe-series-v0.1",
            ReadOnly = true,
            ProcessId = 42,
            SampleIntervalMs = 250,
            SampleCount = 2,
            Samples = [sample, sample]
        };

        Assert.Throws<InvalidDataException>(() => NativeProbeReportParser.ParseSeries(
            JsonSerializer.Serialize(series),
            expectedProcessId: 42,
            expectedSampleCount: 2));
    }

    private const string ValidJson = """
        {
          "mode": "fluidruntime-native-probe-v0.2",
          "read_only": true,
          "would_modify_system": false,
          "pid": 42,
          "captured_at_unix_ms": 1783886400000,
          "sample_interval_ms": 250,
          "process": {
            "image_path": "C:\\Games\\TestGame.exe",
            "priority_class": 32,
            "page_fault_count": 1234,
            "working_set_bytes": 536870912,
            "private_bytes": 419430400
          },
          "gpu": {
            "source": "windows-pdh",
            "local_usage_bytes": 268435456,
            "dedicated_usage_bytes": 268435456,
            "shared_usage_bytes": 33554432,
            "non_local_usage_bytes": 33554432,
            "engine_utilization_sum_percent": 61.5,
            "engine_utilization_peak_percent": 42.0,
            "memory_instance_count": 2,
            "engine_instance_count": 8
          },
          "capabilities": {
            "process_memory": true,
            "gpu_process_memory": true,
            "gpu_engine_utilization": true
          },
          "errors": []
        }
        """;
}
