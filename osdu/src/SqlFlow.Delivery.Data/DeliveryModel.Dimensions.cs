using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Delivery.Data;

public static partial class DeliveryModel
{
    /// <summary>Configures the dimensions with their runs, members, values, attributes, elements, views, records and changes.</summary>
    private static void ConfigureDimensions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeliveryDimension>(e =>
        {
            e.ToTable("Dimension", SchemaName);
            e.HasKey(d => new { d.PartitionId, d.DimensionId });
            e.Property(d => d.DimensionId).ValueGeneratedOnAdd();
            e.Property(d => d.FlowName).HasMaxLength(DeliveryLedger.MaxFlowNameLength).IsRequired();
            e.Property(d => d.Name).HasMaxLength(DeliveryDimension.MaxNameLength).IsRequired();
            e.Property(d => d.Description).HasMaxLength(4000);
            e.Property(d => d.Kind).HasMaxLength(400).IsRequired();
            e.Property(d => d.Query).HasMaxLength(4000);
            e.Property(d => d.Path).HasMaxLength(DeliveryDimension.MaxPathLength).IsRequired();
            e.Property(d => d.FieldIndex).HasMaxLength(16);
            e.Property(d => d.NestedPath).HasMaxLength(DeliveryDimension.MaxPathLength);
            e.Property(d => d.AggregateBy).HasMaxLength(DeliveryDimension.MaxAggregateByLength);
            e.Property(d => d.CleanJson).IsRequired();
            e.Property(d => d.DefinitionHash).HasMaxLength(16).IsRequired();
            e.Property(d => d.TableName).HasMaxLength(128);
            e.Property(d => d.TableConnection).HasMaxLength(2000);
            e.Property(d => d.TableDatabase).HasMaxLength(128);
            e.Property(d => d.TableSchema).HasMaxLength(128);
            e.Property(d => d.KeyColumn).HasMaxLength(128);
            e.Property(d => d.ValueColumn).HasMaxLength(128);
            // A dimension named by its id: every member, original, build and change of it names it so.
            e.HasIndex(d => d.DimensionId).IsUnique();
            // A flow's dimension in a partition, by its name: what a build registers and a page finds.
            e.HasIndex(d => new { d.PartitionId, d.FlowId, d.Name }).IsUnique();
            // The dimensions writing one table, one a partition: what a build asks before it writes and a removal before it drops.
            // It holds what the build asks, so the build reads no row of another dimension, which a build of that one may be
            // writing.
            e.HasIndex(d => d.TableName).IncludeProperties(d => new { d.FlowName, d.Name });
        });

        modelBuilder.Entity<DeliveryDimensionRun>(e =>
        {
            e.ToTable("DimensionRun", SchemaName);
            // A dimension's builds together, in the order they were written: every statement over one dimension seeks
            // its own rows and locks no other's, so a build of another dimension writes beside it rather than waiting
            // on, or deadlocking with, them.
            e.HasKey(r => new { r.PartitionId, r.DimensionId, r.DimensionRunId });
            e.Property(r => r.DimensionRunId).ValueGeneratedOnAdd();
            e.Property(r => r.Actor).HasMaxLength(200).IsRequired();
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.Mode).HasMaxLength(16).IsRequired();
            e.Property(r => r.RecordKey).HasMaxLength(DeliveryDimensionRun.MaxRecordKeyLength);
            e.Property(r => r.DefinitionHash).HasMaxLength(16).IsRequired();
            e.Property(r => r.Query).HasMaxLength(4000);
            e.Property(r => r.AggregateBy).HasMaxLength(DeliveryDimension.MaxAggregateByLength);
            e.Property(r => r.Error).HasMaxLength(4000);
            e.HasIndex(r => r.DimensionRunId).IsUnique();
            // A dimension's builds, newest first; and the platform run's builds.
            e.HasIndex(r => new { r.PartitionId, r.DimensionId, r.StartedUtc });
            e.HasIndex(r => r.RunId);
        });

        modelBuilder.Entity<DeliveryDimensionMember>(e =>
        {
            e.ToTable("DimensionMember", SchemaName);
            // A dimension's members together, in the order they were written: every statement over one dimension seeks
            // its own rows and locks no other's, so a build of another dimension writes beside it rather than waiting
            // on, or deadlocking with, them.
            e.HasKey(m => new { m.PartitionId, m.DimensionId, m.MemberId });
            e.Property(m => m.MemberId).ValueGeneratedOnAdd();
            // A clean value is compared exactly, as a filter an application stored compares it.
            ExactText(e.Property(m => m.Value)).HasMaxLength(256).IsRequired();
            e.HasIndex(m => m.MemberId).IsUnique();
            // A member is a clean value of one dimension, once: what a build matches its members by.
            e.HasIndex(m => new { m.PartitionId, m.DimensionId, m.Value }).IsUnique();
            // The members a dimension holds now, and those it held, each read as one range.
            e.HasIndex(m => new { m.PartitionId, m.DimensionId, m.RemovedRunId });
            // The members most records hold first, a page at a time.
            e.HasIndex(m => new { m.PartitionId, m.DimensionId, m.Records, m.Value }).IsDescending(false, false, true, false);
        });

        modelBuilder.Entity<DeliveryDimensionValue>(e =>
        {
            e.ToTable("DimensionValue", SchemaName);
            // A dimension's keys together, in the order they were written: every statement over one dimension seeks its
            // own rows and locks no other's, so a build of another dimension writes beside it rather than waiting on,
            // or deadlocking with, them.
            e.HasKey(v => new { v.PartitionId, v.DimensionId, v.ValueId });
            e.Property(v => v.ValueId).ValueGeneratedOnAdd();
            ExactText(e.Property(v => v.Original)).HasMaxLength(DeliveryDimensionValue.MaxKeyLength).IsRequired();
            e.Property(v => v.OriginalBytes).HasComputedColumnSql("CAST(DATALENGTH([Original]) AS smallint)", stored: true);
            e.Property(v => v.ElementsHash).HasMaxLength(32).IsFixedLength();
            e.Property(v => v.AttributesHash).HasMaxLength(32).IsFixedLength();
            e.Property(v => v.LeftOut).HasMaxLength(16);
            e.Property(v => v.Note).HasMaxLength(400);
            e.Property(v => v.Label).HasMaxLength(DeliveryDimensionValue.MaxLabelLength);
            e.Property(v => v.LabelFrom).HasMaxLength(DeliveryDimensionValue.MaxOriginalLength);
            e.Property(v => v.Filter).HasMaxLength(DeliveryDimensionValue.MaxFilterLength);
            e.HasIndex(v => v.ValueId).IsUnique();
            // An original is one value of one dimension, once, compared exactly, its length beside it: what a build matches the keys it read by.
            e.HasIndex(v => new { v.PartitionId, v.DimensionId, v.Original, v.OriginalBytes }).IsUnique();
            // A member's originals.
            e.HasIndex(v => new { v.PartitionId, v.DimensionId, v.MemberId });
            // The originals most records hold first, a page at a time.
            e.HasIndex(v => new { v.PartitionId, v.DimensionId, v.Count, v.ValueId }).IsDescending(false, false, true, false);
        });

        modelBuilder.Entity<DeliveryDimensionAttributeValue>(e =>
        {
            e.ToTable("DimensionAttribute", SchemaName);
            // A dimension's attribute values together, in the order they were written: every statement over one
            // dimension seeks its own rows and locks no other's, so a build of another dimension writes beside it
            // rather than waiting on, or deadlocking with, them.
            e.HasKey(a => new { a.PartitionId, a.DimensionId, a.AttributeValueId });
            e.Property(a => a.AttributeValueId).ValueGeneratedOnAdd();
            ExactText(e.Property(a => a.Value)).HasMaxLength(DeliveryDimensionAttributeValue.MaxValueLength).IsRequired();
            e.Property(a => a.ValueFrom).HasMaxLength(DeliveryDimensionValue.MaxOriginalLength);
            e.HasIndex(a => a.AttributeValueId).IsUnique();
            // A key's attributes, read with it a page at a time: one row per value, so a collected attribute holds several,
            // and a key holds a value of an attribute once.
            e.HasIndex(a => new { a.PartitionId, a.DimensionId, a.ValueId, a.AttributeId, a.Value }).IsUnique();
            // The keys an attribute value holds (Country is US), and an attribute's values: one seek either way.
            e.HasIndex(a => new { a.PartitionId, a.DimensionId, a.AttributeId, a.Value });
        });

        modelBuilder.Entity<DeliveryDimensionElement>(e =>
        {
            e.ToTable("DimensionElement", SchemaName);
            // A dimension's element fields together, in the order they were written: every statement over one dimension
            // seeks its own rows and locks no other's, so a build of another dimension writes beside it rather than
            // waiting on, or deadlocking with, them.
            e.HasKey(x => new { x.PartitionId, x.DimensionId, x.ElementValueId });
            e.Property(x => x.ElementValueId).ValueGeneratedOnAdd();
            OptionalOsduId(e.Property(x => x.Value)).HasMaxLength(DeliveryDimensionElement.MaxValueLength);
            e.HasIndex(x => x.ElementValueId).IsUnique();
            // A key's objects in their order, each field once: what a build matches its rows by and its table is laid out from.
            e.HasIndex(x => new { x.PartitionId, x.DimensionId, x.ValueId, x.Seq, x.AttributeId }).IsUnique();
        });

        modelBuilder.Entity<DeliveryDimensionView>(e =>
        {
            e.ToTable("DimensionView", SchemaName);
            e.HasKey(v => v.ViewId);
            e.Property(v => v.Name).HasMaxLength(DeliveryDimensionView.MaxNameLength).IsRequired();
            e.Property(v => v.ViewName).HasMaxLength(128).IsRequired();
            e.Property(v => v.FlowName).HasMaxLength(DeliveryLedger.MaxFlowNameLength).IsRequired();
            e.Property(v => v.TableConnection).HasMaxLength(2000);
            e.Property(v => v.TableDatabase).HasMaxLength(128);
            e.Property(v => v.TableSchema).HasMaxLength(128);
            e.Property(v => v.Description).HasMaxLength(DeliveryDimensionView.MaxDescriptionLength);
            e.Property(v => v.DeclarationJson).IsRequired();
            e.Property(v => v.Sql).IsRequired();
            e.Property(v => v.SqlHash).HasMaxLength(64);
            e.Property(v => v.TablesJson).IsRequired();
            e.Property(v => v.ColumnsJson).IsRequired();
            e.Property(v => v.Note).HasMaxLength(1000);
            e.Property(v => v.WrittenBy).HasMaxLength(200).IsRequired();
            // A view by its name, once in the database: what a build finds its record by, and what refuses a second flow's.
            e.HasIndex(v => v.Name).IsUnique();
            e.HasIndex(v => v.ViewName).IsUnique();
            // A flow's views: what a build drops the undeclared ones of.
            e.HasIndex(v => v.FlowName);
        });

        modelBuilder.Entity<DeliveryDimensionViewCheck>(e =>
        {
            e.ToTable("DimensionViewCheck", SchemaName);
            e.HasKey(c => new { c.PartitionId, c.CheckId });
            e.Property(c => c.CheckId).ValueGeneratedOnAdd();
            e.Property(c => c.Status).HasMaxLength(16).IsRequired();
            e.Property(c => c.Error).HasMaxLength(4000);
            e.Property(c => c.ReadVersion).HasMaxLength(64).IsUnicode(false);
            e.HasIndex(c => c.CheckId).IsUnique();
            // A view's checks of a partition, newest first: its page, and what a build prunes past the newest it keeps.
            e.HasIndex(c => new { c.ViewId, c.PartitionId, c.CheckedUtc });
        });

        modelBuilder.Entity<DeliveryDimensionAttributeName>(e =>
        {
            e.ToTable("DimensionAttributeName", SchemaName);
            // A dimension's attributes together, in the order they were written: every statement over one dimension
            // seeks its own rows and locks no other's, so a build of another dimension writes beside it rather than
            // waiting on, or deadlocking with, them.
            e.HasKey(n => new { n.PartitionId, n.DimensionId, n.AttributeId });
            e.Property(n => n.AttributeId).ValueGeneratedOnAdd();
            ExactText(e.Property(n => n.Name)).HasMaxLength(DeliveryDimensionAttributeValue.MaxNameLength).IsRequired();
            e.HasIndex(n => n.AttributeId).IsUnique();
            // A dimension's attribute by its name, once: what a build finds the number by.
            e.HasIndex(n => new { n.PartitionId, n.DimensionId, n.Name }).IsUnique();
        });

        modelBuilder.Entity<DeliveryDimensionCollectedText>(e =>
        {
            e.ToTable("DimensionCollectedText", SchemaName);
            // A dimension's collected texts together, in the order they were written: every statement over one
            // dimension seeks its own rows and locks no other's, so a build of another dimension writes beside it
            // rather than waiting on, or deadlocking with, them.
            e.HasKey(t => new { t.PartitionId, t.DimensionId, t.TextId });
            e.Property(t => t.TextId).ValueGeneratedOnAdd();
            ExactText(e.Property(t => t.Text)).HasMaxLength(DeliveryDimensionValue.MaxKeyLength).IsRequired();
            e.Property(t => t.TextBytes).HasComputedColumnSql("CAST(DATALENGTH([Text]) AS smallint)", stored: true);
            ExactText(e.Property(t => t.Value)).HasMaxLength(DeliveryDimensionAttributeValue.MaxValueLength).IsRequired();
            e.HasIndex(t => t.TextId).IsUnique();
            // A text of a collected attribute, once, compared exactly, its length beside it.
            e.HasIndex(t => new { t.PartitionId, t.DimensionId, t.AttributeId, t.Text, t.TextBytes }).IsUnique();
            // The texts of the values a search picks: one seek.
            e.HasIndex(t => new { t.PartitionId, t.DimensionId, t.AttributeId, t.Value });
        });

        modelBuilder.Entity<DeliveryDimensionKeyRecord>(e =>
        {
            e.ToTable("DimensionKeyRecord", SchemaName);
            // A dimension's keys' records together, in the order they were written: every statement over one dimension
            // seeks its own rows and locks no other's, so a build of another dimension writes beside it rather than
            // waiting on, or deadlocking with, them.
            e.HasKey(x => new { x.PartitionId, x.DimensionId, x.KeyRecordId });
            e.Property(x => x.KeyRecordId).ValueGeneratedOnAdd();
            ExactText(e.Property(x => x.RecordId)).HasMaxLength(DeliveryDimensionValue.MaxKeyLength).IsRequired();
            ExactText(e.Property(x => x.EntityType)).HasMaxLength(DeliveryDimensionKeyRecord.MaxEntityTypeLength).IsRequired();
            e.HasIndex(x => x.KeyRecordId).IsUnique();
            // A key's records, once each: what a write rewrites them by.
            e.HasIndex(x => new { x.PartitionId, x.DimensionId, x.ValueId, x.RecordId }).IsUnique();
            // The keys read through a record that changed: one seek a record.
            e.HasIndex(x => new { x.PartitionId, x.DimensionId, x.RecordId });
            // The entity types a dimension's keys were read through, which an update asks the search what changed in.
            e.HasIndex(x => new { x.PartitionId, x.DimensionId, x.EntityType });
        });

        modelBuilder.Entity<DeliveryDimensionRecord>(e =>
        {
            e.ToTable("DimensionRecord", SchemaName);
            // A dimension's records' keys together, in the order they were written: every statement over one dimension
            // seeks its own rows and locks no other's, so a build of another dimension writes beside it rather than
            // waiting on, or deadlocking with, them.
            e.HasKey(x => new { x.PartitionId, x.DimensionId, x.DimensionRecordId });
            e.Property(x => x.DimensionRecordId).ValueGeneratedOnAdd();
            ExactText(e.Property(x => x.RecordKey)).HasMaxLength(DeliveryDimensionValue.MaxKeyLength).IsRequired();
            e.HasIndex(x => x.DimensionRecordId).IsUnique();
            // A record's keys, once each: what a load rewrites them by and an incremental load reads them by.
            e.HasIndex(x => new { x.PartitionId, x.DimensionId, x.RecordKey, x.ValueId }).IsUnique();
            // The records of a key: what goes with a key a load removes.
            e.HasIndex(x => new { x.PartitionId, x.DimensionId, x.ValueId });
        });

        modelBuilder.Entity<DeliveryDimensionChange>(e =>
        {
            e.ToTable("DimensionChange", SchemaName);
            // A dimension's changes together, in the order they were written: every statement over one dimension seeks
            // its own rows and locks no other's, so a build of another dimension writes beside it rather than waiting
            // on, or deadlocking with, them.
            e.HasKey(c => new { c.PartitionId, c.DimensionId, c.ChangeId });
            e.Property(c => c.ChangeId).ValueGeneratedOnAdd();
            e.Property(c => c.Change).HasMaxLength(16).IsRequired();
            ExactText(e.Property(c => c.Original)).HasMaxLength(DeliveryDimensionValue.MaxOriginalLength).IsRequired();
            OptionalOsduId(e.Property(c => c.FromValue)).HasMaxLength(256);
            OptionalOsduId(e.Property(c => c.ToValue)).HasMaxLength(256);
            e.HasIndex(c => c.ChangeId).IsUnique();
            // An original's history, and a build's changes.
            e.HasIndex(c => new { c.PartitionId, c.DimensionId, c.ValueId, c.ChangeId });
            e.HasIndex(c => new { c.PartitionId, c.DimensionRunId });
        });
    }
}
