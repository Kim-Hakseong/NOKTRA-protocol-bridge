namespace Pb.Core.Transforms;

/// <summary>
/// Suppresses samples whose engineering value has not moved far enough since the last
/// forwarded one: a value passes when <c>|new - last_sent| &gt;= band</c>. The first value
/// after construction or <see cref="Reset"/> always passes, so a sink never starts empty.
/// </summary>
public sealed class DeadbandFilter
{
    private readonly double _band;
    private bool _hasSent;
    private double _lastSent;

    /// <param name="band">Minimum absolute change to forward. Zero forwards every sample.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="band"/> is negative or not finite.</exception>
    public DeadbandFilter(double band)
    {
        if (!double.IsFinite(band) || band < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(band), band, "Deadband must be finite and non-negative.");
        }

        _band = band;
    }

    /// <summary>The configured band.</summary>
    public double Band => _band;

    /// <summary>True once at least one value has been forwarded.</summary>
    public bool HasSent => _hasSent;

    /// <summary>Last forwarded value; meaningless while <see cref="HasSent"/> is false.</summary>
    public double LastSent => _lastSent;

    /// <summary>
    /// Decides whether <paramref name="value"/> should be forwarded and, when it should,
    /// records it as the new reference. Non-finite values always pass, because a sink must
    /// learn that a source has gone invalid.
    /// </summary>
    public bool ShouldSend(double value)
    {
        if (!_hasSent || !double.IsFinite(value) || !double.IsFinite(_lastSent))
        {
            Accept(value);
            return true;
        }

        if (Math.Abs(value - _lastSent) >= _band)
        {
            Accept(value);
            return true;
        }

        return false;
    }

    /// <summary>Forgets the reference value, so the next sample is forwarded unconditionally.</summary>
    public void Reset()
    {
        _hasSent = false;
        _lastSent = 0.0;
    }

    private void Accept(double value)
    {
        _hasSent = true;
        _lastSent = value;
    }
}
