using FluidRuntime.Cli;

namespace FluidRuntime.Tests;

public sealed class RuntimeOptionsTests
{
    [Fact]
    public void Parse_uses_safe_sampling_defaults()
    {
        var options = RuntimeOptions.Parse(
            ["inspect", "--ledger", "ledger.json", "--out", "report.json"]);

        Assert.Equal(Environment.ProcessId, options.ProcessId);
        Assert.Equal(3, options.SampleCount);
        Assert.Equal(250, options.IntervalMs);
        Assert.Equal(1, options.NativeProbeSampleCount);
        Assert.Equal(10000, options.NativeProbeTimeoutMs);
    }

    [Fact]
    public void Parse_accepts_explicit_process_and_sampling_values()
    {
        var options = RuntimeOptions.Parse(
            [
                "inspect",
                "--ledger", "ledger.json",
                "--pid", "42",
                "--samples", "5",
                "--interval-ms", "100",
                "--out", "report.json"
            ]);

        Assert.Equal(42, options.ProcessId);
        Assert.Equal(5, options.SampleCount);
        Assert.Equal(100, options.IntervalMs);
    }

    [Fact]
    public void Parse_accepts_native_probe_path()
    {
        var options = RuntimeOptions.Parse(
            [
                "inspect",
                "--ledger", "ledger.json",
                "--native-probe", "native-probe.exe",
                "--out", "report.json"
            ]);

        Assert.Equal("native-probe.exe", options.NativeProbePath);
    }

    [Fact]
    public void Parse_accepts_bounded_native_probe_series()
    {
        var options = RuntimeOptions.Parse(
            [
                "inspect",
                "--ledger", "ledger.json",
                "--samples", "4",
                "--interval-ms", "1000",
                "--native-probe", "native-probe.exe",
                "--native-probe-samples", "4",
                "--out", "report.json"
            ]);

        Assert.Equal(4, options.NativeProbeSampleCount);
        Assert.Equal(10000, options.NativeProbeTimeoutMs);
    }

    [Fact]
    public void Parse_requires_explicit_boolean_for_target_mismatch_override()
    {
        var options = RuntimeOptions.Parse(
            [
                "inspect",
                "--ledger", "ledger.json",
                "--allow-ledger-target-mismatch", "true",
                "--out", "report.json"
            ]);

        Assert.True(options.AllowLedgerTargetMismatch);
        Assert.Throws<ArgumentException>(() => RuntimeOptions.Parse(
            [
                "inspect",
                "--ledger", "ledger.json",
                "--allow-ledger-target-mismatch", "yes",
                "--out", "report.json"
            ]));
    }

    [Fact]
    public void Parse_rejects_unknown_option()
    {
        Assert.Throws<ArgumentException>(() => RuntimeOptions.Parse(
            ["inspect", "--ledger", "ledger.json", "--out", "report.json", "--mutate", "true"]));
    }

    [Fact]
    public void Parse_bounds_sampling_and_native_probe_deadlines()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RuntimeOptions.Parse(
            [
                "inspect", "--ledger", "ledger.json", "--out", "report.json",
                "--interval-ms", "60001"
            ]));
        Assert.Throws<ArgumentOutOfRangeException>(() => RuntimeOptions.Parse(
            [
                "inspect", "--ledger", "ledger.json", "--out", "report.json",
                "--native-probe-timeout-ms", "120001"
            ]));

        var options = RuntimeOptions.Parse(
            [
                "inspect", "--ledger", "ledger.json", "--out", "report.json",
                "--interval-ms", "60000"
            ]);
        Assert.Equal(65000, options.NativeProbeTimeoutMs);
    }

    [Theory]
    [InlineData("--native-probe-samples", "101")]
    [InlineData("--native-probe-timeout-ms", "250")]
    public void Parse_rejects_unsafe_native_probe_series_values(string option, string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => RuntimeOptions.Parse(
            [
                "inspect",
                "--ledger", "ledger.json",
                "--samples", "2",
                "--interval-ms", "250",
                "--native-probe", "native-probe.exe",
                "--native-probe-samples", "2",
                option, value,
                "--out", "report.json"
            ]));
    }

    [Fact]
    public void Parse_requires_probe_and_matching_managed_window_for_series()
    {
        Assert.Throws<ArgumentException>(() => RuntimeOptions.Parse(
            [
                "inspect", "--ledger", "ledger.json", "--out", "report.json",
                "--samples", "2", "--native-probe-samples", "2"
            ]));
        Assert.Throws<ArgumentOutOfRangeException>(() => RuntimeOptions.Parse(
            [
                "inspect", "--ledger", "ledger.json", "--out", "report.json",
                "--samples", "2", "--native-probe", "native-probe.exe",
                "--native-probe-samples", "3"
            ]));
    }
}
