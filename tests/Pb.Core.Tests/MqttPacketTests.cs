using System.Text;
using Pb.Core.Mqtt;
using Xunit;

namespace Pb.Core.Tests;

/// <summary>
/// Frame-level tests of the MQTT subset, including the pinned PUBLISH golden vector recorded in
/// spec/mqtt-subset.md §8.
/// </summary>
public sealed class MqttPacketTests
{
    [Fact]
    public void Publish_GoldenVector_IsExactlyTheDesignBytes()
    {
        byte[] packet = MqttPacket.BuildPublish("t/a", Encoding.UTF8.GetBytes("21"));

        Assert.Equal([0x30, 0x07, 0x00, 0x03, 0x74, 0x2F, 0x61, 0x32, 0x31], packet);
    }

    [Fact]
    public void Publish_GoldenVector_RemainingLengthIsTopicPlusPayload()
    {
        byte[] packet = MqttPacket.BuildPublish("t/a", Encoding.UTF8.GetBytes("21"));

        Assert.Equal(0x30, packet[0]);
        Assert.True(MqttPacket.TryReadRemainingLength(packet.AsSpan(1), out int remaining, out int used));
        Assert.Equal(7, remaining);
        Assert.Equal(1, used);

        // The fixed header is the type byte plus the Remaining Length field; the rest is counted.
        Assert.Equal(1 + used + remaining, packet.Length);
    }

    [Fact]
    public void Publish_RoundTripsThroughTheParser()
    {
        byte[] payload = Encoding.UTF8.GetBytes("12.5");
        byte[] packet = MqttPacket.BuildPublish("plant/tank1/level", payload, retain: true);

        (string topic, ReadOnlyMemory<byte> parsed, bool retain) = MqttPacket.ParsePublish(packet[0], packet.AsMemory(2));

        Assert.Equal("plant/tank1/level", topic);
        Assert.Equal(payload, parsed.ToArray());
        Assert.True(retain);
    }

    [Fact]
    public void Publish_RetainSetsOnlyTheLowestFlagBit()
    {
        Assert.Equal(0x30, MqttPacket.BuildPublish("t", []) [0]);
        Assert.Equal(0x31, MqttPacket.BuildPublish("t", [], retain: true)[0]);
    }

    [Fact]
    public void Publish_AnEmptyPayloadIsValid()
    {
        byte[] packet = MqttPacket.BuildPublish("t", []);

        Assert.Equal([0x30, 0x03, 0x00, 0x01, 0x74], packet);
    }

    [Fact]
    public void Publish_APayloadCrossingTheOneByteLengthBoundary_UsesTwoLengthBytes()
    {
        // Topic "t" costs 3 bytes, so a 125-byte payload makes the body exactly 128.
        byte[] packet = MqttPacket.BuildPublish("t", new byte[125]);

        Assert.Equal(0x30, packet[0]);
        Assert.Equal([0x80, 0x01], packet.AsSpan(1, 2).ToArray());
        Assert.Equal(3 + 128, packet.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/+/b")]
    [InlineData("a/#")]
    [InlineData("a\0b")]
    public void Publish_AnInvalidTopic_IsRejected(string topic)
    {
        Assert.Throws<ArgumentException>(() => MqttPacket.BuildPublish(topic, []));
    }

    [Fact]
    public void Publish_ATopicLongerThanTheStringLimit_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => MqttPacket.BuildPublish(new string('a', 65_536), []));
    }

    [Fact]
    public void ParsePublish_RejectsQoSAboveZeroAndOtherPacketTypes()
    {
        Assert.Throws<MqttProtocolException>(() => MqttPacket.ParsePublish(0x32, new byte[] { 0x00, 0x01, 0x74 }));
        Assert.Throws<MqttProtocolException>(() => MqttPacket.ParsePublish(0xC0, ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void Connect_CarriesTheProtocolNameLevelFlagsKeepAliveAndClientId()
    {
        byte[] packet = MqttPacket.BuildConnect("pb", keepAlive: 60);

        Assert.Equal(
            [
                0x10, 0x0E,
                0x00, 0x04, 0x4D, 0x51, 0x54, 0x54, // "MQTT"
                0x04,                               // protocol level 3.1.1
                0x02,                               // clean session
                0x00, 0x3C,                         // keep alive 60
                0x00, 0x02, 0x70, 0x62,             // client id "pb"
            ],
            packet);
    }

    [Fact]
    public void Connect_WithoutACleanSession_ClearsThatFlag()
    {
        byte[] packet = MqttPacket.BuildConnect("pb", 60, cleanSession: false);

        Assert.Equal(0x00, packet[9]);
    }

    [Fact]
    public void Connect_WithCredentials_SetsBothFlagsAndAppendsThemInOrder()
    {
        byte[] packet = MqttPacket.BuildConnect("pb", 30, true, "user", "pass");

        Assert.Equal(0xC2, packet[9]);
        Assert.Equal(0x00, packet[10]);
        Assert.Equal(30, packet[11]);

        int at = 12;
        Assert.Equal("pb", MqttPacket.ReadString(packet.AsSpan(at), out int used));
        at += used;
        Assert.Equal("user", MqttPacket.ReadString(packet.AsSpan(at), out used));
        at += used;
        Assert.Equal("pass", MqttPacket.ReadString(packet.AsSpan(at), out used));
        Assert.Equal(packet.Length, at + used);
    }

    [Fact]
    public void Connect_WithAUserNameOnly_SetsOnlyThatFlag()
    {
        byte[] packet = MqttPacket.BuildConnect("pb", 30, true, "user");

        Assert.Equal(0x82, packet[9]);
    }

    [Fact]
    public void Connect_NeverSetsTheWillFlags()
    {
        byte[] packet = MqttPacket.BuildConnect("pb", 60, true, "u", "p");

        Assert.Equal(0, packet[9] & 0x3C);
    }

    [Fact]
    public void Connect_ZeroKeepAliveIsEncodedAsZero()
    {
        byte[] packet = MqttPacket.BuildConnect("pb", 0);

        Assert.Equal([0x00, 0x00], packet.AsSpan(10, 2).ToArray());
    }

    [Fact]
    public void Connect_AZeroLengthClientIdNeedsACleanSession()
    {
        Assert.Equal(0x00, MqttPacket.BuildConnect(string.Empty, 60)[^1]);
        Assert.Throws<ArgumentException>(() => MqttPacket.BuildConnect(string.Empty, 60, cleanSession: false));
    }

    [Fact]
    public void Connect_APasswordWithoutAUserName_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => MqttPacket.BuildConnect("pb", 60, true, null, "pass"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65_536)]
    public void Connect_AKeepAliveOutsideTheFieldRange_IsRejected(int keepAlive)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MqttPacket.BuildConnect("pb", keepAlive));
    }

    [Fact]
    public void ConnAck_Accepted_ReportsTheSessionPresentFlag()
    {
        Assert.False(MqttPacket.ParseConnAck(0x20, [0x00, 0x00]));
        Assert.True(MqttPacket.ParseConnAck(0x20, [0x01, 0x00]));
    }

    [Theory]
    [InlineData(1, MqttConnectReturnCode.UnacceptableProtocolVersion, "protocol version")]
    [InlineData(2, MqttConnectReturnCode.IdentifierRejected, "identifier rejected")]
    [InlineData(3, MqttConnectReturnCode.ServerUnavailable, "server unavailable")]
    [InlineData(4, MqttConnectReturnCode.BadUserNameOrPassword, "bad user name")]
    [InlineData(5, MqttConnectReturnCode.NotAuthorized, "not authorized")]
    public void ConnAck_ARefusal_ThrowsWithTheDecodedCode(byte code, MqttConnectReturnCode expected, string fragment)
    {
        MqttConnectRefusedException ex = Assert.Throws<MqttConnectRefusedException>(() =>
            MqttPacket.ParseConnAck(0x20, [0x00, code]));

        Assert.Equal(expected, ex.KnownCode);
        Assert.Contains(fragment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnAck_AReservedReturnCode_IsSurfacedVerbatim()
    {
        MqttConnectRefusedException ex = Assert.Throws<MqttConnectRefusedException>(() =>
            MqttPacket.ParseConnAck(0x20, [0x00, 0x7F]));

        Assert.Equal(0x7F, ex.ReturnCode);
        Assert.Null(ex.KnownCode);
        Assert.Contains("reserved", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x30, new byte[] { 0x00, 0x00 }, "Expected CONNACK")]
    [InlineData(0x20, new byte[] { 0x00 }, "2 bytes")]
    [InlineData(0x20, new byte[] { 0x00, 0x00, 0x00 }, "2 bytes")]
    [InlineData(0x20, new byte[] { 0x02, 0x00 }, "acknowledge flags")]
    public void ConnAck_AMalformedPacket_IsRejected(byte firstByte, byte[] body, string fragment)
    {
        MqttProtocolException ex = Assert.Throws<MqttProtocolException>(() => MqttPacket.ParseConnAck(firstByte, body));

        Assert.Contains(fragment, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ControlPacketsWithoutABody_AreExactlyTwoBytes()
    {
        Assert.Equal([0xC0, 0x00], MqttPacket.PingReq.ToArray());
        Assert.Equal([0xD0, 0x00], MqttPacket.PingResp.ToArray());
        Assert.Equal([0xE0, 0x00], MqttPacket.Disconnect.ToArray());
    }

    [Theory]
    [InlineData(0, new byte[] { 0x00 })]
    [InlineData(127, new byte[] { 0x7F })]
    [InlineData(128, new byte[] { 0x80, 0x01 })]
    [InlineData(16_383, new byte[] { 0xFF, 0x7F })]
    [InlineData(16_384, new byte[] { 0x80, 0x80, 0x01 })]
    [InlineData(2_097_151, new byte[] { 0xFF, 0xFF, 0x7F })]
    [InlineData(2_097_152, new byte[] { 0x80, 0x80, 0x80, 0x01 })]
    [InlineData(268_435_455, new byte[] { 0xFF, 0xFF, 0xFF, 0x7F })]
    public void RemainingLength_MatchesTheSpecBoundaries(int length, byte[] expected)
    {
        byte[] buffer = new byte[4];

        int written = MqttPacket.WriteRemainingLength(length, buffer);

        Assert.Equal(expected, buffer.AsSpan(0, written).ToArray());
        Assert.Equal(expected.Length, MqttPacket.RemainingLengthSize(length));
        Assert.True(MqttPacket.TryReadRemainingLength(expected, out int decoded, out int used));
        Assert.Equal(length, decoded);
        Assert.Equal(expected.Length, used);
    }

    [Fact]
    public void RemainingLength_AnIncompleteFieldReportsThatMoreBytesAreNeeded()
    {
        Assert.False(MqttPacket.TryReadRemainingLength([0x80], out _, out _));
        Assert.False(MqttPacket.TryReadRemainingLength([], out _, out _));
    }

    [Fact]
    public void RemainingLength_AFifthContinuationByte_IsMalformed()
    {
        Assert.Throws<MqttProtocolException>(() =>
            MqttPacket.TryReadRemainingLength([0x80, 0x80, 0x80, 0x80, 0x01], out _, out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(268_435_456)]
    public void RemainingLength_OutsideTheEncodableRange_IsRejected(int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MqttPacket.RemainingLengthSize(length));
    }

    [Fact]
    public void RemainingLength_ATooSmallDestination_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            byte[] buffer = new byte[1];
            MqttPacket.WriteRemainingLength(128, buffer);
        });
    }

    [Fact]
    public void String_IsLengthPrefixedUtf8()
    {
        byte[] buffer = new byte[16];

        int written = MqttPacket.WriteString("MQTT", buffer);

        Assert.Equal(6, written);
        Assert.Equal([0x00, 0x04, 0x4D, 0x51, 0x54, 0x54], buffer.AsSpan(0, written).ToArray());
        Assert.Equal(6, MqttPacket.StringSize("MQTT"));
    }

    [Fact]
    public void String_CountsUtf8BytesNotCharacters()
    {
        // "온도" is two characters but six UTF-8 bytes.
        Assert.Equal(8, MqttPacket.StringSize("온도"));

        byte[] buffer = new byte[8];
        MqttPacket.WriteString("온도", buffer);

        Assert.Equal(6, buffer[1]);
        Assert.Equal("온도", MqttPacket.ReadString(buffer, out int used));
        Assert.Equal(8, used);
    }

    [Fact]
    public void String_AnEmptyStringIsJustItsLengthPrefix()
    {
        byte[] buffer = new byte[2];

        Assert.Equal(2, MqttPacket.WriteString(string.Empty, buffer));
        Assert.Equal([0x00, 0x00], buffer);
        Assert.Equal(string.Empty, MqttPacket.ReadString(buffer, out _));
    }

    [Fact]
    public void String_ContainingNul_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => MqttPacket.StringSize("a\0b"));
    }

    [Fact]
    public void String_LongerThanTheFieldAllows_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => MqttPacket.StringSize(new string('a', 65_536)));
    }

    [Theory]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0x00, 0x05, 0x61 })]
    public void ReadString_ATruncatedString_IsRejected(byte[] source)
    {
        Assert.Throws<MqttProtocolException>(() => MqttPacket.ReadString(source, out _));
    }

    [Fact]
    public void ReadString_IllFormedUtf8_IsRejected()
    {
        Assert.Throws<MqttProtocolException>(() => MqttPacket.ReadString([0x00, 0x02, 0xC3, 0x28], out _));
    }

    [Fact]
    public void DescribePacketType_NamesImplementedTypesAndFlagsTheRest()
    {
        Assert.Equal("CONNECT", MqttPacket.DescribePacketType(0x10));
        Assert.Equal("PUBLISH", MqttPacket.DescribePacketType(0x30));
        Assert.Equal("DISCONNECT", MqttPacket.DescribePacketType(0xE0));
        Assert.Contains("unimplemented packet type 8", MqttPacket.DescribePacketType(0x82), StringComparison.Ordinal);
    }
}
