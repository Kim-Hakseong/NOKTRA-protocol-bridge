using Pb.Core.Channels;
using Pb.Core.Configuration;
using Pb.Core.Endpoints;
using Pb.Core.Modbus;
using Pb.Core.Tests.Harness;
using Pb.Core.Time;
using Pb.Core.Transforms;
using Xunit;

namespace Pb.Core.Tests;

/// <summary>
/// Exercises the master against the independently written test slave over a real loopback
/// socket, so the request/response exchange is genuinely round-tripped rather than mocked.
/// </summary>
public sealed class ModbusTcpEndpointTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(300);

    private readonly CancellationTokenSource _testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

    /// <summary>
    /// Bounds every socket operation in this class, so a broken exchange fails the test
    /// instead of hanging the run.
    /// </summary>
    private CancellationToken Ct => _testTimeout.Token;

    private static ChannelSpec Channel(
        string address,
        DataType type = DataType.U16,
        ByteOrder order = ByteOrder.BigEndian,
        string name = "c") =>
        new ChannelSpec(name, "plc", ChannelAddress.Parse(address), type, order);

    private static ModbusTcpEndpoint Master(ModbusTestSlave slave, TimeSpan? timeout = null) =>
        new ModbusTcpEndpoint("plc", new ModbusTcpSettings(
            "127.0.0.1",
            slave.Port,
            slave.UnitId,
            timeout ?? ShortTimeout,
            ShortTimeout));

    [Fact]
    public async Task Read_HoldingRegister_ReturnsTheRegisterBytesBigEndian()
    {
        await using ModbusTestSlave slave = new ModbusTestSlave();
        slave.SetHolding(0x006B, 0x022B);
        await using ModbusTcpEndpoint master = Master(slave);
        await master.ConnectAsync(Ct);

        ReadOnlyMemory<byte> raw = await master.ReadAsync(Channel("holding:107"), Ct);

        Assert.Equal([0x02, 0x2B], raw.ToArray());
        Assert.Equal(0x022B, ValueCodec.Decode(raw.Span, DataType.U16));
        Assert.Empty(slave.ServerFaults);
    }

    [Fact]
    public async Task Read_GoldenVectorRegisterBlock_MatchesTheDesignVector()
    {
        await using ModbusTestSlave slave = new ModbusTestSlave();
        slave.SetHolding(0x006B, 0x022B, 0x0000, 0x0064);
        await using ModbusTcpEndpoint master = Master(slave);
        await master.ConnectAsync(Ct);

        ushort[] registers = await master.ReadRegistersAsync(
            ModbusFunction.ReadHoldingRegisters,
            0x006B,
            3,
            Ct);

        Assert.Equal<ushort[]>([0x022B, 0x0000, 0x0064], registers);
    }

    [Fact]
    public async Task Read_SignedRegister_FeedsTheTransformGoldenVector()
    {
        await using ModbusTestSlave slave = new ModbusTestSlave();
        slave.SetHolding(0, 0xFFF6);
        await using ModbusTcpEndpoint master = Master(slave);
        await master.ConnectAsync(Ct);
        ChannelSpec channel = Channel("holding:0", DataType.S16);
        ChannelPipeline pipeline = new ChannelPipeline(channel, new ValueTransform(Scale: 0.1), new ManualTimeSource());

        ReadOnlyMemory<byte> raw = await master.ReadAsync(channel, Ct);

        Assert.True(pipeline.TryProcess(raw.Span, out Sample sample));
        Assert.Equal(-1.0, sample.Value, 12);
    }

    [Fact]
    public async Task Read_ThirtyTwoBitValue_SpansTwoRegistersInWireOrder()
    {
        await using ModbusTestSlave slave = new ModbusTestSlave();
        slave.SetHolding(10, 0x1234, 0x5678);
        await using ModbusTcpEndpoint master = Master(slave);
        await master.ConnectAsync(Ct);

        ReadOnlyMemory<byte> raw = await master.ReadAsync(Channel("holding:10", DataType.U32), Ct);

        Assert.Equal([0x12, 0x34, 0x56, 0x78], raw.ToArray());
        Assert.Equal((double)0x12345678, ValueCodec.Decode(raw.Span, DataType.U32));
        Assert.Equal((double)0x56781234, ValueCodec.Decode(raw.Span, DataType.U32, ByteOrder.WordSwappedBigEndian));
    }

    [Fact]
    public async Task Read_SixtyFourBitValue_SpansFourRegisters()
    {
        await using ModbusTestSlave slave = new ModbusTestSlave();
        slave.SetHolding(0, 0x3FF0, 0x0000, 0x0000, 0x0000);
        await using ModbusTcpEndpoint master = Master(slave);
        await master.ConnectAsync(Ct);

        ReadOnlyMemory<byte> raw = await master.ReadAsync(Channel("holding:0", DataType.F64), Ct);

        Assert.Equal(8, raw.Length);
        Assert.Equal(1.0, ValueCodec.Decode(raw.Span, DataType.F64));
    }

    [Fact]
    public async Task Read_InputRegister_UsesFunctionFour()
    {
        await using ModbusTestSlave slave = new ModbusTestSlave();
        slave.SetInput(5, 4242);
        slave.SetHolding(5, 1111);
        await using ModbusTcpEndpoint master = Master(slave);
        await master.ConnectAsync(Ct);

        ReadOnlyMemory<byte> raw = await master.ReadAsync(Channel("input:5"), Ct);

        Assert.Equal(4242.0, ValueCodec.Decode(raw.Span, DataType.U16));
    }

    [Theory]
    [InlineData("coil:3", true)]
    [InlineData("coil:4", false)]
    public async Task Read_Coil_ReturnsASingleBooleanByte(string address, bool expected)
    {
        await using ModbusTestSlave slave = new ModbusTestSlave();
        slave.SetCoil(3, true);
        await using ModbusTcpEndpoint master = Master(slave);
        await master.ConnectAsync(Ct);

        ReadOnlyMemory<byte> raw = await master.ReadAsync(Channel(address, DataType.Bool), Ct);

        Assert.Single(raw.ToArray());
        Assert.Equal(expected ? 1.0 : 0.0, ValueCodec.Decode(raw.Span, DataType.Bool));
    }

    [Fact]
    public async Task Read_DiscreteInput_UsesFunctionTwo()
    {
        await using ModbusTestSlave slave = new ModbusTestSlave();
        slave.SetDiscrete(9, true);
        slave.SetCoil(9, false);
        await using ModbusTcpEndpoint master = Master(slave);
        await master.ConnectAsync(Ct);

        ReadOnlyMemory<byte> raw = await master.ReadAsync(Channel("discrete:9", DataType.Bool), Ct);

        Assert.Equal(1.0, ValueCodec.Decode(raw.Span, DataType.Bool));
    }

    [Fact]
    public async Task Read_BoolStoredInARegister_IsTrueWhenAnyBitIsSet()
    {
        await using ModbusTestSlave slave = new ModbusTestSlave();
        slave.SetHolding(1, 0x0100);
        slave.SetHolding(2, 0x0000);
        await using ModbusTcpEndpoint master = Master(slave);
        await master.ConnectAsync(Ct);

        ReadOnlyMemory<byte> set = await master.ReadAsync(Channel("holding:1", DataType.Bool), Ct);
        ReadOnlyMemory<byte> clear = await master.ReadAsync(Channel("holding:2", DataType.Bool), Ct);

        Assert.Equal(1.0, ValueCodec.Decode(set.Span, DataType.Bool));
        Assert.Equal(0.0, ValueCodec.Decode(clear.Span, DataType.Bool));
    }

    [Fact]
    public async Task Read_ManyTimes_AdvancesTheTransactionIdentifier()
    {
        await using ModbusTestSlave slave = new ModbusTestSlave();
        await using ModbusTcpEndpoint master = Master(slave);
        await master.ConnectAsync(Ct);

        for (int i = 0; i < 5; i++)
        {
            await master.ReadAsync(Channel("holding:0"), Ct);
        }

        Assert.Equal(5, master.LastTransactionId);
        Assert.Equal(5, slave.RequestCount);
    }

    [Fact]
    public async Task Read_ConcurrentCalls_AreSerialisedOntoOneConnection()
    {
        await using ModbusTestSlave slave = new ModbusTestSlave();
        for (int i = 0; i < 20; i++)
        {
            slave.SetHolding(i, (ushort)(1000 + i));
        }

        await using ModbusTcpEndpoint master = Master(slave, TimeSpan.FromSeconds(5));
        await master.ConnectAsync(Ct);

        ReadOnlyMemory<byte>[] results = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(async i =>
                await master.ReadAsync(Channel($"holding:{i}"), Ct)));

        for (int i = 0; i < results.Length; i++)
        {
            Assert.Equal(1000.0 + i, ValueCodec.Decode(results[i].Span, DataType.U16));
        }

        Assert.Empty(slave.ServerFaults);
    }

    [Fact]
    public async Task ExceptionResponse_FailsTheReadButKeepsTheConnection()
    {
        await using ModbusTestSlave slave = new ModbusTestSlave { ForcedException = ModbusExceptionCode.IllegalDataAddress };
        await using ModbusTcpEndpoint master = Master(slave);
        await master.ConnectAsync(Ct);

        ModbusExceptionResponseException ex = await Assert.ThrowsAsync<ModbusExceptionResponseException>(
            async () => await master.ReadAsync(Channel("holding:0"), Ct));

        Assert.Equal(ModbusExceptionCode.IllegalDataAddress, ex.KnownCode);
        Assert.Equal(EndpointState.Connected, master.State);

        // The link is still usable once the slave stops refusing.
        slave.ForcedException = null;
        slave.SetHolding(0, 77);
        ReadOnlyMemory<byte> raw = await master.ReadAsync(Channel("holding:0"), Ct);
        Assert.Equal(77.0, ValueCodec.Decode(raw.Span, DataType.U16));
    }

    [Fact]
    public async Task ReadBeyondTheSlaveDataModel_YieldsIllegalDataAddress()
    {
        await using ModbusTestSlave slave = new ModbusTestSlave { AddressLimit = 10 };
        await using ModbusTcpEndpoint master = Master(slave);
        await master.ConnectAsync(Ct);

        ModbusExceptionResponseException ex = await Assert.ThrowsAsync<ModbusExceptionResponseException>(
            async () => await master.ReadAsync(Channel("holding:50"), Ct));

        Assert.Equal(ModbusExceptionCode.IllegalDataAddress, ex.KnownCode);
    }

    [Fact]
    public async Task Timeout_FaultsTheEndpointSoTheSupervisorReconnects()
    {
        await using ModbusTestSlave slave = new ModbusTestSlave { SwallowRequests = true };
        await using ModbusTcpEndpoint master = Master(slave);
        await master.ConnectAsync(Ct);

        EndpointException ex = await Assert.ThrowsAsync<EndpointException>(
            async () => await master.ReadAsync(Channel("holding:0"), Ct));

        Assert.Contains("timed out", ex.Message, StringComparison.Ordinal);
        Assert.Equal(EndpointState.Faulted, master.State);
    }

    [Fact]
    public async Task MismatchedTransactionId_DropsTheConnectionToResynchronise()
    {
        await using ModbusTestSlave slave = new ModbusTestSlave { TransactionIdSkew = 1 };
        await using ModbusTcpEndpoint master = Master(slave);
        await master.ConnectAsync(Ct);

        EndpointException ex = await Assert.ThrowsAsync<EndpointException>(
            async () => await master.ReadAsync(Channel("holding:0"), Ct));

        Assert.Contains("transaction id", ex.Message, StringComparison.Ordinal);
        Assert.Equal(EndpointState.Faulted, master.State);
    }

    [Fact]
    public async Task MismatchedUnitId_DropsTheConnectionToResynchronise()
    {
        await using ModbusTestSlave slave = new ModbusTestSlave { UnitIdSkew = 3 };
        await using ModbusTcpEndpoint master = Master(slave);
        await master.ConnectAsync(Ct);

        EndpointException ex = await Assert.ThrowsAsync<EndpointException>(
            async () => await master.ReadAsync(Channel("holding:0"), Ct));

        Assert.Contains("unit id", ex.Message, StringComparison.Ordinal);
        Assert.Equal(EndpointState.Faulted, master.State);
    }

    [Fact]
    public async Task ReadBeforeConnect_IsRejectedWithAClearMessage()
    {
        await using ModbusTestSlave slave = new ModbusTestSlave();
        await using ModbusTcpEndpoint master = Master(slave);

        EndpointException ex = await Assert.ThrowsAsync<EndpointException>(
            async () => await master.ReadAsync(Channel("holding:0"), Ct));

        Assert.Contains("not connected", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_ToAClosedPort_FaultsWithATransportError()
    {
        ModbusTestSlave slave = new ModbusTestSlave();
        int port = slave.Port;
        await slave.DisposeAsync();

        await using ModbusTcpEndpoint master = new ModbusTcpEndpoint(
            "plc",
            new ModbusTcpSettings("127.0.0.1", port, 1, ShortTimeout, ShortTimeout));

        await Assert.ThrowsAsync<EndpointException>(
            async () => await master.ConnectAsync(Ct));
        Assert.Equal(EndpointState.Faulted, master.State);
    }

    [Fact]
    public async Task Connect_IsIdempotentAndDisconnectIsRepeatable()
    {
        await using ModbusTestSlave slave = new ModbusTestSlave();
        await using ModbusTcpEndpoint master = Master(slave);

        await master.ConnectAsync(Ct);
        await master.ConnectAsync(Ct);
        Assert.Equal(EndpointState.Connected, master.State);

        await master.DisconnectAsync();
        await master.DisconnectAsync();
        Assert.Equal(EndpointState.Disconnected, master.State);

        await master.ConnectAsync(Ct);
        ReadOnlyMemory<byte> raw = await master.ReadAsync(Channel("holding:0"), Ct);
        Assert.Equal(2, raw.Length);
    }

    [Fact]
    public async Task ReconnectAfterAFault_RestoresService()
    {
        await using ModbusTestSlave slave = new ModbusTestSlave { SwallowRequests = true };
        await using ModbusTcpEndpoint master = Master(slave);
        await master.ConnectAsync(Ct);

        await Assert.ThrowsAsync<EndpointException>(
            async () => await master.ReadAsync(Channel("holding:0"), Ct));

        slave.SwallowRequests = false;
        slave.SetHolding(0, 555);
        await master.ConnectAsync(Ct);

        ReadOnlyMemory<byte> raw = await master.ReadAsync(Channel("holding:0"), Ct);

        Assert.Equal(555.0, ValueCodec.Decode(raw.Span, DataType.U16));
        Assert.Equal(EndpointState.Connected, master.State);
    }

    [Fact]
    public async Task UseAfterDispose_IsRejected()
    {
        await using ModbusTestSlave slave = new ModbusTestSlave();
        ModbusTcpEndpoint master = Master(slave);
        await master.ConnectAsync(Ct);
        await master.DisposeAsync();
        await master.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await master.ConnectAsync(Ct));
    }

    [Theory]
    [InlineData("holding:0", DataType.U16, true, null)]
    [InlineData("input:0", DataType.F32, true, null)]
    [InlineData("coil:0", DataType.Bool, true, null)]
    [InlineData("discrete:0", DataType.Bool, true, null)]
    [InlineData("hr:0", DataType.U16, true, null)]
    [InlineData("di:0", DataType.Bool, true, null)]
    [InlineData("offset:0", DataType.U16, false, "not a Modbus space")]
    [InlineData("coil:0", DataType.U16, false, "must be bool")]
    [InlineData("holding:65535", DataType.U32, false, "past the end")]
    public void Supports_ValidatesChannelsAgainstTheAddressSpaceRules(
        string address,
        DataType type,
        bool supported,
        string? fragment)
    {
        ModbusTcpEndpoint master = new ModbusTcpEndpoint("plc", new ModbusTcpSettings("127.0.0.1"));

        bool actual = master.Supports(Channel(address, type), ChannelRole.Source, out string? error);

        Assert.Equal(supported, actual);
        if (fragment is null)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.Contains(fragment, error!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Supports_RejectsEveryChannelAsASinkBecauseWritesAreNotImplemented()
    {
        ModbusTcpEndpoint master = new ModbusTcpEndpoint("plc", new ModbusTcpSettings("127.0.0.1"));

        Assert.False(master.Supports(Channel("holding:0"), ChannelRole.Sink, out string? error));
        Assert.Contains("writes are not implemented", error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_UnsupportedChannel_FailsWithoutTouchingTheWire()
    {
        await using ModbusTestSlave slave = new ModbusTestSlave();
        await using ModbusTcpEndpoint master = Master(slave);
        await master.ConnectAsync(Ct);

        EndpointException ex = await Assert.ThrowsAsync<EndpointException>(
            async () => await master.ReadAsync(Channel("offset:0"), Ct));

        Assert.Contains("not a Modbus space", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, slave.RequestCount);
    }

    [Fact]
    public void Endpoint_ExposesItsIdentityForLogsAndTheMonitor()
    {
        ModbusTcpEndpoint master = new ModbusTcpEndpoint("plc", new ModbusTcpSettings("10.0.0.5", 5020, 7));

        Assert.Equal("plc", master.Id);
        Assert.Equal("modbus_tcp", master.Kind);
        Assert.Equal("10.0.0.5:5020 unit 7", master.Target);
        Assert.Equal(EndpointState.Disconnected, master.State);
    }

    [Fact]
    public void Constructor_RejectsMissingIdentityAndHost()
    {
        Assert.Throws<ArgumentException>(() => new ModbusTcpEndpoint(" ", new ModbusTcpSettings("h")));
        Assert.Throws<ArgumentNullException>(() => new ModbusTcpEndpoint("plc", null!));
        Assert.Throws<ArgumentException>(() => new ModbusTcpEndpoint("plc", new ModbusTcpSettings(" ")));
    }

    [Fact]
    public async Task ReadRegistersAsync_And_ReadBitsAsync_RejectTheWrongFunctionKind()
    {
        await using ModbusTcpEndpoint master = new ModbusTcpEndpoint("plc", new ModbusTcpSettings("127.0.0.1"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await master.ReadRegistersAsync(ModbusFunction.ReadCoils, 0, 1, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await master.ReadBitsAsync(ModbusFunction.ReadHoldingRegisters, 0, 1, Ct));
    }
}

public sealed class ModbusSettingsAndFactoryTests
{
    private static EndpointConfig Endpoint(string body)
    {
        BridgeConfig config = BridgeConfigLoader.Load($"""
            endpoints:
              - id: plc
            {body}
            channels:
              - name: a
                endpoint: plc
                address: holding:0
                type: u16
              - name: b
                endpoint: plc
                address: holding:1
                type: u16
            routes:
              - id: r
                source: a
                sink: b
            """);

        return config.Endpoint("plc");
    }

    [Fact]
    public void Settings_ReadDefaultsWhenOnlyHostIsGiven()
    {
        ModbusTcpSettings settings = ModbusTcpSettings.FromOptions(Endpoint("""
                type: modbus-tcp
                host: 192.168.0.10
            """).Options);

        Assert.Equal("192.168.0.10", settings.Host);
        Assert.Equal(502, settings.Port);
        Assert.Equal(1, settings.UnitId);
        Assert.Equal(ModbusTcpSettings.DefaultRequestTimeout, settings.EffectiveRequestTimeout);
        Assert.Equal(ModbusTcpSettings.DefaultConnectTimeout, settings.EffectiveConnectTimeout);
    }

    [Fact]
    public void Settings_ReadEveryDocumentedKey()
    {
        ModbusTcpSettings settings = ModbusTcpSettings.FromOptions(Endpoint("""
                type: modbus-tcp
                host: plc.local
                port: 5020
                unit_id: 17
                timeout_ms: 250
                connect_timeout_ms: 750
            """).Options);

        Assert.Equal("plc.local", settings.Host);
        Assert.Equal(5020, settings.Port);
        Assert.Equal(17, settings.UnitId);
        Assert.Equal(TimeSpan.FromMilliseconds(250), settings.EffectiveRequestTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(750), settings.EffectiveConnectTimeout);
    }

    [Fact]
    public void Settings_MissingHost_IsRejected()
    {
        Assert.Throws<Pb.Core.Configuration.Yaml.YamlException>(() =>
            ModbusTcpSettings.FromOptions(Endpoint("    type: modbus-tcp").Options));
    }

    [Theory]
    [InlineData("port: 0")]
    [InlineData("port: 70000")]
    [InlineData("unit_id: 248")]
    [InlineData("timeout_ms: 0")]
    [InlineData("connect_timeout_ms: -1")]
    [InlineData("hots: plc.local")]
    public void Settings_OutOfRangeOrUnknownSettings_AreRejected(string line)
    {
        Assert.Throws<Pb.Core.Configuration.Yaml.YamlException>(() =>
            ModbusTcpSettings.FromOptions(Endpoint($"""
                    type: modbus-tcp
                    host: plc.local
                    {line}
                """).Options));
    }

    [Fact]
    public void Factory_CreatesAModbusTcpEndpoint()
    {
        using IDisposable? _ = null;

        IEndpoint endpoint = ModbusEndpointFactory.Create(Endpoint("""
                type: modbus-tcp
                host: plc.local
            """));

        Assert.Equal("plc", endpoint.Id);
        Assert.Equal("modbus_tcp", endpoint.Kind);
    }

    [Theory]
    [InlineData("modbus-rtu")]
    [InlineData("modbus_serial")]
    public void Factory_ModbusOverSerial_IsBlockedByTheSpecGate(string type)
    {
        ConfigException ex = Assert.Throws<ConfigException>(() =>
            ModbusEndpointFactory.Create(Endpoint($"""
                    type: {type}
                    port: /dev/ttyUSB0
                """)));

        Assert.Contains("UNSPECIFIED", ex.Message, StringComparison.Ordinal);
        Assert.Contains("modbus-tcp", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Factory_RejectsNonModbusTypes()
    {
        Assert.Throws<ConfigException>(() => ModbusEndpointFactory.Create(Endpoint("    type: udp")));
        Assert.False(ModbusEndpointFactory.Handles("udp"));
        Assert.True(ModbusEndpointFactory.Handles("modbus_tcp"));
    }
}
