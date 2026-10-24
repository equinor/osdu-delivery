using System.Globalization;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// What an inventory finds of an id (osdu/docs/reference/flow/inventory-builds.md, The findings): what OSDU serves of it set
/// against what the ledgers of the partition hold of it.
/// </summary>
public static class InventoryFindings
{
    /// <summary>A record a ledger delivered at the version OSDU serves, or an id a delivery minted that its unit committed.</summary>
    public const string Tracked = "tracked";

    /// <summary>A record a ledger delivered, which OSDU serves at another version.</summary>
    public const string Drifted = "drifted";

    /// <summary>A record a ledger never confirmed (pending, held or failed, no version), or an id a delivery is still making: a write that landed and was not acknowledged.</summary>
    public const string Unconfirmed = "unconfirmed";

    /// <summary>A record a ledger marks removed, or an id an undo removed or left behind, that OSDU still serves.</summary>
    public const string Stale = "stale";

    /// <summary>A dataset a later delivery of its record replaced, kept live on purpose since earlier versions of the record name it.</summary>
    public const string Superseded = "superseded";

    /// <summary>An id whose undo is due or failed: what an unfinished delivery left, not taken back yet.</summary>
    public const string Undoing = "undoing";

    /// <summary>A record purged from its ledger, or an id a delivery of one minted, that OSDU still serves.</summary>
    public const string Forgotten = "forgotten";

    /// <summary>An id no ledger knows, created by an identity this estate writes as.</summary>
    public const string Orphan = "orphan";

    /// <summary>An id no ledger knows, created by another identity.</summary>
    public const string Foreign = "foreign";

    /// <summary>An id a ledger expects (a delivered record, a live minted id) that storage does not hold.</summary>
    public const string Missing = "missing";

    /// <summary>An id a ledger expects that storage holds and the inventory's read did not list: the index has not caught up, or it is outside the read.</summary>
    public const string Unlisted = "unlisted";

    /// <summary>An id OSDU no longer serves that no ledger expects: removed, as the ledgers say.</summary>
    public const string Gone = "gone";

    /// <summary>An id a build listed that no reconcile has compared with the ledgers yet.</summary>
    public const string Unreconciled = "unreconciled";

    /// <summary>Every finding, in the order a report lists them.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Orphan, Missing, Undoing, Forgotten, Stale, Unconfirmed, Drifted, Unlisted, Foreign, Superseded, Tracked, Gone, Unreconciled];

    /// <summary>The findings a report raises: what the ledgers and OSDU disagree on.</summary>
    public static IReadOnlyList<string> Raised { get; } = [Orphan, Missing, Undoing, Forgotten, Stale, Unconfirmed, Drifted, Unlisted];

    public static bool IsKnown(string? finding) => finding is not null && All.Contains(finding, StringComparer.Ordinal);
}

/// <summary>One inventory as the module's database holds it.</summary>
public sealed record InventoryState
{
    public required int InventoryId { get; init; }

    public required string Partition { get; init; }

    public required Guid FlowId { get; init; }

    public required string FlowName { get; init; }

    public required string Name { get; init; }

    public required string Kind { get; init; }

    public string? Query { get; init; }

    public required string ReadMode { get; init; }

    public required string Versions { get; init; }

    public string? OwnersJson { get; init; }

    public string? OwnersSource { get; init; }

    public required DateTime CreatedUtc { get; init; }

    public required DateTime UpdatedUtc { get; init; }

    public long? LastBuildRunId { get; init; }

    public DateTime? LastBuiltUtc { get; init; }

    public long? LastReconcileRunId { get; init; }

    public DateTime? LastReconciledUtc { get; init; }
}

/// <summary>One build or reconcile of an inventory as the module's database holds it.</summary>
public sealed record InventoryRunState
{
    public required long InventoryRunId { get; init; }

    public required int InventoryId { get; init; }

    public Guid? RunId { get; init; }

    public required string Operation { get; init; }

    public required string Actor { get; init; }

    public required string Status { get; init; }

    public required DateTime StartedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    public required string ReadMode { get; init; }

    public long Listed { get; init; }

    public int Pages { get; init; }

    public long Requests { get; init; }

    public long Added { get; init; }

    public long Changed { get; init; }

    public long Gone { get; init; }

    public long Returned { get; init; }

    public long MissingChecked { get; init; }

    public string? FindingsJson { get; init; }

    public string? OwnersJson { get; init; }

    public string? Error { get; init; }
}

/// <summary>One id of an inventory as the module's database holds it, with what the ledgers hold of it.</summary>
public sealed record InventoryRecordState
{
    public required long InventoryRecordId { get; init; }

    public required int InventoryId { get; init; }

    public required string TargetId { get; init; }

    public string? Kind { get; init; }

    public long? Version { get; init; }

    public string? CreateUser { get; init; }

    public DateTime? CreateTime { get; init; }

    public string? ModifyUser { get; init; }

    public DateTime? ModifyTime { get; init; }

    public DateTime? FirstSeenUtc { get; init; }

    public DateTime? ChangedUtc { get; init; }

    public DateTime? GoneUtc { get; init; }

    public required string Finding { get; init; }

    public required DateTime FindingUtc { get; init; }

    public Guid? LedgerFlowId { get; init; }

    public Guid? DeliveryKey { get; init; }

    public string? LedgerStatus { get; init; }

    public long? LedgerVersion { get; init; }

    public long? ArtifactId { get; init; }

    public string? ArtifactState { get; init; }

    public string? Detail { get; init; }
}

/// <summary>One id a build listed: what OSDU served of it.</summary>
public sealed record InventoryScanRow(string TargetId, string? Kind, long? Version, string? CreateUser, DateTime? CreateTime, string? ModifyUser, DateTime? ModifyTime);

/// <summary>What merging a complete read into an inventory changed.</summary>
public sealed record InventoryMerge(long Listed, long Added, long Changed, long Gone, long Returned);

/// <summary>
/// An id a ledger expects that the inventory's read did not list, to be read from storage: whether an inventory row holds it
/// already, and what the ledger holds of it.
/// </summary>
public sealed record InventoryCandidate(string TargetId, bool Known, Guid? LedgerFlowId, Guid? DeliveryKey, string? LedgerStatus, long? LedgerVersion, long? ArtifactId, string? ArtifactState);

/// <summary>What storage answered for a candidate: whether it holds the id, and the system properties it holds of it.</summary>
public sealed record InventoryCheck(InventoryCandidate Candidate, bool Held, InventoryScanRow? Headers);

/// <summary>An identity that created records of an inventory, with how many of them a ledger claims.</summary>
public sealed record InventoryOwner(string Identity, long Records);

/// <summary>One version of a record an inventory keeps every version of, as storage lists them.</summary>
public sealed record InventoryVersionsRead(long InventoryRecordId, long At, IReadOnlyList<long> Versions);

/// <summary>A record whose versions a build reads: new, or its latest version moved since they were read.</summary>
public sealed record InventoryVersionsDue(long InventoryRecordId, string TargetId, long Version);

/// <summary>What a reconcile set, as the run keeps it.</summary>
public sealed record InventoryCounts(IReadOnlyDictionary<string, long> ByFinding)
{
    public long Of(string finding) => ByFinding.GetValueOrDefault(finding);

    public long Raised => InventoryFindings.Raised.Sum(Of);

    public string Describe() => ByFinding.Count == 0
        ? "no ids"
        : string.Join(", ", InventoryFindings.All.Where(f => Of(f) > 0).Select(f => string.Create(CultureInfo.InvariantCulture, $"{Of(f)} {f}")));
}
