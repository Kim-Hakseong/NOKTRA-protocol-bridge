using Pb.Core.Diagnostics;
using Pb.Core.Endpoints;
using Pb.Core.Time;

namespace Pb.Core.Routing;

/// <summary>
/// Keeps one endpoint connected. Each endpoint has its own supervisor, so an endpoint that is
/// down retries on its own schedule and never holds up the others, so the bridge as a whole keeps
/// working.
/// </summary>
public sealed class EndpointSupervisor
{
    private readonly IEndpoint _endpoint;
    private readonly RouterOptions _options;
    private readonly ITimeSource _time;
    private readonly IBridgeLog _log;
    private readonly List<RouteRuntime> _sourceRoutes = [];
    private readonly object _sync = new object();

    private long _attempts;
    private long _reconnects;
    private bool _hasConnected;
    private string? _lastError;

    public EndpointSupervisor(IEndpoint endpoint, RouterOptions options, ITimeSource time, IBridgeLog log)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>The supervised endpoint.</summary>
    public IEndpoint Endpoint => _endpoint;

    /// <summary>Endpoint id, for logs.</summary>
    public string Id => _endpoint.Id;

    /// <summary>True when the endpoint reports itself connected.</summary>
    public bool IsConnected => _endpoint.State == EndpointState.Connected;

    /// <summary>
    /// Registers a route that reads this endpoint, so its deadband can be reset when the endpoint
    /// reconnects.
    /// </summary>
    public void AddSourceRoute(RouteRuntime route)
    {
        ArgumentNullException.ThrowIfNull(route);

        lock (_sync)
        {
            _sourceRoutes.Add(route);
        }
    }

    /// <summary>
    /// Connects, retries with backoff while it fails, and then performs periodic upkeep until
    /// cancelled. Never throws for a connection problem: that is the state it exists to manage.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        int consecutiveFailures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!IsConnected)
            {
                if (await TryConnectAsync(cancellationToken).ConfigureAwait(false))
                {
                    consecutiveFailures = 0;
                }
                else
                {
                    consecutiveFailures++;
                    TimeSpan backoff = _options.BackoffFor(consecutiveFailures);
                    _log.Warn(Id, $"reconnecting in {backoff.TotalMilliseconds:F0} ms (attempt {consecutiveFailures} failed).");

                    try
                    {
                        await _time.Delay(backoff, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }
            }

            await TickAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await _time.Delay(_options.EffectiveSupervisionInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Makes one connection attempt, recording the outcome. Returns false for a failure the
    /// supervisor should retry.
    /// </summary>
    public async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attempts);

        try
        {
            await _endpoint.ConnectAsync(cancellationToken).ConfigureAwait(false);

            bool reconnected;

            lock (_sync)
            {
                reconnected = _hasConnected;
                _hasConnected = true;
                _lastError = null;

                if (reconnected)
                {
                    _reconnects++;
                }
            }

            if (reconnected)
            {
                // A fresh session must not have its first value compared against a pre-outage one.
                foreach (RouteRuntime route in SourceRoutes())
                {
                    route.OnSourceReconnected();
                }

                _log.Info(Id, $"reconnected to {_endpoint.Target}.");
            }
            else
            {
                _log.Info(Id, $"connected to {_endpoint.Target}.");
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _lastError = ex.Message;
            }

            _log.Warn(Id, $"could not connect: {ex.Message}");
            return false;
        }
    }

    /// <summary>Gives the endpoint its periodic upkeep, if it asked for any.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        if (_endpoint is not IEndpointUpkeep upkeep)
        {
            return;
        }

        try
        {
            await upkeep.TickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _lastError = ex.Message;
            }

            _log.Warn(Id, $"upkeep failed: {ex.Message}");
        }
    }

    /// <summary>Records a failure observed by a route, so the snapshot explains why a reconnect is due.</summary>
    public void OnTransportFailure(string message)
    {
        lock (_sync)
        {
            _lastError = message;
        }
    }

    /// <summary>Takes an immutable snapshot of this endpoint.</summary>
    public EndpointStatus Snapshot()
    {
        lock (_sync)
        {
            return new EndpointStatus(
                _endpoint.Id,
                _endpoint.Kind,
                _endpoint.Target,
                _endpoint.State,
                Interlocked.Read(ref _attempts),
                _reconnects,
                _lastError);
        }
    }

    private RouteRuntime[] SourceRoutes()
    {
        lock (_sync)
        {
            return _sourceRoutes.ToArray();
        }
    }
}
