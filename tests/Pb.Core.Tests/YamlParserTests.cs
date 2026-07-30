using Pb.Core.Configuration.Yaml;
using Xunit;

namespace Pb.Core.Tests;

public sealed class YamlParserTests
{
    private static YamlMapping ParseMapping(string text) =>
        YamlParser.Parse(text).AsMapping("root");

    [Fact]
    public void Parse_EmptyText_YieldsEmptyMapping()
    {
        Assert.Equal(0, ParseMapping(string.Empty).Count);
        Assert.Equal(0, ParseMapping("\n\n   \n").Count);
        Assert.Equal(0, ParseMapping("# only a comment\n").Count);
    }

    [Fact]
    public void Parse_FlatMapping_KeepsKeyOrderAndValues()
    {
        YamlMapping map = ParseMapping("a: 1\nb: two\nc: 3.5\n");

        Assert.Equal(["a", "b", "c"], map.Keys);
        Assert.Equal(1, map.RequireInt("a"));
        Assert.Equal("two", map.RequireString("b"));
        Assert.Equal(3.5, map.RequireDouble("c"));
    }

    [Fact]
    public void Parse_NestedMapping_TracksIndentation()
    {
        YamlMapping map = ParseMapping("outer:\n  inner:\n    leaf: 7\n");

        Assert.Equal(7, map.RequireMapping("outer").RequireMapping("inner").RequireInt("leaf"));
    }

    [Fact]
    public void Parse_SequenceOfMappings_ReadsEveryEntry()
    {
        const string Text = """
            endpoints:
              - id: plc
                port: 502
              - id: udp_out
                port: 5005
            """;

        YamlSequence sequence = ParseMapping(Text).RequireSequence("endpoints");

        Assert.Equal(2, sequence.Count);
        Assert.Equal("plc", sequence.Items[0].AsMapping("e").RequireString("id"));
        Assert.Equal(5005, sequence.Items[1].AsMapping("e").RequireInt("port"));
    }

    [Fact]
    public void Parse_SequenceAtKeyIndentation_IsAccepted()
    {
        const string Text = """
            items:
            - id: a
            - id: b
            """;

        YamlSequence sequence = ParseMapping(Text).RequireSequence("items");

        Assert.Equal(2, sequence.Count);
        Assert.Equal("b", sequence.Items[1].AsMapping("e").RequireString("id"));
    }

    [Fact]
    public void Parse_SequenceOfScalars_ReadsScalarItems()
    {
        YamlSequence sequence = ParseMapping("names:\n  - alpha\n  - beta\n").RequireSequence("names");

        Assert.Equal("alpha", sequence.Items[0].AsScalar("i").RequireText("i"));
        Assert.Equal("beta", sequence.Items[1].AsScalar("i").RequireText("i"));
    }

    [Fact]
    public void Parse_NestedSequenceUnderSequenceItem_IsSupported()
    {
        const string Text = """
            groups:
              - name: first
                members:
                  - a
                  - b
            """;

        YamlMapping group = ParseMapping(Text).RequireSequence("groups").Items[0].AsMapping("g");

        Assert.Equal("first", group.RequireString("name"));
        Assert.Equal(2, group.RequireSequence("members").Count);
    }

    [Fact]
    public void Parse_ValueContainingColon_SplitsOnlyOnTheKeySeparator()
    {
        YamlMapping map = ParseMapping("address: holding:0\n");

        Assert.Equal("holding:0", map.RequireString("address"));
    }

    [Fact]
    public void Parse_CommentsAreStrippedButHashInsideValuesSurvives()
    {
        YamlMapping map = ParseMapping("# leading\ntopic: a#b   # trailing\nother: 1\n");

        Assert.Equal("a#b", map.RequireString("topic"));
        Assert.Equal(1, map.RequireInt("other"));
    }

    [Fact]
    public void Parse_QuotedValues_PreserveSpacingAndSpecialCharacters()
    {
        YamlMapping map = ParseMapping("""
            a: "  spaced  "
            b: 'single # not a comment'
            c: "tab\there"
            d: 'it''s'
            e: "say \"hi\""
            """);

        Assert.Equal("  spaced  ", map.RequireString("a"));
        Assert.Equal("single # not a comment", map.RequireString("b"));
        Assert.Equal("tab\there", map.RequireString("c"));
        Assert.Equal("it's", map.RequireString("d"));
        Assert.Equal("say \"hi\"", map.RequireString("e"));
    }

    [Fact]
    public void Parse_DocumentMarkerIsIgnored()
    {
        Assert.Equal(1, ParseMapping("---\na: 1\n").RequireInt("a"));
    }

    [Fact]
    public void Parse_EndOfDocumentMarkerStopsReading()
    {
        YamlMapping map = ParseMapping("a: 1\n...\nb: 2\n");

        Assert.Equal(["a"], map.Keys);
    }

    [Fact]
    public void Parse_KeyWithoutValue_YieldsNullScalar()
    {
        YamlScalar scalar = ParseMapping("unit:\n").Require("unit").AsScalar("unit");

        Assert.True(scalar.IsNull);
    }

    [Theory]
    [InlineData("~")]
    [InlineData("null")]
    [InlineData("NULL")]
    public void Parse_NullTokens_AreTreatedAsAbsent(string token)
    {
        Assert.True(ParseMapping($"unit: {token}\n").Require("unit").AsScalar("unit").IsNull);
    }

    [Theory]
    [InlineData("a: 1\n\tb: 2\n", "tab")]
    [InlineData("a: [1, 2]\n", "flow collection")]
    [InlineData("a: {b: 1}\n", "flow collection")]
    [InlineData("a: &anchor 1\n", "anchor")]
    [InlineData("a: |\n  text\n", "block scalar")]
    [InlineData("a: \"unterminated\n", "unterminated")]
    [InlineData("a: 'unterminated\n", "unterminated")]
    [InlineData("plain text\n", "key: value")]
    [InlineData("  a: 1\n", "column 1")]
    [InlineData("a: 1\na: 2\n", "duplicate")]
    [InlineData("a: 1\n---\nb: 2\n", "multiple YAML documents")]
    [InlineData("a:\n  b: 1\n    c: 2\n", "indentation")]
    [InlineData("a: 1\n  b: 2\n", "indentation")]
    [InlineData("a b: 1\n", "may only contain")]
    [InlineData("a: \"bad \\q escape\"\n", "unsupported escape")]
    public void Parse_OutsideTheSupportedSubset_ReportsTheLine(string text, string expectedFragment)
    {
        YamlException ex = Assert.Throws<YamlException>(() => YamlParser.Parse(text));

        Assert.Contains(expectedFragment, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(ex.Line >= 1, $"expected a line number in '{ex.Message}'");
    }

    [Fact]
    public void Parse_ReportsTheCorrectLineNumberSkippingBlanksAndComments()
    {
        const string Text = """
            # comment

            a: 1

            b: [oops]
            """;

        YamlException ex = Assert.Throws<YamlException>(() => YamlParser.Parse(Text));

        Assert.Equal(5, ex.Line);
    }

    [Fact]
    public void Parse_CrLfLineEndings_AreHandled()
    {
        YamlMapping map = ParseMapping("a: 1\r\nb:\r\n  c: 2\r\n");

        Assert.Equal(2, map.RequireMapping("b").RequireInt("c"));
    }

    [Fact]
    public void Parse_QuotedKey_IsAccepted()
    {
        YamlMapping map = ParseMapping("\"odd key\": 1\n");

        Assert.Equal(1, map.RequireInt("odd key"));
    }

    [Fact]
    public void Accessors_ReportTheOffendingShapeAndLine()
    {
        YamlMapping map = ParseMapping("a: 1\nb:\n  c: 2\n");

        Assert.Equal(3, Assert.Throws<YamlException>(() => map.RequireString("b")).Line);
        Assert.Equal(1, Assert.Throws<YamlException>(() => map.RequireMapping("a")).Line);
        Assert.Equal(1, Assert.Throws<YamlException>(() => map.RequireSequence("a")).Line);
        Assert.Contains("missing", Assert.Throws<YamlException>(() => map.Require("zzz")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Accessors_ConvertScalarsAndRejectBadValues()
    {
        YamlMapping map = ParseMapping("i: -5\nd: 1e3\nt: yes\nf: off\nbad: abc\n");

        Assert.Equal(-5, map.RequireInt("i"));
        Assert.Equal(1000.0, map.RequireDouble("d"));
        Assert.True(map.GetBool("t", false));
        Assert.False(map.GetBool("f", true));
        Assert.Throws<YamlException>(() => map.RequireInt("bad"));
        Assert.Throws<YamlException>(() => map.RequireDouble("bad"));
        Assert.Throws<YamlException>(() => map.GetBool("bad", false));
    }

    [Fact]
    public void Accessors_FallBackWhenKeyIsAbsentOrEmpty()
    {
        YamlMapping map = ParseMapping("present: 5\nempty:\n");

        Assert.Equal(5, map.GetInt("present", 9));
        Assert.Equal(9, map.GetInt("empty", 9));
        Assert.Equal(9, map.GetInt("missing", 9));
        Assert.Equal(2.5, map.GetDouble("missing", 2.5));
        Assert.Equal("x", map.GetString("empty", "x"));
        Assert.Null(map.GetString("missing"));
    }

    [Fact]
    public void RejectUnknownKeys_NamesTheTypoAndTheKnownKeys()
    {
        YamlMapping map = ParseMapping("known: 1\nknwon: 2\n");

        YamlException ex = Assert.Throws<YamlException>(() => map.RejectUnknownKeys("the section", "known"));

        Assert.Contains("knwon", ex.Message, StringComparison.Ordinal);
        Assert.Contains("known", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, ex.Line);
    }

    [Fact]
    public void Parse_NullText_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => YamlParser.Parse(null!));
    }
}
