using Pb.Core.Transforms;
using Xunit;

namespace Pb.Core.Tests;

public sealed class ValueTransformTests
{
    [Fact]
    public void Identity_LeavesValuesUntouched()
    {
        Assert.Equal(7.25, ValueTransform.Identity.Apply(7.25));
        Assert.True(ValueTransform.Identity.IsIdentityScaling);
        Assert.Equal(0.0, ValueTransform.Identity.Deadband);
        Assert.Null(ValueTransform.Identity.Unit);
    }

    [Theory]
    [InlineData(0.0, 2.0, 1.0, 1.0)]
    [InlineData(10.0, 2.0, 1.0, 21.0)]
    [InlineData(-10.0, 0.1, 0.0, -1.0)]
    [InlineData(100.0, 0.1, 0.0, 10.0)]
    public void Apply_ComputesRawTimesScalePlusOffset(double raw, double scale, double offset, double expected)
    {
        Assert.Equal(expected, new ValueTransform(scale, offset).Apply(raw), 12);
    }

    [Theory]
    [InlineData(21.0, 2.0, 1.0, 10.0)]
    [InlineData(-1.0, 0.1, 0.0, -10.0)]
    public void Invert_RecoversTheRawValue(double engineering, double scale, double offset, double expected)
    {
        Assert.Equal(expected, new ValueTransform(scale, offset).Invert(engineering), 12);
    }

    [Fact]
    public void Invert_ZeroScale_Throws()
    {
        ValueTransform transform = new ValueTransform(Scale: 0.0, Offset: 5.0);

        Assert.Equal(5.0, transform.Apply(1234.0));
        Assert.Throws<InvalidOperationException>(() => transform.Invert(5.0));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Constructor_NonFiniteScale_Throws(double scale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ValueTransform(Scale: scale));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void Constructor_NonFiniteOffset_Throws(double offset)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ValueTransform(Offset: offset));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Constructor_InvalidDeadband_Throws(double deadband)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ValueTransform(Deadband: deadband));
    }

    [Fact]
    public void With_PreservesValidationAndOtherMembers()
    {
        ValueTransform original = new ValueTransform(2.0, 1.0, "V", 0.5);

        ValueTransform changed = original with { Deadband = 1.5 };

        Assert.Equal(2.0, changed.Scale);
        Assert.Equal(1.0, changed.Offset);
        Assert.Equal("V", changed.Unit);
        Assert.Equal(1.5, changed.Deadband);
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = original with { Deadband = -1.0 }; });
    }
}
