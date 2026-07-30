using System.Net.Sockets;
using Pb.Core.Channels;
using Pb.Core.Endpoints;

namespace Pb.Core.Modbus;

/// <summary>
/// MODBUS TCP master. Polls a slave or gateway over one TCP connection, serialising
/// transactions so that a request and its response are never interleaved.
/// </summary>
/// <remarks>
/// Written from spec/modbus-tcp-subset.md only. Reads are issued per channel rather than
/// coalesced into block reads: a personal-scale bridge polls tens of channels, and one
/// request per channel keeps the failure of one address from hiding the values of its
/// neighbours.
/// </remarks>
public sealed class ModbusTcpEndpoint : IEndpoint, IPollSource
{
    private readonly SemaphoreSlim _transactionGate = new SemaphoreSlim(1, 1);
    private readonly byte[] _headerBuffer = new byte[ModbusTcpFrame.HeaderSize];
    private readonly byte[] _pduBuffer = new byte[ModbusFunctions.MaxPduSize];

    private TcpClient? _client;
    private NetworkStream? _stream;
    private ushort _transactionId;
    private bool _disposed;

    public ModbusTcpEndpoint(string id, ModbusTcpSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Host);

        Id = id;
        Settings = settings;
    }

    /// <summary>Driver token this endpoint is configured as.</summary>
    public const string TypeToken = "modbus_tcp";

    public string Id { get; }

    public string Kind => TypeToken;

    public ModbusTcpSettings Settings { get; }

    public EndpointState State { get; private set; } = EndpointState.Disconnected;

    public string Target => Settings.ToString();

    /// <summary>Transaction identifier used by the most recent request.</summary>
    public ushort LastTransactionId => _transactionId;

    public bool Supports(ChannelSpec channel, ChannelRole role, out string? error)
    {
        if (role == ChannelRole.Sink)
        {
            // Write function codes are not in spec/modbus-tcp-subset.md §3; a Modbus slave
            // sink is future work.
            error = "Modbus writes are not implemented, so a modbus-tcp endpoint cannot be a route sink.";
            return false;
        }

        return ModbusAddressSpace.TryPlanRead(channel, out _, out _, out error);
    }

    public async ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _transactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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

                _client = client;
                _stream = client.GetStream();
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
            catch (Exception ex) when (ex is SocketException or IOException)
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
            _transactionGate.Release();
        }
    }

    public async ValueTask DisconnectAsync()
    {
        await _transactionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            CloseTransport(EndpointState.Disconnected);
        }
        finally
        {
            _transactionGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(ChannelSpec channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (!ModbusAddressSpace.TryPlanRead(channel, out ModbusFunction function, out int elements, out string? error))
        {
            throw new EndpointException(Id, $"cannot read channel '{channel.Name}': {error}");
        }

        if (function.IsBitFunction())
        {
            bool[] bits = await ReadBitsAsync(function, channel.Address.Index, 1, cancellationToken).ConfigureAwait(false);
            return new byte[] { bits[0] ? (byte)1 : (byte)0 };
        }

        ReadOnlyMemory<byte> registers = await TransactAsync(
            function,
            channel.Address.Index,
            elements,
            cancellationToken).ConfigureAwait(false);

        if (channel.Type != DataType.Bool)
        {
            return registers;
        }

        // A bool stored in a register is true when any bit of that register is set.
        return new byte[] { registers.Span[0] != 0 || registers.Span[1] != 0 ? (byte)1 : (byte)0 };
    }

    /// <summary>Reads a block of 16-bit registers.</summary>
    public async ValueTask<ushort[]> ReadRegistersAsync(
        ModbusFunction function,
        int startAddress,
        int count,
        CancellationToken cancellationToken)
    {
        if (!function.IsRegisterFunction())
        {
            throw new ArgumentOutOfRangeException(nameof(function), function, "Not a register read function.");
        }

        ReadOnlyMemory<byte> data = await TransactAsync(function, startAddress, count, cancellationToken)
            .ConfigureAwait(false);

        return ModbusPdu.DecodeRegisters(data.Span);
    }

    /// <summary>Reads a block of single bits.</summary>
    public async ValueTask<bool[]> ReadBitsAsync(
        ModbusFunction function,
        int startAddress,
        int count,
        CancellationToken cancellationToken)
    {
        if (!function.IsBitFunction())
        {
            throw new ArgumentOutOfRangeException(nameof(function), function, "Not a bit read function.");
        }

        ReadOnlyMemory<byte> data = await TransactAsync(function, startAddress, count, cancellationToken)
            .ConfigureAwait(false);

        return ModbusPdu.DecodeBits(data.Span, count);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync().ConfigureAwait(false);
        _transactionGate.Dispose();
    }

    /// <summary>
    /// Runs one request/response exchange and returns the response data payload. The gate makes
    /// transactions mutually exclusive, because a MODBUS TCP connection carries one
    /// outstanding transaction at a time in this client.
    /// </summary>
    private async ValueTask<ReadOnlyMemory<byte>> TransactAsync(
        ModbusFunction function,
        int startAddress,
        int quantity,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] pdu = ModbusPdu.BuildReadRequest(function, startAddress, quantity);

        await _transactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NetworkStream stream = _stream
                ?? throw new EndpointException(Id, "is not connected; call ConnectAsync first.");

            ushort transactionId = NextTransactionId();
            byte[] request = ModbusTcpFrame.Wrap(transactionId, Settings.UnitId, pdu);

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Settings.EffectiveRequestTimeout);

            try
            {
                await stream.WriteAsync(request, timeout.Token).ConfigureAwait(false);
                await stream.ReadExactlyAsync(_headerBuffer, timeout.Token).ConfigureAwait(false);

                (ushort responseTransactionId, byte unitId, int pduLength) = ModbusTcpFrame.ParseHeader(_headerBuffer);

                Memory<byte> responsePdu = _pduBuffer.AsMemory(0, pduLength);
                await stream.ReadExactlyAsync(responsePdu, timeout.Token).ConfigureAwait(false);

                // A mismatched transaction or unit id means the stream is no longer aligned with
                // our requests. There is no safe way to resynchronise, so the connection is
                // dropped and the supervisor reconnects.
                if (responseTransactionId != transactionId)
                {
                    CloseTransport(EndpointState.Faulted);
                    throw new EndpointException(
                        Id,
                        $"response carries transaction id {responseTransactionId} but {transactionId} was sent; connection dropped to resynchronise.");
                }

                if (unitId != Settings.UnitId)
                {
                    CloseTransport(EndpointState.Faulted);
                    throw new EndpointException(
                        Id,
                        $"response carries unit id {unitId} but {Settings.UnitId} was addressed; connection dropped to resynchronise.");
                }

                // An exception response is a healthy link refusing a request, so the connection
                // stays up and only this read fails.
                return ExtractResponseData(responsePdu.Span, function, quantity);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                CloseTransport(EndpointState.Faulted);
                throw new EndpointException(
                    Id,
                    $"request to {Target} timed out after {Settings.EffectiveRequestTimeout.TotalMilliseconds:F0} ms.");
            }
            catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException)
            {
                CloseTransport(EndpointState.Faulted);
                throw new EndpointException(Id, $"transport failure talking to {Target}: {ex.Message}", ex);
            }
            catch (ModbusProtocolException ex)
            {
                CloseTransport(EndpointState.Faulted);
                throw new EndpointException(Id, $"malformed response from {Target}: {ex.Message}", ex);
            }
        }
        finally
        {
            _transactionGate.Release();
        }
    }

    /// <summary>
    /// Copies the response payload out of the shared receive buffer. Spans cannot live inside
    /// an async method, so the validation step is factored out here.
    /// </summary>
    private static byte[] ExtractResponseData(ReadOnlySpan<byte> pdu, ModbusFunction function, int quantity) =>
        ModbusPdu.ParseReadResponse(pdu, function, quantity).ToArray();

    private ushort NextTransactionId()
    {
        _transactionId = _transactionId == ushort.MaxValue ? (ushort)1 : (ushort)(_transactionId + 1);
        return _transactionId;
    }

    private void CloseTransport(EndpointState state)
    {
        _stream?.Dispose();
        _stream = null;
        _client?.Dispose();
        _client = null;
        State = state;
    }
}
