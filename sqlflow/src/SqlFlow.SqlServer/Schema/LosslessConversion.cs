using System.Globalization;

namespace SqlFlow.SqlServer.Schema;

/// <summary>
/// The T-SQL that converts a value between two SQL Server types, and the T-SQL that tells whether that conversion
/// loses nothing. Pure text generation, the one definition every check shares: the stored values of a column about to
/// change type, the staged values a load is about to write into a column of another type, and the landed values a
/// declared view type is about to cast.
/// <para>
/// A plain <c>TRY_CONVERT(...) IS NOT NULL</c> is not a lossless test: SQL Server succeeds and changes the value when it
/// converts <c>'2026-11-27 10:15'</c> to <c>date</c> (the time is dropped), <c>'1.239'</c> to <c>decimal(10,2)</c>
/// (rounded), <c>'5'</c> to <c>bit</c> (1), a GUID followed by other characters (the rest is ignored), a value with an
/// offset to <c>datetime2</c> (the offset is dropped), eight fractional digits to <c>datetime2(7)</c> (rounded), a
/// Unicode character to <c>varchar</c> (replaced), and it reads <c>'03/04/2026'</c> by the session's language. So:
/// </para>
/// <list type="bullet">
/// <item>Text to a typed column accepts only a form whose meaning does not depend on the session: an integer exactly as
/// SQL Server renders it (so <c>'007'</c> and <c>'+7'</c> do not pass, as a significant leading zero is not a number),
/// a decimal with no more significant fractional digits than the scale, an ISO <c>yyyy-mm-dd</c> date or date and time
/// with no offset and no more fractional digits than the type keeps, <c>0</c>/<c>1</c>/<c>true</c>/<c>false</c> for a bit,
/// and a 36-character GUID.</item>
/// <item>Every other pair passes when converting back gives the original value exactly (byte for byte for text).</item>
/// </list>
/// </summary>
public static class LosslessConversion
{
    /// <summary>The text types a checked conversion accepts as a source or a target (the deprecated <c>text</c> and
    /// <c>ntext</c>, and the padded <c>char</c>/<c>nchar</c> as a target, are not among them).</summary>
    public static bool IsVariableText(SqlDataType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.BaseType is "varchar" or "nvarchar";
    }

    /// <summary>The typed targets text converts to under a checked conversion.</summary>
    public static bool IsCheckedTextTarget(SqlDataType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.BaseType is "tinyint" or "smallint" or "int" or "bigint" or "decimal" or "date" or "datetime2" or "bit"
            or "uniqueidentifier";
    }

    /// <summary>The expression that converts <paramref name="value"/> (a <paramref name="from"/> expression) to
    /// <paramref name="to"/>. A text value becoming typed is trimmed of spaces first, as the check trims it.</summary>
    public static string Convert(SqlDataType from, SqlDataType to, string value)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return from.Family == SqlTypeFamily.Text && to.Family != SqlTypeFamily.Text
            ? $"CONVERT({to.Render()}, LTRIM(RTRIM({value})))"
            : $"CONVERT({to.Render()}, {value})";
    }

    /// <summary>
    /// An expression that is 1 when <paramref name="value"/> is NULL or converts from <paramref name="from"/> to
    /// <paramref name="to"/> without loss, and 0 otherwise. Never NULL, so <c>... = 0</c> finds every failing value
    /// (a bare predicate would let an UNKNOWN through a <c>NOT</c>).
    /// </summary>
    public static string LosslessFlag(SqlDataType from, SqlDataType to, string value)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return $"CASE WHEN {value} IS NULL THEN 1 WHEN {Predicate(from, to, value)} THEN 1 ELSE 0 END";
    }

    /// <summary>The predicate behind <see cref="LosslessFlag"/> for a non-NULL value (UNKNOWN counts as a failure).</summary>
    public static string Predicate(SqlDataType from, SqlDataType to, string value)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (from.Family == SqlTypeFamily.Text && to.Family != SqlTypeFamily.Text && IsCheckedTextTarget(to))
        {
            return TextToTyped(to, $"LTRIM(RTRIM({value}))");
        }

        if (from.Family == SqlTypeFamily.Text && to.Family == SqlTypeFamily.Text)
        {
            return TextRoundTrip(from, to, value);
        }

        if (from.Family == SqlTypeFamily.Text)
        {
            // Text to a typed column no checked conversion covers (float, money, datetime, time, ...): only the value
            // SQL Server itself would render back passes, which is the strictest reading available.
            var typed = $"TRY_CONVERT({to.Render()}, {value})";
            return $"({typed} IS NOT NULL AND {BytesMatch($"CONVERT({Max(from).Render()}, {typed})", value)})";
        }

        // Typed to anything: convert and convert back; the value must come back exactly. A target too short for the
        // rendering truncates (or renders '*'), a rounding moves the value, an overflow fails: each comes back different.
        var converted = $"TRY_CONVERT({to.Render()}, {value})";
        return $"({converted} IS NOT NULL AND TRY_CONVERT({from.Render()}, {converted}) = {value})";
    }

    private static string TextToTyped(SqlDataType to, string trimmed)
    {
        var type = to.Render();
        switch (to.BaseType)
        {
            case "tinyint":
            case "smallint":
            case "int":
            case "bigint":
                // Exactly SQL Server's rendering of the number: no sign but a leading minus, no leading zero, no
                // fraction, no exponent, no grouping.
                return $"(CONVERT(varchar(30), TRY_CONVERT({type}, {trimmed})) = {trimmed})";

            case "decimal":
            {
                var scale = (to.Scale ?? 0).ToString(CultureInfo.InvariantCulture);
                // Significant fractional digits (trailing zeros do not count) must fit the scale, so nothing rounds;
                // TRY_CONVERT then proves the integer part fits the precision and the text is a decimal literal.
                var fraction = $"SUBSTRING({trimmed}, CHARINDEX('.', {trimmed}) + 1, 4000)";
                var significant = $"(LEN({fraction}) - (PATINDEX('%[^0]%', REVERSE({fraction}) + '1') - 1))";
                return $"(TRY_CONVERT({type}, {trimmed}) IS NOT NULL AND {trimmed} NOT LIKE '%[eE]%' " +
                       $"AND (CHARINDEX('.', {trimmed}) = 0 OR {significant} <= {scale}))";
            }

            case "date":
                // An ISO date, or an ISO date and time whose time is midnight, with no offset.
                return $"({IsoShape(trimmed)} AND TRY_CONVERT(date, {trimmed}) IS NOT NULL " +
                       $"AND (LEN({trimmed}) = 10 OR TRY_CONVERT(datetime2(7), {trimmed}) = CONVERT(datetime2(7), TRY_CONVERT(date, {trimmed}))))";

            case "datetime2":
            {
                // An ISO date or date and time with no offset, no more fractional digits than datetime2 keeps, and none
                // the declared scale would round away.
                return $"({IsoShape(trimmed)} AND TRY_CONVERT(datetime2(7), {trimmed}) IS NOT NULL " +
                       $"AND (CHARINDEX('.', {trimmed}) = 0 OR LEN({trimmed}) - CHARINDEX('.', {trimmed}) <= 7) " +
                       $"AND TRY_CONVERT({type}, {trimmed}) = TRY_CONVERT(datetime2(7), {trimmed}))";
            }

            case "bit":
                return $"(LOWER({trimmed}) IN ('0', '1', 'true', 'false'))";

            case "uniqueidentifier":
                return $"(LEN({trimmed}) = 36 AND TRY_CONVERT(uniqueidentifier, {trimmed}) IS NOT NULL)";

            default:
                throw new ArgumentOutOfRangeException(nameof(to), to.Render(), "Not a checked text target.");
        }
    }

    // yyyy-mm-dd, optionally followed by a space or 'T' and a time, with no 'Z', no '+' and no '-' after the date (an
    // offset). Under every DATEFORMAT and language this form reads the same for date and datetime2.
    private static string IsoShape(string trimmed)
        => $"{trimmed} LIKE '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]%' " +
           $"AND (LEN({trimmed}) = 10 OR SUBSTRING({trimmed}, 11, 1) IN (' ', 'T')) " +
           $"AND {trimmed} NOT LIKE '%[zZ+]%' AND CHARINDEX('-', {trimmed}, 11) = 0";

    // Text to text: convert to the target and back to the source's own type at full length; the bytes must match, so a
    // cut, a replaced character and a dropped trailing space all fail.
    private static string TextRoundTrip(SqlDataType from, SqlDataType to, string value)
        => BytesMatch($"CONVERT({Max(from).Render()}, CONVERT({to.Render()}, {value}))", value);

    // Byte-for-byte equality of a value converted back with the original: no collation, no trailing-space folding.
    private static string BytesMatch(string back, string value)
        => $"(DATALENGTH({back}) = DATALENGTH({value}) AND CONVERT(varbinary(max), {back}) = CONVERT(varbinary(max), {value}))";

    private static SqlDataType Max(SqlDataType text)
        => new() { BaseType = text.BaseType is "nchar" or "nvarchar" or "ntext" or "sysname" ? "nvarchar" : "varchar", Length = -1 };
}
