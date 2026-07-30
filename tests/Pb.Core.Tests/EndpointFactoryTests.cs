using Pb.Core.Configuration;
using Pb.Core.Endpoints;
using Pb.Core.Endpoints.Csv;
using Pb.Core.Endpoints.Serial;
using Pb.Core.Endpoints.Udp;
using Pb.Core.Modbus;
using Xunit;

namespace Pb.Core.Tests;

public sealed class EndpointFactoryTests
{
    /// <summary>
    /// A configuration that exercises every driver at once: a Modbus source, a UDP sink, a
    /// serial line and a CSV log.
    /// </summary>
    private const string FullConfig = """
        bridge:
          name: all-drivers

        endpoints:
          - id: plc
            type: modbus-tcp
            host: 127.0.0.1
            port: 5020
          - id: telemetry
            type: udp
            host: 127.0.0.1
            port: 5005
            frame_bytes: 8
          - id: line
            type: serial
            port: /dev/ttyUSB0
            frame_bytes: 4
          - id: archive
            type: csv
            path: out/log.csv

        channels:
          - name: level_raw
            endpoint: plc
            address: holding:0
            type: u16
          - name: level_udp
            endpoint: telemetry
            address: offset:0
            type: f32
          - name: level_serial
            endpoint: line
            address: offset:0
            type: u16
          - name: level_log
            endpoint: archive
            address: csv:0
            type: f32

        routes:
          - id: to_udp
            source: level_raw
            sink: level_udp
            transform:
              scale: 0.1
          - id: to_serial
            source: level_raw
            sink: level_serial
          - id: to_csv
            source: level_raw
            sink: level_log
        """;

    private static async Task DisposeAllAsync(Dictionary<string, IEndpoint> endpoints)
    {
        foreach (IEndpoint endpoint in endpoints.Values)
        {
            await endpoint.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateAll_BuildsEveryDeclaredDriver()
    {
        BridgeConfig config = BridgeConfigLoader.Load(FullConfig);

        Dictionary<string, IEndpoint> endpoints = EndpointFactory.CreateAll(config);

        try
        {
            Assert.Equal(4, endpoints.Count);
            Assert.IsType<ModbusTcpEndpoint>(endpoints["plc"]);
            Assert.IsType<UdpEndpoint>(endpoints["telemetry"]);
            Assert.IsType<SerialEndpoint>(endpoints["line"]);
            Assert.IsType<CsvFileSink>(endpoints["archive"]);
            Assert.All(endpoints.Values, e => Assert.Equal(EndpointState.Disconnected, e.State));
        }
        finally
        {
            await DisposeAllAsync(endpoints);
        }
    }

    [Fact]
    public async Task CreateAll_ValidTopology_ReportsNoChannelProblems()
    {
        BridgeConfig config = BridgeConfigLoader.Load(FullConfig);
        Dictionary<string, IEndpoint> endpoints = EndpointFactory.CreateAll(config);

        try
        {
            Assert.Empty(EndpointFactory.ValidateChannels(config, endpoints));
        }
        finally
        {
            await DisposeAllAsync(endpoints);
        }
    }

    [Fact]
    public void CreateAll_UnknownType_ListsTheAvailableDrivers()
    {
        ConfigException ex = Assert.Throws<ConfigException>(() => EndpointFactory.CreateAll(BridgeConfigLoader.Load("""
            endpoints:
              - id: x
                type: opc-ua
            channels:
              - name: a
                endpoint: x
                address: offset:0
                type: u16
              - name: b
                endpoint: x
                address: offset:2
                type: u16
            routes:
              - id: r
                source: a
                sink: b
            """)));

        Assert.Contains("unknown type 'opc_ua'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("modbus_tcp", ex.Message, StringComparison.Ordinal);
        Assert.Contains("udp", ex.Message, StringComparison.Ordinal);
        Assert.Contains("serial", ex.Message, StringComparison.Ordinal);
        Assert.Contains("csv", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateAll_BadDriverSettings_AreReportedAgainstTheirEndpoint()
    {
        ConfigException ex = Assert.Throws<ConfigException>(() => EndpointFactory.CreateAll(BridgeConfigLoader.Load("""
            endpoints:
              - id: telemetry
                type: udp
                host: 127.0.0.1
            channels:
              - name: a
                endpoint: telemetry
                address: offset:0
                type: u16
              - name: b
                endpoint: telemetry
                address: offset:2
                type: u16
            routes:
              - id: r
                source: a
                sink: b
            """)));

        Assert.Contains("endpoint 'telemetry'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'port' is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateAll_AccumulatesProblemsFromSeveralEndpoints()
    {
        ConfigException ex = Assert.Throws<ConfigException>(() => EndpointFactory.CreateAll(BridgeConfigLoader.Load("""
            endpoints:
              - id: a
                type: udp
              - id: b
                type: serial
              - id: c
                type: nope
            channels:
              - name: x
                endpoint: a
                address: offset:0
                type: u16
              - name: y
                endpoint: b
                address: offset:0
                type: u16
            routes:
              - id: r
                source: x
                sink: y
            """)));

        Assert.Equal(3, ex.Diagnostics.Count);
    }

    [Fact]
    public void CreateAll_ChannelUsedInTheWrongDirection_IsReported()
    {
        ConfigException ex = Assert.Throws<ConfigException>(() => EndpointFactory.CreateAll(BridgeConfigLoader.Load("""
            endpoints:
              - id: plc
                type: modbus-tcp
                host: 127.0.0.1
              - id: listener
                type: udp
                listen_port: 5099
            channels:
              - name: from_udp
                endpoint: listener
                address: offset:0
                type: u16
              - name: to_plc
                endpoint: plc
                address: holding:0
                type: u16
            routes:
              - id: r
                source: from_udp
                sink: to_plc
            """)));

        Assert.Contains("route 'r'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Modbus writes are not implemented", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateAll_ChannelAddressedForTheWrongDriver_IsReported()
    {
        ConfigException ex = Assert.Throws<ConfigException>(() => EndpointFactory.CreateAll(BridgeConfigLoader.Load("""
            endpoints:
              - id: plc
                type: modbus-tcp
                host: 127.0.0.1
              - id: telemetry
                type: udp
                host: 127.0.0.1
                port: 5005
            channels:
              - name: level_raw
                endpoint: plc
                address: holding:0
                type: u16
              - name: level_udp
                endpoint: telemetry
                address: holding:0
                type: f32
            routes:
              - id: r
                source: level_raw
                sink: level_udp
            """)));

        Assert.Contains("not a frame offset", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateAll_CsvUsedAsASource_IsReported()
    {
        ConfigException ex = Assert.Throws<ConfigException>(() => EndpointFactory.CreateAll(BridgeConfigLoader.Load("""
            endpoints:
              - id: archive
                type: csv
                path: out/log.csv
              - id: telemetry
                type: udp
                host: 127.0.0.1
                port: 5005
            channels:
              - name: from_csv
                endpoint: archive
                address: csv:0
                type: f32
              - name: level_udp
                endpoint: telemetry
                address: offset:0
                type: f32
            routes:
              - id: r
                source: from_csv
                sink: level_udp
            """)));

        Assert.Contains("write-only", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAll_DisabledRoutesAreNotChannelChecked()
    {
        BridgeConfig config = BridgeConfigLoader.Load("""
            endpoints:
              - id: plc
                type: modbus-tcp
                host: 127.0.0.1
              - id: telemetry
                type: udp
                host: 127.0.0.1
                port: 5005
            channels:
              - name: level_raw
                endpoint: plc
                address: holding:0
                type: u16
              - name: level_udp
                endpoint: telemetry
                address: offset:0
                type: f32
            routes:
              - id: live
                source: level_raw
                sink: level_udp
              - id: parked
                source: level_udp
                sink: level_raw
                enabled: false
            """);

        Dictionary<string, IEndpoint> endpoints = EndpointFactory.CreateAll(config);

        try
        {
            Assert.Equal(2, endpoints.Count);
        }
        finally
        {
            await DisposeAllAsync(endpoints);
        }
    }

    [Fact]
    public void CreateAll_ModbusOverSerial_IsStillBlockedByTheSpecGate()
    {
        ConfigException ex = Assert.Throws<ConfigException>(() => EndpointFactory.CreateAll(BridgeConfigLoader.Load("""
            endpoints:
              - id: plc
                type: modbus-rtu
                port: /dev/ttyUSB0
              - id: telemetry
                type: udp
                host: 127.0.0.1
                port: 5005
            channels:
              - name: a
                endpoint: plc
                address: holding:0
                type: u16
              - name: b
                endpoint: telemetry
                address: offset:0
                type: f32
            routes:
              - id: r
                source: a
                sink: b
            """)));

        Assert.Contains("UNSPECIFIED", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_And_CreateAll_RejectNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => EndpointFactory.Create(null!));
        Assert.Throws<ArgumentNullException>(() => EndpointFactory.CreateAll(null!));
        Assert.Throws<ArgumentNullException>(() =>
            EndpointFactory.ValidateChannels(BridgeConfigLoader.Load(FullConfig), null!));
    }
}
