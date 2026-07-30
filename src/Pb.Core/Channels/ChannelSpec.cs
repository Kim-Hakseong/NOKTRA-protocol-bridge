namespace Pb.Core.Channels;

/// <summary>
/// Declares where a value lives on an endpoint and how its bytes are laid out.
/// The same shape describes source channels (read from) and sink channels (written to);
/// the role comes from how a route references it.
/// </summary>
/// <param name="Name">Configuration-unique channel name, used in logs and diagnostics.</param>
/// <param name="Endpoint">Identifier of the endpoint that owns this channel.</param>
/// <param name="Address">Endpoint-relative address.</param>
/// <param name="Type">Wire data type.</param>
/// <param name="ByteOrder">Byte and word arrangement of the wire value.</param>
public sealed record ChannelSpec(
    string Name,
    string Endpoint,
    ChannelAddress Address,
    DataType Type,
    ByteOrder ByteOrder = ByteOrder.BigEndian)
{
    /// <summary>Size in bytes of one wire value of this channel.</summary>
    public int SizeInBytes => ValueCodec.SizeOf(Type);

    public override string ToString() => $"{Name} ({Endpoint} {Address} {Type}/{ByteOrder})";
}
