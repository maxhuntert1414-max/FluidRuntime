using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FluidRuntime.Telemetry;

public sealed class WindowsProcessTelemetrySampler : IProcessTelemetrySampler
{
    private const double BytesPerMegabyte = 1024d * 1024d;

    public async Task<IReadOnlyList<TelemetrySnapshot>> SampleAsync(
        int processId,
        int sampleCount,
        TimeSpan interval,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("FluidRuntime v0.1 telemetry requires Windows.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        using var process = Process.GetProcessById(processId);
        var samples = new List<TelemetrySnapshot>(sampleCount);

        for (var index = 0; index < sampleCount; index++)
        {
            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var timer = Stopwatch.StartNew();

            await Task.Delay(interval, cancellationToken);

            timer.Stop();
            process.Refresh();
            var cpuAfter = process.TotalProcessorTime;
            var cpuPercent = CalculateCpuPercent(cpuAfter - cpuBefore, timer.Elapsed);
            var memory = ReadHostMemory();

            samples.Add(new TelemetrySnapshot(
                DateTimeOffset.UtcNow,
                process.Id,
                process.ProcessName,
                cpuPercent,
                RoundMegabytes(process.WorkingSet64),
                RoundMegabytes(process.PrivateMemorySize64),
                process.Threads.Count,
                memory.PressurePercent,
                memory.AvailableMb));
        }

        return samples;
    }

    private static double CalculateCpuPercent(TimeSpan cpuTime, TimeSpan wallTime)
    {
        var capacity = wallTime.TotalMilliseconds * Environment.ProcessorCount;
        if (capacity <= 0)
        {
            return 0;
        }

        return Math.Round(Math.Clamp(cpuTime.TotalMilliseconds / capacity * 100d, 0d, 100d), 2);
    }

    private static double RoundMegabytes(long bytes) =>
        Math.Round(bytes / BytesPerMegabyte, 2);

    private static HostMemory ReadHostMemory()
    {
        var status = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };

        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new HostMemory(
            status.MemoryLoad,
            Math.Round(status.AvailablePhysical / BytesPerMegabyte, 2));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    private sealed record HostMemory(double PressurePercent, double AvailableMb);
}
