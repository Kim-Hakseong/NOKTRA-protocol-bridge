using Pb.Core.Channels;

namespace Pb.Core.Endpoints;

/// <summary>Connection state of an endpoint, surfaced in route statistics and the monitor UI.</summary>
public enum EndpointState
{
    /// <summary>Never connected, or deliberately disconnected.</summary>
    Disconnected = 0,

    /// <summary>A connection attempt is in progress.</summary>
    Connecting,

    /// <summary>Ready to carry traffic.</summary>
    Connected,

    /// <summary>The last operation failed; the supervisor will retry.</summary>
    Faulted,
}

/// <summary>Which side of a route a channel sits on.</summary>
public enum ChannelRole
{
    /// <summary>The channel is read from.</summary>
    Source = 0,

    /// <summary>The channel is written to.</summary>
    Sink,
}

/// <summary>
/// A protocol attachment point. An endpoint owns its transport and nothing else: it knows how
/// to connect, how to move bytes for a channel, and what state it is in. Routing, retry
/// policy and value conversion live above it.
/// </summary>
public interface IEndpoint : IAsyncDisposable
{
    /// <summary>Configuration id of this endpoint.</summary>
    string Id { get; }

    /// <summary>Driver token, for example <c>modbus_tcp</c>.</summary>
    string Kind { get; }

    /// <summary>Current connection state.</summary>
    EndpointState State { get; }

    /// <summary>
    /// Human-readable description of what this endpoint is attached to, for logs and the
    /// monitor window.
    /// </summary>
    string Target { get; }

    /// <summary>
    /// Establishes the transport. Calling this on an already-connected endpoint returns
    /// without doing anything, so a supervisor can call it freely.
    /// </summary>
    ValueTask ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Closes the transport and returns to <see cref="EndpointState.Disconnected"/>.</summary>
    ValueTask DisconnectAsync();

    /// <summary>
    /// Reports whether this endpoint can serve <paramref name="channel"/> in
    /// <paramref name="role"/>, so that configuration mistakes are caught at start-up rather
    /// than on the first poll.
    /// </summary>
    /// <param name="channel">The channel to check.</param>
    /// <param name="role">Whether the channel will be read or written.</param>
    /// <param name="error">Why the channel is not serviceable, when the method returns false.</param>
    bool Supports(ChannelSpec channel, ChannelRole role, out string? error);
}

/// <summary>
/// An endpoint the router has to ask for values, such as a Modbus master polling a slave.
/// </summary>
public interface IPollSource
{
    /// <summary>
    /// Reads the wire bytes of one channel. The returned memory is at least
    /// <see cref="ChannelSpec.SizeInBytes"/> long and is laid out exactly as it arrived, so
    /// that <see cref="ValueCodec"/> applies the channel's byte order to it.
    /// </summary>
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(ChannelSpec channel, CancellationToken cancellationToken);
}

/// <summary>
/// An endpoint that delivers frames when the far side sends them, such as a UDP listener or a
/// serial port. Every awaiter of <see cref="ReceiveFrameAsync"/> sees every frame that arrives
/// after it started waiting, so several routes can share one endpoint.
/// </summary>
public interface IFrameSource
{
    /// <summary>Frames received since the endpoint was connected.</summary>
    long FramesReceived { get; }

    /// <summary>Waits for the next frame to arrive.</summary>
    ValueTask<ReadOnlyMemory<byte>> ReceiveFrameAsync(CancellationToken cancellationToken);
}

/// <summary>An endpoint values can be written to.</summary>
public interface IValueSink
{
    /// <summary>
    /// Writes one sample to <paramref name="channel"/>. Implementations either complete the
    /// write or throw; partial success is not a state a route has to reason about.
    /// </summary>
    ValueTask WriteAsync(ChannelSpec channel, Sample sample, CancellationToken cancellationToken);
}

/// <summary>
/// An endpoint that needs periodic attention even when no route is moving data — a protocol
/// keep-alive, for instance. The bridge supervisor owns the timing, so endpoints hold no hidden
/// timers and their upkeep stays deterministic under an injected time source.
/// </summary>
public interface IEndpointUpkeep
{
    /// <summary>Does whatever periodic work is due. Called on the supervisor's interval.</summary>
    ValueTask TickAsync(CancellationToken cancellationToken);
}

/// <summary>Raised when an endpoint cannot carry out an operation.</summary>
public sealed class EndpointException : Exception
{
    public EndpointException(string endpointId, string message, Exception? innerException = null)
        : base($"Endpoint '{endpointId}': {message}", innerException) => EndpointId = endpointId;

    public string EndpointId { get; }
}
