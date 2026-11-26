using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Delivery.Data;

public static partial class DeliveryModel
{
    /// <summary>Configures the removal runs with the keys they found, the keys a ledger removed, and the reversals with their items.</summary>
    private static void ConfigureRemovals(ModelBuilder modelBuilder)
    {
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
    }
}
