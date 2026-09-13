using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluidRuntime.Cli;
using FluidRuntime.Native;
using FluidRuntime.Runtime;

namespace FluidRuntime.Tests;

public sealed class NativeTelemetryFactAttribute : FactAttribute
{
    public NativeTelemetryFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Path.Combine(
            Environment.GetEnvironmentVariable("FLUIDRUNTIME_NATIVE") ?? "", "fluidruntime-native-probe.exe")))
            Skip = "Set FLUIDRUNTIME_NATIVE to the built native directory.";
    }
}

public sealed class DailyMonitorTests
{
    [Fact]
    public void Defaults_are_read_only_bounded_and_need_no_ledger()
    {
        var options = DailyMonitorOptions.Parse(["monitor", "--pid", "42"]);
        Assert.Equal(1000, options.IntervalMs);
        Assert.Equal(0, options.Seconds);
        Assert.EndsWith("monitor-latest.json", options.Output);
        Assert.EndsWith("fluidruntime-native-probe.exe", options.Probe);
    }

    [Theory]
    [InlineData("--pid", "0")]
    [InlineData("--pid", "-1")]
    [InlineData("--pid", "4294967296")]
    [InlineData("--interval-ms", "999")]
    [InlineData("--interval-ms", "10001")]
    [InlineData("--seconds", "0")]
    [InlineData("--seconds", "86401")]
    [InlineData("--seconds", "NaN")]
    [InlineData("--out", "target.exe")]
    [InlineData("--priority-seconds", "30")]
    [InlineData("--hook", "true")]
    public void Invalid_or_actuating_options_are_rejected(string option, string value)
    {
        Assert.Throws<ArgumentException>(() => DailyMonitorOptions.Parse(
            option == "--pid" ? ["monitor", option, value] : ["monitor", "--pid", "42", option, value]));
    }

    [Fact]
    public void Missing_duplicate_and_conflicting_arguments_fail()
    {
        Assert.Throws<ArgumentException>(() => DailyMonitorOptions.Parse(["monitor"]));
        Assert.Throws<ArgumentException>(() => DailyMonitorOptions.Parse(["monitor", "--pid"]));
        Assert.Throws<ArgumentException>(() => DailyMonitorOptions.Parse(["monitor", "--pid", "42", "--pid", "43"]));
        Assert.Throws<ArgumentException>(() => DailyMonitorOptions.Parse(
            ["monitor", "--pid", "42", "--cpu-only", "--native-probe", "probe.exe"]));
        Assert.Throws<ArgumentException>(() => DailyMonitorOptions.Parse(
            ["monitor", "--pid", "42", "--cpu-only", "--cpu-only"]));
        Assert.Null(DailyMonitorOptions.Parse(["monitor", "--pid", "42", "--cpu-only"]).Probe);
    }

    [Fact]
    public void History_evicts_oldest_samples_not_identity_or_total()
    {
        var history = new DailyMonitorHistory();
        for (var i = 0; i < 1000000; i++) history.Add(Sample(i));
        Assert.Equal(1000000, history.TotalSamples);
        Assert.Equal(600, history.Snapshot().Length);
        Assert.Equal(999400, history.Snapshot()[0].ElapsedSeconds);
        Assert.Equal(999999, history.Snapshot()[^1].ElapsedSeconds);
    }

    [Fact]
    public void Cpu_is_normalized_and_console_text_cannot_inject_control_sequences()
    {
        Assert.Equal(100, DailyMonitorRunner.CpuPercent(TimeSpan.FromSeconds(10000), TimeSpan.FromSeconds(1)));
        Assert.Equal(0, DailyMonitorRunner.CpuPercent(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1)));
        Assert.Equal(0, DailyMonitorRunner.CpuPercent(TimeSpan.Zero, TimeSpan.Zero));
        Assert.DoesNotContain('\x1b', DailyMonitorRunner.SafeText("app\x1b[2J\n"));
        Assert.Equal(240, DailyMonitorRunner.SafeText(new string('x', 10000)).Length);
    }

    [Fact]
    public void Process_list_formats_numeric_ram_with_stable_alignment()
    {
        Assert.Equal("    42       1.5   app", DailyMonitorRunner.FormatProcessRow(42, 1572864, "app"));
    }

    [Fact]
    public async Task Framing_accepts_fragmented_utf8()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"text\":\"a\\nb\"}");
        using var stream = new FragmentedStream(Frame(bytes));
        Assert.Equal(Encoding.UTF8.GetString(bytes), await NativeProbeStream.ReadRecordAsync(stream, TimeSpan.FromSeconds(1), default));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(262145u)]
    [InlineData(uint.MaxValue)]
    public async Task Frame_length_is_checked_before_allocation(uint length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeProbeStream.ReadRecordAsync(stream, TimeSpan.FromSeconds(1), default));
    }

    [Fact]
    public async Task Truncation_invalid_utf8_cancellation_and_deadline_are_distinct()
    {
        using var header = new MemoryStream(new byte[3]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => NativeProbeStream.ReadRecordAsync(header, TimeSpan.FromSeconds(1), default));
        using var body = new MemoryStream(new byte[] { 2, 0, 0, 0, 1 });
        await Assert.ThrowsAsync<EndOfStreamException>(() => NativeProbeStream.ReadRecordAsync(body, TimeSpan.FromSeconds(1), default));
        using var invalid = new MemoryStream(Frame([0xff]));
        await Assert.ThrowsAsync<DecoderFallbackException>(() => NativeProbeStream.ReadRecordAsync(invalid, TimeSpan.FromSeconds(1), default));
        using var slow = new SlowStream();
        await Assert.ThrowsAsync<TimeoutException>(() => NativeProbeStream.ReadRecordAsync(slow, TimeSpan.FromMilliseconds(10), default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeProbeStream.ReadRecordAsync(slow, TimeSpan.FromSeconds(1), cancelled.Token));
    }

    [Fact]
    public void Streaming_requires_creation_identity_and_valid_metrics()
    {
        var sample = new NativeProbeReport { ProcessStartTimeFileTime = 123, SampleIntervalMs = 1000, CapturedAtUnixMs = 2 };
        NativeProbeStream.Validate(sample, 123, 1000, 1);
        Assert.Throws<InvalidDataException>(() => NativeProbeStream.Validate(sample, 124, 1000, 1));
        Assert.Throws<InvalidDataException>(() => NativeProbeStream.Validate(sample, 123, 2000, 1));
        Assert.Throws<InvalidDataException>(() => NativeProbeStream.Validate(sample, 123, 1000, 2));
        foreach (var value in new[] { -1, double.NaN, double.PositiveInfinity })
            Assert.Throws<InvalidDataException>(() => NativeProbeStream.Validate(
                sample with { Gpu = new() { DedicatedUsageBytes = value } }, 123, 1000, 1));
    }

    [Fact]
    public async Task Cpu_only_monitor_persists_cancellation_and_leaves_target_running()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var target = Process.GetCurrentProcess();
        var output = TempReport();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(2200));
        try
        {
            var before = target.PriorityClass;
            var report = await DailyMonitorRunner.RunAsync(new(target.Id, 1000, 0, output, null), null, stop.Token);
            Assert.True(report.ReadOnly);
            Assert.False(report.WouldModifySystem);
            Assert.Equal("cancelled", report.StopReason);
            Assert.Equal("disabled", report.GpuStatus);
            Assert.InRange(report.TotalSamples, 1, 2);
            Assert.All(report.Samples, s => Assert.Null(s.GpuDedicatedBytes));
            Assert.False(target.HasExited);
            Assert.Equal(before, target.PriorityClass);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            Assert.Equal("stopped", json.RootElement.GetProperty("state").GetString());
            Assert.True(new FileInfo(output).Length < 1024 * 1024);
        }
        finally { Cleanup(output); }
    }

    [Fact]
    public async Task Cancellation_before_start_never_opens_target_or_output()
    {
        var output = TempReport();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DailyMonitorRunner.RunAsync(
            new(int.MaxValue, 1000, 0, output, "missing.exe"), null, stop.Token));
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(output + ".lock"));
    }

    [Fact]
    public async Task Output_lock_prevents_concurrent_report_overwrite()
    {
        if (!OperatingSystem.IsWindows()) return;
        var output = TempReport();
        try
        {
            using var held = new FileStream(output + ".lock", FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await Assert.ThrowsAsync<IOException>(() => DailyMonitorRunner.RunAsync(
                new(Environment.ProcessId, 1000, 1, output, null), null, default));
            Assert.False(File.Exists(output));
        }
        finally { Cleanup(output); }
    }

    [NativeTelemetryFact]
    public async Task Real_native_stream_verifies_identity_and_disposes_on_early_stop()
    {
        using var target = Process.GetCurrentProcess();
        var path = Path.Combine(Environment.GetEnvironmentVariable("FLUIDRUNTIME_NATIVE")!, "fluidruntime-native-probe.exe");
        var creation = target.StartTime.ToUniversalTime().ToFileTimeUtc();
        await using (var reader = NativeProbeStream.ReadAsync(path, target.Id, creation, 1000, 2, default).GetAsyncEnumerator())
        {
            Assert.True(await reader.MoveNextAsync());
            Assert.Equal(target.Id, reader.Current.ProcessId);
            Assert.Equal(creation, reader.Current.ProcessStartTimeFileTime);
        }
        await using var wrong = NativeProbeStream.ReadAsync(path, target.Id, creation + 1, 1000, 1, default).GetAsyncEnumerator();
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await wrong.MoveNextAsync());
        Assert.False(target.HasExited);
    }

    [NativeTelemetryFact]
    public async Task Real_native_stream_finishes_exact_count_and_legacy_series_still_works()
    {
        using var target = Process.GetCurrentProcess();
        var path = Path.Combine(Environment.GetEnvironmentVariable("FLUIDRUNTIME_NATIVE")!, "fluidruntime-native-probe.exe");
        var samples = new List<NativeProbeReport>();
        await foreach (var sample in NativeProbeStream.ReadAsync(path, target.Id,
            target.StartTime.ToUniversalTime().ToFileTimeUtc(), 1000, 2, default)) samples.Add(sample);
        Assert.Equal(2, samples.Count);
        Assert.True(samples[1].CapturedAtUnixMs > samples[0].CapturedAtUnixMs);
        var legacy = await new NativeProbeClient().ProbeSeriesAsync(path, target.Id, 50, 2, TimeSpan.FromSeconds(15));
        Assert.Equal(2, legacy.Count);
    }

    [Fact]
    public async Task Target_exit_stops_monitor_and_saves_final_report()
    {
        if (!OperatingSystem.IsWindows()) return;
        var output = TempReport();
        using var child = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            Arguments = "-NoProfile -NonInteractive -Command Start-Sleep -Seconds 3",
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        try
        {
            var report = await DailyMonitorRunner.RunAsync(new(child.Id, 1000, 20, output, null), null, default)
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("process-exited", report.StopReason);
            Assert.Equal("stopped", report.State);
            Assert.True(child.HasExited);
            Assert.Contains("process-exited", await File.ReadAllTextAsync(output));
        }
        finally { await OwnedProcessLifetime.TerminateAsync(child); Cleanup(output); }
    }

    [Fact]
    public async Task Missing_or_invalid_native_probe_degrades_once_without_stopping_cpu_monitor()
    {
        if (!OperatingSystem.IsWindows()) return;
        var output = TempReport();
        var fake = Path.ChangeExtension(output, ".exe");
        try
        {
            // Both failure modes must leave an honest report, not invented GPU zeros.
            foreach (var exists in new[] { false, true })
            {
                if (exists) await File.WriteAllTextAsync(fake, "not an executable");
                var report = await DailyMonitorRunner.RunAsync(new(Environment.ProcessId, 1000, 2, output, fake), null, default);
                Assert.Equal("stopped", report.State);
                Assert.Equal("unavailable", report.GpuStatus);
                Assert.Equal("duration", report.StopReason);
                Assert.NotNull(report.Warning);
                Assert.NotEmpty(report.Samples);
                Assert.All(report.Samples, s => Assert.Null(s.GpuEnginePeakPercent));
            }
        }
        finally { File.Delete(fake); Cleanup(output); }
    }

    private static DailyMonitorSample Sample(int n) => new(DateTimeOffset.UnixEpoch, n, 0, 1, 1, null, null, null);
    private static string TempReport() => Path.Combine(Path.GetTempPath(), $"fluid-daily-{Guid.NewGuid():N}.json");
    private static void Cleanup(string output) { File.Delete(output); File.Delete(output + ".lock"); }
    private static byte[] Frame(byte[] body)
    {
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }
    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    }
    private sealed class SlowStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }
}
