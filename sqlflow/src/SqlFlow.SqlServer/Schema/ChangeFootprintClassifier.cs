namespace SqlFlow.SqlServer.Schema;

/// <summary>
/// Classifies a single ALTER COLUMN as <see cref="ChangeFootprint.MetadataOnly"/> or
/// <see cref="ChangeFootprint.TableRewrite"/> from the structured <see cref="SqlDataType"/> pair. Pure, no I/O.
/// <para>
/// The rule is SQL Server's, measured rather than assumed (the log bytes an ALTER COLUMN writes over a populated table
/// inside a transaction): only a variable-length column growing within its own base type (<c>varchar(n)</c> to
/// <c>varchar(m)</c>, <c>nvarchar</c> and <c>varbinary</c> alike, neither of them <c>max</c>) is recorded in metadata
/// alone. Every other change touches every row under a schema-modification lock held for the whole statement:
/// <c>varchar</c> to <c>nvarchar</c>, a move to <c>(max)</c>, any fixed-width growth, any integer rank, any decimal
/// precision or scale change (inside one storage-byte class too), any <c>datetime2</c>/<c>time</c>/<c>datetimeoffset</c>
/// scale change (inside one byte class too), <c>real</c> to <c>float</c>, <c>smallmoney</c> to <c>money</c>,
/// <c>date</c> to <c>datetime2</c>, a narrowing, and every change across type families.
/// </para>
/// </summary>
public static class ChangeFootprintClassifier
{
    public static ChangeFootprint Classify(SqlDataType from, SqlDataType to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (from == to)
        {
            return ChangeFootprint.MetadataOnly;
        }

        return IsVariableLengthGrowth(from, to) ? ChangeFootprint.MetadataOnly : ChangeFootprint.TableRewrite;
    }

    /// <summary>True when <paramref name="to"/> is <paramref name="from"/> with a longer (or equal) declared length,
    /// both of one variable-length base type and neither <c>(max)</c>: the one change SQL Server applies in metadata,
    /// and the one it allows on a column that a nonclustered index keys or includes.</summary>
    public static bool IsVariableLengthGrowth(SqlDataType from, SqlDataType to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        return from.BaseType == to.BaseType
               && from.BaseType is "varchar" or "nvarchar" or "varbinary"
               && !from.IsMax
               && !to.IsMax
               && (to.Length ?? 1) >= (from.Length ?? 1);
    }
}
