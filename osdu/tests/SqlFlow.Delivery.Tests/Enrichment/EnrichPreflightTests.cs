using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Validation;
using Xunit;
using static SqlFlow.Delivery.Tests.EnrichmentSuite;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The checks of a mapping's enrichments as a whole (osdu/docs/reference/flow/enrich.md, What the preflight checks): a value the
/// flow supplies that no mapping of the chain declares, two writers of one path unless both add items to a list, an enrichment
/// writing under a key the flow preserves, a <c>$base</c> read of what is not written before the enrichment renders, an
/// enrichment mapping reading rows its enrichment does not have, and a <c>$link</c> inside a preserved key or writing one
/// value of an enrichment taking many rows. Each is refused before anything renders, naming the enrichment and the path.
/// </summary>
[Trait(EnrichmentSuite.Trait, EnrichmentSuite.Name)]
public sealed class EnrichPreflightTests
{
    private static FlowDefinition Flow(IReadOnlyDictionary<string, string>? parameters = null, IReadOnlyList<string>? preserved = null)
    {
        var flow = Samples.Targeting(new FlowTarget
        {
            Endpoint = FakeOsduPlatform.Endpoint,
            Protocol = Protocols.DeliveryProtocol.Storage,
            ProtocolOptions = new ProtocolOptions { PreserveDataKeys = preserved ?? [] },
        });
        return flow with { Render = flow.Render with { Parameters = parameters ?? new Dictionary<string, string>(StringComparer.Ordinal) } };
    }

    private static IReadOnlyList<ValidationIssue> Check(FlowDefinition flow, MappingDefinition owner, params ResolvedStep[] steps)
        => Preflight.CheckChain(flow, owner, TestSchema.Build(), steps, "flows/demo.yaml");

    private static string Only(IReadOnlyList<ValidationIssue> issues) => Assert.Single(issues).Message;

    [Fact]
    public void A_chain_that_only_adds_passes()
    {
        var grade = Fragment("data:\n  Symbol: { $from: grade }\n  Aliases: [{ $from: alias }]", name: "Grade");

        Assert.Empty(Check(Flow(), Owner("Aliases: [x]"), Step("grade", grade)));
    }

    [Fact]
    public void A_value_the_flow_supplies_that_no_mapping_of_the_chain_declares_is_refused()
    {
        var withLabel = Fragment("data:\n  Symbol: { $from: grade }", name: "Labelled", parameters: "label: { required: false }\n");
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["label"] = "x", ["typo"] = "y" };

        var message = Only(Check(Flow(parameters), Owner(), Step("labelled", withLabel)));

        Assert.Contains("render.parameters supplies 'typo', which neither the flow's mapping Thing@1.0.0 nor the mapping of any of its enrichments declares", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_writing_a_value_the_mapping_writes_is_refused_naming_both()
    {
        var clash = Fragment("data:\n  Name: { $from: other }", name: "Clash");

        var message = Only(Check(Flow(), Owner(), Step("clash", clash)));

        Assert.Contains("enrich.clash: enrichment mapping Clash@1.0.0 writes data.Name, which the flow's mapping Thing@1.0.0 writes", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_steps_setting_one_path_are_refused_and_two_adding_to_one_list_are_not()
    {
        var first = Fragment("data:\n  Symbol: { $from: a }\n  Aliases: [{ $from: a }]", name: "First");
        var second = Fragment("data:\n  Symbol: { $from: b }\n  Aliases: [{ $from: b }]", name: "Second");

        var message = Only(Check(Flow(), Owner(), Step("first", first), Step("second", second)));

        Assert.Contains("enrich.second: enrichment mapping Second@1.0.0 writes data.Symbol, which enrichment 'first' (First@1.0.0) writes", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_writing_inside_what_the_mapping_writes_whole_is_refused()
    {
        var owner = Owner("Nested: { $value: { Inner: fixed } }");
        var inside = Fragment("data:\n  Nested:\n    Inner: { $from: x }", name: "Inside");

        var message = Only(Check(Flow(), owner, Step("inside", inside)));

        Assert.Contains("writes data.Nested.Inner, inside data.Nested, which the flow's mapping Thing@1.0.0 writes", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_writing_under_a_preserved_key_is_refused_since_it_would_reach_a_record_only_once()
    {
        var extension = Fragment("data:\n  Aliases: [{ $from: alias }]", name: "Extension");

        var message = Only(Check(Flow(preserved: ["Aliases"]), Owner(), Step("extension", extension)));

        Assert.Contains("carries data.Aliases over from the record OSDU holds into every update", message, StringComparison.Ordinal);
        Assert.Contains("Only the datasets the mapping links there with $link merge with what OSDU holds", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_base_read_of_what_the_mapping_or_an_earlier_step_writes_passes()
    {
        var inner = Fragment("data:\n  Nested:\n    Inner: { $from: inner }", name: "Inner");
        var copy = Fragment("data:\n  Description: { $base: data.Nested.Inner }\n  Symbol: { $base: data.Name }", name: "Copy");

        Assert.Empty(Check(Flow(), Owner(), Step("inner", inner), Step("copy", copy, readsRows: false, after: ["inner"])));
    }

    [Fact]
    public void A_base_read_of_what_a_later_step_writes_is_refused_naming_the_step_to_render_after()
    {
        var copy = Fragment("data:\n  Description: { $base: data.Nested.Inner }", name: "Copy");
        var inner = Fragment("data:\n  Nested:\n    Inner: { $from: inner }", name: "Inner");

        var message = Only(Check(Flow(), Owner(), Step("copy", copy, readsRows: false), Step("inner", inner)));

        Assert.Contains("reads data.Nested.Inner with $base, which enrichment 'inner' writes after this one renders; name it under enrich.copy.after", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_base_read_of_what_the_step_itself_writes_is_refused()
    {
        var self = Fragment("data:\n  Symbol: { $from: s }\n  Description: { $base: data.Symbol }", name: "Self");

        Assert.Contains("which the enrichment itself writes", Only(Check(Flow(), Owner(), Step("self", self))), StringComparison.Ordinal);
    }

    [Fact]
    public void A_base_read_of_what_nothing_writes_or_the_template_lacks_is_refused()
    {
        var unwritten = Fragment("data:\n  Description: { $base: data.Symbol }", name: "Unwritten");
        var unknown = Fragment("data:\n  Description: { $base: data.Nested.Nowhere }", name: "Unknown");

        Assert.Contains("neither the flow's mapping Thing@1.0.0 nor an enrichment that renders before this one writes it", Only(Check(Flow(), Owner(), Step("unwritten", unwritten, readsRows: false))), StringComparison.Ordinal);
        Assert.Contains("declares no data.Nested.Nowhere", Only(Check(Flow(), Owner(), Step("unknown", unknown, readsRows: false))), StringComparison.Ordinal);
    }

    [Fact]
    public void A_fragment_of_a_step_without_rows_reading_a_column_is_refused()
    {
        var reads = Fragment("data:\n  Symbol: { $from: grade }", name: "Reads");

        Assert.Contains("the enrichment reads no rows, so it has no row to read; declare rows: in the mapping's enrich.reads", Only(Check(Flow(), Owner(), Step("reads", reads, readsRows: false))), StringComparison.Ordinal);
    }

    [Fact]
    public void A_fragment_of_a_step_taking_many_rows_reads_them_under_its_forEach_only()
    {
        var outside = Fragment("data:\n  Symbol: { $from: grade }", name: "Outside");
        var inside = Fragment("data:\n  Curves:\n    $forEach: many\n    $item:\n      CurveID: { $from: grade }", name: "Inside");

        Assert.Contains("outside a $forEach, and the enrichment takes many rows per record (rows: many)", Only(Check(Flow(), Owner(), Step("many", outside, rows: EnrichRows.Many))), StringComparison.Ordinal);
        Assert.Empty(Check(Flow(), Owner(), Step("many", inside, rows: EnrichRows.Many)));
    }

    [Fact]
    public void A_fragment_repeating_rows_other_than_its_steps_is_refused()
    {
        var other = Fragment("data:\n  Curves:\n    $forEach: curves\n    $item:\n      CurveID: { $from: id }", name: "Other");

        Assert.Contains("an enrichment mapping reads its enrichment's rows alone: write $forEach: many", Only(Check(Flow(), Owner(), Step("many", other, rows: EnrichRows.Many))), StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_in_a_preserved_list_passes_and_one_inside_a_preserved_object_is_refused()
    {
        const string enrich = "lasfile:\n  rows: one\n  mapping: LasDataset@1.0.0\n";
        var listed = Owner(data: "Aliases: [x, { $link: lasfile }]", enrich: enrich);
        var nested = Owner(data: "Nested:\n  Inner: { $link: lasfile }", enrich: enrich);

        Assert.Empty(Check(Flow(preserved: ["Aliases"]), listed, RecordsStep()));
        var message = Only(Check(Flow(preserved: ["Nested"]), nested, RecordsStep()));
        Assert.Contains("links the datasets of enrich.lasfile inside data.Nested, which target.protocolOptions.preserveDataKeys carries over whole", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_writing_one_value_of_an_enrichment_taking_many_rows_is_refused_and_one_taking_one_row_passes()
    {
        var single = Owner(data: "Symbol: { $link: lasfile }", enrich: "lasfile:\n  rows: many\n  key: [file_name]\n  mapping: LasDataset@1.0.0\n");
        var one = Owner(data: "Symbol: { $link: lasfile }", enrich: "lasfile:\n  rows: one\n  mapping: LasDataset@1.0.0\n");

        var message = Only(Check(Flow(), single, RecordsStep(rows: EnrichRows.Many)));
        Assert.Contains("which registers one for each of its rows (rows: many), and template", message, StringComparison.Ordinal);
        Assert.Contains("link them from a list of values, such as Datasets: [ { $link: lasfile } ]", message, StringComparison.Ordinal);
        Assert.Empty(Check(Flow(), one, RecordsStep()));
    }
}
