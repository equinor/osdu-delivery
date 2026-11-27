using System.Text.Json;
using System.Text.Json.Serialization;
using SqlFlow.Core.Comparison;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Model;
using SqlFlow.Core.Quality;
using SqlFlow.Core.Query;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Core.Compute;

/// <summary>
/// The ad-hoc datasource compute operations a control-plane client can request. Each is a short camelCase
/// string (self-describing in the queue table and in API payloads, like <c>RunStatuses</c>); the set is closed
/// so the trust boundary can refuse anything it does not know how to execute.
/// </summary>
public static class ComputeOperations
{
    /// <summary>Open the resolved connection and report the server version: the cheapest end-to-end proof that
    /// the reference resolves, the network path exists, and the credentials work.</summary>
    public const string TestConnection = "testConnection";

    /// <summary>List the databases visible on the connection (provider-specific catalog query).</summary>
    public const string ListDatabases = "listDatabases";

    /// <summary>List the schemas of one database (or the connection's current database).</summary>
    public const string ListSchemas = "listSchemas";

    /// <summary>List tables/views in a database/schema scope, filtered and paged.</summary>
    public const string ListObjects = "listObjects";

    /// <summary>Search objects by name across the database.</summary>
    public const string SearchObjects = "searchObjects";

    /// <summary>Full structured introspection of one table or view: columns, indexes, constraints.</summary>
    public const string IntrospectObject = "introspectObject";

    /// <summary>Profile one table/view for its minimal unique key(s) (SQL Server / Azure SQL sources only).</summary>
    public const string DetectUniqueKey = "detectUniqueKey";

    /// <summary>Profile the columns of one table/view for the SQL type each one's values convert to, with the counts
    /// behind the choice, through the engine's own type inference (SQL Server / Azure SQL sources only). It returns
    /// counts, lengths and the chosen types, never a value.</summary>
    public const string InferColumnTypes = "inferColumnTypes";

    /// <summary>Report the engine's missing-index advisories (<c>sys.dm_db_missing_index_*</c>) for the scoped
    /// database, ranked by estimated improvement, with a ready-to-review CREATE INDEX suggestion per advisory
    /// (SQL Server / Azure SQL sources only).</summary>
    public const string MissingIndexes = "missingIndexes";

    /// <summary>Report statistics freshness (<c>sys.dm_db_stats_properties</c>) for the scoped database: rows
    /// modified since the last update, sample rates, and which statistics have gone stale, with an UPDATE
    /// STATISTICS suggestion per stale entry (SQL Server / Azure SQL sources only).</summary>
    public const string StatisticsHealth = "statisticsHealth";

    /// <summary>Report per-index read/write usage (<c>sys.dm_db_index_usage_stats</c>) for the scoped database,
    /// surfacing write-only and never-read indexes that cost maintenance without serving queries
    /// (SQL Server / Azure SQL sources only).</summary>
    public const string IndexUsage = "indexUsage";

    /// <summary>Report the plan cache's most expensive statements (<c>sys.dm_exec_query_stats</c>) ranked by
    /// total elapsed time, with per-statement execution counts, CPU, and I/O
    /// (SQL Server / Azure SQL sources only).</summary>
    public const string TopQueries = "topQueries";

    /// <summary>Check one table for duplicate rows on its key, using the key the TABLE declares. READ-ONLY:
    /// it groups and counts. Gated by <c>ControlPlane:DataOps:Enabled</c>.</summary>
    public const string DuplicateKeys = "duplicateKeys";

    /// <summary>Run one APPROVED read-only business query. The statement reaches this operation only after a
    /// human saw it and a one-time plan token was redeemed (or a trusted saved answer a person confirmed runs), so
    /// the queue row is the record of an approved query, never of an ad-hoc one. Gated by
    /// <c>ControlPlane:AiContext:Enabled</c>, and never queued through the compute task endpoint.</summary>
    public const string RunQuery = "runQuery";

    /// <summary>Run one query a report declares (<c>flowType: rpt</c>). The statement is the one the repository's
    /// document holds, placed on the queue row by the control plane and never by a client: a request that runs a
    /// report's query names the report and the query, and gives only values for the parameters the report declares,
    /// each bound as a typed parameter. It runs as <see cref="RunQuery"/> runs, read-only and rolled back.</summary>
    public const string ReportQuery = "reportQuery";

    /// <summary>Compare the current estate against the OLD production baseline over a configured linked
    /// server: object inventory, one object's shape, or one object's rows. Read-only on both estates and
    /// gated by <c>ControlPlane:DataOps:Enabled</c>.</summary>
    public const string CompareBaseline = "compareBaseline";

    /// <summary>Read the header of each of the newest files a file flow's source matches, as a run of that flow reads
    /// them, and set their columns against the table it loads into. The flow's document is the one the catalog stores for
    /// it, placed on the queue row by the control plane and never by a client: a request names the pipeline, never a
    /// location, and the task resolves the document's references with the values the flow's runs are supplied with.</summary>
    public const string ScanSourceColumns = "scanSourceColumns";

    /// <summary>Every operation this build understands, for validation messages.</summary>
    public static readonly string[] All =
        [TestConnection, ListDatabases, ListSchemas, ListObjects, SearchObjects, IntrospectObject, DetectUniqueKey,
         InferColumnTypes, MissingIndexes, StatisticsHealth, IndexUsage, TopQueries, DuplicateKeys, CompareBaseline,
         RunQuery, ReportQuery, ScanSourceColumns];

    /// <summary>The operations that run a statement the control plane placed on the queue row: an approved query, or a
    /// query a report declares. Neither is ever queued through the compute task endpoint.</summary>
    public static bool IsQuery(string? operation)
        => operation is RunQuery or ReportQuery;

    /// <summary>The operations the DataOps feature switch gates. Off, the control plane refuses them with a
    /// clear "not enabled" problem instead of queueing a task no policy permits.</summary>
    public static bool IsDataOps(string? operation)
        => operation is DuplicateKeys or CompareBaseline;

    /// <summary>The warehouse-health subset: DMV probes authored in T-SQL, so they require a SQL Server family
    /// source, exactly like <see cref="DetectUniqueKey"/>.</summary>
    public static bool IsWarehouseHealth(string? operation)
        => operation is MissingIndexes or StatisticsHealth or IndexUsage or TopQueries;

    public static bool IsKnown(string? operation)
        => operation is TestConnection or ListDatabases or ListSchemas or ListObjects or SearchObjects
            or IntrospectObject or DetectUniqueKey or InferColumnTypes or ScanSourceColumns
            || IsQuery(operation) || IsWarehouseHealth(operation) || IsDataOps(operation);

    /// <summary>The longest name an operation a host module registers may have.</summary>
    public const int MaxRegisteredNameLength = 32;

    /// <summary>
    /// Whether <paramref name="name"/> can name an operation a host module registers: 1 to
    /// <see cref="MaxRegisteredNameLength"/> ASCII letters, digits and '-', starting with a lowercase letter (the camelCase
    /// the built-in names use), and not a built-in name in any casing, so a registration can never shadow one.
    /// </summary>
    public static bool IsValidRegisteredName(string? name)
        => name is { Length: > 0 and <= MaxRegisteredNameLength }
           && char.IsAsciiLetterLower(name[0])
           && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
           && !All.Contains(name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The complete, self-contained description of one queued compute task: the operation, the connection
/// REFERENCE it runs against (never a secret; the executing node resolves it from its own environment, exactly
/// like a flow run), and the operation's arguments. This is the one contract shared by the control-plane
/// endpoint (which validates it at the trust boundary and serializes it onto the queue row) and the worker
/// (which deserializes and executes it), so the two ends can never drift.
/// </summary>
public sealed record ComputeTaskPayload
{
    /// <summary>The queue rows carry this payload as compact JSON; camelCase with enum names, matching the
    /// product's other JSON surfaces (run.json, DefinitionJson).</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>One of <see cref="ComputeOperations"/>.</summary>
    public required string Operation { get; init; }

    /// <summary>The connection reference to resolve on the executing node: a whole <c>${env:...}</c> /
    /// <c>${keyvault:...}</c> reference or an <c>@alias</c>. Inline connection strings are deliberately not
    /// accepted on this ad-hoc path (see <see cref="Validate()"/>): the estate's flows declare them through
    /// reviewed git, not through an interactive API.</summary>
    public required string SourceRef { get; init; }

    /// <summary>The provider of the reference (MSSQL / AZDB / MySQL / PostgreSQL / Oracle). Null defaults to
    /// SQL Server for a <c>${...}</c> reference; an <c>@alias</c> always takes its kind from the registry.</summary>
    public DataSourceKind? ProviderKind { get; init; }

    /// <summary>The database scope; null means the connection's current database.</summary>
    public string? Database { get; init; }

    /// <summary>The schema scope (listObjects) or the object's schema (introspectObject / detectUniqueKey).</summary>
    public string? Schema { get; init; }

    /// <summary>The object name for introspectObject / detectUniqueKey.</summary>
    public string? ObjectName { get; init; }

    /// <summary>A name filter for listing operations (parameterized LIKE; wildcards escaped by the reader).</summary>
    public string? NameLike { get; init; }

    /// <summary>The search text for searchObjects.</summary>
    public string? SearchTerm { get; init; }

    public bool IncludeTables { get; init; } = true;

    public bool IncludeViews { get; init; } = true;

    public bool IncludeSystem { get; init; }

    public int Offset { get; init; }

    public int Limit { get; init; } = 200;

    /// <summary>detectUniqueKey sampling: null auto-samples large tables, 0 forces a full scan, a positive
    /// value sets an explicit sample size.</summary>
    public int? SampleSize { get; init; }

    /// <summary>detectUniqueKey: the widest composite key to consider.</summary>
    public int MaxKeyColumns { get; init; } = 4;

    /// <summary>detectUniqueKey: how many candidates to report.</summary>
    public int MaxCandidates { get; init; } = 5;

    /// <summary>detectUniqueKey: confirm sampled candidates against the whole table.</summary>
    public bool VerifyCandidates { get; init; } = true;

    /// <summary>detectUniqueKey: answer from an enforced unique index/constraint without reading rows.</summary>
    public bool TrustDeclaredKeys { get; init; } = true;

    /// <summary>runQuery: the approved statement. It is placed here by the control plane when a plan token is
    /// redeemed, never by a client: the request that starts a run carries a token, not SQL.</summary>
    public string? Sql { get; init; }

    /// <summary>runQuery: the most rows the result carries before it is marked truncated.</summary>
    public int? MaxRows { get; init; }

    /// <summary>runQuery: the command timeout.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>reportQuery: the values the statement's <c>@name</c> parameters take, each with its declared type. Null
    /// when the statement binds none, so a queue row reads back equal to the payload that wrote it.</summary>
    public IReadOnlyList<QueryParameter>? Parameters { get; init; }

    /// <summary>reportQuery: the name of the report the statement is from, so the task history says which report was
    /// read, by whom and when.</summary>
    public string? Report { get; init; }

    /// <summary>reportQuery: the name of the report's query the statement is.</summary>
    public string? ReportQuery { get; init; }

    /// <summary>The longest report name and query name a task carries.</summary>
    public const int MaxReportNameLength = 400;

    /// <summary>duplicateKeys: the columns that identify one real row. Left empty the check reads the key
    /// the TABLE declares; this is where the answer to a report's <c>question</c> comes back when the table
    /// declares no usable key. inferColumnTypes: the columns to profile; left empty, every column.
    /// scanSourceColumns: the columns the caller asks about, each answered with the files that carry it.</summary>
    public IReadOnlyList<string>? Columns { get; init; }

    /// <summary>scanSourceColumns: the flow's document as the catalog stores it for the pipeline, the text a run of the
    /// pipeline executes. Placed here by the control plane, never by a client.</summary>
    public string? Flow { get; init; }

    /// <summary>scanSourceColumns: the path of the flow's file in its repository's checkout, which a location relative to
    /// the flow's own folder resolves against, as a run of the flow from that checkout resolves it.</summary>
    public string? FlowFile { get; init; }

    /// <summary>scanSourceColumns: how many of the newest files the scan reads.</summary>
    public int? Files { get; init; }

    /// <summary>scanSourceColumns: the table the flow loads into as the catalog holds it, which the files' columns are set
    /// against; null when the catalog holds no columns of it.</summary>
    public SourceScanTarget? ScanTarget { get; init; }

    /// <summary>scanSourceColumns: how many of the newest files a scan reads when the request does not say.</summary>
    public const int DefaultScanFiles = 20;

    /// <summary>scanSourceColumns: the most files one scan reads.</summary>
    public const int MaxScanFiles = 200;

    /// <summary>scanSourceColumns: the most columns one scan is asked about.</summary>
    public const int MaxScanExpected = 200;

    /// <summary>scanSourceColumns: the longest flow document a task carries.</summary>
    public const int MaxFlowLength = 1_000_000;

    /// <summary>scanSourceColumns: the most columns of the target table a task carries.</summary>
    public const int MaxScanTargetColumns = 4096;

    /// <summary>scanSourceColumns: the longest flow file path and the longest column name a task carries.</summary>
    public const int MaxScanNameLength = 1024;

    /// <summary>compareBaseline: what to compare (inventory, schema, or data).</summary>
    public BaselineCompareMode? CompareMode { get; init; }

    /// <summary>compareBaseline: the linked server reaching the OLD estate. Allowlisted by the control plane's
    /// configuration at enqueue; the node re-runs every shape and fragment check but takes the allowlist
    /// decision as already made, exactly as a queued run takes its connection reference as already reviewed.</summary>
    public string? LinkedServer { get; init; }

    /// <summary>compareBaseline: the database on the linked server.</summary>
    public string? BaselineDatabase { get; init; }

    /// <summary>compareBaseline: the schema on the old side when it differs from <see cref="Schema"/>.</summary>
    public string? BaselineSchema { get; init; }

    /// <summary>compareBaseline: the object on the old side when the name differs (the ported table is
    /// routinely renamed to match its V3 source, with a compatibility view keeping the old name).</summary>
    public string? BaselineObjectName { get; init; }

    /// <summary>compareBaseline, data mode: the LOGICAL key identifying one real-world reading on BOTH
    /// estates. Not the surrogate key, which each estate assigns independently.</summary>
    public IReadOnlyList<string>? KeyExpressions { get; init; }

    /// <summary>compareBaseline, data mode: the columns compared for value parity; empty means every column
    /// except the identity, the bare key columns, and anything matching the exclusion pattern.</summary>
    public IReadOnlyList<string>? CompareColumns { get; init; }

    /// <summary>compareBaseline, data mode: a LIKE pattern for columns excluded from value parity by default,
    /// so the provenance and audit columns do not report a mismatch on every row.</summary>
    public string? ExcludeColumnPattern { get; init; }

    /// <summary>compareBaseline, data mode: a predicate applied to BOTH sides, without the WHERE keyword.</summary>
    public string? Where { get; init; }

    /// <summary>compareBaseline, data mode: how many example keys each anti-join direction carries.</summary>
    public int SampleRows { get; init; } = 5;

    /// <summary>The arguments of an operation a host module registered (<c>IComputeOperation</c>), all strings so the
    /// contract stays JSON-stable across versions; what they mean is the operation's to validate. Built-in operations take
    /// their typed fields instead and refuse arguments. Null when the task carries none, so a built-in operation's queue
    /// row reads back equal to the payload that wrote it.</summary>
    public IReadOnlyDictionary<string, string>? Arguments { get; init; }

    /// <summary>The most <see cref="Arguments"/> one task carries.</summary>
    public const int MaxArguments = 32;

    /// <summary>The values the control plane supplies for the task's <c>${env:NAME}</c> references
    /// (<see cref="SuppliedReferences"/>), resolved ahead of the executing node's environment exactly as a run's are, so a
    /// task reaches the same connections the estate's runs do. Null when nothing is supplied, so a task's queue row reads
    /// back equal to the payload that wrote it.</summary>
    public IReadOnlyDictionary<string, string>? References { get; init; }

    /// <summary>The longest argument name.</summary>
    public const int MaxArgumentNameLength = 64;

    /// <summary>The longest argument value.</summary>
    public const int MaxArgumentLength = 4000;

    /// <summary>The argument named <paramref name="name"/>, or null when it is absent or blank.</summary>
    public string? Argument(string name)
        => Arguments is not null && Arguments.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>The argument named <paramref name="name"/>; a <see cref="SqlFlowException"/> naming the operation and the
    /// argument when it is absent or blank.</summary>
    public string RequireArgument(string name)
        => Argument(name) ?? throw new SqlFlowException($"The '{Operation}' operation requires the '{name}' argument.");

    /// <summary>The widest page a task may request; larger asks are a request error, not a silent clamp, so the
    /// caller learns the real bound.</summary>
    public const int MaxLimit = 1000;

    public const int MaxSourceRefLength = 512;

    public const int MaxIdentifierLength = 256;

    public const int MaxSearchTermLength = 256;

    /// <summary>The largest explicit detectUniqueKey sample (rows). Above this the measurement cost stops being
    /// an interactive ask; the auto-sample (null) already handles huge tables.</summary>
    public const int MaxSampleSize = 10_000_000;

    /// <summary>Serializes the payload to the compact JSON stored on the queue row.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Deserializes and validates a queue row's payload. Throws <see cref="SqlFlowException"/> when the
    /// JSON is malformed or the payload is invalid: a queue row is data from the database, so the worker treats
    /// it as a trust boundary rather than assuming the enqueuer validated it.</summary>
    public static ComputeTaskPayload FromJson(string json) => FromJson(json, registeredOperations: null);

    /// <summary>Deserializes and validates a queue row's payload, accepting the operations a host module registered
    /// (<paramref name="registeredOperations"/>) beside the built-in ones.</summary>
    public static ComputeTaskPayload FromJson(string json, IReadOnlyCollection<string>? registeredOperations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ComputeTaskPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ComputeTaskPayload>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new SqlFlowException($"The compute task's arguments are not valid JSON: {ex.Message}", ex);
        }

        if (payload is null)
        {
            throw new SqlFlowException("The compute task's arguments deserialized to nothing.");
        }

        payload.Validate(registeredOperations);
        return payload;
    }

    /// <summary>
    /// Validates the payload for its operation. Called at BOTH ends: the control-plane endpoint (so a bad ask
    /// is a 400, never a queued task doomed to fail) and the worker on deserialization (the queue row is data
    /// from the database). Throws <see cref="SqlFlowException"/> with the precise field named.
    /// </summary>
    public void Validate() => Validate(registeredOperations: null);

    /// <summary>
    /// Validates the payload, accepting the operations a host module registered (<paramref name="registeredOperations"/>)
    /// beside the built-in ones. A registered operation's payload is checked as a transport contract only: a bounded
    /// target reference and bounded arguments, their meaning being the operation's to validate. A built-in operation's
    /// payload is checked field by field and takes no arguments.
    /// </summary>
    public void Validate(IReadOnlyCollection<string>? registeredOperations)
    {
        if (References is { Count: > 0 })
        {
            SuppliedReferences.Validate(References, "the task's references");
        }

        if (registeredOperations is not null && registeredOperations.Contains(Operation, StringComparer.Ordinal))
        {
            ValidateRegistered();
            return;
        }

        if (!ComputeOperations.IsKnown(Operation))
        {
            var valid = registeredOperations is { Count: > 0 }
                ? ComputeOperations.All.Concat(registeredOperations.Order(StringComparer.Ordinal))
                : ComputeOperations.All;
            throw new SqlFlowException(
                $"Unknown compute operation '{Operation}'. Valid operations: {string.Join(", ", valid)}.");
        }

        if (Arguments is { Count: > 0 })
        {
            throw new SqlFlowException(
                $"The built-in '{Operation}' operation takes its typed fields, not arguments; arguments belong to an operation a module registered.");
        }

        if (string.IsNullOrWhiteSpace(SourceRef))
        {
            throw new SqlFlowException("A compute task requires a non-blank source reference.");
        }

        if (SourceRef.Length > MaxSourceRefLength)
        {
            throw new SqlFlowException($"The source reference is longer than {MaxSourceRefLength} characters.");
        }

        if (Operation == ComputeOperations.ScanSourceColumns)
        {
            // A scan names the flow whose source it reads; what it reads is the location the flow's own document declares.
            if (HasControlCharacters(SourceRef))
            {
                throw new SqlFlowException("scanSourceColumns names its flow with no control characters.");
            }
        }
        else if (!IsWholeReference(SourceRef))
        {
            throw new SqlFlowException(
                "The source must be a whole ${env:...} / ${keyvault:...} reference or an @alias. Inline " +
                "connection strings are not accepted on the ad-hoc compute path; declare the connection as a " +
                "reference (the estate's flows resolve it the same way).");
        }

        ValidateIdentifier(Database, nameof(Database));
        ValidateIdentifier(Schema, nameof(Schema));
        ValidateIdentifier(ObjectName, nameof(ObjectName));
        ValidateIdentifier(NameLike, nameof(NameLike));

        if (Offset < 0)
        {
            throw new SqlFlowException("offset must be zero or positive.");
        }

        if (Limit is < 1 or > MaxLimit)
        {
            throw new SqlFlowException($"limit must be between 1 and {MaxLimit}.");
        }

        switch (Operation)
        {
            case ComputeOperations.SearchObjects:
                if (string.IsNullOrWhiteSpace(SearchTerm))
                {
                    throw new SqlFlowException("searchObjects requires a non-blank searchTerm.");
                }

                if (SearchTerm.Length > MaxSearchTermLength || HasControlCharacters(SearchTerm))
                {
                    throw new SqlFlowException(
                        $"searchTerm must be at most {MaxSearchTermLength} characters with no control characters.");
                }

                break;

            case ComputeOperations.ListObjects:
                if (!IncludeTables && !IncludeViews)
                {
                    throw new SqlFlowException("listObjects with both tables and views excluded can never return anything.");
                }

                break;

            case ComputeOperations.IntrospectObject or ComputeOperations.DetectUniqueKey or ComputeOperations.InferColumnTypes:
                if (string.IsNullOrWhiteSpace(Schema) || string.IsNullOrWhiteSpace(ObjectName))
                {
                    throw new SqlFlowException($"{Operation} requires both schema and objectName.");
                }

                break;

            case ComputeOperations.DuplicateKeys:
                // Projecting the request is itself the validation, so there is one rule set rather than a copy.
                ToDuplicateKeyRequest();
                break;

            case ComputeOperations.CompareBaseline:
                ToComparisonRequest(LinkedServer is null ? [] : [LinkedServer]);
                break;

            case ComputeOperations.RunQuery or ComputeOperations.ReportQuery:
                ToQueryRunRequest();
                break;
        }

        if (Parameters is not null && Operation != ComputeOperations.ReportQuery)
        {
            throw new SqlFlowException(
                $"Only the {ComputeOperations.ReportQuery} operation binds parameters; '{Operation}' takes none.");
        }

        if ((Report is not null || ReportQuery is not null) && Operation != ComputeOperations.ReportQuery)
        {
            throw new SqlFlowException(
                $"Only the {ComputeOperations.ReportQuery} operation names a report and a query; '{Operation}' names none.");
        }

        if (Operation == ComputeOperations.ScanSourceColumns)
        {
            ValidateScan();
        }
        else if (Flow is not null || FlowFile is not null || Files is not null || ScanTarget is not null)
        {
            throw new SqlFlowException(
                $"Only the {ComputeOperations.ScanSourceColumns} operation carries a flow document; '{Operation}' carries none.");
        }

        foreach (var (name, value) in new[] { ("report", Report), ("reportQuery", ReportQuery) })
        {
            if (value is not null && (value.Length is 0 or > MaxReportNameLength || HasControlCharacters(value)))
            {
                throw new SqlFlowException(
                    $"{name} must be a name of 1 to {MaxReportNameLength} characters with no control characters.");
            }
        }

        if (ComputeOperations.IsWarehouseHealth(Operation))
        {
            // The probes are T-SQL over SQL Server DMVs. An @alias resolves its kind on the node; the executor
            // re-checks the RESOLVED kind there, so a MySQL alias still fails precisely.
            if (ProviderKind is DataSourceKind.MySQL or DataSourceKind.PostgreSQL or DataSourceKind.Oracle)
            {
                throw new SqlFlowException(
                    $"{Operation} reads SQL Server dynamic management views; the source must be SQL Server or " +
                    "Azure SQL (kind mssql or azdb).");
            }
        }

        if (Operation == ComputeOperations.DetectUniqueKey)
        {
            // The profiling is T-SQL, so only SQL Server family sources qualify. An @alias resolves its kind on
            // the node; the executor re-checks the RESOLVED kind there, so a MySQL alias still fails precisely.
            if (ProviderKind is DataSourceKind.MySQL or DataSourceKind.PostgreSQL or DataSourceKind.Oracle)
            {
                throw new SqlFlowException(
                    "detectUniqueKey profiles with T-SQL; the source must be SQL Server or Azure SQL (kind mssql or azdb).");
            }

            if (SampleSize is < 0 or (> 0 and < 1000) or > MaxSampleSize)
            {
                throw new SqlFlowException(
                    $"sampleSize must be 0 (full scan), omitted (auto), or between 1000 and {MaxSampleSize}.");
            }

            if (MaxKeyColumns is < 1 or > 8)
            {
                throw new SqlFlowException("maxKeyColumns must be between 1 and 8.");
            }

            if (MaxCandidates is < 1 or > 20)
            {
                throw new SqlFlowException("maxCandidates must be between 1 and 20.");
            }
        }

        if (Operation == ComputeOperations.InferColumnTypes)
        {
            // The profile is SQL Server's own TRY_CONVERT, as a load's inference is. An @alias resolves its kind on the
            // node, where the executor checks the RESOLVED kind again.
            if (ProviderKind is DataSourceKind.MySQL or DataSourceKind.PostgreSQL or DataSourceKind.Oracle)
            {
                throw new SqlFlowException(
                    "inferColumnTypes profiles with T-SQL; the source must be SQL Server or Azure SQL (kind mssql or azdb).");
            }

            if (SampleSize is < 0 or (> 0 and < 1000) or > MaxSampleSize)
            {
                throw new SqlFlowException(
                    $"sampleSize must be 0 (every row), omitted ({DefaultInferenceSample} rows), or between 1000 and {MaxSampleSize}.");
            }

            if (Columns is { Count: > MaxInferredColumns })
            {
                throw new SqlFlowException($"inferColumnTypes profiles at most {MaxInferredColumns} named columns at once.");
            }

            foreach (var column in Columns ?? [])
            {
                if (string.IsNullOrWhiteSpace(column))
                {
                    throw new SqlFlowException("A column inferColumnTypes is asked to profile has no name.");
                }

                ValidateIdentifier(column, "columns");
            }
        }
    }

    /// <summary>
    /// scanSourceColumns: the flow's document is present and bounded (the control plane placed it; the node parses it as
    /// a run would), the file path and the count are in range, and the columns asked about and the target's columns are
    /// bounded names.
    /// </summary>
    private void ValidateScan()
    {
        if (string.IsNullOrWhiteSpace(Flow))
        {
            throw new SqlFlowException(
                "scanSourceColumns carries the flow's document as the catalog stores it. A client does not send a document " +
                "or a location here: it names the pipeline (POST /pipelines/{id}/source-columns), and the control plane " +
                "places the pipeline's document in this payload.");
        }

        if (Flow.Length > MaxFlowLength)
        {
            throw new SqlFlowException($"The flow document of a scanSourceColumns task is longer than {MaxFlowLength} characters.");
        }

        if (FlowFile is not null && (FlowFile.Length is 0 or > MaxScanNameLength || HasControlCharacters(FlowFile)))
        {
            throw new SqlFlowException($"flowFile must be a path of 1 to {MaxScanNameLength} characters with no control characters.");
        }

        if (Files is < 1 or > MaxScanFiles)
        {
            throw new SqlFlowException($"files must be between 1 and {MaxScanFiles}.");
        }

        if (Columns is { Count: > MaxScanExpected })
        {
            throw new SqlFlowException($"A scan is asked about at most {MaxScanExpected} columns at once.");
        }

        foreach (var column in Columns ?? [])
        {
            ValidateScanName(column, "A column a scan is asked about");
        }

        if (ScanTarget is { } target)
        {
            ValidateScanName(target.ObjectKey, "The target's object key");
            ValidateScanName(target.Schema, "The target's schema");
            ValidateScanName(target.Table, "The target's table");
            if (target.Columns is null || target.Columns.Count > MaxScanTargetColumns)
            {
                throw new SqlFlowException($"The target of a scan carries its columns, at most {MaxScanTargetColumns} of them.");
            }

            foreach (var column in target.Columns)
            {
                ValidateScanName(column, "A column of the target");
            }
        }
    }

    private static void ValidateScanName(string? value, string what)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxScanNameLength || HasControlCharacters(value))
        {
            throw new SqlFlowException($"{what} must be a name of 1 to {MaxScanNameLength} characters with no control characters.");
        }
    }

    /// <summary>inferColumnTypes: how many rows a profile reads when the task does not say: enough for the counts to stand
    /// for a landing table's values, few enough that one ask stays an interactive read.</summary>
    public const int DefaultInferenceSample = 100_000;

    /// <summary>inferColumnTypes: the most columns one task names.</summary>
    public const int MaxInferredColumns = 500;

    /// <summary>The transport contract of an operation a host module registered: a non-blank, bounded target reference
    /// free of control characters (what it names is the operation's business: a flow, an object, a reference), and at
    /// most <see cref="MaxArguments"/> arguments with bounded names and values.</summary>
    private void ValidateRegistered()
    {
        if (string.IsNullOrWhiteSpace(SourceRef) || SourceRef.Length > MaxSourceRefLength || HasControlCharacters(SourceRef))
        {
            throw new SqlFlowException(
                $"The '{Operation}' operation requires a target reference of 1 to {MaxSourceRefLength} characters with no control characters.");
        }

        if (Arguments is null)
        {
            return;
        }

        if (Arguments.Count > MaxArguments)
        {
            throw new SqlFlowException($"The '{Operation}' operation takes at most {MaxArguments} arguments.");
        }

        foreach (var (name, value) in Arguments)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > MaxArgumentNameLength || HasControlCharacters(name))
            {
                throw new SqlFlowException(
                    $"Every argument of the '{Operation}' operation needs a name of 1 to {MaxArgumentNameLength} characters with no control characters.");
            }

            if (value is null || value.Length > MaxArgumentLength)
            {
                throw new SqlFlowException(
                    $"Argument '{name}' of the '{Operation}' operation must be a value of at most {MaxArgumentLength} characters.");
            }
        }
    }

    /// <summary>
    /// Projects the payload onto the duplicate-key contract and validates it. The projection IS the
    /// validation, so the control plane and the node cannot disagree about what the check accepts. Throws
    /// <see cref="SqlFlowException"/> naming the offending field.
    /// </summary>
    public DuplicateKeyRequest ToDuplicateKeyRequest()
    {
        if (Operation != ComputeOperations.DuplicateKeys)
        {
            throw new SqlFlowException(
                $"Only the {ComputeOperations.DuplicateKeys} operation carries a duplicate-key request.");
        }

        if (string.IsNullOrWhiteSpace(Schema) || string.IsNullOrWhiteSpace(ObjectName))
        {
            throw new SqlFlowException(
                $"{ComputeOperations.DuplicateKeys} checks one table, so both schema and objectName are required.");
        }

        return new DuplicateKeyRequest
        {
            Schema = Schema.Trim(),
            ObjectName = ObjectName.Trim(),
            Database = NullIfBlank(Database),
            Columns = Columns ?? [],
            Limit = Limit,
        }.Validate(ProviderKind);
    }

    /// <summary>
    /// Projects the payload onto the query-run contract and validates its bounds. The STATEMENT is validated
    /// separately by the provider's read-only guard, which parses T-SQL; this checks only what Core can.
    /// </summary>
    public QueryRunRequest ToQueryRunRequest()
    {
        if (!ComputeOperations.IsQuery(Operation))
        {
            throw new SqlFlowException(
                $"Only the {ComputeOperations.RunQuery} and {ComputeOperations.ReportQuery} operations carry a query.");
        }

        var report = Operation == ComputeOperations.ReportQuery;
        if (string.IsNullOrWhiteSpace(Sql))
        {
            throw new SqlFlowException(report
                ? "reportQuery carries the statement a report declares. A client does not send SQL here: it names " +
                  "the report and the query, and the control plane places the document's statement in this payload."
                : "runQuery carries the approved statement. A client does not send SQL here: it prepares a query, " +
                  "a person approves it, and the control plane redeems the token into this payload.");
        }

        return new QueryRunRequest
        {
            Sql = Sql,
            Database = NullIfBlank(Database),
            MaxRows = MaxRows ?? QueryRunRequest.DefaultMaxRows,
            TimeoutSeconds = TimeoutSeconds ?? QueryRunRequest.DefaultTimeoutSeconds,
            Parameters = Parameters ?? [],

            // What a report's document declares may read the server's own catalog; an approved query may not. The
            // operation decides, never a field a payload could set for itself.
            Declared = report,
        }.Validate();
    }

    /// <summary>
    /// Projects the payload onto the comparison contract and validates it, returning the normalized request.
    /// </summary>
    /// <param name="allowedLinkedServers">
    /// The linked servers policy permits. The control plane passes its configured allowlist; the node passes
    /// the payload's own linked server, because the allowlist decision was made and recorded at enqueue while
    /// every shape and fragment check still has to run again on the queue row.
    /// </param>
    public BaselineComparisonRequest ToComparisonRequest(IReadOnlyCollection<string> allowedLinkedServers)
    {
        if (Operation != ComputeOperations.CompareBaseline)
        {
            throw new SqlFlowException(
                $"Only the {ComputeOperations.CompareBaseline} operation carries a comparison request.");
        }

        if (CompareMode is not { } mode)
        {
            throw new SqlFlowException(
                $"{ComputeOperations.CompareBaseline} requires a compareMode: inventory, schema, or data.");
        }

        if (string.IsNullOrWhiteSpace(LinkedServer))
        {
            throw new SqlFlowException(
                "compareBaseline reaches the old estate through a linked server; linkedServer is required.");
        }

        if (string.IsNullOrWhiteSpace(BaselineDatabase))
        {
            throw new SqlFlowException("compareBaseline requires baselineDatabase, the database on the linked server.");
        }

        if (string.IsNullOrWhiteSpace(Schema))
        {
            throw new SqlFlowException("compareBaseline requires schema, the schema on the current estate.");
        }

        var request = new BaselineComparisonRequest
        {
            Mode = mode,
            LinkedServer = LinkedServer.Trim(),
            BaselineDatabase = BaselineDatabase.Trim(),
            Schema = Schema.Trim(),
            ObjectName = NullIfBlank(ObjectName),
            BaselineSchema = NullIfBlank(BaselineSchema),
            BaselineObjectName = NullIfBlank(BaselineObjectName),
            KeyExpressions = KeyExpressions ?? [],
            CompareColumns = CompareColumns ?? [],
            ExcludeColumnPattern = string.IsNullOrWhiteSpace(ExcludeColumnPattern)
                ? BaselineComparisonRequest.DefaultExcludeColumnPattern
                : ExcludeColumnPattern,
            Where = NullIfBlank(Where),
            Limit = Limit,
            SampleRows = SampleRows,
        };

        return request.Validate(allowedLinkedServers);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// True when the value IS a reference in its entirety: one <c>${...}</c> token, or one <c>@alias</c> token.
    /// The same shape rule the lineage server-identity uses: a hybrid like <c>"${env:HOST};Password=..."</c> is
    /// a literal, not a reference, and is refused here.
    /// </summary>
    public static bool IsWholeReference(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith('}')
            && trimmed.IndexOf('}', StringComparison.Ordinal) == trimmed.Length - 1)
        {
            return true;
        }

        return trimmed.StartsWith('@') && trimmed.Length > 1
            && !trimmed.Any(char.IsWhiteSpace)
            && !trimmed.Contains(';', StringComparison.Ordinal)
            && !trimmed.Contains('=', StringComparison.Ordinal);
    }

    private static void ValidateIdentifier(string? value, string field)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length > MaxIdentifierLength)
        {
            throw new SqlFlowException($"{field} is longer than {MaxIdentifierLength} characters.");
        }

        if (HasControlCharacters(value))
        {
            throw new SqlFlowException($"{field} contains control characters.");
        }
    }

    private static bool HasControlCharacters(string value) => value.Any(char.IsControl);
}
