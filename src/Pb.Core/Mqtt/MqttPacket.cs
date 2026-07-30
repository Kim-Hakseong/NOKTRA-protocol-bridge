using System.Buffers.Binary;
using System.Text;

namespace Pb.Core.Mqtt;

/// <summary>MQTT control packet types (spec/mqtt-subset.md §1).</summary>
public enum MqttPacketType : byte
{
    Connect = 1,
    ConnAck = 2,
    Publish = 3,
    PingReq = 12,
    PingResp = 13,
    Disconnect = 14,
}

/// <summary>CONNACK return codes (spec §4).</summary>
public enum MqttConnectReturnCode : byte
{
    Accepted = 0,
    UnacceptableProtocolVersion = 1,
    IdentifierRejected = 2,
    ServerUnavailable = 3,
    BadUserNameOrPassword = 4,
    NotAuthorized = 5,
}

/// <summary>A packet that does not conform to spec/mqtt-subset.md.</summary>
public sealed class MqttProtocolException : Exception
{
    public MqttProtocolException(string message)
        : base(message)
    {
    }
}

/// <summary>The broker refused the connection with a CONNACK return code (spec §4).</summary>
public sealed class MqttConnectRefusedException : Exception
{
    public MqttConnectRefusedException(byte returnCode)
        : base($"The broker refused the connection: {MqttPacket.DescribeReturnCode(returnCode)} (0x{returnCode:X2}).")
        => ReturnCode = returnCode;

    /// <summary>The raw return code, preserved even when it is one of the reserved values.</summary>
    public byte ReturnCode { get; }

    /// <summary>The return code as an enum, or null when the code is reserved.</summary>
    public MqttConnectReturnCode? KnownCode => Enum.IsDefined(typeof(MqttConnectReturnCode), ReturnCode)
        ? (MqttConnectReturnCode)ReturnCode
        : null;
}

/// <summary>
/// Encoder and decoder for the MQTT 3.1.1 packets recorded in spec/mqtt-subset.md: CONNECT,
/// CONNACK, PUBLISH at QoS 0, PINGREQ, PINGRESP and DISCONNECT. Nothing else is encodable.
/// </summary>
public static class MqttPacket
{
    /// <summary>Protocol level byte identifying MQTT 3.1.1 (spec §3).</summary>
    public const byte ProtocolLevel = 0x04;

    /// <summary>Protocol name carried in CONNECT (spec §3).</summary>
    public const string ProtocolName = "MQTT";

    /// <summary>Registered TCP port for plain MQTT (spec §7).</summary>
    public const int DefaultPort = 1883;

    /// <summary>Largest value the Remaining Length field can encode (spec §1).</summary>
    public const int MaxRemainingLength = 268_435_455;

    /// <summary>Maximum bytes of a Remaining Length field (spec §1).</summary>
    public const int MaxRemainingLengthBytes = 4;

    /// <summary>Longest UTF-8 encoded string, in bytes (spec §2).</summary>
    public const int MaxStringBytes = 65_535;

    /// <summary>PINGREQ, which has no variable header or payload (spec §6).</summary>
    public static ReadOnlySpan<byte> PingReq => [0xC0, 0x00];

    /// <summary>PINGRESP (spec §6).</summary>
    public static ReadOnlySpan<byte> PingResp => [0xD0, 0x00];

    /// <summary>DISCONNECT (spec §6).</summary>
    public static ReadOnlySpan<byte> Disconnect => [0xE0, 0x00];

    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Number of bytes the Remaining Length field needs for <paramref name="length"/>.</summary>
    public static int RemainingLengthSize(int length)
    {
        if (length is < 0 or > MaxRemainingLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                $"Remaining Length is 0..{MaxRemainingLength} (spec §1).");
        }

        return length switch
        {
            < 128 => 1,
            < 16_384 => 2,
            < 2_097_152 => 3,
            _ => 4,
        };
    }

    /// <summary>Writes a Remaining Length field, seven bits per byte, low group first (spec §1).</summary>
    /// <returns>Number of bytes written.</returns>
    public static int WriteRemainingLength(int length, Span<byte> destination)
    {
        int size = RemainingLengthSize(length);

        if (destination.Length < size)
        {
            throw new ArgumentException($"Need {size} byte(s) for this Remaining Length.", nameof(destination));
        }

        int remaining = length;
        int written = 0;

        do
        {
            byte digit = (byte)(remaining % 128);
            remaining /= 128;

            if (remaining > 0)
            {
                digit |= 0x80;
            }

            destination[written++] = digit;
        }
        while (remaining > 0);

        return written;
    }

    /// <summary>Reads a Remaining Length field.</summary>
    /// <param name="source">Bytes starting at the first Remaining Length byte.</param>
    /// <param name="length">The decoded length.</param>
    /// <param name="bytesUsed">How many bytes the field occupied.</param>
    /// <returns>False when <paramref name="source"/> does not yet hold the whole field.</returns>
    /// <exception cref="MqttProtocolException">The field is longer than four bytes.</exception>
    public static bool TryReadRemainingLength(ReadOnlySpan<byte> source, out int length, out int bytesUsed)
    {
        length = 0;
        bytesUsed = 0;
        int multiplier = 1;

        for (int i = 0; i < source.Length; i++)
        {
            byte digit = source[i];
            length += (digit & 0x7F) * multiplier;
            bytesUsed = i + 1;

            if ((digit & 0x80) == 0)
            {
                return true;
            }

            if (bytesUsed == MaxRemainingLengthBytes)
            {
                throw new MqttProtocolException(
                    "Remaining Length is longer than four bytes, which is malformed (spec §1).");
            }

            multiplier *= 128;
        }

        return false;
    }

    /// <summary>Bytes a UTF-8 encoded string occupies, including its two-byte length prefix (spec §2).</summary>
    public static int StringSize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        int bytes = MeasureUtf8(value);
        return 2 + bytes;
    }

    /// <summary>Writes a UTF-8 encoded string: two-byte big-endian length, then the bytes (spec §2).</summary>
    /// <returns>Number of bytes written.</returns>
    public static int WriteString(string value, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(value);

        int bytes = MeasureUtf8(value);

        if (destination.Length < 2 + bytes)
        {
            throw new ArgumentException($"Need {2 + bytes} byte(s) for this string.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)bytes);
        Utf8.GetBytes(value, destination[2..]);
        return 2 + bytes;
    }

    /// <summary>Reads a UTF-8 encoded string.</summary>
    public static string ReadString(ReadOnlySpan<byte> source, out int bytesUsed)
    {
        if (source.Length < 2)
        {
            throw new MqttProtocolException("A UTF-8 encoded string needs a two-byte length prefix (spec §2).");
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(source);

        if (source.Length < 2 + length)
        {
            throw new MqttProtocolException(
                $"A string declares {length} byte(s) but only {source.Length - 2} are present.");
        }

        bytesUsed = 2 + length;

        try
        {
            return Utf8.GetString(source.Slice(2, length));
        }
        catch (DecoderFallbackException ex)
        {
            throw new MqttProtocolException($"A string is not well-formed UTF-8: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds a CONNECT packet. Will messages are not implemented, so the Will flags are always
    /// zero (spec §3).
    /// </summary>
    /// <param name="clientId">Client identifier; may be empty only when <paramref name="cleanSession"/> is true.</param>
    /// <param name="keepAlive">Keep-alive seconds, 0 to disable.</param>
    /// <param name="cleanSession">Whether the broker discards any previous session state.</param>
    /// <param name="userName">Optional user name.</param>
    /// <param name="password">Optional password; requires <paramref name="userName"/>.</param>
    public static byte[] BuildConnect(
        string clientId,
        int keepAlive,
        bool cleanSession = true,
        string? userName = null,
        string? password = null)
    {
        ArgumentNullException.ThrowIfNull(clientId);

        if (keepAlive is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(keepAlive), keepAlive, "Keep alive is 0..65535 seconds (spec §3).");
        }

        if (clientId.Length == 0 && !cleanSession)
        {
            throw new ArgumentException(
                "A zero-length client identifier is only allowed with a clean session (spec §3).",
                nameof(clientId));
        }

        if (password is not null && userName is null)
        {
            throw new ArgumentException(
                "A password without a user name has no defined position in the payload (spec §3).",
                nameof(password));
        }

        byte flags = 0;

        if (cleanSession)
        {
            flags |= 0x02;
        }

        if (userName is not null)
        {
            flags |= 0x80;
        }

        if (password is not null)
        {
            flags |= 0x40;
        }

        int bodyLength = StringSize(ProtocolName) + 1 + 1 + 2 + StringSize(clientId);

        if (userName is not null)
        {
            bodyLength += StringSize(userName);
        }

        if (password is not null)
        {
            bodyLength += StringSize(password);
        }

        byte[] packet = new byte[1 + RemainingLengthSize(bodyLength) + bodyLength];
        int at = 0;
        packet[at++] = (byte)((byte)MqttPacketType.Connect << 4);
        at += WriteRemainingLength(bodyLength, packet.AsSpan(at));
        at += WriteString(ProtocolName, packet.AsSpan(at));
        packet[at++] = ProtocolLevel;
        packet[at++] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(at), (ushort)keepAlive);
        at += 2;
        at += WriteString(clientId, packet.AsSpan(at));

        if (userName is not null)
        {
            at += WriteString(userName, packet.AsSpan(at));
        }

        if (password is not null)
        {
            at += WriteString(password, packet.AsSpan(at));
        }

        return at == packet.Length
            ? packet
            : throw new InvalidOperationException($"CONNECT length mismatch: wrote {at} of {packet.Length} bytes.");
    }

    /// <summary>
    /// Builds a PUBLISH packet at QoS 0. There is no packet identifier, because that field only
    /// exists for QoS greater than zero (spec §5).
    /// </summary>
    public static byte[] BuildPublish(string topic, ReadOnlySpan<byte> payload, bool retain = false)
    {
        ValidateTopic(topic);

        int bodyLength = StringSize(topic) + payload.Length;
        byte[] packet = new byte[1 + RemainingLengthSize(bodyLength) + bodyLength];
        int at = 0;
        packet[at++] = (byte)(((byte)MqttPacketType.Publish << 4) | (retain ? 0x01 : 0x00));
        at += WriteRemainingLength(bodyLength, packet.AsSpan(at));
        at += WriteString(topic, packet.AsSpan(at));
        payload.CopyTo(packet.AsSpan(at));
        return packet;
    }

    /// <summary>Parses a PUBLISH packet body, returning its topic and payload (spec §5).</summary>
    public static (string Topic, ReadOnlyMemory<byte> Payload, bool Retain) ParsePublish(byte firstByte, ReadOnlyMemory<byte> body)
    {
        if ((firstByte >> 4) != (byte)MqttPacketType.Publish)
        {
            throw new MqttProtocolException($"Packet type 0x{firstByte >> 4:X1} is not PUBLISH.");
        }

        int qos = (firstByte >> 1) & 0x03;

        if (qos != 0)
        {
            throw new MqttProtocolException($"Only QoS 0 is implemented but the packet declares QoS {qos}.");
        }

        string topic = ReadString(body.Span, out int bytesUsed);
        return (topic, body[bytesUsed..], (firstByte & 0x01) != 0);
    }

    /// <summary>Parses a CONNACK body (spec §4).</summary>
    /// <exception cref="MqttConnectRefusedException">The return code is not "accepted".</exception>
    public static bool ParseConnAck(byte firstByte, ReadOnlySpan<byte> body)
    {
        if ((firstByte >> 4) != (byte)MqttPacketType.ConnAck)
        {
            throw new MqttProtocolException(
                $"Expected CONNACK but the packet type is {DescribePacketType(firstByte)}.");
        }

        if (body.Length != 2)
        {
            throw new MqttProtocolException($"A CONNACK body is 2 bytes but {body.Length} were received (spec §4).");
        }

        if ((body[0] & 0xFE) != 0)
        {
            throw new MqttProtocolException(
                $"CONNACK acknowledge flags are 0x{body[0]:X2}; only bit 0 (session present) may be set (spec §4).");
        }

        return body[1] == (byte)MqttConnectReturnCode.Accepted
            ? (body[0] & 0x01) != 0
            : throw new MqttConnectRefusedException(body[1]);
    }

    /// <summary>Validates a PUBLISH topic name (spec §5, S1 §4.7).</summary>
    public static void ValidateTopic(string topic)
    {
        ArgumentNullException.ThrowIfNull(topic);

        if (topic.Length == 0)
        {
            throw new ArgumentException("A topic name must have at least one character (spec §5).", nameof(topic));
        }

        if (topic.Contains('+', StringComparison.Ordinal) || topic.Contains('#', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"A topic name must not contain the wildcards '+' or '#' but '{topic}' does (spec §5).",
                nameof(topic));
        }

        if (topic.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A topic name must not contain U+0000 (spec §5).", nameof(topic));
        }

        if (MeasureUtf8(topic) > MaxStringBytes)
        {
            throw new ArgumentException($"A topic name is at most {MaxStringBytes} UTF-8 bytes (spec §2).", nameof(topic));
        }
    }

    /// <summary>Names a CONNACK return code, keeping reserved codes visible as such (spec §4).</summary>
    public static string DescribeReturnCode(byte code) => code switch
    {
        (byte)MqttConnectReturnCode.Accepted => "connection accepted",
        (byte)MqttConnectReturnCode.UnacceptableProtocolVersion => "unacceptable protocol version",
        (byte)MqttConnectReturnCode.IdentifierRejected => "identifier rejected",
        (byte)MqttConnectReturnCode.ServerUnavailable => "server unavailable",
        (byte)MqttConnectReturnCode.BadUserNameOrPassword => "bad user name or password",
        (byte)MqttConnectReturnCode.NotAuthorized => "not authorized",
        _ => $"reserved return code 0x{code:X2}",
    };

    /// <summary>Names a packet type from a fixed-header first byte, for error messages.</summary>
    public static string DescribePacketType(byte firstByte)
    {
        int type = firstByte >> 4;

        return Enum.IsDefined(typeof(MqttPacketType), (byte)type)
            ? ((MqttPacketType)type).ToString().ToUpperInvariant()
            : $"unimplemented packet type {type}";
    }

    private static int MeasureUtf8(string value)
    {
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A UTF-8 encoded string must not contain U+0000 (spec §2).", nameof(value));
        }

        int bytes;

        try
        {
            bytes = Utf8.GetByteCount(value);
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException($"A string is not well-formed UTF-8: {ex.Message}", nameof(value));
        }

        return bytes <= MaxStringBytes
            ? bytes
            : throw new ArgumentException(
                $"A UTF-8 encoded string is at most {MaxStringBytes} bytes but this one is {bytes} (spec §2).",
                nameof(value));
    }
}
