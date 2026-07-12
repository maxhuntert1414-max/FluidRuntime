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
        Assert.False(options.SkipFirstRedundantCopy);
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
        Assert.False(options.SkipFirstRedundantCopy);
    }

    [Fact]
    public void ParseCopyElision_uses_two_run_defaults_and_rejects_single_run_skip()
    {
        var options = HookLabOptions.ParseCopyElision(
            [
                "copy-elision-lab",
                "--target", "target.exe",
                "--hook", "hook.dll",
                "--out", "report.json"
            ]);

        Assert.False(options.SkipFirstRedundantCopy);
        Assert.Throws<ArgumentException>(() => HookLabOptions.ParseCopyElision(
            [
                "copy-elision-lab",
                "--target", "target.exe",
                "--hook", "hook.dll",
                "--skip-first-redundant-copy", "true",
                "--out", "report.json"
            ]));
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
