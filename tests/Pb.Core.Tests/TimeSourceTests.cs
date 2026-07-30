using Pb.Core.Time;
using Xunit;

namespace Pb.Core.Tests;

public sealed class TimeSourceTests
{
    [Fact]
    public void ManualTimeSource_StartsStoppedAtItsSeedInstant()
    {
        DateTimeOffset start = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.FromHours(9));
        ManualTimeSource time = new ManualTimeSource(start);

        Assert.Equal(start.ToUniversalTime(), time.UtcNow);
        Assert.Equal(TimeSpan.Zero, time.Elapsed);
        Assert.Equal(time.UtcNow, time.UtcNow);
    }

    [Fact]
    public void Advance_MovesWallClockAndMonotonicCounterTogether()
    {
        ManualTimeSource time = new ManualTimeSource();
        DateTimeOffset before = time.UtcNow;

        time.Advance(TimeSpan.FromMilliseconds(1500));

        Assert.Equal(before + TimeSpan.FromMilliseconds(1500), time.UtcNow);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), time.Elapsed);
    }

    [Fact]
    public void Advance_AccumulatesAcrossCalls()
    {
        ManualTimeSource time = new ManualTimeSource();

        time.Advance(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromSeconds(3), time.Elapsed);
    }

    [Fact]
    public void Advance_RejectsNegativeDelta()
    {
        ManualTimeSource time = new ManualTimeSource();

        Assert.Throws<ArgumentOutOfRangeException>(() => time.Advance(TimeSpan.FromTicks(-1)));
    }

    [Fact]
    public void SetUtcNow_MovesTheWallClockWithoutTouchingElapsed()
    {
        ManualTimeSource time = new ManualTimeSource();
        time.Advance(TimeSpan.FromSeconds(5));

        time.SetUtcNow(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), time.UtcNow);
        Assert.Equal(TimeSpan.FromSeconds(5), time.Elapsed);
    }

    [Fact]
    public void SystemTimeSource_ReportsUtcAndNonDecreasingElapsed()
    {
        ITimeSource time = SystemTimeSource.Instance;

        TimeSpan first = time.Elapsed;
        DateTimeOffset now = time.UtcNow;
        TimeSpan second = time.Elapsed;

        Assert.Equal(TimeSpan.Zero, now.Offset);
        Assert.True(second >= first);
        Assert.True(first >= TimeSpan.Zero);
    }
}
