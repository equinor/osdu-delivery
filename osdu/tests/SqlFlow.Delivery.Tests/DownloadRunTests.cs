using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Downloads;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A download flow's runs end to end (osdu/docs/reference/flow/download.md), over the module's database on SQL Server, a
/// platform that serves the search index's cursor read and storage's listing and reads by id, and a local folder: a run writes
/// every record to a JSON file named after its id and records the version each file holds; a later run writes only what is new
/// or changed, a newer version replacing the file; an id OSDU no longer serves is marked gone and keeps its file; two ids that
/// differ only in case never share a file; a record storage does not return is recorded with why, and written by a later run;
/// a forced run, or one writing to another folder, writes every record again; a listing that fails part way changes nothing;
/// and a plan reads no record and writes nothing.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DownloadRunTests : IDisposable
{
    private const string Partition = "dev";
    private const string Kind = "osdu:wks:master-data--Wellbore:1.0.0";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly FakeDimensionPlatform _search = new();
    private readonly StoragePlatform _platform;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "osdu-download-" + Guid.NewGuid().ToString("N"));

    public DownloadRunTests()
    {
        _platform = new StoragePlatform(_search);
    }

    public void Dispose()
    {
        _db.Dispose();
        _platform.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string Id(string key) => $"{Partition}:master-data--Wellbore:{key}";

    private string Flow(string location = "records", string kind = Kind, string read = "search", string extra = "") => $$"""
        flowType: download
        name: wellbore-download
        partitions: [dev]
        source:
          endpoint: http://localhost
          read: {{read}}
        target:
          location: '{{Path.Combine(_root, location)}}'
        downloads:
          - name: Wellbores
            kind: "{{kind}}"
        {{extra}}
        reliability: { retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        parallelism: { recordsAtOnce: 2 }
        """;

    private JsonObject Add(string id, long version = 3, string kind = Kind, string name = "WB")
    {
        var record = _search.Add(id, kind, new JsonObject { ["FacilityName"] = name });
        record["version"] = version;
        record["createUser"] = "delivery-sp@contoso.com";
        record["createTime"] = "2026-09-01T08:00:00.000Z";
        return record;
    }

    private (DownloadRunner Runner, OsduLedger Ledger, DownloadFlowDefinition Flow) Runner(string yaml)
    {
        var ledger = _db.Ledger(_clock);
        var flow = new DeliveryDocumentLoader().ParseDownload(yaml, "flows/download.yaml").ForPartition(Partition);
        var runner = new DownloadRunner(Samples.Engine(ledger, _clock), flow, new Dictionary<string, string>(), Samples.Logger<DownloadRunner>(), _platform, allowLoopback: true);
        return (runner, ledger, flow);
    }

    private static Task<DownloadOutcome> RunAsync(DownloadRunner runner, bool force = false)
        => runner.DownloadAsync([], force, Guid.NewGuid(), "tests", CancellationToken.None);

    /// <summary>Every record of the download, by id, paged as the Downloads tab pages them.</summary>
    private static async Task<Dictionary<string, DownloadRecordState>> RecordsAsync(OsduLedger ledger, int downloadId, string? state = null)
    {
        var all = new Dictionary<string, DownloadRecordState>(StringComparer.Ordinal);
        long? after = null;
        while (true)
        {
            var page = await ledger.ListDownloadRecordsAsync(Partition, downloadId, state, null, after, 2);
            if (page.Count == 0)
            {
                return all;
            }

            foreach (var row in page)
            {
                all.Add(row.TargetId, row);
            }

            after = page[^1].DownloadRecordId;
        }
    }

    private static async Task<DownloadState> DownloadAsync(OsduLedger ledger, DownloadFlowDefinition flow)
        => Assert.Single(await ledger.ListDownloadsAsync(flow.LedgerId));

    private string FileOf(string location, DownloadRecordState record) => Path.Combine(_root, location, "Wellbores", record.FileName);

    [Fact]
    public async Task A_first_run_writes_every_record_to_a_file_named_after_its_id_and_records_what_each_file_holds()
    {
        Add(Id("WB-0001"), version: 11);
        Add(Id("WB-0002"), version: 12);
        Add(Id("ft%2Fs:1"), version: 13);
        var (runner, ledger, flow) = Runner(Flow());

        var outcome = await RunAsync(runner);

        var summary = Assert.Single(outcome.Downloads);
        Assert.Equal((DownloadRunStatus.Completed, 3L, 3L, 3L, 0L, (string?)null), (summary.Status, summary.Listed, summary.Added, summary.Written, summary.Missing, summary.Rewrite));
        Assert.Equal(Path.Combine(_root, "records", "Wellbores"), summary.Location);
        Assert.Equal(
            ["dev~master-data--Wellbore~WB-0001.json", "dev~master-data--Wellbore~WB-0002.json", "dev~master-data--Wellbore~ft=2Fs~1.json"],
            Directory.GetFiles(summary.Location!).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        var download = await DownloadAsync(ledger, flow);
        Assert.Equal((summary.Location, summary.DownloadRunId), (download.Location, download.LastRunId));
        var records = await RecordsAsync(ledger, download.DownloadId);
        Assert.Equal(3, records.Count);
        foreach (var record in records.Values)
        {
            // The file holds the record as storage returned it, and the ledger says which version, how big, and its hash.
            var bytes = await File.ReadAllBytesAsync(FileOf("records", record));
            var held = JsonNode.Parse(bytes)!;
            Assert.Equal(record.TargetId, held["id"]!.GetValue<string>());
            Assert.Equal(record.Version, held["version"]!.GetValue<long>());
            Assert.Equal("WB", held["data"]!["FacilityName"]!.GetValue<string>());
            Assert.Equal((DownloadRecordStates.Written, record.Version, (long)bytes.Length), (record.State, record.WrittenVersion, record.Bytes!.Value));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), record.Sha256);
            Assert.Equal(summary.DownloadRunId, record.WrittenRunId);
            Assert.Equal(record.TargetId, DownloadFileNames.IdOf(record.FileName));
        }

        var counts = await ledger.DownloadCountsAsync(Partition, download.DownloadId);
        Assert.Equal((3L, 0L, 0L, 0L, 3L), (counts.Written, counts.Due, counts.Problem, counts.Gone, counts.Served));
        Assert.Equal(records.Values.Sum(r => r.Bytes!.Value), counts.Bytes);
        var run = Assert.Single(await ledger.ListDownloadRunsAsync(Partition, download.DownloadId, 10));
        Assert.Equal((DownloadRunStatus.Completed, 3L, 3L, summary.Bytes), (run.Status, run.Listed, run.Written, run.Bytes));
        await using var db = _db.CreateDbContext();
        Assert.Equal(0, await db.DeliveryDownloadScans.CountAsync());
    }

    [Fact]
    public async Task A_later_run_writes_only_what_is_new_or_changed_and_a_newer_version_replaces_the_file()
    {
        Add(Id("WB-0001"), version: 11);
        var changing = Add(Id("WB-0002"), version: 12);
        var (runner, ledger, flow) = Runner(Flow());
        await RunAsync(runner);
        var reads = _platform.RecordReads;

        var unchanged = Assert.Single((await RunAsync(runner)).Downloads);
        Assert.Equal((2L, 0L, 0L, 0L), (unchanged.Listed, unchanged.Added, unchanged.Changed, unchanged.Written));
        Assert.Equal(reads, _platform.RecordReads);

        changing["version"] = 20;
        changing["data"]!["FacilityName"] = "WB renamed";
        Add(Id("WB-0003"), version: 30);
        var changed = Assert.Single((await RunAsync(runner)).Downloads);

        Assert.Equal((3L, 1L, 1L, 2L), (changed.Listed, changed.Added, changed.Changed, changed.Written));
        var records = await RecordsAsync(ledger, (await DownloadAsync(ledger, flow)).DownloadId);
        var second = records[Id("WB-0002")];
        Assert.Equal((20L, 20L), (second.Version, second.WrittenVersion));
        Assert.Equal("WB renamed", JsonNode.Parse(await File.ReadAllBytesAsync(FileOf("records", second)))!["data"]!["FacilityName"]!.GetValue<string>());
        Assert.Equal(changed.DownloadRunId, second.WrittenRunId);
        Assert.NotEqual(changed.DownloadRunId, records[Id("WB-0001")].WrittenRunId);
    }

    [Fact]
    public async Task An_id_OSDU_no_longer_serves_is_marked_gone_and_keeps_its_file_and_one_served_again_returns()
    {
        Add(Id("WB-0001"));
        var leaving = Add(Id("WB-0002"));
        var (runner, ledger, flow) = Runner(Flow());
        await RunAsync(runner);
        var downloadId = (await DownloadAsync(ledger, flow)).DownloadId;

        _search.Records.Remove(leaving);
        var gone = Assert.Single((await RunAsync(runner)).Downloads);

        Assert.Equal((1L, 1L, 0L), (gone.Listed, gone.Gone, gone.Written));
        var record = (await RecordsAsync(ledger, downloadId))[Id("WB-0002")];
        Assert.Equal(DownloadRecordStates.Gone, record.State);
        Assert.NotNull(record.GoneUtc);
        Assert.True(File.Exists(FileOf("records", record)));
        Assert.Equal([Id("WB-0002")], (await RecordsAsync(ledger, downloadId, DownloadRecordStates.Gone)).Keys);
        Assert.Equal(1, (await ledger.DownloadCountsAsync(Partition, downloadId)).Gone);

        _search.Records.Add(leaving);
        var back = Assert.Single((await RunAsync(runner)).Downloads);

        Assert.Equal((2L, 1L, 0L, 0L), (back.Listed, back.Returned, back.Gone, back.Written));
        Assert.Equal(DownloadRecordStates.Written, (await RecordsAsync(ledger, downloadId))[Id("WB-0002")].State);
    }

    [Fact]
    public async Task Ids_that_differ_only_in_case_never_share_a_file()
    {
        const string Units = "osdu:wks:reference-data--UnitOfMeasure:1.0.0";
        Add("dev:reference-data--UnitOfMeasure:ft", kind: Units, name: "foot");
        Add("dev:reference-data--UnitOfMeasure:fT", kind: Units, name: "femtotesla");
        var (runner, ledger, flow) = Runner(Flow(kind: Units));

        var summary = Assert.Single((await RunAsync(runner)).Downloads);

        Assert.Equal(2, summary.Written);
        var records = await RecordsAsync(ledger, (await DownloadAsync(ledger, flow)).DownloadId);
        var foot = records["dev:reference-data--UnitOfMeasure:ft"];
        var femtotesla = records["dev:reference-data--UnitOfMeasure:fT"];

        // The id that sorts first takes its own name; the other takes the name its hash makes its own.
        Assert.Equal(DownloadFileNames.Of("dev:reference-data--UnitOfMeasure:fT"), femtotesla.FileName);
        Assert.Equal(DownloadFileNames.Distinct("dev:reference-data--UnitOfMeasure:ft"), foot.FileName);
        Assert.Equal(2, Directory.GetFiles(summary.Location!).Length);
        Assert.Equal("foot", JsonNode.Parse(await File.ReadAllBytesAsync(FileOf("records", foot)))!["data"]!["FacilityName"]!.GetValue<string>());
        Assert.Equal("femtotesla", JsonNode.Parse(await File.ReadAllBytesAsync(FileOf("records", femtotesla)))!["data"]!["FacilityName"]!.GetValue<string>());

        // The search of the records finds either by the spelling a person knows, whatever its case.
        var found = await ledger.ListDownloadRecordsAsync(Partition, foot.DownloadId, null, "UNITOFMEASURE~FT", null, 10);
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public async Task A_record_storage_does_not_return_is_recorded_with_why_and_written_by_a_later_run()
    {
        Add(Id("WB-0001"));
        Add(Id("WB-0002"));
        _platform.DeletedAfterListing.Add(Id("WB-0002"));
        var (runner, ledger, flow) = Runner(Flow());

        var first = Assert.Single((await RunAsync(runner)).Downloads);

        Assert.Equal((DownloadRunStatus.Completed, 1L, 1L), (first.Status, first.Written, first.Missing));
        var downloadId = (await DownloadAsync(ledger, flow)).DownloadId;
        var missing = (await RecordsAsync(ledger, downloadId))[Id("WB-0002")];
        Assert.Equal((DownloadRecordStates.Problem, (long?)null), (missing.State, missing.WrittenVersion));
        Assert.StartsWith("storage named it under invalidRecords", missing.Problem, StringComparison.Ordinal);
        Assert.False(File.Exists(FileOf("records", missing)));
        Assert.Equal(1, (await ledger.DownloadCountsAsync(Partition, downloadId)).Problem);

        _platform.DeletedAfterListing.Clear();
        var second = Assert.Single((await RunAsync(runner)).Downloads);

        Assert.Equal((1L, 0L), (second.Written, second.Missing));
        var written = (await RecordsAsync(ledger, downloadId))[Id("WB-0002")];
        Assert.Equal((DownloadRecordStates.Written, (string?)null), (written.State, written.Problem));
        Assert.True(File.Exists(FileOf("records", written)));
    }

    [Fact]
    public async Task A_forced_run_or_a_run_writing_to_another_folder_writes_every_record_again()
    {
        Add(Id("WB-0001"));
        Add(Id("WB-0002"));
        var (runner, ledger, flow) = Runner(Flow());
        await RunAsync(runner);

        var forced = Assert.Single((await RunAsync(runner, force: true)).Downloads);
        Assert.Equal((DownloadRewrite.Forced, 2L), (forced.Rewrite, forced.Written));

        var (moved, _, _) = Runner(Flow(location: "elsewhere"));
        var summary = Assert.Single((await RunAsync(moved)).Downloads);

        Assert.Equal((DownloadRewrite.Moved, 2L), (summary.Rewrite, summary.Written));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "elsewhere", "Wellbores")).Length);
        var download = await DownloadAsync(ledger, flow);
        Assert.Equal(Path.Combine(_root, "elsewhere", "Wellbores"), download.Location);
        var runs = await ledger.ListDownloadRunsAsync(Partition, download.DownloadId, 10);
        Assert.Equal([DownloadRewrite.Moved, DownloadRewrite.Forced, null], runs.Select(r => r.Rewrite));

        // Once there, the next run writes only what is new or changed again.
        Assert.Equal(0, Assert.Single((await RunAsync(moved)).Downloads).Written);
    }

    [Fact]
    public async Task A_listing_that_fails_part_way_changes_nothing_and_the_run_fails_saying_why()
    {
        for (var i = 0; i < 2500; i++)
        {
            Add(Id($"WB-{i:D5}"));
        }

        var (runner, ledger, flow) = Runner(Flow());
        await RunAsync(runner);
        var downloadId = (await DownloadAsync(ledger, flow)).DownloadId;

        var later = 0;
        _search.Fail = body => body is not null && body.Contains("\"cursor\"", StringComparison.Ordinal) && Interlocked.Increment(ref later) > 1
            ? HttpStatusCode.ServiceUnavailable
            : null;
        _search.Records.RemoveAt(0);
        _clock.Advance(TimeSpan.FromHours(1));

        var failed = await Assert.ThrowsAsync<DownloadRunsFailedException>(() => RunAsync(runner));

        var summary = Assert.Single(failed.Outcome.Downloads);
        Assert.Equal((DownloadRunStatus.Failed, 0, 1), (summary.Status, failed.Outcome.Completed, failed.Outcome.Failed));
        Assert.False(string.IsNullOrWhiteSpace(summary.Error));
        Assert.Empty(await RecordsAsync(ledger, downloadId, DownloadRecordStates.Gone));
        var run = (await ledger.ListDownloadRunsAsync(Partition, downloadId, 5))[0];
        Assert.Equal((DownloadRunStatus.Failed, summary.Error), (run.Status, run.Error));
        await using var db = _db.CreateDbContext();
        Assert.Equal(0, await db.DeliveryDownloadScans.CountAsync());
    }

    [Fact]
    public async Task A_storage_read_lists_every_record_of_the_kind_from_storage_and_writes_each()
    {
        Add(Id("WB-0001"));
        Add(Id("WB-0002"));
        var (runner, _, _) = Runner(Flow(read: "storage"));

        var summary = Assert.Single((await RunAsync(runner)).Downloads);

        Assert.Equal((2L, 2L), (summary.Listed, summary.Written));
        Assert.True(_platform.Listings > 0);
    }

    [Fact]
    public async Task A_plan_counts_each_download_and_names_its_folder_reading_no_record_and_writing_nothing()
    {
        Add(Id("WB-0001"));
        Add(Id("WB-0002"));
        var (runner, ledger, flow) = Runner(Flow());

        var plan = await runner.PlanAsync([], CancellationToken.None);

        var download = Assert.Single(plan.Downloads);
        Assert.Equal(("Wellbores", 2L, "search", Path.Combine(_root, "records", "Wellbores")), (download.Download, download.Records, download.Read, download.Location));
        Assert.Empty(download.Problems);
        Assert.Equal(0, _platform.RecordReads);
        Assert.False(Directory.Exists(_root));
        Assert.Empty(await ledger.ListDownloadsAsync(flow.LedgerId));
    }
}
