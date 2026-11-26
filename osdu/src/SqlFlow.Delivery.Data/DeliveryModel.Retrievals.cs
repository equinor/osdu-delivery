using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Delivery.Data;

public static partial class DeliveryModel
{
    /// <summary>Configures the retrievals: one row per run of a retrieval flow.</summary>
    private static void ConfigureRetrievals(ModelBuilder modelBuilder)
    {
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
    }
}
