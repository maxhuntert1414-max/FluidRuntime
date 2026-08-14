using System.Diagnostics;
using FluidRuntime.Runtime;

namespace FluidRuntime.Native;

public sealed class NativeProbeClient
{
    public async Task<NativeProbeReport> ProbeAsync(
        string executablePath,
        int processId,
        int intervalMs,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalMs);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "Native probe timeout must be between 1 ms and 2 minutes.");
        }

        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Native probe executable was not found.", fullPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fullPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--pid");
        startInfo.ArgumentList.Add(processId.ToString());
        startInfo.ArgumentList.Add("--interval-ms");
        startInfo.ArgumentList.Add(intervalMs.ToString());

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the native probe.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Native probe exceeded its {timeout.TotalMilliseconds:0} ms deadline.");
        }
        finally
        {
            await OwnedProcessLifetime.TerminateAsync(process);
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Native probe exited with code {process.ExitCode}: {stderr.Trim()}");
        }

        return NativeProbeReportParser.Parse(stdout, processId);
    }
}
