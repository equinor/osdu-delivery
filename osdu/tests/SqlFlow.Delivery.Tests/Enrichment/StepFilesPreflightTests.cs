using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Validation;
using SqlFlow.Core.Secrets;
using Xunit;
using static SqlFlow.Delivery.Tests.EnrichmentSuite;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What the chain's preflight checks of a step registering files (osdu/docs/reference/flow/enrich.md, What the preflight
/// checks): the path the datasets are linked from is a list of values of the template, which every writer shares by adding items;
/// it may be a list the flow preserves, whose links an update merges, but not a path inside one; a step's <c>$base</c> read may
/// read what it links; its dataset fragment's reads count as the step's; and the route a flow with step files gets wraps its own.
/// </summary>
[Trait(EnrichmentSuite.Trait, EnrichmentSuite.Name)]
public sealed class StepFilesPreflightTests
{
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

    private static ResolvedStep FilesStep(string name, string link, MappingDefinition? fragment = null, MappingDefinition? datasetFragment = null, IReadOnlyList<string>? after = null)
    {
        var files = new EnrichFiles
        {
            Payload = new FlowPayload { Root = "../data/las", LocationColumn = "las_folder", HashColumn = "las_hash" },
            Link = link,
            Mapping = datasetFragment?.Reference,
        };
        return new ResolvedStep(
            new EnrichStep { Name = name, Mapping = fragment?.Reference, Files = files, ReadsRows = true, After = after ?? [] },
            fragment,
            TestSchema.Build(),
            fragment is null ? null : Renderer(fragment))
        {
            Files = new ResolvedFiles(files, "dataset--File.Generic", datasetFragment, datasetFragment is null ? null : Renderer(datasetFragment)),
        };
    }

    [Fact]
    public void Datasets_linked_from_a_list_of_values_pass_and_share_it_with_the_mapping_and_other_steps()
    {
        var grade = Fragment("data:\n  Aliases: [{ $from: alias }]", name: "Grade");

        Assert.Empty(Check(Owner("Aliases: [x]"), FilesStep("lasfile", "data.Aliases"), FilesStep("dlis", "data.Aliases"), Step("grade", grade)));
    }

    [Fact]
    public void Datasets_linked_from_a_list_the_flow_preserves_pass_since_an_update_merges_the_steps_links()
    {
        Assert.Empty(CheckPreserving(["Aliases"], Owner(), FilesStep("lasfile", "data.Aliases"), FilesStep("dlis", "data.Aliases")));
    }

    [Fact]
    public void A_fragment_adding_to_a_preserved_list_is_refused_though_the_steps_files_may_link_there()
    {
        var grade = Fragment("data:\n  Aliases: [{ $from: alias }]", name: "Grade");

        var message = Assert.Single(CheckPreserving(["Aliases"], Owner(), FilesStep("lasfile", "data.Aliases", fragment: grade))).Message;

        Assert.Contains("enrich.lasfile: fragment Grade@1.0.0 writes data.Aliases, and", message, StringComparison.Ordinal);
        Assert.Contains("Only a step's files may link datasets there (files.link), since an update merges those links with what OSDU holds", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_inside_a_preserved_object_is_refused_since_only_the_preserved_list_itself_merges()
    {
        var issues = CheckPreserving(["Nested"], Owner(), FilesStep("lasfile", "data.Nested.Inner"));

        Assert.Contains(issues, i => i.Message.Contains(
            "enrich.lasfile: files.link names data.Nested.Inner, inside data.Nested, which target.protocolOptions.preserveDataKeys carries over whole from the record OSDU holds",
            StringComparison.Ordinal));
    }

    [Fact]
    public void A_link_the_template_does_not_declare_is_refused_naming_the_default()
    {
        var message = Assert.Single(Check(Owner(), FilesStep("lasfile", "data.Missing"))).Message;

        Assert.Contains("enrich.lasfile: files.link names data.Missing, and template", message, StringComparison.Ordinal);
        Assert.Contains("such as data.Datasets", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("data.Curves", "list of objects")]
    [InlineData("data.Symbol", "a single value")]
    public void A_link_that_is_not_a_list_of_values_is_refused(string link, string shape)
    {
        var message = Assert.Single(Check(Owner(), FilesStep("lasfile", link))).Message;

        Assert.Contains($"files.link names {link}, which template", message, StringComparison.Ordinal);
        Assert.Contains(shape, message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_base_read_of_the_links_an_earlier_step_adds_passes_and_one_of_a_later_steps_is_refused()
    {
        var reader = Fragment("data:\n  Days: { $base: data.Aliases }", name: "Reader");

        Assert.Empty(Check(Owner(), FilesStep("lasfile", "data.Aliases"), Step("reader", reader, after: ["lasfile"])));
        Assert.NotEmpty(Check(Owner(), Step("reader", reader), FilesStep("lasfile", "data.Aliases")));
    }

    [Fact]
    public void The_dataset_fragments_base_reads_count_as_the_steps_own()
    {
        var dataset = Fragment("data:\n  Description: { $base: data.Nested }", name: "LasDataset");

        var message = Assert.Single(Check(Owner(), FilesStep("lasfile", "data.Aliases", datasetFragment: dataset))).Message;

        Assert.Contains("data.Nested", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_flow_whose_steps_register_files_gets_its_own_route_wrapped_and_one_without_gets_it_as_it_was()
    {
        using var http = new HttpRuntime(new FlowReliability(), new SecretResolver([new EnvSecretProvider()]), TimeProvider.System, new FakeOsduPlatform(), allowLoopback: true);
        var loggers = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        var plain = Flow();
        var files = plain with
        {
            Enrich = [new EnrichStep { Name = "lasfile", ReadsRows = true, Files = new EnrichFiles { Payload = new FlowPayload { Root = "r", LocationColumn = "c", HashColumn = "h" } } }],
        };
        var fragmentOnly = plain with { Enrich = [new EnrichStep { Name = "grade", Mapping = "Grade@1.0.0" }] };

        Assert.IsType<OsduRecordProtocol>(await ProtocolFactory.CreateAsync(plain, http, new SecretResolver([new EnvSecretProvider()]), loggers));
        Assert.IsType<OsduRecordProtocol>(await ProtocolFactory.CreateAsync(fragmentOnly, http, new SecretResolver([new EnvSecretProvider()]), loggers));
        var wrapped = Assert.IsType<EnrichPartsProtocol>(await ProtocolFactory.CreateAsync(files, http, new SecretResolver([new EnvSecretProvider()]), loggers));
        Assert.IsType<OsduRecordProtocol>(wrapped.Inner);
    }
}
