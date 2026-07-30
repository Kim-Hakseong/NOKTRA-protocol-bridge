using Pb.Core.Configuration;
using Pb.Core.Endpoints;

namespace Pb.Core.Modbus;

/// <summary>Creates Modbus endpoints from configuration, and refuses the ones the spec gate blocks.</summary>
public static class ModbusEndpointFactory
{
    /// <summary>Type tokens this factory recognises, including the ones it deliberately refuses.</summary>
    public static readonly string[] RecognisedTypes = ["modbus_tcp", "modbus_rtu", "modbus_serial"];

    /// <summary>True when <paramref name="type"/> names a Modbus driver.</summary>
    public static bool Handles(string type) => RecognisedTypes.Contains(type, StringComparer.Ordinal);

    /// <summary>
    /// Builds the endpoint declared by <paramref name="endpoint"/>.
    /// </summary>
    /// <exception cref="ConfigException">
    /// The declared Modbus variant is blocked by spec/modbus-tcp-subset.md, or its settings are invalid.
    /// </exception>
    public static IEndpoint Create(EndpointConfig endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        switch (endpoint.Type)
        {
            case ModbusTcpEndpoint.TypeToken:
                return new ModbusTcpEndpoint(endpoint.Id, ModbusTcpSettings.FromOptions(endpoint.Options));

            case "modbus_rtu" or "modbus_serial":
                // spec/modbus-tcp-subset.md §2.2 records RTU framing but marks the 3.5/1.5
                // character line timing as UNSPECIFIED. Guessing it would produce a driver that
                // silently mis-frames on some baud rates, so the whole variant stays blocked.
                throw new ConfigException(
                    $"endpoint '{endpoint.Id}': Modbus over a serial line is not implemented. "
                    + "spec/modbus-tcp-subset.md §2.2 leaves RTU inter-frame line timing UNSPECIFIED, "
                    + "so no modbus-rtu endpoint is offered. Use modbus-tcp, or a Modbus TCP gateway.",
                    endpoint.Line);

            default:
                throw new ConfigException(
                    $"endpoint '{endpoint.Id}': '{endpoint.Type}' is not a Modbus endpoint type.",
                    endpoint.Line);
        }
    }
}
