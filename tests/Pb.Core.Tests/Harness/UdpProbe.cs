using System.Net;
using System.Net.Sockets;

namespace Pb.Core.Tests.Harness;

/// <summary>
/// A loopback UDP counterpart for endpoint tests: receives what the bridge sends and sends what
/// the bridge should receive.
/// </summary>
internal sealed class UdpProbe : IDisposable
{
    private readonly UdpClient _client;

    public UdpProbe()
    {
        _client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)_client.Client.LocalEndPoint!).Port;
    }

    /// <summary>Loopback port this probe is bound to.</summary>
    public int Port { get; }

    /// <summary>Waits for one datagram and returns its payload.</summary>
    public async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        UdpReceiveResult result = await _client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        return result.Buffer;
    }

    /// <summary>Sends a datagram to a loopback port.</summary>
    public async Task SendAsync(int port, byte[] payload, CancellationToken cancellationToken)
    {
        await _client.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, port), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Finds a free loopback UDP port by binding and releasing it.</summary>
    public static int FreePort()
    {
        using UdpClient probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// An in-memory stand-in for a serial line: a full-duplex pair of streams whose other ends the
/// test drives, so every behaviour above the driver is exercised without hardware.
/// </summary>
internal sealed class FakeSerialLine : Pb.Core.Endpoints.Serial.ISerialLine
{
    private readonly LoopbackStream _stream;

    public FakeSerialLine() => _stream = new LoopbackStream();

    public Stream Stream => _stream;

    /// <summary>True once the endpoint has closed the line.</summary>
    public bool Disposed { get; private set; }

    /// <summary>Bytes the endpoint has written to the line.</summary>
    public byte[] Written => _stream.Written;

    /// <summary>Makes bytes available to the endpoint as if the far side had sent them.</summary>
    public void Feed(params byte[] data) => _stream.Feed(data);

    /// <summary>Signals end-of-line, as an unplugged adapter would.</summary>
    public void EndOfLine() => _stream.EndOfLine();

    /// <summary>Makes the next read or write fail, as a removed adapter would.</summary>
    public void Break() => _stream.Break();

    public void Dispose()
    {
        Disposed = true;
        _stream.Dispose();
    }
}

/// <summary>A stream whose reads come from a test-fed queue and whose writes are recorded.</summary>
internal sealed class LoopbackStream : Stream
{
    private readonly Queue<byte> _inbound = new Queue<byte>();
    private readonly List<byte> _outbound = [];
    private readonly object _sync = new object();

    private TaskCompletionSource _dataAvailable = NewSignal();
    private bool _ended;
    private bool _broken;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public byte[] Written
    {
        get
        {
            lock (_sync)
            {
                return _outbound.ToArray();
            }
        }
    }

    public void Feed(byte[] data)
    {
        lock (_sync)
        {
            foreach (byte b in data)
            {
                _inbound.Enqueue(b);
            }
        }

        Wake();
    }

    public void EndOfLine()
    {
        lock (_sync)
        {
            _ended = true;
        }

        Wake();
    }

    public void Break()
    {
        lock (_sync)
        {
            _broken = true;
        }

        Wake();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        while (true)
        {
            Task waitForData;

            lock (_sync)
            {
                if (_broken)
                {
                    throw new IOException("The line was removed.");
                }

                if (_inbound.Count > 0)
                {
                    int count = Math.Min(buffer.Length, _inbound.Count);

                    for (int i = 0; i < count; i++)
                    {
                        buffer.Span[i] = _inbound.Dequeue();
                    }

                    return count;
                }

                if (_ended)
                {
                    return 0;
                }

                waitForData = _dataAvailable.Task;
            }

            await waitForData.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Releases every reader waiting for data, and arms the next wait.</summary>
    private void Wake()
    {
        TaskCompletionSource waiting;

        lock (_sync)
        {
            waiting = _dataAvailable;
            _dataAvailable = NewSignal();
        }

        waiting.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        lock (_sync)
        {
            if (_broken)
            {
                throw new IOException("The line was removed.");
            }

            _outbound.AddRange(buffer.ToArray());
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Break();
        }

        base.Dispose(disposing);
    }
}
