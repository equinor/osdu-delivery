using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Rendering;

/// <summary>What a render looked for and did not find (<see cref="RenderMiss"/>).</summary>
public enum RenderMissKind
{
    /// <summary>The source value the node reads is empty, so there was nothing to write, look up or search for.</summary>
    Empty,

    /// <summary>An id the node wrote or built names no record of its entity type that the cache version holds.</summary>
    IdNotHeld,

    /// <summary>A <c>$cache</c> node's findBy lines found no record of the type holding the value at the fields they compare.</summary>
    NoMatch,

    /// <summary>Several records of the type answer to the value once case (or punctuation) is ignored, so none is taken.</summary>
    Ambiguous,

    /// <summary>The cache version holds no type of the name the node reads.</summary>
    NoType,

    /// <summary>The cached records found hold nothing at the field the node reads: what is missing is in the cache, not the source.</summary>
    FieldEmpty,

    /// <summary>A search of the platform found no record holding the value at the fields it searched.</summary>
    NotFound,
}

/// <summary>One way a value was looked for: the field compared, the source column the value came from, and the value as compared.</summary>
/// <param name="Field">The field of the cached type, or of the searched kind, the value was compared with.</param>
/// <param name="Column">The source column the value was read from; null for a literal or a value the node computed.</param>
/// <param name="Value">The value as it was compared, after the node's modifiers.</param>
/// <param name="And">
/// True when the value was compared together with the one before it, on one findBy line written with <c>and</c>: a record had
/// to hold both, where the lines themselves are tried one after another.
/// </param>
public sealed record RenderMissTry(string Field, DatasetColumn? Column, string Value, bool And = false);

/// <summary>
/// Why one node of a mapping gave no value for a row, as the render met it: what it looked for, where, and with what. The render
/// states the same as a sentence (<see cref="Reason"/>); this keeps its parts, so a check can say which cache type, field and
/// value to look at and write the queries that show it, without reading the sentence back.
/// </summary>
public sealed record RenderMiss
{
    public required RenderMissKind Kind { get; init; }

    /// <summary>The reason as the render states it, the variable first.</summary>
    public required string Reason { get; init; }

    /// <summary>The source columns behind the value, as the mapping names them: the ones the value was read from.</summary>
    public IReadOnlyList<DatasetColumn> Columns { get; init; } = [];

    /// <summary>
    /// The cached types looked in: the one a <c>$cache</c> node or a lookup reads, or for an id every type of the version that
    /// holds records of its entity type; for a search, the search the mapping declares. Empty for an empty value.
    /// </summary>
    public IReadOnlyList<string> Types { get; init; } = [];

    /// <summary>For an id, the entity type it names a record of; for a search, the kind searched.</summary>
    public string? EntityType { get; init; }

    /// <summary>Each way the value was looked for, in the order the node tried them.</summary>
    public IReadOnlyList<RenderMissTry> Tries { get; init; } = [];

    /// <summary>For several records answering to a value, their ids.</summary>
    public IReadOnlyList<string> Candidates { get; init; } = [];

    /// <summary>Whether the node also compared with punctuation and spacing folded away (<c>$ignoreSeparators</c>).</summary>
    public bool IgnoreSeparators { get; init; }
}
