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
    public void Parse_rejects_unknown_option()
    {
        Assert.Throws<ArgumentException>(() => RuntimeOptions.Parse(
            ["inspect", "--ledger", "ledger.json", "--out", "report.json", "--mutate", "true"]));
    }
}
