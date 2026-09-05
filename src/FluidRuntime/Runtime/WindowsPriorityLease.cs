using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace FluidRuntime.Runtime;

internal sealed record PriorityLeaseReport(int ProcessId, string TargetSha256, long StartTimeUtcTicks,
    string Before, string Requested, string? After, bool Applied, string Restoration,
    double ElapsedMilliseconds, int RequestedSeconds, string Authority = "explicit-user-timed-priority-only");

internal static class WindowsPriorityLease
{
    internal static void RequireUnelevated()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows x64 is required.");
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Application sessions refuse elevated execution. Use a normal terminal.");
    }

    internal static bool CanApply(ProcessPriorityClass current) => current == ProcessPriorityClass.Normal;
    internal static bool CanRestore(ProcessPriorityClass current) => current == ProcessPriorityClass.AboveNormal;

    internal static async Task<int> RunWatchdogAsync(string[] args)
    {
        // This independently timed helper survives collector termination. It has
        // no injection, affinity, high/realtime priority or global-setting path.
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows is required.");
            RequireUnelevated();
            if (args.Length != 7) throw new ArgumentException("Invalid priority lease invocation.");
            var pid = int.Parse(args[1], CultureInfo.InvariantCulture);
            var ticks = long.Parse(args[2], CultureInfo.InvariantCulture);
            var sha = args[3];
            var seconds = int.Parse(args[4], CultureInfo.InvariantCulture);
            if (pid <= 0 || pid == Environment.ProcessId || seconds is < 1 or > 30 ||
                sha.Length != 64 || !sha.All(Uri.IsHexDigit) ||
                !args[5].StartsWith("Local\\FluidRuntimePriority-", StringComparison.Ordinal))
                throw new ArgumentException("Invalid priority lease bounds or identity.");
            using var stop = EventWaitHandle.OpenExisting(args[5]);
            using var process = Process.GetProcessById(pid);
            using var self = Process.GetCurrentProcess();
            if (process.SessionId != self.SessionId || process.StartTime.ToUniversalTime().Ticks != ticks)
                throw new InvalidOperationException("Priority lease target identity/session mismatch.");
            var path = process.MainModule?.FileName ?? throw new InvalidDataException("Target image unavailable.");
            ApplicationSessionOptions.RequireApplicationPath(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actual = Convert.ToHexStringLower(SHA256.HashData(file));
            if (!string.Equals(actual, sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Priority target hash mismatch.");
            if (!IsProcessCritical(process.Handle, out var critical) || critical)
                throw new InvalidOperationException("Critical or unqueryable process is not eligible.");
            using var token = OpenIdentity(process);
            using var current = WindowsIdentity.GetCurrent();
            if (token.User != current.User) throw new InvalidOperationException("Cross-user priority changes are unsupported.");
            var before = process.PriorityClass;
            if (!CanApply(before)) throw new InvalidOperationException("Only Normal -> AboveNormal is eligible; existing policy is preserved.");
            var timer = Stopwatch.StartNew();
            var applied = false;
            string? after = null;
            var restoration = "not-applied";
            try
            {
                process.PriorityClass = ProcessPriorityClass.AboveNormal;
                applied = true;
                process.Refresh();
                if (process.PriorityClass != ProcessPriorityClass.AboveNormal)
                    throw new InvalidOperationException("Windows did not confirm the requested priority.");
                stop.WaitOne(TimeSpan.FromSeconds(seconds));
            }
            finally
            {
                if (applied)
                {
                    try
                    {
                        process.Refresh();
                        if (process.HasExited) restoration = "process-exited";
                        else if (!CanRestore(process.PriorityClass))
                        {
                            after = process.PriorityClass.ToString();
                            restoration = "external-change-preserved";
                        }
                        else
                        {
                            process.PriorityClass = before;
                            process.Refresh();
                            after = process.PriorityClass.ToString();
                            restoration = process.PriorityClass == before ? "restored" : "restore-not-confirmed";
                        }
                    }
                    catch (Exception error) when (error is Win32Exception or InvalidOperationException)
                    {
                        restoration = process.HasExited ? "process-exited" : "restore-failed";
                    }
                }
                var report = new PriorityLeaseReport(pid, actual, ticks, before.ToString(),
                    ProcessPriorityClass.AboveNormal.ToString(), after, applied, restoration,
                    timer.Elapsed.TotalMilliseconds, seconds);
                await AtomicJsonFile.WriteTextAsync(Path.GetFullPath(args[6]), JsonSerializer.Serialize(report,
                    ApplicationSessionRunner.JsonOptions) + Environment.NewLine, CancellationToken.None);
            }
            return restoration is "restored" or "process-exited" or "external-change-preserved" ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Priority lease failed: {error.Message}");
            return 1;
        }
    }

    private static WindowsIdentity OpenIdentity(Process process)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows is required.");
        if (!OpenProcessToken(process.Handle, 8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try { return new WindowsIdentity(token); }
        finally { CloseHandle(token); }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessCritical(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool critical);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
