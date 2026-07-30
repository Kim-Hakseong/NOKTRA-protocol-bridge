using Pb.Core.Configuration;
using Pb.Core.Configuration.Yaml;

namespace Pb.Core.Endpoints.Udp;

/// <summary>Settings of a UDP endpoint, which may receive, send, or both.</summary>
/// <param name="Host">Destination host for outgoing datagrams; null disables sending.</param>
/// <param name="Port">Destination port for outgoing datagrams.</param>
/// <param name="ListenPort">Local port to receive on; null disables receiving.</param>
/// <param name="BindAddress">Local address to bind the receiver to; null means every interface.</param>
/// <param name="FrameBytes">
/// Fixed outgoing payload length. When null the payload grows to fit the highest channel
/// offset written so far.
/// </param>
public sealed record UdpSettings(
    string? Host = null,
    int Port = 0,
    int? ListenPort = null,
    string? BindAddress = null,
    int? FrameBytes = null)
{
    /// <summary>Configuration keys a <c>udp</c> endpoint accepts.</summary>
    public static readonly string[] KnownKeys = ["host", "port", "listen_port", "bind_address", "frame_bytes"];

    /// <summary>Largest datagram payload this endpoint will assemble or accept.</summary>
    public const int MaxFrameBytes = 65507;

    /// <summary>True when outgoing datagrams are configured.</summary>
    public bool CanSend => !string.IsNullOrWhiteSpace(Host) && Port > 0;

    /// <summary>True when incoming datagrams are configured.</summary>
    public bool CanReceive => ListenPort is > 0;

    /// <summary>
    /// Reads settings from a configuration entry. An endpoint that can neither send nor
    /// receive is a configuration mistake, so it is rejected rather than started.
    /// </summary>
    public static UdpSettings FromOptions(EndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.RejectUnknownKeys("a udp endpoint", KnownKeys);

        string? host = options.GetString("host");
        int port = options.Contains("port") ? options.GetRangedInt("port", 0, 1, 65535) : 0;
        int? listenPort = options.Contains("listen_port")
            ? options.GetRangedInt("listen_port", 0, 1, 65535)
            : null;
        int? frameBytes = options.Contains("frame_bytes")
            ? options.GetRangedInt("frame_bytes", 0, 1, MaxFrameBytes)
            : null;

        if (!string.IsNullOrWhiteSpace(host) && port == 0)
        {
            throw new YamlException("'host' is set, so 'port' is required to send datagrams.", options.LineOf("host"));
        }

        if (string.IsNullOrWhiteSpace(host) && port > 0)
        {
            throw new YamlException("'port' is set, so 'host' is required to send datagrams.", options.LineOf("port"));
        }

        UdpSettings settings = new UdpSettings(host, port, listenPort, options.GetString("bind_address"), frameBytes);

        if (!settings.CanSend && !settings.CanReceive)
        {
            throw new YamlException(
                "a udp endpoint needs 'host' + 'port' to send, 'listen_port' to receive, or both.",
                options.Line);
        }

        return settings;
    }

    public override string ToString()
    {
        string send = CanSend ? $"→ {Host}:{Port}" : string.Empty;
        string receive = CanReceive ? $"← :{ListenPort}" : string.Empty;
        return string.Join(' ', new[] { receive, send }.Where(static s => s.Length > 0));
    }
}
