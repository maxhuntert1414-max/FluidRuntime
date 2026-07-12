using System.Diagnostics;

namespace FluidRuntime.Native;

public sealed class NativeProbeClient
{
    public async Task<NativeProbeReport> ProbeAsync(
        string executablePath,
        int processId,
        int intervalMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalMs);

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
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
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
