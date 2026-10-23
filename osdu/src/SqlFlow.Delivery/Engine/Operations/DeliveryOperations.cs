using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Execution;

namespace SqlFlow.Delivery.Engine.Operations;

/// <summary>
/// The ad-hoc operations of a flow: what the GUI's buttons do when they need OSDU or the ingestion tables rather than the
/// ledger. A person's reads run in the control plane as they are asked (<see cref="DirectOperations"/>); what scans or
/// writes (a value check, a removal) is queued for a node. Either way the payload is the same: it names the flow
/// (<c>sourceRef</c>) whose target and credentials it uses and carries the flow file's location (<c>repoRoot</c> +
/// <c>relativePath</c> as the catalog knows them, or an absolute <c>flowFile</c>), and the process running it resolves every
/// credential itself, exactly as it does for a run. The document must declare the named flow, so a stale catalog can never
/// aim an operation at the wrong target. A flow that declares interfaces is acted on through the one the payload names
/// (<c>interface</c>); the others are never touched. A flow that names its partitions is acted on in the one the payload
/// names (<c>partition</c>), resolving its references with the central configuration the control plane supplied for it
/// (<c>references</c>).
/// </summary>
public abstract class DeliveryOperation : IComputeOperation
{
    /// <summary>The task argument naming the partition a flow that names its partitions is acted on in.</summary>
    public const string PartitionArgument = "partition";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly EngineContext _context;

    protected DeliveryOperation(EngineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public abstract string Name { get; }

    public virtual async Task<string> ExecuteAsync(ComputeTaskPayload payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var flowFile = ResolveFlowFile(payload);
        var source = _context.Documents.LoadSource(flowFile);
        if (!string.Equals(source.Name, payload.SourceRef, StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlFlowException($"The flow file declares '{source.Name}', not '{payload.SourceRef}'. The file and the catalog have drifted; re-sync the repository.");
        }

        // The partition the task acts in, settled as a run's is: hard-coded in the flow, or the one named from the registry.
        var requested = payload.Argument(PartitionArgument);
        var registry = source.NeedsRegistry(requested)
            ? await _context.PartitionRegistry.ReadAsync(ct).ConfigureAwait(false)
            : RegisteredPartitions.None;
        var bound = source.Resolve(requested, registry);
        var flow = bound.Interface(payload.Argument("interface"));
        var result = await RunAsync(ContextFor(payload), flow, payload, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    /// <summary>
    /// The process's services as <paramref name="payload"/>'s operation resolves its references with them, as a run of the
    /// same flow and partition does: from the configuration the control plane supplied with the task for that partition (the
    /// partition's own values over the ones set for none), and only then from the process's environment.
    /// </summary>
    protected EngineContext ContextFor(ComputeTaskPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return _context.WithSuppliedReferences(payload.References ?? System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty);
    }

    /// <summary>
    /// Runs the operation on <paramref name="flow"/>, the interface the payload names bound to the partition it names, with
    /// <paramref name="context"/>: the process's services resolving references with the configuration supplied for it.
    /// </summary>
    protected abstract Task<object> RunAsync(EngineContext context, FlowDefinition flow, ComputeTaskPayload payload, CancellationToken ct);

    /// <summary>
    /// A connection to the flow's target for one operation: the one the engine keeps for that target between operations
    /// (<see cref="TargetClients"/>), so a token and its connections are not made again for every read, or one of its own
    /// where the engine keeps none. The caller disposes the lease; a kept connection stays.
    /// </summary>
    protected static async Task<TargetLease> OpenTargetAsync(EngineContext context, FlowDefinition flow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(flow);
        return context.Clients is { } clients
            ? await clients.OpenAsync(context, flow, ct).ConfigureAwait(false)
            : TargetClients.OneOff(context, flow, null, EngineContext.LoopbackAllowed);
    }

    protected ILedger RequireLedger()
        => _context.Ledger ?? throw new SqlFlowException($"The '{Name}' operation needs the ledger, which lives in the module's database this node was started without (Osdu:Database:Connection or SQLFLOW_OSDU_DB).");

    protected static string Actor(ComputeTaskPayload payload)
        => payload.Argument("actor") ?? "unknown";

    /// <summary>
    /// The flow parameter values the task carries in <c>values</c> (a JSON object of strings, and of arrays of strings for a
    /// parameter given several), which fill the record scope's predicate when the operation reads the ingestion tables; none
    /// when it carries none.
    /// </summary>
    protected static IReadOnlyDictionary<string, string> Values(ComputeTaskPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        try
        {
            return RunParameters.ValuesFromJson(payload.Argument("values"));
        }
        catch (SqlFlowException ex)
        {
            throw new SqlFlowException($"The task's 'values' argument is not valid: {ex.Message}", ex);
        }
    }

    private static string ResolveFlowFile(ComputeTaskPayload payload)
    {
        var file = payload.Argument("flowFile");
        if (file is null)
        {
            var root = payload.RequireArgument("repoRoot");
            var relative = payload.RequireArgument("relativePath");
            file = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        file = Path.GetFullPath(file);
        if (!File.Exists(file))
        {
            throw new SqlFlowException($"The flow file for '{payload.SourceRef}' was not found where its repository was synced: {file}. Re-sync the repository.");
        }

        return file;
    }
}

/// <summary>
/// <c>delivery-probe</c>: is the target reachable with the flow's credentials? Calls the service's info endpoint
/// and reports the answer; a refusal is a result, not a failure.
/// </summary>
public sealed class ProbeTargetOperation : DeliveryOperation
{
    public const string OperationName = "delivery-probe";

    public ProbeTargetOperation(EngineContext context)
        : base(context)
    {
    }

    public override string Name => OperationName;

    protected override async Task<object> RunAsync(EngineContext context, FlowDefinition flow, ComputeTaskPayload payload, CancellationToken ct)
    {
        using var target = await OpenTargetAsync(context, flow, ct).ConfigureAwait(false);
        var protocol = await target.ProtocolAsync(ct).ConfigureAwait(false);
        var probe = await protocol.ProbeAsync(ct).ConfigureAwait(false);
        return new
        {
            flow = flow.Label,
            protocol = protocol.Kind.ToString(),
            endpoint = flow.Target.Endpoint,
            auth = flow.Target.Auth.Type.ToString(),
            probe.Reachable,
            probe.Status,
            probe.Detail,
            probe.Path,
            checkedUtc = context.Time.GetUtcNow().UtcDateTime,
        };
    }
}

/// <summary>
/// <c>delivery-read</c>: the record as OSDU holds it right now, for a record's detail page, with the versions the
/// target keeps of it. Takes the ledger's <c>deliveryKey</c> (resolved to the target id) or a <c>targetId</c> directly,
/// and a <c>version</c> to read the record as it was at that version instead of its latest.
/// </summary>
public sealed class ReadRecordOperation : DeliveryOperation
{
    public const string OperationName = "delivery-read";

    public ReadRecordOperation(EngineContext context)
        : base(context)
    {
    }

    public override string Name => OperationName;

    protected override async Task<object> RunAsync(EngineContext context, FlowDefinition flow, ComputeTaskPayload payload, CancellationToken ct)
    {
        var targetId = payload.Argument("targetId");
        var version = VersionOf(payload);
        Guid? deliveryKey = null;
        IReadOnlyDictionary<string, string>? targetState = null;
        if (targetId is null)
        {
            var key = DeliveryKey.Parse(payload.RequireArgument("deliveryKey"));
            deliveryKey = key.Value;
            var record = await RequireLedger().GetRecordAsync(flow.Id, key, ct).ConfigureAwait(false)
                ?? throw new SqlFlowException($"Record {key} is not in the ledger for flow '{flow.Label}'.");
            // What a record's page reads back is what this flow wrote: an id the record never claimed can be another flow's.
            targetId = record.ClaimedTargetId
                ?? throw new SqlFlowException($"Record {key} has not queued a document for OSDU in flow '{flow.Label}', so this flow wrote nothing to read back.");
            targetState = JsonMerge.ToValues(record.TargetStateJson);
        }
        else if (context.Ledger is { } ledger)
        {
            // A target that keeps a record under a key it gave (a DSPDM row) is read by what the record's deliveries recorded.
            var matches = await ledger.ListAsync(flow.Id, new RecordQuery { Search = targetId, Max = 2 }, ct).ConfigureAwait(false);
            if (matches.FirstOrDefault(r => string.Equals(r.TargetId, targetId, StringComparison.Ordinal)) is { } record)
            {
                targetState = JsonMerge.ToValues(record.TargetStateJson);
            }
        }

        using var target = await OpenTargetAsync(context, flow, ct).ConfigureAwait(false);
        var protocol = await target.ProtocolAsync(ct).ConfigureAwait(false);
        return await ReadBackAsync(protocol, flow.Label, targetId, targetState, version, deliveryKey, context.Time, ct).ConfigureAwait(false);
    }

    /// <summary>The version a read task names (<c>version</c>), or null to read the record at its latest.</summary>
    /// <exception cref="SqlFlowException">The argument is not a positive whole number.</exception>
    internal static long? VersionOf(ComputeTaskPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Argument("version") is not { } versionText)
        {
            return null;
        }

        return long.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new SqlFlowException($"'{versionText}' is not a record version: a positive whole number.");
    }

    /// <summary>
    /// One record as <paramref name="protocol"/>'s target holds it, at its latest or at <paramref name="version"/>, with the
    /// versions the target keeps of it, under a correlation id of its own: what every page that reads a record from OSDU
    /// shows, the record page's read-back and the explorer's read alike. <paramref name="through"/> names the connection the
    /// read went through.
    /// </summary>
    internal static async Task<object> ReadBackAsync(
        IDeliveryProtocol protocol, string through, string targetId, IReadOnlyDictionary<string, string>? targetState, long? version, Guid? deliveryKey,
        TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(time);
        using var correlation = Http.OsduCorrelation.Begin();
        JsonObject? document = version is null
            ? await protocol.ReadAsync(targetId, targetState, ct).ConfigureAwait(false)
            : await protocol.ReadVersionAsync(targetId, version.Value, ct).ConfigureAwait(false);

        // The version list is the record's history, read beside the record. A target that refuses the list still
        // answered with the record, so the refusal is reported with it rather than failing the read.
        IReadOnlyList<long>? versions = null;
        string? historyError = null;
        try
        {
            versions = await protocol.VersionsAsync(targetId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeliveryException or HttpRequestException && !ct.IsCancellationRequested)
        {
            historyError = Http.HeaderRedaction.RedactMessage(ex.Message);
        }

        return new
        {
            flow = through,
            deliveryKey,
            targetId,
            correlationId = correlation.Id,
            found = document is not null,
            version = document is null ? null : RecordWriter.ParseVersion(Json.JsonPathReader.SelectValue(JsonSerializer.SerializeToElement(document), "version")),
            readVersion = version,
            versions,
            historyError,
            record = document,
            readUtc = time.GetUtcNow().UtcDateTime,
        };
    }
}

/// <summary>
/// <c>delivery-delete</c>: removes records from OSDU through the flow's protocol and records what happened to each
/// one in the ledger under the requesting <c>actor</c>. <c>scope</c> says how much goes (<c>record</c> reversibly,
/// <c>previous</c> for the latest version, the one before it written back as current, <c>history</c> for the earlier
/// versions only, <c>everything</c> for the record and all its versions), <c>purgeLedger</c> (<c>true</c>, with record or
/// everything) also deletes each record removed from the ledger, keeping one line of it, and the records are named either by
/// <c>deliveryKeys</c> (a comma-separated list, one key for the single-record case) or by <c>filter</c> (the listing
/// whose every match is to be removed), resolved here against the ledger.
/// </summary>
public sealed class DeleteRecordOperation : DeliveryOperation
{
    public const string OperationName = "delivery-delete";

    public DeleteRecordOperation(EngineContext context)
        : base(context)
    {
    }

    public override string Name => OperationName;

    protected override async Task<object> RunAsync(EngineContext context, FlowDefinition flow, ComputeTaskPayload payload, CancellationToken ct)
    {
        var scope = RemovalScopes.Parse(payload.Argument("scope"));
        var purgeLedger = RemovalScopes.ParsePurgeLedger(payload.Argument("purgeLedger"));
        var selection = ReadSelection(payload);
        RequireLedger();
        using var runtime = FlowRuntime.ForTarget(context, flow);
        runtime.Actor = Actor(payload);
        var summary = await runtime.RemoveAsync(selection, scope, purgeLedger, ct).ConfigureAwait(false);
        return new
        {
            flow = flow.Label,
            scope = RemovalScopes.Wire(scope),
            summary.Selected,
            summary.Removed,
            summary.Restored,
            summary.AlreadyGone,
            summary.Purged,
            summary.Skipped,
            summary.Failed,
            summary.Truncated,
            // What the records' unfinished deliveries left, undone before anything was removed.
            undone = summary.Undone,

            // What the pipeline's after step did with what the removal changed in OSDU; absent when it names none.
            cacheAfter = summary.CacheAfter,
            records = summary.Records,
            summary = summary.Describe(),
            actor = runtime.Actor,
            removedUtc = context.Time.GetUtcNow().UtcDateTime,
        };
    }

    /// <summary>The keys the caller listed, or the listing filter it asked to have emptied. Exactly one of the two.</summary>
    private static RemovalSelection ReadSelection(ComputeTaskPayload payload)
    {
        var listed = payload.Argument("deliveryKeys");
        var filter = payload.Argument("filter");
        if (!string.IsNullOrWhiteSpace(listed))
        {
            var keys = listed
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(DeliveryKey.Parse)
                .ToList();
            return RemovalSelection.Of(keys);
        }

        if (string.IsNullOrWhiteSpace(filter))
        {
            throw new SqlFlowException("A removal needs either 'deliveryKeys' or 'filter'; the task carries neither.");
        }

        return RemovalSelection.Of(RemovalFilter.FromJson(filter));
    }
}

/// <summary>
/// A removal's <c>scope</c> on the wire: the lower-case names of the <see cref="RemovalChoice"/>s the API, the task payload
/// and the GUI all use.
/// </summary>
public static class RemovalScopes
{
    public static string Wire(RemovalChoice scope) => scope switch
    {
        RemovalChoice.Record => "record",
        RemovalChoice.Previous => "previous",
        RemovalChoice.History => "history",
        RemovalChoice.Everything => "everything",
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    /// <summary>Parses a wire scope. Null is not a default: a removal must say how much it takes.</summary>
    public static RemovalChoice Parse(string? wire)
        => TryParse(wire, out var scope)
            ? scope
            : throw new SqlFlowException($"'{wire ?? "(none)"}' is not a removal scope; use record, previous, history or everything.");

    /// <summary>
    /// Parses the removal's <c>purgeLedger</c> argument: absent or <c>false</c> leaves the ledger as the removal leaves it,
    /// <c>true</c> deletes what OSDU confirmed removed from it. Anything else is refused rather than read as either, since
    /// a mistyped flag must neither delete history nor quietly keep it.
    /// </summary>
    public static bool ParsePurgeLedger(string? wire) => wire switch
    {
        null or "" or "false" => false,
        "true" => true,
        _ => throw new SqlFlowException($"'{wire}' is not what purgeLedger takes; use true or false."),
    };

    public static bool TryParse(string? wire, out RemovalChoice scope)
    {
        switch (wire)
        {
            case "record": scope = RemovalChoice.Record; return true;
            case "previous": scope = RemovalChoice.Previous; return true;
            case "history": scope = RemovalChoice.History; return true;
            case "everything": scope = RemovalChoice.Everything; return true;
            default: scope = RemovalChoice.Record; return false;
        }
    }
}

/// <summary>
/// The listing filter of a removal, carried through the task payload as JSON. It is the same filter the records
/// list is built from, so what an operator selected with "every record matching this" is what the node resolves.
/// </summary>
public sealed class RemovalFilter
{
    public string? Status { get; set; }

    public string? Search { get; set; }

    public string? Mode { get; set; }

    public Guid? SubmissionId { get; set; }

    /// <summary>The submission whose delivered records the removal is aimed at: what the batch put into OSDU.</summary>
    public Guid? DeliveredBySubmissionId { get; set; }

    public Guid? RunId { get; set; }

    public bool Drifted { get; set; }

    public static RecordQuery FromJson(string json)
    {
        var filter = JsonSerializer.Deserialize<RemovalFilter>(json, DeliveryOperation.JsonOptions)
            ?? throw new SqlFlowException("The removal's filter is not a JSON object.");
        RecordStatus? status = null;
        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            status = Enum.TryParse<RecordStatus>(filter.Status, ignoreCase: true, out var parsed)
                ? parsed
                : throw new SqlFlowException($"'{filter.Status}' is not a record status.");
        }

        return new RecordQuery
        {
            Status = status,
            Search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim(),
            Mode = string.Equals(filter.Mode, "contains", StringComparison.OrdinalIgnoreCase) ? SearchMode.Contains : SearchMode.Prefix,
            SubmissionId = filter.SubmissionId,
            DeliveredBySubmissionId = filter.DeliveredBySubmissionId,
            RunId = filter.RunId,
            Drifted = filter.Drifted,
        };
    }

    public static string ToJson(RecordQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return JsonSerializer.Serialize(
            new RemovalFilter
            {
                Status = query.Status?.ToString().ToLowerInvariant(),
                Search = query.Search,
                Mode = query.Mode == SearchMode.Contains ? "contains" : "prefix",
                SubmissionId = query.SubmissionId,
                DeliveredBySubmissionId = query.DeliveredBySubmissionId,
                RunId = query.RunId,
                Drifted = query.Drifted,
            },
            DeliveryOperation.JsonOptions);
    }
}
