namespace SqlFlow.Catalog;

/// <summary>One column as one read of an object described it: its name, its rendered type, its nullability and its
/// 1-based position.</summary>
/// <param name="Name">The column's name.</param>
/// <param name="DataType">Its type with length and precision (<c>nvarchar(100)</c>), or null when the read did not say.</param>
/// <param name="Nullable">Whether it takes NULL.</param>
/// <param name="Ordinal">Its 1-based position in the object.</param>
public sealed record SchemaColumnState(string Name, string? DataType, bool Nullable, int Ordinal);

/// <summary>
/// The difference between two reads of one object's columns, column by column: what the catalog records as a schema
/// change when a pass reads an object differently from the time before. Columns are matched by name, ignoring case (SQL
/// Server's own rule for a column name, and a pass that only spells a name differently has not changed it). A column
/// is added or dropped when only one read holds it, and changed when both do and its type, its nullability or its place
/// moved. Its place is its rank among the columns BOTH reads hold, so adding or dropping a column does not move the
/// ones after it; a recreated table that put a column elsewhere does.
/// </summary>
public static class SchemaColumnDiff
{
    /// <summary>The columns that moved between <paramref name="before"/> and <paramref name="after"/>, in the order of
    /// <paramref name="after"/>, then the dropped ones in the order of <paramref name="before"/>. Empty when the two
    /// reads describe the same columns.</summary>
    public static IReadOnlyList<CatalogSchemaChangeColumn> Compare(
        IReadOnlyList<SchemaColumnState> before, IReadOnlyList<SchemaColumnState> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var earlier = Index(before);
        var later = Index(after);

        // The rank of each column both reads hold, among only those columns, in each read's own order.
        var shared = later.Keys.Where(earlier.ContainsKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rankBefore = Ranks(before, shared);
        var rankAfter = Ranks(after, shared);

        var changes = new List<CatalogSchemaChangeColumn>();
        foreach (var column in later.Values.OrderBy(c => c.Ordinal))
        {
            if (!earlier.TryGetValue(column.Name, out var was))
            {
                changes.Add(new CatalogSchemaChangeColumn
                {
                    Name = column.Name,
                    ChangeType = SchemaChangeKinds.Added,
                    ToType = column.DataType,
                    ToNullable = column.Nullable,
                    ToOrdinal = column.Ordinal,
                });
                continue;
            }

            var moved = rankBefore[column.Name] != rankAfter[column.Name];
            if (SameType(was.DataType, column.DataType) && was.Nullable == column.Nullable && !moved)
            {
                continue;
            }

            changes.Add(new CatalogSchemaChangeColumn
            {
                Name = column.Name,
                ChangeType = SchemaChangeKinds.Changed,
                FromType = was.DataType,
                ToType = column.DataType,
                FromNullable = was.Nullable,
                ToNullable = column.Nullable,
                FromOrdinal = was.Ordinal,
                ToOrdinal = column.Ordinal,
            });
        }

        foreach (var column in earlier.Values.OrderBy(c => c.Ordinal))
        {
            if (!later.ContainsKey(column.Name))
            {
                changes.Add(new CatalogSchemaChangeColumn
                {
                    Name = column.Name,
                    ChangeType = SchemaChangeKinds.Deleted,
                    FromType = column.DataType,
                    FromNullable = column.Nullable,
                    FromOrdinal = column.Ordinal,
                });
            }
        }

        return changes;
    }

    /// <summary>Every column of an object as added: what the catalog records for an object it saw for the first time.</summary>
    public static IReadOnlyList<CatalogSchemaChangeColumn> AllAdded(IReadOnlyList<SchemaColumnState> columns)
        => Compare([], columns);

    /// <summary>
    /// An object's columns as they stood before <paramref name="newestFirst"/> happened, worked back from
    /// <paramref name="now"/> by undoing each change in turn, newest first: an added column is taken out, a dropped one
    /// put back where it was, a changed one given its earlier type, nullability and place. Null when one of the changes
    /// added the object itself, which did not exist before it.
    /// </summary>
    public static IReadOnlyList<SchemaColumnState>? Before(
        IReadOnlyList<SchemaColumnState> now, IEnumerable<CatalogSchemaChange> newestFirst)
    {
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(newestFirst);

        var columns = Index(now);
        foreach (var change in newestFirst)
        {
            if (change.ChangeType == SchemaChangeKinds.Added)
            {
                return null;
            }

            foreach (var column in change.Columns)
            {
                switch (column.ChangeType)
                {
                    case SchemaChangeKinds.Added:
                        columns.Remove(column.Name);
                        break;
                    case SchemaChangeKinds.Deleted:
                        columns[column.Name] = Earlier(column, null);
                        break;
                    case SchemaChangeKinds.Changed:
                        columns[column.Name] = Earlier(column, columns.GetValueOrDefault(column.Name));
                        break;
                }
            }
        }

        return [.. columns.Values.OrderBy(c => c.Ordinal).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>An object's columns as text, one per line in their order (<c>[name] type NULL</c>), so two states of
    /// it read as a diff of lines. Null for an object with no columns.</summary>
    public static string? Render(IReadOnlyList<SchemaColumnState> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
        {
            return null;
        }

        return string.Join('\n', columns
            .OrderBy(c => c.Ordinal)
            .Select(c => $"[{c.Name.Replace("]", "]]", StringComparison.Ordinal)}] {c.DataType ?? "(type not read)"} {(c.Nullable ? "NULL" : "NOT NULL")}"));
    }

    /// <summary>How many lines <paramref name="after"/> holds that <paramref name="before"/> does not, and the other way
    /// round, each line counted as often as it occurs: a column whose type changed is one line deleted and one added.</summary>
    public static (int Added, int Deleted) LineTally(string? before, string? after)
    {
        var earlier = Lines(before);
        var added = 0;
        foreach (var line in Lines(after).Values.SelectMany(group => group))
        {
            if (earlier.TryGetValue(line, out var left) && left.Count > 0)
            {
                left.RemoveAt(left.Count - 1);
            }
            else
            {
                added++;
            }
        }

        return (added, earlier.Values.Sum(left => left.Count));

        static Dictionary<string, List<string>> Lines(string? text)
            => (text ?? string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .GroupBy(line => line, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
    }

    /// <summary>A column as it was before a change: its earlier type, nullability and place, falling back on what it has
    /// now (<paramref name="now"/>) for anything the change did not record.</summary>
    private static SchemaColumnState Earlier(CatalogSchemaChangeColumn column, SchemaColumnState? now)
        => new(
            column.Name,
            column.FromType ?? now?.DataType,
            column.FromNullable ?? now?.Nullable ?? true,
            column.FromOrdinal ?? now?.Ordinal ?? int.MaxValue);

    /// <summary>Whether two rendered types are the same type: spelled with any case and any spacing
    /// (<c>DECIMAL(18, 2)</c> is <c>decimal(18,2)</c>). A type one read did not give is not taken as a change, since
    /// it says nothing about the column.</summary>
    internal static bool SameType(string? before, string? after)
    {
        if (string.IsNullOrWhiteSpace(before) || string.IsNullOrWhiteSpace(after))
        {
            return true;
        }

        return string.Equals(Squeezed(before), Squeezed(after), StringComparison.OrdinalIgnoreCase);
    }

    private static string Squeezed(string type) => string.Concat(type.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>The columns by name. A read that names a column twice (it cannot, in one object) keeps the first.</summary>
    private static Dictionary<string, SchemaColumnState> Index(IReadOnlyList<SchemaColumnState> columns)
    {
        var index = new Dictionary<string, SchemaColumnState>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns.OrderBy(c => c.Ordinal))
        {
            index.TryAdd(column.Name, column);
        }

        return index;
    }

    private static Dictionary<string, int> Ranks(IReadOnlyList<SchemaColumnState> columns, HashSet<string> shared)
    {
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns.OrderBy(c => c.Ordinal))
        {
            if (shared.Contains(column.Name))
            {
                ranks.TryAdd(column.Name, ranks.Count);
            }
        }

        return ranks;
    }
}
