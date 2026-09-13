namespace FluidRuntime.Runtime;

internal sealed record DailyMonitorSample(
    DateTimeOffset CapturedAt, double ElapsedSeconds, double CpuPercent,
    long WorkingSetBytes, long PrivateBytes, double? GpuEnginePeakPercent,
    double? GpuDedicatedBytes, double? GpuSharedBytes);

internal sealed record DailyMonitorReport(
    string SessionId, int ProcessId, string ProcessName, long ProcessStartTimeFileTime,
    DateTimeOffset StartedAt, DateTimeOffset UpdatedAt, string State, string? StopReason,
    string GpuStatus, string? Warning, long TotalSamples, DailyMonitorSample[] Samples)
{
    public string Mode => "fluidruntime-monitor-v0.1";
    public bool ReadOnly => true;
    public bool WouldModifySystem => false;
    public int SampleCapacity => DailyMonitorHistory.Capacity;
    public long DroppedHistorySamples => TotalSamples - Samples.Length;
    public string Authority => "observation-only; no Gateway authorization or actuation";
}

internal sealed class DailyMonitorHistory
{
    internal const int Capacity = 600;
    private readonly Queue<DailyMonitorSample> samples = new(Capacity);
    internal long TotalSamples { get; private set; }

    internal void Add(DailyMonitorSample sample)
    {
        if (samples.Count == Capacity) samples.Dequeue();
        samples.Enqueue(sample);
        TotalSamples = checked(TotalSamples + 1);
    }

    internal DailyMonitorSample[] Snapshot() => samples.ToArray();
}
