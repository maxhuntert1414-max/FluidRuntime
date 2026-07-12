using FluidRuntime.Contracts;
using FluidRuntime.Runtime;
using FluidRuntime.Telemetry;

namespace FluidRuntime.Tests;

public sealed class RuntimeDecisionEngineTests
{
    private readonly RuntimeDecisionEngine _engine = new();

    [Fact]
    public void Build_blocks_cpu_scheduler_candidate_under_high_cpu_pressure()
    {
        var plan = _engine.Build(SafeLedger(), Telemetry(averageCpu: 76, maximumCpu: 91));

        var candidate = Assert.Single(
            plan.Actions,
            action => action.Action == "prototype-cpu-scheduling-control");
        Assert.True(candidate.Blocked);
        Assert.True(candidate.RequiresNativeBackend);
        Assert.False(plan.WouldModifySystem);
    }

    [Fact]
    public void Build_exposes_blocked_ram_vram_residency_candidate_with_numeric_evidence()
    {
        var ledger = SafeLedger() with
        {
            MemoryReliefTargetMb = 168,
            NativeBlockedSurfaces = ["ram-vram"]
        };

        var plan = _engine.Build(ledger, Telemetry(hostMemoryPressure: 72));

        var candidate = Assert.Single(
            plan.Actions,
            action => action.Action == "prototype-ram-vram-residency-control");
        Assert.True(candidate.Blocked);
        Assert.Equal(168, candidate.Evidence["memory_relief_target_mb"]);
        Assert.Equal(1, candidate.Evidence["ledger_surface_blocked"]);
    }

    [Fact]
    public void Build_keeps_low_pressure_plan_observational()
    {
        var plan = _engine.Build(SafeLedger(), Telemetry());

        var action = Assert.Single(plan.Actions);
        Assert.Equal("continue-process-telemetry", action.Action);
        Assert.False(action.Blocked);
        Assert.Equal("continue-observation", plan.Policy);
        Assert.False(plan.NativePromotionAllowed);
    }

    private static FluidGatewayLedger SafeLedger() => new()
    {
        Mode = "presentmon-operational-ledger-v0.61",
        DryRun = true,
        WouldModifySystem = false,
        Application = "TestGame.exe",
        WastePressureScore = 30,
        NativeBlockerScore = 0,
        NativePromotionAllowed = false,
        NativeBlockedSurfaces = []
    };

    private static TelemetrySummary Telemetry(
        double averageCpu = 12,
        double maximumCpu = 20,
        double hostMemoryPressure = 45) => new(
            ProcessId: 42,
            ProcessName: "TestGame",
            SampleCount: 3,
            AverageCpuPercent: averageCpu,
            MaximumCpuPercent: maximumCpu,
            MaximumWorkingSetMb: 512,
            MaximumPrivateMemoryMb: 420,
            MaximumThreadCount: 16,
            MaximumHostMemoryPressurePercent: hostMemoryPressure,
            MinimumHostAvailableMemoryMb: 8192);
}
