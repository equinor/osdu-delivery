using System.Text;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.SqlServer.Query;

namespace SqlFlow.Delivery.Engine.Assertions;

/// <summary>
/// A query's statement with its <c>{parameter}</c> and <c>{partition}</c> tokens bound as values
/// (osdu/docs/reference/flow/staged-proof-values.md, Parameters): each token outside a comment becomes an <c>@name</c>
/// parameter, so a value is sent to the database beside the statement and never written into it. A token inside a string
/// literal or a quoted name would be read as text, never as the value, so it is refused, saying how to write it.
/// </summary>
public static class StagedSql
{
    /// <summary>The statement with its tokens as <c>@name</c> parameters, and the names of the tokens in the order they first appear.</summary>
    /// <exception cref="FlowValidationException">A token sits inside a string literal or a quoted name.</exception>
    public static (string Sql, IReadOnlyList<string> Tokens) Parameterize(string sql, string at)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var text = new StringBuilder(sql.Length);
        var tokens = new List<string>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '-' && At(sql, i + 1, '-'))
            {
                var end = sql.IndexOf('\n', i);
                end = end < 0 ? sql.Length : end;
                text.Append(sql, i, end - i);
                i = end;
                continue;
            }

            if (c == '/' && At(sql, i + 1, '*'))
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? sql.Length : end + 2;
                text.Append(sql, i, end - i);
                i = end;
                continue;
            }

            if (c is '\'' or '"' or '[')
            {
                var close = c == '[' ? ']' : c;
                var end = i + 1;
                while (end < sql.Length && !(sql[end] == close && !At(sql, end + 1, close)))
                {
                    end += sql[end] == close ? 2 : 1;
                }

                end = Math.Min(sql.Length, end + 1);
                var quoted = sql[i..end];
                if (FirstToken(quoted) is { } inside)
                {
                    throw new FlowValidationException(c == '\''
                        ? $"{at} writes {{{inside}}} inside a string literal ({Shown(quoted)}), where it would be read as text. A token is bound as a value: write it without quotes, as source_system = {{{inside}}}."
                        : $"{at} writes {{{inside}}} inside a quoted name ({Shown(quoted)}). A token is bound as a value, never as a name: name the table or column in the statement itself.");
                }

                text.Append(quoted);
                i = end;
                continue;
            }

            if (c == '{' && TokenAt(sql, i) is { } token)
            {
                text.Append('@').Append(ParameterName(token));
                if (!tokens.Contains(token, StringComparer.Ordinal))
                {
                    tokens.Add(token);
                }

                i += token.Length + 2;
                continue;
            }

            text.Append(c);
            i++;
        }

        return (text.ToString(), tokens);
    }

    /// <summary>
    /// Checks a query as a document declares it: a statement that parses as one read-only SELECT once its tokens are
    /// parameters, each token a parameter the flow declares or <c>{partition}</c>. Returns the tokens it binds.
    /// </summary>
    /// <exception cref="FlowValidationException">The statement is not one read-only SELECT, or names a token the flow does not declare.</exception>
    public static IReadOnlyList<string> Check(string sql, IReadOnlyDictionary<string, FlowParameter> parameters, string at)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var (bound, tokens) = Parameterize(sql, at);
        foreach (var token in tokens)
        {
            if (token != PartitionNames.RunValue && !parameters.ContainsKey(token))
            {
                throw new FlowValidationException($"{at} uses '{{{token}}}', which is neither {{{PartitionNames.RunValue}}} nor declared under parameters.");
            }
        }

        try
        {
            ReadOnlyQueryGuard.Validate(bound);
        }
        catch (SqlFlowException ex)
        {
            throw new FlowValidationException($"{at} is not a query a test may run: {ex.Message}", ex);
        }

        return tokens;
    }

    /// <summary>The read a run makes of a query: its statement with tokens as parameters, and each parameter's value from <paramref name="values"/>.</summary>
    /// <exception cref="DeliveryException">A token has no value in <paramref name="values"/>.</exception>
    public static ReadOnlyRead Read(StagedQuery query, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(values);
        var (sql, tokens) = Parameterize(query.Sql, query.ToString());
        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            parameters[ParameterName(token)] = values.TryGetValue(token, out var value)
                ? value
                : throw new DeliveryException($"{query} uses '{{{token}}}', which the run gives no value.");
        }

        return new ReadOnlyRead { Sql = sql, Parameters = parameters, TimeoutSeconds = query.TimeoutSeconds };
    }

    /// <summary>The parameter a token is bound as: its name, with a leading underscore when it starts with a digit.</summary>
    private static string ParameterName(string token) => char.IsAsciiDigit(token[0]) ? "_" + token : token;

    private static bool At(string text, int index, char c) => index < text.Length && text[index] == c;

    /// <summary>The token (<c>{name}</c>) starting at <paramref name="index"/>, or null when none does.</summary>
    private static string? TokenAt(string text, int index)
    {
        var end = index + 1;
        while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '_'))
        {
            end++;
        }

        return end > index + 1 && end < text.Length && text[end] == '}' ? text[(index + 1)..end] : null;
    }

    private static string? FirstToken(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '{' && TokenAt(text, i) is { } token)
            {
                return token;
            }
        }

        return null;
    }

    private static string Shown(string text) => text.Length > 60 ? text[..60] + "..." : text;
}
