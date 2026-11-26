using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Delivery.Data;

public static partial class DeliveryModel
{
    /// <summary>Configures the audit trail (activities and the records each changed) and the records a ledger purged.</summary>
    private static void ConfigureActivities(ModelBuilder modelBuilder)
    {
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
    }
}
