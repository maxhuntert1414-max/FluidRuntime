using System.ComponentModel;
using System.Diagnostics;

namespace FluidRuntime.Runtime;

internal static class OwnedProcessLifetime
{
    private static readonly TimeSpan ExitDeadline = TimeSpan.FromSeconds(10);

    public static async Task TerminateAsync(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (HasExitedOrIsUnavailable(process))
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (HasExitedOrIsUnavailable(process))
        {
            return;
        }
        catch (Win32Exception) when (HasExitedOrIsUnavailable(process))
        {
            return;
        }

        try
        {
            using var deadline = new CancellationTokenSource(ExitDeadline);
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (InvalidOperationException) when (HasExitedOrIsUnavailable(process))
        {
            return;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"Owned process {process.Id} did not exit within " +
                $"{ExitDeadline.TotalSeconds:0} seconds after termination.");
        }

        if (!HasExitedOrIsUnavailable(process))
        {
            throw new InvalidOperationException(
                $"Owned process {process.Id} did not terminate.");
        }
    }

    private static bool HasExitedOrIsUnavailable(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
