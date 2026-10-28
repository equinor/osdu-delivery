using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Validation;
using Xunit;
using static SqlFlow.Delivery.Tests.EnrichmentSuite;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What the preflight checks of an enrichment adding records (osdu/docs/reference/flow/enrich.md, What the preflight checks):
/// the mapping's <c>$link</c> fills a list of values or a single value of the template, a list every writer shares by adding
/// items; it may sit in a list the flow preserves, whose links an update merges, but not inside one; an enrichment adding paths
/// may not add to a preserved list; an enrichment mapping's <c>$base</c> reads count as its enrichment's; and the route a flow
/// adding records gets wraps its own.
/// </summary>
[Trait(EnrichmentSuite.Trait, EnrichmentSuite.Name)]
public sealed class StepFilesPreflightTests
{
    private const string TwoEnrichments = "lasfile:\n  rows: one\n  mapping: LasDataset@1.0.0\ndlis:\n  rows: one\n  mapping: DlisDataset@1.0.0\n";

    private static FlowDefinition Flow(IReadOnlyList<string>? preserved = null) => Samples.Targeting(new FlowTarget
    {
        Endpoint = FakeOsduPlatform.Endpoint,
        Protocol = Protocols.DeliveryProtocol.Storage,
        ProtocolOptions = new ProtocolOptions { PreserveDataKeys = preserved ?? [] },
    });

    private static IReadOnlyList<ValidationIssue> Check(MappingDefinition owner, params ResolvedStep[] steps)
        => Preflight.CheckChain(Flow(), owner, TestSchema.Build(), steps, "flows/demo.yaml");

    /// <summary>The chain's checks on a flow that carries <paramref name="preserved"/> over from the record OSDU holds.</summary>
    private static IReadOnlyList<ValidationIssue> CheckPreserving(IReadOnlyList<string> preserved, MappingDefinition owner, params ResolvedStep[] steps)
        => Preflight.CheckChain(Flow(preserved), owner, TestSchema.Build(), steps, "flows/demo.yaml");

    /// <summary>The mapping's own preflight, which checks what each <c>$link</c> fills against the template.</summary>
    private static IReadOnlyList<ValidationIssue> Mapping(MappingDefinition owner)
        => Preflight.Check(owner, TestSchema.Build(), TestSchema.References(), TestSchema.Context(), sourceColumns: null).Where(i => i.Severity == IssueSeverity.Error).ToList();

    [Fact]
    public void Records_linked_from_a_list_of_values_pass_and_share_it_with_the_mapping_and_other_enrichments()
    {
        var grade = Fragment("data:\n  Aliases: [{ $from: alias }]", name: "Grade");
        var owner = Owner(data: "Aliases: [x, { $link: lasfile }, { $link: dlis }]", enrich: TwoEnrichments + "grade:\n  rows: one\n  mapping: Grade@1.0.0\n");

        Assert.Empty(Check(owner, RecordsStep("lasfile"), RecordsStep("dlis"), Step("grade", grade)));
        Assert.Empty(Mapping(owner));
    }

    [Fact]
    public void Records_linked_from_a_list_the_flow_preserves_pass_since_an_update_merges_the_links()
    {
        var owner = Owner(data: "Aliases: [{ $link: lasfile }, { $link: dlis }]", enrich: TwoEnrichments);

        Assert.Empty(CheckPreserving(["Aliases"], owner, RecordsStep("lasfile"), RecordsStep("dlis")));
    }

    [Fact]
    public void An_enrichment_adding_to_a_preserved_list_is_refused_though_the_mapping_may_link_records_there()
    {
        var grade = Fragment("data:\n  Aliases: [{ $from: alias }]", name: "Grade");
        var owner = Owner(data: "Aliases: [{ $link: lasfile }]", enrich: "lasfile:\n  rows: one\n  mapping: LasDataset@1.0.0\ngrade:\n  rows: one\n  mapping: Grade@1.0.0\n");

        var message = Assert.Single(CheckPreserving(["Aliases"], owner, RecordsStep("lasfile"), Step("grade", grade))).Message;

        Assert.Contains("enrich.grade: enrichment mapping Grade@1.0.0 writes data.Aliases, and", message, StringComparison.Ordinal);
        Assert.Contains("Only the datasets the mapping links there with $link merge with what OSDU holds", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_inside_a_preserved_object_is_refused_since_only_the_preserved_list_itself_merges()
    {
        var owner = Owner(data: "Nested:\n  Inner: { $link: lasfile }", enrich: "lasfile:\n  rows: one\n  mapping: LasDataset@1.0.0\n");

        var issues = CheckPreserving(["Nested"], owner, RecordsStep("lasfile"));

        Assert.Contains(issues, i => i.Message.Contains(
            "record.data.Nested.Inner links the datasets of enrich.lasfile inside data.Nested, which target.protocolOptions.preserveDataKeys carries over whole from the record OSDU holds",
            StringComparison.Ordinal));
    }

    [Fact]
    public void A_link_filling_a_list_of_objects_is_refused_since_it_gives_references_alone()
    {
        var owner = Owner(data: "Curves: { $link: lasfile }", enrich: "lasfile:\n  rows: one\n  mapping: LasDataset@1.0.0\n");

        var message = Assert.Single(Mapping(owner)).Message;

        Assert.Contains("link.lasfile gives the references to the records an enrichment adds to the record, values, and", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_filling_a_single_value_or_a_list_of_values_passes_the_mappings_preflight()
    {
        Assert.Empty(Mapping(Owner(data: "Symbol: { $link: lasfile }", enrich: "lasfile:\n  rows: one\n  mapping: LasDataset@1.0.0\n")));
        Assert.Empty(Mapping(Owner(data: "Aliases: { $link: lasfile }", enrich: "lasfile:\n  rows: one\n  mapping: LasDataset@1.0.0\n")));
    }

    [Fact]
    public void A_base_read_of_what_the_mapping_links_passes_since_the_links_are_the_mappings()
    {
        var reader = Fragment("data:\n  Days: { $base: data.Aliases }", name: "Reader");
        var owner = Owner(data: "Aliases: [{ $link: lasfile }]", enrich: "lasfile:\n  rows: one\n  mapping: LasDataset@1.0.0\nreader:\n  mapping: Reader@1.0.0\n");

        Assert.Empty(Check(owner, RecordsStep("lasfile"), Step("reader", reader, readsRows: false)));
    }

    [Fact]
    public void The_enrichment_mappings_base_reads_count_as_its_enrichments_own()
    {
        var dataset = Fragment("data:\n  Description: { $base: data.Nested }", name: "LasDataset", kind: DatasetKind);

        var message = Assert.Single(Check(Owner(), RecordsStep("lasfile", mapping: dataset))).Message;

        Assert.Contains("data.Nested", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_flow_adding_records_gets_its_own_route_wrapped_and_one_adding_paths_alone_gets_it_as_it_was()
    {
        using var http = new HttpRuntime(new FlowReliability(), new SecretResolver([new EnvSecretProvider()]), TimeProvider.System, new FakeOsduPlatform(), allowLoopback: true);
        var loggers = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        var plain = Flow();
        var datasets = plain with { Enrich = [RecordsStep(files: LasFiles()).Step] };
        var stored = plain with { Enrich = [RecordsStep(kind: "osdu:wks:work-product-component--Document:1.0.0").Step] };
        var fragmentOnly = plain with { Enrich = [new EnrichStep { Name = "grade", Mapping = "Grade@1.0.0" }] };

        Assert.IsType<OsduRecordProtocol>(await ProtocolFactory.CreateAsync(plain, http, new SecretResolver([new EnvSecretProvider()]), loggers));
        Assert.IsType<OsduRecordProtocol>(await ProtocolFactory.CreateAsync(fragmentOnly, http, new SecretResolver([new EnvSecretProvider()]), loggers));
        var wrapped = Assert.IsType<EnrichPartsProtocol>(await ProtocolFactory.CreateAsync(datasets, http, new SecretResolver([new EnvSecretProvider()]), loggers));
        Assert.IsType<OsduRecordProtocol>(wrapped.Inner);
        Assert.IsType<OsduRecordProtocol>(Assert.IsType<EnrichPartsProtocol>(await ProtocolFactory.CreateAsync(stored, http, new SecretResolver([new EnvSecretProvider()]), loggers)).Inner);
    }
}
