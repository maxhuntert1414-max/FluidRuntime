using System.Text.Json;

namespace FluidRuntime.Native;

public static class NativeProbeReportParser
{
    public static NativeProbeReport Parse(string json, int expectedProcessId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var report = JsonSerializer.Deserialize<NativeProbeReport>(json)
            ?? throw new InvalidDataException("The native probe returned an empty document.");

        Validate(report, expectedProcessId);
        return report;
    }

    public static NativeProbeSeriesReport ParseSeries(
        string json,
        int expectedProcessId,
        int expectedSampleCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedSampleCount);
        var report = JsonSerializer.Deserialize<NativeProbeSeriesReport>(json)
            ?? throw new InvalidDataException("The native probe returned an empty series document.");

        if (!string.Equals(
                report.Mode,
                "fluidruntime-native-probe-series-v0.1",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported native probe series mode '{report.Mode}'.");
        }
        if (!report.ReadOnly || report.WouldModifySystem)
        {
            throw new InvalidDataException("The native probe series did not satisfy the read-only contract.");
        }
        if (report.ProcessId != expectedProcessId)
        {
            throw new InvalidDataException(
                $"Native probe series PID {report.ProcessId} did not match target PID {expectedProcessId}.");
        }
        if (report.SampleCount != expectedSampleCount || report.Samples.Count != expectedSampleCount)
        {
            throw new InvalidDataException(
                $"Native probe series returned {report.Samples.Count} samples; " +
                $"expected {expectedSampleCount}.");
        }

        long previousTimestamp = 0;
        foreach (var sample in report.Samples)
        {
            Validate(sample, expectedProcessId);
            if (sample.SampleIntervalMs != report.SampleIntervalMs)
            {
                throw new InvalidDataException(
                    "Native probe series sample interval did not match its envelope.");
            }
            if (sample.CapturedAtUnixMs <= previousTimestamp)
            {
                throw new InvalidDataException(
                    "Native probe series timestamps were not strictly increasing.");
            }
            previousTimestamp = sample.CapturedAtUnixMs;
        }

        return report;
    }

    private static void Validate(NativeProbeReport report, int expectedProcessId)
    {

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

    }
}
