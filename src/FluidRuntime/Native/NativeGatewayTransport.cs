using System.Buffers;
using System.Runtime.InteropServices;
using FluidLink;

namespace FluidRuntime.Native;

/// <summary>ABI v1 counters. Allocations cover retained PMR state, not all native allocations.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativeGatewayMetrics
{
    public ulong Exchanges, RequestBytes, ResponseBytes, WirePayloadCopyBytes, WirePayloadCopyCount;
    public ulong StateAllocationCount, StateAllocatedBytes, StateBytes, StatePeakBytes;
    public ulong ActiveResources, TrackedOperations;
    public uint Closed, Reserved;
}

public sealed class NativeGatewayTransport : IFluidLinkV2Transport
{
    private readonly object gate = new();
    private readonly NativeGatewayLibrary library;
    private byte[]? output;
    private NativeGatewayLibrary.SessionHandle? session;
    private bool disposed;

    internal NativeGatewayTransport(NativeGatewayLibrary library) => this.library = library;

    public bool IsConnected { get { lock (gate) return session is not null; } }

    public ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (session is null)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(NativeGatewayLibrary.MaxFrameBytes);
                try
                {
                    session = library.CreateSession();
                    output = buffer;
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                    throw;
                }
            }
            return ValueTask.CompletedTask;
        }
    }

    public unsafe ValueTask<FluidLinkV2Frame> ExchangeAsync(
        ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (session is null) throw new InvalidOperationException("Gateway DLL session is not connected.");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint size;
                using (var input = request.Pin())
                    fixed (byte* destination = output)
                    {
                        // SafeHandle marshalling retains the session and module through the native call.
                        NativeGatewayLibrary.Check(library.Exchange(session, (byte*)input.Pointer,
                                checked((uint)request.Length), destination, NativeGatewayLibrary.MaxFrameBytes, out size));
                    }
                // A synchronous native call cannot be preempted safely. Late decisions are discarded.
                cancellationToken.ThrowIfCancellationRequested();
                if (size > NativeGatewayLibrary.MaxFrameBytes) throw new InvalidDataException("Gateway DLL response exceeds capacity.");
                return ValueTask.FromResult(FluidLinkV2FrameCodec.Decode(output.AsSpan(0, checked((int)size))));
            }
            catch
            {
                Abort();
                throw;
            }
        }
    }

    public NativeGatewayMetrics ReadMetrics()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (session is null) throw new InvalidOperationException("No live Gateway DLL session.");
            NativeGatewayLibrary.Check(library.GetMetrics(session, out var metrics,
                (uint)Marshal.SizeOf<NativeGatewayMetrics>()));
            return metrics;
        }
    }

    public void Abort()
    {
        lock (gate)
        {
            session?.Dispose();
            session = null;
            if (output is not null)
            {
                ArrayPool<byte>.Shared.Return(output, clearArray: true);
                output = null;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            Abort();
        }
    }
}
