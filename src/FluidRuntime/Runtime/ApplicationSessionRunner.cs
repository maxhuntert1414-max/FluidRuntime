using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FluidRuntime.Runtime;

internal sealed record ApplicationSample(double ElapsedMilliseconds, double CpuMilliseconds,
    long WorkingSetBytes, long PrivateBytes, int ThreadCount, Dictionary<string, long> Vulkan);
internal sealed record ApplicationSessionReport(string Schema, DateTimeOffset CreatedAtUtc, int ProcessId,
    string Executable, string ExecutableSha256, string LayerSha256, string ArgumentsSha256,
    int RequestedSeconds, bool ObservationRequested,
    bool LayerVerified, bool NativeActuationEnabled, bool PerformanceClaimAllowed, bool ProcessExited,
    int? ExitCode, double ElapsedMilliseconds, string? Failure, IReadOnlyList<ApplicationSample> Samples,
    PriorityLeaseReport? WindowsPriority, IReadOnlyList<string> Limitations);

internal static class ApplicationSessionRunner
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    internal static async Task<int> RunCommandAsync(string[] args)
    {
        if (args is ["app-session", "--help"]) { Console.WriteLine(ApplicationSessionOptions.Usage); return 0; }
        using var cancelled = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancelled.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var options = ApplicationSessionOptions.Parse(args);
            var report = await RunAsync(options, cancelled.Token);
            Console.WriteLine($"Session PID {report.ProcessId}: layer verified={report.LayerVerified}, " +
                $"samples={report.Samples.Count}, priority={report.WindowsPriority?.Restoration ?? "unchanged"}.");
            Console.WriteLine($"Report: {options.Output}. Application is not forcibly terminated.");
            if (report.Failure is not null) Console.Error.WriteLine(report.Failure);
            return report.Failure is null && (!options.ObserveVulkan || report.LayerVerified) ? 0 : 1;
        }
        catch (Exception error) { Console.Error.WriteLine($"Application session failed: {error.Message}"); return 1; }
        finally { Console.CancelKeyPress -= handler; }
    }

    internal static async Task<ApplicationSessionReport> RunAsync(ApplicationSessionOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        WindowsPriorityLease.RequireUnelevated();
        ApplicationSessionOptions.RequireApplicationPath(options.Executable);
        var library = Path.Combine(options.LayerDirectory, "fluidruntime-vulkan-observe.dll");
        using var binding = OwnedBinaryBinding.Open(options.Executable, library);
        var manifestPath = Path.Combine(options.LayerDirectory, "vulkan-observe.json");
        using var manifestLock = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var manifest = JsonDocument.Parse(manifestLock);
        ValidateManifest(manifest.RootElement);
        Directory.CreateDirectory(Path.GetDirectoryName(options.Output)!);
        using var observation = new VulkanObservationReader();
        var start = new ProcessStartInfo(options.Executable)
        { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(options.Executable)!, CreateNoWindow = true };
        foreach (var argument in options.Arguments) start.ArgumentList.Add(argument);
        string Existing(string key) => start.Environment.TryGetValue(key, out var value) ? value ?? "" : "";
        if (options.ObserveVulkan)
        {
            var pathKey = start.Environment.ContainsKey("VK_LAYER_PATH") ? "VK_LAYER_PATH" : "VK_ADD_LAYER_PATH";
            start.Environment[pathKey] = options.LayerDirectory + ";" + Existing(pathKey);
            start.Environment["VK_INSTANCE_LAYERS"] = "VK_LAYER_FLUIDRUNTIME_observe;" +
                Existing("VK_INSTANCE_LAYERS");
            start.Environment["FLUIDRUNTIME_OBSERVE_MAPPING"] = observation.Name;
            start.Environment["FLUIDRUNTIME_OBSERVE_EXE"] = options.Executable;
        }
        else if (Existing("VK_INSTANCE_LAYERS").Contains("VK_LAYER_FLUIDRUNTIME_observe"))
            throw new InvalidOperationException("Baseline refuses an inherited FluidRuntime observation layer.");
        var samples = new List<ApplicationSample>();
        var verified = false;
        string? failure = null;
        Process? watchdog = null;
        Task<string>? watchdogError = null;
        var stopName = "Local\\FluidRuntimePriority-" + Guid.NewGuid().ToString("N");
        using var stopSignal = new EventWaitHandle(false, EventResetMode.ManualReset, stopName);
        var leasePath = options.Output + ".priority-" + Guid.NewGuid().ToString("N") + ".json";
        PriorityLeaseReport? lease = null;
        token.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot launch application.");
        using var exitWaitCancellation = new CancellationTokenSource();
        var processExit = process.WaitForExitAsync(exitWaitCancellation.Token);
        try
        {
            while (timer.Elapsed.TotalSeconds < options.Seconds)
            {
                token.ThrowIfCancellationRequested();
                process.Refresh();
                var counters = observation.Read(process.Id);
                if (process.HasExited) break;
                if (!verified && options.ObserveVulkan && counters["devices"] > 0)
                {
                    binding.ValidateLaunchedProcess(process);
                    verified = true;
                    if (options.PrioritySeconds > 0)
                    {
                        var helper = HelperStartInfo();
                        foreach (var argument in new[] { "windows-priority-lease", process.Id.ToString(),
                            process.StartTime.ToUniversalTime().Ticks.ToString(), binding.TargetSha256,
                            options.PrioritySeconds.ToString(), stopName, leasePath }) helper.ArgumentList.Add(argument);
                        watchdog = Process.Start(helper) ?? throw new InvalidOperationException("Cannot start priority watchdog.");
                        watchdogError = watchdog.StandardError.ReadToEndAsync();
                    }
                }
                samples.Add(new(timer.Elapsed.TotalMilliseconds, process.TotalProcessorTime.TotalMilliseconds,
                    process.WorkingSet64, process.PrivateMemorySize64, process.Threads.Count, counters));
                var remaining = TimeSpan.FromSeconds(options.Seconds) - timer.Elapsed;
                if (remaining > TimeSpan.Zero)
                    await WaitForSampleAsync(processExit, remaining, token);
            }
        }
        catch (OperationCanceledException) { failure = "Session cancelled; telemetry stopped and priority restoration requested."; }
        catch (Exception error) { failure = error.Message; }
        finally
        {
            observation.Stop();
            timer.Stop();
            stopSignal.Set();
            exitWaitCancellation.Cancel();
            try { await processExit; }
            catch (OperationCanceledException) { }
            if (watchdog is not null)
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                    await watchdog.WaitForExitAsync(deadline.Token);
                    var errors = await watchdogError!;
                    if (File.Exists(leasePath)) lease = JsonSerializer.Deserialize<PriorityLeaseReport>(
                        await File.ReadAllTextAsync(leasePath), JsonOptions);
                    if (watchdog.ExitCode != 0 || lease is null) failure = $"Priority watchdog did not confirm restoration: {errors}";
                }
                catch (Exception error) { failure = $"Priority restoration unverified: {error.Message}"; }
                finally { watchdog.Dispose(); }
            }
        }
        process.Refresh();
        var exited = process.HasExited;
        Dictionary<string, long> final;
        try { final = observation.Read(process.Id); }
        catch (InvalidDataException error)
        {
            failure ??= error.Message;
            final = samples.LastOrDefault()?.Vulkan ?? VulkanObservationReader.Counters.ToDictionary(name => name, _ => 0L);
        }
        samples.Add(new(timer.Elapsed.TotalMilliseconds, samples.LastOrDefault()?.CpuMilliseconds ?? 0,
            samples.LastOrDefault()?.WorkingSetBytes ?? 0, samples.LastOrDefault()?.PrivateBytes ?? 0,
            samples.LastOrDefault()?.ThreadCount ?? 0, final));
        if (options.ObserveVulkan && !verified) failure ??= "The selected process did not expose a verified Vulkan device during this session.";
        if (exited && process.ExitCode != 0) failure ??= $"Application exited with code {process.ExitCode}.";
        var argumentsHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(options.Arguments))));
        var report = new ApplicationSessionReport("fluidruntime-application-session-v4", DateTimeOffset.UtcNow,
            process.Id, options.Executable, binding.TargetSha256, binding.HookSha256, argumentsHash,
            options.Seconds, options.ObserveVulkan,
            verified, false, false, exited, exited ? process.ExitCode : null, timer.Elapsed.TotalMilliseconds,
            failure, samples, lease,
            ["Opt-in x64 process-launch observation; no attach, remote injection, driver or registry changes.",
             "Vulkan calls are forwarded; incomplete write/alias provenance never authorizes copy elision.",
             "Recorded copy bytes are not executed GPU bytes; command buffers may be replayed or discarded.",
             "Submitted copy totals cover fully attributed successful queue calls, not GPU completion or physical traffic.",
             "Completed totals are driver-reported queue-prefix completion from existing fence/status/idle calls, not content validation or successful execution after device loss.",
             "Wait-any ambiguity, external fence extensions, concurrent-queue extensions and capacity limits reduce completion coverage; no waits or polls are inserted.",
             "Pending or abandoned tracked submits mean completion was not observed, not that the GPU failed to finish.",
             "Command generations cover primary/secondary recording and replay, not shader writes, pending-state validation or resource validity.",
             "Unknown submit chains, nested secondaries, changed generations and capacity limits exclude the whole call from submitted totals.",
             "Allocation sizes are logical Vulkan requests, not measured VRAM residency or physical traffic.",
             "Buffer-copy categories use bound Vulkan memory-type flags at recording time, not physical transfer direction.",
             "Memory both HOST_VISIBLE and DEVICE_LOCAL has its own category; same-allocation copies do not imply redundancy.",
             "Unknown extension chains, sparse/protected buffers and untracked bindings remain unclassified.",
             "Counter saturation is reported explicitly; affected totals are partial, not exact measurements.",
             "Counters are independently atomic, not a simultaneous snapshot; extension coverage is partial.",
             "Elapsed time is the collector capture window, not frame latency or an exact application benchmark.",
             "The terminal sample carries final Vulkan counters and the last available CPU/memory sample.",
             "Stopping capture disables counter writes. The layer remains loaded until the application exits.",
             "No game-performance claim; compare repeated baseline/observed runs and inspect overhead.",
             "Anti-cheat/protected applications are unsupported; acknowledgement is not automatic compatibility detection.",
             "Priority watchdog survives collector exit, but forcibly killing the watchdog can prevent restoration."]);
        await AtomicJsonFile.WriteTextAsync(options.Output, JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine,
            CancellationToken.None);
        return report;
    }

    internal static async Task WaitForSampleAsync(Task processExit, TimeSpan remaining, CancellationToken token)
    {
        var interval = remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250);
        try { await processExit.WaitAsync(interval, token); }
        catch (TimeoutException) { }
    }

    internal static void ValidateManifest(JsonElement root)
    {
        var layer = root.GetProperty("layer");
        if (root.GetProperty("file_format_version").GetString() != "1.2.0" ||
            layer.GetProperty("name").GetString() != "VK_LAYER_FLUIDRUNTIME_observe" ||
            layer.GetProperty("type").GetString() != "GLOBAL" ||
            layer.GetProperty("library_path").GetString() != ".\\fluidruntime-vulkan-observe.dll" ||
            layer.GetProperty("functions").GetProperty("vkNegotiateLoaderLayerInterfaceVersion").GetString() != "fgNegotiate")
            throw new InvalidDataException("Unexpected observation layer manifest.");
    }
    private static ProcessStartInfo HelperStartInfo()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Runtime executable unavailable.");
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        return start;
    }
}
