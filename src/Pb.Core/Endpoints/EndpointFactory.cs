using Pb.Core.Configuration;
using Pb.Core.Configuration.Yaml;
using Pb.Core.Endpoints.Csv;
using Pb.Core.Endpoints.Serial;
using Pb.Core.Endpoints.Udp;
using Pb.Core.Modbus;
using Pb.Core.Mqtt;

namespace Pb.Core.Endpoints;

/// <summary>
/// Builds endpoints from configuration. This is the one place that maps a <c>type</c> token to a
/// driver, so an unknown type produces a message that lists what is available instead of a
/// missing-key failure somewhere deeper.
/// </summary>
public static class EndpointFactory
{
    /// <summary>Driver tokens that can be configured.</summary>
    public static readonly string[] SupportedTypes =
    [
        ModbusTcpEndpoint.TypeToken,
        UdpEndpoint.TypeToken,
        SerialEndpoint.TypeToken,
        MqttEndpoint.TypeToken,
        CsvFileSink.TypeToken,
    ];

    /// <summary>Builds the endpoint declared by <paramref name="endpoint"/>.</summary>
    /// <exception cref="ConfigException">The type is unknown or blocked, or its settings are invalid.</exception>
    public static IEndpoint Create(EndpointConfig endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        try
        {
            return endpoint.Type switch
            {
                UdpEndpoint.TypeToken => new UdpEndpoint(endpoint.Id, UdpSettings.FromOptions(endpoint.Options)),
                SerialEndpoint.TypeToken => new SerialEndpoint(endpoint.Id, SerialSettings.FromOptions(endpoint.Options)),
                MqttEndpoint.TypeToken => new MqttEndpoint(endpoint.Id, MqttSettings.FromOptions(endpoint.Options)),
                CsvFileSink.TypeToken => new CsvFileSink(endpoint.Id, CsvSinkSettings.FromOptions(endpoint.Options)),
                _ when ModbusEndpointFactory.Handles(endpoint.Type) => ModbusEndpointFactory.Create(endpoint),
                _ => throw new ConfigException(
                    $"endpoint '{endpoint.Id}': unknown type '{endpoint.Type}'. Available types: {string.Join(", ", SupportedTypes)}.",
                    endpoint.Line),
            };
        }
        catch (YamlException ex)
        {
            // Settings problems are configuration problems; report them with the endpoint they
            // came from rather than as a bare parse error.
            throw new ConfigException($"endpoint '{endpoint.Id}': {ex.Reason}", ex.Line);
        }
    }

    /// <summary>
    /// Builds every endpoint in a configuration, accumulating problems so one run reports all
    /// of them.
    /// </summary>
    /// <exception cref="ConfigException">One or more endpoints could not be created.</exception>
    public static Dictionary<string, IEndpoint> CreateAll(BridgeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        Dictionary<string, IEndpoint> endpoints = new Dictionary<string, IEndpoint>(StringComparer.Ordinal);
        List<ConfigDiagnostic> problems = [];

        foreach (EndpointConfig declared in config.Endpoints)
        {
            try
            {
                endpoints.Add(declared.Id, Create(declared));
            }
            catch (ConfigException ex)
            {
                problems.AddRange(ex.Diagnostics);
            }
        }

        problems.AddRange(ValidateChannels(config, endpoints));

        if (problems.Count > 0)
        {
            foreach (IEndpoint endpoint in endpoints.Values)
            {
                endpoint.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            throw new ConfigException(problems);
        }

        return endpoints;
    }

    /// <summary>
    /// Checks every enabled route's channels against the endpoints that will serve them, so a
    /// channel addressed at the wrong space or in the wrong direction is caught before the
    /// bridge starts moving data.
    /// </summary>
    public static List<ConfigDiagnostic> ValidateChannels(
        BridgeConfig config,
        IReadOnlyDictionary<string, IEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(endpoints);

        List<ConfigDiagnostic> problems = [];

        foreach (RouteConfig route in config.EnabledRoutes)
        {
            Check(route, config.Channel(route.Source), ChannelRole.Source);
            Check(route, config.Channel(route.Sink), ChannelRole.Sink);
        }

        return problems;

        void Check(RouteConfig route, ChannelConfig channel, ChannelRole role)
        {
            if (!endpoints.TryGetValue(channel.Endpoint, out IEndpoint? endpoint))
            {
                // A missing endpoint is already reported by whatever failed to create it.
                return;
            }

            if (!endpoint.Supports(channel.Spec, role, out string? error))
            {
                problems.Add(new ConfigDiagnostic(
                    $"route '{route.Id}' uses channel '{channel.Name}' as a {role.ToString().ToLowerInvariant()} on endpoint '{endpoint.Id}' ({endpoint.Kind}): {error}",
                    channel.Line));
                return;
            }

            bool capable = role == ChannelRole.Source
                ? endpoint is IPollSource or IFrameSource
                : endpoint is IValueSink;

            if (!capable)
            {
                problems.Add(new ConfigDiagnostic(
                    $"route '{route.Id}' uses endpoint '{endpoint.Id}' ({endpoint.Kind}) as a {role.ToString().ToLowerInvariant()}, which it cannot be.",
                    channel.Line));
            }
        }
    }
}
