using System.Reflection;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A flow's records, and the records a lookup across flows reached, as the workbook the Records tab, the Records page, the
/// API and <c>sqlflow records export</c> give (osdu/docs/reference/cli/records.md, records export): a row a record with what
/// blocks it and where it came from, sorted by record, the key's parts in the flow's own columns, and an About sheet saying
/// what was exported. A filter matching more than the listing counts is refused before anything is read past the count.
/// </summary>
public sealed class RecordExportTests
{
    private const string Partition = "dev";
    private static readonly DateTime Exported = new(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Delivered = new(2026, 10, 25, 14, 5, 9, DateTimeKind.Utc);

    /// <summary>A flow's interface whose records are keyed by two columns, as a well log's are by its well and its wellbore.</summary>
    private static readonly FlowDefinition Flow = Keyed(Samples.Targeting(new FlowTarget { Endpoint = "http://localhost:9", Protocol = DeliveryProtocol.Storage }, "wellbores"));

    /// <summary>The ledger directory's row of <see cref="Flow"/>.</summary>
    private static readonly LedgerEntry Kept = new()
    {
        FlowId = Flow.Id, Kind = LedgerKinds.Delivery, FlowName = Flow.Name, Interface = "wellbores", LedgerName = "targeting/wellbores", Partition = Partition,
    };

    [Fact]
    public async Task A_flow_s_records_are_written_by_record_with_their_key_s_parts_what_blocks_them_and_where_they_landed()
    {
        var held = Record("NO 34/10-A-48", ["NO 34/10-A-48", "A-48"], "B")
            with
            {
                Status = RecordStatus.Held,
                Blocked = true,
                ProblemHash = 0x1f2e3d4c5b6a7988,
                LastError = "assertion: data.VerticalMeasurement fails \"reference-elevation-range\" with '822'",
                PendingSourceFileName = "stat_comp_welllog_20261023103331.csv",
                PendingSourceRowNumber = 2235,
                SourceFileName = "older.csv",
                SourceRowNumber = 1,
                AttemptCount = 1,
                PendingDocumentRef = "1:0:10",
                Identities = ["NO 34/10-A-48", "22004"],
            };
        var delivered = Record("no 31/2-U-11", ["NO 31/2-U-11", "U-11"], "A")
            with
            {
                Status = RecordStatus.Delivered,
                TargetId = "dev:work-product-component--WellLog:abc",
                TargetVersion = 1_729_012_345_678_901,
                LastDeliveredUtc = Delivered,
                LastVerifyOutcome = VerifyOutcome.Match,
                SourceFileName = "stat_comp_welllog_20261023103331.csv",
                SourceRowNumber = 1546,
            };
        var unlabelled = Record(null, ["NO 34/10-32 A", "32 A"], "C") with { Status = RecordStatus.Pending };
        var ledger = RecordExportLedger.Of(Kept,[held, delivered, unlabelled]);

        var bytes = await WriteAsync(ledger, new RecordQuery { Search = "NO", Mode = SearchMode.Contains });

        using var read = new WorkbookReading(bytes);
        Assert.Equal([RecordExport.RecordsSheet, RecordExport.AboutSheet], read.SheetNames);
        Assert.Equal(
            [
                "Record", "Source key", "well", "wellbore", "Status", "Blocked", "Issue", "Problem", "Source file", "Source row", "Identities",
                "Attempts", "Next attempt (UTC)", "Waiting for", "Document waiting", "Delivered (UTC)", "Verified (UTC)", "Verify", "OSDU id",
                "OSDU version", "Mapping", "Updated (UTC)", "Delivery key",
            ],
            read.Headers());

        // By record, whatever its case; a record its mapping gives no label is named by its source key.
        var rows = read.Rows();
        Assert.Equal(["no 31/2-U-11", "NO 34/10-A-48", "welldb:C"], rows.Select(r => r["Record"].GetText()));

        var blocked = rows[1];
        Assert.Equal(("held", "yes", "1f2e3d4c5b6a7988"), (blocked["Status"].GetText(), blocked["Blocked"].GetText(), blocked["Issue"].GetText()));
        Assert.Equal(held.LastError, blocked["Problem"].GetText());
        Assert.Equal(("NO 34/10-A-48", "A-48"), (blocked["well"].GetText(), blocked["wellbore"].GetText()));

        // A held record names the file and row it was left at, the queued version's, as the Records tab does.
        Assert.Equal(("stat_comp_welllog_20261023103331.csv", 2235d), (blocked["Source file"].GetText(), blocked["Source row"].GetNumber()));
        Assert.Equal(("NO 34/10-A-48; 22004", 1d, "yes"), (blocked["Identities"].GetText(), blocked["Attempts"].GetNumber(), blocked["Document waiting"].GetText()));
        Assert.True(blocked["OSDU id"].IsBlank);
        Assert.Equal(held.DeliveryKey.Value.ToString("D"), blocked["Delivery key"].GetText());

        // Where a delivered record landed: its OSDU version, sixteen digits, is text a spreadsheet keeps whole.
        var landed = rows[0];
        Assert.True(landed["Blocked"].IsBlank);
        Assert.Equal("1729012345678901", landed["OSDU version"].GetText());
        Assert.Equal(Delivered, DateTime.SpecifyKind(landed["Delivered (UTC)"].GetDateTime(), DateTimeKind.Utc));
        Assert.Equal(("match", "WellLog"), (landed["Verify"].GetText(), landed["Mapping"].GetText()));
        Assert.Equal(("stat_comp_welllog_20261023103331.csv", 1546d), (landed["Source file"].GetText(), landed["Source row"].GetNumber()));

        var about = read.About();
        Assert.Equal(Flow.Name, about["Flow"].GetText());
        Assert.Equal("wellbores", about["Interface"].GetText());
        Assert.Equal("targeting/wellbores", about["Ledger"].GetText());
        Assert.Equal(Partition, about["Partition"].GetText());
        Assert.Equal("'NO' anywhere", about["Filter"].GetText());
        Assert.Equal(3d, about["Records"].GetNumber());
        Assert.Equal(Exported, DateTime.SpecifyKind(about["Exported (UTC)"].GetDateTime(), DateTimeKind.Utc));
        Assert.Contains("well, wellbore", read.Notes().Keys);

        // The keys are read once, then the records twice, a page of keys at a time: once to sort them, once to write them.
        Assert.Equal(["CountAsync", "ListKeysAsync", "GetRecordsAsync", "GetLedgerAsync", "GetRecordsAsync"], ledger.Calls);
    }

    [Fact]
    public async Task A_record_taken_out_of_the_ledger_while_the_file_is_written_is_left_out()
    {
        var kept = Record("kept", ["W1", "1"], "1");
        var gone = Record("gone", ["W2", "2"], "2");
        var ledger = RecordExportLedger.Of(Kept,[kept, gone]);
        ledger.GoneAfterSort.Add(gone.DeliveryKey);

        using var read = new WorkbookReading(await WriteAsync(ledger, new RecordQuery()));
        Assert.Equal(["kept"], read.Rows().Select(r => r["Record"].GetText()));
        Assert.Equal(1d, read.About()["Records"].GetNumber());
        Assert.Equal("none: every record of the ledger", read.About()["Filter"].GetText());
    }

    [Fact]
    public async Task A_filter_that_matches_nothing_is_a_workbook_of_its_header_row()
    {
        using var read = new WorkbookReading(await WriteAsync(RecordExportLedger.Of(Kept,[]), new RecordQuery { Status = RecordStatus.Failed }));
        Assert.Empty(read.Rows());
        Assert.Equal(0d, read.About()["Records"].GetNumber());
        Assert.Equal("status failed", read.About()["Filter"].GetText());
    }

    [Theory]
    [InlineData(RecordListing.CountLimit + 1, false)]
    [InlineData(RecordListing.CountLimit + 1, true)]
    [InlineData(120, false)]
    public async Task A_filter_matching_more_than_the_listing_counts_is_refused_before_anything_past_the_count_is_read(int count, bool exact)
    {
        var ledger = RecordExportLedger.Of(Kept,[]);
        ledger.Matched = new BoundedCount(count, exact);
        using var output = new MemoryStream();

        var refused = await Assert.ThrowsAsync<RecordQueryTooBroadException>(
            () => RecordExport.WriteAsync(ledger.Ledger, Flow, new RecordQuery { Search = "NO" }, Exported, output, CancellationToken.None));

        Assert.Contains($"an export writes at most {RecordListing.CountLimit} at once", refused.Message, StringComparison.Ordinal);
        Assert.Equal(["CountAsync"], ledger.Calls);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task A_record_whose_stored_key_is_not_a_key_names_the_record()
    {
        var broken = Record("broken", ["W1", "1"], "1") with { SourceKeyJson = "{not an array" };
        using var output = new MemoryStream();

        var failed = await Assert.ThrowsAsync<DeliveryException>(
            () => RecordExport.WriteAsync(RecordExportLedger.Of(Kept,[broken]).Ledger, Flow, new RecordQuery(), Exported, output, CancellationToken.None));

        Assert.Contains(broken.DeliveryKey.Value.ToString("D"), failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lookup_s_records_are_written_by_flow_then_record_each_naming_its_flow_and_what_its_term_matched()
    {
        var wellbore = Record("WB-2", ["WB-2"], "2", flowId: Guid.NewGuid());
        var log = Record("WB-1 / GR", ["WB-1", "GR"], "1", flowId: Guid.NewGuid());
        var orphan = Record("WB-3", ["WB-3"], "3", flowId: Guid.NewGuid());
        RecordExportHit[] hits =
        [
            new(orphan, null, null, null, ["WB-3"]),
            new(wellbore, "welldb-wellbore-03-delivery", null, "dev", ["WB-2"]),
            new(log, "welldb-welllog-03-delivery", "welllogs", "dev", ["WB-1", "WB-1 / GR"]),
        ];
        var lookup = new RecordLookupExport("WB-", RecordStatus.Held, null, "dev", More: true);
        using var output = new MemoryStream();

        Assert.Equal(3, RecordExport.WriteLookup(lookup, hits, Exported, output));

        using var read = new WorkbookReading(output.ToArray());
        Assert.Equal(["Record", "Source key", "Flow", "Interface", "Partition", "Matched", "Status"], read.Headers().Take(7));
        var rows = read.Rows();
        Assert.Equal(
            [
                ("welldb-wellbore-03-delivery", "WB-2", "WB-2"),
                ("welldb-welllog-03-delivery", "WB-1 / GR", "WB-1; WB-1 / GR"),
                ($"no longer synced: {orphan.FlowId:D}", "WB-3", "WB-3"),
            ],
            rows.Select(r => (r["Flow"].GetText(), r["Record"].GetText(), r["Matched"].GetText())));
        Assert.Equal("welllogs", rows[1]["Interface"].GetText());
        Assert.Equal("dev", rows[2]["Partition"].GetText());

        var about = read.About();
        Assert.StartsWith("'WB-': the start of", about["Lookup"].GetText(), StringComparison.Ordinal);
        Assert.Equal(("held", "every flow", "dev"), (about["Status"].GetText(), about["Flow"].GetText(), about["Partition"].GetText()));
        Assert.Contains($"than the {RecordListing.LookupCandidateLimit} a lookup reaches", about["Reach"].GetText(), StringComparison.Ordinal);
        Assert.Contains("Flow, Interface, Partition", read.Notes().Keys);
    }

    [Fact]
    public void The_newest_records_of_a_lookup_name_no_term_and_no_matched_column()
    {
        using var output = new MemoryStream();
        RecordExport.WriteLookup(new RecordLookupExport(null, null, "welldb-wellbore-03-delivery", null, More: false), [], Exported, output);

        using var read = new WorkbookReading(output.ToArray());
        Assert.DoesNotContain("Matched", read.Headers());
        var about = read.About();
        Assert.StartsWith("none:", about["Lookup"].GetText(), StringComparison.Ordinal);
        Assert.Equal(("every status", "welldb-wellbore-03-delivery", "every partition"), (about["Status"].GetText(), about["Flow"].GetText(), about["Partition"].GetText()));
        Assert.False(about.ContainsKey("Reach"));
    }

    [Fact]
    public void A_filter_is_described_in_words()
    {
        var submission = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal(
            "status held; 'NO 34' at the start; drifted only; issue 00000000000000ff; last planned by submission 11111111-2222-3333-4444-555555555555; never delivered",
            RecordExport.Describe(new RecordQuery
            {
                Status = RecordStatus.Held, Search = "NO 34", Drifted = true, Problem = 0xff, SubmissionId = submission, EverDelivered = false,
            }));
    }

    [Fact]
    public void An_export_is_named_after_what_it_holds()
    {
        Assert.Equal("targeting-wellbores-records-held.xlsx", RecordExport.FileName(Flow, new RecordQuery { Status = RecordStatus.Held }));
        Assert.Equal("targeting-wellbores-dev-records.xlsx", RecordExport.FileName(Flow with { Partition = "dev" }, new RecordQuery()));
        Assert.Equal("records-dev-welldb-wellbore-03-delivery-failed.xlsx", RecordExport.FileName(new RecordLookupExport("x", RecordStatus.Failed, "welldb-wellbore-03-delivery", "dev", false)));
        Assert.Equal("records.xlsx", RecordExport.FileName(new RecordLookupExport(null, null, null, null, false)));
    }

    private static async Task<byte[]> WriteAsync(RecordExportLedger ledger, RecordQuery query)
    {
        using var output = new MemoryStream();
        var rows = await RecordExport.WriteAsync(ledger.Ledger, Flow, query, Exported, output, CancellationToken.None);
        var bytes = output.ToArray();
        using var read = new WorkbookReading(bytes);
        Assert.Equal(read.Rows().Count, rows);
        return bytes;
    }

    private static RecordState Record(string? label, string[] key, string sourceKey, Guid? flowId = null) => new()
    {
        DeliveryKey = new DeliveryKey(Guid.NewGuid()),
        FlowId = flowId ?? Flow.Id,
        Partition = Partition,
        SourceKey = "welldb:" + sourceKey,
        SourceKeyJson = System.Text.Json.JsonSerializer.Serialize(key),
        Label = label,
        MappingName = "WellLog",
        UpdatedUtc = Delivered,
    };

    private static FlowDefinition Keyed(FlowDefinition flow)
        => flow with { Source = flow.Source with { Record = flow.Source.Record with { Key = ["well", "wellbore"] } } };
}

/// <summary>
/// The four reads a flow's export makes of the ledger, answered from records held in memory; any other call fails the test,
/// naming it. <see cref="GoneAfterSort"/> takes records out after the first read of records, as a removal running while the
/// file is written would.
/// </summary>
public class RecordExportLedger : DispatchProxy
{
    private List<RecordState> records = [];
    private LedgerEntry? entry;
    private int reads;

    public BoundedCount? Matched { get; set; }

    public List<string> Calls { get; } = [];

    public HashSet<DeliveryKey> GoneAfterSort { get; } = [];

    public ILedger Ledger => (ILedger)(object)this;

    public static RecordExportLedger Of(LedgerEntry kept, IReadOnlyList<RecordState> held)
    {
        var proxy = (RecordExportLedger)(object)Create<ILedger, RecordExportLedger>();
        proxy.entry = kept;
        proxy.records = [.. held];
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        ArgumentNullException.ThrowIfNull(args);
        Calls.Add(targetMethod.Name);
        switch (targetMethod.Name)
        {
            case nameof(ILedger.CountAsync) when args[1] is RecordQuery:
                Assert.Equal(RecordListing.CountLimit + 1, (int)args[2]!);
                return Task.FromResult(Matched ?? new BoundedCount(records.Count, Exact: true));
            case nameof(ILedger.ListKeysAsync):
                Assert.Equal(RecordListing.CountLimit, (int)args[2]!);
                return Task.FromResult<IReadOnlyList<DeliveryKey>>([.. records.Select(r => r.DeliveryKey).OrderBy(k => k.Value)]);
            case nameof(ILedger.GetRecordsAsync):
                var keys = ((IEnumerable<DeliveryKey>)args[1]!).ToHashSet();
                var found = records.Where(r => keys.Contains(r.DeliveryKey) && (reads == 0 || !GoneAfterSort.Contains(r.DeliveryKey))).ToDictionary(r => r.DeliveryKey);
                reads++;
                return Task.FromResult<IReadOnlyDictionary<DeliveryKey, RecordState>>(found);
            case nameof(ILedger.GetLedgerAsync):
                return Task.FromResult(entry is not null && entry.FlowId == (Guid)args[0]! ? entry : null);
            default:
                throw new InvalidOperationException($"The export asked the ledger for {targetMethod.Name}, which it has no reason to read.");
        }
    }
}
