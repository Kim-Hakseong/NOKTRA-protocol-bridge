using Pb.Core.Channels;

namespace Pb.Core.Endpoints;

/// <summary>
/// Byte-offset addressing for frame-oriented endpoints such as UDP and serial. Channels on
/// those endpoints use the <c>offset:N</c> address space, where N is the zero-based byte
/// position of the value inside the frame.
/// </summary>
public static class FramePayload
{
    /// <summary>Address space token frame-oriented endpoints require.</summary>
    public const string OffsetSpace = "offset";

    /// <summary>Accepted spellings of the byte-offset address space.</summary>
    public static readonly string[] AcceptedSpaces = ["offset", "byte", "bytes"];

    /// <summary>True when <paramref name="space"/> names the byte-offset address space.</summary>
    public static bool IsOffsetSpace(string space) => AcceptedSpaces.Contains(space, StringComparer.Ordinal);

    /// <summary>
    /// Validates that <paramref name="channel"/> is addressable inside a frame and reports the
    /// byte range it occupies.
    /// </summary>
    /// <param name="channel">Channel to place.</param>
    /// <param name="offset">Byte offset of the value inside the frame.</param>
    /// <param name="length">Number of bytes the value occupies.</param>
    /// <param name="maxFrameBytes">Frame size limit, or 0 for no limit.</param>
    /// <param name="error">Why the channel cannot be placed, when the method returns false.</param>
    public static bool TryPlace(
        ChannelSpec channel,
        int maxFrameBytes,
        out int offset,
        out int length,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(channel);

        offset = channel.Address.Index;
        length = channel.SizeInBytes;

        if (!IsOffsetSpace(channel.Address.Space))
        {
            error = $"address space '{channel.Address.Space}' is not a frame offset; use '{OffsetSpace}:N'.";
            return false;
        }

        if (maxFrameBytes > 0 && offset + length > maxFrameBytes)
        {
            error = $"a {channel.Type} at offset {offset} needs {offset + length} byte(s) but the frame is {maxFrameBytes}.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Extracts a channel's bytes from a received frame.
    /// </summary>
    /// <exception cref="ArgumentException">The frame is too short for the channel.</exception>
    public static ReadOnlyMemory<byte> Extract(ChannelSpec channel, ReadOnlyMemory<byte> frame)
    {
        ArgumentNullException.ThrowIfNull(channel);

        int offset = channel.Address.Index;
        int length = channel.SizeInBytes;

        if (offset + length > frame.Length)
        {
            throw new ArgumentException(
                $"channel '{channel.Name}' reads {length} byte(s) at offset {offset}, past the end of a {frame.Length}-byte frame.",
                nameof(frame));
        }

        return frame.Slice(offset, length);
    }
}
