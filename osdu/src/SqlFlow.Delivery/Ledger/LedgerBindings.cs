using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The documents that left with their ledgers behind, and handing those ledgers to a document a repository declares
/// (osdu/docs/reference/concepts/renaming-a-pipeline.md, Missing documents). A flow keeps its ledger under its name, so a
/// flow that leaves every repository leaves its ledger behind: the records it delivered, with the OSDU ids they claimed, or
/// the history of a flow of another kind. The repository sync moves it to the flow that replaced it when the two are one
/// document under two names (<see cref="LedgerMoves"/>); every other case (a flow renamed and changed in one commit, a file
/// deleted in one sync and added back in another, a ledger kept before the sync recorded documents' hashes) leaves it
/// orphaned, and a person names the document that takes it over. The takeover is the same move, asked by that person and
/// recorded as theirs, one per ledger.
/// <para>
/// A ledger is orphaned when it is placed in a partition, holds anything (a delivery ledger's record, a row of another kind's
/// first table), no move takes it away already, and no declared flow keeps it: for a delivery ledger, no interface a
/// repository declares keeps its identity (<see cref="DeliveryInterface"/>); for another kind, no repository declares a flow
/// of its kind under the name it was kept by (<see cref="DeliveryLedgerFlow"/>), since those flows keep their ledgers
/// under their names alone. The orphaned ledgers of one flow are one missing document. A declared flow of its kind takes it
/// over: each ledger goes to the ledger the flow keeps, or would keep, in the same partition; a delivery ledger to the one
/// of the interface of the same name, and the ledger of a flow in the single form to the one of the flow's one interface.
/// The flows that share a mapping document, record table, interface or folder with the missing document come first.
/// </para>
/// </summary>
public static class LedgerBindings
{
    /// <summary>The outcome of a ledger a takeover leaves where it is, since the flow taking over keeps no ledger to give it to.</summary>
    public const string Skipped = "skipped";

    // The rows each orphan holds: for a delivery ledger its records and how many claimed an OSDU id, for another kind the rows
    // of its first table. Read with locking read committed, so a request in a transaction keeps no lock on a ledger of
    // millions of records past the statement.
    private const string HeldSql = """
        SELECT l.[FlowId], COUNT_BIG(r.[FlowId]), {1}
        FROM [osdu].[Ledger] AS l WITH (READCOMMITTEDLOCK)
        JOIN [osdu].[{0}] AS r WITH (READCOMMITTEDLOCK) ON r.[PartitionId] = l.[PartitionId] AND r.[FlowId] = l.[FlowId]
        WHERE l.[FlowId] IN (SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@ids))
        GROUP BY l.[FlowId];
        """;

    private static readonly CompositeFormat Held = CompositeFormat.Parse(HeldSql);

    /// <summary>
    /// The missing documents whose ledgers are kept in <paramref name="partition"/> (its data-partition-id), or in any
    /// partition when it is null: each with its orphaned ledgers there and the declared flows that can take them over, the
    /// likeliest first; the documents that left the most behind first.
    /// </summary>
    public static async Task<IReadOnlyList<OrphanedDocument>> ListAsync(OsduDbContext context, string? partition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var estate = await EstateAsync(context, ct).ConfigureAwait(false);
        var orphans = await OrphansAsync(context, estate, partition, ct).ConfigureAwait(false);
        return [.. orphans
            .GroupBy(o => (o.Ledger.Kind, Flow: o.Ledger.FlowName.ToUpperInvariant()))
            .Select(g => Document(estate, [.. g]))
            .OrderByDescending(d => d.Rows)
            .ThenBy(d => d.Kind, StringComparer.Ordinal)
            .ThenBy(d => d.FlowName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// What a takeover asks: for each orphaned ledger of the missing document <paramref name="request"/> names (in its
    /// partition, or in every one), the bind to the ledger the flow taking over keeps there, or why there is none.
    /// </summary>
    /// <exception cref="DeliveryException">The document is not missing, or no repository declares the flow that is to take it over.</exception>
    public static async Task<IReadOnlyList<LedgerTakeoverStep>> PlanAsync(OsduDbContext context, LedgerTakeoverRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.From);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.To);
        var estate = await EstateAsync(context, ct).ConfigureAwait(false);
        var orphans = (await OrphansAsync(context, estate, request.Partition, ct).ConfigureAwait(false))
            .Where(o => o.Ledger.Kind == request.Kind && string.Equals(o.Ledger.FlowName, request.From.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (orphans.Count == 0)
        {
            var where = request.Partition is null ? string.Empty : $" in partition '{request.Partition}'";
            throw new DeliveryException(
                $"No {request.Kind} flow '{request.From}' left orphaned ledgers{where}, so nothing was taken over: the Orphaned ledgers page lists the documents that did.");
        }

        var declared = Declared(estate, request.Kind, request.To.Trim());
        if (declared.Count == 0)
        {
            throw new DeliveryException(
                $"No repository declares a {request.Kind} flow '{request.To}', so the ledgers of '{request.From}' were not taken over: a missing document is taken over by a declared flow of its kind.");
        }

        if (declared.Select(d => d.RepoId).Distinct().Count() > 1)
        {
            throw new DeliveryException(
                $"Several repositories declare a {request.Kind} flow '{request.To}', each keeping ledgers of its own, so the ledgers of '{request.From}' were not taken over: rename one of them first.");
        }

        var registered = await context.DeliveryLedgers.AsNoTracking().ToDictionaryAsync(l => l.FlowId, l => l.PartitionId, ct).ConfigureAwait(false);
        return [.. orphans.Select(o => Step(o, declared, registered))];
    }

    /// <summary>
    /// Asks the move of one ledger a takeover hands over, as <paramref name="actor"/>, on <paramref name="context"/>, in
    /// whatever transaction it was opened with; answers the move's activity. In a transaction, it holds the lock every ask
    /// holds (<see cref="LedgerMoves.LockAsksAsync"/>) from before it checks the ledger is still orphaned, so what it checks
    /// holds when it asks.
    /// </summary>
    /// <exception cref="DeliveryException">The move cannot be asked; the message says why, and nothing was written.</exception>
    public static async Task<long> RequestAsync(OsduDbContext context, LedgerTakeoverStep step, string actor, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (step.To is not { } to)
        {
            throw new ArgumentException("A step with no ledger to go to is not asked.", nameof(step));
        }

        await LedgerMoves.LockAsksAsync(context, ct).ConfigureAwait(false);
        var estate = await EstateAsync(context, ct).ConfigureAwait(false);
        var ledger = await context.DeliveryLedgers.AsNoTracking()
            .FirstOrDefaultAsync(l => l.FlowId == step.Ledger && l.PartitionId == step.PartitionId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Partition '{step.Partition}' keeps no ledger {step.Ledger:D} any more, so it was not taken over.");
        if (!estate.Orphaned(ledger))
        {
            throw new DeliveryException($"The ledger of '{ledger.FlowName}' in partition '{step.Partition}' is no longer orphaned (a declared flow keeps it, or a move takes it already), so it was not taken over again.");
        }

        var asked = await LedgerMoves.RequestAsync(
            context,
            new LedgerMoveRequest(step.PartitionId, ledger.FlowId, ledger.FlowName, ledger.LedgerName, to.LedgerFlowId, to.FlowName, to.Interface, to.LedgerName, to.RepoId, ledger.Kind, actor),
            nowUtc, ct).ConfigureAwait(false);
        return asked.Ask switch
        {
            LedgerMoveAsk.Asked => asked.ActivityId,
            LedgerMoveAsk.Waits => throw new DeliveryException(
                $"Another move takes the ledger of '{ledger.FlowName}' or of '{to.FlowName}' in partition '{step.Partition}' already, so this one was not asked: take it over once that move is done, which the audit trail shows."),
            _ => throw new DeliveryException(
                $"'{to.FlowName}' keeps its ledger in another partition than '{step.Partition}', and a ledger keeps the rows of one partition, so the ledger of '{ledger.FlowName}' there was not taken over."),
        };
    }

    /// <summary>
    /// The ledger <paramref name="flow"/> keeps in <paramref name="partition"/>: one per partition, <c>name@partition</c>, for
    /// a flow that works in partitions, and one under its name for a flow whose partition is its header's, as its run keeps it.
    /// </summary>
    public static (Guid Id, string Name) OwnLedger(DeliveryLedgerFlow flow, string partition)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        return flow.Partitioned && flow.Kind != LedgerKinds.Retrieval
            ? (Identity.FlowId.Of(flow.FlowName, partition), $"{flow.FlowName}@{partition}")
            : (Identity.FlowId.Of(flow.FlowName), flow.FlowName);
    }

    /// <summary>What the takeover and the listing read of the estate: the partitions, the flows declared, and the moves under way.</summary>
    private sealed record Estate(
        IReadOnlyDictionary<short, string> Partitions,
        IReadOnlyList<DeliveryInterface> Interfaces,
        IReadOnlyList<DeliveryLedgerFlow> Flows,
        IReadOnlySet<Guid> Moving)
    {
        private readonly HashSet<Guid> _kept = [.. Interfaces.Where(i => i.Active).Select(i => i.LedgerFlowId)];

        private readonly HashSet<string> _declared = new(Flows.Where(f => f.Active).Select(f => Name(f.Kind, f.FlowName)), StringComparer.OrdinalIgnoreCase);

        /// <summary>True when no declared flow keeps <paramref name="ledger"/>, and no move takes it away already.</summary>
        public bool Orphaned(DeliveryLedger ledger)
            => !Moving.Contains(ledger.FlowId)
               && (ledger.Kind == LedgerKinds.Delivery ? !_kept.Contains(ledger.FlowId) : !_declared.Contains(Name(ledger.Kind, ledger.FlowName)));

        private static string Name(string kind, string flow) => kind + "\u001f" + flow;
    }

    /// <summary>An orphaned ledger with what it holds.</summary>
    private sealed record Orphan(DeliveryLedger Ledger, string Partition, long Rows, long Claimed);

    private static async Task<Estate> EstateAsync(OsduDbContext context, CancellationToken ct)
    {
        var partitions = await context.DeliveryLedgerPartitions.AsNoTracking().ToDictionaryAsync(p => p.PartitionId, p => p.Name, ct).ConfigureAwait(false);
        var interfaces = await context.DeliveryInterfaces.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var flows = await context.DeliveryLedgerFlows.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var moves = await context.DeliveryActivities.AsNoTracking()
            .Where(a => a.Kind == LedgerMoves.Kind && a.Outcome == LedgerMoves.Running)
            .Select(a => a.ParametersJson)
            .ToListAsync(ct).ConfigureAwait(false);
        var moving = moves.Select(LedgerMoves.Read).Where(m => m is not null).Select(m => m!.From).ToHashSet();
        return new Estate(partitions, interfaces, flows, moving);
    }

    /// <summary>The orphaned ledgers of <paramref name="partition"/>, or of every partition, that hold anything.</summary>
    private static async Task<IReadOnlyList<Orphan>> OrphansAsync(OsduDbContext context, Estate estate, string? partition, CancellationToken ct)
    {
        var scope = estate.Partitions
            .Where(p => partition is null || string.Equals(p.Value, partition.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Key)
            .ToHashSet();
        var ledgers = (await context.DeliveryLedgers.AsNoTracking()
                .Where(l => l.PartitionId != DeliveryModel.UnassignedPartition)
                .ToListAsync(ct).ConfigureAwait(false))
            .Where(l => scope.Contains(l.PartitionId) && estate.Orphaned(l))
            .ToList();
        if (ledgers.Count == 0)
        {
            return [];
        }

        var held = await HeldAsync(context, ledgers, ct).ConfigureAwait(false);
        return [.. ledgers
            .Where(l => held.TryGetValue(l.FlowId, out var rows) && rows.Rows > 0)
            .Select(l => new Orphan(l, estate.Partitions[l.PartitionId], held[l.FlowId].Rows, held[l.FlowId].Claimed))];
    }

    /// <summary>What each of <paramref name="ledgers"/> holds, read a kind at a time.</summary>
    private static async Task<IReadOnlyDictionary<Guid, (long Rows, long Claimed)>> HeldAsync(OsduDbContext context, IReadOnlyCollection<DeliveryLedger> ledgers, CancellationToken ct)
    {
        var held = new Dictionary<Guid, (long, long)>();
        foreach (var kind in ledgers.GroupBy(l => l.Kind, StringComparer.Ordinal))
        {
            var (table, claimed) = kind.Key == LedgerKinds.Delivery
                ? ("Record", "COUNT_BIG(r.[ClaimedTargetId])")
                : LedgerMoves.OtherKinds.TryGetValue(kind.Key, out var tables) ? (tables.Tables[0], "CAST(0 AS bigint)") : (null, string.Empty);
            if (table is null)
            {
                continue;
            }

            await context.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            try
            {
                await using var command = context.Database.GetDbConnection().CreateCommand();
                command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
                command.CommandText = string.Format(CultureInfo.InvariantCulture, Held, table, claimed);
                var ids = command.CreateParameter();
                ids.ParameterName = "@ids";
                ids.DbType = DbType.String;
                ids.Value = JsonSerializer.Serialize(kind.Select(l => l.FlowId).Distinct());
                command.Parameters.Add(ids);
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    held[reader.GetGuid(0)] = (reader.GetInt64(1), reader.GetInt64(2));
                }
            }
            finally
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }

        return held;
    }

    /// <summary>A missing document: the orphaned ledgers of one flow, where it was last declared, and who can take it over.</summary>
    private static OrphanedDocument Document(Estate estate, IReadOnlyList<Orphan> orphans)
    {
        var first = orphans[0].Ledger;

        // Where the flow was last declared: the read model keeps the rows of a flow that left, inactive.
        IReadOnlyList<DeliveryInterface> lastInterfaces = [];
        string? path;
        DateTime? left;
        Guid? repo;
        if (first.Kind == LedgerKinds.Delivery)
        {
            var ids = orphans.Select(o => o.Ledger.FlowId).ToHashSet();
            lastInterfaces = [.. estate.Interfaces.Where(i => ids.Contains(i.LedgerFlowId))];
            var last = lastInterfaces.MaxBy(i => i.LastSeenUtc);
            (path, left, repo) = (last?.RelativePath, last?.LastSeenUtc, last?.RepoId);
        }
        else
        {
            var last = estate.Flows
                .Where(f => f.Kind == first.Kind && string.Equals(f.FlowName, first.FlowName, StringComparison.OrdinalIgnoreCase))
                .MaxBy(f => f.LastSeenUtc);
            (path, left, repo) = (last?.RelativePath, last?.LastSeenUtc, last?.RepoId);
        }

        var ledgers = orphans
            .Select(o =>
            {
                var described = lastInterfaces.Where(i => i.LedgerFlowId == o.Ledger.FlowId).MaxBy(i => i.LastSeenUtc);
                return new OrphanedLedger(o.Ledger.FlowId, o.Partition, o.Ledger.Interface, o.Ledger.LedgerName, o.Rows, o.Claimed, described?.MappingReference, described?.RecordObject);
            })
            .OrderBy(l => l.Partition, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Interface, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new OrphanedDocument(
            first.Kind, first.FlowName, path, left, orphans.Sum(o => o.Rows), orphans.Sum(o => o.Claimed), ledgers,
            Candidates(estate, first.Kind, first.FlowName, ledgers, path is null || repo is null ? null : (repo.Value, path)));
    }

    /// <summary>
    /// The declared flows of <paramref name="kind"/> that can take over a missing document's <paramref name="ledgers"/>: those
    /// that keep, or would keep, a ledger for at least one of them. Ranked by what they share with the missing document (for a
    /// delivery flow, a mapping document its interfaces pinned, a record table they read, an interface name; for any kind,
    /// the folder it was declared in), then by how many of its ledgers they take; the one that alone ranks first, when it
    /// shares a mapping document (or for another kind, its folder), is suggested.
    /// </summary>
    private static IReadOnlyList<TakeoverCandidate> Candidates(
        Estate estate, string kind, string flowName, IReadOnlyList<OrphanedLedger> ledgers, (Guid RepoId, string Path)? origin)
    {
        var found = new List<(TakeoverCandidate Candidate, int Score)>();
        foreach (var flow in DeclaredFlows(estate, kind).Where(f => !string.Equals(f.FlowName, flowName, StringComparison.OrdinalIgnoreCase)))
        {
            var plan = ledgers
                .Select(l => Target(kind, l.Partition, l.Interface, flow) is { } to
                    ? new TakeoverPlanItem(l.Ledger, to.Interface, to.LedgerName)
                    : new TakeoverPlanItem(l.Ledger, null, null))
                .ToList();
            var takes = plan.Count(p => p.ToLedgerName is not null);
            if (takes == 0)
            {
                continue;
            }

            var shares = new List<string>();
            var score = 0;
            if (kind == LedgerKinds.Delivery)
            {
                if (ledgers.Any(l => l.Mapping is { } m && flow.Rows.Any(r => string.Equals(r.MappingReference, m, StringComparison.Ordinal))))
                {
                    shares.Add("mapping");
                    score += 8;
                }

                if (ledgers.Any(l => l.RecordObject is { } t && flow.Rows.Any(r => string.Equals(r.RecordObject, t, StringComparison.OrdinalIgnoreCase))))
                {
                    shares.Add("record table");
                    score += 4;
                }

                if (ledgers.Any(l => l.Interface.Length > 0 && flow.Rows.Any(r => string.Equals(r.Interface, l.Interface, StringComparison.OrdinalIgnoreCase))))
                {
                    shares.Add("interface");
                    score += 2;
                }
            }

            if (origin is { } o && o.RepoId == flow.RepoId && string.Equals(Folder(o.Path), Folder(flow.RelativePath), StringComparison.OrdinalIgnoreCase))
            {
                shares.Add("folder");
                score += 1;
            }

            var mappings = flow.Rows.Select(r => r.MappingReference).Where(m => m is not null).Select(m => m!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var names = flow.Rows.Select(r => r.Interface).Where(i => i.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            found.Add((new TakeoverCandidate(flow.RepoId, flow.FlowName, flow.RelativePath, names, mappings, shares, takes, false, plan), score));
        }

        var ranked = found
            .OrderByDescending(f => f.Score)
            .ThenByDescending(f => f.Candidate.Takes)
            .ThenBy(f => f.Candidate.FlowName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ranked.Count > 0 && ranked[0].Score > 0
            && (ranked.Count == 1 || ranked[1].Score < ranked[0].Score)
            && ranked[0].Candidate.Shares.Contains(kind == LedgerKinds.Delivery ? "mapping" : "folder"))
        {
            ranked[0] = (ranked[0].Candidate with { Suggested = true }, ranked[0].Score);
        }

        return [.. ranked.Select(r => r.Candidate)];
    }

    /// <summary>A declared flow as a takeover sees it: its repository, file and, for a delivery flow, its interfaces' rows.</summary>
    private sealed record DeclaredFlow(Guid RepoId, string FlowName, string RelativePath, IReadOnlyList<DeliveryInterface> Rows, DeliveryLedgerFlow? Document);

    private static IEnumerable<DeclaredFlow> DeclaredFlows(Estate estate, string kind)
        => kind == LedgerKinds.Delivery
            ? estate.Interfaces.Where(i => i.Active)
                .GroupBy(i => (i.RepoId, Flow: i.FlowName.ToUpperInvariant()))
                .Select(g => new DeclaredFlow(g.Key.RepoId, g.First().FlowName, g.First().RelativePath, [.. g], null))
            : estate.Flows.Where(f => f.Active && f.Kind == kind)
                .Select(f => new DeclaredFlow(f.RepoId, f.FlowName, f.RelativePath, [], f));

    private static IReadOnlyList<DeclaredFlow> Declared(Estate estate, string kind, string flowName)
        => [.. DeclaredFlows(estate, kind).Where(f => string.Equals(f.FlowName, flowName, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// The ledger the flow keeps, or would keep, that an orphaned ledger of <paramref name="partition"/> and interface
    /// <paramref name="name"/> goes to: for a delivery flow, its interface of that name described in the partition (or with
    /// no partition of its own), and for the ledger of a flow in the single form (no interface name), its one interface; for
    /// another kind, its ledger in the partition. Null when it keeps none there.
    /// </summary>
    private static LedgerTakeoverTarget? Target(string kind, string partition, string name, IReadOnlyList<DeliveryInterface> rows, DeliveryLedgerFlow? document = null)
    {
        if (kind != LedgerKinds.Delivery)
        {
            if (document is null)
            {
                return null;
            }

            var (id, ledgerName) = OwnLedger(document, partition);
            return new LedgerTakeoverTarget(document.RepoId, document.FlowName, string.Empty, id, ledgerName);
        }

        var placed = rows.Where(r => r.Partition.Length == 0 || string.Equals(r.Partition, partition, StringComparison.OrdinalIgnoreCase)).ToList();
        var row = placed.Find(r => string.Equals(r.Interface, name, StringComparison.OrdinalIgnoreCase))
            ?? (name.Length == 0 && placed.Select(r => r.Interface).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? placed[0] : null);
        return row is null ? null : new LedgerTakeoverTarget(row.RepoId, row.FlowName, row.Interface, row.LedgerFlowId, row.LedgerName);
    }

    private static LedgerTakeoverTarget? Target(string kind, string partition, string name, DeclaredFlow flow)
        => Target(kind, partition, name, flow.Rows, flow.Document);

    private static LedgerTakeoverStep Step(Orphan orphan, IReadOnlyList<DeclaredFlow> declared, IReadOnlyDictionary<Guid, short> registered)
    {
        var ledger = orphan.Ledger;
        var target = declared.Select(f => Target(ledger.Kind, orphan.Partition, ledger.Interface, f)).FirstOrDefault(t => t is not null);
        string? skipped = null;
        if (target is null)
        {
            skipped = ledger.Kind == LedgerKinds.Delivery
                ? $"'{declared[0].FlowName}' declares no interface '{ledger.Interface}' in partition '{orphan.Partition}', so this ledger stays orphaned: give the flow that interface, or take this document over by a flow that has it."
                : $"'{declared[0].FlowName}' keeps no ledger in partition '{orphan.Partition}', so this ledger stays orphaned.";
        }
        else if (registered.TryGetValue(target.LedgerFlowId, out var kept) && kept != ledger.PartitionId)
        {
            skipped = $"'{target.FlowName}' keeps that ledger in another partition than '{orphan.Partition}', and a ledger keeps the rows of one partition, so this ledger stays orphaned.";
            target = null;
        }

        return new LedgerTakeoverStep(ledger.FlowId, ledger.PartitionId, orphan.Partition, ledger.Interface, orphan.Rows, target, skipped);
    }

    private static string Folder(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized[..slash];
    }
}

/// <summary>
/// A missing document, as the Orphaned ledgers page lists it: a flow no repository declares any more, with the ledgers it
/// left behind holding anything, where it was last declared, and the declared flows that can take it over.
/// </summary>
/// <param name="Kind">The kind of flow it was (<see cref="LedgerKinds"/>).</param>
/// <param name="FlowName">The flow's name.</param>
/// <param name="RelativePath">The file it was last declared in, when the read model still holds it.</param>
/// <param name="LeftUtc">When a sync last found it declared, when the read model still holds it.</param>
/// <param name="Rows">What its ledgers hold in all: a delivery flow's records, the rows of another kind's first table (its runs, dimensions or inventories).</param>
/// <param name="Claimed">Of a delivery flow's records, those that claimed an OSDU id; 0 for another kind.</param>
/// <param name="Ledgers">Its orphaned ledgers, one per interface and partition.</param>
/// <param name="Candidates">The declared flows that can take it over, the likeliest first.</param>
public sealed record OrphanedDocument(
    string Kind, string FlowName, string? RelativePath, DateTime? LeftUtc, long Rows, long Claimed,
    IReadOnlyList<OrphanedLedger> Ledgers, IReadOnlyList<TakeoverCandidate> Candidates);

/// <summary>One orphaned ledger of a missing document.</summary>
/// <param name="Ledger">Its identity.</param>
/// <param name="Partition">The partition it is kept in.</param>
/// <param name="Interface">The interface it was kept for, empty for a flow in the single form and for every kind but delivery.</param>
/// <param name="LedgerName">The name it is kept under.</param>
/// <param name="Rows">What it holds.</param>
/// <param name="Claimed">Of a delivery ledger's records, those that claimed an OSDU id.</param>
/// <param name="Mapping">The mapping document its interface last pinned, for a delivery ledger.</param>
/// <param name="RecordObject">The record table its interface last read, for a delivery ledger.</param>
public sealed record OrphanedLedger(Guid Ledger, string Partition, string Interface, string LedgerName, long Rows, long Claimed, string? Mapping, string? RecordObject);

/// <summary>A declared flow that can take over a missing document.</summary>
/// <param name="RepoId">The repository that declares it.</param>
/// <param name="FlowName">The flow.</param>
/// <param name="RelativePath">The file that declares it.</param>
/// <param name="Interfaces">Its interfaces, for a delivery flow that names them.</param>
/// <param name="Mappings">The mapping documents its interfaces pin, for a delivery flow.</param>
/// <param name="Shares">What it shares with the missing document: <c>mapping</c>, <c>record table</c>, <c>interface</c>, <c>folder</c>.</param>
/// <param name="Takes">How many of the missing document's ledgers it would take.</param>
/// <param name="Suggested">True for the one flow that alone ranks first, sharing a mapping document (another kind: its folder).</param>
/// <param name="Plan">Where each of the missing document's ledgers would go, as a takeover by this flow hands it over.</param>
public sealed record TakeoverCandidate(
    Guid RepoId, string FlowName, string RelativePath, IReadOnlyList<string> Interfaces, IReadOnlyList<string> Mappings, IReadOnlyList<string> Shares, int Takes, bool Suggested,
    IReadOnlyList<TakeoverPlanItem> Plan);

/// <summary>Where a takeover by a flow would hand one orphaned ledger.</summary>
/// <param name="Ledger">The orphaned ledger.</param>
/// <param name="ToInterface">The flow's interface it would go to (empty for a flow in the single form and every kind but delivery); null when it would stay orphaned.</param>
/// <param name="ToLedgerName">The name of the ledger it would go to; null when the flow keeps no counterpart of it, and it would stay orphaned.</param>
public sealed record TakeoverPlanItem(Guid Ledger, string? ToInterface, string? ToLedgerName);

/// <summary>A takeover a person asks for: the missing document, the declared flow that takes it over, and optionally one partition.</summary>
/// <param name="Kind">The kind of both flows (<see cref="LedgerKinds"/>).</param>
/// <param name="From">The missing flow.</param>
/// <param name="To">The declared flow that takes over its ledgers.</param>
/// <param name="Partition">The one partition whose ledgers it takes over; every partition when null.</param>
public sealed record LedgerTakeoverRequest(string Kind, string From, string To, string? Partition = null);

/// <summary>The ledger a takeover gives an orphaned ledger to.</summary>
public sealed record LedgerTakeoverTarget(Guid RepoId, string FlowName, string Interface, Guid LedgerFlowId, string LedgerName);

/// <summary>One orphaned ledger of a takeover: the ledger it goes to, or why it stays orphaned.</summary>
/// <param name="Ledger">The orphaned ledger.</param>
/// <param name="PartitionId">Its partition, as the directory numbers it.</param>
/// <param name="Partition">Its partition's data-partition-id.</param>
/// <param name="Interface">The interface it was kept for.</param>
/// <param name="Rows">What it holds.</param>
/// <param name="To">The ledger it goes to, or null when it stays.</param>
/// <param name="Skipped">Why it stays, when it does.</param>
public sealed record LedgerTakeoverStep(Guid Ledger, short PartitionId, string Partition, string Interface, long Rows, LedgerTakeoverTarget? To, string? Skipped);
