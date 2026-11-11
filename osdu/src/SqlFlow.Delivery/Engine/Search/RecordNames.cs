using System.Text.Json;

namespace SqlFlow.Delivery.Engine.Search;

/// <summary>
/// The name a reader knows an OSDU record by (osdu/docs/reference/concepts/explorer.md, the Name column): the first of the
/// properties records are named by that holds a text, tried in order. The explorer and an inventory name a record by the same
/// properties, so a record reads alike on both pages.
/// </summary>
public static class RecordNames
{
    /// <summary>The properties a record's name is read from, in the order they are tried, each by its path from the record's root.</summary>
    public static IReadOnlyList<string> Fields { get; } =
        ["data.FacilityName", "data.Name", "data.ProjectName", "data.Code", "data.DatasetProperties.FileSourceInfo.Name"];

    /// <summary>
    /// The record's name, trimmed; null when it holds none of <see cref="Fields"/> as a text with something in it. A property is
    /// found however the service that answered projected it: nested (<c>data</c> holding <c>FacilityName</c>), under its whole
    /// dotted path at the root (a search's returned field), or under the rest of its path inside <c>data</c>, which is how
    /// storage's read by id returns a nested attribute it was asked for (<c>data["DatasetProperties.FileSourceInfo.Name"]</c>,
    /// os-core-common PersistenceHelper.filterRecordDataFields).
    /// </summary>
    public static string? Of(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var field in Fields)
        {
            if (At(record, field) is { ValueKind: JsonValueKind.String } value && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        return null;
    }

    /// <summary>The value at <paramref name="path"/> under <paramref name="node"/>, the rest of the path tried whole at each step before stepping into its first segment.</summary>
    private static JsonElement? At(JsonElement node, string path)
    {
        while (true)
        {
            if (node.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (node.TryGetProperty(path, out var whole))
            {
                return whole;
            }

            var dot = path.IndexOf('.', StringComparison.Ordinal);
            if (dot < 0 || !node.TryGetProperty(path[..dot], out var inner))
            {
                return null;
            }

            node = inner;
            path = path[(dot + 1)..];
        }
    }
}
