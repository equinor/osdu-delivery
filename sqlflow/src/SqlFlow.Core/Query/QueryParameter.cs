using System.Globalization;
using System.Text.RegularExpressions;

namespace SqlFlow.Core.Query;

/// <summary>
/// The types a query parameter may have. Each is a short lower-case token, the same in a document, in an API payload and
/// on a queue row, and each names the one way its values are written as text (<see cref="QueryParameter.TryRead"/>).
/// </summary>
public static class QueryParameterTypes
{
    /// <summary>Text, at most <see cref="QueryParameter.MaxTextLength"/> characters. Sent as <c>nvarchar</c>.</summary>
    public const string Text = "text";

    /// <summary>A whole number in the range of a 64-bit integer. Sent as <c>bigint</c>.</summary>
    public const string WholeNumber = "integer";

    /// <summary>A decimal number written with a period and no grouping. Sent as <c>decimal</c>.</summary>
    public const string Number = "number";

    /// <summary>A calendar date written <c>yyyy-MM-dd</c>. Sent as <c>date</c>.</summary>
    public const string Date = "date";

    /// <summary>A date and time written <c>yyyy-MM-ddTHH:mm:ss</c>, with optional fractional seconds and no zone. Sent as
    /// <c>datetime2</c>.</summary>
    public const string DateTime = "datetime";

    /// <summary>Yes or no, written <c>true</c> or <c>false</c>. Sent as <c>bit</c>.</summary>
    public const string Flag = "flag";

    /// <summary>Every type, in the order a message lists them.</summary>
    public static readonly IReadOnlyList<string> All = [Text, WholeNumber, Number, Date, DateTime, Flag];

    /// <summary>Whether <paramref name="type"/> is one of <see cref="All"/>, written exactly.</summary>
    public static bool IsKnown(string? type) => type is not null && All.Contains(type, StringComparer.Ordinal);

    /// <summary>The type as a sentence names a value of it: "an integer", "a date".</summary>
    public static string Named(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.Length > 0 && "aeiou".Contains(type[0], StringComparison.OrdinalIgnoreCase) ? $"an {type}" : $"a {type}";
    }
}

/// <summary>
/// One named value a read-only query binds: the <c>@name</c> the statement refers to, the value's type, and the value as
/// text (null is SQL NULL). A value is always sent to the engine as a typed parameter and never written into the
/// statement's text, so no value a person gives can change what the statement is.
/// </summary>
/// <param name="Name">The parameter's name, without the <c>@</c>.</param>
/// <param name="Type">One of <see cref="QueryParameterTypes"/>.</param>
/// <param name="Value">The value as text in its type's form, or null for SQL NULL.</param>
public sealed partial record QueryParameter(string Name, string Type, string? Value)
{
    /// <summary>The longest parameter name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The longest text value.</summary>
    public const int MaxTextLength = 4000;

    /// <summary>The most parameters one statement binds.</summary>
    public const int MaxParameters = 100;

    /// <summary>Whether <paramref name="name"/> can name a parameter: a letter or underscore, then letters, digits or
    /// underscores, at most <see cref="MaxNameLength"/> characters, written without the <c>@</c>.</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    /// <summary>
    /// Reads <paramref name="value"/> as a value of <paramref name="type"/>. On success <paramref name="read"/> is the value
    /// the engine is sent (a <see cref="string"/>, <see cref="long"/>, <see cref="decimal"/>, <see cref="DateOnly"/>,
    /// <see cref="System.DateTime"/> or <see cref="bool"/>) and <paramref name="canonical"/> is the one way that value is
    /// written, so two spellings of one value compare equal. On failure <paramref name="problem"/> says what a value of
    /// the type looks like; the value itself is never repeated, since it may be long or may be something a person typed
    /// by mistake.
    /// </summary>
    public static bool TryRead(string type, string value, out object read, out string canonical, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(value);
        read = value;
        canonical = value;
        problem = null;
        var text = value.Trim();
        switch (type)
        {
            case QueryParameterTypes.Text:
                if (value.Length > MaxTextLength)
                {
                    problem = $"a text value may be at most {MaxTextLength} characters, and this one is {value.Length}";
                    return false;
                }

                return true;

            case QueryParameterTypes.WholeNumber:
                if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
                {
                    problem = "an integer is written as digits with an optional leading minus, such as 30 or -7";
                    return false;
                }

                read = whole;
                canonical = whole.ToString(CultureInfo.InvariantCulture);
                return true;

            case QueryParameterTypes.Number:
                if (!decimal.TryParse(
                        text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
                {
                    problem = "a number is written with a period and no grouping, such as 0.25 or -1200.5";
                    return false;
                }

                // Written without the zeros a person may have typed after the last digit that counts, so 1.50 and 1.5
                // are one value wherever two values are compared (a default with its choices, a run's value with them).
                number /= 1.000000000000000000000000000000m;
                read = number;
                canonical = number.ToString(CultureInfo.InvariantCulture);
                return true;

            case QueryParameterTypes.Date:
                if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    problem = "a date is written yyyy-MM-dd, such as 2026-01-31";
                    return false;
                }

                read = date;
                canonical = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return true;

            case QueryParameterTypes.DateTime:
                if (!System.DateTime.TryParseExact(
                        text, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment))
                {
                    problem = "a date and time is written yyyy-MM-ddTHH:mm:ss without a zone, such as 2026-01-31T06:00:00";
                    return false;
                }

                read = moment;
                canonical = moment.ToString(
                    moment.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd'T'HH:mm:ss" : "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
                    CultureInfo.InvariantCulture);
                return true;

            case QueryParameterTypes.Flag:
                if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
                {
                    read = true;
                    canonical = "true";
                    return true;
                }

                if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
                {
                    read = false;
                    canonical = "false";
                    return true;
                }

                problem = "a flag is written true or false";
                return false;

            default:
                problem = $"'{type}' is not a parameter type; the types are {string.Join(", ", QueryParameterTypes.All)}";
                return false;
        }
    }

    /// <summary>The forms a date and time is read in: to the minute, to the second, or with fractional seconds, with a
    /// <c>T</c> or a space between the date and the time.</summary>
    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm",
    ];

    /// <summary>
    /// Refuses a parameter whose name cannot be a parameter's, whose type is unknown, or whose value is not a value of its
    /// type. The message names the parameter and what is wrong with it.
    /// </summary>
    /// <exception cref="SqlFlowException">The parameter is not one a statement can bind.</exception>
    public QueryParameter Validate()
    {
        if (!IsValidName(Name))
        {
            throw new SqlFlowException(
                $"'{Name}' is not a parameter name: a letter or '_', then letters, digits and '_', at most {MaxNameLength} characters, written without the '@'.");
        }

        if (!QueryParameterTypes.IsKnown(Type))
        {
            throw new SqlFlowException(
                $"Parameter '{Name}' has the type '{Type}'; the types are {string.Join(", ", QueryParameterTypes.All)}.");
        }

        if (Value is not null && !TryRead(Type, Value, out _, out _, out var problem))
        {
            throw new SqlFlowException($"The value of parameter '{Name}' is not {QueryParameterTypes.Named(Type)}: {problem}.");
        }

        return this;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
