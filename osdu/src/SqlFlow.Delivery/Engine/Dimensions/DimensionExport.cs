using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>
/// What a dimension export holds: its values (each human-friendly value with the keys it stands for, its filter and the values
/// its keys' attributes hold), its keys (each exactly as the index holds it, with its label, its value, its own filter and
/// its attributes), or its table (each key with its value and its attributes, a row per value it collects, the rows a set of
/// cascading selects is read from).
/// </summary>
public enum DimensionExportSet
{
    Values,
    Keys,
    Table,
}

/// <summary>How a dimension export is written.</summary>
public enum DimensionExportFormat
{
    /// <summary>Comma-separated values with a header row (RFC 4180), for a spreadsheet.</summary>
    Csv,

    /// <summary>One JSON object a line, every value exactly as the ledger holds it.</summary>
    JsonLines,
}

/// <summary>
/// Writes a dimension's values, keys or table to a stream, a ledger page at a time, so an export of millions of keys never
/// holds more than one page (osdu/docs/reference/cli/dimensions.md, export). The API's download and the CLI's export
/// both write through here, so the two are the same file. A value's and a key's row carries the search filter that
/// finds its records, and a column per attribute the dimension reads (in CSV, several values of one attribute joined by
/// <c>; </c>).
/// </summary>
/// <remarks>
/// <para>
/// The table is the dimension's own table written out (osdu/docs/reference/flow/dimension-table.md, The dimension's table):
/// its rows and columns, a row per key and value it collects (one row for a key of a dimension collecting nothing, or a
/// key collecting no value), with the row's number, the partition, the key's number, the key, its value, one column per
/// attribute named as the dimension declares it, the records of the row (those holding the collected value, or every
/// record of the key) and the search filter finding the key's records. The key and its value are written under the
/// names their columns have in the table (<c>WellboreID</c>, <c>FacilityName</c>), in both formats; in JSON Lines every
/// column is a property of the row, as it is a column of the table. A select lists the distinct values of its column
/// among the rows the other selects leave. A key under no value (left out by cleaning) is no row.
/// </para>
/// </remarks>
/// <remarks>
/// A CSV cell a spreadsheet would run as a formula (text starting <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a
/// carriage return that is not a plain number) is written with a leading apostrophe, which a spreadsheet shows as text; the
/// JSON Lines export keeps every value exactly, for a program to read.
/// </remarks>
public static class DimensionExport
{
    /// <summary>The most values of one attribute a value's row names.</summary>
    private const int MaxAttributeValues = 100;

    /// <summary>How a CSV line ends (RFC 4180).</summary>
    private const string CsvLineEnd = "\r\n";

    /// <summary>How a JSON Lines line ends.</summary>
    private const string JsonLineEnd = "\n";

    private static readonly string[] ValueColumns =
        ["value_id", "value", "records", "records_exact", "keys", "unfilterable", "filter_parts", "filter", "first_seen_utc"];

    private static readonly string[] KeyColumns =
        ["key_id", "key", "label", "label_from", "value", "left_out", "note", "count", "filterable", "filter", "first_seen_utc"];

    /// <summary>The set a query parameter names (<c>values</c>, <c>keys</c> or <c>table</c>), or null for any other text.</summary>
    public static DimensionExportSet? SetOf(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        null or "" or "values" => DimensionExportSet.Values,
        "keys" => DimensionExportSet.Keys,
        "table" => DimensionExportSet.Table,
        _ => null,
    };

    /// <summary>The name a set is given in a query parameter and a file name.</summary>
    public static string NameOf(DimensionExportSet set) => set switch
    {
        DimensionExportSet.Keys => "keys",
        DimensionExportSet.Table => "table",
        _ => "values",
    };

    /// <summary>The format a query parameter names (<c>csv</c>, or <c>jsonl</c> for JSON Lines), or null for any other text.</summary>
    public static DimensionExportFormat? FormatOf(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        null or "" or "csv" => DimensionExportFormat.Csv,
        "jsonl" or "ndjson" or "json-lines" => DimensionExportFormat.JsonLines,
        _ => null,
    };

    /// <summary>The media type of a format.</summary>
    public static string MediaType(DimensionExportFormat format)
        => format == DimensionExportFormat.Csv ? "text/csv; charset=utf-8" : "application/x-ndjson; charset=utf-8";

    /// <summary>The file an export is saved as: the flow, the partition and the dimension, with the set and the format.</summary>
    public static string FileName(DimensionState dimension, DimensionExportSet set, DimensionExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        var stem = string.Join('-', new[] { dimension.FlowName, dimension.Partition ?? "partition", dimension.Name, NameOf(set) }.Select(Safe));
        return stem + (format == DimensionExportFormat.Csv ? ".csv" : ".jsonl");
    }

    /// <summary>
    /// Writes every value (or key) the dimension holds now to <paramref name="output"/> as UTF-8, in order of value (or of
    /// arrival), and returns how many rows it wrote. The stream is left open.
    /// </summary>
    public static async Task<long> WriteAsync(
        ILedger ledger, DimensionState dimension, DimensionExportSet set, DimensionExportFormat format, Stream output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);
        var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 64 * 1024, leaveOpen: true);
        await using (writer.ConfigureAwait(false))
        {
            var rows = await WriteAsync(ledger, dimension, set, format, writer, ct).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
            return rows;
        }
    }

    /// <summary>
    /// Writes every value (or key, or row of the table) the dimension holds now to <paramref name="output"/>, in order of
    /// value (or of arrival), and returns how many rows it wrote. Each line ends as its format says, whatever the writer's
    /// own line ending is: CRLF for CSV (RFC 4180), LF for JSON Lines.
    /// </summary>
    public static async Task<long> WriteAsync(
        ILedger ledger, DimensionState dimension, DimensionExportSet set, DimensionExportFormat format, TextWriter output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(dimension);
        ArgumentNullException.ThrowIfNull(output);
        var ending = format == DimensionExportFormat.Csv ? CsvLineEnd : JsonLineEnd;
        var declared = DimensionRunner.AttributesOf(dimension.AttributesJson);
        var attributes = declared.Select(a => a.Name).ToList();
        return set switch
        {
            DimensionExportSet.Values => await ValuesAsync(ledger, dimension.DimensionId, attributes, format, output, ending, ct).ConfigureAwait(false),
            DimensionExportSet.Keys => await KeysAsync(ledger, dimension.DimensionId, declared, format, output, ending, ct).ConfigureAwait(false),
            _ => await TableAsync(ledger, dimension, format, output, ending, ct).ConfigureAwait(false),
        };
    }

    /// <summary>The header of a CSV export: its fixed columns, then a column per attribute, named <c>attribute_&lt;name&gt;</c>.</summary>
    private static string Header(IEnumerable<string> columns, IReadOnlyList<string> attributes)
        => Csv([.. columns, .. attributes.Select(a => "attribute_" + a)]);

    private static async Task<long> ValuesAsync(
        ILedger ledger, int dimensionId, IReadOnlyList<string> attributes, DimensionExportFormat format, TextWriter writer, string ending, CancellationToken ct)
    {
        if (format == DimensionExportFormat.Csv)
        {
            await writer.WriteAsync(Header(ValueColumns, attributes) + ending).ConfigureAwait(false);
        }

        long rows = 0;
        DimensionMemberCursor? after = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await ledger.ListDimensionMembersAsync(dimensionId, new DimensionMemberQuery(null, false, after, OsduLedger.MaxDimensionPage), ct).ConfigureAwait(false);
            var held = attributes.Count == 0
                ? new Dictionary<long, IReadOnlyList<DimensionMemberAttributeValue>>()
                : (await ledger.MemberAttributesAsync(dimensionId, page.Select(m => m.MemberId).ToList(), MaxAttributeValues, ct).ConfigureAwait(false))
                    .ToDictionary(m => m.MemberId, m => m.Attributes);
            foreach (var member in page)
            {
                var own = held.TryGetValue(member.MemberId, out var list) ? list : [];
                var line = format == DimensionExportFormat.Csv
                    ? Csv([
                        Number(member.MemberId), member.Value, Number(member.Records), Bool(member.RecordsExact), Number(member.Originals),
                        Number(member.Unfilterable), Number(member.FilterParts), member.Filter, Instant(member.FirstSeenUtc),
                        .. attributes.Select(a => Joined(own.Where(v => v.Name == a).Select(v => v.Value)))])
                    : Json(w =>
                    {
                        w.WriteNumber("valueId", member.MemberId);
                        w.WriteString("value", member.Value);
                        w.WriteNumber("records", member.Records);
                        w.WriteBoolean("recordsExact", member.RecordsExact);
                        w.WriteNumber("keys", member.Originals);
                        w.WriteNumber("unfilterable", member.Unfilterable);
                        w.WriteNumber("filterParts", member.FilterParts);
                        w.WriteString("filter", member.Filter);
                        w.WriteString("firstSeenUtc", Instant(member.FirstSeenUtc));
                        if (attributes.Count > 0)
                        {
                            w.WriteStartObject("attributes");
                            foreach (var name in attributes)
                            {
                                w.WriteStartArray(name);
                                foreach (var value in own.Where(v => v.Name == name))
                                {
                                    w.WriteStringValue(value.Value);
                                }

                                w.WriteEndArray();
                            }

                            w.WriteEndObject();
                        }
                    });
                await writer.WriteAsync(line + ending).ConfigureAwait(false);
                rows++;
            }

            if (page.Count < OsduLedger.MaxDimensionPage)
            {
                return rows;
            }

            after = new DimensionMemberCursor(page[^1].Value, page[^1].Records);
        }
    }

    private static async Task<long> KeysAsync(
        ILedger ledger, int dimensionId, IReadOnlyList<DimensionAttributeSpec> declared, DimensionExportFormat format, TextWriter writer, string ending,
        CancellationToken ct)
    {
        var attributes = declared.Select(a => a.Name).ToList();
        if (format == DimensionExportFormat.Csv)
        {
            await writer.WriteAsync(Header(KeyColumns, attributes) + ending).ConfigureAwait(false);
        }

        long rows = 0;
        DimensionValueCursor? after = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await ledger.ListDimensionValuesAsync(
                dimensionId, new DimensionValueQuery(null, null, false, false, after, OsduLedger.MaxDimensionPage), ct).ConfigureAwait(false);
            foreach (var value in page)
            {
                var line = format == DimensionExportFormat.Csv
                    ? Csv([
                        Number(value.ValueId), value.Original, value.Label, value.LabelFrom, value.MemberValue, value.LeftOut, value.Note, Number(value.Count),
                        Bool(value.Filterable), value.Filter, Instant(value.FirstSeenUtc),
                        .. attributes.Select(a => Joined(value.Attributes.Where(v => v.Name == a).Select(v => v.Value)))])
                    : Json(w =>
                    {
                        w.WriteNumber("keyId", value.ValueId);
                        w.WriteString("key", value.Original);
                        w.WriteString("label", value.Label);
                        w.WriteString("labelFrom", value.LabelFrom);
                        w.WriteString("value", value.MemberValue);
                        w.WriteString("leftOut", value.LeftOut);
                        w.WriteString("note", value.Note);
                        w.WriteNumber("count", value.Count);
                        w.WriteBoolean("filterable", value.Filterable);
                        w.WriteString("filter", value.Filter);
                        w.WriteString("firstSeenUtc", Instant(value.FirstSeenUtc));
                        if (attributes.Count > 0)
                        {
                            // An attribute read from the record the key names is one value; a collected one is a list.
                            w.WriteStartObject("attributes");
                            foreach (var attribute in declared)
                            {
                                if (attribute.IsCollected)
                                {
                                    w.WriteStartArray(attribute.Name);
                                    foreach (var collects in value.Attributes.Where(v => v.Name == attribute.Name))
                                    {
                                        w.WriteStringValue(collects.Value);
                                    }

                                    w.WriteEndArray();
                                }
                                else
                                {
                                    w.WriteString(attribute.Name, value.Attributes.FirstOrDefault(v => v.Name == attribute.Name)?.Value);
                                }
                            }

                            w.WriteEndObject();
                        }
                    });
                await writer.WriteAsync(line + ending).ConfigureAwait(false);
                rows++;
            }

            if (page.Count < OsduLedger.MaxDimensionPage)
            {
                return rows;
            }

            after = new DimensionValueCursor(page[^1].ValueId, page[^1].Count);
        }
    }

    /// <summary>The dimension's table, row by row as the database holds it, so the file and the table are the same rows.</summary>
    private static async Task<long> TableAsync(
        ILedger ledger, DimensionState dimension, DimensionExportFormat format, TextWriter writer, string ending, CancellationToken ct)
    {
        var shape = await DimensionTable.ShapeAsync(ledger, dimension, ct).ConfigureAwait(false);
        var names = shape.Attributes;
        var partition = dimension.Partition ?? string.Empty;
        if (format == DimensionExportFormat.Csv)
        {
            await writer.WriteAsync(Csv(["id", "partition", "key_id", shape.KeyColumn, shape.ValueColumn, .. names, "records", "filter"]) + ending).ConfigureAwait(false);
        }

        long rows = 0;
        await foreach (var row in DimensionTable.StreamAsync(ledger, dimension, ct).ConfigureAwait(false))
        {
            var line = format == DimensionExportFormat.Csv
                ? Csv([Number(row.Id), partition, Number(row.KeyId), row.Key, row.Value, .. row.Attributes, Number(row.Records), row.Filter])
                : Json(w =>
                {
                    w.WriteNumber("id", row.Id);
                    w.WriteString("partition", partition);
                    w.WriteNumber("key_id", row.KeyId);
                    w.WriteString(shape.KeyColumn, row.Key);
                    w.WriteString(shape.ValueColumn, row.Value);

                    // Every column of the table is a property of its row: the names are the table's, and no two are alike.
                    for (var i = 0; i < names.Count; i++)
                    {
                        w.WriteString(names[i], i < row.Attributes.Count ? row.Attributes[i] : null);
                    }

                    w.WriteNumber("records", row.Records);
                    w.WriteString("filter", row.Filter);
                });
            await writer.WriteAsync(line + ending).ConfigureAwait(false);
            rows++;
        }

        return rows;
    }

    /// <summary>One CSV line: every cell quoted when it needs to be, a formula a spreadsheet would run made text.</summary>
    internal static string Csv(params string?[] cells)
    {
        var line = new StringBuilder();
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0)
            {
                line.Append(',');
            }

            var cell = Defused(cells[i] ?? string.Empty);
            if (cell.AsSpan().IndexOfAny(",\"\r\n") >= 0 || (cell.Length > 0 && (char.IsWhiteSpace(cell[0]) || char.IsWhiteSpace(cell[^1]))))
            {
                line.Append('"').Append(cell.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            }
            else
            {
                line.Append(cell);
            }
        }

        return line.ToString();
    }

    /// <summary>
    /// <paramref name="cell"/> as a spreadsheet will show it rather than run it: text starting like a formula gains a leading
    /// apostrophe, and a plain number, negative ones included, is left as it is.
    /// </summary>
    internal static string Defused(string cell)
    {
        if (cell.Length == 0 || cell[0] is not ('=' or '+' or '-' or '@' or '\t' or '\r'))
        {
            return cell;
        }

        return double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? cell : "'" + cell;
    }

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        // A download is not embedded in a page, so a value's own characters are written as they are rather than escaped.
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Several values of one attribute in one CSV cell, joined by <c>; </c>; null for none.</summary>
    private static string? Joined(IEnumerable<string> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? null : string.Join("; ", list);
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Instant(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>A part of a file name: letters, digits, dots, dashes and underscores, anything else a dash.</summary>
    private static string Safe(string part)
    {
        var safe = new StringBuilder(part.Length);
        foreach (var c in part)
        {
            safe.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-');
        }

        return safe.Length == 0 ? "dimension" : safe.ToString();
    }
}
