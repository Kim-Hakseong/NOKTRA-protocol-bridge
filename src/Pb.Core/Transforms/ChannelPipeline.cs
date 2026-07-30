using Pb.Core.Channels;
using Pb.Core.Time;

namespace Pb.Core.Transforms;

/// <summary>
/// Stateful per-route conversion chain: wire bytes → decode → scale/offset → deadband gate → <see cref="Sample"/>.
/// One instance belongs to exactly one route, because the deadband reference value is
/// per-route state.
/// </summary>
public sealed class ChannelPipeline
{
    private readonly ITimeSource _time;
    private readonly DeadbandFilter _deadband;

    public ChannelPipeline(ChannelSpec spec, ValueTransform transform, ITimeSource time)
    {
        Spec = spec ?? throw new ArgumentNullException(nameof(spec));
        Transform = transform ?? throw new ArgumentNullException(nameof(transform));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _deadband = new DeadbandFilter(transform.Deadband);
    }

    /// <summary>The source channel this pipeline reads.</summary>
    public ChannelSpec Spec { get; }

    /// <summary>The conversion applied to decoded values.</summary>
    public ValueTransform Transform { get; }

    /// <summary>Deadband state, exposed for route statistics.</summary>
    public DeadbandFilter Deadband => _deadband;

    /// <summary>
    /// Decodes and converts <paramref name="raw"/>, then reports whether the deadband lets
    /// the result through. <paramref name="sample"/> is always the converted value, even when
    /// the method returns false, so callers can display suppressed values.
    /// </summary>
    public bool TryProcess(ReadOnlySpan<byte> raw, out Sample sample)
    {
        sample = Convert(raw);
        return _deadband.ShouldSend(sample.Value);
    }

    /// <summary>Decodes and converts <paramref name="raw"/> without touching deadband state.</summary>
    public Sample Convert(ReadOnlySpan<byte> raw)
    {
        double engineering = Transform.Apply(ValueCodec.Decode(raw, Spec.Type, Spec.ByteOrder));
        return new Sample(engineering, _time.UtcNow, SampleQuality.Good, Transform.Unit);
    }

    /// <summary>
    /// Wraps an already-decoded engineering value as a sample and applies the deadband.
    /// Used by sources that hand over numbers rather than bytes.
    /// </summary>
    public bool TryProcessValue(double raw, out Sample sample)
    {
        sample = new Sample(Transform.Apply(raw), _time.UtcNow, SampleQuality.Good, Transform.Unit);
        return _deadband.ShouldSend(sample.Value);
    }

    /// <summary>
    /// Produces a sample marking the source as unreadable. Bad samples bypass the deadband
    /// and do not become the deadband reference, so recovery re-sends the first good value.
    /// </summary>
    public Sample BadSample() => new Sample(double.NaN, _time.UtcNow, SampleQuality.Bad, Transform.Unit);

    /// <summary>Clears deadband state so the next value is forwarded unconditionally.</summary>
    public void Reset() => _deadband.Reset();
}
