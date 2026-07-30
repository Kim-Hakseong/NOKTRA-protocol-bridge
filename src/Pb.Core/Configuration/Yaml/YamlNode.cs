using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Pb.Core.Configuration.Yaml;

/// <summary>Base of the parsed configuration tree. Every node remembers its source line.</summary>
public abstract class YamlNode
{
    protected YamlNode(int line) => Line = line;

    /// <summary>1-based line number this node started on.</summary>
    public int Line { get; }

    /// <summary>Human-readable kind name, used in error messages.</summary>
    public abstract string Kind { get; }

    /// <summary>Throws unless this node is a mapping.</summary>
    public YamlMapping AsMapping(string context) => this as YamlMapping
        ?? throw new YamlException($"{context} must be a block mapping but is {Kind}.", Line);

    /// <summary>Throws unless this node is a sequence.</summary>
    public YamlSequence AsSequence(string context) => this as YamlSequence
        ?? throw new YamlException($"{context} must be a block sequence but is {Kind}.", Line);

    /// <summary>Throws unless this node is a scalar.</summary>
    public YamlScalar AsScalar(string context) => this as YamlScalar
        ?? throw new YamlException($"{context} must be a scalar value but is {Kind}.", Line);
}

/// <summary>A single value. <see cref="Value"/> is null for an empty (absent) value.</summary>
public sealed class YamlScalar : YamlNode
{
    public YamlScalar(string? value, int line)
        : base(line) => Value = value;

    public string? Value { get; }

    public override string Kind => "a scalar";

    /// <summary>True when the value is absent, empty, or the YAML null tokens <c>~</c> / <c>null</c>.</summary>
    public bool IsNull => Value is null or "" or "~" or "null" or "Null" or "NULL";

    /// <summary>The text of this scalar, rejecting an absent value.</summary>
    public string RequireText(string context) => IsNull
        ? throw new YamlException($"{context} must not be empty.", Line)
        : Value!;

    /// <summary>Parses this scalar as a non-negative or negative 32-bit integer.</summary>
    public int AsInt(string context)
    {
        string text = RequireText(context);
        return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new YamlException($"{context} must be an integer but is '{text}'.", Line);
    }

    /// <summary>Parses this scalar as a finite double.</summary>
    public double AsDouble(string context)
    {
        string text = RequireText(context);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
            ? value
            : throw new YamlException($"{context} must be a finite number but is '{text}'.", Line);
    }

    /// <summary>Parses this scalar as a boolean, accepting the common YAML spellings.</summary>
    public bool AsBool(string context)
    {
        string text = RequireText(context);
        return text.ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => throw new YamlException($"{context} must be true or false but is '{text}'.", Line),
        };
    }

    public override string ToString() => Value ?? string.Empty;
}

/// <summary>An ordered block mapping. Keys are case-sensitive and must be unique.</summary>
public sealed class YamlMapping : YamlNode
{
    private readonly Dictionary<string, YamlNode> _byKey;
    private readonly List<string> _order;

    internal YamlMapping(List<string> order, Dictionary<string, YamlNode> byKey, int line)
        : base(line)
    {
        _order = order;
        _byKey = byKey;
    }

    /// <summary>Creates an empty mapping, used as the neutral element for absent sections.</summary>
    internal static YamlMapping Empty(int line) =>
        new YamlMapping(new List<string>(), new Dictionary<string, YamlNode>(StringComparer.Ordinal), line);

    public override string Kind => "a mapping";

    /// <summary>Keys in the order they appeared in the file.</summary>
    public IReadOnlyList<string> Keys => _order;

    public int Count => _order.Count;

    public bool ContainsKey(string key) => _byKey.ContainsKey(key);

    public bool TryGet(string key, [NotNullWhen(true)] out YamlNode? node) => _byKey.TryGetValue(key, out node);

    /// <summary>Gets a required child node.</summary>
    public YamlNode Require(string key) => _byKey.TryGetValue(key, out YamlNode? node)
        ? node
        : throw new YamlException($"required key '{key}' is missing.", Line);

    public YamlMapping RequireMapping(string key) => Require(key).AsMapping($"'{key}'");

    public YamlSequence RequireSequence(string key) => Require(key).AsSequence($"'{key}'");

    public string RequireString(string key) => Require(key).AsScalar($"'{key}'").RequireText($"'{key}'");

    public int RequireInt(string key) => Require(key).AsScalar($"'{key}'").AsInt($"'{key}'");

    public double RequireDouble(string key) => Require(key).AsScalar($"'{key}'").AsDouble($"'{key}'");

    /// <summary>Gets an optional string, returning <paramref name="fallback"/> when absent or empty.</summary>
    public string? GetString(string key, string? fallback = null)
    {
        if (!_byKey.TryGetValue(key, out YamlNode? node))
        {
            return fallback;
        }

        YamlScalar scalar = node.AsScalar($"'{key}'");
        return scalar.IsNull ? fallback : scalar.Value;
    }

    public int GetInt(string key, int fallback) => _byKey.TryGetValue(key, out YamlNode? node) && !IsEmptyScalar(node)
        ? node.AsScalar($"'{key}'").AsInt($"'{key}'")
        : fallback;

    public double GetDouble(string key, double fallback) => _byKey.TryGetValue(key, out YamlNode? node) && !IsEmptyScalar(node)
        ? node.AsScalar($"'{key}'").AsDouble($"'{key}'")
        : fallback;

    public bool GetBool(string key, bool fallback) => _byKey.TryGetValue(key, out YamlNode? node) && !IsEmptyScalar(node)
        ? node.AsScalar($"'{key}'").AsBool($"'{key}'")
        : fallback;

    /// <summary>Line of a child key, falling back to this mapping's line when absent.</summary>
    public int LineOf(string key) => _byKey.TryGetValue(key, out YamlNode? node) ? node.Line : Line;

    /// <summary>
    /// Rejects keys outside <paramref name="known"/>. A typo in a configuration file must
    /// fail loudly rather than be silently ignored.
    /// </summary>
    public void RejectUnknownKeys(string context, params string[] known)
    {
        foreach (string key in _order)
        {
            if (!known.Contains(key, StringComparer.Ordinal))
            {
                throw new YamlException(
                    $"{context} has unknown key '{key}'. Known keys: {string.Join(", ", known)}.",
                    LineOf(key));
            }
        }
    }

    private static bool IsEmptyScalar(YamlNode node) => node is YamlScalar { IsNull: true };
}

/// <summary>An ordered block sequence.</summary>
public sealed class YamlSequence : YamlNode
{
    private readonly List<YamlNode> _items;

    internal YamlSequence(List<YamlNode> items, int line)
        : base(line) => _items = items;

    public override string Kind => "a sequence";

    public IReadOnlyList<YamlNode> Items => _items;

    public int Count => _items.Count;
}
