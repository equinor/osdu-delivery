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

/// <summary>What a duplicate check of an inventory came to.</summary>
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
        $"duplicate check {CheckId} of '{Inventory}' in '{Partition}': {Groups} group(s) of {Copies} id(s) share a key among the {Keyed} of {Candidates} served id(s) holding one ({Unkeyed} hold none of it); {Decided} decided, {Review} for review, {ToRemove} copy(ies) picked to remove; {Outside} record(s) outside the inventory; {Requests} request(s){(Error is null ? "" : $"; stopped: {Error}")}.");
}

/// <summary>A duplicate check that stopped: the run ends failed, carrying what it came to, the check failed and holding no group.</summary>
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
    /// <summary>The kind a search for the records naming a copy reads: every kind, since a record of any type may name it.</summary>
    private const string EveryKind = "*:*:*:*";

    /// <summary>
    /// Finds which ids of an inventory are copies of one another by the key an operator compares them by, and keeps them as the
    /// check the control plane opened with that key (osdu/docs/reference/flow/inventory-duplicates.md). The key
    /// is read for every record of the inventory's kind (narrowed by its query) through the search, through the flow's own
    /// source, each page kept as it is read and matched with the inventory's ids by the OSDU id; the ids that share a key are
    /// grouped, the largest group first; then each group's copies are read whole from storage, how many values each holds
    /// under <c>data</c> counted and how many records name each counted through the search, and the group judged
    /// (<see cref="InventoryDuplicatePicker.Judge"/>): the most complete copy kept, the others the flow may remove picked to
    /// remove, a group the rules cannot decide left for an operator. Nothing in OSDU changes. A check that stops keeps no group,
    /// and the run ends failed.
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

        var check = await ledger.StartInventoryDuplicateCheckAsync(flowId, state.InventoryId, checkId, runId, state.LastBuildRunId, Now, ct).ConfigureAwait(false)
            ?? throw new DeliveryException(
                $"Inventory '{spec.Name}' in '{partition}' holds no duplicate check {checkId}: it was opened for another inventory, or retired since. Nothing was checked.");
        _log.LogInformation(
            "inventory '{Inventory}' in '{Partition}', duplicate check {Check}: finding the ids that share a key, through {Endpoint}, as {Actor}",
            spec.Name, partition, checkId, _flow.Source.Endpoint, actor);

        var notes = new List<string>();
        var counter = new SearchRequestCounter(_flow.Source, _context.HttpObserver, storageReads: true);
        string? error = null;
        var cancelled = false;
        try
        {
            var key = InventoryDuplicateKey.Parse(check.KeyJson);
            using var http = new HttpRuntime(_flow.Reliability, _context.Secrets, _context.Time, _transport, _allowLoopback, observer: counter);
            var client = await ProtocolFactory.ClientAsync(http, _flow.Source.Endpoint, _flow.Source.Auth, _flow.Source.Headers, _context.Secrets, ct).ConfigureAwait(false);
            var search = new OsduSearch(client, _flow.Source.QueryPath, _flow.Source.SearchPath, _log, _flow.Source.Paging);
            var candidates = await ledger.CountInventoryServedAsync(flowId, state.InventoryId, ct).ConfigureAwait(false);
            await ledger.PlanInventoryDuplicateCheckAsync(flowId, checkId, candidates, counter.Take(), ct).ConfigureAwait(false);
            _log.LogInformation(
                "duplicate check {Check}: reading the key ({Key}) of the {Candidates} id(s) the inventory serves",
                checkId, string.Join(", ", key.Properties.Select(p => $"{p.Path} {InventoryDuplicateKey.NameOf(p.Compare)}")), candidates);

            if (candidates > 0)
            {
                await ReadKeysAsync(ledger, search, key, spec, partition, state.InventoryId, checkId, counter, notes, ct).ConfigureAwait(false);
            }

            var grouping = await ledger.GroupInventoryDuplicatesAsync(flowId, checkId, ct).ConfigureAwait(false);
            _log.LogInformation(
                "duplicate check {Check}: {Groups} group(s) of {Copies} id(s) share a key; {Broad} too broad to pick from; reading each group's copies",
                checkId, grouping.Groups, grouping.Copies, grouping.TooBroad);
            if (grouping.TooBroad > 0)
            {
                notes.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{grouping.TooBroad} group(s) hold more than {InventoryDuplicates.MaxCopiesPicked} copies: a key that broad is not one record written more than once, so their copies were not read and they are left for review; add properties to the key."));
            }

            await JudgeGroupsAsync(ledger, client, search, partition, checkId, grouping.Groups, counter, ct).ConfigureAwait(false);

            var read = await ledger.GetInventoryDuplicateCheckAsync(partition, checkId, ct).ConfigureAwait(false);
            if (read is not null && read.Keyed + read.Unkeyed < read.Candidates)
            {
                notes.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{read.Candidates - read.Keyed - read.Unkeyed} id(s) the inventory serves were not returned by the search (the index has not caught up with them, or does not hold them), so their keys were not read and no group holds them."));
            }

            // What the last write left unsaid is counted with the end, so every request sent is in the check's count.
            await ledger.PlanInventoryDuplicateCheckAsync(flowId, checkId, candidates, counter.Take(), ct).ConfigureAwait(false);
            await ledger.CompleteInventoryDuplicateCheckAsync(flowId, checkId, new InventoryDuplicateCheckEnd(InventoryDuplicates.Completed, notes, null), Now, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
            error = "the run was cancelled before the check read OSDU whole; it holds no group";
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
    /// Reads the key of every record of the inventory's kind, narrowed by its query, a page of the search at a time, only the
    /// properties the key compares returned, and keeps each page as it is read. A property no record held a value at is noted:
    /// the search returns only what its index keeps of a record.
    /// </summary>
    private async Task ReadKeysAsync(
        ILedger ledger, OsduSearch search, InventoryDuplicateKey key, InventorySpec spec, string partition, int inventoryId, long checkId,
        SearchRequestCounter counter, List<string> notes, CancellationToken ct)
    {
        IReadOnlyList<string> returned = ["id", "version", .. key.Paths.Where(p => p is not ("id" or "version"))];
        var held = new long[key.Properties.Count];
        var reported = _context.Time.GetUtcNow();
        long read = 0;
        await foreach (var page in search.PagesAsync(new OsduSearchQuery { Kind = spec.Kind, Query = Query(spec, partition), ReturnedFields = returned }, OsduSearch.MaxPage, ct).ConfigureAwait(false))
        {
            var hits = new List<InventoryDuplicateKeyHit>(page.Hits.Count);
            foreach (var hit in page.Hits)
            {
                if (JsonNode.Parse(hit.GetRawText()) is not JsonObject node || IdOf(node) is not { } id)
                {
                    continue;
                }

                var keyed = key.Read(node);
                foreach (var index in keyed.Held)
                {
                    held[index]++;
                }

                hits.Add(new InventoryDuplicateKeyHit(id, VersionOf(node), keyed.Hash, keyed.ValuesJson));
            }

            await ledger.AddInventoryDuplicateKeysAsync(_flow.LedgerId, inventoryId, checkId, hits, page.Hits.Count, counter.Take(), ct).ConfigureAwait(false);
            read += page.Hits.Count;
            var now = _context.Time.GetUtcNow();
            if (now - reported >= RemovalProgressEvery)
            {
                reported = now;
                _log.LogInformation("duplicate check {Check}: {Read} key(s) read so far", checkId, read);
            }
        }

        if (read == 0)
        {
            notes.Add($"The search returned no record of {spec.Kind}{(spec.Query is null ? string.Empty : " within the inventory's query")}, so no key was read.");
            return;
        }

        for (var i = 0; i < key.Properties.Count; i++)
        {
            if (held[i] == 0)
            {
                notes.Add(
                    $"No record the search returned held a value at {key.Properties[i].Path}: the search returns only what its index keeps of a record (a property the kind's schema declares, as its indexing keeps it), so it compared no record. Check the path, or leave it out of the key.");
            }
        }
    }

    /// <summary>
    /// Reads every group's copies and judges the group, a round of groups at a time in order: each copy read whole from storage,
    /// a hundred ids a request, and how many records name each copy storage holds counted through the search, as many at once
    /// as the flow's <c>parallelism.recordsAtOnce</c>; each round written before the next is read.
    /// </summary>
    private async Task JudgeGroupsAsync(
        ILedger ledger, OsduHttpClient client, OsduSearch search, string partition, long checkId, long groups, SearchRequestCounter counter, CancellationToken ct)
    {
        var removable = _flow.Removal is { } policy
            ? policy.Findings.Where(f => InventoryRemovals.Removable.Contains(f, StringComparer.Ordinal)).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var records = new StorageRecords(client, _flow.Source.RecordQueryPath);
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
            var ids = round.SelectMany(g => g.Copies).Select(c => c.TargetId).Distinct(StringComparer.Ordinal).ToList();
            await Parallel.ForEachAsync(ids.Chunk(StorageRecords.Batch), options, async (chunk, token) =>
            {
                var answer = await records.ReadAsync(chunk, null, token).ConfigureAwait(false);
                foreach (var record in answer.Records)
                {
                    if (OsduSearch.IdOf(record) is { } id)
                    {
                        stored[id] = (InventoryDuplicates.Held, StoredVersionOf(record), AttributesOf(record));
                    }
                }

                var unanswered = answer.Unanswered.ToHashSet(StringComparer.Ordinal);
                foreach (var id in answer.Missing)
                {
                    stored.TryAdd(id, (unanswered.Contains(id) ? InventoryDuplicates.Unanswered : InventoryDuplicates.Missing, null, null));
                }
            }).ConfigureAwait(false);

            var mentions = new ConcurrentDictionary<string, (long? Count, string? Problem)>(StringComparer.Ordinal);
            var held = stored.Where(s => s.Value.State == InventoryDuplicates.Held).Select(s => s.Key).ToList();
            await Parallel.ForEachAsync(held, options, async (id, token) =>
            {
                var reading = RecordExplorer.Interpret(new ExplorerSearch { Mentions = id }, partition);
                var answer = await search.PageAsync(new OsduSearchQuery { Kind = EveryKind, Query = reading.Query, ReturnedFields = ["id"] }, 0, 1, null, token).ConfigureAwait(false);
                mentions[id] = answer.Refusal is { } refusal ? (null, HeaderRedaction.RedactMessage(refusal)) : (answer.Total, null);
            }).ConfigureAwait(false);

            var judgements = round
                .Select(g => InventoryDuplicatePicker.Judge(g.GroupNumber, g.Copies.Select(c => CopyOf(c, stored, mentions)).ToList(), removable, chosen: null))
                .ToList();
            await ledger.WriteInventoryDuplicateJudgementsAsync(_flow.LedgerId, checkId, judgements, counter.Take(), Now, ct).ConfigureAwait(false);
            judged += round.Count;
            var now = _context.Time.GetUtcNow();
            if (now - reported >= RemovalProgressEvery)
            {
                reported = now;
                _log.LogInformation("duplicate check {Check}: {Judged} of {Groups} group(s) judged so far", checkId, judged, groups);
            }
        }
    }

    /// <summary>A copy as the picker judges it: what storage answered for it, and how many records name it.</summary>
    private static DuplicateCopy CopyOf(
        InventoryDuplicateCopyRow row, ConcurrentDictionary<string, (string State, long? Version, int? Attributes)> stored,
        ConcurrentDictionary<string, (long? Count, string? Problem)> mentions)
    {
        // An id the storage read named neither held nor missing is one storage was not asked about: it did not answer for it.
        var (state, version, attributes) = stored.TryGetValue(row.TargetId, out var read) ? read : (InventoryDuplicates.Unanswered, null, null);
        var (named, problem) = mentions.TryGetValue(row.TargetId, out var counted) ? counted : (null, null);
        return new DuplicateCopy(row.InventoryRecordId, row.TargetId, row.Finding, state, version, attributes, named, problem);
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
