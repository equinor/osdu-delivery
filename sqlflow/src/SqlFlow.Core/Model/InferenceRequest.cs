namespace SqlFlow.Core.Model;

/// <summary>
/// A standalone inference job - the dedicated input to the infer process, independent of a pipeline
/// flow definition. Points at a loaded table to profile and carries the inference policy.
/// </summary>
public sealed record InferenceRequest
{
    public required string Connection { get; init; }
    public required string Schema { get; init; }
    public required string Table { get; init; }
    public TypeInferencePolicy Policy { get; init; } = new();

    /// <summary>The type each undeclared column already has in the typed view, by column name. Inference never moves a
    /// column out of the type it has (text stays text, a typed column keeps its type and only widens within its family):
    /// only a declared type changes an existing column's type. Empty on the view's first build.</summary>
    public IReadOnlyDictionary<string, string> ExistingTypes { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The columns to profile, by name; empty profiles every column of the table.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    public string QualifiedName => $"[{Schema}].[{Table}]";
}
