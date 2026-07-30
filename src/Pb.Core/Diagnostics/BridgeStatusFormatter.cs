using System.Globalization;
using System.Text;
using Pb.Core.Configuration;
using Pb.Core.Endpoints;
using Pb.Core.Routing;

namespace Pb.Core.Diagnostics;

/// <summary>
/// Renders a <see cref="BridgeStatus"/> as text. Shared by the CLI and the monitor window so both
/// describe a running bridge identically, and kept here so the wording is covered by tests rather
/// than by looking at a terminal.
/// </summary>
public static class BridgeStatusFormatter
{
    /// <summary>A one-line summary suitable for a status bar or a periodic log line.</summary>
    public static string Summary(BridgeStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        int connected = status.Endpoints.Count(static e => e.State == EndpointState.Connected);
        int faulted = status.Routes.Count(static r => r.Health is RouteHealth.SourceFault or RouteHealth.SinkFault);

        StringBuilder line = new StringBuilder();
        line.Append(status.IsHealthy ? "healthy" : "degraded");
        line.Append(" · up ").Append(Duration(status.Uptime));
        line.Append(" · endpoints ").Append(connected).Append('/').Append(status.Endpoints.Count);
        line.Append(" · forwarded ").Append(status.TotalForwarded.ToString(CultureInfo.InvariantCulture));

        if (faulted > 0)
        {
            line.Append(" · faulted routes ").Append(faulted.ToString(CultureInfo.InvariantCulture));
        }

        if (status.TotalDropped > 0)
        {
            line.Append(" · dropped ").Append(status.TotalDropped.ToString(CultureInfo.InvariantCulture));
        }

        return line.ToString();
    }

    /// <summary>A full report: the summary followed by an endpoint table and a route table.</summary>
    public static string Report(BridgeStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        StringBuilder report = new StringBuilder();
        report.Append(status.Name).Append(" — ").AppendLine(Summary(status));
        report.AppendLine();

        report.AppendLine("Endpoints");
        foreach (string row in Table(
            ["ID", "KIND", "TARGET", "STATE", "ATTEMPTS", "LAST ERROR"],
            status.Endpoints.Select(static e => new[]
            {
                e.Id,
                e.Kind,
                e.Target,
                e.State.ToString(),
                e.ConnectAttempts.ToString(CultureInfo.InvariantCulture),
                e.LastError ?? "-",
            })))
        {
            report.Append("  ").AppendLine(row);
        }

        report.AppendLine();
        report.AppendLine("Routes");
        foreach (string row in Table(
            ["ID", "SOURCE", "SINK", "HEALTH", "READ", "SENT", "HELD", "DROP", "LAST VALUE", "LAST ERROR"],
            status.Routes.Select(static r => new[]
            {
                r.Id,
                r.Source,
                r.Sink,
                r.Health.ToString(),
                r.SamplesRead.ToString(CultureInfo.InvariantCulture),
                r.SamplesForwarded.ToString(CultureInfo.InvariantCulture),
                r.SamplesSuppressed.ToString(CultureInfo.InvariantCulture),
                r.SamplesDropped.ToString(CultureInfo.InvariantCulture),
                Value(r),
                r.LastError ?? "-",
            })))
        {
            report.Append("  ").AppendLine(row);
        }

        return report.ToString();
    }

    /// <summary>Describes the topology a configuration declares, without connecting to anything.</summary>
    public static string Describe(BridgeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        StringBuilder report = new StringBuilder();
        report.Append(config.Name).Append(" — ")
            .Append(config.Endpoints.Count).Append(" endpoint(s), ")
            .Append(config.Channels.Count).Append(" channel(s), ")
            .Append(config.Routes.Count).Append(" route(s), ")
            .Append(config.EnabledRoutes.Count()).AppendLine(" enabled");
        report.AppendLine();

        report.AppendLine("Endpoints");
        foreach (string row in Table(
            ["ID", "TYPE"],
            config.Endpoints.Select(static e => new[] { e.Id, e.Type })))
        {
            report.Append("  ").AppendLine(row);
        }

        report.AppendLine();
        report.AppendLine("Routes");
        foreach (string row in Table(
            ["ID", "SOURCE", "SINK", "TRIGGER", "TRANSFORM", "ENABLED"],
            config.Routes.Select(r => new[]
            {
                r.Id,
                $"{r.Source} ({config.Channel(r.Source).Spec.Address})",
                $"{r.Sink} ({config.Channel(r.Sink).Spec.Address})",
                Trigger(r.Trigger),
                Transform(r),
                r.Enabled ? "yes" : "no",
            })))
        {
            report.Append("  ").AppendLine(row);
        }

        return report.ToString();
    }

    /// <summary>Renders one route as a single line, for the monitor's list.</summary>
    public static string RouteLine(RouteStatus route)
    {
        ArgumentNullException.ThrowIfNull(route);

        string value = Value(route);
        string detail = route.LastError is null ? string.Empty : $" — {route.LastError}";
        return $"{route.Id}: {route.Source} → {route.Sink} · {route.Health} · {value}{detail}";
    }

    /// <summary>Renders one endpoint as a single line, for the monitor's list.</summary>
    public static string EndpointLine(EndpointStatus endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        string detail = endpoint.LastError is null ? string.Empty : $" — {endpoint.LastError}";
        return $"{endpoint.Id} ({endpoint.Kind}): {endpoint.Target} · {endpoint.State}{detail}";
    }

    /// <summary>Formats a route's last value with its unit, or a dash before the first read.</summary>
    public static string Value(RouteStatus route)
    {
        ArgumentNullException.ThrowIfNull(route);

        if (route.LastValue is not double value)
        {
            return "-";
        }

        string text = value.ToString("0.######", CultureInfo.InvariantCulture);
        return route.Unit is null ? text : $"{text} {route.Unit}";
    }

    /// <summary>Formats a duration compactly: <c>3d 04:05:06</c>, <c>04:05:06</c> or <c>5.2s</c>.</summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalMinutes < 1)
        {
            return $"{duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s";
        }

        string clock = $"{duration.Hours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        return duration.Days > 0 ? $"{duration.Days}d {clock}" : clock;
    }

    private static string Trigger(TriggerConfig trigger) => trigger.Mode == TriggerMode.Periodic
        ? $"every {trigger.Period.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms"
        : "on change";

    private static string Transform(RouteConfig route)
    {
        List<string> parts = [];

        if (!route.Transform.IsIdentityScaling)
        {
            parts.Add($"x{route.Transform.Scale.ToString("0.######", CultureInfo.InvariantCulture)}");

            if (route.Transform.Offset != 0.0)
            {
                string sign = route.Transform.Offset > 0 ? "+" : "-";
                parts.Add($"{sign}{Math.Abs(route.Transform.Offset).ToString("0.######", CultureInfo.InvariantCulture)}");
            }
        }

        if (route.Transform.Deadband > 0.0)
        {
            parts.Add($"deadband {route.Transform.Deadband.ToString("0.######", CultureInfo.InvariantCulture)}");
        }

        if (route.Transform.Unit is string unit)
        {
            parts.Add(unit);
        }

        return parts.Count == 0 ? "pass-through" : string.Join(' ', parts);
    }

    /// <summary>Lays out rows as space-padded columns, sized to their widest cell.</summary>
    private static IEnumerable<string> Table(string[] headers, IEnumerable<string[]> rows)
    {
        List<string[]> all = [headers, .. rows];
        int columns = headers.Length;
        int[] widths = new int[columns];

        foreach (string[] row in all)
        {
            for (int i = 0; i < columns && i < row.Length; i++)
            {
                widths[i] = Math.Max(widths[i], row[i].Length);
            }
        }

        foreach (string[] row in all)
        {
            StringBuilder line = new StringBuilder();

            for (int i = 0; i < columns; i++)
            {
                string cell = i < row.Length ? row[i] : string.Empty;

                // The final column is not padded, so lines have no trailing spaces.
                line.Append(i == columns - 1 ? cell : cell.PadRight(widths[i] + 2));
            }

            yield return line.ToString().TrimEnd();
        }
    }
}
