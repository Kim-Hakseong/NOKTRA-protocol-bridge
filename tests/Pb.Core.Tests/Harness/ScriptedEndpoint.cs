using Pb.Core.Channels;
using Pb.Core.Endpoints;

namespace Pb.Core.Tests.Harness;

/// <summary>One value written to a <see cref="ScriptedEndpoint"/>.</summary>
internal sealed record WrittenValue(string Channel, Sample Sample);

/// <summary>
/// An in-memory endpoint whose every behaviour a test can script: what a read returns, when a
/// read or write fails, and how many connection attempts fail before one succeeds. It signals
/// each event, so router tests wait on facts rather than on elapsed time.
/// </summary>
internal sealed class ScriptedEndpoint : IEndpoint, IPollSource, IFrameSource, IValueSink, IEndpointUpkeep
{
    private readonly object _sync = new object();
    private readonly List<WrittenValue> _written = [];

    private TaskCompletionSource _event = NewSignal();
    private TaskCompletionSource<ReadOnlyMemory<byte>> _nextFrame = NewFrameSlot();
    private byte[] _readValue = [0x00, 0x00];
    private long _reads;
    private long _connects;
    private long _ticks;
    private long _framesReceived;
    private int _connectFailuresRemaining;
    private bool _disposed;

    public ScriptedEndpoint(string id, string kind = "scripted")
    {
        Id = id;
        Kind = kind;
    }

    public string Id { get; }

    public string Kind { get; }

    public EndpointState State { get; private set; } = EndpointState.Disconnected;

    public string Target => $"scripted:{Id}";

    public long FramesReceived => Interlocked.Read(ref _framesReceived);

    /// <summary>Reads served so far.</summary>
    public long Reads => Interlocked.Read(ref _reads);

    /// <summary>Connection attempts that reached this endpoint.</summary>
    public long Connects => Interlocked.Read(ref _connects);

    /// <summary>Upkeep ticks received.</summary>
    public long Ticks => Interlocked.Read(ref _ticks);

    /// <summary>Values written to this endpoint, oldest first.</summary>
    public IReadOnlyList<WrittenValue> Written
    {
        get
        {
            lock (_sync)
            {
                return _written.ToArray();
            }
        }
    }

    /// <summary>Set to make every read fail with this message.</summary>
    public string? ReadFailure { get; set; }

    /// <summary>Set to make every write fail with this message.</summary>
    public string? WriteFailure { get; set; }

    /// <summary>
    /// Whether a scripted read or write failure also drops the transport. Off by default, which
    /// models a healthy link refusing one request — a Modbus exception response, say — so clearing
    /// the failure restores service without waiting for a reconnect.
    /// </summary>
    public bool FaultTransportOnFailure { get; set; }

    /// <summary>Set to make the endpoint refuse to be a source in <see cref="Supports"/>.</summary>
    public bool RefuseAsSource { get; set; }

    /// <summary>Set to make the endpoint refuse to be a sink in <see cref="Supports"/>.</summary>
    public bool RefuseAsSink { get; set; }

    /// <summary>Set to make upkeep throw.</summary>
    public string? UpkeepFailure { get; set; }

    /// <summary>Makes the next <paramref name="count"/> connection attempts fail.</summary>
    public void FailNextConnects(int count)
    {
        lock (_sync)
        {
            _connectFailuresRemaining = count;
        }
    }

    /// <summary>Sets the bytes the next reads return.</summary>
    public void SetReadValue(params byte[] value)
    {
        lock (_sync)
        {
            _readValue = value;
        }
    }

    /// <summary>Sets the read value as a big-endian unsigned 16-bit register.</summary>
    public void SetRegister(ushort value) => SetReadValue((byte)(value >> 8), (byte)(value & 0xFF));

    /// <summary>Delivers a frame to whoever is waiting in <see cref="ReceiveFrameAsync"/>.</summary>
    public void PushFrame(params byte[] frame)
    {
        TaskCompletionSource<ReadOnlyMemory<byte>> waiting;

        lock (_sync)
        {
            waiting = _nextFrame;
            _nextFrame = NewFrameSlot();
        }

        Interlocked.Increment(ref _framesReceived);
        waiting.TrySetResult(frame);
        Signal();
    }

    /// <summary>Drops the transport as if the far side had gone away, without a graceful close.</summary>
    public void ForceFault()
    {
        State = EndpointState.Faulted;
        Signal();
    }

    /// <summary>Waits until at least <paramref name="count"/> values have been written here.</summary>
    public Task WaitForWritesAsync(int count, CancellationToken cancellationToken) =>
        WaitForAsync(() => Written.Count >= count, cancellationToken);

    /// <summary>Waits until at least <paramref name="count"/> reads have been served.</summary>
    public Task WaitForReadsAsync(int count, CancellationToken cancellationToken) =>
        WaitForAsync(() => Reads >= count, cancellationToken);

    /// <summary>Waits until the endpoint is connected.</summary>
    public Task WaitForConnectedAsync(CancellationToken cancellationToken) =>
        WaitForAsync(() => State == EndpointState.Connected, cancellationToken);

    /// <summary>Waits until an arbitrary condition over this endpoint's state holds.</summary>
    public async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;

            lock (_sync)
            {
                if (condition())
                {
                    return;
                }

                changed = _event.Task;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public bool Supports(ChannelSpec channel, ChannelRole role, out string? error)
    {
        if (role == ChannelRole.Source && RefuseAsSource)
        {
            error = "scripted endpoint refuses source channels.";
            return false;
        }

        if (role == ChannelRole.Sink && RefuseAsSink)
        {
            error = "scripted endpoint refuses sink channels.";
            return false;
        }

        error = null;
        return true;
    }

    public ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _connects);

        lock (_sync)
        {
            if (_connectFailuresRemaining > 0)
            {
                _connectFailuresRemaining--;
                State = EndpointState.Faulted;
                Signal();
                throw new EndpointException(Id, "scripted connection failure.");
            }
        }

        State = EndpointState.Connected;
        Signal();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisconnectAsync()
    {
        State = EndpointState.Disconnected;
        Signal();
        return ValueTask.CompletedTask;
    }

    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ChannelSpec channel, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ReadFailure is string failure)
        {
            Fail();
            throw new EndpointException(Id, failure);
        }

        Interlocked.Increment(ref _reads);

        byte[] value;
        lock (_sync)
        {
            value = _readValue;
        }

        Signal();
        return ValueTask.FromResult<ReadOnlyMemory<byte>>(value);
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReceiveFrameAsync(CancellationToken cancellationToken)
    {
        if (ReadFailure is string failure)
        {
            Fail();
            throw new EndpointException(Id, failure);
        }

        Task<ReadOnlyMemory<byte>> next;
        lock (_sync)
        {
            next = _nextFrame.Task;
        }

        ReadOnlyMemory<byte> frame = await next.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _reads);
        return frame;
    }

    public ValueTask WriteAsync(ChannelSpec channel, Sample sample, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (WriteFailure is string failure)
        {
            Fail();
            throw new EndpointException(Id, failure);
        }

        lock (_sync)
        {
            _written.Add(new WrittenValue(channel.Name, sample));
        }

        Signal();
        return ValueTask.CompletedTask;
    }

    public ValueTask TickAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _ticks);

        if (UpkeepFailure is string failure)
        {
            Signal();
            throw new EndpointException(Id, failure);
        }

        Signal();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        State = EndpointState.Disconnected;
        Signal();
        return ValueTask.CompletedTask;
    }

    private static TaskCompletionSource NewSignal() =>
        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<ReadOnlyMemory<byte>> NewFrameSlot() =>
        new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Applies a scripted failure, dropping the transport only if asked to.</summary>
    private void Fail()
    {
        if (FaultTransportOnFailure)
        {
            State = EndpointState.Faulted;
        }

        Signal();
    }

    /// <summary>Wakes everyone waiting on a state change.</summary>
    private void Signal()
    {
        TaskCompletionSource waiting;

        lock (_sync)
        {
            waiting = _event;
            _event = NewSignal();
        }

        waiting.TrySetResult();
    }
}
