using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Cli;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// <c>sqlflow template</c> on a host with no module database, as a developer runs it in a working copy: <c>folders</c> reads
/// a repository's template folders from disk, and <c>import --out</c> writes a template of one's own into one
/// (osdu/docs/reference/cli/template.md).
/// </summary>
public sealed class TemplateFolderCommandTests : IDisposable
{
    private const string Thing = TestSchema.Kind;
    private readonly string _root = Samples.NewTempDirectory();

    public TemplateFolderCommandTests()
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

    [Fact]
    public async Task Folders_lists_every_template_folder_of_a_working_copy_with_what_each_file_holds()
    {
        var a = TemplateFolderFixtures.ThingFile("a");
        Write("templates/thing.json", a);
        Write("p/templates/broken.json", "{");
        Write("q/Templates/x.json", a);

        var (exit, output, notes) = await RunAsync("folders", _root);
        Assert.Equal(0, exit);
        var lines = output.ReplaceLineEndings("\n");
        Assert.StartsWith("p/templates\n  p/templates/broken.json  holds no template: the schema is not valid JSON", lines, StringComparison.Ordinal);
        Assert.Contains($"\ntemplates\n  templates/thing.json  {Thing}  {TemplateFolderFixtures.VersionOf(Thing, a)}\n", lines, StringComparison.Ordinal);
        Assert.Contains("WARN   q/Templates is named 'Templates', and a template folder is named 'templates' exactly", notes, StringComparison.Ordinal);

        (exit, output, _) = await RunAsync("folders", _root, "--json");
        Assert.Equal(0, exit);
        var json = JsonNode.Parse(output)!;
        Assert.Equal(["p/templates", "templates"], json["folders"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()));
        Assert.Single(json["warnings"]!.AsArray());
    }

    [Fact]
    public async Task Folders_for_a_document_lists_the_folders_it_reads_nearest_first_and_what_each_kind_settles_to()
    {
        var near = TemplateFolderFixtures.ThingFile("near");
        Write("p/templates/thing.json", near);
        Write("templates/a.json", TemplateFolderFixtures.Schema("osdu:wks:master-data--Well:1.2.0", "A"));
        Write("templates/b.json", TemplateFolderFixtures.Schema("osdu:wks:master-data--Well:1.2.0", "B"));
        var document = Write("p/mappings/Thing@1.0.0.yaml", TemplateFolderFixtures.KindAloneMapping());

        var (exit, output, _) = await RunAsync("folders", "--for", document);
        Assert.Equal(0, exit);
        var lines = output.ReplaceLineEndings("\n");
        Assert.StartsWith("p/mappings/Thing@1.0.0.yaml reads 2 template folder(s), nearest first:\np/templates\n", lines, StringComparison.Ordinal);
        Assert.Contains($"A kind named without a version settles to:\n", lines, StringComparison.Ordinal);
        Assert.Contains("  osdu:wks:master-data--Well:1.2.0  not settled: templates, the nearest template folder on the path of p/mappings/Thing@1.0.0.yaml holding", lines, StringComparison.Ordinal);
        Assert.Contains($"  {Thing}  {TemplateFolderFixtures.VersionOf(Thing, near)}  p/templates/thing.json", lines, StringComparison.Ordinal);

        (exit, output, _) = await RunAsync("folders", "--for", document, "--json");
        Assert.Equal(0, exit);
        var kinds = JsonNode.Parse(output)!["kinds"]!.AsArray();
        Assert.Equal(2, kinds.Count);

        // A document with no template folder on its path is told where one is read.
        var lone = Write("elsewhere/doc.yaml", "documentType: mapping\n");
        Directory.Delete(Path.Combine(_root, "templates"), recursive: true);
        (_, output, _) = await RunAsync("folders", "--for", lone);
        Assert.StartsWith("elsewhere/doc.yaml reads no template folder: a document reads the folder named templates beside it and those above it", output, StringComparison.Ordinal);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("folders", "--for", Path.Combine(_root, "missing.yaml")));
        Assert.Contains("which is not a file", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_with_out_writes_a_template_of_ones_own_into_a_folder_naming_its_kind_without_a_database()
    {
        const string Custom = "acme:wks:master-data--Thing:1.0.0";
        var source = Write("drafts/thing.json", TemplateFolderFixtures.Schema(Custom, declareKind: false));
        var target = Path.Combine(_root, "templates", "acme_wks_master-data--Thing_1.0.0.json");

        var (exit, output, notes) = await RunAsync("import", source, "--kind", Custom, "--out", target);
        Assert.Equal(0, exit);
        var file = Assert.Single(TemplateFolders.Read(Path.Combine(_root, "templates"), path => path).Files);
        Assert.Equal(Custom, file.Kind);
        Assert.Equal($"wrote template {Custom} version {file.Version}; it is not saved in the library, since no module database is configured", output.Trim());
        Assert.Contains($"Wrote the bundled schema of template {Custom} version {file.Version} to {target}", notes, StringComparison.Ordinal);
        Assert.DoesNotContain("names no kind", notes, StringComparison.Ordinal);

        // Without --out, a host with no database has nowhere to save it, and says where templates are read instead.
        var refused = await Assert.ThrowsAsync<FlowValidationException>(() => RunAsync("import", source, "--kind", Custom));
        Assert.Contains("'sqlflow template folders' reads a repository's template folders without one", refused.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<FlowValidationException>(() => RunAsync("list"));
    }

    /// <summary>
    /// Runs <c>sqlflow template</c> as the command line would on a host with no module database; returns the exit code, what
    /// it printed, and what it noted beside that.
    /// </summary>
    private static async Task<(int Exit, string Output, string Notes)> RunAsync(string subcommand, params string[] options)
    {
        var declared = new DeliveryCliModule().Verbs.Single(v => v.Name == "template");
        // A host started without the module's database: no ledger, no saved templates, no cache.
        var stores = Samples.Stores();
        var engine = new EngineContext(
            new DeliveryDocumentLoader(),
            new MemoryIngestionTables(),
            new StoragePayloadFiles(stores),
            stores,
            new SecretResolver([new EnvSecretProvider()]),
            Ledger: null,
            TimeProvider.System,
            NullLoggerFactory.Instance,
            new DefaultProtocolFactory(),
            CompositeDeliveryListener.Empty);
        await using var services = new ServiceCollection().AddSingleton(engine).BuildServiceProvider();
        await using var output = new StringWriter();
        await using var notes = new StringWriter();
        var command = new ModuleCommand(
            services,
            new CliArguments(["template", subcommand, .. options], declared.ValueOptions),
            output,
            notes,
            reason => throw new InvalidOperationException($"'template {subcommand}' was refused as a usage error: {reason}"),
            CancellationToken.None);
        var exit = await DeliveryVerbs.TemplateAsync(command);
        return (exit, output.ToString(), notes.ToString());
    }
}
