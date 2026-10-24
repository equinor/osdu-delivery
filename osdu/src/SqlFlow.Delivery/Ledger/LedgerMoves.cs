using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The move of a ledger to another flow's identity (osdu/docs/reference/concepts/renaming-a-pipeline.md). A flow keeps its
/// ledger under its name, so a renamed flow has a new ledger identity while the history of its old name (for a delivery
/// flow, the records it delivered with the OSDU ids they claimed) stays under the old one. A move is asked in two ways
/// (<see cref="RequestAsync"/>): by the repository sync, when it finds one document under two names, and by a person who
/// binds a ledger no declared flow keeps to the flow of their choice (<see cref="LedgerBindings"/>). Either way it is an
/// activity of kind <see cref="Kind"/> on the new identity, <c>running</c> until the move is done, and the ledger carries it
/// out in committed slices (<see cref="ILedger.CompleteLedgerMovesAsync"/>): the repository sync right after it commits, the
/// bind as it is asked, the control plane's pass for a move that had to wait, and a run of the flow before it reads its
/// ledger if it gets there first, so no run reads half a ledger.
/// <para>
/// Every row the old identity keys (<see cref="KeyedTables"/>) takes the new one, and every row that names the old ledger
/// (<see cref="NamingTables"/>) names the new, so the flow carries on with the records, their history, their claims and its
/// reading position, and sends nothing again that has not changed. A ledger of another kind moves the rows of its own tables
/// (<see cref="OtherKinds"/>). The read model of the pipelines, a cache rollout's cursor and the directory
/// (<see cref="UntouchedTables"/>) are no ledger's rows.
/// </para>
/// </summary>
public static class LedgerMoves
{
    /// <summary>The kind of the activity a move is asked and recorded as.</summary>
    public const string Kind = "ledger-move";

    /// <summary>Who asks for the move of a renamed flow's ledger: the repository sync, as a service.</summary>
    public const string Actor = "service:repository-sync";

    /// <summary>The outcome of a move asked for and not done yet, as every activity under way reads.</summary>
    public const string Running = "running";

    /// <summary>The outcome of a move refused before it began.</summary>
    public const string Failed = "failed";

    /// <summary>The outcome of a move made: every row of the old ledger is the new one's.</summary>
    public const string Completed = "completed";

    /// <summary>
    /// The tables a delivery ledger keeps its rows in under its identity (<c>FlowId</c>), each with an index led by the
    /// partition and the identity, so a ledger's rows are one range of it: a move takes every row of them.
    /// </summary>
    public static IReadOnlyList<string> KeyedTables { get; } =
    [
        "Record", "RecordIdentity", "Attempt", "Submission", "WorkBatch", "Lease", "RecordEvent", "SourceWatermark",
        "Activity", "ActivityRecord", "PurgedRecord", "Artifact", "Reversal", "RemovedKey",
    ];

    /// <summary>The tables whose rows name a delivery ledger (<c>LedgerFlowId</c>) under a key of their own: a move points them at the new identity.</summary>
    public static IReadOnlyList<string> NamingTables { get; } = ["RemovalRun", "InventoryRecord", "InventoryRemovalItem"];

    /// <summary>
    /// What a move takes of a ledger of each other kind of flow (retrieval, assertion, dimension, inventory): the tables its
    /// rows are kept in under its identity, the first of them the one that says whether it holds anything.
    /// </summary>
    public static IReadOnlyDictionary<string, LedgerKindTables> OtherKinds { get; } = new Dictionary<string, LedgerKindTables>(StringComparer.Ordinal)
    {
        [LedgerKinds.Retrieval] = new(LedgerKinds.Retrieval, ["Retrieval", "Activity"], NamedRows: false, "retrieval run(s)"),
        [LedgerKinds.Assertion] = new(LedgerKinds.Assertion, ["AssertionRun", "AssertionResult", "Activity"], NamedRows: false, "assertion run(s)"),
        [LedgerKinds.Dimension] = new(LedgerKinds.Dimension, ["Dimension", "DimensionRun", "Activity"], NamedRows: true, "dimension(s)"),
        [LedgerKinds.Inventory] = new(LedgerKinds.Inventory, ["Inventory", "Activity"], NamedRows: true, "inventory(ies)"),
    };

    /// <summary>
    /// The table whose rows name a dimension flow's ledger (<c>LedgerId</c>) and the flow by its name (<c>FlowName</c>): a
    /// view a build wrote, which the build that made it owns. A move of a dimension ledger points both at the new name.
    /// </summary>
    public const string ViewTable = "DimensionView";

    /// <summary>
    /// The tables that carry a ledger identity a move leaves alone: the read model of each delivery pipeline's interfaces,
    /// which keeps the old pipeline's rows as they were, a cache rollout's cursor, which walks records in delivery-key order
    /// and so passes a moved record at the same place, and the directory, which a move writes itself.
    /// </summary>
    public static IReadOnlyList<string> UntouchedTables { get; } = ["Interface", "UpdateTag", "Ledger"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Asks for the move of <paramref name="request"/>'s ledger on <paramref name="context"/>, in whatever transaction it was
    /// opened with (the sync's, or a bind's): the new identity is registered in the old ledger's partition when no run has
    /// registered it yet, and a <c>running</c> activity of kind <see cref="Kind"/> on it names both ledgers. Asking again for
    /// a move already asked changes nothing. A move asked while another one moves either ledger away, or into the ledger this
    /// one moves away, waits: one ledger's moves are made one after another, so a copy made and deleted between two syncs
    /// never sends rows back while they are still on their way.
    /// </summary>
    /// <returns>
    /// What came of it, with the move's activity when it is asked: <see cref="LedgerMoveAsk.Asked"/> when it is asked, or
    /// was; <see cref="LedgerMoveAsk.Waits"/> when another move of either ledger is under way; <see cref="LedgerMoveAsk.Refused"/>
    /// when the sync asks a move that was refused before, which the audit trail says why of, and which no sync asks again (a
    /// bind is asked again); <see cref="LedgerMoveAsk.OtherPartition"/> when the new identity is registered in another
    /// partition, where the old ledger's rows cannot go, since a ledger keeps the rows of one partition.
    /// </returns>
    public static async Task<LedgerMoveAsked> RequestAsync(OsduDbContext context, LedgerMoveRequest request, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        await LockAsksAsync(context, ct).ConfigureAwait(false);
        var registered = await context.DeliveryLedgers.FirstOrDefaultAsync(l => l.FlowId == request.To, ct).ConfigureAwait(false);
        if (registered is not null && registered.PartitionId != request.PartitionId)
        {
            return new(LedgerMoveAsk.OtherPartition, 0);
        }

        var asked = (await context.DeliveryActivities.AsNoTracking()
                .Where(a => a.PartitionId == request.PartitionId && a.Kind == Kind && (a.Outcome == Running || a.Outcome == Failed))
                .Select(a => new { a.ActivityId, a.Outcome, a.ParametersJson })
                .ToListAsync(ct).ConfigureAwait(false))
            .Select(a => (a.ActivityId, a.Outcome, Move: Read(a.ParametersJson)))
            .Where(a => a.Move is not null)
            .ToList();
        if (!request.Bound && asked.Exists(a => a.Outcome == Failed && a.Move!.From == request.From && a.Move.To == request.To))
        {
            return new(LedgerMoveAsk.Refused, 0);
        }

        var underWay = asked.Where(a => a.Outcome == Running).ToList();
        if (underWay.Find(a => a.Move!.From == request.From && a.Move.To == request.To) is { ActivityId: > 0 } same)
        {
            return new(LedgerMoveAsk.Asked, same.ActivityId);
        }

        if (underWay.Exists(a => a.Move!.From == request.From || a.Move.From == request.To || a.Move.To == request.From))
        {
            return new(LedgerMoveAsk.Waits, 0);
        }

        if (registered is null)
        {
            context.DeliveryLedgers.Add(new DeliveryLedger
            {
                PartitionId = request.PartitionId,
                FlowId = request.To,
                Kind = request.Kind,
                FlowName = request.ToFlow,
                Interface = request.ToInterface,
                LedgerName = request.ToLedger,
                RegisteredUtc = nowUtc,
            });
        }

        var activity = new DeliveryActivity
        {
            PartitionId = request.PartitionId,
            FlowId = request.To,
            FlowName = request.ToFlow,
            Kind = Kind,
            Actor = request.Actor,
            StartedUtc = nowUtc,
            Outcome = Running,
            ParametersJson = JsonSerializer.Serialize(new LedgerMoveParameters
            {
                From = request.From,
                FromFlow = request.FromFlow,
                FromLedger = request.FromLedger,
                To = request.To,
                ToFlow = request.ToFlow,
                ToLedger = request.ToLedger,
                Repository = request.RepoId,
                LedgerKind = request.Kind,
                Bound = request.Bound,
            }, Json),
            Summary = request.Bound
                ? $"binding the ledger of '{request.FromFlow}', which no repository declares, to '{request.ToFlow}'"
                : $"moving the ledger of '{request.FromFlow}' to '{request.ToFlow}', the same document under its newest name",
        };
        context.DeliveryActivities.Add(activity);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        return new(LedgerMoveAsk.Asked, activity.ActivityId);
    }

    // Every ask in a transaction holds this lock until the transaction ends, so two asks (a sync's and a bind's, or two
    // binds) never both find a ledger free and both ask to move it: the second reads what the first asked.
    private const string AskLockSql = """
        SET NOCOUNT ON;
        DECLARE @granted int;
        EXEC @granted = sys.sp_getapplock @Resource = N'SqlFlow.Delivery.LedgerMoveAsk', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = @timeout;
        SELECT CAST(@granted AS int);
        """;

    /// <summary>How long an ask waits for another one to commit: a repository sync's transaction holds the lock to its end.</summary>
    private const int AskLockMilliseconds = 120_000;

    /// <summary>
    /// Takes the lock every ask for a move holds to the end of <paramref name="context"/>'s transaction, before it reads what
    /// is asked already. Outside a transaction there is nothing to hold it for, and nothing is taken.
    /// </summary>
    /// <exception cref="DeliveryException">Another ask kept the lock past the wait.</exception>
    public static async Task LockAsksAsync(OsduDbContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Database.CurrentTransaction is not { } transaction)
        {
            return;
        }

        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = AskLockSql;
        command.CommandTimeout = (AskLockMilliseconds / 1000) + 30;
        var timeout = command.CreateParameter();
        timeout.ParameterName = "@timeout";
        timeout.DbType = DbType.Int32;
        timeout.Value = AskLockMilliseconds;
        command.Parameters.Add(timeout);
        var granted = Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (granted < 0)
        {
            throw new DeliveryException(string.Create(
                CultureInfo.InvariantCulture,
                $"Another ledger move was being asked for (a repository sync, or a bind) and still was after {AskLockMilliseconds / 1000} seconds (sp_getapplock answered {granted}), so nothing was asked: try again once it is done."));
        }
    }

    // Of the ledgers named, those placed in a partition that hold anything, with their partition: for a delivery ledger, a
    // record; for another kind, a row of the table that says what it holds. Read with locking read
    // committed whatever the caller's isolation: the repository sync's transaction is serializable, and a scan of a ledger of
    // held records under it would keep a range lock on each of them, past the 5,000 at which SQL Server locks the whole
    // table instead.
    private const string HoldingSql = """
        SELECT l.[FlowId], l.[PartitionId]
        FROM [osdu].[Ledger] AS l WITH (READCOMMITTEDLOCK)
        WHERE l.[FlowId] IN (SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@ids))
          AND l.[PartitionId] <> 0
          AND EXISTS (
              SELECT 1 FROM [osdu].[{0}] AS r WITH (READCOMMITTEDLOCK)
              WHERE r.[PartitionId] = l.[PartitionId] AND r.[FlowId] = l.[FlowId]);
        """;

    private static readonly CompositeFormat Holding = CompositeFormat.Parse(HoldingSql);

    /// <summary>
    /// Of <paramref name="ledgers"/>, ledgers of <paramref name="kind"/>, the ones placed in a partition that hold anything,
    /// each with its partition number: the ledgers a name that left can leave behind. Read on <paramref name="context"/>'s
    /// connection, in its transaction, without keeping a lock on what it read.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, short>> HoldingAsync(OsduDbContext context, string kind, IReadOnlyCollection<Guid> ledgers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ledgers);
        var holding = new Dictionary<Guid, short>();
        var table = kind == LedgerKinds.Delivery
            ? "Record"
            : OtherKinds.TryGetValue(kind, out var tables) ? tables.Tables[0] : null;
        if (ledgers.Count == 0 || table is null)
        {
            return holding;
        }

        await context.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = string.Format(CultureInfo.InvariantCulture, Holding, table);
            var ids = command.CreateParameter();
            ids.ParameterName = "@ids";
            ids.DbType = DbType.String;
            ids.Value = JsonSerializer.Serialize(ledgers);
            command.Parameters.Add(ids);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                holding[reader.GetGuid(0)] = reader.GetInt16(1);
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        return holding;
    }

    /// <summary>The parameters a move's activity carries, or null for text that is not one.</summary>
    internal static LedgerMoveParameters? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LedgerMoveParameters>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>What asking for a move came to.</summary>
public enum LedgerMoveAsk
{
    /// <summary>The move is asked, now or before.</summary>
    Asked,

    /// <summary>Another move of either ledger is under way; the next sync asks, and a bind is refused for now.</summary>
    Waits,

    /// <summary>The same move was refused before; no sync asks it again.</summary>
    Refused,

    /// <summary>The two ledgers are kept in different partitions, so the move is not asked.</summary>
    OtherPartition,
}

/// <summary>What asking for a move came to, and the move's activity when it is asked (0 otherwise).</summary>
public readonly record struct LedgerMoveAsked(LedgerMoveAsk Ask, long ActivityId);

/// <summary>
/// A move asked for: the ledger of one name of a document and the identity of the name that owns it, as the repository sync
/// asks it, or a ledger no declared flow keeps and the flow a person binds it to.
/// </summary>
/// <param name="PartitionId">The partition both ledgers are kept in, as the directory numbers it.</param>
/// <param name="From">The old ledger's identity.</param>
/// <param name="FromFlow">The flow that kept it, which no repository declares any more, or no longer as the document's owner.</param>
/// <param name="FromLedger">The old ledger's name.</param>
/// <param name="To">The identity of the ledger the flow it moves to keeps.</param>
/// <param name="ToFlow">That flow.</param>
/// <param name="ToInterface">Its interface, empty for a flow in the single form and for every kind but delivery.</param>
/// <param name="ToLedger">The name its ledger is kept under.</param>
/// <param name="RepoId">The repository that declares the flow it moves to.</param>
/// <param name="Kind">The kind of both ledgers (<see cref="LedgerKinds"/>).</param>
/// <param name="Actor">Who asks: the repository sync (<see cref="LedgerMoves.Actor"/>), or the person who binds the ledger.</param>
public sealed record LedgerMoveRequest(
    short PartitionId, Guid From, string FromFlow, string FromLedger, Guid To, string ToFlow, string ToInterface, string ToLedger, Guid RepoId,
    string Kind = LedgerKinds.Delivery, string Actor = LedgerMoves.Actor)
{
    /// <summary>True for a ledger a person binds, false for the move of a renamed flow's ledger the repository sync asks.</summary>
    public bool Bound => !string.Equals(Actor, LedgerMoves.Actor, StringComparison.Ordinal);
}

/// <summary>What a move takes of a ledger of a kind other than delivery.</summary>
/// <param name="Kind">The kind (<see cref="LedgerKinds"/>).</param>
/// <param name="Tables">The tables its rows are kept in under its identity (<c>FlowId</c>), the one that says whether it holds anything first.</param>
/// <param name="NamedRows">
/// True when the first table holds one row of each name per ledger (a flow's dimensions, its inventories): a move then refuses
/// a ledger holding a name the old one holds too, and moves those rows first, in one transaction.
/// </param>
/// <param name="Noun">What the rows of the first table are, for the move's summary.</param>
public sealed record LedgerKindTables(string Kind, IReadOnlyList<string> Tables, bool NamedRows, string Noun);

/// <summary>What a move's activity records of it, as JSON in <c>ParametersJson</c>.</summary>
public sealed record LedgerMoveParameters
{
    public Guid From { get; init; }

    public string FromFlow { get; init; } = string.Empty;

    public string FromLedger { get; init; } = string.Empty;

    public Guid To { get; init; }

    public string ToFlow { get; init; } = string.Empty;

    public string ToLedger { get; init; } = string.Empty;

    public Guid Repository { get; init; }

    /// <summary>True once the move has begun writing: what it checks before it begins holds only until then.</summary>
    public bool Started { get; init; }

    /// <summary>The new identity's records that gave way to the old ledger's so far.</summary>
    public long Superseded { get; init; }

    /// <summary>The kind of both ledgers; a move asked before kinds were recorded is a delivery ledger's.</summary>
    public string LedgerKind { get; init; } = LedgerKinds.Delivery;

    /// <summary>For a ledger of another kind, the rows of its first table the move found when it began.</summary>
    public long Moved { get; init; }

    /// <summary>True for a ledger a person bound to the flow, false for a move the repository sync asked.</summary>
    public bool Bound { get; init; }
}

/// <summary>What one pass at a move did.</summary>
/// <param name="ActivityId">The move's activity.</param>
/// <param name="Partition">The partition both ledgers are kept in.</param>
/// <param name="From">The old ledger's identity.</param>
/// <param name="To">The new one.</param>
/// <param name="Outcome"><c>completed</c>, <c>failed</c>, or <c>running</c> for a move that waits for work in flight on either ledger.</param>
/// <param name="Records">The records moved, with everything they carry.</param>
/// <param name="Superseded">The new identity's records the old ledger held too, which had claimed no OSDU id and gave way to the old ones.</param>
/// <param name="Summary">What the activity says of it.</param>
public sealed record LedgerMoveResult(long ActivityId, string Partition, Guid From, Guid To, string Outcome, long Records, long Superseded, string Summary);
