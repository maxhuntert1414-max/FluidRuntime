using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using FluidRuntime.Runtime;

namespace FluidRuntime.Native;

// Read-only telemetry, not the FluidLink policy transport. Each child schedules
// at most 100 samples/100 seconds of sleep, plus PDH/IO time. Renew the same identity.
internal static class NativeProbeStream
{
    internal const int MaximumRecordBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async IAsyncEnumerable<NativeProbeReport> ReadAsync(
        string path, int processId, long startTimeFileTime, int intervalMs, int sampleCount,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (processId <= 0 || startTimeFileTime <= 0 || intervalMs is < 1000 or > 10000 ||
            sampleCount is < 1 or > 100 || (long)sampleCount * intervalMs > 100000)
            throw new ArgumentException("Invalid bounded probe session.");
        var start = new ProcessStartInfo(Path.GetFullPath(path))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[] { "--stream", "--pid", processId.ToString(CultureInfo.InvariantCulture),
            "--start-time", startTimeFileTime.ToString(CultureInfo.InvariantCulture),
            "--interval-ms", intervalMs.ToString(CultureInfo.InvariantCulture),
            "--samples", sampleCount.ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(arg);

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start native telemetry.");
        var stderr = DrainErrorAsync(process.StandardError, lifetime.Token);
        try
        {
            long previousTimestamp = 0;
            for (var i = 0; i < sampleCount; i++)
            {
                var json = await ReadRecordAsync(process.StandardOutput.BaseStream,
                    TimeSpan.FromMilliseconds(intervalMs + 15000), cancellationToken);
                var sample = NativeProbeReportParser.Parse(json, processId);
                Validate(sample, startTimeFileTime, intervalMs, previousTimestamp);
                previousTimestamp = sample.CapturedAtUnixMs;
                yield return sample;
            }

            try
            {
                using var exitDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                exitDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                // Reject trailing output and include pipe closure in the deadline.
                if (await process.StandardOutput.BaseStream.ReadAsync(new byte[1], exitDeadline.Token) != 0)
                    throw new InvalidDataException("Native telemetry returned extra records.");
                await process.WaitForExitAsync(exitDeadline.Token);
                var error = await stderr.WaitAsync(exitDeadline.Token);
                if (process.ExitCode != 0)
                    throw new IOException($"Native telemetry exited {process.ExitCode}: {error}");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Native telemetry did not close its output pipes.");
            }
        }
        finally
        {
            lifetime.Cancel();
            await OwnedProcessLifetime.TerminateAsync(process);
            try { await stderr; }
            catch (OperationCanceledException) { }
        }
    }

    internal static void Validate(NativeProbeReport sample, long startTime, int intervalMs, long previous)
    {
        if (sample.ProcessStartTimeFileTime != startTime || sample.SampleIntervalMs != intervalMs ||
            sample.CapturedAtUnixMs <= previous)
            throw new InvalidDataException("Native telemetry identity, interval or ordering mismatch.");
        if (sample.Process.WorkingSetBytes < 0 || sample.Process.PrivateBytes < 0 ||
            sample.Errors is null || sample.Errors.Count > 16)
            throw new InvalidDataException("Invalid native telemetry values.");
        foreach (var value in new[] { sample.Gpu.LocalUsageBytes, sample.Gpu.DedicatedUsageBytes,
            sample.Gpu.SharedUsageBytes, sample.Gpu.NonLocalUsageBytes,
            sample.Gpu.EngineUtilizationPeakPercent, sample.Gpu.EngineUtilizationSumPercent })
            if (value.HasValue && (!double.IsFinite(value.Value) || value < 0))
                throw new InvalidDataException("Non-finite or negative GPU counter.");
    }

    internal static async Task<string> ReadRecordAsync(Stream stream, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            var header = new byte[4];
            await stream.ReadExactlyAsync(header, deadline.Token);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (size is 0 or > MaximumRecordBytes)
                throw new InvalidDataException("Native telemetry record exceeds its size limit.");
            var payload = new byte[(int)size];
            await stream.ReadExactlyAsync(payload, deadline.Token);
            return StrictUtf8.GetString(payload);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Native telemetry stopped responding.");
        }
    }

    private static async Task<string> DrainErrorAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            // Drain the pipe without retaining an unbounded diagnostic stream.
            if (text.Length < 4096) text.Append(buffer, 0, Math.Min(count, 4096 - text.Length));
        }
        return text.ToString().Trim();
    }
}
