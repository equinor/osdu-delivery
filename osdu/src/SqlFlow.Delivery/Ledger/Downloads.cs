namespace SqlFlow.Delivery.Ledger;

/// <summary>How download runs are written in the ledger.</summary>
public static class DownloadRunStatus
{
    public const string Running = "running";

    public const string Completed = "completed";

    public const string Failed = "failed";
}

/// <summary>Why a download run wrote every record OSDU serves again, rather than only what was new or changed.</summary>
public static class DownloadRewrite
{
    /// <summary>The run asked for it (<c>force</c> in its payload).</summary>
    public const string Forced = "forced";

    /// <summary>The run writes to another folder than the one the last completed run wrote, which holds none of the files.</summary>
    public const string Moved = "moved";
}

/// <summary>
/// Where a downloaded record stands (osdu/docs/reference/flow/download.md, What a run records), as a page filters the records by
/// it: in the order a page lists them.
/// </summary>
public static class DownloadRecordStates
{
    /// <summary>OSDU serves it and its file holds the version listed.</summary>
    public const string Written = "written";

    /// <summary>OSDU serves it and no file holds the version listed yet: the next run writes it.</summary>
    public const string Due = "due";

    /// <summary>Its last write did not happen, and says why.</summary>
    public const string Problem = "problem";

    /// <summary>OSDU no longer serves it; its file stays where it is.</summary>
    public const string Gone = "gone";

    public static IReadOnlyList<string> All { get; } = [Written, Due, Problem, Gone];

    public static bool IsKnown(string? state) => state is not null && All.Contains(state, StringComparer.Ordinal);

    /// <summary>Where a record stands: gone first, then a failed write, then whether its file holds the version listed.</summary>
    public static string Of(DateTime? goneUtc, string? problem, long? version, long? writtenVersion)
        => goneUtc is not null ? Gone
            : problem is not null ? Problem
            : writtenVersion is null || (version is { } listed && writtenVersion < listed) ? Due
            : Written;
}

/// <summary>One download as the module's database holds it.</summary>
public sealed record DownloadState
{
    public required int DownloadId { get; init; }

    public required string Partition { get; init; }

    public required Guid FlowId { get; init; }

    public required string FlowName { get; init; }

    public required string Name { get; init; }

    public required string Kind { get; init; }

    public string? Query { get; init; }

    public required string ReadMode { get; init; }

    /// <summary>The folder the last completed run wrote the files to; null until a run completed.</summary>
    public string? Location { get; init; }

    public DateTime CreatedUtc { get; init; }

    public DateTime UpdatedUtc { get; init; }

    public long? LastRunId { get; init; }

    public DateTime? LastCompletedUtc { get; init; }
}

/// <summary>How many records of a download stand where (<see cref="DownloadRecordStates"/>), and the bytes their files hold.</summary>
public sealed record DownloadCounts(long Written, long Due, long Problem, long Gone, long Bytes)
{
    /// <summary>The records OSDU serves: every one but the gone.</summary>
    public long Served => Written + Due + Problem;
}

/// <summary>One run of a download as the ledger holds it.</summary>
public sealed record DownloadRunState
{
    public long DownloadRunId { get; init; }

    public int DownloadId { get; init; }

    public Guid? RunId { get; init; }

    public string Actor { get; init; } = "unknown";

    public string Status { get; init; } = DownloadRunStatus.Running;

    public DateTime StartedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    public string ReadMode { get; init; } = "search";

    public string Location { get; init; } = string.Empty;

    /// <summary><see cref="DownloadRewrite.Forced"/>, <see cref="DownloadRewrite.Moved"/>, or null for a run that wrote only what was due.</summary>
    public string? Rewrite { get; init; }

    public long Listed { get; init; }

    public int Pages { get; init; }

    public long Requests { get; init; }

    public long Added { get; init; }

    public long Changed { get; init; }

    public long Gone { get; init; }

    public long Returned { get; init; }

    public long Written { get; init; }

    public long Bytes { get; init; }

    public long Missing { get; init; }

    public string? Error { get; init; }
}

/// <summary>One record of a download as the ledger holds it.</summary>
public sealed record DownloadRecordState
{
    public required long DownloadRecordId { get; init; }

    public required int DownloadId { get; init; }

    public required string TargetId { get; init; }

    public string? Kind { get; init; }

    public long? Version { get; init; }

    public required string FileName { get; init; }

    public DateTime FirstSeenUtc { get; init; }

    public DateTime ChangedUtc { get; init; }

    public DateTime? GoneUtc { get; init; }

    public long? WrittenVersion { get; init; }

    public DateTime? WrittenUtc { get; init; }

    public long? WrittenRunId { get; init; }

    public long? Bytes { get; init; }

    /// <summary>The SHA-256 of the file's bytes, in lower-case hex.</summary>
    public string? Sha256 { get; init; }

    public string? Problem { get; init; }

    public DateTime? ProblemUtc { get; init; }

    /// <summary>Where the record stands (<see cref="DownloadRecordStates"/>).</summary>
    public string State => DownloadRecordStates.Of(GoneUtc, Problem, Version, WrittenVersion);
}

/// <summary>One id a run listed, with its kind and version and the names its file may take (osdu/docs/reference/flow/download.md, File names).</summary>
public sealed record DownloadScanRow(string TargetId, string? Kind, long? Version, string? PlainName, string DistinctName);

/// <summary>What a run's listing changed of its download: the ids listed, new, changed, no longer served and served again.</summary>
public sealed record DownloadMerge(long Listed, long Added, long Changed, long Gone, long Returned);

/// <summary>One record a run writes: its row, its id, the version listed and its file.</summary>
public sealed record DownloadDue(long DownloadRecordId, string TargetId, long? Version, string FileName);

/// <summary>What came of writing one record: the version written, with the file's size and SHA-256, or why nothing was.</summary>
public sealed record DownloadWrite(long DownloadRecordId, long? Version, long? Bytes, byte[]? Sha256, string? Problem)
{
    public static DownloadWrite Written(long downloadRecordId, long? version, long bytes, byte[] sha256)
        => new(downloadRecordId, version, bytes, sha256 ?? throw new ArgumentNullException(nameof(sha256)), null);

    public static DownloadWrite Missed(long downloadRecordId, string problem)
        => new(downloadRecordId, null, null, null, problem ?? throw new ArgumentNullException(nameof(problem)));
}
