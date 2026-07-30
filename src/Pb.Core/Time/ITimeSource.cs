namespace Pb.Core.Time;

/// <summary>
/// Abstracts wall-clock time, monotonic time and waiting, so that time-dependent bridge logic
/// (sampling timestamps, poll periods, reconnect backoff) is deterministically testable.
/// Production code must never call <c>DateTime.UtcNow</c>, <c>Stopwatch</c> or
/// <c>Task.Delay</c> directly.
/// </summary>
public interface ITimeSource
{
    /// <summary>Current UTC wall-clock instant, used for sample timestamps.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// Monotonic time elapsed since this source started. Never decreases and is
    /// unaffected by wall-clock adjustments, so it is the correct basis for
    /// periods, timeouts and backoff.
    /// </summary>
    TimeSpan Elapsed { get; }

    /// <summary>
    /// Completes once <paramref name="duration"/> of this source's time has passed. A duration of
    /// zero or less completes immediately.
    /// </summary>
    Task Delay(TimeSpan duration, CancellationToken cancellationToken);
}
