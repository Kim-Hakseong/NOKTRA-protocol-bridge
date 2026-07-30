using Pb.Core.Configuration;
using Pb.Core.Endpoints;
using Pb.Core.Routing;

namespace Pb.Monitor;

/// <summary>
/// Runs a bridge for the monitor window and keeps a <see cref="MonitorViewModel"/> in step with it.
/// </summary>
/// <remarks>
/// The monitor is a read-only observer, so failures here are shown in the window rather than thrown:
/// a window that reports "this configuration is invalid" is more useful than one that will not open.
/// </remarks>
public sealed class BridgeHost : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();

    private Dictionary<string, IEndpoint>? _endpoints;
    private BridgeRouter? _router;
    private Task? _run;
    private bool _disposed;

    public BridgeHost(MonitorViewModel viewModel) =>
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

    /// <summary>What the window binds to.</summary>
    public MonitorViewModel ViewModel { get; }

    /// <summary>The running router, or null when nothing was started.</summary>
    public BridgeRouter? Router => _router;

    /// <summary>
    /// Loads a configuration and starts moving data. Returns false and puts the reason in the view
    /// model when the configuration cannot be started.
    /// </summary>
    public bool Start(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        ObjectDisposedException.ThrowIf(_disposed, this);

        ViewModel.ConfigurationPath = configurationPath;

        try
        {
            BridgeConfig config = BridgeConfigLoader.LoadFile(configurationPath);
            _endpoints = EndpointFactory.CreateAll(config);
            _router = new BridgeRouter(config, _endpoints);
            _run = _router.RunAsync(_shutdown.Token);
            ViewModel.BridgeName = config.Name;
            ViewModel.Summary = "starting…";
            ViewModel.IsRunning = true;
            return true;
        }
        catch (ConfigException ex)
        {
            ViewModel.ShowFailure(ex.Message);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ViewModel.ShowFailure($"Could not read '{configurationPath}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Copies the current state into the view model. Called on the UI thread by a timer.</summary>
    public void Refresh()
    {
        if (_router is null)
        {
            return;
        }

        ViewModel.Update(_router.Snapshot());
        ViewModel.IsRunning = _router.IsRunning;
    }

    /// <summary>Asks the bridge to stop, without waiting.</summary>
    public void Stop() => _shutdown.Cancel();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_run is not null)
        {
            try
            {
                await _run.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        if (_router is not null)
        {
            await _router.DisposeAsync().ConfigureAwait(false);
        }

        if (_endpoints is not null)
        {
            foreach (IEndpoint endpoint in _endpoints.Values)
            {
                await endpoint.DisposeAsync().ConfigureAwait(false);
            }
        }

        _shutdown.Dispose();
    }
}
