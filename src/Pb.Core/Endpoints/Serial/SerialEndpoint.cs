using System.IO.Ports;
using Pb.Core.Channels;

namespace Pb.Core.Endpoints.Serial;

/// <summary>
/// Serial-line endpoint. Frames are cut from the byte stream by <see cref="FrameReader"/>
/// according to the configured framing, and outgoing frames are assembled from channel values
/// placed at byte offsets, exactly as for UDP.
/// </summary>
/// <remarks>
/// The port itself is reached through an injectable opener, so every behaviour above the
/// driver — framing, offset placement, broadcast of received frames, state transitions — is
/// tested against an in-memory stream. Only the two lines that construct a
/// <see cref="SerialPort"/> need real hardware.
/// </remarks>
public sealed class SerialEndpoint : IEndpoint, IPollSource, IFrameSource, IValueSink
{
    /// <summary>Driver token this endpoint is configured as.</summary>
    public const string TypeToken = "serial";

    private readonly Func<SerialSettings, ISerialLine> _open;
    private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
    private readonly object _receiveSync = new object();

    private ISerialLine? _line;
    private FrameReader? _reader;
    private CancellationTokenSource? _receiveLoopShutdown;
    private Task? _receiveLoop;
    private TaskCompletionSource<ReadOnlyMemory<byte>> _nextFrame = NewFrameSlot();
    private ReadOnlyMemory<byte>? _lastFrame;
    private byte[] _sendBuffer;
    private int _sendLength;
    private long _framesReceived;
    private bool _disposed;

    /// <param name="id">Configuration id.</param>
    /// <param name="settings">Line and framing settings.</param>
    /// <param name="open">
    /// Opens the line. Defaults to a real <see cref="SerialPort"/>; tests substitute a stream.
    /// </param>
    public SerialEndpoint(string id, SerialSettings settings, Func<SerialSettings, ISerialLine>? open = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.PortName);

        Id = id;
        Settings = settings;
        _open = open ?? SystemSerialLine.Open;
        _sendBuffer = new byte[settings.Framing == FramingMode.Fixed ? settings.FrameBytes : 0];
        _sendLength = _sendBuffer.Length;
    }

    public string Id { get; }

    public string Kind => TypeToken;

    public SerialSettings Settings { get; }

    public EndpointState State { get; private set; } = EndpointState.Disconnected;

    public string Target => Settings.ToString();

    public long FramesReceived => Interlocked.Read(ref _framesReceived);

    /// <summary>Frames written since construction.</summary>
    public long FramesSent { get; private set; }

    /// <summary>The most recently received frame, or null when none has arrived yet.</summary>
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

    public bool Supports(ChannelSpec channel, ChannelRole role, out string? error)
    {
        ArgumentNullException.ThrowIfNull(channel);

        int limit = Settings.Framing == FramingMode.Fixed ? Settings.FrameBytes : Settings.MaxFrameBytes;
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
            _line = _open(Settings);
            _reader = new FrameReader(
                _line.Stream,
                Settings.Framing,
                Settings.FrameBytes,
                Settings.Delimiter,
                Settings.MaxFrameBytes);

            _receiveLoopShutdown = new CancellationTokenSource();
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_reader, _receiveLoopShutdown.Token), CancellationToken.None);

            State = EndpointState.Connected;
            return ValueTask.CompletedTask;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
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
        _reader = null;
    }

    /// <summary>Returns the channel's bytes taken from the most recently received frame.</summary>
    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ChannelSpec channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        cancellationToken.ThrowIfCancellationRequested();

        ReadOnlyMemory<byte>? frame = LastFrame
            ?? throw new EndpointException(Id, $"has not received a frame yet, so channel '{channel.Name}' has no value.");

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

        int limit = Settings.Framing == FramingMode.Fixed ? Settings.FrameBytes : Settings.MaxFrameBytes;

        if (!FramePayload.TryPlace(channel, limit, out int offset, out int length, out string? error))
        {
            throw new EndpointException(Id, $"cannot write channel '{channel.Name}': {error}");
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ISerialLine line = _line
                ?? throw new EndpointException(Id, "is not connected; call ConnectAsync first.");

            EnsureSendCapacity(offset + length);
            ValueCodec.Encode(sample.Value, channel.Type, channel.ByteOrder, _sendBuffer.AsSpan(offset, length));

            try
            {
                await line.Stream.WriteAsync(_sendBuffer.AsMemory(0, _sendLength), cancellationToken).ConfigureAwait(false);

                if (Settings.AppendDelimiter)
                {
                    await line.Stream.WriteAsync(new[] { Settings.Delimiter }, cancellationToken).ConfigureAwait(false);
                }

                await line.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                FramesSent++;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                State = EndpointState.Faulted;
                throw new EndpointException(Id, $"could not write to {Target}: {ex.Message}", ex);
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

    private async Task ReceiveLoopAsync(FrameReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ReadOnlyMemory<byte> frame = await reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false);

                // The reader reuses its buffer, so the published frame must be a copy.
                PublishFrame(frame.ToArray());
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or EndOfStreamException or ObjectDisposedException or IOException)
        {
            // The line closed or the endpoint is being disconnected.
        }
        catch (InvalidDataException)
        {
            // The framing configuration does not match the line; stop reading rather than
            // spinning on the same bad data.
            State = EndpointState.Faulted;
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
        waiting.TrySetResult(frame);
    }

    private void EnsureSendCapacity(int required)
    {
        if (Settings.Framing == FramingMode.Fixed)
        {
            _sendLength = Settings.FrameBytes;
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
        _line?.Dispose();
        _line = null;
        State = state;
    }

    private static TaskCompletionSource<ReadOnlyMemory<byte>> NewFrameSlot() =>
        new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>An open serial line: the byte stream plus ownership of the underlying port.</summary>
public interface ISerialLine : IDisposable
{
    /// <summary>The bidirectional byte stream of the line.</summary>
    Stream Stream { get; }
}

/// <summary>A real serial port opened through <see cref="SerialPort"/>.</summary>
internal sealed class SystemSerialLine : ISerialLine
{
    private readonly SerialPort _port;

    private SystemSerialLine(SerialPort port) => _port = port;

    public Stream Stream => _port.BaseStream;

    /// <summary>Opens the configured port. This is the only code path that needs real hardware.</summary>
    public static ISerialLine Open(SerialSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        SerialPort port = new SerialPort(
            settings.PortName,
            settings.BaudRate,
            settings.Parity,
            settings.DataBits,
            settings.StopBits);

        try
        {
            port.Open();
        }
        catch
        {
            port.Dispose();
            throw;
        }

        return new SystemSerialLine(port);
    }

    public void Dispose() => _port.Dispose();
}
