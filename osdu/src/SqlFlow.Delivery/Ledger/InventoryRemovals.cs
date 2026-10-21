using System.Globalization;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// Removing what an inventory found (osdu/docs/reference/flow/inventory.md, What a removal does;
/// osdu/docs/wiki/decisions/0014-inventory-removals.md): which findings may be removed, how much of a record a
/// removal takes, and what it came to for each id. The ids OSDU serves go from OSDU; the gone ids, which OSDU no longer
/// serves, go from the inventory.
/// </summary>
public static class InventoryRemovals
{
    /// <summary>
    /// The findings whose ids an inventory flow may remove from OSDU, when its document allows it: what OSDU serves that no
    /// ledger holds live. An orphan no ledger knows; a stale id a ledger marks removed; a forgotten id belongs to a record
    /// purged from its ledger.
    /// </summary>
    public static IReadOnlyList<string> Removable { get; } = [InventoryFindings.Orphan, InventoryFindings.Stale, InventoryFindings.Forgotten];

    /// <summary>A soft delete (<c>POST /records/delete</c>): the record stops resolving and OSDU keeps it, so it can be brought back.</summary>
    public const string SoftDelete = "record";

    /// <summary>A purge (<c>DELETE /records/{id}</c>): the record and every version of it destroyed; it cannot be undone.</summary>
    public const string Purge = "everything";

    /// <summary>
    /// The inventory's own row of an id OSDU no longer serves, with the versions kept of it: nothing is asked of OSDU, and it
    /// cannot be undone, though a later build lists again an id OSDU serves again. Only gone ids are removed so, and they only so.
    /// </summary>
    public const string InventoryOnly = "inventory";

    /// <summary>Every scope a removal takes, in the order a page offers them.</summary>
    public static IReadOnlyList<string> Scopes { get; } = [SoftDelete, Purge, InventoryOnly];

    /// <summary>Removed as the removal asked: from OSDU, or, a gone id, from the inventory (which keeps only how many, not the id).</summary>
    public const string Removed = "removed";

    /// <summary>OSDU no longer served it when the removal reached it: nothing was asked of OSDU.</summary>
    public const string Gone = "gone";

    /// <summary>Left as it was, since something moved since the inventory found it; the reason says what.</summary>
    public const string Skipped = "skipped";

    /// <summary>OSDU refused or failed the removal; the reason says what it answered.</summary>
    public const string Failed = "failed";

    /// <summary>Every outcome an id of a removal has, in the order a page lists them.</summary>
    public static IReadOnlyList<string> Outcomes { get; } = [Removed, Gone, Skipped, Failed];

    /// <summary>The ids one round of a removal reads, checks, removes and records in one transaction.</summary>
    public const int Chunk = 500;

    /// <summary>
    /// The ids one round of a removal from the inventory alone takes in one transaction: no request to OSDU paces it, and a
    /// round stays under the locks at which SQL Server would lock the whole table rather than its rows.
    /// </summary>
    public const int InventoryChunk = 2000;

    /// <summary>The longest reason the ledger keeps of one id's outcome.</summary>
    public const int MaxReasonLength = 1000;

    /// <summary>The activity a removal is audited under.</summary>
    public const string ActivityKind = "inventory-remove";

    public static bool IsRemovable(string? finding) => finding is not null && Removable.Contains(finding, StringComparer.Ordinal);

    /// <summary>Whether a removal can be asked for the ids of <paramref name="finding"/>: from OSDU (<see cref="Removable"/>), or gone, from the inventory.</summary>
    public static bool IsAskable(string? finding) => IsRemovable(finding) || finding == InventoryFindings.Gone;

    /// <summary>Every finding a removal can be asked for, as a refusal lists them.</summary>
    public static IReadOnlyList<string> Askable { get; } = [.. Removable, InventoryFindings.Gone];

    public static bool IsScope(string? scope) => scope is SoftDelete or Purge or InventoryOnly;

    public static bool IsOutcome(string? outcome) => outcome is not null && Outcomes.Contains(outcome, StringComparer.Ordinal);

    /// <summary>
    /// Why a removal of <paramref name="finding"/>'s ids cannot take <paramref name="scope"/>, or null when it can: an id OSDU
    /// no longer serves has nothing left in OSDU to remove, and an id OSDU serves would be listed again by the next build.
    /// </summary>
    public static string? Mismatch(string finding, string scope)
    {
        if (finding == InventoryFindings.Gone)
        {
            return scope == InventoryOnly
                ? null
                : $"a gone id is one OSDU no longer serves, so there is nothing of it in OSDU to remove; its removal takes the inventory's own row ({InventoryOnly}).";
        }

        return scope == InventoryOnly
            ? $"OSDU serves an id the inventory finds {finding}, and the next build would list it again; it is removed from OSDU ({SoftDelete} or {Purge}), and only gone ids are removed from the inventory alone."
            : null;
    }

    /// <summary>A scope in words, as a run's log and a page say it.</summary>
    public static string Describe(string scope) => scope switch
    {
        Purge => "purged (every version destroyed)",
        InventoryOnly => "removed from the inventory alone (nothing asked of OSDU)",
        _ => "soft deleted (reversible)",
    };

    /// <summary>A reason as the ledger keeps it: cut to its column.</summary>
    public static string? Reason(string? reason)
        => reason is null ? null : reason.Length <= MaxReasonLength ? reason : reason[..MaxReasonLength];
}

/// <summary>
/// What a removal starts with: its inventory, the run it is, who asked, what it was asked to remove, and the selection it takes
/// its ids from when it takes them from one.
/// </summary>
public sealed record InventoryRemovalStart(
    int InventoryId, Guid? RunId, string Actor, string Finding, string Scope, bool NamesIds, long Requested, long? ActivityId, DateTime StartedUtc,
    long? SelectionId = null);

/// <summary>
/// An id a removal may act on, as the inventory holds it and as the ledgers of the partition hold it now: the finding the
/// inventory recorded, and the finding the same comparison gives it at this moment.
/// </summary>
public sealed record InventoryRemovalCandidate(
    long InventoryRecordId,
    string TargetId,
    long? Version,
    string? CreateUser,
    string Recorded,
    string Current,
    Guid? LedgerFlowId,
    Guid? DeliveryKey,
    string? LedgerStatus,
    long? ArtifactId,
    string? ArtifactState);

/// <summary>
/// What one round of a removal from the inventory alone did: the ids it read, the last of them (the next round reads after it),
/// how many rows it removed, and how many it kept (each kept with why, as a skipped id of the removal).
/// </summary>
public sealed record InventoryRemovalRound(int Read, long Last, long Removed, long Skipped);

/// <summary>What a removal did to one id, and why.</summary>
public sealed record InventoryRemovalItem(
    long InventoryRecordId, string TargetId, long? Version, string Finding, string Outcome, string? Reason, Guid? LedgerFlowId, Guid? DeliveryKey);

/// <summary>How many ids of a removal came to each outcome.</summary>
public sealed record InventoryRemovalTally(long Removed, long Gone, long Skipped, long Failed)
{
    public static InventoryRemovalTally None { get; } = new(0, 0, 0, 0);

    public long Total => Removed + Gone + Skipped + Failed;

    public InventoryRemovalTally Add(IEnumerable<InventoryRemovalItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var (removed, gone, skipped, failed) = (Removed, Gone, Skipped, Failed);
        foreach (var item in items)
        {
            switch (item.Outcome)
            {
                case InventoryRemovals.Removed: removed++; break;
                case InventoryRemovals.Gone: gone++; break;
                case InventoryRemovals.Skipped: skipped++; break;
                default: failed++; break;
            }
        }

        return new InventoryRemovalTally(removed, gone, skipped, failed);
    }

    public string Describe() => string.Create(CultureInfo.InvariantCulture, $"{Removed} removed, {Gone} already gone, {Skipped} skipped, {Failed} failed");
}

/// <summary>One removal of an inventory's ids as the module's database holds it.</summary>
public sealed record InventoryRemovalState
{
    public required long InventoryRemovalId { get; init; }

    public required int InventoryId { get; init; }

    public Guid? RunId { get; init; }

    public required string Actor { get; init; }

    public required string Finding { get; init; }

    public required string Scope { get; init; }

    /// <summary>True when the removal named its ids; false when it took every id of the finding.</summary>
    public required bool NamesIds { get; init; }

    /// <summary>How many ids the operator was shown.</summary>
    public required long Requested { get; init; }

    /// <summary>running, completed or failed.</summary>
    public required string Status { get; init; }

    public required DateTime StartedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    public long Removed { get; init; }

    public long Gone { get; init; }

    public long Skipped { get; init; }

    public long Failed { get; init; }

    public string? Error { get; init; }

    /// <summary>The audit trail's activity of the removal.</summary>
    public long? ActivityId { get; init; }

    /// <summary>The selection the removal took its ids from; null for one that named its ids or took every id of the finding.</summary>
    public long? SelectionId { get; init; }
}

/// <summary>One id of a removal as the module's database holds it.</summary>
public sealed record InventoryRemovalItemState
{
    public required long InventoryRemovalItemId { get; init; }

    public required long InventoryRemovalId { get; init; }

    public required long InventoryRecordId { get; init; }

    public required string TargetId { get; init; }

    public long? Version { get; init; }

    public required string Finding { get; init; }

    public required string Outcome { get; init; }

    public string? Reason { get; init; }

    public Guid? LedgerFlowId { get; init; }

    public Guid? DeliveryKey { get; init; }

    public required DateTime RecordedUtc { get; init; }
}
