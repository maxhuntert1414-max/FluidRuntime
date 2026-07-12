using FluidRuntime.Contracts;

namespace FluidRuntime.Tests;

public sealed class FluidGatewayLedgerLoaderTests
{
    [Fact]
    public void Load_accepts_safe_operational_ledger()
    {
        var path = WriteLedger(ValidLedgerJson);

        try
        {
            var ledger = FluidGatewayLedgerLoader.Load(path);

            Assert.Equal("presentmon-operational-ledger-v0.61", ledger.Mode);
            Assert.True(ledger.DryRun);
            Assert.False(ledger.NativePromotionAllowed);
            Assert.Contains("ram-vram", ledger.NativeBlockedSurfaces);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("\"dry_run\": true", "\"dry_run\": false")]
    [InlineData("\"would_modify_system\": false", "\"would_modify_system\": true")]
    [InlineData("\"native_promotion_allowed\": false", "\"native_promotion_allowed\": true")]
    public void Load_rejects_ledger_outside_advisory_boundary(string oldValue, string newValue)
    {
        var path = WriteLedger(ValidLedgerJson.Replace(oldValue, newValue, StringComparison.Ordinal));

        try
        {
            Assert.Throws<InvalidDataException>(() => FluidGatewayLedgerLoader.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteLedger(string json)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, json);
        return path;
    }

    private const string ValidLedgerJson = """
        {
          "mode": "presentmon-operational-ledger-v0.61",
          "dry_run": true,
          "would_modify_system": false,
          "application": "TestGame.exe",
          "waste_pressure_score": 85,
          "native_blocker_score": 50,
          "native_promotion_allowed": false,
          "memory_relief_target_mb": 128,
          "safe_control_surfaces": ["telemetry"],
          "native_blocked_surfaces": ["ram-vram"]
        }
        """;
}
