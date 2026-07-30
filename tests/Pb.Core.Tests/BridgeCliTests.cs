using Pb.Cli;
using Pb.Core.Routing;
using Pb.Core.Tests.Harness;
using Pb.Core.Time;
using Xunit;

namespace Pb.Core.Tests;

/// <summary>
/// Exercises the command-line application through its own entry point, with output and clock
/// injected. The <c>run</c> tests start a real bridge over loopback sockets and stop it by
/// cancelling, which is what Ctrl+C does in production.
/// </summary>
public sealed class BridgeCliTests : IDisposable
{
    private readonly CancellationTokenSource _testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"pb-cli-{Guid.NewGuid():N}");
    private readonly StringWriter _output = new StringWriter();
    private readonly StringWriter _error = new StringWriter();

    private CancellationToken Ct => _testTimeout.Token;

    private string Out => _output.ToString();

    private string Err => _error.ToString();

    public BridgeCliTests() => Directory.CreateDirectory(_directory);

    private BridgeCli Cli(ITimeSource? time = null) => new BridgeCli(
        _output,
        _error,
        time,
        new RouterOptions(SupervisionInterval: TimeSpan.FromSeconds(60)));

    /// <summary>Writes a configuration file and returns its path.</summary>
    private string WriteConfig(string yaml, string name = "config.yaml")
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, yaml);
        return path;
    }

    /// <summary>A valid configuration wired entirely to loopback UDP, so it needs no hardware.</summary>
    private static string ValidConfig(int listenPort, int sendPort) => $"""
        bridge:
          name: cli-demo

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
            transform:
              scale: 0.1
              unit: bar
        """;

    [Fact]
    public async Task Help_PrintsUsageAndSucceeds()
    {
        int exit = await Cli().ExecuteAsync(["--help"], Ct);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("bridge run <config.yaml>", Out, StringComparison.Ordinal);
        Assert.Contains("bridge check <config.yaml>", Out, StringComparison.Ordinal);
        Assert.Empty(Err);
    }

    [Fact]
    public async Task Version_PrintsTheProductAndVersion()
    {
        int exit = await Cli().ExecuteAsync(["--version"], Ct);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal($"{BridgeCli.ProductName} {BridgeCli.Version}", Out.Trim());
    }

    [Fact]
    public async Task NoArguments_IsAUsageError()
    {
        int exit = await Cli().ExecuteAsync([], Ct);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("No command given", Err, StringComparison.Ordinal);
        Assert.Contains("Usage:", Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownCommand_IsAUsageError()
    {
        int exit = await Cli().ExecuteAsync(["frobnicate"], Ct);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("Unknown command 'frobnicate'", Err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("check")]
    [InlineData("run")]
    public async Task ACommandWithoutAPath_IsAUsageError(string command)
    {
        int exit = await Cli().ExecuteAsync([command], Ct);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("needs the path of a configuration file", Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingFile_IsAConfigurationError()
    {
        int exit = await Cli().ExecuteAsync(["check", Path.Combine(_directory, "nope.yaml")], Ct);

        Assert.Equal(ExitCodes.InvalidConfiguration, exit);
        Assert.Contains("No configuration file at", Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_AValidConfiguration_PrintsTheTopologyAndSucceeds()
    {
        string path = WriteConfig(ValidConfig(UdpProbe.FreePort(), UdpProbe.FreePort()));

        int exit = await Cli().ExecuteAsync(["check", path], Ct);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("cli-demo — 2 endpoint(s), 2 channel(s), 1 route(s), 1 enabled", Out, StringComparison.Ordinal);
        Assert.Contains("level_raw (offset:0)", Out, StringComparison.Ordinal);
        Assert.Contains("on change", Out, StringComparison.Ordinal);
        Assert.Contains("x0.1 bar", Out, StringComparison.Ordinal);
        Assert.Contains("is valid", Out, StringComparison.Ordinal);
        Assert.Empty(Err);
    }

    [Fact]
    public async Task Check_DoesNotBindOrConnectAnything()
    {
        int listenPort = UdpProbe.FreePort();
        string path = WriteConfig(ValidConfig(listenPort, UdpProbe.FreePort()));

        Assert.Equal(ExitCodes.Success, await Cli().ExecuteAsync(["check", path], Ct));

        // The port the configuration names must still be free, so 'check' is safe against a live plant.
        using System.Net.Sockets.UdpClient probe = new System.Net.Sockets.UdpClient(
            new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, listenPort));
        Assert.NotNull(probe.Client);
    }

    [Fact]
    public async Task Check_AMalformedFile_ReportsTheLineAndFails()
    {
        string path = WriteConfig("endpoints: [a, b]\n");

        int exit = await Cli().ExecuteAsync(["check", path], Ct);

        Assert.Equal(ExitCodes.InvalidConfiguration, exit);
        Assert.Contains("not a valid configuration", Err, StringComparison.Ordinal);
        Assert.Contains("line 1", Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_ASemanticallyInvalidFile_ListsEveryProblem()
    {
        string path = WriteConfig("""
            endpoints:
              - id: field
                type: udp
                listen_port: 15099
            channels:
              - name: a
                endpoint: nope
                address: offset:0
                type: u16
              - name: b
                endpoint: field
                address: offset:2
                type: u16
            routes:
              - id: r
                source: a
                sink: missing
            """);

        int exit = await Cli().ExecuteAsync(["check", path], Ct);

        Assert.Equal(ExitCodes.InvalidConfiguration, exit);
        Assert.Contains("2 problem(s)", Err, StringComparison.Ordinal);
        Assert.Contains("endpoint 'nope'", Err, StringComparison.Ordinal);
        Assert.Contains("channel 'missing'", Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_ABadDriverSetting_IsReportedAgainstItsEndpoint()
    {
        string path = WriteConfig("""
            endpoints:
              - id: field
                type: udp
                listen_port: 15099
              - id: line
                type: serial
                port: /dev/ttyUSB0
            channels:
              - name: a
                endpoint: field
                address: offset:0
                type: u16
              - name: b
                endpoint: line
                address: offset:0
                type: u16
            routes:
              - id: r
                source: a
                sink: b
            """);

        int exit = await Cli().ExecuteAsync(["check", path], Ct);

        Assert.Equal(ExitCodes.InvalidConfiguration, exit);
        Assert.Contains("endpoint 'line'", Err, StringComparison.Ordinal);
        Assert.Contains("'frame_bytes' is required", Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_ModbusOverSerial_ReportsTheSpecGate()
    {
        string path = WriteConfig("""
            endpoints:
              - id: plc
                type: modbus-rtu
                port: /dev/ttyUSB0
              - id: scada
                type: udp
                host: 127.0.0.1
                port: 15099
            channels:
              - name: a
                endpoint: plc
                address: holding:0
                type: u16
              - name: b
                endpoint: scada
                address: offset:0
                type: f32
            routes:
              - id: r
                source: a
                sink: b
            """);

        int exit = await Cli().ExecuteAsync(["check", path], Ct);

        Assert.Equal(ExitCodes.InvalidConfiguration, exit);
        Assert.Contains("UNSPECIFIED", Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_MovesDataUntilCancelledAndThenPrintsAReport()
    {
        int listenPort = UdpProbe.FreePort();
        using UdpProbe receiver = new UdpProbe();
        string path = WriteConfig(ValidConfig(listenPort, receiver.Port));
        using UdpProbe sender = new UdpProbe();
        using CancellationTokenSource shutdown = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        Task<int> run = Cli().ExecuteAsync(["run", path, "--stats-interval", "0"], shutdown.Token);

        // Feed the bridge until a converted value comes out the other side.
        byte[]? forwarded = null;
        while (forwarded is null)
        {
            Ct.ThrowIfCancellationRequested();
            await sender.SendAsync(listenPort, [0x00, 0x64], Ct);
            Task<byte[]> datagram = receiver.ReceiveAsync(Ct);
            Task completed = await Task.WhenAny(datagram, Task.Delay(100, Ct));

            if (completed == datagram)
            {
                forwarded = await datagram;
            }
        }

        Assert.Equal(10.0f, System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(forwarded));

        await shutdown.CancelAsync();
        int exit = await run;

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("running 'cli-demo'", Out, StringComparison.Ordinal);
        Assert.Contains("[info] field: connected", Out, StringComparison.Ordinal);
        Assert.Contains("Endpoints", Out, StringComparison.Ordinal);
        Assert.Contains("Routes", Out, StringComparison.Ordinal);
        Assert.Contains("10 bar", Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_Quiet_SuppressesProgressButKeepsTheReport()
    {
        string path = WriteConfig(ValidConfig(UdpProbe.FreePort(), UdpProbe.FreePort()));
        using CancellationTokenSource shutdown = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        Task<int> run = Cli().ExecuteAsync(["run", path, "--quiet", "--stats-interval", "0"], shutdown.Token);
        await WaitForAsync(() => Out.Contains("Ctrl+C", StringComparison.Ordinal), Ct);
        await shutdown.CancelAsync();

        Assert.Equal(ExitCodes.Success, await run);
        Assert.DoesNotContain("[info]", Out, StringComparison.Ordinal);
        Assert.Contains("Routes", Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_PrintsAStatusLineOnTheRequestedInterval()
    {
        ManualTimeSource time = new ManualTimeSource();
        string path = WriteConfig(ValidConfig(UdpProbe.FreePort(), UdpProbe.FreePort()));
        using CancellationTokenSource shutdown = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        Task<int> run = Cli(time).ExecuteAsync(["run", path, "--stats-interval", "5"], shutdown.Token);

        // The stats loop, both supervisors and no periodic route: three delays once settled.
        await time.WaitForPendingDelaysAsync(3, Ct);
        time.AdvanceToNextDelay();
        await WaitForAsync(() => Out.Contains("[status]", StringComparison.Ordinal), Ct);

        await shutdown.CancelAsync();
        Assert.Equal(ExitCodes.Success, await run);
        Assert.Contains("[status] healthy", Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--frobnicate")]
    [InlineData("--stats-interval")]
    [InlineData("--stats-interval,-1")]
    [InlineData("--stats-interval,abc")]
    public async Task Run_ABadOption_IsAUsageError(string options)
    {
        string path = WriteConfig(ValidConfig(UdpProbe.FreePort(), UdpProbe.FreePort()));
        string[] args = ["run", path, .. options.Split(',')];

        int exit = await Cli().ExecuteAsync(args, Ct);

        Assert.Equal(ExitCodes.UsageError, exit);
    }

    [Fact]
    public async Task Run_AnInvalidConfiguration_FailsBeforeStarting()
    {
        string path = WriteConfig("routes:\n");

        int exit = await Cli().ExecuteAsync(["run", path], Ct);

        Assert.Equal(ExitCodes.InvalidConfiguration, exit);
        Assert.Contains("not a valid configuration", Err, StringComparison.Ordinal);
        Assert.DoesNotContain("running", Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_AnEndpointThatCannotOpen_KeepsRunningAndReportsIt()
    {
        int taken = UdpProbe.FreePort();
        using System.Net.Sockets.UdpClient squatter = new System.Net.Sockets.UdpClient(
            new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, taken));
        string path = WriteConfig(ValidConfig(taken, UdpProbe.FreePort()));
        using CancellationTokenSource shutdown = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        Task<int> run = Cli().ExecuteAsync(["run", path, "--stats-interval", "0"], shutdown.Token);
        await WaitForAsync(() => Out.Contains("could not connect", StringComparison.Ordinal), Ct);
        await shutdown.CancelAsync();

        Assert.Equal(ExitCodes.Success, await run);
        Assert.Contains("Faulted", Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_TheShippedExampleConfiguration_Starts()
    {
        // The example is part of the product, so a change that breaks it must fail the build.
        string path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples", "loopback.yaml"));

        Assert.True(File.Exists(path), $"the example configuration should be at {path}");

        int exit = await Cli().ExecuteAsync(["check", path], Ct);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("loopback-demo", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RejectsNullWriters()
    {
        Assert.Throws<ArgumentNullException>(() => new BridgeCli(null!, _error));
        Assert.Throws<ArgumentNullException>(() => new BridgeCli(_output, null!));
    }

    [Fact]
    public async Task ExecuteAsync_RejectsNullArguments()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await Cli().ExecuteAsync(null!, Ct));
    }

    public void Dispose()
    {
        _testTimeout.Dispose();
        _output.Dispose();
        _error.Dispose();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
