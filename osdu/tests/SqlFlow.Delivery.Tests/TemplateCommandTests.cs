using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Cli.Hosting;
using SqlFlow.Delivery.Cli;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// <c>sqlflow template show --out</c> on SQL Server: a saved version written as a file, the one the Templates page downloads,
/// for a repository to keep beside the mappings that pin it. The file is the bundled schema in the order it was written, it
/// imports as the same version, the folders it needs are created, and a path that cannot be written is refused with the
/// reason, the version and the path.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class TemplateCommandTests : IDisposable
{
    private readonly OsduTestDatabase _catalog = new();
    private readonly string _folder = Samples.NewTempDirectory();

    public void Dispose()
    {
        _catalog.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task Show_writes_the_saved_version_as_a_file_that_imports_as_the_same_version()
    {
        var saved = Samples.SampleTemplate(Samples.WellboreKind);
        await _catalog.Templates().SaveAsync(saved, "file " + Samples.WellboreKind.Replace(':', '_') + ".json", "tests");
        var path = Path.Combine(_folder, "repo", "templates", "osdu_wks_master-data--Wellbore_1.3.0.json");

        var (exit, output, notes) = await RunAsync("show", "--kind", Samples.WellboreKind, "--version", saved.Version, "--out", path);

        Assert.Equal(0, exit);
        Assert.Equal(TemplateSources.FileText(saved), await File.ReadAllTextAsync(path));
        Assert.Equal(saved.Version, TemplateSources.FromBundledJson(await File.ReadAllTextAsync(path), Samples.WellboreKind, DateTimeOffset.UnixEpoch, path).Version);
        Assert.Contains($"Wrote the bundled schema of template {Samples.WellboreKind} version {saved.Version} to {path}", notes, StringComparison.Ordinal);

        // The note goes beside the output, so show still lays the version out on stdout, and --json stays one document.
        Assert.StartsWith($"{Samples.WellboreKind} version {saved.Version}:", output, StringComparison.Ordinal);
        (exit, output, _) = await RunAsync("show", "--kind", Samples.WellboreKind, "--version", saved.Version, "--out", path, "--json");
        Assert.Equal(0, exit);
        Assert.Equal(saved.Version, System.Text.Json.Nodes.JsonNode.Parse(output)?["version"]?.GetValue<string>());
    }

    [Fact]
    public async Task A_path_that_cannot_be_written_is_refused_with_the_version_and_the_reason()
    {
        var saved = Samples.SampleTemplate(Samples.WellboreKind);
        await _catalog.Templates().SaveAsync(saved, "tests", "tests");

        // A file stands where the folder would have to be, so no folder can be made there.
        var blocker = Path.Combine(_folder, "blocker");
        await File.WriteAllTextAsync(blocker, "not a folder");
        var path = Path.Combine(blocker, "Wellbore.json");

        var refused = await Assert.ThrowsAsync<DeliveryException>(
            () => RunAsync("show", "--kind", Samples.WellboreKind, "--version", saved.Version, "--out", path));
        Assert.StartsWith(
            $"The bundled schema of template {Samples.WellboreKind} version {saved.Version} could not be written to '{path}': ",
            refused.Message,
            StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// Runs <c>sqlflow template</c> as the command line would, with the options the module declares for the verb, over an
    /// engine whose templates are the test database's; returns the exit code, what it printed, and what it noted beside that.
    /// </summary>
    private async Task<(int Exit, string Output, string Notes)> RunAsync(string subcommand, params string[] options)
    {
        var declared = new DeliveryCliModule().Verbs.Single(v => v.Name == "template");
        await using var services = new ServiceCollection()
            .AddSingleton(Samples.Engine(ledger: null, templates: _catalog.Templates()))
            .BuildServiceProvider();
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
