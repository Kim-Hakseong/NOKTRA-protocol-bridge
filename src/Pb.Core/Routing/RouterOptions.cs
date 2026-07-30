namespace Pb.Core.Routing;

/// <summary>
/// Tuning of the routing engine. Defaults suit unattended operation on a small bridge; they are
/// separated from <c>BridgeConfig</c> because they describe how the engine behaves rather than
/// what it is wired to.
/// </summary>
/// <param name="SupervisionInterval">
/// How often each endpoint is checked and given its periodic upkeep, such as an MQTT keep-alive.
/// </param>
/// <param name="InitialReconnectBackoff">Wait before the first reconnect attempt.</param>
/// <param name="MaxReconnectBackoff">Ceiling the backoff doubles up to.</param>
/// <param name="SinkQueueCapacity">
/// Pending writes held per sink endpoint. When the queue is full the oldest pending write is
/// dropped and counted, so a stalled sink slows nothing else and the loss is visible in the
/// route statistics rather than silent.
/// </param>
public sealed record RouterOptions(
    TimeSpan? SupervisionInterval = null,
    TimeSpan? InitialReconnectBackoff = null,
    TimeSpan? MaxReconnectBackoff = null,
    int SinkQueueCapacity = 256)
{
    /// <summary>The defaults.</summary>
    public static RouterOptions Default { get; } = new RouterOptions();

    /// <summary>Effective supervision interval.</summary>
    public TimeSpan EffectiveSupervisionInterval => SupervisionInterval ?? TimeSpan.FromSeconds(1);

    /// <summary>Effective first backoff.</summary>
    public TimeSpan EffectiveInitialReconnectBackoff => InitialReconnectBackoff ?? TimeSpan.FromMilliseconds(500);

    /// <summary>Effective backoff ceiling.</summary>
    public TimeSpan EffectiveMaxReconnectBackoff => MaxReconnectBackoff ?? TimeSpan.FromSeconds(30);

    /// <summary>
    /// Backoff before attempt number <paramref name="attempt"/> (1-based): the initial wait
    /// doubled once per previous failure, capped at the ceiling.
    /// </summary>
    public TimeSpan BackoffFor(int attempt)
    {
        if (attempt < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "Attempt numbers start at 1.");
        }

        TimeSpan initial = EffectiveInitialReconnectBackoff;
        TimeSpan max = EffectiveMaxReconnectBackoff;

        if (initial >= max)
        {
            return max;
        }

        // Doubling in ticks, guarded so a long-running outage cannot overflow the multiplication.
        long ticks = initial.Ticks;
        int doublings = Math.Min(attempt - 1, 40);

        for (int i = 0; i < doublings; i++)
        {
            if (ticks >= max.Ticks / 2)
            {
                return max;
            }

            ticks *= 2;
        }

        return TimeSpan.FromTicks(Math.Min(ticks, max.Ticks));
    }

    /// <summary>Validates the options, so a bad value fails at start-up.</summary>
    public RouterOptions Validated()
    {
        if (EffectiveSupervisionInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(SupervisionInterval), SupervisionInterval, "The supervision interval must be positive.");
        }

        if (EffectiveInitialReconnectBackoff <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialReconnectBackoff), InitialReconnectBackoff, "The initial backoff must be positive.");
        }

        if (EffectiveMaxReconnectBackoff < EffectiveInitialReconnectBackoff)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxReconnectBackoff), MaxReconnectBackoff, "The backoff ceiling cannot be below the initial backoff.");
        }

        return SinkQueueCapacity >= 1
            ? this
            : throw new ArgumentOutOfRangeException(nameof(SinkQueueCapacity), SinkQueueCapacity, "The sink queue must hold at least one pending write.");
    }
}
