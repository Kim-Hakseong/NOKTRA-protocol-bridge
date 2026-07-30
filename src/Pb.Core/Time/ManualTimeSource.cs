namespace Pb.Core.Time;

/// <summary>
/// Test time source. Time only moves when <see cref="Advance"/>, <see cref="AdvanceToNextDelay"/>
/// or <see cref="SetUtcNow"/> is called, and <see cref="Delay"/> completes only when time passes
/// its due point. Together these keep time-dependent tests free of real sleeps.
/// </summary>
public sealed class ManualTimeSource : ITimeSource
{
    private readonly object _sync = new object();
    private readonly List<PendingDelay> _pending = [];

    private TaskCompletionSource _delayRegistered = NewSignal();
    private DateTimeOffset _utcNow;
    private TimeSpan _elapsed;

    public ManualTimeSource()
        : this(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
    {
    }

    public ManualTimeSource(DateTimeOffset start)
    {
        _utcNow = start.ToUniversalTime();
        _elapsed = TimeSpan.Zero;
    }

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_sync)
            {
                return _utcNow;
            }
        }
    }

    public TimeSpan Elapsed
    {
        get
        {
            lock (_sync)
            {
                return _elapsed;
            }
        }
    }

    /// <summary>Number of delays currently waiting for time to pass.</summary>
    public int PendingDelayCount
    {
        get
        {
            lock (_sync)
            {
                return _pending.Count;
            }
        }
    }

    public Task Delay(TimeSpan duration, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        if (duration <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        PendingDelay pending;
        TaskCompletionSource registered;

        lock (_sync)
        {
            pending = new PendingDelay(_elapsed + duration, NewSignal());
            _pending.Add(pending);
            registered = _delayRegistered;
            _delayRegistered = NewSignal();
        }

        // A cancelled delay must not keep a waiter parked, and must not linger in the queue.
        CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            lock (_sync)
            {
                _pending.Remove(pending);
            }

            pending.Completion.TrySetCanceled(cancellationToken);
        });

        registered.TrySetResult();

        return Await(pending, registration);

        static async Task Await(PendingDelay pending, CancellationTokenRegistration registration)
        {
            using (registration)
            {
                await pending.Completion.Task.ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Waits until at least <paramref name="count"/> delays are registered, so a test can be sure
    /// the code under test has parked on its timer before time is moved.
    /// </summary>
    public async Task WaitForPendingDelaysAsync(int count, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task registered;

            lock (_sync)
            {
                if (_pending.Count >= count)
                {
                    return;
                }

                registered = _delayRegistered.Task;
            }

            await registered.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Moves both the wall clock and the monotonic counter forward, completing any delay that comes due.</summary>
    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), delta, "Time cannot move backwards.");
        }

        List<PendingDelay> due;

        lock (_sync)
        {
            _utcNow += delta;
            _elapsed += delta;
            due = TakeDueDelays();
        }

        Complete(due);
    }

    /// <summary>
    /// Advances exactly to the earliest pending delay's due point. Returns false when nothing is
    /// waiting, which lets a test loop "advance until the work is done" without guessing durations.
    /// </summary>
    public bool AdvanceToNextDelay()
    {
        List<PendingDelay> due;

        lock (_sync)
        {
            if (_pending.Count == 0)
            {
                return false;
            }

            TimeSpan earliest = _pending.Min(static p => p.Due);
            TimeSpan delta = earliest - _elapsed;

            if (delta > TimeSpan.Zero)
            {
                _utcNow += delta;
                _elapsed = earliest;
            }

            due = TakeDueDelays();
        }

        Complete(due);
        return true;
    }

    /// <summary>Moves the wall clock only, simulating an external clock adjustment.</summary>
    public void SetUtcNow(DateTimeOffset value)
    {
        lock (_sync)
        {
            _utcNow = value.ToUniversalTime();
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Complete(List<PendingDelay> due)
    {
        foreach (PendingDelay delay in due)
        {
            delay.Completion.TrySetResult();
        }
    }

    /// <summary>Removes and returns every delay whose due point has been reached. Call under the lock.</summary>
    private List<PendingDelay> TakeDueDelays()
    {
        List<PendingDelay> due = [];

        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            if (_pending[i].Due <= _elapsed)
            {
                due.Add(_pending[i]);
                _pending.RemoveAt(i);
            }
        }

        return due;
    }

    private sealed record PendingDelay(TimeSpan Due, TaskCompletionSource Completion);
}
