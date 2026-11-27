using System.Globalization;
using System.Text.Json.Serialization;
using SqlFlow.Core.Model;

namespace SqlFlow.SqlServer.Schema;

/// <summary>How a type change is carried out.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TypeChangeMechanism>))]
public enum TypeChangeMechanism
{
    /// <summary>One <c>ALTER TABLE ... ALTER COLUMN</c> recorded in metadata only.</summary>
    InPlace,

    /// <summary>One <c>ALTER TABLE ... ALTER COLUMN</c> that rewrites every row under a schema-modification lock held for
    /// the whole statement. Used only where the safe rewrite cannot run (a system-versioned table), and only for a
    /// widening.</summary>
    InPlaceRewrite,

    /// <summary>The safe rewrite: a shadow column of the new type, filled in batches while a trigger keeps it in step,
    /// then swapped in under a short schema lock (<see cref="ColumnRetyper"/>).</summary>
    ShadowRewrite,

    /// <summary>Not applied by the engine (tier 3).</summary>
    None,
}

/// <summary>When in a run a type change is applied relative to the load.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TypeChangeTiming>))]
public enum TypeChangeTiming
{
    /// <summary>With schema sync, before the load: the load then writes the source's values into the new type.</summary>
    BeforeLoad,

    /// <summary>After the load commits: a text column becoming typed is converted once the load has written the
    /// source's current rendering into it, so a full-load run re-renders stored values before they are checked.</summary>
    AfterLoad,
}

/// <summary>
/// One pending change to the type of a column that already exists in a target table, as the
/// <see cref="TypeChangeClassifier"/> classified it. Pure data: the plan, the run log, run.json and the catalog all
/// describe a change through this record.
/// </summary>
public sealed record ColumnTypeChange
{
    public required string Column { get; init; }

    /// <summary>The column's type in the table now.</summary>
    public required SqlDataType FromType { get; init; }

    /// <summary>The type the source declares for the column.</summary>
    public required SqlDataType SourceType { get; init; }

    /// <summary>The type the change gives the column: the widened merge for a widening, the source's type otherwise.</summary>
    public required SqlDataType ToType { get; init; }

    public required TypeChangeTier Tier { get; init; }

    public required ChangeFootprint Footprint { get; init; }

    public required TypeChangeMechanism Mechanism { get; init; }

    public TypeChangeTiming Timing { get; init; } = TypeChangeTiming.BeforeLoad;

    /// <summary>True when the target already holds a wider type than the source declares.</summary>
    public bool IsNarrowing { get; init; }

    /// <summary>True for a key, hash, identity or primary-key column.</summary>
    public bool IsKey { get; init; }

    /// <summary>The indexes that key or include the column (recreated by the safe rewrite).</summary>
    public IReadOnlyList<string> Indexes { get; init; } = [];

    /// <summary>The objects that reference the column and stop the engine changing it (computed columns,
    /// constraints, foreign keys, schema-bound modules), each described for a reader.</summary>
    public IReadOnlyList<string> References { get; init; } = [];

    /// <summary>The flow setting the change needs, or null when it applies without one.</summary>
    public string? RequiresOptIn { get; init; }

    /// <summary>Why the change is in its tier, in one sentence.</summary>
    public required string Reason { get; init; }

    /// <summary>For tier 3, what an operator does instead; empty otherwise.</summary>
    public IReadOnlyList<string> ManualSteps { get; init; } = [];

    /// <summary>The setting names, as YAML spells them.</summary>
    public const string AllowTableRewrite = "schema.allowTableRewrite";

    public const string ConvertTypes = "schema.convertTypes";

    /// <summary>One line naming the column, the types, the tier and the footprint, for a log or a plan.</summary>
    public string Describe(string table)
    {
        var footprint = Footprint == ChangeFootprint.MetadataOnly ? "metadata only" : "rewrites the table";
        var facts = new List<string> { $"tier {(int)Tier} {TierName(Tier)}", footprint };
        if (IsKey)
        {
            facts.Add("key column");
        }

        if (Indexes.Count > 0)
        {
            facts.Add("indexed by " + string.Join(", ", Indexes.Select(i => $"[{i}]")));
        }

        if (References.Count > 0)
        {
            facts.Add("referenced by " + string.Join(", ", References));
        }

        var target = FromType == ToType ? SourceType : ToType;
        return string.Create(CultureInfo.InvariantCulture,
            $"{table}.[{Column}] {FromType.Render()} -> {target.Render()} ({string.Join("; ", facts)})");
    }

    public static string TierName(TypeChangeTier tier) => tier switch
    {
        TypeChangeTier.Widening => "widening",
        TypeChangeTier.CheckedConversion => "checked conversion",
        _ => "manual",
    };
}

/// <summary>Builds the <see cref="TypeChangeReport"/> of a classified change.</summary>
public static class TypeChangeReports
{
    public static TypeChangeReport From(
        ColumnTypeChange change, string layer, string table, TypeChangeStatus status, string message,
        long? rows = null, long? failing = null, IReadOnlyList<TypeChangeValueSample>? sample = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        var to = change.FromType == change.ToType ? change.SourceType : change.ToType;
        return new TypeChangeReport
        {
            Layer = layer,
            Table = table,
            Column = change.Column,
            FromType = change.FromType.Render(),
            ToType = to.Render(),
            Tier = change.Tier,
            Status = status,
            RewritesTable = change.Footprint == ChangeFootprint.TableRewrite,
            IsKey = change.IsKey,
            Indexes = change.Indexes,
            References = change.References,
            RequiresOptIn = change.RequiresOptIn,
            Rows = rows,
            FailingValues = failing,
            Sample = sample ?? [],
            Message = message,
            ManualSteps = change.ManualSteps,
        };
    }
}
