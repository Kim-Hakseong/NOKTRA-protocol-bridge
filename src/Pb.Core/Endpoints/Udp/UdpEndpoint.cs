using System.Net;
using System.Net.Sockets;
using Pb.Core.Channels;

namespace Pb.Core.Endpoints.Udp;

/// <summary>
/// UDP endpoint. Receives datagrams into a broadcast slot that every waiting route observes,
/// and assembles outgoing datagrams from channel values placed at byte offsets.
/// </summary>
/// <remarks>
/// Outgoing datagrams are assembled in a payload buffer that persists between writes, so
/// several channels sharing one endpoint pack into one frame layout. Every write sends the
/// whole current payload: a route writing offset 0 and another writing offset 4 therefore
/// produce two datagrams per cycle, each carrying the latest value of both channels.
/// </remarks>
public sealed class UdpEndpoint : IEndpoint, IPollSource, IFrameSource, IValueSink
{
    /// <summary>Driver token this endpoint is configured as.</summary>
    public const string TypeToken = "udp";

    private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
    private readonly object _receiveSync = new object();

    private UdpClient? _receiver;
    private UdpClient? _sender;
    private CancellationTokenSource? _receiveLoopShutdown;
    private Task? _receiveLoop;
    private TaskCompletionSource<ReadOnlyMemory<byte>> _nextFrame = NewFrameSlot();
    private ReadOnlyMemory<byte>? _lastFrame;
    private byte[] _sendBuffer;
    private int _sendLength;
    private long _framesReceived;
    private bool _disposed;

    public UdpEndpoint(string id, UdpSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.CanSend && !settings.CanReceive)
        {
            throw new ArgumentException(
                "A UDP endpoint must be able to send, receive, or both.",
                nameof(settings));
        }

        Id = id;
        Settings = settings;
        _sendBuffer = new byte[settings.FrameBytes ?? 0];
        _sendLength = settings.FrameBytes ?? 0;
    }

    public string Id { get; }

    public string Kind => TypeToken;

    public UdpSettings Settings { get; }

    public EndpointState State { get; private set; } = EndpointState.Disconnected;

    public string Target => Settings.ToString();

    public long FramesReceived => Interlocked.Read(ref _framesReceived);

    /// <summary>The most recently received datagram, or null when none has arrived yet.</summary>
    public ReadOnlyMemory<byte>? LastFrame
    {
        get
        {
            lock (_receiveSync)
            {
                return _lastFrame;
            }
        }
    }

    /// <summary>Datagrams sent since construction.</summary>
    public long DatagramsSent { get; private set; }

    public bool Supports(ChannelSpec channel, ChannelRole role, out string? error)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (role == ChannelRole.Source && !Settings.CanReceive)
        {
            error = "this udp endpoint has no 'listen_port', so it cannot be a route source.";
            return false;
        }

        if (role == ChannelRole.Sink && !Settings.CanSend)
        {
            error = "this udp endpoint has no 'host' + 'port', so it cannot be a route sink.";
            return false;
        }

        int limit = role == ChannelRole.Sink
            ? Settings.FrameBytes ?? UdpSettings.MaxFrameBytes
            : UdpSettings.MaxFrameBytes;

        return FramePayload.TryPlace(channel, limit, out _, out _, out error);
    }

    public ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (State == EndpointState.Connected)
        {
            return ValueTask.CompletedTask;
        }

        State = EndpointState.Connecting;

        try
        {
            if (Settings.CanReceive)
            {
                IPAddress bind = Settings.BindAddress is null ? IPAddress.Any : IPAddress.Parse(Settings.BindAddress);
                _receiver = new UdpClient(new IPEndPoint(bind, Settings.ListenPort!.Value));
                _receiveLoopShutdown = new CancellationTokenSource();
                _receiveLoop = Task.Run(() => ReceiveLoopAsync(_receiver, _receiveLoopShutdown.Token), CancellationToken.None);
            }

            if (Settings.CanSend)
            {
                _sender = new UdpClient();
                _sender.Connect(Settings.Host!, Settings.Port);
            }

            State = EndpointState.Connected;
            return ValueTask.CompletedTask;
        }
        catch (Exception ex) when (ex is SocketException or FormatException or ArgumentException)
        {
            CloseTransport(EndpointState.Faulted);
            throw new EndpointException(Id, $"could not open {Target}: {ex.Message}", ex);
        }
    }

    public async ValueTask DisconnectAsync()
    {
        CancellationTokenSource? shutdown = _receiveLoopShutdown;
        Task? loop = _receiveLoop;

        if (shutdown is not null)
        {
            await shutdown.CancelAsync().ConfigureAwait(false);
        }

        CloseTransport(EndpointState.Disconnected);

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected while shutting the receive loop down.
            }
        }

        shutdown?.Dispose();
        _receiveLoopShutdown = null;
        _receiveLoop = null;
    }

    /// <summary>
    /// Returns the channel's bytes taken from the most recently received datagram. A route with
    /// a periodic trigger therefore samples the latest frame rather than consuming a queue.
    /// </summary>
    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ChannelSpec channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Settings.CanReceive)
        {
            throw new EndpointException(Id, "has no 'listen_port', so it cannot be read.");
        }

        ReadOnlyMemory<byte>? frame = LastFrame
            ?? throw new EndpointException(Id, $"has not received a datagram yet, so channel '{channel.Name}' has no value.");

        try
        {
            return ValueTask.FromResult(FramePayload.Extract(channel, frame.Value));
        }
        catch (ArgumentException ex)
        {
            throw new EndpointException(Id, ex.Message, ex);
        }
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReceiveFrameAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Settings.CanReceive)
        {
            throw new EndpointException(Id, "has no 'listen_port', so it cannot receive frames.");
        }

        Task<ReadOnlyMemory<byte>> next;
        lock (_receiveSync)
        {
            next = _nextFrame.Task;
        }

        return await next.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteAsync(ChannelSpec channel, Sample sample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Settings.CanSend)
        {
            throw new EndpointException(Id, "has no 'host' + 'port', so it cannot be written to.");
        }

        if (!FramePayload.TryPlace(channel, Settings.FrameBytes ?? UdpSettings.MaxFrameBytes, out int offset, out int length, out string? error))
        {
            throw new EndpointException(Id, $"cannot write channel '{channel.Name}': {error}");
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UdpClient sender = _sender
                ?? throw new EndpointException(Id, "is not connected; call ConnectAsync first.");

            EnsureSendCapacity(offset + length);
            ValueCodec.Encode(sample.Value, channel.Type, channel.ByteOrder, _sendBuffer.AsSpan(offset, length));

            try
            {
                await sender.SendAsync(_sendBuffer.AsMemory(0, _sendLength), cancellationToken).ConfigureAwait(false);
                DatagramsSent++;
            }
            catch (SocketException ex)
            {
                State = EndpointState.Faulted;
                throw new EndpointException(Id, $"could not send to {Settings.Host}:{Settings.Port}: {ex.Message}", ex);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync().ConfigureAwait(false);
        _sendGate.Dispose();
    }

    private async Task ReceiveLoopAsync(UdpClient receiver, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult result = await receiver.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                PublishFrame(result.Buffer);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // The endpoint is being disconnected, or the socket was closed underneath us.
        }
    }

    private void PublishFrame(byte[] frame)
    {
        TaskCompletionSource<ReadOnlyMemory<byte>> waiting;

        lock (_receiveSync)
        {
            _lastFrame = frame;
            waiting = _nextFrame;
            _nextFrame = NewFrameSlot();
        }

        Interlocked.Increment(ref _framesReceived);

        // Every route waiting on the previous slot wakes with this frame; the new slot holds
        // the ones that start waiting from now on.
        waiting.TrySetResult(frame);
    }

    private void EnsureSendCapacity(int required)
    {
        if (Settings.FrameBytes is int fixedLength)
        {
            // A declared frame length is authoritative: the payload never grows or shrinks.
            _sendLength = fixedLength;
            return;
        }

        if (required > _sendBuffer.Length)
        {
            byte[] grown = new byte[required];
            _sendBuffer.CopyTo(grown, 0);
            _sendBuffer = grown;
        }

        _sendLength = Math.Max(_sendLength, required);
    }

    private void CloseTransport(EndpointState state)
    {
        _receiver?.Dispose();
        _receiver = null;
        _sender?.Dispose();
        _sender = null;
        State = state;
    }

    private static TaskCompletionSource<ReadOnlyMemory<byte>> NewFrameSlot() =>
        new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
}
