using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// One name of a document in a repository, as the read models describe it: a delivery interface
/// (<see cref="DeliveryInterface"/>) or a flow of another kind that keeps a ledger (<see cref="DeliveryLedgerFlow"/>), with
/// the ledger it keeps in a partition and its document's hash.
/// </summary>
public sealed record LedgerHolder(
    Guid RepoId, string Kind, string FlowName, string Interface, string Partition, string RelativePath, string DocumentHash, Guid LedgerFlowId,
    string LedgerName, bool Active, DateTime FirstSeenUtc)
{
    /// <summary>What the names of one document share: the kind, the document, the interface and the partition.</summary>
    public string Key => string.Join('\u001f', Kind, DocumentHash, Interface.ToUpperInvariant(), Partition.ToUpperInvariant());
}

/// <summary>
/// The rule that keeps each document's ledger with the name that owns it (osdu/docs/reference/concepts/renaming-a-pipeline.md;
/// <see cref="LedgerMoves"/>), for every kind of flow that keeps a ledger. A flow keeps its ledger under its name, so renaming
/// it, or copying its file, gives one document a second ledger identity. A ledger belongs to its document: the names of a
/// repository with one kind, hash, interface and partition are one document, and the one the repository declares that a
/// sync found last (the newest, then the file last by path) owns it. The ledger of every other name of the document,
/// declared or not, that holds anything moves to it, unless a name of another document keeps that ledger (<c>ledger:</c>)
/// or its own document did not read or parse in this sync. A renamed pipeline therefore takes the old name's ledger, a file
/// copied by mistake takes the original's, and deleting the copy gives it back. Two declared names of one document are
/// reported, once, since one of them is most likely a mistake; a move whose ledgers are kept in two partitions is reported
/// and not asked. The sync makes the moves it asked once its transaction has committed, a slice at a time
/// (<see cref="DeliveryCatalogSync.CommittedAsync"/>). A flow changed beyond its name is another document: its old name's
/// ledger is orphaned, and a person binds it (<see cref="LedgerBindings"/>).
/// </summary>
public static class LedgerOwnership
{
    /// <summary>
    /// Asks for the moves <paramref name="rows"/> call for: the names of <paramref name="repoId"/>'s documents, every one the
    /// read model holds, declared or not. <paramref name="keepers"/> answers which declared names, of any repository, keep a
    /// set of ledgers, so a ledger another document keeps is left where it is.
    /// </summary>
    public static async Task RequestMovesAsync(
        OsduDbContext context, Guid repoId, IReadOnlyList<LedgerHolder> rows,
        Func<IReadOnlyCollection<Guid>, CancellationToken, Task<IReadOnlyList<LedgerHolder>>> keepers, LedgerMoveGuard guard, DateTime nowUtc,
        ICollection<string> warnings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(keepers);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(warnings);

        // A document under one name has nothing to move; one under several, with a name declared, has an owner.
        var documents = rows.GroupBy(r => r.Key, StringComparer.Ordinal)
            .Select(g => g.ToList())
            .Where(g => g.Count > 1 && g.Exists(r => r.Active))
            .Select(g => (Owner: Owner(g), Rows: g))
            .ToList();
        if (documents.Count == 0)
        {
            return;
        }

        // One warning per document: a document is described once per partition where it keeps a ledger.
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (owner, group) in documents.Where(d => d.Rows.Count(r => r.Active) > 1))
        {
            var declared = group.Where(r => r.Active).ToList();
            var warning = $"{string.Join(", ", declared.Select(r => r.RelativePath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))}: {Listed(declared)} are one document under "
                + $"{declared.Select(r => r.FlowName).Distinct(StringComparer.OrdinalIgnoreCase).Count()} names, so '{owner.FlowName}', the one found last, keeps its ledger, and the others work on a ledger of their own. "
                + "Delete the copy that is a mistake; its ledger then goes back to the one that stays.";
            if (reported.Add(warning))
            {
                warnings.Add(warning);
            }
        }

        // The ledgers to move: of the document's other names, of a document this sync read.
        var moving = documents
            .SelectMany(d => d.Rows
                .Where(r => r.LedgerFlowId != d.Owner.LedgerFlowId && !guard.UnreadFiles.Contains(r.RelativePath))
                .DistinctBy(r => r.LedgerFlowId)
                .Select(r => (d.Owner, From: r)))
            .ToList();
        if (moving.Count == 0)
        {
            return;
        }

        // Of those, the ones placed in a partition that hold anything (a delivery ledger: a record), and
        // that no name of another document keeps.
        var holding = new Dictionary<Guid, short>();
        foreach (var kind in moving.GroupBy(m => m.From.Kind, StringComparer.Ordinal))
        {
            var ids = kind.Select(m => m.From.LedgerFlowId).Distinct().ToList();
            foreach (var (id, partition) in await LedgerMoves.HoldingAsync(context, kind.Key, ids, ct).ConfigureAwait(false))
            {
                holding[id] = partition;
            }
        }

        var keeping = await keepers(moving.Select(m => m.From.LedgerFlowId).Distinct().ToList(), ct).ConfigureAwait(false);
        foreach (var (owner, from) in moving)
        {
            var keptElsewhere = keeping.Any(k => k.LedgerFlowId == from.LedgerFlowId && (k.RepoId != repoId || k.Key != owner.Key));
            if (!holding.TryGetValue(from.LedgerFlowId, out var partition) || keptElsewhere)
            {
                continue;
            }

            var request = new LedgerMoveRequest(
                partition, from.LedgerFlowId, from.FlowName, from.LedgerName, owner.LedgerFlowId, owner.FlowName, owner.Interface, owner.LedgerName, repoId, owner.Kind);
            if ((await LedgerMoves.RequestAsync(context, request, nowUtc, ct).ConfigureAwait(false)).Ask == LedgerMoveAsk.OtherPartition)
            {
                warnings.Add(
                    $"{owner.RelativePath}: {Describe(owner.FlowName, owner.Interface)} is the same document as '{from.FlowName}', and its ledger is kept in another partition than the ledger of "
                    + $"'{from.FlowName}', so the sync moves no ledger: a ledger keeps the records of one partition."
                    + (owner.Kind == LedgerKinds.Delivery
                        ? $" Keep the old ledger with ledger: {LedgerEntry.OwnName(from.LedgerName, from.Partition)} in the flow's document, or leave the two apart."
                        : string.Empty));
            }
        }
    }

    /// <summary>The name that owns a document's ledger: of those the repository declares, the one a sync found last, then the file last by path.</summary>
    private static LedgerHolder Owner(IEnumerable<LedgerHolder> document)
        => document.Where(r => r.Active)
            .OrderByDescending(r => r.FirstSeenUtc)
            .ThenByDescending(r => r.RelativePath, StringComparer.Ordinal)
            .ThenByDescending(r => r.FlowName, StringComparer.Ordinal)
            .First();

    private static string Listed(IEnumerable<LedgerHolder> rows)
        => string.Join(", ", rows.Select(r => r.FlowName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).Select(name => $"'{name}'"));

    private static string Describe(string flow, string name) => name.Length == 0 ? $"flow '{flow}'" : $"interface '{name}' of flow '{flow}'";
}
