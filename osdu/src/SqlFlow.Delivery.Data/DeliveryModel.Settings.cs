using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Delivery.Data;

public static partial class DeliveryModel
{
    /// <summary>Configures the partitions, the access restrictions they offer, and the configuration properties.</summary>
    private static void ConfigureSettings(ModelBuilder modelBuilder)
    {
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
    }
}
