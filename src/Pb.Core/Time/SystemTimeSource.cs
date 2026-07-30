using System.Diagnostics;

namespace Pb.Core.Time;

/// <summary>Real time source backed by the operating system clock and timer.</summary>
public sealed class SystemTimeSource : ITimeSource
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    /// <summary>Shared instance; the type is stateless apart from its monotonic origin.</summary>
    public static SystemTimeSource Instance { get; } = new SystemTimeSource();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public Task Delay(TimeSpan duration, CancellationToken cancellationToken) => duration <= TimeSpan.Zero
        ? Task.CompletedTask
        : Task.Delay(duration, cancellationToken);
}
