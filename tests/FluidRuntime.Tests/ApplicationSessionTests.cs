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
        Assert.Equal(32, reader.Read(42).Count);
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
