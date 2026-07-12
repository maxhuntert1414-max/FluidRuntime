using System.Text.Json.Serialization;

namespace FluidRuntime.Native;

public sealed record NativeProbeReport
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = string.Empty;

    [JsonPropertyName("read_only")]
    public bool ReadOnly { get; init; }

    [JsonPropertyName("would_modify_system")]
    public bool WouldModifySystem { get; init; }

    [JsonPropertyName("pid")]
    public int ProcessId { get; init; }

    [JsonPropertyName("captured_at_unix_ms")]
    public long CapturedAtUnixMs { get; init; }

    [JsonPropertyName("sample_interval_ms")]
    public int SampleIntervalMs { get; init; }

    [JsonPropertyName("process")]
    public NativeProcessSnapshot Process { get; init; } = new();

    [JsonPropertyName("gpu")]
    public NativeGpuSnapshot Gpu { get; init; } = new();

    [JsonPropertyName("capabilities")]
    public NativeProbeCapabilities Capabilities { get; init; } = new();

    [JsonPropertyName("errors")]
    public List<NativeProbeError> Errors { get; init; } = [];
}

public sealed record NativeProcessSnapshot
{
    [JsonPropertyName("image_path")]
    public string ImagePath { get; init; } = string.Empty;

    [JsonPropertyName("priority_class")]
    public long PriorityClass { get; init; }

    [JsonPropertyName("page_fault_count")]
    public long PageFaultCount { get; init; }

    [JsonPropertyName("working_set_bytes")]
    public long WorkingSetBytes { get; init; }

    [JsonPropertyName("private_bytes")]
    public long PrivateBytes { get; init; }
}

public sealed record NativeGpuSnapshot
{
    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    [JsonPropertyName("local_usage_bytes")]
    public double? LocalUsageBytes { get; init; }

    [JsonPropertyName("dedicated_usage_bytes")]
    public double? DedicatedUsageBytes { get; init; }

    [JsonPropertyName("shared_usage_bytes")]
    public double? SharedUsageBytes { get; init; }

    [JsonPropertyName("non_local_usage_bytes")]
    public double? NonLocalUsageBytes { get; init; }

    [JsonPropertyName("engine_utilization_sum_percent")]
    public double? EngineUtilizationSumPercent { get; init; }

    [JsonPropertyName("engine_utilization_peak_percent")]
    public double? EngineUtilizationPeakPercent { get; init; }

    [JsonPropertyName("memory_instance_count")]
    public int MemoryInstanceCount { get; init; }

    [JsonPropertyName("engine_instance_count")]
    public int EngineInstanceCount { get; init; }
}

public sealed record NativeProbeCapabilities
{
    [JsonPropertyName("process_memory")]
    public bool ProcessMemory { get; init; }

    [JsonPropertyName("gpu_process_memory")]
    public bool GpuProcessMemory { get; init; }

    [JsonPropertyName("gpu_engine_utilization")]
    public bool GpuEngineUtilization { get; init; }
}

public sealed record NativeProbeError
{
    [JsonPropertyName("counter")]
    public string Counter { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;
}
