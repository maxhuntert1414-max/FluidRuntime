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
        Assert.Equal(1000, options.GpuTimeoutMs);
        Assert.Equal(1, options.TrialPairs);
        Assert.Equal(0, options.WarmupPairs);
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
                "--gpu-timeout-ms", "2500",
                "--hardware", "true",
                "--out", "report.json"
            ]);

        Assert.Equal(300, options.FrameCount);
        Assert.Equal(1500, options.HoldMs);
        Assert.Equal(2500, options.GpuTimeoutMs);
        Assert.True(options.UseHardware);
        Assert.False(options.SkipFirstRedundantCopy);
        Assert.Equal(1, options.TrialPairs);
        Assert.Equal(0, options.WarmupPairs);
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
        Assert.Equal(5, options.TrialPairs);
        Assert.Equal(1, options.WarmupPairs);
        Assert.Throws<ArgumentException>(() => HookLabOptions.ParseCopyElision(
            [
                "copy-elision-lab",
                "--target", "target.exe",
                "--hook", "hook.dll",
                "--skip-first-redundant-copy", "true",
                "--out", "report.json"
            ]));

        var explicitOptions = HookLabOptions.ParseCopyElision(
            [
                "copy-elision-lab",
                "--target", "target.exe",
                "--hook", "hook.dll",
                "--trial-pairs", "7",
                "--warmup-pairs", "0",
                "--out", "report.json"
            ]);
        Assert.Equal(7, explicitOptions.TrialPairs);
        Assert.Equal(0, explicitOptions.WarmupPairs);
    }

    [Fact]
    public void ParseManager_uses_paired_lab_defaults()
    {
        var options = HookLabOptions.ParseManager(
            [
                "manager-lab",
                "--target", "target.exe",
                "--hook", "hook.dll",
                "--out", "report.json"
            ]);

        Assert.Equal(5, options.TrialPairs);
        Assert.Equal(1, options.WarmupPairs);
        Assert.False(options.SkipFirstRedundantCopy);
        Assert.False(options.UseManagedControlPolicy);
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
