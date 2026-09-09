using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using FluidRuntime.Runtime;

namespace FluidRuntime.Native;

public sealed record GatewayLibraryIdentity(string Path, string Sha256, uint AbiVersion);

/// <summary>A trusted, hash-pinned module. This is not a sandbox for untrusted DLLs.</summary>
public sealed class NativeGatewayLibrary : IDisposable
{
    public const uint AbiVersion = 0x00010000;
    public const int MaxFrameBytes = 65591;
    private const uint LoadLibrarySearchSystem32 = 0x800;
    private readonly object gate = new();
    private readonly ModuleHandle module;
    private readonly CreateDelegate create;
    private readonly DestroyDelegate destroy;
    internal readonly ExchangeDelegate Exchange;
    internal readonly MetricsDelegate GetMetrics;
    private bool disposed;

    public GatewayLibraryIdentity Identity { get; }
    public WindowsLoopbackPeerIdentity HostIdentity { get; }

    public NativeGatewayLibrary(string path, string expectedSha256)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("The Gateway DLL requires Windows x64.");
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!System.IO.Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute Gateway DLL path is required.", nameof(path));
        if (expectedSha256 is null || expectedSha256.Length != 64 ||
            !expectedSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("An expected DLL SHA-256 is required.", nameof(expectedSha256));

        path = System.IO.Path.GetFullPath(path);
        var libraryFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        FileStream? hostFile = null;
        nint loaded = 0;
        try
        {
            var actualHash = Convert.ToHexString(SHA256.HashData(libraryFile)).ToLowerInvariant();
            if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Gateway DLL SHA-256 does not match the pinned binary.");

            using var process = Process.GetCurrentProcess();
            var hostPath = process.MainModule?.FileName ?? throw new InvalidDataException("Missing host image.");
            hostFile = new FileStream(hostPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            HostIdentity = new WindowsLoopbackPeerIdentity(process.Id, hostPath,
                Convert.ToHexString(SHA256.HashData(hostFile)).ToLowerInvariant(),
                new DateTimeOffset(process.StartTime.ToUniversalTime()));

            // Release imports only OS libraries; do not search adjacent files, CWD or PATH.
            loaded = LoadLibraryExW(path, 0, LoadLibrarySearchSystem32);
            if (loaded == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var actualPath = new StringBuilder(32768);
            var length = GetModuleFileNameW(loaded, actualPath, actualPath.Capacity);
            if (length == 0 || length >= actualPath.Capacity ||
                !string.Equals(System.IO.Path.GetFullPath(actualPath.ToString()), path,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Loaded Gateway DLL path differs from the pinned file.");

            var infoCall = Export<InfoDelegate>(loaded, "fgn_get_abi_info");
            Check(infoCall(AbiVersion, out var info, (uint)Marshal.SizeOf<AbiInfo>()));
            if (info.Version != AbiVersion || info.Size != 40 || info.MaxFrameBytes != MaxFrameBytes ||
                info.MaxSessions != 8 || info.MaxResources != 4096 || info.MaxOperations != 16384 ||
                info.MaxStateBytes != 8 * 1024 * 1024 || (info.Features & 3) != 3)
                throw new InvalidDataException("Gateway DLL ABI or safety limits are incompatible.");
            create = Export<CreateDelegate>(loaded, "fgn_session_create");
            destroy = Export<DestroyDelegate>(loaded, "fgn_session_destroy");
            Exchange = Export<ExchangeDelegate>(loaded, "fgn_session_exchange");
            GetMetrics = Export<MetricsDelegate>(loaded, "fgn_session_get_metrics");
            Identity = new GatewayLibraryIdentity(path, actualHash, AbiVersion);
            module = new ModuleHandle(loaded, libraryFile, hostFile);
        }
        catch
        {
            if (loaded != 0) FreeLibrary(loaded);
            hostFile?.Dispose();
            libraryFile.Dispose();
            throw;
        }
    }

    public NativeGatewayTransport CreateTransport() => new(this);

    internal SessionHandle CreateSession()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return new SessionHandle(module, create, destroy);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            module.Dispose();
        }
    }

    internal static void Check(uint status)
    {
        if (status != 0) throw new NativeGatewayException(status);
    }

    private static T Export<T>(nint module, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));

    [StructLayout(LayoutKind.Sequential)]
    private struct AbiInfo
    {
        public uint Version, Size, MaxFrameBytes, MaxSessions, MaxResources, MaxOperations;
        public ulong MaxStateBytes, Features;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint InfoDelegate(uint version, out AbiInfo info, uint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate uint CreateDelegate(uint version, out ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate uint DestroyDelegate(ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal unsafe delegate uint ExchangeDelegate(SessionHandle handle, byte* request, uint size,
        byte* output, uint capacity, out uint outputSize);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate uint MetricsDelegate(SessionHandle handle, out NativeGatewayMetrics metrics, uint size);

    internal sealed class SessionHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly ModuleHandle module;
        private readonly DestroyDelegate destroy;
        private bool moduleReference;
        internal ulong Token => unchecked((ulong)handle);

        internal SessionHandle(ModuleHandle module, CreateDelegate create, DestroyDelegate destroy)
            : base(true)
        {
            this.module = module;
            this.destroy = destroy;
            try
            {
                module.DangerousAddRef(ref moduleReference);
                Check(create(AbiVersion, out var token));
                if (token == 0) throw new InvalidDataException("Gateway DLL returned an empty handle.");
                SetHandle(unchecked((nint)token));
            }
            catch
            {
                if (moduleReference) module.DangerousRelease();
                moduleReference = false;
                throw;
            }
        }

        protected override bool ReleaseHandle()
        {
            var status = destroy(Token);
            if (moduleReference) module.DangerousRelease();
            moduleReference = false;
            return status == 0;
        }
    }

    internal sealed class ModuleHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly FileStream libraryFile;
        private readonly FileStream hostFile;

        internal ModuleHandle(nint value, FileStream libraryFile, FileStream hostFile) : base(true)
        {
            this.libraryFile = libraryFile;
            this.hostFile = hostFile;
            SetHandle(value);
        }

        protected override bool ReleaseHandle()
        {
            var result = FreeLibrary(handle);
            libraryFile.Dispose();
            hostFile.Dispose();
            return result;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryExW(string path, nint file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileNameW(nint module, StringBuilder path, int size);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(nint module);
}

public sealed class NativeGatewayException(uint status)
    : IOException($"Gateway DLL returned ABI status {status}; no authorization is usable.")
{
    public uint Status { get; } = status;
}
