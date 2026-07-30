namespace Pb.Core.Channels;

/// <summary>
/// One engineering value produced by a source channel after decoding and transformation.
/// This is the only payload shape that travels from sources to sinks.
/// </summary>
/// <param name="Value">Engineering value (raw wire value after scale and offset).</param>
/// <param name="Timestamp">UTC instant the value was produced, taken from an <see cref="Time.ITimeSource"/>.</param>
/// <param name="Quality">Confidence in <paramref name="Value"/>.</param>
/// <param name="Unit">Optional engineering unit label, carried through for sinks that render it.</param>
public readonly record struct Sample(
    double Value,
    DateTimeOffset Timestamp,
    SampleQuality Quality = SampleQuality.Good,
    string? Unit = null);
