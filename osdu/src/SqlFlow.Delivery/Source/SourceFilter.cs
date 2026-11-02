using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Source;

/// <summary>
/// A predicate a flow appends, as written, to the reads of a table it declares (<c>filter</c>): the delivery flow's record table
/// and child datasets (osdu/docs/reference/flow/delivery-filter.md), and a removal flow's table source
/// (osdu/docs/reference/flow/removal.md). It carries its own leading <c>AND</c>, and is checked when the document loads so it
/// stays one predicate inside the read it narrows: it cannot end the statement, open a comment, or close a parenthesis it did
/// not open.
/// </summary>
public static partial class SourceFilter
{
    /// <summary>The longest filter a document may write.</summary>
    public const int MaxLength = 4000;

    /// <summary>
    /// What is wrong with <paramref name="filter"/>, as a sentence to follow <c>"{path} is '...'; "</c>, or null when it is a
    /// predicate a read can append.
    /// </summary>
    public static string? Problem(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Length > MaxLength)
        {
            return $"it is longer than {MaxLength} characters; narrow the rows with a view of the table, and read the view instead";
        }

        if (!LeadingAnd().IsMatch(filter))
        {
            return "it is appended to the read's WHERE as written, so it carries its own leading AND (AND status = 'active')";
        }

        var depth = 0;
        for (var i = 0; i < filter.Length; i++)
        {
            var c = filter[i];
            if (c is '\'' or '[' or '"')
            {
                // A string literal or a quoted identifier, its closing quote doubled when it holds one.
                var close = c == '[' ? ']' : c;
                var end = ClosingQuote(filter, i + 1, close);
                if (end < 0)
                {
                    return $"its {(c == '\'' ? "string" : "quoted name")} opened at character {i + 1} is never closed";
                }

                i = end;
                continue;
            }

            if (c == ';')
            {
                return "it holds a ';', which would end the read it narrows: a filter is one predicate";
            }

            if ((c == '-' && i + 1 < filter.Length && filter[i + 1] == '-') || (c == '/' && i + 1 < filter.Length && filter[i + 1] == '*'))
            {
                return "it holds a comment, which would hide the rest of the read it narrows: a filter is one predicate";
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth < 0)
            {
                return $"it closes a parenthesis at character {i + 1} that it did not open";
            }
        }

        return depth == 0 ? null : "it opens a parenthesis it never closes";
    }

    /// <summary>Where the quote that closes a literal starting at <paramref name="from"/> stands, a doubled one skipped; -1 when none does.</summary>
    private static int ClosingQuote(string filter, int from, char close)
    {
        for (var i = from; i < filter.Length; i++)
        {
            if (filter[i] != close)
            {
                continue;
            }

            if (i + 1 < filter.Length && filter[i + 1] == close)
            {
                i++;
                continue;
            }

            return i;
        }

        return -1;
    }

    /// <summary>A filter's text for a message: at most 60 characters of it.</summary>
    public static string Shown(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return filter.Length > 60 ? filter[..60] + "..." : filter;
    }

    [GeneratedRegex(@"^\s*AND\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingAnd();
}
