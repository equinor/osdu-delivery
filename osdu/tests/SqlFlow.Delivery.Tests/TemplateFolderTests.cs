using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using Xunit;
using static SqlFlow.Delivery.Tests.TemplateFolderFixtures;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The rule a template a document names is settled by (osdu/docs/reference/concepts/templates.md, Template folders), over
/// folders as given: a version named is taken from the nearest folder on the path holding it, and a kind named alone takes
/// the one version the nearest folder holding the kind holds.
/// </summary>
public sealed class TemplateChainTests
{
    private const string WellLog = "osdu:wks:work-product-component--WellLog:1.4.0";
    private const string V1 = "1111111111111111";
    private const string V2 = "2222222222222222";
    private const string V3 = "3333333333333333";

    private static TemplateFolderFile File(string path, string kind, string version) => new(path, path, kind, version, null);

    private static TemplateFolderFile Broken(string path, string problem) => new(path, path, null, null, problem);

    private static TemplateFolder Folder(string path, params TemplateFolderFile[] files) => new(path, path, files);

    private static TemplateChain Chain(params TemplateFolder[] folders)
        => new("p/m/doc.yaml", ["p/m/templates", "p/templates", "templates"], folders);

    [Fact]
    public void A_version_named_is_taken_from_the_nearest_folder_holding_it()
    {
        var near = Folder("p/m/templates", File("p/m/templates/a.json", WellLog, V1));
        var far = Folder("templates", File("templates/a.json", WellLog, V1));
        var pick = Chain(near, far).Pick(WellLog, V1);
        Assert.True(pick.Found);
        Assert.Same(near, pick.Folder);
        Assert.Equal("p/m/templates/a.json", pick.File!.Shown);
        Assert.Equal(new TemplateReference(WellLog, V1), pick.File.Reference);
    }

    [Fact]
    public void A_version_named_is_found_further_up_when_the_nearer_folder_holds_another_version_of_the_kind()
    {
        var pick = Chain(
                Folder("p/m/templates", File("p/m/templates/a.json", WellLog, V1)),
                Folder("templates", File("templates/b.json", WellLog, V2)))
            .Pick(WellLog, V2);
        Assert.Equal("templates/b.json", pick.File!.Shown);
    }

    [Fact]
    public void A_kind_named_alone_takes_the_one_version_the_nearest_folder_holding_it_holds_whatever_folders_further_up_hold()
    {
        var chain = Chain(
            Folder("p/m/templates", File("p/m/templates/other.json", "osdu:wks:master-data--Well:1.2.0", V3)),
            Folder("p/templates", File("p/templates/a.json", WellLog, V1)),
            Folder("templates", File("templates/b.json", WellLog, V2)));
        var pick = chain.Pick(WellLog, null);
        Assert.Equal((V1, "p/templates/a.json"), (pick.File!.Version, pick.File.Shown));
        Assert.Equal("p/templates", pick.Folder!.Shown);

        // A blank version is no version.
        Assert.Equal(V1, chain.Pick(WellLog, "  ").File!.Version);
    }

    [Fact]
    public void Two_files_of_one_version_in_a_folder_are_one_version_and_the_first_in_path_order_is_taken()
    {
        var pick = Chain(Folder("templates", File("templates/a.json", WellLog, V1), File("templates/copy/a.json", WellLog, V1))).Pick(WellLog, null);
        Assert.Equal("templates/a.json", pick.File!.Shown);
    }

    [Fact]
    public void A_kind_named_alone_is_not_settled_when_its_nearest_folder_holds_two_versions_and_the_message_names_both()
    {
        var pick = Chain(
                Folder("p/templates", File("p/templates/a.json", WellLog, V1), File("p/templates/b.json", WellLog, V2)),
                Folder("templates", File("templates/c.json", WellLog, V3)))
            .Pick(WellLog, null);
        Assert.False(pick.Found);
        Assert.Equal(
            $"p/templates, the nearest template folder on the path of p/m/doc.yaml holding {WellLog}, holds 2 versions of it: "
            + $"{V1} (p/templates/a.json), {V2} (p/templates/b.json). Name the version to use, or keep one version of the kind in the folder.",
            pick.Problem);

        // A version named settles it, from the same folder or one further up.
        Assert.Equal("p/templates/b.json", Chain(
            Folder("p/templates", File("p/templates/a.json", WellLog, V1), File("p/templates/b.json", WellLog, V2))).Pick(WellLog, V2).File!.Shown);
    }

    [Fact]
    public void A_version_no_folder_holds_is_said_with_the_versions_on_the_path_the_folders_read_the_unreadable_files_and_where_else_the_repository_holds_it()
    {
        var chain = new TemplateChain(
            "p/m/doc.yaml",
            ["p/m/templates", "p/templates", "templates"],
            [Folder("p/templates", File("p/templates/a.json", WellLog, V1), Broken("p/templates/broken.json", "the schema is not valid JSON"))],
            () => [File("other/templates/x.json", WellLog, V2), File("p/templates/a.json", WellLog, V1)]);
        var pick = chain.Pick(WellLog, V2);
        Assert.False(pick.Found);
        Assert.Equal(
            $"no template folder on the path of p/m/doc.yaml holds template {WellLog} version {V2}. "
            + $"On its path p/templates/a.json holds version {V1}. It reads p/templates. "
            + "A file on its path could not be read as a template: p/templates/broken.json (the schema is not valid JSON). "
            + $"The repository holds version {V2} of {WellLog} at other/templates/x.json, which is not on its path. "
            + $"Put the template's file in one of them: the Templates page downloads a saved version, and 'sqlflow template show --kind {WellLog} --version {V2} "
            + "--out p/templates/osdu_wks_work-product-component--WellLog_1.4.0.json' writes it from the saved templates.",
            pick.Problem);
    }

    [Fact]
    public void A_kind_no_folder_holds_is_said_with_how_to_capture_it_into_the_nearest_folder()
    {
        var pick = Chain(Folder("p/templates", File("p/templates/a.json", "osdu:wks:master-data--Well:1.2.0", V1))).Pick(WellLog, null);
        Assert.Equal(
            $"no template folder on the path of p/m/doc.yaml holds a template of {WellLog}. It reads p/templates. "
            + $"Put the template's file in one of them: the Templates page downloads a saved version, and 'sqlflow template capture --kind {WellLog} "
            + "--out p/templates/osdu_wks_work-product-component--WellLog_1.4.0.json' writes the newest release's.",
            pick.Problem);
    }

    [Fact]
    public void With_no_template_folder_on_the_path_the_message_names_where_one_is_read()
    {
        var pick = Chain().Pick(WellLog, V1);
        Assert.StartsWith(
            $"no template folder on the path of p/m/doc.yaml holds template {WellLog} version {V1}. A document reads the folder named templates beside it "
            + "and those above it, up to the top of its repository, and there is none: p/m/templates, p/templates, templates. Put the template's file in one of them",
            pick.Problem,
            StringComparison.Ordinal);
        Assert.Contains("--out p/m/templates/osdu_wks_", pick.Problem, StringComparison.Ordinal);

        var nowhere = new TemplateChain("doc.yaml", [], []).Pick(WellLog, null);
        Assert.Contains("up to the top of its repository, and there is none. Put the template's file", nowhere.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Kinds_and_versions_are_compared_without_regard_to_case()
    {
        var chain = Chain(Folder("templates", File("templates/a.json", WellLog, "abcdef0123456789")));
        Assert.True(chain.Pick(WellLog.ToUpperInvariant(), "ABCDEF0123456789").Found);
        Assert.True(chain.Pick(WellLog.ToUpperInvariant(), null).Found);
    }

    [Fact]
    public void Elsewhere_is_asked_only_when_nothing_on_the_path_holds_what_was_named()
    {
        var asked = 0;
        var chain = new TemplateChain(
            "doc.yaml", ["templates"], [Folder("templates", File("templates/a.json", WellLog, V1))], () =>
            {
                asked++;
                return [];
            });
        Assert.True(chain.Pick(WellLog, V1).Found);
        Assert.Equal(0, asked);
        Assert.False(chain.Pick(WellLog, V2).Found);
        Assert.False(chain.Pick(WellLog, V3).Found);
        Assert.Equal(1, asked);
    }

    [Fact]
    public void A_message_lists_five_items_and_counts_the_rest()
    {
        Assert.Equal("a, b, c, d, e and 2 more", TemplateChain.Listed(["a", "b", "c", "d", "e", "f", "g"]));
        Assert.Equal("a, b", TemplateChain.Listed(["a", "b"]));
        Assert.Equal(string.Empty, TemplateChain.Listed([]));
    }

    [Fact]
    public void A_file_that_holds_no_template_has_no_reference()
    {
        var broken = Broken("templates/x.json", "the schema names no kind");
        Assert.False(broken.IsTemplate);
        Assert.False(broken.Holds(WellLog));
        Assert.Equal("templates/x.json holds no template: the schema names no kind", Assert.Throws<InvalidOperationException>(() => broken.Reference).Message);
    }
}

/// <summary>
/// The template folders of a repository on disk: which files are read and what each holds, which folders a scan and a
/// document's walk read, and what a template that cannot be settled says (osdu/docs/reference/concepts/templates.md,
/// Template folders).
/// </summary>
public sealed class TemplateFoldersTests : IDisposable
{
    private const string Thing = TestSchema.Kind;
    private readonly string _root = Samples.NewTempDirectory();

    public TemplateFoldersTests()
    {
        // The repository's top is told by its .git folder, as a clone has one.
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string At(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private string Write(string relative, string text)
    {
        var path = At(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    private string Document(string relative) => Write(relative, "documentType: mapping\n");

    [Fact]
    public void A_template_file_holds_the_kind_its_schema_declares_whatever_its_name()
    {
        var json = Schema(Thing);
        Write("templates/anything-at-all.json", json);
        var folder = TemplateFolders.Read(At("templates"), path => TemplateFolders.Relative(_root, path));
        var file = Assert.Single(folder.Files);
        Assert.Equal(("templates/anything-at-all.json", Thing, VersionOf(Thing, json), (string?)null), (file.Shown, file.Kind, file.Version, file.Problem));
        Assert.Equal("templates", folder.Shown);
    }

    [Fact]
    public void Every_json_file_under_the_folder_is_read_and_other_files_are_not()
    {
        Write("templates/a.json", Schema(Thing));
        Write("templates/osdu/b.JSON", Schema("osdu:wks:master-data--Well:1.2.0"));
        Write("templates/README.md", "# templates");
        Write("templates/notes.txt", "notes");
        var files = TemplateFolders.Read(At("templates"), path => TemplateFolders.Relative(_root, path)).Files;
        Assert.Equal(["templates/a.json", "templates/osdu/b.JSON"], files.Select(f => f.Shown));
        Assert.All(files, f => Assert.True(f.IsTemplate));
    }

    [Fact]
    public void A_folder_named_templates_inside_a_template_folder_is_a_folder_of_its_own_and_skipped_folders_are_not_entered()
    {
        Write("templates/a.json", Schema(Thing));
        Write("templates/inner/templates/b.json", Schema(Thing, "Other"));
        Write("templates/.hidden/c.json", Schema(Thing, "Hidden"));
        Write("templates/bin/d.json", Schema(Thing, "Bin"));
        Write("templates/node_modules/e.json", Schema(Thing, "Modules"));
        var outer = TemplateFolders.Read(At("templates"), path => TemplateFolders.Relative(_root, path));
        Assert.Equal(["templates/a.json"], outer.Files.Select(f => f.Shown));

        var scan = TemplateFolders.Scan(_root, CancellationToken.None);
        Assert.Equal(["templates", "templates/inner/templates"], scan.Folders.Select(f => f.Shown));
        Assert.Equal(["templates/inner/templates/b.json"], scan.Folders[1].Files.Select(f => f.Shown));
    }

    [Theory]
    [InlineData("{ not json", "the schema is not valid JSON")]
    [InlineData("", "the schema is not valid JSON")]
    [InlineData("[1, 2]", "the schema is not a JSON object")]
    [InlineData("""{ "type": "object", "properties": { "data": { "type": "object" } } }""", "the schema names no kind. A template file declares the kind it holds in x-osdu-schema-source")]
    [InlineData("""{ "x-osdu-schema-source": "not a kind", "properties": { "data": {} } }""", "x-osdu-schema-source 'not a kind' is not a record kind")]
    [InlineData("""{ "x-osdu-schema-source": "acme:wks:Thing:1.0.0", "properties": { "data": { "$ref": "../abstract/AbstractCommon.1.0.0.json" } } }""", "refers to '../abstract/AbstractCommon.1.0.0.json' outside itself")]
    [InlineData("""{ "x-osdu-schema-source": "acme:wks:Thing:1.0.0", "properties": { "data": { "$ref": "#/definitions/Missing" } } }""", "refers to definition 'Missing', which it does not contain")]
    [InlineData("""{ "x-osdu-schema-source": "acme:wks:Thing:1.0.0", "properties": { "kind": {} } }""", "declares no 'data' property")]
    public void A_file_that_holds_no_template_says_why_without_its_name_in_front(string text, string problem)
    {
        Write("templates/x.json", text);
        var file = Assert.Single(TemplateFolders.Read(At("templates"), path => TemplateFolders.Relative(_root, path)).Files);
        Assert.False(file.IsTemplate);
        Assert.Contains(problem, file.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("templates/x.json:", file.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_larger_than_a_template_holds_is_not_read()
    {
        Write("templates/big.json", new string(' ', TemplateFolders.MaxFileBytes + 1));
        var file = Assert.Single(TemplateFolders.Read(At("templates"), path => path).Files);
        Assert.Equal($"the file is {TemplateFolders.MaxFileBytes + 1} bytes, and a template file holds one bundled schema of at most {TemplateFolders.MaxFileBytes} bytes", file.Problem);
    }

    [Fact]
    public void A_byte_order_mark_line_endings_and_indentation_do_not_move_the_version()
    {
        var json = Schema(Thing);
        var indented = DocumentJson.Indented(JsonNode.Parse(json)!.AsObject()).ReplaceLineEndings("\r\n");
        Write("templates/plain.json", json);
        System.IO.File.WriteAllText(At("templates/bom.json"), indented, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var files = TemplateFolders.Read(At("templates"), path => path).Files;
        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.Equal(VersionOf(Thing, json), f.Version));
    }

    [Fact]
    public void A_folder_with_more_files_than_are_read_says_so()
    {
        for (var i = 0; i <= TemplateFolders.MaxFilesPerFolder; i++)
        {
            Write($"templates/f{i:D5}.json", "{}");
        }

        var files = TemplateFolders.Read(At("templates"), path => TemplateFolders.Relative(_root, path)).Files;
        Assert.Equal(TemplateFolders.MaxFilesPerFolder + 1, files.Count);
        Assert.Equal(
            $"the folder holds {TemplateFolders.MaxFilesPerFolder + 1} .json files, and only the first {TemplateFolders.MaxFilesPerFolder} in path order are read as templates",
            files[^1].Problem);
        Assert.Equal("templates", files[^1].Shown);
    }

    [Fact]
    public void The_scan_finds_every_template_folder_in_path_order_and_warns_of_one_named_otherwise()
    {
        Write("b/templates/x.json", Schema(Thing));
        Write("a/deep/er/templates/y.json", Schema(Thing, "Y"));
        Write("templates/z.json", Schema(Thing, "Z"));
        Write("c/Templates/w.json", Schema(Thing, "W"));
        Write("d/template/v.json", Schema(Thing, "V"));
        Write("e/template/notes.md", "A folder of that name kept for something else.");
        Write(".git/templates/ignored.json", Schema(Thing, "Ignored"));
        Write("obj/templates/ignored.json", Schema(Thing, "Ignored"));
        var scan = TemplateFolders.Scan(_root, CancellationToken.None);
        Assert.Equal(["a/deep/er/templates", "b/templates", "templates"], scan.Folders.Select(f => f.Shown));

        // The plural in another case, and the singular holding schema files, are warned of; a singular holding none is not.
        Assert.Equal(
            [
                "c/Templates is named 'Templates', and a template folder is named 'templates' exactly, so no document reads the templates in it.",
                "d/template is named 'template', and a template folder is named 'templates' exactly, so no document reads the templates in it.",
            ],
            scan.Warnings.Order(StringComparer.Ordinal));
        Assert.Equal(3, scan.Files.Count());

        // The folder named otherwise is not read for a document beside it either: the one above it is.
        Assert.Equal(["templates"], TemplateFolders.ChainFor(Document("c/m.yaml")).Folders.Select(f => f.Shown));
    }

    [Fact]
    public void A_scan_of_a_folder_that_does_not_exist_is_refused()
    {
        var missing = Path.Combine(_root, "nowhere");
        Assert.Equal(
            $"The repository folder '{missing}' does not exist, so its template folders cannot be read.",
            Assert.Throws<DeliveryException>(() => TemplateFolders.Scan(missing, CancellationToken.None)).Message);
    }

    [Fact]
    public void A_link_is_neither_followed_nor_read_and_is_warned_of()
    {
        Write("shared/real/x.json", Schema(Thing));
        Directory.CreateDirectory(At("p"));
        Link(At("p/templates"), At("shared/real"));
        Link(At("q"), At("shared"));
        try
        {
            var scan = TemplateFolders.Scan(_root, CancellationToken.None);
            Assert.Empty(scan.Folders);
            Assert.Equal(["p/templates is a link, and template folders are read only where they are, so no document reads the templates behind it."], scan.Warnings);

            // A document beside the link reads no folder through it, as the scan reads none.
            Assert.Empty(TemplateFolders.ChainFor(Document("p/m.yaml")).Folders);
        }
        finally
        {
            // A link is removed itself, never what it points to.
            Directory.Delete(At("p/templates"));
            Directory.Delete(At("q"));
        }
    }

    /// <summary>A link to a folder: a junction on Windows, which needs no privilege, and a symbolic link elsewhere.</summary>
    private static void Link(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    [Fact]
    public void The_folders_a_document_reads_are_beside_it_and_above_it_nearest_first_up_to_the_repository_top()
    {
        Write("templates/top.json", Schema(Thing));
        Write("p/templates/mid.json", Schema(Thing, "Mid"));
        Write("p/m/templates/near.json", Schema(Thing, "Near"));
        Write("p/m/other/templates/sibling.json", Schema(Thing, "Sibling"));
        var chain = TemplateFolders.ChainFor(Document("p/m/doc.yaml"));
        Assert.Equal("p/m/doc.yaml", chain.Document);
        Assert.Equal(["p/m/templates", "p/templates", "templates"], chain.Looked);
        Assert.Equal(["p/m/templates", "p/templates", "templates"], chain.Folders.Select(f => f.Shown));
        Assert.Equal(["p/m/templates/near.json", "p/templates/mid.json", "templates/top.json"], chain.Templates.Select(f => f.Shown));

        // Nothing above the top is read: a folder named templates beside the repository is another repository's.
        Assert.DoesNotContain(chain.Looked, l => l.StartsWith("..", StringComparison.Ordinal));
    }

    [Fact]
    public void A_worktree_top_is_found_by_its_git_file()
    {
        Write("wt/.git", "gitdir: elsewhere");
        Write("wt/templates/a.json", Schema(Thing));
        Write("templates/outer.json", Schema(Thing, "Outer"));
        var chain = TemplateFolders.ChainFor(Document("wt/m/doc.yaml"));
        Assert.Equal(["m/templates", "templates"], chain.Looked);
        Assert.Equal(["templates/a.json"], chain.Templates.Select(f => f.Shown));
        Assert.Equal(At("wt"), TemplateFolders.RepositoryTop(At("wt/m")));
    }

    [Fact]
    public void A_root_given_bounds_the_walk_and_a_document_outside_it_reads_no_folder()
    {
        Write("templates/outer.json", Schema(Thing));
        Write("repo/templates/inner.json", Schema(Thing, "Inner"));
        var chain = TemplateFolders.ChainFor(Document("repo/m/doc.yaml"), At("repo"));
        Assert.Equal(["m/templates", "templates"], chain.Looked);
        Assert.Equal(["templates/inner.json"], chain.Templates.Select(f => f.Shown));

        var outside = TemplateFolders.ChainFor(Document("elsewhere/doc.yaml"), At("repo"));
        Assert.Empty(outside.Looked);
        Assert.Empty(outside.Folders);
    }

    [Fact]
    public void Without_a_repository_top_the_walk_stops_after_sixteen_folders()
    {
        var bare = Samples.NewTempDirectory();
        try
        {
            System.IO.File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(bare, "templates")).FullName, "a.json"), Schema(Thing));
            var deep = Path.Combine([bare, .. Enumerable.Range(0, TemplateFolders.MaxAscent).Select(i => $"d{i}")]);
            Directory.CreateDirectory(deep);
            var document = Path.Combine(deep, "doc.yaml");
            System.IO.File.WriteAllText(document, "documentType: mapping\n");

            var chain = TemplateFolders.ChainFor(document);
            Assert.Equal(TemplateFolders.MaxAscent, chain.Looked.Count);
            Assert.Empty(chain.Folders);

            // One folder nearer, and the top folder is the last one looked at.
            var nearer = Path.Combine(Path.GetDirectoryName(deep)!, "doc.yaml");
            System.IO.File.WriteAllText(nearer, "documentType: mapping\n");
            Assert.Single(TemplateFolders.ChainFor(nearer).Folders);
        }
        finally
        {
            Directory.Delete(bare, recursive: true);
        }
    }

    [Fact]
    public void A_file_changed_since_its_folder_was_read_is_not_taken_for_the_version_it_held()
    {
        var json = Schema(Thing);
        Write("templates/a.json", json);
        var file = Assert.Single(TemplateFolders.Read(At("templates"), path => TemplateFolders.Relative(_root, path)).Files);
        Assert.Equal(VersionOf(Thing, json), TemplateFolders.Load(file).Version);

        var changed = Schema(Thing, "Changed");
        Write("templates/a.json", changed);
        Assert.Equal(
            $"templates/a.json held template {Thing} version {VersionOf(Thing, json)} and now holds {Thing} version {VersionOf(Thing, changed)}; read it again.",
            Assert.Throws<DeliveryException>(() => TemplateFolders.Load(file)).Message);

        Write("templates/a.json", "{ broken");
        Assert.StartsWith(
            $"templates/a.json held template {Thing} version {VersionOf(Thing, json)} and no longer reads: the schema is not valid JSON",
            Assert.Throws<DeliveryException>(() => TemplateFolders.Load(file)).Message,
            StringComparison.Ordinal);

        System.IO.File.Delete(At("templates/a.json"));
        Assert.EndsWith("no longer reads: the file no longer exists", Assert.Throws<DeliveryException>(() => TemplateFolders.Load(file)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_rewritten_at_once_with_other_content_of_the_same_length_is_read_again()
    {
        var first = Schema(Thing, "Aaaa");
        var second = Schema(Thing, "Bbbb");
        Assert.Equal(first.Length, second.Length);
        var path = Write("templates/a.json", first);
        var written = System.IO.File.GetLastWriteTimeUtc(path);
        Assert.Equal(VersionOf(Thing, first), TemplateFolders.Read(At("templates"), p => p).Files[0].Version);

        // Same length and the same time of writing: only the content tells them apart.
        System.IO.File.WriteAllText(path, second);
        System.IO.File.SetLastWriteTimeUtc(path, written);
        Assert.Equal(VersionOf(Thing, second), TemplateFolders.Read(At("templates"), p => p).Files[0].Version);
    }

    [Fact]
    public async Task A_folder_source_settles_a_documents_templates_from_its_path()
    {
        var near = Schema(Thing, "Near");
        var far = Schema(Thing, "Far");
        Write("templates/thing.json", far);
        Write("p/templates/thing.json", near);
        var document = Document("p/m/doc.yaml");
        var source = new FolderTemplates();

        var alone = await source.ResolveAsync(document, Thing, null);
        Assert.True(alone.Found);
        Assert.Equal((VersionOf(Thing, near), "p/templates/thing.json", FolderTemplates.FolderCapturedBy), (alone.Template!.Version, alone.Template.Origin, alone.Template.CapturedBy));
        Assert.Equal(VersionOf(Thing, near), alone.Schema!.Version);

        Assert.Equal("templates/thing.json", (await source.ResolveAsync(document, Thing, VersionOf(Thing, far))).Template!.Origin);

        var missing = await source.ResolveAsync(document, Thing, "0123456789abcdef");
        Assert.False(missing.Found);
        Assert.StartsWith($"no template folder on the path of p/m/doc.yaml holds template {Thing} version 0123456789abcdef.", missing.Problem, StringComparison.Ordinal);

        var listed = await source.ListForAsync(document);
        Assert.Equal([VersionOf(Thing, near), VersionOf(Thing, far)], listed.Select(t => t.Version));

        Assert.Equal(
            $"template {Thing} is read from the template folders on a document's path, and this document was not read from a file.",
            (await source.ResolveAsync(null, Thing, null)).Problem);
        Assert.Empty(await source.ListForAsync(null));
    }

    [Fact]
    public void Pins_for_a_document_check_its_versions_and_pins_for_kinds_alone_take_a_version_as_written()
    {
        var json = Schema(Thing);
        Write("templates/thing.json", json);
        var document = Document("m/doc.yaml");
        var source = new FolderTemplates();

        Assert.Equal(new TemplateReference(Thing, VersionOf(Thing, json)), source.For(document).Pin(Thing, null, "m/doc.yaml: template"));
        Assert.Equal(new TemplateReference(Thing, VersionOf(Thing, json)), source.For(document).Pin(Thing, VersionOf(Thing, json), "where"));
        var refused = Assert.Throws<FlowValidationException>(() => source.For(document).Pin(Thing, "0123456789abcdef", "m/doc.yaml: template"));
        Assert.StartsWith($"m/doc.yaml: template: no template folder on the path of m/doc.yaml holds template {Thing} version 0123456789abcdef.", refused.Message, StringComparison.Ordinal);

        Assert.Equal(new TemplateReference(Thing, "0123456789abcdef"), source.ForKindsAlone(document).Pin(Thing, "0123456789abcdef", "where"));
        Assert.Equal(VersionOf(Thing, json), source.ForKindsAlone(document).Pin(Thing, null, "where").Version);
        Assert.Equal(new TemplateReference(Thing, string.Empty), TemplatePins.DocumentAlone.Pin(Thing, null, "where"));
    }

    [Fact]
    public void A_mapping_that_names_its_templates_kind_alone_fills_the_version_its_nearest_folder_holds()
    {
        var json = Schema(Thing);
        Write("templates/thing.json", json);
        var path = Write("mappings/Thing@1.0.0.yaml", KindAloneMapping());

        var mapping = new DeliveryDocumentLoader().LoadMapping(path);
        Assert.Equal(new TemplateReference(Thing, VersionOf(Thing, json)), mapping.Template);
        Assert.False(mapping.TemplateVersionNamed);
        Assert.Equal(Path.GetFullPath(path), mapping.Location);
        Assert.Equal(mapping.Location, mapping.TemplateDocument);

        // The folder's file changes: the mapping fills the version it now holds, the document unchanged.
        var changed = Schema(Thing, "Changed");
        Write("templates/thing.json", changed);
        var again = new DeliveryDocumentLoader().LoadMapping(path);
        Assert.Equal(VersionOf(Thing, changed), again.Template.Version);
        Assert.Equal(mapping.Fingerprint, again.Fingerprint);

        // A version named is taken as written when the mapping is loaded, and checked when its template is read.
        var named = new DeliveryDocumentLoader().LoadMapping(Write("mappings/Thing@1.0.0.yaml", KindAloneMapping("0123456789abcdef")));
        Assert.Equal(new TemplateReference(Thing, "0123456789abcdef"), named.Template);
        Assert.True(named.TemplateVersionNamed);
    }

    [Fact]
    public void A_mapping_whose_templates_kind_alone_no_folder_settles_does_not_load_and_says_where_it_looked()
    {
        var path = Write("mappings/Thing@1.0.0.yaml", KindAloneMapping());
        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().LoadMapping(path, "mappings/Thing@1.0.0.yaml"));
        Assert.StartsWith(
            $"mappings/Thing@1.0.0.yaml: template: no template folder on the path of mappings/Thing@1.0.0.yaml holds a template of {Thing}.",
            refused.Message,
            StringComparison.Ordinal);

        // Two versions of the kind in the folder the mapping reads: it has to name one.
        Write("templates/a.json", Schema(Thing, "A"));
        Write("templates/b.json", Schema(Thing, "B"));
        var ambiguous = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().LoadMapping(path, "mappings/Thing@1.0.0.yaml"));
        Assert.Contains("templates, the nearest template folder on the path of mappings/Thing@1.0.0.yaml holding", ambiguous.Message, StringComparison.Ordinal);
        Assert.Contains("holds 2 versions of it", ambiguous.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mapping_read_where_no_template_folder_is_known_has_to_name_its_versions()
    {
        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(KindAloneMapping(), "thing.yaml"));
        Assert.Equal(
            $"thing.yaml: template names {Thing} without a version, which is settled from the template folders on the mapping's path, and the mapping was read "
            + "where no template folder is known. Name the version (version: <16 hexadecimal characters>), or read the mapping in its repository.",
            refused.Message);

        // Read on its own, as a check of the document, it loads with the kind it names.
        var alone = new DeliveryDocumentLoader().ParseMapping(KindAloneMapping(), "thing.yaml", TemplatePins.DocumentAlone);
        Assert.Equal(new TemplateReference(Thing, string.Empty), alone.Template);

        var badVersion = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(KindAloneMapping("not-a-version"), "thing.yaml"));
        Assert.Contains("leave it out to fill the one version of the kind the nearest template folder holds", badVersion.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_search_that_names_its_schemas_kind_alone_reads_the_version_the_folder_holds()
    {
        const string Wellbore = "osdu:wks:master-data--Wellbore:1.3.0";
        var wellbore = Schema(Wellbore);
        Write("templates/thing.json", Schema(Thing));
        Write("templates/wellbore.json", wellbore);
        var document = KindAloneMapping().Replace(
            "dataset:\n",
            "searches:\n  Wellbore:\n    kind: osdu:wks:master-data--Wellbore:*\n    schema: { kind: osdu:wks:master-data--Wellbore:1.3.0 }\ndataset:\n",
            StringComparison.Ordinal).Replace(
            "  data:\n",
            "  data:\n    WellboreID:\n      $search: Wellbore\n      $findBy: data.Name = name\n",
            StringComparison.Ordinal);
        var path = Write("mappings/Thing@1.0.0.yaml", document);
        var mapping = new DeliveryDocumentLoader().LoadMapping(path);
        Assert.Equal(new TemplateReference(Wellbore, VersionOf(Wellbore, wellbore)), mapping.Searches["Wellbore"].Schema);
    }

    [Fact]
    public async Task Folders_the_catalog_recorded_settle_a_document_by_the_same_rule()
    {
        var near = Schema(Thing, "Near");
        var far = Schema(Thing, "Far");
        var stale = Schema(Thing, "Stale");
        // The copies the sync kept, by file: one of them missing, one damaged, one of another version than its row says.
        var copies = new Dictionary<string, RecordedTemplateCopy>(StringComparer.Ordinal)
        {
            ["p/templates/thing.json"] = new(near, DateTime.UnixEpoch),
            ["q/templates/thing.json"] = new("{ not json", DateTime.UnixEpoch),
            ["r/templates/thing.json"] = new(stale, DateTime.UnixEpoch),
        };
        var asked = new List<string>();
        Task<RecordedTemplateCopy?> CopyAsync(string path, CancellationToken ct)
        {
            asked.Add(path);
            return Task.FromResult(copies.GetValueOrDefault(path));
        }

        SyncedTemplateFile Row(string path, string json)
            => new(path, path[..path.LastIndexOf('/')], Thing, VersionOf(Thing, json), null);
        var folders = SyncedTemplates.Of(
            [
                Row("templates/thing.json", far), Row("p/templates/thing.json", near),
                Row("q/templates/thing.json", near), Row("r/templates/thing.json", near),
                new SyncedTemplateFile("p/templates/broken.json", "p/templates", string.Empty, string.Empty, "the schema is not valid JSON"),
            ],
            CopyAsync);

        var chain = folders.ChainFor(".\\p\\m\\doc.yaml");
        Assert.Equal("p/m/doc.yaml", chain.Document);
        Assert.Equal(["p/m/templates", "p/templates", "templates"], chain.Looked);
        Assert.Equal(VersionOf(Thing, near), folders.For("p/m/doc.yaml").Pin(Thing, null, "where").Version);

        var read = await folders.ResolveAsync("p/m/doc.yaml", Thing, null);
        Assert.True(read.Found);
        Assert.Equal("p/templates/thing.json", read.Template!.Origin);

        // A file's copy is read once, however often a page settles on it.
        await folders.ResolveAsync("p/m/doc.yaml", Thing, null);
        Assert.Equal(["p/templates/thing.json"], asked);

        // A copy that is missing, does not read as a schema, or reads as another version: the page says to sync again.
        string Problem(string folder) => $"{folder}/templates/thing.json holds template {Thing} version ";
        var missing = await folders.ResolveAsync("p/m/doc.yaml", Thing, VersionOf(Thing, far));
        Assert.Equal(
            $"templates/thing.json holds template {Thing} version {VersionOf(Thing, far)}, and the copy the sync keeps of it is missing; sync the repository again.",
            missing.Problem);
        Assert.Equal(
            $"{Problem("q")}{VersionOf(Thing, near)}, and the copy the sync keeps of it does not read as a schema; sync the repository again.",
            (await folders.ResolveAsync("q/doc.yaml", Thing, null)).Problem);
        Assert.Equal(
            $"{Problem("r")}{VersionOf(Thing, near)}, and the copy the sync keeps of it reads as version {VersionOf(Thing, stale)}; sync the repository again.",
            (await folders.ResolveAsync("r/doc.yaml", Thing, null)).Problem);

        // Settling needs no schema; with no copies to read, reading one says so.
        var pinsOnly = SyncedTemplates.Of([Row("templates/thing.json", far)], copies: null);
        Assert.Equal(VersionOf(Thing, far), pinsOnly.For("doc.yaml").Pin(Thing, null, "where").Version);
        Assert.Equal(
            $"templates/thing.json holds template {Thing} version {VersionOf(Thing, far)}, and the copy the sync keeps of it is not read here.",
            (await pinsOnly.ResolveAsync("doc.yaml", Thing, null)).Problem);

        Assert.Equal([VersionOf(Thing, near), VersionOf(Thing, far)], (await folders.ListForAsync("p/m/doc.yaml")).Select(t => t.Version));
        Assert.Equal(5, folders.Files.Count);
    }

    [Fact]
    public async Task Folders_a_scan_found_settle_a_document_and_read_its_template_from_the_file()
    {
        var json = Schema(Thing);
        Write("p/templates/thing.json", json);
        var folders = SyncedTemplates.Of(TemplateFolders.Scan(_root, CancellationToken.None));
        var read = await folders.ResolveAsync("p/m/doc.yaml", Thing, null);
        Assert.Equal((VersionOf(Thing, json), "p/templates/thing.json"), (read.Schema!.Version, read.Template!.Origin));

        // The file changed after the scan: what it held is not taken for what it holds now.
        Write("p/templates/thing.json", Schema(Thing, "Changed"));
        Assert.EndsWith("read it again.", (await folders.ResolveAsync("p/m/doc.yaml", Thing, null)).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_schema_of_ones_own_that_names_no_kind_is_given_the_kind_it_is_imported_as()
    {
        var bare = JsonNode.Parse(Schema("acme:wks:master-data--Thing:1.0.0", declareKind: false))!.AsObject();
        Assert.False(TemplateSources.DeclaresKind(bare));
        var declared = TemplateSources.WithKind(bare, "acme:wks:master-data--Thing:1.0.0");
        Assert.True(TemplateSources.DeclaresKind(declared));
        Assert.Equal("acme:wks:master-data--Thing:1.0.0", declared["x-osdu-schema-source"]!.GetValue<string>());

        // A schema that names its kind keeps it: the import refuses another kind than it names.
        var named = JsonNode.Parse(Schema("acme:wks:master-data--Other:1.0.0"))!.AsObject();
        Assert.Equal("acme:wks:master-data--Other:1.0.0", TemplateSources.WithKind(named, "acme:wks:master-data--Thing:1.0.0")["x-osdu-schema-source"]!.GetValue<string>());

        // Imported without the network, a bundled file of one's own is saved naming its kind, so a template folder can hold it.
        var definitions = new OsduDataDefinitions(
            () => throw new InvalidOperationException("a bundled file reads nothing from the network"),
            new Uri("https://example.invalid/api/"), new Uri("https://example.invalid/"),
            Path.Combine(_root, "definitions"), TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), TimeProvider.System);
        var imported = await definitions.ImportAsync(Schema("acme:wks:master-data--Thing:1.0.0", declareKind: false), "acme:wks:master-data--Thing:1.0.0", null, "thing.json");
        Assert.True(TemplateSources.DeclaresKind(imported.Schema.Root));
        Write("templates/thing.json", TemplateSources.FileText(imported.Schema));
        var file = Assert.Single(TemplateFolders.Read(At("templates"), path => path).Files);
        Assert.Equal(("acme:wks:master-data--Thing:1.0.0", imported.Schema.Version), (file.Kind, file.Version));
    }
}

/// <summary>A library of templates in memory, as a store lists and loads them.</summary>
internal sealed class MemoryTemplateStore : ITemplateStore
{
    private readonly List<SchemaSnapshot> _schemas = [];

    public void Add(SchemaSnapshot schema) => _schemas.Add(schema);

    public Task<SchemaSnapshot?> LoadAsync(TemplateReference reference, CancellationToken ct = default)
        => Task.FromResult(_schemas.FirstOrDefault(s => s.Kind == reference.Kind && s.Version == reference.Version));

    public Task<TemplateSaved> SaveAsync(SchemaSnapshot schema, string origin, string actor, CancellationToken ct = default)
    {
        var info = new TemplateInfo(schema.Kind, schema.Version, schema.CapturedUtc.UtcDateTime, actor, origin);
        if (_schemas.Any(s => s.Kind == schema.Kind && s.Version == schema.Version))
        {
            return Task.FromResult(new TemplateSaved(info, TemplateSaveOutcome.Unchanged));
        }

        _schemas.Add(schema);
        return Task.FromResult(new TemplateSaved(info, TemplateSaveOutcome.Created));
    }

    public Task<IReadOnlyList<TemplateInfo>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TemplateInfo>>([.. _schemas.Select(s => new TemplateInfo(s.Kind, s.Version, s.CapturedUtc.UtcDateTime, "tests", "memory"))]);

    public Task DeleteAsync(TemplateReference reference, CancellationToken ct = default)
    {
        _schemas.RemoveAll(s => s.Kind == reference.Kind && s.Version == reference.Version);
        return Task.CompletedTask;
    }
}
