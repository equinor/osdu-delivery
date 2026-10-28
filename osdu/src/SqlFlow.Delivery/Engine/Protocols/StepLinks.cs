using System.Text.Json.Nodes;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// The links a record's steps of <c>enrich:</c> own in the lists an update carries forward from the version OSDU holds
/// (osdu/docs/reference/flow/enrich-records.md, Preserved keys): the datasets its steps link now, registered by this delivery or
/// kept for a step set aside, and those its last delivery linked. A list the steps own links in is merged rather than copied
/// whole: OSDU's items stay, other systems' among them, the step's links the document has are added, and the step's links it
/// no longer has are taken off. Each link is compared as the record holds it, the dataset's id and a colon
/// (<see cref="EnrichParts.Link"/>).
/// </summary>
internal sealed class StepLinks
{
    private readonly HashSet<string> _current;
    private readonly HashSet<string> _previous;

    private StepLinks(IEnumerable<string> current, IEnumerable<string> previous)
    {
        _current = current.Select(EnrichParts.Link).ToHashSet(StringComparer.Ordinal);
        _previous = previous.Select(EnrichParts.Link).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>No step links: every preserved key is copied whole, as a record of a flow without <c>enrich:</c> has it.</summary>
    public static StepLinks None { get; } = new([], []);

    /// <summary>The links the steps of <paramref name="work"/>'s record own: those its document has now, and those its last delivery wrote.</summary>
    public static StepLinks Of(DeliveryWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return new StepLinks(
            work.StepParts.Select(p => p.Part.RecordId).Concat(work.StepKeptRecords),
            EnrichParts.Records(work.TargetState));
    }

    /// <summary>The links of <paramref name="current"/> datasets now and <paramref name="previous"/> ones before, for a test or a caller without a delivery.</summary>
    public static StepLinks Of(IEnumerable<string> current, IEnumerable<string> previous)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previous);
        return new StepLinks(current, previous);
    }

    /// <summary>
    /// The list an update writes in place of <paramref name="stored"/>, the preserved list OSDU holds, given what the document
    /// renders there (<paramref name="rendered"/>, null when it renders nothing): OSDU's items in OSDU's order, less the step's
    /// links the last delivery wrote that the document no longer has, then the step's links the document has that OSDU's list
    /// lacks, each once. Null when the steps own no link of the list, now or before: the list is then copied whole.
    /// </summary>
    public JsonArray? Merge(JsonArray stored, JsonArray? rendered)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var owned = (rendered ?? []).Select(Text).OfType<string>().Where(_current.Contains).Distinct(StringComparer.Ordinal).ToList();
        if (owned.Count == 0 && !stored.Any(item => Text(item) is { } text && _previous.Contains(text)))
        {
            return null;
        }

        var keep = owned.ToHashSet(StringComparer.Ordinal);
        var present = new HashSet<string>(StringComparer.Ordinal);
        var merged = new JsonArray();
        foreach (var item in stored)
        {
            if (Text(item) is { } text && (keep.Contains(text) ? !present.Add(text) : _previous.Contains(text)))
            {
                // A step's link the document no longer has, or one the list holds twice.
                continue;
            }

            merged.Add(item?.DeepClone());
        }

        foreach (var link in owned.Where(link => !present.Contains(link)))
        {
            merged.Add(JsonValue.Create(link));
        }

        return merged;
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
