using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Delivery.Data;

public static partial class DeliveryModel
{
    /// <summary>Configures the compute usage, as measured and by day, and the team each repository folder charges it to.</summary>
    private static void ConfigureComputeUsage(ModelBuilder modelBuilder)
    {
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
    }
}
