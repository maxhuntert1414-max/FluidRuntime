namespace FluidRuntime.Telemetry;

public interface IProcessTelemetrySampler
{
    Task<IReadOnlyList<TelemetrySnapshot>> SampleAsync(
        int processId,
        int sampleCount,
        TimeSpan interval,
        CancellationToken cancellationToken = default);
}
