namespace SqlFlow.Delivery.Data;

// The downloads of download flows (osdu/docs/reference/flow/download.md, What a run records): each download with its
// runs, the ids it lists and the scans that read them.

/// <summary>
/// One download of a download flow in a partition (osdu/docs/reference/flow/download.md, What a run records): what it reads,
/// the folder its last completed run wrote its files to, and that run. Its counts are read from its records.
/// </summary>
public sealed class DeliveryDownload
{
    public short PartitionId { get; set; }

    public int DownloadId { get; set; }

    /// <summary>The ledger identity of the download flow in the partition.</summary>
    public Guid FlowId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    /// <summary>The download's name in its flow: the folder its files are written to.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The kind it reads, as the flow declares it (wildcards allowed).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The query that narrows a search read; null for every record of the kind.</summary>
    public string? Query { get; set; }

    /// <summary>search or storage.</summary>
    public string ReadMode { get; set; } = string.Empty;

    /// <summary>
    /// The folder the last completed run wrote the files to; null until a run completed. A run writing to another folder writes
    /// every record again, since that folder holds none of them.
    /// </summary>
    public string? Location { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    /// <summary>The last run that completed, and when.</summary>
    public long? LastRunId { get; set; }

    public DateTime? LastCompletedUtc { get; set; }
}

/// <summary>
/// One run of a download: the platform run and who asked, how it read OSDU, where it wrote, whether it wrote every record again
/// and why, and what it listed, wrote and could not write.
/// </summary>
public sealed class DeliveryDownloadRun
{
    public short PartitionId { get; set; }

    public long DownloadRunId { get; set; }

    public int DownloadId { get; set; }

    public Guid? RunId { get; set; }

    public string Actor { get; set; } = string.Empty;

    /// <summary>running, completed or failed.</summary>
    public string Status { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    /// <summary>search or storage.</summary>
    public string ReadMode { get; set; } = string.Empty;

    /// <summary>The folder the run wrote the files to.</summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>
    /// Why the run wrote every record OSDU serves again rather than what is new or changed: forced (the run asked), moved (the
    /// folder is not the one the last completed run wrote), or null when it wrote only what was due.
    /// </summary>
    public string? Rewrite { get; set; }

    /// <summary>The ids the read listed, repeats left out.</summary>
    public long Listed { get; set; }

    /// <summary>The pages and requests the run took: the listing's and the storage reads'.</summary>
    public int Pages { get; set; }

    public long Requests { get; set; }

    /// <summary>What the listing changed: ids new to the download, ids whose version or kind moved, ids no longer served, and ids served again.</summary>
    public long Added { get; set; }

    public long Changed { get; set; }

    public long Gone { get; set; }

    public long Returned { get; set; }

    /// <summary>The files the run wrote, and their bytes.</summary>
    public long Written { get; set; }

    public long Bytes { get; set; }

    /// <summary>The ids due that storage did not return, so no file was written for them.</summary>
    public long Missing { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// One id a download lists: its kind and version as the listing last gave them, the file it is written to, the version that
/// file holds with its size and SHA-256, when it was first listed, last changed and found gone, and why its last write failed.
/// </summary>
public sealed class DeliveryDownloadRecord
{
    public short PartitionId { get; set; }

    public long DownloadRecordId { get; set; }

    public int DownloadId { get; set; }

    /// <summary>The OSDU id, compared exactly.</summary>
    public string TargetId { get; set; } = string.Empty;

    public string? Kind { get; set; }

    /// <summary>The version the listing last gave.</summary>
    public long? Version { get; set; }

    /// <summary>The file's name in the download's folder, kept for the id's life: no other id of the download holds it, whatever its case.</summary>
    public string FileName { get; set; } = string.Empty;

    public DateTime FirstSeenUtc { get; set; }

    /// <summary>When the listing last gave another version or kind.</summary>
    public DateTime ChangedUtc { get; set; }

    /// <summary>When a complete listing first did not list it; null while OSDU serves it. Its file stays where it is.</summary>
    public DateTime? GoneUtc { get; set; }

    /// <summary>The version of the record the file holds, as storage returned it; null until a file was written.</summary>
    public long? WrittenVersion { get; set; }

    public DateTime? WrittenUtc { get; set; }

    /// <summary>The download run that wrote the file.</summary>
    public long? WrittenRunId { get; set; }

    /// <summary>The file's size in bytes, and the SHA-256 of its bytes.</summary>
    public long? Bytes { get; set; }

    public byte[]? Sha256 { get; set; }

    /// <summary>Why the last write of the record did not happen (storage did not return it); null once a file holds it.</summary>
    public string? Problem { get; set; }

    public DateTime? ProblemUtc { get; set; }
}

/// <summary>One id a download run listed, with the names its file may take, staged until the listing is whole and merged.</summary>
public sealed class DeliveryDownloadScan
{
    public short PartitionId { get; set; }

    public long ScanId { get; set; }

    public long DownloadRunId { get; set; }

    public string TargetId { get; set; } = string.Empty;

    public string? Kind { get; set; }

    public long? Version { get; set; }

    /// <summary>The id's own name, or null when it cannot be one.</summary>
    public string? PlainName { get; set; }

    /// <summary>The name the file takes when the id's own is none, or another id of the download holds it but for its case.</summary>
    public string DistinctName { get; set; } = string.Empty;
}
