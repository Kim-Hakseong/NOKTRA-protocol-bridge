using Pb.Core.Channels;
using Pb.Core.Configuration;
using Pb.Core.Diagnostics;
using Pb.Core.Endpoints;
using Pb.Core.Routing;
using Pb.Core.Tests.Harness;
using Pb.Core.Time;
using Xunit;

namespace Pb.Core.Tests;

/// <summary>
/// Router tests run entirely on a manual time source and scripted endpoints: every wait is on a
/// state signal or on time the test advances itself, so nothing here sleeps.
/// </summary>
public sealed class BridgeRouterTests
{
    private readonly CancellationTokenSource _testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

    private CancellationToken Ct => _testTimeout.Token;

    /// <summary>A supervision interval far above any route period, so advancing time hits routes first.</summary>
    private static readonly RouterOptions Options = new RouterOptions(
        SupervisionInterval: TimeSpan.FromSeconds(60),
        InitialReconnectBackoff: TimeSpan.FromMilliseconds(100),
        MaxReconnectBackoff: TimeSpan.FromSeconds(10));

    private const string OneRouteConfig = """
        bridge:
          name: test

        endpoints:
          - id: src
            type: udp
            listen_port: 5001
          - id: dst
            type: udp
            host: 127.0.0.1
            port: 5002

        channels:
          - name: level_raw
            endpoint: src
            address: offset:0
            type: u16
          - name: level_out
            endpoint: dst
            address: offset:0
            type: f32

        routes:
          - id: level
            source: level_raw
            sink: level_out
            trigger:
              mode: periodic
              period_ms: 500
            transform:
              scale: 0.1
              unit: bar
        """;

    private static (BridgeConfig Config, Dictionary<string, IEndpoint> Endpoints, ScriptedEndpoint Source, ScriptedEndpoint Sink) Build(
        string yaml = OneRouteConfig)
    {
        BridgeConfig config = BridgeConfigLoader.Load(yaml);
        ScriptedEndpoint source = new ScriptedEndpoint("src");
        ScriptedEndpoint sink = new ScriptedEndpoint("dst");

        Dictionary<string, IEndpoint> endpoints = new Dictionary<string, IEndpoint>(StringComparer.Ordinal)
        {
            ["src"] = source,
            ["dst"] = sink,
        };

        return (config, endpoints, source, sink);
    }

    /// <summary>
    /// Parked delays once a one-route bridge has settled: one supervisor per endpoint plus one
    /// periodic route loop. Waiting for exactly this many before moving the clock is what makes
    /// these tests deterministic — advancing while a loop has not yet reached its timer would
    /// jump past its first period.
    /// </summary>
    private const int OneRouteParkedDelays = 3;

    /// <summary>Parked delays for the two-route configuration: three supervisors, two route loops.</summary>
    private const int TwoRouteParkedDelays = 5;

    /// <summary>
    /// Advances the clock <paramref name="polls"/> times, each time waiting for every loop to be
    /// parked on its timer first and for the resulting read to be served.
    /// </summary>
    private static async Task PollAsync(
        ManualTimeSource time,
        ScriptedEndpoint source,
        int polls,
        CancellationToken ct,
        int parkedDelays = OneRouteParkedDelays)
    {
        for (int i = 0; i < polls; i++)
        {
            long target = source.Reads + 1;
            await time.WaitForPendingDelaysAsync(parkedDelays, ct);
            time.AdvanceToNextDelay();
            await source.WaitForAsync(() => source.Reads >= target, ct);
        }
    }

    /// <summary>
    /// Waits for every loop to be parked on its timer, then advances to the earliest of them. Used
    /// where the expected effect is a failure rather than a read, so there is no read to wait on.
    /// </summary>
    private static async Task SettleAndAdvanceAsync(
        ManualTimeSource time,
        CancellationToken ct,
        int parkedDelays = OneRouteParkedDelays)
    {
        await time.WaitForPendingDelaysAsync(parkedDelays, ct);
        time.AdvanceToNextDelay();
    }

    [Fact]
    public async Task PeriodicRoute_ReadsTransformsAndWritesOnEveryPeriod()
    {
        ManualTimeSource time = new ManualTimeSource();
        (BridgeConfig config, Dictionary<string, IEndpoint> endpoints, ScriptedEndpoint source, ScriptedEndpoint sink) = Build();
        source.SetRegister(100);

        await using BridgeRouter router = new BridgeRouter(config, endpoints, time, Options);
        Task run = router.RunAsync(Ct);

        await source.WaitForConnectedAsync(Ct);
        await sink.WaitForConnectedAsync(Ct);
        await PollAsync(time, source, 1, Ct);
        await sink.WaitForWritesAsync(1, Ct);

        WrittenValue written = sink.Written[0];
        Assert.Equal("level_out", written.Channel);
        Assert.Equal(10.0, written.Sample.Value, 12);
        Assert.Equal("bar", written.Sample.Unit);

        router.Stop();
        await run;
    }

    [Fact]
    public async Task PeriodicRoute_PollsOncePerPeriod()
    {
        ManualTimeSource time = new ManualTimeSource();
        (BridgeConfig config, Dictionary<string, IEndpoint> endpoints, ScriptedEndpoint source, ScriptedEndpoint sink) = Build();
        source.SetRegister(10);

        await using BridgeRouter router = new BridgeRouter(config, endpoints, time, Options);
        Task run = router.RunAsync(Ct);
        await source.WaitForConnectedAsync(Ct);

        await PollAsync(time, source, 3, Ct);

        Assert.Equal(3, source.Reads);
        router.Stop();
        await run;
    }

    [Fact]
    public async Task Deadband_SuppressesInsignificantChangesAndCountsThem()
    {
        ManualTimeSource time = new ManualTimeSource();
        (BridgeConfig config, Dictionary<string, IEndpoint> endpoints, ScriptedEndpoint source, ScriptedEndpoint sink) = Build("""
            endpoints:
              - id: src
                type: udp
                listen_port: 5001
              - id: dst
                type: udp
                host: 127.0.0.1
                port: 5002
            channels:
              - name: level_raw
                endpoint: src
                address: offset:0
                type: u16
              - name: level_out
                endpoint: dst
                address: offset:0
                type: f32
            routes:
              - id: level
                source: level_raw
                sink: level_out
                trigger:
                  mode: periodic
                  period_ms: 100
                transform:
                  scale: 0.1
                  deadband: 0.5
            """);

        await using BridgeRouter router = new BridgeRouter(config, endpoints, time, Options);
        Task run = router.RunAsync(Ct);
        await source.WaitForConnectedAsync(Ct);

        // Registers 10, 13, 16, 14 scale to 1.0, 1.3, 1.6, 1.4 — the DESIGN deadband vector.
        foreach (ushort raw in new ushort[] { 10, 13, 16, 14 })
        {
            source.SetRegister(raw);
            await PollAsync(time, source, 1, Ct);
        }

        await sink.WaitForWritesAsync(2, Ct);

        Assert.Equal([1.0, 1.6], sink.Written.Select(w => Math.Round(w.Sample.Value, 6)));

        RouteStatus status = router.Snapshot().Routes.Single();
        Assert.Equal(4, status.SamplesRead);
        Assert.Equal(2, status.SamplesForwarded);
        Assert.Equal(2, status.SamplesSuppressed);

        router.Stop();
        await run;
    }

    [Fact]
    public async Task OnChangeRoute_ForwardsWheneverTheSourcePushesAFrame()
    {
        ManualTimeSource time = new ManualTimeSource();
        (BridgeConfig config, Dictionary<string, IEndpoint> endpoints, ScriptedEndpoint source, ScriptedEndpoint sink) = Build("""
            endpoints:
              - id: src
                type: udp
                listen_port: 5001
              - id: dst
                type: udp
                host: 127.0.0.1
                port: 5002
            channels:
              - name: level_raw
                endpoint: src
                address: offset:0
                type: u16
              - name: level_out
                endpoint: dst
                address: offset:0
                type: f32
            routes:
              - id: level
                source: level_raw
                sink: level_out
                trigger:
                  mode: on_change
                transform:
                  scale: 0.5
            """);

        await using BridgeRouter router = new BridgeRouter(config, endpoints, time, Options);
        Task run = router.RunAsync(Ct);
        await source.WaitForConnectedAsync(Ct);
        await source.WaitForAsync(() => true, Ct);

        source.PushFrame(0x00, 0x08);
        await sink.WaitForWritesAsync(1, Ct);
        source.PushFrame(0x00, 0x0A);
        await sink.WaitForWritesAsync(2, Ct);

        Assert.Equal([4.0, 5.0], sink.Written.Select(static w => w.Sample.Value));

        router.Stop();
        await run;
    }

    [Fact]
    public async Task OnChangeRoute_OnAnEndpointThatDoesNotPush_ReportsItAndStops()
    {
        ManualTimeSource time = new ManualTimeSource();
        BridgeConfig config = BridgeConfigLoader.Load("""
            endpoints:
              - id: src
                type: modbus-tcp
                host: 127.0.0.1
              - id: dst
                type: udp
                host: 127.0.0.1
                port: 5002
            channels:
              - name: level_raw
                endpoint: src
                address: holding:0
                type: u16
              - name: level_out
                endpoint: dst
                address: offset:0
                type: f32
            routes:
              - id: level
                source: level_raw
                sink: level_out
                trigger:
                  mode: on_change
            """);

        PollOnlyEndpoint source = new PollOnlyEndpoint("src");
        ScriptedEndpoint sink = new ScriptedEndpoint("dst");
        Dictionary<string, IEndpoint> endpoints = new Dictionary<string, IEndpoint>(StringComparer.Ordinal)
        {
            ["src"] = source,
            ["dst"] = sink,
        };

        await using BridgeRouter router = new BridgeRouter(config, endpoints, time, Options);
        Task run = router.RunAsync(Ct);

        await WaitForRouteAsync(router, "level", static s => s.Health == RouteHealth.SourceFault, Ct);

        Assert.Contains("does not push frames", router.Snapshot().Routes.Single().LastError!, StringComparison.Ordinal);

        router.Stop();
        await run;
    }

    [Fact]
    public async Task SourceFailure_FaultsOnlyThatRouteAndTheBridgeKeepsRunning()
    {
        ManualTimeSource time = new ManualTimeSource();
        BridgeConfig config = BridgeConfigLoader.Load(TwoRouteConfig);
        ScriptedEndpoint good = new ScriptedEndpoint("good");
        ScriptedEndpoint bad = new ScriptedEndpoint("bad") { ReadFailure = "sensor unplugged" };
        ScriptedEndpoint sink = new ScriptedEndpoint("dst");
        good.SetRegister(50);

        Dictionary<string, IEndpoint> endpoints = new Dictionary<string, IEndpoint>(StringComparer.Ordinal)
        {
            ["good"] = good,
            ["bad"] = bad,
            ["dst"] = sink,
        };

        await using BridgeRouter router = new BridgeRouter(config, endpoints, time, Options);
        Task run = router.RunAsync(Ct);
        await good.WaitForConnectedAsync(Ct);

        await PollAsync(time, good, 2, Ct, TwoRouteParkedDelays);
        await sink.WaitForWritesAsync(1, Ct);
        await WaitForRouteAsync(router, "from_bad", static s => s.Health == RouteHealth.SourceFault, Ct);

        BridgeStatus status = router.Snapshot();
        RouteStatus healthy = status.Routes.Single(static r => r.Id == "from_good");
        RouteStatus faulted = status.Routes.Single(static r => r.Id == "from_bad");

        Assert.Equal(RouteHealth.Ok, healthy.Health);
        Assert.True(healthy.SamplesForwarded >= 1);
        Assert.Equal(RouteHealth.SourceFault, faulted.Health);
        Assert.Contains("sensor unplugged", faulted.LastError!, StringComparison.Ordinal);
        Assert.Equal(0, faulted.SamplesForwarded);
        Assert.False(status.IsHealthy);

        router.Stop();
        await run;
    }

    [Fact]
    public async Task SourceRecovery_ClearsTheFaultWithoutRestartingTheBridge()
    {
        ManualTimeSource time = new ManualTimeSource();
        (BridgeConfig config, Dictionary<string, IEndpoint> endpoints, ScriptedEndpoint source, ScriptedEndpoint sink) = Build();
        source.ReadFailure = "sensor unplugged";

        await using BridgeRouter router = new BridgeRouter(config, endpoints, time, Options);
        Task run = router.RunAsync(Ct);
        await source.WaitForConnectedAsync(Ct);

        await SettleAndAdvanceAsync(time, Ct);
        await WaitForRouteAsync(router, "level", static s => s.Health == RouteHealth.SourceFault, Ct);

        source.ReadFailure = null;
        source.SetRegister(200);
        await PollAsync(time, source, 1, Ct);
        await sink.WaitForWritesAsync(1, Ct);

        RouteStatus status = router.Snapshot().Routes.Single();
        Assert.Equal(RouteHealth.Ok, status.Health);
        Assert.Equal(1, status.ReadFailures);
        Assert.Equal(20.0, sink.Written[0].Sample.Value, 12);
        Assert.Null(status.LastError);

        router.Stop();
        await run;
    }

    [Fact]
    public async Task SinkFailure_FaultsTheRouteButTheSourceKeepsBeingRead()
    {
        ManualTimeSource time = new ManualTimeSource();
        (BridgeConfig config, Dictionary<string, IEndpoint> endpoints, ScriptedEndpoint source, ScriptedEndpoint sink) = Build();
        source.SetRegister(100);
        sink.WriteFailure = "broker gone";

        await using BridgeRouter router = new BridgeRouter(config, endpoints, time, Options);
        Task run = router.RunAsync(Ct);
        await source.WaitForConnectedAsync(Ct);

        await PollAsync(time, source, 1, Ct);
        await WaitForRouteAsync(router, "level", static s => s.Health == RouteHealth.SinkFault, Ct);
        await PollAsync(time, source, 1, Ct);

        RouteStatus status = router.Snapshot().Routes.Single();
        Assert.Equal(RouteHealth.SinkFault, status.Health);
        Assert.Contains("broker gone", status.LastError!, StringComparison.Ordinal);
        Assert.True(status.SamplesRead >= 2);
        Assert.Equal(0, status.SamplesForwarded);
        Assert.True(status.WriteFailures >= 1);

        router.Stop();
        await run;
    }

    [Fact]
    public async Task SinkRecovery_ForwardsAgainAndClearsTheFault()
    {
        ManualTimeSource time = new ManualTimeSource();
        (BridgeConfig config, Dictionary<string, IEndpoint> endpoints, ScriptedEndpoint source, ScriptedEndpoint sink) = Build();
        source.SetRegister(100);
        sink.WriteFailure = "broker gone";

        await using BridgeRouter router = new BridgeRouter(config, endpoints, time, Options);
        Task run = router.RunAsync(Ct);
        await source.WaitForConnectedAsync(Ct);
        await PollAsync(time, source, 1, Ct);
        await WaitForRouteAsync(router, "level", static s => s.Health == RouteHealth.SinkFault, Ct);

        sink.WriteFailure = null;
        source.SetRegister(300);
        await PollAsync(time, source, 1, Ct);
        await sink.WaitForWritesAsync(1, Ct);
        await WaitForRouteAsync(router, "level", static s => s.Health == RouteHealth.Ok, Ct);

        Assert.Equal(30.0, sink.Written[0].Sample.Value, 12);
        Assert.Null(router.Snapshot().Routes.Single().LastError);

        router.Stop();
        await run;
    }

    [Fact]
    public async Task ConnectFailure_RetriesWithBackoffUntilItSucceeds()
    {
        ManualTimeSource time = new ManualTimeSource();
        (BridgeConfig config, Dictionary<string, IEndpoint> endpoints, ScriptedEndpoint source, ScriptedEndpoint sink) = Build();
        source.FailNextConnects(3);
        source.SetRegister(100);

        await using BridgeRouter router = new BridgeRouter(config, endpoints, time, Options);
        Task run = router.RunAsync(Ct);

        // Each failed attempt parks on its backoff delay; advancing time releases the next attempt.
        while (source.State != EndpointState.Connected)
        {
            await time.WaitForPendingDelaysAsync(1, Ct);
            time.AdvanceToNextDelay();
            await Task.Yield();
        }

        EndpointStatus status = router.Snapshot().Endpoints.Single(static e => e.Id == "src");
        Assert.Equal(4, status.ConnectAttempts);
        Assert.Equal(EndpointState.Connected, status.State);

        router.Stop();
        await run;
    }

    [Fact]
    public async Task Backoff_DoublesAndIsCappedAtTheCeiling()
    {
        RouterOptions options = new RouterOptions(
            InitialReconnectBackoff: TimeSpan.FromMilliseconds(100),
            MaxReconnectBackoff: TimeSpan.FromMilliseconds(800));

        Assert.Equal(TimeSpan.FromMilliseconds(100), options.BackoffFor(1));
        Assert.Equal(TimeSpan.FromMilliseconds(200), options.BackoffFor(2));
        Assert.Equal(TimeSpan.FromMilliseconds(400), options.BackoffFor(3));
        Assert.Equal(TimeSpan.FromMilliseconds(800), options.BackoffFor(4));
        Assert.Equal(TimeSpan.FromMilliseconds(800), options.BackoffFor(5));
        Assert.Equal(TimeSpan.FromMilliseconds(800), options.BackoffFor(500));
        Assert.Throws<ArgumentOutOfRangeException>(() => options.BackoffFor(0));

        await Task.CompletedTask;
    }

    [Fact]
    public async Task Reconnect_ResetsTheDeadbandSoTheFirstValueAfterAnOutageIsForwarded()
    {
        ManualTimeSource time = new ManualTimeSource();
        (BridgeConfig config, Dictionary<string, IEndpoint> endpoints, ScriptedEndpoint source, ScriptedEndpoint sink) = Build("""
            endpoints:
              - id: src
                type: udp
                listen_port: 5001
              - id: dst
                type: udp
                host: 127.0.0.1
                port: 5002
            channels:
              - name: level_raw
                endpoint: src
                address: offset:0
                type: u16
              - name: level_out
                endpoint: dst
                address: offset:0
                type: f32
            routes:
              - id: level
                source: level_raw
                sink: level_out
                trigger:
                  mode: periodic
                  period_ms: 100
                transform:
                  deadband: 1000
            """);

        await using BridgeRouter router = new BridgeRouter(config, endpoints, time, Options);
        Task run = router.RunAsync(Ct);
        await source.WaitForConnectedAsync(Ct);

        source.SetRegister(10);
        await PollAsync(time, source, 1, Ct);
        await sink.WaitForWritesAsync(1, Ct);

        // A second value well inside the deadband is suppressed.
        source.SetRegister(11);
        await PollAsync(time, source, 1, Ct);
        Assert.Single(sink.Written);

        // After an outage the deadband reference is cleared, so the same value is forwarded again.
        source.ForceFault();
        await source.WaitForAsync(() => source.State == EndpointState.Faulted, Ct);
        await AdvanceUntilAsync(time, () => source.State == EndpointState.Connected, Ct);
        await PollAsync(time, source, 1, Ct);
        await sink.WaitForWritesAsync(2, Ct);

        Assert.Equal(2, sink.Written.Count);
        Assert.Equal(1, router.Snapshot().Endpoints.Single(static e => e.Id == "src").Reconnects);

        router.Stop();
        await run;
    }

    [Fact]
    public async Task Upkeep_IsCalledOnTheSupervisionInterval()
    {
        ManualTimeSource time = new ManualTimeSource();
        (BridgeConfig config, Dictionary<string, IEndpoint> endpoints, ScriptedEndpoint source, ScriptedEndpoint sink) = Build();
        RouterOptions options = new RouterOptions(SupervisionInterval: TimeSpan.FromMilliseconds(50));

        await using BridgeRouter router = new BridgeRouter(config, endpoints, time, options);
        Task run = router.RunAsync(Ct);
        await source.WaitForConnectedAsync(Ct);

        long before = source.Ticks;
        await AdvanceUntilAsync(time, () => source.Ticks >= before + 2, Ct);

        Assert.True(source.Ticks >= before + 2);

        router.Stop();
        await run;
    }

    [Fact]
    public async Task UpkeepFailure_IsRecordedWithoutStoppingTheEndpoint()
    {
        ManualTimeSource time = new ManualTimeSource();
        (BridgeConfig config, Dictionary<string, IEndpoint> endpoints, ScriptedEndpoint source, ScriptedEndpoint sink) = Build();
        source.UpkeepFailure = "ping refused";
        source.SetRegister(100);

        await using BridgeRouter router = new BridgeRouter(config, endpoints, time, Options);
        Task run = router.RunAsync(Ct);
        await source.WaitForConnectedAsync(Ct);
        await PollAsync(time, source, 1, Ct);
        await sink.WaitForWritesAsync(1, Ct);

        Assert.Contains("ping refused", router.Snapshot().Endpoints.Single(static e => e.Id == "src").LastError!, StringComparison.Ordinal);
        Assert.Equal(EndpointState.Connected, source.State);

        router.Stop();
        await run;
    }

    [Fact]
    public async Task DisabledRoute_NeitherReadsNorWrites()
    {
        ManualTimeSource time = new ManualTimeSource();
        (BridgeConfig config, Dictionary<string, IEndpoint> endpoints, ScriptedEndpoint source, ScriptedEndpoint sink) = Build("""
            endpoints:
              - id: src
                type: udp
                listen_port: 5001
              - id: dst
                type: udp
                host: 127.0.0.1
                port: 5002
            channels:
              - name: level_raw
                endpoint: src
                address: offset:0
                type: u16
              - name: level_out
                endpoint: dst
                address: offset:0
                type: f32
            routes:
              - id: level
                source: level_raw
                sink: level_out
                enabled: false
                trigger:
                  mode: periodic
                  period_ms: 100
            """);

        await using BridgeRouter router = new BridgeRouter(config, endpoints, time, Options);
        Task run = router.RunAsync(Ct);
        await source.WaitForConnectedAsync(Ct);

        await time.WaitForPendingDelaysAsync(2, Ct);
        time.Advance(TimeSpan.FromSeconds(5));
        await Task.Yield();

        Assert.Equal(0, source.Reads);
        Assert.Empty(sink.Written);
        Assert.Equal(RouteHealth.Disabled, router.Snapshot().Routes.Single().Health);

        router.Stop();
        await run;
    }

    [Fact]
    public async Task Snapshot_ReportsIdentityUptimeAndTotals()
    {
        ManualTimeSource time = new ManualTimeSource();
        (BridgeConfig config, Dictionary<string, IEndpoint> endpoints, ScriptedEndpoint source, ScriptedEndpoint sink) = Build();
        source.SetRegister(100);

        await using BridgeRouter router = new BridgeRouter(config, endpoints, time, Options);
        Task run = router.RunAsync(Ct);
        await source.WaitForConnectedAsync(Ct);
        await PollAsync(time, source, 1, Ct);
        await sink.WaitForWritesAsync(1, Ct);

        BridgeStatus status = router.Snapshot();

        Assert.Equal("test", status.Name);
        Assert.True(status.Uptime > TimeSpan.Zero);
        Assert.Equal(2, status.Endpoints.Count);
        Assert.Equal(1, status.TotalForwarded);
        Assert.Equal(0, status.TotalDropped);
        Assert.True(status.IsHealthy);
        Assert.Equal("scripted", status.Endpoints[0].Kind);
        Assert.Equal("level_raw", status.Routes[0].Source);
        Assert.Equal("level_out", status.Routes[0].Sink);
        Assert.Equal("bar", status.Routes[0].Unit);
        Assert.Equal(10.0, status.Routes[0].LastValue!.Value, 12);
        Assert.NotNull(status.Routes[0].LastForwardedAt);

        router.Stop();
        await run;
    }

    [Fact]
    public async Task Log_ReportsConnectionsAndFailures()
    {
        ManualTimeSource time = new ManualTimeSource();
        (BridgeConfig config, Dictionary<string, IEndpoint> endpoints, ScriptedEndpoint source, ScriptedEndpoint sink) = Build();
        source.ReadFailure = "sensor unplugged";
        List<string> entries = [];
        DelegateBridgeLog log = new DelegateBridgeLog((level, src, message, _) =>
        {
            lock (entries)
            {
                entries.Add($"{level}|{src}|{message}");
            }
        });

        await using BridgeRouter router = new BridgeRouter(config, endpoints, time, Options, log);
        Task run = router.RunAsync(Ct);
        await source.WaitForConnectedAsync(Ct);
        await SettleAndAdvanceAsync(time, Ct);
        await WaitForRouteAsync(router, "level", static s => s.Health == RouteHealth.SourceFault, Ct);

        router.Stop();
        await run;

        string[] snapshot;
        lock (entries)
        {
            snapshot = entries.ToArray();
        }

        Assert.Contains(snapshot, e => e.Contains("Info|test|starting", StringComparison.Ordinal));
        Assert.Contains(snapshot, e => e.Contains("Info|src|connected", StringComparison.Ordinal));
        Assert.Contains(snapshot, e => e.Contains("Warning|level|read failed", StringComparison.Ordinal));
        Assert.Contains(snapshot, e => e.Contains("Info|test|stopped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SinkQueue_DropsTheOldestPendingWriteAndCountsIt()
    {
        ManualTimeSource time = new ManualTimeSource();
        (BridgeConfig config, Dictionary<string, IEndpoint> endpoints, ScriptedEndpoint source, ScriptedEndpoint sink) = Build();
        RouterOptions options = Options with { SinkQueueCapacity = 1 };
        BlockingSink blocking = new BlockingSink("dst");
        endpoints["dst"] = blocking;
        source.SetRegister(100);

        await using BridgeRouter router = new BridgeRouter(config, endpoints, time, options);
        Task run = router.RunAsync(Ct);
        await source.WaitForConnectedAsync(Ct);

        // The first write blocks in the sink; the queue then holds one entry and drops the rest.
        for (int i = 0; i < 6; i++)
        {
            await PollAsync(time, source, 1, Ct);
        }

        await WaitForRouteAsync(router, "level", static s => s.SamplesDropped > 0, Ct);

        RouteStatus status = router.Snapshot().Routes.Single();
        Assert.True(status.SamplesDropped > 0, $"expected drops, got {status.SamplesDropped}");
        Assert.Equal(0, status.WriteFailures);
        Assert.True(router.Snapshot().TotalDropped > 0);

        blocking.Release();
        router.Stop();
        await run;
    }

    [Fact]
    public async Task TwoRoutesSharingOneSink_BothDeliver()
    {
        ManualTimeSource time = new ManualTimeSource();
        BridgeConfig config = BridgeConfigLoader.Load(TwoRouteConfig);
        ScriptedEndpoint good = new ScriptedEndpoint("good");
        ScriptedEndpoint other = new ScriptedEndpoint("bad");
        ScriptedEndpoint sink = new ScriptedEndpoint("dst");
        good.SetRegister(10);
        other.SetRegister(20);

        Dictionary<string, IEndpoint> endpoints = new Dictionary<string, IEndpoint>(StringComparer.Ordinal)
        {
            ["good"] = good,
            ["bad"] = other,
            ["dst"] = sink,
        };

        await using BridgeRouter router = new BridgeRouter(config, endpoints, time, Options);
        Task run = router.RunAsync(Ct);
        await good.WaitForConnectedAsync(Ct);
        await other.WaitForConnectedAsync(Ct);

        await time.WaitForPendingDelaysAsync(4, Ct);
        time.Advance(TimeSpan.FromMilliseconds(500));
        await sink.WaitForWritesAsync(2, Ct);

        Assert.Contains(sink.Written, static w => w.Channel == "out_good");
        Assert.Contains(sink.Written, static w => w.Channel == "out_bad");

        router.Stop();
        await run;
    }

    [Fact]
    public async Task Construction_RejectsAChannelTheEndpointCannotServe()
    {
        (BridgeConfig config, Dictionary<string, IEndpoint> endpoints, ScriptedEndpoint source, _) = Build();
        source.RefuseAsSource = true;

        ConfigException ex = Assert.Throws<ConfigException>(() =>
            new BridgeRouter(config, endpoints, new ManualTimeSource(), Options));

        Assert.Contains("refuses source channels", ex.Message, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Construction_RejectsAMissingEndpoint()
    {
        (BridgeConfig config, Dictionary<string, IEndpoint> endpoints, _, _) = Build();
        endpoints.Remove("dst");

        Assert.Throws<ConfigException>(() => new BridgeRouter(config, endpoints, new ManualTimeSource(), Options));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Construction_RejectsNullArgumentsAndBadOptions()
    {
        (BridgeConfig config, Dictionary<string, IEndpoint> endpoints, _, _) = Build();

        Assert.Throws<ArgumentNullException>(() => new BridgeRouter(null!, endpoints));
        Assert.Throws<ArgumentNullException>(() => new BridgeRouter(config, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BridgeRouter(config, endpoints, new ManualTimeSource(), new RouterOptions(SupervisionInterval: TimeSpan.Zero)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BridgeRouter(config, endpoints, new ManualTimeSource(), new RouterOptions(SinkQueueCapacity: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BridgeRouter(config, endpoints, new ManualTimeSource(), new RouterOptions(
                InitialReconnectBackoff: TimeSpan.FromSeconds(5),
                MaxReconnectBackoff: TimeSpan.FromSeconds(1))));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RunAsync_CannotBeStartedTwiceAndReturnsOnCancellation()
    {
        ManualTimeSource time = new ManualTimeSource();
        (BridgeConfig config, Dictionary<string, IEndpoint> endpoints, ScriptedEndpoint source, _) = Build();

        await using BridgeRouter router = new BridgeRouter(config, endpoints, time, Options);
        Task run = router.RunAsync(Ct);
        await source.WaitForConnectedAsync(Ct);

        Assert.True(router.IsRunning);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await router.RunAsync(Ct));

        router.Stop();
        await run;
        Assert.False(router.IsRunning);
    }

    [Fact]
    public async Task Dispose_StopsARunningRouter()
    {
        ManualTimeSource time = new ManualTimeSource();
        (BridgeConfig config, Dictionary<string, IEndpoint> endpoints, ScriptedEndpoint source, _) = Build();

        BridgeRouter router = new BridgeRouter(config, endpoints, time, Options);
        Task run = router.RunAsync(Ct);
        await source.WaitForConnectedAsync(Ct);

        await router.DisposeAsync();
        await router.DisposeAsync();

        await run;
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await router.RunAsync(Ct));
    }

    private const string TwoRouteConfig = """
        endpoints:
          - id: good
            type: udp
            listen_port: 5001
          - id: bad
            type: udp
            listen_port: 5003
          - id: dst
            type: udp
            host: 127.0.0.1
            port: 5002

        channels:
          - name: from_good_ch
            endpoint: good
            address: offset:0
            type: u16
          - name: from_bad_ch
            endpoint: bad
            address: offset:0
            type: u16
          - name: out_good
            endpoint: dst
            address: offset:0
            type: f32
          - name: out_bad
            endpoint: dst
            address: offset:4
            type: f32

        routes:
          - id: from_good
            source: from_good_ch
            sink: out_good
            trigger:
              mode: periodic
              period_ms: 100
          - id: from_bad
            source: from_bad_ch
            sink: out_bad
            trigger:
              mode: periodic
              period_ms: 100
        """;

    /// <summary>Waits until a route's snapshot satisfies <paramref name="predicate"/>.</summary>
    private static async Task WaitForRouteAsync(
        BridgeRouter router,
        string routeId,
        Func<RouteStatus, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (!predicate(router.Snapshot().Routes.Single(r => r.Id == routeId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    /// <summary>Advances the manual clock, one pending delay at a time, until a condition holds.</summary>
    private static async Task AdvanceUntilAsync(ManualTimeSource time, Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await time.WaitForPendingDelaysAsync(1, cancellationToken);
            time.AdvanceToNextDelay();
            await Task.Yield();
        }
    }

    /// <summary>An endpoint that can be polled but never pushes, to exercise trigger mismatches.</summary>
    private sealed class PollOnlyEndpoint : IEndpoint, IPollSource
    {
        public PollOnlyEndpoint(string id) => Id = id;

        public string Id { get; }

        public string Kind => "poll-only";

        public EndpointState State { get; private set; } = EndpointState.Disconnected;

        public string Target => "poll-only";

        public bool Supports(ChannelSpec channel, ChannelRole role, out string? error)
        {
            error = null;
            return true;
        }

        public ValueTask ConnectAsync(CancellationToken cancellationToken)
        {
            State = EndpointState.Connected;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisconnectAsync()
        {
            State = EndpointState.Disconnected;
            return ValueTask.CompletedTask;
        }

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ChannelSpec channel, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 0x00, 0x01 });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A sink whose first write blocks until released, to fill the sink queue.</summary>
    private sealed class BlockingSink : IEndpoint, IValueSink
    {
        private readonly TaskCompletionSource _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingSink(string id) => Id = id;

        public string Id { get; }

        public string Kind => "blocking";

        public EndpointState State { get; private set; } = EndpointState.Disconnected;

        public string Target => "blocking";

        public void Release() => _release.TrySetResult();

        public bool Supports(ChannelSpec channel, ChannelRole role, out string? error)
        {
            error = null;
            return true;
        }

        public ValueTask ConnectAsync(CancellationToken cancellationToken)
        {
            State = EndpointState.Connected;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisconnectAsync()
        {
            State = EndpointState.Disconnected;
            return ValueTask.CompletedTask;
        }

        public async ValueTask WriteAsync(ChannelSpec channel, Sample sample, CancellationToken cancellationToken) =>
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        public ValueTask DisposeAsync()
        {
            Release();
            return ValueTask.CompletedTask;
        }
    }
}
