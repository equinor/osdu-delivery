using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// The one reader of a condition on a value (osdu/docs/reference/flow/assertion.md, Conditions): an assertion flow's field
/// and column conditions with their <c>where</c> conditions, and a mapping's assertions and theirs
/// (osdu/docs/reference/flow/mapping-assertions.md), all read their conditions here. A condition is therefore written with
/// the same words, takes the same operands and is refused with the same messages wherever it is written, and a rule a
/// mapping states means what the same rule means in a test of what OSDU holds.
/// </summary>
/// <remarks>
/// An assertion flow hands the operands its typed model read; a mapping hands them as its record tree holds them (text, a
/// number, true or false, a list, a map). Every operand is checked here for the shape its operator takes, so both read the
/// same way whatever the document's model already guaranteed.
/// </remarks>
internal static partial class ConditionReader
{
    /// <summary>The most values an <c>in</c> or <c>notIn</c> lists.</summary>
    public const int MaxListed = 1000;

    /// <summary>How long a <c>matches</c> expression may take on one value before it is refused as a failure to evaluate.</summary>
    public static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    /// <summary>The properties an OSDU record has at its root, the only places a field path may start.</summary>
    public static readonly IReadOnlySet<string> RecordRoots = new HashSet<string>(StringComparer.Ordinal)
    {
        "id", "kind", "version", "acl", "legal", "data", "tags", "ancestry", "meta", "createTime", "createUser", "modifyTime", "modifyUser",
    };

    /// <summary>The keys a comparison of a number takes.</summary>
    public static readonly IReadOnlyList<string> ComparisonKeys = ["equals", "notEquals", "atLeast", "atMost", "greaterThan", "lessThan", "between"];

    /// <summary>Every operator of a condition, in the order messages list them.</summary>
    public static readonly IReadOnlyList<string> Operators =
    [
        "equals", "notEquals", "in", "notIn", "atLeast", "atMost", "greaterThan", "lessThan", "between", "matches", "notMatches",
        "startsWith", "endsWith", "contains", "notContains", "exists", "empty", "type", "length", "resolves",
    ];

    /// <summary>The settings a condition takes beside its operator.</summary>
    public static readonly IReadOnlyList<string> Settings = ["ignoreCase", "tolerance"];

    /// <summary>
    /// The condition its one operator states, with what it compares with.
    /// </summary>
    /// <param name="operators">Each operator the document writes, with its operand as the document holds it, in document order.</param>
    /// <param name="ignoreCase">The condition's <c>ignoreCase</c>, or null when it is not written.</param>
    /// <param name="tolerance">The condition's <c>tolerance</c> as the document holds it, or null when it is not written.</param>
    /// <param name="resolvesRefused">
    /// Why <c>resolves</c> does not apply here (said after the place it is written), or null where it does.
    /// </param>
    /// <param name="at">Where the condition is written, for messages.</param>
    /// <param name="source">The file, for messages.</param>
    /// <param name="columns">
    /// True for known values read from a query, whose values compared with name the query's columns
    /// (osdu/docs/reference/flow/staged-proof-values.md, Known values from a query).
    /// </param>
    public static ValueCondition Read(
        IReadOnlyList<(string Key, object Operand)> operators, bool? ignoreCase, object? tolerance, string? resolvesRefused, string at, string source,
        bool columns = false)
    {
        ArgumentNullException.ThrowIfNull(operators);
        ExpectedValue Value(object? raw, string where) => columns ? Column(raw, where, source) : Operand(raw, where, source);
        if (operators.Count != 1)
        {
            var offered = resolvesRefused is null ? Operators : Operators.Where(o => o != "resolves");
            throw new FlowValidationException(operators.Count == 0
                ? $"{source}: {at} names no condition: one of {string.Join(", ", offered)}."
                : $"{source}: {at} names {string.Join(" and ", operators.Select(o => o.Key))}; a condition has one operator. Write between for a range, or one assertion for each.");
        }

        var (key, operand) = operators[0];
        var op = key switch
        {
            "equals" => ValueOperator.EqualTo,
            "notEquals" => ValueOperator.NotEqualTo,
            _ => FlowMapper.ParseEnum<ValueOperator>(key, at, source),
        };
        var where = $"{at}.{key}";
        var condition = new ValueCondition { Operator = op, IgnoreCase = ignoreCase ?? false, Tolerance = Tolerance(tolerance, at, source) };
        switch (op)
        {
            case ValueOperator.EqualTo or ValueOperator.NotEqualTo or ValueOperator.Contains or ValueOperator.NotContains:
                condition = condition with { Operands = [Value(operand, where)] };
                break;
            case ValueOperator.In or ValueOperator.NotIn:
                var listed = operand as IList ?? throw new FlowValidationException($"{source}: {where} lists the values allowed.");
                if (listed.Count is 0 or > MaxListed)
                {
                    throw new FlowValidationException($"{source}: {where} lists between 1 and {MaxListed} values.");
                }

                condition = condition with { Operands = listed.Cast<object?>().Select(v => columns ? Column(v, where, source) : Scalar(v, where, source)).ToList() };
                break;
            case ValueOperator.AtLeast or ValueOperator.AtMost or ValueOperator.GreaterThan or ValueOperator.LessThan:
                condition = condition with { Operands = [Ordered(Value(operand, where), where, source)] };
                break;
            case ValueOperator.Between:
                var bounds = operand as IList;
                if (bounds is not { Count: 2 })
                {
                    throw new FlowValidationException($"{source}: {where} lists two values, the lower bound and the upper, such as [0, 100].");
                }

                var low = Ordered(Value(bounds[0], where), where, source);
                var high = Ordered(Value(bounds[1], where), where, source);
                CheckBounds(low, high, where, source);
                condition = condition with { Operands = [low, high] };
                break;
            case ValueOperator.Matches or ValueOperator.NotMatches:
                var expression = TextOperand(operand)
                    ?? throw new FlowValidationException($"{source}: {where} is a regular expression, written as text, such as {key}: '^[A-Z]+$'.");
                try
                {
                    condition = condition with
                    {
                        Operands = [ExpectedValue.OfText(expression)],
                        Pattern = new Regex(
                            expression,
                            RegexOptions.CultureInvariant | (ignoreCase == true ? RegexOptions.IgnoreCase : RegexOptions.None),
                            PatternTimeout),
                    };
                }
                catch (ArgumentException ex)
                {
                    throw new FlowValidationException($"{source}: {where} '{Shown(expression)}' is not a regular expression: {ex.Message}", ex);
                }

                break;
            case ValueOperator.StartsWith or ValueOperator.EndsWith:
                var text = TextOperand(operand)
                    ?? throw new FlowValidationException($"{source}: {where} is the text a value {(op == ValueOperator.StartsWith ? "starts" : "ends")} with.");
                if (text.Length == 0)
                {
                    throw new FlowValidationException($"{source}: {where} is empty; every value starts and ends with nothing.");
                }

                condition = condition with { Operands = [ExpectedValue.OfText(text)] };
                break;
            case ValueOperator.Exists or ValueOperator.Empty:
                condition = condition with
                {
                    Flag = operand as bool? ?? throw new FlowValidationException($"{source}: {where} is true or false."),
                };
                break;
            case ValueOperator.Type:
                var type = TextOperand(operand) ?? throw new FlowValidationException(
                    $"{source}: {where} names a JSON type: one of string, number, integer, boolean, object, array, null.");
                condition = condition with { JsonType = JsonTypeOf(type.Trim(), where, source) };
                break;
            case ValueOperator.Length:
                condition = condition with { Length = CountComparison(operand, where, source) };
                break;
            default:
                if (resolvesRefused is not null)
                {
                    throw new FlowValidationException($"{source}: {where} {resolvesRefused}");
                }

                condition = operand switch
                {
                    true => condition,
                    string entityType when EntityTypePattern().IsMatch(entityType.Trim()) => condition with { EntityType = entityType.Trim() },
                    _ => throw new FlowValidationException(
                        $"{source}: {where} is true (every value names a record that exists) or the entity type the references point at, such as master-data--Well."),
                };
                break;
        }

        CheckSettings(condition, at, source);
        return condition;
    }

    /// <summary>
    /// The operators a map of a document's own tree writes, each with its operand, in the order they are written: the keys
    /// of <paramref name="map"/> that are operators. An operator written with nothing after it (<c>equals:</c>,
    /// <c>equals: ~</c>) states no condition, as an assertion flow's typed model reads it. The caller refuses the keys it
    /// does not take.
    /// </summary>
    public static List<(string Key, object Operand)> OperatorsOf(IDictionary<object, object> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var operators = new List<(string, object)>();
        foreach (var (key, value) in map)
        {
            var name = Convert.ToString(key, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            if (value is not null && Operators.Contains(name, StringComparer.Ordinal))
            {
                operators.Add((name, value));
            }
        }

        return operators;
    }

    /// <summary>
    /// A comparison of a count (of records, rows, groups, or a length): a whole number, zero or more, meaning equals, or a
    /// mapping of the comparison keys, every one of which has to hold.
    /// </summary>
    public static Comparison CountComparison(object declared, string at, string source)
    {
        // A count written as a query's value ({ query: staged-wellbores }) equals it, as a count written as a number does.
        if (declared is IDictionary map && !IsQuery(map))
        {
            var terms = new List<ComparisonTerm>();
            foreach (System.Collections.DictionaryEntry entry in map)
            {
                var key = entry.Key as string ?? string.Empty;
                if (!ComparisonKeys.Contains(key, StringComparer.Ordinal))
                {
                    throw new FlowValidationException($"{source}: {at} has '{key}', which is not one of {string.Join(", ", ComparisonKeys)}.");
                }

                if (entry.Value is null)
                {
                    throw new FlowValidationException($"{source}: {at}.{key} has no value.");
                }

                terms.Add(Term(key, entry.Value, $"{at}.{key}", source, numbersOnly: true, wholeNumbers: true));
            }

            if (terms.Count == 0)
            {
                throw new FlowValidationException($"{source}: {at} is empty; give it one of {string.Join(", ", ComparisonKeys)}.");
            }

            return new Comparison(terms);
        }

        return new Comparison([Term("equals", declared, at, source, numbersOnly: true, wholeNumbers: true)]);
    }

    /// <summary>One term of a comparison: its operator and the value (or, for between, the two values) it compares with.</summary>
    public static ComparisonTerm Term(string key, object operand, string at, string source, bool numbersOnly, bool wholeNumbers = false)
    {
        ExpectedValue Value(object? raw)
        {
            var value = Operand(raw, at, source);
            if (value.Pending)
            {
                // A query's value is checked to be one the comparison takes once a run has it.
                return value;
            }

            if (numbersOnly && value.Kind != ExpectedValueKind.Number)
            {
                throw new FlowValidationException($"{source}: {at} compares a number, and '{value.Text}' is not one.");
            }

            if (value.Kind is ExpectedValueKind.Boolean or ExpectedValueKind.Null)
            {
                throw new FlowValidationException($"{source}: {at} compares a number or a date, and '{value.Text}' is neither.");
            }

            if (wholeNumbers && value.Number is { } n && (n < 0 || n != Math.Floor(n)))
            {
                throw new FlowValidationException($"{source}: {at} counts, so it is a whole number, zero or more; '{value.Text}' is not.");
            }

            return value;
        }

        var op = key switch
        {
            "equals" => ComparisonOperator.EqualTo,
            "notEquals" => ComparisonOperator.NotEqualTo,
            _ => FlowMapper.ParseEnum<ComparisonOperator>(key, at, source),
        };
        if (op != ComparisonOperator.Between)
        {
            return new ComparisonTerm(op, Value(operand));
        }

        if (operand is not IList { Count: 2 } bounds)
        {
            throw new FlowValidationException($"{source}: {at} lists two values, the lower bound and the upper, such as [1, 10].");
        }

        var low = Value(bounds[0]);
        var high = Value(bounds[1]);
        CheckBounds(low, high, at, source);
        return new ComparisonTerm(op, low, high);
    }

    /// <summary>
    /// A condition's <c>tolerance</c>: a number, zero or more, however the document holds it; null when it is not written.
    /// </summary>
    public static double? Tolerance(object? declared, string at, string source)
    {
        var tolerance = declared is null ? (double?)null : NumberOf(declared);
        return tolerance is { } value && (value < 0 || !double.IsFinite(value))
            ? throw new FlowValidationException($"{source}: {at}.tolerance must be a number, zero or more.")
            : tolerance;
    }

    /// <summary>A number as YAML types it, as the text it writes back as; NaN for anything that is not a number.</summary>
    private static double NumberOf(object declared) => declared switch
    {
        double d => d,
        // A single as the text it writes back as, which is what the document wrote: 0.0005 stays 0.0005, as Scalar reads it.
        float f => double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
        decimal m => (double)m,
        byte or sbyte or short or ushort or int or uint or long or ulong => Convert.ToDouble(declared, CultureInfo.InvariantCulture),
        _ => double.NaN,
    };

    /// <summary>The comparisons a warning band widens: each names a number a value may miss by the band.</summary>
    private static readonly string[] Widened = ["equals", "in", "atLeast", "atMost", "greaterThan", "lessThan", "between"];

    /// <summary>
    /// A warning band as a document writes it (<c>warnWithin</c>, osdu/docs/reference/flow/assertion.md, A warning band): a
    /// distance greater than zero (<c>5</c>), or a percentage of the number compared with (<c>10%</c>), more than 0% and at
    /// most 100%; null when it gives none.
    /// </summary>
    public static WarningBand? Band(object? declared, string at, string source)
    {
        if (declared is null)
        {
            return null;
        }

        const string Shape = "a distance greater than zero, such as 5, or a percentage of the value compared with, such as 10%";
        if (declared is string text)
        {
            var trimmed = text.Trim();
            var percent = trimmed.EndsWith('%');
            var written = percent ? trimmed[..^1].TrimEnd() : trimmed;
            if (double.TryParse(written, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) && double.IsFinite(amount) && amount > 0)
            {
                return !percent || amount <= 100
                    ? new WarningBand(amount, percent)
                    : throw new FlowValidationException($"{source}: {at}.warnWithin '{Shown(trimmed)}' is more than the value itself; a percentage is at most 100%.");
            }

            throw new FlowValidationException($"{source}: {at}.warnWithin '{Shown(trimmed)}' is not a warning band: {Shape}.");
        }

        var distance = NumberOf(declared);
        return double.IsFinite(distance) && distance > 0
            ? new WarningBand(distance, Percent: false)
            : throw new FlowValidationException($"{source}: {at}.warnWithin is not a warning band: {Shape}.");
    }

    /// <summary>Refuses a band beside a comparison of a count or an aggregate it cannot warn of; see <see cref="CheckBand(WarningBand, double?, IReadOnlyList{string}, IReadOnlyList{ExpectedValue}, string, string)"/>.</summary>
    public static void CheckBand(WarningBand band, double? tolerance, Comparison comparison, string at, string source)
    {
        ArgumentNullException.ThrowIfNull(band);
        ArgumentNullException.ThrowIfNull(comparison);
        CheckBand(
            band,
            tolerance,
            comparison.Terms.Select(t => AssertionText.Of(t.Operator)).ToList(),
            comparison.Terms.SelectMany(t => t.Upper is null ? [t.Value] : new[] { t.Value, t.Upper }).ToList(),
            at,
            source);
    }

    /// <summary>
    /// Refuses a warning band that cannot warn of anything: one beside a comparison that is not of numbers, one beside a
    /// comparison saying what a value must not be (notEquals, notIn), which has nothing to widen, and one no wider than the
    /// tolerance the comparison already allows.
    /// </summary>
    public static void CheckBand(WarningBand band, double? tolerance, IReadOnlyList<string> operators, IReadOnlyList<ExpectedValue> operands, string at, string source)
    {
        ArgumentNullException.ThrowIfNull(band);
        ArgumentNullException.ThrowIfNull(operators);
        ArgumentNullException.ThrowIfNull(operands);
        if (operators.FirstOrDefault(o => !Widened.Contains(o, StringComparer.Ordinal)) is { } other)
        {
            throw new FlowValidationException(other is "notEquals" or "notIn"
                ? $"{source}: {at}.warnWithin widens the numbers a value is compared with, and {other} says what the value must not be, which has nothing to widen."
                : $"{source}: {at}.warnWithin widens {string.Join(", ", Widened)} of a number, and the value is compared by {other}.");
        }

        var numbers = new List<double>(operands.Count);
        foreach (var operand in operands.Where(o => !o.Pending))
        {
            if (NumberIn(operand) is not { } number)
            {
                throw new FlowValidationException($"{source}: {at}.warnWithin is a distance between numbers, and {operand} is not a number.");
            }

            numbers.Add(number);
        }

        if (numbers.Count > 0 && numbers.All(n => band.Around(n) <= (tolerance ?? 0)))
        {
            throw new FlowValidationException(band.Percent && numbers.All(n => n == 0)
                ? $"{source}: {at}.warnWithin {band} of 0 is nothing, so nothing would warn; give a distance, such as 1."
                : $"{source}: {at}.warnWithin {band} is no wider than the tolerance {(tolerance ?? 0).ToString("R", CultureInfo.InvariantCulture)} the comparison already allows, so nothing would warn; widen it.");
        }
    }

    /// <summary>Whether a band can widen <paramref name="condition"/>: one of the comparisons it widens, of numbers, and wider than its tolerance.</summary>
    public static bool Widens(WarningBand band, ValueCondition condition)
    {
        ArgumentNullException.ThrowIfNull(band);
        ArgumentNullException.ThrowIfNull(condition);
        if (!Widened.Contains(AssertionText.Of(condition.Operator), StringComparer.Ordinal) || condition.Operands.Count == 0)
        {
            return false;
        }

        // A value a run answers (a query's, or a column of its rows) may be any number, so the band may widen it.
        if (condition.Operands.Any(o => o.Pending))
        {
            return true;
        }

        var numbers = condition.Operands.Select(NumberIn).ToList();
        return numbers.All(n => n is not null) && numbers.Any(n => band.Around(n!.Value) > (condition.Tolerance ?? 0));
    }

    /// <summary>The number a value compared with names: a number, or text holding one, as a comparison reads it.</summary>
    private static double? NumberIn(ExpectedValue value) => value.Kind switch
    {
        ExpectedValueKind.Number => value.Number,
        ExpectedValueKind.Text when double.TryParse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) => n,
        _ => null,
    };

    /// <summary>A scalar as the document writes it: text, a number or a boolean; a mapping or a list is refused.</summary>
    /// <remarks>
    /// The YAML reader types an unquoted number as the smallest type that holds it (a byte, a single, a long), so a number
    /// is kept as the text that type writes it back as, which is what the document wrote: 0.1 stays 0.1, not the single
    /// precision value nearest it, and an integer beyond what a double holds keeps every digit.
    /// </remarks>
    public static ExpectedValue Scalar(object? raw, string at, string source) => raw switch
    {
        null => ExpectedValue.Null,
        string text => ExpectedValue.OfText(text),
        bool flag => ExpectedValue.OfBoolean(flag),
        int or long or short or byte or sbyte or uint or ulong or ushort
            => Number(Convert.ToString(raw, CultureInfo.InvariantCulture)!, at, source),
        double d when double.IsFinite(d) => Number(d.ToString("R", CultureInfo.InvariantCulture), at, source),
        float f when float.IsFinite(f) => Number(f.ToString("R", CultureInfo.InvariantCulture), at, source),
        decimal m => Number(m.ToString(CultureInfo.InvariantCulture), at, source),
        IDictionary or IList => throw new FlowValidationException($"{source}: {at} takes a single value (text, a number or true/false), not a mapping or a list."),
        _ => throw new FlowValidationException($"{source}: {at} holds a value that is not text, a number or true/false."),
    };

    /// <summary>The keys a query written where a value is compared with takes.</summary>
    public static readonly IReadOnlyList<string> QueryKeys = ["query", "connection", "sql", "timeoutSeconds"];

    /// <summary>Whether a mapping is a query's value (<c>{ query: name }</c>, or <c>{ connection: ..., sql: ... }</c>) rather than a condition.</summary>
    public static bool IsQuery(IDictionary map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var keys = map.Keys.Cast<object?>().Select(k => Convert.ToString(k, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty).ToList();
        return keys.Count > 0 && (keys.Contains("query") || keys.Contains("sql")) && keys.All(k => QueryKeys.Contains(k, StringComparer.Ordinal));
    }

    /// <summary>
    /// A value a comparison or a condition compares with (osdu/docs/reference/flow/staged-proof-values.md): a scalar, as
    /// <see cref="Scalar"/> reads it, or the value a query answers, named (<c>{ query: staged-wellbores }</c>, a query the flow
    /// declares under <c>queries</c>) or written in place (<c>{ connection: ${env:OSDU_DATA_DB}, sql: SELECT COUNT(*) FROM ... }</c>).
    /// A named query is read into the value once the document's queries are read; a run answers it before anything is judged.
    /// </summary>
    public static ExpectedValue Operand(object? raw, string at, string source)
    {
        if (raw is not IDictionary map)
        {
            return Scalar(raw, at, source);
        }

        var keys = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in map)
        {
            keys[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty] = entry.Value;
        }

        var stray = keys.Keys.Where(k => !QueryKeys.Contains(k, StringComparer.Ordinal)).ToList();
        if (stray.Count > 0 || keys.Count == 0)
        {
            throw new FlowValidationException(
                $"{source}: {at} takes a single value, or the value of a query: {{ query: <name> }} names one the flow declares under queries, and {{ connection: <reference>, sql: <SELECT> }} writes one here"
                + (stray.Count > 0 ? $"; {string.Join(", ", stray)} is neither." : "."));
        }

        if (keys.TryGetValue("query", out var named))
        {
            if (keys.Count > 1)
            {
                throw new FlowValidationException(
                    $"{source}: {at} names the query {Convert.ToString(named, CultureInfo.InvariantCulture)} the flow declares, which says where it runs and what it asks; connection, sql and timeoutSeconds go with it under queries.");
            }

            var name = named as string;
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new FlowValidationException($"{source}: {at}.query names a query the flow declares under queries.");
            }

            // Its connection and statement are the flow's: the document reads them into it once it has read its queries.
            return ExpectedValue.OfQuery(new StagedQuery { Name = name.Trim(), Connection = string.Empty, Sql = string.Empty });
        }

        return ExpectedValue.OfQuery(Query(keys.GetValueOrDefault("connection"), keys.GetValueOrDefault("sql"), keys.GetValueOrDefault("timeoutSeconds"), null, at, source));
    }

    /// <summary>
    /// A query as a document writes it, under the flow's queries or in place: the database it runs against (a reference, never a
    /// literal secret), its statement, and how long it may run. The statement is checked to be one read-only SELECT, and its
    /// tokens to be the flow's parameters, by the document once it knows them.
    /// </summary>
    public static StagedQuery Query(object? connection, object? sql, object? timeout, string? name, string at, string source)
    {
        if (connection is not string declared || string.IsNullOrWhiteSpace(declared))
        {
            throw new FlowValidationException(
                $"{source}: {at}.connection names the database the query runs against, as a reference such as ${{env:OSDU_DATA_DB}} or ${{keyvault:vault/secret}}.");
        }

        Source.IngestionConnection.CheckDeclared(declared.Trim(), source, at + ".connection");
        if (sql is not string statement || string.IsNullOrWhiteSpace(statement))
        {
            throw new FlowValidationException($"{source}: {at}.sql is the query's one SELECT, such as SELECT COUNT(*) FROM silver.Wellbore.");
        }

        var seconds = StagedQuery.DefaultTimeoutSeconds;
        if (timeout is not null)
        {
            var parsed = timeout switch
            {
                int or long or short or byte => Convert.ToInt64(timeout, CultureInfo.InvariantCulture),
                string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
                _ => -1L,
            };
            if (parsed is < 1 or > StagedQuery.MaxTimeoutSeconds)
            {
                throw new FlowValidationException(string.Create(CultureInfo.InvariantCulture,
                    $"{source}: {at}.timeoutSeconds is how long the query may run: a whole number of seconds, 1 to {StagedQuery.MaxTimeoutSeconds}."));
            }

            seconds = (int)parsed;
        }

        return new StagedQuery { Name = name, Connection = declared.Trim(), Sql = statement.Trim(), TimeoutSeconds = seconds };
    }

    /// <summary>
    /// A value of known values read from a query (osdu/docs/reference/flow/staged-proof-values.md, Known values from a query): the
    /// name of a column of the rows it answers, which each row's value takes the place of, or the value of another query.
    /// </summary>
    public static ExpectedValue Column(object? raw, string at, string source) => raw switch
    {
        IDictionary => Operand(raw, at, source),
        string name when !string.IsNullOrWhiteSpace(name) => ExpectedValue.OfColumn(name.Trim()),
        _ => throw new FlowValidationException(
            $"{source}: {at} names a column of the rows the known values' query answers, and {Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "nothing"} is not a column name; write a value that is the same in every row into the query (SELECT 'welldb' AS source_system)."),
    };

    /// <summary>A path into an OSDU record: dotted names from a root property, arrays crossed implicitly or with [*] and [n].</summary>
    public static string RecordPath(string? declared, string at, string source)
    {
        var path = declared?.Trim() ?? string.Empty;
        if (!PathPattern().IsMatch(path))
        {
            throw new FlowValidationException(
                $"{source}: {at} '{Shown(path)}' is not a path into a record: names separated by dots, such as data.FacilityName or data.VerticalMeasurements[*].VerticalMeasurement.");
        }

        var root = path.Split('.', '[')[0];
        if (!RecordRoots.Contains(root))
        {
            throw new FlowValidationException(
                $"{source}: {at} '{Shown(path)}' starts at '{root}', which is not a property of an OSDU record; a path starts at one of {string.Join(", ", RecordRoots)}.");
        }

        return path;
    }

    /// <summary>
    /// The operand of <paramref name="condition"/> as a document writes it after its operator, one line that
    /// <see cref="Read"/> reads back as the same condition: text quoted (so <c>'5'</c> stays text), numbers and true or
    /// false as they are, a list of values and a range in brackets, a comparison of a count as a map.
    /// </summary>
    public static string Written(ValueCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return condition.Operator switch
        {
            ValueOperator.In or ValueOperator.NotIn or ValueOperator.Between => "[" + string.Join(", ", condition.Operands.Select(Written)) + "]",
            ValueOperator.Matches or ValueOperator.NotMatches or ValueOperator.StartsWith or ValueOperator.EndsWith => QuotedText(condition.Operands[0].Text),
            ValueOperator.Exists or ValueOperator.Empty => condition.Flag ? "true" : "false",
            ValueOperator.Type => AssertionText.Of(condition.JsonType ?? JsonValueType.Text),
            ValueOperator.Length => Written(condition.Length!),
            ValueOperator.Resolves => condition.EntityType ?? "true",
            _ => Written(condition.Operands[0]),
        };
    }

    /// <summary>A value a condition compares with, as a document writes it.</summary>
    public static string Written(ExpectedValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Kind switch
        {
            ExpectedValueKind.Number or ExpectedValueKind.Boolean => value.Text,
            ExpectedValueKind.Null => "~",
            _ => QuotedText(value.Text),
        };
    }

    /// <summary>A comparison of a count as a document writes it: the number alone for equals, else a map of its terms.</summary>
    public static string Written(Comparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        return comparison.Terms is [{ Operator: ComparisonOperator.EqualTo } only]
            ? only.Value.Text
            : "{ " + string.Join(", ", comparison.Terms.Select(t => AssertionText.Of(t.Operator) + ": "
                + (t.Operator == ComparisonOperator.Between ? $"[{t.Value.Text}, {t.Upper!.Text}]" : t.Value.Text))) + " }";
    }

    /// <summary>
    /// Text as YAML quotes it so it reads back as the same text: single-quoted, a quote inside doubled; text holding a
    /// control character double-quoted with JSON's escapes, which YAML reads the same way.
    /// </summary>
    private static string QuotedText(string text)
        => text.Any(char.IsControl) ? System.Text.Json.JsonSerializer.Serialize(text) : "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>The JSON type a <c>type</c> condition names, by JSON Schema's word for it.</summary>
    private static JsonValueType JsonTypeOf(string word, string at, string source) => word switch
    {
        "string" => JsonValueType.Text,
        "number" => JsonValueType.Number,
        "integer" => JsonValueType.WholeNumber,
        "boolean" => JsonValueType.Boolean,
        "object" => JsonValueType.Mapping,
        "array" => JsonValueType.Array,
        "null" => JsonValueType.Null,
        _ => throw new FlowValidationException($"{source}: {at} '{word}' is not one of string, number, integer, boolean, object, array, null."),
    };

    private static void CheckSettings(ValueCondition condition, string at, string source)
    {
        if (condition.IgnoreCase && condition.Operator is not (ValueOperator.EqualTo or ValueOperator.NotEqualTo or ValueOperator.In or ValueOperator.NotIn
                or ValueOperator.Matches or ValueOperator.NotMatches or ValueOperator.StartsWith or ValueOperator.EndsWith
                or ValueOperator.Contains or ValueOperator.NotContains))
        {
            throw new FlowValidationException($"{source}: {at}.ignoreCase applies to text comparisons, not to {AssertionText.Of(condition.Operator)}.");
        }

        if (condition.Tolerance is not null && condition.Operator is not (ValueOperator.EqualTo or ValueOperator.NotEqualTo or ValueOperator.In
                or ValueOperator.NotIn or ValueOperator.Between or ValueOperator.AtLeast or ValueOperator.AtMost or ValueOperator.GreaterThan or ValueOperator.LessThan))
        {
            throw new FlowValidationException($"{source}: {at}.tolerance applies to comparisons of numbers, not to {AssertionText.Of(condition.Operator)}.");
        }

        if (condition.Tolerance is not null && condition.Operands.All(o => o.Kind != ExpectedValueKind.Number && !o.Pending))
        {
            throw new FlowValidationException($"{source}: {at}.tolerance applies to comparisons of numbers, and the value compared with is not a number.");
        }
    }

    private static void CheckBounds(ExpectedValue low, ExpectedValue high, string at, string source)
    {
        if (low.Pending || high.Pending)
        {
            // A bound a run answers is checked against the other once the run has it.
            return;
        }

        if (low.Kind != high.Kind)
        {
            throw new FlowValidationException($"{source}: {at} bounds a range with a {Describe(low)} and a {Describe(high)}; both bounds are numbers, or both dates.");
        }

        var reversed = low.Number is { } a && high.Number is { } b
            ? a > b
            : Instants(low.Text, high.Text) is var (from, to) && from > to;
        if (reversed)
        {
            throw new FlowValidationException($"{source}: {at} runs from {low} down to {high}; write the lower bound first.");
        }
    }

    private static (DateTimeOffset, DateTimeOffset)? Instants(string a, string b)
        => DateTimeOffset.TryParse(a, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var x)
           && DateTimeOffset.TryParse(b, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var y)
            ? (x, y)
            : null;

    private static string Describe(ExpectedValue value) => value.Kind switch
    {
        ExpectedValueKind.Number => "number",
        ExpectedValueKind.Text => "text",
        ExpectedValueKind.Boolean => "boolean",
        _ => "null",
    };

    /// <summary>An operand an ordering compares with: a number, or text (an ISO 8601 date compares as an instant).</summary>
    private static ExpectedValue Ordered(ExpectedValue value, string at, string source)
        => value.Kind is ExpectedValueKind.Number or ExpectedValueKind.Text || value.Pending
            ? value
            : throw new FlowValidationException($"{source}: {at} orders numbers, dates and text, and '{value.Text}' is none of them.");

    private static ExpectedValue Number(string text, string at, string source)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? ExpectedValue.OfNumber(number, text)
            : throw new FlowValidationException($"{source}: {at} holds the number {text}, which is out of range.");

    private static string Shown(string value) => value.Length > 80 ? value[..80] + "..." : value;

    /// <summary>
    /// An operand an operator reads as text, as a typed model reads any scalar written where it takes text: text as it is, a
    /// number in invariant form, true or false; null for a map or a list.
    /// </summary>
    private static string? TextOperand(object operand) => operand switch
    {
        string text => text,
        bool flag => flag ? "true" : "false",
        IDictionary or IList => null,
        _ => Convert.ToString(operand, CultureInfo.InvariantCulture),
    };

    [GeneratedRegex(@"^[A-Za-z_$@][\w$@-]*(\[(\*|\d+)\])*(\.[A-Za-z_$@][\w$@-]*(\[(\*|\d+)\])*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"^[A-Za-z][\w-]*--[A-Za-z][\w-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EntityTypePattern();
}
