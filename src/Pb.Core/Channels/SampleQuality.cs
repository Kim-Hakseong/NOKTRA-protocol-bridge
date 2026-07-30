namespace Pb.Core.Channels;

/// <summary>Confidence attached to a <see cref="Sample"/>.</summary>
public enum SampleQuality
{
    /// <summary>Freshly read from a healthy endpoint.</summary>
    Good = 0,

    /// <summary>Last known value, but the endpoint has not answered since.</summary>
    Stale,

    /// <summary>The read failed; <see cref="Sample.Value"/> is not meaningful.</summary>
    Bad,
}
