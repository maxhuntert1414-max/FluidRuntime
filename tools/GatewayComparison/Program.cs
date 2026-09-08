using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using FluidRuntime.Runtime;

// Test driver only: obtains authorizations but never publishes a native policy.
if (args.Length != 9)
{
    Console.Error.WriteLine("python-port python-pid python-sha native-port native-pid native-sha target hook output");
    return 2;
}
var targetSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[6]))).ToLowerInvariant();
var hookSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[7]))).ToLowerInvariant();
var names = new[] { "Python", "Native" };
var clients = new FluidLinkGatewayUpdateUploadAuthorizer[2];
var processes = new Process[2];
var samples = new List<Sample>[2] { [], [] };
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
try
{
    for (var i = 0; i < 2; ++i)
    {
        var port = int.Parse(args[i * 3], CultureInfo.InvariantCulture);
        var pid = int.Parse(args[i * 3 + 1], CultureInfo.InvariantCulture);
        clients[i] = new("127.0.0.1", port, TimeSpan.FromSeconds(5), pid, args[i * 3 + 2]);
        processes[i] = Process.GetProcessById(pid);
    }
    var identityGuards = true;
    for (var i = 0; i < 2; ++i)
    {
        foreach (var wrongPid in new[] { false, true })
        {
            var wrong = new FluidLinkGatewayUpdateUploadAuthorizer("127.0.0.1",
                int.Parse(args[i * 3], CultureInfo.InvariantCulture), TimeSpan.FromSeconds(5),
                wrongPid ? processes[1 - i].Id : processes[i].Id,
                wrongPid ? args[i * 3 + 2] : new string('0', 64));
            try
            {
                await wrong.AuthorizeAsync(new(0, "warmup", 4194304, 128, targetSha, hookSha), cancellation.Token);
                identityGuards = false;
            }
            catch (GatewayUpdateUploadAuthorizationFailureException) { }
        }
    }
    var contexts = new HashSet<string>(StringComparer.Ordinal);
    for (var pair = 0; pair < 35; ++pair)
    {
        foreach (var i in pair % 2 == 0 ? new[] { 0, 1 } : [1, 0])
        {
            var phase = pair < 5 ? "warmup" : "measured";
            var index = pair < 5 ? pair : pair - 5;
            processes[i].Refresh();
            var before = processes[i].TotalProcessorTime;
            var beforeCycles = CpuCycles.Read(processes[i]);
            var result = await clients[i].AuthorizeAsync(new(index, phase, 4194304, 128, targetSha, hookSha), cancellation.Token);
            result.EnsureMatchesNativePolicy(4194304, 128, index, phase, targetSha, hookSha);
            if (!result.PeerProcessBindingVerified || !contexts.Add(result.AuthorizationContextSha256))
            {
                throw new InvalidDataException("Authorization identity or context guard failed.");
            }
            processes[i].Refresh();
            if (pair >= 5)
            {
                samples[i].Add(new(
                    index,
                    result.AuthorizationLatencyMicroseconds,
                    (processes[i].TotalProcessorTime - before).Ticks * 100,
                    checked(CpuCycles.Read(processes[i]) - beforeCycles),
                    processes[i].PrivateMemorySize64,
                    result.RoundTripCount,
                    result.AuthorizationContextSha256,
                    result.PeerExecutableSha256));
            }
        }
    }
    var summaries = samples.Select(s => new
    {
        p50_us = Percentile(s.Select(x => (double)x.LatencyUs), .5),
        p95_us = Percentile(s.Select(x => (double)x.LatencyUs), .95),
        p99_us = Percentile(s.Select(x => (double)x.LatencyUs), .99),
        server_cpu_ns_per_operation = s.Sum(x => x.ServerCpuNs) / (30.0 * 129),
        server_cpu_cycles_per_operation = s.Sum(x => (double)x.ServerCpuCycles) / (30.0 * 129),
        private_bytes_p50 = Percentile(s.Select(x => (double)x.PrivateBytes), .5)
    }).ToArray();
    var passed = identityGuards && summaries[1].p95_us <= summaries[0].p95_us &&
        summaries[1].p99_us <= summaries[0].p99_us &&
        summaries[1].server_cpu_cycles_per_operation > 0 &&
        summaries[1].server_cpu_cycles_per_operation < summaries[0].server_cpu_cycles_per_operation;
    var report = new
    {
        schema = "fluidruntime-native-gateway-comparison-v1",
        timestamp_utc = DateTimeOffset.UtcNow,
        measured_pairs = 30,
        warmup_pairs = 5,
        candidate_count = 128,
        identity_guards_verified = identityGuards,
        unique_contexts = contexts.Count,
        summaries = names.Select((name, i) => new { backend = name, metrics = summaries[i] }),
        samples = names.Select((name, i) => new { backend = name, runs = samples[i] }),
        comparison_gate_passed = passed,
        native_policy_published = false,
        scope = "Real Runtime authorizer including PID/executable SHA256/context checks; no GPU work",
        cpu_note = "Promotion uses QueryProcessCycleTime cycles, not quantized process-time zeros. Cycles are not nanoseconds."
    };
    await File.WriteAllTextAsync(args[8], JsonSerializer.Serialize(report,
        new JsonSerializerOptions { WriteIndented = true }), cancellation.Token);
    Console.WriteLine($"30 paired authorizations: comparison gate {passed}; identity guards {identityGuards}.");
    return passed ? 0 : 1;
}
finally
{
    foreach (var process in processes)
    {
        process?.Dispose();
    }
}

static double Percentile(IEnumerable<double> samples, double p)
{
    var values = samples.Order().ToArray();
    var position = (values.Length - 1) * p;
    var index = (int)position;
    return values[index] + (values[Math.Min(index + 1, values.Length - 1)] - values[index]) * (position - index);
}
sealed record Sample(int Pair, long LatencyUs, long ServerCpuNs, ulong ServerCpuCycles, long PrivateBytes,
    int RoundTrips, string ContextSha256, string ExecutableSha256);

static class CpuCycles
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryProcessCycleTime(IntPtr processHandle, out ulong cycleTime);
    internal static ulong Read(Process process)
    {
        if (!QueryProcessCycleTime(process.Handle, out var cycles))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        return cycles;
    }
}
