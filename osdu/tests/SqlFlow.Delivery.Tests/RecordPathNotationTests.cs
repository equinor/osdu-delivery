using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How a path names a property whose name holds a dot, a bracket or a space (osdu/docs/reference/flow/mapping-property-names.md):
/// in brackets and quotes, as JSONPath writes a name, while every name a path can write bare is written as it always was.
/// </summary>
public class RecordPathNotationTests
{
    /// <summary>A backslash, built here so no tool between the test and the compiler reads an escape in it.</summary>
    private static readonly string Slash = new('\\', 1);

    [Theory]
    [InlineData("Name", true)]
    [InlineData("$name", true)]
    [InlineData("it's", true)]
    [InlineData("WellLog-1", true)]
    [InlineData("lra.dataID", false)]
    [InlineData("Data Owner", false)]
    [InlineData("a[0]", false)]
    [InlineData("a]b", false)]
    [InlineData("*", false)]
    [InlineData("", false)]
    public void A_name_is_written_bare_only_when_it_holds_no_dot_bracket_or_space(string name, bool plain)
        => Assert.Equal(plain, RecordPathNotation.IsPlain(name));

    [Fact]
    public void Every_kind_of_space_keeps_a_name_from_being_written_bare()
    {
        foreach (var space in new[] { ' ', '\t', '\n', (char)0x85, (char)0xA0, (char)0x2028, (char)0x3000 })
        {
            Assert.False(RecordPathNotation.IsPlain("a" + space + "b"), $"U+{(int)space:X4}");
        }
    }

    [Fact]
    public void A_name_is_quoted_with_the_escapes_a_normalized_json_path_writes()
    {
        Assert.Equal("['lra.dataID']", RecordPathNotation.Quote("lra.dataID"));
        Assert.Equal("['it" + Slash + "'s']", RecordPathNotation.Quote("it's"));
        Assert.Equal("['a" + Slash + Slash + "b']", RecordPathNotation.Quote("a" + Slash + "b"));
        Assert.Equal("['line" + Slash + "nnext']", RecordPathNotation.Quote("line\nnext"));
        Assert.Equal("['bell" + Slash + "u0007']", RecordPathNotation.Quote("bell" + (char)7));
    }

    [Fact]
    public void A_name_is_appended_bare_after_a_dot_or_quoted_in_brackets()
    {
        Assert.Equal("data", RecordPathNotation.Append(string.Empty, "data"));
        Assert.Equal("data.Name", RecordPathNotation.Append("data", "Name"));
        Assert.Equal("tags['lra.dataID']", RecordPathNotation.Append("tags", "lra.dataID"));
        Assert.Equal("['a.b']", RecordPathNotation.Append(string.Empty, "a.b"));
        Assert.Equal("data.Curves[].Unit", RecordPathNotation.Append("data.Curves[]", "Unit"));
        Assert.Equal("data.Curves[]['a b']", RecordPathNotation.Append("data.Curves[]", "a b"));
    }

    [Fact]
    public void Every_name_reads_back_as_it_was_written()
    {
        foreach (var name in new[] { "Name", "lra.dataID", "Data Owner", "a]b", "[x]", "it's", "a\"b", "back" + Slash + "slash", "tab\there", "*", "." })
        {
            var path = RecordPathNotation.Append("tags", name);
            Assert.True(RecordPathNotation.TryParse(path, out var steps, out var error), $"{path}: {error}");
            Assert.Equal([RecordPathStep.OfProperty("tags"), RecordPathStep.OfProperty(name)], steps);
            Assert.Equal(path, RecordPathNotation.Write(steps));
        }
    }

    [Fact]
    public void A_path_reads_into_names_and_the_steps_into_arrays()
    {
        Assert.Equal(
            [RecordPathStep.OfProperty("data"), RecordPathStep.OfProperty("Curves"), RecordPathStep.OfItems(), RecordPathStep.OfProperty("CurveID")],
            Parse("data.Curves[].CurveID"));
        Assert.Equal(
            [RecordPathStep.OfProperty("data"), RecordPathStep.OfProperty("Curves"), RecordPathStep.OfWildcard(), RecordPathStep.OfIndex(3), RecordPathStep.OfProperty("X")],
            Parse("data.Curves[*][3].X"));
        Assert.Equal([RecordPathStep.OfWildcard()], Parse("*"));

        // Either quote, blanks inside the brackets, and a dot before them read the same name.
        var tag = new[] { RecordPathStep.OfProperty("tags"), RecordPathStep.OfProperty("lra.dataID") };
        Assert.Equal(tag, Parse("tags['lra.dataID']"));
        Assert.Equal(tag, Parse("tags[\"lra.dataID\"]"));
        Assert.Equal(tag, Parse("tags[ 'lra.dataID' ]"));
        Assert.Equal(tag, Parse("tags.['lra.dataID']"));

        // A quoted name holds what a bare one cannot, its escapes undone.
        Assert.Equal("a]b", Parse("tags['a]b']")[1].Name);
        Assert.Equal("a'b", Parse("tags['a" + Slash + "'b']")[1].Name);
        Assert.Equal("a\"b", Parse("tags['a\"b']")[1].Name);
        Assert.Equal("A", Parse("tags['" + Slash + "u0041']")[1].Name);
        Assert.Equal("a/b", Parse("tags['a" + Slash + "/b']")[1].Name);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData(".a", "empty property name")]
    [InlineData("a..b", "empty property name")]
    [InlineData("a.", "ends with a dot")]
    [InlineData("a[", "not closed")]
    [InlineData("a['x'", "not closed")]
    [InlineData("a['x']b", "without a dot")]
    [InlineData("a[0]b", "without a dot")]
    [InlineData("a[x]", "is not a step")]
    [InlineData("a]b", "closes no")]
    [InlineData("[0]", "starts with [0]")]
    [InlineData("a.b c", "holds a space")]
    [InlineData("a['']", "empty property name")]
    public void What_is_not_a_path_is_refused_saying_why(string path, string reason)
    {
        Assert.False(RecordPathNotation.TryParse(path, out _, out var error));
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_escape_and_a_control_character_in_a_quoted_name_are_refused()
    {
        Assert.False(RecordPathNotation.TryParse("tags['" + Slash + "q']", out _, out var escape));
        Assert.Contains("is not an escape", escape, StringComparison.Ordinal);
        Assert.False(RecordPathNotation.TryParse("tags['a" + (char)1 + "']", out _, out var control));
        Assert.Contains("control character", control, StringComparison.Ordinal);
    }

    [Fact]
    public void A_schema_path_reads_its_names_as_it_always_has_and_a_quoted_name_whole()
    {
        Assert.Equal(["data", "Curves", "CurveID"], RecordPathNotation.Names("data.Curves.CurveID"));
        Assert.Equal(["a", "b"], RecordPathNotation.Names("a..b"));
        Assert.Equal(["data", "Curves[]"], RecordPathNotation.Names("data.Curves[]"));
        Assert.Equal(["tags", "lra.dataID"], RecordPathNotation.Names("tags['lra.dataID']"));
        Assert.Equal(["data", "X", "a.b", "c"], RecordPathNotation.Names("data.X['a.b'].c"));
        Assert.Null(RecordPathNotation.Names("tags['lra"));
    }

    [Fact]
    public void A_path_lies_within_another_at_a_step_boundary_only()
    {
        Assert.True(RecordPathNotation.IsWithin("osdu.tags", "osdu.tags"));
        Assert.True(RecordPathNotation.IsWithin("osdu.tags.Source", "osdu.tags"));
        Assert.True(RecordPathNotation.IsWithin("osdu.tags['lra.dataID']", "osdu.tags"));
        Assert.True(RecordPathNotation.IsWithin("osdu.data.Curves[].Unit", "osdu.data.Curves"));
        Assert.False(RecordPathNotation.IsWithin("osdu.tagsX", "osdu.tags"));
        Assert.False(RecordPathNotation.IsWithin("osdu.tags", "osdu.tags.Source"));

        Assert.Equal("Unit", RecordPathNotation.Below("data.Curves.Unit", "data.Curves"));
        Assert.Equal("['a.b']", RecordPathNotation.Below("data.X['a.b']", "data.X"));
        Assert.Null(RecordPathNotation.Below("data.X", "data.X"));
        Assert.Null(RecordPathNotation.Below("data.XY", "data.X"));
    }

    [Fact]
    public void A_search_names_a_field_by_its_names_joined_by_dots()
    {
        Assert.Equal("tags.lra.dataID", RecordPathNotation.SearchName("tags['lra.dataID']"));
        Assert.Equal("data.Curves.CurveID", RecordPathNotation.SearchName("data.Curves[*].CurveID"));
        Assert.Equal("data.Curves.CurveID", RecordPathNotation.SearchName("data.Curves[2].CurveID"));
        Assert.Null(RecordPathNotation.SearchName("tags['lra"));
    }

    [Fact]
    public void The_holder_and_the_last_name_of_a_path_are_read_by_its_steps()
    {
        Assert.Equal("data.Curves[]", RecordPathNotation.HolderOf("data.Curves[].Unit"));
        Assert.Equal("data", RecordPathNotation.HolderOf("data.Curves[]"));
        Assert.Equal("tags", RecordPathNotation.HolderOf("tags['lra.dataID']"));
        Assert.Equal(string.Empty, RecordPathNotation.HolderOf("Name"));

        Assert.Equal("Curves[2]", RecordPathNotation.LastName("data.Curves[2]"));
        Assert.Equal("lra.dataID", RecordPathNotation.LastName("tags['lra.dataID']"));
        Assert.Equal("CurveID", RecordPathNotation.LastName("osdu.data.Curves[].CurveID"));
    }

    [Fact]
    public void A_json_path_reads_a_quoted_name_to_its_closing_quote()
    {
        var record = JsonNode.Parse("""
            { "tags": { "lra.dataID": "6632", "a]b": "bracket", "it's": "quote", "Data Owner": "Ops" }, "items": [ { "id": "1" }, { "id": "2" } ] }
            """)!;
        Assert.Equal("6632", Assert.Single(JsonPathReader.SelectNodes(record, "tags['lra.dataID']")).GetValue<string>());
        Assert.Equal("6632", Assert.Single(JsonPathReader.SelectNodes(record, "tags[\"lra.dataID\"]")).GetValue<string>());
        Assert.Equal("bracket", Assert.Single(JsonPathReader.SelectNodes(record, "tags['a]b']")).GetValue<string>());
        Assert.Equal("quote", Assert.Single(JsonPathReader.SelectNodes(record, "tags['it" + Slash + "'s']")).GetValue<string>());
        Assert.Equal("Ops", Assert.Single(JsonPathReader.SelectNodes(record, "tags['Data Owner']")).GetValue<string>());
        Assert.Empty(JsonPathReader.SelectNodes(record, "tags.lra.dataID"));

        // What it read before reads as it did: dotted names, places, every item, and a name in brackets without quotes.
        Assert.Equal(["1", "2"], JsonPathReader.SelectNodes(record, "items[*].id").Select(n => n.GetValue<string>()));
        Assert.Equal("2", Assert.Single(JsonPathReader.SelectNodes(record, "items[1].id")).GetValue<string>());
        Assert.Equal("Ops", Assert.Single(JsonPathReader.SelectNodes(record, "$.tags['Data Owner']")).GetValue<string>());

        Assert.Throws<DeliveryException>(() => JsonPathReader.SelectNodes(record, "tags['lra"));
    }

    private static IReadOnlyList<RecordPathStep> Parse(string path)
    {
        Assert.True(RecordPathNotation.TryParse(path, out var steps, out var error), $"{path}: {error}");
        return steps;
    }
}

/// <summary>A template path names a property whose name a path cannot write bare as one step, in brackets.</summary>
public class TemplatePathNameTests
{
    [Fact]
    public void A_path_writes_a_name_holding_a_dot_as_one_step()
    {
        var tag = Parse("osdu.tags['lra.dataID']");
        Assert.Equal("osdu.tags['lra.dataID']", tag.Text);
        Assert.Equal("tags['lra.dataID']", tag.RecordPath);
        Assert.Equal("tags['lra.dataID']", tag.SchemaPath);
        Assert.Equal(["tags", "lra.dataID"], tag.Segments.Select(s => s.Name));
        Assert.Equal("lra.dataID", tag.Leaf);
        Assert.Equal("osdu.tags", tag.Parent!.Text);
        Assert.Equal(tag, TemplatePath.Of([new TemplatePathSegment("tags", false), new TemplatePathSegment("lra.dataID", false)]));

        var item = Parse("osdu.data.Curves[]['a.b']");
        Assert.True(item.IsRepeated);
        Assert.Equal("osdu.data.Curves", item.Repeater!.Text);
        Assert.Equal(["a.b"], item.WithinItem);
        Assert.Equal("data.Curves['a.b']", item.SchemaPath);

        var root = Parse("osdu['a.b']");
        Assert.Equal("osdu['a.b']", root.Text);
        Assert.Equal("a.b", root.Root);
        Assert.Equal("osdu['a.b']", TemplatePath.TextOf(root.RecordPath));
    }

    [Theory]
    [InlineData("osdu.data.Name")]
    [InlineData("osdu.data.Curves[].CurveUnit")]
    [InlineData("osdu.tags.WellLogNativeUID")]
    [InlineData("osdu.data.ExtensionProperties.WellDB.Curves[].OriginalUnit")]
    [InlineData("osdu.data.$Odd")]
    public void A_path_holding_no_such_name_is_written_as_it_always_was(string text)
        => Assert.Equal(text, Parse(text).Text);

    [Fact]
    public void A_name_quoted_that_could_be_bare_is_one_path_with_the_bare_one()
    {
        Assert.Equal("osdu.tags.Source", Parse("osdu.tags['Source']").Text);
        Assert.Equal(Parse("osdu.tags.Source"), Parse("osdu.tags[\"Source\"]"));
    }

    [Theory]
    [InlineData("osdu.data.Curves[0].X", "names one item of an array")]
    [InlineData("osdu.data.Curves[*].X", "names every item of an array")]
    [InlineData("osdu.data.Curves[][].X", "steps into the items of its items")]
    [InlineData("osdu.tags['lra", "invalid property name")]
    [InlineData("osdu.tags[lra.x]", "invalid property name")]
    [InlineData("osdu.data.Name['a.b'][]", "names the items of an array")]
    [InlineData("osdu", "must start with")]
    public void What_is_not_a_template_path_is_refused(string text, string reason)
    {
        Assert.False(TemplatePath.TryParse(text, out var path, out var error));
        Assert.Null(path);
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_from_steps_steps_into_one_array_at_most_and_never_ends_at_its_items()
    {
        Assert.False(TemplatePath.TryCreate([new("data", false), new("Curves", true), new("Points", true), new("X", false)], out _, out var twice));
        Assert.Contains("steps into more than one array", twice, StringComparison.Ordinal);
        Assert.False(TemplatePath.TryCreate([new("data", false), new("Curves", true)], out _, out var items));
        Assert.Contains("names the items of an array", items, StringComparison.Ordinal);
        Assert.False(TemplatePath.TryCreate([new("data", false), new(string.Empty, false)], out _, out var empty));
        Assert.Contains("empty name", empty, StringComparison.Ordinal);
    }

    private static TemplatePath Parse(string text)
    {
        Assert.True(TemplatePath.TryParse(text, out var path, out var error), $"{text}: {error}");
        return path!;
    }
}
