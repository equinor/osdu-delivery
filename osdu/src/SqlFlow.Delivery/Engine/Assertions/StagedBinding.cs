using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Assertions;

/// <summary>A test whose queries a run answered, and the assertions it could not answer them for.</summary>
/// <param name="Test">The test, each query's value in place of the query, and the rows of known values read from a query in place of the one row naming its columns.</param>
/// <param name="Unanswered">Why each assertion that compares with a query could not be judged, by its index in the test.</param>
/// <param name="Notes">What each query answered, as the test's result notes it.</param>
public sealed record StagedAnswers(AssertionTest Test, IReadOnlyDictionary<int, string> Unanswered, IReadOnlyList<string> Notes);

/// <summary>
/// Answers the queries a test compares with before it is judged (osdu/docs/reference/flow/staged-proof-values.md): each value
/// a query answers takes the query's place, checked to be a value the comparison can take, and known values read from a query
/// take one row for each row it answers, each column the row names replaced by the value that row holds. An assertion whose
/// query fails, or answers what it cannot compare with, is not judged, and says why; the test's other assertions are.
/// </summary>
public static class StagedBinding
{
    /// <summary>Whether a test compares with any query, so a run needs to answer one before it judges the test.</summary>
    public static bool Asks(AssertionTest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        return test.Assertions.Any(a => a is KnownValuesAssertion { Query: not null } || AssertionOperands.Of(a).Any(v => v.Kind == ExpectedValueKind.Query));
    }

    /// <param name="test">The test, its tokens bound.</param>
    /// <param name="queries">Answers the queries, each once per run.</param>
    /// <param name="now">When the run judges the test: an assertion past the day its values hold until is not judged, so its query is not asked.</param>
    /// <param name="ct">Stops the queries.</param>
    public static async Task<StagedAnswers> AnswerAsync(AssertionTest test, StagedQueries queries, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(test);
        ArgumentNullException.ThrowIfNull(queries);
        var assertions = test.Assertions.ToList();
        var unanswered = new Dictionary<int, string>();
        var notes = new List<string>();
        for (var i = 0; i < assertions.Count; i++)
        {
            var assertion = assertions[i];
            if (AssertionActivity.Disabled(assertion) is not null || AssertionActivity.Stale(assertion, test, now) is not null)
            {
                continue;
            }

            try
            {
                var answered = await ValuesAsync(assertion, queries, notes, ct).ConfigureAwait(false);
                if (answered is KnownValuesAssertion { Query: { } query, Answered: false } known)
                {
                    answered = await RowsAsync(known, query, queries, notes, ct).ConfigureAwait(false);
                }

                assertions[i] = answered;
            }
            catch (Exception ex) when (ex is SqlFlowException or TimeoutException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                unanswered[i] = SecretHygiene.RedactedMessage(ex);
            }
        }

        return new StagedAnswers(test with { Assertions = assertions }, unanswered, notes);
    }

    /// <summary>The assertion with the value each query it compares with answers, each checked to be one the comparison takes.</summary>
    private static async Task<TestAssertion> ValuesAsync(TestAssertion assertion, StagedQueries queries, List<string> notes, CancellationToken ct)
    {
        var asked = AssertionOperands.Of(assertion).Where(v => v.Kind == ExpectedValueKind.Query).Select(v => v.Query!).Distinct().ToList();
        if (asked.Count == 0)
        {
            return assertion;
        }

        var answers = new Dictionary<StagedQuery, ExpectedValue>();
        foreach (var query in asked)
        {
            var answer = await queries.ValueAsync(query, ct).ConfigureAwait(false);
            answers[query] = answer;
            lock (notes)
            {
                var note = $"{Capitalized(query.ToString())} answered {Shown(answer)}.";
                if (!notes.Contains(note, StringComparer.Ordinal))
                {
                    notes.Add(note);
                }
            }
        }

        var bound = AssertionOperands.Map(assertion, v => v.Kind == ExpectedValueKind.Query ? answers[v.Query!] : v);
        if (Refusal(bound) is { } refused)
        {
            throw new DeliveryException(refused);
        }

        return bound;
    }

    /// <summary>
    /// Known values read from a query: one row for each row it answers, the one row the document writes taken as the pattern,
    /// each column it names replaced by the value that row holds there. A row holding no value where it is located by locates
    /// nothing, and is not found for that reason.
    /// </summary>
    private static async Task<KnownValuesAssertion> RowsAsync(
        KnownValuesAssertion known, StagedQuery query, StagedQueries queries, List<string> notes, CancellationToken ct)
    {
        var pattern = known.Rows[0];
        var rows = await queries.RowsAsync(query, known.MaxRows ?? KnownValuesAssertion.DefaultQueryRows, ct).ConfigureAwait(false);
        var columns = rows.Columns.Select((name, index) => (name, index)).ToDictionary(c => c.name, c => c.index, StringComparer.OrdinalIgnoreCase);
        var named = AssertionOperands.Of(known).Where(v => v.Kind == ExpectedValueKind.Column).Select(v => v.Text)
            .Concat(pattern.Locate.Where(l => l.Value.Kind == ExpectedValueKind.Column).Select(l => l.Value.Text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var missing = named.Where(c => !columns.ContainsKey(c)).ToList();
        if (missing.Count > 0)
        {
            throw new DeliveryException(
                $"The known values name {(missing.Count == 1 ? "the column" : "the columns")} {string.Join(", ", missing)}, which {query} does not answer; it answers {string.Join(", ", rows.Columns)}.");
        }

        lock (notes)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{Capitalized(query.ToString())} answered {rows.Rows.Count} row(s) of known values."));
        }

        if (rows.Rows.Count == 0)
        {
            throw new DeliveryException($"{Capitalized(query.ToString())} answered no row, so the known values hold nothing to judge: check its WHERE, or disable the assertion until the staged data holds rows.");
        }

        var expanded = new List<KnownRow>(rows.Rows.Count);
        foreach (var values in rows.Rows)
        {
            ExpectedValue Of(ExpectedValue v) => v.Kind == ExpectedValueKind.Column ? values[columns[v.Text]] : v;
            var unlocated = pattern.Locate
                .Where(l => l.Value.Kind == ExpectedValueKind.Column && values[columns[l.Value.Text]].Kind == ExpectedValueKind.Null)
                .Select(l => $"{query} answered no value in {l.Value.Text}, which the row is located by ({l.Column.Path})")
                .FirstOrDefault();
            ValueCondition Condition(ValueCondition c) => c with { Operands = c.Operands.Select(Of).ToList() };
            expanded.Add(pattern with
            {
                Locate = pattern.Locate.Select(l => l with { Value = Of(l.Value) }).ToList(),
                Checks = pattern.Checks.Select(c => c with { Condition = Condition(c.Condition) }).ToList(),
                Curves = pattern.Curves?.Select(c => c with { Condition = Condition(c.Condition) }).ToList(),
                Unlocated = unlocated,
            });
        }

        var bound = known with { Rows = expanded, Answered = true };
        if (Refusal(bound) is { } refused)
        {
            throw new DeliveryException(refused);
        }

        return bound;
    }

    /// <summary>
    /// Why an assertion cannot compare with a value a query answered, or null when it can: a count is a whole number, zero or
    /// more; an ordering compares a number or text; a range runs between two values of one kind.
    /// </summary>
    private static string? Refusal(TestAssertion assertion)
    {
        string? Count(Comparison comparison, string what)
        {
            foreach (var value in comparison.Terms.SelectMany(t => t.Upper is null ? [t.Value] : new[] { t.Value, t.Upper }).Where(v => v.Origin is not null))
            {
                if (value.Number is not { } n || n < 0 || n != Math.Floor(n))
                {
                    return $"{Capitalized(value.Origin!)} answered {Shown(value)}, and {what} is compared with a whole number, zero or more.";
                }
            }

            return null;
        }

        string? Ordered(Comparison comparison)
        {
            foreach (var value in comparison.Terms.SelectMany(t => t.Upper is null ? [t.Value] : new[] { t.Value, t.Upper }).Where(v => v.Origin is not null))
            {
                if (value.Kind is not (ExpectedValueKind.Number or ExpectedValueKind.Text))
                {
                    return $"{Capitalized(value.Origin!)} answered {Shown(value)}, and an aggregate is compared with a number or a date.";
                }
            }

            return null;
        }

        string? Condition(ValueCondition condition)
        {
            if (condition.Length is { } length && Count(length, "a length") is { } refused)
            {
                return refused;
            }

            // Only a value a query answered as one value is checked here: a column of a query's rows differs row by row, and a
            // row whose value cannot be compared differs, saying why, as any row does.
            var answered = condition.Operands.Where(v => v.Origin is not null).ToList();
            if (answered.Count == 0)
            {
                return null;
            }

            if (condition.Operator is ValueOperator.AtLeast or ValueOperator.AtMost or ValueOperator.GreaterThan or ValueOperator.LessThan or ValueOperator.Between
                && answered.FirstOrDefault(v => v.Kind is not (ExpectedValueKind.Number or ExpectedValueKind.Text)) is { } unordered)
            {
                return $"{Capitalized(unordered.Origin!)} answered {Shown(unordered)}, and {AssertionText.Of(condition.Operator)} orders numbers, dates and text.";
            }

            if (condition.Operator == ValueOperator.Between && condition.Operands.Count == 2 && condition.Operands[0].Kind != condition.Operands[1].Kind)
            {
                return $"The range runs from {condition.Operands[0]} to {condition.Operands[1]}; both bounds are numbers, or both dates.";
            }

            return null;
        }

        return assertion switch
        {
            CountAssertion count => Count(count.Comparison, "a count"),
            RowCountAssertion rows => Count(rows.Comparison, "a row count"),
            GroupAssertion group => group.Groups.Select(g => Count(g.Count, "a group's count")).FirstOrDefault(r => r is not null)
                ?? (group.GroupCount is { } groups ? Count(groups, "the group count") : null),
            AggregateAssertion aggregate => aggregate.Function is AggregateFunction.Count or AggregateFunction.Distinct or AggregateFunction.Missing
                ? Count(aggregate.Comparison, "a count of values")
                : Ordered(aggregate.Comparison),
            ValueAssertion value => Condition(value.Condition) ?? value.Where.Select(w => Condition(w.Condition)).FirstOrDefault(r => r is not null),
            KnownValuesAssertion known => known.Rows
                .SelectMany(r => r.Checks.Select(c => c.Condition).Concat(r.CurveChecks.Select(c => c.Condition)))
                .Select(Condition)
                .FirstOrDefault(r => r is not null),
            _ => null,
        };
    }

    private static string Shown(ExpectedValue value) => value.Kind switch
    {
        ExpectedValueKind.Null => "NULL",
        ExpectedValueKind.Text => "\"" + (value.Text.Length > 80 ? value.Text[..80] + "..." : value.Text) + "\"",
        _ => value.Text,
    };

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
