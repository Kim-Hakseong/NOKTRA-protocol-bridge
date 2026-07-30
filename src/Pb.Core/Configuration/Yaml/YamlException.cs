namespace Pb.Core.Configuration.Yaml;

/// <summary>
/// Raised when configuration text cannot be parsed or a node is not of the shape the
/// reader asked for. The originating line is part of the message so that operators can
/// fix the file without a debugger.
/// </summary>
public sealed class YamlException : Exception
{
    public YamlException(string message, int line)
        : base(line > 0 ? $"line {line}: {message}" : message)
    {
        Line = line;
        Reason = message;
    }

    /// <summary>1-based line number the problem was found on, or 0 if not line-specific.</summary>
    public int Line { get; }

    /// <summary>The message without the line prefix.</summary>
    public string Reason { get; }
}
