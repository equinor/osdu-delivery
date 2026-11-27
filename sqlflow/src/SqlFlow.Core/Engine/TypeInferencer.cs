using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using SqlFlow.Core.Model;

namespace SqlFlow.Core.Engine;

/// <summary>
/// Decides a column's SQL type from its <see cref="ColumnProfile"/>, a <see cref="TypeInferencePolicy"/>,
/// and the run's <see cref="ServerLocale"/>, and emits the SELECT expression that produces it. Pure and
/// fully testable: the candidate counts come from SQL Server's own <c>TRY_CONVERT</c>, so a chosen type
/// is always convertible. For date and numeric families it scans the locale-preferred candidates and
/// takes the FIRST that meets the threshold (not the highest count), so ambiguous values resolve to the
/// server locale consistently across every column and only genuinely foreign-formatted columns fall back.
/// The failure mode (<c>CONVERT</c> vs <c>TRY_CONVERT</c> vs keep-string) comes from the policy, which
/// defaults to fail-loud <c>CONVERT</c>.
/// </summary>
public sealed class TypeInferencer
{
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Instance method by design - resolved via DI and a seam for future inference configuration/state.")]
    public InferredColumn Infer(ColumnProfile profile, TypeInferencePolicy policy, ServerLocale locale)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(locale);

        if (profile.NonNull == 0)
        {
            return KeepAsString(profile);
        }

        bool Meets(long count) => (double)count / profile.NonNull >= policy.Threshold;

        // Boolean is detected by token set, NOT TRY_CONVERT(bit) - which coerces any number to 1.
        if (Meets(profile.AsBitTokens))
        {
            return Typed(profile, "bit", policy, profile.AsBitTokens);
        }

        // Integers, unless significant leading zeros mean the string is identity-like (zip/IDs).
        if (Meets(profile.AsBigInt) && !(policy.PreserveLeadingZeros && profile.LeadingZeroInts > 0))
        {
            return Typed(profile, IntegerType(profile), policy, profile.AsBigInt);
        }

        // Numerics: prefer the locale convention, fall back to invariant; take the first that fits.
        var asDecimal = profile.NumericCandidates.FirstOrDefault(c => Meets(c.AsDecimal));
        if (asDecimal is not null)
        {
            return Typed(profile, DecimalType(asDecimal), policy, asDecimal.AsDecimal, numeric: asDecimal, locale: locale);
        }

        // Float only catches what decimal can't (e.g. scientific notation).
        var asFloat = profile.NumericCandidates.FirstOrDefault(c => Meets(c.AsFloat));
        if (asFloat is not null)
        {
            return Typed(profile, "float", policy, asFloat.AsFloat, numeric: asFloat, locale: locale);
        }

        var asDateTime = profile.DateTimeCandidates.FirstOrDefault(c => Meets(c.Count));
        if (asDateTime is not null)
        {
            return Typed(profile, "datetime2", policy, asDateTime.Count, style: asDateTime.Style);
        }

        var asDate = profile.DateCandidates.FirstOrDefault(c => Meets(c.Count));
        if (asDate is not null)
        {
            return Typed(profile, "date", policy, asDate.Count, style: asDate.Style);
        }

        if (Meets(profile.AsGuid))
        {
            return Typed(profile, "uniqueidentifier", policy, profile.AsGuid);
        }

        return KeepAsString(profile);
    }

    /// <summary>
    /// Infers a column that already has <paramref name="existingType"/> in the typed view, keeping that type: inference
    /// never re-types an existing column, so a declared type is the only thing that changes one. Text stays text (the
    /// raw value passed through), whatever its values look like now. A typed column keeps its type, widening within its
    /// family when the values need it (an integer rank, a decimal's digits, a date becoming a date and time); when they
    /// no longer fit its family at all (a fraction in an integer column, a word in a date column) it still keeps its type,
    /// converted fail-loud, so the read of the view stops on the value rather than the column silently becoming
    /// something else. A null <paramref name="existingType"/> (a new column) is inferred as <see cref="Infer(ColumnProfile, TypeInferencePolicy, ServerLocale)"/> does.
    /// </summary>
    public InferredColumn Infer(ColumnProfile profile, TypeInferencePolicy policy, ServerLocale locale, string? existingType)
    {
        var inferred = Infer(profile, policy, locale);
        if (string.IsNullOrWhiteSpace(existingType))
        {
            return inferred;
        }

        var existing = Family(existingType);
        if (existing == Kind.Text)
        {
            return inferred.Converted ? KeepAsString(profile) : inferred;
        }

        var proposed = inferred.Converted ? Family(inferred.DataType) : Kind.Text;
        var strict = policy.OnConvertError == ConvertErrorMode.KeepString ? policy with { OnConvertError = ConvertErrorMode.Fail } : policy;
        if (existing == proposed)
        {
            var widest = Widest(existingType, inferred.DataType, existing);
            return string.Equals(widest, inferred.DataType, StringComparison.OrdinalIgnoreCase) ? inferred : As(profile, widest, existing, strict, locale);
        }

        if (existing == Kind.Date && proposed == Kind.DateTime)
        {
            return inferred; // a date becoming a date and time holds every date: a widening
        }

        return As(profile, existingType, existing == Kind.DateTime && proposed == Kind.Date ? Kind.DateTime : existing, strict, locale);
    }

    private enum Kind
    {
        Text,
        Integer,
        Decimal,
        Float,
        Date,
        DateTime,
        Bit,
        Guid,
        Other,
    }

    private static Kind Family(string type)
    {
        var open = type.IndexOf('(', StringComparison.Ordinal);
        var baseType = (open < 0 ? type : type[..open]).Trim().Trim('[', ']').ToLowerInvariant();
        return baseType switch
        {
            "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "sysname" => Kind.Text,
            "tinyint" or "smallint" or "int" or "bigint" => Kind.Integer,
            "decimal" or "numeric" => Kind.Decimal,
            "float" or "real" => Kind.Float,
            "date" => Kind.Date,
            "datetime2" => Kind.DateTime,
            "bit" => Kind.Bit,
            "uniqueidentifier" => Kind.Guid,
            _ => Kind.Other,
        };
    }

    // The wider of two types of one family: the higher integer rank, the decimal holding both (integer digits and scale
    // each the larger), float over real; any other family keeps the existing type.
    private static string Widest(string existing, string inferred, Kind family)
    {
        switch (family)
        {
            case Kind.Integer:
                return IntegerRank(inferred) > IntegerRank(existing) ? inferred : existing;
            case Kind.Decimal:
            {
                var (p1, s1) = PrecisionScale(existing);
                var (p2, s2) = PrecisionScale(inferred);
                var scale = Math.Max(s1, s2);
                var precision = Math.Clamp(Math.Max(p1 - s1, p2 - s2) + scale, 1, 38);
                return $"decimal({precision.ToString(CultureInfo.InvariantCulture)}, {Math.Min(scale, precision).ToString(CultureInfo.InvariantCulture)})";
            }

            case Kind.Float:
                return existing.StartsWith("float", StringComparison.OrdinalIgnoreCase) || inferred.StartsWith("float", StringComparison.OrdinalIgnoreCase)
                    ? "float"
                    : existing;
            default:
                return existing;
        }
    }

    private static int IntegerRank(string type) => Family(type) != Kind.Integer
        ? 0
        : type.Trim().ToLowerInvariant() switch
        {
            "tinyint" => 1,
            "smallint" => 2,
            "int" => 3,
            _ => 4,
        };

    private static (int Precision, int Scale) PrecisionScale(string type)
    {
        var open = type.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            return (18, 0);
        }

        var parts = type[(open + 1)..type.LastIndexOf(')')].Split(',', StringSplitOptions.TrimEntries);
        var precision = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var scale = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        return (precision, scale);
    }

    // The column typed as `type` whatever inference would pick: the best-converting candidate of the family supplies the
    // style or numeric convention, and a value outside the family fails the read (or becomes NULL under silentNull).
    private static InferredColumn As(ColumnProfile p, string type, Kind family, TypeInferencePolicy policy, ServerLocale locale)
    {
        if (p.NonNull == 0)
        {
            return Typed(p, type, policy, 0);
        }

        switch (family)
        {
            case Kind.Integer:
                return Typed(p, type, policy, p.AsBigInt);
            case Kind.Decimal when p.NumericCandidates.Count > 0:
            {
                var best = p.NumericCandidates.OrderByDescending(c => c.AsDecimal).First();
                return Typed(p, type, policy, best.AsDecimal, numeric: best, locale: locale);
            }

            case Kind.Float when p.NumericCandidates.Count > 0:
            {
                var best = p.NumericCandidates.OrderByDescending(c => c.AsFloat).First();
                return Typed(p, type, policy, best.AsFloat, numeric: best, locale: locale);
            }

            case Kind.Date when p.DateCandidates.Count > 0:
            {
                var best = p.DateCandidates.OrderByDescending(c => c.Count).First();
                return Typed(p, type, policy, best.Count, style: best.Style);
            }

            case Kind.DateTime when p.DateTimeCandidates.Count > 0:
            {
                var best = p.DateTimeCandidates.OrderByDescending(c => c.Count).First();
                return Typed(p, type, policy, best.Count, style: best.Style);
            }

            case Kind.Bit:
                return Typed(p, type, policy, p.AsBitTokens);
            case Kind.Guid:
                return Typed(p, type, policy, p.AsGuid);
            default:
                return Typed(p, type, policy, 0);
        }
    }

    private static string IntegerType(ColumnProfile p)
    {
        var min = p.MinValue ?? long.MinValue;
        var max = p.MaxValue ?? long.MaxValue;

        if (min >= 0 && max <= 255) return "tinyint";
        if (min >= -32768 && max <= 32767) return "smallint";
        if (min >= -2147483648 && max <= 2147483647) return "int";
        return "bigint";
    }

    private static string DecimalType(NumericConversionCandidate c)
    {
        var scale = Math.Clamp(c.MaxScale, 0, 38);
        var precision = Math.Clamp(c.MaxIntegerDigits + scale, 1, 38);
        if (scale > precision)
        {
            scale = precision;
        }

        return $"decimal({precision.ToString(CultureInfo.InvariantCulture)}, {scale.ToString(CultureInfo.InvariantCulture)})";
    }

    private static InferredColumn Typed(
        ColumnProfile p,
        string type,
        TypeInferencePolicy policy,
        long convertibleCount,
        int? style = null,
        NumericConversionCandidate? numeric = null,
        ServerLocale? locale = null)
    {
        // keep-string: only convert when every non-null value fits; otherwise preserve the raw string.
        if (policy.OnConvertError == ConvertErrorMode.KeepString && convertibleCount != p.NonNull)
        {
            return KeepAsString(p);
        }

        var function = policy.OnConvertError == ConvertErrorMode.Fail ? "CONVERT" : "TRY_CONVERT";
        var bracketed = $"[{Escape(p.ColumnName)}]";

        // Numerics are normalized (strip grouping, decimal -> '.') so the locale's convention converts;
        // every other family converts the raw column directly.
        var input = numeric is not null && locale is not null
            ? LocaleConversion.NumericInput(bracketed, numeric.Format, locale)
            : bracketed;

        var styleArgument = style is { } s ? $", {s.ToString(CultureInfo.InvariantCulture)}" : string.Empty;

        return new InferredColumn
        {
            ColumnName = p.ColumnName,
            DataType = type,
            SelectExpression = $"{function}({type}, {input}{styleArgument})",
            Converted = true,
            Style = style,
            NumericFormat = numeric?.Format,
        };
    }

    private static InferredColumn KeepAsString(ColumnProfile p) => new()
    {
        ColumnName = p.ColumnName,
        DataType = $"varchar({Math.Clamp(p.MaxLen, 1, 8000).ToString(CultureInfo.InvariantCulture)})",
        SelectExpression = $"[{Escape(p.ColumnName)}]",
        Converted = false,
    };

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);
}
