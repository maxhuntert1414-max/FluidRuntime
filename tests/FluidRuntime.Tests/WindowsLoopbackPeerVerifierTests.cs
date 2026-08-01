using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using FluidRuntime.Runtime;

namespace FluidRuntime.Tests;

public sealed class WindowsLoopbackPeerVerifierTests
{
    [Fact]
    public async Task Verify_returns_identity_for_exact_tuple_pid_and_hash()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var listenerEndPoint = (IPEndPoint)listener.LocalEndpoint;
        var acceptTask = listener.AcceptTcpClientAsync();
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(listenerEndPoint.Address, listenerEndPoint.Port);
        using var server = await acceptTask;
        using var process = Process.GetCurrentProcess();
        var executablePath = Path.GetFullPath(
            process.MainModule?.FileName ??
            throw new InvalidOperationException("Test process has no main module."));
        var expectedSha256 = ComputeSha256(executablePath);
        var expectedStartedAtUtc = new DateTimeOffset(
            process.StartTime.ToUniversalTime(),
            TimeSpan.Zero);

        var identity = WindowsLoopbackPeerVerifier.Verify(
            (IPEndPoint)client.Client.LocalEndPoint!,
            (IPEndPoint)client.Client.RemoteEndPoint!,
            process.Id,
            expectedSha256);

        Assert.Equal(process.Id, identity.ProcessId);
        Assert.True(PathsEqual(executablePath, identity.ExecutablePath));
        Assert.Equal(expectedSha256, identity.ExecutableSha256);
        Assert.Equal(expectedStartedAtUtc, identity.ProcessStartedAtUtc);
    }

    [Fact]
    public async Task Verify_rejects_wrong_pid_and_executable_hash()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var listenerEndPoint = (IPEndPoint)listener.LocalEndpoint;
        var acceptTask = listener.AcceptTcpClientAsync();
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(listenerEndPoint.Address, listenerEndPoint.Port);
        using var server = await acceptTask;
        using var process = Process.GetCurrentProcess();
        var localEndPoint = (IPEndPoint)client.Client.LocalEndPoint!;
        var remoteEndPoint = (IPEndPoint)client.Client.RemoteEndPoint!;
        var executablePath = Path.GetFullPath(
            process.MainModule?.FileName ??
            throw new InvalidOperationException("Test process has no main module."));
        var expectedSha256 = ComputeSha256(executablePath);
        var wrongLocalPort = localEndPoint.Port == IPEndPoint.MaxPort
            ? localEndPoint.Port - 1
            : localEndPoint.Port + 1;

        Assert.Throws<InvalidDataException>(
            () => WindowsLoopbackPeerVerifier.Verify(
                new IPEndPoint(localEndPoint.Address, wrongLocalPort),
                remoteEndPoint,
                process.Id,
                expectedSha256));

        Assert.Throws<InvalidDataException>(
            () => WindowsLoopbackPeerVerifier.Verify(
                localEndPoint,
                remoteEndPoint,
                int.MaxValue,
                new string('0', 64)));
        Assert.Throws<InvalidDataException>(
            () => WindowsLoopbackPeerVerifier.Verify(
                localEndPoint,
                remoteEndPoint,
                process.Id,
                new string('0', 64)));
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
