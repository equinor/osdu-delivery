using SqlFlow.Core.Model;

namespace SqlFlow.SqlServer.Schema;

/// <summary>
/// Puts a difference between a target column's type and the type its source now declares into one of the three
/// <see cref="TypeChangeTier"/>s, and says how it would be carried out. Pure: the live facts that matter (indexes,
/// references, table states) come in as <see cref="TableDependencies"/>.
/// <list type="bullet">
/// <item><b>Widening</b>: a same-family merge that holds every value of the old type (<see cref="SqlTypeResolution"/>),
/// plus <c>date</c> to <c>datetime2</c>. In place when SQL Server records it in metadata
/// (<see cref="ChangeFootprintClassifier.IsVariableLengthGrowth"/>), otherwise the safe rewrite under
/// <c>schema.allowTableRewrite</c>.</item>
/// <item><b>Checked conversion</b>, only on a column that is not a key, identity, hash or primary-key column: text to
/// <c>tinyint</c>/<c>smallint</c>/<c>int</c>/<c>bigint</c>/<c>decimal</c>/<c>date</c>/<c>datetime2</c>/<c>bit</c>/
/// <c>uniqueidentifier</c>; one of those to <c>varchar</c>/<c>nvarchar</c>; a shorter text or <c>nvarchar</c> to
/// <c>varchar</c>; an integer to a <c>decimal</c> with room for every value of it. Applied by the safe rewrite under
/// <c>schema.convertTypes</c>, after a check that every stored value converts without loss.</item>
/// <item><b>Manual</b>: everything else, and every change the safe rewrite cannot carry (a key column, a lossy change, a
/// system-versioned, columnstore, CDC-tracked or partitioned table, a column a computed column, a constraint, a foreign
/// key, a schema-bound module or a clustered index references, a table with no identity or primary key).</item>
/// </list>
/// A narrowing other than of text is never a change: the column keeps its wider type, which holds every source value.
/// </summary>
public static class TypeChangeClassifier
{
    /// <summary>Classifies the change from <paramref name="existing"/> (the live column) to the type of
    /// <paramref name="desired"/> (the column the source declares). Null when the types are the same or equivalent.</summary>
    public static ColumnTypeChange? Classify(
        SqlColumn existing, SqlColumn desired, bool isKey, TableDependencies table, string tableName)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(table);

        var from = existing.DataType;
        var source = desired.DataType;
        if (from == source)
        {
            return null;
        }

        var key = isKey || existing.IsIdentity || existing.IsPrimaryKey
                  || desired.Role is ColumnRole.HashKey or ColumnRole.Identity || desired.IsPrimaryKey;
        var column = existing.Name;
        var indexes = table.IndexesOn(column);
        var references = table.ReferencesTo(column).Select(r => r.Describe()).ToList();
        var resolution = SqlTypeResolution.Resolve(from, source);

        ColumnTypeChange Change(SqlDataType to, TypeChangeTier tier, TypeChangeMechanism mechanism, string reason,
            string? optIn = null, TypeChangeTiming timing = TypeChangeTiming.BeforeLoad, bool narrowing = false,
            IReadOnlyList<string>? steps = null) => new()
            {
                Column = column,
                FromType = from,
                SourceType = source,
                ToType = to,
                Tier = tier,
                Footprint = ChangeFootprintClassifier.Classify(from, to),
                Mechanism = mechanism,
                Timing = timing,
                IsNarrowing = narrowing,
                IsKey = key,
                Indexes = indexes.Select(i => i.Name).ToList(),
                References = references,
                RequiresOptIn = optIn,
                Reason = reason,
                ManualSteps = steps ?? [],
            };

        switch (resolution.Action)
        {
            case SchemaChangeAction.Keep:
            {
                // The target already holds the source's type. When the reverse also holds, the two are the same type
                // spelled two ways (datetime2 and datetime2(7)): nothing to do.
                if (SqlTypeResolution.Resolve(source, from).Action == SchemaChangeAction.Keep)
                {
                    return null;
                }

                if (!key && IsTextNarrowing(from, source))
                {
                    var blocked = Blockers(table, column, indexes, references, conversion: true);
                    return blocked is null
                        ? Change(source, TypeChangeTier.CheckedConversion, TypeChangeMechanism.ShadowRewrite,
                            $"the source declares the shorter {source.Render()}; narrowing checks every stored value fits first",
                            ChangeConversion, narrowing: true)
                        : Change(source, TypeChangeTier.Manual, TypeChangeMechanism.None, blocked, narrowing: true,
                            steps: BlockedSteps(tableName, column, references, blocked));
                }

                return Change(source, TypeChangeTier.Manual, TypeChangeMechanism.None,
                    $"a narrowing from {from.Render()} to {source.Render()} can drop data; the column keeps {from.Render()}, which holds every value of the source",
                    narrowing: true,
                    steps: [$"Nothing needs doing: {from.Render()} holds every value the source writes. To narrow it anyway, convert the column by hand after checking every value fits {source.Render()}."]);
            }

            case SchemaChangeAction.Alter:
            {
                var merged = resolution.Merged!;
                var metadataOnly = ChangeFootprintClassifier.IsVariableLengthGrowth(from, merged);
                var primaryKeyIndex = indexes.FirstOrDefault(i => i.IsPrimaryKey);
                if (metadataOnly && references.Count == 0 && primaryKeyIndex is null)
                {
                    return Change(merged, TypeChangeTier.Widening, TypeChangeMechanism.InPlace,
                        $"{merged.Render()} holds every value of {from.Render()}, and SQL Server records the change in metadata only");
                }

                if (key)
                {
                    return Change(merged, TypeChangeTier.Manual, TypeChangeMechanism.None,
                        $"[{column}] is a key column, and widening it to {merged.Render()} rebuilds the key's index",
                        steps: KeySteps(tableName, column, merged));
                }

                if (references.Count > 0)
                {
                    var why = $"{string.Join(", ", references)} reference{(references.Count == 1 ? "s" : string.Empty)} [{column}], so SQL Server refuses any ALTER COLUMN on it";
                    return Change(merged, TypeChangeTier.Manual, TypeChangeMechanism.None, why,
                        steps: BlockedSteps(tableName, column, references, why));
                }

                if (table.IsTemporal)
                {
                    // The safe rewrite would update every row, and each update would write the row to the history
                    // table. SQL Server alters a system-versioned table and its history together in place instead,
                    // which it refuses when an index depends on the column.
                    return indexes.Count == 0
                        ? Change(merged, TypeChangeTier.Widening, TypeChangeMechanism.InPlaceRewrite,
                            $"{merged.Render()} holds every value of {from.Render()}; the table is system-versioned, so the column is altered in place with its history, rewriting both under a schema lock for the whole statement",
                            ChangeRewrite)
                        : Change(merged, TypeChangeTier.Manual, TypeChangeMechanism.None,
                            $"the table is system-versioned and [{column}] is indexed by {string.Join(", ", indexes.Select(i => $"[{i.Name}]"))}: SQL Server cannot alter it in place, and rewriting it row by row would fill the history table",
                            steps: TableSteps(tableName, column, merged));
                }

                var blocked = Blockers(table, column, indexes, references, conversion: false);
                return blocked is null
                    ? Change(merged, TypeChangeTier.Widening, TypeChangeMechanism.ShadowRewrite,
                        $"{merged.Render()} holds every value of {from.Render()}, but SQL Server rewrites every row to change it" +
                        (indexes.Count > 0 ? " and refuses an ALTER COLUMN while an index depends on it" : string.Empty),
                        ChangeRewrite)
                    : Change(merged, TypeChangeTier.Manual, TypeChangeMechanism.None, blocked, steps: TableSteps(tableName, column, merged));
            }

            default:
            {
                if (key)
                {
                    return Change(source, TypeChangeTier.Manual, TypeChangeMechanism.None,
                        $"[{column}] is a key column: converting it can merge keys (for example '00123' and '123' both become 123), and a new key type changes the key every downstream record is identified by",
                        steps: KeySteps(tableName, column, source));
                }

                if (!IsCheckedPair(from, source))
                {
                    return Change(source, TypeChangeTier.Manual, TypeChangeMechanism.None,
                        LossyReason(from, source),
                        steps:
                        [
                            $"Declare a type in the source that holds every value of [{column}] without loss (one of the checked conversions), or keep {from.Render()}.",
                            $"To convert to {source.Render()} anyway, decide what each value becomes, convert the column by hand in a maintenance window, then run the flow.",
                        ]);
                }

                if (references.Count > 0)
                {
                    var why = $"{string.Join(", ", references)} reference{(references.Count == 1 ? "s" : string.Empty)} [{column}], so SQL Server refuses any ALTER COLUMN on it";
                    return Change(source, TypeChangeTier.Manual, TypeChangeMechanism.None, why,
                        steps: BlockedSteps(tableName, column, references, why));
                }

                var blocked = Blockers(table, column, indexes, references, conversion: true);
                if (blocked is not null)
                {
                    return Change(source, TypeChangeTier.Manual, TypeChangeMechanism.None, blocked, steps: TableSteps(tableName, column, source));
                }

                var toTyped = from.Family == SqlTypeFamily.Text;
                return Change(source, TypeChangeTier.CheckedConversion, TypeChangeMechanism.ShadowRewrite,
                    toTyped
                        ? $"text becomes {source.Render()}: every stored value must be one SQL Server reads the same under any language, without rounding or dropping a part"
                        : source.Family == SqlTypeFamily.Text
                            ? $"{from.Render()} becomes text: every stored value's rendering must fit {source.Render()}"
                            : $"{from.Render()} becomes {source.Render()}, which holds every value of it",
                    ChangeConversion,
                    toTyped ? TypeChangeTiming.AfterLoad : TypeChangeTiming.BeforeLoad);
            }
        }
    }

    private const string ChangeRewrite = ColumnTypeChange.AllowTableRewrite;
    private const string ChangeConversion = ColumnTypeChange.ConvertTypes;

    /// <summary>True for the cross-family pairs a checked conversion covers.</summary>
    public static bool IsCheckedPair(SqlDataType from, SqlDataType to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (LosslessConversion.IsVariableText(from) && LosslessConversion.IsCheckedTextTarget(to))
        {
            return true;
        }

        if (LosslessConversion.IsCheckedTextTarget(from) && LosslessConversion.IsVariableText(to))
        {
            return true;
        }

        return from.Family == SqlTypeFamily.Integer && to.Family == SqlTypeFamily.Decimal
               && (to.Precision ?? 18) - (to.Scale ?? 0) >= IntegerDigits(from.BaseType);
    }

    // A shorter varchar/nvarchar, or nvarchar to varchar: text that may still fit, which a check decides.
    private static bool IsTextNarrowing(SqlDataType from, SqlDataType to)
        => LosslessConversion.IsVariableText(from) && LosslessConversion.IsVariableText(to);

    private static int IntegerDigits(string baseType) => baseType switch
    {
        "tinyint" => 3,
        "smallint" => 5,
        "int" => 10,
        _ => 19,
    };

    // What stops the safe rewrite on this column, or null. The rewrite adds a column, updates every row through
    // batches keyed by the row identity, keeps writes in step with a trigger, rebuilds the indexes on a copy and swaps.
    private static string? Blockers(
        TableDependencies table, string column, IReadOnlyList<IndexDefinition> indexes, IReadOnlyList<string> references, bool conversion)
    {
        if (references.Count > 0)
        {
            return $"{string.Join(", ", references)} reference{(references.Count == 1 ? "s" : string.Empty)} [{column}], so SQL Server refuses to change it";
        }

        if (!table.Known)
        {
            return null;
        }

        if (table.IsTemporal)
        {
            return "the table is system-versioned: rewriting every row would write each one to its history table";
        }

        if (table.HasColumnStore)
        {
            return "the table has a columnstore index, which a row-by-row rewrite would fragment and which SQL Server rebuilds as a whole";
        }

        if (table.IsCdcTracked)
        {
            return "change data capture tracks the table, and a rewrite would capture every row as changed";
        }

        if (table.IsPartitioned)
        {
            return "the table is partitioned";
        }

        if (table.RowIdentity.Count == 0)
        {
            return "the table has no identity column, primary key or unique index on NOT NULL columns to batch the rewrite by and keep concurrent writes in step";
        }

        var clustered = indexes.FirstOrDefault(i => i.IsClustered);
        if (clustered is not null)
        {
            return $"the clustered index [{clustered.Name}] keys [{column}], so changing it rebuilds the whole table";
        }

        if (indexes.FirstOrDefault(i => i.IsPrimaryKey || i.IsUniqueConstraint) is { } constraint)
        {
            return $"the {(constraint.IsPrimaryKey ? "primary key" : "unique constraint")} [{constraint.Name}] covers [{column}], and a constraint is not an index the engine can rebuild in its place";
        }

        if (conversion && indexes.FirstOrDefault(i => i.FilterNames(column)) is { } filtered)
        {
            return $"the filtered index [{filtered.Name}] filters on [{column}], and its filter would mean something else on the new type";
        }

        return null;
    }

    private static string LossyReason(SqlDataType from, SqlDataType to)
    {
        if (from.Family == to.Family || (from.Family is SqlTypeFamily.Approximate or SqlTypeFamily.Money && to.Family == SqlTypeFamily.Decimal))
        {
            return $"{from.Render()} to {to.Render()} can lose data (a smaller scale or precision rounds, an approximate value is not exact)";
        }

        if (from.Family == SqlTypeFamily.DateTime && to.Family == SqlTypeFamily.DateTime)
        {
            return $"{from.Render()} to {to.Render()} can drop or round part of each value";
        }

        if (from.Family == SqlTypeFamily.Integer && to.Family == SqlTypeFamily.Decimal)
        {
            return $"{to.Render()} has fewer integer digits than {from.Render()} can hold";
        }

        return $"SQL Server's conversion from {from.Render()} to {to.Render()} is not one the engine can check for loss";
    }

    private static IReadOnlyList<string> KeySteps(string table, string column, SqlDataType to) =>
    [
        $"Keep [{column}]'s type in the source as the table has it, or change the key by reloading: write a migration that drops {table} (and the flow's staging table), then run the flow with a full load so every row is written again as {to.Render()}.",
        "A reload gives every row a new identity and InsertedDate_DW, and anything downstream that identifies its records by this key sees every key as new: retire or re-key those records first.",
    ];

    private static IReadOnlyList<string> BlockedSteps(string table, string column, IReadOnlyList<string> references, string why) =>
    [
        references.Count > 0
            ? $"Drop {string.Join(", ", references)} (or change it so it no longer reads [{column}]), run the flow so the engine changes the type, then create it again."
            : $"The engine cannot change [{column}] here because {why}.",
        $"Or change the column by hand in a maintenance window, after checking every value of {table}.[{column}] converts.",
    ];

    private static IReadOnlyList<string> TableSteps(string table, string column, SqlDataType to) =>
    [
        $"Change {table}.[{column}] to {to.Render()} by hand in a maintenance window (ALTER TABLE ... ALTER COLUMN, after checking every value converts), then run the flow.",
        $"Or write a migration that drops {table} and run the flow with a full load, which recreates it with the new type and reloads every row (new identities and InsertedDate_DW).",
    ];
}
