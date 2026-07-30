using System.Threading.Channels;
using Pb.Core.Channels;
using Pb.Core.Configuration;
using Pb.Core.Diagnostics;
using Pb.Core.Endpoints;
using Pb.Core.Time;

namespace Pb.Core.Routing;

/// <summary>
/// The routing engine: source events flow through each route's transform pipe into a per-endpoint
/// sink queue.
/// </summary>
/// <remarks>
/// Every loop is independent and every failure is contained in the state of the route or endpoint
/// it belongs to, which is what lets a dead endpoint leave the rest of the bridge running
/// running. Sinks are drained one queue per endpoint, so writes to one endpoint are serialised
/// and ordered while a slow endpoint never blocks another route's polling.
/// </remarks>
public sealed class BridgeRouter : IAsyncDisposable
{
    private readonly BridgeConfig _config;
    private readonly RouterOptions _options;
    private readonly ITimeSource _time;
    private readonly IBridgeLog _log;
    private readonly List<EndpointSupervisor> _supervisors = [];
    private readonly Dictionary<string, EndpointSupervisor> _supervisorsById;
    private readonly List<RouteRuntime> _routes = [];
    private readonly Dictionary<string, SinkQueue> _sinkQueues;
    private readonly TimeSpan _startedAt;

    private CancellationTokenSource? _shutdown;
    private Task? _running;
    private bool _disposed;

    /// <param name="config">A validated configuration.</param>
    /// <param name="endpoints">Endpoints built for that configuration, keyed by id.</param>
    /// <param name="time">Time source; injected so periods and backoff are testable.</param>
    /// <param name="options">Engine tuning.</param>
    /// <param name="log">Where progress and failures are reported.</param>
    public BridgeRouter(
        BridgeConfig config,
        IReadOnlyDictionary<string, IEndpoint> endpoints,
        ITimeSource? time = null,
        RouterOptions? options = null,
        IBridgeLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(endpoints);

        _config = config;
        _options = (options ?? RouterOptions.Default).Validated();
        _time = time ?? SystemTimeSource.Instance;
        _log = log ?? NullBridgeLog.Instance;
        _startedAt = _time.Elapsed;

        List<ConfigDiagnostic> problems = EndpointFactory.ValidateChannels(config, endpoints);

        if (problems.Count > 0)
        {
            throw new ConfigException(problems);
        }

        _supervisorsById = new Dictionary<string, EndpointSupervisor>(StringComparer.Ordinal);

        foreach (EndpointConfig declared in config.Endpoints)
        {
            if (!endpoints.TryGetValue(declared.Id, out IEndpoint? endpoint))
            {
                throw new ConfigException($"endpoint '{declared.Id}' was declared but not created.", declared.Line);
            }

            EndpointSupervisor supervisor = new EndpointSupervisor(endpoint, _options, _time, _log);
            _supervisors.Add(supervisor);
            _supervisorsById.Add(declared.Id, supervisor);
        }

        _sinkQueues = new Dictionary<string, SinkQueue>(StringComparer.Ordinal);

        foreach (RouteConfig route in config.Routes)
        {
            ChannelSpec source = config.Channel(route.Source).Spec;
            ChannelSpec sink = config.Channel(route.Sink).Spec;
            RouteRuntime runtime = new RouteRuntime(route, source, sink, _time);
            _routes.Add(runtime);

            if (!route.Enabled)
            {
                continue;
            }

            Supervisor(source.Endpoint).AddSourceRoute(runtime);

            if (!_sinkQueues.ContainsKey(sink.Endpoint))
            {
                _sinkQueues.Add(sink.Endpoint, new SinkQueue(Supervisor(sink.Endpoint), _options, _log));
            }
        }
    }

    /// <summary>Live route state, in configuration order.</summary>
    public IReadOnlyList<RouteRuntime> Routes => _routes;

    /// <summary>Endpoint supervisors, in configuration order.</summary>
    public IReadOnlyList<EndpointSupervisor> Endpoints => _supervisors;

    /// <summary>True while <see cref="RunAsync"/> is executing.</summary>
    public bool IsRunning => _running is { IsCompleted: false };

    /// <summary>
    /// Runs every endpoint supervisor, route loop and sink drain until
    /// <paramref name="cancellationToken"/> is cancelled. Returns normally on cancellation, which
    /// is the ordinary way a bridge stops.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
        {
            throw new InvalidOperationException("This router is already running.");
        }

        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = _shutdown.Token;

        _log.Info(_config.Name, $"starting {_config.EnabledRoutes.Count()} route(s) over {_supervisors.Count} endpoint(s).");

        List<Task> loops = [];

        foreach (EndpointSupervisor supervisor in _supervisors)
        {
            loops.Add(Guarded(supervisor.Id, () => supervisor.RunAsync(token)));
        }

        foreach (SinkQueue queue in _sinkQueues.Values)
        {
            loops.Add(Guarded(queue.EndpointId, () => queue.DrainAsync(token)));
        }

        foreach (RouteRuntime route in _routes.Where(static r => r.Config.Enabled))
        {
            loops.Add(Guarded(route.Id, () => RunRouteAsync(route, token)));
        }

        _running = Task.WhenAll(loops);

        try
        {
            await _running.ConfigureAwait(false);
        }
        finally
        {
            foreach (SinkQueue queue in _sinkQueues.Values)
            {
                queue.Complete();
            }

            _log.Info(_config.Name, "stopped.");
        }
    }

    /// <summary>Requests shutdown without waiting for the loops to finish.</summary>
    public void Stop() => _shutdown?.Cancel();

    /// <summary>Takes an immutable snapshot of the whole bridge.</summary>
    public BridgeStatus Snapshot() => new BridgeStatus(
        _config.Name,
        _time.Elapsed - _startedAt,
        _supervisors.Select(static s => s.Snapshot()).ToList(),
        _routes.Select(static r => r.Snapshot()).ToList());

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_shutdown is not null)
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
        }

        if (_running is not null)
        {
            try
            {
                await _running.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _shutdown?.Dispose();
        _shutdown = null;
    }

    private EndpointSupervisor Supervisor(string endpointId) => _supervisorsById.TryGetValue(endpointId, out EndpointSupervisor? supervisor)
        ? supervisor
        : throw new ConfigException($"endpoint '{endpointId}' is referenced by a channel but was not created.", 0);

    /// <summary>
    /// Wraps a loop so that an unexpected exception is logged against its owner and stops that
    /// loop alone. Cancellation is the ordinary exit and is not reported as a failure.
    /// </summary>
    private async Task Guarded(string source, Func<Task> loop)
    {
        try
        {
            await loop().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }
        catch (Exception ex)
        {
            _log.Error(source, $"loop stopped: {ex.Message}", ex);
        }
    }

    private Task RunRouteAsync(RouteRuntime route, CancellationToken cancellationToken) =>
        route.Config.Trigger.Mode == TriggerMode.Periodic
            ? RunPeriodicRouteAsync(route, cancellationToken)
            : RunOnChangeRouteAsync(route, cancellationToken);

    /// <summary>Polls the source on the route's period and forwards what the deadband allows.</summary>
    private async Task RunPeriodicRouteAsync(RouteRuntime route, CancellationToken cancellationToken)
    {
        EndpointSupervisor supervisor = Supervisor(route.Source.Endpoint);

        if (supervisor.Endpoint is not IPollSource source)
        {
            _log.Error(route.Id, $"endpoint '{supervisor.Id}' cannot be polled, so this route cannot run.");
            route.OnSourceFailure($"endpoint '{supervisor.Id}' cannot be polled.");
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            await _time.Delay(route.Config.Trigger.Period, cancellationToken).ConfigureAwait(false);

            if (!supervisor.IsConnected)
            {
                route.OnSourceFailure($"endpoint '{supervisor.Id}' is not connected.");
                continue;
            }

            try
            {
                ReadOnlyMemory<byte> raw = await source.ReadAsync(route.Source, cancellationToken).ConfigureAwait(false);
                Forward(route, raw);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                RecordReadFailure(route, supervisor, ex);
            }
        }
    }

    /// <summary>Forwards whenever the source pushes a frame.</summary>
    private async Task RunOnChangeRouteAsync(RouteRuntime route, CancellationToken cancellationToken)
    {
        EndpointSupervisor supervisor = Supervisor(route.Source.Endpoint);

        if (supervisor.Endpoint is not IFrameSource source)
        {
            _log.Error(route.Id, $"endpoint '{supervisor.Id}' does not push frames, so an on_change trigger cannot run.");
            route.OnSourceFailure($"endpoint '{supervisor.Id}' does not push frames; use a periodic trigger.");
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!supervisor.IsConnected)
            {
                // Nothing will arrive while the transport is down; wait for the supervisor rather
                // than spinning on a closed socket.
                await _time.Delay(_options.EffectiveSupervisionInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                ReadOnlyMemory<byte> frame = await source.ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
                Forward(route, FramePayload.Extract(route.Source, frame));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                RecordReadFailure(route, supervisor, ex);
                await _time.Delay(_options.EffectiveSupervisionInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void Forward(RouteRuntime route, ReadOnlyMemory<byte> raw)
    {
        if (!route.TryAccept(raw.Span, out Sample sample))
        {
            return;
        }

        _sinkQueues[route.Sink.Endpoint].Enqueue(route, sample);
    }

    private void RecordReadFailure(RouteRuntime route, EndpointSupervisor supervisor, Exception ex)
    {
        route.OnSourceFailure(ex.Message);
        supervisor.OnTransportFailure(ex.Message);
        _log.Warn(route.Id, $"read failed: {ex.Message}");
    }

    /// <summary>
    /// One bounded queue of pending writes per sink endpoint. Bounded so that a stalled sink cannot
    /// grow memory without limit; the oldest pending write is dropped and counted, which keeps the
    /// loss visible in the route statistics instead of silent.
    /// </summary>
    private sealed class SinkQueue
    {
        private readonly Channel<PendingWrite> _channel;
        private readonly EndpointSupervisor _supervisor;
        private readonly IBridgeLog _log;

        public SinkQueue(EndpointSupervisor supervisor, RouterOptions options, IBridgeLog log)
        {
            _supervisor = supervisor;
            _log = log;
            // The channel's own DropOldest mode discards silently, which would hide the loss from
            // the route statistics. The queue therefore refuses when full and eviction is done
            // here, where the route that owned the discarded value is known.
            _channel = Channel.CreateBounded<PendingWrite>(new BoundedChannelOptions(options.SinkQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
            });
        }

        public string EndpointId => _supervisor.Id;

        /// <summary>
        /// Queues a write without ever blocking the caller's poll loop. When the queue is full the
        /// oldest pending write is evicted and charged to the route that produced it, so a stalled
        /// sink costs visible, counted samples rather than silent ones or unbounded memory.
        /// </summary>
        public void Enqueue(RouteRuntime route, Sample sample)
        {
            PendingWrite write = new PendingWrite(route, sample);

            // Bounded: each pass either succeeds or frees exactly one slot. The attempt limit is a
            // backstop against a pathological interleaving with the drain loop, never a normal path.
            for (int attempt = 0; attempt < 64; attempt++)
            {
                if (_channel.Writer.TryWrite(write))
                {
                    return;
                }

                if (_channel.Reader.TryRead(out PendingWrite evicted))
                {
                    evicted.Route.OnDropped(
                        $"sink '{EndpointId}' is behind; the oldest pending value was discarded.");
                }
            }

            route.OnDropped($"sink '{EndpointId}' is not accepting writes.");
        }

        public void Complete() => _channel.Writer.TryComplete();

        public async Task DrainAsync(CancellationToken cancellationToken)
        {
            while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out PendingWrite write))
                {
                    await WriteOneAsync(write, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task WriteOneAsync(PendingWrite write, CancellationToken cancellationToken)
        {
            if (_supervisor.Endpoint is not IValueSink sink)
            {
                write.Route.OnSinkFailure($"endpoint '{EndpointId}' cannot be written to.");
                return;
            }

            if (!_supervisor.IsConnected)
            {
                write.Route.OnSinkFailure($"endpoint '{EndpointId}' is not connected.");
                return;
            }

            try
            {
                await sink.WriteAsync(write.Route.Sink, write.Sample, cancellationToken).ConfigureAwait(false);
                write.Route.OnForwarded(write.Sample);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                write.Route.OnSinkFailure(ex.Message);
                _supervisor.OnTransportFailure(ex.Message);
                _log.Warn(write.Route.Id, $"write to '{EndpointId}' failed: {ex.Message}");
            }
        }

        private readonly record struct PendingWrite(RouteRuntime Route, Sample Sample);
    }
}
