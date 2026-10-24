using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Engine.Retrieval;

/// <summary>The window an incremental run covers: <c>[From, To)</c> on <see cref="Field"/>; From is null on the first run.</summary>
public sealed record RetrievalWindow(string Field, DateTime? From, DateTime To)
{
    public bool IsEmpty => From is { } from && To <= from;
}

/// <summary>One file a run wrote: its location, the records in it and its uncompressed size.</summary>
public sealed record RetrievedFile(string Path, long Records, long Bytes);

/// <summary>What one kind produced: the query that ran, the records written, the ones storage could not read back, the files.</summary>
public sealed record RetrievedKind(string Kind, string? Query, long Records, long Missing, IReadOnlyList<RetrievedFile> Files);

/// <summary>What one run produced: the run's result artifact and the ledger row's counts.</summary>
public sealed record RetrievalResult(
    long RetrievalId, string Location, string? ManifestLocation, RetrievalWindow? Window, IReadOnlyList<RetrievedKind> Kinds, long Records, int Files, long Bytes, bool NothingToDo);

/// <summary>What a plan run found: how many records the query matches per kind, without writing any.</summary>
public sealed record RetrievalEstimate(string Kind, string? Query, long TotalCount);

/// <summary>
/// The retrieve operation of a retrieval flow (osdu/docs/reference/flow/retrieval.md): one cursor per kind through the
/// search index, each page streamed into rolling JSON Lines files on the lake (through the store's streamed writer,
/// never buffered), optionally with every hit's full record read back from storage in bounded parallel batches. A
/// ledger row opens when the run starts and closes with its counts and outcome; a manifest in the run's directory lists
/// the files, the window and the records storage could not read back. The trace carries one line per kind, per hundred
/// pages and per file, never per record.
/// </summary>
public sealed class RetrievalRunner
{
    /// <summary>One trace line per this many pages (one hundred thousand records at the default page size).</summary>
    public const int ProgressEveryPages = 100;

    /// <summary>How many ids of records storage could not read back the manifest lists per kind.</summary>
    public const int MissingIdsKept = 1000;

    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    private readonly RetrievalDefinition _flow;
    private readonly IReadOnlyDictionary<string, string> _values;
    private readonly OsduHttpClient _client;
    private readonly FileStoreRegistry _stores;
    private readonly ILedger? _ledger;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly OsduSearch _search;
    private readonly StorageRecords _storage;
    private readonly ConcurrentDictionary<string, List<string>> _missingIds = new(StringComparer.Ordinal);

    public RetrievalRunner(RetrievalDefinition flow, IReadOnlyDictionary<string, string> values, OsduHttpClient client, FileStoreRegistry stores, ILedger? ledger, TimeProvider time, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _flow = flow;
        _values = values;
        _client = client;
        _stores = stores;
        _ledger = ledger;
        _time = time;
        _logger = logger;
        _search = new OsduSearch(client, flow.Source.QueryPath, flow.Source.SearchPath, logger, flow.Source.Paging);
        _storage = new StorageRecords(client, flow.Source.RecordQueryPath);
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>The run's directory: the declared location with its tokens substituted, and a timestamped directory per run unless the location names the run itself.</summary>
    public string Location(Guid runId, DateTime startedUtc)
    {
        var location = FlowParameters.Substitute(_flow.Target.Location, _values)
            .Replace("{run}", runId.ToString("N"), StringComparison.Ordinal)
            .Replace("{date}", startedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var directory = _flow.Target.Location.Contains("{run}", StringComparison.Ordinal)
            ? location
            : FileStoreRegistry.Join(location, startedUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + runId.ToString("N")[..8]);

        // A local path is made absolute with the platform's separators, so the manifest and the ledger name files the same way the store does.
        return directory.Contains("://", StringComparison.Ordinal) ? directory : Path.GetFullPath(directory);
    }

    /// <summary>
    /// The window this run covers, for an incremental flow: from the last completed run's upper bound (or the
    /// declared start, which a forced run goes back to) up to now minus the lag. A window never moves backwards: it starts
    /// no earlier than the declared start, and when now minus the lag is not past its start (a lag raised since the last
    /// run, a start in the future) it is empty at its start, which a run that does nothing records as its upper bound, so the
    /// next run starts there again. Null for a flow that is not incremental.
    /// </summary>
    public async Task<RetrievalWindow?> WindowAsync(DateTime startedUtc, bool force, CancellationToken ct)
    {
        if (_flow.Source.Incremental is not { } incremental)
        {
            return null;
        }

        var from = incremental.Since;
        if (!force && _ledger is not null && await _ledger.LastRetrievalAsync(_flow.Id, RetrievalStatus.Done, ct).ConfigureAwait(false) is { WindowTo: { } upper } last)
        {
            // A run that did nothing before windows were held from moving backwards may have recorded an upper bound below its
            // own start; its start is where the window stood.
            var previous = last.WindowFrom is { } lower && lower > upper ? lower : upper;
            if (from is not { } since || previous > since)
            {
                from = previous;
            }
        }

        var to = startedUtc - TimeSpan.FromMinutes(Math.Max(0, incremental.LagMinutes));
        return new RetrievalWindow(incremental.Field, from, from is { } start && to < start ? start : to);
    }

    /// <summary>
    /// The flow's query with its tokens substituted, narrowed to the window when there is one: on <c>modifyTime</c>, a record
    /// never modified is read by its <c>createTime</c> (<see cref="RecordChanges"/>).
    /// </summary>
    public string? ComposeQuery(RetrievalWindow? window)
    {
        var query = string.IsNullOrWhiteSpace(_flow.Source.Query) ? null : FlowParameters.Substitute(_flow.Source.Query, _values).Trim();
        if (window is null)
        {
            return query;
        }

        var range = RecordChanges.Within(window.Field, window.From, window.To);
        return query is null ? range : "(" + query + ") AND " + range;
    }

    /// <summary>The plan operation: what the query matches per kind, from the index's count, writing nothing.</summary>
    public async Task<(RetrievalWindow? Window, IReadOnlyList<RetrievalEstimate> Estimates)> EstimateAsync(bool force, CancellationToken ct)
    {
        var window = await WindowAsync(Now, force, ct).ConfigureAwait(false);
        var query = ComposeQuery(window);
        var estimates = new List<RetrievalEstimate>(_flow.Source.Kinds.Count);
        if (window is { IsEmpty: true })
        {
            foreach (var kind in _flow.Source.Kinds)
            {
                estimates.Add(new RetrievalEstimate(kind, query, 0));
            }

            return (window, estimates);
        }

        foreach (var kind in _flow.Source.Kinds)
        {
            var total = await _search.CountAsync(new OsduSearchQuery { Kind = kind, Query = query }, ct).ConfigureAwait(false);
            estimates.Add(new RetrievalEstimate(kind, query, total));
        }

        return (window, estimates);
    }

    /// <summary>The retrieve operation.</summary>
    public async Task<RetrievalResult> RunAsync(Guid runId, string actor, bool force, CancellationToken ct)
    {
        var started = Now;
        var window = await WindowAsync(started, force, ct).ConfigureAwait(false);
        var location = Location(runId, started);
        var query = ComposeQuery(window);
        var state = new RetrievalState
        {
            FlowId = _flow.Id,
            FlowName = _flow.Name,
            RunId = runId,
            Actor = actor,
            Kinds = string.Join(",", _flow.Source.Kinds),
            Query = query,
            WindowField = window?.Field,
            WindowFrom = window?.From,
            WindowTo = window?.To,
            Location = location,
            Status = RetrievalStatus.Running,
            StartedUtc = started,
        };
        if (_ledger is not null)
        {
            state = await _ledger.StartRetrievalAsync(state, ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "retrieve: {Kinds} into {Location} (page size {PageSize}, roll every {Roll} record(s){Fetch}){Window}",
            state.Kinds, location, _flow.Source.PageSize, _flow.Target.RollRecords, _flow.Source.FetchRecords ? ", full records from storage" : string.Empty, DescribeWindow(window));

        if (window is { IsEmpty: true })
        {
            _logger.LogInformation("retrieve: the window is empty (its start, the last run's upper bound or the declared since, is not behind now minus the lag); nothing to do, and the next run starts there again.");
            await CloseAsync(state, RetrievalStatus.Done, [], null, null).ConfigureAwait(false);
            return new RetrievalResult(state.RetrievalId, location, null, window, [], 0, 0, 0, NothingToDo: true);
        }

        var kinds = new RetrievedKind?[_flow.Source.Kinds.Count];
        try
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(_flow.Parallelism.RecordsAtOnce, 1, 64), CancellationToken = ct };
            await Parallel.ForEachAsync(Enumerable.Range(0, kinds.Length), options, async (i, token) =>
            {
                kinds[i] = await RetrieveKindAsync(_flow.Source.Kinds[i], query, location, token).ConfigureAwait(false);
            }).ConfigureAwait(false);

            var done = kinds.Select(k => k!).ToList();
            var manifest = await WriteManifestAsync(runId, started, window, done, location, ct).ConfigureAwait(false);
            var records = done.Sum(k => k.Records);
            var files = done.Sum(k => k.Files.Count);
            var bytes = done.Sum(k => k.Files.Sum(f => f.Bytes));
            await CloseAsync(state, RetrievalStatus.Done, done, manifest, null).ConfigureAwait(false);
            _logger.LogInformation("retrieve: {Records} record(s) in {Files} file(s), {Bytes} byte(s) uncompressed; manifest {Manifest}", records, files, bytes, manifest);
            return new RetrievalResult(state.RetrievalId, location, manifest, window, done, records, files, bytes, NothingToDo: false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CloseAsync(state, RetrievalStatus.Cancelled, kinds.Where(k => k is not null).Select(k => k!).ToList(), null, "the run was cancelled").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or JsonException or UnauthorizedAccessException)
        {
            await CloseAsync(state, RetrievalStatus.Failed, kinds.Where(k => k is not null).Select(k => k!).ToList(), null, SecretHygiene.RedactedMessage(ex)).ConfigureAwait(false);
            throw;
        }
    }

    private async Task CloseAsync(RetrievalState state, string status, IReadOnlyList<RetrievedKind> done, string? manifest, string? error)
    {
        if (_ledger is null)
        {
            return;
        }

        try
        {
            await _ledger.CompleteRetrievalAsync(
                state.RetrievalId, status, done.Sum(k => k.Records), done.Sum(k => k.Files.Count), done.Sum(k => k.Files.Sum(f => f.Bytes)), manifest, error, Now, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowException or InvalidOperationException || ex.GetType().Name.Contains("DbUpdate", StringComparison.Ordinal))
        {
            // The run's own outcome is what the caller reports; a ledger row left running is reclaimed by the next run's listing.
            _logger.LogError("Could not close retrieval {RetrievalId} as {Status}: {Message}", state.RetrievalId, status, HeaderRedaction.RedactMessage(ex.Message));
        }
    }

    private async Task<RetrievedKind> RetrieveKindAsync(string kind, string? query, string location, CancellationToken ct)
    {
        var sink = new JsonLinesSink(_stores, FileStoreRegistry.Join(location, Slug(kind)), _flow.Target.RollRecords, _flow.Target.Gzip);
        var missing = 0L;
        var pages = 0;
        long records = 0;
        long? total = null;
        var search = new OsduSearchQuery { Kind = kind, Query = query, ReturnedFields = _flow.Source.ReturnedFields };
        await using (sink.ConfigureAwait(false))
        {
            await foreach (var page in _search.PagesAsync(search, _flow.Source.PageSize, ct).ConfigureAwait(false))
            {
                if (pages == 0 && page.TotalCount is { } totalCount)
                {
                    total = totalCount;
                    _logger.LogInformation("retrieve {Kind}: the index reports {Total} matching record(s)", kind, totalCount);
                }

                pages++;
                if (page.Hits.Count > 0)
                {
                    if (_flow.Source.FetchRecords)
                    {
                        var (written, notFound) = await WriteFetchedAsync(kind, page.Hits, sink, ct).ConfigureAwait(false);
                        records += written;
                        missing += notFound;
                    }
                    else
                    {
                        foreach (var hit in page.Hits)
                        {
                            await sink.WriteAsync(hit, ct).ConfigureAwait(false);
                        }

                        records += page.Hits.Count;
                    }
                }

                if (pages % ProgressEveryPages == 0)
                {
                    _logger.LogInformation("retrieve {Kind}: {Records} record(s){Total} after {Pages} page(s), {Files} file(s) so far", kind, records, total is { } t ? $" of {t.ToString(CultureInfo.InvariantCulture)}" : string.Empty, pages, sink.Files.Count + 1);
                }
            }

            await sink.CompleteAsync().ConfigureAwait(false);
        }

        _logger.LogInformation("retrieve {Kind}: {Records} record(s) in {Files} file(s) over {Pages} page(s){Missing}", kind, records, sink.Files.Count, pages, missing > 0 ? $"; {missing.ToString(CultureInfo.InvariantCulture)} could not be read back from storage" : string.Empty);
        return new RetrievedKind(kind, query, records, missing, sink.Files);
    }

    /// <summary>Reads the page's records back from storage, up to a hundred per request and several requests at a time, and writes them in page order.</summary>
    private async Task<(long Written, long NotFound)> WriteFetchedAsync(string kind, IReadOnlyList<JsonElement> hits, JsonLinesSink sink, CancellationToken ct)
    {
        var ids = new List<string>(hits.Count);
        foreach (var hit in hits)
        {
            if (OsduSearch.IdOf(hit) is { Length: > 0 } text)
            {
                ids.Add(text);
            }
        }

        var chunks = ids.Chunk(RetrievalSource.FetchBatch).ToList();
        var fetched = new (List<JsonElement> Records, List<string> NotFound)[chunks.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(_flow.Source.FetchParallelism, 1, RetrievalSource.MaxFetchParallelism), CancellationToken = ct };
        await Parallel.ForEachAsync(Enumerable.Range(0, chunks.Count), options, async (i, token) =>
        {
            fetched[i] = await FetchAsync(chunks[i], token).ConfigureAwait(false);
        }).ConfigureAwait(false);

        long written = 0;
        long notFound = 0;
        foreach (var (records, gone) in fetched)
        {
            foreach (var record in records)
            {
                await sink.WriteAsync(record, ct).ConfigureAwait(false);
                written++;
            }

            notFound += gone.Count;
            if (gone.Count > 0)
            {
                var kept = _missingIds.GetOrAdd(kind, _ => []);
                lock (kept)
                {
                    kept.AddRange(gone.Take(Math.Max(0, MissingIdsKept - kept.Count)));
                }
            }
        }

        return (written, notFound);
    }

    /// <summary>
    /// One storage read-back of a page's ids, in the page's order (openapi storage v2, POST /query/records); the ids storage
    /// asks to retry get one more request.
    /// </summary>
    private async Task<(List<JsonElement> Records, List<string> NotFound)> FetchAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var read = await _storage.ReadAsync(ids, null, ct).ConfigureAwait(false);
        if (read.Invalid.Count > 0)
        {
            // The index found these but storage did not hand them over. Storage names an id under invalidRecords for a
            // record it does not hold (observed on a live M26 service), so the likeliest reading is a record deleted
            // after it was indexed; one this caller may not read is listed the same way. Named so the missing count
            // can be traced to the ids.
            _logger.LogWarning(
                "retrieve: storage did not return {Count} id(s) the search index listed and names them under invalidRecords (deleted since they were indexed, or not readable by this caller); they are counted as missing. First: {Ids}",
                read.Invalid.Count, string.Join(", ", read.Invalid.Take(5)));
        }

        var byId = read.Records.ToDictionary(r => OsduSearch.IdOf(r)!, StringComparer.Ordinal);
        return (ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList(), read.Missing.ToList());
    }

    private async Task<string> WriteManifestAsync(Guid runId, DateTime started, RetrievalWindow? window, IReadOnlyList<RetrievedKind> kinds, string location, CancellationToken ct)
    {
        var manifest = new JsonObject
        {
            ["flow"] = _flow.Name,
            ["flowId"] = _flow.Id.ToString("D"),
            ["runId"] = runId.ToString("D"),
            ["startedUtc"] = started.ToString("O", CultureInfo.InvariantCulture),
            ["completedUtc"] = Now.ToString("O", CultureInfo.InvariantCulture),
            ["endpoint"] = _flow.Source.Endpoint,
            ["format"] = _flow.Target.Format,
            ["compression"] = _flow.Target.Compression,
            ["window"] = window is null
                ? null
                : new JsonObject
                {
                    ["field"] = window.Field,
                    ["from"] = window.From?.ToString("O", CultureInfo.InvariantCulture),
                    ["to"] = window.To.ToString("O", CultureInfo.InvariantCulture),
                },
            ["records"] = kinds.Sum(k => k.Records),
            ["files"] = kinds.Sum(k => k.Files.Count),
            ["bytes"] = kinds.Sum(k => k.Files.Sum(f => f.Bytes)),
        };
        var list = new JsonArray();
        foreach (var kind in kinds)
        {
            var entry = new JsonObject
            {
                ["kind"] = kind.Kind,
                ["query"] = kind.Query,
                ["records"] = kind.Records,
                ["missing"] = kind.Missing,
                ["files"] = new JsonArray(kind.Files.Select(f => (JsonNode?)new JsonObject { ["path"] = f.Path, ["records"] = f.Records, ["bytes"] = f.Bytes }).ToArray()),
            };
            if (_missingIds.TryGetValue(kind.Kind, out var ids) && ids.Count > 0)
            {
                entry["missingIds"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
            }

            list.Add(entry);
        }

        manifest["kinds"] = list;
        var path = FileStoreRegistry.Join(location, _flow.Target.Manifest);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifest.ToJsonString(ManifestJson)));
        await _stores.WriteAsync(path, stream, ct).ConfigureAwait(false);
        return path;
    }

    private static string DescribeWindow(RetrievalWindow? window)
        => window is null ? string.Empty : $"; {window.Field} in [{OsduSearch.LuceneTime(window.From)} TO {OsduSearch.LuceneTime(window.To)})";

    /// <summary>A kind as a directory name: the separators and wildcards that a path cannot carry are replaced.</summary>
    public static string Slug(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var builder = new StringBuilder(kind.Length);
        foreach (var c in kind)
        {
            builder.Append(c switch
            {
                ':' => '_',
                '*' => 'x',
                '/' or '\\' or '?' or '"' or '<' or '>' or '|' => '_',
                _ => char.IsControl(c) ? '_' : c,
            });
        }

        return builder.ToString();
    }
}

/// <summary>
/// Rolling JSON Lines files: one record per line, a new file every <c>rollRecords</c>, each streamed through the
/// store's writer (a local temp-and-move file, or an Azure block blob) with gzip on top when the flow asks for it.
/// The byte counts are the uncompressed line bytes.
/// </summary>
internal sealed class JsonLinesSink : IAsyncDisposable
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private readonly FileStoreRegistry _stores;
    private readonly string _directory;
    private readonly long _rollRecords;
    private readonly bool _gzip;
    private readonly List<RetrievedFile> _files = [];
    private Stream? _stream;
    private string? _path;
    private long _inFile;
    private long _fileBytes;
    private int _index;

    public JsonLinesSink(FileStoreRegistry stores, string directory, long rollRecords, bool gzip)
    {
        _stores = stores;
        _directory = directory;
        _rollRecords = Math.Max(1, rollRecords);
        _gzip = gzip;
    }

    public IReadOnlyList<RetrievedFile> Files => _files;

    public async Task WriteAsync(JsonElement record, CancellationToken ct)
    {
        if (_stream is null || _inFile >= _rollRecords)
        {
            await RollAsync(ct).ConfigureAwait(false);
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        await _stream!.WriteAsync(bytes, ct).ConfigureAwait(false);
        await _stream.WriteAsync(NewLine, ct).ConfigureAwait(false);
        _inFile++;
        _fileBytes += bytes.Length + 1;
    }

    /// <summary>Closes the open file, so the file list is complete.</summary>
    public Task CompleteAsync() => CloseCurrentAsync();

    public async ValueTask DisposeAsync() => await CloseCurrentAsync().ConfigureAwait(false);

    private async Task RollAsync(CancellationToken ct)
    {
        await CloseCurrentAsync().ConfigureAwait(false);
        _index++;
        _path = FileStoreRegistry.Join(_directory, "part-" + _index.ToString("D5", CultureInfo.InvariantCulture) + ".jsonl" + (_gzip ? ".gz" : string.Empty));
        var raw = await _stores.OpenWriteAsync(_path, ct).ConfigureAwait(false);
        _stream = _gzip ? new GZipStream(raw, CompressionLevel.Optimal, leaveOpen: false) : raw;
        _inFile = 0;
        _fileBytes = 0;
    }

    private async Task CloseCurrentAsync()
    {
        if (_stream is null)
        {
            return;
        }

        var stream = _stream;
        _stream = null;
        await stream.FlushAsync().ConfigureAwait(false);
        await stream.DisposeAsync().ConfigureAwait(false);
        _files.Add(new RetrievedFile(_path!, _inFile, _fileBytes));
    }
}
