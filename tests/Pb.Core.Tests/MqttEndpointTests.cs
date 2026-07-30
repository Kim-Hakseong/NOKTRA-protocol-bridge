using System.Text;
using Pb.Core.Channels;
using Pb.Core.Configuration;
using Pb.Core.Configuration.Yaml;
using Pb.Core.Endpoints;
using Pb.Core.Mqtt;
using Pb.Core.Tests.Harness;
using Pb.Core.Time;
using Xunit;

namespace Pb.Core.Tests;

/// <summary>
/// Loopback tests: the publisher talks to an independently written test broker over a real
/// socket, so CONNECT, CONNACK, PUBLISH, PINGREQ and DISCONNECT are genuinely exchanged.
/// </summary>
public sealed class MqttEndpointTests
{
    private readonly CancellationTokenSource _testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

    private CancellationToken Ct => _testTimeout.Token;

    private static ChannelSpec Channel(string name = "a", string address = "topic:0") =>
        new ChannelSpec(name, "broker", ChannelAddress.Parse(address), DataType.F32);

    private static Sample Value(double value, string? unit = null, SampleQuality quality = SampleQuality.Good) =>
        new Sample(value, new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero), quality, unit);

    private static MqttEndpoint Publisher(
        MqttTestBroker broker,
        ITimeSource? time = null,
        string? prefix = "t",
        MqttPayloadFormat payload = MqttPayloadFormat.Value,
        TimeSpan? keepAlive = null) =>
        new MqttEndpoint(
            "broker",
            new MqttSettings(
                "127.0.0.1",
                broker.Port,
                "pb-test",
                keepAlive ?? TimeSpan.FromSeconds(60),
                TopicPrefix: prefix,
                Payload: payload,
                ConnectTimeout: TimeSpan.FromSeconds(5)),
            time);

    [Fact]
    public async Task Publish_ProducesTheGoldenVectorPacketOnTheWire()
    {
        await using MqttTestBroker broker = new MqttTestBroker();
        await using MqttEndpoint publisher = Publisher(broker);
        await publisher.ConnectAsync(Ct);

        await publisher.WriteAsync(Channel("a"), Value(21.0), Ct);
        await broker.WaitForAsync(MqttPacketType.Publish, 1, Ct);

        ReceivedPacket published = broker.Packets.Single(static p => p.Type == MqttPacketType.Publish);

        Assert.Equal([0x30, 0x07, 0x00, 0x03, 0x74, 0x2F, 0x61, 0x32, 0x31], published.Raw);
        Assert.Equal(1, publisher.MessagesPublished);
        Assert.Empty(broker.Faults);
    }

    [Fact]
    public async Task Connect_SendsConnectAndAcceptsConnAck()
    {
        await using MqttTestBroker broker = new MqttTestBroker { SessionPresent = true };
        await using MqttEndpoint publisher = Publisher(broker);

        await publisher.ConnectAsync(Ct);

        ReceivedPacket connect = broker.Packets.Single(static p => p.Type == MqttPacketType.Connect);

        Assert.Equal(EndpointState.Connected, publisher.State);
        Assert.True(publisher.SessionPresent);
        Assert.Equal("MQTT", MqttPacket.ReadString(connect.Body, out int used));
        Assert.Equal(MqttPacket.ProtocolLevel, connect.Body[used]);
    }

    [Fact]
    public async Task Connect_WhenTheBrokerRefuses_ReportsTheReturnCode()
    {
        await using MqttTestBroker broker = new MqttTestBroker
        {
            ConnectReturnCode = (byte)MqttConnectReturnCode.NotAuthorized,
        };
        await using MqttEndpoint publisher = Publisher(broker);

        EndpointException ex = await Assert.ThrowsAsync<EndpointException>(async () => await publisher.ConnectAsync(Ct));

        Assert.Contains("not authorized", ex.Message, StringComparison.Ordinal);
        Assert.Equal(EndpointState.Faulted, publisher.State);
    }

    [Fact]
    public async Task Connect_WhenTheBrokerAnswersSomethingElse_IsAProtocolError()
    {
        await using MqttTestBroker broker = new MqttTestBroker
        {
            ConnectResponseOverride = [0xD0, 0x00],
        };
        await using MqttEndpoint publisher = Publisher(broker);

        EndpointException ex = await Assert.ThrowsAsync<EndpointException>(async () => await publisher.ConnectAsync(Ct));

        Assert.Contains("PINGRESP", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_WhenTheBrokerNeverAnswers_TimesOut()
    {
        await using MqttTestBroker broker = new MqttTestBroker { SwallowConnect = true };
        await using MqttEndpoint publisher = new MqttEndpoint(
            "broker",
            new MqttSettings("127.0.0.1", broker.Port, ConnectTimeout: TimeSpan.FromMilliseconds(200)));

        EndpointException ex = await Assert.ThrowsAsync<EndpointException>(async () => await publisher.ConnectAsync(Ct));

        Assert.Contains("timed out", ex.Message, StringComparison.Ordinal);
        Assert.Equal(EndpointState.Faulted, publisher.State);
    }

    [Fact]
    public async Task Connect_ToAClosedPort_FaultsWithATransportError()
    {
        MqttTestBroker broker = new MqttTestBroker();
        int port = broker.Port;
        await broker.DisposeAsync();

        await using MqttEndpoint publisher = new MqttEndpoint(
            "broker",
            new MqttSettings("127.0.0.1", port, ConnectTimeout: TimeSpan.FromMilliseconds(500)));

        await Assert.ThrowsAsync<EndpointException>(async () => await publisher.ConnectAsync(Ct));
    }

    [Fact]
    public async Task Publish_BuildsTheTopicFromThePrefixAndTheDottedChannelName()
    {
        await using MqttTestBroker broker = new MqttTestBroker();
        await using MqttEndpoint publisher = Publisher(broker, prefix: "plant");
        await publisher.ConnectAsync(Ct);

        await publisher.WriteAsync(Channel("tank1.level"), Value(1.0), Ct);
        await broker.WaitForAsync(MqttPacketType.Publish, 1, Ct);

        ReceivedPacket published = broker.Packets.Single(static p => p.Type == MqttPacketType.Publish);

        Assert.Equal("plant/tank1/level", MqttPacket.ReadString(published.Body, out _));
    }

    [Fact]
    public async Task Publish_WithoutAPrefix_UsesTheChannelNameAlone()
    {
        await using MqttTestBroker broker = new MqttTestBroker();
        await using MqttEndpoint publisher = Publisher(broker, prefix: null);
        await publisher.ConnectAsync(Ct);

        await publisher.WriteAsync(Channel("level"), Value(1.0), Ct);
        await broker.WaitForAsync(MqttPacketType.Publish, 1, Ct);

        Assert.Equal(
            "level",
            MqttPacket.ReadString(broker.Packets.Single(static p => p.Type == MqttPacketType.Publish).Body, out _));
    }

    [Fact]
    public async Task Publish_JsonPayload_CarriesValueUnitQualityAndTimestamp()
    {
        await using MqttTestBroker broker = new MqttTestBroker();
        await using MqttEndpoint publisher = Publisher(broker, payload: MqttPayloadFormat.Json);
        await publisher.ConnectAsync(Ct);

        await publisher.WriteAsync(Channel("level"), Value(12.5, "bar"), Ct);
        await broker.WaitForAsync(MqttPacketType.Publish, 1, Ct);

        ReceivedPacket published = broker.Packets.Single(static p => p.Type == MqttPacketType.Publish);
        string topic = MqttPacket.ReadString(published.Body, out int used);
        string json = Encoding.UTF8.GetString(published.Body.AsSpan(used));

        Assert.Equal("t/level", topic);
        Assert.Equal(
            "{\"channel\":\"level\",\"value\":12.5,\"quality\":\"good\",\"unit\":\"bar\",\"timestamp\":\"2026-07-30T12:00:00.000Z\"}",
            json);
    }

    [Fact]
    public void JsonPayload_WritesNullForANonFiniteValue()
    {
        MqttEndpoint publisher = new MqttEndpoint(
            "broker",
            new MqttSettings("127.0.0.1", Payload: MqttPayloadFormat.Json));

        string json = Encoding.UTF8.GetString(publisher.FormatPayload(Channel("level"), Value(double.NaN, null, SampleQuality.Bad)));

        Assert.Contains("\"value\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"quality\":\"bad\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"unit\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ValuePayload_IsTheRoundTrippableNumberAlone()
    {
        MqttEndpoint publisher = new MqttEndpoint("broker", new MqttSettings("127.0.0.1"));

        Assert.Equal("21", Encoding.UTF8.GetString(publisher.FormatPayload(Channel(), Value(21.0))));
        Assert.Equal("-1.5", Encoding.UTF8.GetString(publisher.FormatPayload(Channel(), Value(-1.5))));
        Assert.Equal("NaN", Encoding.UTF8.GetString(publisher.FormatPayload(Channel(), Value(double.NaN))));
    }

    [Fact]
    public async Task Publish_ManyValues_KeepsOneConnection()
    {
        await using MqttTestBroker broker = new MqttTestBroker();
        await using MqttEndpoint publisher = Publisher(broker);
        await publisher.ConnectAsync(Ct);

        for (int i = 0; i < 10; i++)
        {
            await publisher.WriteAsync(Channel("a"), Value(i), Ct);
        }

        await broker.WaitForAsync(MqttPacketType.Publish, 10, Ct);

        Assert.Equal(10, publisher.MessagesPublished);
        Assert.Single(broker.Packets, static p => p.Type == MqttPacketType.Connect);
    }

    [Fact]
    public async Task Publish_ConcurrentWrites_AreSerialised()
    {
        await using MqttTestBroker broker = new MqttTestBroker();
        await using MqttEndpoint publisher = Publisher(broker);
        await publisher.ConnectAsync(Ct);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
            await publisher.WriteAsync(Channel($"c{i}"), Value(i), Ct)));

        await broker.WaitForAsync(MqttPacketType.Publish, 20, Ct);

        Assert.Equal(20, publisher.MessagesPublished);
        Assert.Empty(broker.Faults);
    }

    [Fact]
    public async Task KeepAlive_PingsOnlyWhenTheConnectionHasBeenIdleForHalfTheInterval()
    {
        ManualTimeSource time = new ManualTimeSource();
        await using MqttTestBroker broker = new MqttTestBroker();
        await using MqttEndpoint publisher = Publisher(broker, time, keepAlive: TimeSpan.FromSeconds(60));
        await publisher.ConnectAsync(Ct);

        Assert.Equal(TimeSpan.FromSeconds(30), publisher.PingInterval);

        await publisher.TickAsync(Ct);
        Assert.Equal(0, publisher.PingsSent);

        time.Advance(TimeSpan.FromSeconds(29));
        await publisher.TickAsync(Ct);
        Assert.Equal(0, publisher.PingsSent);

        time.Advance(TimeSpan.FromSeconds(1));
        await publisher.TickAsync(Ct);
        await broker.WaitForAsync(MqttPacketType.PingReq, 1, Ct);
        Assert.Equal(1, publisher.PingsSent);

        // The ping itself reset the idle timer.
        await publisher.TickAsync(Ct);
        Assert.Equal(1, publisher.PingsSent);
    }

    [Fact]
    public async Task KeepAlive_APublishAfterALongIdlePeriodPingsFirst()
    {
        ManualTimeSource time = new ManualTimeSource();
        await using MqttTestBroker broker = new MqttTestBroker();
        await using MqttEndpoint publisher = Publisher(broker, time, keepAlive: TimeSpan.FromSeconds(10));
        await publisher.ConnectAsync(Ct);

        time.Advance(TimeSpan.FromSeconds(30));
        await publisher.WriteAsync(Channel("a"), Value(1.0), Ct);
        await broker.WaitForAsync(MqttPacketType.Publish, 1, Ct);

        MqttPacketType[] order = broker.Packets.Select(static p => p.Type).ToArray();

        Assert.Equal([MqttPacketType.Connect, MqttPacketType.PingReq, MqttPacketType.Publish], order);
    }

    [Fact]
    public async Task KeepAlive_ZeroDisablesPinging()
    {
        ManualTimeSource time = new ManualTimeSource();
        await using MqttTestBroker broker = new MqttTestBroker();
        await using MqttEndpoint publisher = Publisher(broker, time, keepAlive: TimeSpan.Zero);
        await publisher.ConnectAsync(Ct);

        Assert.Equal(TimeSpan.Zero, publisher.PingInterval);

        time.Advance(TimeSpan.FromHours(1));
        await publisher.TickAsync(Ct);

        Assert.Equal(0, publisher.PingsSent);
    }

    [Fact]
    public async Task Tick_OnADisconnectedEndpoint_DoesNothing()
    {
        await using MqttTestBroker broker = new MqttTestBroker();
        await using MqttEndpoint publisher = Publisher(broker, new ManualTimeSource(), keepAlive: TimeSpan.FromSeconds(2));

        await publisher.TickAsync(Ct);

        Assert.Equal(0, publisher.PingsSent);
        Assert.Empty(broker.Packets);
    }

    [Fact]
    public async Task Disconnect_SendsDisconnectAndClosesTheConnection()
    {
        await using MqttTestBroker broker = new MqttTestBroker();
        MqttEndpoint publisher = Publisher(broker);
        await publisher.ConnectAsync(Ct);

        await publisher.DisconnectAsync();
        await broker.WaitForAsync(MqttPacketType.Disconnect, 1, Ct);

        Assert.Equal(EndpointState.Disconnected, publisher.State);
        await publisher.DisposeAsync();
    }

    [Fact]
    public async Task Reconnect_AfterDisconnect_StartsAFreshSession()
    {
        await using MqttTestBroker broker = new MqttTestBroker();
        await using MqttEndpoint publisher = Publisher(broker);

        await publisher.ConnectAsync(Ct);
        await publisher.DisconnectAsync();
        await publisher.ConnectAsync(Ct);
        await publisher.WriteAsync(Channel("a"), Value(1.0), Ct);
        await broker.WaitForAsync(MqttPacketType.Publish, 1, Ct);

        Assert.Equal(2, broker.Packets.Count(static p => p.Type == MqttPacketType.Connect));
        Assert.Equal(EndpointState.Connected, publisher.State);
    }

    [Fact]
    public async Task Connect_IsIdempotent()
    {
        await using MqttTestBroker broker = new MqttTestBroker();
        await using MqttEndpoint publisher = Publisher(broker);

        await publisher.ConnectAsync(Ct);
        await publisher.ConnectAsync(Ct);

        Assert.Single(broker.Packets, static p => p.Type == MqttPacketType.Connect);
    }

    [Fact]
    public async Task PublishBeforeConnect_IsRejected()
    {
        await using MqttTestBroker broker = new MqttTestBroker();
        await using MqttEndpoint publisher = Publisher(broker);

        EndpointException ex = await Assert.ThrowsAsync<EndpointException>(async () =>
            await publisher.WriteAsync(Channel("a"), Value(1.0), Ct));

        Assert.Contains("not connected", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_AfterTheBrokerDisappears_FaultsTheEndpoint()
    {
        MqttTestBroker broker = new MqttTestBroker();
        await using MqttEndpoint publisher = Publisher(broker);
        await publisher.ConnectAsync(Ct);
        await broker.DisposeAsync();

        // The first write may still be buffered by the OS; the connection is detected as gone by
        // the time a few have been attempted.
        EndpointException? failure = null;

        for (int i = 0; i < 200 && failure is null; i++)
        {
            try
            {
                await publisher.WriteAsync(Channel("a"), Value(i), Ct);
            }
            catch (EndpointException ex)
            {
                failure = ex;
            }
        }

        Assert.NotNull(failure);
        Assert.Contains("could not send", failure!.Message, StringComparison.Ordinal);
        Assert.Equal(EndpointState.Faulted, publisher.State);
    }

    [Fact]
    public async Task Dispose_RejectsFurtherUse()
    {
        await using MqttTestBroker broker = new MqttTestBroker();
        MqttEndpoint publisher = Publisher(broker);
        await publisher.ConnectAsync(Ct);
        await publisher.DisposeAsync();
        await publisher.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await publisher.ConnectAsync(Ct));
    }

    [Theory]
    [InlineData("topic:0", ChannelRole.Sink, true, null)]
    [InlineData("topic:1", ChannelRole.Sink, false, "must be 'topic:0'")]
    [InlineData("offset:0", ChannelRole.Sink, false, "must be 'topic:0'")]
    [InlineData("topic:0", ChannelRole.Source, false, "publish-only")]
    public void Supports_RequiresTheTopicAddressAndTheSinkDirection(
        string address,
        ChannelRole role,
        bool supported,
        string? fragment)
    {
        MqttEndpoint publisher = new MqttEndpoint("broker", new MqttSettings("127.0.0.1"));

        Assert.Equal(supported, publisher.Supports(Channel("a", address), role, out string? error));

        if (fragment is not null)
        {
            Assert.Contains(fragment, error!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Endpoint_ExposesItsIdentityForLogsAndTheMonitor()
    {
        MqttEndpoint publisher = new MqttEndpoint("broker", new MqttSettings("10.0.0.9", 1884, "bridge-1"));

        Assert.Equal("broker", publisher.Id);
        Assert.Equal("mqtt", publisher.Kind);
        Assert.Equal("10.0.0.9:1884 as 'bridge-1'", publisher.Target);
        Assert.Equal(EndpointState.Disconnected, publisher.State);
    }

    [Fact]
    public void Constructor_RejectsMissingIdentityAndHost()
    {
        Assert.Throws<ArgumentException>(() => new MqttEndpoint(" ", new MqttSettings("h")));
        Assert.Throws<ArgumentNullException>(() => new MqttEndpoint("b", null!));
        Assert.Throws<ArgumentException>(() => new MqttEndpoint("b", new MqttSettings(" ")));
    }
}

public sealed class MqttSettingsTests
{
    private static EndpointOptions Options(string body)
    {
        BridgeConfig config = BridgeConfigLoader.Load($"""
            endpoints:
              - id: broker
            {body}
              - id: src
                type: udp
                listen_port: 5098
            channels:
              - name: a
                endpoint: src
                address: offset:0
                type: u16
              - name: b
                endpoint: broker
                address: topic:0
                type: f32
            routes:
              - id: r
                source: a
                sink: b
            """);

        return config.Endpoint("broker").Options;
    }

    [Fact]
    public void Settings_ReadDefaults()
    {
        MqttSettings settings = MqttSettings.FromOptions(Options("""
                type: mqtt
                host: broker.local
            """));

        Assert.Equal("broker.local", settings.Host);
        Assert.Equal(1883, settings.Port);
        Assert.Equal("protocol-bridge", settings.ClientId);
        Assert.Equal(TimeSpan.FromSeconds(60), settings.EffectiveKeepAlive);
        Assert.True(settings.CleanSession);
        Assert.Null(settings.TopicPrefix);
        Assert.False(settings.Retain);
        Assert.Equal(MqttPayloadFormat.Value, settings.Payload);
    }

    [Fact]
    public void Settings_ReadEveryDocumentedKey()
    {
        MqttSettings settings = MqttSettings.FromOptions(Options("""
                type: mqtt
                host: broker.local
                port: 1884
                client_id: bridge-1
                keep_alive_s: 15
                clean_session: false
                user_name: u
                password: p
                topic_prefix: plant/line1
                retain: true
                payload: json
                connect_timeout_ms: 500
            """));

        Assert.Equal(1884, settings.Port);
        Assert.Equal("bridge-1", settings.ClientId);
        Assert.Equal(15, settings.KeepAliveSeconds);
        Assert.False(settings.CleanSession);
        Assert.Equal("u", settings.UserName);
        Assert.Equal("p", settings.Password);
        Assert.Equal("plant/line1", settings.TopicPrefix);
        Assert.True(settings.Retain);
        Assert.Equal(MqttPayloadFormat.Json, settings.Payload);
        Assert.Equal(TimeSpan.FromMilliseconds(500), settings.EffectiveConnectTimeout);
    }

    [Theory]
    [InlineData("plant", "tank1.level", "plant/tank1/level")]
    [InlineData("plant/", "level", "plant/level")]
    [InlineData(null, "a.b.c", "a/b/c")]
    public void TopicFor_AssemblesThePrefixAndTheDottedName(string? prefix, string channel, string expected)
    {
        MqttSettings settings = new MqttSettings("h", TopicPrefix: prefix);

        Assert.Equal(expected, settings.TopicFor(channel));
    }

    [Theory]
    [InlineData("payload: xml")]
    [InlineData("port: 0")]
    [InlineData("keep_alive_s: 70000")]
    [InlineData("connect_timeout_ms: 0")]
    [InlineData("topic_prefix: \"a/+\"")]
    [InlineData("hostname: broker.local")]
    public void Settings_InvalidValues_AreRejected(string line)
    {
        Assert.Throws<YamlException>(() => MqttSettings.FromOptions(Options($"""
                type: mqtt
                host: broker.local
                {line}
            """)));
    }

    [Fact]
    public void Settings_APasswordWithoutAUserName_IsRejected()
    {
        YamlException ex = Assert.Throws<YamlException>(() => MqttSettings.FromOptions(Options("""
                type: mqtt
                host: broker.local
                password: p
            """)));

        Assert.Contains("needs 'user_name'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_MissingHost_IsRejected()
    {
        Assert.Throws<YamlException>(() => MqttSettings.FromOptions(Options("    type: mqtt")));
    }
}
