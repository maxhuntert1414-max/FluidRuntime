using System.Text.Json;

namespace FluidRuntime.Native;

public static class NativeProbeReportParser
{
    public static NativeProbeReport Parse(string json, int expectedProcessId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var report = JsonSerializer.Deserialize<NativeProbeReport>(json)
            ?? throw new InvalidDataException("The native probe returned an empty document.");

        if (!string.Equals(
                report.Mode,
                "fluidruntime-native-probe-v0.2",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported native probe mode '{report.Mode}'.");
        }

        if (!report.ReadOnly || report.WouldModifySystem)
        {
            throw new InvalidDataException("The native probe did not satisfy the read-only contract.");
        }

        if (report.ProcessId != expectedProcessId)
        {
            throw new InvalidDataException(
                $"Native probe PID {report.ProcessId} did not match target PID {expectedProcessId}.");
        }

        if (report.CapturedAtUnixMs <= 0)
        {
            throw new InvalidDataException("The native probe timestamp is missing or invalid.");
        }

        if (report.Process is null || report.Gpu is null || report.Capabilities is null)
        {
            throw new InvalidDataException(
                "The native probe omitted required process, GPU, or capability data.");
        }

        return report;
    }
}
