using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SqlFlow.Delivery.Data;

// The delivery ledger (osdu/docs/reference/concepts/ledger.md), in the osdu schema: submissions, records and
// append-only attempts, the audit trail of interventions, the source watermarks, and the read model of the mapping and
// cache documents the sync found in the repositories. Statuses are stored as short strings so the tables read without a
// decoder ring. Everything the GUI lists is index-backed. No foreign key or navigation reaches SQLFlow's catalog: a
// run, a group or a repository is referenced by id only.

/// <summary>The EF model of the delivery ledger, in the <c>osdu</c> schema.</summary>
public static partial class DeliveryModel
{
    public const string SchemaName = "osdu";

    /// <summary>
    /// The partition number of a ledger the upgrade to partition keys could not place, until its next run adopts it
    /// (<see cref="DeliveryLedger"/>). No <see cref="DeliveryLedgerPartition"/> row carries it.
    /// </summary>
    public const short UnassignedPartition = 0;

    /// <summary>The widest data-partition-id, as every table that names a partition keeps it.</summary>
    public const int MaxPartitionLength = 200;

    /// <summary>The longest search term key: its table and its column
    /// (osdu/docs/reference/concepts/search-terms.md).</summary>
    public const int SearchTermKeyLength = 600;

    /// <summary>
    /// The collation of the columns that key on an OSDU record id. OSDU ids are case-sensitive:
    /// <c>...UnitOfMeasure:ft</c> (the foot) and <c>...UnitOfMeasure:fT</c> (the femtotesla) are two records, and SQL
    /// Server's default collation folds case, which would make them one key.
    /// </summary>
    public const string OsduIdCollation = "Latin1_General_100_BIN2";

    /// <summary>
    /// The collation a search of text the ledger keeps exactly asks it in: case folded, so a person finds a clean value or an
    /// original by the spelling they know, whatever its case.
    /// </summary>
    public const string SearchCollation = "Latin1_General_100_CI_AS";

    /// <summary>
    /// The longest ingestion file name a record's origin holds. It keeps the (flow, file, row) index key under SQL Server's
    /// 1700-byte limit; a longer file name is refused when the source is opened, naming the file and this limit.
    /// </summary>
    public const int MaxSourceFileNameLength = 800;

    /// <summary>
    /// The longest OSDU id a record holds, as its id, its claim and the id it waits for. A render that gives a longer id holds
    /// the record, naming this limit, rather than storing an id cut short.
    /// </summary>
    public const int MaxTargetIdLength = 500;

    /// <summary>
    /// The longest list of held steps a record keeps: sixteen steps of at most 32 characters each, comma-separated, with room
    /// to spare (the limits a delivery document's <c>enrich:</c> is refused beyond).
    /// </summary>
    public const int MaxHeldStepsLength = 600;

    /// <summary>
    /// The most characters of what a record restricted until complete goes without that the ledger keeps
    /// (<see cref="DeliveryRecord.IncompleteMissing"/>): the values a hold would name, one to a line, those past it counted on a
    /// last line, as an error column keeps an error.
    /// </summary>
    public const int MaxIncompleteMissingLength = 2000;

    /// <summary>
    /// The most characters of an estate commit the ledger keeps (<see cref="DeliveryRecord.RenderCommit"/>): a git object id,
    /// 40 hexadecimal characters under SHA-1 and 64 under SHA-256.
    /// </summary>
    public const int MaxCommitLength = 64;

    /// <summary>
    /// The index of a flow's records restricted until complete, by what they go without: the Records tab's filter, the flow's
    /// count, and the records one set of missing values restricts, the few such records and no others.
    /// </summary>
    public const string IncompleteIndex = "IX_Record_PartitionId_FlowId_Incomplete";

    /// <summary>The longest lease token, and the longest worker name a lease records as its owner.</summary>
    public const int MaxLeaseTokenLength = 200;

    /// <summary>The longest slot a route names an artifact by within its unit.</summary>
    public const int MaxArtifactSlotLength = 200;

    /// <summary>The longest locator an artifact keeps (a landing-zone path, a list of row keys in runs).</summary>
    public const int MaxArtifactLocatorLength = 1000;

    /// <summary>The longest note an artifact keeps of why it was kept or why its undo failed.</summary>
    public const int MaxArtifactNoteLength = 1000;

    /// <summary>
    /// The filtered index of the artifacts still open (an intent, a pending one of an open or abandoned unit, one due or failed
    /// to undo), named because the sweep reaches it by name and repeats its filter.
    /// </summary>
    public const string OpenArtifactIndex = "IX_Artifact_Open";

    /// <summary>The longest kind an inventory reads or a record of it carries.</summary>
    public const int MaxInventoryKindLength = 300;

    /// <summary>
    /// The longest name of a removal, and of the delivery flow it names as pages show it: a flow's name (200) and an interface's
    /// (64) with the slash between them.
    /// </summary>
    public const int MaxRemovalNameLength = 300;

    /// <summary>The longest query an inventory narrows a search with.</summary>
    public const int MaxInventoryQueryLength = 4000;

    /// <summary>The longest identity an inventory keeps as a record's creator or last modifier.</summary>
    public const int MaxInventoryUserLength = 256;

    /// <summary>The longest name an inventory keeps of a record; a longer one is cut to it.</summary>
    public const int MaxInventoryNameLength = 400;

    /// <summary>The longest line an inventory record keeps of what its finding rests on.</summary>
    public const int MaxInventoryDetailLength = 1000;

    /// <summary>The longest text a selection keeps of the values an id holds at the properties its conditions ask.</summary>
    public const int MaxSelectionValuesLength = 4000;

    /// <summary>The length of a selection's conditions key: a SHA-256 in hex.</summary>
    public const int SelectionKeyLength = 64;

    /// <summary>The length of the hash a duplicate check keeps of each id's key: a SHA-256, in bytes.</summary>
    public const int DuplicateKeyHashLength = 32;

    /// <summary>The longest line a duplicate check keeps of why a group came out as it did, or why a copy has its role.</summary>
    public const int MaxDuplicateReasonLength = 1000;

    /// <summary>The longest name of a table the module makes at run time (a duplicate check's): SQL Server's own limit.</summary>
    public const int MaxRunTimeTableNameLength = 128;

    /// <summary>
    /// The longest name of a file a download writes, extension included: below the 255 characters NTFS, APFS and ext4 take, with
    /// room for the <c>.tmp</c> a local write writes beside the file first (osdu/docs/reference/flow/download.md, File names).
    /// </summary>
    public const int MaxDownloadFileNameLength = 240;

    /// <summary>The longest folder a download keeps as where its files are: a local path or a storage URI.</summary>
    public const int MaxDownloadLocationLength = 2000;

    /// <summary>The longest line a download record keeps of why its last write did not happen.</summary>
    public const int MaxDownloadProblemLength = 1000;

    /// <summary>The length of the SHA-256 a download record keeps of its file's bytes.</summary>
    public const int DownloadHashLength = 32;

    /// <summary>
    /// The longest identity token, and the longest display value beside it: 200 characters keep the
    /// (token, flow, key) primary key well under SQL Server's 1700-byte limit for a nonclustered index key, and a
    /// longer value is still found by its start. The ledger's <c>RecordIdentityLimits.MaxTokenLength</c> is this.
    /// </summary>
    public const int MaxIdentityTokenLength = 200;

    /// <summary>
    /// The filtered index of the blocked records not sorted into a problem yet, named because a statement reaches it by
    /// name and repeats its filter: the problem backfill's.
    /// </summary>
    public const string UnsortedProblemIndex = "IX_Record_Unsorted";

    /// <summary>
    /// The filtered index of the parked records (held, failed, removed or reverted) by whether they are blocked, named because
    /// a statement reaches it by name and repeats its filter: a flow's statistics count its blocked records, and its held ones
    /// nothing moves, from it.
    /// </summary>
    public const string ParkedIndex = "IX_Record_Parked";

    /// <param name="modelBuilder">The model being built.</param>
    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // Every ledger table keys by partition first, so one partition's rows are kept together and every read of a
        // flow, a partition or a listing seeks a range of one partition. A row found by an id unique on its own (a
        // submission, an activity, a retrieval, a lease, a work batch) keeps that id unique in an index of its own, for
        // the reads that hold nothing else.
        ConfigureLedger(modelBuilder);
        ConfigureActivities(modelBuilder);
        ConfigureArtifacts(modelBuilder);
        ConfigureInventories(modelBuilder);
        ConfigureDownloads(modelBuilder);
        ConfigureRemovals(modelBuilder);
        ConfigureRetrievals(modelBuilder);
        ConfigureAssertions(modelBuilder);
        ConfigureDimensions(modelBuilder);
        ConfigureRepositories(modelBuilder);
        ConfigureCaches(modelBuilder);
        ConfigureSettings(modelBuilder);
        ConfigureComputeUsage(modelBuilder);
        ConfigureCacheDefinitions(modelBuilder);
    }

    /// <summary>
    /// A column keyed on an OSDU record id: compared exactly by the database (<see cref="OsduIdCollation"/>) and exactly by
    /// the change tracker. Without the ordinal comparer, a context tracking the foot and
    /// the femtotesla side by side (two memberships of one cache flow, say) takes them for one key and refuses the second.
    /// </summary>
    private static PropertyBuilder<string> OsduId(PropertyBuilder<string> property)
    {
        property.Metadata.SetValueComparer(new ValueComparer<string>(
            (left, right) => string.Equals(left, right, StringComparison.Ordinal),
            value => StringComparer.Ordinal.GetHashCode(value),
            value => value));
        return property.UseCollation(OsduIdCollation);
    }

    /// <summary>
    /// A column of text a dimension compares exactly: by the database (<see cref="OsduIdCollation"/>), where the default
    /// collation would take GR and gr for one member, and by the change tracker.
    /// </summary>
    private static PropertyBuilder<string> ExactText(PropertyBuilder<string> property) => OsduId(property);

    /// <summary>A nullable column keyed on an OSDU record id, compared exactly as <see cref="OsduId"/> compares a required one.</summary>
    private static PropertyBuilder<string?> OptionalOsduId(PropertyBuilder<string?> property)
    {
        property.Metadata.SetValueComparer(new ValueComparer<string?>(
            (left, right) => string.Equals(left, right, StringComparison.Ordinal),
            value => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value),
            value => value));
        return property.UseCollation(OsduIdCollation);
    }
}
