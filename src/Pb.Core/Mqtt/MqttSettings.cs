using Pb.Core.Configuration;
using Pb.Core.Configuration.Yaml;

namespace Pb.Core.Mqtt;

/// <summary>How a published value is rendered into the PUBLISH payload.</summary>
public enum MqttPayloadFormat
{
    /// <summary>The value alone, in round-trippable invariant form. Matches the DESIGN golden vector.</summary>
    Value = 0,

    /// <summary>A small JSON object carrying value, unit, quality and timestamp.</summary>
    Json,
}

/// <summary>Settings of an MQTT publisher endpoint.</summary>
/// <param name="Host">Broker host name or address.</param>
/// <param name="Port">Broker TCP port; the registered MQTT port is 1883.</param>
/// <param name="ClientId">Client identifier sent in CONNECT.</param>
/// <param name="KeepAlive">Keep-alive interval; <see cref="TimeSpan.Zero"/> disables the mechanism.</param>
/// <param name="CleanSession">Whether the broker discards previous session state.</param>
/// <param name="UserName">Optional user name.</param>
/// <param name="Password">Optional password.</param>
/// <param name="TopicPrefix">Optional prefix prepended to every channel's topic.</param>
/// <param name="Retain">Whether published messages carry the RETAIN flag.</param>
/// <param name="Payload">How values are rendered.</param>
/// <param name="ConnectTimeout">How long CONNECT may wait for CONNACK.</param>
public sealed record MqttSettings(
    string Host,
    int Port = MqttPacket.DefaultPort,
    string ClientId = "protocol-bridge",
    TimeSpan? KeepAlive = null,
    bool CleanSession = true,
    string? UserName = null,
    string? Password = null,
    string? TopicPrefix = null,
    bool Retain = false,
    MqttPayloadFormat Payload = MqttPayloadFormat.Value,
    TimeSpan? ConnectTimeout = null)
{
    /// <summary>Configuration keys an <c>mqtt</c> endpoint accepts.</summary>
    public static readonly string[] KnownKeys =
    [
        "host", "port", "client_id", "keep_alive_s", "clean_session",
        "user_name", "password", "topic_prefix", "retain", "payload", "connect_timeout_ms",
    ];

    /// <summary>Default keep-alive interval.</summary>
    public static TimeSpan DefaultKeepAlive { get; } = TimeSpan.FromSeconds(60);

    /// <summary>Default CONNACK timeout.</summary>
    public static TimeSpan DefaultConnectTimeout { get; } = TimeSpan.FromMilliseconds(2000);

    /// <summary>Effective keep-alive interval.</summary>
    public TimeSpan EffectiveKeepAlive => KeepAlive ?? DefaultKeepAlive;

    /// <summary>Effective CONNACK timeout.</summary>
    public TimeSpan EffectiveConnectTimeout => ConnectTimeout ?? DefaultConnectTimeout;

    /// <summary>Keep-alive seconds as CONNECT carries them.</summary>
    public int KeepAliveSeconds => (int)Math.Round(EffectiveKeepAlive.TotalSeconds);

    /// <summary>
    /// The topic a channel publishes to: the optional prefix, then the channel name with '.'
    /// read as a topic level separator (spec/mqtt-subset.md §9).
    /// </summary>
    public string TopicFor(string channelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);

        string levels = channelName.Replace('.', '/');

        return string.IsNullOrWhiteSpace(TopicPrefix)
            ? levels
            : $"{TopicPrefix.Trim().TrimEnd('/')}/{levels}";
    }

    /// <summary>Reads settings from a configuration entry.</summary>
    public static MqttSettings FromOptions(EndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.RejectUnknownKeys("an mqtt endpoint", KnownKeys);

        string? password = options.GetString("password");
        string? userName = options.GetString("user_name");

        if (password is not null && userName is null)
        {
            throw new YamlException(
                "'password' needs 'user_name'; a password alone has no defined position in CONNECT (spec §3).",
                options.LineOf("password"));
        }

        string prefix = options.GetString("topic_prefix", string.Empty) ?? string.Empty;

        if (prefix.Contains('+', StringComparison.Ordinal) || prefix.Contains('#', StringComparison.Ordinal))
        {
            throw new YamlException(
                "'topic_prefix' must not contain the wildcards '+' or '#' (spec §5).",
                options.LineOf("topic_prefix"));
        }

        return new MqttSettings(
            options.RequireString("host"),
            options.GetRangedInt("port", MqttPacket.DefaultPort, 1, 65535),
            options.GetString("client_id", "protocol-bridge") ?? "protocol-bridge",
            TimeSpan.FromSeconds(options.GetRangedInt("keep_alive_s", (int)DefaultKeepAlive.TotalSeconds, 0, 65535)),
            options.GetBool("clean_session", true),
            userName,
            password,
            prefix.Length == 0 ? null : prefix,
            options.GetBool("retain", false),
            ParsePayloadFormat(options),
            TimeSpan.FromMilliseconds(options.GetPositiveInt("connect_timeout_ms", (int)DefaultConnectTimeout.TotalMilliseconds)));
    }

    public override string ToString() => $"{Host}:{Port} as '{ClientId}'";

    private static MqttPayloadFormat ParsePayloadFormat(EndpointOptions options) =>
        BridgeConfigLoader.Normalize(options.GetString("payload") ?? "value") switch
        {
            "value" or "raw" or "plain" => MqttPayloadFormat.Value,
            "json" => MqttPayloadFormat.Json,
            var other => throw new YamlException(
                $"'payload' must be 'value' or 'json' but is '{other}'.",
                options.LineOf("payload")),
        };
}
