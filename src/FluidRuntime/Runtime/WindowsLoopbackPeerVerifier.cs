using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace FluidRuntime.Runtime;

public sealed record WindowsLoopbackPeerIdentity(
    int ProcessId,
    string ExecutablePath,
    string ExecutableSha256,
    DateTimeOffset ProcessStartedAtUtc);

public static class WindowsLoopbackPeerVerifier
{
    private const int AddressFamilyInet = 2;
    private const uint ErrorSuccess = 0;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint TcpStateEstablished = 5;
    private const int MaximumTableReadAttempts = 3;

    public static WindowsLoopbackPeerIdentity Verify(
        IPEndPoint localEndPoint,
        IPEndPoint remoteEndPoint,
        int expectedProcessId,
        string expectedExecutableSha256)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Loopback TCP process verification requires Windows.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedProcessId);
        ArgumentNullException.ThrowIfNull(localEndPoint);
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        ValidateEndPoint(localEndPoint, "client local endpoint");
        ValidateEndPoint(remoteEndPoint, "client remote endpoint");
        ValidateSha256(expectedExecutableSha256);

        var matches = ReadTcpRows()
            .Where(row => MatchesServerTuple(row, localEndPoint, remoteEndPoint))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                $"Expected one established owner row for {remoteEndPoint} -> " +
                $"{localEndPoint}; " +
                $"found {matches.Length}.");
        }

        if (matches[0].OwningProcessId != (uint)expectedProcessId)
        {
            throw new InvalidDataException(
                $"Loopback peer PID {matches[0].OwningProcessId} did not match " +
                $"expected PID {expectedProcessId}.");
        }

        return ReadProcessIdentity(
            expectedProcessId,
            expectedExecutableSha256);
    }

    private static void ValidateEndPoint(IPEndPoint endpoint, string description)
    {
        if (endpoint.AddressFamily != AddressFamily.InterNetwork ||
            !IPAddress.IsLoopback(endpoint.Address))
        {
            throw new InvalidDataException(
                $"The {description} must be a connected IPv4 loopback endpoint.");
        }
    }

    private static void ValidateSha256(string sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256) ||
            sha256.Length != 64 ||
            sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Expected executable SHA-256 must contain exactly 64 hexadecimal characters.",
                nameof(sha256));
        }
    }

    private static bool MatchesServerTuple(
        MibTcpRowOwnerPid row,
        IPEndPoint clientLocalEndPoint,
        IPEndPoint clientRemoteEndPoint) =>
        row.State == TcpStateEstablished &&
        DecodeAddress(row.LocalAddress).Equals(clientRemoteEndPoint.Address) &&
        DecodePort(row.LocalPort) == clientRemoteEndPoint.Port &&
        DecodeAddress(row.RemoteAddress).Equals(clientLocalEndPoint.Address) &&
        DecodePort(row.RemotePort) == clientLocalEndPoint.Port;

    private static IPAddress DecodeAddress(uint address) =>
        new(BitConverter.GetBytes(address));

    private static int DecodePort(uint port) =>
        unchecked((ushort)IPAddress.NetworkToHostOrder(
            unchecked((short)(port & ushort.MaxValue))));

    private static IReadOnlyList<MibTcpRowOwnerPid> ReadTcpRows()
    {
        var bufferSize = 0;
        var status = GetExtendedTcpTable(
            IntPtr.Zero,
            ref bufferSize,
            order: false,
            AddressFamilyInet,
            TcpTableClass.OwnerPidAll,
            reserved: 0);
        if (status is not (ErrorSuccess or ErrorInsufficientBuffer) ||
            bufferSize < sizeof(int))
        {
            throw new Win32Exception(
                checked((int)status),
                "Unable to size the Windows TCP owner table.");
        }

        var buffer = IntPtr.Zero;
        try
        {
            for (var attempt = 0; attempt < MaximumTableReadAttempts; ++attempt)
            {
                buffer = buffer == IntPtr.Zero
                    ? Marshal.AllocHGlobal(bufferSize)
                    : Marshal.ReAllocHGlobal(buffer, (IntPtr)bufferSize);
                status = GetExtendedTcpTable(
                    buffer,
                    ref bufferSize,
                    order: false,
                    AddressFamilyInet,
                    TcpTableClass.OwnerPidAll,
                    reserved: 0);
                if (status == ErrorInsufficientBuffer)
                {
                    continue;
                }
                if (status != ErrorSuccess)
                {
                    throw new Win32Exception(
                        checked((int)status),
                        "Unable to read the Windows TCP owner table.");
                }
                return ParseTcpRows(buffer, bufferSize);
            }

            throw new InvalidDataException(
                "The Windows TCP owner table changed repeatedly while being read.");
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static IReadOnlyList<MibTcpRowOwnerPid> ParseTcpRows(
        IntPtr buffer,
        int bufferSize)
    {
        var rowCount = Marshal.ReadInt32(buffer);
        var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
        var requiredSize = checked(sizeof(int) + (long)rowCount * rowSize);
        if (rowCount < 0 || requiredSize > bufferSize)
        {
            throw new InvalidDataException(
                "The Windows TCP owner table returned an invalid row count.");
        }

        var rows = new MibTcpRowOwnerPid[rowCount];
        for (var index = 0; index < rowCount; ++index)
        {
            rows[index] = Marshal.PtrToStructure<MibTcpRowOwnerPid>(
                IntPtr.Add(buffer, sizeof(int) + index * rowSize));
        }
        return rows;
    }

    private static WindowsLoopbackPeerIdentity ReadProcessIdentity(
        int processId,
        string expectedExecutableSha256)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            _ = process.Handle;
            process.Refresh();
            if (process.HasExited)
            {
                throw new InvalidDataException(
                    $"Loopback peer PID {processId} exited during verification.");
            }

            var executablePath = Path.GetFullPath(
                process.MainModule?.FileName ??
                throw new InvalidDataException(
                    $"Loopback peer PID {processId} has no readable main module."));
            var processStartUtc = new DateTimeOffset(
                process.StartTime.ToUniversalTime(),
                TimeSpan.Zero);
            using var stream = new FileStream(
                executablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            var executableSha256 = Convert.ToHexStringLower(
                SHA256.HashData(stream));
            if (!string.Equals(
                    executableSha256,
                    expectedExecutableSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Loopback peer executable SHA-256 did not match PID {processId}.");
            }

            process.Refresh();
            if (process.HasExited)
            {
                throw new InvalidDataException(
                    $"Loopback peer PID {processId} exited during verification.");
            }

            return new WindowsLoopbackPeerIdentity(
                processId,
                executablePath,
                executableSha256,
                processStartUtc);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidOperationException or
            Win32Exception or
            UnauthorizedAccessException or
            IOException)
        {
            throw new InvalidDataException(
                $"Unable to verify live loopback peer PID {processId}.",
                exception);
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        TcpTableClass tableClass,
        uint reserved);

    private enum TcpTableClass
    {
        OwnerPidAll = 5
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningProcessId;
    }
}
