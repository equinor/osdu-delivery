using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Delivery.Data;

public static partial class DeliveryModel
{
    /// <summary>Configures the runs of assertion flows and the result of each test they ran.</summary>
    private static void ConfigureAssertions(ModelBuilder modelBuilder)
    {
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
    }
}
