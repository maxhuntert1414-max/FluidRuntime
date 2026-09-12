using System.IO.MemoryMappedFiles;

namespace FluidRuntime.Runtime;

internal sealed class VulkanObservationReader : IDisposable
{
    internal const uint Magic = 0x4f564746;
    internal const int Version = 4;
    internal const int Size = 672;
    internal static readonly string[] Counters = ["instances", "devices", "allocations", "frees",
        "allocation_bytes", "live_bytes", "peak_bytes", "host_visible_bytes", "device_local_bytes",
        "maps", "flushes", "invalidates", "buffer_binds", "buffer_copies", "buffer_copy_bytes",
        "buffer_image_copies", "fills", "barriers", "submits", "presents", "api_errors",
        "untracked_allocations", "active_instances", "active_devices", "intercepted_calls", "unmaps",
        "image_binds", "queue_waits", "fence_waits", "copy2_calls", "submit2_calls", "telemetry_failures",
        "buffers_created", "buffers_destroyed", "live_buffers", "untracked_buffers",
        "host_to_device_copy_bytes", "device_to_host_copy_bytes", "device_to_device_copy_bytes",
        "host_to_host_copy_bytes", "shared_memory_copy_bytes", "unknown_buffer_copy_bytes",
        "same_allocation_copy_bytes", "buffer_binding_failures", "unclassified_buffer_bindings", "counter_overflows",
        "command_buffers_allocated", "command_buffers_freed", "live_command_buffers", "untracked_command_buffers",
        "command_buffer_begins", "command_buffer_ends", "command_buffer_resets", "command_pool_resets",
        "command_execute_calls", "command_tracking_failures", "successful_submit_calls", "failed_submit_calls",
        "submitted_primary_command_buffers", "submitted_secondary_command_buffers", "resubmitted_command_buffers",
        "submitted_buffer_copies", "submitted_buffer_copy_bytes", "unresolved_submit_calls",
        "command_tracking_overflows", "untracked_command_pools",
        "fences_created", "fences_destroyed", "live_fences", "untracked_fences", "fence_resets",
        "fence_status_queries", "device_waits", "ambiguous_fence_waits", "completion_tracking_failures",
        "completed_submit_calls", "completed_buffer_copies", "completed_buffer_copy_bytes",
        "pending_tracked_submits", "abandoned_tracked_submits"];
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor view;
    internal string Name { get; } = "Local\\FluidRuntimeObserve-" + Guid.NewGuid().ToString("N");

    internal VulkanObservationReader()
    {
        mapping = MemoryMappedFile.CreateNew(Name, Size, MemoryMappedFileAccess.ReadWrite);
        view = mapping.CreateViewAccessor(0, Size, MemoryMappedFileAccess.ReadWrite);
        view.Write(4, Version); view.Write(8, Size); view.Write(12, Counters.Length);
        view.Write(24, 1L); view.Write(0, Magic);
    }
    internal long ProcessId => view.ReadInt64(16);
    internal Dictionary<string, long> Read(int expectedPid)
    {
        if (view.ReadUInt32(0) != Magic || view.ReadInt32(4) != Version || view.ReadInt32(8) != Size ||
            view.ReadInt32(12) != Counters.Length || (ProcessId != 0 && ProcessId != expectedPid))
            throw new InvalidDataException("Observation ABI or process identity mismatch.");
        var values = Counters.Select((name, i) => (name, value: view.ReadInt64(32 + i * 8)))
            .ToDictionary(item => item.name, item => item.value);
        if (values.Values.Any(value => value < 0)) throw new InvalidDataException("Invalid observation counters.");
        return values;
    }
    internal void Stop() => view.Write(24, 0L);
    public void Dispose() { Stop(); view.Dispose(); mapping.Dispose(); }
}
