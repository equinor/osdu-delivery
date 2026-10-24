using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Inventories;

/// <summary>What a duplicate check's run came to: its download, or a pick of its groups' copies.</summary>
public sealed record InventoryDuplicatesOutcome(
    string Operation,
    string Flow,
    string Partition,
    string Inventory,
    long CheckId,
    string Status,
    long Candidates,
    long Keyed,
    long Unkeyed,
    long Outside,
    long Groups,
    long Copies,
    long Decided,
    long Review,
    long ToRemove,
    long Requests,
    IReadOnlyList<string> Notes,
    string? Error)
{
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"duplicate check {CheckId} of '{Inventory}' in '{Partition}': {Groups} group(s) of {Copies} id(s) hold equal values at the key among the {Keyed} of {Candidates} served id(s) holding one ({Unkeyed} hold none of it); {Decided} decided, {Review} for review, {ToRemove} copy(ies) picked to remove; {Outside} record(s) outside the inventory; {Requests} request(s){(Error is null ? "" : $"; stopped: {Error}")}.");
}

/// <summary>A duplicate check's run that stopped: the run ends failed, carrying what it came to.</summary>
public sealed class InventoryDuplicatesFailedException : DeliveryException
{
    public InventoryDuplicatesFailedException(InventoryDuplicatesOutcome outcome)
        : base((outcome ?? throw new ArgumentNullException(nameof(outcome))).Describe())
    {
        Outcome = outcome;
    }

    public InventoryDuplicatesOutcome Outcome { get; }
}

public sealed partial class InventoryRunner
{
    /// <summary>
    /// Runs the step of a duplicate check the control plane asked for (osdu/docs/reference/flow/inventory-duplicates.md): its
    /// download, which reads the properties of every id the inventory serves into the check's own table and groups the table by
    /// the check's key; or a pick, which reads the copies of the groups waiting to be picked and picks the copy each keeps.
    /// Nothing in OSDU changes.
    /// </summary>
    public async Task<InventoryDuplicatesOutcome> FindDuplicatesAsync(string inventoryName, long checkId, Guid runId, string actor, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inventoryName);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var ledger = _context.Ledger ?? throw new DeliveryException(DeliveryServices.NoLedgerMessage);
        var partition = await PartitionAsync(ct).ConfigureAwait(false);
        var spec = _flow.Inventory(inventoryName);
        var flowId = _flow.LedgerId;
        var state = spec is null
            ? null
            : (await ledger.ListInventoriesAsync(partition, ct).ConfigureAwait(false))
                .FirstOrDefault(i => i.FlowId == flowId && string.Equals(i.Name, spec.Name, StringComparison.OrdinalIgnoreCase));
        if (spec is null || state?.LastBuildRunId is null)
        {
            var why = spec is null
                ? $"Inventory flow '{_flow.Name}' has no inventory named '{inventoryName}'. Nothing was checked."
                : $"Inventory '{spec.Name}' has not been built in '{partition}', so it holds no id to check; build it first. Nothing was checked.";
            await ledger.FailInventoryDuplicateCheckAsync(partition, checkId, why, Now, CancellationToken.None).ConfigureAwait(false);
            throw new DeliveryException(why);
        }

        var asked = await ledger.GetInventoryDuplicateCheckAsync(partition, checkId, ct).ConfigureAwait(false);
        return asked is { Phase: InventoryDuplicates.ReadingCopies, Status: InventoryDuplicates.Queued or InventoryDuplicates.Running }
            ? await PickAsync(ledger, partition, spec, checkId, runId, actor, ct).ConfigureAwait(false)
            : await DownloadAsync(ledger, partition, spec, state, checkId, runId, actor, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A check's download: the properties of its attributes read for every record of the inventory's kind (narrowed by its query)
    /// through the search, a page at a time, only those properties returned, each page kept in the check's table as it is read and
    /// matched with the inventory's ids by the OSDU id; then the table grouped by the check's key in SQL. A request per 1,000 ids,
    /// however many duplicates the key finds. A download that stops keeps no group and no table, and the run ends failed.
    /// </summary>
    private async Task<InventoryDuplicatesOutcome> DownloadAsync(
        ILedger ledger, string partition, InventorySpec spec, InventoryState state, long checkId, Guid runId, string actor, CancellationToken ct)
    {
        var flowId = _flow.LedgerId;
        var check = await ledger.StartInventoryDuplicateCheckAsync(flowId, state.InventoryId, checkId, runId, state.LastBuildRunId, Now, ct).ConfigureAwait(false)
            ?? throw new DeliveryException(
                $"Inventory '{spec.Name}' in '{partition}' holds no duplicate check {checkId}: it was opened for another inventory, or retired since. Nothing was checked.");
        _log.LogInformation(
            "inventory '{Inventory}' in '{Partition}', duplicate check {Check}: downloading the properties of every id, through {Endpoint}, as {Actor}",
            spec.Name, partition, checkId, _flow.Source.Endpoint, actor);

        var notes = new List<string>();
        var counter = new SearchRequestCounter(_flow.Source, _context.HttpObserver, storageReads: true);
        string? error = null;
        var cancelled = false;
        try
        {
            var key = InventoryDuplicateKey.Parse(check.KeyJson);
            var attributes = AttributesOf(check, key);
            using var http = new HttpRuntime(_flow.Reliability, _context.Secrets, _context.Time, _transport, _allowLoopback, observer: counter);
            var client = await ProtocolFactory.ClientAsync(http, _flow.Source.Endpoint, _flow.Source.Auth, _flow.Source.Headers, _context.Secrets, ct).ConfigureAwait(false);
            var search = new OsduSearch(client, _flow.Source.QueryPath, _flow.Source.SearchPath, _log, _flow.Source.Paging);
            var candidates = await ledger.CountInventoryServedAsync(flowId, state.InventoryId, ct).ConfigureAwait(false);
            await ledger.PlanInventoryDuplicateCheckAsync(flowId, checkId, candidates, counter.Take(), ct).ConfigureAwait(false);
            await ledger.PrepareInventoryDuplicateTableAsync(flowId, checkId, attributes, ct).ConfigureAwait(false);
            _log.LogInformation(
                "duplicate check {Check}: downloading {Attributes} of the {Candidates} id(s) the inventory serves into {Table}",
                checkId, string.Join(", ", attributes.Select(a => a.Path)), candidates, InventoryDuplicateTable.NameOf(checkId));

            if (candidates > 0)
            {
                await DownloadRowsAsync(ledger, search, attributes, spec, partition, state.InventoryId, checkId, counter, notes, ct).ConfigureAwait(false);
            }

            var grouping = await ledger.GroupInventoryDuplicatesAsync(
                partition,
                checkId,
                new InventoryDuplicateKeying(check.KeyJson, check.KeyHash, key.Paths, check.KeyedBy ?? check.Actor, Now),
                ct).ConfigureAwait(false);
            _log.LogInformation(
                "duplicate check {Check}: {Groups} group(s) of {Copies} id(s) hold equal values at the key; {Broad} too broad to pick from",
                checkId, grouping.Groups, grouping.Copies, grouping.TooBroad);
            if (grouping.TooBroad > 0)
            {
                notes.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{grouping.TooBroad} group(s) hold more than {InventoryDuplicates.MaxCopiesPicked} copies: a key that broad is not one record written more than once, so they are left for review and a pick does not read them; add properties to the key."));
            }

            if (grouping.Keyed + grouping.Unkeyed < candidates)
            {
                notes.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{candidates - grouping.Keyed - grouping.Unkeyed} id(s) the inventory serves were not returned by the search (the index has not caught up with them, or does not hold them), so nothing of them was downloaded and no group holds them."));
            }

            // What the last write left unsaid is counted with the end, so every request sent is in the check's count.
            await ledger.PlanInventoryDuplicateCheckAsync(flowId, checkId, candidates, counter.Take(), ct).ConfigureAwait(false);
            await ledger.CompleteInventoryDuplicateCheckAsync(flowId, checkId, new InventoryDuplicateCheckEnd(InventoryDuplicates.Completed, notes, null), Now, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
            error = "the run was cancelled before the check downloaded every id; it holds no group";
        }
        catch (Exception ex) when (Expected(ex, ct) || ex is OsduQueryException)
        {
            error = HeaderRedaction.RedactMessage(ex.Message);
        }

        if (error is not null)
        {
            _log.LogError("duplicate check {Check}: stopped: {Error}", checkId, error);
            await ledger.CompleteInventoryDuplicateCheckAsync(
                flowId, checkId, new InventoryDuplicateCheckEnd(InventoryDuplicates.Failed, notes, error), Now, CancellationToken.None).ConfigureAwait(false);
        }

        return await EndAsync(ledger, partition, spec, checkId, notes, error, counter, cancelled, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A check's pick: the copies of every group waiting to be picked read whole from storage, a round of groups at a time, how many
    /// records name each counted through the search a batch of copies at a time, and each group judged. A pick that stops keeps
    /// the groups it judged, the check completed still with why it stopped, and a pick asked again reads the groups left.
    /// </summary>
    private async Task<InventoryDuplicatesOutcome> PickAsync(
        ILedger ledger, string partition, InventorySpec spec, long checkId, Guid runId, string actor, CancellationToken ct)
    {
        var flowId = _flow.LedgerId;
        var check = await ledger.StartInventoryDuplicatePickAsync(flowId, checkId, runId, Now, ct).ConfigureAwait(false)
            ?? throw new DeliveryException(
                $"Inventory '{spec.Name}' in '{partition}' holds no duplicate check {checkId} to pick: it was retired, or no pick was asked of it. Nothing was read.");
        _log.LogInformation(
            "inventory '{Inventory}' in '{Partition}', duplicate check {Check}: picking the copy each group keeps, through {Endpoint}, as {Actor}",
            spec.Name, partition, checkId, _flow.Source.Endpoint, actor);

        var notes = NotesOf(check.NotesJson);
        var counter = new SearchRequestCounter(_flow.Source, _context.HttpObserver, storageReads: true);
        string? error = null;
        var cancelled = false;
        try
        {
            using var http = new HttpRuntime(_flow.Reliability, _context.Secrets, _context.Time, _transport, _allowLoopback, observer: counter);
            var client = await ProtocolFactory.ClientAsync(http, _flow.Source.Endpoint, _flow.Source.Auth, _flow.Source.Headers, _context.Secrets, ct).ConfigureAwait(false);
            var search = new OsduSearch(client, _flow.Source.QueryPath, _flow.Source.SearchPath, _log, _flow.Source.Paging);
            await JudgeGroupsAsync(ledger, client, search, checkId, check.Groups - check.GroupsJudged, counter, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
            error = "the run was cancelled before the pick read every group; pick again to read the groups left";
        }
        catch (Exception ex) when (Expected(ex, ct) || ex is OsduQueryException)
        {
            error = HeaderRedaction.RedactMessage(ex.Message) + " The groups the pick read keep what it found; pick again to read the groups left.";
        }

        if (error is not null)
        {
            _log.LogError("duplicate check {Check}: the pick stopped: {Error}", checkId, error);
        }

        await ledger.PlanInventoryDuplicateCheckAsync(flowId, checkId, check.Candidates, counter.Take(), CancellationToken.None).ConfigureAwait(false);
        await ledger.CompleteInventoryDuplicatePickAsync(flowId, checkId, new InventoryDuplicatePickEnd(notes, error), Now, CancellationToken.None).ConfigureAwait(false);
        return await EndAsync(ledger, partition, spec, checkId, notes, error, counter, cancelled, ct).ConfigureAwait(false);
    }

    /// <summary>What a duplicate check's run came to, as its result says it and its log writes it; a run that stopped ends failed.</summary>
    private async Task<InventoryDuplicatesOutcome> EndAsync(
        ILedger ledger, string partition, InventorySpec spec, long checkId, IReadOnlyList<string> notes, string? error, SearchRequestCounter counter, bool cancelled,
        CancellationToken ct)
    {
        var ended = await ledger.GetInventoryDuplicateCheckAsync(partition, checkId, CancellationToken.None).ConfigureAwait(false);
        var outcome = new InventoryDuplicatesOutcome(
            DeliveryOperations.Duplicates, _flow.Name, partition, spec.Name, checkId, ended?.Status ?? InventoryDuplicates.Failed,
            ended?.Candidates ?? 0, ended?.Keyed ?? 0, ended?.Unkeyed ?? 0, ended?.Outside ?? 0, ended?.Groups ?? 0, ended?.Copies ?? 0,
            ended?.Decided ?? 0, ended?.Review ?? 0, ended?.ToRemove ?? 0, ended?.Requests ?? counter.Sent, notes, error);
        _log.LogInformation("{Outcome}", outcome.Describe());
        foreach (var note in notes)
        {
            _log.LogInformation("duplicate check {Check}: {Note}", checkId, note);
        }

        if (cancelled)
        {
            ct.ThrowIfCancellationRequested();
        }

        return error is null ? outcome : throw new InventoryDuplicatesFailedException(outcome);
    }

    /// <summary>
    /// The properties a check downloads, as its row keeps them: checked again as a key's properties are, since they cross a trust
    /// boundary in the module's database. A check opened before checks kept them downloads its key's.
    /// </summary>
    private static IReadOnlyList<DuplicateColumn> AttributesOf(InventoryDuplicateCheckState check, InventoryDuplicateKey key)
    {
        var attributes = InventoryDuplicateTable.Parse(check.AttributesJson);
        if (attributes.Count == 0)
        {
            return InventoryDuplicateTable.AttributesOf(key.Paths);
        }

        var (_, problem) = InventoryDuplicateKey.Of(attributes.Select(a => (string?)a.Path).ToList());
        return problem is null
            ? attributes
            : throw new DeliveryException($"The properties duplicate check {check.InventoryDuplicateCheckId} downloads cannot be read: {problem} Check for duplicates again.");
    }

    /// <summary>
    /// Reads the properties a check downloads of every record of the inventory's kind, narrowed by its query, a page of the search
    /// at a time, only those properties returned, and keeps each page in the check's table as it is read. A property no record
    /// held a value at is noted: the search returns only what its index keeps of a record.
    /// </summary>
    private async Task DownloadRowsAsync(
        ILedger ledger, OsduSearch search, IReadOnlyList<DuplicateColumn> attributes, InventorySpec spec, string partition, int inventoryId, long checkId,
        SearchRequestCounter counter, List<string> notes, CancellationToken ct)
    {
        IReadOnlyList<string> returned = ["id", "version", .. attributes.Select(a => a.Path).Where(p => p is not ("id" or "version"))];
        var held = new long[attributes.Count];
        var reported = _context.Time.GetUtcNow();
        long read = 0;
        await foreach (var page in search.PagesAsync(new OsduSearchQuery { Kind = spec.Kind, Query = Query(spec, partition), ReturnedFields = returned }, OsduSearch.MaxPage, ct).ConfigureAwait(false))
        {
            var rows = new List<InventoryDuplicateRow>(page.Hits.Count);
            foreach (var hit in page.Hits)
            {
                if (JsonNode.Parse(hit.GetRawText()) is not JsonObject node || IdOf(node) is not { } id)
                {
                    continue;
                }

                var values = new List<InventoryDuplicateValue?>(attributes.Count);
                for (var i = 0; i < attributes.Count; i++)
                {
                    var value = InventoryDuplicateKey.ReadAttribute(node, attributes[i].Path);
                    if (value is not null)
                    {
                        held[i]++;
                    }

                    values.Add(value);
                }

                rows.Add(new InventoryDuplicateRow(id, VersionOf(node), values));
            }

            await ledger.AddInventoryDuplicateRowsAsync(_flow.LedgerId, inventoryId, checkId, attributes, rows, page.Hits.Count, counter.Take(), ct).ConfigureAwait(false);
            read += page.Hits.Count;
            var now = _context.Time.GetUtcNow();
            if (now - reported >= RemovalProgressEvery)
            {
                reported = now;
                _log.LogInformation("duplicate check {Check}: {Read} record(s) downloaded so far", checkId, read);
            }
        }

        if (read == 0)
        {
            notes.Add($"The search returned no record of {spec.Kind}{(spec.Query is null ? string.Empty : " within the inventory's query")}, so nothing was downloaded.");
            return;
        }

        for (var i = 0; i < attributes.Count; i++)
        {
            if (held[i] == 0)
            {
                notes.Add(
                    $"No record the search returned held a value at {attributes[i].Path}: the search returns only what its index keeps of a record (a property the kind's schema declares, as its indexing keeps it), so a key of it groups no record. Check the path, or leave it out of the key.");
            }
        }
    }

    /// <summary>
    /// Reads the copies of every group waiting to be picked and judges the group, a round of groups at a time in order: each copy
    /// read whole from storage, a hundred ids a request, as many requests at once as the flow's <c>parallelism.recordsAtOnce</c>;
    /// how many records name each copy storage holds counted through the search a batch of copies at a time
    /// (<see cref="InventoryDuplicateReferences"/>), one search after another; and each round written before the next is read.
    /// </summary>
    private async Task JudgeGroupsAsync(
        ILedger ledger, OsduHttpClient client, OsduSearch search, long checkId, long groups, SearchRequestCounter counter, CancellationToken ct)
    {
        var removable = _flow.Removal is { } policy
            ? policy.Findings.Where(f => InventoryRemovals.Removable.Contains(f, StringComparer.Ordinal)).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var records = new StorageRecords(client, _flow.Source.RecordQueryPath);
        var references = new InventoryDuplicateReferences(search, InventoryDuplicates.ReferencesPerSearch);
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _flow.Parallelism.RecordsAtOnce), CancellationToken = ct };
        var reported = _context.Time.GetUtcNow();
        var after = 0;
        long judged = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var round = await ledger.InventoryDuplicateGroupsToJudgeAsync(_flow.LedgerId, checkId, after, InventoryDuplicates.GroupsPerRound, ct).ConfigureAwait(false);
            if (round.Count == 0)
            {
                return;
            }

            after = round[^1].GroupNumber;
            var stored = new ConcurrentDictionary<string, (string State, long? Version, int? Attributes)>(StringComparer.Ordinal);
            var held = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            var ids = round.SelectMany(g => g.Copies).Select(c => c.TargetId).Distinct(StringComparer.Ordinal).ToList();
            await Parallel.ForEachAsync(ids.Chunk(StorageRecords.Batch), options, async (chunk, token) =>
            {
                var answer = await records.ReadAsync(chunk, null, token).ConfigureAwait(false);
                foreach (var record in answer.Records)
                {
                    if (OsduSearch.IdOf(record) is { } id)
                    {
                        stored[id] = (InventoryDuplicates.Held, StoredVersionOf(record), AttributesOf(record));
                        held[id] = record.GetRawText();
                    }
                }

                var unanswered = answer.Unanswered.ToHashSet(StringComparer.Ordinal);
                foreach (var id in answer.Missing)
                {
                    stored.TryAdd(id, (unanswered.Contains(id) ? InventoryDuplicates.Unanswered : InventoryDuplicates.Missing, null, null));
                }
            }).ConfigureAwait(false);

            var named = await references.CountAsync(held.Select(h => new DuplicateCopyRecord(h.Key, h.Value)).OrderBy(c => c.TargetId, StringComparer.Ordinal).ToList(), ct)
                .ConfigureAwait(false);
            var judgements = round
                .Select(g => InventoryDuplicatePicker.Judge(g.GroupNumber, g.Copies.Select(c => CopyOf(c, stored, named)).ToList(), removable, chosen: null))
                .ToList();
            await ledger.WriteInventoryDuplicateJudgementsAsync(_flow.LedgerId, checkId, judgements, counter.Take(), Now, ct).ConfigureAwait(false);
            judged += round.Count;
            var now = _context.Time.GetUtcNow();
            if (now - reported >= RemovalProgressEvery)
            {
                reported = now;
                _log.LogInformation(
                    "duplicate check {Check}: {Judged} of {Groups} group(s) picked so far, {Searches} search(es) for the records naming their copies",
                    checkId, judged, groups, references.Searches);
            }
        }
    }

    /// <summary>A copy as the picker judges it: what storage answered for it, and how many records name it.</summary>
    private static DuplicateCopy CopyOf(
        InventoryDuplicateCopyRow row, ConcurrentDictionary<string, (string State, long? Version, int? Attributes)> stored,
        IReadOnlyDictionary<string, DuplicateReferenceCount> named)
    {
        // An id the storage read named neither held nor missing is one storage was not asked about: it did not answer for it.
        var (state, version, attributes) = stored.TryGetValue(row.TargetId, out var read) ? read : (InventoryDuplicates.Unanswered, null, null);
        var references = named.TryGetValue(row.TargetId, out var counted) ? counted : new DuplicateReferenceCount(null, null);
        return new DuplicateCopy(row.InventoryRecordId, row.TargetId, row.Finding, state, version, attributes, references.Count, references.Problem);
    }

    /// <summary>The notes a check's download left, which its pick keeps beside its own.</summary>
    private static List<string> NotesOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            // Notes a person changed by hand into something else are left behind rather than stopping the pick.
            return [];
        }
    }

    /// <summary>How many values a record storage returned holds under <c>data</c>.</summary>
    private static int AttributesOf(JsonElement record)
        => record.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? InventoryDuplicatePicker.Attributes(JsonNode.Parse(data.GetRawText()))
            : 0;

    private static long? StoredVersionOf(JsonElement record)
        => record.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt64(out var number) ? number : null;

    /// <summary>The OSDU id a hit names, or null for one without an id, or with one longer than an inventory keeps.</summary>
    private static string? IdOf(JsonObject hit)
    {
        var id = hit["id"] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : null;
        return string.IsNullOrEmpty(id) || id.Length > DeliveryModel.MaxTargetIdLength ? null : id;
    }

    /// <summary>The version a hit holds, written as a number or as digits.</summary>
    private static long? VersionOf(JsonObject hit)
        => hit["version"] is JsonValue v && (v.TryGetValue<long>(out var number)
            || (v.TryGetValue<string>(out var digits) && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number)))
            ? number
            : null;
}
