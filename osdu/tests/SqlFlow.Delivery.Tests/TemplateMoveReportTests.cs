using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Moving a mapping to a newer version of its kind, read against what its document writes: what it has to change, what it
/// writes that is deprecated or changed without breaking it, what it may now fill, how much each change between the two
/// versions weighs for the synced mappings, and the report that says it as Markdown, HTML and JSON.
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

    /// <summary>
    /// Symbol deprecated in favour of a new Mark, Weight given a description, Count given one too, and a new optional Mark:
    /// a minor version that breaks nothing.
    /// </summary>
    private static OsduTemplate Deprecating() => Newer(
        ("\"Symbol\":{\"type\":\"string\"}", "\"Symbol\":{\"type\":\"string\",\"description\":\"DEPRECATED: Use `Mark` instead. A symbol of the thing.\"}"),
        ("\"Weight\":{\"type\":\"number\"}", "\"Weight\":{\"type\":\"number\",\"description\":\"The weight of the thing.\"}"),
        ("\"Count\":{\"type\":\"integer\"}", "\"Count\":{\"type\":\"integer\",\"description\":\"How many there are.\"}"),
        ("\"Aliases\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}", "\"Aliases\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"Mark\":{\"type\":\"string\"}"));

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
        Assert.All(check.Findings, f => Assert.Equal(MappingFindingKind.MustChange, f.Kind));

        var weight = check.Findings[0];
        var type = Assert.Single(weight.Fields);
        Assert.Equal(("type", "number", "string"), (type.Field, type.Before, type.After));
        Assert.Null(weight.Requirement);
        Assert.Equal("dataset.weight", weight.Write?.Reads);

        Assert.Equal(TemplateVariableChangeKind.Removed, check.Findings[1].Change!.Change);

        // What the newer version requires is said as the gate says it, of the mapping pinned to the newer version, without
        // the document the report already names it by.
        var operatorFinding = check.Findings[2];
        Assert.Null(operatorFinding.Change);
        Assert.Equal($"template {NewerKind} requires osdu.data.Operator, which the mapping does not fill.", operatorFinding.Requirement);
    }

    [Fact]
    public void A_requirement_the_mapping_meets_is_minor_and_what_it_does_not_write_means_nothing_for_it()
    {
        // Symbol and Weight changed, but this mapping writes neither; Count became required, and the mapping fills it on every row.
        var newer = Newer(
            (",\"Symbol\":{\"type\":\"string\"}", string.Empty),
            ("\"Weight\":{\"type\":\"number\"}", "\"Weight\":{\"type\":\"string\"}"),
            ("\"required\":[\"Depth\"]", "\"required\":[\"Depth\",\"Count\"]"));
        var fills = TestSchema.Mapping("Count: { $from: count }");

        var ready = MappingMoves.Check(fills, Pinned, newer);
        Assert.Equal(MappingMoveState.Ready, ready.State);
        var count = Assert.Single(ready.Findings);
        Assert.Equal(("osdu.data.Count", MappingFindingKind.Changed), (count.Path, count.Kind));

        var report = Report(newer, [Moved("Thing@1.0.0", ready)]);
        Assert.Contains("| `osdu.data.Count` |  | `dataset.count` | It is now required, and the mapping fills it on every row. |", report.ToMarkdown(), StringComparison.Ordinal);
        Assert.Equal(
            [("osdu.data.Count", TemplateChangeWeight.Minor), ("osdu.data.Weight", TemplateChangeWeight.Unused), ("osdu.data.Symbol", TemplateChangeWeight.Unused)],
            report.WeighedChanges().Select(c => (c.Change.Path, c.Weight)));

        // The same mapping filling Count on some rows only would send records without it.
        var sometimes = MappingMoves.Check(TestSchema.Mapping("Count: { $from: count, $required: false }"), Pinned, newer);
        Assert.Equal(MappingMoveState.NeedsChanges, sometimes.State);
        var finding = Assert.Single(sometimes.Findings);
        Assert.Equal(("osdu.data.Count", MappingFindingKind.MustChange), (finding.Path, finding.Kind));
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

        // The removed object and what it held are both significant: the mapping has to stop writing inside it.
        var weighed = Report(newer, [Moved("Thing@1.0.0", check)]).WeighedChanges();
        Assert.All(weighed, c => Assert.Equal(TemplateChangeWeight.Significant, c.Weight));
        Assert.Equal(["osdu.data.Nested", "osdu.data.Nested.Inner"], weighed.Select(c => c.Change.Path));

        Assert.Equal(MappingMoveState.Current, MappingMoves.Check(mapping, Pinned, Pinned).State);
    }

    [Fact]
    public void A_deprecation_of_what_the_mapping_writes_is_significant_with_its_line_and_what_replaces_it()
    {
        var yaml = TestSchema.MappingDocument("""
            Weight: { $from: weight }
            Symbol: { $from: sym }
            """);
        var mapping = new DeliveryDocumentLoader().ParseMapping(yaml, "thing.yaml");
        var newer = Deprecating();

        var check = MappingMoves.Check(mapping, Pinned, newer, yaml);

        // A deprecation breaks nothing, so the mapping moves as it is, told what is deprecated, what reads differently and
        // what it may now fill, each in the order the document writes them.
        Assert.Equal(MappingMoveState.Ready, check.State);
        Assert.Equal(
            [("osdu.data.Symbol", MappingFindingKind.Deprecated), ("osdu.data.Weight", MappingFindingKind.Changed), ("osdu.data.Mark", MappingFindingKind.MayFill)],
            check.Findings.Select(f => (f.Path, f.Kind)));
        var symbol = check.Findings[0];
        Assert.Equal("Use `Mark` instead.", symbol.Deprecation);
        Assert.False(symbol.AlreadyDeprecated);
        var line = yaml.ReplaceLineEndings("\n").Split('\n').ToList().FindIndex(l => l.TrimStart().StartsWith("Symbol:", StringComparison.Ordinal)) + 1;
        Assert.Equal(new MappingWrite(line, "dataset.sym"), symbol.Write);
        Assert.Contains("osdu.data.Weight", check.Writes);
        Assert.DoesNotContain("osdu.data.Count", check.Writes);

        var report = Report(newer, [Moved("Thing@1.0.0", check)]);
        var weights = report.WeighedChanges().ToDictionary(c => c.Change.Path, c => c.Weight);
        Assert.Equal(TemplateChangeWeight.Significant, weights["osdu.data.Symbol"]);
        Assert.Equal(TemplateChangeWeight.Minor, weights["osdu.data.Weight"]);
        Assert.Equal(TemplateChangeWeight.Unused, weights["osdu.data.Count"]);
        Assert.Equal(TemplateChangeWeight.Unused, weights["osdu.data.Mark"]);

        var md = report.ToMarkdown();
        Assert.Contains(
            "1 synced mapping of the kind: 1 moves as it is. Read against what `Thing@1.0.0` writes, **1 of the 4 changes is significant** (1 deprecates what it writes); 1 is minor, touching what it writes without asking anything of it; 2 touch nothing a synced mapping uses.",
            md,
            StringComparison.Ordinal);
        Assert.Contains("It writes 8 properties: 1 deprecated in 1.1.0, 1 changed without breaking it; 6 are the same. 1.1.0 adds 1 property inside what it writes that it may fill.", md, StringComparison.Ordinal);
        Assert.Contains($"| `osdu.data.Symbol` | {line} | `dataset.sym` | Use `Mark` instead. |", md, StringComparison.Ordinal);
        Assert.Contains("- `osdu.data.Mark`: added: `string`.", md, StringComparison.Ordinal);
        var order = new[] { "#### Deprecated in 1.1.0 (1)", "#### Changed without breaking it (1)", "#### It may now fill (1)", "## Significant changes (1)", "## Minor changes (1)", "## Changes no synced mapping uses (2)" };
        Assert.Equal(order, order.OrderBy(heading => md.IndexOf(heading, StringComparison.Ordinal)));
        Assert.All(order, heading => Assert.Contains(heading, md, StringComparison.Ordinal));
        Assert.Contains("- `osdu.data.Symbol`: changed: description, now deprecated (wording in the schema). Used by `Thing@1.0.0`.", md, StringComparison.Ordinal);

        var json = JsonNode.Parse(report.ToJson())!;
        Assert.Equal((1, 1, 2), ((int)json["counts"]!["significant"]!, (int)json["counts"]!["minor"]!, (int)json["counts"]!["unused"]!));
        var change = json["changes"]!.AsArray().Single(c => (string?)c!["path"] == "osdu.data.Symbol")!;
        Assert.Equal(("Significant", true), ((string?)change["weight"], (bool)change["deprecates"]!));
        Assert.Equal(["Thing@1.0.0"], change["usedBy"]!.AsArray().Select(n => (string?)n));
        var moved = json["mappings"]!.AsArray().Single()!;
        Assert.Equal(8, (int)moved["writes"]!);
        Assert.Equal(1, (int)moved["counts"]!["deprecated"]!);
        var deprecated = moved["findings"]!.AsArray().First()!;
        Assert.Equal(("Deprecated", line, "dataset.sym", "Use `Mark` instead."), ((string?)deprecated["kind"], (int)deprecated["line"]!, (string?)deprecated["reads"], (string?)deprecated["deprecation"]));
    }

    [Fact]
    public void Breaking_changes_to_nothing_a_mapping_writes_are_not_significant()
    {
        // Two breaking changes, to what the mapping (writing Name and Depth alone) never writes.
        var newer = Newer(
            (",\"Symbol\":{\"type\":\"string\"}", string.Empty),
            ("\"Weight\":{\"type\":\"number\"}", "\"Weight\":{\"type\":\"string\"}"));
        var check = MappingMoves.Check(TestSchema.Mapping(), Pinned, newer);
        Assert.Equal(MappingMoveState.Ready, check.State);
        Assert.Empty(check.Findings);

        var report = Report(newer, [Moved("Thing@1.0.0", check)]);
        Assert.All(report.WeighedChanges(), c => Assert.Equal(TemplateChangeWeight.Unused, c.Weight));
        var md = report.ToMarkdown();
        Assert.Contains(
            "Read against what `Thing@1.0.0` writes, **none of the 2 changes is significant**: none touches it, so each matters only to a mapping that starts to use what it changes.",
            md,
            StringComparison.Ordinal);
        Assert.Contains("It writes 6 properties: 1.1.0 changes none of them; 6 are the same.", md, StringComparison.Ordinal);
        Assert.Contains("## Changes no synced mapping uses (2)", md, StringComparison.Ordinal);
        Assert.DoesNotContain("## Significant changes", md, StringComparison.Ordinal);
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
            Moved("Thing@1.0.0", MappingMoves.Check(mapping, Pinned, newer)),
            new TemplateMoveMapping("welldb", "Thing@2.0.0", "mappings/thing2.yaml", NewerKind, newer.Version, new MappingMoveCheck(MappingMoveState.Current, [])),
            new TemplateMoveMapping("archive", "Old@1.0.0", "old.yaml", TestSchema.Kind, null, new MappingMoveCheck(MappingMoveState.Unchecked, []), "it does not *load*"),
        ]);

        var md = report.ToMarkdown();
        Assert.DoesNotContain("\r", md, StringComparison.Ordinal);
        Assert.StartsWith("# Moving Thing from 1.0.0 (v0.29.0) to 1.1.0 (v0.30.0)\n", md, StringComparison.Ordinal);
        Assert.Contains("| Kind | `test:wks:work-product-component--Thing:1.0.0` | `test:wks:work-product-component--Thing:1.1.0` |", md, StringComparison.Ordinal);
        Assert.Contains(
            "3 synced mappings of the kind: 1 needs changes, 1 could not be checked, 1 already pins 1.1.0. Read against what `Thing@1.0.0` writes, **3 of the 3 changes are significant** (3 ask a change of it). 1 mapping could not be checked, so what it writes is not weighed.",
            md,
            StringComparison.Ordinal);
        Assert.Contains("Between the two versions: **3 breaking, 0 additive, 0 wording**;", md, StringComparison.Ordinal);

        // The mappings come first, what needs changes before what could not be checked and what is already there.
        var order = new[] { "### `Thing@1.0.0`: needs changes", "### `Old@1.0.0`: could not be checked", "### `Thing@2.0.0`: already pins 1.1.0", "## Significant changes (3)" };
        Assert.Equal(order, order.OrderBy(heading => md.IndexOf(heading, StringComparison.Ordinal)));
        Assert.Contains("#### To change (3)", md, StringComparison.Ordinal);
        Assert.Contains("| `osdu.data.Weight` |  | `dataset.weight` | Its type is `string`, was `number`: what fills it has to give a value of the new type. |", md, StringComparison.Ordinal);
        Assert.Contains("| `osdu.data.Symbol` |  | `dataset.sym` | removed; it was `string`. Remove what writes it: the newer version has no such property. |", md, StringComparison.Ordinal);
        Assert.Contains($"| `osdu.data.Operator` |  | what it writes inside it | template {NewerKind} requires osdu.data.Operator, which the mapping does not fill. |", md, StringComparison.Ordinal);

        // A problem is prose, its markup escaped.
        Assert.Contains("It could not be checked: it does not \\*load\\*", md, StringComparison.Ordinal);

        // Each change, weighed, with what it asks of a mapping and the mappings that use it.
        Assert.Contains(
            "- `osdu.data.Operator`: added: `string`, required (breaking in the schema). Used by `Thing@1.0.0`. Fill it on every record: the newer version requires it inside an object a mapping of the older version already writes.",
            md,
            StringComparison.Ordinal);
        Assert.DoesNotContain("## Minor changes", md, StringComparison.Ordinal);
        Assert.DoesNotContain("## Changes no synced mapping uses", md, StringComparison.Ordinal);
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
        var report = Report(newer, [Moved("Thing@1.0.0", MappingMoves.Check(mapping, Pinned, newer))]);

        var text = report.ToJson();
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        var json = JsonNode.Parse(text)!.AsObject();
        Assert.Equal("test:wks:work-product-component--Thing", (string?)json["kind"]);
        Assert.Equal("v0.29.0", (string?)json["from"]!["release"]);
        Assert.Equal(3, (int)json["counts"]!["breaking"]!);
        Assert.Equal(1, (int)json["counts"]!["weighedMappings"]!);
        var moved = json["mappings"]!.AsArray().Single()!;
        Assert.Equal("NeedsChanges", (string?)moved["state"]);
        var weight = moved["findings"]!.AsArray().First()!;
        Assert.Equal(("osdu.data.Weight", "MustChange"), ((string?)weight["path"], (string?)weight["kind"]));
        Assert.Equal("Its type is string, was number: what fills it has to give a value of the new type.", (string?)weight["note"]);
        var removed = json["changes"]!.AsArray().Single(c => (string?)c!["path"] == "osdu.data.Symbol")!;
        Assert.Equal(("Removed", "Breaking", "Unused"), ((string?)removed["change"], (string?)removed["impact"], (string?)removed["weight"]));
        Assert.Empty(removed["usedBy"]!.AsArray());
        Assert.Equal("Remove what writes it: the newer version has no such property.", (string?)removed["action"]);
        Assert.Equal("2026-11-26T09:30:00Z", (string?)json["generatedUtc"]);

        // With no mapping to weigh against, nothing is weighed rather than everything unused.
        var alone = JsonNode.Parse(Report(newer, []).ToJson())!;
        Assert.Null(alone["counts"]!["significant"]);
        Assert.Null(alone["changes"]!.AsArray().First()!["weight"]);
    }

    [Fact]
    public void A_report_names_its_file_and_takes_the_formats_it_renders()
    {
        var report = Report(Breaking(), []);
        Assert.Equal("Thing-1.0.0-v0.29.0-to-1.1.0-v0.30.0-report.md", report.FileName(TemplateMoveReportFormat.Markdown));
        Assert.Equal("Thing-1.0.0-v0.29.0-to-1.1.0-v0.30.0-report.html", report.FileName(TemplateMoveReportFormat.Html));
        Assert.Equal(TemplateMoveReportFormat.Markdown, TemplateMoveReport.FormatOf(" MD "));
        Assert.Equal(TemplateMoveReportFormat.Html, TemplateMoveReport.FormatOf("HTML"));
        Assert.Equal(TemplateMoveReportFormat.Json, TemplateMoveReport.FormatOf(null));
        Assert.Null(TemplateMoveReport.FormatOf("pdf"));
        Assert.Equal("text/html; charset=utf-8", TemplateMoveReport.Describe(TemplateMoveReportFormat.Html).MediaType);
        var md = report.ToMarkdown();
        Assert.Contains("No synced repository holds a mapping of `test:wks:work-product-component--Thing`, so no change is weighed against what a mapping writes: each counts in full.", md, StringComparison.Ordinal);
        Assert.Contains("## Breaking changes (3)", md, StringComparison.Ordinal);
    }

    [Fact]
    public void The_html_page_stands_alone_and_sets_what_it_is_given_as_text()
    {
        var newer = Deprecating();
        var yaml = TestSchema.MappingDocument("Symbol: { $from: sym }");
        var check = MappingMoves.Check(new DeliveryDocumentLoader().ParseMapping(yaml, "thing.yaml"), Pinned, newer, yaml);
        var report = Report(newer, [
            new TemplateMoveMapping("<script>alert(1)</script>", "Thing@1.0.0", "mappings/thing.yaml", TestSchema.Kind, Pinned.Version, check),
            new TemplateMoveMapping("archive", "Old@1.0.0", "old.yaml", TestSchema.Kind, null, new MappingMoveCheck(MappingMoveState.Unchecked, []), "a <b> & \u0001c"),
        ]);

        var html = report.ToHtml();
        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        Assert.Contains("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">", html, StringComparison.Ordinal);
        Assert.Contains("<title>Moving Thing from 1.0.0 (v0.29.0) to 1.1.0 (v0.30.0)</title>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("It could not be checked: a &lt;b&gt; &amp; ", html, StringComparison.Ordinal);

        // The verdict and the deprecation's guidance keep their emphasis and code; no marker is left behind.
        Assert.Contains("<strong>1 of the 4 changes is significant</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<td>Use <code>Mark</code> instead.</td>", html, StringComparison.Ordinal);
        Assert.Contains("<code class=\"path\">osdu.<wbr>data.<wbr>Symbol</code>", html, StringComparison.Ordinal);
        Assert.Contains("<details class=\"weight unused\"><summary>Changes no synced mapping uses <span class=\"count\">3</span></summary>", html, StringComparison.Ordinal);
        Assert.DoesNotContain(html, c => c is '\u0001' or '\u0002' or '\u0003' or '\u0004');
        Assert.DoesNotContain(char.ConvertFromUtf32(0x2014), html, StringComparison.Ordinal);
        Assert.EndsWith("</main></body></html>\n", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_deprecation_says_what_replaces_its_property_in_its_first_sentence()
    {
        Assert.True(TemplateDeprecation.IsDeprecated("  DEPRECATED: anything"));
        Assert.False(TemplateDeprecation.IsDeprecated("Deprecated in a later release, maybe."));
        Assert.False(TemplateDeprecation.IsDeprecated(null));
        Assert.Null(TemplateDeprecation.Guidance("The weight of the thing."));
        Assert.Null(TemplateDeprecation.Guidance("DEPRECATED:   "));

        // A full stop inside a code span or a dotted name ends nothing; the sentence is put on one line.
        Assert.Equal(
            "Use data.VerticalMeasurement.VerticalReferenceID instead.",
            TemplateDeprecation.Guidance("DEPRECATED: Use data.VerticalMeasurement.VerticalReferenceID instead. References an entry."));
        Assert.Equal(
            "Use the Well Log Acquisition schema `LogRuns[].LogPasses[].PassTypeID` property.",
            TemplateDeprecation.Guidance("DEPRECATED:  Use the Well Log Acquisition schema `LogRuns[].LogPasses[].PassTypeID` property. This is now a reference value."));
        Assert.Equal("Use the new one\nplease.".Replace('\n', ' '), TemplateDeprecation.Guidance("DEPRECATED: Use the new one\nplease. Old text."));

        // A first sentence too long to quote is cut at a word.
        var guidance = TemplateDeprecation.Guidance("DEPRECATED: " + string.Join(' ', Enumerable.Repeat("word", 100)) + ".");
        Assert.NotNull(guidance);
        Assert.EndsWith("word ...", guidance, StringComparison.Ordinal);
        Assert.True(guidance.Length <= 284);
    }

    private static TemplateMoveMapping Moved(string reference, MappingMoveCheck check)
        => new("welldb", reference, "mappings/thing.yaml", TestSchema.Kind, Pinned.Version, check);

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
}
