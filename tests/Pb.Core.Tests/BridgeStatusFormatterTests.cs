using Pb.Core.Configuration;
using Pb.Core.Diagnostics;
using Pb.Core.Endpoints;
using Pb.Core.Routing;
using Xunit;

namespace Pb.Core.Tests;

public sealed class BridgeStatusFormatterTests
{
    private static EndpointStatus Endpoint(
        string id = "plc",
        EndpointState state = EndpointState.Connected,
        string? error = null) =>
        new EndpointStatus(id, "modbus_tcp", "127.0.0.1:502 unit 1", state, 1, 0, error);

    private static RouteStatus Route(
        string id = "level",
        RouteHealth health = RouteHealth.Ok,
        double? lastValue = 10.0,
        string? unit = "bar",
        long dropped = 0,
        string? error = null) =>
        new RouteStatus(id, "level_raw", "level_out", health, 5, 4, 1, dropped, 0, 0, lastValue, unit, DateTimeOffset.UnixEpoch, error);

    private static BridgeStatus Status(
        IEnumerable<EndpointStatus>? endpoints = null,
        IEnumerable<RouteStatus>? routes = null,
        TimeSpan? uptime = null) =>
        new BridgeStatus(
            "demo",
            uptime ?? TimeSpan.FromSeconds(42),
            (endpoints ?? [Endpoint()]).ToList(),
            (routes ?? [Route()]).ToList());

    [Fact]
    public void Summary_AHealthyBridge_ReportsUptimeEndpointsAndThroughput()
    {
        string summary = BridgeStatusFormatter.Summary(Status());

        Assert.Equal("healthy · up 42.0s · endpoints 1/1 · forwarded 4", summary);
    }

    [Fact]
    public void Summary_ADegradedBridge_CountsFaultedRoutes()
    {
        BridgeStatus status = Status(
            endpoints: [Endpoint(state: EndpointState.Faulted, error: "refused")],
            routes: [Route(health: RouteHealth.SourceFault), Route("other", RouteHealth.SinkFault)]);

        string summary = BridgeStatusFormatter.Summary(status);

        Assert.StartsWith("degraded", summary, StringComparison.Ordinal);
        Assert.Contains("endpoints 0/1", summary, StringComparison.Ordinal);
        Assert.Contains("faulted routes 2", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_MentionsDroppedSamplesOnlyWhenThereAreSome()
    {
        Assert.DoesNotContain("dropped", BridgeStatusFormatter.Summary(Status()), StringComparison.Ordinal);
        Assert.Contains("dropped 7", BridgeStatusFormatter.Summary(Status(routes: [Route(dropped: 7)])), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "0.0s")]
    [InlineData(5.24, "5.2s")]
    [InlineData(5.25, "5.3s")]
    [InlineData(59.9, "59.9s")]
    [InlineData(60, "00:01:00")]
    [InlineData(3661, "01:01:01")]
    [InlineData(90061, "1d 01:01:01")]
    public void Duration_UsesSecondsThenAClockThenDays(double seconds, string expected)
    {
        Assert.Equal(expected, BridgeStatusFormatter.Duration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Duration_NeverRendersNegativeTime()
    {
        Assert.Equal("0.0s", BridgeStatusFormatter.Duration(TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void Value_ShowsTheUnitAndADashBeforeTheFirstRead()
    {
        Assert.Equal("10 bar", BridgeStatusFormatter.Value(Route()));
        Assert.Equal("10", BridgeStatusFormatter.Value(Route(unit: null)));
        Assert.Equal("-", BridgeStatusFormatter.Value(Route(lastValue: null)));
        Assert.Equal("-1.234568", BridgeStatusFormatter.Value(Route(lastValue: -1.23456789, unit: null)));
    }

    [Fact]
    public void Report_ContainsBothTablesWithTheirHeadings()
    {
        string report = BridgeStatusFormatter.Report(Status());

        Assert.Contains("demo — healthy", report, StringComparison.Ordinal);
        Assert.Contains("Endpoints", report, StringComparison.Ordinal);
        Assert.Contains("ID", report, StringComparison.Ordinal);
        Assert.Contains("LAST ERROR", report, StringComparison.Ordinal);
        Assert.Contains("plc", report, StringComparison.Ordinal);
        Assert.Contains("Routes", report, StringComparison.Ordinal);
        Assert.Contains("level_raw", report, StringComparison.Ordinal);
        Assert.Contains("10 bar", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_AlignsColumnsAndLeavesNoTrailingSpaces()
    {
        BridgeStatus status = Status(endpoints:
        [
            Endpoint("a_very_long_endpoint_id"),
            Endpoint("b"),
        ]);

        string[] lines = BridgeStatusFormatter.Report(status)
            .Split(Environment.NewLine)
            .Where(static l => l.Contains("modbus_tcp", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.Equal(l.TrimEnd(), l));

        // Both rows put the kind column at the same offset.
        Assert.Equal(
            lines[0].IndexOf("modbus_tcp", StringComparison.Ordinal),
            lines[1].IndexOf("modbus_tcp", StringComparison.Ordinal));
    }

    [Fact]
    public void Report_ShowsADashForAnEndpointWithNoError()
    {
        Assert.Contains(" -", BridgeStatusFormatter.Report(Status()), StringComparison.Ordinal);
    }

    [Fact]
    public void Lines_RenderOneEndpointAndOneRouteCompactly()
    {
        Assert.Equal(
            "plc (modbus_tcp): 127.0.0.1:502 unit 1 · Connected",
            BridgeStatusFormatter.EndpointLine(Endpoint()));

        Assert.Equal(
            "plc (modbus_tcp): 127.0.0.1:502 unit 1 · Faulted — refused",
            BridgeStatusFormatter.EndpointLine(Endpoint(state: EndpointState.Faulted, error: "refused")));

        Assert.Equal(
            "level: level_raw → level_out · Ok · 10 bar",
            BridgeStatusFormatter.RouteLine(Route()));

        Assert.Equal(
            "level: level_raw → level_out · SourceFault · 10 bar — timed out",
            BridgeStatusFormatter.RouteLine(Route(health: RouteHealth.SourceFault, error: "timed out")));
    }

    [Fact]
    public void Describe_ReportsTheTopologyWithoutConnecting()
    {
        BridgeConfig config = BridgeConfigLoader.Load("""
            bridge:
              name: described

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
                address: holding:7
                type: u16
              - name: level_out
                endpoint: telemetry
                address: offset:0
                type: f32

            routes:
              - id: scaled
                source: level_raw
                sink: level_out
                trigger:
                  mode: periodic
                  period_ms: 250
                transform:
                  scale: 0.1
                  offset: -3
                  unit: bar
                  deadband: 0.05
              - id: parked
                source: level_raw
                sink: level_out
                enabled: false
                trigger:
                  mode: on_change
            """);

        string description = BridgeStatusFormatter.Describe(config);

        Assert.Contains("described — 2 endpoint(s), 2 channel(s), 2 route(s), 1 enabled", description, StringComparison.Ordinal);
        Assert.Contains("holding:7", description, StringComparison.Ordinal);
        Assert.Contains("offset:0", description, StringComparison.Ordinal);
        Assert.Contains("every 250 ms", description, StringComparison.Ordinal);
        Assert.Contains("on change", description, StringComparison.Ordinal);
        Assert.Contains("x0.1 -3 deadband 0.05 bar", description, StringComparison.Ordinal);
        Assert.Contains("pass-through", description, StringComparison.Ordinal);
        Assert.Contains("yes", description, StringComparison.Ordinal);
        Assert.Contains("no", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Formatters_RejectNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => BridgeStatusFormatter.Summary(null!));
        Assert.Throws<ArgumentNullException>(() => BridgeStatusFormatter.Report(null!));
        Assert.Throws<ArgumentNullException>(() => BridgeStatusFormatter.Describe(null!));
        Assert.Throws<ArgumentNullException>(() => BridgeStatusFormatter.RouteLine(null!));
        Assert.Throws<ArgumentNullException>(() => BridgeStatusFormatter.EndpointLine(null!));
        Assert.Throws<ArgumentNullException>(() => BridgeStatusFormatter.Value(null!));
    }
}
