using System.Globalization;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// Finding the duplicate records of an inventory (osdu/docs/reference/flow/inventory-duplicates.md;
/// osdu/docs/wiki/decisions/0027-inventory-duplicate-checks.md): an operator's key, the properties two records must hold alike
/// to be copies of one another, read for every id the inventory serves; the ids that share a key grouped; each group's copies
/// read whole, and the most complete one kept; and the copies picked to remove made a selection the inventory's removal takes.
/// </summary>
public static class InventoryDuplicates
{
    /// <summary>Opened, and its check queued as a run of the inventory flow.</summary>
    public const string Queued = InventorySelections.Queued;

    /// <summary>Its check is reading OSDU.</summary>
    public const string Running = InventorySelections.Running;

    /// <summary>Its check read OSDU whole: its groups are every set of the inventory's ids that share the key.</summary>
    public const string Completed = InventorySelections.Completed;

    /// <summary>Its check stopped; it holds no group, and the reason says why.</summary>
    public const string Failed = InventorySelections.Failed;

    /// <summary>Kept past <see cref="KeptFor"/> because a removal took its picks: its key and counts stay, its groups are gone.</summary>
    public const string Expired = InventorySelections.Expired;

    /// <summary>A running check reading every id's key through the search.</summary>
    public const string ReadingKeys = "keys";

    /// <summary>A running check reading each group's copies from storage and picking the one to keep.</summary>
    public const string ReadingCopies = "copies";

    /// <summary>A group whose copies are not read yet.</summary>
    public const string Pending = "pending";

    /// <summary>A group whose copy to keep the check picked: the most complete, and the others picked to remove where the flow may.</summary>
    public const string Picked = "picked";

    /// <summary>A group whose copy to keep an operator chose.</summary>
    public const string Chosen = "chosen";

    /// <summary>A group the check would not pick for: an operator has to look at it and choose, and nothing of it is removed until then.</summary>
    public const string Review = "review";

    /// <summary>A group of which storage holds fewer than two copies now: there is nothing left to choose between.</summary>
    public const string Settled = "settled";

    /// <summary>Every outcome a group has, in the order a page lists them.</summary>
    public static IReadOnlyList<string> Outcomes { get; } = [Review, Picked, Chosen, Settled, Pending];

    /// <summary>The copy its group keeps.</summary>
    public const string Keep = "keep";

    /// <summary>A copy picked to remove: one the flow may remove that is not the copy kept.</summary>
    public const string Remove = "remove";

    /// <summary>A copy that is not kept and stays: one the inventory may not remove (another identity's, a ledger's), and why.</summary>
    public const string Leave = "leave";

    /// <summary>A copy of a group left for review: nothing is decided of it until an operator chooses.</summary>
    public const string ForReview = "review";

    /// <summary>A copy storage no longer holds: it has nothing to keep or remove.</summary>
    public const string Gone = "gone";

    /// <summary>Every role a copy has, in the order a group lists them.</summary>
    public static IReadOnlyList<string> Roles { get; } = [Keep, Remove, Leave, ForReview, Gone];

    /// <summary>Storage returned the copy when its group was read.</summary>
    public const string Held = "held";

    /// <summary>Storage holds no such record now.</summary>
    public const string Missing = "missing";

    /// <summary>Storage asked to be asked again for the copy, and did not answer the second time either.</summary>
    public const string Unanswered = "unanswered";

    /// <summary>
    /// The findings of a copy a ledger holds live: a delivered record (at its version or another, or not confirmed yet), an id a
    /// delivery minted and keeps, or one it is undoing. Such a copy is what a delivery writes, so the check never picks it to
    /// remove; a group holding two of them is a delivery writing one thing under two ids.
    /// </summary>
    public static IReadOnlyList<string> LedgerHeld { get; } =
        [InventoryFindings.Tracked, InventoryFindings.Drifted, InventoryFindings.Unconfirmed, InventoryFindings.Undoing, InventoryFindings.Superseded];

    /// <summary>The most properties a key compares.</summary>
    public const int MaxKeyProperties = 100;

    /// <summary>
    /// The most copies of one group the check reads and picks from. A key shared by more records is too broad to be one record
    /// written more than once: the group is left for review, its copies unread, and the reason says to narrow the key.
    /// </summary>
    public const int MaxCopiesPicked = 100;

    /// <summary>The most keys one write of a check keeps, in one transaction.</summary>
    public const int WriteChunk = 5000;

    /// <summary>The most groups one round of the copies pass reads, judges and writes.</summary>
    public const int GroupsPerRound = 200;

    /// <summary>How long a check's groups are kept, as a selection's ids are (<see cref="InventorySelections.KeptFor"/>).</summary>
    public static readonly TimeSpan KeptFor = InventorySelections.KeptFor;

    /// <summary>Every status a check has, in the order it moves through them.</summary>
    public static IReadOnlyList<string> Statuses { get; } = [Queued, Running, Completed, Failed, Expired];

    /// <summary>Whether a check has yet to end.</summary>
    public static bool IsUnderWay(string status) => InventorySelections.IsUnderWay(status);

    /// <summary>Whether a ledger holds the copy live (<see cref="LedgerHeld"/>).</summary>
    public static bool IsLedgerHeld(string finding) => LedgerHeld.Contains(finding, StringComparer.Ordinal);

    /// <summary>A reason as the database keeps it: at most <see cref="Data.DeliveryModel.MaxDuplicateReasonLength"/> characters.</summary>
    public static string? Reason(string? reason)
        => reason is null || reason.Length <= Data.DeliveryModel.MaxDuplicateReasonLength
            ? reason
            : string.Concat(reason.AsSpan(0, Data.DeliveryModel.MaxDuplicateReasonLength - 3), "...");
}

/// <summary>What a write of groups' judgements is: the check's own (its copies pass), an operator's choice, or a choice taken back.</summary>
public enum DuplicateChoice
{
    /// <summary>The check judged the groups; who chose a group's copy before stays as it was.</summary>
    None = 0,

    /// <summary>An operator chose the copy a group keeps: who and when are recorded with it.</summary>
    Choose = 1,

    /// <summary>An operator took a choice back: the group is the check's to pick again, and who chose is cleared.</summary>
    Clear = 2,
}

/// <summary>An id of a selection a duplicate check made, with the copy its group keeps, which a removal checks storage still holds first.</summary>
public sealed record InventorySelectionKeeper(long InventoryRecordId, long KeptInventoryRecordId, string KeptTargetId);

/// <summary>The columns a selection made of a duplicate check's picks shows beside each id: the key's paths.</summary>
public static class InventoryDuplicateColumns
{
    /// <summary>The longest list of columns a selection keeps (its <c>ColumnsJson</c>).</summary>
    public const int MaxLength = 4000;

    /// <summary>
    /// The key's first <paramref name="max"/> paths as a JSON list, as many of them as fit <see cref="MaxLength"/>; null for none.
    /// </summary>
    public static string? Of(IReadOnlyList<string> paths, int max)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var shown = new List<string>();
        foreach (var path in paths.Take(max))
        {
            shown.Add(path);
            if (System.Text.Json.JsonSerializer.Serialize(shown).Length > MaxLength)
            {
                shown.RemoveAt(shown.Count - 1);
                break;
            }
        }

        return shown.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(shown);
    }
}

/// <summary>What a check starts with: its inventory, who asked, and its key as written and hashed.</summary>
public sealed record InventoryDuplicateCheckStart(int InventoryId, string Actor, string KeyJson, string KeyHash, long? BuildRunId, DateTime CreatedUtc);

/// <summary>
/// One record the search returned while a check read the keys: its OSDU id, the version the index holds, the hash of its key
/// (null when it holds no value at any of the key's properties) and what it holds there (JSON).
/// </summary>
public sealed record InventoryDuplicateKeyHit(string TargetId, long? Version, byte[]? KeyHash, string? ValuesJson);

/// <summary>What one write of the keys kept: the inventory's ids holding a key and holding none, records outside it, and ids at another version.</summary>
public sealed record InventoryDuplicateKeyWrite(long Keyed, long Unkeyed, long Outside, long Changed);

/// <summary>What grouping the keys came to: the groups of ids that share a key, the ids in them, and the groups too broad to pick from.</summary>
public sealed record InventoryDuplicateGrouping(long Groups, long Copies, long TooBroad);

/// <summary>One copy of a group as the copies pass reads it: its inventory row and OSDU id, and the finding the inventory gives it now.</summary>
public sealed record InventoryDuplicateCopyRow(long InventoryRecordId, string TargetId, string Finding, long? ListedVersion);

/// <summary>A group the copies pass reads: its number, and its copies in the inventory's order.</summary>
public sealed record InventoryDuplicateGroupRow(int GroupNumber, IReadOnlyList<InventoryDuplicateCopyRow> Copies);

/// <summary>What the copies pass found of one copy, and the role it gives it.</summary>
public sealed record InventoryDuplicateCopyFigures(
    long InventoryRecordId, string StorageState, long? StoredVersion, int? Attributes, long? MentionedBy, string Role, string? Reason);

/// <summary>How one group came out: its outcome and why, the copy it keeps, and each copy's figures and role.</summary>
public sealed record InventoryDuplicateJudgement(
    int GroupNumber, string Outcome, string? Reason, long? KeptInventoryRecordId, IReadOnlyList<InventoryDuplicateCopyFigures> Copies);

/// <summary>How a check ended: its status, what it had to say, and why it failed.</summary>
public sealed record InventoryDuplicateCheckEnd(string Status, IReadOnlyList<string> Notes, string? Error);

/// <summary>One duplicate check of an inventory as the module's database holds it.</summary>
public sealed record InventoryDuplicateCheckState
{
    public required long InventoryDuplicateCheckId { get; init; }

    public required int InventoryId { get; init; }

    public Guid? RunId { get; init; }

    public required string Actor { get; init; }

    public required string KeyJson { get; init; }

    public required string KeyHash { get; init; }

    /// <summary>queued, running, completed, failed or expired.</summary>
    public required string Status { get; init; }

    /// <summary>keys or copies, while it runs.</summary>
    public string? Phase { get; init; }

    public string? NotesJson { get; init; }

    public long? BuildRunId { get; init; }

    public long Candidates { get; init; }

    public long Checked { get; init; }

    public long Keyed { get; init; }

    public long Unkeyed { get; init; }

    public long Outside { get; init; }

    public long Changed { get; init; }

    public long Groups { get; init; }

    public long Copies { get; init; }

    public long GroupsJudged { get; init; }

    public long Decided { get; init; }

    public long Review { get; init; }

    public long ToRemove { get; init; }

    public long Requests { get; init; }

    public long? SelectionId { get; init; }

    public DateTime? ChosenUtc { get; init; }

    public required DateTime CreatedUtc { get; init; }

    public DateTime? StartedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    public string? Error { get; init; }

    /// <summary>What the check came to, in a line a run's log writes.</summary>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"duplicate check {InventoryDuplicateCheckId}: {Status}, {Groups} group(s) of {Copies} id(s) sharing a key among {Keyed} keyed of {Candidates} served; {Decided} decided, {Review} for review, {ToRemove} copy(ies) to remove; {Requests} request(s){(Error is null ? "" : $"; {Error}")}");
}

/// <summary>One group of a check as the module's database holds it.</summary>
public sealed record InventoryDuplicateGroupState(
    int GroupNumber, int Copies, string? ValuesJson, string Outcome, string? Reason, long? KeptInventoryRecordId, string? ChosenBy, DateTime? ChosenUtc);

/// <summary>One copy of a group: the inventory's row of it now, and what the check read and decided of it.</summary>
public sealed record InventoryDuplicateMemberState(
    InventoryRecordState Record, int GroupNumber, long? IndexedVersion, string? ValuesJson, string? StorageState, long? StoredVersion, int? Attributes,
    long? MentionedBy, string? Role, string? Reason);

/// <summary>A group with its copies, as a page lists it.</summary>
public sealed record InventoryDuplicateGroupPage(InventoryDuplicateGroupState Group, IReadOnlyList<InventoryDuplicateMemberState> Members);

/// <summary>What a sweep of a partition's old checks did: those deleted whole, and those kept for a removal with their groups deleted.</summary>
public sealed record InventoryDuplicateSweep(int Deleted, int Expired);
