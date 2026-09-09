namespace FluidLink;

/// <summary>
/// A frame channel. The client owns and disposes the supplied transport,
/// serializes its calls, and retains all negotiation and response validation.
/// Implementations must discard their session after Abort, never switch backends,
/// and return a response whose memory remains valid after the next exchange.
/// Request memory is borrowed until ExchangeAsync completes; implementations
/// must not retain it or return response slices backed by the request buffer.
/// </summary>
public interface IFluidLinkV2Transport : IDisposable
{
    bool IsConnected { get; }

    ValueTask ConnectAsync(CancellationToken cancellationToken);

    ValueTask<FluidLinkV2Frame> ExchangeAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken);

    void Abort();
}
