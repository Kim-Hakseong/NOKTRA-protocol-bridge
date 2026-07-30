using Pb.Core.Channels;
using Pb.Core.Configuration;
using Xunit;

namespace Pb.Core.Tests;

public sealed class BridgeConfigLoaderTests
{
    /// <summary>
    /// Builds a valid configuration in code so that no fixture file has to be kept in sync,
    /// then lets each test replace one fragment to exercise a single rule.
    /// </summary>
    private static string Config(
        string? bridge = "bridge:\n  name: demo\n",
        string? endpoints = null,
        string? channels = null,
        string? routes = null)
    {
        endpoints ??= """
            endpoints:
              - id: plc
                type: modbus-tcp
                host: 127.0.0.1
                port: 502
              - id: udp_out
                type: udp
                host: 127.0.0.1
                port: 5005
            """;

        channels ??= """
            channels:
              - name: level_raw
                endpoint: plc
                address: holding:0
                type: u16
              - name: level_out
                endpoint: udp_out
                address: offset:0
                type: f32
            """;

        routes ??= """
            routes:
              - id: level
                source: level_raw
                sink: level_out
                trigger:
                  mode: periodic
                  period_ms: 500
                transform:
                  scale: 0.1
                  unit: bar
                  deadband: 0.05
            """;

        return string.Join("\n", new[] { bridge, endpoints, channels, routes }.Where(static s => s is not null));
    }

    private static IReadOnlyList<ConfigDiagnostic> Diagnostics(string text)
    {
        Assert.False(BridgeConfigLoader.TryLoad(text, out BridgeConfig? config, out IReadOnlyList<ConfigDiagnostic> diagnostics));
        Assert.Null(config);
        Assert.NotEmpty(diagnostics);
        return diagnostics;
    }

    private static void AssertReports(string text, string expectedFragment)
    {
        IReadOnlyList<ConfigDiagnostic> diagnostics = Diagnostics(text);

        Assert.Contains(
            diagnostics,
            d => d.Message.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Load_ValidConfiguration_ProducesTheDeclaredTopology()
    {
        BridgeConfig config = BridgeConfigLoader.Load(Config());

        Assert.Equal("demo", config.Name);
        Assert.Equal(2, config.Endpoints.Count);
        Assert.Equal(2, config.Channels.Count);
        Assert.Single(config.Routes);

        EndpointConfig plc = config.Endpoint("plc");
        Assert.Equal("modbus_tcp", plc.Type);
        Assert.Equal("127.0.0.1", plc.Options.RequireString("host"));
        Assert.Equal(502, plc.Options.RequireInt("port"));

        ChannelSpec source = config.Channel("level_raw").Spec;
        Assert.Equal("plc", source.Endpoint);
        Assert.Equal(new ChannelAddress("holding", 0), source.Address);
        Assert.Equal(DataType.U16, source.Type);
        Assert.Equal(ByteOrder.BigEndian, source.ByteOrder);

        RouteConfig route = config.Routes[0];
        Assert.Equal("level", route.Id);
        Assert.True(route.Enabled);
        Assert.Equal(TriggerMode.Periodic, route.Trigger.Mode);
        Assert.Equal(TimeSpan.FromMilliseconds(500), route.Trigger.Period);
        Assert.Equal(0.1, route.Transform.Scale);
        Assert.Equal(0.0, route.Transform.Offset);
        Assert.Equal("bar", route.Transform.Unit);
        Assert.Equal(0.05, route.Transform.Deadband);
    }

    [Fact]
    public void Load_WithoutBridgeSection_UsesTheDefaultName()
    {
        BridgeConfig config = BridgeConfigLoader.Load(Config(bridge: null));

        Assert.Equal(BridgeConfigLoader.DefaultBridgeName, config.Name);
    }

    [Fact]
    public void Load_WithoutTriggerOrTransform_AppliesDefaults()
    {
        BridgeConfig config = BridgeConfigLoader.Load(Config(routes: """
            routes:
              - id: level
                source: level_raw
                sink: level_out
            """));

        RouteConfig route = config.Routes[0];
        Assert.Equal(TriggerMode.Periodic, route.Trigger.Mode);
        Assert.Equal(TimeSpan.FromSeconds(1), route.Trigger.Period);
        Assert.True(route.Transform.IsIdentityScaling);
        Assert.Equal(0.0, route.Transform.Deadband);
        Assert.Null(route.Transform.Unit);
    }

    [Fact]
    public void Load_OnChangeTrigger_NeedsNoPeriod()
    {
        BridgeConfig config = BridgeConfigLoader.Load(Config(routes: """
            routes:
              - id: level
                source: level_raw
                sink: level_out
                trigger:
                  mode: on_change
            """));

        Assert.Equal(TriggerMode.OnChange, config.Routes[0].Trigger.Mode);
        Assert.Equal(TimeSpan.Zero, config.Routes[0].Trigger.Period);
    }

    [Fact]
    public void Load_DisabledRoute_IsKeptButExcludedFromEnabledRoutes()
    {
        BridgeConfig config = BridgeConfigLoader.Load(Config(routes: """
            routes:
              - id: level
                source: level_raw
                sink: level_out
                enabled: false
            """));

        Assert.Single(config.Routes);
        Assert.Empty(config.EnabledRoutes);
        Assert.Empty(config.SourceEndpointIds);
        Assert.Empty(config.SinkEndpointIds);
    }

    [Fact]
    public void Load_ResolvesSourceAndSinkEndpointIds()
    {
        BridgeConfig config = BridgeConfigLoader.Load(Config());

        Assert.Equal(["plc"], config.SourceEndpointIds);
        Assert.Equal(["udp_out"], config.SinkEndpointIds);
    }

    [Theory]
    [InlineData("u16", DataType.U16)]
    [InlineData("uint16", DataType.U16)]
    [InlineData("WORD", DataType.U16)]
    [InlineData("s16", DataType.S16)]
    [InlineData("int16", DataType.S16)]
    [InlineData("u32", DataType.U32)]
    [InlineData("s32", DataType.S32)]
    [InlineData("u64", DataType.U64)]
    [InlineData("s64", DataType.S64)]
    [InlineData("f32", DataType.F32)]
    [InlineData("float", DataType.F32)]
    [InlineData("real", DataType.F32)]
    [InlineData("f64", DataType.F64)]
    [InlineData("bool", DataType.Bool)]
    public void Load_AcceptsTheDocumentedDataTypeSpellings(string token, DataType expected)
    {
        BridgeConfig config = BridgeConfigLoader.Load(Config(channels: $"""
            channels:
              - name: level_raw
                endpoint: plc
                address: holding:0
                type: {token}
              - name: level_out
                endpoint: udp_out
                address: offset:0
                type: f32
            """));

        Assert.Equal(expected, config.Channel("level_raw").Spec.Type);
    }

    [Theory]
    [InlineData("big_endian", ByteOrder.BigEndian)]
    [InlineData("abcd", ByteOrder.BigEndian)]
    [InlineData("little-endian", ByteOrder.LittleEndian)]
    [InlineData("dcba", ByteOrder.LittleEndian)]
    [InlineData("byte_swapped", ByteOrder.ByteSwappedBigEndian)]
    [InlineData("badc", ByteOrder.ByteSwappedBigEndian)]
    [InlineData("word_swapped", ByteOrder.WordSwappedBigEndian)]
    [InlineData("CDAB", ByteOrder.WordSwappedBigEndian)]
    public void Load_AcceptsTheDocumentedByteOrderSpellings(string token, ByteOrder expected)
    {
        BridgeConfig config = BridgeConfigLoader.Load(Config(channels: $"""
            channels:
              - name: level_raw
                endpoint: plc
                address: holding:0
                type: u32
                byte_order: {token}
              - name: level_out
                endpoint: udp_out
                address: offset:0
                type: f32
            """));

        Assert.Equal(expected, config.Channel("level_raw").Spec.ByteOrder);
    }

    [Fact]
    public void Load_UnknownDataType_IsRejected() =>
        AssertReports(
            Config(channels: """
                channels:
                  - name: level_raw
                    endpoint: plc
                    address: holding:0
                    type: f48
                  - name: level_out
                    endpoint: udp_out
                    address: offset:0
                    type: f32
                """),
            "must be one of bool, u16");

    [Fact]
    public void Load_UnknownByteOrder_IsRejected() =>
        AssertReports(
            Config(channels: """
                channels:
                  - name: level_raw
                    endpoint: plc
                    address: holding:0
                    type: u32
                    byte_order: sideways
                  - name: level_out
                    endpoint: udp_out
                    address: offset:0
                    type: f32
                """),
            "byte_order");

    [Fact]
    public void Load_BareRegisterNumberAsAddress_IsRejected() =>
        AssertReports(
            Config(channels: """
                channels:
                  - name: level_raw
                    endpoint: plc
                    address: 40001
                    type: u16
                  - name: level_out
                    endpoint: udp_out
                    address: offset:0
                    type: f32
                """),
            "space:index");

    [Fact]
    public void Load_ChannelReferencingUnknownEndpoint_IsRejected() =>
        AssertReports(
            Config(channels: """
                channels:
                  - name: level_raw
                    endpoint: typo
                    address: holding:0
                    type: u16
                  - name: level_out
                    endpoint: udp_out
                    address: offset:0
                    type: f32
                """),
            "not declared");

    [Fact]
    public void Load_RouteReferencingUnknownChannels_IsRejected()
    {
        IReadOnlyList<ConfigDiagnostic> diagnostics = Diagnostics(Config(routes: """
            routes:
              - id: level
                source: nope
                sink: also_nope
            """));

        Assert.Equal(2, diagnostics.Count);
        Assert.Contains(diagnostics, d => d.Message.Contains("reads channel 'nope'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("writes channel 'also_nope'", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_RouteReadingAndWritingOneChannel_IsRejected() =>
        AssertReports(
            Config(routes: """
                routes:
                  - id: level
                    source: level_raw
                    sink: level_raw
                """),
            "reads and writes the same channel");

    [Fact]
    public void Load_TwoEnabledRoutesWritingOneSink_IsRejected() =>
        AssertReports(
            Config(routes: """
                routes:
                  - id: first
                    source: level_raw
                    sink: level_out
                  - id: second
                    source: level_raw
                    sink: level_out
                """),
            "only one writer");

    [Fact]
    public void Load_SecondWriterDisabled_IsAllowed()
    {
        BridgeConfig config = BridgeConfigLoader.Load(Config(routes: """
            routes:
              - id: first
                source: level_raw
                sink: level_out
              - id: standby
                source: level_raw
                sink: level_out
                enabled: false
            """));

        Assert.Equal(2, config.Routes.Count);
        Assert.Single(config.EnabledRoutes);
    }

    [Fact]
    public void Load_DuplicateEndpointId_IsRejected() =>
        AssertReports(
            Config(endpoints: """
                endpoints:
                  - id: plc
                    type: modbus-tcp
                  - id: plc
                    type: udp
                  - id: udp_out
                    type: udp
                """),
            "duplicate endpoint id");

    [Fact]
    public void Load_DuplicateChannelName_IsRejected() =>
        AssertReports(
            Config(channels: """
                channels:
                  - name: level_raw
                    endpoint: plc
                    address: holding:0
                    type: u16
                  - name: level_raw
                    endpoint: udp_out
                    address: offset:0
                    type: f32
                  - name: level_out
                    endpoint: udp_out
                    address: offset:4
                    type: f32
                """),
            "duplicate channel name");

    [Fact]
    public void Load_DuplicateRouteId_IsRejected() =>
        AssertReports(
            Config(routes: """
                routes:
                  - id: level
                    source: level_raw
                    sink: level_out
                  - id: level
                    source: level_raw
                    sink: level_raw
                """),
            "duplicate route id");

    [Theory]
    [InlineData("endpoints")]
    [InlineData("channels")]
    [InlineData("routes")]
    public void Load_MissingRequiredSection_IsRejected(string section)
    {
        string text = section switch
        {
            "endpoints" => Config(endpoints: string.Empty),
            "channels" => Config(channels: string.Empty),
            _ => Config(routes: string.Empty),
        };

        AssertReports(text, $"the '{section}' section is required");
    }

    [Fact]
    public void Load_UnknownTopLevelSection_IsRejected() =>
        AssertReports(Config() + "\nextras:\n  a: 1\n", "unknown key 'extras'");

    [Fact]
    public void Load_UnknownRouteKey_IsRejected() =>
        AssertReports(
            Config(routes: """
                routes:
                  - id: level
                    source: level_raw
                    sink: level_out
                    scale: 0.1
                """),
            "unknown key 'scale'");

    [Fact]
    public void Load_UnknownChannelKey_IsRejected() =>
        AssertReports(
            Config(channels: """
                channels:
                  - name: level_raw
                    endpoint: plc
                    address: holding:0
                    type: u16
                    endian: big
                  - name: level_out
                    endpoint: udp_out
                    address: offset:0
                    type: f32
                """),
            "unknown key 'endian'");

    [Fact]
    public void Load_UnknownTriggerMode_IsRejected() =>
        AssertReports(
            Config(routes: """
                routes:
                  - id: level
                    source: level_raw
                    sink: level_out
                    trigger:
                      mode: whenever
                """),
            "periodic or on_change");

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Load_NonPositivePeriod_IsRejected(string period) =>
        AssertReports(
            Config(routes: $"""
                routes:
                  - id: level
                    source: level_raw
                    sink: level_out
                    trigger:
                      mode: periodic
                      period_ms: {period}
                """),
            "greater than 0");

    [Fact]
    public void Load_PeriodOnAnOnChangeTrigger_IsRejected() =>
        AssertReports(
            Config(routes: """
                routes:
                  - id: level
                    source: level_raw
                    sink: level_out
                    trigger:
                      mode: on_change
                      period_ms: 100
                """),
            "does not apply to an on_change trigger");

    [Fact]
    public void Load_NegativeDeadband_IsRejected() =>
        AssertReports(
            Config(routes: """
                routes:
                  - id: level
                    source: level_raw
                    sink: level_out
                    transform:
                      deadband: -0.5
                """),
            "must not be negative");

    [Fact]
    public void Load_IdentifierWithIllegalCharacters_IsRejected() =>
        AssertReports(
            Config(endpoints: """
                endpoints:
                  - id: "bad id"
                    type: udp
                  - id: udp_out
                    type: udp
                """),
            "may only contain");

    [Fact]
    public void Load_SectionOfTheWrongShape_IsRejected() =>
        AssertReports(Config(endpoints: "endpoints:\n  id: plc\n"), "must be a block sequence");

    [Fact]
    public void Load_AccumulatesEveryProblemInOnePass()
    {
        IReadOnlyList<ConfigDiagnostic> diagnostics = Diagnostics("""
            endpoints:
              - id: plc
                type: udp
            channels:
              - name: a
                endpoint: missing_endpoint
                address: 40001
                type: u16
            routes:
              - id: r
                source: a
                sink: nowhere
            """);

        Assert.True(diagnostics.Count >= 3, $"expected at least 3 diagnostics, got {diagnostics.Count}");
        Assert.All(diagnostics, d => Assert.False(string.IsNullOrWhiteSpace(d.Message)));
    }

    [Fact]
    public void Load_UnparseableText_ReportsOneStructuralProblem()
    {
        IReadOnlyList<ConfigDiagnostic> diagnostics = Diagnostics("endpoints: [a, b]\n");

        Assert.Single(diagnostics);
        Assert.Equal(1, diagnostics[0].Line);
    }

    [Fact]
    public void Load_InvalidConfiguration_ThrowsWithEveryDiagnosticInTheMessage()
    {
        ConfigException ex = Assert.Throws<ConfigException>(() => BridgeConfigLoader.Load(Config(routes: """
            routes:
              - id: level
                source: nope
                sink: also_nope
            """)));

        Assert.Equal(2, ex.Diagnostics.Count);
        Assert.Contains("nope", ex.Message, StringComparison.Ordinal);
        Assert.Contains("also_nope", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostics_RenderWithTheirLineNumber()
    {
        Assert.Equal("line 7: bad", new ConfigDiagnostic("bad", 7).ToString());
        Assert.Equal("bad", new ConfigDiagnostic("bad", 0).ToString());
    }

    [Fact]
    public void LoadFile_ReadsFromDisk()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pb-config-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, Config());

        try
        {
            Assert.Equal("demo", BridgeConfigLoader.LoadFile(path).Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFile_RejectsAnEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => BridgeConfigLoader.LoadFile("  "));
    }

    [Fact]
    public void Endpoint_And_Channel_LookupsRejectUnknownNames()
    {
        BridgeConfig config = BridgeConfigLoader.Load(Config());

        Assert.Throws<KeyNotFoundException>(() => config.Endpoint("nope"));
        Assert.Throws<KeyNotFoundException>(() => config.Channel("nope"));
    }

    [Fact]
    public void TryLoad_NullText_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BridgeConfigLoader.TryLoad(null!, out _, out _));
    }
}
