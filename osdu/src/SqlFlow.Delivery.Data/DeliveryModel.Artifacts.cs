using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Delivery.Data;

public static partial class DeliveryModel
{
    /// <summary>Configures the artifacts: every object a delivery created in OSDU, or set out to create, and its undo.</summary>
    private static void ConfigureArtifacts(ModelBuilder modelBuilder)
    {
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
    }
}
