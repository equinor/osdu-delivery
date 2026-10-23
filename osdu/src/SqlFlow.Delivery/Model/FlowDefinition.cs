using System.Text.Json.Serialization;
using SqlFlow.Core;

namespace SqlFlow.Delivery.Model;

/// <summary>
/// The operational half of the document model (osdu/docs/reference/flow/delivery.md) for one interface of a source:
/// which ingestion tables the records come from, which pinned mapping renders them, where they go and how reliably.
/// Only <see cref="Render"/> affects what a document is; everything else changes only how it gets there and stays out
/// of the content hash (osdu/docs/reference/concepts/change-detection.md, Render-affecting versus operational). A
/// document in the single form is one such definition with no <see cref="Interface"/>.
/// </summary>
public sealed record FlowDefinition
{
    public const string FlowTypeName = "delivery";

    /// <summary>The longest name a ledger identity is derived from: the width the ledger keeps a flow's name in.</summary>
    public const int MaxLedgerNameLength = 200;

    /// <summary>Path of the file the flow was loaded from, for error messages. Null for inline documents.</summary>
    public string? SourcePath { get; init; }

    /// <summary>The flow's name: the source's pipeline, shared by every interface of the document.</summary>
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>The platform batch the flow belongs to (grouping in listings and batch runs), from the envelope.</summary>
    public string? Batch { get; init; }

    /// <summary>The interface of its source this definition delivers, or null for a document in the single form.</summary>
    public string? Interface { get; init; }

    /// <summary>The name of an existing ledger the interface adopts (<c>ledger:</c>), or null when it keeps its own.</summary>
    public string? AdoptedLedger { get; init; }

    /// <summary>
    /// The partitions the flow may deliver to (<c>partitions</c>, osdu/docs/reference/flow/delivery.md, Partitions), in
    /// document order; empty for a flow that names none and delivers to the partition its <c>target.headers</c> name.
    /// </summary>
    public IReadOnlyList<DeclaredPartition> Partitions { get; init; } = [];

    /// <summary>
    /// The partition this definition is bound to (<see cref="ForPartition"/>): the one partition a run or a request of a flow
    /// that names its partitions acts on. Null for a flow that names none, and for one that names some before it is bound.
    /// </summary>
    public string? Partition { get; init; }

    /// <summary>True when the flow names the partitions it may deliver to.</summary>
    public bool DeclaresPartitions => Partitions.Count > 0;

    /// <summary>
    /// True for a flow that names neither its partitions nor a <c>data-partition-id</c> header: it serves every partition
    /// registered with the catalog (osdu/docs/reference/concepts/partitions.md, The partition registry), so the same
    /// document delivers to every environment, and a run binds it to the one it targets, the registry's default when it
    /// names none.
    /// </summary>
    public bool FollowsRegistry { get; init; }

    /// <summary>
    /// The partition that keeps the ledger the flow kept before it served partitions: <c>keepLedger</c> at the top of a flow
    /// that follows the registry. A flow that names its partitions marks it on the partition's entry instead. Null when every
    /// partition keeps a ledger of its own.
    /// </summary>
    public string? LedgerPartition { get; init; }

    /// <summary>True when the flow works in partitions, named or registered, so a run or a request binds it to one of them.</summary>
    public bool Partitioned => DeclaresPartitions || FollowsRegistry;

    /// <summary>True when the flow works in partitions and this definition is not yet bound to one of them.</summary>
    public bool IsUnbound => Partitioned && Partition is null;

    /// <summary>
    /// What the flow's own ledger identity is derived from, before any partition: the adopted ledger, the flow's name for the
    /// single form, and <c>flow/interface</c> for an interface of a source. A flow that names no partitions keeps its ledger
    /// under it, and so does the partition marked <c>keepLedger</c> of one that names them.
    /// </summary>
    public string OwnLedgerName => AdoptedLedger ?? Label;

    /// <summary>True when this definition is bound to the partition that keeps the flow's own ledger (<c>keepLedger</c>).</summary>
    public bool KeepsOwnLedger => Partition is not null
        && (Partitions.Any(p => p.KeepsLedger && string.Equals(p.Name, Partition, StringComparison.OrdinalIgnoreCase))
            || string.Equals(LedgerPartition, Partition, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The name the ledger records this definition under: <see cref="OwnLedgerName"/>, and <c>name@partition</c> for a
    /// partition that keeps a ledger of its own.
    /// </summary>
    /// <exception cref="InvalidOperationException">The flow names its partitions and this definition is bound to none.</exception>
    /// <remarks>
    /// Derived from the partition a definition is bound to, so it is no part of the document as written: the definition the
    /// catalog keeps as JSON leaves it out, as it does for a flow bound to no partition yet.
    /// </remarks>
    [JsonIgnore]
    public string LedgerName
    {
        get
        {
            ThrowIfUnbound();
            return Partition is null || KeepsOwnLedger ? OwnLedgerName : $"{OwnLedgerName}@{Partition}";
        }
    }

    /// <summary>How the definition is named in logs, traces and messages: the flow, and its interface when it has one.</summary>
    public string Label => Interface is null ? Name : $"{Name}/{Interface}";

    /// <summary>
    /// The ledger identity (see <see cref="Identity.FlowId"/>): derived from <see cref="OwnLedgerName"/>, and together with
    /// the partition for a partition that keeps a ledger of its own.
    /// </summary>
    /// <exception cref="InvalidOperationException">The flow names its partitions and this definition is bound to none.</exception>
    /// <remarks>Derived from the partition, as <see cref="LedgerName"/> is, and left out of the definition kept as JSON.</remarks>
    [JsonIgnore]
    public Guid Id
    {
        get
        {
            ThrowIfUnbound();
            return Partition is null || KeepsOwnLedger ? Identity.FlowId.Of(OwnLedgerName) : Identity.FlowId.Of(OwnLedgerName, Partition);
        }
    }

    /// <summary>
    /// This definition bound to <paramref name="partition"/>, one of the partitions the flow names, or for a flow that follows
    /// the registry, any partition: the flow as if it had been written for that one partition. Its requests carry the
    /// partition as <c>data-partition-id</c>, the mapping reads it as the kind's <c>dataPartition</c>, and its ledger is the
    /// partition's. Whether the partition is registered is the caller's to check (<see cref="SourceDefinition.Resolve"/>).
    /// </summary>
    /// <exception cref="DeliveryException">The flow works in no partitions, does not name this one, or it is no partition id.</exception>
    public FlowDefinition ForPartition(string partition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var wanted = partition.Trim();
        if (!Partitioned)
        {
            throw new DeliveryException(
                $"Flow '{Label}' names no partitions; it delivers to the partition its target.headers name, so it cannot be bound to '{wanted}'.");
        }

        string bound;
        if (DeclaresPartitions)
        {
            bound = Partitions.FirstOrDefault(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase))?.Name
                ?? throw new DeliveryException(
                    $"Flow '{Label}' does not deliver to partition '{wanted}'; it names {PartitionNames.Listed(Partitions.Select(p => p.Name))}.");
        }
        else
        {
            bound = Snapshots.CacheScope.IsPartitionId(wanted)
                ? wanted
                : throw new DeliveryException($"Flow '{Label}' cannot be bound to '{wanted}': a partition is a data-partition-id (letters, digits, underscore, hyphen and dot).");
        }

        var headers = new Dictionary<string, string>(Target.Headers, StringComparer.OrdinalIgnoreCase)
        {
            [Snapshots.CacheScope.PartitionHeader] = bound,
        };
        return this with { Partition = bound, Target = Target with { Headers = headers } };
    }

    private void ThrowIfUnbound()
    {
        if (IsUnbound)
        {
            throw new InvalidOperationException(DeclaresPartitions
                ? $"Flow '{Label}' names the partitions {PartitionNames.Listed(Partitions.Select(p => p.Name))} and keeps a ledger for each; bind it to the partition a run or request acts on before asking for its ledger."
                : $"Flow '{Label}' serves every partition registered with the catalog and keeps a ledger for each; bind it to the partition a run or request acts on before asking for its ledger.");
        }
    }

    /// <summary>The interfaces of the same source this one waits for (<c>after:</c>), beside those its mapping implies.</summary>
    public IReadOnlyList<string> After { get; init; } = [];

    /// <summary>When record failures stop the interface (<c>failWhen:</c>).</summary>
    public FlowFailWhen FailWhen { get; init; } = new();

    /// <summary>
    /// What the pipeline brings up to date in its partition's cache before it renders and after it delivers (<c>cache:</c>,
    /// osdu/docs/reference/flow/delivery.md, cache).
    /// </summary>
    public FlowCacheSteps Cache { get; init; } = new();

    /// <summary>
    /// Why the interface goes by the route <see cref="FlowTarget.Protocol"/> names: what it declared that decided it. Null for
    /// the single form, whose document names its protocol itself.
    /// </summary>
    public string? RouteReason { get; init; }

    public IReadOnlyDictionary<string, FlowParameter> Parameters { get; init; } = new Dictionary<string, FlowParameter>(StringComparer.Ordinal);

    public required FlowSource Source { get; init; }

    public required FlowRender Render { get; init; }

    public FlowChange Change { get; init; } = new();

    public required FlowTarget Target { get; init; }

    public FlowReliability Reliability { get; init; } = new();

    public FlowParallelism Parallelism { get; init; } = new();

    public FlowVerify Verify { get; init; } = new();

    /// <summary>
    /// The secret references the target declares (auth secrets, header values, token body values, the endpoint),
    /// keyed by their document path: what the platform lists as the flow's credential references. Only references
    /// (<c>${env:...}</c>, <c>${keyvault:...}</c>) are listed, never resolved values.
    /// </summary>
    public IEnumerable<KeyValuePair<string, string>> CredentialReferences()
    {
        if (IsReference(Source.Connection))
        {
            yield return new("source.connection", Source.Connection);
        }

        if (Target.Auth.SecretRef is { } secret)
        {
            yield return new("target.auth.secretRef", secret);
        }

        if (Target.Auth.SecondarySecretRef is { } secondary)
        {
            yield return new("target.auth.secondarySecretRef", secondary);
        }

        if (IsReference(Target.Endpoint))
        {
            yield return new("target.endpoint", Target.Endpoint);
        }

        foreach (var (name, value) in Target.Headers.Where(kv => IsReference(kv.Value)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            yield return new($"target.headers.{name}", value);
        }

        if (Target.Auth.Token is { } token)
        {
            foreach (var (name, value) in token.Body.Where(kv => IsReference(kv.Value)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                yield return new($"target.auth.token.body.{name}", value);
            }
        }
    }

    private static bool IsReference(string value) => value.Contains("${", StringComparison.Ordinal);
}

public sealed record FlowParameter
{
    public bool Required { get; init; }

    public string? Default { get; init; }

    public string? Description { get; init; }
}

/// <summary>
/// Where the flow's records come from (osdu/docs/reference/flow/delivery.md, source): the keyed ingestion tables
/// SQLFlow's pre-ingestion and ingestion flows load. The record table holds one row per record; each child dataset a
/// mapping repeats is a table of its own joined to it on the record key; payload files are named by a column of the
/// record row and read from where they sit. SQLFlow's system columns give each row its change time and its origin file
/// and row.
/// </summary>
public sealed record FlowSource
{
    /// <summary>
    /// The connection reference (<c>${env:NAME}</c>, <c>${keyvault:NAME}</c>) of the database holding the ingestion tables,
    /// resolved on the node that runs the flow; never a resolved connection string.
    /// </summary>
    public required string Connection { get; init; }

    /// <summary>The record table: its three-part name, its key and the scope predicate its parameters bind.</summary>
    public required FlowSourceTable Record { get; init; }

    /// <summary>The child datasets a mapping repeats, by the name it reads them under (<c>dataset.name.column</c>).</summary>
    public IReadOnlyDictionary<string, FlowSourceDataset> Datasets { get; init; } = new Dictionary<string, FlowSourceDataset>(StringComparer.Ordinal);

    /// <summary>Payload sets: where each record's files are, named by a column of its row.</summary>
    public IReadOnlyDictionary<string, FlowPayload> Payloads { get; init; } = new Dictionary<string, FlowPayload>(StringComparer.Ordinal);

    /// <summary>
    /// The record row's business version column (a timestamp, or RFC 3339 / ISO 8601 text; text without an offset is read
    /// as UTC). Ordered as well as compared: a row carrying a version older than the one delivered or queued is skipped as
    /// stale, so a late row never takes OSDU back to an earlier version. Null when the source declares none.
    /// </summary>
    public string? LastModified { get; init; }

    /// <summary>The names of SQLFlow's system columns on the ingestion tables.</summary>
    public FlowSystemColumns SystemColumns { get; init; } = new();

    /// <summary>How an incremental read windows, pages and isolates its reads.</summary>
    public FlowIncremental Incremental { get; init; } = new();

    /// <summary>
    /// Where the intake writes its work batches (the rendered documents the drains read back): a directory or storage
    /// prefix the nodes can write, relative to the flow file when it is a relative path. Supports {parameter} tokens.
    /// </summary>
    public required string Work { get; init; }
}

/// <summary>The record table of a flow's source.</summary>
public sealed record FlowSourceTable
{
    /// <summary>The table's three-part name, <c>[database].[schema].[table]</c>.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming", "CA1720:Identifier contains type name",
        Justification = "The flow document's key is 'object'; the model mirrors the document so a message about it names what the author wrote.")]
    public required string Object { get; init; }

    /// <summary>The key columns, in order: the ingestion flow's <c>load.keyColumns</c>, and the mapping's <c>dataset.key</c>.</summary>
    public required IReadOnlyList<string> Key { get; init; }

    /// <summary>
    /// The record table's identity primary key (the column SQLFlow's ingestion creates with <c>target.identityColumn</c>,
    /// such as <c>RecId</c>), or null. A read pages by it, and a fan-out cuts its slices on it: ranges of one ascending
    /// integer, found by counting the candidates per range of values instead of ranking every candidate by its key.
    /// Required when the flow fans out. A record is still identified by <see cref="Key"/>.
    /// </summary>
    public string? PrimaryKey { get; init; }

    /// <summary>The scope predicate: a column of the record table bound to a flow parameter (<c>[column] = @parameter</c>).</summary>
    public IReadOnlyDictionary<string, string> Scope { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>A child dataset of a flow's source: a table of rows each belonging to one record.</summary>
public sealed record FlowSourceDataset
{
    public const int DefaultMaxRowsPerRecord = 100_000;

    public const int MaxRowsPerRecordCeiling = 1_000_000;

    /// <summary>The table's three-part name.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming", "CA1720:Identifier contains type name",
        Justification = "The flow document's key is 'object'; the model mirrors the document so a message about it names what the author wrote.")]
    public required string Object { get; init; }

    /// <summary>How a child row joins its record: child column to record column, covering every key column of the record.</summary>
    public required IReadOnlyDictionary<string, string> Join { get; init; }

    /// <summary>The columns child rows are ordered by within a record.</summary>
    public IReadOnlyList<string> OrderBy { get; init; } = [];

    /// <summary>The most child rows one record may carry; a record above it is held rather than rendered.</summary>
    public int MaxRowsPerRecord { get; init; } = DefaultMaxRowsPerRecord;
}

/// <summary>One payload set: where a record's files sit and what says whether they changed.</summary>
public sealed record FlowPayload
{
    public const string DefaultPattern = "*";

    /// <summary>
    /// The folder or storage prefix the payload files must sit under: relative to the flow file when it is a relative
    /// path, with {parameter} tokens. A record's location that falls outside it holds the record.
    /// </summary>
    public required string Root { get; init; }

    /// <summary>The record column holding the record's payload folder: relative to <see cref="Root"/>, or absolute under a root.</summary>
    public string? LocationColumn { get; init; }

    /// <summary>The glob the payload files match under the folder.</summary>
    public string Pattern { get; init; } = DefaultPattern;

    /// <summary>The record column holding the payload's content hash.</summary>
    public string? HashColumn { get; init; }

    /// <summary>The record column holding how many files the payload has, which spares a listing when planning.</summary>
    public string? ChunkCountColumn { get; init; }
}

/// <summary>The names of SQLFlow's system columns an ingestion table carries.</summary>
public sealed record FlowSystemColumns
{
    public const string DefaultUpdated = "UpdatedDate_DW";

    public const string DefaultFileName = "FileName_DW";

    public const string DefaultRowNumber = "RowNumber_DW";

    public const string DefaultDeleted = "DeletedDate_DW";

    public const string DefaultInserted = "InsertedDate_DW";

    /// <summary>When the ingestion flow last changed the row: what an incremental read windows on.</summary>
    public string Updated { get; init; } = DefaultUpdated;

    /// <summary>
    /// When the ingestion flow first inserted the row, which it never moves afterwards: what dates the record's arrival in
    /// the ingestion table. Used when the table carries it; null when the flow opts out (<c>inserted: ~</c>).
    /// </summary>
    public string? Inserted { get; init; } = DefaultInserted;

    /// <summary>
    /// The file the row was landed from: each record's origin file in the ledger. Used when the table carries it; null when
    /// the flow opts out (<c>fileName: ~</c>).
    /// </summary>
    public string? FileName { get; init; } = DefaultFileName;

    /// <summary>The row's position in that file: each record's origin row in the ledger. Used when the table carries it; null when the flow opts out.</summary>
    public string? RowNumber { get; init; } = DefaultRowNumber;

    /// <summary>The soft-delete stamp; used when the table carries it, unless the flow opts out.</summary>
    public string? Deleted { get; init; } = DefaultDeleted;

    /// <summary>
    /// Whether the flow named <see cref="FileName"/> itself, so a table without it is refused rather than read without one:
    /// a misspelt name would otherwise lose every record's origin file without a word.
    /// </summary>
    public bool FileNameDeclared { get; init; }

    /// <summary>Whether the flow named <see cref="RowNumber"/> itself, so a table without it is refused rather than read without one.</summary>
    public bool RowNumberDeclared { get; init; }

    /// <summary>Whether the flow named <see cref="Deleted"/> itself, so a table without it is refused rather than read without one.</summary>
    public bool DeletedDeclared { get; init; }

    /// <summary>Whether the flow named <see cref="Inserted"/> itself, so a table without it is refused rather than read without one.</summary>
    public bool InsertedDeclared { get; init; }
}

/// <summary>How the source is read incrementally.</summary>
public sealed record FlowIncremental
{
    public const int DefaultOverlapSeconds = 900;

    public const int MaxOverlapSeconds = 86_400;

    public const int DefaultPageSize = 1000;

    public const int MaxPageSize = 100_000;

    /// <summary>How far below the last watermark the next read looks again, for rows whose statement committed late.</summary>
    public int OverlapSeconds { get; init; } = DefaultOverlapSeconds;

    /// <summary>Record keys per page.</summary>
    public int PageSize { get; init; } = DefaultPageSize;

    public SourceIsolation Isolation { get; init; } = SourceIsolation.Snapshot;

    /// <summary>Seconds a read may run; 0 waits as long as the run does (the run's cancellation bounds it).</summary>
    public int CommandTimeoutSeconds { get; init; }
}

/// <summary>How consistently the result sets of one page see the database.</summary>
public enum SourceIsolation
{
    /// <summary>Every result set of a page reads one snapshot, so a record and its child rows always agree.</summary>
    Snapshot,

    /// <summary>Each result set reads what is committed when it runs.</summary>
    ReadCommitted,
}

public sealed record FlowRender
{
    /// <summary>Pinned mapping reference in the form Name@version. Never floating
    /// (osdu/docs/reference/flow/delivery.md, render).</summary>
    public required string Mapping { get; init; }

    /// <summary>The <see cref="CacheVersion"/> that takes whichever version of the cache is current when the run starts.</summary>
    public const string CurrentCacheVersion = "current";

    /// <summary>
    /// The version of the cache a render reads, the cache of the partition the flow delivers to: <c>current</c> (the default)
    /// takes the partition cache's current version when the run starts and records it in the render context; a version label
    /// pins that version.
    /// </summary>
    public string CacheVersion { get; init; } = CurrentCacheVersion;

    /// <summary>Values for the parameters the mapping declares. They enter the content hash
    /// (osdu/docs/reference/concepts/change-detection.md, Render-affecting versus operational).</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The mappings directory, relative to the flow file. Null finds the nearest <c>mappings</c> directory walking up from the flow file.</summary>
    public string? MappingsDirectory { get; init; }

    public string MappingName => SplitMapping().Name;

    public string MappingVersion => SplitMapping().Version;

    private (string Name, string Version) SplitMapping()
    {
        var at = Mapping.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 || at == Mapping.Length - 1
            ? throw new FlowValidationException($"render.mapping '{Mapping}' must be pinned as 'Name@version'.")
            : (Mapping[..at], Mapping[(at + 1)..]);
    }
}

public enum ChangeDetection
{
    /// <summary>Documents only (<c>change.detect</c>): compare the hash of the rendered document with the ledger's.</summary>
    RenderedHash,

    /// <summary>Payloads only (<c>change.payloadDetect</c>): compare the payload's content hash with the ledger's.</summary>
    ContentHash,

    /// <summary>Always deliver.</summary>
    Always,

    /// <summary>
    /// Payloads only: the payload files under the record's location column are its watermark. A payload is reconsidered
    /// when a file was modified after the ones OSDU's payload was delivered from, or the set of files changed; the
    /// payload's hash column, when it declares one, is still the final check, so a rewrite with the same content is not
    /// uploaded again. Files older than what was delivered are stale and never sent.
    /// </summary>
    LastModified,
}

public enum UnchangedAction
{
    Skip,
    Deliver,
}

public sealed record FlowChange
{
    public ChangeDetection Detect { get; init; } = ChangeDetection.RenderedHash;

    public ChangeDetection PayloadDetect { get; init; } = ChangeDetection.ContentHash;

    public UnchangedAction OnUnchanged { get; init; } = UnchangedAction.Skip;

    /// <summary>
    /// Enable the tier-0 window gate: an incremental run whose change window holds no changed row of the record table or
    /// its datasets, and no record the ledger asked to plan again, completes without reading a record.
    /// </summary>
    public bool UseSourceVersions { get; init; } = true;
}

public sealed record FlowTarget
{
    /// <summary>Base URL of the delivery endpoint. Supports ${env:...} references.</summary>
    public required string Endpoint { get; init; }

    public TargetAuth Auth { get; init; } = new() { Type = TargetAuthType.None };

    /// <summary>Extra headers on every delivery request (for example an APIM subscription key or data-partition-id).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public required Protocols.DeliveryProtocol Protocol { get; init; }

    public ProtocolOptions ProtocolOptions { get; init; } = new();

    /// <summary>
    /// The DDMSs the flow declares under <c>target.ddms</c>, in document order: where each is under the endpoint, its
    /// call pattern and its collections. No entity type is served by two of them. A record on the ddms route goes to the
    /// one serving its entity type, and otherwise to the Wellbore DDMS under <see cref="ProtocolOptions.DdmsRoot"/>
    /// (the engine's <c>DdmsRouting</c>).
    /// </summary>
    public IReadOnlyList<DdmsService> Ddms { get; init; } = [];

    /// <summary>How the workflow route delivers the interface (<c>workflow:</c>); null for every other route.</summary>
    public WorkflowRoute? Workflow { get; init; }

    /// <summary>
    /// The Airflow instance behind the target's Workflow service (<c>target.airflow</c>), for the workflow outputs only
    /// Airflow's REST API returns; null when the flow reads none.
    /// </summary>
    public AirflowAccess? Airflow { get; init; }

    /// <summary>
    /// The External Data Services deployment behind the target (<c>target.eds</c>): how the records that configure it are
    /// checked before they are sent. Without the block they are checked with the defaults.
    /// </summary>
    public EdsTarget Eds { get; init; } = new();

    /// <summary>
    /// The Production DDMS core service behind the target (<c>target.dspdm</c>): where the dspdm route reaches it, and the
    /// business objects its kinds are rows of. Without the block the endpoint is DSPDM, and every default applies.
    /// </summary>
    public DspdmTarget Dspdm { get; init; } = new();

    /// <summary>
    /// The Reservoir DDMS behind the target (<c>target.etp</c>): where its ETP WebSocket answers, the dataspace its
    /// records go into, and what bounds one message. Without the block every default applies, and each record names its
    /// own dataspace.
    /// </summary>
    public EtpTarget Etp { get; init; } = new();

    /// <summary>
    /// What a record's references are checked against before it is sent (<c>target.verifyReferences</c>,
    /// osdu/docs/reference/concepts/preflight.md, References checked in storage).
    /// </summary>
    public ReferenceVerification VerifyReferences { get; init; } = ReferenceVerification.None;

    /// <summary>
    /// What the gate before a record is sent does with the record's verdict (<c>target.validation</c>, or an interface's
    /// <c>validation</c>; osdu/docs/reference/concepts/preflight.md, Validation before a record is sent): send it and
    /// record why, or hold it.
    /// </summary>
    public ValidationPolicy Validation { get; init; } = ValidationPolicy.Default;
}

/// <summary>
/// What the gate before a record is sent does with what a check of the record against its schema found
/// (<see cref="Validation.ValidationVerdict"/>). Every document whose metadata is sent is checked and its verdict recorded,
/// whatever the policy; the policy decides only which verdicts hold the record.
/// </summary>
public sealed record ValidationPolicy
{
    public static ValidationPolicy Default { get; } = new();

    /// <summary>What a record that breaks its schema does: sent and recorded (<c>report</c>, the default) or held (<c>enforce</c>).</summary>
    public ValidationMode Mode { get; init; } = ValidationMode.Report;

    /// <summary>What a record some part of which could not be checked does: sent (the default) or held.</summary>
    public UnverifiedAction Unverified { get; init; } = UnverifiedAction.Send;

    /// <summary>Whether a verdict of <paramref name="outcome"/> holds the record under this policy.</summary>
    public bool Holds(Validation.ValidationOutcome outcome) => outcome switch
    {
        Validation.ValidationOutcome.Invalid => Mode == ValidationMode.Enforce,
        Validation.ValidationOutcome.Unverified => Unverified == UnverifiedAction.Hold,
        _ => false,
    };

    /// <summary>How a hold names the setting that made it, as the flow writes it.</summary>
    public static string Setting(Validation.ValidationOutcome outcome) => outcome == Validation.ValidationOutcome.Unverified
        ? "validation.unverified is hold"
        : "validation.mode is enforce";
}

/// <summary>What the gate does with a record that breaks its schema.</summary>
public enum ValidationMode
{
    /// <summary>The record is sent, and its verdict recorded on the attempt and the record.</summary>
    Report,

    /// <summary>The record is held with its document kept, under an issue naming the rules it breaks.</summary>
    Enforce,
}

/// <summary>What the gate does with a record some part of which could not be checked.</summary>
public enum UnverifiedAction
{
    /// <summary>The record is sent, and the parts not checked recorded.</summary>
    Send,

    /// <summary>The record is held with its document kept.</summary>
    Hold,
}

/// <summary>What the OSDU ids a record refers to are checked against before the record is sent.</summary>
public enum ReferenceVerification
{
    /// <summary>
    /// The ledger alone: a record waits for a record it refers to that another record of the ledger holds and has not
    /// delivered, and refers to any other id as it is, since that record is OSDU's or another system's.
    /// </summary>
    None,

    /// <summary>
    /// The ledger, then OSDU's storage service for the ids no record of the ledger holds: a record referring to an id
    /// storage does not hold is held, for sources that must not write dangling references.
    /// </summary>
    Storage,
}

public enum TargetAuthType
{
    None,
    Bearer,
    ApiKeyHeader,
    Basic,
    OAuth2ClientCredentials,
}

public sealed record TargetAuth
{
    public required TargetAuthType Type { get; init; }

    /// <summary>Primary secret reference: the token (Bearer/api key), the password (Basic) or the client secret (OAuth2).</summary>
    public string? SecretRef { get; init; }

    /// <summary>Secondary secret reference: the username (Basic) or the client id (OAuth2).</summary>
    public string? SecondarySecretRef { get; init; }

    public string? HeaderName { get; init; }

    public string? ValuePrefix { get; init; }

    public TargetTokenEndpoint? Token { get; init; }
}

public sealed record TargetTokenEndpoint
{
    public string? Url { get; init; }

    public string? DiscoveryUrl { get; init; }

    public IReadOnlyDictionary<string, string> Body { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public bool BasicAuthClient { get; init; }

    public string TokenPath { get; init; } = "access_token";

    public string ApplyPrefix { get; init; } = "Bearer ";
}

/// <summary>Protocol-specific knobs, parameterised by the flow rather than authored as steps
/// (osdu/docs/reference/concepts/protocols.md, Why a route is code the flow parameterises, not steps the flow
/// writes).</summary>
public sealed record ProtocolOptions
{
    /// <summary>Path (under the endpoint) that accepts an array of records. Default depends on the protocol.</summary>
    public string? RecordPath { get; init; }

    /// <summary>HTTP method for the record write: PUT (storage) or POST (wellbore DDMS). Default depends on the protocol.</summary>
    public string? RecordMethod { get; init; }

    /// <summary>Path template for the single-request bulk upload; {id} is the target id.</summary>
    public string? DataPath { get; init; }

    /// <summary>Path template for creating a bulk session.</summary>
    public string? SessionPath { get; init; }

    /// <summary>Path template for one chunk in a session; {sessionId} is the session id.</summary>
    public string? SessionDataPath { get; init; }

    /// <summary>Path template for committing or abandoning a session.</summary>
    public string? SessionCommitPath { get; init; }

    /// <summary>Path template for reading a record back (verify).</summary>
    public string? VerifyPath { get; init; }

    /// <summary>Path template for the logical (revertible) delete. Default depends on the protocol.</summary>
    public string? DeletePath { get; init; }

    /// <summary>Path template for the physical purge of the record and all its versions. Default depends on the protocol.</summary>
    public string? PurgePath { get; init; }

    /// <summary>
    /// Path template for the physical purge of a record's earlier versions, the latest one left live (openapi
    /// storage v2, <c>DELETE /records/{id}/versions</c>). Versions are owned by the storage service for every kind
    /// of record, so this defaults to the storage path even for protocols that deliver through another service.
    /// </summary>
    public string? PurgeVersionsPath { get; init; }

    /// <summary>
    /// Path of the storage service's bulk soft delete (<c>POST /records/delete</c>), which takes a list of record
    /// ids in one request. Used only for the reversible scope; the purges have no bulk endpoint.
    /// </summary>
    public string? BulkDeletePath { get; init; }

    /// <summary>Path of the service's info endpoint the probe calls. Default depends on the protocol.</summary>
    public string? ProbePath { get; init; }

    /// <summary>Which payload set (from source.payloads) the protocol streams. Null for record-only protocols.</summary>
    public string? Payload { get; init; }

    /// <summary>Use a session when the chunk count exceeds this. Default 1: single-chunk payloads go in one request.</summary>
    public int SessionThresholdChunks { get; init; } = 1;

    /// <summary>
    /// Cells (rows times columns) one parquet payload chunk may carry, checked before anything is sent. Defaults to
    /// the wellbore DDMS ceiling (<see cref="WellboreDdmsBulkLimits.MaxChunkValues"/>); 0 does not check.
    /// </summary>
    public long MaxChunkValues { get; init; } = WellboreDdmsBulkLimits.MaxChunkValues;

    /// <summary>
    /// Columns one parquet payload chunk may carry, checked before anything is sent. Defaults to the wellbore DDMS
    /// ceiling from OSDU M26 (<see cref="WellboreDdmsBulkLimits.MaxChunkColumns"/>); a target on M23 or M25 declares
    /// <see cref="WellboreDdmsBulkLimits.MaxChunkColumnsThroughM25"/>; 0 does not check.
    /// </summary>
    public int MaxChunkColumns { get; init; } = WellboreDdmsBulkLimits.MaxChunkColumns;

    /// <summary>Media type of the payload chunks.</summary>
    public string PayloadContentType { get; init; } = "application/x-parquet";

    /// <summary>
    /// The media type a record's files are uploaded with, to the file service's landing zone or the dataset service's
    /// staging area. Null takes <see cref="PayloadContentType"/> on a route whose payload is its files (file, manifest,
    /// dataset, workflow), and <see cref="DefaultFilesContentType"/> on a route that also sends bulk data (fileAndDdms,
    /// manifestAndDdms), whose payload content type is the bulk data's.
    /// </summary>
    public string? FilesContentType { get; init; }

    /// <summary>The type a route that also sends bulk data uploads its files as, when the flow names none for them.</summary>
    public const string DefaultFilesContentType = "application/octet-stream";

    /// <summary>
    /// These options as an upload of a record's files reads them: <see cref="PayloadContentType"/> is the type the files
    /// go as (<see cref="FilesContentType"/>). <paramref name="besideBulk"/> says the route also sends bulk data.
    /// </summary>
    public ProtocolOptions ForFiles(bool besideBulk)
        => this with { PayloadContentType = FilesContentType ?? (besideBulk ? DefaultFilesContentType : PayloadContentType) };

    /// <summary>The JSON path in the write response holding id:version strings.</summary>
    public string VersionPath { get; init; } = "recordIdVersions[0]";

    /// <summary>
    /// Sends <c>skipdupes=true</c> on the record write (openapi storage v2, PUT /records): "Skip duplicates when
    /// updating records with the same value". A record the service skips is named under <c>skippedRecordIds</c>
    /// and keeps its current version, and the ledger settles it at the version it already held.
    ///
    /// Off by default, because the spec does not say which parts of a record the service compares. Deliveries are
    /// already gated on the hash of the whole rendered document, so a write that reaches storage is a document that
    /// changed. If the service judged sameness by <c>data</c> alone, a change to only <c>acl</c>, <c>legal</c> or
    /// <c>tags</c> would be skipped: OSDU would keep the old access control or legal tags while the ledger recorded
    /// the change as delivered. What skipping buys is small (no extra version on a forced redelivery, or on a retry
    /// after a write that landed), so a flow opts in only once the comparison is confirmed for its target.
    /// </summary>
    public bool SkipDuplicates { get; init; }

    /// <summary>
    /// Path of the storage service's batched header read (<c>POST /query/records</c> by default), used to verify
    /// many records' versions in one request instead of one read per record.
    /// </summary>
    public string? VerifyBatchPath { get; init; }

    /// <summary>
    /// Well log protocol: where the wellbore DDMS sits under the flow's endpoint, when that endpoint is the OSDU
    /// platform root rather than the DDMS itself. The DDMS is deployed under <c>/api/os-wellbore-ddms</c> (the
    /// platform's ingress route, and the OSDU C# client's ServiceRegistry), and its own paths
    /// (<c>/ddms/v3/welllogs</c>, <c>/about</c>) are relative to that. Null, the default, means the endpoint already
    /// is the DDMS or a facade serving its paths. Set, every DDMS default path is taken under it, and the operations
    /// the storage service owns (the history purge) resolve under the endpoint as they do for every other protocol.
    /// A path option the flow sets explicitly is used as written either way.
    /// </summary>
    public string? DdmsRoot { get; init; }

    /// <summary>
    /// Whether a deliver or intake run asks the legal service about the mapping's legal tags before planning anything
    /// (openapi legal v1, <c>POST /legaltags:validate</c>). Default true. Off, the run starts without asking and says
    /// so in its log; storage still refuses a record whose tag is invalid, one record at a time.
    /// </summary>
    public bool ValidateLegalTags { get; init; } = true;

    /// <summary>
    /// The legal service's validate endpoint, when the default (<c>/api/legal/v1/legaltags:validate</c> under a
    /// platform-root endpoint) is not where it is: a path under the endpoint, or an absolute URL for a well log flow
    /// whose endpoint is the DDMS itself.
    /// </summary>
    public string? LegalValidatePath { get; init; }

    /// <summary>
    /// Data keys OSDU owns that must be copied forward from the existing record when updating
    /// (osdu/docs/reference/flow/routes.md, Options every route reads). Empty means the rendered document replaces the
    /// whole data block.
    /// </summary>
    public IReadOnlyList<string> PreserveDataKeys { get; init; } = [];

    /// <summary>Records per write request for the protocols that accept arrays (the storage service takes up to 500). Default 100.</summary>
    public int BatchSize { get; init; } = 100;

    /// <summary>The largest array the target accepts in one write; a flow cannot raise <see cref="BatchSize"/> above it.</summary>
    public const int MaxBatchSize = 500;

    /// <summary>File and manifest protocols: the path that hands out a signed upload location (file service v2).</summary>
    public string? UploadUrlPath { get; init; }

    /// <summary>File and manifest protocols: the path that registers a file's metadata record after the upload.</summary>
    public string? FileMetadataPath { get; init; }

    /// <summary>The kind of the dataset record registered per uploaded file. Default osdu:wks:dataset--File.Generic:1.0.0.</summary>
    public string DatasetKind { get; init; } = "osdu:wks:dataset--File.Generic:1.0.0";

    /// <summary>Extra headers on the signed-URL upload itself (the Azure landing zone needs x-ms-blob-type: BlockBlob).</summary>
    public IReadOnlyDictionary<string, string> UploadHeaders { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The data property of the record that lists the dataset ids of its files. Default Datasets.</summary>
    public string DatasetsProperty { get; init; } = "Datasets";

    /// <summary>Manifest protocol: the workflow (DAG) name the manifest is handed to. Default Osdu_ingest.</summary>
    public string WorkflowName { get; init; } = "Osdu_ingest";

    /// <summary>Manifest protocol: the path that triggers a workflow run; {workflow} is the workflow name.</summary>
    public string? WorkflowRunPath { get; init; }

    /// <summary>Manifest protocol: the path that reports a run's status; {workflow} and {runId} are substituted.</summary>
    public string? WorkflowStatusPath { get; init; }

    /// <summary>Manifest protocol: seconds between status polls. Default 10.</summary>
    public int WorkflowPollSeconds { get; init; } = 10;

    /// <summary>Manifest protocol: how long a workflow run may take before the record is retried. Default 60 minutes.</summary>
    public int WorkflowTimeoutMinutes { get; init; } = 60;

    /// <summary>
    /// Manifest protocol: how long to wait, after registering a record's files, for the search index to list the new
    /// datasets before the manifest names them. Ingestion checks references against the index and drops a record whose
    /// dataset it cannot find yet (observed on a live M26 service). 0 does not wait. Default 120 seconds.
    /// <para>
    /// File and manifest protocols: it also bounds how long a registration resumed after an interrupted try waits for
    /// the index to list the dataset that try may have created, before registering the file again.
    /// </para>
    /// </summary>
    public int DatasetIndexWaitSeconds { get; init; } = 120;

    /// <summary>
    /// File and manifest protocols: the search query path the waits for registered datasets ask (the manifest's wait
    /// for its own datasets, and a resumed registration's lookup by landing-zone path). Default /api/search/v2/query.
    /// </summary>
    public string? SearchQueryPath { get; init; }

    /// <summary>Manifest protocol: the manifest kind. Default osdu:wks:Manifest:1.0.0.</summary>
    public string ManifestKind { get; init; } = "osdu:wks:Manifest:1.0.0";

    /// <summary>
    /// File and manifest protocols: how long the signed upload URL stays valid (openapi file v2 expiryTime: 30M,
    /// 12H, 2D). Null takes the service default of one hour; a large file over a slow link needs more.
    /// </summary>
    public string? UploadUrlExpiry { get; init; }

    /// <summary>File and manifest protocols: the path that deletes a dataset record and its file on purge; {id} is the dataset id.</summary>
    public string? FileDeletePath { get; init; }

    /// <summary>
    /// Manifest protocol: the manifest section the records go into (one of <see cref="ManifestSections"/>). Null
    /// derives it from each record's kind.
    /// </summary>
    public string? ManifestSection { get; init; }

    /// <summary>Manifest protocol: the AppKey in the workflow's execution context. Default osdu-delivery.</summary>
    public string WorkflowAppKey { get; init; } = "osdu-delivery";

    /// <summary>Manifest protocol: extra entries of the execution context's Payload, beside AppKey and data-partition-id.</summary>
    public IReadOnlyDictionary<string, string> WorkflowPayload { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Manifest protocol: the storage path that reads records back by id after a run. Default /api/storage/v2/query/records.</summary>
    public string? RecordQueryPath { get; init; }

    /// <summary>
    /// ddms route: where the Register service reads a DDMS registration (openapi register v1, <c>GET /ddms/{id}</c>),
    /// for a DDMS the flow declares with <c>register</c>. Default <c>/api/register/v1/ddms/{id}</c> under the endpoint.
    /// </summary>
    public string? RegisterPath { get; init; }

    /// <summary>Dataset service: the path that hands out a storage location for a dataset kind (openapi dataset v1, POST storageInstructions).</summary>
    public string? DatasetInstructionsPath { get; init; }

    /// <summary>Dataset service: the path that registers datasets (openapi dataset v1, PUT registerDataset).</summary>
    public string? DatasetRegisterPath { get; init; }

    /// <summary>Dataset service: the path that answers where registered datasets can be read (openapi dataset v1, POST retrievalInstructions).</summary>
    public string? DatasetRetrievalPath { get; init; }

    /// <summary>Dataset service: the reversible removal of a dataset record; {id} is its id (openapi dataset v1, POST metadataRecord/{id}/softDelete).</summary>
    public string? DatasetSoftDeletePath { get; init; }

    /// <summary>Manifest protocol: when a manifest goes to the ingestion workflow by reference rather than inline.</summary>
    public ManifestReference ManifestByReference { get; init; } = ManifestReference.Never;

    /// <summary>
    /// Manifest protocol: the size of a trigger request, in kilobytes, above which <see cref="ManifestReference.Auto"/>
    /// sends the manifest by reference. Default 12000, the limit External Data Services applies
    /// (osdu/specs/workflows/INTEGRATION.md section 3.3).
    /// </summary>
    public int ManifestInlineLimitKb { get; init; } = DefaultManifestInlineLimitKb;

    public const int DefaultManifestInlineLimitKb = 12_000;

    /// <summary>Manifest protocol: the workflow a manifest by reference is handed to. Default Osdu_ingest_by_reference.</summary>
    public string ByReferenceWorkflowName { get; init; } = DefaultByReferenceWorkflowName;

    public const string DefaultByReferenceWorkflowName = "Osdu_ingest_by_reference";

    /// <summary>Manifest and workflow protocols: the path that reads a workflow by name; {workflow} is its name (openapi workflow v1, GET workflow/{workflow_name}).</summary>
    public string? WorkflowPath { get; init; }

    /// <summary>
    /// ddms route, RAFS shape: the content schema version a content table follows when its file name names none
    /// (<c>nmr.parquet</c> rather than <c>nmr.1.1.0.parquet</c>). Default 1.0.0, a version every RAFS content type has.
    /// </summary>
    public string ContentSchemaVersion { get; init; } = DefaultContentSchemaVersion;

    public const string DefaultContentSchemaVersion = "1.0.0";

    /// <summary>The sections of an osdu:wks:Manifest:1.0.0 a record can be placed in.</summary>
    public static readonly IReadOnlyList<string> ManifestSections = ["ReferenceData", "MasterData", "WorkProduct", "WorkProductComponents", "Datasets"];
}

/// <summary>When the manifest route hands a manifest to the ingestion workflow by reference (osdu/specs/workflows/INTEGRATION.md section 3.3).</summary>
public enum ManifestReference
{
    /// <summary>Every manifest goes inline, in the trigger request.</summary>
    Never,

    /// <summary>Every manifest is stored as a dataset and handed over by its id.</summary>
    Always,

    /// <summary>A manifest whose trigger request is above the inline limit goes by reference when the target has the workflow, and is split otherwise.</summary>
    Auto,
}

/// <summary>
/// What makes a flow's requests hold (osdu/docs/reference/flow/delivery.md, reliability): retries, timeouts, the rate limit,
/// TLS, the addresses it may reach, response and request ceilings, and how long a lease on a record lasts. How much runs at
/// once, on one node and across the fleet, is the flow's <see cref="FlowParallelism"/>.
/// </summary>
public sealed record FlowReliability
{
    public FlowRetry Retry { get; init; } = new();

    /// <summary>Non-retryable statuses that mark a record held instead of failing the run.</summary>
    public IReadOnlyList<int> SkipStatusCodes { get; init; } = [];

    public int TimeoutSeconds { get; init; } = 100;

    public double RateLimitRps { get; init; }

    public bool VerifyTls { get; init; } = true;

    public IReadOnlyList<string> UrlAllowlist { get; init; } = [];

    public long MaxResponseBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// The target's declared request body ceiling (osdu/docs/reference/concepts/availability-and-retention.md, Size
    /// ceilings). A payload chunk above it is held before anything is sent, instead of surfacing as a 413 mid-session.
    /// 0 means not declared.
    /// </summary>
    public long MaxRequestBodyBytes { get; init; }

    /// <summary>How long a worker's lease on a record lasts before a sweep may reclaim it.</summary>
    public int LeaseSeconds { get; init; } = 300;
}

/// <summary>
/// How much of a delivery flow's work runs at once (osdu/docs/reference/flow/delivery.md, parallelism): within one run, and
/// across the fleet, where a large submission fans out to member runs the nodes share. The assertion, dimension, inventory
/// and retrieval kinds take <see cref="RunParallelism"/>, and a cache flow none.
/// </summary>
public sealed record FlowParallelism
{
    /// <summary>The records one run sends at once; a verify or reverse run reads this many at once too. Default 8.</summary>
    public int Concurrency { get; init; } = 8;

    /// <summary>
    /// The most member runs a deliver run spreads a submission of at least <see cref="FanOutMinRecords"/> records over across
    /// the fleet, planning first (when the record table names its identity primary key), then sending, never more than it
    /// has work batches for (osdu/docs/reference/concepts/submissions.md, Many nodes). <see cref="DefaultFanOut"/> unless the
    /// flow says otherwise; 0 keeps everything on the run that claimed the work.
    /// </summary>
    public int FanOut { get; init; } = DefaultFanOut;

    /// <summary>How many member runs a large submission spreads over unless the flow says otherwise.</summary>
    public const int DefaultFanOut = 8;

    /// <summary>Fan out only when the source holds at least this many candidate records, or a submission this many planned ones. Default 1000.</summary>
    public int FanOutMinRecords { get; init; } = 1000;

    /// <summary>Render batches rendered at once on one node. 0 means half the processors, at least one.</summary>
    public int Renderers { get; init; }

    /// <summary>
    /// Records per work batch: one file of rendered documents the intake writes, one claim a drain takes, one unit of
    /// progress the submission page shows (osdu/docs/reference/concepts/submissions.md, Work batches). Default 500.
    /// </summary>
    public int BatchRecords { get; init; } = 500;

    /// <summary>Records claimed per ledger round trip when retrying individual records.</summary>
    public int BatchSize { get; init; } = 50;

    /// <summary>The largest fan-out a flow may declare.</summary>
    public const int MaxFanOut = 64;

    /// <summary>The most renderers a flow may declare.</summary>
    public const int MaxRenderers = 256;

    /// <summary>The most records a work batch may hold.</summary>
    public const int MaxBatchRecords = 100_000;

    /// <summary>The renderers on this host.</summary>
    public int EffectiveRenderers => Renderers > 0 ? Renderers : Math.Max(1, Environment.ProcessorCount / 2);
}

/// <summary>
/// The <c>parallelism</c> of an assertion, dimension, inventory or retrieval flow: what one run works on at once. None of
/// them fans out across the fleet.
/// </summary>
public sealed record RunParallelism
{
    /// <summary>
    /// What one run works on at once: an assertion run's tests (and each test's requests in flight), a dimension build's
    /// dimensions (and each one's searches), an inventory build's version lists, a retrieval's kinds. Default 8.
    /// </summary>
    public int Concurrency { get; init; } = 8;
}

public enum BackoffKind
{
    Exponential,
    Fixed,
}

public sealed record FlowRetry
{
    public int Attempts { get; init; } = 4;

    public BackoffKind Backoff { get; init; } = BackoffKind.Exponential;

    public int BaseDelayMs { get; init; } = 500;

    public int MaxDelayMs { get; init; } = 30_000;

    public bool HonorRetryAfter { get; init; } = true;

    /// <summary>Backoff between record-level attempts across worker passes (minutes, exponential per attempt).</summary>
    public int RecordBaseDelayMinutes { get; init; } = 1;

    public int RecordMaxDelayMinutes { get; init; } = 60;
}

public sealed record FlowVerify
{
    /// <summary>Re-queue drifted or missing records for redelivery.</summary>
    public bool Reconcile { get; init; }
}
