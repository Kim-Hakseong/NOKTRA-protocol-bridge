using System.IO.Ports;
using Pb.Core.Configuration;
using Pb.Core.Configuration.Yaml;

namespace Pb.Core.Endpoints.Serial;

/// <summary>Settings of a serial endpoint.</summary>
/// <param name="PortName">Operating-system port name, for example <c>/dev/ttyUSB0</c> or <c>COM3</c>.</param>
/// <param name="BaudRate">Line speed.</param>
/// <param name="Parity">Parity scheme.</param>
/// <param name="DataBits">Data bits per character.</param>
/// <param name="StopBits">Stop bits per character.</param>
/// <param name="Framing">How the byte stream is cut into frames.</param>
/// <param name="FrameBytes">Frame length for fixed framing.</param>
/// <param name="Delimiter">Terminating byte for delimited framing.</param>
/// <param name="AppendDelimiter">Whether writes append the delimiter.</param>
/// <param name="MaxFrameBytes">Upper bound on a delimited frame.</param>
public sealed record SerialSettings(
    string PortName,
    int BaudRate = 9600,
    Parity Parity = Parity.None,
    int DataBits = 8,
    StopBits StopBits = StopBits.One,
    FramingMode Framing = FramingMode.Fixed,
    int FrameBytes = 0,
    byte Delimiter = (byte)'\n',
    bool AppendDelimiter = true,
    int MaxFrameBytes = 4096)
{
    /// <summary>Configuration keys a <c>serial</c> endpoint accepts.</summary>
    public static readonly string[] KnownKeys =
    [
        "port", "baud_rate", "parity", "data_bits", "stop_bits",
        "framing", "frame_bytes", "delimiter", "append_delimiter", "max_frame_bytes",
    ];

    /// <summary>Reads settings from a configuration entry.</summary>
    public static SerialSettings FromOptions(EndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.RejectUnknownKeys("a serial endpoint", KnownKeys);

        string portName = options.RequireString("port");
        FramingMode framing = ParseFraming(options);
        int maxFrameBytes = options.GetRangedInt("max_frame_bytes", 4096, 1, 1 << 20);
        int frameBytes = options.GetRangedInt("frame_bytes", 0, 0, maxFrameBytes);

        if (framing == FramingMode.Fixed && frameBytes < 1)
        {
            throw new YamlException(
                "'frame_bytes' is required and must be at least 1 when framing is 'fixed'.",
                options.LineOf("frame_bytes"));
        }

        return new SerialSettings(
            portName,
            options.GetPositiveInt("baud_rate", 9600),
            ParseParity(options),
            options.GetRangedInt("data_bits", 8, 5, 8),
            ParseStopBits(options),
            framing,
            frameBytes,
            ParseDelimiter(options),
            options.GetBool("append_delimiter", framing == FramingMode.Delimiter),
            maxFrameBytes);
    }

    public override string ToString() => $"{PortName} {BaudRate} {DataBits}{Parity.ToString()[0]}{StopBitsText}";

    private string StopBitsText => StopBits switch
    {
        StopBits.One => "1",
        StopBits.Two => "2",
        StopBits.OnePointFive => "1.5",
        _ => "0",
    };

    private static FramingMode ParseFraming(EndpointOptions options) =>
        BridgeConfigLoader.Normalize(options.GetString("framing") ?? "fixed") switch
        {
            "fixed" or "length" => FramingMode.Fixed,
            "delimiter" or "delimited" => FramingMode.Delimiter,
            var other => throw new YamlException(
                $"'framing' must be 'fixed' or 'delimiter' but is '{other}'.",
                options.LineOf("framing")),
        };

    private static Parity ParseParity(EndpointOptions options) =>
        BridgeConfigLoader.Normalize(options.GetString("parity") ?? "none") switch
        {
            "none" or "n" => Parity.None,
            "odd" or "o" => Parity.Odd,
            "even" or "e" => Parity.Even,
            "mark" or "m" => Parity.Mark,
            "space" or "s" => Parity.Space,
            var other => throw new YamlException(
                $"'parity' must be none, odd, even, mark or space but is '{other}'.",
                options.LineOf("parity")),
        };

    private static StopBits ParseStopBits(EndpointOptions options) =>
        BridgeConfigLoader.Normalize(options.GetString("stop_bits") ?? "1") switch
        {
            "1" or "one" => StopBits.One,
            "2" or "two" => StopBits.Two,
            "1.5" or "onepointfive" or "one_point_five" => StopBits.OnePointFive,
            var other => throw new YamlException(
                $"'stop_bits' must be 1, 1.5 or 2 but is '{other}'.",
                options.LineOf("stop_bits")),
        };

    /// <summary>
    /// Reads the delimiter, accepting a decimal or <c>0x</c> byte value and the two escapes a
    /// line-oriented device actually uses.
    /// </summary>
    private static byte ParseDelimiter(EndpointOptions options)
    {
        string? text = options.GetString("delimiter");

        if (text is null)
        {
            return (byte)'\n';
        }

        // A double-quoted "\n" has already become a real control character by the time it gets
        // here; a control character is never a decimal or hex spelling, so it is unambiguous.
        if (text.Length == 1 && char.IsControl(text[0]))
        {
            return (byte)text[0];
        }

        string token = text.Trim();

        switch (token.ToLowerInvariant())
        {
            case "\\n" or "lf" or "newline":
                return (byte)'\n';
            case "\\r" or "cr":
                return (byte)'\r';
            case "\\0" or "nul" or "null":
                return 0;
        }

        bool hex = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        string digits = hex ? token[2..] : token;
        int value = hex
            ? int.TryParse(digits, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int parsedHex) ? parsedHex : -1
            : int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsedDec) ? parsedDec : -1;

        return value is >= 0 and <= 255
            ? (byte)value
            : throw new YamlException(
                $"'delimiter' must be a byte value 0..255 (decimal or 0x..), or one of \\n, \\r, \\0, but is '{token}'.",
                options.LineOf("delimiter"));
    }
}
