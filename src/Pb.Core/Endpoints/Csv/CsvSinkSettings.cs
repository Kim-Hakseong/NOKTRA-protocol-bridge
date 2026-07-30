using Pb.Core.Configuration;
using Pb.Core.Configuration.Yaml;

namespace Pb.Core.Endpoints.Csv;

/// <summary>Settings of a CSV file sink.</summary>
/// <param name="Path">File to append rows to. Its directory is created if missing.</param>
/// <param name="WriteHeader">Whether a header row is written when the file is created.</param>
/// <param name="Delimiter">Field separator.</param>
/// <param name="FlushEveryRow">
/// Whether every row is flushed to disk. On for unattended operation, so a power loss costs at
/// most one row; off buffers for throughput.
/// </param>
/// <param name="TimestampFormat">
/// Format string for the timestamp column. Timestamps are always converted to UTC first, so the
/// default ends in a literal <c>Z</c>.
/// </param>
public sealed record CsvSinkSettings(
    string Path,
    bool WriteHeader = true,
    string Delimiter = ",",
    bool FlushEveryRow = true,
    string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
{
    /// <summary>Configuration keys a <c>csv</c> endpoint accepts.</summary>
    public static readonly string[] KnownKeys = ["path", "header", "delimiter", "flush_every_row", "timestamp_format"];

    /// <summary>The header row this sink writes.</summary>
    public string HeaderRow => string.Join(Delimiter, "timestamp", "channel", "value", "unit", "quality");

    /// <summary>Reads settings from a configuration entry.</summary>
    public static CsvSinkSettings FromOptions(EndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.RejectUnknownKeys("a csv endpoint", KnownKeys);

        string delimiter = options.GetString("delimiter", ",") ?? ",";

        if (delimiter.Length == 0)
        {
            throw new YamlException("'delimiter' must not be empty.", options.LineOf("delimiter"));
        }

        string format = options.GetString("timestamp_format", "yyyy-MM-dd'T'HH:mm:ss.fff'Z'") ?? "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        try
        {
            _ = DateTimeOffset.UnixEpoch.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (FormatException ex)
        {
            throw new YamlException($"'timestamp_format' is not a valid format string: {ex.Message}", options.LineOf("timestamp_format"));
        }

        return new CsvSinkSettings(
            options.RequireString("path"),
            options.GetBool("header", true),
            delimiter,
            options.GetBool("flush_every_row", true),
            format);
    }

    public override string ToString() => Path;
}
