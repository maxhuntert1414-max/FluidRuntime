using System.Text.Json;

namespace FluidRuntime.Contracts;

public static class FluidGatewayLedgerLoader
{
    public static FluidGatewayLedger Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = File.OpenRead(path);
        var ledger = JsonSerializer.Deserialize<FluidGatewayLedger>(stream)
            ?? throw new InvalidDataException("The FluidGateway ledger is empty.");

        Validate(ledger);
        return ledger;
    }

    public static void Validate(FluidGatewayLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        if (string.IsNullOrWhiteSpace(ledger.Mode) ||
            !ledger.Mode.StartsWith("presentmon-operational-ledger-", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Unsupported ledger mode. Expected a PresentMon operational ledger.");
        }

        if (!ledger.DryRun || ledger.WouldModifySystem || ledger.NativePromotionAllowed)
        {
            throw new InvalidDataException(
                "FluidRuntime v0.1 only accepts dry-run ledgers with native promotion disabled.");
        }
    }
}
