using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The read model of sources and interfaces (osdu/docs/reference/flow/interfaces.md, The interface read model): the
/// repository sync describes every interface of every delivery flow with the ledger identity it keeps, the route it
/// goes by and the kind its mapping fills; an interface the repository no longer declares stays findable as inactive;
/// a ledger kept by two flows is reported, and so is a flow renamed while the ledger of its old name holds its claims.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class InterfaceCatalogSyncTests : IDisposable
{
    private readonly OsduTestDatabase _module = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-interface-sync-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _repo = Guid.NewGuid();

    public void Dispose()
    {
        _module.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private const string Source = """
        flowType: delivery
        name: petrel
        source:
          connection: ${env:PETREL_DB}
          work: ../.work
        target:
          endpoint: ${env:OSDU_URL}
          headers: { data-partition-id: dev }
        interfaces:
          wellbores:
            record: { object: Petrel.ing.Wellbore, key: [facility_name] }
            datasets:
              aliases: { object: Petrel.ing.WellboreAlias, join: { facility_name: facility_name }, orderBy: [alias_name] }
            mapping: Wellbore@1.0.0
          logs:
            ledger: welldb-welllog-03-header-delivery
            record: { object: Petrel.ing.WellLog, key: [uwi, log_id] }
            files: { root: ../data/logs, locationColumn: log_folder, hashColumn: log_hash }
            mapping: WellLog@1.4.0
            after: [wellbores]
        """;

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task<(IReadOnlyList<string> Warnings, SqlFlow.Catalog.CatalogSyncExtensionResult Result)> SyncAsync(DateTime? now = null)
    {
        var warnings = new List<string>();
        await using var db = _module.CreateDbContext();
        var result = await new DeliveryCatalogSync(new DeliveryDocumentLoader()).ReconcileAsync(
            db, _repo, _root, now ?? new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc), warnings, CancellationToken.None);
        return (warnings, result);
    }

    private async Task<List<Data.DeliveryInterface>> RowsAsync()
    {
        await using var db = _module.CreateDbContext();
        return await db.DeliveryInterfaces.AsNoTracking().OrderBy(i => i.FlowName).ThenBy(i => i.Ordinal).ToListAsync();
    }

    [Fact]
    public async Task Every_interface_of_every_delivery_flow_is_described_with_the_ledger_it_keeps()
    {
        Write("flows/petrel.yaml", Source);
        Write("flows/wells-wellbore-03-header-delivery.yaml", File.ReadAllText(Samples.WellboreFlowFile));
        Write("mappings/Wellbore@1.0.0.yaml", File.ReadAllText(Path.Combine(Samples.FixtureMappings, "Wellbore@1.0.0.yaml")));
        Write("templates/osdu_wks_master-data--Wellbore_1.3.0.json", File.ReadAllText(Path.Combine(Samples.TemplateFiles, "osdu_wks_master-data--Wellbore_1.3.0.json")));

        var (warnings, result) = await SyncAsync();

        Assert.Empty(warnings);
        var rows = await RowsAsync();
        Assert.Equal(3, rows.Count);
        var wellbores = rows.Single(r => r.FlowName == "petrel" && r.Interface == "wellbores");
        Assert.Equal((0, "storage", FlowId.Of("petrel/wellbores"), "petrel/wellbores"), (wellbores.Ordinal, wellbores.Route, wellbores.LedgerFlowId, wellbores.LedgerName));
        Assert.Equal(Samples.WellboreKind, wellbores.Kind);
        Assert.Equal("Petrel.ing.Wellbore", wellbores.RecordObject);
        Assert.Equal("flows/petrel.yaml", wellbores.RelativePath);
        Assert.Contains("storage service", wellbores.RouteReason, StringComparison.Ordinal);
        Assert.True(wellbores.Active);

        var logs = rows.Single(r => r.Interface == "logs");
        Assert.Equal((1, "file", FlowId.Of("welldb-welllog-03-header-delivery"), "welldb-welllog-03-header-delivery"), (logs.Ordinal, logs.Route, logs.LedgerFlowId, logs.LedgerName));
        // The repository holds no WellLog mapping, so its kind is not known yet.
        Assert.Equal(string.Empty, logs.Kind);
        Assert.Equal(["wellbores"], JsonSerializer.Deserialize<string[]>(logs.AfterJson)!);

        // A flow in the single form is one row with no interface name, keeping the ledger of its own name.
        var single = rows.Single(r => r.FlowName == "wells-wellbore-03-header-delivery");
        Assert.Equal((string.Empty, FlowId.Of("wells-wellbore-03-header-delivery"), "storage"), (single.Interface, single.LedgerFlowId, single.Route));
        Assert.Null(single.RouteReason);
        Assert.True(result.Added >= 3);

        // Syncing the same tree again changes nothing.
        var (_, again) = await SyncAsync(new DateTime(2026, 9, 16, 13, 0, 0, DateTimeKind.Utc));
        Assert.Equal(0, again.Added);
        Assert.Equal(0, again.Updated);
        Assert.True((await RowsAsync()).All(r => r.LastSeenUtc == new DateTime(2026, 9, 16, 13, 0, 0, DateTimeKind.Utc)));

        await using var db = _module.CreateDbContext();
        var keeping = await DeliveryInterfaceCatalog.KeepingAsync(db, FlowId.Of("welldb-welllog-03-header-delivery"), CancellationToken.None);
        Assert.Equal(new InterfaceLocation(_repo, "petrel", "logs", FlowId.Of("welldb-welllog-03-header-delivery"), "welldb-welllog-03-header-delivery", true), Assert.Single(keeping));
        Assert.Equal(["wellbores", "logs"], (await DeliveryInterfaceCatalog.OfFlowAsync(db, _repo, "petrel", null, CancellationToken.None)).Select(i => i.Interface));
    }

    [Fact]
    public async Task An_interface_records_the_cache_version_it_renders_against_and_a_pin_the_retention_pruned_is_reported()
    {
        const string Pinned = "20260901T060000Z";
        Write("flows/wells-wellbore-03-header-delivery.yaml", File.ReadAllText(Samples.WellboreFlowFile));
        Write("mappings/Wellbore@1.0.0.yaml", File.ReadAllText(Path.Combine(Samples.FixtureMappings, "Wellbore@1.0.0.yaml")));
        Write("templates/osdu_wks_master-data--Wellbore_1.3.0.json", File.ReadAllText(Path.Combine(Samples.TemplateFiles, "osdu_wks_master-data--Wellbore_1.3.0.json")));
        Write("flows/welldb-welllog-03-header-delivery.yaml", File.ReadAllText(Samples.Flow)
            .Replace("mapping: WellLog@1.4.0", $"mapping: WellLog@1.4.0\n  cacheVersion: {Pinned}", StringComparison.Ordinal));

        var (warnings, _) = await SyncAsync();
        Assert.Empty(warnings);
        var rows = await RowsAsync();
        // What the cache's retention keeps for each: the current version, which it always keeps, or the version pinned.
        Assert.Equal(Model.FlowRender.CurrentCacheVersion, rows.Single(r => r.FlowName == "wells-wellbore-03-header-delivery").CacheVersion);
        Assert.Equal(Pinned, rows.Single(r => r.FlowName == "welldb-welllog-03-header-delivery").CacheVersion);

        // The version is in the cache, and its records are pruned: the next sync says the flow's runs fail, and what to do.
        await using (var db = _module.CreateDbContext())
        {
            var captured = new DateTime(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc);
            db.DeliveryCacheVersions.Add(new Data.DeliveryCacheVersion
            {
                Id = Guid.NewGuid(),
                Scope = "dev",
                FlowName = "welldb-osdu-00-reference-cache",
                Version = Pinned,
                Sequence = 1,
                CapturedUtc = captured,
                ContentHash = new string('0', 64),
                CapturedBy = "tests",
                Origin = "seeded",
                TypesJson = "[]",
                PrunedUtc = captured.AddDays(30),
            });
            await db.SaveChangesAsync();
        }

        var (pruned, _) = await SyncAsync();
        var warning = Assert.Single(pruned);
        Assert.Contains("flows/welldb-welllog-03-header-delivery.yaml: flow 'welldb-welllog-03-header-delivery' pins version 20260901T060000Z of the cache", warning, StringComparison.Ordinal);
        Assert.Contains("the cache's retention has pruned that version's records, so its runs fail", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_interface_the_repository_no_longer_declares_stays_findable_as_inactive()
    {
        Write("flows/petrel.yaml", Source);
        await SyncAsync();

        // The logs interface goes, then the whole document.
        Write("flows/petrel.yaml", Source[..Source.IndexOf("  logs:", StringComparison.Ordinal)]);
        var (_, removedOne) = await SyncAsync();
        Assert.Equal(1, removedOne.Removed);
        var rows = await RowsAsync();
        Assert.True(rows.Single(r => r.Interface == "wellbores").Active);
        Assert.False(rows.Single(r => r.Interface == "logs").Active);

        await using (var db = _module.CreateDbContext())
        {
            // Its records still lead to their flow, and the flow's own listing leaves it out.
            var keeping = Assert.Single(await DeliveryInterfaceCatalog.KeepingAsync(db, FlowId.Of("welldb-welllog-03-header-delivery"), CancellationToken.None));
            Assert.False(keeping.Active);
            Assert.Equal(["wellbores"], (await DeliveryInterfaceCatalog.OfFlowAsync(db, _repo, "petrel", null, CancellationToken.None)).Select(i => i.Interface));
        }

        File.Delete(Path.Combine(_root, "flows", "petrel.yaml"));
        await SyncAsync();
        Assert.All(await RowsAsync(), r => Assert.False(r.Active));
        await using (var db = _module.CreateDbContext())
        {
            Assert.Contains("petrel", await DeliveryInterfaceCatalog.DescribedFlowsAsync(db, _repo, CancellationToken.None));
        }

        // Declared again, it is active again.
        Write("flows/petrel.yaml", Source);
        await SyncAsync();
        Assert.All(await RowsAsync(), r => Assert.True(r.Active));
    }

    [Fact]
    public async Task A_ledger_kept_by_two_flows_is_reported_and_a_document_that_does_not_parse_describes_nothing()
    {
        Write("flows/petrel.yaml", Source);
        Write("flows/welldb-welllog-03-header-delivery.yaml", File.ReadAllText(Samples.Flow));
        Write("flows/broken.yaml", "flowType: delivery\nname: broken\n");
        Write("flows/zz-again.yaml", Source.Replace("wellbores:", "cores:", StringComparison.Ordinal).Replace("after: [wellbores]", "after: [cores]", StringComparison.Ordinal));

        var (warnings, result) = await SyncAsync();

        Assert.Contains(warnings, w => w.StartsWith("flows/zz-again.yaml: delivery flow 'petrel' is already declared by flows/petrel.yaml", StringComparison.Ordinal));
        var shared = Assert.Single(warnings, w => w.Contains("the ledger 'welldb-welllog-03-header-delivery' is kept by", StringComparison.Ordinal));
        Assert.Contains("interface 'logs' of flow 'petrel'", shared, StringComparison.Ordinal);
        Assert.Contains("flow 'welldb-welllog-03-header-delivery'", shared, StringComparison.Ordinal);
        Assert.Equal(1, result.Invalid);
        var rows = await RowsAsync();
        Assert.DoesNotContain(rows, r => r.FlowName == "broken");
        Assert.DoesNotContain(rows, r => r.Interface == "cores");
    }

    private const string OldName = "welldb-welllog-03-header-delivery";
    private const string NewName = "welldb-welllog-03-v140-header-delivery";
    private const string OldFile = "flows/welldb-welllog-03-header-delivery.yaml";
    private const string NewFile = "flows/welldb-welllog-03-v140-header-delivery.yaml";

    /// <summary>The sample delivery flow under <paramref name="name"/>.</summary>
    private static string Flow(string name) => File.ReadAllText(Samples.Flow).ReplaceLineEndings("\n")
        .Replace($"name: {OldName}\n", $"name: {name}\n", StringComparison.Ordinal);

    /// <summary>The flow synced under its old name, with a record its runs delivered: the OSDU id it claimed.</summary>
    private async Task<OsduLedger> DeliveredAsync(string name = OldName, string file = OldFile, string key = "a")
    {
        Write(file, Flow(name));
        Assert.Empty((await SyncAsync()).Warnings);
        var ledger = _module.Ledger(new TestClock());
        await ledger.RegisterAsync(FlowId.Of(name), "dev", name);
        await ledger.UpsertPendingAsync(FlowId.Of(name), [Claiming(FlowId.Of(name), key)]);
        return ledger;
    }

    private void Delete(string relative) => File.Delete(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private async Task<List<Data.DeliveryActivity>> MovesAsync()
    {
        await using var db = _module.CreateDbContext();
        return await db.DeliveryActivities.AsNoTracking().Where(a => a.Kind == LedgerMoves.Kind).OrderBy(a => a.ActivityId).ToListAsync();
    }

    [Fact]
    public async Task A_pipeline_renamed_with_its_file_has_its_ledger_moved_right_after_the_sync()
    {
        var ledger = await DeliveredAsync();

        // Renamed, and its file with it: the sync pairs the two by what they read and asks for the move, without a word.
        Delete(OldFile);
        Write(NewFile, Flow(NewName));
        Assert.Empty((await SyncAsync()).Warnings);
        var asked = Assert.Single(await MovesAsync());
        Assert.Equal((FlowId.Of(NewName), LedgerMoves.Running, LedgerMoves.Actor), (asked.FlowId, asked.Outcome, asked.Actor));
        Assert.Equal("dev", (await ledger.GetLedgerAsync(FlowId.Of(NewName)))!.Partition);

        // The control plane's pass moves it: the record is the renamed pipeline's, and the old ledger is gone.
        var moved = Assert.Single(await ledger.CompleteLedgerMovesAsync(null));
        Assert.Equal(("completed", 1L), (moved.Outcome, moved.Records));
        Assert.Equal("dev:work-product-component--WellLog:a", Assert.Single(await ledger.ListAsync(FlowId.Of(NewName), new RecordQuery())).ClaimedTargetId);
        Assert.Null(await ledger.GetLedgerAsync(FlowId.Of(OldName)));

        // Later syncs find nothing more to move.
        Assert.Empty((await SyncAsync()).Warnings);
        Assert.Single(await MovesAsync());
        Assert.Empty(await ledger.CompleteLedgerMovesAsync(null));
    }

    [Fact]
    public async Task A_file_copied_by_mistake_takes_the_ledger_and_deleting_the_copy_gives_it_back()
    {
        var ledger = await DeliveredAsync();

        // A copy of the document under another name: the copy, found last, owns the ledger, and the sync says so.
        Write("flows/copy.yaml", Flow("welldb-welllog-copy"));
        var warning = Assert.Single((await SyncAsync(new DateTime(2026, 9, 16, 13, 0, 0, DateTimeKind.Utc))).Warnings);
        Assert.Equal(
            $"flows/copy.yaml, {OldFile}: '{OldName}', 'welldb-welllog-copy' are one document under 2 names, so 'welldb-welllog-copy', the one found last, keeps its ledger, "
            + "and a run of any other holds every record whose id that ledger claimed. Delete the copy that is a mistake; its ledger then goes back to the one that stays.",
            warning);
        var copied = Assert.Single(await ledger.CompleteLedgerMovesAsync(null));
        Assert.Equal(("completed", 1L, FlowId.Of("welldb-welllog-copy")), (copied.Outcome, copied.Records, copied.To));
        Assert.Single(await ledger.ListAsync(FlowId.Of("welldb-welllog-copy"), new RecordQuery()));

        // The copy goes: the ledger goes back to the document's one name.
        Delete("flows/copy.yaml");
        Assert.Empty((await SyncAsync(new DateTime(2026, 9, 16, 14, 0, 0, DateTimeKind.Utc))).Warnings);
        var back = Assert.Single(await ledger.CompleteLedgerMovesAsync(null));
        Assert.Equal(("completed", 1L, FlowId.Of(OldName)), (back.Outcome, back.Records, back.To));
        Assert.Equal("dev:work-product-component--WellLog:a", Assert.Single(await ledger.ListAsync(FlowId.Of(OldName), new RecordQuery())).ClaimedTargetId);
        Assert.Equal(2, (await MovesAsync()).Count);
    }

    [Fact]
    public async Task A_document_that_does_not_parse_or_differs_beyond_its_name_keeps_its_ledger()
    {
        await DeliveredAsync();

        // Renamed and edited: another document, so the old ledger stays where it is.
        Delete(OldFile);
        Write(NewFile, Flow(NewName).Replace("flowType: delivery\n", "flowType: delivery\ndescription: The Recall well logs, renamed.\n", StringComparison.Ordinal));
        Assert.Empty((await SyncAsync(new DateTime(2026, 9, 16, 13, 0, 0, DateTimeKind.Utc))).Warnings);
        Assert.Empty(await MovesAsync());

        // The old document comes back unreadable beside a copy of it under another name: the old pipeline only looks gone,
        // so its ledger stays where it is.
        Write(OldFile, Flow(OldName).Replace("source:\n", "sauce:\n", StringComparison.Ordinal));
        Write("flows/copy.yaml", Flow("welldb-welllog-copy"));
        var (warnings, result) = await SyncAsync(new DateTime(2026, 9, 16, 14, 0, 0, DateTimeKind.Utc));
        Assert.Equal(1, result.Invalid);
        Assert.Empty(warnings);
        Assert.Empty(await MovesAsync());
    }

    [Fact]
    public async Task A_pipeline_renamed_with_its_mapping_edited_is_another_pipeline_and_keeps_its_ledger_apart()
    {
        // The flow with its mapping and template, delivered under its old name.
        const string Mapping = "mappings/WellLog@1.4.0.yaml";
        var mapping = File.ReadAllText(Path.Combine(Samples.Mappings, "WellLog@1.4.0.yaml"));
        Write(Mapping, mapping);
        Write("templates/osdu_wks_work-product-component--WellLog_1.4.0.json", File.ReadAllText(Path.Combine(Samples.TemplateFiles, "osdu_wks_work-product-component--WellLog_1.4.0.json")));
        Write(OldFile, Flow(OldName));
        await SyncAsync();
        var ledger = _module.Ledger(new TestClock());
        await ledger.RegisterAsync(FlowId.Of(OldName), "dev", OldName);
        await ledger.UpsertPendingAsync(FlowId.Of(OldName), [Claiming(FlowId.Of(OldName), "a")]);

        // Renamed, and the mapping it pins edited in the same commit: what renders it changed, so it is another pipeline.
        Delete(OldFile);
        Write(NewFile, Flow(NewName));
        Write(Mapping, mapping + "\n# Edited with the rename.\n");
        await SyncAsync(new DateTime(2026, 9, 16, 13, 0, 0, DateTimeKind.Utc));
        Assert.Empty(await MovesAsync());

        // The mapping put back as it was: the same pipeline under its new name, which takes the ledger.
        Write(Mapping, mapping);
        await SyncAsync(new DateTime(2026, 9, 16, 14, 0, 0, DateTimeKind.Utc));
        Assert.Equal(FlowId.Of(NewName), Assert.Single(await MovesAsync()).FlowId);
    }

    /// <summary>Work for a record of <paramref name="flow"/> that queues a document, which claims the record's OSDU id.</summary>
    private static RecordState Claiming(Guid flow, string key) => new()
    {
        DeliveryKey = DeliveryKey.Derive("test", [key]),
        FlowId = flow,
        SourceKey = key,
        MappingName = "WellLog",
        TargetId = "dev:work-product-component--WellLog:" + key,
        LastSubmissionId = Guid.NewGuid(),
        PendingDocumentRef = "0:0:10",
        PendingRenderContext = "{}",
        PendingSourceFingerprint = "fp",
        PendingMetadataHash = "mh",
        PendingMetadata = true,
    };
}
