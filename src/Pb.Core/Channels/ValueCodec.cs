using System.Buffers.Binary;

namespace Pb.Core.Channels;

/// <summary>
/// Converts between wire bytes and the bridge's single engineering-value type,
/// <see cref="double"/>. All four <see cref="ByteOrder"/> arrangements are handled by
/// normalising the buffer to most-significant-byte-first and then reading it big-endian.
/// </summary>
public static class ValueCodec
{
    /// <summary>Largest wire value size this codec handles, in bytes.</summary>
    public const int MaxValueSize = 8;

    /// <summary>Wire size of <paramref name="type"/> in bytes.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> is not a known data type.</exception>
    public static int SizeOf(DataType type) => type switch
    {
        DataType.Bool => 1,
        DataType.U16 or DataType.S16 => 2,
        DataType.U32 or DataType.S32 or DataType.F32 => 4,
        DataType.U64 or DataType.S64 or DataType.F64 => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown data type."),
    };

    /// <summary>
    /// Decodes the first <see cref="SizeOf"/> bytes of <paramref name="raw"/> as
    /// <paramref name="type"/> laid out in <paramref name="order"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="raw"/> is shorter than the wire size.</exception>
    public static double Decode(ReadOnlySpan<byte> raw, DataType type, ByteOrder order = ByteOrder.BigEndian)
    {
        int size = SizeOf(type);
        if (raw.Length < size)
        {
            throw new ArgumentException(
                $"Need {size} byte(s) to decode {type} but got {raw.Length}.",
                nameof(raw));
        }

        Span<byte> canonical = stackalloc byte[MaxValueSize];
        Span<byte> value = canonical[..size];
        Normalize(raw[..size], order, value);

        return type switch
        {
            DataType.Bool => value[0] != 0 ? 1.0 : 0.0,
            DataType.U16 => BinaryPrimitives.ReadUInt16BigEndian(value),
            DataType.S16 => BinaryPrimitives.ReadInt16BigEndian(value),
            DataType.U32 => BinaryPrimitives.ReadUInt32BigEndian(value),
            DataType.S32 => BinaryPrimitives.ReadInt32BigEndian(value),
            DataType.U64 => BinaryPrimitives.ReadUInt64BigEndian(value),
            DataType.S64 => BinaryPrimitives.ReadInt64BigEndian(value),
            DataType.F32 => BinaryPrimitives.ReadSingleBigEndian(value),
            DataType.F64 => BinaryPrimitives.ReadDoubleBigEndian(value),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown data type."),
        };
    }

    /// <summary>
    /// Encodes <paramref name="value"/> into the first <see cref="SizeOf"/> bytes of
    /// <paramref name="destination"/>. Integer targets saturate at their range limits and
    /// map NaN to zero, so a bad source value degrades a single sample instead of
    /// faulting the bridge.
    /// </summary>
    /// <returns>Number of bytes written.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than the wire size.</exception>
    public static int Encode(double value, DataType type, ByteOrder order, Span<byte> destination)
    {
        int size = SizeOf(type);
        if (destination.Length < size)
        {
            throw new ArgumentException(
                $"Need {size} byte(s) to encode {type} but got {destination.Length}.",
                nameof(destination));
        }

        Span<byte> canonical = stackalloc byte[MaxValueSize];
        Span<byte> encoded = canonical[..size];

        switch (type)
        {
            case DataType.Bool:
                encoded[0] = value is not 0.0 && !double.IsNaN(value) ? (byte)1 : (byte)0;
                break;
            case DataType.U16:
                BinaryPrimitives.WriteUInt16BigEndian(encoded, (ushort)ToUnsigned(value, ushort.MaxValue));
                break;
            case DataType.S16:
                BinaryPrimitives.WriteInt16BigEndian(encoded, (short)ToSigned(value, short.MinValue, short.MaxValue));
                break;
            case DataType.U32:
                BinaryPrimitives.WriteUInt32BigEndian(encoded, (uint)ToUnsigned(value, uint.MaxValue));
                break;
            case DataType.S32:
                BinaryPrimitives.WriteInt32BigEndian(encoded, (int)ToSigned(value, int.MinValue, int.MaxValue));
                break;
            case DataType.U64:
                BinaryPrimitives.WriteUInt64BigEndian(encoded, ToUnsigned(value, ulong.MaxValue));
                break;
            case DataType.S64:
                BinaryPrimitives.WriteInt64BigEndian(encoded, ToSigned(value, long.MinValue, long.MaxValue));
                break;
            case DataType.F32:
                BinaryPrimitives.WriteSingleBigEndian(encoded, (float)value);
                break;
            case DataType.F64:
                BinaryPrimitives.WriteDoubleBigEndian(encoded, value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown data type.");
        }

        Normalize(encoded, order, destination[..size]);
        return size;
    }

    /// <summary>
    /// Rearranges <paramref name="source"/> into <paramref name="destination"/> so that a
    /// buffer laid out in <paramref name="order"/> becomes most-significant-byte-first,
    /// and vice versa. Every supported arrangement is its own inverse, so encode and
    /// decode share this one routine.
    /// </summary>
    private static void Normalize(ReadOnlySpan<byte> source, ByteOrder order, Span<byte> destination)
    {
        source.CopyTo(destination);

        if (destination.Length < 2)
        {
            return;
        }

        switch (order)
        {
            case ByteOrder.BigEndian:
                break;
            case ByteOrder.LittleEndian:
                destination.Reverse();
                break;
            case ByteOrder.ByteSwappedBigEndian:
                SwapBytesWithinWords(destination);
                break;
            case ByteOrder.WordSwappedBigEndian:
                ReverseWordOrder(destination);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(order), order, "Unknown byte order.");
        }
    }

    private static void SwapBytesWithinWords(Span<byte> buffer)
    {
        for (int i = 0; i + 1 < buffer.Length; i += 2)
        {
            (buffer[i], buffer[i + 1]) = (buffer[i + 1], buffer[i]);
        }
    }

    private static void ReverseWordOrder(Span<byte> buffer)
    {
        int words = buffer.Length / 2;
        for (int i = 0; i < words / 2; i++)
        {
            int left = i * 2;
            int right = (words - 1 - i) * 2;
            (buffer[left], buffer[right]) = (buffer[right], buffer[left]);
            (buffer[left + 1], buffer[right + 1]) = (buffer[right + 1], buffer[left + 1]);
        }
    }

    private static long ToSigned(double value, long min, long max)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded <= min)
        {
            return min;
        }

        return rounded >= max ? max : (long)rounded;
    }

    private static ulong ToUnsigned(double value, ulong max)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded <= 0.0)
        {
            return 0;
        }

        return rounded >= max ? max : (ulong)rounded;
    }
}
