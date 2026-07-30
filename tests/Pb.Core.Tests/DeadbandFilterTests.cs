using Pb.Core.Transforms;
using Xunit;

namespace Pb.Core.Tests;

public sealed class DeadbandFilterTests
{
    [Fact]
    public void FirstValue_AlwaysForwarded()
    {
        DeadbandFilter filter = new DeadbandFilter(100.0);

        Assert.True(filter.ShouldSend(0.0));
        Assert.Equal(0.0, filter.LastSent);
        Assert.True(filter.HasSent);
    }

    [Fact]
    public void ZeroBand_ForwardsEveryValueIncludingRepeats()
    {
        DeadbandFilter filter = new DeadbandFilter(0.0);

        Assert.True(filter.ShouldSend(1.0));
        Assert.True(filter.ShouldSend(1.0));
        Assert.True(filter.ShouldSend(0.999));
    }

    [Fact]
    public void ChangeExactlyEqualToBand_IsForwarded()
    {
        DeadbandFilter filter = new DeadbandFilter(0.5);
        filter.ShouldSend(1.0);

        Assert.True(filter.ShouldSend(1.5));
    }

    [Fact]
    public void ReferenceIsLastForwardedValue_NotLastSeenValue()
    {
        DeadbandFilter filter = new DeadbandFilter(1.0);
        filter.ShouldSend(0.0);

        Assert.False(filter.ShouldSend(0.9));
        Assert.False(filter.ShouldSend(0.95));
        Assert.True(filter.ShouldSend(1.0));
        Assert.Equal(1.0, filter.LastSent);
    }

    [Fact]
    public void DriftBelowBand_NeverForwardsAndNeverAccumulates()
    {
        DeadbandFilter filter = new DeadbandFilter(1.0);
        filter.ShouldSend(0.0);

        int forwarded = Enumerable.Range(1, 10)
            .Select(step => 0.09 * step)
            .Count(filter.ShouldSend);

        Assert.Equal(0, forwarded);
        Assert.Equal(0.0, filter.LastSent);
    }

    [Fact]
    public void NegativeDirectionUsesAbsoluteChange()
    {
        DeadbandFilter filter = new DeadbandFilter(2.0);
        filter.ShouldSend(10.0);

        Assert.False(filter.ShouldSend(8.5));
        Assert.True(filter.ShouldSend(8.0));
    }

    [Fact]
    public void NonFiniteValue_AlwaysForwardedAndRecovers()
    {
        DeadbandFilter filter = new DeadbandFilter(5.0);
        filter.ShouldSend(1.0);

        Assert.True(filter.ShouldSend(double.NaN));
        Assert.True(filter.ShouldSend(1.0));
        Assert.False(filter.ShouldSend(2.0));
    }

    [Fact]
    public void Reset_MakesTheNextValueUnconditional()
    {
        DeadbandFilter filter = new DeadbandFilter(5.0);
        filter.ShouldSend(1.0);
        Assert.False(filter.ShouldSend(2.0));

        filter.Reset();

        Assert.False(filter.HasSent);
        Assert.True(filter.ShouldSend(2.0));
    }

    [Theory]
    [InlineData(-0.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Constructor_InvalidBand_Throws(double band)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeadbandFilter(band));
    }
}
