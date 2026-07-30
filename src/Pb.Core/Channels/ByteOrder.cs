namespace Pb.Core.Channels;

/// <summary>
/// Byte and word arrangement of a multi-byte channel value on the wire.
/// Patterns are written for a 4-byte value whose most-significant-first bytes
/// are A B C D, which is the notation industrial gateways use. For 2-byte values
/// only the byte order matters; for 8-byte values the same rules apply per
/// 16-bit word.
/// </summary>
public enum ByteOrder
{
    /// <summary>A B C D — most significant byte first. The Modbus default.</summary>
    BigEndian = 0,

    /// <summary>D C B A — fully reversed, least significant byte first.</summary>
    LittleEndian,

    /// <summary>B A D C — big-endian word order, bytes swapped inside each 16-bit word.</summary>
    ByteSwappedBigEndian,

    /// <summary>C D A B — 16-bit words in reverse order, bytes untouched inside each word.</summary>
    WordSwappedBigEndian,
}
