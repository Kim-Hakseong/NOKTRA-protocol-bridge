namespace Pb.Core.Configuration;

/// <summary>One problem found while validating a configuration.</summary>
/// <param name="Message">Operator-facing description of the problem.</param>
/// <param name="Line">1-based source line, or 0 when the problem is not line-specific.</param>
public readonly record struct ConfigDiagnostic(string Message, int Line)
{
    public override string ToString() => Line > 0 ? $"line {Line}: {Message}" : Message;
}
