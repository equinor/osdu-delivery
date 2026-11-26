using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Delivery.Data;

public static partial class DeliveryModel
{
    /// <summary>Configures the partition caches (versions, items, members and the sets records were built from) and the schema lineage.</summary>
    private static void ConfigureCaches(ModelBuilder modelBuilder)
    {
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
    }
}
