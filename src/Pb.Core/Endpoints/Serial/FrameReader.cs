namespace Pb.Core.Endpoints.Serial;

/// <summary>How a byte stream is cut into frames.</summary>
public enum FramingMode
{
    /// <summary>Every frame is exactly <c>frame_bytes</c> long.</summary>
    Fixed = 0,

    /// <summary>A frame ends at a delimiter byte, which is not part of the frame.</summary>
    Delimiter,
}

/// <summary>
/// Cuts a byte stream into frames. A serial line carries no framing of its own, so the mode is
/// a configuration choice; keeping it separate from the port makes it testable without
/// hardware.
/// </summary>
public sealed class FrameReader
{
    private readonly Stream _stream;
    private readonly byte[] _buffer;
    private readonly byte[] _readChunk = new byte[1];

    private int _length;

    /// <param name="stream">Stream to read from.</param>
    /// <param name="mode">Framing mode.</param>
    /// <param name="frameBytes">Frame length for <see cref="FramingMode.Fixed"/>.</param>
    /// <param name="delimiter">Terminating byte for <see cref="FramingMode.Delimiter"/>.</param>
    /// <param name="maxFrameBytes">
    /// Upper bound on a delimited frame. A stream that never delivers the delimiter must fail
    /// rather than grow without limit.
    /// </param>
    public FrameReader(Stream stream, FramingMode mode, int frameBytes = 0, byte delimiter = (byte)'\n', int maxFrameBytes = 4096)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (mode == FramingMode.Fixed && frameBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(frameBytes), frameBytes, "Fixed framing needs a frame length of at least 1 byte.");
        }

        if (maxFrameBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFrameBytes), maxFrameBytes, "A frame limit of at least 1 byte is required.");
        }

        if (mode == FramingMode.Fixed && frameBytes > maxFrameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(frameBytes), frameBytes, $"A fixed frame cannot exceed the {maxFrameBytes}-byte limit.");
        }

        _stream = stream;
        Mode = mode;
        FrameBytes = frameBytes;
        Delimiter = delimiter;
        MaxFrameBytes = maxFrameBytes;
        _buffer = new byte[mode == FramingMode.Fixed ? frameBytes : maxFrameBytes];
    }

    public FramingMode Mode { get; }

    public int FrameBytes { get; }

    public byte Delimiter { get; }

    public int MaxFrameBytes { get; }

    /// <summary>Bytes of a partially received frame currently held.</summary>
    public int Pending => _length;

    /// <summary>
    /// Reads the next complete frame. The returned memory is only valid until the next call,
    /// because the reader reuses one buffer.
    /// </summary>
    /// <exception cref="EndOfStreamException">The stream ended mid-frame.</exception>
    /// <exception cref="InvalidDataException">A delimited frame exceeded <see cref="MaxFrameBytes"/>.</exception>
    public async ValueTask<ReadOnlyMemory<byte>> ReadFrameAsync(CancellationToken cancellationToken)
    {
        if (Mode == FramingMode.Fixed)
        {
            await _stream.ReadExactlyAsync(_buffer.AsMemory(0, FrameBytes), cancellationToken).ConfigureAwait(false);
            return _buffer.AsMemory(0, FrameBytes);
        }

        _length = 0;

        while (true)
        {
            int read = await _stream.ReadAsync(_readChunk.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                throw new EndOfStreamException(
                    _length == 0
                        ? "The stream ended before a frame started."
                        : $"The stream ended after {_length} byte(s) without the 0x{Delimiter:X2} delimiter.");
            }

            byte value = _readChunk[0];

            if (value == Delimiter)
            {
                return _buffer.AsMemory(0, _length);
            }

            if (_length == MaxFrameBytes)
            {
                int overflow = _length;
                _length = 0;
                throw new InvalidDataException(
                    $"No 0x{Delimiter:X2} delimiter arrived within {overflow} byte(s); the framing configuration does not match the line.");
            }

            _buffer[_length++] = value;
        }
    }
}
