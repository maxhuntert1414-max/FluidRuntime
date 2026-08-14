using System.Diagnostics;
using FluidRuntime.Runtime;

namespace FluidRuntime.Tests;

public sealed class RuntimeHardeningTests
{
    [Fact]
    public async Task Owned_process_termination_is_complete_and_idempotent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command Start-Sleep -Seconds 30",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Unable to start test process.");

        await OwnedProcessLifetime.TerminateAsync(process);
        await OwnedProcessLifetime.TerminateAsync(process);

        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task Cancelled_atomic_write_preserves_previous_report()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"fluidruntime-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "report.json");
        try
        {
            await AtomicJsonFile.WriteTextAsync(path, "stable");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AtomicJsonFile.WriteTextAsync(path, "partial", cancellation.Token));

            Assert.Equal("stable", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Failed_atomic_replace_preserves_previous_report()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"fluidruntime-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "report.json");
        try
        {
            await AtomicJsonFile.WriteTextAsync(path, "stable");
            await using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var exception = await Record.ExceptionAsync(() =>
                    AtomicJsonFile.WriteTextAsync(path, "partial"));
                Assert.True(
                    exception is IOException or UnauthorizedAccessException,
                    $"Unexpected exception type: {exception?.GetType().FullName ?? "none"}");
            }

            Assert.Equal("stable", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
