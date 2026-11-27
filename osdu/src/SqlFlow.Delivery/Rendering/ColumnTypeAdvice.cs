using System.Globalization;
using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Rendering;

/// <summary>
/// The SQL type a source column should have for the property of the template a mapping writes it into, and what the
/// column's type does now: the render's own rules for each kind of property (<see cref="NumberValues"/> for a number,
/// <see cref="DateValues"/> for a date), stated for a column rather than for a value. A column read only to find a record
/// (a lookup's key, a reference's code) is matched as text whatever the property it leads to, so it is advised as text.
/// </summary>
public static partial class ColumnTypeAdvice
{
    /// <summary>The type a column that feeds a number property should have: exact, with room for any depth or reading, as
    /// the repository's guides declare it.</summary>
    public const string NumberType = "decimal(38,18)";

    /// <summary>The type a column that feeds an integer property should have.</summary>
    public const string IntegerType = "bigint";

    /// <summary>The type a column that feeds a date and time property should have.</summary>
    public const string DateTimeType = "datetime2";

    /// <summary>The type a column that feeds a date property should have.</summary>
    public const string DateType = "date";

    /// <summary>The type a column that feeds a true-or-false property should have.</summary>
    public const string BooleanType = "bit";

    /// <summary>The type a column that feeds a text property should have when nothing says how long its values are.</summary>
    public const string TextType = "nvarchar(max)";

    /// <summary>Whether a value of the column's type renders as the property needs.</summary>
    public static class Verdicts
    {
        /// <summary>Every value of the type renders as the property needs it.</summary>
        public const string Fits = "fits";

        /// <summary>The render reads a value of the type from its text: each value has to be written as the property's
        /// kind of value, or the record is held.</summary>
        public const string Parsed = "parsed";

        /// <summary>The type renders, but not exactly: a floating point number's digits, or a whole number for a code.</summary>
        public const string Approximate = "approximate";

        /// <summary>The render refuses every value of the type for the property, and holds the record.</summary>
        public const string Refused = "refused";

        /// <summary>Nothing says what the column's type is.</summary>
        public const string Unknown = "unknown";
    }

    /// <summary>The count of a value profile (the <c>evidence</c> of an <c>inferColumnTypes</c> answer) that must equal its
    /// <c>nonNull</c> count for the column's values to convert to what the property needs: how many of them convert to a
    /// decimal, a whole number, a date and time, a date or a boolean. None for a text property, which takes any value.</summary>
    public static class ProfileCounts
    {
        public const string AsDecimal = "asDecimal";
        public const string AsInteger = "asInteger";
        public const string AsDateTime = "asDateTime";
        public const string AsDate = "asDate";
        public const string AsBoolean = "asBoolean";
    }

    /// <summary>
    /// The advice for one column: the type it should have, what its type does now, why, and which count of a value profile
    /// says its values fit.
    /// </summary>
    /// <param name="Recommended">The SQL type to declare for the column.</param>
    /// <param name="Verdict">One of <see cref="Verdicts"/>, for the column's type now.</param>
    /// <param name="Reason">Why, in a sentence that names the property's kind and the column's type.</param>
    /// <param name="ProfileCount">One of <see cref="ProfileCounts"/>, or null when any value fits.</param>
    public sealed record Advice(string Recommended, string Verdict, string Reason, string? ProfileCount);

    /// <summary>
    /// The advice for a column the mapping writes into a property of <paramref name="propertyType"/> (as the mapping's
    /// outline names it: <c>number</c>, <c>integer</c>, <c>boolean</c>, <c>date-time</c>, <c>date</c>, <c>reference</c>,
    /// <c>string</c> or another text format), given the column's SQL type now (<paramref name="columnType"/>, null when
    /// unknown), whether the mapping changes the value on its way (<paramref name="changedOnItsWay"/>: a modifier that
    /// parses or rewrites text, which needs text in), and whether the column is read only to find a record
    /// (<paramref name="matchedOnly"/>).
    /// </summary>
    public static Advice For(string? propertyType, string? columnType, bool changedOnItsWay, bool matchedOnly)
    {
        var family = FamilyOf(columnType);
        if (matchedOnly || changedOnItsWay)
        {
            return Text(
                family,
                matchedOnly ? "it is read to find a record, which is matched by its text" : "the mapping reworks its text on the way",
                codeLike: true);
        }

        return (propertyType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "number" => Number(family, columnType),
            "integer" => Integer(family, columnType),
            "boolean" => Boolean(family),
            "date-time" => Dated(family, DateTimeType, ProfileCounts.AsDateTime, "a date and time"),
            "date" => Dated(family, DateType, ProfileCounts.AsDate, "a date"),
            _ => Text(family, "the property is text", codeLike: propertyType is "reference"),
        };
    }

    private static Advice Number(TypeFamily family, string? type) => family switch
    {
        TypeFamily.Exact or TypeFamily.Integer => new(NumberType, Verdicts.Fits, $"The property is a number and {type} is an exact number.", ProfileCounts.AsDecimal),
        TypeFamily.Approximate => new(NumberType, Verdicts.Approximate, $"The property is a number and {type} is a floating point number, whose digits can differ from the ones the source wrote: declare {NumberType}.", ProfileCounts.AsDecimal),
        TypeFamily.Bit => new(NumberType, Verdicts.Refused, $"The property is a number and the render refuses a {type} value (a boolean is not a number), so every record is held: declare {NumberType}.", ProfileCounts.AsDecimal),
        TypeFamily.Temporal => new(NumberType, Verdicts.Refused, $"The property is a number and the render refuses a {type} value (a date is not a number): declare {NumberType}.", ProfileCounts.AsDecimal),
        TypeFamily.Text => new(NumberType, Verdicts.Parsed, $"The property is a number and {type} is text, which the render reads as a number with a point for decimals; a value that is not one holds its record: declare {NumberType}.", ProfileCounts.AsDecimal),
        TypeFamily.Other => new(NumberType, Verdicts.Refused, $"The property is a number and {type} is not one: declare {NumberType}.", ProfileCounts.AsDecimal),
        _ => new(NumberType, Verdicts.Unknown, $"The property is a number; the column's type is not known: declare {NumberType}.", ProfileCounts.AsDecimal),
    };

    private static Advice Integer(TypeFamily family, string? type)
    {
        if (family == TypeFamily.Exact && type is not null && ScaleOf(type) > 0)
        {
            return new(IntegerType, Verdicts.Parsed, $"The property is a whole number and {type} has a fraction, which holds a record whose value has one: declare {IntegerType}.", ProfileCounts.AsInteger);
        }

        return family switch
        {
            TypeFamily.Integer or TypeFamily.Exact => new(IntegerType, Verdicts.Fits, $"The property is a whole number and {type} is one.", ProfileCounts.AsInteger),
            TypeFamily.Approximate => new(IntegerType, Verdicts.Approximate, $"The property is a whole number and {type} is a floating point number: declare {IntegerType}.", ProfileCounts.AsInteger),
            TypeFamily.Bit => new(IntegerType, Verdicts.Refused, $"The property is a whole number and the render refuses a {type} value (a boolean is not a number): declare {IntegerType}.", ProfileCounts.AsInteger),
            TypeFamily.Temporal => new(IntegerType, Verdicts.Refused, $"The property is a whole number and the render refuses a {type} value: declare {IntegerType}.", ProfileCounts.AsInteger),
            TypeFamily.Text => new(IntegerType, Verdicts.Parsed, $"The property is a whole number and {type} is text, which the render reads as a number; a value that is not a whole number holds its record: declare {IntegerType}.", ProfileCounts.AsInteger),
            TypeFamily.Other => new(IntegerType, Verdicts.Refused, $"The property is a whole number and {type} is not one: declare {IntegerType}.", ProfileCounts.AsInteger),
            _ => new(IntegerType, Verdicts.Unknown, $"The property is a whole number; the column's type is not known: declare {IntegerType}.", ProfileCounts.AsInteger),
        };
    }

    private static Advice Boolean(TypeFamily family) => family switch
    {
        TypeFamily.Bit => new(BooleanType, Verdicts.Fits, "The property is true or false and bit is one.", ProfileCounts.AsBoolean),
        TypeFamily.Unknown => new(BooleanType, Verdicts.Unknown, $"The property is true or false; the column's type is not known: declare {BooleanType}.", ProfileCounts.AsBoolean),
        _ => new(BooleanType, Verdicts.Parsed, $"The property is true or false: declare {BooleanType}, so a value is one or the other rather than text or a number the render has to read.", ProfileCounts.AsBoolean),
    };

    private static Advice Dated(TypeFamily family, string recommended, string convertsTo, string what) => family switch
    {
        TypeFamily.Temporal => new(recommended, Verdicts.Fits, $"The property is {what} and the column holds one, which the render writes in UTC.", convertsTo),
        TypeFamily.Text => new(recommended, Verdicts.Parsed, $"The property is {what} and the column is text, which reaches the record as it is written: every value must already be an RFC 3339 date, or a date modifier must read it in the format the source writes. Declare {recommended}, and the render writes it.", convertsTo),
        TypeFamily.Unknown => new(recommended, Verdicts.Unknown, $"The property is {what}; the column's type is not known: declare {recommended}.", convertsTo),
        _ => new(recommended, Verdicts.Refused, $"The property is {what} and the column's type is not a date: declare {recommended}.", convertsTo),
    };

    private static Advice Text(TypeFamily family, string why, bool codeLike) => family switch
    {
        TypeFamily.Text => new(TextType, Verdicts.Fits, $"Text: {why}, and the column is text."
            + " Declare a length the values fit where one is known (the profile's longest value).", null),
        TypeFamily.Unknown => new(TextType, Verdicts.Unknown, $"Text: {why}; the column's type is not known.", null),
        TypeFamily.Integer or TypeFamily.Exact or TypeFamily.Approximate when codeLike => new(TextType, Verdicts.Approximate,
            $"Text: {why}, and the column is a number, written as the number's own digits: a code with leading zeros loses them. Keep it as the text the source wrote.", null),
        _ => new(TextType, Verdicts.Parsed, $"Text: {why}; the column's values are written as text.", null),
    };

    /// <summary>The digits after the point a <c>decimal(p,s)</c> or <c>numeric(p,s)</c> keeps; 0 when none are given.</summary>
    private static int ScaleOf(string type)
    {
        var match = ScaleExpression().Match(type);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    private static TypeFamily FamilyOf(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return TypeFamily.Unknown;
        }

        var name = type.Trim().ToLowerInvariant();
        var paren = name.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0)
        {
            name = name[..paren].Trim();
        }

        return name switch
        {
            "bit" => TypeFamily.Bit,
            "tinyint" or "smallint" or "int" or "bigint" => TypeFamily.Integer,
            "decimal" or "numeric" or "money" or "smallmoney" => TypeFamily.Exact,
            "float" or "real" => TypeFamily.Approximate,
            "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" or "time" => TypeFamily.Temporal,
            "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "uniqueidentifier" or "xml" or "sysname" => TypeFamily.Text,
            _ => TypeFamily.Other,
        };
    }

    [GeneratedRegex(@"\(\s*\d+\s*,\s*(\d+)\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex ScaleExpression();

    private enum TypeFamily
    {
        Unknown,
        Bit,
        Integer,
        Exact,
        Approximate,
        Temporal,
        Text,
        Other,
    }
}
