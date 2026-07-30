using Pb.Core.Configuration;

namespace Pb.Core.Modbus;

/// <summary>Transport settings of a MODBUS TCP master endpoint.</summary>
/// <param name="Host">Host name or address of the slave / gateway.</param>
/// <param name="Port">TCP port; the registered MODBUS port is 502.</param>
/// <param name="UnitId">Unit identifier placed in the MBAP header.</param>
/// <param name="RequestTimeout">How long one request may wait for its response.</param>
/// <param name="ConnectTimeout">How long one connection attempt may take.</param>
public sealed record ModbusTcpSettings(
    string Host,
    int Port = ModbusTcpFrame.DefaultPort,
    byte UnitId = 1,
    TimeSpan? RequestTimeout = null,
    TimeSpan? ConnectTimeout = null)
{
    /// <summary>Configuration keys a <c>modbus-tcp</c> endpoint accepts.</summary>
    public static readonly string[] KnownKeys = ["host", "port", "unit_id", "timeout_ms", "connect_timeout_ms"];

    /// <summary>Default per-request timeout.</summary>
    public static TimeSpan DefaultRequestTimeout { get; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>Default per-attempt connect timeout.</summary>
    public static TimeSpan DefaultConnectTimeout { get; } = TimeSpan.FromMilliseconds(2000);

    /// <summary>Effective per-request timeout.</summary>
    public TimeSpan EffectiveRequestTimeout => RequestTimeout ?? DefaultRequestTimeout;

    /// <summary>Effective per-attempt connect timeout.</summary>
    public TimeSpan EffectiveConnectTimeout => ConnectTimeout ?? DefaultConnectTimeout;

    /// <summary>
    /// Reads settings from a configuration entry, rejecting unknown or out-of-range values so
    /// that a mistake stops the bridge at start-up instead of at the first poll.
    /// </summary>
    public static ModbusTcpSettings FromOptions(EndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.RejectUnknownKeys("a modbus-tcp endpoint", KnownKeys);

        return new ModbusTcpSettings(
            options.RequireString("host"),
            options.GetRangedInt("port", ModbusTcpFrame.DefaultPort, 1, 65535),
            (byte)options.GetRangedInt("unit_id", 1, 0, ModbusRtuFrame.MaxSlaveAddress),
            TimeSpan.FromMilliseconds(options.GetPositiveInt("timeout_ms", (int)DefaultRequestTimeout.TotalMilliseconds)),
            TimeSpan.FromMilliseconds(options.GetPositiveInt("connect_timeout_ms", (int)DefaultConnectTimeout.TotalMilliseconds)));
    }

    public override string ToString() => $"{Host}:{Port} unit {UnitId}";
}
