namespace SqlFlow.Delivery.Model;

/// <summary>
/// How the two columns of a dimension's table that hold its key and its value are named
/// (osdu/docs/reference/flow/dimension-table.md, The dimension's table): after what the dimension reads, so a table of
/// wellbores says <c>WellboreID</c> and <c>FacilityName</c>, as its attribute columns say <c>Country</c> and
/// <c>Field</c>. The key's column takes the property the dimension's path ends with; the value's takes the property its
/// label ends with, or the dimension's own name when it reads no label, the value then being the key itself, cleaned.
/// Where the two would be the same (a dimension <c>Source</c> reading <c>data.Source</c> with no label), the value's
/// column keeps the name, being the one a person reads, and the key's takes <c>Key</c> at its end: <c>Source</c> and
/// <c>SourceKey</c>. A document names either one itself (<c>columns: { key, value }</c>) when a name reads badly or
/// cannot name a column, and to keep a name through a change of path.
/// </summary>
public static class DimensionColumnNames
{
    /// <summary>
    /// The word the key's column is asked for by whatever it is named (an order, a role in a page), and its name in a
    /// table made before dimensions named their columns.
    /// </summary>
    public const string KeyRole = "key";

    /// <summary>The word the value's column is asked for by whatever it is named, and its name in a table made before dimensions named their columns.</summary>
    public const string ValueRole = "value";

    /// <summary>The columns every dimension's table has under the same name, which neither the key's nor the value's can take.</summary>
    public static readonly IReadOnlyList<string> Fixed = ["id", "partition", "key_id", "records", "filter"];

    /// <summary>The rule a column's name keeps to, as messages state it.</summary>
    public const string Rule = "a letter, then letters, digits and underscores, at most 64";

    /// <summary>Whether <paramref name="name"/> can name a column: the rule an attribute's name keeps to.</summary>
    public static bool IsName(string? name) => DimensionAttributeSpec.IsName(name);

    /// <summary>What the key's column takes at its end where the value's column has the name the path gives it.</summary>
    public const string KeySuffix = "Key";

    /// <summary>
    /// The key's column as <paramref name="path"/> names it, beside a value's column named
    /// <paramref name="valueColumn"/>: the property the path ends with (<c>data.WellboreID</c> is <c>WellboreID</c>), with
    /// <see cref="KeySuffix"/> at its end where the value's column has that name, ignoring case (<c>data.Source</c>
    /// beside a value's column <c>Source</c> is <c>SourceKey</c>), since a table has one column of a name.
    /// </summary>
    public static string KeyOf(string path, string valueColumn)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(valueColumn);
        var property = PropertyOf(path);
        return string.Equals(property, valueColumn, StringComparison.OrdinalIgnoreCase) ? property + KeySuffix : property;
    }

    /// <summary>
    /// The value's column as the dimension names it: the property its last label path ends with
    /// (<c>[data.GeoContexts.GeoPoliticalEntityID, data.GeoPoliticalEntityName]</c> is <c>GeoPoliticalEntityName</c>), or
    /// with no label the dimension's name, whatever is not a letter, a digit or an underscore made an underscore.
    /// </summary>
    public static string ValueOf(IReadOnlyList<string> label, string dimension)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(dimension);
        return label.Count > 0
            ? PropertyOf(label[^1])
            : string.Concat(dimension.Trim().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_'));
    }

    /// <summary>
    /// Why <paramref name="name"/> cannot be the column of the key (or, with <paramref name="value"/>, of the value), or
    /// null when it can: it keeps to the rule of a column's name, is none of the columns every table has, and is not the
    /// word the other of the two is asked for by.
    /// </summary>
    public static string? Problem(string name, bool value)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!IsName(name))
        {
            return $"is not a column name ({Rule})";
        }

        if (Fixed.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return $"is a column every dimension's table has already ({string.Join(", ", Fixed)})";
        }

        return string.Equals(name, value ? KeyRole : ValueRole, StringComparison.OrdinalIgnoreCase)
            ? $"is the word the {(value ? "key" : "value")}'s column is asked for by, whatever it is named"
            : null;
    }

    /// <summary>
    /// The columns of a dimension whose names no document settled (one built before dimensions named their columns): the
    /// names its path, label and name give when they can name its columns beside its attributes, else the two words the
    /// columns are asked for by, which no attribute can take.
    /// </summary>
    public static (string Key, string Value) Settled(string path, IReadOnlyList<string> label, string dimension, IEnumerable<string> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        var value = ValueOf(label, dimension);
        var key = KeyOf(path, value);
        var taken = attributes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Problem(key, value: false) is null && Problem(value, value: true) is null
            && !string.Equals(key, value, StringComparison.OrdinalIgnoreCase) && !taken.Contains(key) && !taken.Contains(value)
                ? (key, value)
                : (KeyRole, ValueRole);
    }

    /// <summary>The property a path ends with, its filter aside; the text after its last dot when it is no path a label is read through.</summary>
    public static string PropertyOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var text = path.Trim();
        return DimensionPath.Parse(text).Path is { Segments.Count: > 0 } parsed
            ? parsed.Segments[^1].Name
            : text[(text.LastIndexOf('.') + 1)..];
    }
}
