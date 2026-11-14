using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// Deleting an orphaned ledger (osdu/docs/reference/concepts/renaming-a-pipeline.md, Deleting what a missing document left
/// behind): what a flow no repository declares any more left behind, deleted for good by a person who decides no flow is to
/// take it over. It deletes history and never what only the ledger knows of OSDU, so a ledger is refused while it holds
/// such knowledge (<see cref="OsduLedger.DeleteOrphanedLedgersAsync"/>): a delivery ledger with a record OSDU may still
/// hold, an inventory ledger with a removal that took records out of OSDU, a dimension another flow still reads.
/// <para>
/// A deletion is asked and carried out as a move is (<see cref="LedgerMoves"/>): an activity of kind <see cref="Kind"/> on
/// the ledger itself, by the person who asked, <c>running</c> until every row is gone, carried out in committed slices under
/// the lock a move into the ledger takes, and taken up again by the control plane's pass when it stopped. While it runs the
/// ledger is no longer listed as orphaned, and no move of it is asked. The ledger's audit trail, the deletion included, its
/// entry in the directory and, for a delivery ledger, one line of each record (<c>osdu.PurgedRecord</c>) and the artifacts
/// its deliveries recorded stay.
/// </para>
/// </summary>
public static class LedgerDeletes
{
    /// <summary>The kind of the activity a deletion is asked and recorded as.</summary>
    public const string Kind = "delete-orphaned-ledger";

    /// <summary>
    /// What a deletion removes of a ledger of each kind whose rows are kept in tables of their own, parents after the rows
    /// that name them: the tables that carry the ledger's identity (<c>FlowId</c>), each with the tables whose rows name one
    /// of its rows. A delivery ledger is deleted as a flow's own is (<see cref="ILedger.DeleteLedgerAsync"/>), and a
    /// dimension ledger a dimension at a time, as a dimension its flow no longer declares is removed
    /// (<see cref="ILedger.RemoveDimensionAsync"/>).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<LedgerDeleteTable>> Trees { get; } = new Dictionary<string, IReadOnlyList<LedgerDeleteTable>>(StringComparer.Ordinal)
    {
        [LedgerKinds.Retrieval] = [new("Retrieval")],
        [LedgerKinds.Assertion] = [new("AssertionResult"), new("AssertionRun")],
        [LedgerKinds.Download] =
        [
            new("Download", "DownloadId", WideKey: false)
            {
                Children =
                [
                    new("DownloadRun", "DownloadRunId") { Children = [new("DownloadScan")] },
                    new("DownloadRecord"),
                ],
            },
        ],
        [LedgerKinds.Inventory] =
        [
            new("Inventory", "InventoryId", WideKey: false)
            {
                Children =
                [
                    new("InventoryRun", "InventoryRunId") { Children = [new("InventoryScan")] },
                    new("InventoryDuplicateCheck", "InventoryDuplicateCheckId") { Children = [new("InventoryDuplicateMember"), new("InventoryDuplicateGroup")] },
                    new("InventorySelection", "InventorySelectionId") { Children = [new("InventorySelectionItem")] },
                    new("InventoryRemoval", "InventoryRemovalId") { Children = [new("InventoryRemovalItem")] },
                    new("InventoryRecord", "InventoryRecordId", Paged: true) { Children = [new("InventoryVersion")] },
                ],
            },
        ],
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Every table of <paramref name="tree"/>, parents and the tables that name them.</summary>
    public static IEnumerable<LedgerDeleteTable> Flatten(IEnumerable<LedgerDeleteTable> tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        foreach (var table in tree)
        {
            yield return table;
            foreach (var child in Flatten(table.Children))
            {
                yield return child;
            }
        }
    }

    /// <summary>
    /// The deletions asked and not done in partition <paramref name="partitionId"/>, or in every partition when it is null:
    /// each ledger being deleted with the deletion's activity. Read on <paramref name="context"/>, in its transaction.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, long>> RunningAsync(OsduDbContext context, short? partitionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var rows = await context.DeliveryActivities.AsNoTracking()
            .Where(a => a.Kind == Kind && a.Outcome == LedgerMoves.Running && (partitionId == null || a.PartitionId == partitionId))
            .OrderBy(a => a.ActivityId)
            .Select(a => new { a.ActivityId, a.FlowId })
            .ToListAsync(ct).ConfigureAwait(false);
        var running = new Dictionary<Guid, long>();
        foreach (var row in rows)
        {
            running.TryAdd(row.FlowId, row.ActivityId);
        }

        return running;
    }

    /// <summary>What a deletion's activity records of it, as JSON.</summary>
    internal static string Write(LedgerDeleteParameters parameters) => JsonSerializer.Serialize(parameters, Json);

    /// <summary>The parameters a deletion's activity carries, or null for text that is not one.</summary>
    internal static LedgerDeleteParameters? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LedgerDeleteParameters>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>One table a deletion empties of a ledger's rows.</summary>
/// <param name="Table">The table.</param>
/// <param name="Key">The column that numbers its rows, which its <see cref="Children"/> name by the same column; null for a table no other names.</param>
/// <param name="WideKey">True when <paramref name="Key"/> is a <c>bigint</c>, false when it is an <c>int</c>.</param>
/// <param name="Paged">
/// True when a ledger can keep more of its rows than are ever listed at once: its rows go a page at a time, each page in one
/// transaction with the rows of its <see cref="Children"/> that name it, which then name nothing further themselves.
/// </param>
public sealed record LedgerDeleteTable(string Table, string? Key = null, bool WideKey = true, bool Paged = false)
{
    /// <summary>The tables whose rows name a row of this one by <see cref="Key"/>: their rows go before the row they name.</summary>
    public IReadOnlyList<LedgerDeleteTable> Children { get; init; } = [];
}

/// <summary>A deletion a person asks for: the missing document and the one partition whose orphaned ledgers of it go.</summary>
/// <param name="Kind">The kind of flow it was (<see cref="LedgerKinds"/>).</param>
/// <param name="Flow">The missing flow.</param>
/// <param name="Partition">The partition whose ledgers of it are deleted.</param>
public sealed record LedgerDeleteRequest(string Kind, string Flow, string Partition);

/// <summary>One orphaned ledger of a deletion, as it stands before anything is asked.</summary>
/// <param name="Ledger">The orphaned ledger.</param>
/// <param name="PartitionId">Its partition, as the directory numbers it.</param>
/// <param name="Partition">Its partition's data-partition-id.</param>
/// <param name="Kind">Its kind (<see cref="LedgerKinds"/>).</param>
/// <param name="FlowName">The flow that kept it.</param>
/// <param name="Interface">The interface it was kept for, empty for a flow in the single form and for every kind but delivery.</param>
/// <param name="LedgerName">The name it is kept under.</param>
/// <param name="Rows">What it holds: a delivery ledger's records, the rows of another kind's first table.</param>
/// <param name="Claimed">Of a delivery ledger's records, those that claimed an OSDU id.</param>
/// <param name="Refusal">Why it cannot be deleted now, in full, with what to do instead; null when it can.</param>
/// <param name="RefusedFor">The reason in a few words, for a line that names the ledger; null when it can be deleted.</param>
public sealed record LedgerDeleteStep(
    Guid Ledger, short PartitionId, string Partition, string Kind, string FlowName, string Interface, string LedgerName, long Rows, long Claimed,
    string? Refusal = null, string? RefusedFor = null);

/// <summary>Why an orphaned ledger cannot be deleted now.</summary>
/// <param name="Reason">In a few words, as a line beside the ledger says it: <c>OSDU may still hold 12 of its records</c>.</param>
/// <param name="Detail">In full, with the ledger, its partition, why it matters and what to do instead.</param>
public sealed record LedgerDeleteRefusal(string Reason, string Detail);

/// <summary>What a deletion's activity records of it, as JSON in <c>ParametersJson</c>.</summary>
public sealed record LedgerDeleteParameters
{
    /// <summary>The ledger deleted.</summary>
    public Guid Ledger { get; init; }

    /// <summary>The name it was kept under.</summary>
    public string LedgerName { get; init; } = string.Empty;

    /// <summary>Its kind (<see cref="LedgerKinds"/>).</summary>
    public string LedgerKind { get; init; } = LedgerKinds.Delivery;

    /// <summary>The flow that kept it, which no repository declares any more.</summary>
    public string Flow { get; init; } = string.Empty;

    /// <summary>The interface it was kept for, empty for a flow in the single form and for every kind but delivery.</summary>
    public string Interface { get; init; } = string.Empty;

    /// <summary>What it held when the deletion was asked: a delivery ledger's records, the rows of another kind's first table.</summary>
    public long Rows { get; init; }

    /// <summary>Of a delivery ledger's records, those that had claimed an OSDU id.</summary>
    public long Claimed { get; init; }
}

/// <summary>What one pass at a deletion did.</summary>
/// <param name="ActivityId">The deletion's activity; 0 for a ledger refused before anything was asked.</param>
/// <param name="Partition">The partition the ledger is kept in.</param>
/// <param name="Ledger">The ledger.</param>
/// <param name="Interface">The interface it was kept for.</param>
/// <param name="Outcome"><c>completed</c>, <c>failed</c> for one refused, or <c>running</c> for one that waits for work in flight or that another process is deleting.</param>
/// <param name="Rows">What the ledger held when the deletion was asked.</param>
/// <param name="Summary">What the activity says of it, or why nothing was asked.</param>
public sealed record LedgerDeleteResult(long ActivityId, string Partition, Guid Ledger, string Interface, string Outcome, long Rows, string Summary);
