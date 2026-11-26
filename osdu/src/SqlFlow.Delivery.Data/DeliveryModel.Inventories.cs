using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Delivery.Data;

public static partial class DeliveryModel
{
    /// <summary>Configures the inventories with their runs, records, versions, removals, selections, duplicate checks and scans.</summary>
    private static void ConfigureInventories(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeliveryInventory>(e =>
        {
            e.ToTable("Inventory", SchemaName);
            e.HasKey(i => new { i.PartitionId, i.InventoryId });
            e.Property(i => i.InventoryId).ValueGeneratedOnAdd();
            e.Property(i => i.FlowName).HasMaxLength(DeliveryLedger.MaxFlowNameLength).IsRequired();
            e.Property(i => i.Name).HasMaxLength(200).IsRequired();
            e.Property(i => i.Kind).HasMaxLength(MaxInventoryKindLength).IsRequired();
            e.Property(i => i.Query).HasMaxLength(MaxInventoryQueryLength);
            e.Property(i => i.ReadMode).HasMaxLength(16).IsRequired();
            e.Property(i => i.Versions).HasMaxLength(16).IsRequired();
            e.Property(i => i.OwnersSource).HasMaxLength(16);
            // One row per inventory of a flow's ledger in the partition: a build finds its own by flow and name.
            e.HasIndex(i => new { i.PartitionId, i.FlowId, i.Name }).IsUnique();
        });

        modelBuilder.Entity<DeliveryInventoryRun>(e =>
        {
            e.ToTable("InventoryRun", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.InventoryRunId });
            e.Property(r => r.InventoryRunId).ValueGeneratedOnAdd();
            e.Property(r => r.Operation).HasMaxLength(16).IsRequired();
            e.Property(r => r.Actor).HasMaxLength(200).IsRequired();
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.ReadMode).HasMaxLength(16).IsRequired();
            e.Property(r => r.Error).HasMaxLength(4000);
            // An inventory's runs, newest first: its page and its next build's look at the last ones.
            e.HasIndex(r => new { r.PartitionId, r.InventoryId, r.InventoryRunId });
        });

        modelBuilder.Entity<DeliveryInventoryRecord>(e =>
        {
            e.ToTable("InventoryRecord", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.InventoryRecordId });
            e.Property(r => r.InventoryRecordId).ValueGeneratedOnAdd();
            OsduId(e.Property(r => r.TargetId)).HasMaxLength(MaxTargetIdLength);
            e.Property(r => r.Name).HasMaxLength(MaxInventoryNameLength);
            e.Property(r => r.Kind).HasMaxLength(MaxInventoryKindLength);
            e.Property(r => r.CreateUser).HasMaxLength(MaxInventoryUserLength);
            e.Property(r => r.ModifyUser).HasMaxLength(MaxInventoryUserLength);
            e.Property(r => r.Finding).HasMaxLength(16).IsRequired();
            e.Property(r => r.LedgerStatus).HasMaxLength(16);
            e.Property(r => r.ArtifactState).HasMaxLength(16);
            e.Property(r => r.Detail).HasMaxLength(MaxInventoryDetailLength);
            // One row per id of an inventory: the merge seeks it by id.
            e.HasIndex(r => new { r.PartitionId, r.InventoryId, r.TargetId }).IsUnique();
            // An inventory's records of one finding, in order: the report's grid pages through them, and its counts are read here.
            e.HasIndex(r => new { r.PartitionId, r.InventoryId, r.Finding, r.InventoryRecordId });
            // An inventory's records in order, whatever their finding: the report's grid of every id and the export page through
            // them, seeking the inventory rather than walking every inventory of the partition.
            e.HasIndex(r => new { r.PartitionId, r.InventoryId, r.InventoryRecordId });
            // An OSDU id across every inventory of the partition: a lookup by id answers what each inventory found of it.
            e.HasIndex(r => new { r.PartitionId, r.TargetId }).IncludeProperties(r => new { r.InventoryId, r.Finding, r.GoneUtc });
        });

        modelBuilder.Entity<DeliveryInventoryVersion>(e =>
        {
            e.ToTable("InventoryVersion", SchemaName);
            e.HasKey(v => new { v.PartitionId, v.InventoryRecordId, v.Version });
        });

        modelBuilder.Entity<DeliveryInventoryRemoval>(e =>
        {
            e.ToTable("InventoryRemoval", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.InventoryRemovalId });
            e.Property(r => r.InventoryRemovalId).ValueGeneratedOnAdd();
            e.Property(r => r.Actor).HasMaxLength(200).IsRequired();
            e.Property(r => r.Finding).HasMaxLength(16).IsRequired();
            e.Property(r => r.Scope).HasMaxLength(16).IsRequired();
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.Error).HasMaxLength(4000);
            // An inventory's removals, newest first: its page lists them, and a removal closes the earlier ones its process left open.
            e.HasIndex(r => new { r.PartitionId, r.InventoryId, r.InventoryRemovalId });
        });

        modelBuilder.Entity<DeliveryInventoryRemovalItem>(e =>
        {
            e.ToTable("InventoryRemovalItem", SchemaName);
            e.HasKey(i => new { i.PartitionId, i.InventoryRemovalItemId });
            e.Property(i => i.InventoryRemovalItemId).ValueGeneratedOnAdd();
            OsduId(e.Property(i => i.TargetId)).HasMaxLength(MaxTargetIdLength);
            e.Property(i => i.Finding).HasMaxLength(16).IsRequired();
            e.Property(i => i.Outcome).HasMaxLength(16).IsRequired();
            e.Property(i => i.Reason).HasMaxLength(1000);
            // A removal's ids in order, and those of one outcome in order: its page pages through either.
            e.HasIndex(i => new { i.PartitionId, i.InventoryRemovalId, i.InventoryRemovalItemId });
            e.HasIndex(i => new { i.PartitionId, i.InventoryRemovalId, i.Outcome, i.InventoryRemovalItemId });
            // An OSDU id across every removal of the partition: a lookup by id answers what was removed of it, by whom and when.
            e.HasIndex(i => new { i.PartitionId, i.TargetId }).IncludeProperties(i => new { i.InventoryRemovalId, i.Outcome });
        });

        modelBuilder.Entity<DeliveryInventorySelection>(e =>
        {
            e.ToTable("InventorySelection", SchemaName);
            e.HasKey(s => new { s.PartitionId, s.InventorySelectionId });
            e.Property(s => s.InventorySelectionId).ValueGeneratedOnAdd();
            e.Property(s => s.Actor).HasMaxLength(200).IsRequired();
            e.Property(s => s.ConditionsJson).IsRequired();
            e.Property(s => s.ConditionsKey).HasMaxLength(SelectionKeyLength).IsUnicode(false).IsRequired();
            e.Property(s => s.FiltersJson).IsRequired();
            e.Property(s => s.Status).HasMaxLength(16).IsRequired();
            e.Property(s => s.Direction).HasMaxLength(16);
            e.Property(s => s.ColumnsJson).HasMaxLength(4000);
            e.Property(s => s.Error).HasMaxLength(4000);
            // The same conditions asked again of an inventory find the newest match made of them.
            e.HasIndex(s => new { s.PartitionId, s.InventoryId, s.ConditionsKey, s.InventorySelectionId });
            // The matches a sweep retires, oldest first.
            e.HasIndex(s => new { s.PartitionId, s.CreatedUtc });
        });

        modelBuilder.Entity<DeliveryInventorySelectionItem>(e =>
        {
            e.ToTable("InventorySelectionItem", SchemaName);
            // A selection's ids in the inventory's own order: the grid pages through them, and a removal joins them by row.
            e.HasKey(i => new { i.PartitionId, i.InventorySelectionId, i.InventoryRecordId });
            e.Property(i => i.ValuesJson).HasMaxLength(MaxSelectionValuesLength);
        });

        modelBuilder.Entity<DeliveryInventoryDuplicateCheck>(e =>
        {
            e.ToTable("InventoryDuplicateCheck", SchemaName);
            e.HasKey(c => new { c.PartitionId, c.InventoryDuplicateCheckId });
            e.Property(c => c.InventoryDuplicateCheckId).ValueGeneratedOnAdd();
            e.Property(c => c.Actor).HasMaxLength(200).IsRequired();
            e.Property(c => c.KeyJson).IsRequired();
            e.Property(c => c.KeyHash).HasMaxLength(SelectionKeyLength).IsUnicode(false).IsRequired();
            e.Property(c => c.TableName).HasMaxLength(MaxRunTimeTableNameLength);
            e.Property(c => c.KeyedBy).HasMaxLength(200);
            e.Property(c => c.Status).HasMaxLength(16).IsRequired();
            e.Property(c => c.Phase).HasMaxLength(16);
            e.Property(c => c.Error).HasMaxLength(4000);
            // An inventory's checks, newest first: the tab lists them, and a key asked again groups the newest.
            e.HasIndex(c => new { c.PartitionId, c.InventoryId, c.InventoryDuplicateCheckId });
            // The checks a sweep retires, oldest first.
            e.HasIndex(c => new { c.PartitionId, c.CreatedUtc });
        });

        modelBuilder.Entity<DeliveryInventoryDuplicateGroup>(e =>
        {
            e.ToTable("InventoryDuplicateGroup", SchemaName);
            // A check's groups in order, largest first: the list pages through them, and the copies pass reads them so.
            e.HasKey(g => new { g.PartitionId, g.InventoryDuplicateCheckId, g.GroupNumber });
            e.Property(g => g.KeyHash).HasMaxLength(DuplicateKeyHashLength).IsFixedLength().IsRequired();
            e.Property(g => g.ValuesJson).HasMaxLength(MaxSelectionValuesLength);
            e.Property(g => g.Outcome).HasMaxLength(16).IsRequired();
            e.Property(g => g.Reason).HasMaxLength(MaxDuplicateReasonLength);
            e.Property(g => g.ChosenBy).HasMaxLength(200);
            // The groups of one outcome in order: the list's tabs page through them.
            e.HasIndex(g => new { g.PartitionId, g.InventoryDuplicateCheckId, g.Outcome, g.GroupNumber });
        });

        modelBuilder.Entity<DeliveryInventoryDuplicateMember>(e =>
        {
            e.ToTable("InventoryDuplicateMember", SchemaName);
            // A check's copies in the inventory's own order: the grouping gives them their values a chunk at a time, and a
            // grouping again, a failed check and a sweep delete them a batch at a time.
            e.HasKey(m => new { m.PartitionId, m.InventoryDuplicateCheckId, m.InventoryRecordId });
            e.Property(m => m.KeyHash).HasMaxLength(DuplicateKeyHashLength).IsFixedLength().IsRequired();
            e.Property(m => m.ValuesJson).HasMaxLength(MaxSelectionValuesLength);
            e.Property(m => m.StorageState).HasMaxLength(16);
            e.Property(m => m.Role).HasMaxLength(16);
            e.Property(m => m.Reason).HasMaxLength(MaxDuplicateReasonLength);
            // A group's copies in order: the list shows them, a pick reads them so, and a selection is made of them.
            e.HasIndex(m => new { m.PartitionId, m.InventoryDuplicateCheckId, m.GroupNumber, m.InventoryRecordId });
        });

        modelBuilder.Entity<DeliveryInventoryScan>(e =>
        {
            e.ToTable("InventoryScan", SchemaName);
            e.HasKey(s => new { s.PartitionId, s.ScanId });
            e.Property(s => s.ScanId).ValueGeneratedOnAdd();
            OsduId(e.Property(s => s.TargetId)).HasMaxLength(MaxTargetIdLength);
            e.Property(s => s.Name).HasMaxLength(MaxInventoryNameLength);
            e.Property(s => s.Kind).HasMaxLength(MaxInventoryKindLength);
            e.Property(s => s.CreateUser).HasMaxLength(MaxInventoryUserLength);
            e.Property(s => s.ModifyUser).HasMaxLength(MaxInventoryUserLength);
            // A build's ids by id: its merge reads them in this order, and a build's leftovers are deleted by run.
            e.HasIndex(s => new { s.PartitionId, s.InventoryRunId, s.TargetId });
        });
    }
}
