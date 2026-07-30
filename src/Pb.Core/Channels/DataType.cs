namespace Pb.Core.Channels;

/// <summary>
/// Wire data type of a channel. Every type decodes to and encodes from
/// <see cref="double"/>, which is the single engineering-value representation
/// carried through the bridge.
/// </summary>
public enum DataType
{
    /// <summary>Single byte, zero = false, non-zero = true. Decodes to 0.0 or 1.0.</summary>
    Bool = 0,

    /// <summary>Unsigned 16-bit integer (one Modbus register).</summary>
    U16,

    /// <summary>Signed 16-bit integer, two's complement.</summary>
    S16,

    /// <summary>Unsigned 32-bit integer (two Modbus registers).</summary>
    U32,

    /// <summary>Signed 32-bit integer, two's complement.</summary>
    S32,

    /// <summary>Unsigned 64-bit integer. Values above 2^53 lose precision as a double.</summary>
    U64,

    /// <summary>Signed 64-bit integer. Magnitudes above 2^53 lose precision as a double.</summary>
    S64,

    /// <summary>IEEE 754 single-precision float.</summary>
    F32,

    /// <summary>IEEE 754 double-precision float.</summary>
    F64,
}
