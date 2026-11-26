using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Delivery.Data;

public static partial class DeliveryModel
{
    /// <summary>Configures the update tags that roll a cache change out, the cache flows' declared types, and the schema version.</summary>
    private static void ConfigureCacheDefinitions(ModelBuilder modelBuilder)
    {
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
}
