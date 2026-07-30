using Pb.Core.Channels;
using Pb.Core.Transforms;

namespace Pb.Core.Configuration;

/// <summary>How a route decides when to move a value.</summary>
public enum TriggerMode
{
    /// <summary>
    /// The route samples its source on a fixed period. Required for poll-driven sources
    /// such as a Modbus TCP master.
    /// </summary>
    Periodic = 0,

    /// <summary>
    /// The route forwards whenever its source produces a value. Only meaningful for sources
    /// that push, such as a UDP listener or a serial port.
    /// </summary>
    OnChange,
}

/// <summary>When a route fires.</summary>
/// <param name="Mode">Trigger kind.</param>
/// <param name="Period">Sampling period; <see cref="TimeSpan.Zero"/> for <see cref="TriggerMode.OnChange"/>.</param>
/// <param name="Line">Source line of the trigger section.</param>
public sealed record TriggerConfig(TriggerMode Mode, TimeSpan Period, int Line = 0)
{
    /// <summary>Default trigger: poll once a second.</summary>
    public static TriggerConfig DefaultPeriodic { get; } = new TriggerConfig(TriggerMode.Periodic, TimeSpan.FromSeconds(1));
}

/// <summary>One endpoint declaration.</summary>
/// <param name="Id">Configuration-unique endpoint id referenced by channels.</param>
/// <param name="Type">Driver type token, for example <c>modbus-tcp</c> or <c>udp</c>.</param>
/// <param name="Options">Driver-specific settings, validated by the driver.</param>
/// <param name="Line">Source line of the endpoint entry.</param>
public sealed record EndpointConfig(string Id, string Type, EndpointOptions Options, int Line = 0);

/// <summary>One channel declaration, resolved against its endpoint.</summary>
/// <param name="Spec">The channel's address and wire layout.</param>
/// <param name="Line">Source line of the channel entry.</param>
public sealed record ChannelConfig(ChannelSpec Spec, int Line = 0)
{
    public string Name => Spec.Name;

    public string Endpoint => Spec.Endpoint;
}

/// <summary>One route: read a source channel, transform, write a sink channel.</summary>
/// <param name="Id">Configuration-unique route id used in logs and statistics.</param>
/// <param name="Source">Name of the source channel.</param>
/// <param name="Sink">Name of the sink channel.</param>
/// <param name="Trigger">When the route fires.</param>
/// <param name="Transform">Scale, offset, unit and deadband applied between source and sink.</param>
/// <param name="Enabled">False parks the route without deleting its configuration.</param>
/// <param name="Line">Source line of the route entry.</param>
public sealed record RouteConfig(
    string Id,
    string Source,
    string Sink,
    TriggerConfig Trigger,
    ValueTransform Transform,
    bool Enabled = true,
    int Line = 0);

/// <summary>
/// A complete, cross-referenced bridge configuration. An instance can only be obtained from
/// <see cref="BridgeConfigLoader"/>, which guarantees that every route names channels that
/// exist and every channel names an endpoint that exists.
/// </summary>
public sealed class BridgeConfig
{
    private readonly Dictionary<string, EndpointConfig> _endpointsById;
    private readonly Dictionary<string, ChannelConfig> _channelsByName;

    internal BridgeConfig(
        string name,
        IReadOnlyList<EndpointConfig> endpoints,
        IReadOnlyList<ChannelConfig> channels,
        IReadOnlyList<RouteConfig> routes)
    {
        Name = name;
        Endpoints = endpoints;
        Channels = channels;
        Routes = routes;
        _endpointsById = endpoints.ToDictionary(static e => e.Id, StringComparer.Ordinal);
        _channelsByName = channels.ToDictionary(static c => c.Name, StringComparer.Ordinal);
    }

    /// <summary>Display name of this bridge, defaulting to <c>bridge</c>.</summary>
    public string Name { get; }

    public IReadOnlyList<EndpointConfig> Endpoints { get; }

    public IReadOnlyList<ChannelConfig> Channels { get; }

    public IReadOnlyList<RouteConfig> Routes { get; }

    /// <summary>Routes that are not parked.</summary>
    public IEnumerable<RouteConfig> EnabledRoutes => Routes.Where(static r => r.Enabled);

    /// <summary>Looks up an endpoint by id. The loader guarantees referenced ids resolve.</summary>
    public EndpointConfig Endpoint(string id) => _endpointsById.TryGetValue(id, out EndpointConfig? endpoint)
        ? endpoint
        : throw new KeyNotFoundException($"No endpoint '{id}' in this configuration.");

    /// <summary>Looks up a channel by name. The loader guarantees referenced names resolve.</summary>
    public ChannelConfig Channel(string name) => _channelsByName.TryGetValue(name, out ChannelConfig? channel)
        ? channel
        : throw new KeyNotFoundException($"No channel '{name}' in this configuration.");

    /// <summary>Endpoint ids that at least one enabled route reads from.</summary>
    public IEnumerable<string> SourceEndpointIds => EnabledRoutes
        .Select(r => Channel(r.Source).Endpoint)
        .Distinct(StringComparer.Ordinal);

    /// <summary>Endpoint ids that at least one enabled route writes to.</summary>
    public IEnumerable<string> SinkEndpointIds => EnabledRoutes
        .Select(r => Channel(r.Sink).Endpoint)
        .Distinct(StringComparer.Ordinal);
}
