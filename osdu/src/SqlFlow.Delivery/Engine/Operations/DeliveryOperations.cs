using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Hosting;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Execution;

namespace SqlFlow.Delivery.Engine.Operations;

/// <summary>
/// The ad-hoc operations of a flow: what the GUI's buttons do when they need OSDU or the ingestion tables rather than the
/// ledger. A person's reads run in the control plane as they are asked (<see cref="DirectOperations"/>); what scans (a value
/// check) is queued for a node. What writes to OSDU or the ledger (a removal among them) is a run of the flow instead, so it
/// is listed with the flow's runs and never runs beside another of them. Either way the payload is the same: it names the flow
/// (<c>sourceRef</c>) whose target and credentials it uses and carries the flow file's location (<c>repoRoot</c> +
/// <c>relativePath</c> as the catalog knows them, with the repository's id, or an absolute <c>flowFile</c>), and the process
/// running it resolves every credential itself, exactly as it does for a run. The document must declare the named flow, so
/// a stale catalog can never aim an operation at the wrong target. A flow that declares interfaces is acted on through the
/// one the payload names (<c>interface</c>); the others are never touched. A flow that names its partitions is acted on in
/// the one the payload names (<c>partition</c>), resolving its references with the central configuration the control
/// plane supplied for it (<c>references</c>).
/// </summary>
public abstract class DeliveryOperation : IComputeOperation
{
    /// <summary>The task argument naming the partition a flow that names its partitions is acted on in.</summary>
    public const string PartitionArgument = "partition";

    /// <summary>The task argument naming the repository the flow is synced from, by its catalog id.</summary>
    public const string RepositoryArgument = "repoId";

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

    /// <summary>
    /// The operation code the operation's OSDU requests carry in their correlation id, <c>osdu-delivery-&lt;code&gt;-&lt;id&gt;</c>
    /// with a new id each time (osdu/docs/reference/concepts/request-correlation.md): it is the product's own, whichever flow's
    /// target it reads through.
    /// </summary>
    public abstract string CorrelationOperation { get; }

    public virtual async Task<string> ExecuteAsync(ComputeTaskPayload payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var correlation = OsduCorrelation.Begin(CorrelationOperation);
        var flowFile = await ResolveFlowFileAsync(payload, ct).ConfigureAwait(false);
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
        => _context.Ledger ?? throw new SqlFlowException($"The '{Name}' operation needs the ledger, which lives in the module's database this node was started without. {OsduModuleDatabase.StartWithCatalogConnection}");

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

    /// <summary>The flow's file: an absolute <c>flowFile</c> as given, or <c>relativePath</c> in its repository's synced folder.</summary>
    private async Task<string> ResolveFlowFileAsync(ComputeTaskPayload payload, CancellationToken ct)
    {
        var file = payload.Argument("flowFile") is { } given
            ? Path.GetFullPath(given)
            : Path.GetFullPath(Path.Combine(
                await RepositoryFolderAsync(payload, ct).ConfigureAwait(false),
                payload.RequireArgument("relativePath").Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(file))
        {
            throw new SqlFlowException($"The flow file for '{payload.SourceRef}' was not found where its repository was synced: {file}. Re-sync the repository.");
        }

        return file;
    }

    /// <summary>
    /// The folder holding the flow's repository on this host: the one the catalog recorded (<c>repoRoot</c>) while this host
    /// sees it, else the one the engine finds for the repository the task names (<see cref="EngineContext.RepositoryFolders"/>),
    /// which checks the repository's synced commit out again from its remote. So a host whose checkouts are gone (a redeployed
    /// container, a node that never synced) reads the file the last sync recorded, as a run of the flow does.
    /// </summary>
    private async Task<string> RepositoryFolderAsync(ComputeTaskPayload payload, CancellationToken ct)
    {
        var recorded = payload.Argument("repoRoot");
        if (!string.IsNullOrWhiteSpace(recorded) && Directory.Exists(recorded))
        {
            return recorded;
        }

        if (!Guid.TryParse(payload.Argument(RepositoryArgument), out var repoId))
        {
            var where = string.IsNullOrWhiteSpace(recorded)
                ? "the task names no folder its repository was synced into"
                : $"{recorded}, where its repository was synced, is not on this host";
            throw new SqlFlowException(
                $"The flow file for '{payload.SourceRef}' cannot be read: {where}, and the task names no repository ('{RepositoryArgument}') to check out again.");
        }

        var folder = await _context.RepositoryFolders.FolderOfAsync(repoId, ct).ConfigureAwait(false);
        return folder.Path ?? throw new SqlFlowException($"The flow file for '{payload.SourceRef}' cannot be read: {folder.Problem}.");
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

    public override string CorrelationOperation => "probe";

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

    /// <summary>
    /// The operation code a record's read-back carries in its correlation id; one the explorer or a run makes carries theirs,
    /// as every request made inside an operation or a run does.
    /// </summary>
    internal const string ReadCorrelation = "read";

    public override string Name => OperationName;

    public override string CorrelationOperation => ReadCorrelation;

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
        using var correlation = Http.OsduCorrelation.Begin(ReadCorrelation);
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
/// A removal's <c>scope</c> on the wire: the lower-case names of the <see cref="RemovalChoice"/>s the API, the remove run's
/// payload and the GUI all use.
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

    /// <summary>Reads a wire scope, exactly as written. Null is not a default: a removal must say how much it takes.</summary>
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
/// The listing filter of a removal, as a remove run's payload carries it (<c>filter</c>). It is the filter the records list
/// is built from, every part of it, the issue included, so what an operator selected with "every record matching this" is
/// what the run removes and never more: a part left out here would widen the set the run resolves past the one the operator
/// was shown and counted. A property it does not know is refused, as the payload around it refuses one.
/// </summary>
public sealed class RemovalFilter
{
    private static readonly JsonSerializerOptions Strict = new(DeliveryOperation.JsonOptions)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public string? Status { get; set; }

    public string? Search { get; set; }

    /// <summary>How the search matches: <c>contains</c>, or a prefix when left out.</summary>
    public string? Mode { get; set; }

    public Guid? SubmissionId { get; set; }

    /// <summary>The submission whose delivered records the removal is aimed at: what the batch put into OSDU.</summary>
    public Guid? DeliveredBySubmissionId { get; set; }

    public Guid? RunId { get; set; }

    /// <summary>Only drifted records when true; left out, every record.</summary>
    public bool? Drifted { get; set; }

    /// <summary>The issue whose blocked records the listing keeps: the sixteen characters the flow's issues listing gives it.</summary>
    public string? Issue { get; set; }

    public static RecordQuery FromJson(string json)
    {
        RemovalFilter? filter;
        try
        {
            filter = JsonSerializer.Deserialize<RemovalFilter>(json, Strict);
        }
        catch (JsonException ex)
        {
            throw new SqlFlowException($"payload filter is not a listing filter: {ex.Message}", ex);
        }

        if (filter is null)
        {
            throw new SqlFlowException("payload filter is not a JSON object.");
        }

        RecordStatus? status = null;
        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            status = Enum.TryParse<RecordStatus>(filter.Status, ignoreCase: true, out var parsed)
                ? parsed
                : throw new SqlFlowException($"payload filter status '{filter.Status}' is not a record status.");
        }

        long? problem = null;
        if (!string.IsNullOrWhiteSpace(filter.Issue))
        {
            problem = ProblemSignature.TryParse(filter.Issue.Trim().ToLowerInvariant(), out var issue)
                ? issue
                : throw new SqlFlowException(
                    $"payload filter issue '{filter.Issue}' is not an issue: an issue is the {ProblemSignature.TextLength} characters the flow's issues listing gives it.");
        }

        return new RecordQuery
        {
            Status = status,
            Problem = problem,
            Search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim(),
            Mode = string.Equals(filter.Mode, "contains", StringComparison.OrdinalIgnoreCase) ? SearchMode.Contains : SearchMode.Prefix,
            SubmissionId = filter.SubmissionId,
            DeliveredBySubmissionId = filter.DeliveredBySubmissionId,
            RunId = filter.RunId,
            Drifted = filter.Drifted == true,
        };
    }

    public static string ToJson(RecordQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Only the parts that narrow the listing are written, so the run's payload says what was asked and no more.
        return JsonSerializer.Serialize(
            new RemovalFilter
            {
                Status = query.Status?.ToString().ToLowerInvariant(),
                Search = query.Search,
                Mode = query.Mode == SearchMode.Contains ? "contains" : null,
                SubmissionId = query.SubmissionId,
                DeliveredBySubmissionId = query.DeliveredBySubmissionId,
                RunId = query.RunId,
                Drifted = query.Drifted ? true : null,
                Issue = query.Problem is { } issue ? ProblemSignature.Format(issue) : null,
            },
            DeliveryOperation.JsonOptions);
    }
}
