namespace Pb.Core.Modbus;

/// <summary>
/// CRC-16 used by MODBUS serial framing: initial value <c>0xFFFF</c>, reversed generator
/// <c>0xA001</c>, appended to a frame low byte first.
/// </summary>
/// <remarks>
/// Implemented from spec/modbus-tcp-subset.md §2.2 (MODBUS over Serial Line V1.02 §2.5.1.2,
/// Application Protocol V1.1b3 Appendix B). The 256-entry table is generated on first use
/// from that definition rather than pasted in, so the constant that has to be trusted is
/// the polynomial alone.
/// </remarks>
public static class ModbusCrc16
{
    /// <summary>Reversed representation of the generator polynomial x^16 + x^15 + x^2 + 1.</summary>
    public const ushort Polynomial = 0xA001;

    /// <summary>Initial CRC register value.</summary>
    public const ushort Seed = 0xFFFF;

    private static readonly ushort[] Table = BuildTable();

    /// <summary>Computes the CRC of <paramref name="data"/>.</summary>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = Seed;

        foreach (byte b in data)
        {
            crc = (ushort)((crc >> 8) ^ Table[(byte)(crc ^ b)]);
        }

        return crc;
    }

    /// <summary>
    /// Appends the CRC of <paramref name="frame"/> to <paramref name="destination"/> in
    /// transmission order: low byte first.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than 2 bytes.</exception>
    public static void Append(ReadOnlySpan<byte> frame, Span<byte> destination)
    {
        if (destination.Length < 2)
        {
            throw new ArgumentException("Need 2 bytes for the CRC.", nameof(destination));
        }

        ushort crc = Compute(frame);
        destination[0] = (byte)(crc & 0xFF);
        destination[1] = (byte)(crc >> 8);
    }

    /// <summary>
    /// True when <paramref name="frameWithCrc"/> ends with a CRC matching its own contents.
    /// Running the algorithm over a frame including its CRC bytes yields zero.
    /// </summary>
    public static bool Check(ReadOnlySpan<byte> frameWithCrc) =>
        frameWithCrc.Length >= 3 && Compute(frameWithCrc) == 0;

    private static ushort[] BuildTable()
    {
        ushort[] table = new ushort[256];

        for (int i = 0; i < table.Length; i++)
        {
            ushort value = (ushort)i;

            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0
                    ? (ushort)((value >> 1) ^ Polynomial)
                    : (ushort)(value >> 1);
            }

            table[i] = value;
        }

        return table;
    }
}
