using Pb.Core.Channels;
using Xunit;

namespace Pb.Core.Tests;

public sealed class ValueCodecTests
{
    [Theory]
    [InlineData(DataType.Bool, 1)]
    [InlineData(DataType.U16, 2)]
    [InlineData(DataType.S16, 2)]
    [InlineData(DataType.U32, 4)]
    [InlineData(DataType.S32, 4)]
    [InlineData(DataType.F32, 4)]
    [InlineData(DataType.U64, 8)]
    [InlineData(DataType.S64, 8)]
    [InlineData(DataType.F64, 8)]
    public void SizeOf_KnownTypes_MatchesWireWidth(DataType type, int expected)
    {
        Assert.Equal(expected, ValueCodec.SizeOf(type));
    }

    [Fact]
    public void SizeOf_UnknownType_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ValueCodec.SizeOf((DataType)99));
    }

    [Theory]
    [InlineData(ByteOrder.BigEndian, 0x11, 0x22, 0x33, 0x44)]
    [InlineData(ByteOrder.LittleEndian, 0x44, 0x33, 0x22, 0x11)]
    [InlineData(ByteOrder.ByteSwappedBigEndian, 0x22, 0x11, 0x44, 0x33)]
    [InlineData(ByteOrder.WordSwappedBigEndian, 0x33, 0x44, 0x11, 0x22)]
    public void Decode_U32_HonoursEveryByteArrangement(ByteOrder order, byte b0, byte b1, byte b2, byte b3)
    {
        byte[] raw = [b0, b1, b2, b3];

        Assert.Equal((double)0x11223344U, ValueCodec.Decode(raw, DataType.U32, order));
    }

    [Theory]
    [InlineData(ByteOrder.BigEndian)]
    [InlineData(ByteOrder.LittleEndian)]
    [InlineData(ByteOrder.ByteSwappedBigEndian)]
    [InlineData(ByteOrder.WordSwappedBigEndian)]
    public void Decode_SixteenBitWordOrdersCollapseToTwoDistinctLayouts(ByteOrder order)
    {
        byte[] raw = [0x12, 0x34];
        bool bytesSwapped = order is ByteOrder.LittleEndian or ByteOrder.ByteSwappedBigEndian;

        double actual = ValueCodec.Decode(raw, DataType.U16, order);

        Assert.Equal(bytesSwapped ? (double)0x3412 : 0x1234, actual);
    }

    [Theory]
    [InlineData(DataType.U16, 0.0)]
    [InlineData(DataType.U16, 65535.0)]
    [InlineData(DataType.S16, -32768.0)]
    [InlineData(DataType.S16, 32767.0)]
    [InlineData(DataType.U32, 4294967295.0)]
    [InlineData(DataType.S32, -2147483648.0)]
    [InlineData(DataType.F32, 1.5)]
    [InlineData(DataType.F64, -1234.5678)]
    [InlineData(DataType.Bool, 1.0)]
    [InlineData(DataType.Bool, 0.0)]
    [InlineData(DataType.S64, -9007199254740992.0)]
    [InlineData(DataType.U64, 9007199254740992.0)]
    public void EncodeThenDecode_RoundTripsInEveryByteOrder(DataType type, double value)
    {
        foreach (ByteOrder order in Enum.GetValues<ByteOrder>())
        {
            byte[] buffer = new byte[ValueCodec.SizeOf(type)];

            int written = ValueCodec.Encode(value, type, order, buffer);

            Assert.Equal(buffer.Length, written);
            Assert.Equal(value, ValueCodec.Decode(buffer, type, order));
        }
    }

    [Fact]
    public void Encode_F32_PreservesSinglePrecisionValueExactly()
    {
        byte[] buffer = new byte[4];

        ValueCodec.Encode(3.5f, DataType.F32, ByteOrder.BigEndian, buffer);

        Assert.Equal([0x40, 0x60, 0x00, 0x00], buffer);
    }

    [Theory]
    [InlineData(DataType.U16, 70000.0, 65535.0)]
    [InlineData(DataType.U16, -5.0, 0.0)]
    [InlineData(DataType.S16, 40000.0, 32767.0)]
    [InlineData(DataType.S16, -40000.0, -32768.0)]
    [InlineData(DataType.S32, 5e18, 2147483647.0)]
    public void Encode_OutOfRangeInteger_SaturatesInsteadOfWrapping(DataType type, double value, double expected)
    {
        byte[] buffer = new byte[ValueCodec.SizeOf(type)];

        ValueCodec.Encode(value, type, ByteOrder.BigEndian, buffer);

        Assert.Equal(expected, ValueCodec.Decode(buffer, type, ByteOrder.BigEndian));
    }

    [Theory]
    [InlineData(DataType.U16)]
    [InlineData(DataType.S32)]
    [InlineData(DataType.S64)]
    [InlineData(DataType.Bool)]
    public void Encode_NaNToIntegerTarget_BecomesZero(DataType type)
    {
        byte[] buffer = new byte[ValueCodec.SizeOf(type)];

        ValueCodec.Encode(double.NaN, type, ByteOrder.BigEndian, buffer);

        Assert.Equal(0.0, ValueCodec.Decode(buffer, type, ByteOrder.BigEndian));
    }

    [Theory]
    [InlineData(1.5, 2.0)]
    [InlineData(2.5, 3.0)]
    [InlineData(-1.5, -2.0)]
    [InlineData(1.4, 1.0)]
    public void Encode_FractionalToIntegerTarget_RoundsAwayFromZero(double value, double expected)
    {
        byte[] buffer = new byte[2];

        ValueCodec.Encode(value, DataType.S16, ByteOrder.BigEndian, buffer);

        Assert.Equal(expected, ValueCodec.Decode(buffer, DataType.S16, ByteOrder.BigEndian));
    }

    [Fact]
    public void Encode_MaxUInt64_SaturatesWithoutOverflow()
    {
        byte[] buffer = new byte[8];

        ValueCodec.Encode(double.MaxValue, DataType.U64, ByteOrder.BigEndian, buffer);

        Assert.Equal([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], buffer);
    }

    [Fact]
    public void Decode_BufferShorterThanType_Throws()
    {
        byte[] raw = [0x01];

        Assert.Throws<ArgumentException>(() => ValueCodec.Decode(raw, DataType.U32, ByteOrder.BigEndian));
    }

    [Fact]
    public void Encode_BufferShorterThanType_Throws()
    {
        byte[] buffer = new byte[3];

        Assert.Throws<ArgumentException>(() => ValueCodec.Encode(1.0, DataType.U32, ByteOrder.BigEndian, buffer));
    }

    [Fact]
    public void Decode_UnknownByteOrder_Throws()
    {
        byte[] raw = [0x00, 0x01];

        Assert.Throws<ArgumentOutOfRangeException>(() => ValueCodec.Decode(raw, DataType.U16, (ByteOrder)42));
    }

    [Fact]
    public void Decode_BoolTreatsAnyNonZeroByteAsTrue()
    {
        Assert.Equal(1.0, ValueCodec.Decode([0x7F], DataType.Bool));
        Assert.Equal(0.0, ValueCodec.Decode([0x00], DataType.Bool));
    }

    [Fact]
    public void Decode_IgnoresBytesBeyondTheWireWidth()
    {
        byte[] raw = [0x00, 0x2A, 0xDE, 0xAD];

        Assert.Equal(42.0, ValueCodec.Decode(raw, DataType.U16, ByteOrder.BigEndian));
    }

    [Fact]
    public void Decode_EightByteWordSwap_ReversesWordsNotBytes()
    {
        byte[] raw = [0x77, 0x88, 0x55, 0x66, 0x33, 0x44, 0x11, 0x22];

        double actual = ValueCodec.Decode(raw, DataType.U64, ByteOrder.WordSwappedBigEndian);

        Assert.Equal((double)0x1122334455667788UL, actual);
    }
}
