namespace SqlFlow.Core.Reports;

/// <summary>
/// Reads the parameters a report's statement refers to. It scans the text the way T-SQL does where it matters for this:
/// an <c>@name</c> inside a string, a quoted or bracketed name, or a comment is not a parameter, and <c>@@name</c> is a
/// system function. It does not decide whether the statement is a valid, read-only <c>SELECT</c>: the T-SQL parser does
/// that, where the statement is checked and again where it runs.
/// </summary>
public static class ReportSql
{
    /// <summary>
    /// The names the statement refers to as <c>@name</c>, without the <c>@</c>, each once, in the order the statement
    /// first names it. Names are compared without regard to case, as T-SQL compares a variable's name, and the first
    /// spelling is the one returned.
    /// </summary>
    public static IReadOnlyList<string> ParameterNames(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (c == '-' && next == '-')
            {
                // A line comment runs to the end of the line.
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && next == '*')
            {
                i = SkipBlockComment(sql, i);
                continue;
            }

            if (c == '\'')
            {
                // A string: a doubled quote is a quote inside it.
                i = SkipQuoted(sql, i, '\'');
                continue;
            }

            if (c == '"')
            {
                i = SkipQuoted(sql, i, '"');
                continue;
            }

            if (c == '[')
            {
                // A bracketed name: a doubled closing bracket is a bracket inside it.
                i = SkipQuoted(sql, i, ']');
                continue;
            }

            if (c == '@')
            {
                if (next == '@')
                {
                    // @@ROWCOUNT and its kind: a system function, read to the end of its name.
                    i += 2;
                    while (i < sql.Length && IsNamePart(sql[i]))
                    {
                        i++;
                    }

                    continue;
                }

                var start = i + 1;
                var end = start;
                while (end < sql.Length && (IsNamePart(sql[end]) || sql[end] == '@'))
                {
                    end++;
                }

                if (end > start)
                {
                    var name = sql[start..end];
                    if (seen.Add(name))
                    {
                        names.Add(name);
                    }
                }

                i = Math.Max(end, i + 1);
                continue;
            }

            if (IsNamePart(c))
            {
                // A name or a number is read whole. T-SQL lets a name carry an '@' after its first character, so one
                // inside a word belongs to the word and is never taken for a parameter.
                while (i < sql.Length && (IsNamePart(sql[i]) || sql[i] == '@'))
                {
                    i++;
                }

                continue;
            }

            i++;
        }

        return names;
    }

    private static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || c is '_' or '#' or '$';

    /// <summary>The index after the block comment that starts at <paramref name="start"/>; T-SQL nests them. An
    /// unterminated comment runs to the end of the text.</summary>
    private static int SkipBlockComment(string sql, int start)
    {
        var depth = 0;
        var i = start;
        while (i < sql.Length)
        {
            if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                depth++;
                i += 2;
                continue;
            }

            if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/')
            {
                depth--;
                i += 2;
                if (depth == 0)
                {
                    return i;
                }

                continue;
            }

            i++;
        }

        return sql.Length;
    }

    /// <summary>The index after the quoted run that starts at <paramref name="start"/> and ends at
    /// <paramref name="close"/>, a doubled <paramref name="close"/> standing for the character itself. An unterminated run
    /// goes to the end of the text.</summary>
    private static int SkipQuoted(string sql, int start, char close)
    {
        var i = start + 1;
        while (i < sql.Length)
        {
            if (sql[i] == close)
            {
                if (i + 1 < sql.Length && sql[i + 1] == close)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return sql.Length;
    }
}
