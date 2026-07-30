using Pb.Core.Channels;
using Pb.Core.Configuration;
using Pb.Core.Configuration.Yaml;
using Pb.Core.Endpoints;
using Pb.Core.Endpoints.Csv;
using Xunit;

namespace Pb.Core.Tests;

public sealed class CsvFileSinkTests : IDisposable
{
    private readonly CancellationTokenSource _testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"pb-csv-{Guid.NewGuid():N}");

    private CancellationToken Ct => _testTimeout.Token;

    private string Path0 => Path.Combine(_directory, "log.csv");

    private static ChannelSpec Channel(string name = "level", string address = "csv:0") =>
        new ChannelSpec(name, "log", ChannelAddress.Parse(address), DataType.F32);

    private static Sample Value(double value, string? unit = "bar", SampleQuality quality = SampleQuality.Good) =>
        new Sample(value, new DateTimeOffset(2026, 7, 30, 12, 34, 56, 789, TimeSpan.Zero), quality, unit);

    [Fact]
    public async Task Write_AppendsAHeaderAndOneRowPerSample()
    {
        await using CsvFileSink sink = new CsvFileSink("log", new CsvSinkSettings(Path0));
        await sink.ConnectAsync(Ct);

        await sink.WriteAsync(Channel(), Value(10.0), Ct);
        await sink.WriteAsync(Channel("flow"), Value(2.5, "l/min"), Ct);

        string[] lines = await File.ReadAllLinesAsync(Path0, Ct);

        Assert.Equal("timestamp,channel,value,unit,quality", lines[0]);
        Assert.Equal("2026-07-30T12:34:56.789Z,level,10,bar,Good", lines[1]);
        Assert.Equal("2026-07-30T12:34:56.789Z,flow,2.5,l/min,Good", lines[2]);
        Assert.Equal(2, sink.RowsWritten);
    }

    [Fact]
    public async Task Write_CreatesMissingDirectories()
    {
        string nested = Path.Combine(_directory, "a", "b", "log.csv");
        await using CsvFileSink sink = new CsvFileSink("log", new CsvSinkSettings(nested));

        await sink.ConnectAsync(Ct);
        await sink.WriteAsync(Channel(), Value(1.0), Ct);

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public async Task Reopening_AppendsWithoutRepeatingTheHeader()
    {
        await using (CsvFileSink first = new CsvFileSink("log", new CsvSinkSettings(Path0)))
        {
            await first.ConnectAsync(Ct);
            await first.WriteAsync(Channel(), Value(1.0), Ct);
        }

        await using (CsvFileSink second = new CsvFileSink("log", new CsvSinkSettings(Path0)))
        {
            await second.ConnectAsync(Ct);
            await second.WriteAsync(Channel(), Value(2.0), Ct);
        }

        string[] lines = await File.ReadAllLinesAsync(Path0, Ct);

        Assert.Equal(3, lines.Length);
        Assert.Single(lines, static l => l.StartsWith("timestamp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Write_WithHeaderDisabled_StartsWithData()
    {
        await using CsvFileSink sink = new CsvFileSink("log", new CsvSinkSettings(Path0, WriteHeader: false));
        await sink.ConnectAsync(Ct);

        await sink.WriteAsync(Channel(), Value(1.0), Ct);

        string[] lines = await File.ReadAllLinesAsync(Path0, Ct);

        Assert.Single(lines);
        Assert.StartsWith("2026-", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_HonoursACustomDelimiterAndTimestampFormat()
    {
        CsvSinkSettings settings = new CsvSinkSettings(Path0, Delimiter: ";", TimestampFormat: "yyyy-MM-dd HH:mm:ss");
        await using CsvFileSink sink = new CsvFileSink("log", settings);
        await sink.ConnectAsync(Ct);

        await sink.WriteAsync(Channel(), Value(1.5), Ct);

        string[] lines = await File.ReadAllLinesAsync(Path0, Ct);

        Assert.Equal("timestamp;channel;value;unit;quality", lines[0]);
        Assert.Equal("2026-07-30 12:34:56;level;1.5;bar;Good", lines[1]);
    }

    [Fact]
    public async Task Write_QuotesFieldsThatWouldBreakTheLayout()
    {
        await using CsvFileSink sink = new CsvFileSink("log", new CsvSinkSettings(Path0));
        await sink.ConnectAsync(Ct);

        await sink.WriteAsync(Channel("a,b"), Value(1.0, "he said \"hi\""), Ct);

        string[] lines = await File.ReadAllLinesAsync(Path0, Ct);

        Assert.Contains("\"a,b\"", lines[1], StringComparison.Ordinal);
        Assert.Contains("\"he said \"\"hi\"\"\"", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_RecordsBadQualityAndNonFiniteValues()
    {
        await using CsvFileSink sink = new CsvFileSink("log", new CsvSinkSettings(Path0));
        await sink.ConnectAsync(Ct);

        await sink.WriteAsync(Channel(), Value(double.NaN, null, SampleQuality.Bad), Ct);

        string[] lines = await File.ReadAllLinesAsync(Path0, Ct);

        Assert.Equal("2026-07-30T12:34:56.789Z,level,NaN,,Bad", lines[1]);
    }

    [Fact]
    public async Task Write_RoundTripsValuesWithFullPrecision()
    {
        await using CsvFileSink sink = new CsvFileSink("log", new CsvSinkSettings(Path0, WriteHeader: false));
        await sink.ConnectAsync(Ct);

        await sink.WriteAsync(Channel(), Value(1.0 / 3.0), Ct);

        string[] lines = await File.ReadAllLinesAsync(Path0, Ct);
        double parsed = double.Parse(lines[0].Split(',')[2], System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(1.0 / 3.0, parsed);
    }

    [Fact]
    public async Task Write_WithoutFlushingEveryRow_StillPersistsOnDisconnect()
    {
        CsvFileSink sink = new CsvFileSink("log", new CsvSinkSettings(Path0, FlushEveryRow: false));
        await sink.ConnectAsync(Ct);
        await sink.WriteAsync(Channel(), Value(1.0), Ct);

        await sink.DisconnectAsync();

        Assert.Equal(2, (await File.ReadAllLinesAsync(Path0, Ct)).Length);
        await sink.DisposeAsync();
    }

    [Fact]
    public async Task WriteBeforeConnect_IsRejected()
    {
        await using CsvFileSink sink = new CsvFileSink("log", new CsvSinkSettings(Path0));

        EndpointException ex = await Assert.ThrowsAsync<EndpointException>(async () =>
            await sink.WriteAsync(Channel(), Value(1.0), Ct));

        Assert.Contains("not open", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_ToAPathThatIsADirectory_FaultsWithAClearMessage()
    {
        Directory.CreateDirectory(_directory);
        await using CsvFileSink sink = new CsvFileSink("log", new CsvSinkSettings(_directory));

        await Assert.ThrowsAsync<EndpointException>(async () => await sink.ConnectAsync(Ct));
        Assert.Equal(EndpointState.Faulted, sink.State);
    }

    [Fact]
    public async Task Connect_IsIdempotentAndReconnectable()
    {
        await using CsvFileSink sink = new CsvFileSink("log", new CsvSinkSettings(Path0));

        await sink.ConnectAsync(Ct);
        await sink.ConnectAsync(Ct);
        Assert.Equal(EndpointState.Connected, sink.State);

        await sink.DisconnectAsync();
        await sink.DisconnectAsync();
        Assert.Equal(EndpointState.Disconnected, sink.State);

        await sink.ConnectAsync(Ct);
        await sink.WriteAsync(Channel(), Value(1.0), Ct);

        Assert.Equal(1, sink.RowsWritten);
    }

    [Fact]
    public async Task Dispose_RejectsFurtherUse()
    {
        CsvFileSink sink = new CsvFileSink("log", new CsvSinkSettings(Path0));
        await sink.ConnectAsync(Ct);
        await sink.DisposeAsync();
        await sink.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await sink.ConnectAsync(Ct));
    }

    [Theory]
    [InlineData("csv:0", ChannelRole.Sink, true)]
    [InlineData("csv:1", ChannelRole.Sink, false)]
    [InlineData("offset:0", ChannelRole.Sink, false)]
    [InlineData("csv:0", ChannelRole.Source, false)]
    public void Supports_RequiresTheCsvAddressAndTheSinkDirection(string address, ChannelRole role, bool supported)
    {
        CsvFileSink sink = new CsvFileSink("log", new CsvSinkSettings(Path0));

        Assert.Equal(supported, sink.Supports(Channel("level", address), role, out string? error));
        Assert.Equal(supported, error is null);
    }

    [Fact]
    public void Endpoint_ExposesItsIdentityForLogsAndTheMonitor()
    {
        CsvFileSink sink = new CsvFileSink("log", new CsvSinkSettings(Path0));

        Assert.Equal("log", sink.Id);
        Assert.Equal("csv", sink.Kind);
        Assert.Equal(Path0, sink.Target);
    }

    [Fact]
    public void Constructor_RejectsMissingIdentityAndPath()
    {
        Assert.Throws<ArgumentException>(() => new CsvFileSink(" ", new CsvSinkSettings(Path0)));
        Assert.Throws<ArgumentNullException>(() => new CsvFileSink("log", null!));
        Assert.Throws<ArgumentException>(() => new CsvFileSink("log", new CsvSinkSettings(" ")));
    }

    public void Dispose()
    {
        _testTimeout.Dispose();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

public sealed class CsvSinkSettingsTests
{
    private static EndpointOptions Options(string body)
    {
        BridgeConfig config = BridgeConfigLoader.Load($"""
            endpoints:
              - id: log
            {body}
              - id: src
                type: udp
                listen_port: 5099
            channels:
              - name: a
                endpoint: src
                address: offset:0
                type: u16
              - name: b
                endpoint: log
                address: csv:0
                type: f32
            routes:
              - id: r
                source: a
                sink: b
            """);

        return config.Endpoint("log").Options;
    }

    [Fact]
    public void Settings_ReadDefaults()
    {
        CsvSinkSettings settings = CsvSinkSettings.FromOptions(Options("""
                type: csv
                path: out/log.csv
            """));

        Assert.Equal("out/log.csv", settings.Path);
        Assert.True(settings.WriteHeader);
        Assert.Equal(",", settings.Delimiter);
        Assert.True(settings.FlushEveryRow);
    }

    [Fact]
    public void Settings_ReadEveryDocumentedKey()
    {
        CsvSinkSettings settings = CsvSinkSettings.FromOptions(Options("""
                type: csv
                path: out/log.csv
                header: false
                delimiter: ";"
                flush_every_row: false
                timestamp_format: "HH:mm:ss"
            """));

        Assert.False(settings.WriteHeader);
        Assert.Equal(";", settings.Delimiter);
        Assert.False(settings.FlushEveryRow);
        Assert.Equal("HH:mm:ss", settings.TimestampFormat);
    }

    [Fact]
    public void Settings_MissingPath_IsRejected()
    {
        Assert.Throws<YamlException>(() => CsvSinkSettings.FromOptions(Options("    type: csv")));
    }

    [Fact]
    public void Settings_UnknownKey_IsRejected()
    {
        Assert.Throws<YamlException>(() => CsvSinkSettings.FromOptions(Options("""
                type: csv
                path: out/log.csv
                seperator: ";"
            """)));
    }

    [Fact]
    public void Settings_AnInvalidTimestampFormat_IsRejected()
    {
        YamlException ex = Assert.Throws<YamlException>(() => CsvSinkSettings.FromOptions(Options("""
                type: csv
                path: out/log.csv
                timestamp_format: "yyyy-MM-dd'T"
            """)));

        Assert.Contains("timestamp_format", ex.Message, StringComparison.Ordinal);
    }
}
