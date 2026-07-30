using Pb.Core.Configuration.Yaml;

namespace Pb.Core.Configuration;

/// <summary>
/// The endpoint-specific settings of one endpoint entry: every key except <c>id</c> and
/// <c>type</c>. The loader deliberately does not interpret these — each endpoint driver
/// knows its own keys and validates them when it is constructed, which keeps the loader
/// free of protocol knowledge and keeps unknown-key detection next to the code that
/// defines the keys.
/// </summary>
public sealed class EndpointOptions
{
    private readonly YamlMapping _mapping;
    private readonly HashSet<string> _excluded;

    internal EndpointOptions(YamlMapping mapping, IEnumerable<string> excluded)
    {
        _mapping = mapping;
        _excluded = new HashSet<string>(excluded, StringComparer.Ordinal);
    }

    /// <summary>An empty option set, for endpoints that need no settings.</summary>
    public static EndpointOptions Empty { get; } = new EndpointOptions(YamlMapping.Empty(0), []);

    /// <summary>Line the owning endpoint entry starts on.</summary>
    public int Line => _mapping.Line;

    /// <summary>Option keys present, in file order.</summary>
    public IReadOnlyList<string> Keys => _mapping.Keys.Where(k => !_excluded.Contains(k)).ToList();

    public bool Contains(string key) => !_excluded.Contains(key) && _mapping.ContainsKey(key);

    /// <summary>Line of an option, falling back to the endpoint's line when absent.</summary>
    public int LineOf(string key) => _mapping.LineOf(key);

    /// <summary>Reads a required string option.</summary>
    /// <exception cref="YamlException">The option is missing or empty.</exception>
    public string RequireString(string key)
    {
        Guard(key);
        return _mapping.RequireString(key);
    }

    /// <summary>Reads a required integer option.</summary>
    public int RequireInt(string key)
    {
        Guard(key);
        return _mapping.RequireInt(key);
    }

    /// <summary>Reads an optional string option.</summary>
    public string? GetString(string key, string? fallback = null) => Contains(key) ? _mapping.GetString(key, fallback) : fallback;

    /// <summary>Reads an optional integer option.</summary>
    public int GetInt(string key, int fallback) => Contains(key) ? _mapping.GetInt(key, fallback) : fallback;

    /// <summary>Reads an optional double option.</summary>
    public double GetDouble(string key, double fallback) => Contains(key) ? _mapping.GetDouble(key, fallback) : fallback;

    /// <summary>Reads an optional boolean option.</summary>
    public bool GetBool(string key, bool fallback) => Contains(key) ? _mapping.GetBool(key, fallback) : fallback;

    /// <summary>
    /// Reads an optional integer option that must be positive, used for ports, periods and
    /// timeouts.
    /// </summary>
    public int GetPositiveInt(string key, int fallback)
    {
        int value = GetInt(key, fallback);
        return value > 0
            ? value
            : throw new YamlException($"'{key}' must be greater than 0 but is {value}.", LineOf(key));
    }

    /// <summary>Reads an optional integer option that must fall inside an inclusive range.</summary>
    public int GetRangedInt(string key, int fallback, int min, int max)
    {
        int value = GetInt(key, fallback);
        return value >= min && value <= max
            ? value
            : throw new YamlException($"'{key}' must be between {min} and {max} but is {value}.", LineOf(key));
    }

    /// <summary>
    /// Rejects any option outside <paramref name="known"/>, so that a misspelled setting
    /// fails at start-up instead of silently doing nothing.
    /// </summary>
    public void RejectUnknownKeys(string context, params string[] known)
    {
        foreach (string key in Keys)
        {
            if (!known.Contains(key, StringComparer.Ordinal))
            {
                throw new YamlException(
                    $"{context} has unknown setting '{key}'. Known settings: {string.Join(", ", known)}.",
                    LineOf(key));
            }
        }
    }

    private void Guard(string key)
    {
        if (_excluded.Contains(key))
        {
            throw new InvalidOperationException($"'{key}' is an endpoint header key, not a driver setting.");
        }
    }
}
