using System.ComponentModel;
using Pb.Core.Endpoints;
using Pb.Core.Routing;
using Pb.Monitor;
using Xunit;

namespace Pb.Core.Tests;

/// <summary>
/// Covers the monitor's binding layer without starting a UI. The window's launch is checked by
/// <c>Pb.Monitor --smoke</c>, and its layout by <c>Pb.Monitor --screenshot</c>, which renders the
/// real view off-screen.
/// </summary>
public sealed class MonitorViewModelTests
{
    private static BridgeStatus Status(
        string name = "demo",
        bool healthy = true,
        int routes = 2,
        int endpoints = 2) =>
        new BridgeStatus(
            name,
            TimeSpan.FromSeconds(30),
            Enumerable.Range(0, endpoints)
                .Select(i => new EndpointStatus(
                    $"ep{i}",
                    "modbus_tcp",
                    $"127.0.0.1:{5000 + i}",
                    healthy ? EndpointState.Connected : EndpointState.Faulted,
                    healthy ? 1 : 4,
                    0,
                    healthy ? null : "refused"))
                .ToList(),
            Enumerable.Range(0, routes)
                .Select(i => new RouteStatus(
                    $"r{i}",
                    "src",
                    "dst",
                    healthy ? RouteHealth.Ok : RouteHealth.SinkFault,
                    3,
                    2,
                    1,
                    0,
                    0,
                    0,
                    1.5,
                    "bar",
                    DateTimeOffset.UnixEpoch,
                    healthy ? null : "broker gone"))
                .ToList());

    [Fact]
    public void Update_FillsTheHeaderTotalsAndBothRosters()
    {
        MonitorViewModel viewModel = new MonitorViewModel();

        viewModel.Update(Status());

        Assert.Equal("demo", viewModel.BridgeName);
        Assert.True(viewModel.IsHealthy);
        Assert.True(viewModel.IsRunning);
        Assert.Equal("HEALTHY", viewModel.StateText);
        Assert.Equal("30.0s", viewModel.UptimeText);
        Assert.Equal("2/2", viewModel.ConnectedText);
        Assert.Equal(1.0, viewModel.ConnectedFraction);
        Assert.Equal("6", viewModel.ReadText);
        Assert.Equal("4", viewModel.ForwardedText);
        Assert.Equal("2", viewModel.SuppressedText);
        Assert.Equal("0", viewModel.DroppedText);
        Assert.Equal("2/2", viewModel.RouteCountText);
        Assert.Equal(2, viewModel.Endpoints.Count);
        Assert.Equal(2, viewModel.Routes.Count);
    }

    [Fact]
    public void Update_BuildsARouteRowThatCarriesEverythingTheRosterShows()
    {
        MonitorViewModel viewModel = new MonitorViewModel();

        viewModel.Update(Status(routes: 1));
        RouteRow row = viewModel.Routes[0];

        Assert.Equal("r0", row.Id);
        Assert.Equal("src  →  dst", row.Flow);
        Assert.Equal(RouteHealth.Ok, row.Health);
        Assert.Equal("OK", row.HealthText);
        Assert.Equal("1.5 bar", row.Value);
        Assert.Equal("R 3   S 2   H 1   D 0", row.Counters);
        Assert.Null(row.Detail);
    }

    [Fact]
    public void Update_BuildsAnEndpointRowThatCarriesEverythingTheRosterShows()
    {
        MonitorViewModel viewModel = new MonitorViewModel();

        viewModel.Update(Status(endpoints: 1));
        EndpointRow row = viewModel.Endpoints[0];

        Assert.Equal("ep0", row.Id);
        Assert.Equal("MODBUS-TCP", row.Kind);
        Assert.Equal("127.0.0.1:5000", row.Target);
        Assert.Equal(EndpointState.Connected, row.State);
        Assert.Equal("CONNECTED", row.StateText);
        Assert.Equal("attempt 1", row.Detail);
    }

    [Fact]
    public void Update_ADegradedBridge_SurfacesTheFailuresOnTheRows()
    {
        MonitorViewModel viewModel = new MonitorViewModel();

        viewModel.Update(Status(healthy: false));

        Assert.False(viewModel.IsHealthy);
        Assert.Equal("DEGRADED", viewModel.StateText);
        Assert.Equal("0/2", viewModel.ConnectedText);
        Assert.Equal(0.0, viewModel.ConnectedFraction);
        Assert.Equal("refused", viewModel.Endpoints[0].Detail);
        Assert.Equal(EndpointState.Faulted, viewModel.Endpoints[0].State);
        Assert.Equal("SINK FAULT", viewModel.Routes[0].HealthText);
        Assert.Equal("broker gone", viewModel.Routes[0].Detail);
    }

    [Fact]
    public void Update_ADisabledRouteIsNotCountedAsLive()
    {
        MonitorViewModel viewModel = new MonitorViewModel();
        BridgeStatus status = Status(routes: 2) with
        {
            Routes =
            [
                Status(routes: 1).Routes[0],
                Status(routes: 1).Routes[0] with { Id = "parked", Health = RouteHealth.Disabled },
            ],
        };

        viewModel.Update(status);

        Assert.Equal("1/2", viewModel.RouteCountText);
        Assert.Equal("DISABLED", viewModel.Routes[1].HealthText);
    }

    [Fact]
    public void Update_ThousandsAreGroupedSoLongRunsStayReadable()
    {
        MonitorViewModel viewModel = new MonitorViewModel();
        BridgeStatus status = Status(routes: 1) with
        {
            Routes = [Status(routes: 1).Routes[0] with { SamplesRead = 1_234_567, SamplesForwarded = 1000 }],
        };

        viewModel.Update(status);

        Assert.Equal("1,234,567", viewModel.ReadText);
        Assert.Equal("1,000", viewModel.ForwardedText);
        Assert.Contains("R 1,234,567", viewModel.Routes[0].Counters, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_RaisesChangeNotificationsForBoundProperties()
    {
        MonitorViewModel viewModel = new MonitorViewModel();
        List<string> changed = [];
        ((INotifyPropertyChanged)viewModel).PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        viewModel.Update(Status());

        Assert.Contains(nameof(MonitorViewModel.BridgeName), changed);
        Assert.Contains(nameof(MonitorViewModel.StateText), changed);
        Assert.Contains(nameof(MonitorViewModel.IsHealthy), changed);
        Assert.Contains(nameof(MonitorViewModel.ConnectedFraction), changed);
    }

    [Fact]
    public void Update_ReusesRosterRowsSoTheListDoesNotFlicker()
    {
        MonitorViewModel viewModel = new MonitorViewModel();
        viewModel.Update(Status());

        List<string> changes = [];
        ((System.Collections.Specialized.INotifyCollectionChanged)viewModel.Routes).CollectionChanged +=
            (_, e) => changes.Add(e.Action.ToString());

        // The same status again must not touch the collection at all.
        viewModel.Update(Status());

        Assert.Empty(changes);
    }

    [Fact]
    public void Update_GrowsAndShrinksTheRostersToMatchTheBridge()
    {
        MonitorViewModel viewModel = new MonitorViewModel();

        viewModel.Update(Status(routes: 3));
        Assert.Equal(3, viewModel.Routes.Count);

        viewModel.Update(Status(routes: 1));
        Assert.Single(viewModel.Routes);
        Assert.Equal("r0", viewModel.Routes[0].Id);

        viewModel.Update(Status(routes: 4));
        Assert.Equal(4, viewModel.Routes.Count);
    }

    [Fact]
    public void ShowFailure_ReplacesTheStatusAndClearsTheRosters()
    {
        MonitorViewModel viewModel = new MonitorViewModel();
        viewModel.Update(Status());

        viewModel.ShowFailure("config is broken");

        Assert.Equal("config is broken", viewModel.Summary);
        Assert.Equal("STOPPED", viewModel.StateText);
        Assert.False(viewModel.IsHealthy);
        Assert.False(viewModel.IsRunning);
        Assert.Empty(viewModel.Endpoints);
        Assert.Empty(viewModel.Routes);
    }

    [Fact]
    public void ANewViewModel_ReportsThatNothingIsLoaded()
    {
        MonitorViewModel viewModel = new MonitorViewModel();

        Assert.Contains("No configuration loaded", viewModel.Summary, StringComparison.Ordinal);
        Assert.Equal("IDLE", viewModel.StateText);
        Assert.False(viewModel.IsRunning);
        Assert.Empty(viewModel.Routes);
    }

    [Fact]
    public void Update_And_ShowFailure_RejectInvalidArguments()
    {
        MonitorViewModel viewModel = new MonitorViewModel();

        Assert.Throws<ArgumentNullException>(() => viewModel.Update(null!));
        Assert.Throws<ArgumentException>(() => viewModel.ShowFailure(" "));
    }

    [Fact]
    public void DemoStatus_IsTheLoopbackExampleWithNoBrokerReachable()
    {
        // The rendered design study must stay a real scenario: if the example changes shape, this
        // fails rather than the screenshot quietly drifting away from the product.
        BridgeStatus status = DemoStatus.Create();

        Assert.Equal("loopback-demo", status.Name);
        Assert.Equal(4, status.Endpoints.Count);
        Assert.Equal(4, status.Routes.Count);
        Assert.False(status.IsHealthy);
        Assert.Single(status.Endpoints, static e => e.State == EndpointState.Faulted);
        Assert.Single(status.Routes, static r => r.Health == RouteHealth.Disabled);
        Assert.Equal("examples/loopback.yaml", DemoStatus.ConfigurationPath);
    }

    [Theory]
    [InlineData(new[] { "--config", "a.yaml" }, "a.yaml", null)]
    [InlineData(new[] { "-c", "b.yaml" }, "b.yaml", null)]
    [InlineData(new string[0], null, null)]
    [InlineData(new[] { "--smoke" }, null, null)]
    [InlineData(new[] { "--config" }, null, "--config needs")]
    [InlineData(new[] { "--config", "--smoke" }, null, "--config needs")]
    [InlineData(new[] { "--nope" }, null, "Unknown option")]
    [InlineData(new[] { "--scale", "9" }, null, "--scale needs")]
    [InlineData(new[] { "--screenshot" }, null, "--screenshot needs")]
    public void CommandLine_IsParsedWithClearErrors(string[] args, string? expectedPath, string? expectedError)
    {
        Program.Options? options = Program.Options.Parse(args, out string? usageError);

        if (expectedError is null)
        {
            Assert.NotNull(options);
            Assert.Equal(expectedPath, options!.ConfigurationPath);
            Assert.Null(usageError);
        }
        else
        {
            Assert.Null(options);
            Assert.Contains(expectedError, usageError!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CommandLine_ReadsTheScreenshotOptions()
    {
        Program.Options? options = Program.Options.Parse(["--screenshot", "out.png", "--scale", "3"], out string? error);

        Assert.Null(error);
        Assert.Equal("out.png", options!.ScreenshotPath);
        Assert.Equal(3, options.Scale);
        Assert.False(options.Smoke);
    }

    [Fact]
    public void CommandLine_DefaultsToTwiceTheLogicalSizeForReview()
    {
        Program.Options? options = Program.Options.Parse(["--screenshot", "out.png"], out _);

        Assert.Equal(2, options!.Scale);
    }

    [Fact]
    public async Task BridgeHost_AnInvalidConfiguration_IsShownInTheWindowRatherThanThrown()
    {
        MonitorViewModel viewModel = new MonitorViewModel();
        await using BridgeHost host = new BridgeHost(viewModel);
        string path = Path.Combine(Path.GetTempPath(), $"pb-monitor-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(path, "routes:\n", CancellationToken.None);

        try
        {
            bool started = host.Start(path);

            Assert.False(started);
            Assert.False(viewModel.IsRunning);
            Assert.Contains("required", viewModel.Summary, StringComparison.Ordinal);
            Assert.Equal(path, viewModel.ConfigurationPath);
            Assert.Null(host.Router);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BridgeHost_AMissingFile_IsShownInTheWindow()
    {
        MonitorViewModel viewModel = new MonitorViewModel();
        await using BridgeHost host = new BridgeHost(viewModel);

        Assert.False(host.Start(Path.Combine(Path.GetTempPath(), $"pb-missing-{Guid.NewGuid():N}.yaml")));
        Assert.False(viewModel.IsRunning);
    }

    [Fact]
    public async Task BridgeHost_AValidConfiguration_RunsAndRefreshesTheViewModel()
    {
        MonitorViewModel viewModel = new MonitorViewModel();
        await using BridgeHost host = new BridgeHost(viewModel);
        string path = Path.Combine(Path.GetTempPath(), $"pb-monitor-{Guid.NewGuid():N}.yaml");
        int listenPort = Harness.UdpProbe.FreePort();
        int sendPort = Harness.UdpProbe.FreePort();

        await File.WriteAllTextAsync(path, $"""
            bridge:
              name: monitored

            endpoints:
              - id: field
                type: udp
                listen_port: {listenPort}
                bind_address: 127.0.0.1
              - id: scada
                type: udp
                host: 127.0.0.1
                port: {sendPort}

            channels:
              - name: level_raw
                endpoint: field
                address: offset:0
                type: u16
              - name: level_out
                endpoint: scada
                address: offset:0
                type: f32

            routes:
              - id: level
                source: level_raw
                sink: level_out
                trigger:
                  mode: on_change
            """, CancellationToken.None);

        try
        {
            Assert.True(host.Start(path));
            Assert.Equal("monitored", viewModel.BridgeName);
            Assert.NotNull(host.Router);

            host.Refresh();

            Assert.Equal(2, viewModel.Endpoints.Count);
            Assert.Single(viewModel.Routes);
            Assert.True(viewModel.IsRunning);

            host.Stop();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BridgeHost_RefreshBeforeStart_DoesNothing()
    {
        MonitorViewModel viewModel = new MonitorViewModel();
        await using BridgeHost host = new BridgeHost(viewModel);

        host.Refresh();

        Assert.Empty(viewModel.Routes);
        Assert.False(viewModel.IsRunning);
    }

    [Fact]
    public async Task BridgeHost_RejectsNullAndEmptyArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new BridgeHost(null!));

        await using BridgeHost host = new BridgeHost(new MonitorViewModel());
        Assert.Throws<ArgumentException>(() => host.Start(" "));
    }
}
