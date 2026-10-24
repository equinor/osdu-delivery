using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Engine.Inventories;

/// <summary>
/// One copy of a group of duplicates as it is judged: its inventory row and OSDU id, the finding the inventory gives it, what
/// storage answered for it (<see cref="InventoryDuplicates.Held"/>, <see cref="InventoryDuplicates.Missing"/> or
/// <see cref="InventoryDuplicates.Unanswered"/>) at which version, how many values it holds under <c>data</c>, and how many
/// records name it (null where the search could not count them, with why).
/// </summary>
public sealed record DuplicateCopy(
    long InventoryRecordId, string TargetId, string Finding, string StorageState, long? StoredVersion, int? Attributes, long? MentionedBy,
    string? MentionsProblem = null);

/// <summary>
/// How complete a copy of a record is, and which copy of a group of duplicates is kept (osdu/docs/reference/flow/inventory-duplicates.md,
/// Which copy is kept): the copy holding the most values under <c>data</c>, a ledger's copy before any other, and the rest
/// picked to remove where the flow may remove them; a group the rules cannot decide is left for an operator, and nothing of it
/// is removed until one chooses. Pure: the same copies are judged the same way, in a run and when an operator's choice is
/// written.
/// </summary>
public static class InventoryDuplicatePicker
{
    /// <summary>
    /// How many values <paramref name="data"/> holds: every number, true or false, and text holding more than spaces, each item
    /// of a list counted on its own, and every value inside an object. A copy whose bulk data a DDMS keeps holds the link the
    /// DDMS writes to it (<c>ExtensionProperties.wdms.bulkURI</c>, and its entry in <c>DDMSDatasets</c>), and a copy naming its
    /// files names them under <c>Datasets</c>, so the count tells a copy with them from one without.
    /// </summary>
    public static int Attributes(JsonNode? data)
    {
        switch (data)
        {
            case null:
                return 0;
            case JsonObject block:
                {
                    var count = 0;
                    foreach (var (_, value) in block)
                    {
                        count = checked(count + Attributes(value));
                    }

                    return count;
                }

            case JsonArray list:
                {
                    var count = 0;
                    foreach (var item in list)
                    {
                        count = checked(count + Attributes(item));
                    }

                    return count;
                }

            default:
                return data.GetValueKind() switch
                {
                    JsonValueKind.String => string.IsNullOrWhiteSpace(data.GetValue<string>()) ? 0 : 1,
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => 1,
                    _ => 0,
                };
        }
    }

    /// <summary>
    /// How group <paramref name="groupNumber"/> comes out, and each copy's role in it. With <paramref name="chosen"/>, the copy an
    /// operator chose is kept; else the check picks: the copy a ledger holds, when one does and none holds more values, or else
    /// the copy holding the most values, alone. The other copies are picked to remove where <paramref name="removable"/> (the
    /// findings the flow may remove) holds their finding, and left with why otherwise. The group is left for review when two
    /// copies a ledger holds share the key, when the ledger's copy holds fewer values than another, when two copies hold the
    /// most, when a copy picked to remove is named by another record or that could not be counted, and when storage did not say
    /// whether it holds a copy. A group of which storage holds one copy or none is settled.
    /// </summary>
    public static InventoryDuplicateJudgement Judge(int groupNumber, IReadOnlyList<DuplicateCopy> copies, IReadOnlySet<string> removable, long? chosen)
    {
        ArgumentNullException.ThrowIfNull(copies);
        ArgumentNullException.ThrowIfNull(removable);
        if (copies.Count == 0)
        {
            throw new ArgumentException("A group of duplicates holds at least one copy.", nameof(copies));
        }

        var held = copies.Where(c => c.StorageState == InventoryDuplicates.Held).ToList();
        var unanswered = copies.Where(c => c.StorageState == InventoryDuplicates.Unanswered).ToList();

        if (chosen is { } keep)
        {
            var kept = held.FirstOrDefault(c => c.InventoryRecordId == keep);
            if (kept is null)
            {
                var missing = copies.FirstOrDefault(c => c.InventoryRecordId == keep);
                return Review(groupNumber, copies, missing is null
                    ? "the copy chosen to keep is not one of the group's"
                    : $"the copy chosen to keep, {missing.TargetId}, is no longer held by storage; choose another");
            }

            return Decide(groupNumber, copies, kept, removable, InventoryDuplicates.Chosen, $"an operator chose to keep {kept.TargetId}", chosenByOperator: true);
        }

        if (unanswered.Count > 0)
        {
            return Review(groupNumber, copies, string.Create(
                CultureInfo.InvariantCulture,
                $"storage did not say whether it holds {Ids(unanswered)}, so the group cannot be judged; check again"));
        }

        if (held.Count < 2)
        {
            return held.Count == 1
                ? Settled(groupNumber, copies, held[0], $"storage holds one copy of the key now, {held[0].TargetId}: the others are gone")
                : Settled(groupNumber, copies, null, "storage holds none of the group's copies now");
        }

        var ledgers = held.Where(c => InventoryDuplicates.IsLedgerHeld(c.Finding)).ToList();
        if (ledgers.Count > 1)
        {
            return Review(groupNumber, copies, string.Create(
                CultureInfo.InvariantCulture,
                $"{ledgers.Count} copies are held by a ledger ({Ids(ledgers)}): a delivery writes one record under more than one id, so look at the source and the key before removing any"));
        }

        var most = held.Max(c => c.Attributes ?? 0);
        DuplicateCopy keeper;
        string why;
        if (ledgers.Count == 1)
        {
            keeper = ledgers[0];
            var fuller = held.FirstOrDefault(c => (c.Attributes ?? 0) > (keeper.Attributes ?? 0));
            if (fuller is not null)
            {
                return Review(groupNumber, copies, string.Create(
                    CultureInfo.InvariantCulture,
                    $"the copy a ledger holds, {keeper.TargetId}, holds {keeper.Attributes ?? 0} values and {fuller.TargetId} holds {fuller.Attributes ?? 0}: the delivery leaves out what the other copy holds, so compare them before choosing"));
            }

            why = string.Create(CultureInfo.InvariantCulture, $"a ledger holds it ({keeper.Finding}), and no copy holds more values than its {most}");
        }
        else
        {
            var top = held.Where(c => (c.Attributes ?? 0) == most).ToList();
            if (top.Count > 1)
            {
                return Review(groupNumber, copies, string.Create(
                    CultureInfo.InvariantCulture,
                    $"{top.Count} copies hold the most values ({most} each: {Ids(top)}), so compare them and choose the copy to keep"));
            }

            keeper = top[0];
            why = string.Create(CultureInfo.InvariantCulture, $"it holds the most values: {most}");
        }

        var judged = Decide(groupNumber, copies, keeper, removable, InventoryDuplicates.Picked, why, chosenByOperator: false);
        var named = held
            .Where(c => judged.Copies.Any(f => f.InventoryRecordId == c.InventoryRecordId && f.Role == InventoryDuplicates.Remove))
            .Where(c => c.MentionedBy is not 0)
            .ToList();
        if (named.Count == 0)
        {
            return judged;
        }

        var first = named[0];
        return Review(groupNumber, copies, first.MentionedBy is { } count
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{first.TargetId}, picked to remove, is named by {count} record(s){(named.Count > 1 ? $" (and {named.Count - 1} more copy(ies) picked to remove are named too)" : string.Empty)}: removing it would leave them pointing at nothing, so repoint them to {keeper.TargetId}, or choose the copy to keep")
            : $"the search could not count the records naming {first.TargetId}, picked to remove ({first.MentionsProblem ?? "it gave no count"}), so whether removing it leaves records pointing at nothing is not known; choose the copy to keep");
    }

    /// <summary>The group decided: <paramref name="keeper"/> kept, each other copy held picked to remove or left with why, each copy gone marked so.</summary>
    private static InventoryDuplicateJudgement Decide(
        int groupNumber, IReadOnlyList<DuplicateCopy> copies, DuplicateCopy keeper, IReadOnlySet<string> removable, string outcome, string why, bool chosenByOperator)
    {
        var figures = new List<InventoryDuplicateCopyFigures>(copies.Count);
        foreach (var copy in copies)
        {
            if (copy.InventoryRecordId == keeper.InventoryRecordId)
            {
                figures.Add(Figures(copy, InventoryDuplicates.Keep, chosenByOperator ? "chosen by an operator" : why));
            }
            else if (copy.StorageState == InventoryDuplicates.Unanswered)
            {
                figures.Add(Figures(copy, InventoryDuplicates.Leave, "storage did not say whether it holds it, so it is left; check again to judge it"));
            }
            else if (copy.StorageState != InventoryDuplicates.Held)
            {
                figures.Add(Figures(copy, InventoryDuplicates.Gone, GoneWhy(copy)));
            }
            else
            {
                var (role, reason) = Other(copy, keeper, removable);
                if (role == InventoryDuplicates.Remove && chosenByOperator && copy.MentionedBy is { } named and > 0)
                {
                    reason = string.Create(CultureInfo.InvariantCulture, $"{reason}; {named} record(s) name it, and are left pointing at nothing once it goes");
                }

                figures.Add(Figures(copy, role, reason));
            }
        }

        return new InventoryDuplicateJudgement(groupNumber, outcome, InventoryDuplicates.Reason(why), keeper.InventoryRecordId, figures);
    }

    /// <summary>A copy beside the one kept: picked to remove, or left, with why.</summary>
    private static (string Role, string Reason) Other(DuplicateCopy copy, DuplicateCopy keeper, IReadOnlySet<string> removable)
    {
        var compared = string.Create(CultureInfo.InvariantCulture, $"it holds {copy.Attributes ?? 0} values, and the copy kept holds {keeper.Attributes ?? 0}");
        if (copy.Finding == InventoryFindings.Foreign)
        {
            return (InventoryDuplicates.Leave, $"another identity created it, so only its owner can remove it; {compared}");
        }

        if (InventoryDuplicates.IsLedgerHeld(copy.Finding))
        {
            return (InventoryDuplicates.Leave, $"a ledger holds it ({copy.Finding}): its delivery flow writes it, so it is removed through that flow, not here; {compared}");
        }

        if (removable.Contains(copy.Finding))
        {
            return (InventoryDuplicates.Remove, compared);
        }

        if (InventoryRemovals.Removable.Contains(copy.Finding, StringComparer.Ordinal))
        {
            return (InventoryDuplicates.Leave, removable.Count == 0
                ? $"the flow declares no removal, so it only reads OSDU; {compared}"
                : $"the flow allows removing {string.Join(", ", removable.Order(StringComparer.Ordinal))} ids, not {copy.Finding} ones; {compared}");
        }

        return (InventoryDuplicates.Leave, $"the inventory finds it {copy.Finding}, and only orphan, stale and forgotten ids are removed from an inventory; {compared}");
    }

    private static InventoryDuplicateJudgement Review(int groupNumber, IReadOnlyList<DuplicateCopy> copies, string why)
        => new(
            groupNumber, InventoryDuplicates.Review, InventoryDuplicates.Reason(why), null,
            copies.Select(c => c.StorageState == InventoryDuplicates.Held || c.StorageState == InventoryDuplicates.Unanswered
                ? Figures(c, InventoryDuplicates.ForReview, Described(c))
                : Figures(c, InventoryDuplicates.Gone, GoneWhy(c))).ToList());

    private static InventoryDuplicateJudgement Settled(int groupNumber, IReadOnlyList<DuplicateCopy> copies, DuplicateCopy? left, string why)
        => new(
            groupNumber, InventoryDuplicates.Settled, InventoryDuplicates.Reason(why), left?.InventoryRecordId,
            copies.Select(c => c.InventoryRecordId == left?.InventoryRecordId
                ? Figures(c, InventoryDuplicates.Keep, "the one copy storage still holds")
                : Figures(c, InventoryDuplicates.Gone, GoneWhy(c))).ToList());

    private static InventoryDuplicateCopyFigures Figures(DuplicateCopy copy, string role, string? reason)
        => new(copy.InventoryRecordId, copy.StorageState, copy.StoredVersion, copy.Attributes, copy.MentionedBy, role, InventoryDuplicates.Reason(reason));

    private static string GoneWhy(DuplicateCopy copy) => copy.StorageState == InventoryDuplicates.Unanswered
        ? "storage did not say whether it holds it"
        : "storage no longer holds it";

    private static string Described(DuplicateCopy copy) => copy.StorageState == InventoryDuplicates.Unanswered
        ? "storage did not say whether it holds it"
        : string.Create(
            CultureInfo.InvariantCulture,
            $"it holds {copy.Attributes ?? 0} values{(copy.MentionedBy is { } named and > 0 ? $", and {named} record(s) name it" : string.Empty)}");

    private static string Ids(IReadOnlyList<DuplicateCopy> copies)
        => string.Join(", ", copies.Take(3).Select(c => c.TargetId)) + (copies.Count > 3 ? string.Create(CultureInfo.InvariantCulture, $" and {copies.Count - 3} more") : string.Empty);
}
