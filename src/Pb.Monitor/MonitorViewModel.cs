using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Pb.Core.Diagnostics;
using Pb.Core.Endpoints;
using Pb.Core.Routing;

namespace Pb.Monitor;

/// <summary>
/// One endpoint as the window shows it. A record so that a refresh which changes nothing compares
/// equal and leaves the bound list untouched.
/// </summary>
/// <param name="Id">Endpoint id.</param>
/// <param name="Kind">Driver token, upper-cased for the micro label.</param>
/// <param name="Target">What it is attached to.</param>
/// <param name="State">Connection state, which selects the row's accent.</param>
/// <param name="StateText">The state as a short upper-case label.</param>
/// <param name="Detail">Attempt count, and the last error when there is one.</param>
public sealed record EndpointRow(
    string Id,
    string Kind,
    string Target,
    EndpointState State,
    string StateText,
    string Detail);

/// <summary>One route as the window shows it.</summary>
/// <param name="Id">Route id.</param>
/// <param name="Flow">Source and sink channel names, as one arrowed string.</param>
/// <param name="Health">Health, which selects the row's accent.</param>
/// <param name="HealthText">The health as a short upper-case label.</param>
/// <param name="Value">Last engineering value with its unit.</param>
/// <param name="Counters">Read, sent, held and dropped counts as one aligned string.</param>
/// <param name="Detail">The last error, or null.</param>
public sealed record RouteRow(
    string Id,
    string Flow,
    RouteHealth Health,
    string HealthText,
    string Value,
    string Counters,
    string? Detail);

/// <summary>
/// What the monitor window shows. The wording of values, units and durations comes from
/// <see cref="BridgeStatusFormatter"/> so the window and the CLI describe a bridge identically, and
/// so the text is covered by tests rather than by looking at a window.
/// </summary>
public sealed partial class MonitorViewModel : ObservableObject
{
    [ObservableProperty]
    private string _bridgeName = "protocol bridge";

    [ObservableProperty]
    private string _summary = "No configuration loaded.";

    [ObservableProperty]
    private string _configurationPath = "-";

    [ObservableProperty]
    private bool _isHealthy;

    [ObservableProperty]
    private bool _isRunning;

    /// <summary>HEALTHY, DEGRADED or IDLE, for the header chip.</summary>
    [ObservableProperty]
    private string _stateText = "IDLE";

    /// <summary>How long the bridge has been running.</summary>
    [ObservableProperty]
    private string _uptimeText = "-";

    /// <summary>Connected endpoints over total, for the corner dial.</summary>
    [ObservableProperty]
    private string _connectedText = "0/0";

    /// <summary>Fraction of endpoints connected, 0 to 1, for the dial sweep.</summary>
    [ObservableProperty]
    private double _connectedFraction;

    [ObservableProperty]
    private string _forwardedText = "0";

    [ObservableProperty]
    private string _suppressedText = "0";

    [ObservableProperty]
    private string _droppedText = "0";

    [ObservableProperty]
    private string _readText = "0";

    [ObservableProperty]
    private string _routeCountText = "0";

    /// <summary>Date shown in the header, supplied by the caller so nothing here reads the clock.</summary>
    [ObservableProperty]
    private string _dateText = string.Empty;

    /// <summary>One row per endpoint.</summary>
    public ObservableCollection<EndpointRow> Endpoints { get; } = [];

    /// <summary>One row per route.</summary>
    public ObservableCollection<RouteRow> Routes { get; } = [];

    /// <summary>Refreshes every property from a snapshot.</summary>
    public void Update(BridgeStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        int connected = status.Endpoints.Count(static e => e.State == EndpointState.Connected);

        BridgeName = status.Name;
        Summary = BridgeStatusFormatter.Summary(status);
        IsHealthy = status.IsHealthy;
        IsRunning = true;
        StateText = status.IsHealthy ? "HEALTHY" : "DEGRADED";
        UptimeText = BridgeStatusFormatter.Duration(status.Uptime);
        ConnectedText = $"{connected}/{status.Endpoints.Count}";
        ConnectedFraction = status.Endpoints.Count == 0 ? 0 : (double)connected / status.Endpoints.Count;
        ForwardedText = Count(status.TotalForwarded);
        DroppedText = Count(status.TotalDropped);
        SuppressedText = Count(status.Routes.Sum(static r => r.SamplesSuppressed));
        ReadText = Count(status.Routes.Sum(static r => r.SamplesRead));
        RouteCountText = $"{status.Routes.Count(static r => r.Health != RouteHealth.Disabled)}/{status.Routes.Count}";

        Replace(Endpoints, status.Endpoints.Select(ToRow));
        Replace(Routes, status.Routes.Select(ToRow));
    }

    /// <summary>Shows a failure instead of a status, for a configuration that could not be started.</summary>
    public void ShowFailure(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Summary = message;
        IsHealthy = false;
        IsRunning = false;
        StateText = "STOPPED";
        UptimeText = "-";
        ConnectedText = "0/0";
        ConnectedFraction = 0;
        Endpoints.Clear();
        Routes.Clear();
    }

    private static EndpointRow ToRow(EndpointStatus endpoint)
    {
        string detail = endpoint.LastError is null
            ? $"attempt {endpoint.ConnectAttempts.ToString(CultureInfo.InvariantCulture)}"
              + (endpoint.Reconnects > 0 ? $" · {endpoint.Reconnects.ToString(CultureInfo.InvariantCulture)} reconnect(s)" : string.Empty)
            : endpoint.LastError;

        return new EndpointRow(
            endpoint.Id,
            endpoint.Kind.Replace('_', '-').ToUpperInvariant(),
            endpoint.Target,
            endpoint.State,
            endpoint.State.ToString().ToUpperInvariant(),
            detail);
    }

    private static RouteRow ToRow(RouteStatus route) => new RouteRow(
        route.Id,
        $"{route.Source}  →  {route.Sink}",
        route.Health,
        Label(route.Health),
        BridgeStatusFormatter.Value(route),
        $"R {Count(route.SamplesRead)}   S {Count(route.SamplesForwarded)}   H {Count(route.SamplesSuppressed)}   D {Count(route.SamplesDropped)}",
        route.LastError);

    /// <summary>Splits camel-cased health names so the micro label reads as two words.</summary>
    private static string Label(RouteHealth health) => health switch
    {
        RouteHealth.SourceFault => "SOURCE FAULT",
        RouteHealth.SinkFault => "SINK FAULT",
        _ => health.ToString().ToUpperInvariant(),
    };

    private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Rewrites a bound collection in place. Only rows that actually changed are replaced, so the
    /// list does not flicker on every refresh.
    /// </summary>
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> rows)
    {
        int index = 0;

        foreach (T row in rows)
        {
            if (index < target.Count)
            {
                if (!EqualityComparer<T>.Default.Equals(target[index], row))
                {
                    target[index] = row;
                }
            }
            else
            {
                target.Add(row);
            }

            index++;
        }

        while (target.Count > index)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}
