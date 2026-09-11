using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text.Json;
using FluidRuntime.Runtime;

namespace FluidRuntime.Tests;

public sealed class ApplicationSessionTests
{
    private static string[] Args => ["app-session", "--exe", "app.exe", "--layer-dir", "native",
        "--out", "session.json", "--acknowledge-no-anticheat", "true"];

    [Fact]
    public async Task Cancelled_session_is_rejected_before_touching_files_or_launching()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ApplicationSessionRunner.RunAsync(
            new("missing.exe", "missing", "unused.json", 10, 0, false, []), cancellation.Token));
    }

    [Fact]
    public async Task Completed_application_does_not_wait_for_next_sampling_tick()
    {
        var wait = ApplicationSessionRunner.WaitForSampleAsync(Task.CompletedTask,
            TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(wait.IsCompletedSuccessfully);
        await wait;
    }

    [Fact]
    public async Task Sampling_wait_honors_capture_deadline_and_cancellation()
    {
        var running = new TaskCompletionSource();
        await ApplicationSessionRunner.WaitForSampleAsync(running.Task,
            TimeSpan.Zero, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ApplicationSessionRunner.WaitForSampleAsync(running.Task, TimeSpan.FromSeconds(10), cancellation.Token));
    }

    [Fact]
    public void Launch_is_opt_in_and_application_arguments_are_not_options()
    {
        var options = ApplicationSessionOptions.Parse([.. Args, "--", "--priority-seconds", "999", "with spaces", "--help"]);
        Assert.True(options.ObserveVulkan);
        Assert.Equal(0, options.PrioritySeconds);
        Assert.Equal(new[] { "--priority-seconds", "999", "with spaces", "--help" }, options.Arguments);
    }

    [Theory]
    [InlineData("--acknowledge-no-anticheat", "false")]
    [InlineData("--priority-seconds", "31")]
    [InlineData("--priority-seconds", "11")]
    [InlineData("--seconds", "0")]
    [InlineData("--seconds", "121")]
    [InlineData("--pid", "42")]
    [InlineData("--observe-vulkan", "maybe")]
    public void Options_reject_unsafe_or_ambiguous_scope(string key, string value) =>
        Assert.Throws<ArgumentException>(() => ApplicationSessionOptions.Parse([.. Args, key, value]));

    [Fact]
    public void Baseline_cannot_request_priority()
    {
        Assert.Throws<ArgumentException>(() => ApplicationSessionOptions.Parse(
            [.. Args, "--observe-vulkan", "false", "--priority-seconds", "1"]));
        Assert.Throws<ArgumentException>(() => ApplicationSessionOptions.Parse([]));
        Assert.Throws<ArgumentException>(() => ApplicationSessionOptions.Parse(Args[..^2]));
    }

    [Theory]
    [InlineData(ProcessPriorityClass.Normal, true, false)]
    [InlineData(ProcessPriorityClass.AboveNormal, false, true)]
    [InlineData(ProcessPriorityClass.High, false, false)]
    [InlineData(ProcessPriorityClass.RealTime, false, false)]
    [InlineData(ProcessPriorityClass.BelowNormal, false, false)]
    public void Lease_never_clobbers_existing_non_normal_policy(ProcessPriorityClass priority, bool apply, bool restore)
    {
        Assert.Equal(apply, WindowsPriorityLease.CanApply(priority));
        Assert.Equal(restore, WindowsPriorityLease.CanRestore(priority));
    }

    [Fact]
    public void Observation_mapping_is_bounded_process_bound_and_revocable()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var reader = new VulkanObservationReader();
        using var mapping = MemoryMappedFile.OpenExisting(reader.Name);
        using var writer = mapping.CreateViewAccessor();
        Assert.Equal(66, reader.Read(42).Count);
        Assert.Equal(560, VulkanObservationReader.Size);
        Assert.Equal(3, writer.ReadInt32(4));
        writer.Write(32 + 36 * 8, 4096L);
        Assert.Equal(4096, reader.Read(42)["host_to_device_copy_bytes"]);
        writer.Write(32 + 62 * 8, 8192L);
        Assert.Equal(8192, reader.Read(42)["submitted_buffer_copy_bytes"]);
        writer.Write(16, 42L);
        writer.Write(32 + 19 * 8, 123L);
        Assert.Equal(123, reader.Read(42)["presents"]);
        Assert.Throws<InvalidDataException>(() => reader.Read(43));
        reader.Stop();
        Assert.Equal(0L, writer.ReadInt64(24));
        writer.Write(32, -1L);
        Assert.Throws<InvalidDataException>(() => reader.Read(42));
        writer.Write(32, 0L);
        writer.Write(4, 99);
        Assert.Throws<InvalidDataException>(() => reader.Read(42));
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(4, 2)]
    [InlineData(8, 288)]
    [InlineData(8, 400)]
    [InlineData(12, 32)]
    [InlineData(12, 46)]
    public void Observation_rejects_legacy_or_inconsistent_shared_layout(int offset, int value)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var reader = new VulkanObservationReader();
        using var mapping = MemoryMappedFile.OpenExisting(reader.Name);
        using var writer = mapping.CreateViewAccessor();
        writer.Write(offset, value);
        Assert.Throws<InvalidDataException>(() => reader.Read(42));
    }

    [Fact]
    public void Manifest_cannot_select_an_unbound_dll_or_another_layer()
    {
        const string manifest = """
        {"file_format_version":"1.2.0","layer":{"name":"VK_LAYER_FLUIDRUNTIME_observe",
        "type":"GLOBAL","library_path":".\\fluidruntime-vulkan-observe.dll",
        "functions":{"vkNegotiateLoaderLayerInterfaceVersion":"fgNegotiate"}}}
        """;
        using var valid = JsonDocument.Parse(manifest);
        ApplicationSessionRunner.ValidateManifest(valid.RootElement);
        foreach (var original in new[] { "GLOBAL", "fgNegotiate", "fluidruntime-vulkan-observe.dll" })
        {
            using var changed = JsonDocument.Parse(manifest.Replace(original, "untrusted"));
            Assert.Throws<InvalidDataException>(() => ApplicationSessionRunner.ValidateManifest(changed.RootElement));
        }
    }
}
