using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Inventories;

/// <summary>What a match of an inventory's ids came to.</summary>
public sealed record InventoryMatchOutcome(
    string Operation,
    string Flow,
    string Partition,
    string Inventory,
    long SelectionId,
    string Status,
    string? Direction,
    string? Query,
    long Candidates,
    long Matched,
    long Outside,
    long Changed,
    long Requests,
    IReadOnlyList<string> Notes,
    string? Error)
{
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"match of selection {SelectionId} of '{Inventory}' in '{Partition}': {Matched} of {Candidates} served id(s) hold what its conditions ask, {Outside} record(s) found outside the inventory, {Changed} changed since its build, {Requests} search request(s){(Direction is null ? "" : $", read by {Direction}")}{(Error is null ? "" : $"; stopped: {Error}")}.");
}

/// <summary>A match that stopped: the run ends failed, carrying what it came to, its selection failed and holding no id.</summary>
public sealed class InventoryMatchFailedException : DeliveryException
{
    public InventoryMatchFailedException(InventoryMatchOutcome outcome)
        : base((outcome ?? throw new ArgumentNullException(nameof(outcome))).Describe())
    {
        Outcome = outcome;
    }

    public InventoryMatchOutcome Outcome { get; }
}

public sealed partial class InventoryRunner
{
    /// <summary>
    /// Finds which ids of an inventory hold what an operator's conditions ask in OSDU, and keeps them as the selection the
    /// control plane opened with those conditions (osdu/docs/reference/flow/inventory-selections.md, Selecting ids by what OSDU holds).
    /// The conditions are written as the explorer writes them, a source column read through other records first, and asked of
    /// the search through the flow's own source. The match counts the records they find in the inventory's kind (one
    /// request), then reads them whichever way sends fewer searches: every record they find, joined with the inventory's ids
    /// by the OSDU id; or the inventory's ids a chunk at a time, each chunk asked with the conditions. Each page is kept as it
    /// is read, with the version the index holds and the values each id holds at the conditions' properties, so a page following
    /// the match sees it advance. Nothing in OSDU changes. A match that stops keeps no id, and the run ends failed.
    /// </summary>
    public async Task<InventoryMatchOutcome> MatchAsync(string inventoryName, long selectionId, Guid runId, string actor, CancellationToken ct)
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
                ? $"Inventory flow '{_flow.Name}' has no inventory named '{inventoryName}'. Nothing was matched."
                : $"Inventory '{spec.Name}' has not been built in '{partition}', so it holds no id to match; build it first. Nothing was matched.";
            await ledger.FailInventorySelectionAsync(partition, selectionId, why, Now, CancellationToken.None).ConfigureAwait(false);
            throw new DeliveryException(why);
        }

        if (await ledger.GetInventorySelectionAsync(partition, selectionId, ct).ConfigureAwait(false) is { DuplicateCheckId: { } check })
        {
            throw new DeliveryException(
                $"Selection {selectionId} of '{partition}' holds the copies duplicate check {check} picked to remove; a match fills only a selection opened with conditions. Nothing was matched.");
        }

        var selection = await ledger.StartInventorySelectionAsync(flowId, state.InventoryId, selectionId, runId, state.LastBuildRunId, Now, ct).ConfigureAwait(false)
            ?? throw new DeliveryException(
                $"Inventory '{spec.Name}' in '{partition}' holds no selection {selectionId}: it was opened for another inventory, or retired since. Nothing was matched.");
        _log.LogInformation(
            "inventory '{Inventory}' in '{Partition}', selection {Selection}: matching what its conditions ask, through {Endpoint}, as {Actor}",
            spec.Name, partition, selectionId, _flow.Source.Endpoint, actor);

        var notes = new List<string>();
        IReadOnlyList<string> columns = [];
        string? query = null;
        string? direction = null;
        var counter = new SearchRequestCounter(_flow.Source, _context.HttpObserver);
        string? error = null;
        var cancelled = false;
        try
        {
            var filters = SelectionFilters(selection.FiltersJson, spec.Kind);
            using var http = new HttpRuntime(_flow.Reliability, _context.Secrets, _context.Time, _transport, _allowLoopback, observer: counter);
            var client = await ProtocolFactory.ClientAsync(http, _flow.Source.Endpoint, _flow.Source.Auth, _flow.Source.Headers, _context.Secrets, ct).ConfigureAwait(false);
            var search = new OsduSearch(client, _flow.Source.QueryPath, _flow.Source.SearchPath, _log, _flow.Source.Paging);

            // A source column reached through other records is read first, as the explorer reads it, into a condition on the
            // inventory's own records; every condition is then one clause of the query.
            var asked = await new RecordExplorer(search, partition, _log)
                .ThroughOthersAsync(new ExplorerSearch { Kind = spec.Kind, Filters = filters }, notes, ct).ConfigureAwait(false);
            query = RecordExplorer.Compose(null, asked.Filters);
            columns = SelectionColumns(asked.Filters);
            IReadOnlyList<string> returned = ["id", "version", .. columns];
            var candidates = await ledger.CountInventoryServedAsync(flowId, state.InventoryId, ct).ConfigureAwait(false);

            // The records the conditions find among those the inventory reads: its own query narrows them, as its build did.
            var narrowed = Query(spec, partition);
            var scope = query is null ? narrowed : narrowed is null ? query : OsduSearch.Within(narrowed, query);
            var found = candidates == 0 ? 0 : await search.CountAsync(new OsduSearchQuery { Kind = spec.Kind, Query = scope, ReturnedFields = ["id"] }, ct).ConfigureAwait(false);
            var perSearch = InventorySelections.IdsPerSearch(Clauses(asked.Filters));
            direction = InventorySelections.Direction(found, candidates, OsduSearch.MaxPage, perSearch);
            await ledger.PlanInventorySelectionAsync(
                flowId, selectionId, candidates, direction == InventorySelections.ByConditions ? found : candidates, direction, query, counter.Take(), ct).ConfigureAwait(false);
            _log.LogInformation(
                "selection {Selection}: the conditions find {Found} record(s) of {Kind}, and the inventory serves {Candidates} id(s); reading by {Direction}{PerSearch}",
                selectionId, found, spec.Kind, candidates, direction,
                direction == InventorySelections.ByIds ? string.Create(CultureInfo.InvariantCulture, $", {perSearch} id(s) a search") : string.Empty);

            if (found > 0)
            {
                if (direction == InventorySelections.ByConditions)
                {
                    await MatchByConditionsAsync(ledger, search, spec.Kind, scope, returned, columns, state.InventoryId, selectionId, counter, ct).ConfigureAwait(false);
                }
                else
                {
                    await MatchByIdsAsync(ledger, search, spec.Kind, query, returned, columns, state.InventoryId, selectionId, perSearch, counter, notes, ct).ConfigureAwait(false);
                }
            }

            // What the last write left unsaid is counted with the end, so every request sent is in the selection's count.
            await ledger.AddInventorySelectionItemsAsync(flowId, state.InventoryId, selectionId, [], 0, counter.Take(), ct).ConfigureAwait(false);
            await ledger.CompleteInventorySelectionAsync(
                flowId, selectionId, new InventorySelectionEnd(InventorySelections.Completed, query, direction, notes, columns, null), Now, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
            error = "the run was cancelled before the match read OSDU whole; it holds no id";
        }
        catch (Exception ex) when (Expected(ex, ct) || ex is OsduQueryException)
        {
            error = HeaderRedaction.RedactMessage(ex.Message);
        }

        if (error is not null)
        {
            _log.LogError("selection {Selection}: the match stopped: {Error}", selectionId, error);
            await ledger.CompleteInventorySelectionAsync(
                flowId, selectionId, new InventorySelectionEnd(InventorySelections.Failed, query, direction, notes, columns, error), Now, CancellationToken.None).ConfigureAwait(false);
        }

        var ended = await ledger.GetInventorySelectionAsync(partition, selectionId, CancellationToken.None).ConfigureAwait(false);
        var outcome = new InventoryMatchOutcome(
            DeliveryOperations.Match, _flow.Name, partition, spec.Name, selectionId, ended?.Status ?? InventorySelections.Failed, direction, query,
            ended?.Candidates ?? 0, ended?.Matched ?? 0, ended?.Outside ?? 0, ended?.Changed ?? 0, ended?.Requests ?? counter.Sent, notes, error);
        _log.LogInformation("{Outcome}", outcome.Describe());
        foreach (var note in notes)
        {
            _log.LogInformation("selection {Selection}: {Note}", selectionId, note);
        }

        if (cancelled)
        {
            ct.ThrowIfCancellationRequested();
        }

        return error is null ? outcome : throw new InventoryMatchFailedException(outcome);
    }

    /// <summary>Every record the conditions find in the inventory's kind, a page at a time, each page joined with the inventory's ids as it is read.</summary>
    private async Task MatchByConditionsAsync(
        ILedger ledger, OsduSearch search, string kind, string? scope, IReadOnlyList<string> returned, IReadOnlyList<string> columns, int inventoryId, long selectionId,
        SearchRequestCounter counter, CancellationToken ct)
    {
        var reported = _context.Time.GetUtcNow();
        long read = 0;
        await foreach (var page in search.PagesAsync(new OsduSearchQuery { Kind = kind, Query = scope, ReturnedFields = returned }, OsduSearch.MaxPage, ct).ConfigureAwait(false))
        {
            var hits = new List<InventorySelectionHit>(page.Hits.Count);
            foreach (var hit in page.Hits)
            {
                if (SelectionHit(hit, columns) is { } kept)
                {
                    hits.Add(kept);
                }
            }

            await ledger.AddInventorySelectionItemsAsync(_flow.LedgerId, inventoryId, selectionId, hits, page.Hits.Count, counter.Take(), ct).ConfigureAwait(false);
            read += page.Hits.Count;
            reported = Progress(selectionId, read, reported);
        }
    }

    /// <summary>
    /// The inventory's served ids in its own order, <paramref name="perSearch"/> at a time, each chunk asked with the conditions,
    /// so a broad condition costs a search per chunk however many records of the kind it finds. An id that cannot be written into a
    /// search is left out of it, and a note counts them.
    /// </summary>
    private async Task MatchByIdsAsync(
        ILedger ledger, OsduSearch search, string kind, string? query, IReadOnlyList<string> returned, IReadOnlyList<string> columns, int inventoryId, long selectionId,
        int perSearch, SearchRequestCounter counter, List<string> notes, CancellationToken ct)
    {
        var idField = OsduField.Keyword("id");
        var reported = _context.Time.GetUtcNow();
        long after = 0;
        long read = 0;
        long unasked = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = await ledger.InventoryServedAsync(_flow.LedgerId, inventoryId, after, perSearch, ct).ConfigureAwait(false);
            if (chunk.Count == 0)
            {
                break;
            }

            after = chunk[^1].InventoryRecordId;
            var ids = new List<string>(chunk.Count);
            foreach (var served in chunk)
            {
                try
                {
                    _ = OsduQuery.Equal(idField, served.TargetId);
                    ids.Add(served.TargetId);
                }
                catch (OsduQueryException)
                {
                    unasked++;
                }
            }

            var hits = new List<InventorySelectionHit>(ids.Count);
            if (ids.Count > 0)
            {
                var named = OsduQuery.AnyOf(idField, ids).Text;
                var asked = OsduSearch.Within(query, named);
                var (_, found) = await search.FirstAsync(new OsduSearchQuery { Kind = kind, Query = asked, ReturnedFields = returned }, ids.Count, ct).ConfigureAwait(false);
                foreach (var hit in found)
                {
                    if (SelectionHit(hit, columns) is { } kept)
                    {
                        hits.Add(kept);
                    }
                }
            }

            await ledger.AddInventorySelectionItemsAsync(_flow.LedgerId, inventoryId, selectionId, hits, chunk.Count, counter.Take(), ct).ConfigureAwait(false);
            read += chunk.Count;
            reported = Progress(selectionId, read, reported);
            if (chunk.Count < perSearch)
            {
                break;
            }
        }

        if (unasked > 0)
        {
            notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{unasked} id(s) of the inventory could not be written into a search, so no condition was asked of them and none is selected."));
        }
    }

    /// <summary>Writes how far a match got at most every <see cref="RemovalProgressEvery"/>, so a match of millions writes a few dozen lines.</summary>
    private DateTimeOffset Progress(long selectionId, long read, DateTimeOffset reported)
    {
        var now = _context.Time.GetUtcNow();
        if (now - reported < RemovalProgressEvery)
        {
            return reported;
        }

        _log.LogInformation("selection {Selection}: {Read} read so far", selectionId, read);
        return now;
    }

    /// <summary>
    /// The conditions a selection keeps as the search is asked them (an explorer search, <see cref="ExplorerSearch.ToJson"/>),
    /// read back and checked as the control plane checked them, since they cross a trust boundary in the module's database, and
    /// checked again against the kind the inventory reads now.
    /// </summary>
    internal static IReadOnlyList<ExplorerFilter> SelectionFilters(string searchJson, string kind)
    {
        var search = ExplorerSearch.Parse(searchJson);
        if (search.Filters.Count == 0)
        {
            throw new DeliveryException("The selection holds no condition, so it would match every id; ask for a match with at least one condition.");
        }

        var problem = (search with { Kind = kind }).Problem();
        return problem is null ? search.Filters : throw new DeliveryException($"The selection's conditions cannot be asked of {kind}: {problem}");
    }

    /// <summary>
    /// The properties a selection's rows show the values of: those its conditions ask, each once, but the id and the kind,
    /// which every row shows already, at most as many as a page of the explorer shows.
    /// </summary>
    internal static IReadOnlyList<string> SelectionColumns(IReadOnlyList<ExplorerFilter> filters)
        => filters
            .Select(f => f.Path)
            .Where(p => p is not ("id" or "kind") && OsduPath.IsPath(p))
            .Distinct(StringComparer.Ordinal)
            .Take(ExplorerSearch.MaxColumns)
            .ToList();

    /// <summary>The clauses the conditions take in a query: one each, or one per value compared.</summary>
    internal static int Clauses(IReadOnlyList<ExplorerFilter> filters)
        => filters.Sum(f => f.Condition is ExplorerCondition.AnyOf or ExplorerCondition.NoneOf ? Math.Max(1, f.Compared().Count) : 1);

    /// <summary>
    /// A record the search found, as a selection keeps it: its OSDU id, the version the index holds, and the values it holds at
    /// <paramref name="columns"/>; null for a hit without an id, or with one longer than an inventory keeps.
    /// </summary>
    private static InventorySelectionHit? SelectionHit(JsonElement hit, IReadOnlyList<string> columns)
        => JsonNode.Parse(hit.GetRawText()) is JsonObject node ? SelectionHit(node, columns) : null;

    private static InventorySelectionHit? SelectionHit(JsonObject hit, IReadOnlyList<string> columns)
    {
        var id = hit["id"] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : null;
        if (string.IsNullOrEmpty(id) || id.Length > DeliveryModel.MaxTargetIdLength)
        {
            return null;
        }

        long? version = hit["version"] is JsonValue v && (v.TryGetValue<long>(out var number)
            || (v.TryGetValue<string>(out var digits) && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number)))
            ? number
            : null;
        return new InventorySelectionHit(id, version, SelectionValues(hit, columns));
    }

    /// <summary>
    /// What a hit holds at each column, as JSON (property to values), within what a selection keeps of it: values are left off the
    /// end of the longest list until it fits, so a row always says what it holds first. Null when it holds nothing there.
    /// </summary>
    internal static string? SelectionValues(JsonObject hit, IReadOnlyList<string> columns)
    {
        var held = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            if (RecordExplorer.ValuesAt(hit, column) is { Count: > 0 } values)
            {
                held[column] = [.. values];
            }
        }

        return InventorySelectionValues.Fit(held);
    }
}
