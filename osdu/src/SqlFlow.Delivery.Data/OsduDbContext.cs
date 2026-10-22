using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Delivery.Data;

/// <summary>
/// The OSDU module's database: every table and view of the delivery ledger in the <c>osdu</c> schema, with its own
/// migrations recorded in <c>[osdu].[__EFMigrationsHistory]</c> and its own version row
/// (<see cref="OsduSchemaVersion"/>). Nothing here references SQLFlow's catalog. On the control plane the context opens
/// on the catalog database's connection and enlists in its transaction, so a run queued with the rows that describe it
/// commits as one unit; a node opens it with a login that reaches only the <c>osdu</c> schema.
/// </summary>
public sealed class OsduDbContext : DbContext
{
    /// <summary>The name of the migrations history table, which lives in <see cref="DeliveryModel.SchemaName"/>.</summary>
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    /// <summary>The module version the current migrations produce; written to <see cref="OsduSchemaVersion.ModuleVersion"/>.</summary>
    public const string ModuleVersion = "1.55.0";

    /// <summary>
    /// The oldest SQLFlow catalog migration this schema works with: the one that records the values the control plane
    /// supplies with a run, which carry the central configuration set for no partition to every run, beside the fan-out
    /// run groups, run results and run kind arguments the OSDU flow's intake and drain rely on.
    /// </summary>
    public const string MinimumCatalogMigration = "20261014215105_RunSuppliedReferences";

    public OsduDbContext(DbContextOptions<OsduDbContext> options)
        : base(options)
    {
    }

    /// <summary>The partitions the ledger keys by, each with the number every ledger key starts with.</summary>
    public DbSet<DeliveryLedgerPartition> DeliveryLedgerPartitions => Set<DeliveryLedgerPartition>();

    /// <summary>Every ledger: a flow's (or an interface's) rows in one partition, under one ledger identity.</summary>
    public DbSet<DeliveryLedger> DeliveryLedgers => Set<DeliveryLedger>();

    public DbSet<DeliverySubmission> DeliverySubmissions => Set<DeliverySubmission>();

    public DbSet<DeliveryRecord> DeliveryRecords => Set<DeliveryRecord>();

    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();

    public DbSet<DeliveryWorkBatch> DeliveryWorkBatches => Set<DeliveryWorkBatch>();

    public DbSet<DeliveryLease> DeliveryLeases => Set<DeliveryLease>();

    public DbSet<DeliveryRecordIdentity> DeliveryRecordIdentities => Set<DeliveryRecordIdentity>();

    public DbSet<DeliveryRecordEvent> DeliveryRecordEvents => Set<DeliveryRecordEvent>();

    public DbSet<DeliverySourceWatermark> DeliverySourceWatermarks => Set<DeliverySourceWatermark>();

    public DbSet<DeliveryActivity> DeliveryActivities => Set<DeliveryActivity>();

    /// <summary>The records an intervention made for many records changed, each named once.</summary>
    public DbSet<DeliveryActivityRecord> DeliveryActivityRecords => Set<DeliveryActivityRecord>();

    /// <summary>The reversals of runs and submissions, one row per source of a ledger.</summary>
    public DbSet<DeliveryReversal> DeliveryReversals => Set<DeliveryReversal>();

    /// <summary>The records each reversal reaches, with what it did to each.</summary>
    public DbSet<DeliveryReversalItem> DeliveryReversalItems => Set<DeliveryReversalItem>();

    /// <summary>What the ledger keeps of a record deleted from it after it was removed from OSDU: one line each.</summary>
    public DbSet<DeliveryPurgedRecord> DeliveryPurgedRecords => Set<DeliveryPurgedRecord>();

    /// <summary>What deliveries created in OSDU, or set out to create: one row per object, minted id, session or rows a unit made.</summary>
    public DbSet<DeliveryArtifact> DeliveryArtifacts => Set<DeliveryArtifact>();

    /// <summary>The inventories of inventory flows: every id an OSDU kind holds, compared with the ledgers of its partition.</summary>
    public DbSet<DeliveryInventory> DeliveryInventories => Set<DeliveryInventory>();

    public DbSet<DeliveryInventoryRun> DeliveryInventoryRuns => Set<DeliveryInventoryRun>();

    public DbSet<DeliveryInventoryRecord> DeliveryInventoryRecords => Set<DeliveryInventoryRecord>();

    public DbSet<DeliveryInventoryVersion> DeliveryInventoryVersions => Set<DeliveryInventoryVersion>();

    public DbSet<DeliveryInventoryScan> DeliveryInventoryScans => Set<DeliveryInventoryScan>();

    /// <summary>The removals an operator asked of an inventory's ids, one row per removal.</summary>
    public DbSet<DeliveryInventoryRemoval> DeliveryInventoryRemovals => Set<DeliveryInventoryRemoval>();

    /// <summary>What each removal did to each id it reached: removed, already gone, skipped or failed, and why.</summary>
    public DbSet<DeliveryInventoryRemovalItem> DeliveryInventoryRemovalItems => Set<DeliveryInventoryRemovalItem>();

    /// <summary>The ids of inventories that hold what an operator's conditions ask in OSDU, and the matches that found them.</summary>
    public DbSet<DeliveryInventorySelection> DeliveryInventorySelections => Set<DeliveryInventorySelection>();

    public DbSet<DeliveryInventorySelectionItem> DeliveryInventorySelectionItems => Set<DeliveryInventorySelectionItem>();

    public DbSet<DeliveryRetrieval> DeliveryRetrievals => Set<DeliveryRetrieval>();

    /// <summary>The runs of assertion flows, one row per run and partition.</summary>
    public DbSet<DeliveryAssertionRun> DeliveryAssertionRuns => Set<DeliveryAssertionRun>();

    /// <summary>The result of every test in every run of an assertion flow.</summary>
    public DbSet<DeliveryAssertionResult> DeliveryAssertionResults => Set<DeliveryAssertionResult>();

    /// <summary>The dimensions of dimension flows, one row per dimension and partition.</summary>
    public DbSet<DeliveryDimension> DeliveryDimensions => Set<DeliveryDimension>();

    /// <summary>Every build of every dimension.</summary>
    public DbSet<DeliveryDimensionRun> DeliveryDimensionRuns => Set<DeliveryDimensionRun>();

    /// <summary>The members of the dimensions: their clean values.</summary>
    public DbSet<DeliveryDimensionMember> DeliveryDimensionMembers => Set<DeliveryDimensionMember>();

    /// <summary>The originals of the dimensions: their values exactly as the index holds them.</summary>
    public DbSet<DeliveryDimensionValue> DeliveryDimensionValues => Set<DeliveryDimensionValue>();

    /// <summary>The attributes of the dimensions' originals, one row per original and attribute.</summary>
    public DbSet<DeliveryDimensionAttributeValue> DeliveryDimensionAttributeValues => Set<DeliveryDimensionAttributeValue>();

    public DbSet<DeliveryDimensionCollectedText> DeliveryDimensionCollectedTexts => Set<DeliveryDimensionCollectedText>();

    /// <summary>The fields of the objects of a nested array the dimensions' keys hold, one row per object and field.</summary>
    public DbSet<DeliveryDimensionElement> DeliveryDimensionElements => Set<DeliveryDimensionElement>();

    /// <summary>The records each key's label and attributes were read through, which an incremental load finds keys to read again by.</summary>
    public DbSet<DeliveryDimensionKeyRecord> DeliveryDimensionKeyRecords => Set<DeliveryDimensionKeyRecord>();

    /// <summary>The keys each record of a dimension held when a load last read it, by the record's unique key.</summary>
    public DbSet<DeliveryDimensionRecord> DeliveryDimensionRecords => Set<DeliveryDimensionRecord>();

    /// <summary>The views dimension flows declare over their tables, as their builds last wrote them.</summary>
    public DbSet<DeliveryDimensionView> DeliveryDimensionViews => Set<DeliveryDimensionView>();

    /// <summary>What builds' checks of the views found, a row per view, partition and check.</summary>
    public DbSet<DeliveryDimensionViewCheck> DeliveryDimensionViewChecks => Set<DeliveryDimensionViewCheck>();

    /// <summary>The attributes of dimensions, each under the number its values are joined by.</summary>
    public DbSet<DeliveryDimensionAttributeName> DeliveryDimensionAttributeNames => Set<DeliveryDimensionAttributeName>();

    /// <summary>What each build changed of the dimensions' originals.</summary>
    public DbSet<DeliveryDimensionChange> DeliveryDimensionChanges => Set<DeliveryDimensionChange>();

    public DbSet<DeliveryMapping> DeliveryMappings => Set<DeliveryMapping>();

    /// <summary>The search terms the mappings of active delivery flows give, written by every sync
    /// (osdu/docs/reference/concepts/search-terms.md).</summary>
    public DbSet<DeliverySearchTerm> DeliverySearchTerms => Set<DeliverySearchTerm>();

    /// <summary>What people made of search terms: their names, whether they are left out, the route each is searched through.</summary>
    public DbSet<DeliverySearchTermRefinement> DeliverySearchTermRefinements => Set<DeliverySearchTermRefinement>();

    public DbSet<DeliveryInterface> DeliveryInterfaces => Set<DeliveryInterface>();

    /// <summary>The files of the template folders of every synced repository, written by every sync.</summary>
    public DbSet<DeliveryRepositoryTemplate> DeliveryRepositoryTemplates => Set<DeliveryRepositoryTemplate>();

    public DbSet<DeliveryCacheDefinition> DeliveryCacheDefinitions => Set<DeliveryCacheDefinition>();

    /// <summary>The central configuration the control plane supplies to the runs it queues.</summary>
    public DbSet<DeliveryConfigProperty> DeliveryConfigProperties => Set<DeliveryConfigProperty>();

    /// <summary>The OSDU partitions registered with the catalog, and the default one.</summary>
    public DbSet<DeliveryPartition> DeliveryPartitions => Set<DeliveryPartition>();

    public DbSet<DeliveryCacheVersion> DeliveryCacheVersions => Set<DeliveryCacheVersion>();

    public DbSet<DeliveryCacheItem> DeliveryCacheItems => Set<DeliveryCacheItem>();

    public DbSet<DeliveryCacheMember> DeliveryCacheMembers => Set<DeliveryCacheMember>();

    public DbSet<SchemaLineage> SchemaLineages => Set<SchemaLineage>();

    public DbSet<SchemaLineageKind> SchemaLineageKinds => Set<SchemaLineageKind>();

    public DbSet<DeliveryCacheSet> DeliveryCacheSets => Set<DeliveryCacheSet>();

    public DbSet<DeliveryCacheSetEntry> DeliveryCacheSetEntries => Set<DeliveryCacheSetEntry>();

    public DbSet<DeliveryUpdateTag> DeliveryUpdateTags => Set<DeliveryUpdateTag>();

    public DbSet<OsduSchemaVersion> SchemaVersions => Set<OsduSchemaVersion>();

    /// <summary>Options for SQL Server over a connection string, with the history table in the <c>osdu</c> schema.</summary>
    public static DbContextOptions<OsduDbContext> SqlServerOptions(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new DbContextOptionsBuilder<OsduDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(MigrationsHistoryTable, DeliveryModel.SchemaName))
            .Options;
    }

    /// <summary>
    /// Options for SQL Server over an existing connection the caller owns (the catalog's, on the control plane), with the
    /// history table in the <c>osdu</c> schema. The context never opens or disposes a connection it was given; to commit
    /// with the owner's transaction, the caller enlists it through <c>Database.UseTransaction</c>.
    /// </summary>
    public static DbContextOptions<OsduDbContext> SqlServerOptions(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new DbContextOptionsBuilder<OsduDbContext>()
            .UseSqlServer(connection, sql => sql.MigrationsHistoryTable(MigrationsHistoryTable, DeliveryModel.SchemaName))
            .Options;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => DeliveryModel.Configure(modelBuilder);
}
