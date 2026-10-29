using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Inventories;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Engine.Downloads;

/// <summary>What one download's run came to.</summary>
public sealed record DownloadSummary(
    string Download,
    string Kind,
    string Status,
    long? DownloadRunId,
    string? Location,
    string? Rewrite,
    long Listed,
    long Added,
    long Changed,
    long Gone,
    long Returned,
    long Written,
    long Bytes,
    long Missing,
    int Pages,
    long Requests,
    string? Error)
{
    /// <summary>What the run did, in a line.</summary>
    public string Describe() => Status == DownloadRunStatus.Failed
        ? $"{Download}: failed: {Error}"
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{Download}: {Listed} listed ({Added} new, {Changed} changed, {Gone} gone, {Returned} served again); {Written} file(s) written, {Bytes} byte(s){(Missing > 0 ? $", {Missing} not returned by storage" : string.Empty)}{(Rewrite is null ? string.Empty : $", every record written again ({Rewrite})")}, in {Location}");
}

/// <summary>What a run of a download flow came to, download by download.</summary>
public sealed record DownloadOutcome(string Operation, string Flow, string Partition, int Completed, int Failed, IReadOnlyList<DownloadSummary> Downloads)
{
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Operation} of {Completed + Failed} download(s) of '{Flow}' in '{Partition}': {Completed} completed, {Failed} failed. {string.Join(" ", Downloads.Select(d => d.Describe()))}");
}

/// <summary>What a plan found of one download: how it would read, how many records it would list, and where it would write.</summary>
public sealed record DownloadPlanSummary(string Download, string Kind, string? Query, string Read, long? Records, IReadOnlyList<string> Kinds, string? Location, IReadOnlyList<string> Problems);

/// <summary>What a plan of a download flow found, download by download; it reads no record and writes nothing.</summary>
public sealed record DownloadPlanOutcome(string Operation, string Flow, string Partition, IReadOnlyList<DownloadPlanSummary> Downloads);

/// <summary>Every download of a run was taken up and at least one failed: the run ends failed, carrying how each came out.</summary>
public sealed class DownloadRunsFailedException : DeliveryException
{
    public DownloadRunsFailedException(DownloadOutcome outcome)
        : base((outcome ?? throw new ArgumentNullException(nameof(outcome))).Describe())
    {
        Outcome = outcome;
    }

    public DownloadOutcome Outcome { get; }
}

/// <summary>
/// Runs a download flow (osdu/docs/reference/flow/download.md): each download lists every id its kind holds, as an inventory
/// lists them, stages the listing and merges it into the download once whole, then reads from storage every record whose file
/// holds no version, or an older one than listed, a hundred to a request, and writes each to its folder as a JSON file named
/// after its id (<see cref="DownloadFileNames"/>), recording on its row the version the file holds with its size and SHA-256.
/// A run that writes to another folder than the last, or is forced, writes every record OSDU serves again. An id OSDU no
/// longer serves is marked gone and its file left in place. Nothing is written to OSDU. The trace carries a line per download
/// and per ten thousand files, never per record.
/// </summary>
public sealed class DownloadRunner
{
    /// <summary>The ids a run stages in one write.</summary>
    public const int StageChunk = 10_000;

    /// <summary>The records due one pass reads, writes and records in one transaction.</summary>
    public const int WriteChunk = 1_000;

    /// <summary>One trace line per this many files written.</summary>
    public const int ProgressEvery = 10_000;

    /// <summary>How a record is written: indented, so a person reads the file as it is.</summary>
    private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = true };

    private readonly EngineContext _context;
    private readonly DownloadFlowDefinition _flow;
    private readonly IReadOnlyDictionary<string, string> _values;
    private readonly ILogger _log;
    private readonly HttpMessageHandler? _transport;
    private readonly bool _allowLoopback;

    /// <param name="context">The run's engine context.</param>
    /// <param name="flow">The flow bound to the partition the run reads (or a flow whose partition is its header's).</param>
    /// <param name="values">The flow's parameter values, resolved.</param>
    /// <param name="log">The run's log.</param>
    /// <param name="transport">Replaces the built transport (null builds the configured one); it exists for tests.</param>
    /// <param name="allowLoopback">Lets the URL guard accept a loopback endpoint; it exists for tests.</param>
    public DownloadRunner(
        EngineContext context, DownloadFlowDefinition flow, IReadOnlyDictionary<string, string> values, ILogger log,
        HttpMessageHandler? transport = null, bool allowLoopback = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(log);
        _context = context;
        _flow = flow;
        _values = values;
        _log = log;
        _transport = transport;
        _allowLoopback = allowLoopback || EngineContext.LoopbackAllowed;
    }

    private DateTime Now => _context.Time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// The folder a download's files go to in <paramref name="partition"/>: the flow's location with <c>{partition}</c> and its
    /// parameters filled, then the download's name.
    /// </summary>
    /// <exception cref="DeliveryException">The filled location is a relative path.</exception>
    public string Folder(DownloadSpec download, string partition)
    {
        ArgumentNullException.ThrowIfNull(download);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var location = FlowParameters.Substitute(_flow.Target.Location, new Dictionary<string, string>(_values, StringComparer.Ordinal) { [PartitionNames.RunValue] = partition }).Trim();
        if (!DownloadMapper.IsAbsolute(location))
        {
            throw new DeliveryException(
                $"Download flow '{_flow.Name}' writes to '{location}' once its tokens are filled, a relative path, and a run reads its flow from a checkout of its own, so the files would land inside that checkout. Give target.location, or the parameter it opens with, an absolute path or a storage URI.");
        }

        // A local folder is made absolute with the platform's separators, so the ledger names files the way the store writes them.
        var root = location.Contains("://", StringComparison.Ordinal) ? location : Path.GetFullPath(location);
        return FileStoreRegistry.Join(root, download.Name);
    }

    /// <summary>Counts what each download the run selects would list, and says where it would write and what would stop it; reads no record and writes nothing.</summary>
    public async Task<DownloadPlanOutcome> PlanAsync(IReadOnlyCollection<string> names, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(names);
        var partition = await PartitionAsync(ct).ConfigureAwait(false);
        var selected = _flow.Select(names);
        using var http = new HttpRuntime(_flow.Reliability, _context.Secrets, _context.Time, _transport, _allowLoopback, observer: _context.HttpObserver);
        var client = await ProtocolFactory.ClientAsync(http, _flow.Source.Endpoint, _flow.Source.Auth, _flow.Source.Headers, _context.Secrets, ct).ConfigureAwait(false);
        var search = new OsduSearch(client, _flow.Source.QueryPath, _flow.Source.SearchPath, _log, _flow.Source.Paging);
        var summaries = new List<DownloadPlanSummary>(selected.Count);
        foreach (var download in selected)
        {
            var problems = new List<string>();
            long? records = null;
            IReadOnlyList<string> kinds = [];
            string? folder = null;
            var query = Query(download, partition);
            try
            {
                folder = Folder(download, partition);
                records = await search.CountAsync(new OsduSearchQuery { Kind = download.Kind, Query = query, ReturnedFields = ["id"] }, ct).ConfigureAwait(false);
                if (_flow.Source.Read == InventoryRead.Storage && !OsduKind.IsExact(download.Kind))
                {
                    kinds = await StorageInventoryReader.KindsAsync(client, _context.Time, _flow.Source, download.Kind, new InventoryReadStats(), ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (Expected(ex, ct))
            {
                problems.Add(HeaderRedaction.RedactMessage(ex.Message));
            }

            summaries.Add(new DownloadPlanSummary(download.Name, download.Kind, query, ReadName(_flow.Source.Read), records, kinds, folder, problems));
        }

        return new DownloadPlanOutcome(DeliveryOperations.Plan, _flow.Name, partition, summaries);
    }

    /// <summary>
    /// Runs the downloads the run selects (every one when it names none): lists, merges and writes each. With
    /// <paramref name="force"/>, every record OSDU serves is written again.
    /// </summary>
    public async Task<DownloadOutcome> DownloadAsync(IReadOnlyCollection<string> names, bool force, Guid runId, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var ledger = _context.Ledger ?? throw new DeliveryException(DeliveryServices.NoLedgerMessage);
        var partition = await PartitionAsync(ct).ConfigureAwait(false);
        var selected = _flow.Select(names);
        await ledger.RegisterLedgerAsync(
            new LedgerEntry
            {
                FlowId = _flow.LedgerId,
                Partition = partition,
                Kind = LedgerKinds.Download,
                FlowName = _flow.Name,
                LedgerName = _flow.LedgerName,
            },
            ct).ConfigureAwait(false);
        _log.LogInformation(
            "download flow '{Flow}' in partition '{Partition}': {Count} download(s), listing through {Read}{Forced}",
            _flow.Name, partition, selected.Count, ReadName(_flow.Source.Read), force ? ", every record written again as the run asks" : string.Empty);

        using var http = new HttpRuntime(_flow.Reliability, _context.Secrets, _context.Time, _transport, _allowLoopback, observer: _context.HttpObserver);
        var client = await ProtocolFactory.ClientAsync(http, _flow.Source.Endpoint, _flow.Source.Auth, _flow.Source.Headers, _context.Secrets, ct).ConfigureAwait(false);
        var summaries = new List<DownloadSummary>(selected.Count);
        foreach (var download in selected)
        {
            summaries.Add(await RunOneAsync(ledger, client, download, partition, force, runId, actor, ct).ConfigureAwait(false));
        }

        var outcome = new DownloadOutcome(
            DeliveryOperations.Download, _flow.Name, partition,
            summaries.Count(s => s.Status == DownloadRunStatus.Completed), summaries.Count(s => s.Status == DownloadRunStatus.Failed), summaries);
        return outcome.Failed > 0 ? throw new DownloadRunsFailedException(outcome) : outcome;
    }

    private async Task<DownloadSummary> RunOneAsync(
        ILedger ledger, OsduHttpClient client, DownloadSpec download, string partition, bool force, Guid runId, string actor, CancellationToken ct)
    {
        var flowId = _flow.LedgerId;
        var read = ReadName(_flow.Source.Read);
        var state = await ledger.RegisterDownloadAsync(flowId, _flow.Name, download.Name, download.Kind, download.Query, read, ct).ConfigureAwait(false);
        string folder;
        try
        {
            folder = Folder(download, partition);
        }
        catch (DeliveryException ex)
        {
            _log.LogError("download '{Download}' failed: {Error}", download.Name, ex.Message);
            return new DownloadSummary(download.Name, download.Kind, DownloadRunStatus.Failed, null, null, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, ex.Message);
        }

        var rewrite = force
            ? DownloadRewrite.Forced
            : state.Location is { } last && !string.Equals(last, folder, StringComparison.Ordinal) ? DownloadRewrite.Moved : null;
        var started = Now;
        var runRow = await ledger.StartDownloadRunAsync(flowId, state.DownloadId, runId, actor, read, folder, rewrite, started, ct).ConfigureAwait(false);
        if (rewrite == DownloadRewrite.Moved)
        {
            _log.LogInformation(
                "download '{Download}': its files were written to {Last}, and the flow now writes to {Folder}, which holds none of them; every record is written there again",
                download.Name, state.Location, folder);
        }

        var stats = new InventoryReadStats();
        var merge = new DownloadMerge(0, 0, 0, 0, 0);
        var writes = new WriteTally();
        try
        {
            merge = await ListAndMergeAsync(ledger, client, download, state, runRow, partition, stats, ct).ConfigureAwait(false);
            await WriteDueAsync(ledger, client, download, state, runRow, folder, rewrite is not null, stats, writes, ct).ConfigureAwait(false);
            var summary = new DownloadSummary(
                download.Name, download.Kind, DownloadRunStatus.Completed, runRow, folder, rewrite, merge.Listed, merge.Added, merge.Changed, merge.Gone,
                merge.Returned, writes.Written, writes.Bytes, writes.Missing, stats.Pages, stats.Requests, null);
            await ledger.CompleteDownloadRunAsync(flowId, runRow, Outcome(summary, started), Now, ct).ConfigureAwait(false);
            _log.LogInformation("{Summary}", summary.Describe());
            return summary;
        }
        catch (Exception ex) when (Expected(ex, ct))
        {
            var error = HeaderRedaction.RedactMessage(ex.Message);
            _log.LogError(ex is DeliveryException or HttpRequestException or IOException ? null : ex, "download '{Download}' failed: {Error}", download.Name, error);
            var summary = new DownloadSummary(
                download.Name, download.Kind, DownloadRunStatus.Failed, runRow, folder, rewrite, merge.Listed, merge.Added, merge.Changed, merge.Gone,
                merge.Returned, writes.Written, writes.Bytes, writes.Missing, stats.Pages, stats.Requests, error);
            await ledger.CompleteDownloadRunAsync(flowId, runRow, Outcome(summary, started), Now, CancellationToken.None).ConfigureAwait(false);
            return summary;
        }
    }

    /// <summary>Lists the download whole into its stage, a chunk at a time, then merges it; a listing that fails part way merges nothing.</summary>
    private async Task<DownloadMerge> ListAndMergeAsync(
        ILedger ledger, OsduHttpClient client, DownloadSpec download, DownloadState state, long runRow, string partition, InventoryReadStats stats, CancellationToken ct)
    {
        IInventoryReader reader = _flow.Source.Read == InventoryRead.Storage
            ? new StorageInventoryReader(client, _flow.Source, _context.Time)
            : new SearchInventoryReader(new OsduSearch(client, _flow.Source.QueryPath, _flow.Source.SearchPath, _log, _flow.Source.Paging));
        var buffer = new List<DownloadScanRow>(StageChunk);
        long listed = 0;
        await foreach (var page in reader.ReadAsync(download.AsInventory(), Query(download, partition), stats, ct).ConfigureAwait(false))
        {
            foreach (var row in page)
            {
                buffer.Add(new DownloadScanRow(row.TargetId, row.Kind, row.Version, DownloadFileNames.Of(row.TargetId), DownloadFileNames.Distinct(row.TargetId)));
            }

            listed += page.Count;
            if (buffer.Count >= StageChunk)
            {
                await ledger.AppendDownloadScanAsync(_flow.LedgerId, runRow, buffer, ct).ConfigureAwait(false);
                buffer.Clear();
            }
        }

        if (buffer.Count > 0)
        {
            await ledger.AppendDownloadScanAsync(_flow.LedgerId, runRow, buffer, ct).ConfigureAwait(false);
        }

        _log.LogInformation("download '{Download}': {Listed} id(s) listed in {Pages} page(s), {Requests} request(s); merging", download.Name, listed, stats.Pages, stats.Requests);
        return await ledger.MergeDownloadAsync(_flow.LedgerId, state.DownloadId, runRow, Now, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes every record due, a pass of <see cref="WriteChunk"/> at a time in row order: each pass's records read from storage
    /// a hundred to a request, several requests at once, each record written to its file, and what each came to recorded, the
    /// files already written recorded even when the pass stops part way, so the ledger never misses a file it holds.
    /// </summary>
    private async Task WriteDueAsync(
        ILedger ledger, OsduHttpClient client, DownloadSpec download, DownloadState state, long runRow, string folder, bool every,
        InventoryReadStats stats, WriteTally tally, CancellationToken ct)
    {
        var storage = new StorageRecords(client, _flow.Source.RecordQueryPath);
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _flow.Parallelism.RecordsAtOnce), CancellationToken = ct };
        long after = 0;
        var nextProgress = ProgressEvery;
        while (true)
        {
            var due = await ledger.DownloadsDueAsync(_flow.LedgerId, state.DownloadId, every, after, WriteChunk, ct).ConfigureAwait(false);
            if (due.Count == 0)
            {
                return;
            }

            var results = new ConcurrentBag<DownloadWrite>();
            try
            {
                await Parallel.ForEachAsync(due.Chunk(StorageRecords.Batch), options, async (chunk, token) =>
                {
                    var read = await storage.ReadAsync(chunk.Select(d => d.TargetId).ToList(), null, token).ConfigureAwait(false);
                    stats.Request();
                    var byId = read.Records.ToDictionary(r => OsduSearch.IdOf(r)!, StringComparer.Ordinal);
                    foreach (var record in chunk)
                    {
                        if (!byId.TryGetValue(record.TargetId, out var held))
                        {
                            results.Add(DownloadWrite.Missed(record.DownloadRecordId, Why(record.TargetId, read)));
                            continue;
                        }

                        var bytes = JsonSerializer.SerializeToUtf8Bytes(held, FileJson);
                        using (var content = new MemoryStream(bytes, writable: false))
                        {
                            await _context.Stores.WriteAsync(FileStoreRegistry.Join(folder, record.FileName), content, token).ConfigureAwait(false);
                        }

                        results.Add(DownloadWrite.Written(record.DownloadRecordId, VersionOf(held) ?? record.Version, bytes.LongLength, SHA256.HashData(bytes)));
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                // What was written is the folder's, whether or not the pass completed: the ledger says so before anything else happens.
                var recorded = results.ToList();
                await ledger.RecordDownloadWritesAsync(_flow.LedgerId, runRow, recorded, Now, CancellationToken.None).ConfigureAwait(false);
                tally.Add(recorded);
            }

            after = due[^1].DownloadRecordId;
            if (tally.Written >= nextProgress)
            {
                _log.LogInformation("download '{Download}': {Written} file(s) written so far, {Bytes} byte(s)", download.Name, tally.Written, tally.Bytes);
                nextProgress = (int)Math.Min(int.MaxValue, (tally.Written / ProgressEvery + 1) * ProgressEvery);
            }
        }
    }

    /// <summary>Why storage handed over no record for an id it was asked for, as its answer says.</summary>
    private static string Why(string targetId, StorageRead read)
        => read.Unanswered.Contains(targetId, StringComparer.Ordinal)
            ? "storage asked twice to be asked again for it and did not say whether it holds it; the next run asks again"
            : read.Invalid.Contains(targetId, StringComparer.Ordinal)
                ? "storage named it under invalidRecords: deleted since it was listed, or not readable by the flow's identity"
                : "storage did not return it: deleted since it was listed, or not readable by the flow's identity";

    /// <summary>The version a record read from storage carries, or null when it carries none.</summary>
    private static long? VersionOf(JsonElement record)
        => record.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt64(out var value) ? value : null;

    private static DownloadRunState Outcome(DownloadSummary summary, DateTime started) => new()
    {
        DownloadRunId = summary.DownloadRunId ?? 0,
        Status = summary.Status,
        StartedUtc = started,
        Location = summary.Location ?? string.Empty,
        Rewrite = summary.Rewrite,
        Listed = summary.Listed,
        Pages = summary.Pages,
        Requests = summary.Requests,
        Added = summary.Added,
        Changed = summary.Changed,
        Gone = summary.Gone,
        Returned = summary.Returned,
        Written = summary.Written,
        Bytes = summary.Bytes,
        Missing = summary.Missing,
        Error = summary.Error,
    };

    /// <summary>The download's query with its tokens filled: the flow's parameters, and <c>{partition}</c> the partition the run reads.</summary>
    private string? Query(DownloadSpec download, string partition)
        => download.Query is { } query
            ? FlowParameters.Substitute(query, new Dictionary<string, string>(_values, StringComparer.Ordinal) { [PartitionNames.RunValue] = partition })
            : null;

    /// <summary>
    /// The partition the run reads, by its data-partition-id: the one the flow is bound to, or for a flow whose partition is its
    /// header's, the header's resolved on this node.
    /// </summary>
    private async Task<string> PartitionAsync(CancellationToken ct)
    {
        if (_flow.Partition is { } bound)
        {
            return bound;
        }

        var declared = _flow.Source.Headers.FirstOrDefault(h => h.Key.Equals(CacheScope.PartitionHeader, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(declared))
        {
            throw new DeliveryException(
                $"Download flow '{_flow.Name}' is bound to no partition and names no '{CacheScope.PartitionHeader}' in its source.headers, so the partition it reads is unknown.");
        }

        return CacheScope.Normalize(await _context.Secrets.ResolveAsync(declared, ct).ConfigureAwait(false), $"{_flow.Name}: source.headers");
    }

    private static string ReadName(InventoryRead read) => read == InventoryRead.Storage ? "storage" : "search";

    /// <summary>What a failure of one download is: anything but the run itself being cancelled, which stops every download.</summary>
    private static bool Expected(Exception ex, CancellationToken ct)
        => (ex is not OperationCanceledException || !ct.IsCancellationRequested)
           && ex is SqlFlowException or HttpRequestException or IOException or TimeoutException or JsonException or InvalidOperationException
               or UnauthorizedAccessException or Microsoft.Data.SqlClient.SqlException or OperationCanceledException;

    /// <summary>What a run's writes came to, over its passes.</summary>
    private sealed class WriteTally
    {
        public long Written { get; private set; }

        public long Bytes { get; private set; }

        public long Missing { get; private set; }

        public void Add(IReadOnlyList<DownloadWrite> writes)
        {
            foreach (var write in writes)
            {
                if (write.Problem is null)
                {
                    Written++;
                    Bytes += write.Bytes ?? 0;
                }
                else
                {
                    Missing++;
                }
            }
        }
    }
}
