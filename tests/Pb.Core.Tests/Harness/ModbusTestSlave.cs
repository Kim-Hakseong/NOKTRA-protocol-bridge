using System.Net;
using System.Net.Sockets;
using Pb.Core.Modbus;

namespace Pb.Core.Tests.Harness;

/// <summary>
/// A MODBUS TCP slave for tests: listens on a loopback port, serves an in-memory data model
/// and answers exactly the read functions recorded in spec/modbus-tcp-subset.md §3, replying
/// with exception <c>01</c> to anything else.
/// </summary>
/// <remarks>
/// This is deliberately an independent implementation of the server side of the protocol. It
/// makes the master's tests real request/response exchanges over a socket rather than a mock
/// agreeing with itself, and it is the same slave the M7 end-to-end test drives.
/// </remarks>
internal sealed class ModbusTestSlave : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
    private readonly Task _acceptLoop;
    private readonly ushort[] _holding = new ushort[0x1_0000];
    private readonly ushort[] _input = new ushort[0x1_0000];
    private readonly bool[] _coils = new bool[0x1_0000];
    private readonly bool[] _discrete = new bool[0x1_0000];
    private readonly List<Exception> _serverFaults = [];
    private readonly object _sync = new object();

    private int _requestCount;
    private bool _disposed;

    /// <param name="unitId">Unit identifier this slave answers to.</param>
    /// <param name="port">
    /// Loopback port to listen on, or 0 to take any free one. A fixed port lets a test stop and
    /// restart the same slave, which is how reconnect behaviour is exercised.
    /// </param>
    public ModbusTestSlave(byte unitId = 1, int port = 0)
    {
        UnitId = unitId;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
    }

    /// <summary>Loopback port the slave is listening on.</summary>
    public int Port { get; }

    /// <summary>Unit identifier this slave answers to.</summary>
    public byte UnitId { get; }

    /// <summary>Number of requests served since construction.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>
    /// Set to answer every request with this exception code instead of data, to exercise the
    /// master's handling of a healthy link that refuses requests.
    /// </summary>
    public ModbusExceptionCode? ForcedException { get; set; }

    /// <summary>
    /// Set to reply with a transaction id offset by this amount, to exercise the master's
    /// resynchronisation path.
    /// </summary>
    public int TransactionIdSkew { get; set; }

    /// <summary>Set to reply with a unit id offset by this amount.</summary>
    public int UnitIdSkew { get; set; }

    /// <summary>Set to accept requests and never answer, to exercise the master's timeout.</summary>
    public bool SwallowRequests { get; set; }

    /// <summary>Highest register index the data model exposes; reads beyond it answer exception 02.</summary>
    public int AddressLimit { get; set; } = 0x1_0000;

    public void SetHolding(int address, ushort value) => _holding[address] = value;

    public void SetHolding(int address, params ushort[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        values.CopyTo(_holding, address);
    }

    public void SetInput(int address, ushort value) => _input[address] = value;

    public void SetCoil(int address, bool value) => _coils[address] = value;

    public void SetDiscrete(int address, bool value) => _discrete[address] = value;

    /// <summary>Exceptions the server loop hit, so a test never passes because the slave died quietly.</summary>
    public IReadOnlyList<Exception> ServerFaults
    {
        get
        {
            lock (_sync)
            {
                return _serverFaults.ToArray();
            }
        }
    }

    /// <summary>Stops accepting connections, so the next master request fails at the transport.</summary>
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
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (SocketException)
        {
            // The listener was stopped by a test.
        }
        catch (ObjectDisposedException)
        {
            // The listener was stopped by a test.
        }

        await Task.WhenAll(clients).ConfigureAwait(false);
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            byte[] header = new byte[ModbusTcpFrame.HeaderSize];
            byte[] pdu = new byte[ModbusFunctions.MaxPduSize];

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
                    (ushort transactionId, byte unitId, int pduLength) = ModbusTcpFrame.ParseHeader(header);
                    await stream.ReadExactlyAsync(pdu.AsMemory(0, pduLength), cancellationToken).ConfigureAwait(false);

                    Interlocked.Increment(ref _requestCount);

                    if (SwallowRequests)
                    {
                        continue;
                    }

                    byte[] responsePdu = BuildResponse(pdu.AsMemory(0, pduLength));
                    byte[] response = ModbusTcpFrame.Wrap(
                        (ushort)(transactionId + TransactionIdSkew),
                        (byte)(unitId + UnitIdSkew),
                        responsePdu);

                    await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or EndOfStreamException or IOException)
            {
                // The master closed the connection or the slave is shutting down.
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    _serverFaults.Add(ex);
                }
            }
        }
    }

    private byte[] BuildResponse(ReadOnlyMemory<byte> requestPdu)
    {
        byte function = requestPdu.Span[0];

        if (!ModbusFunctions.IsSupported(function))
        {
            return ModbusPdu.BuildExceptionResponse(function, ModbusExceptionCode.IllegalFunction);
        }

        if (ForcedException is ModbusExceptionCode forced)
        {
            return ModbusPdu.BuildExceptionResponse(function, forced);
        }

        (ModbusFunction code, int start, int quantity) = ModbusPdu.ParseReadRequest(requestPdu.Span);

        if (quantity < 1 || quantity > code.MaxElementsPerRead())
        {
            return ModbusPdu.BuildExceptionResponse(function, ModbusExceptionCode.IllegalDataValue);
        }

        if (start + quantity > AddressLimit)
        {
            return ModbusPdu.BuildExceptionResponse(function, ModbusExceptionCode.IllegalDataAddress);
        }

        return code switch
        {
            ModbusFunction.ReadHoldingRegisters => ModbusPdu.BuildRegisterResponse(code, _holding.AsSpan(start, quantity)),
            ModbusFunction.ReadInputRegisters => ModbusPdu.BuildRegisterResponse(code, _input.AsSpan(start, quantity)),
            ModbusFunction.ReadCoils => ModbusPdu.BuildBitResponse(code, _coils.AsSpan(start, quantity)),
            _ => ModbusPdu.BuildBitResponse(code, _discrete.AsSpan(start, quantity)),
        };
    }
}
