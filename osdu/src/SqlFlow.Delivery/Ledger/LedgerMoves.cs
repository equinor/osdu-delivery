using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The move of a delivery ledger to the pipeline that replaced the one that kept it
/// (osdu/docs/reference/concepts/renaming-a-pipeline.md). A delivery flow keeps its ledger under its name, so a renamed
/// pipeline has a new ledger identity while the records its old name delivered, with the OSDU ids they claimed, stay under
/// the old one. The repository sync finds
/// a document under two names and asks for the move (<see cref="RequestAsync"/>): an activity of kind <see cref="Kind"/> on the new
/// identity, <c>running</c> until the move is done. The ledger then carries it out in committed slices
/// (<see cref="ILedger.CompleteLedgerMovesAsync"/>): the control plane right after the sync, and the renamed pipeline's run
/// before it writes anything if it gets there first, so no run reads half a ledger.
/// <para>
/// Every row the old identity keys (<see cref="KeyedTables"/>) takes the new one, and every row that names the old ledger
/// (<see cref="NamingTables"/>) names the new, so the renamed pipeline carries on with the records, their history, their
/// claims and its reading position, and sends nothing again that has not changed. The tables of the other flow kinds'
/// ledgers, the read model of the pipelines and a cache rollout's cursor (<see cref="UntouchedTables"/>) are not a delivery
/// ledger's rows.
/// </para>
/// </summary>
public static class LedgerMoves
{
    /// <summary>The kind of the activity a move is asked and recorded as.</summary>
    public const string Kind = "ledger-move";

    /// <summary>Who asks for a move: the repository sync, as a service.</summary>
    public const string Actor = "service:repository-sync";

    /// <summary>The outcome of a move asked for and not done yet, as every activity under way reads.</summary>
    public const string Running = "running";

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
    /// The tables that carry a ledger identity a move leaves alone: the ledgers of the other flow kinds (retrieval,
    /// assertion, dimension, inventory), the read model of each pipeline's interfaces, which keeps the old pipeline's rows
    /// as they were, a cache rollout's cursor, which walks records in delivery-key order and so passes a moved record at the
    /// same place, and the directory, which a move rewrites itself.
    /// </summary>
    public static IReadOnlyList<string> UntouchedTables { get; } =
    [
        "Retrieval", "AssertionRun", "AssertionResult", "Dimension", "DimensionRun", "Inventory", "Interface", "UpdateTag", "Ledger",
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Asks for the move of <paramref name="request"/>'s ledger on <paramref name="context"/>, in whatever transaction it was
    /// opened with (the sync's): the new identity is registered in the old ledger's partition when no run has registered it
    /// yet, and a <c>running</c> activity of kind <see cref="Kind"/> on it names both ledgers. Asking again for a move already
    /// asked changes nothing. A move asked while another one moves either ledger away, or into the ledger this one moves
    /// away, waits: one ledger's moves are made one after another, so a copy made and deleted between two syncs never sends
    /// rows back while they are still on their way.
    /// </summary>
    /// <returns>
    /// <see cref="LedgerMoveAsk.Asked"/> when it is asked, or was; <see cref="LedgerMoveAsk.Waits"/> when the next sync asks;
    /// <see cref="LedgerMoveAsk.OtherPartition"/> when the new identity is registered in another partition, where the old
    /// ledger's records cannot go, since a ledger keeps the records of one partition.
    /// </returns>
    public static async Task<LedgerMoveAsk> RequestAsync(OsduDbContext context, LedgerMoveRequest request, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var registered = await context.DeliveryLedgers.FirstOrDefaultAsync(l => l.FlowId == request.To, ct).ConfigureAwait(false);
        if (registered is not null && registered.PartitionId != request.PartitionId)
        {
            return LedgerMoveAsk.OtherPartition;
        }

        var underWay = (await context.DeliveryActivities.AsNoTracking()
                .Where(a => a.PartitionId == request.PartitionId && a.Kind == Kind && a.Outcome == Running)
                .Select(a => a.ParametersJson)
                .ToListAsync(ct).ConfigureAwait(false))
            .Select(Read)
            .OfType<LedgerMoveParameters>()
            .ToList();
        if (underWay.Exists(m => m.From == request.From && m.To == request.To))
        {
            return LedgerMoveAsk.Asked;
        }

        if (underWay.Exists(m => m.From == request.From || m.From == request.To || m.To == request.From))
        {
            return LedgerMoveAsk.Waits;
        }

        if (registered is null)
        {
            context.DeliveryLedgers.Add(new DeliveryLedger
            {
                PartitionId = request.PartitionId,
                FlowId = request.To,
                Kind = LedgerKinds.Delivery,
                FlowName = request.ToFlow,
                Interface = request.ToInterface,
                LedgerName = request.ToLedger,
                RegisteredUtc = nowUtc,
            });
        }

        context.DeliveryActivities.Add(new DeliveryActivity
        {
            PartitionId = request.PartitionId,
            FlowId = request.To,
            FlowName = request.ToFlow,
            Kind = Kind,
            Actor = Actor,
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
            }, Json),
            Summary = $"moving the ledger of '{request.FromFlow}' to '{request.ToFlow}', the same document under its newest name",
        });
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        return LedgerMoveAsk.Asked;
    }

    // Of the ledgers named, those placed in a partition whose records claimed an OSDU id, with their partition. Read with
    // locking read committed whatever the caller's isolation: the repository sync's transaction is serializable, and a scan
    // of a ledger of held records under it would keep a range lock on each of them, past the 5,000 at which SQL Server locks
    // the whole table instead.
    private const string ClaimingSql = """
        SELECT l.[FlowId], l.[PartitionId]
        FROM [osdu].[Ledger] AS l WITH (READCOMMITTEDLOCK)
        WHERE l.[FlowId] IN (SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@ids))
          AND l.[PartitionId] <> 0
          AND EXISTS (
              SELECT 1 FROM [osdu].[Record] AS r WITH (READCOMMITTEDLOCK)
              WHERE r.[PartitionId] = l.[PartitionId] AND r.[FlowId] = l.[FlowId] AND r.[ClaimedTargetId] IS NOT NULL);
        """;

    /// <summary>
    /// Of <paramref name="ledgers"/>, the ones placed in a partition whose records claimed an OSDU id, each with its partition
    /// number: the ledgers a pipeline that left can leave behind. Read on <paramref name="context"/>'s connection, in its
    /// transaction, without keeping a lock on what it read.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, short>> ClaimingAsync(OsduDbContext context, IReadOnlyCollection<Guid> ledgers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ledgers);
        var claiming = new Dictionary<Guid, short>();
        if (ledgers.Count == 0)
        {
            return claiming;
        }

        await context.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = ClaimingSql;
            var ids = command.CreateParameter();
            ids.ParameterName = "@ids";
            ids.DbType = DbType.String;
            ids.Value = JsonSerializer.Serialize(ledgers);
            command.Parameters.Add(ids);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                claiming[reader.GetGuid(0)] = reader.GetInt16(1);
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        return claiming;
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

    /// <summary>Another move of either ledger is under way; the next sync asks.</summary>
    Waits,

    /// <summary>The two ledgers are kept in different partitions, so the move is not asked.</summary>
    OtherPartition,
}

/// <summary>A move the repository sync asks for: the ledger of one name of a document, and the identity of the name that owns it.</summary>
/// <param name="PartitionId">The partition both ledgers are kept in, as the directory numbers it.</param>
/// <param name="From">The old ledger's identity.</param>
/// <param name="FromFlow">The pipeline that kept it, which the repository no longer declares.</param>
/// <param name="FromLedger">The old ledger's name.</param>
/// <param name="To">The identity of the pipeline that replaced it.</param>
/// <param name="ToFlow">That pipeline.</param>
/// <param name="ToInterface">Its interface, empty for a flow in the single form.</param>
/// <param name="ToLedger">The name its ledger is kept under.</param>
/// <param name="RepoId">The repository that declares both.</param>
public sealed record LedgerMoveRequest(
    short PartitionId, Guid From, string FromFlow, string FromLedger, Guid To, string ToFlow, string ToInterface, string ToLedger, Guid RepoId);

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
