using SqlFlow.Delivery.Engine.Search;

namespace SqlFlow.Delivery.Model;

/// <summary>
/// A retrieval flow (<c>flowType: retrieval</c>, osdu/docs/reference/flow/retrieval.md): the reverse direction. OSDU's
/// search index is paged through a cursor, kind by kind, and every hit (or, when the flow asks for it, the full record
/// read back from storage) streams into JSON Lines files on the lake, rolled by record count, with a manifest per run
/// and a ledger row per run. An incremental flow carries a watermark on a record timestamp field from run to run.
/// </summary>
public sealed record RetrievalDefinition
{
    public const string FlowTypeName = "retrieval";

    /// <summary>Path of the file the flow was loaded from, for error messages. Null for inline documents.</summary>
    public string? SourcePath { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>The platform batch the flow belongs to, from the envelope.</summary>
    public string? Batch { get; init; }

    /// <summary>
    /// The flow's operation code (<c>telemetry.operation</c>), which its runs' correlation id says what ran with
    /// (osdu/docs/reference/concepts/request-correlation.md); the kind's own when the flow names none.
    /// </summary>
    public string TelemetryOperation { get; init; } = FlowTelemetry.Retrieval;

    /// <summary>Stable id derived from <see cref="Name"/>, the same way a delivery flow's is.</summary>
    public Guid Id => Identity.FlowId.Of(Name);

    public IReadOnlyDictionary<string, FlowParameter> Parameters { get; init; } = new Dictionary<string, FlowParameter>(StringComparer.Ordinal);

    public required RetrievalSource Source { get; init; }

    public required RetrievalTarget Target { get; init; }

    public FlowReliability Reliability { get; init; } = new();

    /// <summary>How many kinds are retrieved at once (<c>parallelism.recordsAtOnce</c>).</summary>
    public RunParallelism Parallelism { get; init; } = new();

    /// <summary>The secret references the source declares, keyed by document path; references only, never values.</summary>
    public IEnumerable<KeyValuePair<string, string>> CredentialReferences()
    {
        if (Source.Auth.SecretRef is { } secret)
        {
            yield return new("source.auth.secretRef", secret);
        }

        if (Source.Auth.SecondarySecretRef is { } secondary)
        {
            yield return new("source.auth.secondarySecretRef", secondary);
        }

        if (IsReference(Source.Endpoint))
        {
            yield return new("source.endpoint", Source.Endpoint);
        }

        foreach (var (name, value) in Source.Headers.Where(kv => IsReference(kv.Value)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            yield return new($"source.headers.{name}", value);
        }

        if (Source.Auth.Token is { } token)
        {
            foreach (var (name, value) in token.Body.Where(kv => IsReference(kv.Value)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                yield return new($"source.auth.token.body.{name}", value);
            }
        }
    }

    private static bool IsReference(string value) => value.Contains("${", StringComparison.Ordinal);
}

/// <summary>The OSDU side of a retrieval: the platform, the kinds and query, and how the index is paged.</summary>
public sealed record RetrievalSource
{
    /// <summary>Records per search page; the search service caps a page at 1000.</summary>
    public const int MaxPageSize = 1000;

    /// <summary>Records per storage read-back request (openapi storage v2, MultiRecordIds takes at most 100).</summary>
    public const int FetchBatch = 100;

    public const int MaxFetchParallelism = 64;

    public const string DefaultSearchPath = "/api/search/v2/query_with_cursor";
    public const string DefaultQueryPath = "/api/search/v2/query";
    public const string DefaultRecordQueryPath = "/api/storage/v2/query/records";

    /// <summary>The platform base URL; ${env:NAME} and ${keyvault:NAME} references allowed.</summary>
    public required string Endpoint { get; init; }

    public TargetAuth Auth { get; init; } = new() { Type = TargetAuthType.None };

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The kinds to retrieve, each {authority}:{source}:{entityType}:{version} with wildcards per segment; each kind is one cursor.</summary>
    public required IReadOnlyList<string> Kinds { get; init; }

    /// <summary>A Lucene query narrowing the kinds, with {parameter} tokens substituted. Null takes every record of the kinds.</summary>
    public string? Query { get; init; }

    /// <summary>The fields to project the hits on; empty returns whole hits.</summary>
    public IReadOnlyList<string> ReturnedFields { get; init; } = [];

    public int PageSize { get; init; } = MaxPageSize;

    /// <summary>The cursor search path (openapi search v2, POST /query_with_cursor).</summary>
    public string SearchPath { get; init; } = DefaultSearchPath;

    /// <summary>
    /// How a read of every record a search matches pages through them (<c>source.paging</c>): through the search cursor, the
    /// default, or in createTime order through the plain search, which holds nothing on the service between pages.
    /// </summary>
    public SearchPaging Paging { get; init; } = SearchPaging.Cursor;

    /// <summary>The offset search path the plan operation counts with (openapi search v2, POST /query, trackTotalCount).</summary>
    public string QueryPath { get; init; } = DefaultQueryPath;

    /// <summary>The watermark field and lag of an incremental flow; null retrieves everything the query matches on every run.</summary>
    public RetrievalIncremental? Incremental { get; init; }

    /// <summary>Read every hit's full record back from storage (the index holds a projection) before writing it.</summary>
    public bool FetchRecords { get; init; }

    /// <summary>The storage path that reads records back by id (openapi storage v2, POST /query/records).</summary>
    public string RecordQueryPath { get; init; } = DefaultRecordQueryPath;

    /// <summary>Concurrent storage read-back requests per page when <see cref="FetchRecords"/> is set.</summary>
    public int FetchParallelism { get; init; } = 4;
}

/// <summary>
/// The watermark of an incremental retrieval: a run takes the records whose <see cref="Field"/> falls in
/// <c>[last run's upper bound, now minus the lag)</c>; the lag keeps records the indexer has not caught up with
/// for the next run instead of losing them. On <c>modifyTime</c> a record never modified, which OSDU gives no
/// <c>modifyTime</c>, is read by its <c>createTime</c> (<see cref="Engine.Search.RecordChanges"/>).
/// </summary>
public sealed record RetrievalIncremental
{
    public string Field { get; init; } = "modifyTime";

    /// <summary>Where the first run starts (and where a forced run restarts); null takes everything before the upper bound.</summary>
    public DateTime? Since { get; init; }

    public int LagMinutes { get; init; } = 5;
}

/// <summary>The lake side of a retrieval: where the run's files go and how they are cut.</summary>
public sealed record RetrievalTarget
{
    public const string JsonLines = "jsonl";
    public const string NoCompression = "none";
    public const string GzipCompression = "gzip";

    /// <summary>
    /// The run's directory root: a local path or an Azure Storage URI, with {parameter}, {run} and {date} tokens.
    /// Without a {run} token every run gets its own timestamped directory beneath it, so runs never overwrite.
    /// </summary>
    public required string Location { get; init; }

    /// <summary>The file format; JSON Lines is the one an OSDU record (free-form JSON) fits.</summary>
    public string Format { get; init; } = JsonLines;

    /// <summary>none or gzip.</summary>
    public string Compression { get; init; } = NoCompression;

    /// <summary>Records per file before the next one is started.</summary>
    public long RollRecords { get; init; } = 100_000;

    /// <summary>The manifest file name inside the run's directory.</summary>
    public string Manifest { get; init; } = "manifest.json";

    public bool Gzip => Compression.Equals(GzipCompression, StringComparison.OrdinalIgnoreCase);
}
