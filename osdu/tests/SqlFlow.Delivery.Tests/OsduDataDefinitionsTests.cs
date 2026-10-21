using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The OSDU data definitions kept as a local copy: the release list read over the repository's GitLab API (page by page)
/// and kept on disk, each release's Generated folder downloaded once as an archive and read from disk after that, across
/// instances, a sync reading the list again and downloading what is missing, and the failures each step can meet.
/// </summary>
public sealed class OsduDataDefinitionsTests : IDisposable
{
    private const string OlderCommit = "0000000000000000000000000000000000000291";
    private const string ReleaseCommit = "99f8fc88d8ad838b5738ac5ad92ac643538b5766";
    private const string NextCommit = "31f0000000000000000000000000000000000031";
    private const string WellboreKind = "osdu:wks:master-data--Wellbore:1.3.0";
    private const string GenericKind = "osdu:wks:dataset--GenericDataset:1.0.0";
    private const string ContentKind = "osdu:wks:content--SeismicHistogram:1.0.0";

    /// <summary>
    /// The v0.30.0 release's Generated folder, reduced to a Wellbore schema and the abstract schemas it refers to, with the
    /// three shapes a release's index holds besides a record schema of its own folder: a building block (the access control
    /// list), a content schema, which describes what sits inside a record and declares no data of its own, and a generic
    /// kind, whose file a release keeps under manifest/ rather than under the folder its entity group names.
    /// </summary>
    private static readonly Dictionary<string, string> Tree = new(StringComparer.Ordinal)
    {
        ["SchemaStatus.json"] = """
            { "osdu:wks:AbstractAccessControlList:1.0.0": "PUBLISHED", "osdu:wks:master-data--Wellbore:1.0.0": "PUBLISHED",
              "osdu:wks:master-data--Wellbore:1.3.0": "PUBLISHED", "osdu:wks:master-data--Well:1.2.0": "DEVELOPMENT", "osdu:wks:Manifest:1.0.0": "PUBLISHED",
              "osdu:wks:content--SeismicHistogram:1.0.0": "PUBLISHED", "osdu:wks:dataset--GenericDataset:1.0.0": "PUBLISHED" }
            """,
        ["master-data/Well.1.2.0.json"] = """
            { "x-osdu-schema-source": "osdu:wks:master-data--Well:1.2.0", "type": "object",
              "properties": { "data": { "$ref": "../abstract/AbstractFacility.1.1.0.json" } } }
            """,
        ["content/SeismicHistogram.1.0.0.json"] = """
            { "x-osdu-schema-source": "osdu:wks:content--SeismicHistogram:1.0.0", "type": "object",
              "allOf": [ { "$ref": "../abstract/AbstractContent.1.0.0.json" },
                         { "type": "object", "properties": { "SeismicRecordID": { "type": "string" } } } ] }
            """,
        ["manifest/GenericDataset.1.0.0.json"] = """
            { "x-osdu-schema-source": "osdu:wks:dataset--GenericDataset:1.0.0", "type": "object",
              "properties": {
                "acl": { "$ref": "../abstract/AbstractAccessControlList.1.0.0.json" },
                "data": { "type": "object", "properties": { "Name": { "type": "string" } } } } }
            """,
        ["abstract/AbstractContent.1.0.0.json"] = """
            { "type": "object", "properties": { "Name": { "type": "string" } } }
            """,
        ["master-data/Wellbore.1.0.0.json"] = """
            { "x-osdu-schema-source": "osdu:wks:master-data--Wellbore:1.0.0", "type": "object",
              "properties": { "data": { "$ref": "../abstract/AbstractFacility.1.1.0.json" } } }
            """,
        ["master-data/Wellbore.1.3.0.json"] = """
            { "$id": "https://schema.osdu.opengroup.org/json/master-data/Wellbore.1.3.0.json", "x-osdu-schema-source": "osdu:wks:master-data--Wellbore:1.3.0", "type": "object",
              "properties": {
                "acl": { "$ref": "../abstract/AbstractAccessControlList.1.0.0.json" },
                "data": { "allOf": [ { "$ref": "../abstract/AbstractFacility.1.1.0.json" }, { "type": "object", "properties": { "WellID": { "type": "string" } } } ] } } }
            """,
        ["abstract/AbstractAccessControlList.1.0.0.json"] = """
            { "type": "object", "properties": { "owners": { "type": "array", "items": { "type": "string" } } } }
            """,
        ["abstract/AbstractFacility.1.1.0.json"] = """
            { "type": "object", "properties": { "FacilityName": { "type": "string" }, "Owners": { "$ref": "AbstractAccessControlList.1.0.0.json" } } }
            """,
    };

    private static readonly Dictionary<string, string> OlderTree = new(StringComparer.Ordinal)
    {
        ["SchemaStatus.json"] = """{ "osdu:wks:master-data--Well:1.0.0": "DEVELOPMENT" }""",
        ["master-data/Well.1.0.0.json"] = """
            { "x-osdu-schema-source": "osdu:wks:master-data--Well:1.0.0", "type": "object",
              "properties": { "data": { "type": "object", "properties": { "FacilityName": { "type": "string" } } } } }
            """,
    };

    private readonly string _cache = Samples.NewTempDirectory();

    public void Dispose()
    {
        if (Directory.Exists(_cache))
        {
            Directory.Delete(_cache, recursive: true);
        }
    }

    [Fact]
    public async Task The_release_list_is_read_page_by_page_and_kept_on_disk_for_the_next_instance()
    {
        var repository = new Repository();
        using var http = repository.Client();
        var definitions = Definitions(http, Timeout.InfiniteTimeSpan);

        var releases = await definitions.ReleasesAsync();
        Assert.Equal(["v0.30.0", "v0.29.1", "v0.28.3.1"], releases.Select(r => r.Name));
        Assert.Equal(ReleaseCommit, releases[0].Commit);
        Assert.Equal(2, repository.TagPages);
        Assert.NotNull(definitions.SyncedUtc);
        Assert.False(definitions.IsLocal(releases[0]));

        // A new instance over the same local copy (a restart) answers from disk without asking the repository.
        var offline = new Repository { TagsFail = true };
        using var offlineHttp = offline.Client();
        var restarted = Definitions(offlineHttp, Timeout.InfiniteTimeSpan);
        Assert.Equal(["v0.30.0", "v0.29.1", "v0.28.3.1"], (await restarted.ReleasesAsync()).Select(r => r.Name));
        Assert.Equal(0, offline.TagPages);
    }

    [Fact]
    public async Task A_release_is_downloaded_once_and_read_from_disk_after_that_even_by_the_next_instance()
    {
        var repository = new Repository();
        using var http = repository.Client();
        var clock = new TestClock();
        var definitions = Definitions(http, Timeout.InfiniteTimeSpan, clock);

        var index = await definitions.IndexAsync(release: null);
        Assert.Equal("v0.30.0", index.Release.Name);

        // Only the kinds a template can be laid out from: the abstract building blocks, the manifest and the content
        // schema are not records a mapping fills, and the generic kind is, at the file the release keeps it in.
        Assert.Equal(
            [GenericKind, "osdu:wks:master-data--Well:1.2.0", "osdu:wks:master-data--Wellbore:1.3.0", "osdu:wks:master-data--Wellbore:1.0.0"],
            index.Schemas.Select(s => s.Kind));
        var wellbore = index.Schemas.Single(s => s.Kind == WellboreKind);
        Assert.Equal(("master-data--Wellbore", "1.3.0", "PUBLISHED", "master-data/Wellbore.1.3.0.json"), (wellbore.EntityType, wellbore.Version, wellbore.Status, wellbore.Path));
        Assert.Equal(
            "https://community.opengroup.org/osdu/data/data-definitions/-/blob/v0.30.0/Generated/master-data/Wellbore.1.3.0.json",
            definitions.FileWebUrl(index.Release, wellbore.Path).AbsoluteUri);
        Assert.True(definitions.IsLocal(index.Release));

        var file = await definitions.FetchAsync("v0.30.0", WellboreKind);
        Assert.Equal($"OSDU data definitions v0.30.0 ({ReleaseCommit[..12]}) Generated/master-data/Wellbore.1.3.0.json", file.Origin);
        Assert.Equal(["master-data/Wellbore.1.3.0.json", "abstract/AbstractAccessControlList.1.0.0.json", "abstract/AbstractFacility.1.1.0.json"], file.Files);
        Assert.Equal(["AbstractAccessControlList.1.0.0", "AbstractFacility.1.1.0"], Assert.IsType<JsonObject>(file.Schema.Root["definitions"]).Select(d => d.Key));
        var template = OsduTemplate.From(file.Schema);
        foreach (var path in new[] { "osdu.acl.owners", "osdu.data.FacilityName", "osdu.data.WellID" })
        {
            Assert.NotNull(template.Find(TemplatePath.TryParse(path, out var parsed, out _) ? parsed! : throw new InvalidOperationException(path)));
        }

        // The whole release came down as one archive, however many files were read.
        Assert.Equal([ReleaseCommit], repository.Archives);

        // The next instance reads the same release from disk: no archive, and the same template version.
        var restarted = Definitions(http, Timeout.InfiniteTimeSpan, clock);
        Assert.Equal(file.Schema.Version, (await restarted.FetchAsync("v0.30.0", WellboreKind)).Schema.Version);
        Assert.Equal([ReleaseCommit], repository.Archives);

        // An older release is its own download, at its own commit.
        Assert.Equal(["osdu:wks:master-data--Well:1.0.0"], (await definitions.IndexAsync("v0.29.1")).Schemas.Select(s => s.Kind));
        Assert.Equal([ReleaseCommit, OlderCommit], repository.Archives);

        // A checkout of the same release bundles to the same template version.
        var root = Samples.NewTempDirectory();
        foreach (var (path, json) in Tree)
        {
            var target = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, json);
        }

        Assert.Equal(file.Schema.Version, (await TemplateSources.FromDirectoryAsync(root, WellboreKind, clock)).Version);
    }

    [Fact]
    public async Task A_kind_is_read_from_the_file_that_declares_it_and_what_is_not_a_record_schema_is_left_out_of_the_index()
    {
        var repository = new Repository();
        using var http = repository.Client();
        var definitions = Definitions(http, Timeout.InfiniteTimeSpan);
        var index = await definitions.IndexAsync("v0.30.0");

        // A generic kind: its entity group says dataset/, and the release keeps it under manifest/, which is where it is read
        // from, listed at, linked to, and bundled from.
        var generic = index.Schemas.Single(s => s.Kind == GenericKind);
        Assert.Equal("manifest/GenericDataset.1.0.0.json", generic.Path);
        Assert.Equal(
            "https://community.opengroup.org/osdu/data/data-definitions/-/blob/v0.30.0/Generated/manifest/GenericDataset.1.0.0.json",
            definitions.FileWebUrl(index.Release, generic.Path).AbsoluteUri);

        var file = await definitions.FetchAsync("v0.30.0", GenericKind);
        Assert.Equal("manifest/GenericDataset.1.0.0.json", file.Path);
        Assert.Equal(["manifest/GenericDataset.1.0.0.json", "abstract/AbstractAccessControlList.1.0.0.json"], file.Files);
        Assert.NotNull(OsduTemplate.From(file.Schema).Find(TemplatePath.TryParse("osdu.data.Name", out var name, out _) ? name! : throw new InvalidOperationException()));

        var published = await definitions.PublishedFileAsync("v0.30.0", GenericKind);
        Assert.Equal(("manifest/GenericDataset.1.0.0.json", "PUBLISHED"), (published.Path, published.Status));

        // A content schema describes what sits inside a record, so it is in no index and no template is laid out from it.
        Assert.DoesNotContain(index.Schemas, s => s.Kind == ContentKind);
        var content = await Assert.ThrowsAsync<DeliveryException>(() => definitions.FetchAsync("v0.30.0", ContentKind));
        Assert.Contains(
            "Generated/content/SeismicHistogram.1.0.0.json at v0.30.0: the schema declares no 'data' property",
            content.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_published_schema_file_brought_in_from_outside_is_bundled_with_the_files_it_refers_to_from_a_release()
    {
        var repository = new Repository();
        using var http = repository.Client();
        var definitions = Definitions(http, Timeout.InfiniteTimeSpan);

        // A release's own Wellbore file, downloaded and given a kind of its own, as someone making a schema of their own does.
        const string kind = "osdu:wks:master-data--WellboreTest:1.5.1";
        var text = Tree["master-data/Wellbore.1.3.0.json"].Replace(WellboreKind, kind, StringComparison.Ordinal);

        var imported = await definitions.ImportAsync(text, kind, "v0.30.0", "file WellboreTest.1.5.1.json");
        Assert.Equal("v0.30.0", imported.Release?.Name);
        Assert.Equal(["abstract/AbstractAccessControlList.1.0.0.json", "abstract/AbstractFacility.1.1.0.json"], imported.Files);
        Assert.Equal(["AbstractAccessControlList.1.0.0", "AbstractFacility.1.1.0"], Assert.IsType<JsonObject>(imported.Schema.Root["definitions"]).Select(d => d.Key));
        Assert.Equal(kind, imported.Schema.Kind);
        Assert.Equal(
            $"file WellboreTest.1.5.1.json, references from OSDU data definitions v0.30.0 ({ReleaseCommit[..12]})",
            imported.Origin("file WellboreTest.1.5.1.json"));

        // It lays out exactly as the release's own file does, since it refers to the same shared schemas.
        var published = await definitions.FetchAsync("v0.30.0", WellboreKind);
        Assert.Equal(
            OsduTemplate.From(published.Schema).Variables.Select(v => v.Path.Text),
            OsduTemplate.From(imported.Schema).Variables.Select(v => v.Path.Text));

        // A schema that arrives bundled is taken as it is, reads no release, and saves as the same version.
        var bundled = await definitions.ImportAsync(DocumentJson.Compact(imported.Schema.Root), kind, release: null, "pasted schema");
        Assert.Null(bundled.Release);
        Assert.Empty(bundled.Files);
        Assert.Equal(imported.Schema.Version, bundled.Schema.Version);
        Assert.Equal("pasted schema", bundled.Origin("pasted schema"));

        // A shared schema the release does not hold is named, and so is a release the data definitions do not have.
        var missing = await Assert.ThrowsAsync<DataDefinitionsException>(() => definitions.ImportAsync(
            text.Replace("AbstractFacility.1.1.0", "AbstractFacility.9.9.9", StringComparison.Ordinal), kind, null, "file WellboreTest.1.5.1.json"));
        Assert.True(missing.NotFound);
        Assert.Contains("Generated/abstract/AbstractFacility.9.9.9.json at v0.30.0", missing.Message, StringComparison.Ordinal);
        var noRelease = await Assert.ThrowsAsync<DataDefinitionsException>(() => definitions.ImportAsync(text, kind, "v9.9.9", "file WellboreTest.1.5.1.json"));
        Assert.True(noRelease.NotFound);

        // The kind the schema declares is still the kind its file holds.
        var other = await Assert.ThrowsAsync<DeliveryException>(() => definitions.ImportAsync(text, "osdu:wks:master-data--Other:1.0.0", "v0.30.0", "file WellboreTest.1.5.1.json"));
        Assert.Contains($"describes '{kind}'", other.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sync_reads_the_release_list_again_and_downloads_the_release_it_is_asked_for()
    {
        var repository = new Repository();
        using var http = repository.Client();
        var definitions = Definitions(http, Timeout.InfiniteTimeSpan);
        Assert.Equal("v0.30.0", (await definitions.ReleasesAsync())[0].Name);

        repository.NextRelease = true;

        // Kept until a sync: the list does not change on its own.
        Assert.DoesNotContain(await definitions.ReleasesAsync(), r => r.Name == "v0.31.0");

        var sync = await definitions.SyncAsync(release: null);
        Assert.Equal("v0.31.0", sync.Releases[0].Name);
        Assert.Equal(["v0.31.0"], sync.Downloaded);
        Assert.True(definitions.IsLocal(sync.Releases[0]));

        // A release already on disk is not downloaded again.
        var again = await definitions.SyncAsync("v0.31.0");
        Assert.Empty(again.Downloaded);
        Assert.Equal([NextCommit], repository.Archives);

        await Assert.ThrowsAsync<DataDefinitionsException>(() => definitions.SyncAsync("v9.9.9"));
    }

    [Fact]
    public async Task A_release_a_cleanup_emptied_is_not_on_disk_and_is_put_back_whole_by_the_next_read_or_a_sync()
    {
        var repository = new Repository();
        using var http = repository.Client();
        var definitions = Definitions(http, Timeout.InfiniteTimeSpan);
        var schemas = (await definitions.IndexAsync(release: null)).Schemas.Select(s => s.Kind).ToList();
        var folder = Path.Combine(_cache, ReleaseCommit);

        // A cleanup of the temp folder deletes the files and leaves the folders standing.
        DeleteFiles(folder);
        Assert.True(Directory.Exists(Path.Combine(folder, OsduDataDefinitions.TreeRoot, "master-data")));

        var restarted = Definitions(http, Timeout.InfiniteTimeSpan);
        var release = (await restarted.ReleasesAsync())[0];
        Assert.False(restarted.IsLocal(release));
        Assert.Equal(schemas, (await restarted.IndexAsync("v0.30.0")).Schemas.Select(s => s.Kind));
        Assert.True(restarted.IsLocal(release));
        Assert.Equal([ReleaseCommit, ReleaseCommit], repository.Archives);

        // Emptied again while the instance runs: it found the release whole, and a sync looks at the disk again.
        DeleteFiles(folder);
        var sync = await restarted.SyncAsync("v0.30.0");
        Assert.Equal(["v0.30.0"], sync.Downloaded);
        Assert.True(restarted.IsLocal(release));
        Assert.True(File.Exists(Path.Combine(folder, OsduDataDefinitions.TreeRoot, OsduDataDefinitions.IndexFile)));
        Assert.Empty((await restarted.SyncAsync("v0.30.0")).Downloaded);
    }

    [Fact]
    public async Task A_file_the_local_copy_loses_while_an_instance_runs_is_put_back_and_read_and_the_files_still_there_are_left_alone()
    {
        var repository = new Repository();
        using var http = repository.Client();
        var definitions = Definitions(http, Timeout.InfiniteTimeSpan);
        await definitions.IndexAsync(release: null);
        var tree = Path.Combine(_cache, ReleaseCommit, OsduDataDefinitions.TreeRoot);
        var facility = Path.Combine(tree, "abstract", "AbstractFacility.1.1.0.json");
        File.Delete(facility);

        // A reader in another process holds a file still there; putting the copy back whole must not need it.
        using (new FileStream(Path.Combine(tree, "master-data", "Wellbore.1.3.0.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var file = await definitions.FetchAsync("v0.30.0", WellboreKind);
            Assert.Equal(["master-data/Wellbore.1.3.0.json", "abstract/AbstractAccessControlList.1.0.0.json", "abstract/AbstractFacility.1.1.0.json"], file.Files);
        }

        Assert.True(File.Exists(facility));
        Assert.Equal([ReleaseCommit, ReleaseCommit], repository.Archives);

        // A file the release does not hold is still not found, and is no reason to download it again.
        var missing = await Assert.ThrowsAsync<DataDefinitionsException>(() => definitions.FetchAsync("v0.30.0", "osdu:wks:master-data--Missing:1.0.0"));
        Assert.True(missing.NotFound);
        Assert.Equal([ReleaseCommit, ReleaseCommit], repository.Archives);
    }

    [Fact]
    public async Task A_release_unpacked_before_its_files_were_listed_is_downloaded_once_more_and_keeps_the_files_it_holds()
    {
        var repository = new Repository();
        using var http = repository.Client();
        var definitions = Definitions(http, Timeout.InfiniteTimeSpan);
        var release = (await definitions.IndexAsync(release: null)).Release;
        var folder = Path.Combine(_cache, ReleaseCommit);
        File.Delete(Path.Combine(folder, OsduDataDefinitions.ManifestFile));
        var index = Path.Combine(folder, OsduDataDefinitions.TreeRoot, OsduDataDefinitions.IndexFile);
        var stamped = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(index, stamped);

        var restarted = Definitions(http, Timeout.InfiniteTimeSpan);
        Assert.False(restarted.IsLocal(release));
        await restarted.IndexAsync("v0.30.0");
        Assert.Equal([ReleaseCommit, ReleaseCommit], repository.Archives);
        Assert.Equal(stamped, File.GetLastWriteTimeUtc(index));
        Assert.True(File.Exists(Path.Combine(folder, OsduDataDefinitions.ManifestFile)));

        // Listed now: the next instance finds it whole without a download.
        var again = Definitions(http, Timeout.InfiniteTimeSpan);
        Assert.True(again.IsLocal(release));
        await again.IndexAsync("v0.30.0");
        Assert.Equal([ReleaseCommit, ReleaseCommit], repository.Archives);
    }

    [Fact]
    public async Task What_the_repository_does_not_hold_is_not_found_and_a_repository_that_fails_says_so()
    {
        var repository = new Repository();
        using var http = repository.Client();
        var definitions = Definitions(http, Timeout.InfiniteTimeSpan);

        var release = await Assert.ThrowsAsync<DataDefinitionsException>(() => definitions.IndexAsync("v9.9.9"));
        Assert.True(release.NotFound);
        Assert.Contains("no release 'v9.9.9'; the latest is v0.30.0", release.Message, StringComparison.Ordinal);

        var kind = await Assert.ThrowsAsync<DataDefinitionsException>(() => definitions.FetchAsync(null, "osdu:wks:master-data--Missing:1.0.0"));
        Assert.True(kind.NotFound);
        Assert.Contains("Generated/master-data/Missing.1.0.0.json at v0.30.0", kind.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<FlowValidationException>(() => definitions.FetchAsync(null, "Wellbore"));

        // No list on disk and the repository down: the failure is the answer.
        var down = new Repository { TagsFail = true };
        using var downHttp = down.Client();
        var failure = await Assert.ThrowsAsync<DataDefinitionsException>(() => Definitions(downHttp, Timeout.InfiniteTimeSpan, cache: Samples.NewTempDirectory()).ReleasesAsync());
        Assert.False(failure.NotFound);
        Assert.Contains("answered 503", failure.Message, StringComparison.Ordinal);

        // An archive whose entry would land outside the release folder is refused, and nothing of it is kept.
        var hostile = new Repository { HostileArchive = true };
        using var hostileHttp = hostile.Client();
        var refused = Definitions(hostileHttp, Timeout.InfiniteTimeSpan, cache: Samples.NewTempDirectory());
        var outside = await Assert.ThrowsAsync<DataDefinitionsException>(() => refused.IndexAsync(null));
        Assert.Contains("leads outside the release folder", outside.Message, StringComparison.Ordinal);
        Assert.False(refused.IsLocal((await refused.ReleasesAsync())[0]));
    }

    [Fact]
    public async Task A_reference_that_leads_outside_the_data_definitions_is_refused()
    {
        var root = Samples.NewTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "master-data"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "master-data", "Wellbore.1.3.0.json"),
            """{ "type": "object", "properties": { "acl": { "$ref": "../../private/AbstractAccessControlList.1.0.0.json" }, "data": { "type": "object" } } }""");

        var ex = await Assert.ThrowsAsync<DeliveryException>(() => TemplateSources.FromDirectoryAsync(root, WellboreKind, new TestClock()));
        Assert.Contains("leads outside the data definitions", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_example_record_is_read_from_the_newest_release_once_and_kept_beside_it_for_the_next_instance()
    {
        var repository = new Repository();
        repository.Examples["Examples/master-data/Wellbore.1.3.0.json"] = """
            { "kind": "osdu:wks:master-data--Wellbore:1.3.0", "data": { "FacilityName": "Example FacilityName" } }
            """;
        using var http = repository.Client();
        var definitions = Definitions(http, Timeout.InfiniteTimeSpan);

        var example = await definitions.ExampleAsync(WellboreKind);

        Assert.NotNull(example);
        Assert.Equal((WellboreKind, "v0.30.0", "Examples/master-data/Wellbore.1.3.0.json"), (example.Kind, example.Release, example.Path));
        Assert.Equal("https://community.opengroup.org/osdu/data/data-definitions/-/blob/v0.30.0/Examples/master-data/Wellbore.1.3.0.json", example.WebUrl.AbsoluteUri);
        Assert.Equal("Example FacilityName", example.Record["data"]!["FacilityName"]!.GetValue<string>());

        // The file is named whole in one path segment, its slashes escaped, at the release's commit, as GitLab's API takes it.
        Assert.Equal([("Examples%2Fmaster-data%2FWellbore.1.3.0.json", ReleaseCommit)], repository.ExampleReads);

        // Asked again, the instance answers from what it read; a new instance over the same copy answers from disk.
        Assert.NotNull(await definitions.ExampleAsync(WellboreKind));
        repository.ExamplesFail = true;
        var restarted = Definitions(http, Timeout.InfiniteTimeSpan);
        Assert.Equal("Example FacilityName", (await restarted.ExampleAsync(WellboreKind))!.Record["data"]!["FacilityName"]!.GetValue<string>());
        Assert.Single(repository.ExampleReads);

        // The example made the release's folder before the release was downloaded; the download fills it in.
        Assert.False(restarted.IsLocal((await restarted.ReleasesAsync())[0]));
        Assert.Contains(WellboreKind, (await restarted.IndexAsync(release: null)).Schemas.Select(s => s.Kind));
        Assert.True(File.Exists(Path.Combine(_cache, ReleaseCommit, "Examples", "master-data", "Wellbore.1.3.0.json")));
    }

    [Fact]
    public async Task A_kind_the_release_publishes_no_example_of_has_none_and_that_is_remembered_on_disk()
    {
        var repository = new Repository();
        using var http = repository.Client();
        var definitions = Definitions(http, Timeout.InfiniteTimeSpan);

        Assert.Null(await definitions.ExampleAsync("osdu:wks:master-data--Well:1.2.0"));
        Assert.Single(repository.ExampleReads);

        // Neither this instance nor the next asks the repository again for a file it said it does not hold.
        Assert.Null(await definitions.ExampleAsync("osdu:wks:master-data--Well:1.2.0"));
        Assert.Null(await Definitions(http, Timeout.InfiniteTimeSpan).ExampleAsync("osdu:wks:master-data--Well:1.2.0"));
        Assert.Single(repository.ExampleReads);
    }

    [Fact]
    public async Task A_repository_that_cannot_give_an_example_throws_and_keeps_nothing_so_the_next_ask_tries_again()
    {
        var repository = new Repository { ExamplesFail = true };
        repository.Examples["Examples/master-data/Wellbore.1.3.0.json"] = """{ "kind": "osdu:wks:master-data--Wellbore:1.3.0" }""";
        using var http = repository.Client();
        var definitions = Definitions(http, Timeout.InfiniteTimeSpan);

        var failure = await Assert.ThrowsAsync<DataDefinitionsException>(() => definitions.ExampleAsync(WellboreKind));
        Assert.False(failure.NotFound);
        Assert.Contains("answered 503", failure.Message, StringComparison.Ordinal);

        repository.ExamplesFail = false;
        Assert.NotNull(await definitions.ExampleAsync(WellboreKind));
    }

    [Theory]
    [InlineData("osdu:wks:work-product-component--WellLog:1.4.0", "Examples/work-product-component/WellLog.1.4.0.json")]
    [InlineData("osdu:wks:reference-data--UnitOfMeasure:1.0.0", "Examples/reference-data/UnitOfMeasure.1.0.0.json")]
    [InlineData("acme:wks:master-data--Wellbore:1.3.0", null)]
    [InlineData("osdu:wks:Wellbore:1.3.0", null)]
    [InlineData("osdu:wks:master-data--:1.3.0", null)]
    [InlineData("osdu:wks:master-data--../escape:1.3.0", null)]
    [InlineData("osdu:wks:master-data--Well bore:1.3.0", null)]
    [InlineData("osdu:wks:master-data--Wellbore:1.x.0", null)]
    [InlineData("osdu:wks:master-data--Wellbore", null)]
    [InlineData("osdu:wks:master-data--Wellbore:1.3.0:extra", null)]
    public void Only_an_OSDU_kind_whose_parts_are_plain_names_an_example_file(string kind, string? path)
        => Assert.Equal(path, OsduDataDefinitions.ExamplePath(kind));

    private OsduDataDefinitions Definitions(HttpClient http, TimeSpan freshness, TimeProvider? time = null, string? cache = null)
        => new(() => http, OsduDataDefinitions.DefaultApiUrl, OsduDataDefinitions.DefaultWebUrl, cache ?? _cache, freshness, TimeSpan.FromMinutes(1), time ?? new TestClock());

    /// <summary>Deletes every file under <paramref name="folder"/> and leaves its folders standing, as a cleanup of the temp folder does.</summary>
    private static void DeleteFiles(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList())
        {
            File.Delete(file);
        }
    }

    /// <summary>The repository's GitLab API: the tag list in two pages, and each release's Generated folder as a tar.gz archive.</summary>
    private sealed class Repository
    {
        public bool TagsFail { get; init; }

        public bool HostileArchive { get; init; }

        public bool NextRelease { get; set; }

        public int TagPages { get; private set; }

        public List<string> Archives { get; } = [];

        /// <summary>The example records the repository publishes, by their path, as its Examples folder holds them.</summary>
        public Dictionary<string, string> Examples { get; } = new(StringComparer.Ordinal);

        /// <summary>Each example file asked for: its path as the request named it, still escaped, and the commit it was asked at.</summary>
        public List<(string Path, string Ref)> ExampleReads { get; } = [];

        public bool ExamplesFail { get; set; }

        public HttpClient Client() => new(new Handler(this), disposeHandler: true);

        private HttpResponseMessage Answer(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;
            if (path.EndsWith("/repository/tags", StringComparison.Ordinal))
            {
                if (TagsFail)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("""{ "message": "maintenance" }""") };
                }

                TagPages++;
                // The page number ends the query (per_page=100&page=1); "per_page=100" alone must not read as page 1.
                if (query.EndsWith("&page=1", StringComparison.Ordinal))
                {
                    var next = NextRelease ? $$"""{ "name": "v0.31.0", "commit": { "id": "{{NextCommit}}", "committed_date": "2026-10-01T10:00:00.000+00:00" } },""" : string.Empty;
                    var first = Json($$"""
                        [ {{next}}
                          { "name": "v0.29.1", "commit": { "id": "{{OlderCommit}}", "committed_date": "2026-01-29T15:47:11.000+08:00" } },
                          { "name": "milestone-27", "commit": { "id": "{{OlderCommit}}" } } ]
                        """);
                    first.Headers.Add("X-Next-Page", "2");
                    return first;
                }

                return Json($$"""
                    [ { "name": "v0.30.0", "commit": { "id": "{{ReleaseCommit}}", "committed_date": "2026-07-17T14:55:57.000+08:00" } },
                      { "name": "v0.28.3.1", "commit": { "id": "{{OlderCommit}}" } } ]
                    """);
            }

            const string Files = "/repository/files/";
            var files = path.IndexOf(Files, StringComparison.Ordinal);
            if (files >= 0 && path.EndsWith("/raw", StringComparison.Ordinal))
            {
                if (ExamplesFail)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("""{ "message": "maintenance" }""") };
                }

                var escaped = path[(files + Files.Length)..^"/raw".Length];
                var reference = query.TrimStart('?').Split('&').Select(part => part.Split('=', 2)).Where(part => part[0] == "ref").Select(part => part[1]).Single();
                ExampleReads.Add((escaped, reference));
                return reference == ReleaseCommit && Examples.TryGetValue(Uri.UnescapeDataString(escaped), out var json)
                    ? Json(json)
                    : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{ "message": "404 File Not Found" }""") };
            }

            if (path.EndsWith("/repository/archive.tar.gz", StringComparison.Ordinal) && query.Contains("path=Generated", StringComparison.Ordinal))
            {
                foreach (var (commit, tree) in new[] { (ReleaseCommit, Tree), (OlderCommit, OlderTree), (NextCommit, Tree) })
                {
                    if (query.Contains("sha=" + commit, StringComparison.Ordinal))
                    {
                        Archives.Add(commit);
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive(commit, tree, HostileArchive)) };
                    }
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string json)
            => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        /// <summary>A GitLab archive of the Generated folder: one top folder named for the project, release and commit.</summary>
        private static byte[] Archive(string commit, IReadOnlyDictionary<string, string> tree, bool hostile)
        {
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
            {
                var top = $"data-definitions-{commit}-Generated/";
                tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, top));
                foreach (var (path, json) in tree)
                {
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, top + "Generated/" + path) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(json)) });
                }

                if (hostile)
                {
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, top + "Generated/../../escaped.json") { DataStream = new MemoryStream("{}"u8.ToArray()) });
                }
            }

            return buffer.ToArray();
        }

        private sealed class Handler(Repository repository) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(repository.Answer(request));
        }
    }
}
