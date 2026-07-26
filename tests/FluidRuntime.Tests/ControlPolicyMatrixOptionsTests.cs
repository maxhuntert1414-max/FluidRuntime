using FluidRuntime.Cli;
using FluidRuntime.Native;

namespace FluidRuntime.Tests;

public sealed class ControlPolicyMatrixOptionsTests
{
    [Fact]
    public void Parse_requires_distinct_release_and_debug_inputs()
    {
        var options = ControlPolicyMatrixOptions.Parse(
        [
            "control-policy-matrix",
            "--release-target", "release-target.exe",
            "--release-hook", "release-hook.dll",
            "--debug-target", "debug-target.exe",
            "--debug-hook", "debug-hook.dll",
            "--out", "matrix.json"
        ]);

        Assert.Equal("release-target.exe", options.ReleaseTargetPath);
        Assert.Equal("release-hook.dll", options.ReleaseHookPath);
        Assert.Equal("debug-target.exe", options.DebugTargetPath);
        Assert.Equal("debug-hook.dll", options.DebugHookPath);
        Assert.Equal("matrix.json", options.OutputPath);
        Assert.Equal(20, ControlPolicyMatrixOptions.RepetitionsPerCase);
    }

    [Fact]
    public void Parse_rejects_missing_or_unknown_options()
    {
        Assert.Throws<ArgumentException>(() => ControlPolicyMatrixOptions.Parse(
        [
            "control-policy-matrix",
            "--release-target", "release-target.exe",
            "--out", "matrix.json"
        ]));
        Assert.Throws<ArgumentException>(() => ControlPolicyMatrixOptions.Parse(
        [
            "control-policy-matrix",
            "--mystery", "value"
        ]));
    }

    [Fact]
    public void Matrix_defines_all_fail_closed_policy_shapes()
    {
        Assert.Equal(8, HookControlPolicyCases.Matrix.Count);
        Assert.Equal(8, HookControlPolicyCases.Matrix.Distinct().Count());

        const ulong frequency = 1_000;
        const long now = 10_000;
        var policies = HookControlPolicyCases.Matrix.ToDictionary(
            policyCase => policyCase,
            policyCase => policyCase.CreateLabPolicy(frequency, now));

        Assert.Equal(1, policies[HookControlPolicyCase.Valid].Epoch);
        Assert.Equal(2, policies[HookControlPolicyCase.WrongEpoch].Epoch);
        Assert.Equal(2UL, policies[HookControlPolicyCase.UnknownAction].ActionMask);
        Assert.Equal(129UL, policies[HookControlPolicyCase.WrongBudget].ActionBudget);
        Assert.Equal(15_000, policies[HookControlPolicyCase.TooLongExpiry].ExpiresAtQpc);
        Assert.Equal(9_999, policies[HookControlPolicyCase.AlreadyExpired].ExpiresAtQpc);
        Assert.Equal(
            10_100,
            policies[HookControlPolicyCase.AcceptedThenExpired].ExpiresAtQpc);
    }
}
