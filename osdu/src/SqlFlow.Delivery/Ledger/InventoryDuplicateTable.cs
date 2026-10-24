using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlFlow.Delivery.Data;
using SqlFlow.SqlServer.Schema;

namespace SqlFlow.Delivery.Ledger;

/// <summary>One property a duplicate check downloaded: its path from the record's root, and the column of the check's table holding it.</summary>
public sealed record DuplicateColumn(string Path, string Column);

/// <summary>
/// A duplicate check's own table, <c>[osdu].[dup_&lt;check&gt;]</c> (osdu/docs/reference/flow/inventory-duplicates.md, The check's
/// table): one row for each id of the inventory the check downloaded, and for each property it downloaded a column holding the
/// values as the record holds them (a JSON list of text, readable as a sheet) and a column holding a hash of them as a key
/// compares them, which the check's groups are formed by in SQL. The check's run makes it through SQLFlow's schema evolution as
/// a dimension's table is made; no migration creates it, and the sweep that retires the check drops it.
/// </summary>
public static class InventoryDuplicateTable
{
    /// <summary>What the name of every check's table starts with.</summary>
    public const string Prefix = "dup_";

    /// <summary>The id's row of the inventory: the table's key.</summary>
    public const string RecordColumn = "inventory_record_id";

    /// <summary>The OSDU id.</summary>
    public const string TargetColumn = "target_id";

    /// <summary>The version the search index held of the record when the check downloaded it.</summary>
    public const string VersionColumn = "version";

    /// <summary>The longest a property's column is named, so its hash's column, its name and a suffix, stays within SQL Server's 128.</summary>
    public const int MaxColumnLength = 100;

    /// <summary>The bytes of a hash column: a SHA-256.</summary>
    public const int HashLength = 32;

    /// <summary>The column holding the hash of a property's values, beside the column holding them.</summary>
    public static string HashColumn(string column) => column + "__hash";

    /// <summary>The table of check <paramref name="checkId"/>.</summary>
    public static string NameOf(long checkId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(checkId, 1);
        return Prefix + checkId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The columns of the properties a check downloads, one each, in their order: the path without its <c>data</c> root, each dot
    /// an underscore (<c>data.Curves.Mnemonic</c> is <c>Curves_Mnemonic</c>, <c>tags.Source</c> is <c>tags_Source</c>), cut to
    /// <see cref="MaxColumnLength"/>, and made unique ignoring case (a fixed column's name or another property's taken: a number
    /// after it).
    /// </summary>
    public static IReadOnlyList<DuplicateColumn> AttributesOf(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var taken = new HashSet<string>([RecordColumn, TargetColumn, VersionColumn], StringComparer.OrdinalIgnoreCase);
        var attributes = new List<DuplicateColumn>(paths.Count);
        foreach (var path in paths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            var plain = new StringBuilder();
            foreach (var c in path.StartsWith("data.", StringComparison.Ordinal) ? path["data.".Length..] : path)
            {
                plain.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
            }

            var name = plain.Length > MaxColumnLength ? plain.ToString(0, MaxColumnLength) : plain.ToString();
            var column = name;
            for (var n = 2; !taken.Add(column); n++)
            {
                var suffix = "_" + n.ToString(CultureInfo.InvariantCulture);
                column = (name.Length + suffix.Length > MaxColumnLength ? name[..(MaxColumnLength - suffix.Length)] : name) + suffix;
            }

            attributes.Add(new DuplicateColumn(path, column));
        }

        return attributes;
    }

    /// <summary>The properties a check downloaded, as its row keeps them.</summary>
    public static string ToJson(IReadOnlyList<DuplicateColumn> attributes)
        => JsonSerializer.Serialize(attributes.Select(a => new Written(a.Path, a.Column)).ToList());

    /// <summary>
    /// The properties a check's row says it downloaded, checked again, since its columns are written into statements: each
    /// column is a name of letters, digits and underscores no longer than <see cref="MaxColumnLength"/>, none named twice.
    /// </summary>
    /// <exception cref="DeliveryException">The text is not the list a check keeps.</exception>
    public static IReadOnlyList<DuplicateColumn> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        List<Written>? written;
        try
        {
            written = JsonSerializer.Deserialize<List<Written>>(json);
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"The properties a duplicate check downloaded are not the list a check keeps ({ex.Message}); check for duplicates again.", ex);
        }

        var seen = new HashSet<string>([RecordColumn, TargetColumn, VersionColumn], StringComparer.OrdinalIgnoreCase);
        var attributes = new List<DuplicateColumn>();
        foreach (var item in written ?? [])
        {
            if (item?.Path is not { Length: > 0 } path || item.Column is not { Length: > 0 and <= MaxColumnLength } column
                || !column.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') || !seen.Add(column))
            {
                throw new DeliveryException("The properties a duplicate check downloaded name a column a check's table cannot have; check for duplicates again.");
            }

            attributes.Add(new DuplicateColumn(path, column));
        }

        return attributes;
    }

    /// <summary>
    /// The columns the table is to have, which SQLFlow's schema evolution brings it to: the id's row (the key), the OSDU id and
    /// the version the index held; then, for each property, its values and their hash.
    /// </summary>
    internal static IReadOnlyList<SqlColumn> Desired(IReadOnlyList<DuplicateColumn> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        var columns = new List<SqlColumn>
        {
            new() { Name = RecordColumn, DataType = RunTimeTables.Type("bigint"), IsNullable = false, IsPrimaryKey = true },
            new() { Name = TargetColumn, DataType = RunTimeTables.Text(DeliveryModel.MaxTargetIdLength), IsNullable = false },
            new() { Name = VersionColumn, DataType = RunTimeTables.Type("bigint"), IsNullable = true },
        };
        foreach (var attribute in attributes)
        {
            columns.Add(new SqlColumn { Name = attribute.Column, DataType = RunTimeTables.Text(-1), IsNullable = true });
            columns.Add(new SqlColumn { Name = HashColumn(attribute.Column), DataType = RunTimeTables.Binary(HashLength), IsNullable = true });
        }

        return columns;
    }

    /// <summary>The table's key, which schema evolution plans it by.</summary>
    internal static readonly IReadOnlySet<string> KeyColumns = new HashSet<string>([RecordColumn], StringComparer.OrdinalIgnoreCase);

    /// <summary>One property as a check's row keeps it.</summary>
    private sealed record Written(string? Path, string? Column);
}
