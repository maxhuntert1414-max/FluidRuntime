using FluidRuntime.Cli;

namespace FluidRuntime.Tests;

public sealed class HookLabOptionsTests
{
    [Fact]
    public void Parse_uses_bounded_lab_defaults()
    {
        var options = HookLabOptions.Parse(
            [
                "hook-lab",
                "--target", "target.exe",
                "--hook", "hook.dll",
                "--out", "report.json"
            ]);

        Assert.Equal(120, options.FrameCount);
        Assert.Equal(1000, options.HoldMs);
        Assert.False(options.UseHardware);
    }

    [Fact]
    public void Parse_accepts_explicit_hardware_lab_values()
    {
        var options = HookLabOptions.Parse(
            [
                "hook-lab",
                "--target", "target.exe",
                "--hook", "hook.dll",
                "--frames", "300",
                "--hold-ms", "1500",
                "--hardware", "true",
                "--out", "report.json"
            ]);

        Assert.Equal(300, options.FrameCount);
        Assert.Equal(1500, options.HoldMs);
        Assert.True(options.UseHardware);
    }

    [Fact]
    public void Parse_rejects_unbounded_or_implicit_values()
    {
        Assert.Throws<ArgumentException>(() => HookLabOptions.Parse(
            [
                "hook-lab",
                "--target", "target.exe",
                "--hook", "hook.dll",
                "--frames", "10001",
                "--out", "report.json"
            ]));
        Assert.Throws<ArgumentException>(() => HookLabOptions.Parse(
            [
                "hook-lab",
                "--target", "target.exe",
                "--hook", "hook.dll",
                "--hardware", "yes",
                "--out", "report.json"
            ]));
    }
}
