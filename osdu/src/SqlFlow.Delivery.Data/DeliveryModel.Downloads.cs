using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Delivery.Data;

public static partial class DeliveryModel
{
    /// <summary>Configures the downloads with their runs, the records each wrote as a file, and the scans a run merges.</summary>
    private static void ConfigureDownloads(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeliveryDownload>(e =>
        {
            e.ToTable("Download", SchemaName);
            e.HasKey(d => new { d.PartitionId, d.DownloadId });
            e.Property(d => d.DownloadId).ValueGeneratedOnAdd();
            e.Property(d => d.FlowName).HasMaxLength(DeliveryLedger.MaxFlowNameLength).IsRequired();
            e.Property(d => d.Name).HasMaxLength(200).IsRequired();
            e.Property(d => d.Kind).HasMaxLength(MaxInventoryKindLength).IsRequired();
            e.Property(d => d.Query).HasMaxLength(MaxInventoryQueryLength);
            e.Property(d => d.ReadMode).HasMaxLength(16).IsRequired();
            e.Property(d => d.Location).HasMaxLength(MaxDownloadLocationLength);
            // One row per download of a flow's ledger in the partition: a run finds its own by flow and name.
            e.HasIndex(d => new { d.PartitionId, d.FlowId, d.Name }).IsUnique();
        });

        modelBuilder.Entity<DeliveryDownloadRun>(e =>
        {
            e.ToTable("DownloadRun", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.DownloadRunId });
            e.Property(r => r.DownloadRunId).ValueGeneratedOnAdd();
            e.Property(r => r.Actor).HasMaxLength(200).IsRequired();
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.ReadMode).HasMaxLength(16).IsRequired();
            e.Property(r => r.Location).HasMaxLength(MaxDownloadLocationLength).IsRequired();
            e.Property(r => r.Rewrite).HasMaxLength(16);
            e.Property(r => r.Error).HasMaxLength(4000);
            // A download's runs, newest first: its page lists them, and a run closes the earlier ones its process left open.
            e.HasIndex(r => new { r.PartitionId, r.DownloadId, r.DownloadRunId });
        });

        modelBuilder.Entity<DeliveryDownloadRecord>(e =>
        {
            e.ToTable("DownloadRecord", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.DownloadRecordId });
            e.Property(r => r.DownloadRecordId).ValueGeneratedOnAdd();
            OsduId(e.Property(r => r.TargetId)).HasMaxLength(MaxTargetIdLength);
            e.Property(r => r.Kind).HasMaxLength(MaxInventoryKindLength);
            // A file name is compared ignoring case, as the folders of Windows and macOS compare it, so the unique index below
            // never lets two ids of a download share a file there.
            e.Property(r => r.FileName).HasMaxLength(MaxDownloadFileNameLength).UseCollation(SearchCollation).IsRequired();
            e.Property(r => r.Sha256).HasMaxLength(DownloadHashLength).IsFixedLength();
            e.Property(r => r.Problem).HasMaxLength(MaxDownloadProblemLength);
            // One row per id of a download: the merge seeks it by id.
            e.HasIndex(r => new { r.PartitionId, r.DownloadId, r.TargetId }).IsUnique();
            // One id per file of a download, whatever the case of its name: the merge gives a new id the name no other holds.
            e.HasIndex(r => new { r.PartitionId, r.DownloadId, r.FileName }).IsUnique();
            // A download's records in order, with what says whether each is due: a run walks them for those it writes, and the
            // page's grid pages through them.
            e.HasIndex(r => new { r.PartitionId, r.DownloadId, r.DownloadRecordId })
                .IncludeProperties(r => new { r.TargetId, r.Version, r.WrittenVersion, r.GoneUtc, r.FileName });
            // An OSDU id across every download of the partition: a lookup by id answers which downloads wrote it, and where.
            e.HasIndex(r => new { r.PartitionId, r.TargetId }).IncludeProperties(r => new { r.DownloadId, r.FileName, r.WrittenVersion, r.GoneUtc });
        });

        modelBuilder.Entity<DeliveryDownloadScan>(e =>
        {
            e.ToTable("DownloadScan", SchemaName);
            e.HasKey(s => new { s.PartitionId, s.ScanId });
            e.Property(s => s.ScanId).ValueGeneratedOnAdd();
            OsduId(e.Property(s => s.TargetId)).HasMaxLength(MaxTargetIdLength);
            e.Property(s => s.Kind).HasMaxLength(MaxInventoryKindLength);
            e.Property(s => s.PlainName).HasMaxLength(MaxDownloadFileNameLength).UseCollation(SearchCollation);
            e.Property(s => s.DistinctName).HasMaxLength(MaxDownloadFileNameLength).UseCollation(SearchCollation).IsRequired();
            // A run's ids by id: its merge reads them in this order, and a run's leftovers are deleted by run.
            e.HasIndex(s => new { s.PartitionId, s.DownloadRunId, s.TargetId });
        });
    }
}
