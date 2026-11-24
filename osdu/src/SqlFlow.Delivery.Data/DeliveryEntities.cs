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
public static class DeliveryModel
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

        // The ledger's directory: the partitions it keys by, and the ledger of every flow in one of them.
        modelBuilder.Entity<DeliveryLedgerPartition>(e =>
        {
            e.ToTable("LedgerPartition", SchemaName);
            e.HasKey(p => p.PartitionId);
            e.Property(p => p.PartitionId).ValueGeneratedOnAdd();
            e.Property(p => p.Name).HasMaxLength(MaxPartitionLength).IsRequired();
            // A partition has one number whatever the case it is written in, as the registry compares names.
            e.HasIndex(p => p.Name).IsUnique();
        });

        modelBuilder.Entity<DeliveryLedger>(e =>
        {
            e.ToTable("Ledger", SchemaName);
            // A partition's ledgers together, as every ledger table keeps its rows.
            e.HasKey(l => new { l.PartitionId, l.FlowId });
            e.Property(l => l.Kind).HasMaxLength(16).IsRequired();
            e.Property(l => l.FlowName).HasMaxLength(DeliveryLedger.MaxFlowNameLength).IsRequired();
            e.Property(l => l.Interface).HasMaxLength(DeliveryInterface.MaxInterfaceLength).IsRequired();
            e.Property(l => l.LedgerName).HasMaxLength(200).IsRequired();
            // A ledger belongs to one partition: its identity is unique on its own, and every read of it starts here.
            e.HasIndex(l => l.FlowId).IsUnique();
        });

        // Every ledger table keys by partition first, so one partition's rows are kept together and every read of a
        // flow, a partition or a listing seeks a range of one partition. A row found by an id unique on its own (a
        // submission, an activity, a retrieval, a lease, a work batch) keeps that id unique in an index of its own, for
        // the reads that hold nothing else.
        modelBuilder.Entity<DeliverySubmission>(e =>
        {
            e.ToTable("Submission", SchemaName);
            e.HasKey(s => new { s.PartitionId, s.SubmissionId });
            e.Property(s => s.FlowName).HasMaxLength(200).IsRequired();
            e.Property(s => s.MappingReference).HasMaxLength(200).IsRequired();
            e.Property(s => s.RenderContext).IsRequired();
            e.Property(s => s.WorkLocation).HasMaxLength(2000);
            e.Property(s => s.ParametersJson).IsRequired();
            e.Property(s => s.Status).HasMaxLength(16).IsRequired();
            e.Property(s => s.Error).HasMaxLength(4000);
            e.Property(s => s.Kind).HasMaxLength(16).IsRequired();
            e.Property(s => s.SourceConnection).HasMaxLength(400).IsRequired();
            e.Property(s => s.SourceObject).HasMaxLength(400).IsRequired();
            // A submission named by its id alone: a run's payload, a link, a record's last submission.
            e.HasIndex(s => s.SubmissionId).IsUnique();
            e.HasIndex(s => new { s.PartitionId, s.FlowId, s.ReceivedUtc });
            e.HasIndex(s => new { s.PartitionId, s.FlowId, s.Status });
            // The flow's submission listing filtered by kind.
            e.HasIndex(s => new { s.PartitionId, s.FlowId, s.Kind, s.ReceivedUtc });
            // The submissions of a partition, most recent first, across its flows.
            e.HasIndex(s => new { s.PartitionId, s.ReceivedUtc });
            // A run page links the run to the plan it coordinated.
            e.HasIndex(s => s.RunId);
        });

        modelBuilder.Entity<DeliveryRecord>(e =>
        {
            e.ToTable("Record", SchemaName);
            // A record is one flow's in one partition: two flows reading the same source row keep two records, and so does
            // one flow delivering it to two partitions, under the same delivery key. A flow's key-ordered walks (a
            // removal's key list, a key-scoped plan) read the key in order within the partition.
            e.HasKey(r => new { r.PartitionId, r.FlowId, r.DeliveryKey });
            e.Property(r => r.SourceKey).HasMaxLength(400).IsRequired();
            e.Property(r => r.SourceKeyJson).HasMaxLength(2000);
            e.Property(r => r.Label).HasMaxLength(400);
            e.Property(r => r.MappingName).HasMaxLength(200).IsRequired();
            e.Property(r => r.SourceFingerprint).HasMaxLength(200);
            e.Property(r => r.SourceFileName).HasMaxLength(MaxSourceFileNameLength);
            e.Property(r => r.MetadataHash).HasMaxLength(64);
            e.Property(r => r.PayloadHash).HasMaxLength(64);
            e.Property(r => r.HeldRulesHash).HasMaxLength(64);
            e.Property(r => r.RenderCommit).HasMaxLength(MaxCommitLength);
            e.Property(r => r.TargetId).HasMaxLength(MaxTargetIdLength);
            OptionalOsduId(e.Property(r => r.ClaimedTargetId)).HasMaxLength(MaxTargetIdLength);
            OptionalOsduId(e.Property(r => r.WaitingFor)).HasMaxLength(MaxTargetIdLength);
            OptionalOsduId(e.Property(r => r.ParentId)).HasMaxLength(MaxTargetIdLength);
            OptionalOsduId(e.Property(r => r.ChildLinkKey)).HasMaxLength(MaxTargetIdLength);
            OptionalOsduId(e.Property(r => r.OccupiedTargetId)).HasMaxLength(MaxTargetIdLength);
            e.Property(r => r.HeldSteps).HasMaxLength(MaxHeldStepsLength);
            // Unbounded: an ACL lists every group a mapping's lookups find (a log's field and country groups among them), and
            // the records beside the record add theirs, so no length fits every record a restriction must give back.
            e.Property(r => r.RestrictedFromAcl);
            e.Property(r => r.IncompleteMissing).HasMaxLength(MaxIncompleteMissingLength);
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.LastVerifyOutcome).HasMaxLength(16);
            e.Property(r => r.LeaseOwner).HasMaxLength(200);
            e.Property(r => r.LastError).HasMaxLength(2000);
            e.Property(r => r.PendingSourceFingerprint).HasMaxLength(200);
            e.Property(r => r.PendingSourceFileName).HasMaxLength(MaxSourceFileNameLength);
            e.Property(r => r.PendingMetadataHash).HasMaxLength(64);
            e.Property(r => r.PendingPayloadHash).HasMaxLength(64);
            e.Property(r => r.PendingPayloadLocation).HasMaxLength(2000);
            e.Property(r => r.PendingDocumentRef).HasMaxLength(64);
            e.Property(r => r.ValidationOutcome).HasMaxLength(16);
            e.Property(r => r.AcceptedMetadataHash).HasMaxLength(64);

            // Worker and intake paths. The worker's reads (the claim, what is due next, the settled submissions with due
            // work) are answered from these two alone, for a flow and for one submission of it.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.Status, r.NextAttemptUtc })
                .IncludeProperties(r => new { r.LastSubmissionId, r.UpdatedUtc, r.PendingDocumentRef });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.LastSubmissionId, r.Status, r.NextAttemptUtc })
                .IncludeProperties(r => r.UpdatedUtc);
            // The rollout walks one set's records in key order, then flow order; a set is one partition's, and the filtered
            // index keeps untagged records out of it.
            e.HasIndex(r => new { r.CacheSetId, r.DeliveryKey, r.FlowId }).HasFilter("[CacheSetId] IS NOT NULL");
            // A cache version that brings in what held records missed finds them by the set their held render read.
            e.HasIndex(r => new { r.HeldCacheSetId, r.Status }).HasFilter("[HeldCacheSetId] IS NOT NULL");
            // A parent that lands finds the records of its partition that read it, of whatever flow, to plan them again.
            e.HasIndex(r => new { r.PartitionId, r.ParentId }).HasFilter("[ParentId] IS NOT NULL");
            // A child that lands finds the records of its partition that link it, of whatever flow, by the unique segment of
            // its id, to plan them again.
            e.HasIndex(r => new { r.PartitionId, r.ChildLinkKey }).HasFilter("[ChildLinkKey] IS NOT NULL");
            // The records of a flow that an enrichment held in and was set aside on: the Records tab's filter and the flow's
            // count, the few such records and no others.
            e.HasIndex(r => new { r.PartitionId, r.FlowId }, "IX_Record_PartitionId_FlowId_HeldSteps").IncludeProperties(r => r.HeldSteps).HasFilter("[HeldSteps] IS NOT NULL");
            // The records of a flow whose access in OSDU is restricted, and no others: the Records tab's filter and the flow's
            // count.
            e.HasIndex(r => new { r.PartitionId, r.FlowId }, "IX_Record_PartitionId_FlowId_Restricted").IncludeProperties(r => r.RestrictionId).HasFilter("[RestrictionId] IS NOT NULL");
            // A registered restriction's records, counted for its page and refusing its removal while any is left. Records not
            // restricted stay out of it. No foreign key backs it: adding one would read every row of the ledger under a schema
            // lock as the migration ran, and the removal of a restriction is refused in the same transaction that counts it.
            e.HasIndex(r => r.RestrictionId).HasFilter("[RestrictionId] IS NOT NULL");
            // The records of a flow restricted until complete (osdu/docs/reference/flow/mapping-restricted-access.md), by what
            // they go without: the Records tab's filter, the flow's count and the groups of missing values, the few such records
            // and no others, however many the ledger holds.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.IncompleteHash }, IncompleteIndex)
                .IncludeProperties(r => r.IncompleteRestrictionId)
                .HasFilter("[IncompleteRestrictionId] IS NOT NULL");
            // A registered restriction's records restricted until complete, counted with those an operator restricted for its
            // page and refusing its removal while any is left: OSDU holds them under its groups.
            e.HasIndex(r => r.IncompleteRestrictionId).HasFilter("[IncompleteRestrictionId] IS NOT NULL");
            // One OSDU record, one flow: the database refuses a second flow's claim on an id, whatever races the intakes
            // run. An id names its partition, so this stays unique across every partition.
            e.HasIndex(r => r.ClaimedTargetId).IsUnique().HasFilter("[ClaimedTargetId] IS NOT NULL");
            e.HasIndex(r => new { r.LastSubmissionId, r.WorkBatch });
            // The records a lease holds: the ones a checkpoint or a close hands back, and the ones a submission waits on.
            e.HasIndex(r => r.LeaseOwner);
            // A submission's records, most recent first, read in index order however many the submission holds.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.LastSubmissionId, r.UpdatedUtc });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.LastVerifiedUtc });

            // GUI: one flow's prefix search and recency listings, all answered from an index.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.Label });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.SourceKey });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.TargetId });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.UpdatedUtc });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.Status, r.UpdatedUtc });

            // The parked records (held, failed, removed or reverted) by whether they are blocked: what a flow's statistics
            // count as blocked, and as held with nothing to move them (unblocked, unasked, with no document), each a seek of
            // a range that holds the parked records alone, however many records the ledger holds.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.Blocked, r.Status }, ParkedIndex)
                .IncludeProperties(r => new { r.PlanRequestedUtc, r.PendingDocumentRef })
                .HasFilter("[Status] IN (N'held', N'failed', N'deleted', N'reverted')");
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.LastDeliveredUtc });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.LastVerifyOutcome });

            // The lookup across the flows of a partition (the search box, read in the partition the workbench works in): a
            // delivery key, an OSDU id, a source key, a label prefix or an ingestion file name answers from these.
            e.HasIndex(r => new { r.PartitionId, r.DeliveryKey });
            e.HasIndex(r => new { r.PartitionId, r.TargetId });
            e.HasIndex(r => new { r.PartitionId, r.SourceKey });
            e.HasIndex(r => new { r.PartitionId, r.Label });
            e.HasIndex(r => new { r.PartitionId, r.SourceFileName });

            // The Records page with nothing typed: the most recently updated records across the flows of a partition, and
            // the most recent of one custody state, read from the end of an index instead of by ordering the whole ledger.
            e.HasIndex(r => new { r.PartitionId, r.UpdatedUtc });
            e.HasIndex(r => new { r.PartitionId, r.Status, r.UpdatedUtc });

            // Which records came from this file, inside a flow, in milliseconds.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.SourceFileName, r.SourceRowNumber });

            // The records the ledger asked to be planned again, paged by the planner each run: the filter keeps the
            // index as small as the backlog.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.PlanRequestedUtc }).HasFilter("[PlanRequestedUtc] IS NOT NULL");

            // The records waiting for an id, released when the record holding that id lands in the same partition: the
            // filter keeps the index as small as what is waiting, so the release every settle runs costs a seek.
            e.HasIndex(r => new { r.PartitionId, r.WaitingFor }).HasFilter("[WaitingFor] IS NOT NULL");

            // The blocked records by the problem that keeps them so: a flow's problems counted with their held and failed
            // records, their newest and oldest, and their files; a problem's records listed newest first and released. The
            // filter keeps the index as small as what is blocked, however many records the ledger holds.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.ProblemHash, r.UpdatedUtc })
                .IncludeProperties(r => new { r.Status, r.PendingSourceFileName })
                .HasFilter("[ProblemHash] IS NOT NULL");

            // The blocked records not sorted into a problem yet: held or failed before the ledger kept problems, or by a
            // completion an older build appended. The control plane's backfill reads and sorts them from here, so the index
            // is empty but for that backlog. A query reaches it only by repeating the filter, which the backfill names.
            e.HasIndex(r => new { r.PartitionId, r.FlowId }, UnsortedProblemIndex)
                .HasFilter("[ProblemHash] IS NULL AND [Blocked]=(1) AND ([Status] IN (N'held', N'failed'))");

            // A flow's records by what the last check of their documents came to: counted for the flow's page, and the
            // invalid or unverified ones listed. The filter leaves out the records no check has reached.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.ValidationOutcome, r.UpdatedUtc })
                .HasFilter("[ValidationOutcome] IS NOT NULL");

            // A flow's records whose last checked document failed an assertion of its mapping: counted for the flow's page and
            // listed. The filter leaves out the records that met every assertion, or whose mapping states none.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.AssertionFailures, r.UpdatedUtc })
                .HasFilter("[AssertionFailures]>(0)");
        });

        modelBuilder.Entity<DeliveryRecordIdentity>(e =>
        {
            e.ToTable("RecordIdentity", SchemaName);
            // One row per token per record: the partition, then the token, so the row is written and read by what it
            // identifies within the partition the lookup reads.
            e.HasKey(i => new { i.PartitionId, i.Token, i.FlowId, i.DeliveryKey });
            e.Property(i => i.Token).HasMaxLength(MaxIdentityTokenLength).IsRequired();
            e.Property(i => i.Display).HasMaxLength(MaxIdentityTokenLength).IsRequired();
            e.Property(i => i.Kind).HasMaxLength(16).IsRequired();

            // The lookup: any identifier an operator holds, as a prefix, across the flows of a partition. The primary key
            // answers it, and the record it names is read by joining to the record's own key.
            //
            // The other direction: a record's own tokens, to rewrite or delete them when its identity changes.
            e.HasIndex(i => new { i.PartitionId, i.FlowId, i.DeliveryKey });

            // The same lookup narrowed to one flow (the Records page's flow filter): its candidates are that flow's own
            // tokens, so a prefix many other flows share cannot use up the candidate bound before this flow is reached.
            e.HasIndex(i => new { i.PartitionId, i.FlowId, i.Token });
        });

        modelBuilder.Entity<DeliveryAttempt>(e =>
        {
            e.ToTable("Attempt", SchemaName);
            // Append-only: the ever-increasing id within each partition, so every drain appends at the end of its range.
            e.HasKey(a => new { a.PartitionId, a.AttemptId });
            e.Property(a => a.AttemptId).ValueGeneratedOnAdd();
            e.Property(a => a.Worker).HasMaxLength(200).IsRequired();
            e.Property(a => a.Outcome).HasMaxLength(16).IsRequired();
            e.Property(a => a.Phase).HasMaxLength(32).IsRequired();
            e.Property(a => a.MetadataHash).HasMaxLength(64);
            e.Property(a => a.PayloadHash).HasMaxLength(64);
            e.Property(a => a.Error).HasMaxLength(2000);
            e.Property(a => a.SourceFileName).HasMaxLength(MaxSourceFileNameLength);
            // A record's timeline, and the later attempt pruning looks for beside each one it removes.
            e.HasIndex(a => new { a.PartitionId, a.FlowId, a.DeliveryKey, a.StartedUtc });
            // Pruning by age, across every partition: retention is the estate's, not a partition's.
            e.HasIndex(a => a.StartedUtc);
            // A submission's attempts, and the records they settled by outcome, counted when the submission closes: the
            // count reads this index alone, however many attempts a full plan wrote.
            e.HasIndex(a => new { a.SubmissionId, a.Outcome, a.Phase }).IncludeProperties(a => a.DeliveryKey);
            // A run's records: the listing's run filter seeks the run and joins on the record's key without reading the attempt.
            e.HasIndex(a => new { a.RunId, a.PartitionId, a.FlowId, a.DeliveryKey });
        });

        modelBuilder.Entity<DeliveryWorkBatch>(e =>
        {
            e.ToTable("WorkBatch", SchemaName);
            e.HasKey(b => new { b.PartitionId, b.SubmissionId, b.Index });
            e.Property(b => b.Location).HasMaxLength(2000).IsRequired();
            e.Property(b => b.Status).HasMaxLength(16).IsRequired();
            e.Property(b => b.LeaseOwner).HasMaxLength(200);
            e.Property(b => b.Error).HasMaxLength(2000);
            // The claim: the oldest queued batch of a flow (or a submission).
            e.HasIndex(b => new { b.PartitionId, b.FlowId, b.Status, b.CreatedUtc });
            // A batch named by its submission and place, and a submission's batches in order, counted by status: the batch's
            // identity is unique on its own.
            e.HasIndex(b => new { b.SubmissionId, b.Index }).IsUnique().IncludeProperties(b => b.Status);
        });

        modelBuilder.Entity<DeliveryLease>(e =>
        {
            e.ToTable("Lease", SchemaName);
            e.HasKey(l => new { l.PartitionId, l.Token });
            e.Property(l => l.Token).HasMaxLength(MaxLeaseTokenLength);
            e.Property(l => l.Owner).HasMaxLength(MaxLeaseTokenLength).IsRequired();
            // A lease named by its token: a renewal, a checkpoint, a close, a record's holder.
            e.HasIndex(l => l.Token).IsUnique();
            // The sweep: a flow's leases that ran out. A submission's leases: what its run waits on.
            e.HasIndex(l => new { l.PartitionId, l.FlowId, l.ExpiresUtc });
            e.HasIndex(l => new { l.SubmissionId, l.ExpiresUtc });
        });

        modelBuilder.Entity<DeliveryRecordEvent>(e =>
        {
            e.ToTable("RecordEvent", SchemaName);
            // Clustered on an ever-increasing id within each partition: every worker only appends, at the end of the
            // partition's range.
            e.HasKey(v => new { v.PartitionId, v.EventId });
            e.Property(v => v.EventId).ValueGeneratedOnAdd();
            e.Property(v => v.LeaseToken).HasMaxLength(MaxLeaseTokenLength).IsRequired();
            e.Property(v => v.Kind).HasMaxLength(16).IsRequired();
            e.Property(v => v.Status).HasMaxLength(16);
            e.Property(v => v.Error).HasMaxLength(2000);
            e.Property(v => v.ValidationOutcome).HasMaxLength(16);
            e.Property(v => v.TargetId).HasMaxLength(500);
            e.Property(v => v.ClaimDocumentRef).HasMaxLength(64);
            e.Property(v => v.ClaimSourceFingerprint).HasMaxLength(200);
            e.Property(v => v.ClaimSourceFileName).HasMaxLength(MaxSourceFileNameLength);
            e.Property(v => v.ClaimMetadataHash).HasMaxLength(64);
            e.Property(v => v.ClaimPayloadHash).HasMaxLength(64);
            e.Property(v => v.IncompleteMissing).HasMaxLength(MaxIncompleteMissingLength);
            e.Property(v => v.RenderCommit).HasMaxLength(MaxCommitLength);
            // What a lease applies: its events, record by record, the latest last.
            e.HasIndex(v => new { v.LeaseToken, v.FlowId, v.DeliveryKey, v.EventId });
            // A flow's recovery: its events old enough that only a lease that is gone can have left them.
            e.HasIndex(v => new { v.PartitionId, v.FlowId, v.AtUtc }).IncludeProperties(v => v.LeaseToken);
        });

        modelBuilder.Entity<DeliverySourceWatermark>(e =>
        {
            e.ToTable("SourceWatermark", SchemaName);
            e.HasKey(w => new { w.PartitionId, w.FlowId, w.Scope });
            e.Property(w => w.Scope).HasMaxLength(400);
            e.Property(w => w.ContextHash).HasMaxLength(64);
        });

        modelBuilder.Entity<DeliveryActivity>(e =>
        {
            e.ToTable("Activity", SchemaName);
            e.HasKey(a => new { a.PartitionId, a.ActivityId });
            e.Property(a => a.ActivityId).ValueGeneratedOnAdd();
            e.Property(a => a.FlowName).HasMaxLength(200).IsRequired();
            e.Property(a => a.Kind).HasMaxLength(32).IsRequired();
            e.Property(a => a.Actor).HasMaxLength(200).IsRequired();
            e.Property(a => a.Outcome).HasMaxLength(16).IsRequired();
            e.Property(a => a.Summary).HasMaxLength(2000);
            // An entry named by its id: the audit trail's detail, and the run that completes it.
            e.HasIndex(a => a.ActivityId).IsUnique();
            e.HasIndex(a => new { a.PartitionId, a.FlowId, a.StartedUtc });
            e.HasIndex(a => new { a.PartitionId, a.FlowId, a.DeliveryKey, a.StartedUtc });
            e.HasIndex(a => a.SubmissionId);
            e.HasIndex(a => a.RunId);
            // The audit trail of a partition, newest first, and filtered by action or by actor.
            e.HasIndex(a => new { a.PartitionId, a.Kind, a.StartedUtc });
            e.HasIndex(a => new { a.PartitionId, a.Actor, a.StartedUtc });
            e.HasIndex(a => new { a.PartitionId, a.StartedUtc });
            // The trail as it opens, without the idle runs, and the count of those it leaves out: a partition whose schedules
            // fire every hour holds mostly idle runs, so both are a seek rather than a walk past them.
            e.HasIndex(a => new { a.PartitionId, a.Idle, a.StartedUtc });
        });

        modelBuilder.Entity<DeliveryActivityRecord>(e =>
        {
            e.ToTable("ActivityRecord", SchemaName);
            // A record's requests are one seek of its key, in the order the trail numbered them.
            e.HasKey(a => new { a.PartitionId, a.FlowId, a.DeliveryKey, a.ActivityId });
        });

        modelBuilder.Entity<DeliveryPurgedRecord>(e =>
        {
            e.ToTable("PurgedRecord", SchemaName);
            e.HasKey(p => new { p.PartitionId, p.PurgedRecordId });
            e.Property(p => p.PurgedRecordId).ValueGeneratedOnAdd();
            e.Property(p => p.SourceKey).HasMaxLength(400).IsRequired();
            e.Property(p => p.SourceKeyJson).HasMaxLength(2000);
            e.Property(p => p.Label).HasMaxLength(400);
            OptionalOsduId(e.Property(p => p.TargetId)).HasMaxLength(500);
            e.Property(p => p.PurgedBy).HasMaxLength(200).IsRequired();
            // The deleted records whose rows wait to be read again, paged by each run beside the records asked to be planned
            // again, in the same order: the filter keeps the index as small as what waits.
            e.HasIndex(p => new { p.PartitionId, p.FlowId, p.PlanRequestedUtc, p.DeliveryKey })
                .IncludeProperties(p => p.SourceKeyJson)
                .HasFilter("[PlanRequestedUtc] IS NOT NULL");
            // A record's page asks by its key what became of a record the ledger no longer holds, and a lookup by OSDU id finds
            // which record went under it.
            e.HasIndex(p => new { p.PartitionId, p.FlowId, p.DeliveryKey });
            e.HasIndex(p => new { p.PartitionId, p.TargetId }).HasFilter("[TargetId] IS NOT NULL");
            // What a ledger deleted lately, by when: an assertion run asks it before it judges what the search index lists, which
            // can still list a record a removal took out of OSDU moments before.
            e.HasIndex(p => new { p.PartitionId, p.FlowId, p.PurgedUtc }).IncludeProperties(p => p.TargetId);
        });

        modelBuilder.Entity<DeliveryArtifact>(e =>
        {
            e.ToTable("Artifact", SchemaName);
            e.HasKey(a => new { a.PartitionId, a.ArtifactId });
            e.Property(a => a.ArtifactId).ValueGeneratedOnAdd();
            e.Property(a => a.Slot).HasMaxLength(MaxArtifactSlotLength).IsRequired();
            e.Property(a => a.Role).HasMaxLength(16).IsRequired();
            OptionalOsduId(e.Property(a => a.TargetId)).HasMaxLength(MaxTargetIdLength);
            e.Property(a => a.Locator).HasMaxLength(MaxArtifactLocatorLength);
            e.Property(a => a.State).HasMaxLength(16).IsRequired();
            e.Property(a => a.Note).HasMaxLength(MaxArtifactNoteLength);
            e.Property(a => a.SettledBy).HasMaxLength(200);
            // One row per slot of a unit: an intent completed by its id, and a step reported again by a resumed try, update it.
            // Its prefix is a record's artifacts, one seek for the record's page and for a claim.
            e.HasIndex(a => new { a.PartitionId, a.FlowId, a.DeliveryKey, a.UnitId, a.Slot }).IsUnique();
            // Every id a delivery minted, by OSDU id: what the inventory joins with what OSDU serves, and a lookup by id.
            e.HasIndex(a => new { a.PartitionId, a.TargetId })
                .HasFilter("[TargetId] IS NOT NULL")
                .IncludeProperties(a => new { a.State, a.Role, a.FlowId, a.DeliveryKey });
            // What is still to undo, and what an abandoned unit left: the sweep reads only these, and the flow's counts and its
            // records with an undo to finish are read from it alone, however many an outage left open.
            e.HasIndex(a => new { a.PartitionId, a.FlowId, a.State, a.NextUndoUtc })
                .HasDatabaseName(OpenArtifactIndex)
                .HasFilter("[State] IN (N'intent', N'pending', N'due', N'failed')")
                .IncludeProperties(a => new { a.DeliveryKey, a.UnitId, a.UndoAttempts, a.CreatedUtc });
        });

        modelBuilder.Entity<DeliveryInventory>(e =>
        {
            e.ToTable("Inventory", SchemaName);
            e.HasKey(i => new { i.PartitionId, i.InventoryId });
            e.Property(i => i.InventoryId).ValueGeneratedOnAdd();
            e.Property(i => i.FlowName).HasMaxLength(DeliveryLedger.MaxFlowNameLength).IsRequired();
            e.Property(i => i.Name).HasMaxLength(200).IsRequired();
            e.Property(i => i.Kind).HasMaxLength(MaxInventoryKindLength).IsRequired();
            e.Property(i => i.Query).HasMaxLength(MaxInventoryQueryLength);
            e.Property(i => i.ReadMode).HasMaxLength(16).IsRequired();
            e.Property(i => i.Versions).HasMaxLength(16).IsRequired();
            e.Property(i => i.OwnersSource).HasMaxLength(16);
            // One row per inventory of a flow's ledger in the partition: a build finds its own by flow and name.
            e.HasIndex(i => new { i.PartitionId, i.FlowId, i.Name }).IsUnique();
        });

        modelBuilder.Entity<DeliveryInventoryRun>(e =>
        {
            e.ToTable("InventoryRun", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.InventoryRunId });
            e.Property(r => r.InventoryRunId).ValueGeneratedOnAdd();
            e.Property(r => r.Operation).HasMaxLength(16).IsRequired();
            e.Property(r => r.Actor).HasMaxLength(200).IsRequired();
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.ReadMode).HasMaxLength(16).IsRequired();
            e.Property(r => r.Error).HasMaxLength(4000);
            // An inventory's runs, newest first: its page and its next build's look at the last ones.
            e.HasIndex(r => new { r.PartitionId, r.InventoryId, r.InventoryRunId });
        });

        modelBuilder.Entity<DeliveryInventoryRecord>(e =>
        {
            e.ToTable("InventoryRecord", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.InventoryRecordId });
            e.Property(r => r.InventoryRecordId).ValueGeneratedOnAdd();
            OsduId(e.Property(r => r.TargetId)).HasMaxLength(MaxTargetIdLength);
            e.Property(r => r.Name).HasMaxLength(MaxInventoryNameLength);
            e.Property(r => r.Kind).HasMaxLength(MaxInventoryKindLength);
            e.Property(r => r.CreateUser).HasMaxLength(MaxInventoryUserLength);
            e.Property(r => r.ModifyUser).HasMaxLength(MaxInventoryUserLength);
            e.Property(r => r.Finding).HasMaxLength(16).IsRequired();
            e.Property(r => r.LedgerStatus).HasMaxLength(16);
            e.Property(r => r.ArtifactState).HasMaxLength(16);
            e.Property(r => r.Detail).HasMaxLength(MaxInventoryDetailLength);
            // One row per id of an inventory: the merge seeks it by id.
            e.HasIndex(r => new { r.PartitionId, r.InventoryId, r.TargetId }).IsUnique();
            // An inventory's records of one finding, in order: the report's grid pages through them, and its counts are read here.
            e.HasIndex(r => new { r.PartitionId, r.InventoryId, r.Finding, r.InventoryRecordId });
            // An inventory's records in order, whatever their finding: the report's grid of every id and the export page through
            // them, seeking the inventory rather than walking every inventory of the partition.
            e.HasIndex(r => new { r.PartitionId, r.InventoryId, r.InventoryRecordId });
            // An OSDU id across every inventory of the partition: a lookup by id answers what each inventory found of it.
            e.HasIndex(r => new { r.PartitionId, r.TargetId }).IncludeProperties(r => new { r.InventoryId, r.Finding, r.GoneUtc });
        });

        modelBuilder.Entity<DeliveryInventoryVersion>(e =>
        {
            e.ToTable("InventoryVersion", SchemaName);
            e.HasKey(v => new { v.PartitionId, v.InventoryRecordId, v.Version });
        });

        modelBuilder.Entity<DeliveryInventoryRemoval>(e =>
        {
            e.ToTable("InventoryRemoval", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.InventoryRemovalId });
            e.Property(r => r.InventoryRemovalId).ValueGeneratedOnAdd();
            e.Property(r => r.Actor).HasMaxLength(200).IsRequired();
            e.Property(r => r.Finding).HasMaxLength(16).IsRequired();
            e.Property(r => r.Scope).HasMaxLength(16).IsRequired();
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.Error).HasMaxLength(4000);
            // An inventory's removals, newest first: its page lists them, and a removal closes the earlier ones its process left open.
            e.HasIndex(r => new { r.PartitionId, r.InventoryId, r.InventoryRemovalId });
        });

        modelBuilder.Entity<DeliveryInventoryRemovalItem>(e =>
        {
            e.ToTable("InventoryRemovalItem", SchemaName);
            e.HasKey(i => new { i.PartitionId, i.InventoryRemovalItemId });
            e.Property(i => i.InventoryRemovalItemId).ValueGeneratedOnAdd();
            OsduId(e.Property(i => i.TargetId)).HasMaxLength(MaxTargetIdLength);
            e.Property(i => i.Finding).HasMaxLength(16).IsRequired();
            e.Property(i => i.Outcome).HasMaxLength(16).IsRequired();
            e.Property(i => i.Reason).HasMaxLength(1000);
            // A removal's ids in order, and those of one outcome in order: its page pages through either.
            e.HasIndex(i => new { i.PartitionId, i.InventoryRemovalId, i.InventoryRemovalItemId });
            e.HasIndex(i => new { i.PartitionId, i.InventoryRemovalId, i.Outcome, i.InventoryRemovalItemId });
            // An OSDU id across every removal of the partition: a lookup by id answers what was removed of it, by whom and when.
            e.HasIndex(i => new { i.PartitionId, i.TargetId }).IncludeProperties(i => new { i.InventoryRemovalId, i.Outcome });
        });

        modelBuilder.Entity<DeliveryInventorySelection>(e =>
        {
            e.ToTable("InventorySelection", SchemaName);
            e.HasKey(s => new { s.PartitionId, s.InventorySelectionId });
            e.Property(s => s.InventorySelectionId).ValueGeneratedOnAdd();
            e.Property(s => s.Actor).HasMaxLength(200).IsRequired();
            e.Property(s => s.ConditionsJson).IsRequired();
            e.Property(s => s.ConditionsKey).HasMaxLength(SelectionKeyLength).IsUnicode(false).IsRequired();
            e.Property(s => s.FiltersJson).IsRequired();
            e.Property(s => s.Status).HasMaxLength(16).IsRequired();
            e.Property(s => s.Direction).HasMaxLength(16);
            e.Property(s => s.ColumnsJson).HasMaxLength(4000);
            e.Property(s => s.Error).HasMaxLength(4000);
            // The same conditions asked again of an inventory find the newest match made of them.
            e.HasIndex(s => new { s.PartitionId, s.InventoryId, s.ConditionsKey, s.InventorySelectionId });
            // The matches a sweep retires, oldest first.
            e.HasIndex(s => new { s.PartitionId, s.CreatedUtc });
        });

        modelBuilder.Entity<DeliveryInventorySelectionItem>(e =>
        {
            e.ToTable("InventorySelectionItem", SchemaName);
            // A selection's ids in the inventory's own order: the grid pages through them, and a removal joins them by row.
            e.HasKey(i => new { i.PartitionId, i.InventorySelectionId, i.InventoryRecordId });
            e.Property(i => i.ValuesJson).HasMaxLength(MaxSelectionValuesLength);
        });

        modelBuilder.Entity<DeliveryInventoryDuplicateCheck>(e =>
        {
            e.ToTable("InventoryDuplicateCheck", SchemaName);
            e.HasKey(c => new { c.PartitionId, c.InventoryDuplicateCheckId });
            e.Property(c => c.InventoryDuplicateCheckId).ValueGeneratedOnAdd();
            e.Property(c => c.Actor).HasMaxLength(200).IsRequired();
            e.Property(c => c.KeyJson).IsRequired();
            e.Property(c => c.KeyHash).HasMaxLength(SelectionKeyLength).IsUnicode(false).IsRequired();
            e.Property(c => c.TableName).HasMaxLength(MaxRunTimeTableNameLength);
            e.Property(c => c.KeyedBy).HasMaxLength(200);
            e.Property(c => c.Status).HasMaxLength(16).IsRequired();
            e.Property(c => c.Phase).HasMaxLength(16);
            e.Property(c => c.Error).HasMaxLength(4000);
            // An inventory's checks, newest first: the tab lists them, and a key asked again groups the newest.
            e.HasIndex(c => new { c.PartitionId, c.InventoryId, c.InventoryDuplicateCheckId });
            // The checks a sweep retires, oldest first.
            e.HasIndex(c => new { c.PartitionId, c.CreatedUtc });
        });

        modelBuilder.Entity<DeliveryInventoryDuplicateGroup>(e =>
        {
            e.ToTable("InventoryDuplicateGroup", SchemaName);
            // A check's groups in order, largest first: the list pages through them, and the copies pass reads them so.
            e.HasKey(g => new { g.PartitionId, g.InventoryDuplicateCheckId, g.GroupNumber });
            e.Property(g => g.KeyHash).HasMaxLength(DuplicateKeyHashLength).IsFixedLength().IsRequired();
            e.Property(g => g.ValuesJson).HasMaxLength(MaxSelectionValuesLength);
            e.Property(g => g.Outcome).HasMaxLength(16).IsRequired();
            e.Property(g => g.Reason).HasMaxLength(MaxDuplicateReasonLength);
            e.Property(g => g.ChosenBy).HasMaxLength(200);
            // The groups of one outcome in order: the list's tabs page through them.
            e.HasIndex(g => new { g.PartitionId, g.InventoryDuplicateCheckId, g.Outcome, g.GroupNumber });
        });

        modelBuilder.Entity<DeliveryInventoryDuplicateMember>(e =>
        {
            e.ToTable("InventoryDuplicateMember", SchemaName);
            // A check's copies in the inventory's own order: the grouping gives them their values a chunk at a time, and a
            // grouping again, a failed check and a sweep delete them a batch at a time.
            e.HasKey(m => new { m.PartitionId, m.InventoryDuplicateCheckId, m.InventoryRecordId });
            e.Property(m => m.KeyHash).HasMaxLength(DuplicateKeyHashLength).IsFixedLength().IsRequired();
            e.Property(m => m.ValuesJson).HasMaxLength(MaxSelectionValuesLength);
            e.Property(m => m.StorageState).HasMaxLength(16);
            e.Property(m => m.Role).HasMaxLength(16);
            e.Property(m => m.Reason).HasMaxLength(MaxDuplicateReasonLength);
            // A group's copies in order: the list shows them, a pick reads them so, and a selection is made of them.
            e.HasIndex(m => new { m.PartitionId, m.InventoryDuplicateCheckId, m.GroupNumber, m.InventoryRecordId });
        });

        modelBuilder.Entity<DeliveryInventoryScan>(e =>
        {
            e.ToTable("InventoryScan", SchemaName);
            e.HasKey(s => new { s.PartitionId, s.ScanId });
            e.Property(s => s.ScanId).ValueGeneratedOnAdd();
            OsduId(e.Property(s => s.TargetId)).HasMaxLength(MaxTargetIdLength);
            e.Property(s => s.Name).HasMaxLength(MaxInventoryNameLength);
            e.Property(s => s.Kind).HasMaxLength(MaxInventoryKindLength);
            e.Property(s => s.CreateUser).HasMaxLength(MaxInventoryUserLength);
            e.Property(s => s.ModifyUser).HasMaxLength(MaxInventoryUserLength);
            // A build's ids by id: its merge reads them in this order, and a build's leftovers are deleted by run.
            e.HasIndex(s => new { s.PartitionId, s.InventoryRunId, s.TargetId });
        });

        modelBuilder.Entity<DeliveryDownload>(e =>
        {
            e.ToTable("Download", SchemaName);
            e.HasKey(d => new { d.PartitionId, d.DownloadId });
            e.Property(d => d.DownloadId).ValueGeneratedOnAdd();
            e.Property(d => d.FlowName).HasMaxLength(DeliveryLedger.MaxFlowNameLength).IsRequired();
            e.Property(d => d.Name).HasMaxLength(200).IsRequired();
            e.Property(d => d.Kind).HasMaxLength(MaxInventoryKindLength).IsRequired();
            e.Property(d => d.Query).HasMaxLength(MaxInventoryQueryLength);
            e.Property(d => d.ReadMode).HasMaxLength(16).IsRequired();
            e.Property(d => d.Location).HasMaxLength(MaxDownloadLocationLength);
            // One row per download of a flow's ledger in the partition: a run finds its own by flow and name.
            e.HasIndex(d => new { d.PartitionId, d.FlowId, d.Name }).IsUnique();
        });

        modelBuilder.Entity<DeliveryDownloadRun>(e =>
        {
            e.ToTable("DownloadRun", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.DownloadRunId });
            e.Property(r => r.DownloadRunId).ValueGeneratedOnAdd();
            e.Property(r => r.Actor).HasMaxLength(200).IsRequired();
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.ReadMode).HasMaxLength(16).IsRequired();
            e.Property(r => r.Location).HasMaxLength(MaxDownloadLocationLength).IsRequired();
            e.Property(r => r.Rewrite).HasMaxLength(16);
            e.Property(r => r.Error).HasMaxLength(4000);
            // A download's runs, newest first: its page lists them, and a run closes the earlier ones its process left open.
            e.HasIndex(r => new { r.PartitionId, r.DownloadId, r.DownloadRunId });
        });

        modelBuilder.Entity<DeliveryDownloadRecord>(e =>
        {
            e.ToTable("DownloadRecord", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.DownloadRecordId });
            e.Property(r => r.DownloadRecordId).ValueGeneratedOnAdd();
            OsduId(e.Property(r => r.TargetId)).HasMaxLength(MaxTargetIdLength);
            e.Property(r => r.Kind).HasMaxLength(MaxInventoryKindLength);
            // A file name is compared ignoring case, as the folders of Windows and macOS compare it, so the unique index below
            // never lets two ids of a download share a file there.
            e.Property(r => r.FileName).HasMaxLength(MaxDownloadFileNameLength).UseCollation(SearchCollation).IsRequired();
            e.Property(r => r.Sha256).HasMaxLength(DownloadHashLength).IsFixedLength();
            e.Property(r => r.Problem).HasMaxLength(MaxDownloadProblemLength);
            // One row per id of a download: the merge seeks it by id.
            e.HasIndex(r => new { r.PartitionId, r.DownloadId, r.TargetId }).IsUnique();
            // One id per file of a download, whatever the case of its name: the merge gives a new id the name no other holds.
            e.HasIndex(r => new { r.PartitionId, r.DownloadId, r.FileName }).IsUnique();
            // A download's records in order, with what says whether each is due: a run walks them for those it writes, and the
            // page's grid pages through them.
            e.HasIndex(r => new { r.PartitionId, r.DownloadId, r.DownloadRecordId })
                .IncludeProperties(r => new { r.TargetId, r.Version, r.WrittenVersion, r.GoneUtc, r.FileName });
            // An OSDU id across every download of the partition: a lookup by id answers which downloads wrote it, and where.
            e.HasIndex(r => new { r.PartitionId, r.TargetId }).IncludeProperties(r => new { r.DownloadId, r.FileName, r.WrittenVersion, r.GoneUtc });
        });

        modelBuilder.Entity<DeliveryDownloadScan>(e =>
        {
            e.ToTable("DownloadScan", SchemaName);
            e.HasKey(s => new { s.PartitionId, s.ScanId });
            e.Property(s => s.ScanId).ValueGeneratedOnAdd();
            OsduId(e.Property(s => s.TargetId)).HasMaxLength(MaxTargetIdLength);
            e.Property(s => s.Kind).HasMaxLength(MaxInventoryKindLength);
            e.Property(s => s.PlainName).HasMaxLength(MaxDownloadFileNameLength).UseCollation(SearchCollation);
            e.Property(s => s.DistinctName).HasMaxLength(MaxDownloadFileNameLength).UseCollation(SearchCollation).IsRequired();
            // A run's ids by id: its merge reads them in this order, and a run's leftovers are deleted by run.
            e.HasIndex(s => new { s.PartitionId, s.DownloadRunId, s.TargetId });
        });

        modelBuilder.Entity<DeliveryRemovalRun>(e =>
        {
            e.ToTable("RemovalRun", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.RemovalRunId });
            e.Property(r => r.RemovalRunId).ValueGeneratedOnAdd();
            e.Property(r => r.FlowName).HasMaxLength(DeliveryLedger.MaxFlowNameLength).IsRequired();
            e.Property(r => r.Entry).HasMaxLength(MaxRemovalNameLength).IsRequired();
            e.Property(r => r.DeliveryFlow).HasMaxLength(MaxRemovalNameLength).IsRequired();
            e.Property(r => r.Operation).HasMaxLength(16).IsRequired();
            e.Property(r => r.Actor).HasMaxLength(200).IsRequired();
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.ApprovedBy).HasMaxLength(200);
            e.Property(r => r.Error).HasMaxLength(4000);
            // A removal's runs, newest first: the flow's page lists them, and a run closes the earlier ones its process left open.
            e.HasIndex(r => new { r.PartitionId, r.FlowName, r.Entry, r.RemovalRunId });
            // A delivery flow's removal runs, newest first: its page says what removal flows took out of its ledger.
            e.HasIndex(r => new { r.PartitionId, r.LedgerFlowId, r.RemovalRunId });
        });

        modelBuilder.Entity<DeliveryRemovalKey>(e =>
        {
            e.ToTable("RemovalKey", SchemaName);
            e.HasKey(k => new { k.PartitionId, k.RemovalRunId, k.Side, k.DeliveryKey });
            e.Property(k => k.SourceKeyJson).HasMaxLength(2000);
            e.Property(k => k.FileName).HasMaxLength(MaxSourceFileNameLength);
        });

        modelBuilder.Entity<DeliveryRemovedKey>(e =>
        {
            e.ToTable("RemovedKey", SchemaName);
            e.HasKey(k => new { k.PartitionId, k.FlowId, k.DeliveryKey });
            e.Property(k => k.SourceKeyJson).HasMaxLength(2000);
            e.Property(k => k.Reason).HasMaxLength(16).IsRequired();
            e.Property(k => k.FileName).HasMaxLength(MaxSourceFileNameLength);
            e.Property(k => k.State).HasMaxLength(24).IsRequired();
            e.Property(k => k.Outcome).HasMaxLength(24);
            e.Property(k => k.Detail).HasMaxLength(1000);
            e.Property(k => k.RemovalFlow).HasMaxLength(DeliveryLedger.MaxFlowNameLength).IsRequired();
            e.Property(k => k.Entry).HasMaxLength(MaxRemovalNameLength).IsRequired();
            e.Property(k => k.ApprovedBy).HasMaxLength(200);
            // A ledger's removed keys of one state in key order: a removal takes the pending ones in turn, an approval the ones
            // waiting for it, a page lists either, and the flow's statistics count each.
            e.HasIndex(k => new { k.PartitionId, k.FlowId, k.State, k.DeliveryKey });
            // What one removal run found: its page lists them.
            e.HasIndex(k => new { k.PartitionId, k.DetectedRunId });
        });

        modelBuilder.Entity<DeliveryReversal>(e =>
        {
            e.ToTable("Reversal", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.ReversalId });
            e.Property(r => r.ReversalId).ValueGeneratedOnAdd();
            e.Property(r => r.FlowName).HasMaxLength(200).IsRequired();
            e.Property(r => r.SourceKind).HasMaxLength(16).IsRequired();
            e.Property(r => r.SubmissionsJson).IsRequired();
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.RequestedBy).HasMaxLength(200).IsRequired();
            e.Property(r => r.Error).HasMaxLength(2000);
            // A reversal named by its id alone: a run's payload, a link.
            e.HasIndex(r => r.ReversalId).IsUnique();
            // One reversal per source of a ledger: asking again resumes it rather than opening a second one.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.SourceKind, r.SourceId }).IsUnique();
            // A ledger's reversals, newest first.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.RequestedUtc });
        });

        modelBuilder.Entity<DeliveryReversalItem>(e =>
        {
            e.ToTable("ReversalItem", SchemaName);
            // A reversal's records together, in key order: what its listing adds to and its settlement walks.
            e.HasKey(i => new { i.PartitionId, i.ReversalId, i.DeliveryKey });
            OptionalOsduId(e.Property(i => i.TargetId)).HasMaxLength(500);
            e.Property(i => i.Prior).HasMaxLength(16).IsRequired();
            e.Property(i => i.State).HasMaxLength(16).IsRequired();
            e.Property(i => i.Outcome).HasMaxLength(24);
            e.Property(i => i.Detail).HasMaxLength(2000);
            // The next page of what is still to do, in key order, and the counts by state and outcome, read from this alone.
            e.HasIndex(i => new { i.PartitionId, i.ReversalId, i.State, i.DeliveryKey }).IncludeProperties(i => i.Outcome);
            // A reversal's records by what came of them, a page at a time.
            e.HasIndex(i => new { i.PartitionId, i.ReversalId, i.Outcome, i.DeliveryKey });
        });

        modelBuilder.Entity<DeliveryRetrieval>(e =>
        {
            e.ToTable("Retrieval", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.RetrievalId });
            e.Property(r => r.RetrievalId).ValueGeneratedOnAdd();
            e.Property(r => r.FlowName).HasMaxLength(200).IsRequired();
            e.Property(r => r.Actor).HasMaxLength(200).IsRequired();
            e.Property(r => r.Kinds).HasMaxLength(4000).IsRequired();
            e.Property(r => r.WindowField).HasMaxLength(200);
            e.Property(r => r.Location).HasMaxLength(2000).IsRequired();
            e.Property(r => r.ManifestLocation).HasMaxLength(2000);
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.Error).HasMaxLength(4000);
            // A retrieval named by its id: the run that completes it.
            e.HasIndex(r => r.RetrievalId).IsUnique();
            // The flow's listing, the watermark chain (the last done run), and the run's row.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.StartedUtc });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.Status, r.StartedUtc });
            e.HasIndex(r => r.RunId);
        });

        modelBuilder.Entity<DeliveryAssertionRun>(e =>
        {
            e.ToTable("AssertionRun", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.AssertionRunId });
            e.Property(r => r.AssertionRunId).ValueGeneratedOnAdd();
            e.Property(r => r.FlowName).HasMaxLength(200).IsRequired();
            e.Property(r => r.Actor).HasMaxLength(200).IsRequired();
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.DefinitionsHash).HasMaxLength(64);
            e.Property(r => r.Error).HasMaxLength(4000);
            // A run named by its id: its report, and the results that close it.
            e.HasIndex(r => r.AssertionRunId).IsUnique();
            // The flow's runs in a partition, newest first; and the platform run's row.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.StartedUtc });
            e.HasIndex(r => r.RunId);
        });

        modelBuilder.Entity<DeliveryAssertionResult>(e =>
        {
            e.ToTable("AssertionResult", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.ResultId });
            e.Property(r => r.ResultId).ValueGeneratedOnAdd();
            e.Property(r => r.TestName).HasMaxLength(100).IsRequired();
            e.Property(r => r.Kind).HasMaxLength(400).IsRequired();
            e.Property(r => r.Outcome).HasMaxLength(16).IsRequired();
            e.Property(r => r.Severity).HasMaxLength(16);
            e.Property(r => r.DefinitionHash).HasMaxLength(32).IsRequired();
            e.Property(r => r.Error).HasMaxLength(4000);
            e.Property(r => r.Detail).IsRequired();
            e.HasIndex(r => r.ResultId).IsUnique();
            // A run's report: its results in the order they were recorded.
            e.HasIndex(r => new { r.PartitionId, r.AssertionRunId });
            // One test's history, newest run first, and the latest result of each test of a flow: one seek per test.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.TestName, r.AssertionRunId });
        });

        modelBuilder.Entity<DeliveryDimension>(e =>
        {
            e.ToTable("Dimension", SchemaName);
            e.HasKey(d => new { d.PartitionId, d.DimensionId });
            e.Property(d => d.DimensionId).ValueGeneratedOnAdd();
            e.Property(d => d.FlowName).HasMaxLength(DeliveryLedger.MaxFlowNameLength).IsRequired();
            e.Property(d => d.Name).HasMaxLength(DeliveryDimension.MaxNameLength).IsRequired();
            e.Property(d => d.Description).HasMaxLength(4000);
            e.Property(d => d.Kind).HasMaxLength(400).IsRequired();
            e.Property(d => d.Query).HasMaxLength(4000);
            e.Property(d => d.Path).HasMaxLength(DeliveryDimension.MaxPathLength).IsRequired();
            e.Property(d => d.FieldIndex).HasMaxLength(16);
            e.Property(d => d.NestedPath).HasMaxLength(DeliveryDimension.MaxPathLength);
            e.Property(d => d.AggregateBy).HasMaxLength(DeliveryDimension.MaxAggregateByLength);
            e.Property(d => d.CleanJson).IsRequired();
            e.Property(d => d.DefinitionHash).HasMaxLength(16).IsRequired();
            e.Property(d => d.TableName).HasMaxLength(128);
            e.Property(d => d.TableConnection).HasMaxLength(2000);
            e.Property(d => d.TableDatabase).HasMaxLength(128);
            e.Property(d => d.TableSchema).HasMaxLength(128);
            e.Property(d => d.KeyColumn).HasMaxLength(128);
            e.Property(d => d.ValueColumn).HasMaxLength(128);
            // A dimension named by its id: every member, original, build and change of it names it so.
            e.HasIndex(d => d.DimensionId).IsUnique();
            // A flow's dimension in a partition, by its name: what a build registers and a page finds.
            e.HasIndex(d => new { d.PartitionId, d.FlowId, d.Name }).IsUnique();
            // The dimensions writing one table, one a partition: what a build asks before it writes and a removal before it drops.
            // It holds what the build asks, so the build reads no row of another dimension, which a build of that one may be
            // writing.
            e.HasIndex(d => d.TableName).IncludeProperties(d => new { d.FlowName, d.Name });
        });

        modelBuilder.Entity<DeliveryDimensionRun>(e =>
        {
            e.ToTable("DimensionRun", SchemaName);
            // A dimension's builds together, in the order they were written: every statement over one dimension seeks
            // its own rows and locks no other's, so a build of another dimension writes beside it rather than waiting
            // on, or deadlocking with, them.
            e.HasKey(r => new { r.PartitionId, r.DimensionId, r.DimensionRunId });
            e.Property(r => r.DimensionRunId).ValueGeneratedOnAdd();
            e.Property(r => r.Actor).HasMaxLength(200).IsRequired();
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.Mode).HasMaxLength(16).IsRequired();
            e.Property(r => r.RecordKey).HasMaxLength(DeliveryDimensionRun.MaxRecordKeyLength);
            e.Property(r => r.DefinitionHash).HasMaxLength(16).IsRequired();
            e.Property(r => r.Query).HasMaxLength(4000);
            e.Property(r => r.AggregateBy).HasMaxLength(DeliveryDimension.MaxAggregateByLength);
            e.Property(r => r.Error).HasMaxLength(4000);
            e.HasIndex(r => r.DimensionRunId).IsUnique();
            // A dimension's builds, newest first; and the platform run's builds.
            e.HasIndex(r => new { r.PartitionId, r.DimensionId, r.StartedUtc });
            e.HasIndex(r => r.RunId);
        });

        modelBuilder.Entity<DeliveryDimensionMember>(e =>
        {
            e.ToTable("DimensionMember", SchemaName);
            // A dimension's members together, in the order they were written: every statement over one dimension seeks
            // its own rows and locks no other's, so a build of another dimension writes beside it rather than waiting
            // on, or deadlocking with, them.
            e.HasKey(m => new { m.PartitionId, m.DimensionId, m.MemberId });
            e.Property(m => m.MemberId).ValueGeneratedOnAdd();
            // A clean value is compared exactly, as a filter an application stored compares it.
            ExactText(e.Property(m => m.Value)).HasMaxLength(256).IsRequired();
            e.HasIndex(m => m.MemberId).IsUnique();
            // A member is a clean value of one dimension, once: what a build matches its members by.
            e.HasIndex(m => new { m.PartitionId, m.DimensionId, m.Value }).IsUnique();
            // The members a dimension holds now, and those it held, each read as one range.
            e.HasIndex(m => new { m.PartitionId, m.DimensionId, m.RemovedRunId });
            // The members most records hold first, a page at a time.
            e.HasIndex(m => new { m.PartitionId, m.DimensionId, m.Records, m.Value }).IsDescending(false, false, true, false);
        });

        modelBuilder.Entity<DeliveryDimensionValue>(e =>
        {
            e.ToTable("DimensionValue", SchemaName);
            // A dimension's keys together, in the order they were written: every statement over one dimension seeks its
            // own rows and locks no other's, so a build of another dimension writes beside it rather than waiting on,
            // or deadlocking with, them.
            e.HasKey(v => new { v.PartitionId, v.DimensionId, v.ValueId });
            e.Property(v => v.ValueId).ValueGeneratedOnAdd();
            ExactText(e.Property(v => v.Original)).HasMaxLength(DeliveryDimensionValue.MaxKeyLength).IsRequired();
            e.Property(v => v.OriginalBytes).HasComputedColumnSql("CAST(DATALENGTH([Original]) AS smallint)", stored: true);
            e.Property(v => v.ElementsHash).HasMaxLength(32).IsFixedLength();
            e.Property(v => v.AttributesHash).HasMaxLength(32).IsFixedLength();
            e.Property(v => v.LeftOut).HasMaxLength(16);
            e.Property(v => v.Note).HasMaxLength(400);
            e.Property(v => v.Label).HasMaxLength(DeliveryDimensionValue.MaxLabelLength);
            e.Property(v => v.LabelFrom).HasMaxLength(DeliveryDimensionValue.MaxOriginalLength);
            e.Property(v => v.Filter).HasMaxLength(DeliveryDimensionValue.MaxFilterLength);
            e.HasIndex(v => v.ValueId).IsUnique();
            // An original is one value of one dimension, once, compared exactly, its length beside it: what a build matches the keys it read by.
            e.HasIndex(v => new { v.PartitionId, v.DimensionId, v.Original, v.OriginalBytes }).IsUnique();
            // A member's originals.
            e.HasIndex(v => new { v.PartitionId, v.DimensionId, v.MemberId });
            // The originals most records hold first, a page at a time.
            e.HasIndex(v => new { v.PartitionId, v.DimensionId, v.Count, v.ValueId }).IsDescending(false, false, true, false);
        });

        modelBuilder.Entity<DeliveryDimensionAttributeValue>(e =>
        {
            e.ToTable("DimensionAttribute", SchemaName);
            // A dimension's attribute values together, in the order they were written: every statement over one
            // dimension seeks its own rows and locks no other's, so a build of another dimension writes beside it
            // rather than waiting on, or deadlocking with, them.
            e.HasKey(a => new { a.PartitionId, a.DimensionId, a.AttributeValueId });
            e.Property(a => a.AttributeValueId).ValueGeneratedOnAdd();
            ExactText(e.Property(a => a.Value)).HasMaxLength(DeliveryDimensionAttributeValue.MaxValueLength).IsRequired();
            e.Property(a => a.ValueFrom).HasMaxLength(DeliveryDimensionValue.MaxOriginalLength);
            e.HasIndex(a => a.AttributeValueId).IsUnique();
            // A key's attributes, read with it a page at a time: one row per value, so a collected attribute holds several,
            // and a key holds a value of an attribute once.
            e.HasIndex(a => new { a.PartitionId, a.DimensionId, a.ValueId, a.AttributeId, a.Value }).IsUnique();
            // The keys an attribute value holds (Country is US), and an attribute's values: one seek either way.
            e.HasIndex(a => new { a.PartitionId, a.DimensionId, a.AttributeId, a.Value });
        });

        modelBuilder.Entity<DeliveryDimensionElement>(e =>
        {
            e.ToTable("DimensionElement", SchemaName);
            // A dimension's element fields together, in the order they were written: every statement over one dimension
            // seeks its own rows and locks no other's, so a build of another dimension writes beside it rather than
            // waiting on, or deadlocking with, them.
            e.HasKey(x => new { x.PartitionId, x.DimensionId, x.ElementValueId });
            e.Property(x => x.ElementValueId).ValueGeneratedOnAdd();
            OptionalOsduId(e.Property(x => x.Value)).HasMaxLength(DeliveryDimensionElement.MaxValueLength);
            e.HasIndex(x => x.ElementValueId).IsUnique();
            // A key's objects in their order, each field once: what a build matches its rows by and its table is laid out from.
            e.HasIndex(x => new { x.PartitionId, x.DimensionId, x.ValueId, x.Seq, x.AttributeId }).IsUnique();
        });

        modelBuilder.Entity<DeliveryDimensionView>(e =>
        {
            e.ToTable("DimensionView", SchemaName);
            e.HasKey(v => v.ViewId);
            e.Property(v => v.Name).HasMaxLength(DeliveryDimensionView.MaxNameLength).IsRequired();
            e.Property(v => v.ViewName).HasMaxLength(128).IsRequired();
            e.Property(v => v.FlowName).HasMaxLength(DeliveryLedger.MaxFlowNameLength).IsRequired();
            e.Property(v => v.TableConnection).HasMaxLength(2000);
            e.Property(v => v.TableDatabase).HasMaxLength(128);
            e.Property(v => v.TableSchema).HasMaxLength(128);
            e.Property(v => v.Description).HasMaxLength(DeliveryDimensionView.MaxDescriptionLength);
            e.Property(v => v.DeclarationJson).IsRequired();
            e.Property(v => v.Sql).IsRequired();
            e.Property(v => v.SqlHash).HasMaxLength(64);
            e.Property(v => v.TablesJson).IsRequired();
            e.Property(v => v.ColumnsJson).IsRequired();
            e.Property(v => v.Note).HasMaxLength(1000);
            e.Property(v => v.WrittenBy).HasMaxLength(200).IsRequired();
            // A view by its name, once in the database: what a build finds its record by, and what refuses a second flow's.
            e.HasIndex(v => v.Name).IsUnique();
            e.HasIndex(v => v.ViewName).IsUnique();
            // A flow's views: what a build drops the undeclared ones of.
            e.HasIndex(v => v.FlowName);
        });

        modelBuilder.Entity<DeliveryDimensionViewCheck>(e =>
        {
            e.ToTable("DimensionViewCheck", SchemaName);
            e.HasKey(c => new { c.PartitionId, c.CheckId });
            e.Property(c => c.CheckId).ValueGeneratedOnAdd();
            e.Property(c => c.Status).HasMaxLength(16).IsRequired();
            e.Property(c => c.Error).HasMaxLength(4000);
            e.Property(c => c.ReadVersion).HasMaxLength(64).IsUnicode(false);
            e.HasIndex(c => c.CheckId).IsUnique();
            // A view's checks of a partition, newest first: its page, and what a build prunes past the newest it keeps.
            e.HasIndex(c => new { c.ViewId, c.PartitionId, c.CheckedUtc });
        });

        modelBuilder.Entity<DeliveryDimensionAttributeName>(e =>
        {
            e.ToTable("DimensionAttributeName", SchemaName);
            // A dimension's attributes together, in the order they were written: every statement over one dimension
            // seeks its own rows and locks no other's, so a build of another dimension writes beside it rather than
            // waiting on, or deadlocking with, them.
            e.HasKey(n => new { n.PartitionId, n.DimensionId, n.AttributeId });
            e.Property(n => n.AttributeId).ValueGeneratedOnAdd();
            ExactText(e.Property(n => n.Name)).HasMaxLength(DeliveryDimensionAttributeValue.MaxNameLength).IsRequired();
            e.HasIndex(n => n.AttributeId).IsUnique();
            // A dimension's attribute by its name, once: what a build finds the number by.
            e.HasIndex(n => new { n.PartitionId, n.DimensionId, n.Name }).IsUnique();
        });

        modelBuilder.Entity<DeliveryDimensionCollectedText>(e =>
        {
            e.ToTable("DimensionCollectedText", SchemaName);
            // A dimension's collected texts together, in the order they were written: every statement over one
            // dimension seeks its own rows and locks no other's, so a build of another dimension writes beside it
            // rather than waiting on, or deadlocking with, them.
            e.HasKey(t => new { t.PartitionId, t.DimensionId, t.TextId });
            e.Property(t => t.TextId).ValueGeneratedOnAdd();
            ExactText(e.Property(t => t.Text)).HasMaxLength(DeliveryDimensionValue.MaxKeyLength).IsRequired();
            e.Property(t => t.TextBytes).HasComputedColumnSql("CAST(DATALENGTH([Text]) AS smallint)", stored: true);
            ExactText(e.Property(t => t.Value)).HasMaxLength(DeliveryDimensionAttributeValue.MaxValueLength).IsRequired();
            e.HasIndex(t => t.TextId).IsUnique();
            // A text of a collected attribute, once, compared exactly, its length beside it.
            e.HasIndex(t => new { t.PartitionId, t.DimensionId, t.AttributeId, t.Text, t.TextBytes }).IsUnique();
            // The texts of the values a search picks: one seek.
            e.HasIndex(t => new { t.PartitionId, t.DimensionId, t.AttributeId, t.Value });
        });

        modelBuilder.Entity<DeliveryDimensionKeyRecord>(e =>
        {
            e.ToTable("DimensionKeyRecord", SchemaName);
            // A dimension's keys' records together, in the order they were written: every statement over one dimension
            // seeks its own rows and locks no other's, so a build of another dimension writes beside it rather than
            // waiting on, or deadlocking with, them.
            e.HasKey(x => new { x.PartitionId, x.DimensionId, x.KeyRecordId });
            e.Property(x => x.KeyRecordId).ValueGeneratedOnAdd();
            ExactText(e.Property(x => x.RecordId)).HasMaxLength(DeliveryDimensionValue.MaxKeyLength).IsRequired();
            ExactText(e.Property(x => x.EntityType)).HasMaxLength(DeliveryDimensionKeyRecord.MaxEntityTypeLength).IsRequired();
            e.HasIndex(x => x.KeyRecordId).IsUnique();
            // A key's records, once each: what a write rewrites them by.
            e.HasIndex(x => new { x.PartitionId, x.DimensionId, x.ValueId, x.RecordId }).IsUnique();
            // The keys read through a record that changed: one seek a record.
            e.HasIndex(x => new { x.PartitionId, x.DimensionId, x.RecordId });
            // The entity types a dimension's keys were read through, which an update asks the search what changed in.
            e.HasIndex(x => new { x.PartitionId, x.DimensionId, x.EntityType });
        });

        modelBuilder.Entity<DeliveryDimensionRecord>(e =>
        {
            e.ToTable("DimensionRecord", SchemaName);
            // A dimension's records' keys together, in the order they were written: every statement over one dimension
            // seeks its own rows and locks no other's, so a build of another dimension writes beside it rather than
            // waiting on, or deadlocking with, them.
            e.HasKey(x => new { x.PartitionId, x.DimensionId, x.DimensionRecordId });
            e.Property(x => x.DimensionRecordId).ValueGeneratedOnAdd();
            ExactText(e.Property(x => x.RecordKey)).HasMaxLength(DeliveryDimensionValue.MaxKeyLength).IsRequired();
            e.HasIndex(x => x.DimensionRecordId).IsUnique();
            // A record's keys, once each: what a load rewrites them by and an incremental load reads them by.
            e.HasIndex(x => new { x.PartitionId, x.DimensionId, x.RecordKey, x.ValueId }).IsUnique();
            // The records of a key: what goes with a key a load removes.
            e.HasIndex(x => new { x.PartitionId, x.DimensionId, x.ValueId });
        });

        modelBuilder.Entity<DeliveryDimensionChange>(e =>
        {
            e.ToTable("DimensionChange", SchemaName);
            // A dimension's changes together, in the order they were written: every statement over one dimension seeks
            // its own rows and locks no other's, so a build of another dimension writes beside it rather than waiting
            // on, or deadlocking with, them.
            e.HasKey(c => new { c.PartitionId, c.DimensionId, c.ChangeId });
            e.Property(c => c.ChangeId).ValueGeneratedOnAdd();
            e.Property(c => c.Change).HasMaxLength(16).IsRequired();
            ExactText(e.Property(c => c.Original)).HasMaxLength(DeliveryDimensionValue.MaxOriginalLength).IsRequired();
            OptionalOsduId(e.Property(c => c.FromValue)).HasMaxLength(256);
            OptionalOsduId(e.Property(c => c.ToValue)).HasMaxLength(256);
            e.HasIndex(c => c.ChangeId).IsUnique();
            // An original's history, and a build's changes.
            e.HasIndex(c => new { c.PartitionId, c.DimensionId, c.ValueId, c.ChangeId });
            e.HasIndex(c => new { c.PartitionId, c.DimensionRunId });
        });

        modelBuilder.Entity<DeliveryMapping>(e =>
        {
            e.ToTable("Mapping", SchemaName);
            e.HasKey(m => m.Id);
            e.Property(m => m.Reference).HasMaxLength(200).IsRequired();
            e.Property(m => m.Name).HasMaxLength(150).IsRequired();
            e.Property(m => m.Version).HasMaxLength(50).IsRequired();
            e.Property(m => m.Kind).HasMaxLength(200).IsRequired();
            e.Property(m => m.TemplateVersion).HasMaxLength(64).IsRequired();
            e.Property(m => m.TemplatePath).HasMaxLength(DeliveryRepositoryTemplate.MaxPathLength).IsRequired().HasDefaultValue(string.Empty);
            e.Property(m => m.RelativePath).HasMaxLength(1000).IsRequired();
            e.Property(m => m.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(m => m.Yaml).IsRequired();
            e.Property(m => m.SummaryJson).IsRequired();
            e.Property(m => m.Status).HasMaxLength(16).IsRequired();
            e.Property(m => m.Message).HasMaxLength(4000);
            e.HasIndex(m => new { m.RepoId, m.Reference }).IsUnique();
            e.HasIndex(m => new { m.RepoId, m.Kind });
        });

        modelBuilder.Entity<DeliverySearchTerm>(e =>
        {
            e.ToTable("SearchTerm", SchemaName);
            e.HasKey(t => t.Id);
            e.Property(t => t.TermKey).HasMaxLength(SearchTermKeyLength).IsRequired();
            e.Property(t => t.Source).HasMaxLength(DeliverySearchTerm.MaxSourceLength).IsRequired();
            e.Property(t => t.EntityType).HasMaxLength(DeliverySearchTerm.MaxEntityTypeLength).IsRequired();
            e.Property(t => t.Column).HasMaxLength(DeliverySearchTerm.MaxColumnLength).IsRequired();
            e.Property(t => t.RoutesJson).IsRequired();
            e.Property(t => t.FlowsJson).IsRequired();
            e.HasIndex(t => new { t.RepoId, t.TermId, t.EntityType }).IsUnique();
            // The search terms page and the explorer list the terms of one entity type, from every repository.
            e.HasIndex(t => new { t.EntityType, t.TermId });
        });

        modelBuilder.Entity<DeliverySearchTermRefinement>(e =>
        {
            e.ToTable("SearchTermRefinement", SchemaName);
            e.HasKey(r => r.TermId);
            e.Property(r => r.TermKey).HasMaxLength(SearchTermKeyLength).IsRequired();
            e.Property(r => r.EntityType).HasMaxLength(DeliverySearchTerm.MaxEntityTypeLength).IsRequired();
            e.Property(r => r.Name).HasMaxLength(DeliverySearchTermRefinement.MaxNameLength);
            e.Property(r => r.Route).HasMaxLength(DeliverySearchTermRefinement.MaxRouteLength);
            e.Property(r => r.Note).HasMaxLength(DeliverySearchTermRefinement.MaxNoteLength);
            e.Property(r => r.UpdatedBy).HasMaxLength(200).IsRequired();
            // A name is the term's within its entity type: a rename checks it is free there.
            e.HasIndex(r => new { r.EntityType, r.Name });
        });

        modelBuilder.Entity<DeliveryInterface>(e =>
        {
            e.ToTable("Interface", SchemaName);
            e.HasKey(i => i.Id);
            e.Property(i => i.FlowName).HasMaxLength(400).IsRequired();
            e.Property(i => i.Interface).HasMaxLength(DeliveryInterface.MaxInterfaceLength).IsRequired();
            e.Property(i => i.LedgerName).HasMaxLength(200).IsRequired();
            e.Property(i => i.Route).HasMaxLength(32).IsRequired();
            e.Property(i => i.RouteReason).HasMaxLength(1000);
            e.Property(i => i.MappingReference).HasMaxLength(200).IsRequired();
            e.Property(i => i.CacheVersion).HasMaxLength(64);
            e.Property(i => i.Kind).HasMaxLength(200).IsRequired();
            e.Property(i => i.RecordObject).HasMaxLength(400).IsRequired();
            e.Property(i => i.AfterJson).HasMaxLength(4000).IsRequired();
            e.Property(i => i.RelativePath).HasMaxLength(1000).IsRequired();
            e.Property(i => i.Partition).HasMaxLength(200).IsRequired();
            e.Property(i => i.TargetPartition).HasMaxLength(200).IsRequired().HasDefaultValue(string.Empty);
            e.Property(i => i.DocumentHash).HasMaxLength(64);
            e.HasIndex(i => new { i.RepoId, i.FlowName, i.Interface, i.Partition }).IsUnique();
            // A record's page, a submission's page and the record search find the pipeline behind a ledger identity.
            e.HasIndex(i => i.LedgerFlowId);
        });

        modelBuilder.Entity<DeliveryLedgerFlow>(e =>
        {
            e.ToTable("LedgerFlow", SchemaName);
            e.HasKey(d => d.Id);
            e.Property(d => d.FlowName).HasMaxLength(400).IsRequired();
            e.Property(d => d.Kind).HasMaxLength(DeliveryLedgerFlow.MaxKindLength).IsRequired();
            e.Property(d => d.DocumentHash).HasMaxLength(64).IsRequired();
            e.Property(d => d.RelativePath).HasMaxLength(1000).IsRequired();
            e.HasIndex(d => new { d.RepoId, d.FlowName }).IsUnique();
            // Whether a declared flow keeps the ledgers of a kind and name: the orphaned ledgers, and the pipeline behind one.
            e.HasIndex(d => new { d.Kind, d.FlowName });
        });

        modelBuilder.Entity<DeliveryRepositoryTemplate>(e =>
        {
            e.ToTable("RepositoryTemplate", SchemaName);
            e.HasKey(t => t.Id);
            e.Property(t => t.Path).HasMaxLength(DeliveryRepositoryTemplate.MaxPathLength).IsRequired();
            e.Property(t => t.Folder).HasMaxLength(DeliveryRepositoryTemplate.MaxPathLength).IsRequired();
            e.Property(t => t.Kind).HasMaxLength(200).IsRequired();
            e.Property(t => t.Version).HasMaxLength(64).IsRequired();
            e.Property(t => t.Problem).HasMaxLength(DeliveryRepositoryTemplate.MaxProblemLength);
            e.Property(t => t.Parent).HasMaxLength(200);
            // A sync reconciles one repository's files, and a page reads them to settle that repository's documents.
            e.HasIndex(t => new { t.RepoId, t.Path }).IsUnique();
        });

        modelBuilder.Entity<DeliveryCacheVersion>(e =>
        {
            e.ToTable("CacheVersion", SchemaName);
            // Stored in partition order (the clustered index below), so everything a refresh reads or writes of its
            // partition's versions is a range seek of that partition, and refreshes of different partitions never touch,
            // and so never wait on, each other's rows.
            e.HasKey(v => v.Id).IsClustered(false);
            e.Property(v => v.Scope).HasMaxLength(200).IsRequired();
            e.Property(v => v.FlowName).HasMaxLength(200).IsRequired();
            e.Property(v => v.Version).HasMaxLength(64).IsRequired();
            e.Property(v => v.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(v => v.PreviousVersion).HasMaxLength(64);
            e.Property(v => v.CapturedBy).HasMaxLength(200).IsRequired();
            e.Property(v => v.Origin).HasMaxLength(1000).IsRequired();
            e.Property(v => v.TypesJson).IsRequired();
            e.Property(v => v.SystemPropertiesJson).IsRequired().HasDefaultValue("[]");
            e.Property(v => v.PrunedBy).HasMaxLength(200);
            e.HasIndex(v => new { v.Scope, v.Version }).IsUnique();
            // The sequence is what a concurrent second write of the same partition collides on, so two captures can never
            // both claim the next version. It is the table's clustered key: a partition's versions in order.
            e.HasIndex(v => new { v.Scope, v.Sequence }).IsUnique().IsClustered();
            e.HasIndex(v => v.RunId);
        });

        modelBuilder.Entity<DeliveryCacheItem>(e =>
        {
            e.ToTable("CacheItem", SchemaName);
            // Stored in partition order (the clustered index below) for the same reason as the versions: a refresh reads
            // and closes only its own partition's rows, whatever plan the table's size leads to.
            e.HasKey(i => i.ItemId).IsClustered(false);
            e.HasIndex(i => new { i.Scope, i.ItemId }).IsUnique().IsClustered();
            e.Property(i => i.Scope).HasMaxLength(200).IsRequired();
            e.Property(i => i.TypeName).HasMaxLength(200).IsRequired();
            e.Property(i => i.EntityType).HasMaxLength(200).IsRequired();
            OsduId(e.Property(i => i.RecordId)).HasMaxLength(512).IsRequired();
            e.Property(i => i.FieldsJson).IsRequired();
            e.Property(i => i.FieldsHash).HasColumnType("binary(32)").HasComputedColumnSql(DeliveryCacheItem.FieldsHashSql, stored: false);
            e.Property(i => i.Terms).IsRequired();
            // A record holds one range per distinct content: the type listing is its prefix, so this index answers both a
            // version's listing of a type and one record's history. The end of each range rides along, so a version's
            // listing, count and page of a type are read from the index alone, and only the rows a page shows are looked up.
            e.HasIndex(i => new { i.Scope, i.TypeName, i.RecordId, i.FromSequence }).IsUnique().IncludeProperties(i => new { i.ToSequence });
            // The rows the newest version holds, by type: what a merge compares its capture against, read page by page in
            // record order, with each row's hash in place of its values. ToSequence is included because SQL Server looks a
            // filtered column up in the table to check a query's IS NULL unless the index holds it.
            e.HasIndex(i => new { i.Scope, i.TypeName, i.RecordId })
                .HasFilter("[ToSequence] IS NULL")
                .IncludeProperties(i => new { i.EntityType, i.FieldsHash, i.ToSequence });
            // The closed ranges: what a retention removes, and what the history counts as ended and changed, from the index
            // alone.
            e.HasIndex(i => new { i.Scope, i.ToSequence })
                .HasFilter("[ToSequence] IS NOT NULL")
                .IncludeProperties(i => new { i.FromSequence, i.TypeName, i.RecordId });
            // The rows each version began: what a version changed is counted from them, by the history and by a retention
            // recording a version's changes before it removes the version's records.
            e.HasIndex(i => new { i.Scope, i.FromSequence }).IncludeProperties(i => new { i.TypeName, i.RecordId });
        });

        modelBuilder.Entity<SchemaLineage>(e =>
        {
            e.ToTable("SchemaLineage", SchemaName);
            e.HasKey(l => l.Id);
            e.Property(l => l.Release).HasMaxLength(SchemaLineage.MaxReleaseLength).IsRequired();
            e.Property(l => l.Commit).HasMaxLength(64).IsRequired();
            e.Property(l => l.BuiltBy).HasMaxLength(200).IsRequired();
            e.Property(l => l.FailuresJson).IsRequired();
            // A release is built once: building it again replaces its kinds under the same row.
            e.HasIndex(l => l.Release).IsUnique();
        });

        modelBuilder.Entity<SchemaLineageKind>(e =>
        {
            e.ToTable("SchemaLineageKind", SchemaName);
            e.HasKey(k => new { k.LineageId, k.Kind });
            e.Property(k => k.Kind).HasMaxLength(400).IsRequired();
            e.Property(k => k.EntityType).HasMaxLength(256).IsRequired();
            e.Property(k => k.Version).HasMaxLength(64).IsRequired();
            e.Property(k => k.Status).HasMaxLength(32);
            e.Property(k => k.RelationshipsJson).IsRequired();
            // The lineage page reads a release's kinds by entity type; the OSDU order looks a kind up across releases.
            e.HasIndex(k => new { k.LineageId, k.EntityType });
            e.HasIndex(k => k.Kind);
        });

        modelBuilder.Entity<DeliveryCacheMember>(e =>
        {
            e.ToTable("CacheMember", SchemaName);
            e.Property(m => m.Scope).HasMaxLength(200).IsRequired();
            e.Property(m => m.TypeName).HasMaxLength(200).IsRequired();
            OsduId(e.Property(m => m.RecordId)).HasMaxLength(512).IsRequired();
            e.Property(m => m.FlowName).HasMaxLength(200).IsRequired();
            // A merge reads who holds each record of the captured types, and replaces one flow's rows of a type.
            e.HasKey(m => new { m.Scope, m.TypeName, m.RecordId, m.FlowName });
            e.HasIndex(m => new { m.Scope, m.FlowName, m.TypeName });
        });

        modelBuilder.Entity<DeliveryCacheSet>(e =>
        {
            e.ToTable("CacheSet", SchemaName);
            e.HasKey(c => c.SetId);
            e.Property(c => c.SetHash).HasMaxLength(64).IsRequired();
            e.HasIndex(c => c.SetHash).IsUnique();
            // The planner reads this every run: a filtered index keeps it to the handful of gated sets.
            e.HasIndex(c => c.Gated).HasFilter("[Gated] = 1");
        });

        modelBuilder.Entity<DeliveryCacheSetValue>(e =>
        {
            e.ToTable("CacheSetValue", SchemaName);
            // Kept in the order the values were first read (the identity), so the table's key is eight bytes that only grow.
            e.HasKey(v => v.ValueId);
            e.Property(v => v.Scope).HasMaxLength(200).IsRequired();
            e.Property(v => v.TypeName).HasMaxLength(200).IsRequired();
            OsduId(e.Property(v => v.ItemId)).HasMaxLength(512).IsRequired();
            e.Property(v => v.Path).HasMaxLength(400).IsRequired();
            e.Property(v => v.Kind).HasMaxLength(16).IsRequired();
            e.Property(v => v.ValueHash).HasColumnType("binary(32)").IsRequired();
            e.Property(v => v.ValueText).HasMaxLength(400).IsRequired();
            // A value is kept once. The index's prefix is the impact query: the values read from a cached record of a
            // partition's cache, and, by the prefix of their items, a type's values looked up together.
            e.HasIndex(v => new { v.Scope, v.TypeName, v.ItemId, v.Path, v.Kind, v.ValueHash }).IsUnique();
        });

        modelBuilder.Entity<DeliveryCacheSetEntry>(e =>
        {
            e.ToTable("CacheSetEntry", SchemaName);
            // Kept in the order the sets were written (the identity), so the table's key is eight bytes that only grow,
            // and each index below carries that alone to reach a row.
            e.HasKey(c => c.EntryId);
            // What a set holds, each value once: what a record's page reads and the sweep deletes a removed set by.
            e.HasIndex(c => new { c.SetId, c.ValueId }).IsUnique();
            // The sets holding a value: the second half of the impact query, and how the sweep tells a value no set holds.
            e.HasIndex(c => c.ValueId);
        });

        modelBuilder.Entity<DeliveryPartition>(e =>
        {
            e.ToTable("Partition", SchemaName);
            e.HasKey(p => p.Name);
            e.Property(p => p.Name).HasMaxLength(200).IsRequired();
            e.Property(p => p.Description).HasMaxLength(DeliveryPartition.MaxDescriptionLength);
            e.Property(p => p.CreatedBy).HasMaxLength(200).IsRequired();
            e.Property(p => p.UpdatedBy).HasMaxLength(200).IsRequired();
            e.Property(p => p.CacheVersionsKept).HasDefaultValue(3);
            // At most one partition is the default.
            e.HasIndex(p => p.IsDefault).IsUnique().HasFilter("[IsDefault] = 1");
        });

        modelBuilder.Entity<DeliveryAccessRestriction>(e =>
        {
            e.ToTable("AccessRestriction", SchemaName);
            e.HasKey(a => a.RestrictionId);
            e.Property(a => a.Partition).HasMaxLength(200).IsRequired();
            e.Property(a => a.Name).HasMaxLength(DeliveryAccessRestriction.MaxNameLength).IsRequired();
            e.Property(a => a.Description).HasMaxLength(DeliveryAccessRestriction.MaxDescriptionLength);
            e.Property(a => a.Viewers).HasMaxLength(DeliveryAccessRestriction.MaxGroupsLength).IsRequired();
            e.Property(a => a.Owners).HasMaxLength(DeliveryAccessRestriction.MaxGroupsLength).IsRequired();
            e.Property(a => a.CreatedBy).HasMaxLength(200).IsRequired();
            e.Property(a => a.UpdatedBy).HasMaxLength(200).IsRequired();
            // One name per partition, whatever its case, as operators pick it; a partition's restrictions listed by name.
            e.HasIndex(a => new { a.Partition, a.Name }).IsUnique();
        });

        modelBuilder.Entity<DeliveryConfigProperty>(e =>
        {
            e.ToTable("ConfigProperty", SchemaName);
            e.HasKey(c => c.Id);
            e.Property(c => c.Name).HasMaxLength(DeliveryConfigNames.MaxNameLength).IsRequired();
            e.Property(c => c.Value).HasMaxLength(DeliveryConfigNames.MaxValueLength).IsRequired();
            e.Property(c => c.Description).HasMaxLength(DeliveryConfigNames.MaxDescriptionLength);
            e.Property(c => c.UpdatedBy).HasMaxLength(200).IsRequired();
            e.Property(c => c.Partition).HasMaxLength(200);
            // One value per name per scope: the control plane's own value is the row with no repository, and a value for
            // no particular partition the row with no partition. The filter is cleared because EF excludes nulls from a
            // unique index over nullable columns by default, which would leave those rows unconstrained; SQL Server treats
            // nulls as equal, so one index covers every scope.
            e.HasIndex(c => new { c.RepoId, c.Partition, c.Name }).IsUnique().HasFilter(null);
            e.HasIndex(c => c.Name);
        });

        modelBuilder.Entity<DeliveryComputeUsage>(e =>
        {
            e.ToTable("ComputeUsage", SchemaName);
            // Keyed by when the work ended, so a period's rows are one range of the table: what every summary reads, and
            // what a cleanup folds from the old end.
            e.HasKey(u => new { u.EndedUtc, u.UsageId });
            e.Property(u => u.UsageId).ValueGeneratedOnAdd();
            e.Property(u => u.Node).HasMaxLength(DeliveryComputeUsage.MaxNodeLength).IsRequired();
            e.Property(u => u.Kind).HasMaxLength(DeliveryComputeUsage.MaxKindLength).IsRequired();
            e.Property(u => u.Team).HasMaxLength(DeliveryComputeUsage.MaxTeamLength).IsRequired();
            e.Property(u => u.Repo).HasMaxLength(DeliveryComputeUsage.MaxRepoLength).IsRequired();
            e.Property(u => u.Project).HasMaxLength(DeliveryComputeUsage.MaxProjectLength).IsRequired();
            e.Property(u => u.Flow).HasMaxLength(DeliveryComputeUsage.MaxFlowLength).IsRequired();
            e.Property(u => u.RequestedBy).HasMaxLength(DeliveryComputeUsage.MaxRequestedByLength);
            e.Property(u => u.Outcome).HasMaxLength(DeliveryComputeUsage.MaxOutcomeLength).IsRequired();
            // A measurement named by the id its node gave it: the check that keeps a batch written twice from landing twice.
            e.HasIndex(u => u.MeasurementId).IsUnique();
        });

        modelBuilder.Entity<DeliveryComputeUsageDay>(e =>
        {
            e.ToTable("ComputeUsageDay", SchemaName);
            e.HasKey(d => d.UsageDayId);
            e.Property(d => d.Team).HasMaxLength(DeliveryComputeUsage.MaxTeamLength).IsRequired();
            e.Property(d => d.Repo).HasMaxLength(DeliveryComputeUsage.MaxRepoLength).IsRequired();
            e.Property(d => d.Project).HasMaxLength(DeliveryComputeUsage.MaxProjectLength).IsRequired();
            e.Property(d => d.Flow).HasMaxLength(DeliveryComputeUsage.MaxFlowLength).IsRequired();
            e.Property(d => d.Kind).HasMaxLength(DeliveryComputeUsage.MaxKindLength).IsRequired();
            // One row per day, team and flow, which a fold adds to; and a period's days, which every summary reads.
            e.HasIndex(d => new { d.Day, d.Team, d.Repo, d.Project, d.Flow, d.Kind }).IsUnique();
        });

        modelBuilder.Entity<DeliveryRepositoryTeam>(e =>
        {
            e.ToTable("RepositoryTeam", SchemaName);
            // A repository's folders together: what a sync reconciles, and what a piece of work's team is found in. A
            // folder's name is matched exactly, as a node on Linux tells folders apart.
            e.HasKey(t => new { t.RepoId, t.Folder });
            e.Property(t => t.Folder).HasMaxLength(DeliveryRepositoryTeam.MaxFolderLength).UseCollation(OsduIdCollation).IsRequired();
            e.Property(t => t.Team).HasMaxLength(DeliveryComputeUsage.MaxTeamLength).IsRequired();
        });

        modelBuilder.Entity<DeliveryUpdateTag>(e =>
        {
            e.ToTable("UpdateTag", SchemaName);
            e.HasKey(t => t.TagId);
            e.Property(t => t.Kind).HasMaxLength(16).IsRequired();
            e.Property(t => t.Scope).HasMaxLength(200).IsRequired();
            e.Property(t => t.TypeName).HasMaxLength(200).IsRequired();
            OsduId(e.Property(t => t.ItemId)).HasMaxLength(512).IsRequired();
            e.Property(t => t.Path).HasMaxLength(400).IsRequired();
            e.Property(t => t.Change).HasMaxLength(16).IsRequired();
            e.Property(t => t.OldValue).HasMaxLength(400);
            e.Property(t => t.NewValue).HasMaxLength(400);
            e.Property(t => t.FromVersion).HasMaxLength(64);
            e.Property(t => t.ToVersion).HasMaxLength(64).IsRequired();
            e.Property(t => t.Mode).HasMaxLength(16).IsRequired();
            e.Property(t => t.Status).HasMaxLength(16).IsRequired();
            e.Property(t => t.DecidedBy).HasMaxLength(200);
            e.Property(t => t.SetIds).IsRequired();
            // The rollout queue, across every partition.
            e.HasIndex(t => t.Status);
            e.HasIndex(t => new { t.Scope, t.TypeName, t.ItemId, t.Path, t.Status });
            // A partition's changes of one status, newest first: the tag id the table is keyed by orders them.
            e.HasIndex(t => new { t.Scope, t.Status });
        });

        modelBuilder.Entity<DeliveryCacheDefinition>(e =>
        {
            e.ToTable("CacheDefinition", SchemaName);
            e.HasKey(c => c.Id);
            e.Property(c => c.FlowName).HasMaxLength(200).IsRequired();
            e.Property(c => c.Scope).HasMaxLength(200).IsRequired();
            e.Property(c => c.Origin).HasMaxLength(16).IsRequired().HasDefaultValue("osdu");
            e.Property(c => c.Endpoint).HasMaxLength(1000);
            e.Property(c => c.Connection).HasMaxLength(1000);
            e.Property(c => c.SourceObject).HasMaxLength(400);
            e.Property(c => c.KeyField).HasMaxLength(128);
            e.Property(c => c.DictionaryPath).HasMaxLength(1000);
            e.Property(c => c.RelativePath).HasMaxLength(1000).IsRequired();
            e.Property(c => c.Name).HasMaxLength(200).IsRequired();
            e.Property(c => c.EntityType).HasMaxLength(200).IsRequired();
            e.Property(c => c.Kind).HasMaxLength(400);
            e.Property(c => c.Query).HasMaxLength(4000);
            e.Property(c => c.FieldsJson).IsRequired();
            e.Property(c => c.OnChange).HasMaxLength(16).IsRequired();
            e.Property(c => c.RetentionDays).HasDefaultValue(7);
            // A cache flow that names its partitions declares each of its types once per partition it builds a cache for.
            e.HasIndex(c => new { c.RepoId, c.FlowName, c.Name, c.Scope }).IsUnique();
            // A refresh reads every declaration of its partition; the GUI lists a partition's types and the flows filling them.
            e.HasIndex(c => new { c.Scope, c.Name });
            e.HasIndex(c => c.FlowName);
        });

        modelBuilder.Entity<OsduSchemaVersion>(e =>
        {
            e.ToTable("SchemaVersion", SchemaName, t => t.HasCheckConstraint("CK_SchemaVersion_SingleRow", "[Id] = 1"));
            e.HasKey(v => v.Id);
            e.Property(v => v.Id).ValueGeneratedNever();
            e.Property(v => v.ModuleVersion).HasMaxLength(32).IsRequired();
            e.Property(v => v.LastMigration).HasMaxLength(150).IsRequired();
            e.Property(v => v.AppliedBy).HasMaxLength(200).IsRequired();
            e.Property(v => v.MinimumCatalogMigration).HasMaxLength(150).IsRequired();
        });
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
