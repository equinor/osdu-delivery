using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// Builds the search a flow's mapping answers its <c>search.</c> sources and its <c>$parent</c> nodes from. A seam like
/// <see cref="IProtocolFactory"/>: a host asks the platform the flow delivers to, and a suite that delivers to no platform
/// answers from what it was given.
/// </summary>
public interface IRecordSearchFactory
{
    /// <param name="flow">The flow whose mapping searches.</param>
    /// <param name="target">The flow's target, reached when the first question is asked and not before.</param>
    /// <param name="loggers">Where the search says what it asked and what came back; for a run, its own log and live trace.</param>
    IRecordSearch Create(FlowDefinition flow, Func<CancellationToken, Task<OsduHttpClient>> target, ILoggerFactory loggers);
}

/// <summary>The search a host uses: the OSDU search service of the platform the flow delivers to.</summary>
public sealed class PlatformRecordSearchFactory : IRecordSearchFactory
{
    /// <summary>The one factory every context shares; it holds nothing of its own.</summary>
    public static PlatformRecordSearchFactory Instance { get; } = new();

    public IRecordSearch Create(FlowDefinition flow, Func<CancellationToken, Task<OsduHttpClient>> target, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(loggers);

        // A record's parent is read where the flow's route reads records back by id: its own path when it names one.
        return new OsduRecordSearch(
            target, loggers.CreateLogger<OsduRecordSearch>(), flow.Target.ProtocolOptions.RecordQueryPath ?? OsduManifestProtocol.DefaultRecordQueryPath);
    }
}

/// <summary>
/// Answers a run's search questions from the OSDU search service (openapi search v2, <c>POST /query</c>): one query per
/// question, sent under the flow's own target, auth and partition, asking for the ids of at most a handful of the
/// records that match exactly. One record is the answer; none is an answer too; several are named so the hold says
/// which records answer to the value. Reads the records' parents from the storage service the same way
/// (osdu/docs/reference/flow/mapping-parent.md): by id, a hundred to a request (<see cref="StorageRecords"/>).
/// </summary>
/// <remarks>
/// <para>
/// Answers are kept for the run, found or not, so each distinct question is asked once however many records ask it,
/// and a question two renderers ask at the same moment is sent once. At most <see cref="MaxConcurrentQueries"/> queries
/// are in flight at a time, so a batch of new values is asked in parallel without flooding the service.
/// </para>
/// <para>
/// A query the service refuses (400) is that question's answer, and the records asking it are held with the service's
/// words. Anything else that keeps an answer from coming (the service unreachable, the credentials refused, a failure
/// that outlasts the flow's retries, a body that is not a search result) fails the call, since taking a missing answer
/// for finding nothing would hold records for a reason that is not true.
/// </para>
/// <para>
/// A parent storage does not hand over (under invalidRecords, or not returned at all) is not there for the flow: never
/// delivered, deleted, or hidden from the flow's identity. One storage asks to retry twice, a record of another entity type
/// under the parent's id, or a failed request fails the call, as a search's does. A run keeps at most
/// <see cref="MaxKeptParents"/> parents, each trimmed to the sections its mapping reads: a render reads its parent right
/// after the round that read it, and a run reading one parent per record never holds them all.
/// </para>
/// </remarks>
public sealed class OsduRecordSearch : IRecordSearch, IDisposable
{
    /// <summary>The search the questions are asked of (openapi search v2).</summary>
    public const string QueryPath = "/api/search/v2/query";

    /// <summary>How many queries one run keeps in flight at a time.</summary>
    public const int MaxConcurrentQueries = 8;

    /// <summary>How many parents a run keeps read at once; the oldest is let go first.</summary>
    public const int MaxKeptParents = 20_000;

    /// <summary>How much of a refusal's body a hold reason quotes.</summary>
    private const int RefusalPreview = 500;

    private static readonly IReadOnlySet<int> Refusals = new HashSet<int> { (int)HttpStatusCode.BadRequest };

    private readonly Func<CancellationToken, Task<OsduHttpClient>> _client;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<SearchQuestion, SearchAnswer> _answers = new();
    private readonly ConcurrentDictionary<SearchQuestion, Lazy<Task<SearchAnswer>>> _asking = new();
    private readonly ConcurrentDictionary<ParentQuestion, ParentAnswer> _parents = new();
    private readonly ConcurrentQueue<ParentQuestion> _parentsKept = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement?>> _reading = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots = new(MaxConcurrentQueries, MaxConcurrentQueries);
    private readonly string _recordPath;

    /// <param name="client">The flow's target, reached when the first question is asked and not before.</param>
    /// <param name="log">Where each round of questions is reported.</param>
    /// <param name="recordPath">The storage path records are read by id at (openapi storage v2, <c>POST /query/records</c>).</param>
    public OsduRecordSearch(Func<CancellationToken, Task<OsduHttpClient>> client, ILogger log, string recordPath = OsduManifestProtocol.DefaultRecordQueryPath)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordPath);
        _client = client;
        _log = log;
        _recordPath = recordPath;
    }

    public bool TryAnswer(SearchQuestion question, [NotNullWhen(true)] out SearchAnswer? answer)
    {
        ArgumentNullException.ThrowIfNull(question);
        return _answers.TryGetValue(question, out answer);
    }

    public bool TryRead(ParentQuestion question, [NotNullWhen(true)] out ParentAnswer? answer)
    {
        ArgumentNullException.ThrowIfNull(question);
        return _parents.TryGetValue(question, out answer);
    }

    public async Task AnswerAsync(IReadOnlyCollection<PlatformQuestion> questions, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(questions);
        await Task.WhenAll(
            SearchAsync(questions.OfType<SearchQuestion>().ToList(), ct),
            ReadParentsAsync(questions.OfType<ParentQuestion>().ToList(), ct)).ConfigureAwait(false);
    }

    /// <summary>Asks the search questions of <paramref name="questions"/> this run has not answered yet.</summary>
    private async Task SearchAsync(IReadOnlyList<SearchQuestion> questions, CancellationToken ct)
    {
        var asked = questions.Where(q => !_answers.ContainsKey(q)).Distinct().ToList();
        if (asked.Count == 0)
        {
            return;
        }

        var answers = await Task.WhenAll(asked.Select(q => AnswerOnceAsync(q, ct))).ConfigureAwait(false);
        _log.LogInformation(
            "Searched the platform for {Count} value(s): {Found} found, {NotFound} not found, {Ambiguous} matching several records, {Refused} refused.",
            asked.Count,
            answers.Count(a => a.Outcome == SearchOutcome.Found),
            answers.Count(a => a.Outcome == SearchOutcome.NotFound),
            answers.Count(a => a.Outcome == SearchOutcome.Ambiguous),
            answers.Count(a => a.Outcome == SearchOutcome.Refused));
    }

    /// <summary>
    /// Reads the parents <paramref name="questions"/> name that this run has not read yet: each id once, however many records
    /// ask for it, a hundred to a request, at most <see cref="MaxConcurrentQueries"/> requests in flight, and an id another
    /// caller is reading already awaited rather than read twice.
    /// </summary>
    private async Task ReadParentsAsync(IReadOnlyList<ParentQuestion> questions, CancellationToken ct)
    {
        var wanted = questions.Where(q => !_parents.ContainsKey(q)).Distinct().ToList();
        if (wanted.Count == 0)
        {
            return;
        }

        var mine = new List<(string Id, TaskCompletionSource<JsonElement?> Read)>();
        var reads = new Dictionary<string, Task<JsonElement?>>(StringComparer.Ordinal);
        foreach (var id in wanted.Select(q => q.Id).Distinct(StringComparer.Ordinal))
        {
            var read = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reading = _reading.GetOrAdd(id, read);
            if (ReferenceEquals(reading, read))
            {
                mine.Add((id, read));
            }

            reads[id] = reading.Task;
        }

        try
        {
            await Task.WhenAll(mine.Chunk(StorageRecords.Batch).Select(chunk => ReadChunkAsync(chunk, ct))).ConfigureAwait(false);
            var answered = 0;
            var found = 0;
            foreach (var question in wanted)
            {
                var record = await reads[question.Id].WaitAsync(ct).ConfigureAwait(false);
                var answer = record is { } held ? Answer(question, held) : ParentAnswer.None;
                Keep(question, answer);
                answered++;
                found += answer.Found ? 1 : 0;
            }

            _log.LogInformation(
                "Read {Count} parent record(s) from storage: {Found} found, {Missing} not held.", answered, found, answered - found);
        }
        finally
        {
            // A read is settled either way: what it found is kept by question above, and a failure leaves the id to be read
            // again by the next caller.
            foreach (var (id, read) in mine)
            {
                _reading.TryRemove(KeyValuePair.Create(id, read));
            }
        }
    }

    /// <summary>Reads one request's worth of parents and settles each id's read with the record, or null when storage holds none.</summary>
    private async Task ReadChunkAsync((string Id, TaskCompletionSource<JsonElement?> Read)[] chunk, CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = await _client(ct).ConfigureAwait(false);
            var read = await new StorageRecords(client, _recordPath).ReadAsync(chunk.Select(c => c.Id).ToList(), attributes: null, ct).ConfigureAwait(false);
            if (read.Unanswered.Count > 0)
            {
                throw new DeliveryException(
                    $"The storage service asked twice to retry reading the parent record{(read.Unanswered.Count == 1 ? string.Empty : "s")} {string.Join(", ", read.Unanswered.Take(5))}{(read.Unanswered.Count > 5 ? $" and {read.Unanswered.Count - 5} more" : string.Empty)}; "
                    + "nothing says whether it holds them, so no record waiting on one is held for a parent it may hold.");
            }

            var records = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var record in read.Records)
            {
                if (OsduSearch.IdOf(record) is { } id)
                {
                    records[id] = record;
                }
            }

            foreach (var (id, settle) in chunk)
            {
                settle.TrySetResult(records.TryGetValue(id, out var record) ? record : null);
            }
        }
        catch (Exception ex)
        {
            foreach (var (_, settle) in chunk)
            {
                settle.TrySetException(ex);
            }

            throw;
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>
    /// The answer a parent storage holds gives <paramref name="question"/>: its kind, its version and the sections the mapping
    /// reads, copied out. A record of another entity type under the parent's id is storage answering something else, and fails.
    /// </summary>
    internal static ParentAnswer Answer(ParentQuestion question, JsonElement record)
    {
        ArgumentNullException.ThrowIfNull(question);
        var entityType = question.Id.Split(':') is { Length: >= 3 } parts ? parts[1] : string.Empty;
        var kind = record.TryGetProperty("kind", out var kindValue) && kindValue.ValueKind == JsonValueKind.String ? kindValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(kind) || kind.Split(':') is not { Length: 4 } kindParts || !string.Equals(kindParts[2], entityType, StringComparison.Ordinal))
        {
            throw new DeliveryException(
                $"The storage service answered the parent record {question.Id} with a record of {(string.IsNullOrWhiteSpace(kind) ? "no kind" : kind)}, and a parent of {question.Kind} is a {entityType} record.");
        }

        var version = record.TryGetProperty("version", out var versionValue) && versionValue.TryGetInt64(out var number) ? number : 0;
        var whole = JsonNode.Parse(record.GetRawText()) as JsonObject
            ?? throw new DeliveryException($"The storage service answered the parent record {question.Id} with something that is not a record.");
        var sections = new JsonObject();
        foreach (var path in question.Paths)
        {
            var steps = path.Split('.');
            JsonNode? node = whole;
            foreach (var step in steps)
            {
                node = node is JsonObject holder && holder.TryGetPropertyValue(step, out var next) ? next : null;
            }

            if (node is null)
            {
                continue;
            }

            var into = sections;
            foreach (var step in steps[..^1])
            {
                if (into[step] is not JsonObject inner)
                {
                    inner = new JsonObject();
                    into[step] = inner;
                }

                into = inner;
            }

            into[steps[^1]] = node.DeepClone();
        }

        return ParentAnswer.Of(sections, kind, version);
    }

    /// <summary>Keeps <paramref name="answer"/> for the run, letting the oldest go past <see cref="MaxKeptParents"/>.</summary>
    private void Keep(ParentQuestion question, ParentAnswer answer)
    {
        if (_parents.TryAdd(question, answer))
        {
            _parentsKept.Enqueue(question);
        }

        while (_parents.Count > MaxKeptParents && _parentsKept.TryDequeue(out var oldest))
        {
            _parents.TryRemove(oldest, out _);
        }
    }

    /// <summary>The answer to <paramref name="question"/>, asking it only when no other caller is asking it already.</summary>
    private async Task<SearchAnswer> AnswerOnceAsync(SearchQuestion question, CancellationToken ct)
    {
        if (_answers.TryGetValue(question, out var known))
        {
            return known;
        }

        var asking = _asking.GetOrAdd(question, q => new Lazy<Task<SearchAnswer>>(() => AskAsync(q, ct)));
        try
        {
            var answer = await asking.Value.WaitAsync(ct).ConfigureAwait(false);
            _answers.TryAdd(question, answer);
            return answer;
        }
        finally
        {
            // Settled either way: an answer is kept above, and a failure leaves the question to be asked again.
            _asking.TryRemove(KeyValuePair.Create(question, asking));
        }
    }

    private async Task<SearchAnswer> AskAsync(SearchQuestion question, CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = await _client(ct).ConfigureAwait(false);
            var body = new JsonObject
            {
                ["kind"] = question.Kind,
                ["query"] = question.Query,
                ["limit"] = SearchAnswer.NamedCandidates,
                ["returnedFields"] = new JsonArray("id"),
            };

            var result = await client.SendJsonAsync(HttpMethod.Post, client.Url(QueryPath), body, Refusals, ct, idempotent: true).ConfigureAwait(false);
            if (result.Status == HttpStatusCode.BadRequest)
            {
                var said = Preview(result.BodyText);
                _log.LogWarning("The search service refused the query {Query} on {Kind}: {Refusal}", question.Query, question.Kind, said);
                return SearchAnswer.Refused(said);
            }

            var answer = Read(question, result.BodyText);
            _log.LogDebug("Searched {Kind} with {Query}: {Answer}", question.Kind, question.Query, answer.Describe());
            return answer;
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>
    /// The answer a search result gives: its ids, checked to be records of the kind asked, and its count. A body that is
    /// not a search result, or names records of another kind, is a platform answering something else, and fails.
    /// </summary>
    internal static SearchAnswer Read(SearchQuestion question, string body)
    {
        JsonObject result;
        try
        {
            result = JsonNode.Parse(body) as JsonObject
                ?? throw new DeliveryException($"The search service answered the query {question.Query} on {question.Kind} with something that is not a JSON object: {Preview(body)}");
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"The search service answered the query {question.Query} on {question.Kind} with a body that is not JSON: {Preview(body)}", ex);
        }

        if (result["results"] is not JsonArray results)
        {
            throw new DeliveryException($"The search service answered the query {question.Query} on {question.Kind} without a 'results' list: {Preview(body)}");
        }

        var entityType = question.Kind.Split(':')[2];
        var ids = new List<string>(results.Count);
        foreach (var hit in results)
        {
            if (hit is not JsonObject record || record["id"] is not JsonValue value || !value.TryGetValue<string>(out var id) || string.IsNullOrWhiteSpace(id))
            {
                throw new DeliveryException($"The search service answered the query {question.Query} on {question.Kind} with a result that has no id: {Preview(body)}");
            }

            var segments = id.Split(':');
            if (segments.Length < 3 || !string.Equals(segments[1], entityType, StringComparison.Ordinal))
            {
                throw new DeliveryException($"The search service answered the query {question.Query} on {question.Kind} with {id}, which is not a {entityType} record.");
            }

            ids.Add(id);
        }

        // The count is the service's; a result list longer than it would be the service contradicting itself, and the
        // records in hand are the surer of the two.
        var total = result["totalCount"] is JsonValue count && count.TryGetValue<long>(out var counted) ? Math.Max(counted, ids.Count) : ids.Count;
        return total switch
        {
            0 => SearchAnswer.None,
            1 when ids.Count == 1 => SearchAnswer.Found(ids[0]),
            1 => throw new DeliveryException($"The search service counted one record for the query {question.Query} on {question.Kind} and returned none: {Preview(body)}"),
            _ => SearchAnswer.Ambiguous(total, ids.Distinct(StringComparer.Ordinal).ToList()),
        };
    }

    public void Dispose() => _slots.Dispose();

    private static string Preview(string text)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= RefusalPreview ? flat : flat[..RefusalPreview] + "...";
    }
}
