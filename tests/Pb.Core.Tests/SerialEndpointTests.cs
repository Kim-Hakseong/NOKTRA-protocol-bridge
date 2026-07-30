using System.IO.Ports;
using Pb.Core.Channels;
using Pb.Core.Configuration;
using Pb.Core.Configuration.Yaml;
using Pb.Core.Endpoints;
using Pb.Core.Endpoints.Serial;
using Pb.Core.Tests.Harness;
using Xunit;

namespace Pb.Core.Tests;

public sealed class FrameReaderTests
{
    private readonly CancellationTokenSource _testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

    private CancellationToken Ct => _testTimeout.Token;

    [Fact]
    public async Task FixedFraming_CutsTheStreamIntoEqualFrames()
    {
        using MemoryStream stream = new MemoryStream([1, 2, 3, 4, 5, 6]);
        FrameReader reader = new FrameReader(stream, FramingMode.Fixed, frameBytes: 3);

        Assert.Equal([1, 2, 3], (await reader.ReadFrameAsync(Ct)).ToArray());
        Assert.Equal([4, 5, 6], (await reader.ReadFrameAsync(Ct)).ToArray());
    }

    [Fact]
    public async Task FixedFraming_APartialFrameAtEndOfStream_IsAnError()
    {
        using MemoryStream stream = new MemoryStream([1, 2]);
        FrameReader reader = new FrameReader(stream, FramingMode.Fixed, frameBytes: 3);

        await Assert.ThrowsAsync<EndOfStreamException>(async () => await reader.ReadFrameAsync(Ct));
    }

    [Fact]
    public async Task DelimiterFraming_ReturnsFramesWithoutTheDelimiter()
    {
        using MemoryStream stream = new MemoryStream([0x41, 0x42, (byte)'\n', 0x43, (byte)'\n']);
        FrameReader reader = new FrameReader(stream, FramingMode.Delimiter);

        Assert.Equal([0x41, 0x42], (await reader.ReadFrameAsync(Ct)).ToArray());
        Assert.Equal([0x43], (await reader.ReadFrameAsync(Ct)).ToArray());
    }

    [Fact]
    public async Task DelimiterFraming_AnEmptyFrameIsValid()
    {
        using MemoryStream stream = new MemoryStream([(byte)'\n', 0x41, (byte)'\n']);
        FrameReader reader = new FrameReader(stream, FramingMode.Delimiter);

        Assert.Empty((await reader.ReadFrameAsync(Ct)).ToArray());
        Assert.Equal([0x41], (await reader.ReadFrameAsync(Ct)).ToArray());
    }

    [Fact]
    public async Task DelimiterFraming_ACustomDelimiterIsHonoured()
    {
        using MemoryStream stream = new MemoryStream([0x01, 0x02, 0x03, 0x00, 0x04, 0x00]);
        FrameReader reader = new FrameReader(stream, FramingMode.Delimiter, delimiter: 0x00);

        Assert.Equal([0x01, 0x02, 0x03], (await reader.ReadFrameAsync(Ct)).ToArray());
        Assert.Equal([0x04], (await reader.ReadFrameAsync(Ct)).ToArray());
    }

    [Fact]
    public async Task DelimiterFraming_StreamEndingMidFrame_IsAnError()
    {
        using MemoryStream stream = new MemoryStream([0x41, 0x42]);
        FrameReader reader = new FrameReader(stream, FramingMode.Delimiter);

        EndOfStreamException ex = await Assert.ThrowsAsync<EndOfStreamException>(async () => await reader.ReadFrameAsync(Ct));

        Assert.Contains("without the 0x0A delimiter", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DelimiterFraming_AFrameLongerThanTheLimit_IsRejectedAsAConfigurationMismatch()
    {
        using MemoryStream stream = new MemoryStream(Enumerable.Repeat((byte)0x41, 32).ToArray());
        FrameReader reader = new FrameReader(stream, FramingMode.Delimiter, maxFrameBytes: 8);

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadFrameAsync(Ct));

        Assert.Contains("does not match the line", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DelimiterFraming_AFrameExactlyAtTheLimit_IsAccepted()
    {
        byte[] data = [.. Enumerable.Repeat((byte)0x41, 8), (byte)'\n'];
        using MemoryStream stream = new MemoryStream(data);
        FrameReader reader = new FrameReader(stream, FramingMode.Delimiter, maxFrameBytes: 8);

        Assert.Equal(8, (await reader.ReadFrameAsync(Ct)).Length);
    }

    [Fact]
    public async Task Reader_AssemblesFramesFromBytesArrivingOneAtATime()
    {
        using LoopbackStream stream = new LoopbackStream();
        FrameReader reader = new FrameReader(stream, FramingMode.Fixed, frameBytes: 4);

        ValueTask<ReadOnlyMemory<byte>> pending = reader.ReadFrameAsync(Ct);
        stream.Feed([0x01]);
        stream.Feed([0x02, 0x03]);
        stream.Feed([0x04]);

        Assert.Equal([0x01, 0x02, 0x03, 0x04], (await pending).ToArray());
    }

    [Theory]
    [InlineData(FramingMode.Fixed, 0, 16)]
    [InlineData(FramingMode.Fixed, -1, 16)]
    [InlineData(FramingMode.Fixed, 32, 16)]
    [InlineData(FramingMode.Delimiter, 0, 0)]
    public void Constructor_RejectsIncoherentFramingSettings(FramingMode mode, int frameBytes, int maxFrameBytes)
    {
        using MemoryStream stream = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameReader(stream, mode, frameBytes, (byte)'\n', maxFrameBytes));
    }

    [Fact]
    public void Constructor_RejectsANullStream()
    {
        Assert.Throws<ArgumentNullException>(() => new FrameReader(null!, FramingMode.Delimiter));
    }
}

public sealed class SerialEndpointTests
{
    private readonly CancellationTokenSource _testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

    private CancellationToken Ct => _testTimeout.Token;

    private static ChannelSpec Channel(
        string address = "offset:0",
        DataType type = DataType.U16,
        ByteOrder order = ByteOrder.BigEndian,
        string name = "c") =>
        new ChannelSpec(name, "line", ChannelAddress.Parse(address), type, order);

    private static Sample Value(double value) =>
        new Sample(value, DateTimeOffset.UnixEpoch, SampleQuality.Good, null);

    private static (SerialEndpoint Endpoint, FakeSerialLine Line) Fixed(int frameBytes = 4)
    {
        FakeSerialLine line = new FakeSerialLine();
        SerialSettings settings = new SerialSettings("/dev/fake", Framing: FramingMode.Fixed, FrameBytes: frameBytes, AppendDelimiter: false);
        return (new SerialEndpoint("line", settings, _ => line), line);
    }

    private static (SerialEndpoint Endpoint, FakeSerialLine Line) Delimited(byte delimiter = (byte)'\n', bool appendDelimiter = true)
    {
        FakeSerialLine line = new FakeSerialLine();
        SerialSettings settings = new SerialSettings(
            "/dev/fake",
            Framing: FramingMode.Delimiter,
            Delimiter: delimiter,
            AppendDelimiter: appendDelimiter,
            MaxFrameBytes: 64);
        return (new SerialEndpoint("line", settings, _ => line), line);
    }

    [Fact]
    public async Task Receive_FixedFrames_BecomeReadableByChannel()
    {
        (SerialEndpoint endpoint, FakeSerialLine line) = Fixed();
        await using SerialEndpoint owned = endpoint;
        await owned.ConnectAsync(Ct);

        ValueTask<ReadOnlyMemory<byte>> pending = owned.ReceiveFrameAsync(Ct);
        line.Feed(0x00, 0x64, 0x00, 0x2A);
        await pending;

        ReadOnlyMemory<byte> first = await owned.ReadAsync(Channel("offset:0"), Ct);
        ReadOnlyMemory<byte> second = await owned.ReadAsync(Channel("offset:2"), Ct);

        Assert.Equal(100.0, ValueCodec.Decode(first.Span, DataType.U16));
        Assert.Equal(42.0, ValueCodec.Decode(second.Span, DataType.U16));
        Assert.Equal(1, owned.FramesReceived);
    }

    [Fact]
    public async Task Receive_DelimitedFrames_AreCutAtTheDelimiter()
    {
        (SerialEndpoint endpoint, FakeSerialLine line) = Delimited();
        await using SerialEndpoint owned = endpoint;
        await owned.ConnectAsync(Ct);

        ValueTask<ReadOnlyMemory<byte>> pending = owned.ReceiveFrameAsync(Ct);
        line.Feed(0x01, 0x02, (byte)'\n');
        ReadOnlyMemory<byte> frame = await pending;

        Assert.Equal([0x01, 0x02], frame.ToArray());
    }

    [Fact]
    public async Task Receive_SuccessiveFramesAreIndependentCopies()
    {
        (SerialEndpoint endpoint, FakeSerialLine line) = Fixed(2);
        await using SerialEndpoint owned = endpoint;
        await owned.ConnectAsync(Ct);

        ValueTask<ReadOnlyMemory<byte>> first = owned.ReceiveFrameAsync(Ct);
        line.Feed(0x00, 0x01);
        ReadOnlyMemory<byte> firstFrame = await first;

        ValueTask<ReadOnlyMemory<byte>> second = owned.ReceiveFrameAsync(Ct);
        line.Feed(0x00, 0x02);
        ReadOnlyMemory<byte> secondFrame = await second;

        Assert.Equal([0x00, 0x01], firstFrame.ToArray());
        Assert.Equal([0x00, 0x02], secondFrame.ToArray());
        Assert.Equal(2, owned.FramesReceived);
    }

    [Fact]
    public async Task Write_FixedFraming_SendsExactlyTheDeclaredFrameLength()
    {
        (SerialEndpoint endpoint, FakeSerialLine line) = Fixed(4);
        await using SerialEndpoint owned = endpoint;
        await owned.ConnectAsync(Ct);

        await owned.WriteAsync(Channel("offset:0", DataType.U16), Value(100), Ct);
        await owned.WriteAsync(Channel("offset:2", DataType.U16, name: "b"), Value(42), Ct);

        Assert.Equal([0x00, 0x64, 0x00, 0x00, 0x00, 0x64, 0x00, 0x2A], line.Written);
        Assert.Equal(2, owned.FramesSent);
    }

    [Fact]
    public async Task Write_DelimitedFraming_AppendsTheDelimiter()
    {
        (SerialEndpoint endpoint, FakeSerialLine line) = Delimited();
        await using SerialEndpoint owned = endpoint;
        await owned.ConnectAsync(Ct);

        await owned.WriteAsync(Channel("offset:0", DataType.U16), Value(1), Ct);

        Assert.Equal([0x00, 0x01, (byte)'\n'], line.Written);
    }

    [Fact]
    public async Task Write_DelimitedFramingWithoutAppending_SendsOnlyThePayload()
    {
        (SerialEndpoint endpoint, FakeSerialLine line) = Delimited(appendDelimiter: false);
        await using SerialEndpoint owned = endpoint;
        await owned.ConnectAsync(Ct);

        await owned.WriteAsync(Channel("offset:0", DataType.U16), Value(1), Ct);

        Assert.Equal([0x00, 0x01], line.Written);
    }

    [Fact]
    public async Task Write_PastADeclaredFixedFrame_IsRejected()
    {
        (SerialEndpoint endpoint, _) = Fixed(2);
        await using SerialEndpoint owned = endpoint;
        await owned.ConnectAsync(Ct);

        EndpointException ex = await Assert.ThrowsAsync<EndpointException>(async () =>
            await owned.WriteAsync(Channel("offset:0", DataType.U32), Value(1), Ct));

        Assert.Contains("the frame is 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_AfterTheLineIsRemoved_FaultsTheEndpoint()
    {
        (SerialEndpoint endpoint, FakeSerialLine line) = Fixed();
        await using SerialEndpoint owned = endpoint;
        await owned.ConnectAsync(Ct);
        line.Break();

        EndpointException ex = await Assert.ThrowsAsync<EndpointException>(async () =>
            await owned.WriteAsync(Channel("offset:0", DataType.U16), Value(1), Ct));

        Assert.Contains("could not write", ex.Message, StringComparison.Ordinal);
        Assert.Equal(EndpointState.Faulted, owned.State);
    }

    [Fact]
    public async Task FramingThatDoesNotMatchTheLine_FaultsTheEndpointInsteadOfSpinning()
    {
        FakeSerialLine line = new FakeSerialLine();
        SerialSettings settings = new SerialSettings(
            "/dev/fake",
            Framing: FramingMode.Delimiter,
            Delimiter: (byte)'\n',
            MaxFrameBytes: 4);
        await using SerialEndpoint endpoint = new SerialEndpoint("line", settings, _ => line);
        await endpoint.ConnectAsync(Ct);

        line.Feed(0x41, 0x41, 0x41, 0x41, 0x41);

        while (endpoint.State != EndpointState.Faulted)
        {
            await Task.Yield();
            Ct.ThrowIfCancellationRequested();
        }

        Assert.Equal(EndpointState.Faulted, endpoint.State);
    }

    [Fact]
    public async Task Read_BeforeAnyFrameArrives_ReportsThatThereIsNoValueYet()
    {
        (SerialEndpoint endpoint, _) = Fixed();
        await using SerialEndpoint owned = endpoint;
        await owned.ConnectAsync(Ct);

        EndpointException ex = await Assert.ThrowsAsync<EndpointException>(async () =>
            await owned.ReadAsync(Channel(), Ct));

        Assert.Contains("has not received a frame yet", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_PastTheEndOfTheReceivedFrame_IsReported()
    {
        (SerialEndpoint endpoint, FakeSerialLine line) = Fixed(2);
        await using SerialEndpoint owned = endpoint;
        await owned.ConnectAsync(Ct);

        ValueTask<ReadOnlyMemory<byte>> pending = owned.ReceiveFrameAsync(Ct);
        line.Feed(0x00, 0x01);
        await pending;

        EndpointException ex = await Assert.ThrowsAsync<EndpointException>(async () =>
            await owned.ReadAsync(Channel("offset:0", DataType.F64), Ct));

        Assert.Contains("past the end", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteBeforeConnect_IsRejected()
    {
        (SerialEndpoint endpoint, _) = Fixed();
        await using SerialEndpoint owned = endpoint;

        EndpointException ex = await Assert.ThrowsAsync<EndpointException>(async () =>
            await owned.WriteAsync(Channel(), Value(1), Ct));

        Assert.Contains("not connected", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_WhenThePortCannotBeOpened_FaultsWithATransportError()
    {
        SerialSettings settings = new SerialSettings("/dev/nonexistent", Framing: FramingMode.Fixed, FrameBytes: 2);
        await using SerialEndpoint endpoint = new SerialEndpoint(
            "line",
            settings,
            _ => throw new IOException("no such device"));

        EndpointException ex = await Assert.ThrowsAsync<EndpointException>(async () => await endpoint.ConnectAsync(Ct));

        Assert.Contains("no such device", ex.Message, StringComparison.Ordinal);
        Assert.Equal(EndpointState.Faulted, endpoint.State);
    }

    [Fact]
    public async Task Disconnect_ClosesTheLineAndIsRepeatable()
    {
        (SerialEndpoint endpoint, FakeSerialLine line) = Fixed();
        SerialEndpoint owned = endpoint;
        await owned.ConnectAsync(Ct);

        await owned.DisconnectAsync();
        await owned.DisconnectAsync();

        Assert.True(line.Disposed);
        Assert.Equal(EndpointState.Disconnected, owned.State);
        await owned.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_RejectsFurtherUse()
    {
        (SerialEndpoint endpoint, _) = Fixed();
        await endpoint.ConnectAsync(Ct);
        await endpoint.DisposeAsync();
        await endpoint.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await endpoint.ConnectAsync(Ct));
    }

    [Theory]
    [InlineData("offset:0", DataType.U16, true)]
    [InlineData("bytes:2", DataType.U16, true)]
    [InlineData("offset:3", DataType.U16, false)]
    [InlineData("holding:0", DataType.U16, false)]
    public void Supports_ChecksTheOffsetFitsTheFrame(string address, DataType type, bool supported)
    {
        (SerialEndpoint endpoint, _) = Fixed(4);

        Assert.Equal(supported, endpoint.Supports(Channel(address, type), ChannelRole.Source, out _));
    }

    [Fact]
    public void Endpoint_ExposesItsIdentityForLogsAndTheMonitor()
    {
        SerialSettings settings = new SerialSettings("/dev/ttyUSB0", 115200, Parity.Even, 7, StopBits.Two, FramingMode.Fixed, 4);
        SerialEndpoint endpoint = new SerialEndpoint("line", settings, _ => new FakeSerialLine());

        Assert.Equal("line", endpoint.Id);
        Assert.Equal("serial", endpoint.Kind);
        Assert.Equal("/dev/ttyUSB0 115200 7E2", endpoint.Target);
    }

    [Fact]
    public void Constructor_RejectsMissingIdentityAndPort()
    {
        SerialSettings settings = new SerialSettings("/dev/fake", Framing: FramingMode.Fixed, FrameBytes: 2);

        Assert.Throws<ArgumentException>(() => new SerialEndpoint(" ", settings, _ => new FakeSerialLine()));
        Assert.Throws<ArgumentNullException>(() => new SerialEndpoint("line", null!));
        Assert.Throws<ArgumentException>(() => new SerialEndpoint("line", settings with { PortName = " " }, _ => new FakeSerialLine()));
    }
}

public sealed class SerialSettingsTests
{
    private static EndpointOptions Options(string body)
    {
        BridgeConfig config = BridgeConfigLoader.Load($"""
            endpoints:
              - id: line
            {body}
            channels:
              - name: a
                endpoint: line
                address: offset:0
                type: u16
              - name: b
                endpoint: line
                address: offset:2
                type: u16
            routes:
              - id: r
                source: a
                sink: b
            """);

        return config.Endpoint("line").Options;
    }

    [Fact]
    public void Settings_ReadDefaultsForAFixedFrameLine()
    {
        SerialSettings settings = SerialSettings.FromOptions(Options("""
                type: serial
                port: /dev/ttyUSB0
                frame_bytes: 8
            """));

        Assert.Equal("/dev/ttyUSB0", settings.PortName);
        Assert.Equal(9600, settings.BaudRate);
        Assert.Equal(Parity.None, settings.Parity);
        Assert.Equal(8, settings.DataBits);
        Assert.Equal(StopBits.One, settings.StopBits);
        Assert.Equal(FramingMode.Fixed, settings.Framing);
        Assert.Equal(8, settings.FrameBytes);
        Assert.False(settings.AppendDelimiter);
    }

    [Fact]
    public void Settings_ReadEveryDocumentedKey()
    {
        SerialSettings settings = SerialSettings.FromOptions(Options("""
                type: serial
                port: COM3
                baud_rate: 115200
                parity: even
                data_bits: 7
                stop_bits: 2
                framing: delimiter
                delimiter: 0x0D
                append_delimiter: false
                max_frame_bytes: 128
            """));

        Assert.Equal("COM3", settings.PortName);
        Assert.Equal(115200, settings.BaudRate);
        Assert.Equal(Parity.Even, settings.Parity);
        Assert.Equal(7, settings.DataBits);
        Assert.Equal(StopBits.Two, settings.StopBits);
        Assert.Equal(FramingMode.Delimiter, settings.Framing);
        Assert.Equal(0x0D, settings.Delimiter);
        Assert.False(settings.AppendDelimiter);
        Assert.Equal(128, settings.MaxFrameBytes);
    }

    [Fact]
    public void Settings_DelimiterFramingDefaultsToAppendingTheDelimiter()
    {
        SerialSettings settings = SerialSettings.FromOptions(Options("""
                type: serial
                port: /dev/ttyUSB0
                framing: delimiter
            """));

        Assert.True(settings.AppendDelimiter);
        Assert.Equal((byte)'\n', settings.Delimiter);
    }

    [Theory]
    [InlineData("delimiter: \"\\n\"", (byte)'\n')]
    [InlineData("delimiter: lf", (byte)'\n')]
    [InlineData("delimiter: cr", (byte)'\r')]
    [InlineData("delimiter: \"\\0\"", (byte)0)]
    [InlineData("delimiter: 10", (byte)10)]
    [InlineData("delimiter: 0xFF", (byte)255)]
    public void Settings_AcceptTheDocumentedDelimiterSpellings(string line, byte expected)
    {
        SerialSettings settings = SerialSettings.FromOptions(Options($"""
                type: serial
                port: /dev/ttyUSB0
                framing: delimiter
                {line}
            """));

        Assert.Equal(expected, settings.Delimiter);
    }

    [Theory]
    [InlineData("framing: sideways")]
    [InlineData("parity: sometimes")]
    [InlineData("stop_bits: 3")]
    [InlineData("data_bits: 9")]
    [InlineData("baud_rate: 0")]
    [InlineData("delimiter: 300")]
    [InlineData("delimiter: nope")]
    [InlineData("max_frame_bytes: 0")]
    [InlineData("prot: /dev/ttyUSB0")]
    public void Settings_InvalidValues_AreRejected(string line)
    {
        Assert.Throws<YamlException>(() => SerialSettings.FromOptions(Options($"""
                type: serial
                port: /dev/ttyUSB0
                frame_bytes: 4
                {line}
            """)));
    }

    [Fact]
    public void Settings_FixedFramingWithoutAFrameLength_IsRejected()
    {
        YamlException ex = Assert.Throws<YamlException>(() => SerialSettings.FromOptions(Options("""
                type: serial
                port: /dev/ttyUSB0
            """)));

        Assert.Contains("'frame_bytes' is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_MissingPort_IsRejected()
    {
        Assert.Throws<YamlException>(() => SerialSettings.FromOptions(Options("    type: serial")));
    }
}
