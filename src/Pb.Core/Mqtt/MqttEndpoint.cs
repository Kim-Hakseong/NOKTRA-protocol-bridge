using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Pb.Core.Channels;
using Pb.Core.Endpoints;
using Pb.Core.Time;

namespace Pb.Core.Mqtt;

/// <summary>
/// Lightweight MQTT 3.1.1 publisher: CONNECT / CONNACK, PUBLISH at QoS 0, PINGREQ / PINGRESP and
/// DISCONNECT, and nothing else (spec/mqtt-subset.md).
/// </summary>
/// <remarks>
/// Keep-alive is driven by <see cref="TickAsync"/> rather than a hidden timer, so the bridge
/// supervisor owns all periodic work and the behaviour is deterministic under an injected time
/// source. Publishing also sends a ping first when the line has been idle, so a slow route never
/// lets the broker time the session out.
/// </remarks>
public sealed class MqttEndpoint : IEndpoint, IValueSink, IEndpointUpkeep
{
    /// <summary>Driver token this endpoint is configured as.</summary>
    public const string TypeToken = "mqtt";

    /// <summary>Address space MQTT channels must use (spec §9).</summary>
    public const string AddressSpace = "topic";

    private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
    private readonly ITimeSource _time;
    private readonly byte[] _receiveBuffer = new byte[8];

    private TcpClient? _client;
    private NetworkStream? _stream;
    private TimeSpan _lastSent;
    private bool _disposed;

    public MqttEndpoint(string id, MqttSettings settings, ITimeSource? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Host);

        Id = id;
        Settings = settings;
        _time = time ?? SystemTimeSource.Instance;
    }

    public string Id { get; }

    public string Kind => TypeToken;

    public MqttSettings Settings { get; }

    public EndpointState State { get; private set; } = EndpointState.Disconnected;

    public string Target => Settings.ToString();

    /// <summary>Messages published since construction.</summary>
    public long MessagesPublished { get; private set; }

    /// <summary>Keep-alive pings sent since construction.</summary>
    public long PingsSent { get; private set; }

    /// <summary>True when the broker reported an existing session in CONNACK.</summary>
    public bool SessionPresent { get; private set; }

    /// <summary>
    /// How long the connection may stay idle before a ping is due: half the keep-alive interval,
    /// which leaves ample margin inside the 1.5x window the broker allows.
    /// </summary>
    public TimeSpan PingInterval => Settings.EffectiveKeepAlive == TimeSpan.Zero
        ? TimeSpan.Zero
        : Settings.EffectiveKeepAlive / 2;

    public bool Supports(ChannelSpec channel, ChannelRole role, out string? error)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (role == ChannelRole.Source)
        {
            // Only the publish half of MQTT is implemented; SUBSCRIBE is not in the spec subset.
            error = "an mqtt endpoint is publish-only, so it cannot be a route source.";
            return false;
        }

        if (!string.Equals(channel.Address.Space, AddressSpace, StringComparison.Ordinal) || channel.Address.Index != 0)
        {
            error = $"an mqtt channel is addressed by name, so its address must be '{AddressSpace}:0'.";
            return false;
        }

        try
        {
            MqttPacket.ValidateTopic(Settings.TopicFor(channel.Name));
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }

        error = null;
        return true;
    }

    public async ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stream is not null)
            {
                return;
            }

            State = EndpointState.Connecting;
            TcpClient client = new TcpClient { NoDelay = true };

            try
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(Settings.EffectiveConnectTimeout);

                await client.ConnectAsync(Settings.Host, Settings.Port, timeout.Token).ConfigureAwait(false);
                NetworkStream stream = client.GetStream();

                byte[] connect = MqttPacket.BuildConnect(
                    Settings.ClientId,
                    Settings.KeepAliveSeconds,
                    Settings.CleanSession,
                    Settings.UserName,
                    Settings.Password);

                await stream.WriteAsync(connect, timeout.Token).ConfigureAwait(false);
                SessionPresent = await ReadConnAckAsync(stream, timeout.Token).ConfigureAwait(false);

                _client = client;
                _stream = stream;
                _lastSent = _time.Elapsed;
                State = EndpointState.Connected;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                client.Dispose();
                State = EndpointState.Faulted;
                throw new EndpointException(
                    Id,
                    $"connecting to {Target} timed out after {Settings.EffectiveConnectTimeout.TotalMilliseconds:F0} ms.");
            }
            catch (MqttConnectRefusedException ex)
            {
                client.Dispose();
                State = EndpointState.Faulted;
                throw new EndpointException(Id, ex.Message, ex);
            }
            catch (Exception ex) when (ex is SocketException or IOException or EndOfStreamException or MqttProtocolException)
            {
                client.Dispose();
                State = EndpointState.Faulted;
                throw new EndpointException(Id, $"could not connect to {Target}: {ex.Message}", ex);
            }
            catch
            {
                client.Dispose();
                State = EndpointState.Faulted;
                throw;
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Sends DISCONNECT if the connection is up, then closes it. The spec requires the client to
    /// close the connection after DISCONNECT and send nothing further.
    /// </summary>
    public async ValueTask DisconnectAsync()
    {
        await _sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stream is not null)
            {
                try
                {
                    await _stream.WriteAsync(MqttPacket.Disconnect.ToArray()).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    // A broker that has already gone away needs no farewell.
                }
            }

            CloseTransport(EndpointState.Disconnected);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask WriteAsync(ChannelSpec channel, Sample sample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Supports(channel, ChannelRole.Sink, out string? error))
        {
            throw new EndpointException(Id, $"cannot publish channel '{channel.Name}': {error}");
        }

        byte[] packet = MqttPacket.BuildPublish(
            Settings.TopicFor(channel.Name),
            FormatPayload(channel, sample),
            Settings.Retain);

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NetworkStream stream = _stream
                ?? throw new EndpointException(Id, "is not connected; call ConnectAsync first.");

            await SendKeepAliveIfDueAsync(stream, cancellationToken).ConfigureAwait(false);
            await SendAsync(stream, packet, cancellationToken).ConfigureAwait(false);
            MessagesPublished++;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Periodic upkeep called by the bridge supervisor: sends a keep-alive ping when the
    /// connection has been idle for longer than <see cref="PingInterval"/>.
    /// </summary>
    public async ValueTask TickAsync(CancellationToken cancellationToken)
    {
        if (_disposed || _stream is null)
        {
            return;
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stream is not null)
            {
                await SendKeepAliveIfDueAsync(_stream, cancellationToken).ConfigureAwait(false);
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

        await DisconnectAsync().ConfigureAwait(false);
        _disposed = true;
        _sendGate.Dispose();
    }

    /// <summary>Renders the payload of one sample (spec §5 leaves the payload to the application).</summary>
    internal byte[] FormatPayload(ChannelSpec channel, Sample sample)
    {
        if (Settings.Payload == MqttPayloadFormat.Value)
        {
            return Encoding.UTF8.GetBytes(sample.Value.ToString("R", CultureInfo.InvariantCulture));
        }

        StringBuilder json = new StringBuilder(96);
        json.Append("{\"channel\":\"").Append(JsonEscape(channel.Name)).Append('"');
        json.Append(",\"value\":").Append(FormatJsonNumber(sample.Value));
        json.Append(",\"quality\":\"").Append(sample.Quality.ToString().ToLowerInvariant()).Append('"');

        if (sample.Unit is not null)
        {
            json.Append(",\"unit\":\"").Append(JsonEscape(sample.Unit)).Append('"');
        }

        json.Append(",\"timestamp\":\"")
            .Append(sample.Timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
            .Append("\"}");

        return Encoding.UTF8.GetBytes(json.ToString());
    }

    /// <summary>
    /// JSON has no literal for a non-finite number, so those are written as null with the
    /// quality field carrying the reason.
    /// </summary>
    private static string FormatJsonNumber(double value) => double.IsFinite(value)
        ? value.ToString("R", CultureInfo.InvariantCulture)
        : "null";

    private static string JsonEscape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    private async ValueTask SendKeepAliveIfDueAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        if (PingInterval == TimeSpan.Zero || _time.Elapsed - _lastSent < PingInterval)
        {
            return;
        }

        await SendAsync(stream, MqttPacket.PingReq.ToArray(), cancellationToken).ConfigureAwait(false);
        PingsSent++;
    }

    private async ValueTask SendAsync(NetworkStream stream, byte[] packet, CancellationToken cancellationToken)
    {
        try
        {
            await stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
            _lastSent = _time.Elapsed;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            CloseTransport(EndpointState.Faulted);
            throw new EndpointException(Id, $"could not send to {Target}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Reads the CONNACK that must answer CONNECT. PINGRESP packets are tolerated ahead of it
    /// only in the sense that any other packet type is a protocol error, which is what the spec
    /// requires of a first response.
    /// </summary>
    private async ValueTask<bool> ReadConnAckAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        await stream.ReadExactlyAsync(_receiveBuffer.AsMemory(0, 2), cancellationToken).ConfigureAwait(false);
        byte firstByte = _receiveBuffer[0];

        // Check the type before the length, so a broker answering with the wrong packet produces a
        // message that names what it actually sent.
        if ((firstByte >> 4) != (byte)MqttPacketType.ConnAck)
        {
            throw new MqttProtocolException(
                $"Expected CONNACK but the broker sent {MqttPacket.DescribePacketType(firstByte)}.");
        }

        if (!MqttPacket.TryReadRemainingLength(_receiveBuffer.AsSpan(1, 1), out int bodyLength, out _))
        {
            throw new MqttProtocolException("A CONNACK Remaining Length must fit in one byte (spec §4).");
        }

        if (bodyLength != 2)
        {
            throw new MqttProtocolException($"A CONNACK body is 2 bytes but the header announces {bodyLength}.");
        }

        await stream.ReadExactlyAsync(_receiveBuffer.AsMemory(2, 2), cancellationToken).ConfigureAwait(false);
        return ParseConnAck(firstByte, _receiveBuffer);
    }

    /// <summary>Spans cannot live inside an async method, so parsing is factored out.</summary>
    private static bool ParseConnAck(byte firstByte, byte[] buffer) =>
        MqttPacket.ParseConnAck(firstByte, buffer.AsSpan(2, 2));

    private void CloseTransport(EndpointState state)
    {
        _stream?.Dispose();
        _stream = null;
        _client?.Dispose();
        _client = null;
        State = state;
    }
}
