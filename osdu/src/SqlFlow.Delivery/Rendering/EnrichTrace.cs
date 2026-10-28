using System.Text.Json.Nodes;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Rendering;

/// <summary>
/// What a record's ledger keeps of where its enrichments came from and what they landed as (osdu/docs/reference/flow/enrich.md,
/// What the ledger keeps): values of the record's target state, which travels with the record wherever it goes (a move, a
/// reversal, a purge), so an enrichment is traced from the record it belongs to. For each enrichment reading rows, how many
/// rows the delivered document was built from and the ingestion files they came from; for each record an enrichment adds,
/// the row it was rendered from and the version OSDU holds of it.
/// </summary>
public static class EnrichTrace
{
    /// <summary>The most ingestion files listed for one enrichment's rows; more are counted, not listed.</summary>
    public const int MaxFiles = 20;

    /// <summary>The target state value holding where an enrichment's rows came from: <c>enrich.source.&lt;name&gt;</c>.</summary>
    public static string RowsKey(string enrichment) => "enrich.source." + enrichment;

    /// <summary>The target state value holding the row a record an enrichment adds was rendered from: <c>enrich.&lt;id&gt;.source</c>.</summary>
    public static string SourceKey(string recordId) => "enrich." + recordId + ".source";

    /// <summary>The target state value holding the version OSDU holds of a record an enrichment adds: <c>enrich.&lt;id&gt;.version</c>.</summary>
    public static string VersionKey(string recordId) => "enrich." + recordId + ".version";

    /// <summary>
    /// Where one row came from, as <c>file#row</c>: the ingestion file it was landed from and its position in that file, from
    /// SQLFlow's system columns the flow names; <c>#row</c> without a file, and null when the table carries neither.
    /// </summary>
    public static string? Row(SourceRow row, FlowSystemColumns columns)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(columns);
        var file = columns.FileName is { } fileColumn ? row.GetString(fileColumn)?.Trim() : null;
        var number = columns.RowNumber is { } rowColumn ? row.GetString(rowColumn)?.Trim() : null;
        if (string.IsNullOrEmpty(file) && string.IsNullOrEmpty(number))
        {
            return null;
        }

        return string.IsNullOrEmpty(number) ? file : $"{file}#{number}";
    }

    /// <summary>
    /// Where an enrichment's rows came from, as canonical JSON: how many rows the record had, the ingestion files they were
    /// landed from (at most <see cref="MaxFiles"/>, in the order the rows came), and, for one row, where that row is.
    /// </summary>
    public static string Rows(IReadOnlyList<SourceRow> rows, FlowSystemColumns columns)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(columns);
        var files = new List<string>();
        foreach (var row in rows)
        {
            if (columns.FileName is { } fileColumn && row.GetString(fileColumn)?.Trim() is { Length: > 0 } file && !files.Contains(file, StringComparer.Ordinal))
            {
                files.Add(file);
            }
        }

        var node = new JsonObject
        {
            ["files"] = new JsonArray(files.Take(MaxFiles).Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
            ["filesCounted"] = files.Count,
            ["rows"] = rows.Count,
        };
        if (rows.Count == 1 && Row(rows[0], columns) is { } one)
        {
            node["row"] = one;
        }

        return CanonicalJson.ToString(node);
    }
}
