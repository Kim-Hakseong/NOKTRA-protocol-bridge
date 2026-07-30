using Pb.Core.Channels;
using Pb.Core.Configuration.Yaml;
using Pb.Core.Transforms;

namespace Pb.Core.Configuration;

/// <summary>
/// Turns configuration text into a validated <see cref="BridgeConfig"/>.
/// Problems are accumulated rather than thrown one at a time, so a single run reports every
/// mistake in the file with its line number.
/// </summary>
public static class BridgeConfigLoader
{
    /// <summary>Bridge name used when the <c>bridge.name</c> key is absent.</summary>
    public const string DefaultBridgeName = "bridge";

    private static readonly string[] RootKeys = ["bridge", "endpoints", "channels", "routes"];
    private static readonly string[] BridgeKeys = ["name"];
    private static readonly string[] ChannelKeys = ["name", "endpoint", "address", "type", "byte_order"];
    private static readonly string[] RouteKeys = ["id", "source", "sink", "trigger", "transform", "enabled"];
    private static readonly string[] TriggerKeys = ["mode", "period_ms"];
    private static readonly string[] TransformKeys = ["scale", "offset", "unit", "deadband"];

    /// <summary>Loads and validates configuration text.</summary>
    /// <exception cref="ConfigException">The configuration is invalid; every problem found is attached.</exception>
    public static BridgeConfig Load(string yamlText)
    {
        if (!TryLoad(yamlText, out BridgeConfig? config, out IReadOnlyList<ConfigDiagnostic> diagnostics))
        {
            throw new ConfigException(diagnostics);
        }

        return config!;
    }

    /// <summary>Loads and validates a configuration file.</summary>
    /// <exception cref="ConfigException">The configuration is invalid; every problem found is attached.</exception>
    public static BridgeConfig LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Load(File.ReadAllText(path));
    }

    /// <summary>
    /// Non-throwing counterpart of <see cref="Load"/>. Returns false with a populated
    /// <paramref name="diagnostics"/> list for any invalid input, including unparseable text.
    /// </summary>
    public static bool TryLoad(
        string yamlText,
        out BridgeConfig? config,
        out IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(yamlText);

        List<ConfigDiagnostic> problems = [];
        config = null;

        YamlMapping root;
        try
        {
            root = YamlParser.Parse(yamlText).AsMapping("the configuration document");
            root.RejectUnknownKeys("the configuration document", RootKeys);
        }
        catch (YamlException ex)
        {
            diagnostics = [new ConfigDiagnostic(ex.Reason, ex.Line)];
            return false;
        }

        string name = ReadBridgeName(root, problems);
        List<EndpointConfig> endpoints = ReadEndpoints(root, problems);
        List<ChannelConfig> channels = ReadChannels(root, problems);
        List<RouteConfig> routes = ReadRoutes(root, problems);

        CrossCheck(endpoints, channels, routes, problems);

        diagnostics = problems;
        if (problems.Count > 0)
        {
            return false;
        }

        config = new BridgeConfig(name, endpoints, channels, routes);
        return true;
    }

    private static string ReadBridgeName(YamlMapping root, List<ConfigDiagnostic> problems)
    {
        if (!root.TryGet("bridge", out YamlNode? node))
        {
            return DefaultBridgeName;
        }

        try
        {
            YamlMapping bridge = node.AsMapping("'bridge'");
            bridge.RejectUnknownKeys("'bridge'", BridgeKeys);
            string? name = bridge.GetString("name");
            return string.IsNullOrWhiteSpace(name) ? DefaultBridgeName : name;
        }
        catch (YamlException ex)
        {
            problems.Add(new ConfigDiagnostic(ex.Reason, ex.Line));
            return DefaultBridgeName;
        }
    }

    private static List<EndpointConfig> ReadEndpoints(YamlMapping root, List<ConfigDiagnostic> problems)
    {
        List<EndpointConfig> endpoints = [];

        foreach (YamlMapping entry in Section(root, "endpoints", problems))
        {
            try
            {
                string id = RequireIdentifier(entry, "id", "an endpoint id");
                string type = Normalize(entry.RequireString("type"));

                if (type.Length == 0)
                {
                    throw new YamlException("'type' must not be empty.", entry.LineOf("type"));
                }

                endpoints.Add(new EndpointConfig(
                    id,
                    type,
                    new EndpointOptions(entry, ["id", "type"]),
                    entry.Line));
            }
            catch (YamlException ex)
            {
                problems.Add(new ConfigDiagnostic($"endpoint: {ex.Reason}", ex.Line));
            }
        }

        return endpoints;
    }

    private static List<ChannelConfig> ReadChannels(YamlMapping root, List<ConfigDiagnostic> problems)
    {
        List<ChannelConfig> channels = [];

        foreach (YamlMapping entry in Section(root, "channels", problems))
        {
            try
            {
                entry.RejectUnknownKeys("a channel", ChannelKeys);

                string name = RequireIdentifier(entry, "name", "a channel name");
                string endpoint = RequireIdentifier(entry, "endpoint", "a channel endpoint");
                string addressText = entry.RequireString("address");

                if (!ChannelAddress.TryParse(addressText, out ChannelAddress address, out string? addressError))
                {
                    throw new YamlException(addressError, entry.LineOf("address"));
                }

                DataType type = ParseDataType(entry.RequireString("type"), entry.LineOf("type"));
                ByteOrder order = entry.ContainsKey("byte_order")
                    ? ParseByteOrder(entry.RequireString("byte_order"), entry.LineOf("byte_order"))
                    : ByteOrder.BigEndian;

                channels.Add(new ChannelConfig(
                    new ChannelSpec(name, endpoint, address, type, order),
                    entry.Line));
            }
            catch (YamlException ex)
            {
                problems.Add(new ConfigDiagnostic($"channel: {ex.Reason}", ex.Line));
            }
        }

        return channels;
    }

    private static List<RouteConfig> ReadRoutes(YamlMapping root, List<ConfigDiagnostic> problems)
    {
        List<RouteConfig> routes = [];

        foreach (YamlMapping entry in Section(root, "routes", problems))
        {
            try
            {
                entry.RejectUnknownKeys("a route", RouteKeys);

                string id = RequireIdentifier(entry, "id", "a route id");
                string source = entry.RequireString("source");
                string sink = entry.RequireString("sink");
                bool enabled = entry.GetBool("enabled", true);

                TriggerConfig trigger = entry.TryGet("trigger", out YamlNode? triggerNode)
                    ? ReadTrigger(triggerNode.AsMapping("'trigger'"))
                    : TriggerConfig.DefaultPeriodic;

                ValueTransform transform = entry.TryGet("transform", out YamlNode? transformNode)
                    ? ReadTransform(transformNode.AsMapping("'transform'"))
                    : ValueTransform.Identity;

                routes.Add(new RouteConfig(id, source, sink, trigger, transform, enabled, entry.Line));
            }
            catch (YamlException ex)
            {
                problems.Add(new ConfigDiagnostic($"route: {ex.Reason}", ex.Line));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                problems.Add(new ConfigDiagnostic($"route: {ex.Message}", entry.Line));
            }
        }

        return routes;
    }

    private static TriggerConfig ReadTrigger(YamlMapping trigger)
    {
        trigger.RejectUnknownKeys("'trigger'", TriggerKeys);

        string modeText = Normalize(trigger.GetString("mode") ?? "periodic");
        TriggerMode mode = modeText switch
        {
            "periodic" or "poll" => TriggerMode.Periodic,
            "on_change" or "change" or "event" => TriggerMode.OnChange,
            _ => throw new YamlException(
                $"'mode' must be periodic or on_change but is '{modeText}'.",
                trigger.LineOf("mode")),
        };

        if (mode == TriggerMode.OnChange)
        {
            if (trigger.ContainsKey("period_ms"))
            {
                throw new YamlException(
                    "'period_ms' does not apply to an on_change trigger; the source drives the route.",
                    trigger.LineOf("period_ms"));
            }

            return new TriggerConfig(TriggerMode.OnChange, TimeSpan.Zero, trigger.Line);
        }

        int periodMs = trigger.GetInt("period_ms", (int)TriggerConfig.DefaultPeriodic.Period.TotalMilliseconds);
        if (periodMs <= 0)
        {
            throw new YamlException(
                $"'period_ms' must be greater than 0 but is {periodMs}.",
                trigger.LineOf("period_ms"));
        }

        return new TriggerConfig(TriggerMode.Periodic, TimeSpan.FromMilliseconds(periodMs), trigger.Line);
    }

    private static ValueTransform ReadTransform(YamlMapping transform)
    {
        transform.RejectUnknownKeys("'transform'", TransformKeys);

        double scale = transform.GetDouble("scale", 1.0);
        double offset = transform.GetDouble("offset", 0.0);
        double deadband = transform.GetDouble("deadband", 0.0);

        if (deadband < 0.0)
        {
            throw new YamlException(
                $"'deadband' must not be negative but is {deadband}.",
                transform.LineOf("deadband"));
        }

        return new ValueTransform(scale, offset, transform.GetString("unit"), deadband);
    }

    private static void CrossCheck(
        List<EndpointConfig> endpoints,
        List<ChannelConfig> channels,
        List<RouteConfig> routes,
        List<ConfigDiagnostic> problems)
    {
        HashSet<string> endpointIds = ReportDuplicates(
            endpoints.Select(e => (e.Id, e.Line)),
            "endpoint id",
            problems);

        HashSet<string> channelNames = ReportDuplicates(
            channels.Select(c => (c.Name, c.Line)),
            "channel name",
            problems);

        ReportDuplicates(routes.Select(r => (r.Id, r.Line)), "route id", problems);

        foreach (ChannelConfig channel in channels)
        {
            if (!endpointIds.Contains(channel.Endpoint))
            {
                problems.Add(new ConfigDiagnostic(
                    $"channel '{channel.Name}' refers to endpoint '{channel.Endpoint}', which is not declared.",
                    channel.Line));
            }
        }

        Dictionary<string, RouteConfig> sinkOwners = new Dictionary<string, RouteConfig>(StringComparer.Ordinal);

        foreach (RouteConfig route in routes)
        {
            if (!channelNames.Contains(route.Source))
            {
                problems.Add(new ConfigDiagnostic(
                    $"route '{route.Id}' reads channel '{route.Source}', which is not declared.",
                    route.Line));
            }

            if (!channelNames.Contains(route.Sink))
            {
                problems.Add(new ConfigDiagnostic(
                    $"route '{route.Id}' writes channel '{route.Sink}', which is not declared.",
                    route.Line));
            }

            if (string.Equals(route.Source, route.Sink, StringComparison.Ordinal))
            {
                problems.Add(new ConfigDiagnostic(
                    $"route '{route.Id}' reads and writes the same channel '{route.Source}'.",
                    route.Line));
            }

            if (!route.Enabled)
            {
                continue;
            }

            // Two live routes driving one sink would race and interleave values, so the
            // configuration must pick one.
            if (sinkOwners.TryGetValue(route.Sink, out RouteConfig? owner))
            {
                problems.Add(new ConfigDiagnostic(
                    $"routes '{owner.Id}' and '{route.Id}' both write channel '{route.Sink}'; a sink can have only one writer.",
                    route.Line));
            }
            else
            {
                sinkOwners.Add(route.Sink, route);
            }
        }
    }

    private static HashSet<string> ReportDuplicates(
        IEnumerable<(string Value, int Line)> items,
        string what,
        List<ConfigDiagnostic> problems)
    {
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

        foreach ((string value, int line) in items)
        {
            if (!seen.Add(value))
            {
                problems.Add(new ConfigDiagnostic($"duplicate {what} '{value}'.", line));
            }
        }

        return seen;
    }

    /// <summary>
    /// Yields the mapping entries of a required top-level list section. A missing, empty or
    /// wrongly-shaped section produces one diagnostic instead of aborting the whole load, so
    /// the rest of the file is still checked.
    /// </summary>
    private static IReadOnlyList<YamlMapping> Section(YamlMapping root, string key, List<ConfigDiagnostic> problems)
    {
        if (!root.TryGet(key, out YamlNode? node) || node is YamlScalar { IsNull: true })
        {
            problems.Add(new ConfigDiagnostic(
                $"the '{key}' section is required and must declare at least one entry.",
                root.LineOf(key)));
            return [];
        }

        List<YamlMapping> entries = [];

        try
        {
            YamlSequence sequence = node.AsSequence($"'{key}'");

            if (sequence.Count == 0)
            {
                problems.Add(new ConfigDiagnostic($"the '{key}' section must declare at least one entry.", sequence.Line));
                return [];
            }

            for (int i = 0; i < sequence.Count; i++)
            {
                entries.Add(sequence.Items[i].AsMapping($"'{key}' entry {i + 1}"));
            }
        }
        catch (YamlException ex)
        {
            problems.Add(new ConfigDiagnostic(ex.Reason, ex.Line));
            return [];
        }

        return entries;
    }

    private static string RequireIdentifier(YamlMapping entry, string key, string what)
    {
        string value = entry.RequireString(key).Trim();

        if (value.Length == 0)
        {
            throw new YamlException($"{what} must not be empty.", entry.LineOf(key));
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.'))
            {
                throw new YamlException(
                    $"{what} '{value}' may only contain letters, digits, '_', '-' and '.'.",
                    entry.LineOf(key));
            }
        }

        return value;
    }

    private static DataType ParseDataType(string text, int line) => Normalize(text) switch
    {
        "bool" or "boolean" or "bit" => DataType.Bool,
        "u16" or "uint16" or "word" or "ushort" => DataType.U16,
        "s16" or "int16" or "short" => DataType.S16,
        "u32" or "uint32" or "dword" or "uint" => DataType.U32,
        "s32" or "int32" or "int" => DataType.S32,
        "u64" or "uint64" or "ulong" => DataType.U64,
        "s64" or "int64" or "long" => DataType.S64,
        "f32" or "float" or "real" or "single" => DataType.F32,
        "f64" or "double" or "lreal" => DataType.F64,
        _ => throw new YamlException(
            $"'type' must be one of bool, u16, s16, u32, s32, u64, s64, f32, f64 but is '{text}'.",
            line),
    };

    private static ByteOrder ParseByteOrder(string text, int line) => Normalize(text) switch
    {
        "big_endian" or "big" or "be" or "abcd" => ByteOrder.BigEndian,
        "little_endian" or "little" or "le" or "dcba" => ByteOrder.LittleEndian,
        "byte_swapped" or "byte_swapped_big_endian" or "badc" => ByteOrder.ByteSwappedBigEndian,
        "word_swapped" or "word_swapped_big_endian" or "cdab" => ByteOrder.WordSwappedBigEndian,
        _ => throw new YamlException(
            $"'byte_order' must be one of big_endian (abcd), little_endian (dcba), byte_swapped (badc), word_swapped (cdab) but is '{text}'.",
            line),
    };

    /// <summary>
    /// Folds a configuration token to its canonical form so that <c>modbus-tcp</c>,
    /// <c>modbus_tcp</c> and <c>Modbus-TCP</c> all mean the same thing.
    /// </summary>
    internal static string Normalize(string text) =>
        text.Trim().ToLowerInvariant().Replace('-', '_');
}
