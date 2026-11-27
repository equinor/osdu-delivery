namespace SqlFlow.Core.Model;

/// <summary>
/// The output of the independent inference process: the optimal type per column, the full transform
/// SELECT you can run against the table, and (when validated) a per-column sanity check. Serializes to
/// JSON - viewed directly (no metadata DB) or stored into the pre-ingestion transform metadata.
/// </summary>
public sealed record InferenceReport
{
    public required string Schema { get; init; }
    public required string Table { get; init; }
    public required string OnConvertError { get; init; }

    /// <summary>The locale the run bound to (the server's, or an explicit override) - e.g. "nb-NO".</summary>
    public required string Culture { get; init; }

    /// <summary>The date-component ordering applied to every column (Ymd / Dmy / Mdy).</summary>
    public required string DateOrder { get; init; }

    public required IReadOnlyList<InferredColumnReport> Columns { get; init; }

    /// <summary>
    /// A runnable SELECT that applies every column's inferred conversion against the table. Useful to
    /// eyeball or execute to confirm the conversions before committing to the typed schema.
    /// </summary>
    public required string TransformSelect { get; init; }

    /// <summary>
    /// Per-column sanity check of the conversions against the actual data; null when validation was not
    /// run. Flags columns where non-null values would silently become NULL under the inferred type.
    /// </summary>
    public InferenceValidation? Validation { get; init; }
}

public sealed record InferredColumnReport
{
    public required string ColumnName { get; init; }
    public required string DataType { get; init; }
    public required string SelectExpression { get; init; }
    public bool Converted { get; init; }
    public int? Style { get; init; }

    /// <summary>Numeric convention used to normalize the column (Locale / Invariant); null if non-numeric.</summary>
    public string? NumericFormat { get; init; }

    public long Sampled { get; init; }
    public long Total { get; init; }

    /// <summary>What the profile counted of the column's values, which the type was chosen from: the counts each type
    /// converts, and the longest text and widest number seen. Counts and lengths only, never a value.</summary>
    public ColumnEvidence? Evidence { get; init; }
}

/// <summary>
/// The counts behind an inferred column type: of the column's non-null values in the profiled rows, how many convert to
/// each type the inference considers, with the type picked when every one of them does. A column whose values are all
/// 0 and 1 converts to <c>bit</c>, a whole number and a decimal alike, which is why the counts are worth reading beside
/// the type chosen: the type a value must have where it goes may be another of those it fits.
/// </summary>
public sealed record ColumnEvidence
{
    /// <summary>The non-null values profiled.</summary>
    public long NonNull { get; init; }

    /// <summary>Values that are boolean tokens (0, 1, true, false, yes, no, y, n).</summary>
    public long AsBoolean { get; init; }

    /// <summary>Values that convert to <c>bigint</c>.</summary>
    public long AsInteger { get; init; }

    /// <summary>Integer values with significant leading zeros (a code, a zip), which inference keeps as text.</summary>
    public long WithLeadingZeros { get; init; }

    /// <summary>Values that convert to <c>decimal</c> under the convention that fits most of them.</summary>
    public long AsDecimal { get; init; }

    /// <summary>Values that convert to <c>float</c> under that convention (scientific notation included).</summary>
    public long AsFloat { get; init; }

    /// <summary>The most digits after the decimal point, under that convention.</summary>
    public int MaxScale { get; init; }

    /// <summary>The most digits before the decimal point, under that convention.</summary>
    public int MaxIntegerDigits { get; init; }

    /// <summary>Values that convert to <c>datetime2</c> under the date style that fits most of them.</summary>
    public long AsDateTime { get; init; }

    /// <summary>Values that convert to <c>date</c> under the date style that fits most of them.</summary>
    public long AsDate { get; init; }

    /// <summary>Values that convert to <c>uniqueidentifier</c>.</summary>
    public long AsGuid { get; init; }

    /// <summary>The longest value, in characters.</summary>
    public int MaxLength { get; init; }

    /// <summary>The counts of one profile.</summary>
    public static ColumnEvidence From(ColumnProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var numeric = profile.NumericCandidates.OrderByDescending(c => c.AsDecimal).ThenByDescending(c => c.AsFloat).FirstOrDefault();
        return new ColumnEvidence
        {
            NonNull = profile.NonNull,
            AsBoolean = profile.AsBitTokens,
            AsInteger = profile.AsBigInt,
            WithLeadingZeros = profile.LeadingZeroInts,
            AsDecimal = numeric?.AsDecimal ?? 0,
            AsFloat = numeric?.AsFloat ?? 0,
            MaxScale = numeric?.MaxScale ?? 0,
            MaxIntegerDigits = numeric?.MaxIntegerDigits ?? 0,
            AsDateTime = profile.DateTimeCandidates.Select(c => c.Count).DefaultIfEmpty(0).Max(),
            AsDate = profile.DateCandidates.Select(c => c.Count).DefaultIfEmpty(0).Max(),
            AsGuid = profile.AsGuid,
            MaxLength = profile.MaxLen,
        };
    }
}

/// <summary>Result of running the inferred conversions against the table to sanity-check them.</summary>
public sealed record InferenceValidation
{
    /// <summary>True when no converted column would silently null out any non-null value.</summary>
    public required bool IsValid { get; init; }

    public required IReadOnlyList<ColumnValidation> Columns { get; init; }
}

/// <summary>How well an inferred type fits a column's actual data.</summary>
public sealed record ColumnValidation
{
    public required string ColumnName { get; init; }
    public required string DataType { get; init; }

    /// <summary>Non-null, non-empty values evaluated.</summary>
    public long NonNull { get; init; }

    /// <summary>Non-null values that the inferred conversion turns into NULL (data loss). Flagged.</summary>
    public long SilentNulls { get; init; }

    /// <summary>Percentage of evaluated values that convert cleanly (100 when nothing is lost).</summary>
    public double FitPercent { get; init; }

    /// <summary>
    /// ok (converts cleanly), lossy (some values would silently null out - flagged), kept-string (no
    /// conversion applied), or empty (no values to evaluate).
    /// </summary>
    public required string Status { get; init; }
}

/// <summary>A converted column to verify: its inferred type, optional date style, and numeric convention.</summary>
public sealed record ConversionCheck
{
    public required string ColumnName { get; init; }
    public required string DataType { get; init; }
    public int? Style { get; init; }

    /// <summary>
    /// The numeric convention to normalize the column with before converting; null for non-numeric
    /// columns. Ensures the validation reproduces exactly the conversion the transform emits.
    /// </summary>
    public NumericFormat? NumericFormat { get; init; }
}

/// <summary>The raw counts from running a conversion check against the table.</summary>
public sealed record ConversionCheckResult
{
    public required string ColumnName { get; init; }
    public long NonNull { get; init; }
    public long SilentNulls { get; init; }
}
