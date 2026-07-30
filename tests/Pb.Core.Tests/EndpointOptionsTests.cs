using Pb.Core.Configuration;
using Pb.Core.Configuration.Yaml;
using Xunit;

namespace Pb.Core.Tests;

public sealed class EndpointOptionsTests
{
    private static EndpointOptions Options(string endpointBody)
    {
        BridgeConfig config = BridgeConfigLoader.Load($"""
            endpoints:
              - id: ep
            {endpointBody}
              - id: sink_ep
                type: udp
            channels:
              - name: a
                endpoint: ep
                address: holding:0
                type: u16
              - name: b
                endpoint: sink_ep
                address: offset:0
                type: f32
            routes:
              - id: r
                source: a
                sink: b
            """);

        return config.Endpoint("ep").Options;
    }

    [Fact]
    public void Options_ExposeEverythingExceptTheHeaderKeys()
    {
        EndpointOptions options = Options("""
                type: udp
                host: 10.0.0.5
                port: 5005
            """);

        Assert.Equal(["host", "port"], options.Keys);
        Assert.False(options.Contains("id"));
        Assert.False(options.Contains("type"));
        Assert.True(options.Contains("host"));
    }

    [Fact]
    public void RequireString_And_RequireInt_ReadDriverSettings()
    {
        EndpointOptions options = Options("""
                type: udp
                host: 10.0.0.5
                port: 5005
            """);

        Assert.Equal("10.0.0.5", options.RequireString("host"));
        Assert.Equal(5005, options.RequireInt("port"));
    }

    [Fact]
    public void RequireString_MissingSetting_ReportsTheEndpointLine()
    {
        EndpointOptions options = Options("    type: udp");

        YamlException ex = Assert.Throws<YamlException>(() => options.RequireString("host"));

        Assert.Contains("host", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.Line >= 1);
    }

    [Fact]
    public void Getters_FallBackWhenTheSettingIsAbsent()
    {
        EndpointOptions options = Options("    type: udp");

        Assert.Equal("localhost", options.GetString("host", "localhost"));
        Assert.Equal(502, options.GetInt("port", 502));
        Assert.Equal(1.5, options.GetDouble("factor", 1.5));
        Assert.True(options.GetBool("keep_alive", true));
        Assert.Equal(1000, options.GetPositiveInt("timeout_ms", 1000));
        Assert.Equal(1, options.GetRangedInt("unit_id", 1, 0, 247));
    }

    [Fact]
    public void GetPositiveInt_RejectsZeroAndNegativeValues()
    {
        EndpointOptions options = Options("""
                type: udp
                timeout_ms: 0
            """);

        YamlException ex = Assert.Throws<YamlException>(() => options.GetPositiveInt("timeout_ms", 1000));

        Assert.Contains("greater than 0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetRangedInt_RejectsValuesOutsideTheRange()
    {
        EndpointOptions options = Options("""
                type: modbus-tcp
                unit_id: 300
            """);

        YamlException ex = Assert.Throws<YamlException>(() => options.GetRangedInt("unit_id", 1, 0, 247));

        Assert.Contains("between 0 and 247", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectUnknownKeys_NamesTheTypoAndTheKnownSettings()
    {
        EndpointOptions options = Options("""
                type: udp
                host: 10.0.0.5
                prot: 5005
            """);

        YamlException ex = Assert.Throws<YamlException>(() => options.RejectUnknownKeys("a udp endpoint", "host", "port"));

        Assert.Contains("prot", ex.Message, StringComparison.Ordinal);
        Assert.Contains("host, port", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectUnknownKeys_AcceptsAKnownSetOfSettings()
    {
        EndpointOptions options = Options("""
                type: udp
                host: 10.0.0.5
                port: 5005
            """);

        options.RejectUnknownKeys("a udp endpoint", "host", "port", "ttl");
    }

    [Fact]
    public void ReadingAHeaderKeyAsASetting_IsAProgrammingError()
    {
        EndpointOptions options = Options("    type: udp");

        Assert.Throws<InvalidOperationException>(() => options.RequireString("type"));
        Assert.Throws<InvalidOperationException>(() => options.RequireInt("id"));
    }

    [Fact]
    public void Empty_HasNoSettings()
    {
        Assert.Empty(EndpointOptions.Empty.Keys);
        Assert.False(EndpointOptions.Empty.Contains("host"));
        Assert.Equal("x", EndpointOptions.Empty.GetString("host", "x"));
    }
}
