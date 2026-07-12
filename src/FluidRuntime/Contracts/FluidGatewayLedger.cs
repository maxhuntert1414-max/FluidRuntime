using System.Text.Json.Serialization;

namespace FluidRuntime.Contracts;

public sealed record FluidGatewayLedger
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = string.Empty;

    [JsonPropertyName("dry_run")]
    public bool DryRun { get; init; }

    [JsonPropertyName("would_modify_system")]
    public bool WouldModifySystem { get; init; }

    [JsonPropertyName("application")]
    public string Application { get; init; } = string.Empty;

    [JsonPropertyName("waste_pressure_score")]
    public double WastePressureScore { get; init; }

    [JsonPropertyName("native_blocker_score")]
    public double NativeBlockerScore { get; init; }

    [JsonPropertyName("native_promotion_allowed")]
    public bool NativePromotionAllowed { get; init; }

    [JsonPropertyName("memory_relief_target_mb")]
    public double MemoryReliefTargetMb { get; init; }

    [JsonPropertyName("native_blocked_surfaces")]
    public List<string> NativeBlockedSurfaces { get; init; } = [];
}
