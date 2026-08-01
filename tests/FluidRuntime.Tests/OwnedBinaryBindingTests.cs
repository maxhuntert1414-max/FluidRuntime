using System.Diagnostics;
using System.Security.Cryptography;
using FluidRuntime.Runtime;

namespace FluidRuntime.Tests;

public sealed class OwnedBinaryBindingTests
{
    [Fact]
    public void Open_freezes_canonical_paths_hashes_and_write_access()
    {
        var directory = CreateTemporaryDirectory();
        var targetPath = Path.Combine(directory, "target.exe");
        var hookPath = Path.Combine(directory, "hook.dll");
        var targetBytes = new byte[] { 1, 2, 3, 4 };
        var hookBytes = new byte[] { 5, 6, 7, 8 };
        File.WriteAllBytes(targetPath, targetBytes);
        File.WriteAllBytes(hookPath, hookBytes);

        try
        {
            using (var binding = OwnedBinaryBinding.Open(targetPath, hookPath))
            {
                Assert.Equal(Path.GetFullPath(targetPath), binding.TargetPath);
                Assert.Equal(Path.GetFullPath(hookPath), binding.HookPath);
                Assert.Equal(ComputeSha256(targetBytes), binding.TargetSha256);
                Assert.Equal(ComputeSha256(hookBytes), binding.HookSha256);

                if (OperatingSystem.IsWindows())
                {
                    Assert.Throws<IOException>(() =>
                    {
                        using var writer = new FileStream(
                            targetPath,
                            FileMode.Open,
                            FileAccess.Write,
                            FileShare.Read);
                    });
                    Assert.Throws<IOException>(() =>
                    {
                        using var writer = new FileStream(
                            hookPath,
                            FileMode.Open,
                            FileAccess.Write,
                            FileShare.Read);
                    });
                }
            }

            File.WriteAllBytes(targetPath, new byte[] { 9 });
            Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(targetPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ValidateLaunchedProcess_accepts_matching_loaded_binaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        var targetPath = Path.GetFullPath(
            process.MainModule?.FileName ??
            throw new InvalidOperationException("Test process has no main module."));
        var hookPath = process.Modules
            .Cast<ProcessModule>()
            .Select(module => module.FileName)
            .FirstOrDefault(path =>
                File.Exists(path) && !PathsEqual(path, targetPath)) ??
            targetPath;
        using var binding = OwnedBinaryBinding.Open(targetPath, hookPath);

        var validation = binding.ValidateLaunchedProcess(process);

        Assert.Equal(process.Id, validation.ProcessId);
        Assert.True(PathsEqual(targetPath, validation.TargetExecutablePath));
        Assert.True(PathsEqual(hookPath, validation.HookModulePath));
        Assert.Equal(binding.TargetSha256, validation.TargetSha256);
        Assert.Equal(binding.HookSha256, validation.HookSha256);
    }

    [Fact]
    public void ValidateLaunchedProcess_rejects_an_unloaded_hook()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = CreateTemporaryDirectory();
        var hookPath = Path.Combine(directory, "not-loaded.dll");
        File.WriteAllBytes(hookPath, new byte[] { 1, 2, 3, 4 });
        try
        {
            using var process = Process.GetCurrentProcess();
            var targetPath = Path.GetFullPath(
                process.MainModule?.FileName ??
                throw new InvalidOperationException("Test process has no main module."));
            using var binding = OwnedBinaryBinding.Open(targetPath, hookPath);

            Assert.Throws<InvalidDataException>(
                () => binding.ValidateLaunchedProcess(process));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"FluidRuntime.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string ComputeSha256(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
}
