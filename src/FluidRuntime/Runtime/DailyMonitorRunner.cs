using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FluidRuntime.Cli;
using FluidRuntime.Native;

namespace FluidRuntime.Runtime;

internal static class DailyMonitorRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    internal static async Task<int> RunCommandAsync(string[] args)
    {
        if (args.Length == 0 || args.Skip(1).SequenceEqual(new[] { "--help" }))
        {
            Console.WriteLine(DailyMonitorOptions.Usage);
            return 0;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Daily monitoring requires Windows.");
            if (args[0] == "processes") return ListProcesses(args);
            var options = DailyMonitorOptions.Parse(args);
            Console.WriteLine($"FluidRuntime | READ ONLY | PID {options.ProcessId} | Ctrl+C to stop");
            Console.WriteLine($"Report: {options.Output}");
            Console.WriteLine("CPU%   RAM MiB   Private MiB   GPU engine%   Dedicated MiB   Shared MiB");
            var report = await RunAsync(options, Render, cancellation.Token);
            Console.WriteLine($"\nStopped: {report.StopReason}; {report.TotalSamples} samples; GPU: {report.GpuStatus}.");
            if (report.Warning is not null) Console.WriteLine($"Note: {SafeText(report.Warning)}");
            return report.State == "failed" ? 1 : 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or Win32Exception or
            InvalidOperationException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Console.Error.WriteLine($"FluidRuntime: {SafeText(error.Message)}");
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    internal static async Task<DailyMonitorReport> RunAsync(
        DailyMonitorOptions options, Action<DailyMonitorSample>? display, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var target = Process.GetProcessById(options.ProcessId);
        // Force a retained OS handle before reading identity. No PID-following on restart.
        _ = target.Handle;
        var startTime = target.StartTime.ToUniversalTime().ToFileTimeUtc();
        var name = target.ProcessName;
        var startedAt = DateTimeOffset.UtcNow;
        var sessionId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(options.Output)!);
        // Keep the lock file: deleting it could race with the next monitor's open.
        using var outputLock = new FileStream(options.Output + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var history = new DailyMonitorHistory();
        var gpuStatus = options.Probe is null ? "disabled" : File.Exists(options.Probe) ? "starting" : "unavailable";
        string? warning = gpuStatus == "unavailable" ? "Native probe not found; CPU/RAM only. Use --native-probe or the portable package." : null;
        var state = "running";
        string? reason = null;
        var clock = Stopwatch.StartNew();
        var lastWrite = TimeSpan.Zero;
        var lastDisplay = TimeSpan.FromSeconds(-5);
        var previousTime = clock.Elapsed;
        var previousCpu = target.TotalProcessorTime;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.Seconds > 0) stop.CancelAfter(TimeSpan.FromSeconds(options.Seconds));
        var exitWatch = WatchExitAsync(target, stop);
        try
        {
            await SaveAsync();
            var useProbe = gpuStatus == "starting";
            while (!stop.IsCancellationRequested)
            {
                if (useProbe)
                {
                    await using var reader = NativeProbeStream.ReadAsync(options.Probe!, options.ProcessId,
                        startTime, options.IntervalMs, 100000 / options.IntervalMs, stop.Token).GetAsyncEnumerator();
                    while (useProbe)
                    {
                        try
                        {
                            if (!await reader.MoveNextAsync()) break;
                        }
                        catch (Exception error) when (error is IOException or InvalidDataException or Win32Exception or
                            TimeoutException or JsonException or ArgumentException)
                        {
                            if (stop.IsCancellationRequested) break;
                            // One failure disables the helper for this session; never spin-retry.
                            useProbe = false;
                            gpuStatus = "unavailable";
                            warning = $"Native telemetry stopped: {error.Message}. Continuing CPU/RAM only.";
                            break;
                        }
                        await CaptureAsync(reader.Current);
                    }
                }
                else
                {
                    await Task.Delay(options.IntervalMs, stop.Token);
                    await CaptureAsync(null);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when ((error is Win32Exception or InvalidOperationException) && target.HasExited)
        {
            // The process can exit between the liveness check and a metric query.
        }
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            state = "failed";
            reason = "collection-error";
            warning = error.Message;
        }
        finally
        {
            stop.Cancel();
            await exitWatch;
            if (state != "failed")
            {
                state = "stopped";
                reason = target.HasExited ? "process-exited" : cancellationToken.IsCancellationRequested ? "cancelled" : "duration";
            }
            // Final persistence is independent of user cancellation; never terminate the target.
            await SaveAsync();
        }
        return Report();

        DailyMonitorReport Report() => new(sessionId, options.ProcessId, name, startTime,
            startedAt, DateTimeOffset.UtcNow, state, reason, gpuStatus, warning, history.TotalSamples, history.Snapshot());

        Task SaveAsync() => AtomicJsonFile.WriteTextAsync(options.Output, JsonSerializer.Serialize(Report(), JsonOptions));

        async Task CaptureAsync(NativeProbeReport? native)
        {
            stop.Token.ThrowIfCancellationRequested();
            target.Refresh();
            if (target.HasExited) { stop.Cancel(); return; }
            var now = clock.Elapsed;
            var cpu = target.TotalProcessorTime;
            var cpuPercent = CpuPercent(cpu - previousCpu, now - previousTime);
            previousCpu = cpu;
            previousTime = now;
            if (native is not null)
                gpuStatus = native.Capabilities.GpuEngineUtilization && native.Capabilities.GpuProcessMemory
                    ? "available" : "partial-or-unavailable";
            var sample = new DailyMonitorSample(DateTimeOffset.UtcNow, Math.Round(now.TotalSeconds, 3), cpuPercent,
                target.WorkingSet64, target.PrivateMemorySize64,
                native?.Capabilities.GpuEngineUtilization == true ? native.Gpu.EngineUtilizationPeakPercent : null,
                native?.Capabilities.GpuProcessMemory == true ? native.Gpu.DedicatedUsageBytes : null,
                native?.Capabilities.GpuProcessMemory == true ? native.Gpu.SharedUsageBytes : null);
            history.Add(sample);
            if (!Console.IsOutputRedirected || now - lastDisplay >= TimeSpan.FromSeconds(5))
            {
                display?.Invoke(sample);
                lastDisplay = now;
            }
            if (now - lastWrite >= TimeSpan.FromSeconds(5))
            {
                await SaveAsync();
                lastWrite = now;
            }
        }
    }

    private static async Task WatchExitAsync(Process target, CancellationTokenSource stop)
    {
        try { await target.WaitForExitAsync(stop.Token); stop.Cancel(); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    internal static double CpuPercent(TimeSpan cpu, TimeSpan elapsed) => elapsed <= TimeSpan.Zero ? 0 :
        Math.Round(Math.Clamp(cpu.TotalMilliseconds / (elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100, 0, 100), 2);

    internal static string SafeText(string text) => new(text.Take(240).Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    internal static string FormatProcessRow(int pid, long workingSetBytes, string name) =>
        FormattableString.Invariant($"{pid,6} {workingSetBytes / (1024d * 1024),9:F1}   {SafeText(name)}");

    private static void Render(DailyMonitorSample sample)
    {
        static string Number(double? value) => value?.ToString("F1", CultureInfo.InvariantCulture) ?? "NA";
        const double mib = 1024 * 1024;
        var row = $"{Number(sample.CpuPercent),5} {Number(sample.WorkingSetBytes / mib),9} " +
            $"{Number(sample.PrivateBytes / mib),13} {Number(sample.GpuEnginePeakPercent),13} " +
            $"{Number(sample.GpuDedicatedBytes / mib),15} {Number(sample.GpuSharedBytes / mib),12}";
        if (Console.IsOutputRedirected) { Console.WriteLine(row); return; }
        var width = Math.Max(1, Console.WindowWidth - 1);
        Console.Write("\r" + row[..Math.Min(row.Length, width)].PadRight(Math.Min(80, width)));
    }

    private static int ListProcesses(string[] args)
    {
        if (args.Length != 1 && (args.Length != 3 || args[1] != "--name"))
            throw new ArgumentException("Usage: fluidruntime processes [--name text]");
        var filter = args.Length == 3 ? args[2] : "";
        using var current = Process.GetCurrentProcess();
        var session = current.SessionId;
        var rows = new List<(int Pid, string Name, long Ram)>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != session || process.HasExited ||
                        !process.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                    rows.Add((process.Id, SafeText(process.ProcessName), process.WorkingSet64));
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        Console.WriteLine("   PID   RAM MiB   Process (current Windows session)");
        foreach (var row in rows.OrderByDescending(r => r.Ram))
            Console.WriteLine(FormatProcessRow(row.Pid, row.Ram, row.Name));
        Console.WriteLine("\nSelect explicitly: fluidruntime monitor --pid <PID>");
        return 0;
    }
}
