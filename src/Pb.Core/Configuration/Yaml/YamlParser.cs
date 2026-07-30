using System.Text;

namespace Pb.Core.Configuration.Yaml;

/// <summary>
/// Parser for the YAML subset the bridge configuration uses: block mappings, block
/// sequences, plain and quoted scalars, comments and a single optional <c>---</c> document
/// marker. Everything outside that subset — flow collections, anchors and aliases, block
/// scalars, tabs for indentation, multiple documents — is rejected with the offending line
/// rather than half-interpreted.
/// </summary>
/// <remarks>
/// A full YAML implementation is deliberately out of scope: the allowed dependency list
/// carries no YAML package, and a narrow, fully-tested subset is safer for unattended
/// operation than a partial re-implementation of the whole standard.
/// </remarks>
public static class YamlParser
{
    /// <summary>Parses configuration text into a node tree.</summary>
    /// <returns>The root node, or an empty mapping when the document holds no content.</returns>
    /// <exception cref="YamlException">The text is outside the supported subset.</exception>
    public static YamlNode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<RawLine> lines = Scan(text);
        if (lines.Count == 0)
        {
            return YamlMapping.Empty(1);
        }

        Cursor cursor = new Cursor(lines);
        if (cursor.Current.Indent != 0)
        {
            throw new YamlException("the document must start at column 1.", cursor.Current.Number);
        }

        YamlNode root = ParseNode(cursor, 0);
        if (!cursor.AtEnd)
        {
            throw new YamlException("unexpected content after the end of the document.", cursor.Current.Number);
        }

        return root;
    }

    private static List<RawLine> Scan(string text)
    {
        List<RawLine> lines = [];
        string[] rawLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        bool sawContent = false;

        for (int i = 0; i < rawLines.Length; i++)
        {
            int number = i + 1;
            string raw = rawLines[i].TrimEnd();

            int tab = raw.IndexOf('\t', StringComparison.Ordinal);
            if (tab >= 0 && raw.AsSpan(0, tab).IsWhiteSpace())
            {
                throw new YamlException("tabs cannot be used for indentation; use spaces.", number);
            }

            string content = StripComment(raw);
            if (content.AsSpan().IsWhiteSpace())
            {
                continue;
            }

            int indent = 0;
            while (indent < content.Length && content[indent] == ' ')
            {
                indent++;
            }

            string body = content[indent..].TrimEnd();

            if (body == "---")
            {
                if (sawContent)
                {
                    throw new YamlException("multiple YAML documents are not supported.", number);
                }

                continue;
            }

            if (body == "...")
            {
                break;
            }

            sawContent = true;
            lines.Add(new RawLine(number, indent, body));
        }

        return lines;
    }

    /// <summary>
    /// Removes a trailing comment. A <c>#</c> only starts a comment when it is at the start
    /// of the line or preceded by whitespace and not inside quotes, matching YAML's rule so
    /// that values such as <c>topic: a#b</c> survive intact.
    /// </summary>
    private static string StripComment(string line)
    {
        char quote = '\0';

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (quote != '\0')
            {
                if (quote == '"' && c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (c == '#' && (i == 0 || line[i - 1] is ' ' or '\t'))
            {
                return line[..i];
            }
        }

        return line;
    }

    private static YamlNode ParseNode(Cursor cursor, int indent) =>
        IsSequenceItem(cursor.Current.Content) ? ParseSequence(cursor, indent) : ParseMapping(cursor, indent);

    private static bool IsSequenceItem(string content) =>
        content.Length > 0 && content[0] == '-' && (content.Length == 1 || content[1] == ' ');

    private static YamlSequence ParseSequence(Cursor cursor, int indent)
    {
        int startLine = cursor.Current.Number;
        List<YamlNode> items = [];

        while (!cursor.AtEnd)
        {
            RawLine line = cursor.Current;

            if (line.Indent < indent)
            {
                break;
            }

            if (line.Indent > indent)
            {
                throw new YamlException(
                    $"unexpected indentation; expected a sequence item at column {indent + 1}.",
                    line.Number);
            }

            if (!IsSequenceItem(line.Content))
            {
                throw new YamlException("expected a '- ' sequence item at this indentation.", line.Number);
            }

            int afterDash = 1;
            while (afterDash < line.Content.Length && line.Content[afterDash] == ' ')
            {
                afterDash++;
            }

            string rest = line.Content[afterDash..];

            if (rest.Length == 0)
            {
                cursor.Advance();
                items.Add(!cursor.AtEnd && cursor.Current.Indent > indent
                    ? ParseNode(cursor, cursor.Current.Indent)
                    : new YamlScalar(null, line.Number));
                continue;
            }

            int restIndent = indent + afterDash;

            if (IsSequenceItem(rest) || FindKeySeparator(rest) >= 0)
            {
                // Re-anchor the inline content as if it were written on its own line, so a
                // nested mapping or sequence continues at the column the content starts in.
                cursor.Replace(new RawLine(line.Number, restIndent, rest));
                items.Add(ParseNode(cursor, restIndent));
                continue;
            }

            cursor.Advance();
            items.Add(ParseScalar(rest, line.Number));
        }

        return new YamlSequence(items, startLine);
    }

    private static YamlMapping ParseMapping(Cursor cursor, int indent)
    {
        int startLine = cursor.Current.Number;
        List<string> order = [];
        Dictionary<string, YamlNode> byKey = new Dictionary<string, YamlNode>(StringComparer.Ordinal);

        while (!cursor.AtEnd)
        {
            RawLine line = cursor.Current;

            if (line.Indent < indent)
            {
                break;
            }

            if (line.Indent > indent)
            {
                throw new YamlException(
                    $"unexpected indentation; expected a key at column {indent + 1}.",
                    line.Number);
            }

            if (IsSequenceItem(line.Content))
            {
                throw new YamlException("expected 'key: value' but found a sequence item.", line.Number);
            }

            int separator = FindKeySeparator(line.Content);
            if (separator < 0)
            {
                throw new YamlException(
                    $"expected 'key: value' but found '{line.Content}'.",
                    line.Number);
            }

            string key = ReadKey(line.Content[..separator], line.Number);
            string valueText = line.Content[(separator + 1)..].Trim();

            if (byKey.ContainsKey(key))
            {
                throw new YamlException($"duplicate key '{key}'.", line.Number);
            }

            YamlNode value;
            if (valueText.Length > 0)
            {
                value = ParseScalar(valueText, line.Number);
                cursor.Advance();
            }
            else
            {
                cursor.Advance();

                if (!cursor.AtEnd && cursor.Current.Indent > indent)
                {
                    value = ParseNode(cursor, cursor.Current.Indent);
                }
                else if (!cursor.AtEnd && cursor.Current.Indent == indent && IsSequenceItem(cursor.Current.Content))
                {
                    // YAML allows a block sequence to sit at its key's own indentation.
                    value = ParseSequence(cursor, indent);
                }
                else
                {
                    value = new YamlScalar(null, line.Number);
                }
            }

            order.Add(key);
            byKey.Add(key, value);
        }

        return new YamlMapping(order, byKey, startLine);
    }

    /// <summary>
    /// Finds the index of the ':' that separates a key from its value: the first colon
    /// outside quotes that is followed by whitespace or ends the line. This is what keeps
    /// <c>address: holding:0</c> from splitting on the wrong colon.
    /// </summary>
    private static int FindKeySeparator(string content)
    {
        char quote = '\0';

        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];

            if (quote != '\0')
            {
                if (quote == '"' && c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (c == ':' && (i == content.Length - 1 || content[i + 1] == ' '))
            {
                return i;
            }
        }

        return -1;
    }

    private static string ReadKey(string text, int line)
    {
        string key = text.Trim();

        if (key.Length == 0)
        {
            throw new YamlException("a mapping key must not be empty.", line);
        }

        if (key[0] is '"' or '\'')
        {
            return Unquote(key, line);
        }

        foreach (char c in key)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.'))
            {
                throw new YamlException(
                    $"mapping key '{key}' may only contain letters, digits, '_', '-' and '.'; quote it if that is intended.",
                    line);
            }
        }

        return key;
    }

    private static YamlScalar ParseScalar(string text, int line)
    {
        string value = text.Trim();

        if (value.Length == 0)
        {
            return new YamlScalar(null, line);
        }

        switch (value[0])
        {
            case '"' or '\'':
                return new YamlScalar(Unquote(value, line), line);
            case '[' or '{':
                throw new YamlException("flow collections ('[...]', '{...}') are not supported; use block style.", line);
            case '&' or '*':
                throw new YamlException("anchors and aliases are not supported.", line);
            case '|' or '>':
                throw new YamlException("block scalars ('|', '>') are not supported.", line);
            default:
                return new YamlScalar(value, line);
        }
    }

    private static string Unquote(string text, int line)
    {
        char quote = text[0];

        if (text.Length < 2 || text[^1] != quote)
        {
            throw new YamlException($"unterminated {(quote == '"' ? "double" : "single")}-quoted value.", line);
        }

        string inner = text[1..^1];

        if (quote == '\'')
        {
            // In YAML single quotes only '' is an escape, standing for one quote.
            for (int i = 0; i < inner.Length; i++)
            {
                if (inner[i] != '\'')
                {
                    continue;
                }

                if (i + 1 >= inner.Length || inner[i + 1] != '\'')
                {
                    throw new YamlException("a single-quoted value may only contain a quote written as ''.", line);
                }

                i++;
            }

            return inner.Replace("''", "'", StringComparison.Ordinal);
        }

        StringBuilder builder = new StringBuilder(inner.Length);
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];

            if (c != '\\')
            {
                if (c == '"')
                {
                    throw new YamlException("an unescaped quote inside a double-quoted value.", line);
                }

                builder.Append(c);
                continue;
            }

            if (i + 1 >= inner.Length)
            {
                throw new YamlException("a double-quoted value ends with a dangling '\\'.", line);
            }

            char escape = inner[++i];
            builder.Append(escape switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '0' => '\0',
                '\\' => '\\',
                '"' => '"',
                '/' => '/',
                _ => throw new YamlException($"unsupported escape '\\{escape}'.", line),
            });
        }

        return builder.ToString();
    }

    private readonly record struct RawLine(int Number, int Indent, string Content);

    private sealed class Cursor
    {
        private readonly List<RawLine> _lines;
        private int _index;

        public Cursor(List<RawLine> lines) => _lines = lines;

        public bool AtEnd => _index >= _lines.Count;

        public RawLine Current => _lines[_index];

        public void Advance() => _index++;

        public void Replace(RawLine line) => _lines[_index] = line;
    }
}
