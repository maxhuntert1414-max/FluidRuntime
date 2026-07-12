namespace FluidRuntime.Telemetry;

public sealed record TelemetrySummary(
    int ProcessId,
    string ProcessName,
    int SampleCount,
    double AverageCpuPercent,
    double MaximumCpuPercent,
    double MaximumWorkingSetMb,
    double MaximumPrivateMemoryMb,
    int MaximumThreadCount,
    double MaximumHostMemoryPressurePercent,
    double MinimumHostAvailableMemoryMb)
{
    public static TelemetrySummary From(IReadOnlyList<TelemetrySnapshot> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            throw new ArgumentException("At least one telemetry sample is required.", nameof(samples));
        }

        var first = samples[0];
        return new TelemetrySummary(
            first.ProcessId,
            first.ProcessName,
            samples.Count,
            Math.Round(samples.Average(sample => sample.CpuPercent), 2),
            samples.Max(sample => sample.CpuPercent),
            samples.Max(sample => sample.WorkingSetMb),
            samples.Max(sample => sample.PrivateMemoryMb),
            samples.Max(sample => sample.ThreadCount),
            samples.Max(sample => sample.HostMemoryPressurePercent),
            samples.Min(sample => sample.HostAvailableMemoryMb));
    }
}
