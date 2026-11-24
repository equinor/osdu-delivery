using System.Collections;
using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

// Known values (osdu/docs/reference/flow/known-values.md): rows of proof values, read from the document or from a
// query of the staged tables (osdu/docs/reference/flow/staged-proof-values.md), each row located by full paths
// and checking proof values with the comparison and warning band each states.
internal static partial class AssertionMapper
{
    /// <summary>
    /// Known values (osdu/docs/reference/flow/assertion.md, Known values): rows of proof values, each a full path and the
    /// value (or condition) the record a row finds holds there, and the full paths and values the row finds its record by.
    /// What the assertion lists under <c>locateBy</c> and <c>check</c> every row takes, beside its own; with no rows, those
    /// are the one row. What a path is in the kind's schema (a property, a reference, a list of objects) is checked against the
    /// templates when a run reads them; here every path is read, every row is located by something, every value suits its
    /// comparison, and no two rows are located alike.
    /// </summary>
    private static KnownValuesAssertion MapKnownValues(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "knownValues", ["knownValues"], at, test);
        if (test.ByIds)
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} finds its records by the values its rows give, and the test names its records by id; give the test a query (or none) instead of ids.");
        }

        var known = y.KnownValues!;
        var block = at + ".knownValues";
        var staged = KnownQuery(known, block, test);
        if (staged is not null)
        {
            // Every value the known values compare with names a column of the rows the query answers.
            test = test with { Columns = true };
        }
        else if (known.MaxRows is not null)
        {
            throw new FlowValidationException($"{test.Source}: {block}.maxRows bounds the rows a query answers, and the known values read no query; take it out, or give them query or sql.");
        }

        var tolerance = ConditionReader.Tolerance(known.Tolerance, block, test.Source);
        var band = ConditionReader.Band(known.WarnWithin, block, test.Source);
        var ignoreCase = known.IgnoreCase ?? false;
        var sharedLocate = ReadLocate(known.LocateBy, ignoreCase, block + ".locateBy", test);
        var sharedChecks = ReadChecks(known.Check, tolerance, band, ignoreCase, block + ".check", test);
        var sharedCurves = ReadCurves(known.Curves, tolerance, band, block + ".curves", test);
        var declared = known.Rows is { Count: > 0 } listed ? listed : [new AssertionKnownRowYaml()];
        if (declared.Count > MaxRows)
        {
            throw new FlowValidationException($"{test.Source}: {block}.rows lists {declared.Count} rows; known values hold at most {MaxRows}. Split them over several assertions.");
        }

        var rows = new List<KnownRow>(declared.Count);
        var located = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < declared.Count; i++)
        {
            var rowAt = known.Rows is { Count: > 0 } ? string.Create(CultureInfo.InvariantCulture, $"{block}.rows[{i}]") : block;
            var row = declared[i] ?? throw new FlowValidationException($"{test.Source}: {rowAt} is empty.");
            var locate = Joined(sharedLocate, ReadLocate(row.LocateBy, ignoreCase, rowAt + ".locateBy", test), l => l.Column.Path, "is located by", rowAt, test);
            var checks = Joined(sharedChecks, ReadChecks(row.Check, tolerance, band, ignoreCase, rowAt + ".check", test), c => c.Column.Label, "checks", rowAt, test);
            if (locate.Count is 0 or > MaxLocatingPaths)
            {
                throw new FlowValidationException(locate.Count == 0
                    ? $"{test.Source}: {rowAt} is located by nothing; give it locateBy, the full paths and values its record holds, such as data.Name: COMPOSITE."
                    : $"{test.Source}: {rowAt} is located by {locate.Count} paths; a row is located by at most {MaxLocatingPaths}.");
            }

            if (checks.Count > MaxProofValues)
            {
                throw new FlowValidationException($"{test.Source}: {rowAt} checks {checks.Count} proof values; a row checks at most {MaxProofValues}. Split it over two rows of their own assertions.");
            }

            var clash = locate.Select(l => l.Column.Path).Intersect(checks.Where(c => c.Column.Aggregate is null).Select(c => c.Column.Path), StringComparer.Ordinal).FirstOrDefault();
            if (clash is not null)
            {
                throw new FlowValidationException($"{test.Source}: {rowAt} is located by {clash} and checks it as well; a value a row is found by is the record's already.");
            }

            var identity = string.Join("\u001f", locate.OrderBy(l => l.Column.Path, StringComparer.Ordinal)
                .Select(l => l.Column.Path + "=" + (l.Column.IgnoreCase ? l.Value.Text.ToUpperInvariant() : l.Value.Text)));
            if (located.TryGetValue(identity, out var earlier))
            {
                throw new FlowValidationException(string.Create(CultureInfo.InvariantCulture,
                    $"{test.Source}: {rowAt} is located as rows[{earlier}] is ({string.Join(" / ", locate.Select(l => l.Value.Text))}); a record is held to one row of proof values, so locate each once."));
            }

            var curves = Joined(sharedCurves, ReadCurves(row.Curves, tolerance, band, rowAt + ".curves", test), c => c.Label, "checks", rowAt, test);
            var measured = curves.Select(c => c.Curve).Distinct(StringComparer.Ordinal).Count();
            if (measured > CurveProofValues.MaxCurves)
            {
                throw new FlowValidationException(string.Create(CultureInfo.InvariantCulture,
                    $"{test.Source}: {rowAt} holds proof values of {measured} curves; a row holds them for at most {CurveProofValues.MaxCurves}. Split them over two rows of their own assertions."));
            }

            located[identity] = i;
            rows.Add(new KnownRow(locate, checks, DisabledOf(row.Disabled, rowAt, test.Source)) { Curves = curves.Count == 0 ? null : curves });
        }

        var anyCurves = rows.Any(r => r.CurveChecks.Count > 0);
        if (anyCurves && !test.Bulk)
        {
            throw new FlowValidationException(
                $"{test.Source}: {block} holds curve proof values, which are measured over each record's bulk data; give the test a bulk block (bulk: {{ maxRows: 1000000 }}).");
        }

        string? curveIndex = null;
        if (known.CurveIndex is not null)
        {
            curveIndex = CheckColumn(known.CurveIndex, block + ".curveIndex", test.Source);
            if (!rows.Any(r => r.CurveChecks.Any(c => CurveProofValues.ReadsIndex(c.Measure))))
            {
                throw new FlowValidationException(
                    $"{test.Source}: {block}.curveIndex names the curve a curve's first and last values are read at, and no row holds a first or last proof value; take it out, or give a curve first: or last:.");
            }
        }

        var notFound = known.NotFound is null ? (AssertionSeverity?)null : FlowMapper.ParseEnum<AssertionSeverity>(known.NotFound.Trim(), block + ".notFound", test.Source);
        int? maxRows = null;
        if (known.MaxRows is { } bound)
        {
            maxRows = bound is >= 1 and <= KnownValuesAssertion.MaxQueryRows
                ? bound
                : throw new FlowValidationException(string.Create(CultureInfo.InvariantCulture,
                    $"{test.Source}: {block}.maxRows is the most rows the query may answer: 1 to {KnownValuesAssertion.MaxQueryRows}."));
        }

        return new KnownValuesAssertion
        {
            Rows = rows,
            NotFound = notFound,
            CurveIndex = curveIndex,
            Query = staged,
            MaxRows = maxRows,
            Label = staged is null
                ? string.Create(CultureInfo.InvariantCulture, $"known values of {rows.Count} row(s)")
                : $"known values of each row of {staged}",
        };
    }

    /// <summary>
    /// The query known values read their rows from (osdu/docs/reference/flow/staged-proof-values.md, Known values from a query):
    /// one the flow declares, by its name (<c>query</c>), or one written here (<c>connection</c> and <c>sql</c>); null for rows
    /// the document writes. A query's rows are the rows, so the known values write no rows of their own.
    /// </summary>
    private static StagedQuery? KnownQuery(AssertionKnownValuesYaml known, string block, TestContext test)
    {
        var inline = known.Connection is not null || known.Sql is not null || known.TimeoutSeconds is not null;
        if (known.Query is null && !inline)
        {
            return null;
        }

        if (known.Query is not null && inline)
        {
            throw new FlowValidationException(
                $"{test.Source}: {block} names the query {known.Query} the flow declares, and writes connection, sql or timeoutSeconds as well; name a query, or write one here, not both.");
        }

        if (known.Rows is { Count: > 0 })
        {
            throw new FlowValidationException(
                $"{test.Source}: {block} reads its rows from a query, and lists rows as well; each row the query answers is a row, so give locateBy, check and curves the columns they read instead.");
        }

        if (known.Query is { } named)
        {
            var name = named.Trim();
            return test.Queries.TryGetValue(name, out var declared)
                ? declared
                : throw new FlowValidationException(test.Queries.Count == 0
                    ? $"{test.Source}: {block}.query names '{name}', and the flow declares no queries; declare it under queries, with its connection and sql."
                    : $"{test.Source}: {block}.query names '{name}', which the flow does not declare under queries; it declares {string.Join(", ", test.Queries.Keys.Order(StringComparer.Ordinal))}.");
        }

        var query = ConditionReader.Query(known.Connection, known.Sql, known.TimeoutSeconds, null, block, test.Source);
        Engine.Assertions.StagedSql.Check(query.Sql, test.Parameters, $"{test.Source}: {block}.sql");
        return query;
    }

    /// <summary>The most full paths a row of known values is located by.</summary>
    public const int MaxLocatingPaths = 8;

    /// <summary>The most proof values one row of known values checks.</summary>
    public const int MaxProofValues = 60;

    /// <summary>The comparisons a proof value holds the values a record holds to.</summary>
    private static readonly string[] CheckComparisons =
    [
        "equals", "notEquals", "in", "notIn", "atLeast", "atMost", "greaterThan", "lessThan", "between", "matches", "notMatches",
        "startsWith", "endsWith", "contains", "notContains", "exists", "empty", "type", "length",
    ];

    /// <summary>What the assertion lists for every row, followed by what the row lists itself; a path both list is refused.</summary>
    private static List<T> Joined<T>(IReadOnlyList<T> shared, IReadOnlyList<T> own, Func<T, string> path, string verb, string at, TestContext test)
    {
        var twice = own.Select(path).Intersect(shared.Select(path), StringComparer.Ordinal).FirstOrDefault();
        if (twice is not null)
        {
            throw new FlowValidationException($"{test.Source}: {at} {verb} {twice}, which the assertion lists for every row already; give it in one place.");
        }

        return [.. shared, .. own];
    }

    /// <summary>The full paths and values a row is located by: each value a scalar, or a mapping of equals with its ignoreCase and tolerance.</summary>
    private static IReadOnlyList<KnownLocate> ReadLocate(Dictionary<string, object?>? declared, bool ignoreCase, string at, TestContext test)
    {
        var locate = new List<KnownLocate>();
        foreach (var (written, raw) in declared ?? [])
        {
            var pathAt = $"{at}.{written}";
            var column = ReadKnownPath(written, pathAt, test) with { IgnoreCase = ignoreCase };
            ExpectedValue value;
            if (raw is IDictionary map)
            {
                object? equals = null;
                var given = false;
                foreach (System.Collections.DictionaryEntry entry in map)
                {
                    switch (entry.Key as string)
                    {
                        case "equals":
                            equals = entry.Value;
                            given = true;
                            break;
                        case "ignoreCase":
                            column = column with { IgnoreCase = Flag(entry.Value, pathAt + ".ignoreCase", test) };
                            break;
                        case "tolerance":
                            column = column with { Tolerance = ConditionReader.Tolerance(entry.Value, pathAt, test.Source) };
                            break;
                        default:
                            throw new FlowValidationException(
                                $"{test.Source}: {pathAt} has '{entry.Key}'; a row is located by values its record equals: a value, or a mapping of equals, ignoreCase and tolerance.");
                    }
                }

                if (!given)
                {
                    throw new FlowValidationException($"{test.Source}: {pathAt} gives no value; write it, or a mapping with equals.");
                }

                value = test.Columns ? ConditionReader.Column(equals, pathAt, test.Source) : ConditionReader.Scalar(equals, pathAt, test.Source);
            }
            else
            {
                value = test.Columns ? ConditionReader.Column(raw, pathAt, test.Source) : ConditionReader.Scalar(raw, pathAt, test.Source);
            }

            if (value.Kind == ExpectedValueKind.Query)
            {
                throw new FlowValidationException($"{test.Source}: {pathAt} locates the row by a value its record holds, written here or named as a column of the known values' query, not by the value of another query.");
            }

            if (value.Kind == ExpectedValueKind.Null)
            {
                throw new FlowValidationException($"{test.Source}: {pathAt} gives no value; a row is found by the values its record holds, so each is given.");
            }

            locate.Add(new KnownLocate(column, value));
        }

        return locate;
    }

    /// <summary>
    /// The proof values a row holds its record to: each a full path and a value it equals, ~ for no value at all, or a
    /// condition written as a mapping (one comparison, with tolerance, warnWithin, ignoreCase, values, an aggregate and its
    /// except, and disabled), as a field assertion's condition is written. The assertion's warning band serves every proof
    /// value it can widen that gives none itself.
    /// </summary>
    private static IReadOnlyList<KnownCheck> ReadChecks(
        Dictionary<string, object?>? declared, double? tolerance, WarningBand? band, bool ignoreCase, string at, TestContext test)
    {
        var checks = new List<KnownCheck>();
        foreach (var (written, raw) in declared ?? [])
        {
            var pathAt = $"{at}.{written}";
            var column = ReadKnownPath(written, pathAt, test);
            if (raw is not IDictionary map || ConditionReader.IsQuery(map))
            {
                var condition = raw is null
                    ? new ValueCondition { Operator = ValueOperator.Exists, Flag = false }
                    : Proof([("equals", raw)], null, null, tolerance, ignoreCase, pathAt, test);
                checks.Add(new KnownCheck(column with { WarnWithin = Shared(band, condition) }, condition));
                continue;
            }

            var operators = new List<(string Key, object Operand)>();
            object? columnTolerance = null;
            object? columnBand = null;
            bool? columnIgnoreCase = null;
            string? values = null;
            string? aggregate = null;
            List<object>? except = null;
            object? disabled = null;
            foreach (System.Collections.DictionaryEntry entry in map)
            {
                var key = entry.Key as string ?? string.Empty;
                switch (key)
                {
                    case "tolerance":
                        columnTolerance = entry.Value;
                        break;
                    case "warnWithin":
                        columnBand = entry.Value ?? throw new FlowValidationException($"{test.Source}: {pathAt}.warnWithin has no value.");
                        break;
                    case "ignoreCase":
                        columnIgnoreCase = Flag(entry.Value, pathAt + ".ignoreCase", test);
                        break;
                    case "values":
                        values = entry.Value as string ?? throw new FlowValidationException($"{test.Source}: {pathAt}.values is all or any.");
                        break;
                    case "aggregate":
                        aggregate = entry.Value as string ?? throw new FlowValidationException($"{test.Source}: {pathAt}.aggregate names an aggregate, such as count.");
                        break;
                    case "except":
                        except = (entry.Value as IList)?.Cast<object>().ToList()
                            ?? throw new FlowValidationException($"{test.Source}: {pathAt}.except lists the values the aggregate leaves out.");
                        break;
                    case "disabled":
                        disabled = entry.Value ?? string.Empty;
                        break;
                    default:
                        if (!CheckComparisons.Contains(key, StringComparer.Ordinal))
                        {
                            throw new FlowValidationException(
                                $"{test.Source}: {pathAt} has '{entry.Key}'; a proof value is a value, or a condition: one of {string.Join(", ", CheckComparisons)}, with tolerance, warnWithin, ignoreCase, values, aggregate, except and disabled.");
                        }

                        if (entry.Value is not null)
                        {
                            operators.Add((key, entry.Value));
                        }

                        break;
                }
            }

            var function = aggregate is null ? (AggregateFunction?)null : FlowMapper.ParseEnum<AggregateFunction>(aggregate.Trim(), pathAt + ".aggregate", test.Source);
            if (function == AggregateFunction.Missing)
            {
                throw new FlowValidationException(
                    $"{test.Source}: {pathAt}.aggregate missing counts records without a value, and a proof value reads the values of one record; hold its count to 0 instead.");
            }

            if (function is not null && operators.Any(o => !ConditionReader.ComparisonKeys.Contains(o.Key, StringComparer.Ordinal)))
            {
                throw new FlowValidationException(
                    $"{test.Source}: {pathAt} compares {AssertionText.Of(function.Value)}({column.Path}), a number, so it takes one of {string.Join(", ", ConditionReader.ComparisonKeys)}.");
            }

            var condition2 = Proof(operators, columnIgnoreCase, columnTolerance, tolerance, ignoreCase, pathAt, test);
            if (function is { } taken)
            {
                var dated = taken is AggregateFunction.Min or AggregateFunction.Max;
                if (condition2.Operands.Any(o => !o.Pending && o.Kind != ExpectedValueKind.Number && !(dated && o.Kind == ExpectedValueKind.Text && Engine.Assertions.RecordValues.TryInstant(o.Text, out _))))
                {
                    throw new FlowValidationException(
                        $"{test.Source}: {pathAt} compares {AssertionText.Of(taken)}({column.Path}), which is a number{(dated ? " or a date" : string.Empty)}; give a number{(dated ? " or an ISO 8601 date" : string.Empty)}.");
                }
            }

            if (function is not null && values is not null)
            {
                throw new FlowValidationException(
                    $"{test.Source}: {pathAt} aggregates its values and says which of them are held to the condition; an aggregate is taken of them all. Write two proof values.");
            }

            if (except is not null && function is null)
            {
                throw new FlowValidationException(
                    $"{test.Source}: {pathAt}.except leaves values out of an aggregate, and the proof value aggregates nothing; give it an aggregate, such as count.");
            }

            var anyValue = (values?.Trim().ToLowerInvariant()) switch
            {
                null or "all" => false,
                "any" => true,
                _ => throw new FlowValidationException($"{test.Source}: {pathAt}.values '{values}' is all or any."),
            };
            var own = ConditionReader.Band(columnBand, pathAt, test.Source);
            if (own is not null)
            {
                ConditionReader.CheckBand(own, condition2.Tolerance, [AssertionText.Of(condition2.Operator)], condition2.Operands, pathAt, test.Source);
            }

            checks.Add(new KnownCheck(
                column with
                {
                    AnyValue = anyValue,
                    Aggregate = function,
                    Except = except is null ? [] : MapExcept(except, pathAt, test),
                    WarnWithin = own ?? Shared(band, condition2),
                },
                condition2,
                DisabledOf(disabled, pathAt, test.Source)));
        }

        return checks;
    }

    /// <summary>
    /// The condition a proof value states: its comparison with the settings it writes itself, and the assertion's tolerance and
    /// ignoreCase where it writes none and they fit (a tolerance where a number is compared, ignoreCase where text is), so the
    /// assertion's settings serve its numbers and its text alike.
    /// </summary>
    private static ValueCondition Proof(
        IReadOnlyList<(string Key, object Operand)> operators, bool? ignoreCase, object? tolerance, double? sharedTolerance, bool sharedIgnoreCase, string at, TestContext test)
    {
        var condition = ConditionReader.Read(operators, ignoreCase, tolerance, ResolvesRefused, at, test.Source, test.Columns);
        var takesTolerance = tolerance is null && sharedTolerance is not null
            && condition.Operator is ValueOperator.EqualTo or ValueOperator.NotEqualTo or ValueOperator.In or ValueOperator.NotIn or ValueOperator.Between
                or ValueOperator.AtLeast or ValueOperator.AtMost or ValueOperator.GreaterThan or ValueOperator.LessThan
            && condition.Operands.Any(o => o.Kind == ExpectedValueKind.Number || o.Pending);
        var takesIgnoreCase = ignoreCase is null && sharedIgnoreCase
            && condition.Operator is ValueOperator.EqualTo or ValueOperator.NotEqualTo or ValueOperator.In or ValueOperator.NotIn or ValueOperator.Matches
                or ValueOperator.NotMatches or ValueOperator.StartsWith or ValueOperator.EndsWith or ValueOperator.Contains or ValueOperator.NotContains
            && condition.Operands.Any(o => o.Kind == ExpectedValueKind.Text || o.Pending);
        return takesTolerance || takesIgnoreCase
            ? ConditionReader.Read(operators, takesIgnoreCase ? true : ignoreCase, takesTolerance ? sharedTolerance : tolerance, ResolvesRefused, at, test.Source, test.Columns)
            : condition;
    }

    /// <summary>The assertion's warning band for a proof value giving none: where it can widen the proof value's comparison, else none.</summary>
    private static WarningBand? Shared(WarningBand? band, ValueCondition condition)
        => band is not null && ConditionReader.Widens(band, condition) ? band : null;

    /// <summary>Why <c>resolves</c> is not a proof value's comparison.</summary>
    private const string ResolvesRefused = "does not apply to a proof value; read the record a reference names with a list of paths instead, such as '[data.WellboreID, data.FacilityName]'.";

    private static bool Flag(object? declared, string at, TestContext test) => declared switch
    {
        bool flag => flag,
        string text when bool.TryParse(text, out var parsed) => parsed,
        _ => throw new FlowValidationException($"{test.Source}: {at} is true or false."),
    };
}
