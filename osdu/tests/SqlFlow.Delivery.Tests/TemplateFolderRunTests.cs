using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using Xunit;
using static SqlFlow.Delivery.Tests.TemplateFolderFixtures;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What reads a repository document's templates from the template folders on its path, as every host composes it: a render,
/// an assertion flow's check of its tests, and a dimension's blueprint (osdu/docs/reference/concepts/templates.md, Template
/// folders).
/// </summary>
public sealed class TemplateFolderRunTests : IDisposable
{
    private const string Thing = TestSchema.Kind;
    private readonly string _root = Samples.NewTempDirectory();

    public TemplateFolderRunTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static FlowDefinition Flow() => Samples.Targeting(new FlowTarget
    {
        Endpoint = "https://osdu.example.test",
        Protocol = DeliveryProtocol.Storage,
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [CacheScope.PartitionHeader] = "dev" },
    }) with
    {
        Render = new FlowRender
        {
            Mapping = "Thing@1.0.0",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { [RenderContext.DataPartitionParameter] = "dev" },
        },
    };

    private RenderResolver Resolver()
        => new(new MappingCatalog(Path.Combine(_root, "mappings"), new DeliveryDocumentLoader()), cache: null, FolderTemplates.Default, new SecretResolver([new EnvSecretProvider()]));

    [Fact]
    public async Task A_render_fills_the_template_the_folders_on_its_mappings_path_hold_and_follows_the_file()
    {
        var first = ThingFile("first");
        Write("templates/thing.json", first);
        Write("mappings/Thing@1.0.0.yaml", TemplateFolderFixtures.KindAloneMapping());

        var resolved = await Resolver().ResolveAsync(Flow());
        var version = TemplateFolderFixtures.VersionOf(Thing, first);
        Assert.Equal(version, resolved.Context.SchemaSnapshotVersion);
        Assert.Equal(version, resolved.Schema.Version);
        Assert.Equal("templates/thing.json", resolved.TemplateOrigin);
        Assert.False(resolved.Mapping.TemplateVersionNamed);

        // The file changes and the mapping does not: the next render fills the version the folder holds now, and its render
        // context moves with it, so each record's own hash decides whether it is sent again.
        var second = ThingFile("second");
        Write("templates/thing.json", second);
        var again = await Resolver().ResolveAsync(Flow());
        Assert.Equal(TemplateFolderFixtures.VersionOf(Thing, second), again.Context.SchemaSnapshotVersion);
        Assert.Equal(resolved.Context.MappingFingerprint, again.Context.MappingFingerprint);
        Assert.NotEqual(resolved.Context.Hash(), again.Context.Hash());
    }

    [Fact]
    public async Task A_render_whose_pinned_version_no_folder_on_its_path_holds_does_not_start_and_says_where_it_looked()
    {
        var held = ThingFile("held");
        Write("templates/thing.json", held);
        Write("mappings/Thing@1.0.0.yaml", TemplateFolderFixtures.KindAloneMapping("0123456789abcdef"));

        var refused = await Assert.ThrowsAsync<FlowValidationException>(() => Resolver().ResolveAsync(Flow()));
        Assert.Contains(
            $"mapping Thing@1.0.0 cannot render: no template folder on the path of mappings/Thing@1.0.0.yaml holds template {Thing} version 0123456789abcdef. "
            + $"On its path templates/thing.json holds version {TemplateFolderFixtures.VersionOf(Thing, held)}. It reads templates.",
            refused.Message,
            StringComparison.Ordinal);

        // A version no folder holds renders nothing, wherever else it might be kept: the folders decide.
        Write("mappings/Thing@1.0.0.yaml", TemplateFolderFixtures.KindAloneMapping(TestSchema.Build().Version));
        Assert.False((await FolderTemplates.Default.ResolveAsync(Path.Combine(_root, "mappings", "Thing@1.0.0.yaml"), Thing, TestSchema.Build().Version)).Found);
        await Assert.ThrowsAsync<FlowValidationException>(() => Resolver().ResolveAsync(Flow()));
    }

    [Fact]
    public async Task An_assertion_flows_tests_are_checked_against_the_templates_its_folders_hold()
    {
        var held = ThingFile("assertions");
        Write("templates/thing.json", held);
        var flowPath = Write("assertions/thing-assertions.yaml", "flowType: assertion\n");
        var tests = new DeliveryDocumentLoader().ParseAssertion($$"""
            flowType: assertion
            name: thing-assertions
            source:
              endpoint: https://osdu.example.test
              headers: { data-partition-id: dev }
            tests:
              - name: named
                kind: {{Thing}}
                assert:
                  - { field: data.Name, exists: true }
              - name: pinned
                kind: {{Thing}}
                template: 0123456789abcdef
                assert:
                  - { field: data.Name, exists: true }
            """, flowPath).Tests;

        var fits = await AssertionTemplates.FitAsync(tests, FolderTemplates.Default, flowPath, mappings: null, CancellationToken.None);
        Assert.Equal(TemplateFolderFixtures.VersionOf(Thing, held), fits["named"].Version);
        Assert.Empty(fits["named"].Problems);
        var problem = Assert.Single(fits["pinned"].Problems);
        Assert.StartsWith(
            $"The test's fields are checked against the template of {Thing}, and no template folder on the path of assertions/thing-assertions.yaml holds template {Thing} version 0123456789abcdef.",
            problem,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dimension_reads_the_one_version_of_each_kind_its_flows_folders_hold_and_says_which_kinds_it_cannot_settle()
    {
        const string Well = "osdu:wks:master-data--Well:1.2.0";
        var near = TemplateFolderFixtures.Schema(Well, "Near");
        Write("flows/templates/well.json", near);
        Write("templates/well.json", TemplateFolderFixtures.Schema(Well, "Far"));
        Write("templates/thing-a.json", ThingFile("a"));
        Write("templates/thing-b.json", ThingFile("b"));
        var document = Write("flows/dimensions.yaml", "flowType: dimension\n");

        var (found, problems) = await DimensionBlueprints.MatchingAsync("*:*:*:*", FolderTemplates.Default, document);
        Assert.Equal([(Well, TemplateFolderFixtures.VersionOf(Well, near))], found.Select(f => (f.Template.Kind, f.Template.Version)));
        var ambiguous = Assert.Single(problems);
        Assert.StartsWith($"templates, the nearest template folder on the path of flows/dimensions.yaml holding {Thing}, holds 2 versions of it", ambiguous, StringComparison.Ordinal);

        // A pattern matching one kind only reads that kind.
        var (wells, none) = await DimensionBlueprints.MatchingAsync("*:*:master-data--Well:*", FolderTemplates.Default, document);
        Assert.Single(wells);
        Assert.Empty(none);
    }
}
