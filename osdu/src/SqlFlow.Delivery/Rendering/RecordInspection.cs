using System.Text.Json.Nodes;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Rendering;

/// <summary>What one variable of a record came to for one row: written, left out, not meant for the row, or held.</summary>
public enum EntryOutcomeKind
{
    /// <summary>The entry wrote a value.</summary>
    Value,

    /// <summary>The entry wrote nothing and the record goes without the variable: its value was empty, and the entry is optional.</summary>
    Empty,

    /// <summary>
    /// The mapping means no value for this row: the entry's <c>$when</c> does not hold, or an entry of a repeated item met no
    /// item to write, since the row has no child row its repeater takes.
    /// </summary>
    NotApplicable,

    /// <summary>The entry could not give a value and holds the record, with the reasons the render holds it for.</summary>
    Held,

    /// <summary>
    /// The entry waits on a search the platform has not been asked yet. Inspecting the row again once it is answered settles
    /// the outcome; a question that never gets an answer holds the record, as a run holds it.
    /// </summary>
    Waiting,
}

/// <summary>What one entry gave for one row, or for one item of a repeated array of the row.</summary>
public sealed record EntryOutcome
{
    /// <summary>
    /// The variable: the entry's target, or, for what the record requires of <c>data</c> that no entry fills directly, the
    /// variable's path (<c>osdu.data.VerticalMeasurement</c>).
    /// </summary>
    public required string Target { get; init; }

    /// <summary>The entry that fills the variable, or null for a required variable of <c>data</c> filled through what it holds.</summary>
    public MappingEntry? Entry { get; init; }

    /// <summary>
    /// For an entry of a repeated item: the child row the item is written from, counting from 0 in the order the source
    /// read the child dataset. Null for a variable of the record itself.
    /// </summary>
    public int? Item { get; init; }

    public required EntryOutcomeKind Kind { get; init; }

    /// <summary>The value written, converted to the variable's type; null for every other outcome.</summary>
    public JsonNode? Value { get; init; }

    /// <summary>
    /// Why: the hold reasons of a held outcome, the question of a waiting one, why the value was empty for an empty one, and
    /// the condition that does not hold for one that does not apply. Empty for a value.
    /// </summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];
}

/// <summary>
/// One record inspected entry by entry (<see cref="MappingRenderer.Inspect"/>): the record the selected entries assemble, and
/// what each of them gave on the way. With every entry selected, the document is the one a delivery render writes before it
/// normalizes and hashes it, and the holds are the ones it holds the record for.
/// </summary>
public sealed record RecordInspection
{
    public DeliveryKey? Key { get; init; }

    public required string SourceKey { get; init; }

    /// <summary>The record as the selected entries assemble it: the whole record when every entry is selected.</summary>
    public required JsonObject Document { get; init; }

    /// <summary>What each selected entry gave, in the order the render met them: the record's own entries, then each repeater's items.</summary>
    public required IReadOnlyList<EntryOutcome> Outcomes { get; init; }

    /// <summary>Every reason the selected entries hold the record for, as a render states them.</summary>
    public required IReadOnlyList<string> Holds { get; init; }

    /// <summary>Questions the platform has to answer before the outcomes that wait on them settle.</summary>
    public IReadOnlyList<PlatformQuestion> Unanswered { get; init; } = [];

    /// <summary>
    /// What the assertions of the entries evaluated found (osdu/docs/reference/flow/mapping-assertions.md), or null when the
    /// mapping states none. Nothing a failure would leave out is left out of <see cref="Document"/>.
    /// </summary>
    public Validation.AssertionFindings? Assertions { get; init; }

    public bool IsIncomplete => Unanswered.Count > 0;
}

/// <summary>
/// Which variables an inspection evaluates: every entry, or the ones reaching the variables named. An entry reaches a named
/// variable when it fills that variable, something inside it, or a value that holds it (a list of objects written whole
/// holds the properties of its items). What the record checks as a whole (the data a schema requires, a required array
/// with no item) is checked only for a variable whose every entry is evaluated.
/// </summary>
public sealed class EntrySelection
{
    private readonly IReadOnlyList<string> _paths;

    private EntrySelection(IReadOnlyList<string> paths) => _paths = paths;

    /// <summary>Every entry of the mapping.</summary>
    public static EntrySelection All { get; } = new([]);

    /// <summary>The entries reaching <paramref name="paths"/>; none named selects every entry.</summary>
    public static EntrySelection Of(IEnumerable<TemplatePath> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var texts = paths.Select(p => p.Text).Distinct(StringComparer.Ordinal).ToList();
        return texts.Count == 0 ? All : new EntrySelection(texts);
    }

    public bool IsAll => _paths.Count == 0;

    /// <summary>The variables named, as template paths; empty when every entry is selected.</summary>
    public IReadOnlyList<string> Paths => _paths;

    /// <summary>Whether an entry filling <paramref name="target"/> is evaluated: it fills a named variable, something inside one, or a value holding one.</summary>
    public bool Includes(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return IsAll || _paths.Any(path => Within(target, path) || Within(path, target));
    }

    /// <summary>Whether every entry that could fill <paramref name="target"/> is evaluated, so what the record checks of it as a whole holds.</summary>
    public bool Covers(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return IsAll || _paths.Any(path => Within(target, path));
    }

    /// <summary>Whether <paramref name="inner"/> is <paramref name="outer"/> or lies inside it: a property of it, or of its items.</summary>
    public static bool Within(string inner, string outer)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(outer);
        return string.Equals(inner, outer, StringComparison.Ordinal)
            || (inner.Length > outer.Length
                && inner.StartsWith(outer, StringComparison.Ordinal)
                && (inner[outer.Length] == '.' || inner.AsSpan(outer.Length).StartsWith("[].", StringComparison.Ordinal)));
    }
}

/// <summary>
/// The outcomes one inspection collects as the render meets the selected entries. It belongs to one inspection, never to
/// the renderer, which parallel workers share.
/// </summary>
internal sealed class InspectionTrail(EntrySelection selection)
{
    private readonly List<EntryOutcome> _outcomes = [];

    public EntrySelection Selection => selection;

    public IReadOnlyList<EntryOutcome> Outcomes => _outcomes;

    public void Add(EntryOutcome outcome) => _outcomes.Add(outcome);

    /// <summary>
    /// What the record found of a variable of <c>data</c> the schema requires and the render left out: the variable's own
    /// outcome is held for it, unless it still waits on a search that decides it; a variable no entry fills directly gains
    /// an outcome of its own.
    /// </summary>
    public void Required(string target, string reason)
    {
        for (var i = 0; i < _outcomes.Count; i++)
        {
            var outcome = _outcomes[i];
            if (outcome.Item is not null || !string.Equals(outcome.Target, target, StringComparison.Ordinal))
            {
                continue;
            }

            if (outcome.Kind != EntryOutcomeKind.Waiting)
            {
                IReadOnlyList<string> earlier = outcome.Kind == EntryOutcomeKind.Held ? outcome.Reasons : [];
                _outcomes[i] = outcome with { Kind = EntryOutcomeKind.Held, Value = null, Reasons = [.. earlier, reason] };
            }

            return;
        }

        _outcomes.Add(new EntryOutcome { Target = target, Kind = EntryOutcomeKind.Held, Reasons = [reason] });
    }
}
