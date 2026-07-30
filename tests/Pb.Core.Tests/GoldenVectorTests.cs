using Pb.Core.Channels;
using Pb.Core.Time;
using Pb.Core.Transforms;
using Xunit;

namespace Pb.Core.Tests;

/// <summary>
/// The pinned transform and deadband golden vectors. These assertions are
/// contractual: they must never be relaxed, only added to.
/// </summary>
public sealed class GoldenVectorTests
{
    [Fact]
    public void Scale_RawTenWithA2B1_Yields21()
    {
        ValueTransform transform = new ValueTransform(Scale: 2.0, Offset: 1.0);

        Assert.Equal(21.0, transform.Apply(10.0));
    }

    [Fact]
    public void Decode_SignedRegisterFFF6_YieldsMinusTen()
    {
        byte[] raw = [0xFF, 0xF6];

        Assert.Equal(-10.0, ValueCodec.Decode(raw, DataType.S16, ByteOrder.BigEndian));
    }

    [Fact]
    public void Pipeline_SignedRegisterFFF6ScaledByTenth_YieldsMinusOne()
    {
        ChannelSpec spec = new ChannelSpec("t", "e", ChannelAddress.Parse("holding:0"), DataType.S16);
        ChannelPipeline pipeline = new ChannelPipeline(spec, new ValueTransform(Scale: 0.1), new ManualTimeSource());

        bool forwarded = pipeline.TryProcess([0xFF, 0xF6], out Sample sample);

        Assert.True(forwarded);
        Assert.Equal(-1.0, sample.Value, 12);
    }

    [Fact]
    public void Deadband_HalfBandOverFourValues_ForwardsTwo()
    {
        DeadbandFilter filter = new DeadbandFilter(0.5);
        double[] sequence = [1.0, 1.3, 1.6, 1.4];

        double[] forwarded = sequence.Where(filter.ShouldSend).ToArray();

        Assert.Equal([1.0, 1.6], forwarded);
    }
}
