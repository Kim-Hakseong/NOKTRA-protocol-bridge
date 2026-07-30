using Pb.Core.Channels;
using Xunit;

namespace Pb.Core.Tests;

public sealed class ChannelAddressTests
{
    [Theory]
    [InlineData("holding:0", "holding", 0)]
    [InlineData("holding:107", "holding", 107)]
    [InlineData("  coil : 12 ", "coil", 12)]
    [InlineData("HOLDING:5", "holding", 5)]
    [InlineData("offset:0004", "offset", 4)]
    [InlineData("input-register:65535", "input-register", 65535)]
    [InlineData("byte_offset:1", "byte_offset", 1)]
    public void Parse_AcceptsSpaceColonIndex(string text, string space, int index)
    {
        ChannelAddress address = ChannelAddress.Parse(text);

        Assert.Equal(space, address.Space);
        Assert.Equal(index, address.Index);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("40001")]
    [InlineData("holding")]
    [InlineData(":5")]
    [InlineData("holding:")]
    [InlineData("holding:-1")]
    [InlineData("holding:+1")]
    [InlineData("holding:abc")]
    [InlineData("holding:1.5")]
    [InlineData("hold ing:1")]
    [InlineData("holding:99999999999999")]
    public void TryParse_RejectsMalformedAddressesWithAReason(string text)
    {
        bool ok = ChannelAddress.TryParse(text, out ChannelAddress address, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.NotEqual(string.Empty, error);
        Assert.Equal(default, address);
    }

    [Fact]
    public void TryParse_RejectsNull()
    {
        Assert.False(ChannelAddress.TryParse(null, out _, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Parse_MalformedAddress_ThrowsFormatExceptionCarryingTheReason()
    {
        FormatException ex = Assert.Throws<FormatException>(() => ChannelAddress.Parse("40001"));

        Assert.Contains("40001", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_RoundTripsThroughParse()
    {
        ChannelAddress original = new ChannelAddress("holding", 107);

        Assert.Equal("holding:107", original.ToString());
        Assert.Equal(original, ChannelAddress.Parse(original.ToString()));
    }

    [Fact]
    public void Equality_IsByValue()
    {
        Assert.Equal(new ChannelAddress("holding", 1), new ChannelAddress("holding", 1));
        Assert.NotEqual(new ChannelAddress("holding", 1), new ChannelAddress("input", 1));
        Assert.NotEqual(new ChannelAddress("holding", 1), new ChannelAddress("holding", 2));
    }
}
