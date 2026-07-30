namespace Pb.Core.Transforms;

/// <summary>
/// Stateless conversion applied between a source channel and a sink channel:
/// <c>engineering = raw * scale + offset</c>, tagged with an optional unit and paired with a
/// deadband that the routing layer uses to suppress insignificant changes.
/// </summary>
/// <param name="Scale">Multiplier <c>a</c>. May be zero, which pins the output to <paramref name="Offset"/>.</param>
/// <param name="Offset">Addend <c>b</c>.</param>
/// <param name="Unit">Engineering unit label carried onto produced samples.</param>
/// <param name="Deadband">
/// Minimum absolute change in the engineering value required to forward a sample.
/// Zero forwards every sample. Must be finite and non-negative.
/// </param>
public sealed record ValueTransform(
    double Scale = 1.0,
    double Offset = 0.0,
    string? Unit = null,
    double Deadband = 0.0)
{
    private readonly double _scale = Finite(Scale, nameof(Scale));
    private readonly double _offset = Finite(Offset, nameof(Offset));
    private readonly double _deadband = NonNegative(Deadband, nameof(Deadband));

    /// <summary>Pass-through transform: no scaling, no offset, no deadband.</summary>
    public static ValueTransform Identity { get; } = new ValueTransform();

    /// <inheritdoc cref="ValueTransform(double, double, string?, double)"/>
    public double Scale
    {
        get => _scale;
        init => _scale = Finite(value, nameof(Scale));
    }

    /// <inheritdoc cref="ValueTransform(double, double, string?, double)"/>
    public double Offset
    {
        get => _offset;
        init => _offset = Finite(value, nameof(Offset));
    }

    /// <inheritdoc cref="ValueTransform(double, double, string?, double)"/>
    public double Deadband
    {
        get => _deadband;
        init => _deadband = NonNegative(value, nameof(Deadband));
    }

    /// <summary>True when this transform leaves values untouched.</summary>
    public bool IsIdentityScaling => Scale == 1.0 && Offset == 0.0;

    /// <summary>Converts a raw wire value to its engineering value.</summary>
    public double Apply(double raw) => (raw * Scale) + Offset;

    /// <summary>
    /// Converts an engineering value back to a raw wire value, for sinks that write
    /// into a scaled address space.
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="Scale"/> is zero, so the transform is not invertible.</exception>
    public double Invert(double engineering) => Scale != 0.0
        ? (engineering - Offset) / Scale
        : throw new InvalidOperationException("A transform with scale 0 cannot be inverted.");

    private static double Finite(double value, string name) => double.IsFinite(value)
        ? value
        : throw new ArgumentOutOfRangeException(name, value, $"{name} must be a finite number.");

    private static double NonNegative(double value, string name) => double.IsFinite(value) && value >= 0.0
        ? value
        : throw new ArgumentOutOfRangeException(name, value, $"{name} must be finite and non-negative.");
}
