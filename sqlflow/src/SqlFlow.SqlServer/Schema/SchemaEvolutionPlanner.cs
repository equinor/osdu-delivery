using SqlFlow.Core.Model;

namespace SqlFlow.SqlServer.Schema;

/// <summary>
/// Plans how to evolve a target table to the desired schema. For a missing target it creates everything. For an
/// existing target it adds new columns, and classifies every column whose type differs from the source's with
/// <see cref="TypeChangeClassifier"/> into the three <see cref="TypeChangeTier"/>s: the in-place widenings go to
/// <see cref="EvolutionPlan.ColumnsToAlter"/> (the DDL batch), and every change, applied in place or not, is listed in
/// <see cref="EvolutionPlan.TypeChanges"/> for the safe rewrite and the report. A cross-family change on a key, hash, or
/// identity column is also a critical mismatch, which blocks the load. Target-only columns are never dropped, and a
/// nullable target column is never tightened to NOT NULL. Pure.
/// </summary>
public static class SchemaEvolutionPlanner
{
    /// <summary>Plans on the types alone, with no live facts about the table.</summary>
    public static EvolutionPlan Plan(
        IReadOnlyList<SqlColumn> desired,
        IReadOnlyList<SqlColumn>? actual,
        IReadOnlySet<string> keyColumns)
        => Plan(desired, actual, keyColumns, TableDependencies.None, "the target");

    /// <summary>Plans against the live table: <paramref name="table"/> carries its indexes, references and states, which
    /// decide each type change's tier and mechanism. <paramref name="tableName"/> names the table in the manual steps.</summary>
    public static EvolutionPlan Plan(
        IReadOnlyList<SqlColumn> desired,
        IReadOnlyList<SqlColumn>? actual,
        IReadOnlySet<string> keyColumns,
        TableDependencies table,
        string tableName)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(keyColumns);
        ArgumentNullException.ThrowIfNull(table);

        if (actual is null)
        {
            return new EvolutionPlan { CreateTable = true, CreateColumns = desired };
        }

        var actualByName = actual.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var add = new List<SqlColumn>();
        var alter = new List<ColumnAlter>();
        var changes = new List<ColumnTypeChange>();
        var critical = new List<CriticalMismatch>();
        var drift = new List<DriftFinding>();

        foreach (var column in desired)
        {
            if (!actualByName.TryGetValue(column.Name, out var existing))
            {
                add.Add(column);
                continue;
            }

            var change = TypeChangeClassifier.Classify(existing, column, keyColumns.Contains(column.Name), table, tableName);
            if (change is not null)
            {
                changes.Add(change);
                if (change.Mechanism is TypeChangeMechanism.InPlace or TypeChangeMechanism.InPlaceRewrite)
                {
                    alter.Add(new ColumnAlter
                    {
                        Name = existing.Name,
                        FromType = existing.DataType,
                        ToType = change.ToType,
                        IsNullable = existing.IsNullable, // never tighten an existing column's nullability
                        Footprint = change.Footprint,
                    });
                }

                // A key whose type family changes would change what identifies a row: the load must not run on it.
                if (change.IsKey && SqlTypeResolution.Resolve(existing.DataType, column.DataType).Action == SchemaChangeAction.Incompatible)
                {
                    critical.Add(new CriticalMismatch
                    {
                        Column = column.Name,
                        Reason = $"cannot evolve [{existing.DataType.Render()}] to [{column.DataType.Render()}]: {change.Reason}. " +
                                 string.Join(" ", change.ManualSteps),
                    });
                }
            }

            if (!column.IsNullable && existing.IsNullable)
            {
                drift.Add(new DriftFinding
                {
                    Kind = DriftKind.NullabilityNotTightened,
                    Column = column.Name,
                    Detail = "desired NOT NULL but the target column is nullable; left nullable (never tightened on an existing table)",
                });
            }
        }

        var desiredNames = desired.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in actual)
        {
            if (!desiredNames.Contains(existing.Name) && !ColumnRetyper.IsShadowColumnName(existing.Name))
            {
                drift.Add(new DriftFinding
                {
                    Kind = DriftKind.ExtraTargetColumn,
                    Column = existing.Name,
                    Detail = "present in the target, absent from the source; retained (never dropped)",
                });
            }
        }

        return new EvolutionPlan
        {
            ColumnsToAdd = add,
            ColumnsToAlter = alter,
            TypeChanges = changes,
            CriticalMismatches = critical,
            Drift = drift,
        };
    }
}
