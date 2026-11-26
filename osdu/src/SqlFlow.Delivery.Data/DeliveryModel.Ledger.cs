using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Delivery.Data;

public static partial class DeliveryModel
{
    /// <summary>Configures the ledger's directory and its submissions, records, attempts, work batches, leases, events and watermarks.</summary>
    private static void ConfigureLedger(ModelBuilder modelBuilder)
    {
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
    }
}
