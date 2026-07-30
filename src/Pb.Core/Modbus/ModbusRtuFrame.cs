namespace Pb.Core.Modbus;

/// <summary>
/// Frame codec for MODBUS serial (RTU) ADUs, per spec/modbus-tcp-subset.md §2.2:
/// slave address (1), PDU, CRC-16 (2, low byte first).
/// </summary>
/// <remarks>
/// This is a pure encoder/decoder. RTU <em>line timing</em> — the 3.5-character inter-frame
/// silence and 1.5-character intra-frame limit — is left unspecified in the spec document, so
/// no Modbus endpoint over a serial line is offered; see
/// <c>ModbusEndpointFactory</c>, which rejects that configuration explicitly. The codec
/// exists because it is what pins down the CRC golden vector.
/// </remarks>
public static class ModbusRtuFrame
{
    /// <summary>Lowest individually addressable slave address.</summary>
    public const byte MinSlaveAddress = 1;

    /// <summary>Highest individually addressable slave address.</summary>
    public const byte MaxSlaveAddress = 247;

    /// <summary>Address that addresses every slave at once.</summary>
    public const byte BroadcastAddress = 0;

    /// <summary>Largest complete RTU ADU: address, largest PDU and CRC.</summary>
    public const int MaxAduSize = 256;

    /// <summary>Builds a complete RTU frame: address, PDU, then the CRC low byte first.</summary>
    public static byte[] Wrap(byte slaveAddress, ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length is 0 or > ModbusFunctions.MaxPduSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pdu),
                pdu.Length,
                $"A PDU is 1..{ModbusFunctions.MaxPduSize} bytes (spec §1).");
        }

        byte[] frame = new byte[1 + pdu.Length + 2];
        frame[0] = slaveAddress;
        pdu.CopyTo(frame.AsSpan(1));
        ModbusCrc16.Append(frame.AsSpan(0, 1 + pdu.Length), frame.AsSpan(1 + pdu.Length));
        return frame;
    }

    /// <summary>Validates the CRC of a complete RTU frame and returns its address and PDU.</summary>
    /// <exception cref="ModbusProtocolException">The frame is too short or its CRC does not match.</exception>
    public static (byte SlaveAddress, ReadOnlyMemory<byte> Pdu) Unwrap(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length < 4)
        {
            throw new ModbusProtocolException(
                $"An RTU frame is at least 4 bytes (address, one PDU byte, CRC) but {frame.Length} were received.");
        }

        if (frame.Length > MaxAduSize)
        {
            throw new ModbusProtocolException(
                $"An RTU frame is at most {MaxAduSize} bytes but {frame.Length} were received (spec §2.2).");
        }

        if (!ModbusCrc16.Check(frame.Span))
        {
            throw new ModbusProtocolException("RTU frame CRC does not match its contents.");
        }

        return (frame.Span[0], frame[1..^2]);
    }

    /// <summary>True when <paramref name="address"/> addresses exactly one slave.</summary>
    public static bool IsUnicastAddress(byte address) => address is >= MinSlaveAddress and <= MaxSlaveAddress;
}
