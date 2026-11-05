using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Rendering;

namespace SqlFlow.Delivery.Source;

/// <summary>One column of a source table, as the database describes it.</summary>
internal sealed record SourceColumn(string Name, string TypeName, short MaxLength, byte Precision, byte Scale, bool Nullable)
{
    private static readonly HashSet<string> Uncomparable = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "ntext", "image", "xml", "sql_variant", "geography", "geometry", "hierarchyid",
    };

    /// <summary>The type as SQL spells it, with the length, precision and scale a parameter or a table variable needs.</summary>
    public string SqlType => TypeName.ToLowerInvariant() switch
    {
        "nchar" or "nvarchar" => $"{TypeName}({(MaxLength < 0 ? "max" : (MaxLength / 2).ToString(CultureInfo.InvariantCulture))})",
        "char" or "varchar" or "binary" or "varbinary" => $"{TypeName}({(MaxLength < 0 ? "max" : MaxLength.ToString(CultureInfo.InvariantCulture))})",
        "decimal" or "numeric" => $"{TypeName}({Precision.ToString(CultureInfo.InvariantCulture)},{Scale.ToString(CultureInfo.InvariantCulture)})",
        "datetime2" or "datetimeoffset" or "time" => $"{TypeName}({Scale.ToString(CultureInfo.InvariantCulture)})",
        _ => TypeName,
    };

    /// <summary>How many characters the column holds, or <see cref="int.MaxValue"/> for an unbounded one.</summary>
    public int MaxCharacters => MaxLength < 0
        ? int.MaxValue
        : TypeName.StartsWith('n') ? MaxLength / 2 : MaxLength;

    /// <summary>Whether the column can be compared, indexed and carried in a table variable's column list.</summary>
    public bool Comparable => !Uncomparable.Contains(TypeName) && MaxLength >= 0;

    /// <summary>Whether the column holds a whole number.</summary>
    public bool IsInteger => TypeName.ToLowerInvariant() is "bigint" or "int" or "smallint" or "tinyint";

    /// <summary>
    /// Whether every value the column holds is a whole number that fits a <see cref="long"/>: an integer column, or a decimal
    /// one without a scale and at most 18 digits.
    /// </summary>
    public bool IsWholeNumber => IsInteger || (TypeName.ToLowerInvariant() is "decimal" or "numeric" && Scale == 0 && Precision <= 18);

    /// <summary>Whether the column holds text: a character type, or SQL Server's own json type, which reads as its text.</summary>
    public bool IsText => TypeName.ToLowerInvariant() is "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "json";

    /// <summary>Whether the column holds a date and time.</summary>
    public bool IsMoment => TypeName.ToLowerInvariant() is "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" or "date";

    /// <summary>A parameter of this column's own type, so a comparison against it stays index seekable.</summary>
    public SqlParameter Parameter(string name, object value)
    {
        var parameter = new SqlParameter(name, SqlDbType.NVarChar) { Value = value };
        switch (TypeName.ToLowerInvariant())
        {
            case "bigint":
            case "int":
            case "smallint":
            case "tinyint":
                parameter.SqlDbType = SqlDbType.BigInt;
                parameter.Value = Number(value);
                break;
            case "bit":
                parameter.SqlDbType = SqlDbType.Bit;
                parameter.Value = value is bool flag ? flag : bool.Parse(Text(value));
                break;
            case "uniqueidentifier":
                parameter.SqlDbType = SqlDbType.UniqueIdentifier;
                parameter.Value = value is Guid guid ? guid : Guid.Parse(Text(value));
                break;
            case "decimal":
            case "numeric":
            case "money":
            case "smallmoney":
                parameter.SqlDbType = SqlDbType.Decimal;
                parameter.Precision = Precision;
                parameter.Scale = Scale;
                parameter.Value = value is decimal number ? number : decimal.Parse(Text(value), CultureInfo.InvariantCulture);
                break;
            case "float":
            case "real":
                parameter.SqlDbType = SqlDbType.Float;
                parameter.Value = value is double real ? real : double.Parse(Text(value), CultureInfo.InvariantCulture);
                break;
            case "date":
            case "datetime":
            case "datetime2":
            case "smalldatetime":
                parameter.SqlDbType = TypeName.Equals("datetime", StringComparison.OrdinalIgnoreCase) ? SqlDbType.DateTime : SqlDbType.DateTime2;
                parameter.Value = value is DateTime moment ? moment : DateTime.Parse(Text(value), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                break;
            case "datetimeoffset":
                parameter.SqlDbType = SqlDbType.DateTimeOffset;
                parameter.Value = value switch
                {
                    DateTimeOffset offset => offset,
                    DateTime plain => new DateTimeOffset(DateTime.SpecifyKind(plain, DateTimeKind.Utc)),
                    _ => DateTimeOffset.Parse(Text(value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                };
                break;
            case "char":
            case "varchar":
                parameter.SqlDbType = SqlDbType.VarChar;
                parameter.Size = MaxLength < 0 ? -1 : MaxLength;
                parameter.Value = Text(value);
                break;
            default:
                parameter.Size = MaxLength < 0 ? -1 : MaxCharacters;
                parameter.Value = Text(value);
                break;
        }

        return parameter;
    }

    private static string Text(object value) => SourceRow.Stringify(value) ?? string.Empty;

    private static long Number(object value)
        => value is long number ? number : long.Parse(Text(value), NumberStyles.Integer, CultureInfo.InvariantCulture);
}

/// <summary>What the source database says about a table's columns: one query every reader of an ingestion table shares.</summary>
internal static class IngestionColumns
{
    /// <summary>
    /// Every column of <paramref name="table"/>, keyed by name ignoring case; empty when the table does not exist or the
    /// login cannot see it. A <see cref="SqlException"/> is the caller's to describe, since only it knows what it was reading.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, SourceColumn>> ReadAsync(
        SqlConnection connection, SourceObjectName table, int commandTimeoutSeconds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(table);
        await using var command = connection.CreateCommand();
        command.CommandText = IngestionSql.Columns(table);
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.Add(new SqlParameter(IngestionSql.ObjectParameter, SqlDbType.NVarChar, 386) { Value = table.ToString() });
        var columns = new Dictionary<string, SourceColumn>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var column = new SourceColumn(reader.GetString(0), reader.GetString(1), reader.GetInt16(2), reader.GetByte(3), reader.GetByte(4), reader.GetBoolean(5));
            columns[column.Name] = column;
        }

        return columns;
    }
}
