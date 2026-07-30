using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Pb.Core.Configuration;
using Pb.Core.Endpoints;
using Pb.Core.Mqtt;
using Pb.Core.Routing;
using Pb.Core.Tests.Harness;
using Pb.Core.Time;
using Xunit;

namespace Pb.Core.Tests;

/// <summary>
/// End-to-end tests: a real Modbus TCP slave, the real bridge, and real UDP / CSV / MQTT
/// receivers, connected over loopback sockets and the filesystem. Nothing between the slave's
/// register and the receiver's bytes is substituted, so these tests are what prove the milestones
/// fit together. Only the clock is injected, which is what keeps poll periods deterministic.
/// </summary>
public sealed class EndToEndBridgeTests : IDisposable
{
    private readonly CancellationTokenSource _testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"pb-e2e-{Guid.NewGuid():N}");

    private CancellationToken Ct => _testTimeout.Token;

    /// <summary>A long supervision interval, so advancing time reaches route periods first.</summary>
    private static readonly RouterOptions Options = new RouterOptions(
        SupervisionInterval: TimeSpan.FromSeconds(60),
        InitialReconnectBackoff: TimeSpan.FromMilliseconds(100),
        MaxReconnectBackoff: TimeSpan.FromSeconds(1));

    [Fact]
    public async Task GoldenVector_Holding100AtScaleTenth_ArrivesAtTheUdpReceiverAsFloatTen()
    {
        await using ModbusTestSlave slave = new ModbusTestSlave();
        slave.SetHolding(0, 100);
        using UdpProbe receiver = new UdpProbe();

        BridgeConfig config = BridgeConfigLoader.Load($"""
            bridge:
              name: e2e

            endpoints:
              - id: plc
                type: modbus-tcp
                host: 127.0.0.1
                port: {slave.Port}
                unit_id: {slave.UnitId}
                timeout_ms: 5000
              - id: telemetry
                type: udp
                host: 127.0.0.1
                port: {receiver.Port}

            channels:
              - name: level_raw
                endpoint: plc
                address: holding:0
                type: u16
              - name: level_out
                endpoint: telemetry
                address: offset:0
                type: f32

            routes:
              - id: level
                source: level_raw
                sink: level_out
                trigger:
                  mode: periodic
                  period_ms: 100
                transform:
                  scale: 0.1
                  unit: bar
            """);

        Dictionary<string, IEndpoint> endpoints = EndpointFactory.CreateAll(config);
        ManualTimeSource time = new ManualTimeSource();

        try
        {
            await using BridgeRouter router = new BridgeRouter(config, endpoints, time, Options);
            Task run = router.RunAsync(Ct);

            Task<byte[]> datagram = receiver.ReceiveAsync(Ct);
            await WaitForAsync(() => AllConnected(router), Ct);
            await time.WaitForPendingDelaysAsync(3, Ct);
            time.AdvanceToNextDelay();

            byte[] payload = await datagram;

            Assert.Equal(4, payload.Length);
            Assert.Equal(10.0f, BinaryPrimitives.ReadSingleBigEndian(payload));

            BridgeStatus status = router.Snapshot();
            Assert.True(status.IsHealthy);
            Assert.Equal(1, status.TotalForwarded);
            Assert.Equal(0, status.TotalDropped);
            Assert.Equal(10.0, status.Routes.Single().LastValue!.Value, 12);
            Assert.Equal("bar", status.Routes.Single().Unit);
            Assert.Empty(slave.ServerFaults);

            router.Stop();
            await run;
        }
        finally
        {
            await DisposeAllAsync(endpoints);
        }
    }

    [Fact]
    public async Task Deadband_OnlyChangedValuesReachTheReceiver()
    {
        await using ModbusTestSlave slave = new ModbusTestSlave();
        using UdpProbe receiver = new UdpProbe();

        BridgeConfig config = BridgeConfigLoader.Load($"""
            endpoints:
              - id: plc
                type: modbus-tcp
                host: 127.0.0.1
                port: {slave.Port}
                timeout_ms: 5000
              - id: telemetry
                type: udp
                host: 127.0.0.1
                port: {receiver.Port}

            channels:
              - name: level_raw
                endpoint: plc
                address: holding:0
                type: u16
              - name: level_out
                endpoint: telemetry
                address: offset:0
                type: f32

            routes:
              - id: level
                source: level_raw
                sink: level_out
                trigger:
                  mode: periodic
                  period_ms: 100
                transform:
                  scale: 0.1
                  deadband: 0.5
            """);

        Dictionary<string, IEndpoint> endpoints = EndpointFactory.CreateAll(config);
        ManualTimeSource time = new ManualTimeSource();

        try
        {
            await using BridgeRouter router = new BridgeRouter(config, endpoints, time, Options);
            Task run = router.RunAsync(Ct);
            await WaitForAsync(() => AllConnected(router), Ct);

            List<float> received = [];
            Task<byte[]> pending = receiver.ReceiveAsync(Ct);

            // The DESIGN deadband sequence, driven through the real slave's register.
            foreach (ushort register in new ushort[] { 10, 13, 16, 14 })
            {
                slave.SetHolding(0, register);
                long readsBefore = router.Snapshot().Routes.Single().SamplesRead;

                await time.WaitForPendingDelaysAsync(3, Ct);
                time.AdvanceToNextDelay();
                await WaitForAsync(() => router.Snapshot().Routes.Single().SamplesRead > readsBefore, Ct);

                if (pending.IsCompleted)
                {
                    received.Add(BinaryPrimitives.ReadSingleBigEndian(await pending));
                    pending = receiver.ReceiveAsync(Ct);
                }
            }

            await WaitForAsync(() => router.Snapshot().Routes.Single().SamplesForwarded == 2, Ct);

            if (pending.IsCompleted)
            {
                received.Add(BinaryPrimitives.ReadSingleBigEndian(await pending));
            }

            RouteStatus status = router.Snapshot().Routes.Single();
            Assert.Equal(4, status.SamplesRead);
            Assert.Equal(2, status.SamplesForwarded);
            Assert.Equal(2, status.SamplesSuppressed);
            Assert.Equal([1.0f, 1.6f], received);

            router.Stop();
            await run;
        }
        finally
        {
            await DisposeAllAsync(endpoints);
        }
    }

    [Fact]
    public async Task OneSourceFansOutToUdpCsvAndMqtt()
    {
        await using ModbusTestSlave slave = new ModbusTestSlave();
        slave.SetHolding(0, 100);
        using UdpProbe receiver = new UdpProbe();
        await using MqttTestBroker broker = new MqttTestBroker();
        Directory.CreateDirectory(_directory);
        string csvPath = Path.Combine(_directory, "log.csv");

        BridgeConfig config = BridgeConfigLoader.Load($"""
            endpoints:
              - id: plc
                type: modbus-tcp
                host: 127.0.0.1
                port: {slave.Port}
                timeout_ms: 5000
              - id: telemetry
                type: udp
                host: 127.0.0.1
                port: {receiver.Port}
              - id: archive
                type: csv
                path: {csvPath}
              - id: broker
                type: mqtt
                host: 127.0.0.1
                port: {broker.Port}
                topic_prefix: plant

            channels:
              - name: level_raw
                endpoint: plc
                address: holding:0
                type: u16
              - name: level_udp
                endpoint: telemetry
                address: offset:0
                type: f32
              - name: level_csv
                endpoint: archive
                address: csv:0
                type: f32
              - name: tank1.level
                endpoint: broker
                address: topic:0
                type: f32

            routes:
              - id: to_udp
                source: level_raw
                sink: level_udp
                trigger:
                  mode: periodic
                  period_ms: 100
                transform:
                  scale: 0.1
                  unit: bar
              - id: to_csv
                source: level_raw
                sink: level_csv
                trigger:
                  mode: periodic
                  period_ms: 100
                transform:
                  scale: 0.1
                  unit: bar
              - id: to_mqtt
                source: level_raw
                sink: tank1.level
                trigger:
                  mode: periodic
                  period_ms: 100
                transform:
                  scale: 0.1
                  unit: bar
            """);

        Dictionary<string, IEndpoint> endpoints = EndpointFactory.CreateAll(config);
        ManualTimeSource time = new ManualTimeSource();

        try
        {
            await using BridgeRouter router = new BridgeRouter(config, endpoints, time, Options);
            Task run = router.RunAsync(Ct);

            Task<byte[]> datagram = receiver.ReceiveAsync(Ct);
            await WaitForAsync(() => AllConnected(router), Ct);

            // Four supervisors and three periodic route loops.
            await time.WaitForPendingDelaysAsync(7, Ct);
            time.AdvanceToNextDelay();

            await WaitForAsync(() => router.Snapshot().TotalForwarded == 3, Ct);
            await broker.WaitForAsync(MqttPacketType.Publish, 1, Ct);

            Assert.Equal(10.0f, BinaryPrimitives.ReadSingleBigEndian(await datagram));

            ReceivedPacket published = broker.Packets.Single(static p => p.Type == MqttPacketType.Publish);
            Assert.Equal("plant/tank1/level", MqttPacket.ReadString(published.Body, out int used));
            Assert.Equal("10", Encoding.UTF8.GetString(published.Body.AsSpan(used)));

            router.Stop();
            await run;

            // The CSV writer flushes every row, so the file is readable while the bridge runs; it is
            // read after the stop so the assertion cannot race the first write.
            string[] lines = await File.ReadAllLinesAsync(csvPath, Ct);
            Assert.Equal("timestamp,channel,value,unit,quality", lines[0]);
            string[] fields = lines[1].Split(',');
            Assert.Equal("level_csv", fields[1]);
            Assert.Equal(10.0, double.Parse(fields[2], CultureInfo.InvariantCulture), 12);
            Assert.Equal("bar", fields[3]);
            Assert.Equal("Good", fields[4]);
        }
        finally
        {
            await DisposeAllAsync(endpoints);
        }
    }

    [Fact]
    public async Task WhenTheSlaveDies_ItsRouteFaultsAndTheOtherRouteKeepsDelivering()
    {
        ModbusTestSlave slave = new ModbusTestSlave();
        slave.SetHolding(0, 100);
        using UdpProbe pushSource = new UdpProbe();
        using UdpProbe modbusReceiver = new UdpProbe();
        using UdpProbe pushReceiver = new UdpProbe();
        int listenPort = UdpProbe.FreePort();

        BridgeConfig config = BridgeConfigLoader.Load($"""
            endpoints:
              - id: plc
                type: modbus-tcp
                host: 127.0.0.1
                port: {slave.Port}
                timeout_ms: 500
                connect_timeout_ms: 500
              - id: listener
                type: udp
                listen_port: {listenPort}
                bind_address: 127.0.0.1
              - id: out_modbus
                type: udp
                host: 127.0.0.1
                port: {modbusReceiver.Port}
              - id: out_push
                type: udp
                host: 127.0.0.1
                port: {pushReceiver.Port}

            channels:
              - name: level_raw
                endpoint: plc
                address: holding:0
                type: u16
              - name: pushed_raw
                endpoint: listener
                address: offset:0
                type: u16
              - name: level_out
                endpoint: out_modbus
                address: offset:0
                type: f32
              - name: pushed_out
                endpoint: out_push
                address: offset:0
                type: f32

            routes:
              - id: from_modbus
                source: level_raw
                sink: level_out
                trigger:
                  mode: periodic
                  period_ms: 100
                transform:
                  scale: 0.1
              - id: from_push
                source: pushed_raw
                sink: pushed_out
                trigger:
                  mode: on_change
                transform:
                  scale: 0.5
            """);

        Dictionary<string, IEndpoint> endpoints = EndpointFactory.CreateAll(config);
        ManualTimeSource time = new ManualTimeSource();

        try
        {
            await using BridgeRouter router = new BridgeRouter(config, endpoints, time, Options);
            Task run = router.RunAsync(Ct);
            await WaitForAsync(() => AllConnected(router), Ct);

            // Both routes deliver while everything is healthy.
            Task<byte[]> firstModbus = modbusReceiver.ReceiveAsync(Ct);
            await time.WaitForPendingDelaysAsync(5, Ct);
            time.AdvanceToNextDelay();
            Assert.Equal(10.0f, BinaryPrimitives.ReadSingleBigEndian(await firstModbus));

            Task<byte[]> firstPush = pushReceiver.ReceiveAsync(Ct);
            await pushSource.SendAsync(listenPort, [0x00, 0x08], Ct);
            Assert.Equal(4.0f, BinaryPrimitives.ReadSingleBigEndian(await firstPush));

            // The slave goes away for good.
            await slave.DisposeAsync();
            await AdvanceUntilAsync(time, () => Route(router, "from_modbus").Health == RouteHealth.SourceFault, Ct);

            // The push route is untouched and still delivers.
            Task<byte[]> secondPush = pushReceiver.ReceiveAsync(Ct);
            await pushSource.SendAsync(listenPort, [0x00, 0x14], Ct);
            Assert.Equal(10.0f, BinaryPrimitives.ReadSingleBigEndian(await secondPush));

            BridgeStatus status = router.Snapshot();
            Assert.Equal(RouteHealth.SourceFault, Route(router, "from_modbus").Health);
            Assert.Equal(RouteHealth.Ok, Route(router, "from_push").Health);
            Assert.Equal(2, Route(router, "from_push").SamplesForwarded);
            Assert.False(status.IsHealthy);
            Assert.NotNull(status.Endpoints.Single(static e => e.Id == "plc").LastError);
            Assert.True(router.IsRunning);

            router.Stop();
            await run;
        }
        finally
        {
            await DisposeAllAsync(endpoints);
            await slave.DisposeAsync();
        }
    }

    [Fact]
    public async Task ARestartedSlaveIsPickedUpAgainWithoutRestartingTheBridge()
    {
        int slavePort = FreeTcpPort();
        using UdpProbe receiver = new UdpProbe();

        BridgeConfig config = BridgeConfigLoader.Load($"""
            endpoints:
              - id: plc
                type: modbus-tcp
                host: 127.0.0.1
                port: {slavePort}
                timeout_ms: 500
                connect_timeout_ms: 500
              - id: telemetry
                type: udp
                host: 127.0.0.1
                port: {receiver.Port}

            channels:
              - name: level_raw
                endpoint: plc
                address: holding:0
                type: u16
              - name: level_out
                endpoint: telemetry
                address: offset:0
                type: f32

            routes:
              - id: level
                source: level_raw
                sink: level_out
                trigger:
                  mode: periodic
                  period_ms: 100
                transform:
                  scale: 0.1
                  deadband: 1000
            """);

        Dictionary<string, IEndpoint> endpoints = EndpointFactory.CreateAll(config);
        ManualTimeSource time = new ManualTimeSource();

        try
        {
            await using BridgeRouter router = new BridgeRouter(config, endpoints, time, Options);
            Task run = router.RunAsync(Ct);

            // The bridge starts before the slave exists, so the first attempts must fail and retry.
            await AdvanceUntilAsync(time, () => Endpoint(router, "plc").ConnectAttempts >= 2, Ct);
            Assert.NotEqual(EndpointState.Connected, Endpoint(router, "plc").State);

            await using ModbusTestSlave slave = new ModbusTestSlave(port: slavePort);
            slave.SetHolding(0, 100);

            Task<byte[]> datagram = receiver.ReceiveAsync(Ct);
            await AdvanceUntilAsync(time, () => Route(router, "level").SamplesForwarded >= 1, Ct);

            Assert.Equal(10.0f, BinaryPrimitives.ReadSingleBigEndian(await datagram));
            Assert.Equal(EndpointState.Connected, Endpoint(router, "plc").State);
            Assert.True(Endpoint(router, "plc").ConnectAttempts >= 3);

            router.Stop();
            await run;
        }
        finally
        {
            await DisposeAllAsync(endpoints);
        }
    }

    [Fact]
    public async Task AModbusExceptionResponse_FaultsTheRouteButLeavesTheEndpointConnected()
    {
        await using ModbusTestSlave slave = new ModbusTestSlave { AddressLimit = 1 };
        using UdpProbe receiver = new UdpProbe();

        BridgeConfig config = BridgeConfigLoader.Load($"""
            endpoints:
              - id: plc
                type: modbus-tcp
                host: 127.0.0.1
                port: {slave.Port}
                timeout_ms: 5000
              - id: telemetry
                type: udp
                host: 127.0.0.1
                port: {receiver.Port}

            channels:
              - name: out_of_range
                endpoint: plc
                address: holding:500
                type: u16
              - name: level_out
                endpoint: telemetry
                address: offset:0
                type: f32

            routes:
              - id: level
                source: out_of_range
                sink: level_out
                trigger:
                  mode: periodic
                  period_ms: 100
            """);

        Dictionary<string, IEndpoint> endpoints = EndpointFactory.CreateAll(config);
        ManualTimeSource time = new ManualTimeSource();

        try
        {
            await using BridgeRouter router = new BridgeRouter(config, endpoints, time, Options);
            Task run = router.RunAsync(Ct);
            await WaitForAsync(() => AllConnected(router), Ct);

            await time.WaitForPendingDelaysAsync(3, Ct);
            time.AdvanceToNextDelay();
            await WaitForAsync(() => Route(router, "level").Health == RouteHealth.SourceFault, Ct);

            Assert.Contains("illegal data address", Route(router, "level").LastError!, StringComparison.Ordinal);

            // The link itself is healthy, so the endpoint must stay connected rather than reconnect.
            Assert.Equal(EndpointState.Connected, Endpoint(router, "plc").State);
            Assert.Equal(1, Endpoint(router, "plc").ConnectAttempts);
            Assert.Equal(0, Endpoint(router, "plc").Reconnects);

            router.Stop();
            await run;
        }
        finally
        {
            await DisposeAllAsync(endpoints);
        }
    }

    public void Dispose()
    {
        _testTimeout.Dispose();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static RouteStatus Route(BridgeRouter router, string id) =>
        router.Snapshot().Routes.Single(r => r.Id == id);

    private static EndpointStatus Endpoint(BridgeRouter router, string id) =>
        router.Snapshot().Endpoints.Single(e => e.Id == id);

    private static bool AllConnected(BridgeRouter router) =>
        router.Snapshot().Endpoints.All(static e => e.State == EndpointState.Connected);

    /// <summary>Finds a free loopback TCP port by binding and releasing it.</summary>
    private static int FreeTcpPort()
    {
        System.Net.Sockets.TcpListener probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    /// <summary>Advances the manual clock, one pending delay at a time, until a condition holds.</summary>
    private static async Task AdvanceUntilAsync(ManualTimeSource time, Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await time.WaitForPendingDelaysAsync(1, cancellationToken);
            time.AdvanceToNextDelay();
            await Task.Yield();
        }
    }

    private static async Task DisposeAllAsync(Dictionary<string, IEndpoint> endpoints)
    {
        foreach (IEndpoint endpoint in endpoints.Values)
        {
            await endpoint.DisposeAsync();
        }
    }
}
