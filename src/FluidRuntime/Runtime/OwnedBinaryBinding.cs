using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;

namespace FluidRuntime.Runtime;

public sealed record OwnedBinaryProcessValidation(
    int ProcessId,
    string TargetExecutablePath,
    string TargetSha256,
    string HookModulePath,
    string HookSha256);

public sealed class OwnedBinaryBinding : IDisposable
{
    private readonly object streamGate = new();
    private readonly FileStream targetHandle;
    private readonly FileStream hookHandle;
    private bool disposed;

    private OwnedBinaryBinding(
        string targetPath,
        string hookPath,
        FileStream targetHandle,
        FileStream hookHandle,
        string targetSha256,
        string hookSha256)
    {
        TargetPath = targetPath;
        HookPath = hookPath;
        this.targetHandle = targetHandle;
        this.hookHandle = hookHandle;
        TargetSha256 = targetSha256;
        HookSha256 = hookSha256;
    }

    public string TargetPath { get; }

    public string HookPath { get; }

    public string TargetSha256 { get; }

    public string HookSha256 { get; }

    public static OwnedBinaryBinding Open(string targetPath, string hookPath)
    {
        var canonicalTargetPath = RequireFile(targetPath, nameof(targetPath));
        var canonicalHookPath = RequireFile(hookPath, nameof(hookPath));
        FileStream? targetHandle = null;
        FileStream? hookHandle = null;
        try
        {
            targetHandle = OpenReadLocked(canonicalTargetPath);
            hookHandle = OpenReadLocked(canonicalHookPath);
            return new OwnedBinaryBinding(
                canonicalTargetPath,
                canonicalHookPath,
                targetHandle,
                hookHandle,
                ComputeSha256(targetHandle),
                ComputeSha256(hookHandle));
        }
        catch
        {
            hookHandle?.Dispose();
            targetHandle?.Dispose();
            throw;
        }
    }

    public OwnedBinaryProcessValidation ValidateLaunchedProcess(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Owned process module validation requires Windows.");
        }

        ArgumentNullException.ThrowIfNull(process);
        ThrowIfDisposed();
        try
        {
            process.Refresh();
            if (process.HasExited)
            {
                throw new InvalidDataException(
                    "The owned target exited before binary validation.");
            }

            var mainModulePath = CanonicalizeReportedPath(
                process.MainModule?.FileName ??
                throw new InvalidDataException(
                    "The owned target has no readable main module."));
            if (!PathsEqual(mainModulePath, TargetPath))
            {
                throw new InvalidDataException(
                    $"Owned target module '{mainModulePath}' did not match " +
                    $"the frozen target '{TargetPath}'.");
            }

            string? loadedHookPath = null;
            foreach (ProcessModule module in process.Modules)
            {
                var modulePath = CanonicalizeReportedPath(module.FileName);
                if (PathsEqual(modulePath, HookPath))
                {
                    loadedHookPath = modulePath;
                    break;
                }
            }
            if (loadedHookPath is null)
            {
                throw new InvalidDataException(
                    $"Frozen hook module '{HookPath}' is not loaded in PID {process.Id}.");
            }

            string currentTargetSha256;
            string currentHookSha256;
            lock (streamGate)
            {
                currentTargetSha256 = ComputeSha256(targetHandle);
                currentHookSha256 = ComputeSha256(hookHandle);
            }
            if (!string.Equals(
                    currentTargetSha256,
                    TargetSha256,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    currentHookSha256,
                    HookSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A frozen owned binary changed during process validation.");
            }

            process.Refresh();
            if (process.HasExited)
            {
                throw new InvalidDataException(
                    "The owned target exited during binary validation.");
            }

            return new OwnedBinaryProcessValidation(
                process.Id,
                mainModulePath,
                currentTargetSha256,
                loadedHookPath,
                currentHookSha256);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            Win32Exception or
            UnauthorizedAccessException or
            IOException)
        {
            throw new InvalidDataException(
                $"Unable to validate owned target PID {process.Id} binaries.",
                exception);
        }
    }

    public void Dispose()
    {
        lock (streamGate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            hookHandle.Dispose();
            targetHandle.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    private static FileStream OpenReadLocked(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);

    private static string RequireFile(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "A binary path is required.",
                parameterName);
        }
        var canonicalPath = Path.GetFullPath(path);
        if (!File.Exists(canonicalPath))
        {
            throw new FileNotFoundException(
                $"Owned binary does not exist: {canonicalPath}",
                canonicalPath);
        }
        return canonicalPath;
    }

    private static string CanonicalizeReportedPath(string path) =>
        Path.GetFullPath(path);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string ComputeSha256(FileStream stream)
    {
        stream.Position = 0;
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        finally
        {
            stream.Position = 0;
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(disposed, this);
}
