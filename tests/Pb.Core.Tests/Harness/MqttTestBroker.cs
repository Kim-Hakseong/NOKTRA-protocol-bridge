using System.Net;
using System.Net.Sockets;
using Pb.Core.Mqtt;

namespace Pb.Core.Tests.Harness;

/// <summary>One control packet as the test broker received it.</summary>
internal sealed record ReceivedPacket(MqttPacketType Type, byte FirstByte, byte[] Body)
{
    /// <summary>The complete packet as it arrived, fixed header included.</summary>
    public byte[] Raw { get; init; } = [];
}

/// <summary>
/// A minimal MQTT 3.1.1 broker for tests: accepts one connection at a time, answers CONNECT with
/// CONNACK and PINGREQ with PINGRESP, and records every packet it received so a test can assert
/// on the exact bytes the client produced.
/// </summary>
/// <remarks>
/// The publisher is verified by frame-level unit tests plus loopback rather than by standing up a
/// broker container, so this
/// independently written server plays the far side over a real socket. It reads packets with its
/// own framing loop, which also exercises the client's Remaining Length encoding.
/// </remarks>
internal sealed class MqttTestBroker : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
    private readonly Task _acceptLoop;
    private readonly List<ReceivedPacket> _packets = [];
    private readonly List<Exception> _faults = [];
    private readonly object _sync = new object();

    private TaskCompletionSource _packetArrived = NewSignal();
    private bool _disposed;

    public MqttTestBroker()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
    }

    /// <summary>Loopback port the broker is listening on.</summary>
    public int Port { get; }

    /// <summary>Return code the broker answers CONNECT with.</summary>
    public byte ConnectReturnCode { get; set; } = (byte)MqttConnectReturnCode.Accepted;

    /// <summary>Session Present flag the broker reports in CONNACK.</summary>
    public bool SessionPresent { get; set; }

    /// <summary>Set to answer CONNECT with something that is not a CONNACK.</summary>
    public byte[]? ConnectResponseOverride { get; set; }

    /// <summary>Set to accept the connection and never answer CONNECT.</summary>
    public bool SwallowConnect { get; set; }

    /// <summary>Packets received so far, oldest first.</summary>
    public IReadOnlyList<ReceivedPacket> Packets
    {
        get
        {
            lock (_sync)
            {
                return _packets.ToArray();
            }
        }
    }

    /// <summary>Exceptions the broker loop hit, so a test never passes because the broker died.</summary>
    public IReadOnlyList<Exception> Faults
    {
        get
        {
            lock (_sync)
            {
                return _faults.ToArray();
            }
        }
    }

    /// <summary>Waits until at least <paramref name="count"/> packets of <paramref name="type"/> have arrived.</summary>
    public async Task WaitForAsync(MqttPacketType type, int count, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task arrived;

            lock (_sync)
            {
                if (_packets.Count(p => p.Type == type) >= count)
                {
                    return;
                }

                arrived = _packetArrived.Task;
            }

            await arrived.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Stops accepting connections, so the next client request fails at the transport.</summary>
    public void StopListening() => _listener.Stop();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);

        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
            // Already stopped by a test.
        }

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        _shutdown.Dispose();
    }

    private static TaskCompletionSource NewSignal() =>
        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        List<Task> clients = [];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                clients.Add(Task.Run(() => ServeAsync(client, cancellationToken), CancellationToken.None));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Shutting down, or the listener was stopped by a test.
        }

        await Task.WhenAll(clients).ConfigureAwait(false);
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    ReceivedPacket packet = await ReadPacketAsync(stream, cancellationToken).ConfigureAwait(false);
                    Record(packet);

                    switch (packet.Type)
                    {
                        case MqttPacketType.Connect when SwallowConnect:
                            break;
                        case MqttPacketType.Connect:
                            await stream.WriteAsync(
                                ConnectResponseOverride ?? BuildConnAck(),
                                cancellationToken).ConfigureAwait(false);
                            break;
                        case MqttPacketType.PingReq:
                            await stream.WriteAsync(MqttPacket.PingResp.ToArray(), cancellationToken).ConfigureAwait(false);
                            break;
                        case MqttPacketType.Disconnect:
                            return;
                        default:
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or EndOfStreamException or IOException)
            {
                // The client closed the connection or the broker is shutting down.
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    _faults.Add(ex);
                }
            }
        }
    }

    private byte[] BuildConnAck() => [0x20, 0x02, SessionPresent ? (byte)0x01 : (byte)0x00, ConnectReturnCode];

    private static async Task<ReceivedPacket> ReadPacketAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] first = new byte[1];
        await stream.ReadExactlyAsync(first, cancellationToken).ConfigureAwait(false);

        List<byte> lengthBytes = [];
        int bodyLength;

        while (true)
        {
            byte[] digit = new byte[1];
            await stream.ReadExactlyAsync(digit, cancellationToken).ConfigureAwait(false);
            lengthBytes.Add(digit[0]);

            if (MqttPacket.TryReadRemainingLength(lengthBytes.ToArray(), out bodyLength, out _))
            {
                break;
            }
        }

        byte[] body = new byte[bodyLength];

        if (bodyLength > 0)
        {
            await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        }

        byte[] raw = [first[0], .. lengthBytes, .. body];

        return new ReceivedPacket((MqttPacketType)(first[0] >> 4), first[0], body) { Raw = raw };
    }

    private void Record(ReceivedPacket packet)
    {
        TaskCompletionSource waiting;

        lock (_sync)
        {
            _packets.Add(packet);
            waiting = _packetArrived;
            _packetArrived = NewSignal();
        }

        waiting.TrySetResult();
    }
}
