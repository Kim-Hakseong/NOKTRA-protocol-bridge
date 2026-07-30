using Pb.Core.Endpoints;

namespace Pb.Core.Routing;

/// <summary>Health of a single route.</summary>
public enum RouteHealth
{
    /// <summary>Configured but has not moved a value yet.</summary>
    Starting = 0,

    /// <summary>Moving values, or legitimately suppressing them by deadband.</summary>
    Ok,

    /// <summary>The source could not be read; the rest of the bridge is unaffected.</summary>
    SourceFault,

    /// <summary>The sink could not be written; the rest of the bridge is unaffected.</summary>
    SinkFault,

    /// <summary>Parked by configuration (<c>enabled: false</c>).</summary>
    Disabled,
}

/// <summary>An immutable snapshot of one route's state and counters.</summary>
/// <param name="Id">Route id.</param>
/// <param name="Source">Source channel name.</param>
/// <param name="Sink">Sink channel name.</param>
/// <param name="Health">Current health.</param>
/// <param name="SamplesRead">Values successfully read from the source.</param>
/// <param name="SamplesForwarded">Values written to the sink.</param>
/// <param name="SamplesSuppressed">Values the deadband held back.</param>
/// <param name="SamplesDropped">Values discarded because the sink queue was full.</param>
/// <param name="ReadFailures">Failed source reads.</param>
/// <param name="WriteFailures">Failed sink writes.</param>
/// <param name="LastValue">Most recent engineering value, or null before the first read.</param>
/// <param name="Unit">Engineering unit of the route's transform.</param>
/// <param name="LastForwardedAt">When a value was last written, or null.</param>
/// <param name="LastError">Most recent failure message, or null.</param>
public sealed record RouteStatus(
    string Id,
    string Source,
    string Sink,
    RouteHealth Health,
    long SamplesRead,
    long SamplesForwarded,
    long SamplesSuppressed,
    long SamplesDropped,
    long ReadFailures,
    long WriteFailures,
    double? LastValue,
    string? Unit,
    DateTimeOffset? LastForwardedAt,
    string? LastError);

/// <summary>An immutable snapshot of one endpoint's state.</summary>
/// <param name="Id">Endpoint id.</param>
/// <param name="Kind">Driver token.</param>
/// <param name="Target">What it is attached to.</param>
/// <param name="State">Connection state.</param>
/// <param name="ConnectAttempts">Connection attempts since the bridge started.</param>
/// <param name="Reconnects">Successful connections after the first one.</param>
/// <param name="LastError">Most recent connection failure message, or null.</param>
public sealed record EndpointStatus(
    string Id,
    string Kind,
    string Target,
    EndpointState State,
    long ConnectAttempts,
    long Reconnects,
    string? LastError);

/// <summary>An immutable snapshot of the whole bridge, for the CLI and the monitor window.</summary>
/// <param name="Name">Bridge name from configuration.</param>
/// <param name="Uptime">How long the router has been running.</param>
/// <param name="Endpoints">Endpoint snapshots, in configuration order.</param>
/// <param name="Routes">Route snapshots, in configuration order.</param>
public sealed record BridgeStatus(
    string Name,
    TimeSpan Uptime,
    IReadOnlyList<EndpointStatus> Endpoints,
    IReadOnlyList<RouteStatus> Routes)
{
    /// <summary>True when every endpoint is connected and no route is faulted.</summary>
    public bool IsHealthy =>
        Endpoints.All(static e => e.State == EndpointState.Connected)
        && Routes.All(static r => r.Health is RouteHealth.Ok or RouteHealth.Starting or RouteHealth.Disabled);

    /// <summary>Values written to sinks across all routes.</summary>
    public long TotalForwarded => Routes.Sum(static r => r.SamplesForwarded);

    /// <summary>Values discarded across all routes because a sink queue was full.</summary>
    public long TotalDropped => Routes.Sum(static r => r.SamplesDropped);
}
