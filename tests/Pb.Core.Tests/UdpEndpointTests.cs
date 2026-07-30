using System.Buffers.Binary;
using Pb.Core.Channels;
using Pb.Core.Endpoints;
using Pb.Core.Endpoints.Udp;
using Pb.Core.Tests.Harness;
using Pb.Core.Time;
using Xunit;

namespace Pb.Core.Tests;

public sealed class UdpEndpointTests
{
    private readonly CancellationTokenSource _testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

    private CancellationToken Ct => _testTimeout.Token;

    private static ChannelSpec Channel(
        string address = "offset:0",
        DataType type = DataType.F32,
        ByteOrder order = ByteOrder.BigEndian,
        string name = "c") =>
        new ChannelSpec(name, "udp", ChannelAddress.Parse(address), type, order);

    private static Sample Value(double value, string? unit = null) =>
        new Sample(value, new ManualTimeSource().UtcNow, SampleQuality.Good, unit);

    [Fact]
    public async Task Send_EncodesTheValueAtItsChannelOffset()
    {
        using UdpProbe probe = new UdpProbe();
        await using UdpEndpoint endpoint = new UdpEndpoint("out", new UdpSettings("127.0.0.1", probe.Port));
        await endpoint.ConnectAsync(Ct);

        Task<byte[]> received = probe.ReceiveAsync(Ct);
        await endpoint.WriteAsync(Channel(), Value(10.0), Ct);

        byte[] payload = await received;

        Assert.Equal(4, payload.Length);
        Assert.Equal(10.0f, BinaryPrimitives.ReadSingleBigEndian(payload));
        Assert.Equal(1, endpoint.DatagramsSent);
    }

    [Fact]
    public async Task Send_PacksSeveralChannelsIntoOnePayloadLayout()
    {
        using UdpProbe probe = new UdpProbe();
        await using UdpEndpoint endpoint = new UdpEndpoint("out", new UdpSettings("127.0.0.1", probe.Port, FrameBytes: 8));
        await endpoint.ConnectAsync(Ct);

        Task<byte[]> first = probe.ReceiveAsync(Ct);
        await endpoint.WriteAsync(Channel("offset:0", DataType.F32, name: "a"), Value(1.5), Ct);
        byte[] firstPayload = await first;

        Task<byte[]> second = probe.ReceiveAsync(Ct);
        await endpoint.WriteAsync(Channel("offset:4", DataType.U32, name: "b"), Value(7), Ct);
        byte[] secondPayload = await second;

        Assert.Equal(8, firstPayload.Length);
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleBigEndian(firstPayload));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(firstPayload.AsSpan(4)));

        // The second write keeps the first channel's value, because the payload persists.
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleBigEndian(secondPayload));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32BigEndian(secondPayload.AsSpan(4)));
    }

    [Fact]
    public async Task Send_WithoutADeclaredFrameLength_GrowsToFitTheHighestOffset()
    {
        using UdpProbe probe = new UdpProbe();
        await using UdpEndpoint endpoint = new UdpEndpoint("out", new UdpSettings("127.0.0.1", probe.Port));
        await endpoint.ConnectAsync(Ct);

        Task<byte[]> first = probe.ReceiveAsync(Ct);
        await endpoint.WriteAsync(Channel("offset:0", DataType.U16, name: "a"), Value(1), Ct);
        Assert.Equal(2, (await first).Length);

        Task<byte[]> second = probe.ReceiveAsync(Ct);
        await endpoint.WriteAsync(Channel("offset:6", DataType.U16, name: "b"), Value(2), Ct);
        byte[] grown = await second;

        Assert.Equal(8, grown.Length);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(grown));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(grown.AsSpan(6)));
    }

    [Fact]
    public async Task Send_HonoursTheChannelByteOrder()
    {
        using UdpProbe probe = new UdpProbe();
        await using UdpEndpoint endpoint = new UdpEndpoint("out", new UdpSettings("127.0.0.1", probe.Port));
        await endpoint.ConnectAsync(Ct);

        Task<byte[]> received = probe.ReceiveAsync(Ct);
        await endpoint.WriteAsync(Channel("offset:0", DataType.U32, ByteOrder.LittleEndian), Value(0x11223344), Ct);

        Assert.Equal([0x44, 0x33, 0x22, 0x11], await received);
    }

    [Fact]
    public async Task Send_BeyondADeclaredFrameLength_IsRejected()
    {
        using UdpProbe probe = new UdpProbe();
        await using UdpEndpoint endpoint = new UdpEndpoint("out", new UdpSettings("127.0.0.1", probe.Port, FrameBytes: 4));
        await endpoint.ConnectAsync(Ct);

        EndpointException ex = await Assert.ThrowsAsync<EndpointException>(async () =>
            await endpoint.WriteAsync(Channel("offset:4", DataType.U32), Value(1), Ct));

        Assert.Contains("the frame is 4", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Receive_MakesTheLatestFrameReadableByChannel()
    {
        int listenPort = UdpProbe.FreePort();
        using UdpProbe probe = new UdpProbe();
        await using UdpEndpoint endpoint = new UdpEndpoint("in", new UdpSettings(ListenPort: listenPort, BindAddress: "127.0.0.1"));
        await endpoint.ConnectAsync(Ct);

        ValueTask<ReadOnlyMemory<byte>> pending = endpoint.ReceiveFrameAsync(Ct);
        await probe.SendAsync(listenPort, [0x41, 0x20, 0x00, 0x00, 0x00, 0x2A], Ct);
        await pending;

        ReadOnlyMemory<byte> level = await endpoint.ReadAsync(Channel("offset:0", DataType.F32), Ct);
        ReadOnlyMemory<byte> count = await endpoint.ReadAsync(Channel("offset:4", DataType.U16), Ct);

        Assert.Equal(10.0, ValueCodec.Decode(level.Span, DataType.F32));
        Assert.Equal(42.0, ValueCodec.Decode(count.Span, DataType.U16));
        Assert.Equal(1, endpoint.FramesReceived);
    }

    [Fact]
    public async Task Receive_WakesEveryWaiterWithTheSameFrame()
    {
        int listenPort = UdpProbe.FreePort();
        using UdpProbe probe = new UdpProbe();
        await using UdpEndpoint endpoint = new UdpEndpoint("in", new UdpSettings(ListenPort: listenPort, BindAddress: "127.0.0.1"));
        await endpoint.ConnectAsync(Ct);

        Task<ReadOnlyMemory<byte>>[] waiters =
        [
            endpoint.ReceiveFrameAsync(Ct).AsTask(),
            endpoint.ReceiveFrameAsync(Ct).AsTask(),
            endpoint.ReceiveFrameAsync(Ct).AsTask(),
        ];

        await probe.SendAsync(listenPort, [0x01, 0x02], Ct);
        ReadOnlyMemory<byte>[] frames = await Task.WhenAll(waiters);

        Assert.All(frames, frame => Assert.Equal([0x01, 0x02], frame.ToArray()));
    }

    [Fact]
    public async Task Receive_SuccessiveFramesReplaceTheReadableValue()
    {
        int listenPort = UdpProbe.FreePort();
        using UdpProbe probe = new UdpProbe();
        await using UdpEndpoint endpoint = new UdpEndpoint("in", new UdpSettings(ListenPort: listenPort, BindAddress: "127.0.0.1"));
        await endpoint.ConnectAsync(Ct);

        for (int i = 1; i <= 3; i++)
        {
            ValueTask<ReadOnlyMemory<byte>> pending = endpoint.ReceiveFrameAsync(Ct);
            await probe.SendAsync(listenPort, [0x00, (byte)i], Ct);
            await pending;
        }

        ReadOnlyMemory<byte> latest = await endpoint.ReadAsync(Channel("offset:0", DataType.U16), Ct);

        Assert.Equal(3.0, ValueCodec.Decode(latest.Span, DataType.U16));
        Assert.Equal(3, endpoint.FramesReceived);
    }

    [Fact]
    public async Task Read_BeforeAnyFrameArrives_ReportsThatThereIsNoValueYet()
    {
        await using UdpEndpoint endpoint = new UdpEndpoint("in", new UdpSettings(ListenPort: UdpProbe.FreePort(), BindAddress: "127.0.0.1"));
        await endpoint.ConnectAsync(Ct);

        EndpointException ex = await Assert.ThrowsAsync<EndpointException>(async () =>
            await endpoint.ReadAsync(Channel(), Ct));

        Assert.Contains("has not received a datagram yet", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_PastTheEndOfTheReceivedFrame_IsReported()
    {
        int listenPort = UdpProbe.FreePort();
        using UdpProbe probe = new UdpProbe();
        await using UdpEndpoint endpoint = new UdpEndpoint("in", new UdpSettings(ListenPort: listenPort, BindAddress: "127.0.0.1"));
        await endpoint.ConnectAsync(Ct);

        ValueTask<ReadOnlyMemory<byte>> pending = endpoint.ReceiveFrameAsync(Ct);
        await probe.SendAsync(listenPort, [0x01, 0x02], Ct);
        await pending;

        EndpointException ex = await Assert.ThrowsAsync<EndpointException>(async () =>
            await endpoint.ReadAsync(Channel("offset:0", DataType.F64), Ct));

        Assert.Contains("past the end", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendOnlyEndpoint_CannotBeReadAndReceiveOnlyEndpointCannotBeWritten()
    {
        await using UdpEndpoint sendOnly = new UdpEndpoint("out", new UdpSettings("127.0.0.1", 9999));
        await using UdpEndpoint receiveOnly = new UdpEndpoint("in", new UdpSettings(ListenPort: UdpProbe.FreePort(), BindAddress: "127.0.0.1"));
        await sendOnly.ConnectAsync(Ct);
        await receiveOnly.ConnectAsync(Ct);

        await Assert.ThrowsAsync<EndpointException>(async () => await sendOnly.ReadAsync(Channel(), Ct));
        await Assert.ThrowsAsync<EndpointException>(async () => await sendOnly.ReceiveFrameAsync(Ct));
        await Assert.ThrowsAsync<EndpointException>(async () => await receiveOnly.WriteAsync(Channel(), Value(1), Ct));
    }

    [Fact]
    public async Task Endpoint_ThatCanSendAndReceive_DoesBoth()
    {
        int bridgeListenPort = UdpProbe.FreePort();
        using UdpProbe probe = new UdpProbe();
        await using UdpEndpoint endpoint = new UdpEndpoint(
            "duplex",
            new UdpSettings("127.0.0.1", probe.Port, bridgeListenPort, "127.0.0.1"));
        await endpoint.ConnectAsync(Ct);

        ValueTask<ReadOnlyMemory<byte>> inbound = endpoint.ReceiveFrameAsync(Ct);
        await probe.SendAsync(bridgeListenPort, [0x00, 0x7B], Ct);
        await inbound;

        Task<byte[]> outbound = probe.ReceiveAsync(Ct);
        await endpoint.WriteAsync(Channel("offset:0", DataType.U16), Value(456), Ct);

        Assert.Equal(123.0, ValueCodec.Decode((await endpoint.ReadAsync(Channel("offset:0", DataType.U16), Ct)).Span, DataType.U16));
        Assert.Equal(456, BinaryPrimitives.ReadUInt16BigEndian(await outbound));
        Assert.Contains("→", endpoint.Target, StringComparison.Ordinal);
        Assert.Contains("←", endpoint.Target, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("offset:0", ChannelRole.Sink, true, null)]
    [InlineData("byte:2", ChannelRole.Sink, true, null)]
    [InlineData("holding:0", ChannelRole.Sink, false, "not a frame offset")]
    [InlineData("offset:0", ChannelRole.Source, false, "no 'listen_port'")]
    public void Supports_ChecksAddressSpaceAndDirection(string address, ChannelRole role, bool supported, string? fragment)
    {
        UdpEndpoint endpoint = new UdpEndpoint("out", new UdpSettings("127.0.0.1", 9999));

        Assert.Equal(supported, endpoint.Supports(Channel(address), role, out string? error));

        if (fragment is not null)
        {
            Assert.Contains(fragment, error!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Supports_RejectsASinkChannelPastADeclaredFrameLength()
    {
        UdpEndpoint endpoint = new UdpEndpoint("out", new UdpSettings("127.0.0.1", 9999, FrameBytes: 4));

        Assert.True(endpoint.Supports(Channel("offset:0", DataType.F32), ChannelRole.Sink, out _));
        Assert.False(endpoint.Supports(Channel("offset:2", DataType.F32), ChannelRole.Sink, out _));
    }

    [Fact]
    public async Task WriteBeforeConnect_IsRejected()
    {
        await using UdpEndpoint endpoint = new UdpEndpoint("out", new UdpSettings("127.0.0.1", 9999));

        EndpointException ex = await Assert.ThrowsAsync<EndpointException>(async () =>
            await endpoint.WriteAsync(Channel(), Value(1), Ct));

        Assert.Contains("not connected", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_IsIdempotentAndReconnectable()
    {
        using UdpProbe probe = new UdpProbe();
        await using UdpEndpoint endpoint = new UdpEndpoint("out", new UdpSettings("127.0.0.1", probe.Port));

        await endpoint.ConnectAsync(Ct);
        await endpoint.ConnectAsync(Ct);
        Assert.Equal(EndpointState.Connected, endpoint.State);

        await endpoint.DisconnectAsync();
        Assert.Equal(EndpointState.Disconnected, endpoint.State);

        await endpoint.ConnectAsync(Ct);
        Task<byte[]> received = probe.ReceiveAsync(Ct);
        await endpoint.WriteAsync(Channel("offset:0", DataType.U16), Value(5), Ct);

        Assert.Equal(5, BinaryPrimitives.ReadUInt16BigEndian(await received));
    }

    [Fact]
    public async Task Connect_ToAPortAlreadyInUse_FaultsWithATransportError()
    {
        int port = UdpProbe.FreePort();
        await using UdpEndpoint first = new UdpEndpoint("in1", new UdpSettings(ListenPort: port, BindAddress: "127.0.0.1"));
        await first.ConnectAsync(Ct);

        await using UdpEndpoint second = new UdpEndpoint("in2", new UdpSettings(ListenPort: port, BindAddress: "127.0.0.1"));

        await Assert.ThrowsAsync<EndpointException>(async () => await second.ConnectAsync(Ct));
        Assert.Equal(EndpointState.Faulted, second.State);
    }

    [Fact]
    public async Task Connect_WithAnUnparseableBindAddress_FaultsWithATransportError()
    {
        await using UdpEndpoint endpoint = new UdpEndpoint("in", new UdpSettings(ListenPort: 9999, BindAddress: "not-an-address"));

        await Assert.ThrowsAsync<EndpointException>(async () => await endpoint.ConnectAsync(Ct));
    }

    [Fact]
    public async Task Dispose_StopsTheReceiveLoopAndRejectsFurtherUse()
    {
        UdpEndpoint endpoint = new UdpEndpoint("in", new UdpSettings(ListenPort: UdpProbe.FreePort(), BindAddress: "127.0.0.1"));
        await endpoint.ConnectAsync(Ct);
        await endpoint.DisposeAsync();
        await endpoint.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await endpoint.ConnectAsync(Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await endpoint.ReceiveFrameAsync(Ct));
    }

    [Fact]
    public void Constructor_RejectsAnEndpointThatCanNeitherSendNorReceive()
    {
        Assert.Throws<ArgumentException>(() => new UdpEndpoint("x", new UdpSettings()));
        Assert.Throws<ArgumentException>(() => new UdpEndpoint(" ", new UdpSettings("127.0.0.1", 1)));
        Assert.Throws<ArgumentNullException>(() => new UdpEndpoint("x", null!));
    }

    [Fact]
    public async Task ReceiveFrame_IsCancellable()
    {
        await using UdpEndpoint endpoint = new UdpEndpoint("in", new UdpSettings(ListenPort: UdpProbe.FreePort(), BindAddress: "127.0.0.1"));
        await endpoint.ConnectAsync(Ct);

        using CancellationTokenSource cancelled = new CancellationTokenSource();
        ValueTask<ReadOnlyMemory<byte>> pending = endpoint.ReceiveFrameAsync(cancelled.Token);
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }
}
