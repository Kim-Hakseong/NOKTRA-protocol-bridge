using System.Globalization;
using System.Text;
using Pb.Core.Channels;

namespace Pb.Core.Endpoints.Csv;

/// <summary>
/// Appends one row per sample to a CSV file: timestamp, channel, value, unit, quality.
/// </summary>
/// <remarks>
/// A CSV sink is addressed by channel name rather than by a wire address, so its channels use
/// the address space <c>csv:0</c>; any other space or index is rejected at start-up rather than
/// silently ignored. Rows are appended, never rewritten, so restarting the bridge extends the
/// log instead of truncating it.
/// </remarks>
public sealed class CsvFileSink : IEndpoint, IValueSink
{
    /// <summary>Driver token this endpoint is configured as.</summary>
    public const string TypeToken = "csv";

    /// <summary>Address space CSV channels must use.</summary>
    public const string AddressSpace = "csv";

    private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);

    private StreamWriter? _writer;
    private bool _disposed;

    public CsvFileSink(string id, CsvSinkSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Path);

        Id = id;
        Settings = settings;
    }

    public string Id { get; }

    public string Kind => TypeToken;

    public CsvSinkSettings Settings { get; }

    public EndpointState State { get; private set; } = EndpointState.Disconnected;

    public string Target => Settings.Path;

    /// <summary>Rows written since construction.</summary>
    public long RowsWritten { get; private set; }

    public bool Supports(ChannelSpec channel, ChannelRole role, out string? error)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (role == ChannelRole.Source)
        {
            error = "a csv endpoint is write-only, so it cannot be a route source.";
            return false;
        }

        if (!string.Equals(channel.Address.Space, AddressSpace, StringComparison.Ordinal) || channel.Address.Index != 0)
        {
            error = $"a csv channel is addressed by name, so its address must be '{AddressSpace}:0'.";
            return false;
        }

        error = null;
        return true;
    }

    public ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (_writer is not null)
        {
            return ValueTask.CompletedTask;
        }

        State = EndpointState.Connecting;

        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(Settings.Path));

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            bool needsHeader = Settings.WriteHeader && !File.Exists(Settings.Path);
            _writer = new StreamWriter(
                new FileStream(Settings.Path, FileMode.Append, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (needsHeader)
            {
                _writer.WriteLine(Settings.HeaderRow);
                _writer.Flush();
            }

            State = EndpointState.Connected;
            return ValueTask.CompletedTask;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            State = EndpointState.Faulted;
            throw new EndpointException(Id, $"could not open '{Settings.Path}': {ex.Message}", ex);
        }
    }

    public async ValueTask DisconnectAsync()
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_writer is not null)
            {
                await _writer.FlushAsync().ConfigureAwait(false);
                await _writer.DisposeAsync().ConfigureAwait(false);
                _writer = null;
            }

            State = EndpointState.Disconnected;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask WriteAsync(ChannelSpec channel, Sample sample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StreamWriter writer = _writer
                ?? throw new EndpointException(Id, "is not open; call ConnectAsync first.");

            try
            {
                await writer.WriteLineAsync(FormatRow(channel, sample)).ConfigureAwait(false);

                if (Settings.FlushEveryRow)
                {
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                RowsWritten++;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                State = EndpointState.Faulted;
                throw new EndpointException(Id, $"could not write to '{Settings.Path}': {ex.Message}", ex);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync().ConfigureAwait(false);
        _writeGate.Dispose();
    }

    /// <summary>Renders one row, quoting any field that would otherwise break the layout.</summary>
    internal string FormatRow(ChannelSpec channel, Sample sample)
    {
        string timestamp = sample.Timestamp.ToUniversalTime().ToString(Settings.TimestampFormat, CultureInfo.InvariantCulture);
        string value = sample.Value.ToString("R", CultureInfo.InvariantCulture);

        return string.Join(
            Settings.Delimiter,
            Quote(timestamp),
            Quote(channel.Name),
            Quote(value),
            Quote(sample.Unit ?? string.Empty),
            Quote(sample.Quality.ToString()));
    }

    private string Quote(string field)
    {
        bool needsQuotes = field.Contains(Settings.Delimiter, StringComparison.Ordinal)
            || field.Contains('"', StringComparison.Ordinal)
            || field.Contains('\n', StringComparison.Ordinal)
            || field.Contains('\r', StringComparison.Ordinal);

        return needsQuotes
            ? $"\"{field.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : field;
    }
}
