using System.Text.Json.Nodes;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Moving a mapping to a newer version of its kind: what a mapping has to change, set against what it writes and what the
/// newer version requires, and the report that says it with every change between the two versions.
/// </summary>
public class TemplateMoveReportTests
{
    private const string NewerKind = "test:wks:work-product-component--Thing:1.1.0";

    private static readonly DateTime Generated = new(2026, 11, 26, 9, 30, 0, DateTimeKind.Utc);

    /// <summary>The test template with <paramref name="edits"/> made to its JSON, as version 1.1.0 of its kind.</summary>
    private static OsduTemplate Newer(params (string Find, string Replace)[] edits)
    {
        var json = TestSchema.Build().Root.ToJsonString();
        foreach (var (find, replace) in edits)
        {
            Assert.Contains(find, json, StringComparison.Ordinal);
            json = json.Replace(find, replace, StringComparison.Ordinal);
        }

        return OsduTemplate.From(SchemaSnapshot.Parse(NewerKind, json, DateTimeOffset.UnixEpoch));
    }

    private static OsduTemplate Pinned => OsduTemplate.From(TestSchema.Build());

    /// <summary>Symbol removed, Weight now text, and a new Operator the record has to carry.</summary>
    private static OsduTemplate Breaking() => Newer(
        (",\"Symbol\":{\"type\":\"string\"}", string.Empty),
        ("\"Weight\":{\"type\":\"number\"}", "\"Weight\":{\"type\":\"string\"}"),
        ("\"Aliases\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}", "\"Aliases\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"Operator\":{\"type\":\"string\"}"),
        ("\"required\":[\"Depth\"]", "\"required\":[\"Depth\",\"Operator\"]"));

    [Fact]
    public void A_mapping_is_held_to_what_it_writes_and_to_what_the_newer_version_requires()
    {
        var mapping = TestSchema.Mapping("""
            Weight: { $from: weight }
            Symbol: { $from: sym }
            """);

        var check = MappingMoves.Check(mapping, Pinned, Breaking());

        Assert.Equal(MappingMoveState.NeedsChanges, check.State);
        Assert.Equal(["osdu.data.Weight", "osdu.data.Symbol", "osdu.data.Operator"], check.Findings.Select(f => f.Path));

        var weight = check.Findings[0];
        var type = Assert.Single(weight.Fields);
        Assert.Equal(("type", "number", "string"), (type.Field, type.Before, type.After));
        Assert.Null(weight.Requirement);

        Assert.Equal(TemplateVariableChangeKind.Removed, check.Findings[1].Change!.Change);

        // What the newer version requires is said as the gate says it, of the mapping pinned to the newer version, without
        // the document the report already names it by.
        var operatorFinding = check.Findings[2];
        Assert.Null(operatorFinding.Change);
        Assert.Equal($"template {NewerKind} requires osdu.data.Operator, which the mapping does not fill.", operatorFinding.Requirement);
    }

    [Fact]
    public void What_the_mapping_does_not_write_and_what_it_already_fills_are_no_findings()
    {
        // Symbol and Weight changed, but this mapping writes neither; Count became required, and the mapping fills it on every row.
        var newer = Newer(
            (",\"Symbol\":{\"type\":\"string\"}", string.Empty),
            ("\"Weight\":{\"type\":\"number\"}", "\"Weight\":{\"type\":\"string\"}"),
            ("\"required\":[\"Depth\"]", "\"required\":[\"Depth\",\"Count\"]"));
        var fills = TestSchema.Mapping("Count: { $from: count }");

        var ready = MappingMoves.Check(fills, Pinned, newer);
        Assert.Equal(MappingMoveState.Ready, ready.State);
        Assert.Empty(ready.Findings);

        // The same mapping filling Count on some rows only would send records without it.
        var sometimes = MappingMoves.Check(TestSchema.Mapping("Count: { $from: count, $required: false }"), Pinned, newer);
        Assert.Equal(MappingMoveState.NeedsChanges, sometimes.State);
        var finding = Assert.Single(sometimes.Findings);
        Assert.Equal("osdu.data.Count", finding.Path);
        Assert.Contains("$required: false", finding.Requirement, StringComparison.Ordinal);
    }

    [Fact]
    public void A_removed_object_is_one_finding_and_a_template_that_says_the_same_is_current()
    {
        var mapping = TestSchema.Mapping("Nested: { Inner: { $from: inner } }");
        var newer = Newer((",\"Nested\":{\"type\":\"object\",\"properties\":{\"Inner\":{\"type\":\"string\"}}}", string.Empty));

        var check = MappingMoves.Check(mapping, Pinned, newer);
        var removed = Assert.Single(check.Findings);
        Assert.Equal("osdu.data.Nested", removed.Path);

        Assert.Equal(MappingMoveState.Current, MappingMoves.Check(mapping, Pinned, Pinned).State);
    }

    [Fact]
    public void The_report_leads_with_the_mappings_and_says_what_each_change_asks_of_one()
    {
        var newer = Breaking();
        var mapping = TestSchema.Mapping("""
            Weight: { $from: weight }
            Symbol: { $from: sym }
            """);
        var report = Report(newer, [
            new TemplateMoveMapping("welldb", "Thing@1.0.0", "mappings/thing.yaml", TestSchema.Kind, Pinned.Version, MappingMoves.Check(mapping, Pinned, newer)),
            new TemplateMoveMapping("welldb", "Thing@2.0.0", "mappings/thing2.yaml", NewerKind, newer.Version, new MappingMoveCheck(MappingMoveState.Current, [])),
            new TemplateMoveMapping("archive", "Old@1.0.0", "old.yaml", TestSchema.Kind, null, new MappingMoveCheck(MappingMoveState.Unchecked, []), "it does not *load*"),
        ]);

        var md = report.ToMarkdown();
        Assert.DoesNotContain("\r", md, StringComparison.Ordinal);
        Assert.StartsWith("# Moving Thing from 1.0.0 (v0.29.0) to 1.1.0 (v0.30.0)\n", md, StringComparison.Ordinal);
        Assert.Contains("| Kind | `test:wks:work-product-component--Thing:1.0.0` | `test:wks:work-product-component--Thing:1.1.0` |", md, StringComparison.Ordinal);
        Assert.Contains("**3 breaking, 0 additive, 0 wording**", md, StringComparison.Ordinal);
        Assert.Contains("A mapping written for 1.0.0 needs a look at the breaking changes before it moves to 1.1.0.", md, StringComparison.Ordinal);

        // The mappings come first, what needs changes before what could not be checked and what is already there.
        Assert.Contains("3 mappings: 1 need changes, 1 could not be checked, 1 already pin 1.1.0.", md, StringComparison.Ordinal);
        var order = new[] { "### `Thing@1.0.0`: needs changes", "### `Old@1.0.0`: could not be checked", "### `Thing@2.0.0`: already pins 1.1.0", "## Breaking changes (3)" };
        Assert.Equal(order, order.OrderBy(heading => md.IndexOf(heading, StringComparison.Ordinal)));
        Assert.Contains("- `osdu.data.Weight`: Its type is `string`, was `number`: what fills it has to give a value of the new type.", md, StringComparison.Ordinal);
        Assert.Contains("- `osdu.data.Symbol`: removed; it was `string`. Remove what writes it: the newer version has no such property.", md, StringComparison.Ordinal);
        Assert.Contains($"- `osdu.data.Operator`: template {NewerKind} requires osdu.data.Operator, which the mapping does not fill.", md, StringComparison.Ordinal);

        // A problem is prose, its markup escaped.
        Assert.Contains("It could not be checked: it does not \\*load\\*", md, StringComparison.Ordinal);

        // The changes themselves, each with what it asks of a mapping.
        Assert.Contains("- `osdu.data.Operator`: added: `string`, required. Fill it on every record: the newer version requires it inside an object a mapping of the older version already writes.", md, StringComparison.Ordinal);
        Assert.DoesNotContain("## Additive changes", md, StringComparison.Ordinal);
        Assert.DoesNotContain("## Wording changes", md, StringComparison.Ordinal);
        Assert.Contains("Every shared schema the kind refers to is the same in both (2).", md, StringComparison.Ordinal);
        Assert.Contains("`sqlflow template capture --kind test:wks:work-product-component--Thing:1.1.0 --release v0.30.0 --out <folder>/<file>.json`", md, StringComparison.Ordinal);
        Assert.EndsWith("Generated 2026-11-26T09:30:00Z by OSDU Delivery.\n", md, StringComparison.Ordinal);
        Assert.DoesNotContain(char.ConvertFromUtf32(0x2014), md, StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_as_json_carries_the_same_findings_as_plain_text()
    {
        var newer = Breaking();
        var mapping = TestSchema.Mapping("Weight: { $from: weight }");
        var report = Report(newer, [new TemplateMoveMapping("welldb", "Thing@1.0.0", "mappings/thing.yaml", TestSchema.Kind, Pinned.Version, MappingMoves.Check(mapping, Pinned, newer))]);

        var text = report.ToJson();
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        var json = JsonNode.Parse(text)!.AsObject();
        Assert.Equal("test:wks:work-product-component--Thing", (string?)json["kind"]);
        Assert.Equal("v0.29.0", (string?)json["from"]!["release"]);
        Assert.Equal(3, (int)json["counts"]!["breaking"]!);
        var moved = json["mappings"]!.AsArray().Single()!;
        Assert.Equal("NeedsChanges", (string?)moved["state"]);
        var weight = moved["findings"]!.AsArray().First()!;
        Assert.Equal("osdu.data.Weight", (string?)weight["path"]);
        Assert.Equal("Its type is string, was number: what fills it has to give a value of the new type.", (string?)weight["note"]);
        var removed = json["changes"]!.AsArray().Single(c => (string?)c!["path"] == "osdu.data.Symbol")!;
        Assert.Equal(("Removed", "Breaking"), ((string?)removed["change"], (string?)removed["impact"]));
        Assert.Equal("Remove what writes it: the newer version has no such property.", (string?)removed["action"]);
        Assert.Equal("2026-11-26T09:30:00Z", (string?)json["generatedUtc"]);
    }

    [Fact]
    public void A_report_names_its_file_and_takes_the_formats_it_renders()
    {
        var report = Report(Breaking(), []);
        Assert.Equal("Thing-1.0.0-v0.29.0-to-1.1.0-v0.30.0-report.md", report.FileName(TemplateMoveReportFormat.Markdown));
        Assert.Equal(TemplateMoveReportFormat.Markdown, TemplateMoveReport.FormatOf(" MD "));
        Assert.Equal(TemplateMoveReportFormat.Json, TemplateMoveReport.FormatOf(null));
        Assert.Null(TemplateMoveReport.FormatOf("html"));
        Assert.Contains("No synced repository holds a mapping of `test:wks:work-product-component--Thing`.", report.ToMarkdown(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_holding_backticks_and_pipes_stays_one_code_span()
    {
        // A pattern with a backtick and an alternation, as a change shows it.
        var newer = Newer(("\"Symbol\":{\"type\":\"string\"}", "\"Symbol\":{\"type\":\"string\",\"pattern\":\"^(a|`b`)$\"}"));
        var md = Report(newer, []).ToMarkdown();
        Assert.Contains("Its values now have to match ``^(a|`b`)$``.", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_versions_that_say_the_same_are_no_change()
    {
        var md = Report(Pinned, [], sameFile: true).ToMarkdown();
        Assert.Contains("**No change.** The two versions are the same schema, file for file.", md, StringComparison.Ordinal);
        Assert.DoesNotContain("## Moving a mapping", md, StringComparison.Ordinal);
    }

    private static TemplateMoveReport Report(OsduTemplate newer, IReadOnlyList<TemplateMoveMapping> mappings, bool sameFile = false)
        => new(
            new TemplateMoveSide(TestSchema.Kind, "v0.29.0", "Generated/work-product-component/Thing.1.0.0.json", new Uri("https://example.org/v0.29.0/Thing.1.0.0.json"), "PUBLISHED", Pinned.Version),
            new TemplateMoveSide(newer.Kind, "v0.30.0", "Generated/work-product-component/Thing.1.1.0.json", new Uri("https://example.org/v0.30.0/Thing.1.1.0.json"), null, newer.Version),
            sameFile,
            OnlyIdentifiersDiffer: false,
            TemplateComparer.Compare(Pinned, newer),
            SameSharedSchemas: 2,
            SharedSchemas: [],
            mappings,
            Generated);
}
