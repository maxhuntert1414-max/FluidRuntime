namespace FluidRuntime.Telemetry;

public sealed record TelemetrySnapshot(
    DateTimeOffset TimestampUtc,
    int ProcessId,
    string ProcessName,
    double CpuPercent,
    double WorkingSetMb,
    double PrivateMemoryMb,
    int ThreadCount,
    double HostMemoryPressurePercent,
    double HostAvailableMemoryMb);
