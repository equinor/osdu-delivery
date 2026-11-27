using SqlFlow.Core.Model;
using SqlFlow.SqlServer.Schema;

namespace SqlFlow.SqlServer.Ingestion;

/// <summary>A column a run would add to the target, or create the target with: its name and SQL type.</summary>
public sealed record IngestionPlanColumn(string Name, string Type);

/// <summary>A column a run would alter in place with one <c>ALTER COLUMN</c>: its type now, the type it gets, and whether
/// SQL Server rewrites every row to change it (a run does that only with <c>schema.allowTableRewrite</c>).</summary>
public sealed record IngestionPlanAlter(string Name, string FromType, string ToType, bool RewritesTable);

/// <summary>
/// What a run of an ingestion flow would do to its target, worked out against the live source and target without changing
/// either (<see cref="IngestionFlowRunner.PlanAsync"/>): whether the target exists, what schema sync would add and alter in
/// place, the critical mismatches that would stop the load, the target columns it keeps although the source no longer has
/// them, and one <see cref="TypeChangeReport"/> per change to an existing column's type, whose status is what a run with the
/// flow's settings would do (<see cref="TypeChangeStatus.Planned"/> when it would apply it).
/// </summary>
public sealed record IngestionPlan
{
    /// <summary>The source object the run reads.</summary>
    public required string Source { get; init; }

    /// <summary>The qualified target table.</summary>
    public required string Target { get; init; }

    public required bool TargetExists { get; init; }

    /// <summary>The flow's <c>schema.sync</c>: off, a run changes no column of the target and the plan lists none.</summary>
    public required bool SchemaSync { get; init; }

    /// <summary>The flow's <c>schema.convertTypes</c> and <c>schema.allowTableRewrite</c>, the opt-ins the statuses follow.</summary>
    public required TypeChangePolicy Policy { get; init; }

    /// <summary>The columns a run would add; for a target that does not exist, every column it would create it with.</summary>
    public IReadOnlyList<IngestionPlanColumn> ColumnsToAdd { get; init; } = [];

    /// <summary>The in-place alters a run's DDL would carry.</summary>
    public IReadOnlyList<IngestionPlanAlter> ColumnsToAlter { get; init; } = [];

    /// <summary>The key, hash or identity columns whose type change stops the run at schema sync, before any of its DDL
    /// runs and before it loads the target. Each is also a tier 3 change in <see cref="TypeChanges"/>.</summary>
    public IReadOnlyList<CriticalMismatch> CriticalMismatches { get; init; } = [];

    /// <summary>The target columns the source no longer has, which a run keeps (it never drops a column).</summary>
    public IReadOnlyList<string> ExtraColumns { get; init; } = [];

    /// <summary>Every change to the type of an existing target column, and every rewrite an earlier run left unfinished, as
    /// a run with the flow's settings would report it.</summary>
    public IReadOnlyList<TypeChangeReport> TypeChanges { get; init; } = [];

    /// <summary>True when a critical mismatch stops the load: a run fails at schema sync, before it loads the target.</summary>
    public bool IsBlocked => CriticalMismatches.Count > 0;

    /// <summary>True when the plan is blocked (a run would fail), or a type change needs an operator (a run would end with
    /// a warning).</summary>
    public bool NeedsAttention => IsBlocked || TypeChanges.Any(c => c.NeedsAttention);
}
