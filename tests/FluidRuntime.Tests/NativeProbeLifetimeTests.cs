using System.Diagnostics;
using FluidRuntime.Native;
using FluidRuntime.Runtime;

namespace FluidRuntime.Tests;

public sealed class NativeProbeLifetimeTests
{
    [Fact]
    public async Task Cancelled_probe_never_launches_or_accesses_target()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NativeProbeClient().ProbeAsync(
            "missing.exe", 42, 10, TimeSpan.FromSeconds(1), cancellation.Token));
    }

    [Fact]
    public async Task Probe_deadline_covers_inherited_output_pipes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), $"fluid-probe-child-{Guid.NewGuid():N}.txt");
        try
        {
            var start = new ProcessStartInfo("powershell.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("$s = New-Object Diagnostics.ProcessStartInfo; " +
                "$s.FileName = 'powershell.exe'; $s.Arguments = '-NoProfile -Command Start-Sleep -Seconds 30'; " +
                "$s.UseShellExecute = $false; $s.CreateNoWindow = $true; " +
                "$p = [Diagnostics.Process]::Start($s); " +
                $"[IO.File]::WriteAllText('{path.Replace("'", "''")}', [string]$p.Id)");
            // Cold PowerShell startup on hosted Windows can exceed two seconds.
            // The descendant must still outlive both the probe and test deadlines.
            var error = await Assert.ThrowsAsync<TimeoutException>(() => NativeProbeClient.RunProcessAsync(
                start, TimeSpan.FromSeconds(10), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Contains("Native probe exceeded", error.Message);
            Assert.True(File.Exists(path), "The parent must have launched the pipe-holding descendant.");
        }
        finally
        {
            if (File.Exists(path))
            {
                var pid = int.Parse(await File.ReadAllTextAsync(path));
                try
                {
                    using var child = Process.GetProcessById(pid);
                    await OwnedProcessLifetime.TerminateAsync(child);
                }
                catch (ArgumentException) { }
                File.Delete(path);
            }
        }
    }
}
