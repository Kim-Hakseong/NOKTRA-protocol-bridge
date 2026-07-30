using Pb.Core.Endpoints;
using Pb.Core.Routing;

namespace Pb.Monitor;

/// <summary>
/// The scenario the off-screen render uses: the shipped <c>examples/loopback.yaml</c> bridge, with
/// no MQTT broker reachable.
/// </summary>
/// <remarks>
/// The numbers are the ones a real run of that example produces — three healthy endpoints, an MQTT
/// endpoint retrying, one parked route — rather than invented ones. A plausible-looking arrangement
/// would not show whether the layout survives real data density, which is the only reason to render
/// a design study at all.
/// </remarks>
public static class DemoStatus
{
    /// <summary>The configuration this scenario runs.</summary>
    public const string ConfigurationPath = "examples/loopback.yaml";

    /// <summary>Builds the scenario.</summary>
    public static BridgeStatus Create() => new BridgeStatus(
        "loopback-demo",
        TimeSpan.FromSeconds(3754),
        [
            new EndpointStatus("field", "udp", "← :15010", EndpointState.Connected, 1, 0, null),
            new EndpointStatus("scada", "udp", "→ 127.0.0.1:15011", EndpointState.Connected, 1, 0, null),
            new EndpointStatus("archive", "csv", "out/loopback.csv", EndpointState.Connected, 1, 0, null),
            new EndpointStatus(
                "broker",
                "mqtt",
                "127.0.0.1:1883 as 'noktra-protocol-bridge'",
                EndpointState.Faulted,
                9,
                0,
                "could not connect to 127.0.0.1:1883: Connection refused"),
        ],
        [
            new RouteStatus(
                "level_to_scada",
                "level_raw",
                "level_out",
                RouteHealth.Ok,
                1382,
                1216,
                166,
                0,
                0,
                0,
                10.0,
                "bar",
                DateTimeOffset.UnixEpoch,
                null),
            new RouteStatus(
                "temperature_to_scada",
                "temperature_raw",
                "temperature_out",
                RouteHealth.Ok,
                1382,
                944,
                438,
                0,
                0,
                0,
                -5.5,
                "degC",
                DateTimeOffset.UnixEpoch,
                null),
            new RouteStatus(
                "level_to_csv",
                "level_raw",
                "level_log",
                RouteHealth.Ok,
                3754,
                3754,
                0,
                0,
                0,
                0,
                10.0,
                "bar",
                DateTimeOffset.UnixEpoch,
                null),
            new RouteStatus(
                "level_to_mqtt",
                "level_raw",
                "tank1.level",
                RouteHealth.Disabled,
                0,
                0,
                0,
                0,
                0,
                0,
                null,
                "bar",
                null,
                null),
        ]);
}
