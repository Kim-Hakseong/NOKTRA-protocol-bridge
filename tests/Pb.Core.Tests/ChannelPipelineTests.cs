using Pb.Core.Channels;
using Pb.Core.Time;
using Pb.Core.Transforms;
using Xunit;

namespace Pb.Core.Tests;

public sealed class ChannelPipelineTests
{
    private static ChannelSpec Spec(DataType type = DataType.U16, ByteOrder order = ByteOrder.BigEndian) =>
        new ChannelSpec("level", "plc", ChannelAddress.Parse("holding:0"), type, order);

    [Fact]
    public void Convert_AppliesDecodeThenScaleAndStampsTheInjectedClock()
    {
        ManualTimeSource time = new ManualTimeSource(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero));
        ChannelPipeline pipeline = new ChannelPipeline(Spec(), new ValueTransform(0.1, 0.0, "bar"), time);

        Sample sample = pipeline.Convert([0x00, 0x64]);

        Assert.Equal(10.0, sample.Value, 12);
        Assert.Equal(time.UtcNow, sample.Timestamp);
        Assert.Equal("bar", sample.Unit);
        Assert.Equal(SampleQuality.Good, sample.Quality);
    }

    [Fact]
    public void Convert_DoesNotDisturbDeadbandState()
    {
        ChannelPipeline pipeline = new ChannelPipeline(Spec(), new ValueTransform(Deadband: 5.0), new ManualTimeSource());

        pipeline.Convert([0x00, 0x64]);

        Assert.False(pipeline.Deadband.HasSent);
        Assert.True(pipeline.TryProcess([0x00, 0x64], out _));
    }

    [Fact]
    public void TryProcess_ReturnsConvertedValueEvenWhenSuppressed()
    {
        ChannelPipeline pipeline = new ChannelPipeline(Spec(), new ValueTransform(Deadband: 10.0), new ManualTimeSource());
        Assert.True(pipeline.TryProcess([0x00, 0x00], out _));

        bool forwarded = pipeline.TryProcess([0x00, 0x05], out Sample sample);

        Assert.False(forwarded);
        Assert.Equal(5.0, sample.Value);
    }

    [Fact]
    public void TryProcess_TimestampsAdvanceWithTheClock()
    {
        ManualTimeSource time = new ManualTimeSource();
        ChannelPipeline pipeline = new ChannelPipeline(Spec(), ValueTransform.Identity, time);

        pipeline.TryProcess([0x00, 0x01], out Sample first);
        time.Advance(TimeSpan.FromSeconds(30));
        pipeline.TryProcess([0x00, 0x02], out Sample second);

        Assert.Equal(TimeSpan.FromSeconds(30), second.Timestamp - first.Timestamp);
    }

    [Fact]
    public void TryProcessValue_SkipsDecodingButStillScalesAndGates()
    {
        ChannelPipeline pipeline = new ChannelPipeline(Spec(), new ValueTransform(2.0, 1.0, Deadband: 4.0), new ManualTimeSource());

        Assert.True(pipeline.TryProcessValue(10.0, out Sample first));
        Assert.Equal(21.0, first.Value);

        Assert.False(pipeline.TryProcessValue(11.0, out Sample second));
        Assert.Equal(23.0, second.Value);

        Assert.True(pipeline.TryProcessValue(12.0, out Sample third));
        Assert.Equal(25.0, third.Value);
    }

    [Fact]
    public void BadSample_IsNaNAndDoesNotBecomeTheDeadbandReference()
    {
        ManualTimeSource time = new ManualTimeSource();
        ChannelPipeline pipeline = new ChannelPipeline(Spec(), new ValueTransform(Deadband: 100.0, Unit: "V"), time);
        pipeline.TryProcess([0x00, 0x0A], out _);

        Sample bad = pipeline.BadSample();

        Assert.True(double.IsNaN(bad.Value));
        Assert.Equal(SampleQuality.Bad, bad.Quality);
        Assert.Equal("V", bad.Unit);
        Assert.Equal(time.UtcNow, bad.Timestamp);
        Assert.Equal(10.0, pipeline.Deadband.LastSent);
        Assert.False(pipeline.TryProcess([0x00, 0x0B], out _));
    }

    [Fact]
    public void Reset_ForwardsTheNextValueUnconditionally()
    {
        ChannelPipeline pipeline = new ChannelPipeline(Spec(), new ValueTransform(Deadband: 100.0), new ManualTimeSource());
        pipeline.TryProcess([0x00, 0x0A], out _);
        Assert.False(pipeline.TryProcess([0x00, 0x0B], out _));

        pipeline.Reset();

        Assert.True(pipeline.TryProcess([0x00, 0x0B], out _));
    }

    [Fact]
    public void Pipeline_HonoursTheChannelByteOrder()
    {
        ChannelPipeline pipeline = new ChannelPipeline(
            Spec(DataType.U32, ByteOrder.WordSwappedBigEndian),
            ValueTransform.Identity,
            new ManualTimeSource());

        // Words arrive low-word-first, so 00 00 | 00 01 decodes as 0x0001_0000.
        pipeline.TryProcess([0x00, 0x00, 0x00, 0x01], out Sample sample);

        Assert.Equal(65536.0, sample.Value);
    }

    [Fact]
    public void Constructor_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new ChannelPipeline(null!, ValueTransform.Identity, new ManualTimeSource()));
        Assert.Throws<ArgumentNullException>(() => new ChannelPipeline(Spec(), null!, new ManualTimeSource()));
        Assert.Throws<ArgumentNullException>(() => new ChannelPipeline(Spec(), ValueTransform.Identity, null!));
    }

    [Fact]
    public void SpecReportsWireWidth()
    {
        Assert.Equal(2, Spec().SizeInBytes);
        Assert.Equal(4, Spec(DataType.F32).SizeInBytes);
        Assert.Contains("level", Spec().ToString(), StringComparison.Ordinal);
    }
}
