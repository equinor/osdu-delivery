using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Preview;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using Xunit;
using static SqlFlow.Delivery.Tests.EnrichmentSuite;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The <c>$link</c> node (osdu/docs/reference/flow/enrich-records.md): written in the mapping that keys the record, naming an
/// enrichment of it that adds records, for the record as a whole (never under a <c>$forEach</c>, in an item of a list of objects
/// or as an alternative of <c>$coalesce</c>), with <c>$when</c> and <c>$description</c> alone beside it; refused naming what to
/// write otherwise. It gives every id in a list of values and the one id of an enrichment taking one row elsewhere, and what a
/// preview shows of an enrichment's records.
/// </summary>
[Trait(EnrichmentSuite.Trait, EnrichmentSuite.Name)]
public sealed class LinkTests
{
    private const string OneRow = "lasfile:\n  rows: one\n  mapping: LasDataset@1.0.0\n";

    private static string Refused(string data, string enrich = OneRow)
        => Assert.Throws<FlowValidationException>(() => Owner(data: data, enrich: enrich)).Message;

    [Fact]
    public void A_link_reads_as_a_node_of_its_own_naming_its_enrichment()
    {
        var owner = Owner(data: "Aliases: [x, { $link: lasfile, $description: The log's LAS file. }]", enrich: OneRow);

        var node = owner.Entries.SelectMany(e => e.ValueNodes).Single(n => n.Source?.Kind == MappingSourceKind.Link);
        Assert.Equal(("lasfile", "link.lasfile", false, "The log's LAS file."), (node.Source!.LinkName, node.Source.ToString(), node.Required, node.Description));
    }

    [Theory]
    [InlineData("Curves:\n  $forEach: curves\n  $item:\n    CurveID: { $link: lasfile }", "a row of curves has none of its own; write the link outside the $forEach")]
    [InlineData("Curves: [{ CurveID: { $link: lasfile } }]", "and an item of the list of objects")]
    [InlineData("Symbol:\n  $coalesce:\n    - $from: symbol\n    - $link: lasfile", "a $link node gives the ids of the records an enrichment registers for the record, and nothing when it registers none")]
    [InlineData("Symbol: { $link: lasfile, $required: true }", "a $link node never holds its record")]
    [InlineData("Symbol: { $link: lasfile, $modifiers: [upper] }", "so it takes '$when' and '$description' beside it, not '$modifiers'")]
    [InlineData("Symbol: { $link: 9lives }", "$link names an enrichment of the mapping, such as Datasets: [ { $link: lasfile } ]; '9lives' is not the name of one")]
    [InlineData("Symbol: { $link: ghost }", "$link names 'ghost', which is no enrichment of the mapping; it declares lasfile")]
    public void A_link_written_where_it_has_no_one_meaning_is_refused_naming_what_to_write(string data, string reason)
        => Assert.Contains(reason, Refused(data), StringComparison.Ordinal);

    [Fact]
    public void A_link_in_a_mapping_declaring_no_enrichment_says_where_to_declare_it()
        => Assert.Contains(
            "$link names 'lasfile', and the mapping declares no enrichment; declare it under enrich.lasfile",
            Assert.Throws<FlowValidationException>(() => Owner(data: "Symbol: { $link: lasfile }")).Message,
            StringComparison.Ordinal);

    [Fact]
    public void A_link_to_an_enrichment_naming_bulk_data_alone_is_refused_since_it_adds_no_record()
        => Assert.Contains(
            "$link names enrich.grid, which names the record's bulk data alone and adds no record",
            Refused("Symbol: { $link: grid }", "grid:\n  rows: one\n  bulk: true\n"),
            StringComparison.Ordinal);

    [Fact]
    public void An_enrichment_mapping_links_nothing_of_its_own()
    {
        var refused = Assert.Throws<FlowValidationException>(() => Fragment("data:\n  Aliases: [{ $link: lasfile }]"));

        Assert.Contains("an enrichment mapping declares no enrichments; write the link in the mapping that keys the record", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_whose_when_does_not_hold_leaves_the_property_out()
    {
        var owner = Owner(data: "Symbol: { $link: lasfile, $when: 'name = \"b\"' }", enrich: OneRow);
        var chain = new RecordChain(Renderer(owner), [RecordsStep(files: LasFiles())]);
        IReadOnlyDictionary<string, string?>[] rows = [Row(("las_folder", "f"), ("las_hash", "h"))];

        Assert.Null(At(chain.Render(Record("a", 1, ("lasfile", rows))).Document, "data.Symbol"));
        Assert.NotNull(At(chain.Render(Record("b", 1, ("lasfile", rows))).Document, "data.Symbol"));
    }

    [Fact]
    public void A_link_writing_one_value_of_several_records_holds_the_record_naming_the_way_on()
    {
        var owner = Owner(data: "Symbol: { $link: lasfile }", enrich: "lasfile:\n  rows: many\n  key: [file_name]\n  mapping: LasDataset@1.0.0\n");
        var chain = new RecordChain(Renderer(owner), [RecordsStep(rows: EnrichRows.Many, files: LasFiles())]);

        var result = chain.Render(Record("a", 1, ("lasfile", [Row(("file_name", "a.las")), Row(("file_name", "b.las"))])));

        Assert.True(result.IsHeld);
        Assert.Contains(
            result.Holds,
            h => h.Contains("$link: lasfile gives the 2 datasets the enrichment registers for this record, and the template takes one value here", StringComparison.Ordinal));
    }

    [Fact]
    public void A_mapping_rendered_without_its_enrichments_links_nothing()
    {
        var owner = Owner(data: "Symbol: { $link: lasfile }", enrich: OneRow);

        var result = Renderer(owner).Render(Record("a", 1, ("lasfile", [Row(("las_folder", "f"))])));

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Null(At(result.Document, "data.Symbol"));
    }

    [Fact]
    public void A_preview_shows_each_record_an_enrichment_adds_and_says_it_adds_records()
    {
        var mapping = Fragment("data:\n  Name: { $from: file_name }", name: "LasDataset", kind: DatasetKind);
        var step = RecordsStep(files: LasFiles(), mapping: mapping);
        var result = new RecordChain(Renderer(Owner(data: "Aliases: [{ $link: lasfile }]", enrich: OneRow)), [step])
            .Render(Record("a", 1, ("lasfile", [Row(("file_name", "a.las"), ("las_folder", "f"), ("las_hash", "h"))])));

        var preview = PreviewEnrichStep.Of(Assert.Single(result.Enrichment));
        var added = Assert.Single(preview.Records);
        Assert.Equal((result.Enrichment[0].Records[0].Id, DatasetKind, 0), (added.Id, added.Kind, added.Row));
        Assert.Equal("a.las", added.Record!["data"]!["Name"]!.GetValue<string>());
        Assert.Equal(["records"], PreviewEnrichStep.SuppliesOf(step.Step));
        Assert.Equal(["records", "bulk"], PreviewEnrichStep.SuppliesOf(step.Step with { Bulk = LasFiles() }));
        Assert.Empty(PreviewEnrichStep.SuppliesOf(Step("grade", Fragment("data:\n  Symbol: { $from: grade }", name: "Grade")).Step));
        Assert.Null(Assert.Single(PreviewEnrichStep.WithoutValues([preview])[0].Records).Record);
    }

    [Fact]
    public void Linked_ids_are_the_records_own_with_the_enrichments_suffix_made_before_anything_is_sent()
    {
        var step = RecordsStep(files: LasFiles());
        var links = new EnrichLinks(Record("a", 1, ("lasfile", [Row(("las_folder", "f"))])), [step], new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

        var ids = links.Ids("lasfile", "dev:work-product-component--Thing:abc");

        Assert.Equal(["dev:dataset--File.Generic:abc-enrich.lasfile"], ids);
        Assert.Empty(links.Ids("lasfile", null));
        Assert.Empty(links.Ids("ghost", "dev:work-product-component--Thing:abc"));
        var kept = new EnrichLinks(Record("a", 1), [step], new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["lasfile"] = ["kept"] });
        Assert.Equal(["kept"], kept.Ids("lasfile", "dev:work-product-component--Thing:abc"));
    }

    [Fact]
    public void A_record_written_as_canonical_JSON_keeps_its_link_text()
    {
        var owner = Owner(data: "Aliases: [{ $link: lasfile }]", enrich: OneRow);
        var result = new RecordChain(Renderer(owner), [RecordsStep(files: LasFiles())]).Render(Record("a", 1, ("lasfile", [Row(("las_folder", "f"))])));

        var link = Assert.Single((JsonArray)At(result.Document, "data.Aliases")!)!.GetValue<string>();
        Assert.EndsWith("-enrich.lasfile:", link, StringComparison.Ordinal);
        Assert.Contains(link, result.Canonical, StringComparison.Ordinal);
    }
}
