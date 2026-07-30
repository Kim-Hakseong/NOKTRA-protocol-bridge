using Pb.Core.Channels;
using Pb.Core.Configuration;
using Pb.Core.Time;
using Pb.Core.Transforms;

namespace Pb.Core.Routing;

/// <summary>
/// The live state of one route: its conversion pipeline and its counters. A route is read by its
/// source loop and written by its sink drain loop, which are different tasks, so every mutation
/// goes through the lock.
/// </summary>
public sealed class RouteRuntime
{
    private readonly object _sync = new object();
    private readonly ChannelPipeline _pipeline;

    private RouteHealth _health;
    private long _read;
    private long _forwarded;
    private long _suppressed;
    private long _dropped;
    private long _readFailures;
    private long _writeFailures;
    private double? _lastValue;
    private DateTimeOffset? _lastForwardedAt;
    private string? _lastError;

    public RouteRuntime(RouteConfig config, ChannelSpec source, ChannelSpec sink, ITimeSource time)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(time);

        Config = config;
        Source = source;
        Sink = sink;
        _pipeline = new ChannelPipeline(source, config.Transform, time);
        _health = config.Enabled ? RouteHealth.Starting : RouteHealth.Disabled;
    }

    /// <summary>The route as configured.</summary>
    public RouteConfig Config { get; }

    /// <summary>Channel this route reads.</summary>
    public ChannelSpec Source { get; }

    /// <summary>Channel this route writes.</summary>
    public ChannelSpec Sink { get; }

    /// <summary>Route id, for logs.</summary>
    public string Id => Config.Id;

    /// <summary>Current health.</summary>
    public RouteHealth Health
    {
        get
        {
            lock (_sync)
            {
                return _health;
            }
        }
    }

    /// <summary>
    /// Converts wire bytes into a sample and reports whether the deadband lets it through.
    /// Counts the read either way.
    /// </summary>
    public bool TryAccept(ReadOnlySpan<byte> raw, out Sample sample)
    {
        bool forward = _pipeline.TryProcess(raw, out sample);
        Count(sample, forward);
        return forward;
    }

    /// <summary>
    /// Converts an already-decoded raw value into a sample and reports whether the deadband lets
    /// it through.
    /// </summary>
    public bool TryAcceptValue(double raw, out Sample sample)
    {
        bool forward = _pipeline.TryProcessValue(raw, out sample);
        Count(sample, forward);
        return forward;
    }

    /// <summary>Records that a value reached the sink.</summary>
    public void OnForwarded(Sample sample)
    {
        lock (_sync)
        {
            _forwarded++;
            _lastForwardedAt = sample.Timestamp;

            if (_health != RouteHealth.Disabled)
            {
                _health = RouteHealth.Ok;
                _lastError = null;
            }
        }
    }

    /// <summary>Records that the source could not be read.</summary>
    public void OnSourceFailure(string message)
    {
        lock (_sync)
        {
            _readFailures++;
            _lastError = message;

            if (_health != RouteHealth.Disabled)
            {
                _health = RouteHealth.SourceFault;
            }
        }
    }

    /// <summary>Records that the sink could not be written.</summary>
    public void OnSinkFailure(string message)
    {
        lock (_sync)
        {
            _writeFailures++;
            _lastError = message;

            if (_health != RouteHealth.Disabled)
            {
                _health = RouteHealth.SinkFault;
            }
        }
    }

    /// <summary>
    /// Records that a pending write was discarded because the sink queue was full. This is
    /// counted separately from a write failure: the sink is not broken, it is behind.
    /// </summary>
    public void OnDropped(string message)
    {
        lock (_sync)
        {
            _dropped++;
            _lastError = message;
        }
    }

    /// <summary>
    /// Clears the deadband reference after the source endpoint reconnects, so the first value of
    /// the new session is always forwarded rather than compared against a pre-outage value.
    /// </summary>
    public void OnSourceReconnected()
    {
        _pipeline.Reset();
    }

    /// <summary>Takes an immutable snapshot of this route.</summary>
    public RouteStatus Snapshot()
    {
        lock (_sync)
        {
            return new RouteStatus(
                Config.Id,
                Config.Source,
                Config.Sink,
                _health,
                _read,
                _forwarded,
                _suppressed,
                _dropped,
                _readFailures,
                _writeFailures,
                _lastValue,
                Config.Transform.Unit,
                _lastForwardedAt,
                _lastError);
        }
    }

    private void Count(Sample sample, bool forward)
    {
        lock (_sync)
        {
            _read++;
            _lastValue = sample.Value;

            if (!forward)
            {
                _suppressed++;
            }

            if (_health == RouteHealth.SourceFault)
            {
                // A successful read clears a read fault; a pending write fault stays until a write
                // succeeds.
                _health = RouteHealth.Ok;
                _lastError = null;
            }
        }
    }
}
