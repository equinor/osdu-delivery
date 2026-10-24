using System.Globalization;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// Selecting an inventory's ids by what OSDU holds (osdu/docs/reference/flow/inventory-selections.md, Selecting ids by what OSDU holds):
/// an operator's conditions on the records' properties, asked of OSDU's search, matched with the inventory's own ids by the
/// OSDU id, and kept as a selection a page shows and a removal takes its ids from.
/// </summary>
public static class InventorySelections
{
    /// <summary>Created, and its match queued as a run of the inventory flow.</summary>
    public const string Queued = "queued";

    /// <summary>Its match is reading OSDU.</summary>
    public const string Running = "running";

    /// <summary>Its match read OSDU whole: its ids are the inventory's that hold what the conditions ask.</summary>
    public const string Completed = "completed";

    /// <summary>Its match stopped; it holds no id, and the reason says why.</summary>
    public const string Failed = "failed";

    /// <summary>Kept past <see cref="KeptFor"/> because a removal took its ids: its conditions and counts stay, its ids are gone.</summary>
    public const string Expired = "expired";

    /// <summary>The match read every record the conditions find in the inventory's kind, and joined them with the inventory's ids.</summary>
    public const string ByConditions = "conditions";

    /// <summary>The match asked the conditions of the inventory's ids, a chunk of them at a time.</summary>
    public const string ByIds = "ids";

    /// <summary>
    /// The most ids one search of an ids-first match names, as a dimension's labels name them
    /// (<c>DimensionLabeler.IdsPerQuery</c>): far inside the 1,024 clauses the service allows, with room for the conditions.
    /// </summary>
    public const int IdsPerQuery = 500;

    /// <summary>The fewest ids one search of an ids-first match names, however many clauses the conditions take.</summary>
    public const int MinIdsPerQuery = 100;

    /// <summary>The clauses the search service allows in one query (openapi search v2).</summary>
    public const int MaxClauses = 1024;

    /// <summary>The clauses kept free beside the ids and the conditions, for the groups and the kind the service adds.</summary>
    public const int SpareClauses = 24;

    /// <summary>The most ids one write of a match keeps, in one transaction.</summary>
    public const int WriteChunk = 5000;

    /// <summary>
    /// How long a selection's ids are kept. A match made longer ago is retired by the next one of its partition: deleted whole,
    /// or, when a removal took its ids, kept with its conditions and counts and its ids deleted (<see cref="Expired"/>).
    /// </summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(7);

    /// <summary>Every status a selection has, in the order it moves through them.</summary>
    public static IReadOnlyList<string> Statuses { get; } = [Queued, Running, Completed, Failed, Expired];

    /// <summary>Whether a selection's match has yet to end.</summary>
    public static bool IsUnderWay(string status) => status is Queued or Running;

    /// <summary>
    /// How many ids one search of an ids-first match names beside conditions taking <paramref name="conditionClauses"/>
    /// clauses: what the service's clause limit leaves, between <see cref="MinIdsPerQuery"/> and <see cref="IdsPerQuery"/>.
    /// </summary>
    public static int IdsPerSearch(int conditionClauses)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(conditionClauses);
        return Math.Clamp(MaxClauses - SpareClauses - conditionClauses, MinIdsPerQuery, IdsPerQuery);
    }

    /// <summary>
    /// Which way a match reads OSDU, the one that sends fewer searches: the records the conditions find
    /// (<paramref name="found"/> of them, a page of <paramref name="pageSize"/> at a time), or the inventory's
    /// <paramref name="candidates"/> ids, <paramref name="idsPerSearch"/> at a time. Records first when both send as many.
    /// </summary>
    public static string Direction(long found, long candidates, int pageSize, int idsPerSearch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(found);
        ArgumentOutOfRangeException.ThrowIfNegative(candidates);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(idsPerSearch, 1);
        var byConditions = (found + pageSize - 1) / pageSize;
        var byIds = (candidates + idsPerSearch - 1) / idsPerSearch;
        return byConditions <= byIds ? ByConditions : ByIds;
    }
}

/// <summary>What a selection starts with: its inventory, who asked, and its conditions as written and as the search is asked them.</summary>
public sealed record InventorySelectionStart(
    int InventoryId, string Actor, string ConditionsJson, string ConditionsKey, string FiltersJson, long? BuildRunId, DateTime CreatedUtc);

/// <summary>One record the search found for a match: its OSDU id, the version the index holds, and the values it holds at the conditions' properties (JSON).</summary>
public sealed record InventorySelectionHit(string TargetId, long? Version, string? ValuesJson);

/// <summary>An id of an inventory OSDU serves, which an ids-first match asks the conditions of.</summary>
public sealed record InventoryServedId(long InventoryRecordId, string TargetId, long? Version);

/// <summary>What one write of a match kept: the inventory's ids it matched, records the inventory serves no row of, and ids at another version.</summary>
public sealed record InventorySelectionWrite(long Matched, long Outside, long Changed);

/// <summary>How a match ended: its status, the query and direction it took, what it had to say, its columns, and why it failed.</summary>
public sealed record InventorySelectionEnd(
    string Status, string? Query, string? Direction, IReadOnlyList<string> Notes, IReadOnlyList<string> Columns, string? Error);

/// <summary>One selection of an inventory's ids as the module's database holds it.</summary>
public sealed record InventorySelectionState
{
    public required long InventorySelectionId { get; init; }

    public required int InventoryId { get; init; }

    public Guid? RunId { get; init; }

    public required string Actor { get; init; }

    public required string ConditionsJson { get; init; }

    public required string ConditionsKey { get; init; }

    public required string FiltersJson { get; init; }

    /// <summary>queued, running, completed, failed or expired.</summary>
    public required string Status { get; init; }

    public string? Query { get; init; }

    public string? Direction { get; init; }

    public string? NotesJson { get; init; }

    public string? ColumnsJson { get; init; }

    public long? BuildRunId { get; init; }

    public long Candidates { get; init; }

    public long ToCheck { get; init; }

    public long Checked { get; init; }

    public long Matched { get; init; }

    public long Outside { get; init; }

    public long Changed { get; init; }

    public long Requests { get; init; }

    public required DateTime CreatedUtc { get; init; }

    public DateTime? StartedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    public string? Error { get; init; }

    /// <summary>The duplicate check whose picks the selection holds; null for a selection a match of conditions filled.</summary>
    public long? DuplicateCheckId { get; init; }

    /// <summary>The key the duplicate check's groups were formed by when the selection was made of its picks; null for one a match filled.</summary>
    public string? DuplicateKeyJson { get; init; }

    /// <summary>What the selection came to, in a line a run's log writes.</summary>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"selection {InventorySelectionId}: {Status}, {Matched} id(s) matched of {Candidates} served, {Outside} outside the inventory, {Changed} changed since its build, {Requests} search request(s){(Error is null ? "" : $"; {Error}")}");
}

/// <summary>One id of a selection, with what the inventory holds of it now and what the match read of it.</summary>
public sealed record InventorySelectionRecord(InventoryRecordState Record, long? MatchedVersion, string? ValuesJson);

/// <summary>What a sweep of a partition's old selections did: those deleted whole, and those kept for a removal with their ids deleted.</summary>
public sealed record InventorySelectionSweep(int Deleted, int Expired);

/// <summary>The values a selection keeps of an id at the properties its conditions ask, as its row holds them (JSON, property to values).</summary>
public static class InventorySelectionValues
{
    /// <summary>The values <paramref name="json"/> holds, property to values; none for no text.</summary>
    /// <exception cref="DeliveryException">The text is not the shape a match writes: the row was written by something else.</exception>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json) is { } held
                ? held.ToDictionary(h => h.Key, h => (IReadOnlyList<string>)h.Value.Where(v => v is not null).ToList(), StringComparer.Ordinal)
                : Empty;
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new DeliveryException($"The values a selection kept of an id are not the property-to-values JSON a match writes ({ex.Message}); match the conditions again.", ex);
        }
    }

    /// <summary>
    /// Values a row keeps of a record, property to values, as JSON within <see cref="Data.DeliveryModel.MaxSelectionValuesLength"/>:
    /// values are left off the end of the longest list until it fits, so a row always says what it holds first, and a property
    /// is dropped once its list is down to one value that still does not fit. Null when nothing is held, or nothing fits.
    /// <paramref name="held"/> is cut in place.
    /// </summary>
    public static string? Fit(Dictionary<string, List<string>> held)
    {
        ArgumentNullException.ThrowIfNull(held);
        while (held.Count > 0)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(held);
            if (json.Length <= Data.DeliveryModel.MaxSelectionValuesLength)
            {
                return json;
            }

            var longest = held.OrderByDescending(h => h.Value.Count).ThenByDescending(h => h.Value.Sum(v => v.Length)).First();
            if (longest.Value.Count > 1)
            {
                longest.Value.RemoveAt(longest.Value.Count - 1);
            }
            else
            {
                held.Remove(longest.Key);
            }
        }

        return null;
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Empty = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
}
