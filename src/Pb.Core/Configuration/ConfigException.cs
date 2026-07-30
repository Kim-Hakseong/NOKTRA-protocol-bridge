namespace Pb.Core.Configuration;

/// <summary>
/// Raised when a configuration is structurally sound but semantically invalid. Carries every
/// diagnostic found, so one run of the loader tells the operator about all the mistakes
/// rather than only the first.
/// </summary>
public sealed class ConfigException : Exception
{
    public ConfigException(IReadOnlyList<ConfigDiagnostic> diagnostics)
        : base(Format(diagnostics)) => Diagnostics = diagnostics;

    public ConfigException(string message, int line)
        : this([new ConfigDiagnostic(message, line)])
    {
    }

    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; }

    private static string Format(IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (diagnostics.Count == 1)
        {
            return $"Invalid configuration: {diagnostics[0]}";
        }

        string details = string.Join(Environment.NewLine, diagnostics.Select(static d => $"  - {d}"));
        return $"Invalid configuration ({diagnostics.Count} problems):{Environment.NewLine}{details}";
    }
}
