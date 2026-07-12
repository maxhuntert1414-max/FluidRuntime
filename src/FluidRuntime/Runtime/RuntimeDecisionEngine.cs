using FluidRuntime.Contracts;
using FluidRuntime.Native;
using FluidRuntime.Telemetry;

namespace FluidRuntime.Runtime;

public sealed class RuntimeDecisionEngine
{
    private const double CpuAveragePressureThreshold = 70d;
    private const double CpuPeakPressureThreshold = 85d;
    private const double HostMemoryPressureThreshold = 85d;

    public RuntimeDecisionPlan Build(
        FluidGatewayLedger ledger,
        TelemetrySummary telemetry,
        NativeProbeReport? nativeProbe = null)
    {
        FluidGatewayLedgerLoader.Validate(ledger);
        ArgumentNullException.ThrowIfNull(telemetry);

        var actions = new List<RuntimeActionCandidate>
        {
            new(
                "continue-process-telemetry",
                "telemetry",
                "More live samples can confirm whether the trace pressure persists.",
                "observe",
                RequiresNativeBackend: false,
                RequiresPrivilege: false,
                Blocked: false,
                Evidence: new Dictionary<string, double>
                {
                    ["sample_count"] = telemetry.SampleCount,
                    ["waste_pressure_score"] = ledger.WastePressureScore
                })
        };

        if (telemetry.AverageCpuPercent >= CpuAveragePressureThreshold ||
            telemetry.MaximumCpuPercent >= CpuPeakPressureThreshold)
        {
            actions.Add(new RuntimeActionCandidate(
                "prototype-cpu-scheduling-control",
                "cpu-scheduling",
                "Sustained CPU pressure may delay producer work before GPU submission.",
                "hold-for-native-backend",
                RequiresNativeBackend: true,
                RequiresPrivilege: true,
                Blocked: true,
                Evidence: new Dictionary<string, double>
                {
                    ["average_cpu_percent"] = telemetry.AverageCpuPercent,
                    ["maximum_cpu_percent"] = telemetry.MaximumCpuPercent
                }));
        }

        var ramVramBlocked = ledger.NativeBlockedSurfaces?.Any(
            surface => string.Equals(surface, "ram-vram", StringComparison.OrdinalIgnoreCase)) == true;
        if (ramVramBlocked ||
            ledger.MemoryReliefTargetMb > 0 ||
            telemetry.MaximumHostMemoryPressurePercent >= HostMemoryPressureThreshold)
        {
            var memoryEvidence = new Dictionary<string, double>
            {
                ["host_memory_pressure_percent"] = telemetry.MaximumHostMemoryPressurePercent,
                ["memory_relief_target_mb"] = ledger.MemoryReliefTargetMb,
                ["ledger_surface_blocked"] = ramVramBlocked ? 1 : 0
            };
            AddNativeGpuEvidence(memoryEvidence, nativeProbe);

            actions.Add(new RuntimeActionCandidate(
                "prototype-ram-vram-residency-control",
                "ram-vram",
                "Memory pressure or the gateway ledger indicates avoidable residency churn.",
                "hold-for-native-backend",
                RequiresNativeBackend: true,
                RequiresPrivilege: true,
                Blocked: true,
                Evidence: memoryEvidence));
        }

        if (ledger.NativeBlockerScore > 0 && actions.All(action => !action.RequiresNativeBackend))
        {
            actions.Add(new RuntimeActionCandidate(
                "build-native-control-backend",
                "native-runtime",
                "The measured opportunity cannot be acted on through managed telemetry alone.",
                "hold-for-native-backend",
                RequiresNativeBackend: true,
                RequiresPrivilege: false,
                Blocked: true,
                Evidence: new Dictionary<string, double>
                {
                    ["native_blocker_score"] = ledger.NativeBlockerScore
                }));
        }

        var combinedPressure = Math.Round(new[]
        {
            ledger.WastePressureScore,
            telemetry.AverageCpuPercent,
            telemetry.MaximumHostMemoryPressurePercent,
            Math.Min(nativeProbe?.Gpu.EngineUtilizationSumPercent ?? 0, 100)
        }.Max(), 2);

        var policy = actions.Any(action => action.RequiresNativeBackend)
            ? "prepare-native-controls-keep-execution-blocked"
            : "continue-observation";

        return new RuntimeDecisionPlan(
            "fluidruntime-decision-plan-v0.2",
            DryRun: true,
            WouldModifySystem: false,
            ExecutionGuard: "advisory-only",
            Policy: policy,
            CombinedPressureScore: combinedPressure,
            NativePromotionAllowed: false,
            Actions: actions);
    }

    private static void AddNativeGpuEvidence(
        IDictionary<string, double> evidence,
        NativeProbeReport? nativeProbe)
    {
        const double bytesPerMegabyte = 1024d * 1024d;
        if (nativeProbe?.Gpu.LocalUsageBytes is double localUsage)
        {
            evidence["gpu_local_usage_mb"] = Math.Round(localUsage / bytesPerMegabyte, 2);
        }
        if (nativeProbe?.Gpu.SharedUsageBytes is double sharedUsage)
        {
            evidence["gpu_shared_usage_mb"] = Math.Round(sharedUsage / bytesPerMegabyte, 2);
        }
        if (nativeProbe?.Gpu.EngineUtilizationSumPercent is double utilization)
        {
            evidence["gpu_engine_utilization_sum_percent"] = utilization;
        }
    }
}
