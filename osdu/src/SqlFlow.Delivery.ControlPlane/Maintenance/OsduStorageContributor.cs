using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.ControlPlane.Maintenance;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.ControlPlane.Maintenance;

/// <summary>
/// The <c>osdu</c> schema's storage areas on SQLFlow's Maintenance page (osdu/docs/reference/concepts/storage-and-retention.md):
/// every table of the module's model, and the tables dimension builds and duplicate checks create at run time, grouped as an
/// operator thinks of them, each with what it holds, what maintenance may do with it and why. The rule every verdict follows
/// is the product's: every delivered record stays reconstructible from the ledger alone. So the records, submissions,
/// activities, render contexts and every audit row are kept, work in progress and working sets clean themselves, and three
/// areas may be removed at a cost each states, never unless an admin sets how long they stay: the delivery attempts a later
/// attempt of the same record superseded, the free-text logs of finished activities, and the assertion runs whose every
/// result a later run superseded. Their cleanups are the ledger's own (<see cref="ILedger"/>), which keep each record's
/// latest attempt and each test's latest result whatever the days.
/// </summary>
public sealed class OsduStorageContributor : IStorageContributor
{
    /// <summary>The delivery attempts a later attempt of the same record superseded.</summary>
    public const string AttemptsArea = OsduSchema.Module + ".attempts";

    /// <summary>The captured logs of finished activities.</summary>
    public const string ActivityLogsArea = OsduSchema.Module + ".activity-logs";

    /// <summary>The assertion runs whose every result a later run superseded.</summary>
    public const string AssertionRunsArea = OsduSchema.Module + ".assertion-runs";

    private const string Schema = DeliveryModel.SchemaName;

    private const string LedgerGroup = "Delivery ledger";
    private const string Audit = "Activity and audit";
    private const string Assertions = "Assertions";
    private const string Cache = "Reference data cache";
    private const string Dimensions = "Dimensions";
    private const string Inventories = "Inventories";
    private const string Removals = "Removals and retrievals";
    private const string Setup = "Repositories and setup";

    private readonly IServiceProvider? _services;

    /// <summary>
    /// The contributor the control plane resolves per request or pass. It takes the ledger from <paramref name="services"/>
    /// only when it counts or cleans, so describing the areas never opens the module's database.
    /// </summary>
    public OsduStorageContributor(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>The areas alone, for a check that reads them without a database.</summary>
    public OsduStorageContributor()
    {
    }

    public string Owner => "OSDU Delivery";

    public IReadOnlyList<StorageArea> Areas { get; } = Describe();

    public async Task<long> CountAsync(StorageArea area, DateTime olderThanUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(area);
        var ledger = Ledger();
        return area.Id switch
        {
            AttemptsArea => await ledger.CountPrunableAttemptsAsync(olderThanUtc, ct).ConfigureAwait(false),
            ActivityLogsArea => await ledger.CountActivityLogsAsync(olderThanUtc, ct).ConfigureAwait(false),
            AssertionRunsArea => await ledger.CountPrunableAssertionRunsAsync(olderThanUtc, ct).ConfigureAwait(false),
            _ => throw NotCleanable(area),
        };
    }

    public async Task<long> CleanAsync(StorageArea area, DateTime olderThanUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(area);
        var ledger = Ledger();
        return area.Id switch
        {
            AttemptsArea => await ledger.PruneAttemptsAsync(olderThanUtc, ct).ConfigureAwait(false),
            ActivityLogsArea => await ledger.ClearActivityLogsAsync(olderThanUtc, ct).ConfigureAwait(false),
            AssertionRunsArea => await ledger.PruneAssertionRunsAsync(olderThanUtc, ct).ConfigureAwait(false),
            _ => throw NotCleanable(area),
        };
    }

    private ILedger Ledger()
        => _services?.GetRequiredService<ILedger>()
            ?? throw new InvalidOperationException("This storage contributor describes the osdu schema's areas only; resolve it from the control plane's services to clean them.");

    private static InvalidOperationException NotCleanable(StorageArea area)
        => new($"Storage area '{area.Id}' takes no retention, so nothing cleans it from the Maintenance page.");

    private static StorageTable T(string name) => StorageTable.Named(Schema, name);

    private static IReadOnlyList<StorageArea> Describe()
    {
        var kept = InventorySelections.KeptFor.TotalDays.ToString(CultureInfo.InvariantCulture);
        return
        [
            new()
            {
                Id = OsduSchema.Module + ".records",
                Group = LedgerGroup,
                Title = "Delivery records",
                Verdict = StorageVerdict.Keep,
                Holds = "One row per record a delivery flow delivers in a partition: its state, the OSDU id and version it landed as, its hashes and the source file and row it came from; and the identifying values record search finds it by.",
                Reason = "This is the ledger itself. Every record's state, the delivered, pending, failed and deleted counts, and record search are read from it.",
                Brief = "Only an operator's delete",
                Lifecycle = "Removed only by an operator: deleting a removed record from the ledger, or a flow's whole ledger. One line of each stays in Purged records.",
                Tables = [T("Record"), T("RecordIdentity")],
                ManagedAt = "/delivery/records",
            },
            new()
            {
                Id = AttemptsArea,
                Group = LedgerGroup,
                Title = "Delivery attempts",
                Verdict = StorageVerdict.Optional,
                Holds = "One row per try to deliver a record, and per decision written as one (held, unchanged, stale, removed, restored): when, by which run, the outcome, the error, the OSDU version it landed as and the steps it took.",
                Reason = "A record's earlier tries leave its timeline and its chain of changes, and a reversal or removal of an old run or submission no longer finds what such a record held then. The delivered, pending, failed and deleted counts are unaffected: they come from the records. Traceability keeps every attempt, so set a limit only when the ledger's size calls for it.",
                Retention = new StorageRetention("Each record's latest attempt, whatever its age, so every record's last outcome stays explainable.", "attempts"),
                Tables = [T("Attempt")],
                ManagedAt = "/delivery/records",
            },
            new()
            {
                Id = OsduSchema.Module + ".submissions",
                Group = LedgerGroup,
                Title = "Submissions and work batches",
                Verdict = StorageVerdict.Keep,
                Holds = "One row per plan a delivery made (its window or keys, its counts and status), each batch of rendered documents it drained, and how far each flow has read its source.",
                Reason = "A record names the submission that last sent it and an attempt its batch; a watermark is where the next plan starts reading.",
                Brief = "Deleted with the flow's ledger",
                Lifecycle = "Deleted with the flow's ledger.",
                Grows = true,
                Tables = [T("Submission"), T("WorkBatch"), T("SourceWatermark")],
                ManagedAt = "/delivery/records",
            },
            new()
            {
                Id = OsduSchema.Module + ".work-in-progress",
                Group = LedgerGroup,
                Title = "Leases and pending steps",
                Verdict = StorageVerdict.Safe,
                Holds = "A worker's claim on the records it is sending, and the steps it appended under that claim.",
                Reason = "They exist only while records are being sent; the attempts are the permanent copy of what a worker did.",
                Brief = "When the lease closes",
                Lifecycle = "Removed automatically when a worker's lease closes or, after a worker stopped, when the next run recovers it.",
                Tables = [T("Lease"), T("RecordEvent")],
            },
            new()
            {
                Id = OsduSchema.Module + ".artifacts",
                Group = LedgerGroup,
                Title = "Objects created in OSDU",
                Verdict = StorageVerdict.Keep,
                Holds = "One row per object a delivery created, or meant to create, in OSDU besides its record (a dataset, a file), with its state and how to undo it.",
                Reason = "It is how what a delivery left in OSDU is found and undone, and part of each record's history.",
                Brief = "Never removed",
                Lifecycle = "A step's progress row goes when its unit commits; the objects' rows stay, even when a flow's ledger is deleted.",
                Grows = true,
                Tables = [T("Artifact")],
            },
            new()
            {
                Id = OsduSchema.Module + ".purged-records",
                Group = LedgerGroup,
                Title = "Purged records",
                Verdict = StorageVerdict.Keep,
                Holds = "The one line kept of each record deleted from the ledger: its key, label, OSDU id, last version, how many attempts it had, who deleted it and when.",
                Reason = "It is the only trace of a record after it left the ledger; the next run and the record page read it.",
                Brief = "Never removed",
                Lifecycle = "Never removed.",
                Grows = true,
                Tables = [T("PurgedRecord")],
            },
            new()
            {
                Id = OsduSchema.Module + ".removed-keys",
                Group = LedgerGroup,
                Title = "Keys the source let go of",
                Verdict = StorageVerdict.Keep,
                Holds = "One row per record key the source no longer holds, with what became of its record.",
                Reason = "It keeps a record the source removed out of delivery.",
                Brief = "When the key is lifted",
                Lifecycle = "Lifted when the source holds the key again, a newer delivery of it comes, or an operator lifts it.",
                Tables = [T("RemovedKey")],
                ManagedAt = "/delivery/removals",
            },
            new()
            {
                Id = OsduSchema.Module + ".ledger-directory",
                Group = LedgerGroup,
                Title = "Ledger directory",
                Verdict = StorageVerdict.Keep,
                Holds = "Each ledger (flow, interface and partition), the number every ledger key of a partition starts with, each flow as the repository sync found it, and each interface with the cache version it renders against.",
                Reason = "Every read and write of the ledger starts here.",
                Brief = "Never removed",
                Lifecycle = "Never removed; a flow that leaves its repository is marked inactive.",
                Tables = [T("Ledger"), T("LedgerPartition"), T("LedgerFlow"), T("Interface")],
            },
            new()
            {
                Id = OsduSchema.Module + ".activities",
                Group = Audit,
                Title = "Activities and reversals",
                Verdict = StorageVerdict.Keep,
                Holds = "One row per run and per operator action (release, redeliver, reverse, verify, purge): who, when, with which inputs, the outcome and a summary; the records each bulk action reached; and each reversal with what OSDU held before it.",
                Reason = "This is the audit trail: who did what, to which records, and when.",
                Brief = "Never removed",
                Lifecycle = "Activities are never removed; reversals go with the flow's ledger. An activity's free-text log is its own area.",
                Grows = true,
                Tables = [T("Activity"), T("ActivityRecord"), T("Reversal"), T("ReversalItem")],
                ManagedAt = "/delivery/activity",
            },
            new()
            {
                Id = ActivityLogsArea,
                Group = Audit,
                Title = "Activity logs",
                Verdict = StorageVerdict.Optional,
                Holds = "The log each finished run or operator action captured, up to 200,000 characters per activity.",
                Reason = "Only the free text goes. Who did what, when, with which inputs, the outcome and its summary stay, and each record's own history is in its attempts; what is lost is the line-by-line account of an old run.",
                Retention = new StorageRetention("An activity still running keeps its log, since it is written when the activity completes.", "logs"),
                Column = new StorageColumn(Schema, "Activity", "Log"),
                ManagedAt = "/delivery/activity",
            },
            new()
            {
                Id = AssertionRunsArea,
                Group = Assertions,
                Title = "Assertion runs and results",
                Verdict = StorageVerdict.Optional,
                Holds = "One row per run of an assertion flow in a partition with its counts, and one per test's result in it, with every assertion's outcome and the records that failed it.",
                Reason = "A removed run's report and results go, so a test's history and trend start at the oldest run kept. A run goes only whole, and only once later runs have superseded every result in it.",
                Retention = new StorageRetention("Each test's latest result, and so every run that still holds one, whatever its age.", "runs"),
                Tables = [T("AssertionRun"), T("AssertionResult")],
                ManagedAt = "/delivery/assertions",
            },
            new()
            {
                Id = OsduSchema.Module + ".cache",
                Group = Cache,
                Title = "Partition cache",
                Verdict = StorageVerdict.Keep,
                Holds = "The reference data each partition's cache holds, by version: every record a cache flow captured, the versions that hold it, the flow that holds it, and what each cache flow declares.",
                Reason = "Every delivery renders against the current version, and a flow may pin an older one. Its size is the reference data itself.",
                Brief = "Old versions after retentionDays",
                Lifecycle = "Every refresh prunes the records of replaced versions older than the cache flow's retentionDays (7 by default); the current version, the one it replaced and every pinned version stay, and a pruned version stays listed. An admin can prune it now on the Cache page.",
                Tables = [T("CacheVersion"), T("CacheItem"), T("CacheMember"), T("CacheDefinition")],
                ManagedAt = "/delivery/cache",
            },
            new()
            {
                Id = OsduSchema.Module + ".render-context",
                Group = Cache,
                Title = "Render context of delivered records",
                Verdict = StorageVerdict.Keep,
                Holds = "The cached values each delivered record was rendered with, each distinct set stored once, and every cache change that reached delivered records with its decision and rollout.",
                Reason = "A delivered record's render is reconstructible from them after its cache version is gone, and why records moved after a cache change is answered from them.",
                Brief = "Never removed",
                Lifecycle = "Never removed.",
                Grows = true,
                Tables = [T("CacheSet"), T("CacheSetEntry"), T("UpdateTag")],
            },
            new()
            {
                Id = OsduSchema.Module + ".dimensions",
                Group = Dimensions,
                Title = "Dimensions",
                Verdict = StorageVerdict.Keep,
                Holds = "Each dimension's members and the original values behind them, their attributes and nested elements, the records each key was read from, the views declared over them, and each dimension's own table (osdu.dim_*).",
                Reason = "This is what the dimension flows publish, as it stands now: each build rewrites it, so its size is the size of the dimensions, not of their history.",
                Brief = "Rewritten by each build",
                Lifecycle = "A build rewrites it. A dimension its flow no longer declares is removed, with its table, on the Dimensions page.",
                Tables =
                [
                    T("Dimension"), T("DimensionMember"), T("DimensionValue"), T("DimensionAttribute"), T("DimensionAttributeName"),
                    T("DimensionElement"), T("DimensionCollectedText"), T("DimensionKeyRecord"), T("DimensionRecord"), T("DimensionView"),
                    StorageTable.StartingWith(Schema, DimensionTables.Prefix),
                ],
                ManagedAt = "/delivery/dimensions",
            },
            new()
            {
                Id = OsduSchema.Module + ".dimension-history",
                Group = Dimensions,
                Title = "Dimension builds and changes",
                Verdict = StorageVerdict.Keep,
                Holds = "One row per dimension build with its counts, and one per change a build made to a key.",
                Reason = "They are each dimension's build history, its change log and its build statistics.",
                Brief = "Removed with the dimension",
                Lifecycle = "Removed only with the dimension.",
                Grows = true,
                Tables = [T("DimensionRun"), T("DimensionChange")],
                ManagedAt = "/delivery/dimensions",
            },
            new()
            {
                Id = OsduSchema.Module + ".dimension-staging",
                Group = Dimensions,
                Title = "Dimension build staging",
                Verdict = StorageVerdict.Safe,
                Holds = "What each running dimension build has read so far: its keys, their values, labels and attributes, the values laid out from them, the texts it collected, the objects of a nested array, and the records the keys were read through or are held by.",
                Reason = "A build merges it into its dimension when it completes, so its tables exist only while a build runs, or as what a build whose process stopped left.",
                Brief = "Dropped when the build ends",
                Lifecycle = $"Each build stages into tables of its own (osdu.{DimensionStage.TablePrefix}*) and drops them when it ends, written or not. What a build whose process stopped left is dropped by the next build of the same dimension: once the build is closed, or once it has run for {DimensionStage.AbandonedAfter.TotalHours.ToString(CultureInfo.InvariantCulture)} hours. Removing the dimension drops them too.",
                Tables = [StorageTable.StartingWith(Schema, DimensionStage.TablePrefix)],
                ManagedAt = "/delivery/dimensions",
            },
            new()
            {
                Id = OsduSchema.Module + ".dimension-view-checks",
                Group = Dimensions,
                Title = "View checks",
                Verdict = StorageVerdict.Safe,
                Holds = "One row per check of a declared view in a partition.",
                Reason = "Only the recent checks say whether a view holds now.",
                Brief = $"Newest {DeliveryDimensionViewCheck.Kept.ToString(CultureInfo.InvariantCulture)} per view",
                Lifecycle = $"Each view keeps its newest {DeliveryDimensionViewCheck.Kept.ToString(CultureInfo.InvariantCulture)} checks per partition; older ones are removed automatically.",
                Tables = [T("DimensionViewCheck")],
                ManagedAt = "/delivery/dimensions",
            },
            new()
            {
                Id = OsduSchema.Module + ".inventories",
                Group = Inventories,
                Title = "Inventories",
                Verdict = StorageVerdict.Keep,
                Holds = "Each inventory, every OSDU id it holds or its ledger expects with its finding, and the versions storage keeps of each.",
                Reason = "It is what OSDU holds as the last build read it, and what reconciliations and removals work from.",
                Brief = "Rewritten by each build",
                Lifecycle = "Each build rewrites it. Ids gone from OSDU stay listed until an operator removes them from the inventory.",
                Tables = [T("Inventory"), T("InventoryRecord"), T("InventoryVersion")],
                ManagedAt = "/delivery/inventories",
            },
            new()
            {
                Id = OsduSchema.Module + ".inventory-history",
                Group = Inventories,
                Title = "Inventory builds and removals",
                Verdict = StorageVerdict.Keep,
                Holds = "One row per inventory build or reconcile, and each removal an operator asked for, with what it did to each id.",
                Reason = "They are the build history and the record of every removal from OSDU.",
                Brief = "Never removed",
                Lifecycle = "Never removed.",
                Grows = true,
                Tables = [T("InventoryRun"), T("InventoryRemoval"), T("InventoryRemovalItem")],
                ManagedAt = "/delivery/inventories",
            },
            new()
            {
                Id = OsduSchema.Module + ".inventory-work",
                Group = Inventories,
                Title = "Matches and duplicate checks",
                Verdict = StorageVerdict.Safe,
                Holds = "The ids a build is staging, the matches and duplicate checks operators asked for with the ids each found, and each duplicate check's own table (osdu.dup_*).",
                Reason = "They are working sets for a decision, made again in moments; one a removal used is kept as part of that removal's record.",
                Brief = $"After {kept} days",
                Lifecycle = $"Removed automatically: a build's staging when it ends, a match or a duplicate check {kept} days after it was made (one a removal used keeps its row), and a check's table when its download fails or a later download of the inventory completes.",
                Tables =
                [
                    T("InventoryScan"), T("InventorySelection"), T("InventorySelectionItem"), T("InventoryDuplicateCheck"),
                    T("InventoryDuplicateGroup"), T("InventoryDuplicateMember"), StorageTable.StartingWith(Schema, InventoryDuplicateTable.Prefix),
                ],
                ManagedAt = "/delivery/inventories",
            },
            new()
            {
                Id = OsduSchema.Module + ".removal-runs",
                Group = Removals,
                Title = "Removal runs",
                Verdict = StorageVerdict.Keep,
                Holds = "One row per run of a removal flow, with what it compared and removed.",
                Reason = "It is the record of what each removal took out of the ledger and OSDU.",
                Brief = "Never removed",
                Lifecycle = "Never removed.",
                Grows = true,
                Tables = [T("RemovalRun")],
                ManagedAt = "/delivery/removals",
            },
            new()
            {
                Id = OsduSchema.Module + ".removal-staging",
                Group = Removals,
                Title = "Removal staging",
                Verdict = StorageVerdict.Safe,
                Holds = "The keys a running removal staged to compare the source with the ledger.",
                Reason = "They exist only while a removal runs.",
                Brief = "When the run closes",
                Lifecycle = "Cleared when the run closes, or by the next run of the removal.",
                Tables = [T("RemovalKey")],
            },
            new()
            {
                Id = OsduSchema.Module + ".retrievals",
                Group = Removals,
                Title = "Retrievals",
                Verdict = StorageVerdict.Keep,
                Holds = "One row per retrieval run: its window, the files it wrote and its outcome.",
                Reason = "The latest finished one is where the next run starts, and the rest are the retrieval history.",
                Brief = "Never removed",
                Lifecycle = "Never removed.",
                Grows = true,
                Tables = [T("Retrieval")],
            },
            new()
            {
                Id = OsduSchema.Module + ".repository-documents",
                Group = Setup,
                Title = "Mappings, templates and search terms",
                Verdict = StorageVerdict.Keep,
                Holds = "The mappings and template files each repository sync found, the search terms its flows give, and what people made of each term.",
                Reason = "Deliveries render from the mappings and templates, and the pages read all of them.",
                Brief = "Rewritten by each sync",
                Lifecycle = "Rewritten by each repository sync, which removes what a repository no longer holds; a term's refinement stays until someone resets it.",
                Tables = [T("Mapping"), T("RepositoryTemplate"), T("SearchTerm"), T("SearchTermRefinement")],
                ManagedAt = "/delivery/templates",
            },
            new()
            {
                Id = OsduSchema.Module + ".schema-lineage",
                Group = Setup,
                Title = "OSDU schema lineage",
                Verdict = StorageVerdict.Keep,
                Holds = "Each OSDU release's kinds and how they relate, built from the OSDU data definitions.",
                Reason = "The OSDU order reads a kind's place from it when no template folder holds its template.",
                Brief = "Rebuilt per release",
                Lifecycle = "One set per release built; building a release again replaces its own.",
                Tables = [T("SchemaLineage"), T("SchemaLineageKind")],
                ManagedAt = "/delivery/order",
            },
            new()
            {
                Id = OsduSchema.Module + ".setup",
                Group = Setup,
                Title = "Partitions and configuration",
                Verdict = StorageVerdict.Keep,
                Holds = "The registered partitions, the central configuration values, and the module schema's version and migrations.",
                Reason = "Every run and every page reads them.",
                Brief = "Managed on the Partitions page",
                Lifecycle = "Managed on the Partitions page and by the deployment's configuration.",
                Tables = [T("Partition"), T("ConfigProperty"), T("SchemaVersion"), T(OsduDbContext.MigrationsHistoryTable)],
                ManagedAt = "/delivery/partitions",
            },
        ];
    }
}
