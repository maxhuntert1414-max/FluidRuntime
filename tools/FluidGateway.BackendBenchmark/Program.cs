using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FluidLink;
using FluidRuntime.Native;
using FluidRuntime.Runtime;

if (args.Length != 3)
    throw new ArgumentException("Usage: benchmark <server.exe> <FluidGatewayNative.dll> <output.json>");
var executable = Path.GetFullPath(args[0]);
var dll = Path.GetFullPath(args[1]);
var output = Path.GetFullPath(args[2]);
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
var serverHash = Hash(executable);
var dllHash = Hash(dll);
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
listener.Stop();
var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
foreach (var value in new[] { "serve-events", "--host", "127.0.0.1", "--port", port.ToString() })
    start.ArgumentList.Add(value);
using var server = Process.Start(start) ?? throw new IOException("Cannot start owned server.");
try
{
    for (var attempt = 0; ; attempt++)
    {
        try { using var ready = new TcpClient(); await ready.ConnectAsync(IPAddress.Loopback, port); break; }
        catch (SocketException) when (attempt < 100 && !server.HasExited) { await Task.Delay(50); }
    }
    using var module = new NativeGatewayLibrary(dll, dllHash);
    using var isolated = new FluidLinkGatewayUpdateUploadAuthorizer("127.0.0.1", port,
        TimeSpan.FromSeconds(5), server.Id, serverHash);
    using var embedded = new FluidLinkGatewayUpdateUploadAuthorizer(module, TimeSpan.FromSeconds(5));
    var results = new List<Sample>();
    for (var pair = -5; pair < 30; pair++)
    {
        foreach (var mode in pair % 2 == 0 ? new[] { "server", "inprocess" } : new[] { "inprocess", "server" })
        {
            var batch = await Batch(mode, Math.Max(pair, 0));
            var authorization = await Authorize(mode, Math.Max(pair, 0));
            if (pair >= 0) { results.Add(batch); results.Add(authorization); }
        }
    }
    var summaries = results.GroupBy(x => new { x.Mode, x.Workload }).Select(group =>
    {
        var samples = group.ToArray();
        var latencies = samples.SelectMany(x => x.LatenciesUs).Order().ToArray();
        var operations = samples.Sum(x => x.Operations);
        return new
        {
            group.Key.Mode,
            group.Key.Workload,
            Pairs = samples.Length,
            Calls = latencies.Length,
            Operations = operations,
            P50Us = Percentile(latencies, .5),
            P95Us = Percentile(latencies, .95),
            P99Us = Percentile(latencies, .99),
            CpuNsPerOperation = samples.Sum(x => x.HostCpuNs + x.ServerCpuNs) / (double)operations,
            CpuCyclesPerOperation = samples.Sum(x => (double)x.HostCycles + x.ServerCycles) / operations,
            ManagedAllocatedBytesPerOperation = samples.Sum(x => x.ManagedAllocatedBytes) / (double)operations,
            CallsPerSecond = latencies.Length * 1e6 / latencies.Sum(),
            OperationsPerSecond = operations * 1e6 / latencies.Sum(),
            MaxHostPrivateBytes = samples.Max(x => x.HostPrivateBytes),
            MaxServerPrivateBytes = samples.Max(x => x.ServerPrivateBytes)
        };
    }).ToArray();
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
    {
        Schema = "fluidgateway-inprocess-ab-v1",
        CreatedUtc = DateTimeOffset.UtcNow,
        ServerSha256 = serverHash,
        LibrarySha256 = dllHash,
        RuntimeAssemblySha256 = Hash(typeof(FluidLinkGatewayUpdateUploadAuthorizer).Assembly.Location),
        ClientAssemblySha256 = Hash(typeof(FluidLinkV2Client).Assembly.Location),
        BenchmarkAssemblySha256 = Hash(System.Reflection.Assembly.GetExecutingAssembly().Location),
        Framework = RuntimeInformation.FrameworkDescription,
        OS = RuntimeInformation.OSDescription,
        ProcessorCount = Environment.ProcessorCount,
        WarmupPairs = 5,
        MeasuredPairs = 30,
        Scope = new[]
        {
            "Same .NET client, FluidLink v2 batch codec and C++ core; alternate AB/BA order.",
            "Batch: 64 calls x 128 operations, connection/setup outside timed region, DLL preloaded.",
            "Authorization: 32 complete fresh-session 10-exchange authorizations per pair/backend; no GPU actuation.",
            "Latency covers validation and transport; CPU/allocated bytes also include loop bookkeeping and decision checks.",
            "Managed allocated bytes: GC.GetTotalAllocatedBytes(true), all managed threads, not object count or native heap.",
            "CPU: host plus owned server in isolated mode; DLL CPU runs in host. CPU cycles supplement coarse Windows CPU time.",
            "DLL native metrics: retained PMR allocations and explicit payload copies in decode_frame/encode_frame_into only.",
            "Server native allocation/copy totals and OS/kernel/STL copies are unmeasured, not zero; wire bytes are not copy counts.",
            "DLL loading/hash cost is one-time and excluded; no typed/zero-copy calls, FPS or input-delay claims."
        },
        Summaries = summaries,
        Samples = results
    }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    Console.WriteLine(JsonSerializer.Serialize(summaries, new JsonSerializerOptions { WriteIndented = true }));

    async Task<Sample> Batch(string mode, int pair)
    {
        using var native = mode == "inprocess" ? module.CreateTransport() : null;
        await using var client = native is null ? new FluidLinkV2Client("127.0.0.1", port) : new FluidLinkV2Client(native);
        await client.HandshakeBatchAsync("backend-benchmark", "1");
        await client.SendResourceEventAsync(FluidLinkV2ResourceEvent.Register("ram", FluidLinkV2ResourceKind.Buffer,
            FluidLinkV2MemoryLayer.Ram, FluidLinkV2Lifetime.Session, 4194304));
        await client.SendResourceEventAsync(FluidLinkV2ResourceEvent.Register("vram", FluidLinkV2ResourceKind.Buffer,
            FluidLinkV2MemoryLayer.Vram, FluidLinkV2Lifetime.Session, 4194304));
        var beforeNative = native?.ReadMetrics();
        var times = new double[64];
        var before = Measure(mode);
        for (var i = 0; i < times.Length; i++)
        {
            var request = new FluidLinkV2OperationBatchEvent(Guid.NewGuid().ToString("N"), 128,
                FluidLinkV2OperationType.Upload, FluidLinkV2Queue.Copy, 0, 4194304, "ram", "vram");
            var started = Stopwatch.GetTimestamp();
            var reply = await client.SendOperationBatchAsync(request);
            times[i] = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
            for (var j = 0; j < reply.Decisions.Count; j++)
            {
                var expected = i == 0 && j == 0 ? FluidLinkV2DecisionOpcode.Execute : FluidLinkV2DecisionOpcode.DeduplicateIdenticalTransfer;
                if (!reply.Decisions[j].Accepted || reply.Decisions[j].DecisionOpcode != expected)
                    throw new InvalidDataException("Batch decision parity failed.");
            }
        }
        var after = Measure(mode);
        var metrics = native?.ReadMetrics();
        await client.GoodbyeAsync();
        return Sample.Create(mode, "batch-128", pair, 64 * 128, times, before, after, beforeNative, metrics);
    }

    async Task<Sample> Authorize(string mode, int pair)
    {
        var times = new double[32];
        var before = Measure(mode);
        for (var i = 0; i < times.Length; i++)
        {
            var request = new GatewayUpdateUploadAuthorizationRequest(pair * 32 + i, "measured", 4194304, 128,
                serverHash, dllHash);
            var started = Stopwatch.GetTimestamp();
            var result = await (mode == "server" ? isolated : embedded).AuthorizeAsync(request);
            times[i] = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
            result.EnsureMatchesNativePolicy(4194304, 128, request.PairIndex, "measured", serverHash, dllHash);
        }
        return Sample.Create(mode, "authorization-128", pair, 32 * 128, times, before, Measure(mode));
    }

    Snapshot Measure(string mode)
    {
        using var host = Process.GetCurrentProcess();
        host.Refresh(); server.Refresh();
        var allocation = GC.GetTotalAllocatedBytes(true);
        return new(allocation, host.TotalProcessorTime.Ticks * 100, mode == "server" ? server.TotalProcessorTime.Ticks * 100 : 0,
            Cycles(host), mode == "server" ? Cycles(server) : 0, host.PrivateMemorySize64,
            mode == "server" ? server.PrivateMemorySize64 : 0);
    }
}
finally
{
    if (!server.HasExited) server.Kill();
    await server.WaitForExitAsync();
}

static double Percentile(double[] values, double p)
{
    var index = (values.Length - 1) * p;
    var lower = (int)index;
    return values[lower] + (values[Math.Min(lower + 1, values.Length - 1)] - values[lower]) * (index - lower);
}
static ulong Cycles(Process process)
{
    if (!CpuNative.QueryProcessCycleTime(process.Handle, out var cycles))
        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    return cycles;
}
internal static class CpuNative
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryProcessCycleTime(nint handle, out ulong cycles);
}
internal sealed record Snapshot(long Allocation, long HostCpu, long ServerCpu, ulong HostCycles, ulong ServerCycles,
    long HostPrivate, long ServerPrivate);
internal sealed record Sample(string Mode, string Workload, int Pair, int Operations, double[] LatenciesUs,
    long ManagedAllocatedBytes, long HostCpuNs, long ServerCpuNs, ulong HostCycles, ulong ServerCycles,
    long HostPrivateBytes, long ServerPrivateBytes, NativeGatewayMetrics? NativeBefore, NativeGatewayMetrics? NativeAfter)
{
    internal static Sample Create(string mode, string workload, int pair, int operations, double[] times,
        Snapshot before, Snapshot after, NativeGatewayMetrics? nativeBefore = null, NativeGatewayMetrics? nativeAfter = null) =>
        new(mode, workload, pair, operations, times, after.Allocation - before.Allocation,
            after.HostCpu - before.HostCpu, after.ServerCpu - before.ServerCpu,
            after.HostCycles - before.HostCycles, after.ServerCycles - before.ServerCycles,
            after.HostPrivate, after.ServerPrivate, nativeBefore, nativeAfter);
}
